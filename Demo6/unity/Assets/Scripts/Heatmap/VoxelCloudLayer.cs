using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WifiHeatmap.Core;
using WifiHeatmap.Recording;

namespace WifiHeatmap.Heatmap
{
    /// <summary>Anchor-relative sparse cloud. Keeps bounded raw observations so resolution is reversible.
    /// Only measured cells are drawn; billboard skirts soften edges, not RF extrapolation.</summary>
    public sealed class VoxelCloudLayer : IDisposable
    {
        public const int MaxSamples = 100000, MaxVisibleCells = 20000;
        private sealed class Group
        {
            public VoxelGrid Grid;
            public readonly List<(SampleRecord Sample, float Quality)> Raw = new List<(SampleRecord, float)>();
            public GameObject Object;
            public Mesh Mesh;
            public bool Dirty = true;
            public double MinimumDbm = double.PositiveInfinity, MaximumDbm = double.NegativeInfinity;
        }
        private readonly Dictionary<Guid, Group> groups = new Dictionary<Guid, Group>();
        private readonly AnchorRegistry anchors;
        private readonly CloudSettings settings;
        private readonly Material material;
        private readonly string label;
        private int rawCount;
        private bool rebinPending;
        public object DatasetIdentity { get; private set; } = new object();
        public int Samples => rawCount;
        public int Cells { get; private set; }
        public int Unresolved { get; private set; }
        public int OmittedCells { get; private set; }
        public int LimitDrops { get; private set; }
        public int DegradedTimingSamples { get; private set; }
        public bool Visible { get; set; } = true;
        public VoxelCloudLayer(AnchorRegistry anchors, CloudSettings settings, Material material, string label)
        { this.anchors = anchors; this.settings = settings; this.material = material; this.label = label; settings.Changed += OnSettings; }
        public void Add(Guid anchor, SampleRecord sample, long uncertaintyUs = -1)
        {
            // Timing-degraded observations can be previewed with lower confidence. Tracking loss,
            // unresolved alignment and known uncertainty beyond the preview budget remain excluded.
            if (!SpatialDisplayQuality.CanDisplay(sample.Flags, uncertaintyUs)) return;
            float quality = SpatialDisplayQuality.Weight(sample.Flags, uncertaintyUs);
            if (rawCount >= MaxSamples) { LimitDrops++; return; }
            if (!groups.TryGetValue(anchor, out Group group))
            { group = new Group { Grid = new VoxelGrid(settings.Resolution) }; groups.Add(anchor, group); }
            if (!group.Grid.Add(sample.Position, sample.RssiDbm, sample.StdDevDb, sample.TimeUs, quality)) return;
            group.Raw.Add((sample, quality)); rawCount++; group.Dirty = true;
            group.MinimumDbm = Math.Min(group.MinimumDbm, sample.RssiDbm);
            group.MaximumDbm = Math.Max(group.MaximumDbm, sample.RssiDbm);
            if ((sample.Flags & SampleFlags.ClockUncertain) != 0 || uncertaintyUs > SpatialDisplayQuality.PreciseTimingUs)
                DegradedTimingSamples++;
        }
        private void OnSettings(bool rebin)
        {
            // Coalesce rapid slider changes; rebuild at the display refresh cadence.
            rebinPending |= rebin;
            foreach (Group group in groups.Values) group.Dirty = true;
        }
        public void Refresh()
        {
            if (rebinPending)
            {
                foreach (Group group in groups.Values)
                {
                    group.Grid = new VoxelGrid(settings.Resolution);
                    foreach (var observation in group.Raw)
                    {
                        SampleRecord sample = observation.Sample;
                        group.Grid.Add(sample.Position, sample.RssiDbm, sample.StdDevDb, sample.TimeUs, observation.Quality);
                    }
                }
                rebinPending = false;
            }
            // Resolve once, then fit the visible dataset before building any mesh colors. A hidden
            // live layer must not overwrite the range chosen for a loaded session (or vice versa).
            var resolved = new Dictionary<Guid, AnchorRegistry.AnchorInfo>();
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            foreach (var pair in groups)
            {
                if (!anchors.TryResolve(pair.Key, out var anchor) || !anchor.Localized || anchor.Transform == null) continue;
                resolved.Add(pair.Key, anchor);
                if (pair.Value.Raw.Count == 0) continue;
                minimum = Math.Min(minimum, pair.Value.MinimumDbm);
                maximum = Math.Max(maximum, pair.Value.MaximumDbm);
            }
            if (Visible) settings.ApplyAutoRange(minimum, maximum);
            Cells = Unresolved = OmittedCells = 0;
            int remaining = MaxVisibleCells;
            foreach (var pair in groups)
            {
                Group group = pair.Value;
                bool hasAnchor = resolved.TryGetValue(pair.Key, out AnchorRegistry.AnchorInfo anchor);
                if (!hasAnchor)
                {
                    Unresolved += group.Raw.Count;
                    if (group.Object != null) group.Object.SetActive(false);
                    continue;
                }
                int shown = Math.Min(remaining, group.Grid.Count);
                remaining -= shown; Cells += shown; OmittedCells += group.Grid.Count - shown;
                if (group.Object == null)
                {
                    group.Object = new GameObject(label);
                    group.Object.AddComponent<MeshFilter>();
                    var renderer = group.Object.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                    group.Dirty = true;
                }
                group.Object.transform.SetParent(anchor.Transform, false);
                group.Object.SetActive(Visible && shown > 0);
                if (group.Dirty || group.Mesh == null || group.Mesh.vertexCount != shown * 4)
                {
                    if (group.Mesh != null) UnityEngine.Object.Destroy(group.Mesh);
                    group.Mesh = Build(group.Grid, shown);
                    group.Object.GetComponent<MeshFilter>().sharedMesh = group.Mesh;
                    group.Dirty = false;
                }
            }
        }
        private Mesh Build(VoxelGrid grid, int count)
        {
            var vertices = new Vector3[count * 4]; var colors = new Color[count * 4];
            var uv = new Vector2[count * 4]; var sizes = new Vector2[count * 4]; var indices = new int[count * 6];
            Vector2[] corners = { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            int i = 0;
            Bounds bounds = new Bounds();
            foreach (VoxelGrid.Cell cell in grid.Cells)
            {
                if (i == count) break;
                Vector3 center = cell.Center.ToUnity();
                if (i == 0) bounds = new Bounds(center, Vector3.zero); else bounds.Encapsulate(center);
                Color color = settings.Evaluate((float)cell.MeanDbm);
                // Keep sparse measured cells visible, while retaining a separate confidence fade.
                // The minimum is 20% of the user's translucent opacity, never an opaque fill.
                color.a = settings.Opacity * Mathf.Lerp(.2f, 1, cell.Confidence(grid.Resolution));
                for (int c = 0; c < 4; c++)
                { vertices[i * 4 + c] = center; colors[i * 4 + c] = color; uv[i * 4 + c] = corners[c]; sizes[i * 4 + c] = new Vector2(grid.Resolution * .9f, 0); }
                int v = i * 4, t = i * 6;
                indices[t] = v; indices[t + 1] = v + 1; indices[t + 2] = v + 2;
                indices[t + 3] = v; indices[t + 4] = v + 2; indices[t + 5] = v + 3; i++;
            }
            var mesh = new Mesh { name = "Measured voxel cloud", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uv); mesh.SetUVs(1, sizes); mesh.SetTriangles(indices, 0);
            bounds.Expand(grid.Resolution * 2); mesh.bounds = bounds;
            return mesh;
        }
        /// <summary>Read-only display estimates, with the same resolution/confidence and cell cap as the cloud.
        /// Positions remain anchor-local. No renderer geometry or raw records need to be inspected by a volume.</summary>
        public void CopyCells(List<CloudCell> output)
        {
            int remaining = MaxVisibleCells;
            foreach (var pair in groups)
            {
                if (!anchors.TryResolve(pair.Key, out var anchor)) continue;
                foreach (VoxelGrid.Cell cell in pair.Value.Grid.Cells)
                {
                    if (remaining-- <= 0) return;
                    output.Add(new CloudCell(anchor.Transform, cell.Center.ToUnity(), (float)cell.MeanDbm,
                        cell.Confidence(pair.Value.Grid.Resolution)));
                }
            }
        }
        public readonly struct CloudCell
        {
            public readonly Transform Anchor;
            public readonly Vector3 Center;
            public readonly float Dbm, Confidence;
            public CloudCell(Transform anchor, Vector3 center, float dbm, float confidence)
            { Anchor = anchor; Center = center; Dbm = dbm; Confidence = confidence; }
        }
        /// <summary>Borrowed display meshes only. Consumers must not mutate or destroy them.
        /// Resolve alignment again on every read; a stale visible GameObject is not proof of localization.</summary>
        public void CopyVisuals(List<CloudVisual> output)
        {
            foreach (var pair in groups)
            {
                Group group = pair.Value;
                if (group.Mesh == null || group.Mesh.vertexCount == 0 ||
                    !anchors.TryResolve(pair.Key, out var anchor)) continue;
                output.Add(new CloudVisual(anchor.Transform, group.Mesh, material));
            }
        }
        public readonly struct CloudVisual
        {
            public readonly Transform Anchor;
            public readonly Mesh Mesh;
            public readonly Material Material;
            public CloudVisual(Transform anchor, Mesh mesh, Material material)
            { Anchor = anchor; Mesh = mesh; Material = material; }
        }
        public void Clear()
        {
            foreach (Group group in groups.Values)
            { if (group.Mesh != null) UnityEngine.Object.Destroy(group.Mesh); if (group.Object != null) UnityEngine.Object.Destroy(group.Object); }
            DatasetIdentity = new object();
            groups.Clear(); rawCount = Cells = Unresolved = OmittedCells = LimitDrops = DegradedTimingSamples = 0;
        }
        public void Dispose() { settings.Changed -= OnSettings; Clear(); }
    }
}
