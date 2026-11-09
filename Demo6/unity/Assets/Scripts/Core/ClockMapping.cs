using System;
using System.Collections.Generic;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// Maps the probe's clock onto the headset's monotonic clock from PING/PONG exchanges.
    /// Each exchange gives an offset estimate whose error is at most half its round trip (the legs
    /// may be asymmetric), so the fit trusts the lowest-round-trip exchanges, fits a linear drift once
    /// the exchanges span long enough, and reports a conservative uncertainty with every conversion.
    /// A different probe boot ID discards everything: that clock restarted.
    /// </summary>
    public sealed class ClockMapping
    {
        /// <summary>Exchanges whose round trip exceeds this are ignored (BLE retries, a blocking SCAN).</summary>
        public long MaxRoundTripUs { get; set; } = 250_000;
        /// <summary>Only exchanges this recent (host time) are fitted.</summary>
        public long WindowUs { get; set; } = 120_000_000;
        /// <summary>Drift is fitted only when the kept exchanges span at least this long.</summary>
        public long MinDriftSpanUs { get; set; } = 10_000_000;
        /// <summary>Largest drift believed physical (crystals are tens of ppm); larger fits fall back to constant offset.</summary>
        public double MaxDriftPpm { get; set; } = 500;
        /// <summary>Drift assumed when extrapolating away from the exchanges (unfitted, or beyond the fit).</summary>
        public double DriftAllowancePpm { get; set; } = 100;
        /// <summary>Share of kept exchanges (lowest round trips first) used in the fit; at least 3 when available.</summary>
        public double BestFraction { get; set; } = 0.3;

        private readonly List<PingExchange> exchanges = new List<PingExchange>();
        private bool fitted;
        private double referenceHostUs;
        private double offsetAtReferenceUs;
        private double drift;              // d(offset)/d(host), dimensionless
        private double residualUs;
        private long bestRoundTripUs;
        private long worstUsedRoundTripUs;
        private bool driftFitted;
        private double newestHostUs;
        private double oldestUsedHostUs;

        public ulong BootId { get; private set; }
        public int ExchangeCount => exchanges.Count;
        public bool HasMapping => fitted;
        public long BestRoundTripUs => fitted ? bestRoundTripUs : -1;
        public double DriftPpm => drift * 1e6;
        public bool DriftFitted => driftFitted;
        /// <summary>Host time of the newest accepted exchange.</summary>
        public long LastExchangeHostUs => (long)newestHostUs;

        public void Reset(ulong bootId = 0)
        {
            exchanges.Clear();
            BootId = bootId;
            fitted = false;
            driftFitted = false;
            drift = 0;
        }

        /// <summary>Adds an exchange and refits. Returns false if it was rejected.</summary>
        public bool Add(PingExchange exchange)
        {
            if (exchange.BootId != BootId)
            {
                Reset(exchange.BootId);
            }

            long roundTrip = exchange.RoundTripUs;
            if (roundTrip < 0 || roundTrip > MaxRoundTripUs)
            {
                return false;
            }

            exchanges.Add(exchange);
            double newest = exchange.MidpointHostUs;
            exchanges.RemoveAll(e => e.MidpointHostUs < newest - WindowUs);
            Fit();
            return true;
        }

        /// <summary>Converts a probe timestamp to headset monotonic time, with an error bound.</summary>
        public bool TryProbeToHost(ulong probeUs, out long hostUs, out long uncertaintyUs)
        {
            hostUs = 0;
            uncertaintyUs = ProbeSample.UnknownUncertainty;
            if (!fitted)
            {
                return false;
            }

            // probe = host + offset(host), offset(host) = a + drift * (host - ref)  =>  solve for host.
            double host = (probeUs - offsetAtReferenceUs + drift * referenceHostUs) / (1.0 + drift);
            hostUs = (long)Math.Round(host);

            // Outside the span of the fitted exchanges, the drift estimate's error grows with distance.
            double outside = host > newestHostUs ? host - newestHostUs
                : host < oldestUsedHostUs ? oldestUsedHostUs - host
                : 0;
            double allowance = (driftFitted ? DriftAllowancePpm * 0.25 : DriftAllowancePpm) * 1e-6;
            double extrapolationUs = driftFitted ? outside * allowance
                : Math.Abs(host - referenceHostUs) * allowance;
            // Each used offset is within its round trip / 2 of the truth, so their weighted fit is
            // within the worst of those (plus the fit residual for the drift line).
            uncertaintyUs = (long)Math.Ceiling(worstUsedRoundTripUs / 2.0 + residualUs + extrapolationUs);
            return true;
        }

        private void Fit()
        {
            fitted = false;
            if (exchanges.Count == 0)
            {
                return;
            }

            List<PingExchange> sorted = new List<PingExchange>(exchanges);
            sorted.Sort((a, b) => a.RoundTripUs.CompareTo(b.RoundTripUs));
            int use = Math.Min(sorted.Count, Math.Max(3, (int)Math.Ceiling(sorted.Count * BestFraction)));
            List<PingExchange> best = sorted.GetRange(0, use);

            bestRoundTripUs = best[0].RoundTripUs;
            worstUsedRoundTripUs = best[best.Count - 1].RoundTripUs;
            newestHostUs = double.MinValue;
            oldestUsedHostUs = double.MaxValue;
            double weightSum = 0, meanX = 0, meanY = 0;
            foreach (PingExchange e in best)
            {
                double w = Weight(e);
                weightSum += w;
                meanX += w * e.MidpointHostUs;
                meanY += w * e.OffsetUs;
                oldestUsedHostUs = Math.Min(oldestUsedHostUs, e.MidpointHostUs);
            }
            foreach (PingExchange e in exchanges)
            {
                newestHostUs = Math.Max(newestHostUs, e.MidpointHostUs);
            }
            meanX /= weightSum;
            meanY /= weightSum;

            referenceHostUs = meanX;
            offsetAtReferenceUs = meanY;
            drift = 0;
            driftFitted = false;

            if (best.Count >= 3 && newestHostUs - oldestUsedHostUs >= MinDriftSpanUs)
            {
                double sxx = 0, sxy = 0;
                foreach (PingExchange e in best)
                {
                    double w = Weight(e);
                    double dx = e.MidpointHostUs - meanX;
                    sxx += w * dx * dx;
                    sxy += w * dx * (e.OffsetUs - meanY);
                }
                double slope = sxx > 0 ? sxy / sxx : 0;
                if (Math.Abs(slope) * 1e6 <= MaxDriftPpm)
                {
                    drift = slope;
                    driftFitted = true;
                }
            }

            double squares = 0;
            foreach (PingExchange e in best)
            {
                double predicted = offsetAtReferenceUs + drift * (e.MidpointHostUs - referenceHostUs);
                double r = e.OffsetUs - predicted;
                squares += r * r;
            }
            residualUs = best.Count > 1 ? Math.Sqrt(squares / best.Count) : 0;
            fitted = true;
        }

        private static double Weight(PingExchange e)
        {
            // Inverse variance of a uniform error of half-width rtt/2; floor avoids division by zero.
            double halfWidth = Math.Max(e.RoundTripUs / 2.0, 100.0);
            return 1.0 / (halfWidth * halfWidth);
        }
    }
}
