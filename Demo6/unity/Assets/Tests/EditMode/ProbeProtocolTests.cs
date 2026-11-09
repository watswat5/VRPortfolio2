using NUnit.Framework;

namespace WifiHeatmap.Core.Tests
{
    /// <summary>Field parsing and SAMPLE/PONG lines, using the exact formats the firmware prints.</summary>
    public sealed class ProbeProtocolTests
    {
        // Same field order and number formats as WiFiProbe.ino's SAMPLE printf.
        private const string Sample =
            "SAMPLE session=17 seq=4 probe_us=9320000 bssid=aa:bb:cc:dd:ee:ff channel=36 rssi=-58 frames=13 " +
            "mean=-58.46 min=-64 max=-55 stddev=2.41 window_start_us=9270000 window_end_us=9370000";

        private const string Pong = "PONG 5 host_us=3810042000 probe_rx_us=9211000 probe_tx_us=9211068 boot=9842331";

        [Test]
        public void ParsesFirmwareSample()
        {
            Assert.IsTrue(ProbeLineParser.TryParseSample(Sample, 123, out ProbeSample s));
            Assert.AreEqual(17UL, s.SessionId);
            Assert.AreEqual(4UL, s.Sequence);
            Assert.AreEqual(9320000UL, s.ProbeTimeUs);
            Assert.AreEqual("AA:BB:CC:DD:EE:FF", s.Bssid);
            Assert.AreEqual(36, s.Channel);
            Assert.AreEqual(-58, s.RssiDbm);
            Assert.AreEqual(13UL, s.Frames);
            Assert.AreEqual(-58.46, s.MeanDbm, 1e-9);
            Assert.AreEqual(-64, s.MinDbm);
            Assert.AreEqual(-55, s.MaxDbm);
            Assert.AreEqual(2.41, s.StdDevDb, 1e-9);
            Assert.AreEqual(9270000UL, s.WindowStartUs);
            Assert.AreEqual(9370000UL, s.WindowEndUs);
            Assert.AreEqual(123L, s.ReceivedHostUs);
            Assert.IsFalse(s.HasHostTime, "no clock mapping applied yet");
        }

        [Test]
        public void SampleToleratesUnknownTrailingFields()
        {
            Assert.IsTrue(ProbeLineParser.TryParseSample(Sample + " future_field=7 note=\"hi there\"", 0, out _));
        }

        [Test]
        public void SampleWithoutWindowFieldsIsAccepted()
        {
            string line = Sample.Substring(0, Sample.IndexOf(" window_start_us"));
            Assert.IsTrue(ProbeLineParser.TryParseSample(line, 0, out ProbeSample s));
            Assert.AreEqual(0UL, s.WindowStartUs);
        }

        [TestCase("frames=13 ", "", TestName = "missing frames")]
        [TestCase("bssid=aa:bb:cc:dd:ee:ff", "bssid=aa:bb:cc:dd:ee", TestName = "short bssid")]
        [TestCase("rssi=-58", "rssi=-500", TestName = "rssi out of range")]
        [TestCase("rssi=-58", "rssi=abc", TestName = "rssi not a number")]
        [TestCase("channel=36", "channel=0", TestName = "channel zero")]
        [TestCase("frames=13", "frames=0", TestName = "no frames")]
        [TestCase("mean=-58.46", "mean=-70.00", TestName = "mean below min")]
        [TestCase("stddev=2.41", "stddev=-1.00", TestName = "negative stddev")]
        [TestCase("session=17", "session=0", TestName = "session zero")]
        [TestCase(" window_end_us=9370000", "", TestName = "window start without end")]
        [TestCase("window_start_us=9270000", "window_start_us=9330000", TestName = "midpoint outside window")]
        [TestCase("SAMPLE ", "SAMPLES ", TestName = "wrong line kind")]
        public void RejectsBadSample(string find, string replace)
        {
            StringAssert.Contains(find, Sample);
            Assert.IsFalse(ProbeLineParser.TryParseSample(Sample.Replace(find, replace), 0, out _));
        }

        [Test]
        public void ParsesPong()
        {
            Assert.IsTrue(ProbeLineParser.TryParsePong(Pong, 3810060000, out PingExchange e));
            Assert.AreEqual(3810042000L, e.HostSendUs);
            Assert.AreEqual(3810060000L, e.HostReceiveUs);
            Assert.AreEqual(9211000UL, e.ProbeReceiveUs);
            Assert.AreEqual(9211068UL, e.ProbeSendUs);
            Assert.AreEqual(9842331UL, e.BootId);
            Assert.AreEqual(18000 - 68, e.RoundTripUs);
        }

        [Test]
        public void RejectsPongArrivingBeforeItWasSent()
        {
            Assert.IsFalse(ProbeLineParser.TryParsePong(Pong, 3810041000, out _));
        }

        [Test]
        public void RejectsPongWithProbeSendBeforeReceive()
        {
            string line = Pong.Replace("probe_tx_us=9211068", "probe_tx_us=9210000");
            Assert.IsFalse(ProbeLineParser.TryParsePong(line, 3810060000, out _));
        }

        [Test]
        public void KeysMustStartAToken()
        {
            Assert.IsTrue(ProbeFields.TryReadIntField("X xrssi=-5 rssi=-60", "rssi", out int rssi));
            Assert.AreEqual(-60, rssi);
            Assert.IsFalse(ProbeFields.TryReadIntField("X xrssi=-5", "rssi", out _));
        }

        [Test]
        public void QuotedFieldsKeepSpacesAndDecodeEscapes()
        {
            const string line = "AP 3 bssid=00:11:22:33:44:55 ssid=\"Home \\\"WiFi\\\" \\\\ 5G\\u00E9\" security=\"WPA2\"";
            Assert.IsTrue(ProbeFields.TryReadQuotedField(line, "ssid", out string ssid));
            Assert.AreEqual("Home \"WiFi\" \\ 5Gé", ssid);
            Assert.IsTrue(ProbeFields.TryReadQuotedField(line, "security", out string security));
            Assert.AreEqual("WPA2", security);
        }

        [Test]
        public void UnterminatedQuotedFieldIsRejected()
        {
            Assert.IsFalse(ProbeFields.TryReadQuotedField("AP 3 ssid=\"never closed", "ssid", out _));
        }

        [Test]
        public void EmptyQuotedFieldIsValid()
        {
            Assert.IsTrue(ProbeFields.TryReadQuotedField("AP 3 ssid=\"\" x=1", "ssid", out string ssid));
            Assert.AreEqual(string.Empty, ssid);
        }

        [TestCase("aa:bb:cc:dd:ee:ff", "AA:BB:CC:DD:EE:FF")]
        [TestCase(" 00:11:22:33:44:55 ", "00:11:22:33:44:55")]
        public void NormalizesBssid(string input, string expected)
        {
            Assert.IsTrue(ProbeFields.TryNormalizeBssid(input, out string normalized));
            Assert.AreEqual(expected, normalized);
        }

        [TestCase("aa:bb:cc:dd:ee")]
        [TestCase("aa:bb:cc:dd:ee:fg")]
        [TestCase("aabb.ccdd.eeff")]
        [TestCase("a:bb:cc:dd:ee:ff")]
        [TestCase(null)]
        public void RejectsBadBssid(string input)
        {
            Assert.IsFalse(ProbeFields.TryNormalizeBssid(input, out _));
        }

        [Test]
        public void FieldsAfterIdSkipsKindAndId()
        {
            Assert.AreEqual("protocol=1.0 boot=9", ProbeFields.FieldsAfterId("HELLO 1 protocol=1.0 boot=9"));
            Assert.AreEqual(string.Empty, ProbeFields.FieldsAfterId("HELLO 1"));
        }

        [TestCase("HELLO 1 protocol=1.1 device=\"WiFiProbe\" fw=\"0.3.0\" boot=7", 1, 1)]
        [TestCase("HELLO 1 protocol=1.0 device=\"WiFiProbe\" fw=\"0.2.0\" boot=7", 1, 0)]
        [TestCase("HELLO 1 protocol=1 boot=7", 1, 0)]
        public void HelloProtocolIsParsed(string line, int major, int minor)
        {
            Assert.IsTrue(ProbeProtocol.TryParseProtocol(line, out int parsedMajor, out int parsedMinor));
            Assert.AreEqual(major, parsedMajor);
            Assert.AreEqual(minor, parsedMinor);
        }

        [TestCase("HELLO 1 boot=7")]
        [TestCase("HELLO 1 protocol=one.1 boot=7")]
        [TestCase("HELLO 1 protocol=1.x boot=7")]
        public void MalformedHelloProtocolIsRejected(string line)
        {
            Assert.IsFalse(ProbeProtocol.TryParseProtocol(line, out _, out _));
        }

        [TestCase(1, 1, 1)]   // new app, new firmware
        [TestCase(1, 0, 0)]   // new app, 1.0 firmware: falls back to 1.0
        [TestCase(0, 1, 0)]   // fallback HELLO 1.0, firmware that over-reports: never above what was asked
        [TestCase(1, 5, 1)]   // a future firmware minor never exceeds the host's
        public void NegotiationUsesTheEarlierMinor(int requested, int reply, int expected)
        {
            Assert.AreEqual(expected, ProbeProtocol.Negotiate(requested, reply));
        }

        [Test]
        public void HelloCommandAsksForAMinor()
        {
            Assert.AreEqual("HELLO 3 1.1", ProbeProtocol.HelloCommand(3, 1));
            Assert.AreEqual("HELLO 4 1.0", ProbeProtocol.HelloCommand(4, 0));
        }

        [TestCase("BUTTONS buttons=3 probe_us=12", true, 3)]
        [TestCase("SAMPLE session=1 seq=2 probe_us=3 bssid=00:11:22:33:44:55 channel=6 rssi=-50 frames=2 mean=-50 min=-51 max=-49 stddev=1 buttons=1", true, 1)]
        [TestCase("SAMPLE session=1 seq=2 probe_us=3 bssid=00:11:22:33:44:55 channel=6 rssi=-50 frames=2 mean=-50 min=-51 max=-49 stddev=1", false, 0)]
        [TestCase("CAPS 2 buzzer=true buttons=\"A,B\"", false, 0)]
        [TestCase("BUTTONS buttons=-1 probe_us=12", false, 0)]
        public void ButtonStateIsOptional(string line, bool present, int expected)
        {
            Assert.AreEqual(present, ProbeProtocol.TryParseButtons(line, out int buttons));
            if (present) Assert.AreEqual(expected, buttons);
        }

        [Test]
        public void ButtonsFieldDoesNotBreakSampleParsing()
        {
            const string line = "SAMPLE session=1 seq=2 probe_us=3 bssid=00:11:22:33:44:55 channel=6 rssi=-50 frames=2 mean=-50 min=-51 max=-49 stddev=1 buttons=2";
            Assert.IsTrue(ProbeLineParser.TryParseSample(line, 10, out ProbeSample sample));
            Assert.AreEqual(-50, sample.RssiDbm);
        }

        [Test]
        public void ScanWaitsLongForTheFirstLineThenShortBetweenLines()
        {
            // Nothing yet: the probe is still scanning, so the deadline runs from the command.
            Assert.AreEqual(130.0, ProbeProtocol.ScanReplyDeadline(100.0, null, 30.0, 5.0));
            // Once lines arrive, each one extends the wait by the gap, so a lost SCAN_DONE ends it soon after.
            Assert.AreEqual(117.0, ProbeProtocol.ScanReplyDeadline(100.0, 112.0, 30.0, 5.0));
        }
    }
}
