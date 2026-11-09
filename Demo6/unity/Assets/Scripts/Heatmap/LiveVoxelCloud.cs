using UnityEngine;
using WifiHeatmap.Probe;
using WifiHeatmap.Recording;
using WifiHeatmap.Core;

namespace WifiHeatmap.Heatmap
{
    public sealed class LiveVoxelCloud : MonoBehaviour
    {
        [SerializeField] private SessionRecorder recorder;
        [SerializeField] private SessionViewer viewer;
        [SerializeField] private AnchorRegistry anchors;
        [SerializeField] private Esp32BleSignalTester probe;
        [SerializeField] private ProbeMount mount;
        [SerializeField] private CloudSettings settings;
        [SerializeField] private Material cloudMaterial;
        private VoxelCloudLayer layer;
        private float nextRefresh;
        public string Status => layer == null ? "Cloud starting..." :
            $"Live: {layer.Cells} voxels / {layer.Samples} samples; {layer.Unresolved} unresolved\n" +
            (mount.IsFrozenAlignment || mount.HasUnsavedProbeOffset ? "Finish and save mount adjustment to place readings." :
            !probe.IsStreaming ? "Select a network to measure. Record to save the scan." :
            recorder.PlacementStatus) +
            (layer.LimitDrops > 0 || layer.OmittedCells > 0 ? $"\nDisplay limit: {layer.LimitDrops} samples / {layer.OmittedCells} cells omitted. Recordings retain raw samples." : "");
        private void OnEnable()
        {
            layer = new VoxelCloudLayer(anchors, settings, cloudMaterial, "Live Wi-Fi cloud");
            recorder.SpatialSampleReceived += Add;
            probe.TargetChanged += TargetChanged;
            probe.StateChanged += StateChanged;
            mount.CalibrationChanged += Clear;
        }
        private void OnDisable()
        {
            recorder.SpatialSampleReceived -= Add; probe.TargetChanged -= TargetChanged;
            probe.StateChanged -= StateChanged; mount.CalibrationChanged -= Clear;
            layer?.Dispose(); layer = null;
        }
        private void Add(AnchorRegistry.AnchorInfo anchor, SampleRecord sample, long uncertaintyUs) { layer.Add(anchor.Uuid, sample, uncertaintyUs); }
        private void TargetChanged(Esp32BleSignalTester.AccessPointInfo _) { Clear(); viewer.Unload(); }
        private void StateChanged(Esp32BleSignalTester.SessionState state)
        {
            if (state == Esp32BleSignalTester.SessionState.Streaming) { Clear(); viewer.Unload(); }
        }
        public void Clear() { layer?.Clear(); }
        public object DatasetIdentity => layer?.DatasetIdentity;
        public void CopyCells(System.Collections.Generic.List<VoxelCloudLayer.CloudCell> output)
        { layer?.CopyCells(output); }
        public void CopyVisuals(System.Collections.Generic.List<VoxelCloudLayer.CloudVisual> output)
        { layer?.CopyVisuals(output); }
        private void Update()
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .1f;
            layer.Visible = viewer.LoadedFolder == null;
            layer.Refresh();
        }
    }
}
