using System;
using UnityEngine;
using WifiHeatmap.Core;
using WifiHeatmap.Heatmap;

namespace WifiHeatmap.Recording
{
    /// <summary>Rebins saved raw observations into the same translucent cloud used during capture.
    /// Unresolved anchors and invalid tracking/timing samples are never painted as measured signal.</summary>
    [DisallowMultipleComponent]
    public sealed class SessionViewer : MonoBehaviour
    {
        [SerializeField] private AnchorRegistry anchors;
        [SerializeField] private CloudSettings settings;
        [SerializeField] private Material cloudMaterial;
        private SessionData loaded;
        private VoxelCloudLayer layer;
        private float nextRefresh;
        private int rejected;
        public string LoadedFolder => loaded?.Folder;
        public SessionHeader LoadedHeader => loaded?.Header;
        public int Shown { get; private set; }
        public int Unresolved { get; private set; }
        public string Summary { get; private set; } = string.Empty;
        public event Action Changed;
        public bool Load(string folder)
        {
            Unload();
            try
            {
                loaded = SessionStore.Read(folder);
                layer = new VoxelCloudLayer(anchors, settings, cloudMaterial, "Saved Wi-Fi cloud");
                foreach (SampleRecord record in loaded.Records)
                {
                    if (!SpatialDisplayQuality.CanDisplay(record.Flags)) { rejected++; continue; }
                    layer.Add(loaded.Header.Anchors[record.AnchorIndex].Uuid, record);
                }
                Refresh(); return true;
            }
            catch (Exception exception)
            {
                Unload(); Summary = $"Could not read the session: {exception.Message}"; Changed?.Invoke(); return false;
            }
        }
        public void Unload()
        {
            layer?.Dispose(); layer = null; loaded = null;
            Shown = Unresolved = rejected = 0; Summary = string.Empty; Changed?.Invoke();
        }
        private void OnDestroy() { layer?.Dispose(); }
        private void OnDisable() { if (layer != null) { layer.Visible = false; layer.Refresh(); } }
        public object DatasetIdentity => layer?.DatasetIdentity;
        public void CopyCells(System.Collections.Generic.List<VoxelCloudLayer.CloudCell> output)
        { layer?.CopyCells(output); }
        public void CopyVisuals(System.Collections.Generic.List<VoxelCloudLayer.CloudVisual> output)
        { layer?.CopyVisuals(output); }
        private void Update()
        {
            if (layer == null || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .25f; layer.Visible = true; Refresh();
        }
        private void Refresh()
        {
            layer.Refresh(); Shown = layer.Samples - layer.Unresolved; Unresolved = layer.Unresolved;
            Summary = $"{layer.Cells} voxels / {Shown} samples; {Unresolved} unresolved, {rejected} quality rejected";
            if (layer.LimitDrops > 0 || layer.OmittedCells > 0)
                Summary += $"; display limit: {layer.LimitDrops} samples / {layer.OmittedCells} cells omitted";
            if (layer.DegradedTimingSamples > 0) Summary += $"; {layer.DegradedTimingSamples} timing-degraded (bound unavailable in saved format)";
            if (loaded.DroppedBytes > 0) Summary += "; damaged tail ignored";
            Changed?.Invoke();
        }
    }
}
