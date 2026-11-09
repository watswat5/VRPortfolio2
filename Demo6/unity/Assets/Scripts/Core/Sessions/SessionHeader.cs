using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// session.json: everything about a recording that is not per-sample. Rewritten (atomically)
    /// when the anchor table grows and when the session ends.
    /// </summary>
    public sealed class SessionHeader
    {
        public const string FormatName = "wifiheatmap.session";
        public const int CurrentFormatVersion = 1;

        public string Format { get; set; } = FormatName;
        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public string SessionId { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public AppInfo App { get; set; } = new AppInfo();
        public AccessPointInfo AccessPoint { get; set; } = new AccessPointInfo();
        public ProbeInfo Probe { get; set; } = new ProbeInfo();
        public MountInfo Mount { get; set; } = new MountInfo();
        /// <summary>Anchors that samples are relative to; a record's AnchorIndex is a row here. Only grows.</summary>
        public List<AnchorEntry> Anchors { get; set; } = new List<AnchorEntry>();
        public RecordsInfo Records { get; set; } = new RecordsInfo();
        public string Notes { get; set; } = string.Empty;

        public sealed class AppInfo
        {
            public string Version { get; set; } = string.Empty;
            public string Unity { get; set; } = string.Empty;
            public string MetaXr { get; set; } = string.Empty;
        }

        public sealed class AccessPointInfo
        {
            public string Bssid { get; set; } = string.Empty;
            public int Channel { get; set; }
            public string Band { get; set; } = string.Empty;
            public string Ssid { get; set; } = string.Empty;
            public string Security { get; set; } = string.Empty;
            /// <summary>A network's radios on Channel measured together (strongest first), each sample the strongest
            /// of them; empty for a single radio (Bssid) and in sessions recorded before protocol 1.2.</summary>
            public List<string> Bssids { get; set; } = new List<string>();
            /// <summary>How many radios the probe measured (its SELECTED aps=); 0 in sessions before protocol 1.2.</summary>
            public int MeasuredRadios { get; set; }
        }

        public sealed class ProbeInfo
        {
            public string DeviceName { get; set; } = string.Empty;
            public string Hello { get; set; } = string.Empty;
            public string Caps { get; set; } = string.Empty;
            public ulong BootId { get; set; }
            public int ReportIntervalMs { get; set; }
        }

        public sealed class MountInfo
        {
            public string MountId { get; set; } = string.Empty;
            public string CalibrationId { get; set; } = string.Empty;
            /// <summary>Optional full transform snapshot; older sessions contain only the IDs.</summary>
            public ProbeCalibration Calibration { get; set; }
        }

        public sealed class AnchorEntry
        {
            public const string MrukRoom = "mrukRoom";
            public const string SpatialAnchor = "spatialAnchor";

            public int Index { get; set; }
            /// <summary><see cref="MrukRoom"/> or <see cref="SpatialAnchor"/>.</summary>
            public string Kind { get; set; } = string.Empty;
            public Guid Uuid { get; set; }
            public string Label { get; set; } = string.Empty;
        }

        public sealed class RecordsInfo
        {
            public string File { get; set; } = SessionFiles.SamplesFileName;
            public int RecordSize { get; set; } = SampleRecord.Size;
            public long Count { get; set; }
            /// <summary>True once the session was finalised, by Stop or by crash recovery.</summary>
            public bool Complete { get; set; }
            /// <summary>True if crash recovery finalised it (the tail after the last good block was dropped).</summary>
            public bool Recovered { get; set; }
        }

        public int IndexOfAnchor(Guid uuid)
        {
            return Anchors.FindIndex(a => a.Uuid == uuid);
        }

        /// <summary>Adds an anchor to the table (if new) and returns its index.</summary>
        public int AddAnchor(string kind, Guid uuid, string label)
        {
            int index = IndexOfAnchor(uuid);
            if (index >= 0)
            {
                return index;
            }
            if (Anchors.Count > byte.MaxValue)
            {
                throw new InvalidOperationException("A session can reference at most 256 anchors.");
            }
            Anchors.Add(new AnchorEntry { Index = Anchors.Count, Kind = kind, Uuid = uuid, Label = label ?? string.Empty });
            return Anchors.Count - 1;
        }

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Include,
        };

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Settings);
        }

        public static SessionHeader FromJson(string json)
        {
            SessionHeader header = JsonConvert.DeserializeObject<SessionHeader>(json, Settings);
            if (header == null || header.Format != FormatName)
            {
                throw new FormatException("Not a WiFi heatmap session header.");
            }
            if (header.FormatVersion != CurrentFormatVersion)
            {
                throw new FormatException($"Session format version {header.FormatVersion} is not supported (expected {CurrentFormatVersion}).");
            }
            return header;
        }
    }
}
