using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using WifiHeatmap.Core;
using WifiHeatmap.Meter;
using WifiHeatmap.Probe;

namespace WifiHeatmap.Recording
{
    /// <summary>
    /// Writes probe samples to a session on disk. Each sample is placed at the antenna pose from the
    /// moment it was measured (probe clock mapped to the headset clock, pose history interpolated),
    /// stored relative to the anchor it belongs to. One session covers one access point; it ends
    /// when recording stops, the probe stops streaming, or the network changes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SessionRecorder : MonoBehaviour
    {
        [SerializeField] private Esp32BleSignalTester probe;
        [SerializeField] private ProbeMount mount;
        [SerializeField] private AnchorRegistry anchors;
        [SerializeField] private SimulatedRssiSource simulated;
        [SerializeField] private EmfMeter meter;

        [Header("Quality limits")]
        [Tooltip("Samples whose measurement time is less certain than this are kept but flagged.")]
        [SerializeField, Min(1f)] private float maxClockUncertaintyMs = 15f;
        [Tooltip("How far past the newest tracked pose a sample may be placed (by extrapolation).")]
        [SerializeField, Min(0f)] private float maxExtrapolationMs = 30f;

        [Header("Writing")]
        [SerializeField, Min(0.1f)] private float flushSeconds = 1f;
        [SerializeField, Min(0.5f)] private float diskSyncSeconds = 5f;

        private readonly PoseHistory history = new PoseHistory(1024);
        private SessionFileWriter writer;
        private float nextFlush;
        private float nextDiskSync;
        private float nextIndicator;
        private float startedAt;
        private Task diskSync = Task.CompletedTask;
        private readonly HashSet<int> anchorsUsed = new HashSet<int>();

        public static string SessionsRoot => Path.Combine(Application.persistentDataPath, "sessions");

        public bool IsRecording => writer != null;
        public string CurrentFolder => writer?.Folder;
        public SessionHeader CurrentHeader => writer?.Header;
        public long Written => writer?.RecordCount ?? lastWritten;
        public int DroppedNoClock { get; private set; }
        public int DroppedNoPose { get; private set; }
        public int DroppedNoAnchor { get; private set; }
        public int FlaggedSamples { get; private set; }
        public int AnchorsUsed => anchorsUsed.Count;
        public float Duration => IsRecording ? Time.realtimeSinceStartup - startedAt : lastDuration;
        public string LastMessage { get; private set; } = string.Empty;
        public IReadOnlyList<string> RecoveredAtStartup { get; private set; } = Array.Empty<string>();

        /// <summary>Recording started or stopped, or a session file changed.</summary>
        public event Action Changed;

        /// <summary>Time-aligned antenna sample, also emitted while disk recording is off.</summary>
        public event Action<AnchorRegistry.AnchorInfo, SampleRecord, long> SpatialSampleReceived;
        public string PlacementStatus { get; private set; } = "Waiting for a measurement.";

        private long lastWritten;
        private float lastDuration;

        private void Awake()
        {
            Directory.CreateDirectory(SessionsRoot);
            try
            {
                RecoveredAtStartup = SessionStore.RecoverIncomplete(SessionsRoot);
                if (RecoveredAtStartup.Count > 0)
                {
                    LastMessage = $"Recovered {RecoveredAtStartup.Count} unfinished session(s).";
                    Debug.Log(LastMessage, this);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Session recovery failed: {exception.Message}", this);
            }
        }

        private void OnEnable()
        {
            mount.CalibrationChanged += history.Clear;
            probe.SampleReceived += OnSample;
            probe.ClockExchanged += OnClockExchange;
            probe.StateChanged += OnProbeState;
        }

        private void OnDisable()
        {
            mount.CalibrationChanged -= history.Clear;
            probe.SampleReceived -= OnSample;
            probe.ClockExchanged -= OnClockExchange;
            probe.StateChanged -= OnProbeState;
            StopRecording("Recorder disabled.");
        }

        private void OnApplicationQuit()
        {
            StopRecording("App closing.");
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                history.Clear();
                writer?.Flush(true);
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                history.Clear();
                writer?.Flush(true);
            }
        }

        private void Update()
        {
            // Antenna pose every frame, on the same clock the probe samples are mapped onto.
            Transform antenna = mount.Antenna;
            bool tracked = mount.ProbeTracked();
            history.Add(HostClock.NowMicroseconds, antenna.position.ToNumerics(), antenna.rotation.ToNumerics(), tracked);

            if (!IsRecording)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now >= nextFlush)
            {
                nextFlush = now + flushSeconds;
                writer.Flush(false);
            }
            if (now >= nextDiskSync && diskSync.IsCompleted)
            {
                nextDiskSync = now + diskSyncSeconds;
                SessionFileWriter current = writer;
                diskSync = Task.Run(() => current.Flush(true));
            }
            if (now >= nextIndicator)
            {
                nextIndicator = now + 0.5f;
                meter?.SetRecording(true, $"REC {FormatDuration(Duration)}  {writer.RecordCount}");
            }
        }

        /// <summary>Starts a session for the network the probe is measuring. False with a reason if it can't.</summary>
        public bool StartRecording(out string reason)
        {
            reason = string.Empty;
            if (IsRecording)
            {
                reason = "Already recording.";
                return false;
            }
            if (mount.IsFrozenAlignment || mount.HasUnsavedProbeOffset)
            {
                reason = "Finish aligning and Save or Load the probe offset before recording.";
                return false;
            }
            if (simulated != null && simulated.enabled)
            {
                reason = "Turn off the simulated signal; simulated readings are never recorded.";
                return false;
            }
            if (!probe.IsStreaming || probe.Target == null)
            {
                reason = "Pick a network to measure first (Network tab).";
                return false;
            }

            SessionHeader header = new SessionHeader
            {
                SessionId = SessionFiles.NewSessionId(DateTime.UtcNow),
                StartedUtc = DateTime.UtcNow,
            };
            header.App.Version = Application.version;
            header.App.Unity = Application.unityVersion;
            header.App.MetaXr = OVRPlugin.version?.ToString() ?? string.Empty;
            header.AccessPoint.Bssid = probe.Target.Bssid;
            header.AccessPoint.Channel = probe.Target.Channel;
            header.AccessPoint.Band = probe.Target.Band;
            header.AccessPoint.Ssid = probe.Target.Ssid;
            header.AccessPoint.Security = probe.Target.Security;
            if (probe.MeasuredRadioCount > 1)
            {
                header.AccessPoint.Bssids.AddRange(probe.Target.Bssids);
            }
            header.AccessPoint.MeasuredRadios = probe.MeasuredRadioCount;
            header.Probe.DeviceName = probe.DeviceName;
            header.Probe.Hello = probe.DeviceInfo;
            header.Probe.Caps = probe.Capabilities;
            header.Probe.BootId = probe.Clock.BootId;
            header.Probe.ReportIntervalMs = Mathf.RoundToInt(probe.ReportIntervalSeconds * 1000f);
            header.Mount.MountId = mount.MountId;
            header.Mount.CalibrationId = mount.CalibrationId;
            header.Mount.Calibration = mount.Snapshot();

            // Fix the anchor table up front where possible, so indices are stable from the first sample.
            List<AnchorRegistry.AnchorInfo> available = anchors.GetAll();
            if (anchors.CurrentMode == AnchorRegistry.Mode.Manual)
            {
                AnchorRegistry.AnchorInfo chosen = available.Find(a => a.Uuid == anchors.ChosenManual);
                if (chosen == null || chosen.Transform == null)
                {
                    reason = "The chosen manual anchor isn't loaded here. Choose another, or switch to Auto (rooms).";
                    return false;
                }
                header.AddAnchor(chosen.Kind, chosen.Uuid, chosen.Label);
            }
            else
            {
                foreach (AnchorRegistry.AnchorInfo room in available.FindAll(a => a.IsRoom))
                {
                    header.AddAnchor(room.Kind, room.Uuid, room.Label);
                }
                if (header.Anchors.Count == 0 && !available.Exists(a => !a.IsRoom && a.Localized))
                {
                    reason = "No room scan or anchor here. Run Space Setup, or place an anchor (Anchors tab).";
                    return false;
                }
            }

            try
            {
                Directory.CreateDirectory(SessionsRoot);
                writer = SessionFileWriter.Create(SessionsRoot, header);
                mount.CalibrationLocked = true;
            }
            catch (Exception exception)
            {
                reason = $"Could not create the session: {exception.Message}";
                return false;
            }

            DroppedNoClock = DroppedNoPose = DroppedNoAnchor = FlaggedSamples = 0;
            anchorsUsed.Clear();
            startedAt = Time.realtimeSinceStartup;
            nextFlush = startedAt + flushSeconds;
            nextDiskSync = startedAt + diskSyncSeconds;
            LastMessage = $"Recording {probe.Target.DisplayName} to {header.SessionId}.";
            Changed?.Invoke();
            return true;
        }

        public void StopRecording(string why = "Recording stopped.")
        {
            if (writer == null)
            {
                return;
            }
            SessionFileWriter finished = writer;
            writer = null;
            mount.CalibrationLocked = false;
            lastWritten = finished.RecordCount;
            lastDuration = Time.realtimeSinceStartup - startedAt;
            try
            {
                diskSync.Wait(2000);
                finished.Complete(DateTime.UtcNow);
                LastMessage = $"{why} Saved {finished.RecordCount} samples.";
            }
            catch (Exception exception)
            {
                finished.Dispose();
                LastMessage = $"{why} Finalising failed ({exception.Message}); it will be recovered on next launch.";
                Debug.LogWarning(LastMessage, this);
            }
            meter?.SetRecording(false, string.Empty);
            Changed?.Invoke();
        }

        private void OnProbeState(Esp32BleSignalTester.SessionState state)
        {
            if (IsRecording && state != Esp32BleSignalTester.SessionState.Streaming &&
                state != Esp32BleSignalTester.SessionState.AwaitingStop)
            {
                StopRecording("The probe stopped measuring.");
            }
        }

        private void OnClockExchange(PingExchange exchange)
        {
            writer?.AppendClockExchange(exchange);
        }

        private void OnSample(ProbeSample sample)
        {
            SessionHeader.AccessPointInfo ap = writer?.Header.AccessPoint;
            if (ap != null && (sample.Bssid != ap.Bssid || sample.Channel != ap.Channel))
            {
                StopRecording("The network changed.");
                return;
            }
            if (!sample.HasHostTime)
            {
                DroppedNoClock++;
                PlacementStatus = "No clock mapping yet; waiting for a PONG.";
                return;
            }

            SampleFlags flags = SampleFlags.None;
            if (sample.HostTimeUncertaintyUs > maxClockUncertaintyMs * 1000f)
            {
                flags |= SampleFlags.ClockUncertain;
            }
            if (!history.TryGet(sample.HostTimeUs, (long)(maxExtrapolationMs * 1000f), out PoseHistory.Lookup pose))
            {
                DroppedNoPose++;
                PlacementStatus = "No tracked pose history at this measurement's time.";
                return;
            }
            if (pose.Extrapolated)
            {
                flags |= SampleFlags.PoseExtrapolated;
            }
            if (pose.Degraded)
            {
                flags |= SampleFlags.TrackingDegraded;
            }

            Vector3 worldPosition = pose.Position.ToUnity();
            Quaternion worldRotation = pose.Rotation.ToUnity();
            if (!anchors.TryChoose(worldPosition, out AnchorRegistry.AnchorInfo anchor))
            {
                DroppedNoAnchor++;
                PlacementStatus = "No room/anchor available. Use Anchors > Place anchor at probe.";
                return;
            }
            if (!anchor.Localized)
            {
                flags |= SampleFlags.AnchorNotLocalized;
            }

            Vector3 relativePosition = anchor.Transform.InverseTransformPoint(worldPosition);
            Quaternion relativeRotation = Quaternion.Inverse(anchor.Transform.rotation) * worldRotation;
            SampleRecord record = SampleRecord.Create(sample.Sequence, sample.HostTimeUs, relativePosition.ToNumerics(),
                relativeRotation.ToNumerics(), sample.MeanDbm, sample.StdDevDb, sample.Frames, 0, flags);
            PlacementStatus = pose.Degraded ? $"{(mount != null && mount.IsOnHand ? "Wrist (hand)" : "Probe-controller")} tracking was lost at measurement time; placement paused."
                : !anchor.Localized ? "Room/anchor is not localised; placement paused."
                : sample.HostTimeUncertaintyUs > SpatialDisplayQuality.MaxPreviewTimingUs
                    ? $"Clock uncertainty +/-{sample.HostTimeUncertaintyUs / 1000f:0} ms exceeds 100 ms preview limit."
                : (flags & SampleFlags.ClockUncertain) != 0
                    ? $"Timing-degraded preview: clock +/-{sample.HostTimeUncertaintyUs / 1000f:0} ms. Move slowly."
                : $"Clock +/-{sample.HostTimeUncertaintyUs / 1000f:0} ms. Confidence fades sparse/variable cells.";
            if (!mount.IsFrozenAlignment && !mount.HasUnsavedProbeOffset && (simulated == null || !simulated.enabled))
                SpatialSampleReceived?.Invoke(anchor, record, sample.HostTimeUncertaintyUs);
            if (writer == null) return;
            int index = writer.AddAnchor(anchor.Kind, anchor.Uuid, anchor.Label);
            anchorsUsed.Add(index);
            record.AnchorIndex = (byte)index;
            writer.Append(record);
            if (flags != SampleFlags.None)
            {
                FlaggedSamples++;
            }
        }

        private static string FormatDuration(float seconds)
        {
            TimeSpan span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }
    }

    /// <summary>Conversions between Unity and System.Numerics types (the core assembly has no Unity references).</summary>
    public static class NumericsConversions
    {
        public static System.Numerics.Vector3 ToNumerics(this Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);
        public static System.Numerics.Quaternion ToNumerics(this Quaternion q) => new System.Numerics.Quaternion(q.x, q.y, q.z, q.w);
        public static Vector3 ToUnity(this System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
        public static Quaternion ToUnity(this System.Numerics.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
