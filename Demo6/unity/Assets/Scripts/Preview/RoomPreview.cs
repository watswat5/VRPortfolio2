using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WifiHeatmap.Heatmap;
using WifiHeatmap.Probe;
using WifiHeatmap.Recording;

namespace WifiHeatmap.Preview
{
    /// <summary>Detached miniature. Transforms affect display copies only; measurement coordinates stay untouched.</summary>
    public sealed class RoomPreview : MonoBehaviour
    {
        [SerializeField] private MrukPreviewSource source;
        [SerializeField] private RoomScanController scan;
        [SerializeField] private LiveVoxelCloud live;
        [SerializeField] private SessionViewer viewer;
        [SerializeField] private ProbeMount mount;
        [SerializeField] private OVRCameraRig rig;
        [SerializeField] private Transform head;
        [SerializeField] private Material roomMaterial;
        [SerializeField] private Material fogMaterial;
        [SerializeField] private CloudSettings settings;
        private MiniatureFog fog;
        private enum CloudMode { Fog, Voxels, Hidden }
        private CloudMode cloudMode = CloudMode.Fog;
        private int fogSteps = 24;
        [SerializeField] private bool signalDensityEnabled;
        [SerializeField, Range(0,1)] private float signalDensityAmount = .5f;
        [Tooltip("The miniature fog's own density, independent of the Cloud tab's opacity (which is the full-size cloud's).")]
        [SerializeField, Range(MinFogDensity, MaxFogDensity)] private float fogDensity = .35f;
        public const float MinFogDensity = .05f, MaxFogDensity = 4f;
        private readonly List<VoxelCloudLayer.CloudCell> fogCells = new List<VoxelCloudLayer.CloudCell>();
        private Transform model, content, marker, probeMarker;
        private MeshFilter geometry;
        private Mesh markerMesh, probeMarkerMesh;
        private readonly List<VoxelCloudLayer.CloudVisual> visuals = new List<VoxelCloudLayer.CloudVisual>();
        private readonly List<MeshFilter> clouds = new List<MeshFilter>();
        private bool shown, raw, hideCeiling = true, reframe = true;
        private bool previousLeft, previousRight, heldLeft, heldRight, armed;
        private int grabMode;
        private Pose startHand;
        private Vector3 startPosition, startMidpoint;
        private Quaternion startRotation;
        private float startScale, startDistance, nextRefresh;
        public bool Shown => shown;
        public bool RawMesh => raw;
        public bool HideCeiling => hideCeiling;
        public bool ShowCloud => cloudMode != CloudMode.Hidden;
        public string CloudModeLabel => cloudMode.ToString();
        public int FogSteps => fogSteps;
        public bool SignalDensityEnabled => signalDensityEnabled;
        public float SignalDensityAmount => signalDensityAmount;
        public float FogDensity => fogDensity;
        public float Scale => model == null ? .1f : model.localScale.x;
        public string Status { get; private set; } = "Show preview to load the room model.";
        private void Awake()
        {
            model = new GameObject("Room miniature (visual copies only)").transform;
            content = new GameObject("Room coordinates").transform; content.SetParent(model,false);
            geometry = MakeVisual("Room geometry",content,roomMaterial);
            marker = MakeVisual("Headset position",content,roomMaterial).transform;
            markerMesh = Arrow("Headset position arrow", 1f, Color.yellow);
            marker.GetComponent<MeshFilter>().sharedMesh = markerMesh;
            // the probe's antenna, pointing where its controller points: cyan like the calibration target, smaller
            probeMarker = MakeVisual("Probe position",content,roomMaterial).transform;
            probeMarkerMesh = Arrow("Probe position arrow", .6f, new Color(.2f,.95f,1f));
            probeMarker.GetComponent<MeshFilter>().sharedMesh = probeMarkerMesh;
            fog = new MiniatureFog(content, fogMaterial);
            ApplySignalDensity();
            model.gameObject.SetActive(false);
        }
        /// <summary>A small tetrahedral arrow pointing along +Z, in room metres.</summary>
        private static Mesh Arrow(string name, float size, Color color)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { new Vector3(0,0,.18f)*size,new Vector3(-.1f,0,-.1f)*size,new Vector3(.1f,0,-.1f)*size,new Vector3(0,.18f,-.05f)*size };
            mesh.triangles = new[] {0,1,2,0,3,1,0,2,3,1,3,2};
            mesh.colors = new[] { color,color,color,color };
            mesh.RecalculateBounds();
            return mesh;
        }
        public void ToggleShown()
        {
            shown = !shown; ResetGrab();
            if (shown) { reframe = true; Refresh(true); }
            else { model.gameObject.SetActive(false); fog.Hide(); }
        }
        public void ToggleGeometry() { raw = !raw; Refresh(true); }
        public void ToggleCeiling() { hideCeiling = !hideCeiling; Rebuild(); }
        public void ToggleCloud() { cloudMode = (CloudMode)(((int)cloudMode + 1) % 3); Refresh(true); }
        public void CycleFogSteps() { fogSteps = fogSteps == 16 ? 24 : fogSteps == 24 ? 48 : 16; Refresh(true); }
        public void ToggleSignalDensity() { signalDensityEnabled = !signalDensityEnabled; ApplySignalDensity(); }
        public void SetSignalDensityAmount(float value)
        { signalDensityAmount = Mathf.Clamp01(value); ApplySignalDensity(); }
        private void ApplySignalDensity() { fog?.SetSignalDensity(signalDensityEnabled,signalDensityAmount); }
        public void SetFogDensity(float value) { fogDensity = Mathf.Clamp(value,MinFogDensity,MaxFogDensity); }
        public void SetScale(float value)
        { if (model != null) { model.localScale = Vector3.one * Mathf.Clamp(value,.02f,1); ResetGrab(); } }
        public void Recenter() { reframe = true; ResetGrab(); Refresh(true); }
        private void Rebuild()
        {
            try { source.Rebuild(hideCeiling); }
            catch (System.Exception exception) { Status = $"Could not build room preview: {exception.Message}"; model.gameObject.SetActive(false); }
        }
        private static MeshFilter MakeVisual(string name, Transform parent, Material material)
        {
            var go = new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer)); go.transform.SetParent(parent,false);
            var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            return go.GetComponent<MeshFilter>();
        }
        private void Update()
        {
            if (!shown) return;
            if (source.Dirty || Time.unscaledTime >= nextRefresh) { Refresh(false); nextRefresh = Time.unscaledTime + .1f; }
            if (model.gameObject.activeSelf && Application.isFocused && OVRManager.hasInputFocus && !scan.Busy && !mount.IsFrozenAlignment)
                Manipulate();
            else ResetGrab();
        }
        private void Refresh(bool force)
        {
            if (!shown) return;
            if (source.Dirty || (force && source.RoomTransform == null)) { Rebuild(); reframe = true; ResetGrab(); }
            if (scan.Busy || !source.Localized || source.RoomTransform == null)
            {
                model.gameObject.SetActive(false); fog.Suspend(); ResetGrab();
                Status = scan.Busy ? scan.Status : source.Status + " Preview hidden until room localization is valid."; return;
            }
            geometry.sharedMesh = raw ? source.ScannedMesh : source.RoomsAndBoxes;
            content.localPosition = -source.LocalBounds.center;
            if (reframe)
            {
                Vector3 forward = Vector3.ProjectOnPlane(head.forward,Vector3.up).normalized;
                if (forward.sqrMagnitude < .1f) forward = Vector3.forward;
                model.SetPositionAndRotation(head.position + forward*.85f + Vector3.down*.45f,source.RoomTransform.rotation);
                float span = Mathf.Max(source.LocalBounds.size.x,source.LocalBounds.size.y,source.LocalBounds.size.z,.1f);
                model.localScale = Vector3.one * Mathf.Clamp(.65f/span,.02f,1); reframe = false;
            }
            model.gameObject.SetActive(true);
            // Missing raw data stays missing; never silently substitute semantic bounds.
            geometry.gameObject.SetActive(geometry.sharedMesh != null && geometry.sharedMesh.vertexCount > 0);
            Status = !geometry.gameObject.activeSelf ? (raw ? "No scanned mesh in this room. Choose Rooms & boxes or rescan." : "No labeled planes/bounds in this room.") :
                $"{(raw ? "Scanned mesh" : "Rooms & boxes")} | 1:{1/Scale:0.0}\n" +
                (mount != null && mount.IsOnHand ? "Pinch near model: one hand moves/rotates; two scale."
                                                 : "Grip near model: one controller moves/rotates; two scale.");
            if (hideCeiling && raw) Status += "\nMesh ceiling cutaway is approximate (top 15 cm).";
            RefreshCloud();
            bool tracked = rig != null && OVRPlugin.positionTracked && OVRPlugin.GetNodeOrientationTracked(OVRPlugin.Node.EyeCenter);
            marker.gameObject.SetActive(tracked);
            if (tracked)
            {
                marker.localPosition = source.RoomTransform.InverseTransformPoint(head.position);
                marker.localRotation = Quaternion.Inverse(source.RoomTransform.rotation) * Quaternion.LookRotation(head.forward,Vector3.up);
            }
            bool probeTracked = mount != null && mount.ProbeTracked();
            probeMarker.gameObject.SetActive(probeTracked);
            if (probeTracked)
            {
                Transform controller = mount.transform.parent != null ? mount.transform.parent : mount.transform;
                probeMarker.localPosition = source.RoomTransform.InverseTransformPoint(mount.Antenna.position);
                probeMarker.localRotation = Quaternion.Inverse(source.RoomTransform.rotation) * Quaternion.LookRotation(controller.forward,controller.up);
            }
        }
        private void RefreshCloud()
        {
            if (cloudMode == CloudMode.Fog)
            {
                fogCells.Clear();
                object dataset;
                if (viewer.LoadedFolder != null) { viewer.CopyCells(fogCells); dataset = viewer.DatasetIdentity; }
                else { live.CopyCells(fogCells); dataset = live.DatasetIdentity; }
                fog.Refresh(fogCells, dataset, source.RoomTransform, source.LocalBounds, settings, fogSteps, fogDensity);
                foreach (var filter in clouds) filter.gameObject.SetActive(false);
                Status += "\n" + fog.Status;
                return;
            }
            fog.Hide();
            visuals.Clear();
            if (cloudMode == CloudMode.Voxels) { if (viewer.LoadedFolder != null) viewer.CopyVisuals(visuals); else live.CopyVisuals(visuals); }
            int count=0;
            Matrix4x4 roomFromWorld = source.RoomTransform.worldToLocalMatrix;
            foreach (var visual in visuals)
            {
                if (visual.Anchor == null || visual.Mesh == null) continue;
                Bounds bounds = MrukPreviewSource.TransformBounds(roomFromWorld*visual.Anchor.localToWorldMatrix,visual.Mesh.bounds);
                if (!source.LocalBounds.Intersects(bounds)) continue;
                if (count == clouds.Count) clouds.Add(MakeVisual("Miniature Wi-Fi cloud",content,visual.Material));
                MeshFilter filter = clouds[count++]; filter.sharedMesh = visual.Mesh;
                filter.GetComponent<MeshRenderer>().sharedMaterial = visual.Material;
                filter.transform.SetLocalPositionAndRotation(source.RoomTransform.InverseTransformPoint(visual.Anchor.position),
                    Quaternion.Inverse(source.RoomTransform.rotation)*visual.Anchor.rotation);
                filter.transform.localScale = Vector3.one; filter.gameObject.SetActive(true);
            }
            for (int i=count;i<clouds.Count;i++) clouds[i].gameObject.SetActive(false);
        }
        private void Manipulate()
        {
            bool validLeft = PreviewGrabInput.Read(rig,true,out Pose left,out bool leftGrip);
            bool validRight = PreviewGrabInput.Read(rig,false,out Pose right,out bool rightGrip);
            if ((heldLeft && !validLeft) || (heldRight && !validRight)) { ResetGrab(); return; }
            leftGrip &= validLeft; rightGrip &= validRight;
            // Opening/recentering/refocusing requires both grips released before acquisition.
            if (!armed) { armed = !leftGrip && !rightGrip; previousLeft=leftGrip; previousRight=rightGrip; return; }
            if (!leftGrip) heldLeft=false;
            if (!rightGrip) heldRight=false;
            Bounds bounds = MrukPreviewSource.TransformBounds(model.localToWorldMatrix,new Bounds(Vector3.zero,source.LocalBounds.size));
            bounds.Expand(.24f);
            if (leftGrip && !previousLeft && bounds.Contains(left.position)) heldLeft=true;
            if (rightGrip && !previousRight && bounds.Contains(right.position)) heldRight=true;
            previousLeft=leftGrip; previousRight=rightGrip;
            int mode = heldLeft && heldRight ? 3 : heldLeft ? 1 : heldRight ? 2 : 0;
            if (mode != grabMode)
            {
                grabMode=mode; startPosition=model.position; startRotation=model.rotation; startScale=Scale;
                startHand=mode==1?left:right; startMidpoint=(left.position+right.position)*.5f;
                startDistance=Vector3.Distance(left.position,right.position);
                return;
            }
            if (mode==3 && startDistance > .05f)
            {
                float distance=Vector3.Distance(left.position,right.position);
                if (distance < .05f) return;
                float scale=Mathf.Clamp(startScale*distance/startDistance,.02f,1);
                model.localScale=Vector3.one*scale;
                model.position=(left.position+right.position)*.5f+(startPosition-startMidpoint)*(scale/startScale);
            }
            else if (mode==1 || mode==2)
            {
                Pose hand=mode==1?left:right;
                Quaternion rotation=hand.rotation*Quaternion.Inverse(startHand.rotation);
                model.SetPositionAndRotation(hand.position+rotation*(startPosition-startHand.position),rotation*startRotation);
            }
        }
        private void ResetGrab() { heldLeft=heldRight=previousLeft=previousRight=armed=false; grabMode=0; }
        private void OnApplicationFocus(bool focus) { if (!focus) ResetGrab(); }
        private void OnApplicationPause(bool pause) { if (pause) ResetGrab(); }
        private void OnDisable() { if (model != null) model.gameObject.SetActive(false); fog?.Hide(); ResetGrab(); }
        private void OnDestroy() { fog?.Dispose(); if (model != null) Destroy(model.gameObject); if (markerMesh != null) Destroy(markerMesh); if (probeMarkerMesh != null) Destroy(probeMarkerMesh); }
    }
}
