using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace WifiHeatmap.Core
{
    /// <summary>Versioned rigid transforms in Unity controller-local coordinates. No vendor types.</summary>
    public sealed class ProbeCalibration
    {
        public const string Convention = "unity-left-handed-metres-xyzw-controllerFromPod-podFromAntenna";
        [JsonProperty(Required = Required.Always)]
        public int Version { get; set; } = 1;
        [JsonProperty(Required = Required.Always)]
        public string Coordinates { get; set; } = Convention;
        [JsonProperty(Required = Required.Always)]
        public string MountId { get; set; }
        [JsonProperty(Required = Required.Always)]
        public string Controller { get; set; }
        [JsonProperty(Required = Required.Always)]
        public string HeadsetModel { get; set; }
        [JsonProperty(Required = Required.Always)]
        public string CalibrationId { get; set; }
        public DateTime SavedUtc { get; set; }
        public string Method { get; set; } = "manual-visual-alignment";
        [JsonProperty(Required = Required.Always)]
        public float[] PodPosition { get; set; }
        [JsonProperty(Required = Required.Always)]
        public float[] PodRotation { get; set; }
        [JsonProperty(Required = Required.Always)]
        public float[] AntennaPosition { get; set; }
        [JsonProperty(Required = Required.Always)]
        public float[] AntennaRotation { get; set; }

        public void Validate(string mount, string controller, string model)
        {
            if (Version != 1 || Coordinates != Convention)
                throw new InvalidDataException("Unsupported probe calibration format or coordinates.");
            if (string.IsNullOrWhiteSpace(MountId) || string.IsNullOrWhiteSpace(Controller) || string.IsNullOrWhiteSpace(HeadsetModel))
                throw new InvalidDataException("Calibration mount, controller and headset model are required.");
            if (MountId != mount || Controller != controller || HeadsetModel != model)
                throw new InvalidDataException("Calibration belongs to a different mount, controller, or headset model.");
            if (string.IsNullOrWhiteSpace(CalibrationId) || CalibrationId.Length > 128)
                throw new InvalidDataException("Calibration identity is missing or too long.");
            Position(PodPosition);
            Position(AntennaPosition);
            Rotation(PodRotation);
            Rotation(AntennaRotation);
        }

        private static void Position(float[] value)
        {
            if (!Finite(value, 3) || value.Sum(x => x * x) > 0.5f * 0.5f)
                throw new InvalidDataException("Calibration position must be finite and within 0.5 m of its parent.");
        }

        private static void Rotation(float[] value)
        {
            if (!Finite(value, 4) || Math.Abs(value.Sum(x => x * x) - 1f) > 0.001f)
                throw new InvalidDataException("Calibration rotation must be a normalized finite quaternion.");
        }

        private static bool Finite(float[] value, int length) => value != null && value.Length == length &&
            value.All(x => !float.IsNaN(x) && !float.IsInfinity(x));
    }

    public static class ProbeCalibrationFile
    {
        public const int MaximumBytes = 16384;

        public static ProbeCalibration Load(string path, string mount, string controller, string model)
        {
            if (new FileInfo(path).Length > MaximumBytes)
                throw new InvalidDataException("Calibration file is too large.");
            var data = JsonConvert.DeserializeObject<ProbeCalibration>(File.ReadAllText(path),
                new JsonSerializerSettings { MaxDepth = 8 });
            if (data == null) throw new InvalidDataException("Empty calibration file.");
            data.Validate(mount, controller, model);
            return data;
        }

        public static void Save(string path, ProbeCalibration data)
        {
            data.Validate(data.MountId, data.Controller, data.HeadsetModel);
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidDataException("Calibration file is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    /// <summary>Mirrors a controller-local transform across controller X, between the left and right controllers.</summary>
    public static class ProbeMirror
    {
        public static float[] Position(float[] p) => new[] { -p[0], p[1], p[2] };
        public static float[] Rotation(float[] q) => new[] { q[0], -q[1], -q[2], q[3] };
    }
}
