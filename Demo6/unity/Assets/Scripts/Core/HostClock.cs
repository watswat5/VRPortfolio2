using System;
using System.Diagnostics;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// The headset's monotonic clock in microseconds. Every timestamp that is fused with probe data
    /// (BLE receive times, PING send times, pose history) must come from here, never from wall time.
    /// </summary>
    public static class HostClock
    {
        private static readonly double TicksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;

        private static Func<long> source = StopwatchMicroseconds;

        public static long NowMicroseconds => source();

        /// <summary>Platform adapter initialization only: configure before pose capture, transport or
        /// recording starts. All callers then share its epoch and units. Never switch on reconnect.</summary>
        public static void ConfigureTimeSource(Func<long> monotonicMicroseconds)
        {
            source = monotonicMicroseconds ?? throw new ArgumentNullException(nameof(monotonicMicroseconds));
        }

        private static long StopwatchMicroseconds() => (long)(Stopwatch.GetTimestamp() * TicksToMicroseconds);
    }
}
