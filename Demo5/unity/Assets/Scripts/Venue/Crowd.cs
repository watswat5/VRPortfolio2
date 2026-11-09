// The crowd: flat cutouts (art/crowd: four types, 12 moods x 2 frames each) standing on the floor in front of the
// stage, in the box centred on this transform (`size`: width and depth). Made at Start from `seed`, so the room looks
// the same every time: mixed types, some mirrored, sizes varied, no two closer than `spacing`, none inside anything
// solid. Each turns to face you.
// Their mood follows the battle's crowd level (0-11, the sheets' columns), each a step either side now and then, and
// each changing a moment after the level does, so it ripples through the room. Hysteresis keeps anyone from flicking
// between two moods: the level has to get clearly past the halfway point (moodDeadband) and stay there a moment
// (moodSettle) before they change; a big jump only waits for the ripple. They flip between the two frames on
// the beat (half of them on the off-beat), all bob to it (a smooth dip on each beat, deeper the livelier the room), and
// from "into it" up they hop. Before and after a battle they're
// warming up to an idle tempo.
using System.Collections.Generic;
using Freestyle.Battle;
using UnityEngine;

namespace Freestyle.Venue
{
    public class Crowd : MonoBehaviour
    {
        public BattleCoordinator battle;
        [Tooltip("One material per crowd type, each on its sheet (12 columns of moods x 2 rows of frames).")]
        public Material[] types = new Material[0];
        [Tooltip("The floor area, centred on this transform: x across, z deep (y is ignored).")]
        public Vector3 size = new(7f, 0f, 6.5f);
        public int count = 45;
        public float spacing = 0.75f;
        public int seed = 7;
        [Tooltip("How tall a figure stands, metres (random in this range).")]
        public Vector2 height = new(1.6f, 1.85f);
        [Tooltip("Where a standing figure's head tops out in its cell, pixels from the top (the sheets' feet are at 336): " +
                 "how the figures are sized to `height`.")]
        public float headTopPx = 110f;

        [Header("Mood")]
        [Tooltip("The battle's crowd level is shown this many times livelier (capped at 11): the rules rarely go past 6, " +
                 "which at 1.5 looks like a 9.")]
        public float liveliness = 1.5f;
        [Tooltip("The crowd level when there's no battle on.")]
        [Range(0, 11)] public float idleLevel = 4f;
        public float idleBpm = 92f;
        [Tooltip("Seconds (random up to this) before someone catches up with the room's mood.")]
        public float rippleDelay = 0.4f;
        [Tooltip("Score hysteresis: how far past the halfway point between two moods the level has to go before someone " +
                 "changes (0: the moment it rounds the other way). Stops a level hovering on a boundary flipping people.")]
        [Range(0, 1)] public float moodDeadband = 0.35f;
        [Tooltip("Time hysteresis: seconds a new mood has to hold before someone takes it up (on top of their ripple " +
                 "delay); if the level comes back first, nothing changes. A jump of two moods or more doesn't wait.")]
        public float moodSettle = 0.5f;
        [Tooltip("Everyone bobs to the beat, smoothly: down on the beat, up between. This is how far at the top mood, metres " +
                 "(a dead room barely moves, each person a little more or less, a little behind or ahead).")]
        public float bob = 0.03f;
        [Tooltip("When a battle ends the room cheers: this many moods up (capped at 11), for cheerTime seconds, the last " +
                 "cheerFade of them easing back down. Whoever won.")]
        public float cheerBoost = 3f;
        public float cheerTime = 8f, cheerFade = 3f;
        [Tooltip("Hop height on the beat at the top mood, metres (from 'into it' up).")]
        public float hop = 0.12f;

        // the sheets (art/crowd/sheet.json)
        const int Columns = 12, Rows = 2;
        const float CellW = 256f, CellH = 384f, PivotX = 128f, PivotY = 336f;

        class Person
        {
            public Transform t;
            public Mesh mesh;
            public int bias, mood = -1, frame = -1, target;
            public float offset, delay, changeAt, baseY;
            public float bobSize, bobLag;   // their own share of the bob, and how far off the beat they are (beats)
            public bool mirror;
        }

        readonly List<Person> people = new();
        float cheerStart = -100f, cheerLen = 8f;
        BattlePhase lastPhase;

        /// <summary>How much of the end-of-battle cheer is on now, 0-1.</summary>
        float Cheer()
        {
            float t = Time.time - cheerStart, fadeLen = Mathf.Min(cheerFade, cheerLen * 0.5f);
            return t < 0f || t > cheerLen ? 0f : Mathf.Clamp01((cheerLen - t) / Mathf.Max(0.01f, fadeLen));
        }

        /// <summary>The room cheers now for this many seconds (the DJ's introductions): as at the end of a battle,
        /// shorter.</summary>
        public void CheerFor(float seconds)
        {
            cheerStart = Time.time;
            cheerLen = seconds;
        }

        void Start()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            if (types.Length == 0) return;
            var rnd = new System.Random(seed);
            float R() => (float)rnd.NextDouble();
            var spots = new List<Vector3>();
            for (int tries = 0; spots.Count < count && tries < count * 40; tries++)
            {
                var p = transform.TransformPoint(new Vector3((R() - 0.5f) * size.x, 0f, (R() - 0.5f) * size.z));
                if (spots.Exists(s => (s - p).sqrMagnitude < spacing * spacing)) continue;
                if (Physics.CheckCapsule(p + Vector3.up * 0.3f, p + Vector3.up * 1.5f, 0.2f, ~0, QueryTriggerInteraction.Ignore)) continue;
                spots.Add(p);
            }
            for (int i = 0; i < spots.Count; i++)
            {
                var go = new GameObject("Person " + i);
                go.transform.SetParent(transform, true);
                go.transform.position = spots[i];
                var person = new Person
                {
                    t = go.transform, mirror = R() < 0.5f, offset = R() < 0.5f ? 0f : 0.5f, delay = R() * rippleDelay,
                    bias = R() < 0.2f ? -1 : R() < 0.25f ? 1 : 0, baseY = spots[i].y,
                    bobSize = Mathf.Lerp(0.7f, 1.3f, R()), bobLag = Mathf.Lerp(-0.06f, 0.1f, R()),
                };
                float tall = Mathf.Lerp(height.x, height.y, R());
                person.mesh = Quad(tall / (PivotY - headTopPx));
                go.AddComponent<MeshFilter>().sharedMesh = person.mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = types[rnd.Next(types.Length)];
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                people.Add(person);
            }
        }

        /// <summary>A quad for one cell, `m` metres a pixel, its pivot (the foot of the stand) at the origin.</summary>
        static Mesh Quad(float m)
        {
            float x0 = -PivotX * m, x1 = (CellW - PivotX) * m, y0 = -(CellH - PivotY) * m, y1 = PivotY * m;
            var mesh = new Mesh { name = "CrowdPerson" };
            mesh.SetVertices(new[] { new Vector3(x0, y0), new Vector3(x0, y1), new Vector3(x1, y1), new Vector3(x1, y0) });
            mesh.SetUVs(0, new Vector2[4]);
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.RecalculateBounds();
            mesh.bounds = new Bounds(mesh.bounds.center, mesh.bounds.size + Vector3.up * 0.3f);   // room for the hop
            return mesh;
        }

        void SetCell(Person p, int mood, int frame)
        {
            p.mood = mood; p.frame = frame;
            float inset = 0.5f / (CellW * Columns);   // half a texel, so the next cell doesn't bleed in
            float u0 = (float)mood / Columns + inset, u1 = (float)(mood + 1) / Columns - inset;
            if (p.mirror) (u0, u1) = (u1, u0);
            float v1 = 1f - (float)frame / Rows, v0 = v1 - 1f / Rows;   // row 0 (frame A) at the top of the sheet
            p.mesh.SetUVs(0, new[] { new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0) });
        }

        // the beat clock: during a battle, and after it while the beat plays on
        bool Battling => battle && battle.BeatPlaying && battle.BeatIndex >= 0;

        void Update()
        {
            if (people.Count == 0) return;
            bool on = Battling || battle && battle.Phase == BattlePhase.Finished;   // (not while it starts: no level yet)
            if (battle && battle.Phase != lastPhase)
            {
                if (battle.Phase == BattlePhase.Finished) CheerFor(cheerTime);   // the battle's over: the room goes up
                lastPhase = battle.Phase;
            }
            float level = Mathf.Min(11f, (on ? battle.CrowdLevel * liveliness : idleLevel) + cheerBoost * Cheer());
            double beats = Battling ? battle.BeatIndex + battle.BeatPhase : Time.time * idleBpm / 60.0;
            Vector3 eye = Camera.main ? Camera.main.transform.position : transform.position + Vector3.back * 10f;
            foreach (var p in people)
            {
                // the mood this person would be in: theirs until the level is clearly past the halfway point (score
                // hysteresis), then the nearest one
                float want = level + p.bias;
                int target = p.mood < 0 || Mathf.Abs(want - p.mood) > 0.5f + moodDeadband ? Mathf.RoundToInt(want) : p.mood;
                target = Mathf.Clamp(target, 0, Columns - 1);
                if (target != p.target)
                {
                    // a new mood has to hold a moment before they take it up (time hysteresis), plus their ripple
                    // delay; a big jump (a punchline's pop) only waits for the ripple
                    bool big = p.mood >= 0 && Mathf.Abs(target - p.mood) >= 2;
                    p.target = target;
                    p.changeAt = Time.time + p.delay + (big ? 0f : moodSettle);
                }
                int mood = p.mood < 0 || Time.time >= p.changeAt ? p.target : p.mood;
                double b = beats + p.offset;
                int frame = (int)(System.Math.Floor(b) % 2 + 2) % 2;
                if (mood != p.mood || frame != p.frame) SetCell(p, mood, frame);

                // a hop on the beat, from "into it" (6) up, bigger the wilder
                float lift = mood >= 6 ? hop * (mood - 5) / 6f * Mathf.Max(0f, Mathf.Sin((float)(b - System.Math.Floor(b)) * Mathf.PI)) : 0f;
                // and everyone's bob: a smooth dip on each beat, up between, deeper the more the room is into it
                double bb = beats + p.bobLag;
                float phase = (float)(bb - System.Math.Floor(bb));
                float dip = bob * p.bobSize * (0.15f + 0.85f * mood / (Columns - 1f)) * (0.5f + 0.5f * Mathf.Cos(phase * 2f * Mathf.PI));
                var pos = p.t.position;
                pos.y = p.baseY + lift - dip;
                p.t.position = pos;
                // facing you (turning round the up axis only)
                var to = eye - pos; to.y = 0;
                if (to.sqrMagnitude > 1e-4f) p.t.rotation = Quaternion.LookRotation(-to);
            }
        }

        void OnDestroy()
        {
            foreach (var p in people) if (p.mesh) Destroy(p.mesh);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.8f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(new Vector3(0, 0.9f, 0), new Vector3(size.x, 1.8f, size.z));
        }
    }
}
