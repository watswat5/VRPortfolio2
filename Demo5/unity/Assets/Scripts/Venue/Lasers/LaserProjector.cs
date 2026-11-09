// A laser projector playing ILDA shows (IldaClip, from .ild files): each point of the current frame is an aim of the
// scanner, ±scanAngle degrees across its field (+X right, +Y up, out along this transform's forward). A ray goes out each
// way to the first surface it meets; consecutive lit points become a thin sheet of light from the aperture through the
// haze, and a line of light where they land. Points beyond maxRays are thinned (a stretch with any blanked point in it
// stays dark, so the beam never draws across a blanked move).
// Frames advance with the beat while the battle's beat plays (the show loops every loopBeats beats), else at frameRate.
// One mesh, rebuilt each frame, drawn with Freestyle/LaserBeam (additive): about maxRays raycasts a frame. The beams in
// the air show as smoke shows a real laser (the Smoke settings, SmokeVolume): brightest in the swirling smoke, and
// toward you when they're aimed your way.
using System.Collections.Generic;
using Freestyle.Battle;
using UnityEngine;

namespace Freestyle.Venue.Lasers
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LaserProjector : MonoBehaviour
    {
        [Tooltip("The shows it can play; `current` is the one playing.")]
        public IldaClip[] shows = new IldaClip[0];
        public int current;
        public bool on = true;

        [Header("Optics")]
        [Tooltip("Degrees from the middle to the edge of the scan field (ILDA's ±32768).")]
        public float scanAngle = 30f;
        [Tooltip("How far a beam goes when it meets nothing (m).")]
        public float range = 40f;
        [Tooltip("What the beams stop at.")]
        public LayerMask surfaces = ~((1 << 2) | (1 << 5));   // not Ignore Raycast, not UI
        [Tooltip("Rays a frame at most: frames with more points are thinned to this.")]
        public int maxRays = 300;

        [Header("Look")]
        [Tooltip("How much haze is in the air: how strongly the beams show between the projector and where they land " +
                 "(0: clean air, only the lines on the surfaces; 1: as set below; 2: thick fog). The lines aren't affected.")]
        [Range(0, 3)] public float hazeDensity = 1f;
        /// <summary>The venue's haze, for every projector at once (a fog machine, a settings slider): multiplies each
        /// projector's own hazeDensity.</summary>
        public static float GlobalHaze = 1f;
        [Tooltip("How bright the sheets of light through the haze are (they add up where beams overlap).")]
        [Range(0, 1)] public float beamBrightness = 0.04f;
        [Tooltip("How bright the beams are at the aperture, where they all start.")]
        [Range(0, 1)] public float apertureBrightness = 0.25f;
        [Tooltip("How bright the lines where the beams land are.")]
        [Range(0, 2)] public float lineBrightness = 1.2f;
        [Tooltip("Width of the lines where the beams land (m).")]
        public float lineWidth = 0.012f;
        [Tooltip("A single beam (a dot held in place, as beam shows use): its width at the far end (m) and brightness.")]
        public float beamWidth = 0.03f;
        [Range(0, 2)] public float singleBeamBrightness = 0.7f;

        [Header("Smoke (how the beams show in the air)")]
        [Tooltip("The smoke repeats every 1/smokeScale metres (its tile): bigger numbers, smaller swirls.")]
        public float smokeScale = 0.3f;
        [Tooltip("Seconds for the smoke's swirling to come round again (its loop).")]
        public float smokeLoopSeconds = 12f;
        [Tooltip("Which way and how fast the smoke drifts (m/s, world space).")]
        public Vector3 wind = new(0.12f, 0.04f, 0.08f);
        [Tooltip("The thin even haze under the smoke: 0 the beams show only in the smoke, 1 evenly everywhere.")]
        [Range(0, 1)] public float evenHaze = 0.04f;
        [Tooltip("How patchy the smoke is: higher, thicker wisps with clearer air between.")]
        [Range(0.5f, 4)] public float smokeContrast = 1.6f;
        [Tooltip("How brightly the smoke lights up.")]
        [Range(0, 6)] public float smokeBrightness = 3.5f;
        [Tooltip("How much more the smoke scatters a beam's light ahead of it (toward you, when it's aimed at you).")]
        [Range(0, 0.9f)] public float forwardScatter = 0.5f;
        [Tooltip("Beams fade out within this distance of your eyes (m).")]
        public float nearFade = 0.6f;

        [Header("Timing")]
        public BattleCoordinator battle;
        [Tooltip("While the beat plays: beats for one pass through the show.")]
        public float loopBeats = 4f;
        [Tooltip("Frames a second when there's no beat.")]
        public float frameRate = 30f;
        [Tooltip("Hold this frame of the show instead of playing it (-1: play). For aiming, and stills.")]
        public int holdFrame = -1;

        public IldaClip Show => shows != null && shows.Length > 0 ? shows[Mathf.Clamp(current, 0, shows.Length - 1)] : null;
        public int Frame { get; private set; }

        Mesh mesh;
        readonly List<Vector3> verts = new();
        readonly List<Color> colours = new();   // light added, each channel (may go over 1)
        readonly List<int> tris = new();
        readonly List<(Vector3 at, Vector3 normal, Color32 c, bool lit, bool hit)> beams = new();
        float clock;
        MaterialPropertyBlock smoke;
        MeshRenderer view;

        void Awake()
        {
            mesh = new Mesh { name = "Laser Beams" };
            mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = view = GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (!r.sharedMaterial) r.sharedMaterial = new Material(Shader.Find("Freestyle/LaserBeam"));
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
        }

        /// <summary>The next show in the list.</summary>
        public void Next() { if (shows.Length > 0) { current = (current + 1) % shows.Length; clock = 0; } }

        void LateUpdate()
        {
            var show = Show;
            mesh.Clear();
            if (!on || !show || show.Frames.Count == 0) return;
            int n = show.Frames.Count;
            if (holdFrame >= 0) Frame = holdFrame % n;
            else if (battle && battle.BeatPlaying && loopBeats > 0)
                Frame = Mathf.FloorToInt((battle.BeatIndex + battle.BeatPhase) / loopBeats * n) % n;
            else
            {
                clock += Time.deltaTime;
                Frame = Mathf.FloorToInt(clock * frameRate) % n;
            }
            if (Frame < 0) Frame += n;
            Cast(show.Frames[Frame].points);
            Build();
            Smoke();
        }

        static readonly RaycastHit[] hits = new RaycastHit[8];

        /// <summary>A ray each way the (thinned) frame aims: where it lands, and whether the beam to it is on.</summary>
        void Cast(IldaPoint[] points)
        {
            beams.Clear();
            int step = Mathf.Max(1, Mathf.CeilToInt(points.Length / (float)Mathf.Max(1, maxRays)));
            Vector3 origin = transform.position;
            for (int i = 0; i < points.Length; i += step)
            {
                // lit only if every point since the last one kept is lit (so a blanked move stays dark)
                bool lit = true;
                for (int k = Mathf.Max(0, i - step + 1); k <= i; k++) lit &= points[k].Lit;
                var pt = points[i];
                Vector3 dir = transform.rotation * (Quaternion.Euler(-pt.position.y * scanAngle, pt.position.x * scanAngle, 0) * Vector3.forward);
                Vector3 at = origin + dir * range, normal = -dir;
                bool hit = false;
                int count = Physics.RaycastNonAlloc(origin, dir, hits, range, surfaces, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int h = 0; h < count; h++)
                {
                    if (hits[h].collider is CharacterController) continue;   // not the player's own capsule
                    if (hits[h].distance < best) { best = hits[h].distance; at = hits[h].point; normal = hits[h].normal; hit = true; }
                }
                beams.Add((at, normal, pt.color, lit, hit));
            }
        }

        void Build()
        {
            verts.Clear(); colours.Clear(); tris.Clear();
            Vector3 origin = Vector3.zero;   // the mesh is in this transform's space
            float haze = Mathf.Max(0, hazeDensity * GlobalHaze);   // the beams in the air; the lines on surfaces stay as they are
            for (int i = 0; i < beams.Count; i++)
            {
                var b = beams[i];
                if (!b.lit) continue;
                Vector3 end = transform.InverseTransformPoint(b.at);
                Color c = b.c;
                // the stretch from the point before (ILDA draws the move to a lit point); where it's no length at all (the
                // first point, or a dot held in place: beam shows), a single beam instead
                Vector3 start = i > 0 ? transform.InverseTransformPoint(beams[i - 1].at) : end;
                if ((end - start).sqrMagnitude > beamWidth * beamWidth)
                {
                    var a = beams[i - 1];
                    // the sheet of light from the aperture to this stretch
                    if (haze > 0) Tri(origin, start, end, Scale(c, apertureBrightness * haze), Scale(a.c, beamBrightness * haze), Scale(c, beamBrightness * haze));
                    // and the line where it lands (only on a surface, a little off it)
                    if (a.hit && b.hit)
                    {
                        Vector3 n = transform.InverseTransformDirection(b.normal);
                        Ribbon(start + n * 0.002f, end + n * 0.002f, n, lineWidth, Scale(a.c, lineBrightness, 0), Scale(c, lineBrightness, 0));
                    }
                }
                else
                {
                    // a beam to a dot
                    Vector3 side = Vector3.Cross(end.normalized, Vector3.up).normalized * beamWidth * 0.5f;
                    if (side.sqrMagnitude < 1e-10f) side = Vector3.right * beamWidth * 0.5f;
                    if (haze > 0) Tri(origin, end - side, end + side, Scale(c, singleBeamBrightness * haze), Scale(c, singleBeamBrightness * 0.6f * haze), Scale(c, singleBeamBrightness * 0.6f * haze));
                }
            }
            mesh.SetVertices(verts);
            mesh.SetColors(colours);
            mesh.SetTriangles(tris, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * range * 2);
        }

        void Smoke()
        {
            smoke ??= new MaterialPropertyBlock();
            view.GetPropertyBlock(smoke);
            smoke.SetTexture("_Smoke", SmokeVolume.Texture);
            smoke.SetFloat("_SmokeSize", SmokeVolume.Size);
            smoke.SetFloat("_SmokeFrames", SmokeVolume.Frames);
            smoke.SetFloat("_SmokeLoop", smokeLoopSeconds);
            smoke.SetFloat("_SmokeScale", smokeScale);
            smoke.SetVector("_Wind", wind);
            smoke.SetFloat("_Haze", evenHaze);
            smoke.SetFloat("_Clumps", smokeContrast);
            smoke.SetFloat("_Thickness", smokeBrightness);
            smoke.SetFloat("_Forward", forwardScatter);
            smoke.SetFloat("_NearFade", nearFade);
            view.SetPropertyBlock(smoke);
        }

        void Tri(Vector3 a, Vector3 b, Vector3 c, Color ca, Color cb, Color cc)
        {
            int k = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            colours.Add(ca); colours.Add(cb); colours.Add(cc);
            tris.Add(k); tris.Add(k + 1); tris.Add(k + 2);
        }

        void Ribbon(Vector3 a, Vector3 b, Vector3 normal, float width, Color ca, Color cb)
        {
            Vector3 along = b - a;
            Vector3 side = Vector3.Cross(normal, along.sqrMagnitude > 1e-10f ? along.normalized : Vector3.up).normalized * width * 0.5f;
            int k = verts.Count;
            verts.Add(a - side); verts.Add(a + side); verts.Add(b + side); verts.Add(b - side);
            colours.Add(ca); colours.Add(ca); colours.Add(cb); colours.Add(cb);
            tris.Add(k); tris.Add(k + 1); tris.Add(k + 2);
            tris.Add(k); tris.Add(k + 2); tris.Add(k + 3);
        }

        /// <summary>A colour's light times k; alpha 1 marks a beam in the air (lit by the smoke), 0 a line on a surface.</summary>
        static Color Scale(Color c, float k, float air = 1) => new(c.r * k, c.g * k, c.b * k, air);
    }
}
