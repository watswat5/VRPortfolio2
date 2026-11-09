namespace WifiHeatmap.Core
{
    /// <summary>Prototype display policy v1. BLE measurements on Quest 3 (2026-10-29) had
    /// 47-169 ms RTT; a 15 ms precision flag must not mean no preview at all. Known uncertainty
    /// above 100 ms remains excluded. This is a display budget, not a verified accuracy tolerance.</summary>
    public static class SpatialDisplayQuality
    {
        public const long PreciseTimingUs = 15000;
        public const long MaxPreviewTimingUs = 100000;
        private const SampleFlags Allowed = SampleFlags.ClockUncertain | SampleFlags.PoseExtrapolated;
        public static bool CanDisplay(SampleFlags flags, long uncertaintyUs = -1) =>
            (flags & ~Allowed) == SampleFlags.None && uncertaintyUs <= MaxPreviewTimingUs;

        public static float Weight(SampleFlags flags, long uncertaintyUs = -1)
        {
            float quality = (flags & SampleFlags.PoseExtrapolated) != 0 ? .6f : 1;
            if (uncertaintyUs > PreciseTimingUs)
                quality *= (float)PreciseTimingUs / uncertaintyUs;
            else if ((flags & SampleFlags.ClockUncertain) != 0)
                quality *= .2f; // Old format-1 recordings retain only the flag, not a numeric bound.
            return quality;
        }
    }
}
