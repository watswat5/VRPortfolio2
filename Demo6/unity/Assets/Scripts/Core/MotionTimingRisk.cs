using System;

namespace WifiHeatmap.Core
{
    /// <summary>First-order displacement from timing uncertainty at the current antenna speed.
    /// This is guidance, not a bound on total spatial error: acceleration, pose and RF-window error are separate.</summary>
    public static class MotionTimingRisk
    {
        public static bool TryEvaluate(double speedMetersPerSecond, double uncertaintyMicroseconds,
            double warningMeters, double fullWarningMeters, out double displacementMeters, out double level)
        {
            displacementMeters = level = 0;
            if (!Finite(speedMetersPerSecond) || speedMetersPerSecond < 0 ||
                !Finite(uncertaintyMicroseconds) || uncertaintyMicroseconds < 0 ||
                !Finite(warningMeters) || warningMeters < 0 ||
                !Finite(fullWarningMeters) || fullWarningMeters <= warningMeters) return false;
            double displacement = speedMetersPerSecond * (uncertaintyMicroseconds * 1e-6);
            if (!Finite(displacement)) return false;
            displacementMeters = displacement;
            level = Math.Max(0, Math.Min(1, (displacement-warningMeters)/(fullWarningMeters-warningMeters)));
            return true;
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
