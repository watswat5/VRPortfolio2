using System;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// WiFiProbe protocol versions. The host asks for 1.<see cref="HostMinor"/> in HELLO; the probe answers the lower
    /// of that and its own maximum, so mismatched app and firmware settle on the earliest version both support:
    /// 1.0 firmware answers protocol=1.0, and 1.1 firmware answering a 1.0 host behaves exactly like 1.0.
    /// 1.1 adds the BUZZ / BUZZ_OFF commands, CAPS buzzer= and buttons=, and button state as a buttons= field on
    /// SAMPLE and STATUS plus BUTTONS events. A host must only use them when the negotiated minor is at least 1.
    /// 1.2 adds SELECT ... group=ssid (every radio of the chosen one's SSID on its channel counts towards the reading),
    /// aps= on SELECTED and STATUS, and CAPS select_group="ssid"; only at minor 2 or more.
    /// </summary>
    public static class ProbeProtocol
    {
        public const int Major = 1;
        /// <summary>The newest minor version this host speaks.</summary>
        public const int HostMinor = 2;
        /// <summary>The first minor version with the buzzer and buttons.</summary>
        public const int BuzzerAndButtonsMinor = 1;
        /// <summary>The first minor version that measures a network's radios on a channel together.</summary>
        public const int NetworkGroupMinor = 2;
        public const int ButtonA = 1, ButtonB = 2;

        public static string HelloCommand(ulong id, int minor) => $"HELLO {id} {Major}.{minor}";

        /// <summary>SELECT for one radio, or (<paramref name="group"/>, 1.2) for every radio the probe scanned with its
        /// SSID on <paramref name="channel"/>.</summary>
        public static string SelectCommand(ulong id, string bssid, int channel, bool group) =>
            group ? $"SELECT {id} {bssid} {channel} group=ssid" : $"SELECT {id} {bssid} {channel}";

        /// <summary>How many radios a SELECTED (or STATUS) line says are measured: its 1.2 aps= field, else 1.</summary>
        public static int MeasuredRadioCount(string line) =>
            ProbeFields.TryReadIntField(line, "aps", out int aps) && aps > 0 ? aps : 1;

        /// <summary>
        /// When a host stops waiting for a SCAN reply. The probe scans before it prints anything, so the first line
        /// gets <paramref name="firstReplySeconds"/> from the command; after that, each AP line may be at most
        /// <paramref name="gapSeconds"/> behind the previous one. A lost SCAN_DONE (or AP line) on a congested BLE
        /// link then ends the wait instead of leaving the host scanning forever. Version-independent: every
        /// firmware ends a scan with SCAN_DONE, so this only acts when that line never arrives.
        /// </summary>
        public static double ScanReplyDeadline(double sentAt, double? lastLineAt, double firstReplySeconds, double gapSeconds) =>
            lastLineAt.HasValue ? lastLineAt.Value + gapSeconds : sentAt + firstReplySeconds;

        /// <summary>Reads protocol=major.minor from a HELLO reply. A reply without a minor counts as minor 0.</summary>
        public static bool TryParseProtocol(string helloReply, out int major, out int minor)
        {
            major = minor = 0;
            if (!ProbeFields.TryReadField(helloReply, "protocol", out string text) || string.IsNullOrEmpty(text))
            {
                return false;
            }
            int dot = text.IndexOf('.');
            string majorText = dot < 0 ? text : text.Substring(0, dot);
            if (!int.TryParse(majorText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out major))
            {
                return false;
            }
            if (dot < 0)
            {
                return true;
            }
            return int.TryParse(text.Substring(dot + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out minor);
        }

        /// <summary>The minor both sides use: never more than the host asked for, whatever the probe claims.</summary>
        public static int Negotiate(int requestedMinor, int replyMinor) => Math.Max(0, Math.Min(requestedMinor, replyMinor));

        /// <summary>Reads the buttons= bitmask (A = 1, B = 2) from a 1.1 SAMPLE, STATUS or BUTTONS line.</summary>
        public static bool TryParseButtons(string line, out int buttons)
        {
            buttons = 0;
            return ProbeFields.TryReadIntField(line, "buttons", out buttons) && buttons >= 0 && buttons <= 255;
        }
    }
}
