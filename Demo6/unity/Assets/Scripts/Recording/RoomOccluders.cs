using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace WifiHeatmap.Recording
{
    /// <summary>
    /// Invisible, depth-only copies of every local room's walls, floor, ceiling and furniture, so the voxel cloud is
    /// hidden behind them: cells measured in the next room don't float through the wall, and a couch hides what's
    /// behind it. Complements the Depth API (EnvironmentDepthManager), which handles hands, people and anything moved
    /// since the scan but is noisy at edges and distance; these give sharp, stable edges for the scanned room.
    ///
    /// Each mesh is a child of its MRUK anchor, built in anchor space, so it follows world-lock moves. Surfaces are
    /// set back by <see cref="slack"/> so cells measured right against a wall or tabletop aren't cut in half. Doors
    /// and windows are cut out of their walls (a closed door is left to the Depth API). Invisible walls (open-plan
    /// boundaries), lamps and plants don't occlude; tables occlude only their top. A room's occluders are off until
    /// AnchorRegistry reports it localised, so an unaligned room never hides cells in the wrong place.
    /// </summary>
    public sealed class RoomOccluders : MonoBehaviour
    {
        [SerializeField] private AnchorRegistry anchors;
        [SerializeField] private Material occluderMaterial;
        [SerializeField, Tooltip("Metres each surface is pushed away from the room (furniture shrunk) before it occludes.")]
        private float slack = 0.03f;
        [SerializeField, Tooltip("Thickness of a table's occluding top, in metres.")]
        private float tableTop = 0.04f;

        private const MRUKAnchor.SceneLabels Planes = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE |
                                                      MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING;
        private const MRUKAnchor.SceneLabels Walls = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE;
        private const MRUKAnchor.SceneLabels Openings = MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME;
        private const MRUKAnchor.SceneLabels NotSolid = MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.PLANT |
                                                        MRUKAnchor.SceneLabels.GLOBAL_MESH | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE |
                                                        Openings | MRUKAnchor.SceneLabels.WALL_ART;

        private readonly Dictionary<MRUKRoom, List<GameObject>> built = new Dictionary<MRUKRoom, List<GameObject>>();
        private readonly List<Mesh> meshes = new List<Mesh>();   // owned here: an occluder dies with its anchor, its mesh doesn't
        private MRUK sdk;
        private bool dirty = true;
        private float nextLocalisationCheck;

        /// <summary>Off hides every occluder (the cloud then relies on the Depth API alone).</summary>
        public bool ShowOccluders { get; set; } = true;
        public int OccluderCount { get; private set; }

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
                dirty = true;
            }
            if (dirty) Rebuild();
            if (Time.unscaledTime >= nextLocalisationCheck)
            {
                nextLocalisationCheck = Time.unscaledTime + 0.5f;
                foreach (var pair in built)
                {
                    bool show = ShowOccluders && pair.Key != null && anchors != null && anchors.TryResolve(pair.Key.Anchor.Uuid, out _);
                    foreach (GameObject go in pair.Value) if (go != null && go.activeSelf != show) go.SetActive(show);
                }
            }
        }

        private void RoomChanged(MRUKRoom _) { dirty = true; }

        private void Unsubscribe()
        {
            if (sdk == null) return;
            sdk.RoomCreatedEvent.RemoveListener(RoomChanged);
            sdk.RoomUpdatedEvent.RemoveListener(RoomChanged);
            sdk.RoomRemovedEvent.RemoveListener(RoomChanged);
        }

        private void Rebuild()
        {
            dirty = false; Clear();
            if (anchors == null || occluderMaterial == null) return;
            foreach (MRUKRoom room in anchors.LocalRooms())
            {
                var list = new List<GameObject>();
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    var label = anchor.Label;
                    if ((label & NotSolid) != 0) continue;
                    Mesh mesh = null;
                    if ((label & Planes) != 0 && anchor.PlaneRect is Rect rect)
                        mesh = PlaneMesh(rect, (label & Walls) != 0 ? OpeningsOn(room, anchor, rect) : null);
                    else if (anchor.VolumeBounds is Bounds bounds)
                        mesh = BoxMesh(anchor.transform, bounds, (label & MRUKAnchor.SceneLabels.TABLE) != 0);
                    if (mesh == null) continue;
                    meshes.Add(mesh);
                    var go = new GameObject("Wi-Fi cloud occluder") { hideFlags = HideFlags.DontSave };
                    go.transform.SetParent(anchor.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = occluderMaterial;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    go.SetActive(false);   // shown once the room is localised
                    list.Add(go);
                }
                built[room] = list;
                OccluderCount += list.Count;
            }
            nextLocalisationCheck = 0f;
        }

        /// <summary>Door and window rectangles lying in this wall, in its own plane coordinates.</summary>
        private static List<Rect> OpeningsOn(MRUKRoom room, MRUKAnchor wall, Rect wallRect)
        {
            var result = new List<Rect>();
            Matrix4x4 wallFromWorld = wall.transform.worldToLocalMatrix;
            foreach (MRUKAnchor opening in room.Anchors)
            {
                if ((opening.Label & Openings) == 0 || !(opening.PlaneRect is Rect r)) continue;
                Matrix4x4 wallFromOpening = wallFromWorld * opening.transform.localToWorldMatrix;
                float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
                bool onWall = true;
                foreach (var corner in new[] { new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax) })
                {
                    Vector3 p = wallFromOpening.MultiplyPoint3x4(corner);
                    onWall &= Mathf.Abs(p.z) < 0.15f;
                    xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x); yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
                }
                var hole = Rect.MinMaxRect(Mathf.Max(xMin, wallRect.xMin), Mathf.Max(yMin, wallRect.yMin),
                                           Mathf.Min(xMax, wallRect.xMax), Mathf.Min(yMax, wallRect.yMax));
                if (onWall && hole.width > 0.01f && hole.height > 0.01f) result.Add(hole);
            }
            return result;
        }

        /// <summary>The plane's rectangle, set back along -Z (planes face into the room), minus any openings.</summary>
        private Mesh PlaneMesh(Rect rect, List<Rect> holes)
        {
            var xs = new List<float> { rect.xMin, rect.xMax }; var ys = new List<float> { rect.yMin, rect.yMax };
            if (holes != null) foreach (Rect h in holes) { xs.Add(h.xMin); xs.Add(h.xMax); ys.Add(h.yMin); ys.Add(h.yMax); }
            xs.Sort(); ys.Sort();
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int i = 0; i + 1 < xs.Count; i++)
            for (int j = 0; j + 1 < ys.Count; j++)
            {
                float x0 = xs[i], x1 = xs[i + 1], y0 = ys[j], y1 = ys[j + 1];
                if (x1 - x0 < 1e-4f || y1 - y0 < 1e-4f) continue;
                var centre = new Vector2((x0 + x1) / 2, (y0 + y1) / 2);
                bool open = false;
                if (holes != null) foreach (Rect h in holes) open |= h.Contains(centre);
                if (open) continue;
                Quad(new Vector3(x0, y0, -slack), new Vector3(x1, y0, -slack), new Vector3(x1, y1, -slack), new Vector3(x0, y1, -slack), vertices, triangles);
            }
            return vertices.Count == 0 ? null : MakeMesh(vertices, triangles);
        }

        /// <summary>The volume's box shrunk by the slack; for a table, only a slab at its top.</summary>
        private Mesh BoxMesh(Transform anchor, Bounds bounds, bool table)
        {
            Vector3 min = bounds.min + Vector3.one * slack, max = bounds.max - Vector3.one * slack;
            if (table)
            {
                // The local axis nearest world up (MRUK volumes use +Z) holds the top.
                Vector3 up = anchor.InverseTransformDirection(Vector3.up);
                int axis = Mathf.Abs(up.x) > Mathf.Abs(up.y) ? (Mathf.Abs(up.x) > Mathf.Abs(up.z) ? 0 : 2) : (Mathf.Abs(up.y) > Mathf.Abs(up.z) ? 1 : 2);
                if (up[axis] >= 0) min[axis] = Mathf.Max(min[axis], max[axis] - tableTop);
                else max[axis] = Mathf.Min(max[axis], min[axis] + tableTop);
            }
            if (min.x >= max.x || min.y >= max.y || min.z >= max.z) return null;
            Vector3 a = min, z = max;
            Vector3[] p = { new Vector3(a.x,a.y,a.z), new Vector3(z.x,a.y,a.z), new Vector3(z.x,z.y,a.z), new Vector3(a.x,z.y,a.z),
                new Vector3(a.x,a.y,z.z), new Vector3(z.x,a.y,z.z), new Vector3(z.x,z.y,z.z), new Vector3(a.x,z.y,z.z) };
            int[] faces = {0,1,2,3, 5,4,7,6, 4,0,3,7, 1,5,6,2, 3,2,6,7, 4,5,1,0};
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int i = 0; i < faces.Length; i += 4) Quad(p[faces[i]], p[faces[i+1]], p[faces[i+2]], p[faces[i+3]], vertices, triangles);
            return MakeMesh(vertices, triangles);
        }

        private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, List<Vector3> vertices, List<int> triangles)
        {
            int start = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }

        private static Mesh MakeMesh(List<Vector3> vertices, List<int> triangles)
        {
            var mesh = new Mesh { name = "Wi-Fi cloud occluder" };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
            return mesh;
        }

        private void Clear()
        {
            foreach (var list in built.Values)
            foreach (GameObject go in list) if (go != null) Destroy(go);
            foreach (Mesh mesh in meshes) if (mesh != null) Destroy(mesh);
            built.Clear(); meshes.Clear(); OccluderCount = 0;
        }

        private void OnDestroy() { Unsubscribe(); Clear(); }
    }
}
