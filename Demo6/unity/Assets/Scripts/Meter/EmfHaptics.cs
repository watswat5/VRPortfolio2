using System;
using UnityEngine;
using WifiHeatmap.Core;
using WifiHeatmap.Probe;
using WifiHeatmap.Heatmap;

namespace WifiHeatmap.Meter
{
    /// <summary>Movement warning: antenna speed times clock uncertainty drives pulses on the probe's controller.
    /// With the probe on the wrist (hand tracking, no controller to vibrate) the pulses go to the probe's own buzzer
    /// when it has one (protocol 1.1), else to a tone in the headset.
    /// Uses tracking-space antenna motion so MRUK world-lock adjustments do not look like movement.</summary>
    [DisallowMultipleComponent]
    public sealed class EmfHaptics : MonoBehaviour
    {
        [SerializeField] private EmfMeter meter;
        [SerializeField] private Esp32BleSignalTester probe;
        [SerializeField] private ProbeMount mount;
        [SerializeField] private Transform trackingSpace;
        [SerializeField] private CloudSettings settings;
        [SerializeField] private bool hapticsEnabled = true;

        [Header("Timing-related displacement as a fraction of voxel width")]
        [SerializeField, Range(0,1)] private float warningVoxelFraction = .1f;
        [SerializeField, Range(.01f,2)] private float fullWarningVoxelFraction = .5f;
        [SerializeField, Min(.01f)] private float speedSmoothingSeconds = .08f;
        [SerializeField, Min(.5f)] private float sampleStaleSeconds = 2.5f;
        [SerializeField, Min(1f)] private float clockStaleSeconds = 5f;

        [Header("Pulses")]
        [SerializeField, Min(.1f)] private float slowestPulsesPerSecond = 1.5f;
        [SerializeField, Min(.1f)] private float fastestPulsesPerSecond = 14f;
        [SerializeField, Range(.01f,.2f)] private float pulseSeconds = .035f;
        [SerializeField, Range(0,1)] private float weakestAmplitude = .15f;
        [SerializeField, Range(0,1)] private float strongestAmplitude = 1;
        [SerializeField] private Vector2 frequencyRange = new Vector2(.25f,1);
        [SerializeField, Range(0,1)] private float continuousAbove = .97f;

        [Header("Wrist probe (no controller to vibrate)")]
        [Tooltip("Buzzer pitch at the weakest and strongest warning, in Hz.")]
        [SerializeField] private Vector2 buzzerHzRange = new Vector2(2000,4000);
        [Tooltip("Headset tone at the weakest and strongest warning when the probe has no buzzer, in Hz.")]
        [SerializeField] private Vector2 toneHzRange = new Vector2(440,880);
        [SerializeField, Range(0,1)] private float toneVolume = .35f;

        private Vector3 previousPosition, smoothedVelocity;
        private long previousPoseUs, lastReceivedUs;
        private ulong lastProbeUs, sampleBoot;
        private bool hasPose, hasSample, vibrating;
        private OVRInput.Controller vibratingController;
        private enum Output { Controller, Buzzer, Tone }
        private Output vibratingOutput;
        private float buzzRenewAt;        // when the current buzz needs renewing (a pulse that runs on is sustained)
        private bool buzzSustained;
        private const float SustainedBuzzSeconds = .25f, SustainedRenewSeconds = .2f;
        private AudioSource tone;
        private float phase;
        public float SpeedMetersPerSecond { get; private set; }
        public double TimingDisplacementMeters { get; private set; } = double.NaN;
        public double TimingUncertaintyUs { get; private set; } = double.NaN;
        public string Status { get; private set; } = "Movement haptics: waiting for measurement.";
        public bool HapticsEnabled
        {
            get => hapticsEnabled;
            set { hapticsEnabled=value; ResetMotion(); }
        }
        private void OnEnable()
        {
            if (probe != null) { probe.SampleReceived += OnSample; probe.StateChanged += OnState; }
            if (mount != null) mount.CalibrationChanged += ResetMotion;
            OVRManager.TrackingOriginChangePending += OnTrackingOriginChanged;
            ResetAll();
        }
        private void OnDisable()
        {
            if (probe != null) { probe.SampleReceived -= OnSample; probe.StateChanged -= OnState; }
            if (mount != null) mount.CalibrationChanged -= ResetMotion;
            OVRManager.TrackingOriginChangePending -= OnTrackingOriginChanged;
            ResetAll();
        }
        private void OnSample(ProbeSample sample)
        {
            lastReceivedUs=sample.ReceivedHostUs; lastProbeUs=sample.ProbeTimeUs;
            sampleBoot=probe.Clock.BootId; hasSample=true;
        }
        private void OnState(Esp32BleSignalTester.SessionState _) { ResetAll(); }
        private void OnTrackingOriginChanged(OVRManager.TrackingOrigin _, OVRPose? __) { ResetAll(); }
        private void OnApplicationFocus(bool focus) { if (!focus) ResetAll(); }
        private void OnApplicationPause(bool pause) { if (pause) ResetAll(); }
        private void ResetAll() { hasSample=false; ResetMotion(); }
        private void ResetMotion()
        {
            hasPose=false; phase=0; SpeedMetersPerSecond=0; smoothedVelocity=Vector3.zero;
            TimingDisplacementMeters=TimingUncertaintyUs=double.NaN; Stop();
        }
        private void LateUpdate()
        {
            if (probe==null || mount==null || trackingSpace==null || settings==null)
            { ResetMotion(); Status="Movement haptics unavailable: missing probe/mount/tracking/settings reference."; return; }
            if (!probe.IsStreaming)
            { ResetMotion(); Status="Movement guidance: start measuring a network."; return; }
            if (mount.IsFrozenAlignment || mount.HasUnsavedProbeOffset)
            { ResetMotion(); Status="Movement guidance paused: finish and save mount calibration."; return; }
            if (!Application.isFocused || !OVRManager.hasInputFocus ||
                !mount.ProbeTracked())
            { ResetAll(); Status="Movement guidance paused: tracking/input focus unavailable."; return; }
            long now=HostClock.NowMicroseconds;
            Vector3 position=trackingSpace.InverseTransformPoint(mount.Antenna.position);
            double elapsed=(now-previousPoseUs)*1e-6;
            if (!hasPose || elapsed<=0 || elapsed>.25 || !Finite(position) ||
                Vector3.Distance(previousPosition,position)>1)
            {
                ResetMotion(); previousPosition=position; previousPoseUs=now; hasPose=Finite(position);
                Status="Movement guidance: acquiring a fresh pose."; return;
            }
            Vector3 velocity=(position-previousPosition)/(float)elapsed;
            float blend=1-Mathf.Exp(-(float)elapsed/Mathf.Max(.01f,speedSmoothingSeconds));
            // Filter signed velocity before magnitude so stationary tracking jitter does not
            // accumulate a positive speed bias, especially with fine voxels.
            smoothedVelocity=Vector3.Lerp(smoothedVelocity,velocity,blend);
            SpeedMetersPerSecond=smoothedVelocity.magnitude;
            previousPosition=position; previousPoseUs=now;
            if (!hasSample || now<lastReceivedUs || now-lastReceivedUs>sampleStaleSeconds*1e6 ||
                (meter!=null && !meter.HasSignal))
            {
                phase=0; TimingDisplacementMeters=TimingUncertaintyUs=double.NaN; Stop();
                Status=$"Speed {SpeedMetersPerSecond:0.00} m/s | waiting for fresh probe data."; return;
            }
            ClockMapping clock=probe.Clock;
            bool known=clock.HasMapping && clock.BootId==sampleBoot && now>=clock.LastExchangeHostUs &&
                now-clock.LastExchangeHostUs<=clockStaleSeconds*1e6 &&
                clock.TryProbeToHost(lastProbeUs,out _,out _);
            double uncertainty=double.NaN;
            if (known && clock.TryProbeToHost(lastProbeUs,out long measuredUs,out long uncertaintyUs))
            {
                // Refresh the bound from the current fit and conservatively age it until another report.
                // Callback/Unity dispatch latency is not substituted for clock uncertainty.
                uncertainty=uncertaintyUs+Math.Max(0,now-measuredUs)*Math.Max(0,clock.DriftAllowancePpm)*1e-6;
            }
            float voxel=settings.Resolution;
            bool valid=MotionTimingRisk.TryEvaluate(SpeedMetersPerSecond,uncertainty,
                voxel*warningVoxelFraction,voxel*fullWarningVoxelFraction,
                out double displacement,out double level);
            if (!valid)
            {
                TimingDisplacementMeters=TimingUncertaintyUs=double.NaN;
                Status=$"Speed {SpeedMetersPerSecond:0.00} m/s | timing unknown/stale: wait for clock sync.";
                if (hapticsEnabled) UnknownTimingPulse((float)elapsed); else Stop();
                return;
            }
            TimingUncertaintyUs=uncertainty; TimingDisplacementMeters=displacement;
            Status=$"{SpeedMetersPerSecond:0.00} m/s | clock +/-{uncertainty/1000:0.0} ms | motion ~{displacement*100:0.0} cm ({displacement/voxel*100:0}% voxel)";
            if (!hapticsEnabled || level<=0) { phase=0; Stop(); return; }
            Pulse((float)level,(float)elapsed);
        }
        private void Pulse(float level,float elapsed)
        {
            float amplitude=Mathf.Lerp(weakestAmplitude,strongestAmplitude,level);
            float frequency=Mathf.Lerp(frequencyRange.x,frequencyRange.y,level);
            if (level>=continuousAbove) { Vibrate(frequency,amplitude); return; }
            float rate=Mathf.Lerp(slowestPulsesPerSecond,fastestPulsesPerSecond,level);
            phase=Mathf.Repeat(phase+rate*elapsed,1);
            if (phase<Mathf.Clamp01(pulseSeconds*rate)) Vibrate(frequency,amplitude); else Stop();
        }
        private void UnknownTimingPulse(float elapsed)
        {
            // A distinct double tap means unknown, not a fabricated zero/large timing error.
            phase=Mathf.Repeat(phase+elapsed,1.5f);
            if (phase<.06f || (phase>=.18f && phase<.24f)) Vibrate(.4f,.35f); else Stop();
        }
        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        private Output CurrentOutput => !mount.IsOnHand ? Output.Controller : probe.SupportsBuzzer ? Output.Buzzer : Output.Tone;
        private void Vibrate(float frequency,float amplitude)
        {
            // The probe can move to another controller or the wrist; stop whatever was buzzing first.
            Output output=CurrentOutput;
            if (vibrating && (vibratingOutput!=output || vibratingController!=mount.Controller)) Stop();
            bool starting=!vibrating;
            vibratingOutput=output; vibratingController=mount.Controller; vibrating=true;
            switch (output)
            {
                case Output.Controller:
                    OVRInput.SetControllerVibration(frequency,amplitude,vibratingController); break;
                case Output.Buzzer:
                    // Each warning starts as one short BUZZ that ends by itself. Still on when that would end, it is
                    // sustained: a longer BUZZ, renewed before it runs out, and BUZZ_OFF when the warning stops.
                    float now=Time.unscaledTime;
                    int hz=Mathf.RoundToInt(Mathf.Lerp(buzzerHzRange.x,buzzerHzRange.y,Mathf.InverseLerp(frequencyRange.x,frequencyRange.y,frequency)));
                    if (starting)
                    {
                        buzzSustained=false;
                        probe.Buzz(Mathf.Max(1,Mathf.RoundToInt(pulseSeconds*1000)),hz);
                        buzzRenewAt=now+pulseSeconds;
                    }
                    else if (now>=buzzRenewAt)
                    {
                        buzzSustained=true;
                        probe.Buzz(Mathf.RoundToInt(SustainedBuzzSeconds*1000),hz);
                        buzzRenewAt=now+SustainedRenewSeconds;
                    }
                    break;
                case Output.Tone:
                    EnsureTone();
                    tone.pitch=Mathf.Lerp(toneHzRange.x,toneHzRange.y,Mathf.InverseLerp(frequencyRange.x,frequencyRange.y,frequency))/ToneBaseHz;
                    tone.volume=toneVolume*amplitude;
                    if (!tone.isPlaying) tone.Play();
                    break;
            }
        }
        private void Stop()
        {
            if (!vibrating) return;
            switch (vibratingOutput)
            {
                case Output.Controller: OVRInput.SetControllerVibration(0,0,vibratingController); break;
                // Short pulses end by themselves; only a sustained buzz needs switching off.
                case Output.Buzzer: if (buzzSustained) probe.BuzzOff(); break;
                case Output.Tone: if (tone!=null) tone.Stop(); break;
            }
            vibrating=false; buzzSustained=false; buzzRenewAt=0;
        }
        private const float ToneBaseHz=441;   // divides 44.1 kHz evenly, so the looped clip has whole cycles
        private void EnsureTone()
        {
            if (tone!=null) return;
            const int rate=44100;
            var samples=new float[rate/(int)ToneBaseHz*100];   // 100 whole cycles, so the loop is seamless
            for (int i=0;i<samples.Length;i++) samples[i]=Mathf.Sin(2*Mathf.PI*ToneBaseHz*i/rate);
            var clip=AudioClip.Create("Movement warning tone",samples.Length,1,rate,false);
            clip.SetData(samples,0);
            tone=gameObject.AddComponent<AudioSource>();
            tone.clip=clip; tone.loop=true; tone.playOnAwake=false; tone.spatialBlend=0;
        }
    }
}
