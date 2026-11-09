using System;
using System.IO;
using UnityEngine;
using Oculus.Interaction.Input;
using WifiHeatmap.Core;
using WifiHeatmap.Meter;

namespace WifiHeatmap.Probe
{
    /// <summary>
    /// Where the probe and its antenna sit: the pod clipped to the left or right Touch Plus controller, or a
    /// wrist-worn probe (an M5StickC Plus2 strapped on top of the forearm, long side toward the fingers) on the
    /// left or right hand, tracked by hand tracking. Place this as a child of the LeftControllerAnchor at the
    /// pod's centre; the Antenna child marks the antenna. SetRightHanded and SetOnHand move it between the four
    /// carriers, and each carrier keeps its own saved calibration (the probe's, and the EMF meter's placement on it). Default offsets are estimates, not calibrations:
    /// the controller ones come from the CAD, the wrist one from the tracked hand's joints the first time it is seen.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProbeMount : MonoBehaviour
    {
        // Estimated from hardware/probe_mount.py on Meta's Touch Plus render model, which sits at
        // identity under the controller anchor: the ring clip is centred on the face plate and the
        // pod lies 31-61 mm out from it toward the trigger, 23.3 mm tall. Expect errors of 1-2 cm
        // until the mount is calibrated on a printed part. These are the left-hand offsets; the right-hand
        // estimate mirrors them across controller-local X. Neither is a measured calibration.
        public static readonly Vector3 EstimatedPodPosition = new Vector3(0.002f, 0.017f, 0.058f);
        public static readonly Vector3 EstimatedAntennaOffset = new Vector3(0.001f, 0.011f, 0.0f);
        public static readonly Vector3 PodSize = new Vector3(0.0492f, 0.0233f, 0.0301f);
        // The wrist probe in the same axes (x across the wrist, y out of the screen, z toward the fingers):
        // the M5StickC Plus2's case, 25 x 13.5 x 48 mm.
        public static readonly Vector3 WristProbeSize = new Vector3(0.025f, 0.0135f, 0.048f);
        // Wrist estimate, from the hand's own joints: the device's centre this far back from the wrist joint toward
        // the elbow and this far out from the back of the wrist (half the wrist's thickness plus half the case).
        private const float WristEstimateBack = 0.045f, WristEstimateUp = 0.03f;

        [Tooltip("Hardware mount this offset belongs to; a different mount, cable route or board means a new calibration.")]
        [SerializeField] private string mountId = "probe_mount_left (ring clip pod)";
        [Tooltip("The mount on the right controller, which has its own calibration.")]
        [SerializeField] private string rightMountId = "probe_mount_right (ring clip pod)";
        [Tooltip("The wrist-worn probe on each hand (hand tracking), each with its own calibration.")]
        [SerializeField] private string leftWristMountId = "m5stickc_plus2_wrist_left";
        [SerializeField] private string rightWristMountId = "m5stickc_plus2_wrist_right";
        [Tooltip("Identifies the calibration that produced this transform. 'estimate' means uncalibrated.")]
        [SerializeField] private string calibrationId = "estimate";
        [SerializeField] private Transform antenna;
        [Tooltip("The scene's translucent pod box. Only its material is used now: the probe is shown as a ball at the " +
                 "antenna with X/Y/Z arrows (the target), built at runtime, which fits any board or mount.")]
        [SerializeField] private Renderer placeholder;
        [Tooltip("The target's default when no setting is saved yet (Meter > Probe target). It shows anyway while " +
                 "calibrating (CalibrationOpen, or aligning the meter).")]
        [SerializeField] private bool showPlaceholder;

        private const string SidePreference = "probe-controller-side";
        private const string CarrierPreference = "probe-carrier-hand";
        private const string TargetPreference = "probe-target-shown";
        private bool right, onHand;
        private bool wristEstimatePending;              // the wrist estimate waits for the first confident hand pose
        private OVRPlugin.HandState handState;
        private bool otherIndexWasPinched, otherMiddleWasPinched;
        private Hand[] hands = new Hand[0];             // the Interaction SDK hands, found when an alignment begins
        private float untrackedSince = -1f;             // when the wrist's hand tracking dropped during an alignment
        private float captureRequestedUntil;            // a capture asked for while the hand was briefly untracked
        private const float HandTrackingGrace = 1.5f;   // hand confidence dips when the other hand comes close to pinch
        private const float PinchOn = 0.02f, PinchOff = 0.035f;   // thumb-to-fingertip distances, metres

        /// <summary>The probe's controller (LTouch/RTouch), or its hand (LHand/RHand) when worn on the wrist.
        /// Its tracking is the probe's; a controller's buttons and haptics are too.</summary>
        public OVRInput.Controller Controller => onHand
            ? (right ? OVRInput.Controller.RHand : OVRInput.Controller.LHand)
            : (right ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch);
        public bool IsRightHanded => right;
        /// <summary>True when the probe is worn on the wrist and tracked by hand tracking rather than on a controller.</summary>
        public bool IsOnHand => onHand;
        public string SideName => right ? "right" : "left";
        public string CarrierName => onHand ? $"{SideName} wrist" : $"{SideName} controller";
        public string MountId => onHand ? (right ? rightWristMountId : leftWristMountId) : (right ? rightMountId : mountId);
        /// <summary>The probe's outer size in its own axes: the controller pod, or the wrist device's case.</summary>
        public Vector3 ProbeSize => onHand ? WristProbeSize : PodSize;
        public string CalibrationId => calibrationId;
        public bool IsCalibrated => !probeUnsaved && !string.IsNullOrEmpty(calibrationId) && calibrationId != "estimate";
        /// <summary>The probe's offset or the EMF meter's placement on it differs from what is saved.</summary>
        public bool HasUnsavedChanges => probeUnsaved || (Meter != null && Meter.HasUnsavedOffset);
        /// <summary>The probe's own offset is unsaved (what readings are placed with); the meter's place doesn't count.</summary>
        public bool HasUnsavedProbeOffset => probeUnsaved;
        private bool probeUnsaved;
        /// <summary>A frozen alignment is under way, of the probe or (IsMeterAlignment) of the meter.</summary>
        public bool IsFrozenAlignment => frozenTarget != null;
        public bool IsMeterAlignment => frozenTarget != null && alignMeter;
        private GameObject frozenTarget;
        private bool alignMeter;
        [Tooltip("The EMF meter fixed to this probe (Align meter). Empty: the meter in the scene whose mount this is.")]
        [SerializeField] private EmfMeter meter;
        public EmfMeter Meter
        {
            get
            {
                if (meter == null)
                    foreach (EmfMeter candidate in FindObjectsByType<EmfMeter>(FindObjectsSortMode.None))
                        if (candidate.Mount == this) { meter = candidate; break; }
                return meter;
            }
        }
        private bool triggerReleased;
        private string calibrationMethod = "manual-visual-alignment";
        public bool CalibrationLocked { get; set; }
        public string CalibrationMessage { get; private set; } = "Using scene estimate.";
        // The target: a ball centred on the antenna, with the probe's axes as arrows (X red across, Y green up from
        // the face or screen, Z blue along the probe: out toward the trigger, or toward the fingers on the wrist).
        public const float TargetBallDiameter = 0.016f, TargetArrowLength = 0.04f;
        private Transform target;
        private readonly System.Collections.Generic.List<UnityEngine.Object> targetAssets = new System.Collections.Generic.List<UnityEngine.Object>();

        public string CalibrationPath => Path.Combine(Application.persistentDataPath,
            onHand ? $"probe-calibration-{SideName}-wrist.json" : $"probe-calibration-{SideName}.json");
        public event Action CalibrationChanged;

        private void Awake()
        {
            bool savedRight = false, savedHand = false;
            try
            {
                savedRight = PlayerPrefs.GetInt(SidePreference, 0) == 1;
                savedHand = PlayerPrefs.GetInt(CarrierPreference, 0) == 1;
                showPlaceholder = PlayerPrefs.GetInt(TargetPreference, showPlaceholder ? 1 : 0) == 1;
            }
            catch (Exception e) { Debug.LogWarning("Probe carrier preference unreadable: " + e.Message, this); }
            if (savedRight || savedHand) SetCarrier(savedRight, savedHand, false);
            else if (File.Exists(CalibrationPath)) LoadCalibration();
        }

        /// <summary>Moves the probe to the other side, keeping it on a controller or the wrist.</summary>
        public bool SetRightHanded(bool value, bool remember = true) => SetCarrier(value, onHand, remember);

        /// <summary>Moves the probe between the controller and the wrist (hand tracking), keeping its side.</summary>
        public bool SetOnHand(bool value, bool remember = true) => SetCarrier(right, value, remember);

        /// <summary>Moves the probe to a side and carrier, with that carrier's saved offset or its estimate.
        /// Refused while locked, mid-alignment, or with an unsaved offset that the move would discard.</summary>
        public bool SetCarrier(bool rightSide, bool hand, bool remember = true)
        {
            if (rightSide == right && hand == onHand) return true;
            if (CalibrationLocked || IsFrozenAlignment) return false;
            if (HasUnsavedChanges)
            {
                CalibrationMessage = "Save or Load the offset before moving the probe.";
                CalibrationChanged?.Invoke();
                return false;
            }
            Transform anchor = CarrierAnchor(rightSide, hand);
            if (anchor == null)
            {
                CalibrationMessage = $"No {(rightSide ? "right" : "left")} {(hand ? "hand" : "controller")} anchor in the camera rig.";
                CalibrationChanged?.Invoke();
                return false;
            }
            right = rightSide;
            onHand = hand;
            transform.SetParent(anchor, false);
            if (remember)
            {
                try
                {
                    PlayerPrefs.SetInt(SidePreference, right ? 1 : 0);
                    PlayerPrefs.SetInt(CarrierPreference, onHand ? 1 : 0);
                    PlayerPrefs.Save();
                }
                catch (Exception e) { Debug.LogWarning("Probe carrier preference not saved: " + e.Message, this); }
            }
            ApplyEstimate();
            probeUnsaved = false;
            string side = $"Probe on the {CarrierName}: ";
            if (!File.Exists(CalibrationPath)) CalibrationMessage = side + "using the estimate until you align and save it.";
            else if (LoadCalibration()) CalibrationMessage = side + "saved offset loaded.";
            else CalibrationMessage = side + "using the estimate. " + CalibrationMessage;
            CalibrationChanged?.Invoke();
            return true;
        }

        /// <summary>The controller anchor, or for the wrist the hand anchor, which follows the tracked hand's root
        /// (the wrist) through the same late-latched rig path as a controller.</summary>
        private Transform CarrierAnchor(bool rightSide, bool hand)
        {
            var rig = GetComponentInParent<OVRCameraRig>(true);
            string name = hand ? (rightSide ? "RightHandAnchor" : "LeftHandAnchor")
                               : (rightSide ? "RightControllerAnchor" : "LeftControllerAnchor");
            return rig == null ? null : FindChild(rig.transform, name);
        }

        private static Transform FindChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                Transform found = FindChild(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>This carrier's estimate: the left-hand controller offsets, mirrored for the right controller; on
        /// the wrist, derived from the tracked hand's joints (see TryWristEstimate) once the hand is seen.</summary>
        private void ApplyEstimate()
        {
            if (onHand)
            {
                // Antenna at the device's centre: the Plus2's antenna position inside its case isn't modelled.
                transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                Antenna.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                wristEstimatePending = true;
                TryWristEstimate();
            }
            else
            {
                transform.SetLocalPositionAndRotation(ForSide(EstimatedPodPosition), Quaternion.identity);
                Antenna.SetLocalPositionAndRotation(ForSide(EstimatedAntennaOffset), Quaternion.identity);
                wristEstimatePending = false;
            }
            if (placeholder != null) placeholder.transform.localScale = ProbeSize;
            calibrationId = "estimate";
            calibrationMethod = "manual-visual-alignment";
        }

        /// <summary>
        /// Places the wrist estimate from the tracked hand's joints, independent of the runtime's hand-root axis
        /// convention: z from the wrist toward the middle knuckle, y out of the back of the hand (from the index and
        /// little knuckles, mirrored by handedness), the device centred behind the wrist joint on top of the forearm.
        /// The hand root follows the hand, not the forearm, so bending the wrist moves this estimate too.
        /// </summary>
        private bool TryWristEstimate()
        {
            if (!wristEstimatePending || transform.parent == null || !HandConfident(Controller == OVRInput.Controller.RHand)) return false;
            Handedness wanted = right ? Handedness.Right : Handedness.Left;
            foreach (Hand hand in FindObjectsByType<Hand>(FindObjectsSortMode.None))
            {
                if (hand.Handedness != wanted || !hand.IsTrackedDataValid || !hand.IsHighConfidence) continue;
                if (!hand.GetJointPose(HandJointId.HandWristRoot, out Pose wrist) ||
                    !hand.GetJointPose(HandJointId.HandMiddle1, out Pose middle) ||
                    !hand.GetJointPose(HandJointId.HandIndex1, out Pose index) ||
                    !hand.GetJointPose(HandJointId.HandPinky1, out Pose little)) continue;
                Vector3 fingers = middle.position - wrist.position;
                Vector3 back = Vector3.Cross(index.position - wrist.position, little.position - wrist.position) * (right ? 1f : -1f);
                if (fingers.sqrMagnitude < 1e-6f || back.sqrMagnitude < 1e-8f) continue;
                fingers.Normalize();
                back = Vector3.ProjectOnPlane(back, fingers).normalized;
                Vector3 centre = wrist.position - fingers * WristEstimateBack + back * WristEstimateUp;
                Transform anchor = transform.parent;
                transform.SetLocalPositionAndRotation(anchor.InverseTransformPoint(centre),
                    (Quaternion.Inverse(anchor.rotation) * Quaternion.LookRotation(fingers, back)).normalized);
                wristEstimatePending = false;
                CalibrationMessage = $"Wrist estimate placed from your {SideName} hand. Freeze and fit the device to refine it.";
                CalibrationChanged?.Invoke();
                return true;
            }
            return false;
        }

        private Vector3 ForSide(Vector3 left)
        {
            if (!right) return left;
            float[] m = ProbeMirror.Position(new[] { left.x, left.y, left.z });
            return new Vector3(m[0], m[1], m[2]);
        }

        public ProbeCalibration Snapshot(string id = null)
        {
            Vector3 p = transform.localPosition, a = Antenna.localPosition;
            Quaternion r = transform.localRotation, b = Antenna.localRotation;
            return new ProbeCalibration
            {
                MountId = MountId, Controller = Controller.ToString(), HeadsetModel = SystemInfo.deviceModel,
                CalibrationId = id ?? calibrationId, SavedUtc = DateTime.UtcNow,
                Method = id != null || IsCalibrated || probeUnsaved ? calibrationMethod : "uncalibrated-estimate",
                PodPosition = new[] { p.x, p.y, p.z }, PodRotation = new[] { r.x, r.y, r.z, r.w },
                AntennaPosition = new[] { a.x, a.y, a.z }, AntennaRotation = new[] { b.x, b.y, b.z, b.w },
            };
        }

        public bool SaveCalibration()
        {
            if (CalibrationLocked || IsFrozenAlignment) return false;
            try
            {
                // Only the meter changed: keep the probe's saved offset (or estimate) as it is.
                bool meterChanged = Meter != null && Meter.HasUnsavedOffset;
                if (probeUnsaved || !meterChanged)
                {
                    var data = Snapshot("manual-" + Guid.NewGuid().ToString("N"));
                    ProbeCalibrationFile.Save(CalibrationPath, data);
                    calibrationId = data.CalibrationId;
                    probeUnsaved = false;
                }
                if (meterChanged) Meter.SaveOffset();
                CalibrationMessage = meterChanged ? (Meter.IsAligned ? "Saved, with the meter's aligned placement." :
                    "Saved; the meter is back in its default place.") : "Offset saved (visual alignment).";
                CalibrationChanged?.Invoke();
                return true;
            }
            catch (Exception e) { CalibrationMessage = "Save failed: " + e.Message; return false; }
        }

        public bool LoadCalibration()
        {
            if (CalibrationLocked) return false;
            if (IsMeterAlignment) CancelFrozenAlignment();
            Meter?.LoadOffset();   // the meter's saved placement, or its default
            try
            {
                var data = ProbeCalibrationFile.Load(CalibrationPath, MountId, Controller.ToString(), SystemInfo.deviceModel);
                CancelFrozenAlignment();
                calibrationMethod = data.Method;
                // Validate the entire document before changing either transform.
                transform.localPosition = new Vector3(data.PodPosition[0], data.PodPosition[1], data.PodPosition[2]);
                transform.localRotation = new Quaternion(data.PodRotation[0], data.PodRotation[1], data.PodRotation[2], data.PodRotation[3]);
                Antenna.localPosition = new Vector3(data.AntennaPosition[0], data.AntennaPosition[1], data.AntennaPosition[2]);
                Antenna.localRotation = new Quaternion(data.AntennaRotation[0], data.AntennaRotation[1], data.AntennaRotation[2], data.AntennaRotation[3]);
                calibrationId = data.CalibrationId;
                probeUnsaved = false;
                CalibrationMessage = "Saved offset loaded.";
                CalibrationChanged?.Invoke();
                return true;
            }
            catch (Exception e) { CalibrationMessage = "Load failed: " + e.Message; return false; }
        }

        public void ResetCalibration()
        {
            if (CalibrationLocked) return;
            CancelFrozenAlignment();
            ApplyEstimate();
            Meter?.ResetOffset();
            probeUnsaved = true;
            CalibrationMessage = "Scene estimate restored, and the meter's default place. Save to replace the saved ones, or Load to undo.";
            CalibrationChanged?.Invoke();
        }
        /// <summary>Freeze a visual target only. The real antenna remains parented to its controller,
        /// and sample ingestion/recording is paused via IsFrozenAlignment until capture or cancellation.</summary>
        public bool BeginFrozenAlignment()
        {
            if (CalibrationLocked || IsFrozenAlignment || transform.parent == null || EnsureTarget() == null)
                return false;
            if (!ProbeTracked())
            {
                CalibrationMessage = onHand && wristEstimatePending
                    ? $"Show your {SideName} hand to the headset so the wrist estimate can be placed."
                    : $"Track the {CarrierName} before freezing the target.";
                return false;
            }
            alignMeter = false;
            frozenTarget = new GameObject("Frozen probe alignment target");
            frozenTarget.transform.SetPositionAndRotation(transform.position, transform.rotation);
            frozenTarget.transform.localScale = transform.lossyScale;
            // Copy the target (ball and arrows) at the same pose under the frozen mount pose; capturing then moves
            // the mount so its live target lands where this one was. Not the mount or antenna themselves.
            PlaceTarget();
            GameObject visual = Instantiate(target.gameObject, frozenTarget.transform, false);
            visual.name = "Antenna target";
            visual.SetActive(true);
            visual.transform.SetLocalPositionAndRotation(target.localPosition, target.localRotation);
            triggerReleased = false; // The click used to start alignment must not capture it.
            hands = FindObjectsByType<Hand>(FindObjectsSortMode.None);
            untrackedSince = -1f; captureRequestedUntil = 0f;
            otherIndexWasPinched = otherMiddleWasPinched = true;   // a pinch already held must be released first
            ApplyPlaceholder();
            CalibrationMessage = $"Centre the probe's antenna in the frozen ball and line it up with the arrows ({AxesHint}). " +
                $"{CaptureInstruction} to attach; {CancelInstruction} cancels. Save offset afterwards.";
            CalibrationChanged?.Invoke();
            return true;
        }

        /// <summary>The same freeze-and-fit for the EMF meter: the meter stays where it is in the room while you move
        /// the probe until the meter sits where you want it on the probe; capturing fixes it there, relative to the
        /// probe. Sample ingestion pauses as for the probe's alignment.</summary>
        public bool BeginMeterAlignment()
        {
            if (CalibrationLocked || IsFrozenAlignment || transform.parent == null) return false;
            if (Meter == null) { CalibrationMessage = "No EMF meter is fixed to this probe."; CalibrationChanged?.Invoke(); return false; }
            if (!ProbeTracked())
            {
                CalibrationMessage = onHand && wristEstimatePending
                    ? $"Show your {SideName} hand to the headset so the wrist estimate can be placed."
                    : $"Track the {CarrierName} before freezing the meter.";
                return false;
            }
            alignMeter = true;
            frozenTarget = new GameObject("Frozen meter alignment target");
            frozenTarget.transform.SetPositionAndRotation(Meter.transform.position, Meter.transform.rotation);
            Meter.Hold(true);
            triggerReleased = false;
            hands = FindObjectsByType<Hand>(FindObjectsSortMode.None);
            untrackedSince = -1f; captureRequestedUntil = 0f;
            otherIndexWasPinched = otherMiddleWasPinched = true;
            ApplyPlaceholder();
            CalibrationMessage = $"The meter is frozen in the room. Move the {CarrierName} until the meter sits where you want it. " +
                $"{CaptureInstruction} to attach it; {CancelInstruction} cancels. Save offset afterwards.";
            CalibrationChanged?.Invoke();
            return true;
        }

        public bool CaptureFrozenAlignment()
        {
            if (!IsFrozenAlignment || CalibrationLocked || !ProbeTracked() || transform.parent == null) return false;
            if (alignMeter)
            {
                Vector3 local = transform.InverseTransformPoint(frozenTarget.transform.position);
                if (local.sqrMagnitude > .5f * .5f || !float.IsFinite(local.sqrMagnitude))
                {
                    CalibrationMessage = $"Bring the probe within 50 cm of the frozen meter, then {CaptureInstruction.ToLowerInvariant()}.";
                    return false;
                }
                Meter.SetAlignedOffset(local, Quaternion.Inverse(transform.rotation) * frozenTarget.transform.rotation);
                RemoveFrozenTarget();
                CalibrationMessage = "Meter attached where you placed it. Open Mount > Save offset to keep it after restart.";
                CalibrationChanged?.Invoke();
                return true;
            }
            Transform controller = transform.parent;
            Vector3 position = controller.InverseTransformPoint(frozenTarget.transform.position);
            Quaternion rotation = Quaternion.Inverse(controller.rotation) * frozenTarget.transform.rotation;
            // A misplaced controller must not produce a silently clamped calibration.
            if (position.sqrMagnitude > .5f * .5f || !float.IsFinite(position.sqrMagnitude))
            {
                CalibrationMessage = $"Move the probe into the frozen target (within 50 cm of the {CarrierName}), then {CaptureInstruction.ToLowerInvariant()}.";
                return false;
            }
            transform.SetLocalPositionAndRotation(position, rotation.normalized);
            probeUnsaved = true;
            calibrationId = "estimate";
            calibrationMethod = "frozen-target-visual-alignment";
            RemoveFrozenTarget();
            CalibrationMessage = "Probe attached at the aligned pose. Open Mount > Save offset to keep it after restart.";
            CalibrationChanged?.Invoke();
            return true;
        }

        public void CancelFrozenAlignment()
        {
            if (!IsFrozenAlignment) return;
            bool wasMeter = alignMeter;
            RemoveFrozenTarget();
            CalibrationMessage = wasMeter ? "Meter alignment cancelled; its previous place retained." :
                "Frozen alignment cancelled; previous offset retained.";
            CalibrationChanged?.Invoke();
        }

        private void RemoveFrozenTarget()
        {
            if (frozenTarget != null) { frozenTarget.SetActive(false); Destroy(frozenTarget); }
            frozenTarget = null;
            if (alignMeter && meter != null) meter.Hold(false);
            alignMeter = false;
            ApplyPlaceholder();
        }

        /// <summary>The probe controller's upper face button (Y on the left, B on the right), which cancels alignment.</summary>
        public string CancelButtonName => right ? "B" : "Y";
        /// <summary>The probe controller's lower face button (X on the left, A on the right), which toggles the menu.</summary>
        public string MenuButtonName => right ? "A" : "X";
        /// <summary>The other hand, which pinches to capture and cancel while the probe hand holds still.</summary>
        public string OtherSideName => right ? "left" : "right";
        /// <summary>Which way each arrow points on the real probe, for the alignment instructions.</summary>
        public string AxesHint => onHand
            ? "blue toward your fingers, green out of the screen"
            : "blue along the probe toward the trigger, green up from the controller face";
        public string CaptureInstruction => onHand
            ? $"Pinch {OtherSideName} index finger and thumb (or press the probe's A button)"
            : $"Release, then press the {SideName.ToUpperInvariant()} trigger";
        public string CancelInstruction => onHand
            ? $"a {OtherSideName} middle-finger pinch (or the probe's B button)"
            : $"{SideName.ToUpperInvariant()} {CancelButtonName}";

        /// <summary>Whether the probe's carrier has a trustworthy pose this frame: its controller tracked, or for the
        /// wrist, its hand tracked with high confidence and the wrist estimate placed.</summary>
        public bool ProbeTracked()
        {
            if (!onHand)
                return OVRInput.GetControllerPositionTracked(Controller) && OVRInput.GetControllerOrientationTracked(Controller);
            return !wristEstimatePending && HandConfident(right);
        }

        private bool HandConfident(bool rightHand)
        {
            if (!OVRPlugin.GetHandState(OVRPlugin.Step.Render, rightHand ? OVRPlugin.Hand.HandRight : OVRPlugin.Hand.HandLeft, ref handState))
                return false;
            return (handState.Status & OVRPlugin.HandStatus.HandTracked) != 0 &&
                   handState.HandConfidence == OVRPlugin.TrackingConfidence.High;
        }

        /// <summary>A pinch starting this frame on the other hand. Pinch-to-button mapping is off in this project, and the
        /// runtime's pinch flags aren't always reported, so the fingertip distance counts too (with hysteresis).</summary>
        private bool OtherHandPinchDown(HandFinger finger, ref bool wasPinched)
        {
            Handedness other = right ? Handedness.Left : Handedness.Right;
            bool middle = finger == HandFinger.Middle, flagged = false;
            float gap = float.PositiveInfinity, otherGap = float.PositiveInfinity;
            foreach (Hand hand in hands)
            {
                if (hand == null || hand.Handedness != other || !hand.IsTrackedDataValid) continue;
                flagged |= hand.GetFingerIsPinching(finger);
                if (hand.GetJointPose(HandJointId.HandThumbTip, out Pose thumb) &&
                    hand.GetJointPose(HandJointId.HandIndexTip, out Pose index) &&
                    hand.GetJointPose(HandJointId.HandMiddleTip, out Pose middleTip))
                {
                    float toIndex = Vector3.Distance(thumb.position, index.position);
                    float toMiddle = Vector3.Distance(thumb.position, middleTip.position);
                    gap = Mathf.Min(gap, middle ? toMiddle : toIndex);
                    otherGap = Mathf.Min(otherGap, middle ? toIndex : toMiddle);
                }
            }
            var raw = middle ? OVRPlugin.HandFingerPinch.Middle : OVRPlugin.HandFingerPinch.Index;
            flagged |= OVRPlugin.GetHandState(OVRPlugin.Step.Render, right ? OVRPlugin.Hand.HandLeft : OVRPlugin.Hand.HandRight, ref handState) &&
                       (handState.Status & OVRPlugin.HandStatus.InputStateValid) != 0 && (handState.Pinches & raw) != 0;
            // An index pinch brings the middle tip close too, so a distance pinch belongs to the closer finger.
            bool near = gap < PinchOn && gap < otherGap;
            bool pinched = flagged || near || (wasPinched && gap <= PinchOff);
            bool down = pinched && !wasPinched;
            wasPinched = pinched;
            return down;
        }

        /// <summary>Captures now, or within a second if the wrist's tracking has just dipped.</summary>
        private void RequestCapture()
        {
            if (ProbeTracked()) CaptureFrozenAlignment();
            else
            {
                captureRequestedUntil = Time.unscaledTime + 1f;
                CalibrationMessage = $"Capturing as soon as the {SideName} hand is tracked again...";
            }
        }

        /// <summary>Probe hardware buttons (wrist probe, protocol 1.1): A captures and B cancels a frozen alignment.</summary>
        public void ProbeButtonPressed(char button)
        {
            if (!IsFrozenAlignment) return;
            if (button == 'B') CancelFrozenAlignment();
            else if (button == 'A') RequestCapture();
        }

        private void LateUpdate()
        {
            PlaceTarget();   // follows the antenna through estimates, loads and captures
            if (wristEstimatePending) TryWristEstimate();
            if (!IsFrozenAlignment) return;
            if (CalibrationLocked) { CancelFrozenAlignment(); return; }
            bool tracked = ProbeTracked();
            if (onHand)
            {
                // Only a sustained loss cancels: the wrist device and the pinching hand both lower hand confidence.
                if (tracked) untrackedSince = -1f;
                else if (untrackedSince < 0f) untrackedSince = Time.unscaledTime;
                else if (Time.unscaledTime - untrackedSince > HandTrackingGrace)
                {
                    CancelFrozenAlignment();
                    CalibrationMessage = $"Alignment cancelled: the {SideName} hand lost tracking. Keep it in view and try again.";
                    return;
                }
                bool cancel = OtherHandPinchDown(HandFinger.Middle, ref otherMiddleWasPinched);
                bool capture = OtherHandPinchDown(HandFinger.Index, ref otherIndexWasPinched);
                if (cancel) { CancelFrozenAlignment(); return; }
                if (capture) RequestCapture();
                else if (tracked && Time.unscaledTime < captureRequestedUntil) { captureRequestedUntil = 0f; CaptureFrozenAlignment(); }
                return;
            }
            if (!tracked) { CancelFrozenAlignment(); return; }
            if (OVRInput.GetDown(OVRInput.Button.Two, Controller)) { CancelFrozenAlignment(); return; }
            if (!OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, Controller)) triggerReleased = true;
            if (triggerReleased && OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, Controller))
                CaptureFrozenAlignment();
        }

        private void OnApplicationFocus(bool focused) { if (!focused) CancelFrozenAlignment(); }
        private void OnApplicationPause(bool paused) { if (paused) CancelFrozenAlignment(); }
        private void OnTrackingOriginChanged(OVRManager.TrackingOrigin origin, OVRPose? previous) { CancelFrozenAlignment(); }
        private void OnDisable()
        {
            OVRManager.TrackingOriginChangePending -= OnTrackingOriginChanged;
            CancelFrozenAlignment();
        }

        public Transform Antenna => antenna != null ? antenna : transform;

        /// <summary>Antenna pose in world space for the current frame.</summary>
        public Pose AntennaPose => new Pose(Antenna.position, Antenna.rotation);

        /// <summary>The setting (Meter > Probe target), remembered across restarts: show the target outside calibration.</summary>
        public bool ShowPlaceholder
        {
            get => showPlaceholder;
            set
            {
                showPlaceholder = value;
                try { PlayerPrefs.SetInt(TargetPreference, value ? 1 : 0); PlayerPrefs.Save(); }
                catch (Exception e) { Debug.LogWarning("Probe target preference not saved: " + e.Message, this); }
                ApplyPlaceholder();
            }
        }

        /// <summary>The calibration panel is open: the target shows whatever the setting, and goes back to it after.</summary>
        public bool CalibrationOpen
        {
            get => calibrationOpen;
            set { calibrationOpen = value; ApplyPlaceholder(); }
        }
        private bool calibrationOpen;

        private void OnEnable()
        {
            OVRManager.TrackingOriginChangePending += OnTrackingOriginChanged;
            ApplyPlaceholder();
        }

        private void OnValidate()
        {
            if (placeholder != null) placeholder.enabled = false;
        }

        private void ApplyPlaceholder()
        {
            if (placeholder != null) placeholder.enabled = false;   // the box is replaced by the target
            if (!Application.isPlaying) return;
            Transform shown = EnsureTarget();
            // While the probe is fitted to its frozen copy the live one hides; aligning the meter shows the probe's.
            bool calibrating = calibrationOpen || IsMeterAlignment;
            if (shown != null) shown.gameObject.SetActive((showPlaceholder || calibrating) && (!IsFrozenAlignment || IsMeterAlignment));
        }

        /// <summary>The live target: at the antenna, in the probe's own axes (the mount's rotation, not the antenna's).</summary>
        private void PlaceTarget()
        {
            if (target != null) target.SetLocalPositionAndRotation(transform.InverseTransformPoint(Antenna.position), Quaternion.identity);
        }

        private Transform EnsureTarget()
        {
            if (target != null || !Application.isPlaying) return target;
            Material baseMaterial = placeholder != null ? placeholder.sharedMaterial : null;
            if (baseMaterial == null) return null;
            target = new GameObject("Probe target (antenna)").transform;
            target.SetParent(transform, false);
            AddPart(target, "Ball", NewSphere(), Vector3.zero, Quaternion.identity,
                Vector3.one * TargetBallDiameter, Tinted(baseMaterial, new Color(.2f, .9f, 1f, .45f)));
            Mesh arrow = ArrowMesh();
            AddPart(target, "X", arrow, Vector3.zero, Quaternion.Euler(0f, 90f, 0f), Vector3.one, Tinted(baseMaterial, new Color(1f, .25f, .25f, .9f)));
            AddPart(target, "Y", arrow, Vector3.zero, Quaternion.Euler(-90f, 0f, 0f), Vector3.one, Tinted(baseMaterial, new Color(.3f, 1f, .3f, .9f)));
            AddPart(target, "Z", arrow, Vector3.zero, Quaternion.identity, Vector3.one, Tinted(baseMaterial, new Color(.3f, .5f, 1f, .9f)));
            PlaceTarget();
            return target;
        }

        private Mesh NewSphere()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh mesh = temp.GetComponent<MeshFilter>().sharedMesh;   // Unity's built-in sphere, shared, not owned
            // At once, not at the end of the frame: for that frame its collider sits at the world origin, where the
            // interaction rig's locomotor, starting up, takes it for ground and keeps its gravity on (NoRigLocomotion).
            DestroyImmediate(temp);
            return mesh;
        }

        private Material Tinted(Material source, Color color)
        {
            var material = new Material(source) { name = source.name + " (target)" };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            targetAssets.Add(material);
            return material;
        }

        private static void AddPart(Transform parent, string name, Mesh mesh, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            var part = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(parent, false);
            part.transform.SetLocalPositionAndRotation(position, rotation);
            part.transform.localScale = scale;
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>An arrow along +Z from the ball's surface: a square shaft and a pyramid tip, in metres.</summary>
        private Mesh ArrowMesh()
        {
            float s = .0011f, start = TargetBallDiameter / 2f, neck = TargetArrowLength - .009f, tip = TargetArrowLength, h = .0035f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int i = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d); t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 }); }
            void Tri(Vector3 a, Vector3 b, Vector3 c) { int i = v.Count; v.Add(a); v.Add(b); v.Add(c); t.AddRange(new[] { i, i + 1, i + 2 }); }
            Vector3 P(float x, float y, float z) => new Vector3(x, y, z);
            // shaft sides
            Quad(P(-s, -s, start), P(-s, s, start), P(-s, s, neck), P(-s, -s, neck));
            Quad(P(s, -s, start), P(s, -s, neck), P(s, s, neck), P(s, s, start));
            Quad(P(-s, s, start), P(s, s, start), P(s, s, neck), P(-s, s, neck));
            Quad(P(-s, -s, start), P(-s, -s, neck), P(s, -s, neck), P(s, -s, start));
            // tip: base square and four faces
            Quad(P(-h, -h, neck), P(-h, h, neck), P(h, h, neck), P(h, -h, neck));
            Vector3 apex = P(0, 0, tip);
            Tri(P(-h, -h, neck), apex, P(-h, h, neck));
            Tri(P(-h, h, neck), apex, P(h, h, neck));
            Tri(P(h, h, neck), apex, P(h, -h, neck));
            Tri(P(h, -h, neck), apex, P(-h, -h, neck));
            var mesh = new Mesh { name = "Probe target arrow" };
            mesh.SetVertices(v); mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            targetAssets.Add(mesh);
            return mesh;
        }

        private void OnDestroy()
        {
            foreach (UnityEngine.Object asset in targetAssets) if (asset != null) Destroy(asset);
            targetAssets.Clear();
        }

        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.8f);
            Gizmos.DrawWireCube(Vector3.zero, ProbeSize);
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(Antenna.position, TargetBallDiameter / 2f);
        }
    }
}
