using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Rendering;
using WifiHeatmap.Recording;

namespace WifiHeatmap.Preview
{
    /// <summary>Read-only SDK boundary: owns visual copies, never moves a tracked room/anchor.</summary>
    public sealed class MrukPreviewSource : MonoBehaviour
    {
        [SerializeField] private AnchorRegistry anchors;
        private MRUK sdk;
        private MRUKRoom room;
        public Transform RoomTransform => room == null ? null : room.transform;
        public Guid RoomId => room == null ? Guid.Empty : room.Anchor.Uuid;
        public Mesh RoomsAndBoxes { get; private set; }
        public Mesh ScannedMesh { get; private set; }
        public Bounds LocalBounds { get; private set; }
        public bool Dirty { get; private set; } = true;
        public bool Localized => room != null && anchors.TryResolve(RoomId, out _);
        public string Status { get; private set; } = "No room loaded. Use Scan room on the headset.";
        private void Update()
        {
            if (sdk != MRUK.Instance)
            {
                Unsubscribe(); sdk = MRUK.Instance;
                if (sdk != null)
                {
                    sdk.RoomCreatedEvent.AddListener(RoomChanged);
                    sdk.RoomUpdatedEvent.AddListener(RoomChanged);
                    sdk.RoomRemovedEvent.AddListener(RoomChanged);
                }
                Dirty = true;
            }
            if (sdk != null && sdk.GetCurrentRoom() != room) Dirty = true;
        }
        private void RoomChanged(MRUKRoom _) { Dirty = true; }
        private void Unsubscribe()
        {
            if (sdk == null) return;
            sdk.RoomCreatedEvent.RemoveListener(RoomChanged);
            sdk.RoomUpdatedEvent.RemoveListener(RoomChanged);
            sdk.RoomRemovedEvent.RemoveListener(RoomChanged);
        }
        public bool Rebuild(bool hideCeiling)
        {
            Dirty = false; ReleaseMeshes();
            room = sdk == null ? null : sdk.GetCurrentRoom();
            if (room == null || !room.IsLocal)
            { Status = "No local room. Use Scan room on the headset."; return false; }
            var vertices = new List<Vector3>(); var colors = new List<Color>(); var triangles = new List<int>();
            Matrix4x4 roomFromWorld = room.transform.worldToLocalMatrix;
            foreach (MRUKAnchor anchor in room.Anchors)
            {
                var label = anchor.Label;
                if ((label & MRUKAnchor.SceneLabels.GLOBAL_MESH) != 0 ||
                    (hideCeiling && (label & MRUKAnchor.SceneLabels.CEILING) != 0)) continue;
                Matrix4x4 roomFromAnchor = roomFromWorld * anchor.transform.localToWorldMatrix;
                Color color = (label & MRUKAnchor.SceneLabels.FLOOR) != 0 ? new Color(.22f,.48f,.55f,.6f) :
                    (label & MRUKAnchor.SceneLabels.WALL_FACE) != 0 ? new Color(.55f,.7f,.8f,.28f) :
                    new Color(.3f,.72f,.62f,.5f);
                if (anchor.VolumeBounds is Bounds b)
                {
                    Vector3 a = b.min, z = b.max;
                    Vector3[] p = { new Vector3(a.x,a.y,a.z), new Vector3(z.x,a.y,a.z), new Vector3(z.x,z.y,a.z), new Vector3(a.x,z.y,a.z),
                        new Vector3(a.x,a.y,z.z), new Vector3(z.x,a.y,z.z), new Vector3(z.x,z.y,z.z), new Vector3(a.x,z.y,z.z) };
                    int[] faces = {0,1,2,3, 5,4,7,6, 4,0,3,7, 1,5,6,2, 3,2,6,7, 4,5,1,0};
                    for (int i = 0; i < faces.Length; i += 4)
                        Quad(p[faces[i]],p[faces[i+1]],p[faces[i+2]],p[faces[i+3]],roomFromAnchor,color,vertices,colors,triangles);
                }
                else if (anchor.PlaneRect is Rect r)
                    Quad(new Vector3(r.xMin,r.yMin,0),new Vector3(r.xMax,r.yMin,0),new Vector3(r.xMax,r.yMax,0),
                        new Vector3(r.xMin,r.yMax,0),roomFromAnchor,color,vertices,colors,triangles);
            }
            RoomsAndBoxes = MakeMesh("Room planes and furniture bounds",vertices,colors,triangles);
            // Copy global geometry so ceiling cutaway cannot alter the SDK-owned mesh.
            Mesh source = room.GlobalMeshAnchor == null ? null : room.GlobalMeshAnchor.GlobalMesh;
            if (source != null && source.isReadable)
            {
                Vector3[] raw = source.vertices;
                Matrix4x4 worldFromMesh = room.GlobalMeshAnchor.transform.localToWorldMatrix;
                float ceiling = room.GetRoomBounds().max.y - .15f;
                var keep = new bool[raw.Length]; vertices.Clear(); colors.Clear(); triangles.Clear();
                for (int i = 0; i < raw.Length; i++)
                {
                    Vector3 world = worldFromMesh.MultiplyPoint3x4(raw[i]);
                    keep[i] = !hideCeiling || world.y < ceiling;
                    vertices.Add(roomFromWorld.MultiplyPoint3x4(world)); colors.Add(new Color(.55f,.72f,.82f,.4f));
                }
                int[] indices = source.triangles;
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a=indices[i], b=indices[i+1], c=indices[i+2];
                    if (!keep[a] && !keep[b] && !keep[c]) continue;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                }
                if (triangles.Count > 0) ScannedMesh = MakeMesh("Room scanned mesh copy",vertices,colors,triangles);
            }
            // Bounds include the full room height even with its ceiling hidden.
            Bounds worldBounds = room.GetRoomBounds();
            LocalBounds = TransformBounds(roomFromWorld, worldBounds);
            if (LocalBounds.size.sqrMagnitude < .01f && RoomsAndBoxes.vertexCount > 0) LocalBounds = RoomsAndBoxes.bounds;
            Status = Localized ? "Room loaded." : "Room unresolved. Waiting for localization.";
            return RoomsAndBoxes.vertexCount > 0 || ScannedMesh != null;
        }
        private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Matrix4x4 matrix, Color color,
            List<Vector3> vertices, List<Color> colors, List<int> triangles)
        {
            int start = vertices.Count;
            foreach (Vector3 p in new[] { a,b,c,d }) { vertices.Add(matrix.MultiplyPoint3x4(p)); colors.Add(color); }
            triangles.AddRange(new[] { start,start+1,start+2,start,start+2,start+3 });
        }
        private static Mesh MakeMesh(string label, List<Vector3> vertices, List<Color> colors, List<int> triangles)
        {
            var mesh = new Mesh { name = label, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetTriangles(triangles,0); mesh.RecalculateBounds(); return mesh;
        }
        public static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
        {
            var result = new Bounds(matrix.MultiplyPoint3x4(bounds.center),Vector3.zero);
            for (int i=0;i<8;i++) result.Encapsulate(matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1))));
            return result;
        }
        private void ReleaseMeshes()
        { if (RoomsAndBoxes != null) Destroy(RoomsAndBoxes); if (ScannedMesh != null) Destroy(ScannedMesh); RoomsAndBoxes = ScannedMesh = null; }
        private void OnDestroy() { Unsubscribe(); ReleaseMeshes(); }
    }
}
