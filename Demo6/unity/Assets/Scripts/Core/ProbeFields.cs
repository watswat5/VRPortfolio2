using System;
using System.Globalization;
using System.Text;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// Field access for WiFiProbe v1 lines: <c>KIND [id] key=value key="quoted value" ...</c>.
    /// The single parser for the protocol's tokens; callers decide which fields are required.
    /// </summary>
    public static class ProbeFields
    {
        /// <summary>Finds an unquoted key=value token (keys must start a token, so "xrssi=" never matches "rssi").</summary>
        public static bool TryReadField(string line, string key, out string value)
        {
            value = null;
            int start = FindKey(line, key + "=");
            if (start < 0)
            {
                return false;
            }

            start += key.Length + 1;
            int end = line.IndexOf(' ', start);
            if (end < 0)
            {
                end = line.Length;
            }
            value = line.Substring(start, end - start);
            return value.Length > 0;
        }

        public static bool TryReadIntField(string line, string key, out int value)
        {
            value = 0;
            return TryReadField(line, key, out string text) &&
                   int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryReadULongField(string line, string key, out ulong value)
        {
            value = 0;
            return TryReadField(line, key, out string text) &&
                   ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryReadDoubleField(string line, string key, out double value)
        {
            value = 0;
            return TryReadField(line, key, out string text) &&
                   double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture, out value) &&
                   !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>Reads key="..." with the protocol's JSON-style escapes; quoted values may contain spaces.</summary>
        public static bool TryReadQuotedField(string line, string key, out string value)
        {
            value = null;
            int start = FindKey(line, key + "=\"");
            if (start < 0)
            {
                return false;
            }

            StringBuilder text = new StringBuilder();
            for (int i = start + key.Length + 2; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    value = text.ToString();
                    return true;
                }
                if (c != '\\' || i + 1 >= line.Length)
                {
                    text.Append(c);
                    continue;
                }

                char escaped = line[++i];
                switch (escaped)
                {
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'u':
                        if (i + 4 < line.Length &&
                            int.TryParse(line.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            text.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: text.Append(escaped); break;
                }
            }
            return false;   // unterminated string
        }

        /// <summary>Everything after "&lt;REPLY&gt; &lt;id&gt; ".</summary>
        public static string FieldsAfterId(string line)
        {
            int first = line.IndexOf(' ');
            int second = first < 0 ? -1 : line.IndexOf(' ', first + 1);
            return second < 0 ? string.Empty : line.Substring(second + 1);
        }

        /// <summary>Validates "AA:BB:CC:DD:EE:FF" (any case) and returns it upper-case.</summary>
        public static bool TryNormalizeBssid(string input, out string normalized)
        {
            normalized = null;
            if (input == null)
            {
                return false;
            }

            string[] octets = input.Trim().Split(':');
            if (octets.Length != 6)
            {
                return false;
            }

            byte[] bytes = new byte[6];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (octets[i].Length != 2 ||
                    !byte.TryParse(octets[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                {
                    return false;
                }
            }

            normalized = string.Join(":", Array.ConvertAll(bytes, value => value.ToString("X2", CultureInfo.InvariantCulture)));
            return true;
        }

        private static int FindKey(string line, string marker)
        {
            if (line == null)
            {
                return -1;
            }
            int start = line.IndexOf(marker, StringComparison.Ordinal);
            while (start > 0 && line[start - 1] != ' ')
            {
                start = line.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
            }
            return start;
        }
    }
}
