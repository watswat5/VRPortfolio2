using System;
using System.Collections.Generic;
using System.Numerics;

namespace WifiHeatmap.Core
{
    /// <summary>Sparse measured cells. Estimator v1: mean observed dBm, at most one contribution
    /// per cell per 250 ms. This is an RSSI display estimator, not a physical power average.</summary>
    public sealed class VoxelGrid
    {
        public sealed class Cell
        {
            public Vector3 Center { get; internal set; }
            public int Reports { get; internal set; }
            public int Contributions { get; internal set; }
            public double MeanDbm { get; internal set; }
            internal long Bucket = long.MinValue;
            internal double M2, WindowVariance, QualitySum;
            internal Vector3 Min, Max;
            public float Confidence(float resolution)
            {
                // Heuristic, not calibrated accuracy. Repeated stationary reports cannot earn full coverage.
                double coverage = Math.Min(1, Vector3.Distance(Min, Max) / resolution);
                double variance = Contributions > 1 ? M2 / (Contributions - 1) : 0;
                return (float)((.2 + .8 * Math.Min(1, Contributions / 8.0)) * (.35 + .65 * coverage) /
                    (1 + Math.Sqrt(Math.Max(0, variance + WindowVariance / Math.Max(1, Contributions))) / 8) * QualitySum / Math.Max(1, Contributions));
            }
        }
        private readonly Dictionary<(int, int, int), Cell> cells = new Dictionary<(int, int, int), Cell>();
        public float Resolution { get; }
        public IEnumerable<Cell> Cells => cells.Values;
        public int Count => cells.Count;
        public VoxelGrid(float resolution)
        {
            if (float.IsNaN(resolution) || float.IsInfinity(resolution) || resolution < .02f || resolution > .5f)
                throw new ArgumentOutOfRangeException(nameof(resolution));
            Resolution = resolution;
        }
        public bool Add(Vector3 position, double dbm, double stddev, long timeUs, float quality = 1)
        {
            if (!Finite(position.X) || !Finite(position.Y) || !Finite(position.Z) ||
                position.LengthSquared() > 1000000 || !Finite(dbm) || dbm < -150 || dbm > 10 ||
                !Finite(stddev) || stddev < 0 || timeUs < 0 || !Finite(quality) || quality < 0 || quality > 1) return false;
            var key = ((int)Math.Floor(position.X / Resolution), (int)Math.Floor(position.Y / Resolution),
                (int)Math.Floor(position.Z / Resolution));
            if (!cells.TryGetValue(key, out Cell cell))
            {
                cell = new Cell { Center = new Vector3(key.Item1 + .5f, key.Item2 + .5f, key.Item3 + .5f) * Resolution,
                    Min = position, Max = position };
                cells.Add(key, cell);
            }
            cell.Reports++;
            cell.Min = Vector3.Min(cell.Min, position); cell.Max = Vector3.Max(cell.Max, position);
            long bucket = timeUs / 250000;
            if (bucket <= cell.Bucket) return true;
            cell.Bucket = bucket;
            cell.Contributions++;
            cell.QualitySum += quality;
            double delta = dbm - cell.MeanDbm;
            cell.MeanDbm += delta / cell.Contributions;
            cell.M2 += delta * (dbm - cell.MeanDbm);
            cell.WindowVariance += stddev * stddev;
            return true;
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
