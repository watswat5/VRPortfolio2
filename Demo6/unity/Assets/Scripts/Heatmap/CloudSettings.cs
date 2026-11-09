using System;
using UnityEngine;
using WifiHeatmap.Meter;
using WifiHeatmap.Core;

namespace WifiHeatmap.Heatmap
{
    /// <summary>Shared live/replay display settings. Display changes never change recorded RSSI.</summary>
    public sealed class CloudSettings : MonoBehaviour
    {
        [SerializeField] private EmfMeter meter;
        [SerializeField, Range(.02f, .5f)] private float resolution = .12f;
        [SerializeField, Range(.05f, .8f)] private float opacity = .35f;
        [SerializeField] private float lowDbm = -90, highDbm = -30;
        [SerializeField] private bool autoScale;
        public bool AutoScale => autoScale;
        [SerializeField, Range(0, 1)] private float lowStop = .15f, highStop = .85f;
        [SerializeField] private Color lowColor = new Color(.95f, .15f, .1f);
        [SerializeField] private Color middleColor = new Color(1, .7f, .05f);
        [SerializeField] private Color highColor = new Color(.1f, .9f, .3f);
        public float Resolution => resolution;
        public float Opacity => opacity;
        public float LowDbm => lowDbm;
        public float HighDbm => highDbm;
        public float LowStop => lowStop;
        public float HighStop => highStop;
        public float LowColorDbm => Mathf.Lerp(lowDbm, highDbm, lowStop);
        public float HighColorDbm => Mathf.Lerp(lowDbm, highDbm, highStop);
        public float MiddleColorDbm => (LowColorDbm + HighColorDbm) * .5f;
        public event Action<bool> Changed; // true: resolution changed, rebin retained raw samples
        private void Start() { ApplyMeter(); }
        public Color GetColor(int index) => index == 0 ? lowColor : index == 1 ? middleColor : highColor;
        public Color Evaluate(float dbm)
        {
            float t = Mathf.InverseLerp(LowColorDbm, HighColorDbm, dbm);
            return t <= .5f ? Color.Lerp(lowColor, middleColor, t * 2) : Color.Lerp(middleColor, highColor, t * 2 - 1);
        }
        public void SetResolution(float value)
        {
            value = Mathf.Clamp(Mathf.Round(value * 100) / 100, .02f, .5f);
            if (Mathf.Approximately(value, resolution)) return;
            resolution = value; Changed?.Invoke(true);
        }
        public void SetOpacity(float value) { opacity = Mathf.Clamp(value, .05f, .8f); Changed?.Invoke(false); }
        public void SetLowDbm(float value) { lowDbm = Mathf.Clamp(Mathf.Round(value), SignalDisplayRange.MinimumDbm, highDbm - SignalDisplayRange.MinimumSpanDb); Notify(); }
        public void SetHighDbm(float value) { highDbm = Mathf.Clamp(Mathf.Round(value), lowDbm + SignalDisplayRange.MinimumSpanDb, SignalDisplayRange.MaximumDbm); Notify(); }
        public void SetAutoScale(bool value)
        {
            if (autoScale == value) return;
            autoScale = value; Notify();
        }
        public void ApplyAutoRange(double minimum, double maximum)
        {
            if (!autoScale || !SignalDisplayRange.TryFromSamples(minimum, maximum, out var range)) return;
            if (Mathf.Approximately(lowDbm, range.Low) && Mathf.Approximately(highDbm, range.High)) return;
            lowDbm = range.Low; highDbm = range.High; Notify();
        }
        public void SetStops(float low, float high)
        {
            lowStop = Mathf.Clamp(low, 0, .95f); highStop = Mathf.Clamp(high, lowStop + .05f, 1); Notify();
        }
        public void SetColor(int index, Color color)
        {
            color.a = 1;
            if (index == 0) lowColor = color; else if (index == 1) middleColor = color; else highColor = color;
            Notify();
        }
        private void Notify() { ApplyMeter(); Changed?.Invoke(false); }
        private void ApplyMeter() { if (meter != null) meter.SetRange(Mathf.RoundToInt(lowDbm), Mathf.RoundToInt(highDbm)); }
    }
}
