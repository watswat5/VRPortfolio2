using System;

namespace WifiHeatmap.Core
{
    /// <summary>Display range v1, in whole dBm. Expand narrow observations to a 5 dB span so
    /// one reading cannot turn tiny fluctuations into the full color gradient.</summary>
    public readonly struct SignalDisplayRange
    {
        public const int MinimumDbm = -150, MaximumDbm = 10, MinimumSpanDb = 5;
        public int Low { get; }
        public int High { get; }
        private SignalDisplayRange(int low, int high) { Low = low; High = high; }
        public static bool TryFromSamples(double minimum, double maximum, out SignalDisplayRange range)
        {
            range = default;
            if (double.IsNaN(minimum) || double.IsInfinity(minimum) || double.IsNaN(maximum) ||
                double.IsInfinity(maximum) || minimum > maximum || minimum < MinimumDbm || maximum > MaximumDbm) return false;
            int low = (int)Math.Floor(minimum), high = (int)Math.Ceiling(maximum);
            if (high - low < MinimumSpanDb)
            {
                low = (int)Math.Floor((low + high - MinimumSpanDb) / 2.0);
                high = low + MinimumSpanDb;
            }
            if (low < MinimumDbm) { low = MinimumDbm; high = low + MinimumSpanDb; }
            if (high > MaximumDbm) { high = MaximumDbm; low = high - MinimumSpanDb; }
            range = new SignalDisplayRange(low, high); return true;
        }
    }
}
