using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WifiHeatmap.Preview;

namespace WifiHeatmap.UI
{
    public sealed class RoomPreviewPanel : MonoBehaviour
    {
        [SerializeField] private RoomPreview preview;
        [SerializeField] private RoomScanController scan;
        [SerializeField] private Button show, geometry, ceiling, cloud, recenter, scanRoom, reload, fogQuality;
        [SerializeField] private Slider zoom;
        [SerializeField] private GameObject mainPage, fogPage;
        [SerializeField] private Button fogOptions, fogBack, signalDensity;
        [SerializeField] private Slider densityEffect;
        [SerializeField] private TMP_Text densityLabel;
        [Tooltip("The preview fog's own density (0-1 on a curve to RoomPreview's min-max). Empty: added at runtime.")]
        [SerializeField] private Slider fogDensity;
        [SerializeField] private TMP_Text fogDensityLabel;
        [SerializeField] private TMP_Text zoomLabel, status;
        private string lastScanStatus;
        private float scanMessageUntil;
        private void Start()
        {
            lastScanStatus = scan.Status;
            fogOptions.onClick.AddListener(() => ShowFogOptions(true));
            fogBack.onClick.AddListener(() => ShowFogOptions(false));
            signalDensity.onClick.AddListener(preview.ToggleSignalDensity);
            densityEffect.onValueChanged.AddListener(preview.SetSignalDensityAmount);
            EnsureFogDensity();
            fogDensity.onValueChanged.AddListener(v => preview.SetFogDensity(DensityOf(v)));
            ShowFogOptions(false);
            show.onClick.AddListener(preview.ToggleShown);
            geometry.onClick.AddListener(preview.ToggleGeometry);
            ceiling.onClick.AddListener(preview.ToggleCeiling);
            cloud.onClick.AddListener(preview.ToggleCloud);
            fogQuality.onClick.AddListener(preview.CycleFogSteps);
            recenter.onClick.AddListener(preview.Recenter);
            zoom.onValueChanged.AddListener(preview.SetScale);
            scanRoom.onClick.AddListener(scan.ScanRoom); reload.onClick.AddListener(scan.ReloadRoom);
        }
        private void ShowFogOptions(bool show) { mainPage.SetActive(!show); fogPage.SetActive(show); }

        // The density slider runs 0-1 on a squared curve: fine control at the low end, up to 5x the old maximum.
        private static float DensityOf(float v) => Mathf.Lerp(RoomPreview.MinFogDensity, RoomPreview.MaxFogDensity, v * v);
        private static float SliderOf(float d) => Mathf.Sqrt(Mathf.InverseLerp(RoomPreview.MinFogDensity, RoomPreview.MaxFogDensity, d));

        /// <summary>A fog page built before the density slider existed: add it (copying the signal-effect slider) and
        /// lay the page out as ProjectSetup now builds it.</summary>
        private void EnsureFogDensity()
        {
            if (fogDensity != null && fogDensityLabel != null) return;
            fogDensity = Instantiate(densityEffect, densityEffect.transform.parent);
            fogDensity.name = "Density";
            fogDensity.minValue = 0; fogDensity.maxValue = 1;
            fogDensityLabel = Instantiate(densityLabel, densityLabel.transform.parent);
            fogDensityLabel.name = "DensityLabel";
            void At(Transform t, float y, Vector2? size = null)
            {
                if (t == null) return;
                var r = (RectTransform)t;
                r.anchoredPosition = new Vector2(r.anchoredPosition.x, y);
                if (size.HasValue) r.sizeDelta = size.Value;
            }
            var title = fogPage.transform.Find("Title");
            if (title != null && title.TryGetComponent(out TMP_Text titleText)) titleText.text = "Preview fog";
            At(fogDensity.transform, 72); At(fogDensityLabel.transform, 72);
            At(signalDensity.transform, 34, new Vector2(360, 30));
            At(densityEffect.transform, -2); At(densityLabel.transform, -2);
            var explanation = fogPage.transform.Find("Explanation");
            At(explanation, -54, new Vector2(480, 62));
            if (explanation != null && explanation.TryGetComponent(out TMP_Text text)) text.fontSize = 12;
        }
        private void Update()
        {
            MenuStyle.SetLabel(show,preview.Shown ? "Hide preview" : "Show preview");
            MenuStyle.SetLabel(geometry,preview.RawMesh ? "Scanned mesh" : "Rooms & boxes");
            MenuStyle.SetLabel(ceiling,preview.HideCeiling ? "Ceiling: hidden" : "Ceiling: shown");
            MenuStyle.SetLabel(cloud,"Heatmap: " + preview.CloudModeLabel);
            MenuStyle.SetLabel(fogQuality,$"Fog quality: {preview.FogSteps} steps / ray");
            MenuStyle.SetLabel(signalDensity,preview.SignalDensityEnabled ? "Signal density: On" : "Signal density: Off");
            MenuStyle.Tint(signalDensity,preview.SignalDensityEnabled);
            densityEffect.SetValueWithoutNotify(preview.SignalDensityAmount);
            densityEffect.interactable = preview.SignalDensityEnabled && !scan.Busy;
            densityLabel.text = $"Signal effect: {preview.SignalDensityAmount*100:0}%";
            fogDensity.SetValueWithoutNotify(SliderOf(preview.FogDensity));
            fogDensityLabel.text = $"Density: {preview.FogDensity:0.00}";
            fogDensity.interactable = !scan.Busy;
            fogOptions.interactable = fogBack.interactable = signalDensity.interactable = !scan.Busy;
            zoom.SetValueWithoutNotify(preview.Scale); zoomLabel.text=$"Scale 1:{1/preview.Scale:0.0}";
            show.interactable=geometry.interactable=ceiling.interactable=cloud.interactable=
                recenter.interactable=fogQuality.interactable=zoom.interactable=scanRoom.interactable=reload.interactable=!scan.Busy;
            if (lastScanStatus != scan.Status)
            { lastScanStatus = scan.Status; scanMessageUntil = Time.unscaledTime + 8; }
            status.text = scan.Busy || Time.unscaledTime < scanMessageUntil || !preview.Shown ? scan.Status : preview.Status;
        }
    }
}
