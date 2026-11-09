using System;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// One SAMPLE report from the probe: RSSI statistics for the selected radio over one reporting
    /// window, timed on the probe's clock. Host-clock fields are filled in once a clock mapping exists.
    /// </summary>
    public readonly struct ProbeSample
    {
        public const long UnknownUncertainty = -1;

        public ProbeSample(ulong sessionId, ulong sequence, ulong probeTimeUs, ulong windowStartUs, ulong windowEndUs,
            string bssid, int channel, int rssiDbm, double meanDbm, int minDbm, int maxDbm, double stdDevDb, ulong frames,
            long receivedHostUs, long hostTimeUs = 0, long hostTimeUncertaintyUs = UnknownUncertainty)
        {
            SessionId = sessionId;
            Sequence = sequence;
            ProbeTimeUs = probeTimeUs;
            WindowStartUs = windowStartUs;
            WindowEndUs = windowEndUs;
            Bssid = bssid;
            Channel = channel;
            RssiDbm = rssiDbm;
            MeanDbm = meanDbm;
            MinDbm = minDbm;
            MaxDbm = maxDbm;
            StdDevDb = stdDevDb;
            Frames = frames;
            ReceivedHostUs = receivedHostUs;
            HostTimeUs = hostTimeUs;
            HostTimeUncertaintyUs = hostTimeUncertaintyUs;
        }

        public ulong SessionId { get; }
        public ulong Sequence { get; }
        /// <summary>Middle of the observation window, probe clock (µs since probe boot).</summary>
        public ulong ProbeTimeUs { get; }
        /// <summary>First and last received frame of the window (probe clock); 0 when not reported.</summary>
        public ulong WindowStartUs { get; }
        public ulong WindowEndUs { get; }
        public string Bssid { get; }
        public int Channel { get; }
        /// <summary>Rounded window mean, dBm (the value the meter shows).</summary>
        public int RssiDbm { get; }
        public double MeanDbm { get; }
        public int MinDbm { get; }
        public int MaxDbm { get; }
        public double StdDevDb { get; }
        public ulong Frames { get; }
        /// <summary>Headset monotonic time when the line's last byte arrived. Not the measurement time.</summary>
        public long ReceivedHostUs { get; }
        /// <summary>Measurement time (window midpoint) on the headset monotonic clock; valid only if HasHostTime.</summary>
        public long HostTimeUs { get; }
        /// <summary>Bound on the error of HostTimeUs, µs; UnknownUncertainty when there is no clock mapping.</summary>
        public long HostTimeUncertaintyUs { get; }
        public bool HasHostTime => HostTimeUncertaintyUs != UnknownUncertainty;

        public ProbeSample WithHostTime(long hostTimeUs, long uncertaintyUs)
        {
            return new ProbeSample(SessionId, Sequence, ProbeTimeUs, WindowStartUs, WindowEndUs, Bssid, Channel, RssiDbm,
                MeanDbm, MinDbm, MaxDbm, StdDevDb, Frames, ReceivedHostUs, hostTimeUs, Math.Max(0, uncertaintyUs));
        }
    }

    /// <summary>One PING/PONG round trip, used to map the probe clock onto the headset clock.</summary>
    public readonly struct PingExchange
    {
        public PingExchange(long hostSendUs, long hostReceiveUs, ulong probeReceiveUs, ulong probeSendUs, ulong bootId)
        {
            HostSendUs = hostSendUs;
            HostReceiveUs = hostReceiveUs;
            ProbeReceiveUs = probeReceiveUs;
            ProbeSendUs = probeSendUs;
            BootId = bootId;
        }

        public long HostSendUs { get; }
        public long HostReceiveUs { get; }
        public ulong ProbeReceiveUs { get; }
        public ulong ProbeSendUs { get; }
        public ulong BootId { get; }

        /// <summary>Time on the wire (both directions), excluding the probe's own processing time.</summary>
        public long RoundTripUs => (HostReceiveUs - HostSendUs) - (long)(ProbeSendUs - ProbeReceiveUs);

        /// <summary>Probe clock minus host clock, assuming the two legs took equal time (NTP estimate).</summary>
        public double OffsetUs => (((double)ProbeReceiveUs - HostSendUs) + ((double)ProbeSendUs - HostReceiveUs)) / 2.0;

        /// <summary>Host time the offset estimate refers to.</summary>
        public double MidpointHostUs => (HostSendUs + (double)HostReceiveUs) / 2.0;
    }

    /// <summary>Strict parsing of the probe's data-bearing lines into typed values.</summary>
    public static class ProbeLineParser
    {
        /// <summary>
        /// Parses "SAMPLE session= seq= probe_us= bssid= channel= rssi= frames= mean= min= max= stddev=
        /// [window_start_us= window_end_us=] ...". Unknown trailing fields are ignored; any missing
        /// required field, malformed number or out-of-range value rejects the line.
        /// </summary>
        public static bool TryParseSample(string line, long receivedHostUs, out ProbeSample sample)
        {
            sample = default;
            if (line == null || !line.StartsWith("SAMPLE ", StringComparison.Ordinal))
            {
                return false;
            }

            if (!ProbeFields.TryReadULongField(line, "session", out ulong session) ||
                !ProbeFields.TryReadULongField(line, "seq", out ulong sequence) ||
                !ProbeFields.TryReadULongField(line, "probe_us", out ulong probeUs) ||
                !ProbeFields.TryReadField(line, "bssid", out string bssidText) ||
                !ProbeFields.TryNormalizeBssid(bssidText, out string bssid) ||
                !ProbeFields.TryReadIntField(line, "channel", out int channel) ||
                !ProbeFields.TryReadIntField(line, "rssi", out int rssi) ||
                !ProbeFields.TryReadULongField(line, "frames", out ulong frames) ||
                !ProbeFields.TryReadDoubleField(line, "mean", out double mean) ||
                !ProbeFields.TryReadIntField(line, "min", out int min) ||
                !ProbeFields.TryReadIntField(line, "max", out int max) ||
                !ProbeFields.TryReadDoubleField(line, "stddev", out double stddev))
            {
                return false;
            }

            if (channel < 1 || channel > 233 || !IsPlausibleDbm(rssi) || !IsPlausibleDbm(min) || !IsPlausibleDbm(max) ||
                mean < min - 0.01 || mean > max + 0.01 || min > max || stddev < 0 || frames == 0 || session == 0)
            {
                return false;
            }

            ulong windowStart = 0, windowEnd = 0;
            bool hasStart = ProbeFields.TryReadULongField(line, "window_start_us", out windowStart);
            bool hasEnd = ProbeFields.TryReadULongField(line, "window_end_us", out windowEnd);
            if (hasStart != hasEnd || (hasStart && (windowStart > probeUs || probeUs > windowEnd)))
            {
                return false;
            }

            sample = new ProbeSample(session, sequence, probeUs, windowStart, windowEnd, bssid, channel, rssi, mean, min,
                max, stddev, frames, receivedHostUs);
            return true;
        }

        /// <summary>Parses "PONG id host_us= probe_rx_us= probe_tx_us= boot=" (host_us echoes our PING).</summary>
        public static bool TryParsePong(string line, long hostReceiveUs, out PingExchange exchange)
        {
            exchange = default;
            if (line == null || !line.StartsWith("PONG ", StringComparison.Ordinal) ||
                !ProbeFields.TryReadULongField(line, "host_us", out ulong hostSend) ||
                !ProbeFields.TryReadULongField(line, "probe_rx_us", out ulong probeReceive) ||
                !ProbeFields.TryReadULongField(line, "probe_tx_us", out ulong probeSend) ||
                !ProbeFields.TryReadULongField(line, "boot", out ulong boot))
            {
                return false;
            }

            if (hostSend > long.MaxValue || (long)hostSend > hostReceiveUs || probeSend < probeReceive)
            {
                return false;
            }

            exchange = new PingExchange((long)hostSend, hostReceiveUs, probeReceive, probeSend, boot);
            return true;
        }

        private static bool IsPlausibleDbm(int dbm)
        {
            return dbm >= -128 && dbm <= 20;
        }
    }
}
