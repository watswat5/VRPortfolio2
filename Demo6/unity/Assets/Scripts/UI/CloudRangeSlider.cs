using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WifiHeatmap.Heatmap;

namespace WifiHeatmap.UI
{
    /// <summary>One gradient bar, two independently draggable handles. Uses event camera for world-space rays.</summary>
    public sealed class CloudRangeSlider : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        [SerializeField] private RectTransform lowHandle, highHandle;
        [SerializeField] private RawImage gradient;
        private CloudSettings settings;
        private Texture2D texture;
        private bool draggingLow;
        public void Bind(CloudSettings value)
        {
            settings = value;
            texture = new Texture2D(128, 1, TextureFormat.RGBA32, false) { name = "Cloud gradient", wrapMode = TextureWrapMode.Clamp };
            gradient.texture = texture;
            settings.Changed += Redraw; Redraw(false);
        }
        private void OnDestroy()
        { if (settings != null) settings.Changed -= Redraw; if (texture != null) Destroy(texture); }
        public void OnPointerDown(PointerEventData data)
        {
            if (!TryValue(data, out float value)) return;
            draggingLow = Mathf.Abs(value - settings.LowStop) <= Mathf.Abs(value - settings.HighStop);
            Apply(value);
        }
        public void OnDrag(PointerEventData data) { if (TryValue(data, out float value)) Apply(value); }
        private bool TryValue(PointerEventData data, out float value)
        {
            value = 0; var rect = (RectTransform)transform;
            if (settings == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, data.position, data.pressEventCamera, out Vector2 point)) return false;
            value = Mathf.InverseLerp(rect.rect.xMin, rect.rect.xMax, point.x); return true;
        }
        private void Apply(float value)
        {
            if (draggingLow) settings.SetStops(Mathf.Min(value, settings.HighStop - .05f), settings.HighStop);
            else settings.SetStops(settings.LowStop, Mathf.Max(value, settings.LowStop + .05f));
        }
        private void Redraw(bool _)
        {
            SetHandle(lowHandle, settings.LowStop); SetHandle(highHandle, settings.HighStop);
            for (int x = 0; x < texture.width; x++) texture.SetPixel(x, 0, settings.Evaluate(Mathf.Lerp(settings.LowDbm, settings.HighDbm, x / (float)(texture.width - 1))));
            texture.Apply(false);
        }
        private static void SetHandle(RectTransform handle, float value)
        { handle.anchorMin = handle.anchorMax = new Vector2(value, .5f); handle.anchoredPosition = Vector2.zero; }
    }
}
