using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WifiHeatmap.Probe;
using WifiHeatmap.Recording;

namespace WifiHeatmap.UI
{
    /// <summary>Probe carrier (left/right, controller/wrist) and freeze-and-fit calibration of the probe and of the EMF
    /// meter's place on it. Never moves a room anchor.</summary>
    public sealed class ProbeCalibrationPanel : MonoBehaviour
    {
        [SerializeField] private ProbeMount mount;
        [SerializeField] private SessionRecorder recorder;
        [SerializeField] private Button sideButton;
        [SerializeField] private Button carrierButton;
        [SerializeField] private Button freezeButton;
        [Tooltip("Freeze-and-fit for the EMF meter. Empty: added beside the freeze button at runtime.")]
        [SerializeField] private Button meterButton;
        [SerializeField] private Button saveButton;
        [SerializeField] private Button loadButton;
        [SerializeField] private Button resetButton;
        [SerializeField] private TMP_Text info;

        private void Awake()
        {
            sideButton.onClick.AddListener(() => mount.SetRightHanded(!mount.IsRightHanded));
            carrierButton.onClick.AddListener(() => mount.SetOnHand(!mount.IsOnHand));
            saveButton.onClick.AddListener(() => mount.SaveCalibration());
            loadButton.onClick.AddListener(() => mount.LoadCalibration());
            resetButton.onClick.AddListener(() => mount.ResetCalibration());
            freezeButton.onClick.AddListener(() =>
            {
                if (mount.IsFrozenAlignment) mount.CancelFrozenAlignment();
                else if (mount.BeginFrozenAlignment()) GetComponentInParent<ProbeConfigMenu>().Hide();
            });
            EnsureMeterButton();
            meterButton.onClick.AddListener(() =>
            {
                if (mount.IsFrozenAlignment) mount.CancelFrozenAlignment();
                else if (mount.BeginMeterAlignment()) GetComponentInParent<ProbeConfigMenu>().Hide();
            });
        }

        // The probe target shows while this panel (the Mount tab) is open, whatever the Meter tab's setting.
        private void OnEnable() { if (mount != null) mount.CalibrationOpen = true; }
        private void OnDisable() { if (mount != null) mount.CalibrationOpen = false; }

        private void Update()
        {
            if (mount == null) return;
            bool locked = mount.CalibrationLocked || (recorder != null && recorder.IsRecording);
            freezeButton.interactable = !locked && !mount.IsMeterAlignment;
            meterButton.interactable = !locked && mount.Meter != null && (!mount.IsFrozenAlignment || mount.IsMeterAlignment);
            sideButton.interactable = carrierButton.interactable = saveButton.interactable = !locked && !mount.IsFrozenAlignment;
            loadButton.interactable = resetButton.interactable = !locked;
            MenuStyle.SetLabel(sideButton, mount.IsRightHanded ? "Side: right" : "Side: left");
            MenuStyle.SetLabel(carrierButton, mount.IsOnHand ? "On: wrist (hands)" : "On: controller");
            bool probeAlign = mount.IsFrozenAlignment && !mount.IsMeterAlignment;
            MenuStyle.SetLabel(freezeButton, probeAlign ? "Cancel probe alignment" : "Align probe");
            MenuStyle.Tint(freezeButton, probeAlign);
            MenuStyle.SetLabel(meterButton, mount.IsMeterAlignment ? "Cancel meter alignment" : "Align meter");
            MenuStyle.Tint(meterButton, mount.IsMeterAlignment);
            Vector3 p = mount.transform.localPosition * 1000f, r = mount.transform.localEulerAngles;
            string side = mount.SideName.ToUpperInvariant();
            if (mount.IsFrozenAlignment)
            {
                info.text = (mount.IsMeterAlignment ? "The meter is fixed in the room. Move the probe until the meter sits where you want it.\n"
                        : $"Target is fixed in the room. Centre the antenna in the ball; line the probe up with the arrows ({mount.AxesHint}).\n") +
                    $"{mount.CaptureInstruction} to attach; {mount.CancelInstruction} cancels.\n" + mount.CalibrationMessage;
                return;
            }
            info.text = (locked ? "Stop recording to move the probe or change its offset.\n" :
                    mount.IsOnHand ? $"Align probe: centre the wrist device's antenna in the frozen ball. {mount.CaptureInstruction} to attach; " +
                                     $"{mount.CancelInstruction} cancels. Align meter: place the meter the same way. The hand menu gesture (palm up, pinch) opens this menu.\n" :
                    $"Align probe: centre the antenna in the frozen ball. Align meter: place the meter the same way. {side} {mount.MenuButtonName} opens this menu.\n") +
                $"Position mm: {p.x:F1}, {p.y:F1}, {p.z:F1}\nRotation deg: {r.x:F1}, {r.y:F1}, {r.z:F1}\n" +
                mount.CalibrationMessage;
        }

        /// <summary>Scenes built before meter alignment get its button at runtime: the freeze button's row split in two,
        /// as ProjectSetup builds it.</summary>
        private void EnsureMeterButton()
        {
            if (meterButton != null) return;
            meterButton = Instantiate(freezeButton, freezeButton.transform.parent);
            meterButton.name = "MeterAlign";
            meterButton.onClick.RemoveAllListeners();
            Place(freezeButton, -117f, 230f); Place(meterButton, 117f, 230f);
        }

        private static void Place(Button button, float x, float width)
        {
            var rect = (RectTransform)button.transform;
            rect.anchoredPosition = new Vector2(x, rect.anchoredPosition.y);
            rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
        }
    }
}
