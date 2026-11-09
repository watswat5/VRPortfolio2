using System;
using System.IO;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using WifiHeatmap.Core;
using WifiHeatmap.Probe;

namespace WifiHeatmap.Meter
{
    /// <summary>
    /// Ghost-hunter style signal meter: an arc of LEDs from red (weak) to green (strong) with a dBm
    /// readout. With a probe mount it is fixed to the probe pod like a handheld scanner's screen: just
    /// beyond the pod's front (along the controller's forward), far enough out that it never covers the
    /// probe, square to the controller, its screen facing the same way as the pod's top (what you look
    /// down at in a normal grip) and its up along the controller's forward. It moves rigidly with the
    /// controller. That placement can be replaced by an aligned one (ProbeMount's Align meter), saved per probe carrier
    /// next to the probe's calibration and kept relative to the probe, so recalibrating the probe carries the meter
    /// with it. Without a mount it hovers above the followed transform and turns to face the viewer. Feed it with SetRssi from the probe or a
    /// simulated source.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EmfMeter : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Header("Display range")]
        [SerializeField] private int weakestDbm = -90;
        [SerializeField] private int strongestDbm = -30;

        [Header("LEDs (ordered weak to strong)")]
        [SerializeField] private Renderer[] leds = new Renderer[0];
        [SerializeField, Range(0f, 1f)] private float unlitBrightness = 0.12f;
        [Tooltip("How fast the lit level rises toward a stronger reading (levels per second).")]
        [SerializeField, Min(0.1f)] private float attackPerSecond = 6f;
        [Tooltip("How fast the lit level falls toward a weaker reading (levels per second).")]
        [SerializeField, Min(0.1f)] private float releasePerSecond = 1.5f;
        [Tooltip("With no new reading for this long, the meter shows no signal.")]
        [SerializeField, Min(0.1f)] private float staleAfterSeconds = 2.5f;

        [Header("Readout")]
        [SerializeField] private TMP_Text readout;
        [SerializeField] private TMP_Text weakScaleLabel, strongScaleLabel;
        [SerializeField] private TMP_Text status;
        [Tooltip("Small red REC line shown while a session is recording.")]
        [SerializeField] private TMP_Text recordingLabel;

        [Header("On the probe pod")]
        [Tooltip("The probe pod to sit in front of. Unset: the ProbeMount under Follow, else hover above Follow.")]
        [SerializeField] private ProbeMount mount;
        [Tooltip("Gap between the front of the pod and the bottom of the meter, in metres.")]
        [SerializeField, Min(0f)] private float podGap = 0.015f;

        [Header("Hover (no probe mount)")]
        [SerializeField] private Transform follow;
        [SerializeField] private Transform faceTowards;
        [Tooltip("Hover point: metres above the followed transform (world up) and along its flattened forward.")]
        [SerializeField] private float hoverHeight = 0.11f;
        [SerializeField] private float hoverForward = 0.03f;
        [SerializeField, Min(0f)] private float followSharpness = 18f;
        [SerializeField, Min(0f)] private float bobAmplitude = 0.003f;

        private MaterialPropertyBlock block;
        private Color[] ledColors = new Color[0];
        private float level;
        private float lastSampleTime = float.NegativeInfinity;
        private int lastDbm;
        private bool hasPose;

        // An aligned placement in the probe's frame, replacing the default one; saved per carrier.
        private bool aligned, offsetUnsaved, held;
        private Vector3 alignedPosition;
        private Quaternion alignedRotation = Quaternion.identity;
        private string offsetLoadedFor;

        private sealed class SavedOffset
        {
            public int Version { get; set; } = 1;
            public string MountId { get; set; }
            public float[] Position { get; set; }
            public float[] Rotation { get; set; }
        }

        private const float BodyBelowPivot = 0.031f;   // the generated body reaches this far below the meter's origin
        // Screen (-Z) along the pod's +Y (its top), up along the pod's +Z (the controller's forward); red stays on -X.
        private static readonly Quaternion OnPod = Quaternion.LookRotation(Vector3.down, Vector3.forward);

        /// <summary>Smoothed level, 0 (weakest) to 1 (strongest), as shown on the LEDs.</summary>
        public float Level01 => level;

        /// <summary>True while readings are arriving.</summary>
        public bool HasSignal => Time.time - lastSampleTime <= staleAfterSeconds;

        public int LatestDbm => lastDbm;
        public int WeakestDbm => weakestDbm;
        public int StrongestDbm => strongestDbm;
        public void SetRange(int low, int high)
        {
            weakestDbm = Mathf.Clamp(low, SignalDisplayRange.MinimumDbm, SignalDisplayRange.MaximumDbm - SignalDisplayRange.MinimumSpanDb);
            strongestDbm = Mathf.Clamp(high, weakestDbm + SignalDisplayRange.MinimumSpanDb, SignalDisplayRange.MaximumDbm);
            UpdateScaleLabels();
        }

        public Transform Follow
        {
            get => follow;
            set => follow = value;
        }

        public Transform FaceTowards
        {
            get => faceTowards;
            set => faceTowards = value;
        }

        /// <summary>New RSSI reading in dBm (wire the probe's OnSignalStrengthDbm here).</summary>
        public void SetRssi(int dbm)
        {
            lastDbm = dbm;
            lastSampleTime = Time.time;
            if (readout != null)
            {
                readout.text = $"{dbm} dBm";
            }
        }

        public void SetStatus(string message)
        {
            if (status != null)
            {
                status.color = new Color(0.75f, 0.85f, 0.75f);
                status.text = Shorten(message);
            }
        }

        public void SetError(string message)
        {
            if (status != null)
            {
                status.color = new Color(1f, 0.45f, 0.35f);
                status.text = Shorten(message);
            }
        }

        /// <summary>Shows or hides the recording indicator (e.g. "REC 1:23  456").</summary>
        public void SetRecording(bool recording, string text)
        {
            if (recordingLabel != null)
            {
                recordingLabel.gameObject.SetActive(recording);
                recordingLabel.text = text;
            }
        }

        /// <summary>Maps dBm onto the selected scale, 0..1.</summary>
        /// <summary>The probe this meter is fixed to, or null when it hovers.</summary>
        public ProbeMount Mount => mount;
        /// <summary>The meter's placement differs from its saved one (an alignment, or a reset not yet saved).</summary>
        public bool HasUnsavedOffset => offsetUnsaved;
        public bool IsAligned => aligned;
        /// <summary>Saved next to the probe's calibration file for the same carrier.</summary>
        public string OffsetPath => mount == null ? null : Path.Combine(Path.GetDirectoryName(mount.CalibrationPath),
            Path.GetFileName(mount.CalibrationPath).Replace("probe-calibration", "emf-meter"));

        /// <summary>Keeps the meter where it is in the room (during its alignment) instead of following the probe.</summary>
        public void Hold(bool value) => held = value;

        /// <summary>Uses this placement, in the probe's frame, until saved, loaded or reset.</summary>
        public void SetAlignedOffset(Vector3 localPosition, Quaternion localRotation)
        {
            aligned = true;
            alignedPosition = localPosition;
            alignedRotation = localRotation.normalized;
            offsetUnsaved = true;
        }

        /// <summary>Back to the default placement in front of the probe (saving then removes the saved one).</summary>
        public void ResetOffset()
        {
            offsetUnsaved = aligned || File.Exists(OffsetPath ?? "");
            aligned = false;
        }

        /// <summary>Writes the aligned placement, or removes the saved one if it was reset. Throws on failure.</summary>
        public void SaveOffset()
        {
            string path = OffsetPath;
            if (path == null) return;
            if (!aligned)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                var data = new SavedOffset
                {
                    MountId = mount.MountId,
                    Position = new[] { alignedPosition.x, alignedPosition.y, alignedPosition.z },
                    Rotation = new[] { alignedRotation.x, alignedRotation.y, alignedRotation.z, alignedRotation.w },
                };
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(data, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            offsetUnsaved = false;
            offsetLoadedFor = mount.CalibrationPath;
        }

        /// <summary>The saved placement for the probe's current carrier, or the default if there is none (or it
        /// belongs to another mount or is unreadable). Discards an unsaved alignment.</summary>
        public void LoadOffset()
        {
            aligned = false;
            offsetUnsaved = false;
            if (mount == null) return;
            offsetLoadedFor = mount.CalibrationPath;
            string path = OffsetPath;
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 4096) return;
                var data = JsonConvert.DeserializeObject<SavedOffset>(File.ReadAllText(path));
                if (data == null || data.Version != 1 || data.MountId != mount.MountId ||
                    data.Position == null || data.Position.Length != 3 || data.Rotation == null || data.Rotation.Length != 4)
                    throw new InvalidDataException("not this mount's meter placement");
                var position = new Vector3(data.Position[0], data.Position[1], data.Position[2]);
                var rotation = new Quaternion(data.Rotation[0], data.Rotation[1], data.Rotation[2], data.Rotation[3]);
                float norm = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
                if (!float.IsFinite(position.sqrMagnitude) || position.sqrMagnitude > .5f * .5f || !float.IsFinite(norm) || Mathf.Abs(norm - 1f) > .001f)
                    throw new InvalidDataException("placement out of range");
                aligned = true;
                alignedPosition = position;
                alignedRotation = rotation.normalized;
            }
            catch (Exception e) { Debug.LogWarning($"EMF meter placement {path} ignored: {e.Message}", this); }
        }

        public float Normalize(int dbm)
        {
            return Mathf.InverseLerp(weakestDbm, strongestDbm, dbm);
        }

        private void Awake()
        {
            // Scenes saved before the meter moved onto the pod only reference the controller; its ProbeMount is a child.
            if (mount == null && follow != null) mount = follow.GetComponentInChildren<ProbeMount>(true);
            block = new MaterialPropertyBlock();
            BuildColors();
            if (readout != null)
            {
                readout.text = "-- dBm";
            }
            UpdateScaleLabels();
            SetRecording(false, string.Empty);
        }

        private void OnValidate()
        {
            if (strongestDbm <= weakestDbm)
            {
                strongestDbm = weakestDbm + 1;
            }
            BuildColors();
        }

        private void UpdateScaleLabels()
        {
            if (weakScaleLabel != null) weakScaleLabel.text = weakestDbm.ToString();
            if (strongScaleLabel != null) strongScaleLabel.text = strongestDbm.ToString();
        }

        private void BuildColors()
        {
            ledColors = new Color[leds.Length];
            for (int i = 0; i < leds.Length; i++)
            {
                // Hue 0 (red) at the weak end to 1/3 (green) at the strong end, through yellow.
                float t = leds.Length > 1 ? i / (float)(leds.Length - 1) : 1f;
                ledColors[i] = Color.HSVToRGB(t / 3f, 1f, 1f);
            }
        }

        private void Update()
        {
            float target = HasSignal ? Normalize(lastDbm) : 0f;
            float rate = target > level ? attackPerSecond : releasePerSecond;
            level = Mathf.MoveTowards(level, target, rate * Time.deltaTime);

            if (!HasSignal && readout != null && readout.text != "-- dBm")
            {
                readout.text = "-- dBm";
            }
            DrawLeds();
        }

        private void LateUpdate()
        {
            if (mount != null)
            {
                SitOnPod();
                return;
            }
            if (follow == null)
            {
                return;
            }

            Vector3 forward = Vector3.ProjectOnPlane(follow.forward, Vector3.up);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.zero;
            float bob = Mathf.Sin(Time.time * 2.1f) * bobAmplitude;
            Vector3 targetPosition = follow.position + Vector3.up * (hoverHeight + bob) + forward * hoverForward;

            Quaternion targetRotation = transform.rotation;
            if (faceTowards != null)
            {
                // The meter's face points along -Z, so +Z looks away from the viewer.
                Vector3 away = Vector3.ProjectOnPlane(targetPosition - faceTowards.position, Vector3.up);
                if (away.sqrMagnitude > 1e-6f)
                {
                    targetRotation = Quaternion.LookRotation(away.normalized, Vector3.up);
                }
            }

            if (!hasPose || followSharpness <= 0f)
            {
                transform.SetPositionAndRotation(targetPosition, targetRotation);
                hasPose = true;
                return;
            }

            float t = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, targetPosition, t),
                Quaternion.Slerp(transform.rotation, targetRotation, t));
        }

        private void SitOnPod()
        {
            if (held) return;
            if (offsetLoadedFor != mount.CalibrationPath) LoadOffset();   // first frame, or the probe changed carrier
            Transform pod = mount.transform;
            if (aligned)
            {
                transform.SetPositionAndRotation(pod.TransformPoint(alignedPosition), pod.rotation * alignedRotation);
                return;
            }
            // Default: fixed in the pod's frame (square to the controller), just past its front edge, screen facing its top.
            float ahead = mount.ProbeSize.z * 0.5f + podGap + BodyBelowPivot;
            transform.SetPositionAndRotation(pod.TransformPoint(new Vector3(0f, 0f, ahead)), pod.rotation * OnPod);
        }

        private void DrawLeds()
        {
            if (block == null || ledColors.Length != leds.Length)
            {
                return;
            }

            float lit = level * leds.Length;
            bool flicker = level > 0.97f && Mathf.Repeat(Time.time * 9f, 1f) < 0.35f;
            for (int i = 0; i < leds.Length; i++)
            {
                if (leds[i] == null)
                {
                    continue;
                }

                // Partially lit LED for the fractional step, so the arc moves smoothly.
                float on = Mathf.Clamp01(lit - i);
                if (flicker && i == leds.Length - 1)
                {
                    on *= 0.4f;
                }
                float brightness = Mathf.Lerp(unlitBrightness, 1f, on);
                block.SetColor(BaseColorId, ledColors[i] * brightness);
                leds[i].SetPropertyBlock(block);
            }
        }

        private static string Shorten(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }
            return message.Length <= 40 ? message : message.Substring(0, 37) + "...";
        }
    }
}
