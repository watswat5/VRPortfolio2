using System;
using NUnit.Framework;

namespace WifiHeatmap.Core.Tests
{
    /// <summary>
    /// ClockMapping against a simulated probe: its clock started at a different time, runs at a
    /// slightly different rate, and talks over a link with BLE-like, asymmetric, random delays.
    /// </summary>
    public sealed class ClockMappingTests
    {
        private const long HostStartUs = 3_810_000_000;   // headset up for about an hour
        private const double ProbeAtHostStartUs = 9_000_000; // probe booted 9 s before the first PING

        /// <summary>Simulated probe clock: probe = start + (host - hostStart) * (1 + drift).</summary>
        private sealed class SimulatedProbe
        {
            private readonly double driftPpm;
            private readonly Random random;
            private readonly double minLegUs;
            private readonly double maxLegUs;

            public SimulatedProbe(double driftPpm, int seed, double minLegUs = 5_000, double maxLegUs = 45_000)
            {
                this.driftPpm = driftPpm;
                random = new Random(seed);
                this.minLegUs = minLegUs;
                this.maxLegUs = maxLegUs;
            }

            public ulong BootId { get; set; } = 42;

            public ulong ProbeTime(double hostUs)
            {
                return (ulong)Math.Round(ProbeAtHostStartUs + (hostUs - HostStartUs) * (1 + driftPpm * 1e-6));
            }

            public PingExchange Exchange(long hostSendUs, double extraUplinkUs = 0)
            {
                double up = Leg() + extraUplinkUs;
                double processing = 50 + random.NextDouble() * 400;
                double down = Leg();
                ulong probeRx = ProbeTime(hostSendUs + up);
                ulong probeTx = ProbeTime(hostSendUs + up + processing);
                long hostReceive = (long)Math.Round(hostSendUs + up + processing + down);
                return new PingExchange(hostSendUs, hostReceive, probeRx, probeTx, BootId);
            }

            private double Leg()
            {
                return minLegUs + random.NextDouble() * (maxLegUs - minLegUs);
            }
        }

        private static ClockMapping Feed(SimulatedProbe probe, int count, double periodSeconds)
        {
            ClockMapping clock = new ClockMapping();
            for (int i = 0; i < count; i++)
            {
                Assert.IsTrue(clock.Add(probe.Exchange(HostStartUs + (long)(i * periodSeconds * 1e6))));
            }
            return clock;
        }

        [Test]
        public void NoMappingBeforeFirstExchange()
        {
            ClockMapping clock = new ClockMapping();
            Assert.IsFalse(clock.HasMapping);
            Assert.IsFalse(clock.TryProbeToHost(1234, out _, out long uncertainty));
            Assert.AreEqual(ProbeSample.UnknownUncertainty, uncertainty);
        }

        [Test]
        public void SymmetricFixedDelayIsExact()
        {
            SimulatedProbe probe = new SimulatedProbe(driftPpm: 0, seed: 1, minLegUs: 10_000, maxLegUs: 10_000);
            ClockMapping clock = Feed(probe, 5, 1.0);

            long eventHost = HostStartUs + 2_500_000;
            Assert.IsTrue(clock.TryProbeToHost(probe.ProbeTime(eventHost), out long mapped, out long uncertainty));
            Assert.LessOrEqual(Math.Abs(mapped - eventHost), 300, "processing jitter only");
            Assert.LessOrEqual(Math.Abs(mapped - eventHost), uncertainty);
        }

        [TestCase(0.0, 11)]
        [TestCase(40.0, 12)]
        [TestCase(-35.0, 13)]
        [TestCase(120.0, 14)]
        public void ErrorStaysWithinReportedUncertainty(double driftPpm, int seed)
        {
            SimulatedProbe probe = new SimulatedProbe(driftPpm, seed);
            ClockMapping clock = Feed(probe, 61, 1.0);   // one minute of 1 Hz PINGs

            // Inside the PING span and up to 5 s after the last PING.
            for (double t = 0; t <= 65; t += 0.37)
            {
                double eventHost = HostStartUs + t * 1e6;
                Assert.IsTrue(clock.TryProbeToHost(probe.ProbeTime(eventHost), out long mapped, out long uncertainty));
                double error = Math.Abs(mapped - eventHost);
                Assert.LessOrEqual(error, uncertainty, $"t={t:F2}s: error {error:F0} µs exceeds bound {uncertainty} µs");
                Assert.Less(error, 15_000, $"t={t:F2}s: error {error:F0} µs");
            }
        }

        [Test]
        public void FitsDriftOnceExchangesSpanLongEnough()
        {
            SimulatedProbe probe = new SimulatedProbe(driftPpm: 40, seed: 21, minLegUs: 7_000, maxLegUs: 9_000);
            ClockMapping clock = Feed(probe, 5, 1.0);
            Assert.IsFalse(clock.DriftFitted, "5 s is too short to fit drift");

            for (int i = 5; i <= 60; i++)
            {
                clock.Add(probe.Exchange(HostStartUs + i * 1_000_000L));
            }
            Assert.IsTrue(clock.DriftFitted);
            Assert.AreEqual(40, clock.DriftPpm, 15, "fitted drift (ppm)");
        }

        [Test]
        public void SlowOutliersDoNotSkewTheFit()
        {
            SimulatedProbe probe = new SimulatedProbe(driftPpm: 0, seed: 31, minLegUs: 8_000, maxLegUs: 12_000);
            ClockMapping clock = new ClockMapping();
            for (int i = 0; i < 30; i++)
            {
                // Every third PING is delayed 150 ms on the way out (a BLE retry): very asymmetric.
                double extra = i % 3 == 0 ? 150_000 : 0;
                clock.Add(probe.Exchange(HostStartUs + i * 1_000_000L, extra));
            }

            double eventHost = HostStartUs + 15_000_000;
            Assert.IsTrue(clock.TryProbeToHost(probe.ProbeTime(eventHost), out long mapped, out long uncertainty));
            Assert.Less(Math.Abs(mapped - eventHost), 5_000, "outliers would shift it by ~75 ms if they were used");
            Assert.Less(uncertainty, 20_000);
        }

        [Test]
        public void RejectsImpossibleAndOverlongRoundTrips()
        {
            ClockMapping clock = new ClockMapping { MaxRoundTripUs = 250_000 };
            // Probe claims it spent longer than the whole round trip: negative wire time.
            Assert.IsFalse(clock.Add(new PingExchange(HostStartUs, HostStartUs + 1_000, 100, 5_000, 1)));
            // Half a second on the wire (e.g. answered after a blocking SCAN).
            Assert.IsFalse(clock.Add(new PingExchange(HostStartUs, HostStartUs + 500_000, 100, 200, 1)));
            Assert.AreEqual(0, clock.ExchangeCount);
            Assert.IsFalse(clock.HasMapping);
        }

        [Test]
        public void NewBootIdDiscardsTheOldClock()
        {
            SimulatedProbe probe = new SimulatedProbe(driftPpm: 0, seed: 41);
            ClockMapping clock = Feed(probe, 10, 1.0);
            Assert.AreEqual(10, clock.ExchangeCount);

            probe.BootId = 43;
            Assert.IsTrue(clock.Add(probe.Exchange(HostStartUs + 20_000_000)));
            Assert.AreEqual(43UL, clock.BootId);
            Assert.AreEqual(1, clock.ExchangeCount);
        }

        [Test]
        public void UncertaintyGrowsWhenPingsStop()
        {
            SimulatedProbe probe = new SimulatedProbe(driftPpm: 20, seed: 51);
            ClockMapping clock = Feed(probe, 20, 1.0);

            double lastPing = HostStartUs + 19_000_000;
            clock.TryProbeToHost(probe.ProbeTime(lastPing), out _, out long fresh);
            clock.TryProbeToHost(probe.ProbeTime(lastPing + 60_000_000), out long mapped, out long stale);
            Assert.Greater(stale, fresh, "a minute without PINGs must widen the bound");
            Assert.LessOrEqual(Math.Abs(mapped - (lastPing + 60_000_000)), stale);
        }

        [Test]
        public void OldExchangesLeaveTheWindow()
        {
            SimulatedProbe probe = new SimulatedProbe(driftPpm: 0, seed: 61);
            ClockMapping clock = new ClockMapping { WindowUs = 30_000_000 };
            for (int i = 0; i < 100; i++)
            {
                clock.Add(probe.Exchange(HostStartUs + i * 1_000_000L));
            }
            Assert.LessOrEqual(clock.ExchangeCount, 32);
        }
    }
}
