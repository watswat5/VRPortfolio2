using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WifiHeatmap.Heatmap;

namespace WifiHeatmap.UI
{
    public sealed class CloudPanel : MonoBehaviour
    {
        [SerializeField] private CloudSettings settings;
        [SerializeField] private LiveVoxelCloud live;
        [SerializeField] private Slider resolution, opacity, lowDbm, highDbm;
        [SerializeField] private TMP_Text resolutionLabel, opacityLabel, lowLabel, highLabel, rangeLabel, status;
        [SerializeField] private CloudRangeSlider range;
        [SerializeField] private Button colorsButton, clearButton, backButton, autoScaleButton;
        [SerializeField] private GameObject mainPage, colorsPage;
        [SerializeField] private Button[] colorButtons;
        [SerializeField] private Slider red, green, blue;
        [SerializeField] private TMP_Text colorLabel;
        private int selected;
        private bool initialized;
        private void Start()
        {
            initialized = true;
            resolution.onValueChanged.AddListener(settings.SetResolution);
            opacity.onValueChanged.AddListener(settings.SetOpacity);
            lowDbm.onValueChanged.AddListener(settings.SetLowDbm);
            highDbm.onValueChanged.AddListener(settings.SetHighDbm);
            autoScaleButton.onClick.AddListener(() => settings.SetAutoScale(!settings.AutoScale));
            colorsButton.onClick.AddListener(() => ShowColors(true));
            backButton.onClick.AddListener(() => ShowColors(false)); clearButton.onClick.AddListener(live.Clear);
            for (int i = 0; i < colorButtons.Length; i++) { int index = i; colorButtons[i].onClick.AddListener(() => SelectColor(index)); }
            red.onValueChanged.AddListener(_ => UpdateColor()); green.onValueChanged.AddListener(_ => UpdateColor()); blue.onValueChanged.AddListener(_ => UpdateColor());
            range.Bind(settings); settings.Changed += Refresh;
            ShowColors(false); SelectColor(0); Refresh(false);
        }
        private void OnDestroy() { if (initialized) settings.Changed -= Refresh; }
        private void ShowColors(bool show) { mainPage.SetActive(!show); colorsPage.SetActive(show); }
        private void SelectColor(int index)
        {
            selected = index; Color c = settings.GetColor(index);
            red.SetValueWithoutNotify(c.r); green.SetValueWithoutNotify(c.g); blue.SetValueWithoutNotify(c.b);
            Refresh(false);
        }
        private void UpdateColor() { settings.SetColor(selected, new Color(red.value, green.value, blue.value)); }
        private void Refresh(bool _)
        {
            resolution.SetValueWithoutNotify(settings.Resolution); opacity.SetValueWithoutNotify(settings.Opacity);
            lowDbm.SetValueWithoutNotify(settings.LowDbm); highDbm.SetValueWithoutNotify(settings.HighDbm);
            lowDbm.interactable = highDbm.interactable = !settings.AutoScale;
            MenuStyle.SetLabel(autoScaleButton, settings.AutoScale ? "Auto scale: On" : "Auto scale: Off");
            MenuStyle.Tint(autoScaleButton, settings.AutoScale);
            resolutionLabel.text = $"Voxel: {settings.Resolution * 100:0} cm";
            opacityLabel.text = $"Opacity: {settings.Opacity * 100:0}%";
            lowLabel.text = $"Low: {settings.LowDbm:0} dBm"; highLabel.text = $"High: {settings.HighDbm:0} dBm";
            rangeLabel.text = $"Color anchors: {settings.LowColorDbm:0} / {settings.MiddleColorDbm:0} / {settings.HighColorDbm:0} dBm";
            for (int i = 0; i < colorButtons.Length; i++) MenuStyle.Tint(colorButtons[i], settings.GetColor(i));
            colorLabel.text = $"Editing {(selected == 0 ? "low" : selected == 1 ? "middle" : "high")} color   RGB {red.value:0.00} / {green.value:0.00} / {blue.value:0.00}";
        }
        private void Update() { status.text = live.Status; }
    }
}
