// A face that shows how the character feels, on the Rocketbox face's expression shapes (ARKit AK_*, with FACS AU_*
// lip-corner pullers to strengthen the smiles, which are mild on these faces alone): the MC's mood
// (Performer.mood, from BattleRules: cocky, heated, rattled, amused, respectful, calm) held as an expression, eased in
// and out; a gesture (laughs, winces, glares, ...) flashed over it for a moment; and what they're doing (hype: a big
// grin; the DJ hosting: a showman's smile). A character's own resting face (the interviewer's polite half-smile)
// sits under it all.
// It keeps to brows, eyes, cheeks, nose and the mouth's corners, so it layers over the lip sync (the visemes, and the
// jaw they open) and the blinks (EyeBlink*, LookAndBlink) without fighting them. Runs after the Animator.
// Performer adds one at Awake if the character has the shapes, so the character prefabs need no rebuild.
using System.Collections.Generic;
using UnityEngine;

namespace Freestyle.Avatar
{
    [DefaultExecutionOrder(100)]
    public class FaceExpression : MonoBehaviour
    {
        public Performer performer;
        [Tooltip("The face with the ARKit blendshapes (blendShape1.AK_*). Empty: found.")]
        public SkinnedMeshRenderer face;
        [Tooltip("How strong every expression is (1 = as designed).")]
        [Range(0, 2)] public float strength = 1f;
        [Tooltip("Seconds to ease most of the way into a new expression.")]
        public float ease = 0.35f;
        [Tooltip("Seconds a gesture's flash holds before easing back to the mood.")]
        public float flashHold = 1.1f;
        [Tooltip("Under everything: this character's resting face (one of the expressions below), e.g. \"polite\".")]
        public string resting = "";

        // name -> (ARKit shape, weight 0-100). Weights tuned to read at stage distance without going cartoon.
        static readonly Dictionary<string, (string shape, float w)[]> Expressions = new()
        {
            ["calm"] = new (string, float)[0],
            ["cocky"] = new[] { ("MouthSmileLeft", 60f), ("L_LipCornerPuller", 55f), ("BrowOuterUpLeft", 35f), ("EyeSquintLeft", 25f), ("EyeSquintRight", 15f), ("CheekSquintLeft", 20f) },
            ["heated"] = new[] { ("BrowDownLeft", 75f), ("BrowDownRight", 75f), ("UpperLipRaiser", 30f), ("NoseSneerLeft", 45f), ("NoseSneerRight", 40f), ("EyeSquintLeft", 35f), ("EyeSquintRight", 35f), ("MouthPressLeft", 25f), ("MouthPressRight", 25f) },
            ["rattled"] = new[] { ("BrowInnerUp", 75f), ("EyeWideLeft", 35f), ("EyeWideRight", 35f), ("MouthFrownLeft", 35f), ("MouthFrownRight", 35f), ("MouthStretchLeft", 15f), ("MouthStretchRight", 15f) },
            ["amused"] = new[] { ("MouthSmileLeft", 60f), ("MouthSmileRight", 55f), ("LipCornerPuller", 45f), ("CheekRaiser", 35f), ("CheekSquintLeft", 35f), ("CheekSquintRight", 35f), ("BrowOuterUpLeft", 15f), ("BrowOuterUpRight", 15f) },
            ["respectful"] = new[] { ("BrowInnerUp", 30f), ("BrowOuterUpLeft", 20f), ("BrowOuterUpRight", 20f), ("MouthSmileLeft", 30f), ("MouthSmileRight", 30f), ("LipCornerPuller", 20f) },
            // gestures and activities
            ["laughs"] = new[] { ("MouthSmileLeft", 100f), ("MouthSmileRight", 100f), ("LipCornerPuller", 75f), ("CheekRaiser", 60f), ("CheekSquintLeft", 60f), ("CheekSquintRight", 60f), ("EyeSquintLeft", 45f), ("EyeSquintRight", 45f) },
            ["winces"] = new[] { ("BrowInnerUp", 60f), ("BrowDownLeft", 30f), ("BrowDownRight", 30f), ("EyeSquintLeft", 60f), ("EyeSquintRight", 60f), ("NoseSneerLeft", 30f), ("NoseSneerRight", 30f), ("MouthStretchLeft", 30f), ("MouthStretchRight", 30f) },
            ["glares"] = new[] { ("BrowDownLeft", 90f), ("BrowDownRight", 90f), ("EyeSquintLeft", 50f), ("EyeSquintRight", 50f), ("MouthPressLeft", 40f), ("MouthPressRight", 40f) },
            ["steps_back"] = new[] { ("BrowInnerUp", 55f), ("BrowOuterUpLeft", 45f), ("BrowOuterUpRight", 45f), ("EyeWideLeft", 55f), ("EyeWideRight", 55f) },
            ["shrugs"] = new[] { ("BrowOuterUpLeft", 50f), ("BrowOuterUpRight", 50f), ("MouthShrugUpper", 30f), ("MouthSmileRight", 20f) },
            ["nods"] = new[] { ("BrowInnerUp", 25f), ("MouthSmileLeft", 25f), ("MouthSmileRight", 25f), ("MouthPressLeft", 20f), ("MouthPressRight", 20f) },
            ["hype"] = new[] { ("MouthSmileLeft", 85f), ("MouthSmileRight", 85f), ("LipCornerPuller", 60f), ("CheekRaiser", 40f), ("CheekSquintLeft", 45f), ("CheekSquintRight", 45f), ("BrowOuterUpLeft", 30f), ("BrowOuterUpRight", 30f) },
            ["showman"] = new[] { ("MouthSmileLeft", 60f), ("MouthSmileRight", 55f), ("LipCornerPuller", 40f), ("CheekSquintLeft", 25f), ("CheekSquintRight", 25f), ("BrowOuterUpLeft", 25f), ("BrowOuterUpRight", 20f) },
            ["focused"] = new[] { ("BrowDownLeft", 20f), ("BrowDownRight", 20f), ("EyeSquintLeft", 15f), ("EyeSquintRight", 15f) },
            ["polite"] = new[] { ("MouthSmileLeft", 28f), ("MouthSmileRight", 28f), ("LipCornerPuller", 15f), ("BrowInnerUp", 10f) },
        };

        const string Prefix = "blendShape1.";
        static readonly string[] Sets = { "AK_", "AU_", "HB_" };   // ARKit, FACS action units, extras (not AA_VI: visemes)
        readonly Dictionary<string, int> index = new();     // ARKit name -> blendshape index
        readonly Dictionary<int, float> shown = new();       // index -> weight now
        readonly Dictionary<int, float> target = new();
        string flash;
        float flashUntil = -1f;

        /// <summary>Whether this face has the ARKit shapes to show expressions with.</summary>
        public static bool CanShow(SkinnedMeshRenderer smr) =>
            smr && smr.sharedMesh && smr.sharedMesh.GetBlendShapeIndex(Prefix + "AK_44_MouthSmileLeft") >= 0;

        void Awake()
        {
            if (!performer) performer = GetComponent<Performer>();
            if (!face)
                foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (CanShow(r)) { face = r; break; }
            if (!face || !face.sharedMesh) { enabled = false; return; }
            var mesh = face.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string n = mesh.GetBlendShapeName(i);
                if (!n.StartsWith(Prefix)) continue;
                string rest = n.Substring(Prefix.Length);   // AK_44_MouthSmileLeft, AU_12_L_LipCornerPuller
                if (System.Array.FindIndex(Sets, rest.StartsWith) < 0) continue;
                int u = rest.IndexOf('_', 3);
                if (u > 0) index.TryAdd(rest.Substring(u + 1), i);   // MouthSmileLeft, L_LipCornerPuller
            }
            if (string.IsNullOrEmpty(resting) && performer && performer.characterId == "interviewer") resting = "polite";
        }

        void OnEnable() { if (performer) performer.Gestured += Flash; }
        void OnDisable() { if (performer) performer.Gestured -= Flash; }

        /// <summary>Shows this expression (a gesture's, or any of the names above) for a moment over the mood.</summary>
        public void Flash(string expression)
        {
            if (expression == null || !Expressions.ContainsKey(expression)) return;
            flash = expression;
            flashUntil = Time.time + flashHold;
        }

        /// <summary>What the face is showing now (the flash, else what they're doing or feel), for tests and debugging.</summary>
        public string Showing { get; private set; } = "calm";

        string Current()
        {
            if (flash != null && Time.time < flashUntil) return flash;
            if (!performer) return "calm";
            return performer.activity switch
            {
                Performer.Activity.Hype => "hype",
                Performer.Activity.Hosting => "showman",
                Performer.Activity.Working => "focused",
                _ => Expressions.ContainsKey(performer.mood ?? "") ? performer.mood : "calm",
            };
        }

        void LateUpdate()
        {
            Showing = Current();
            target.Clear();
            Add(resting, 1f);
            Add(Showing, 1f);
            // ease every shape toward its target (shapes not in it, back to 0)
            float k = 1f - Mathf.Exp(-Time.deltaTime * 3f / Mathf.Max(0.01f, ease));
            foreach (var i in target.Keys) if (!shown.ContainsKey(i)) shown[i] = 0f;
            var keys = new List<int>(shown.Keys);
            foreach (int i in keys)
            {
                target.TryGetValue(i, out float t);
                float w = Mathf.Lerp(shown[i], t, k);
                if (w < 0.05f && t == 0f) { shown.Remove(i); face.SetBlendShapeWeight(i, 0f); continue; }
                shown[i] = w;
                face.SetBlendShapeWeight(i, w);
            }
        }

        void Add(string expression, float scale)
        {
            if (string.IsNullOrEmpty(expression) || !Expressions.TryGetValue(expression, out var shapes)) return;
            foreach (var (shape, w) in shapes)
                if (index.TryGetValue(shape, out int i))
                {
                    target.TryGetValue(i, out float t);
                    target[i] = Mathf.Min(100f, Mathf.Max(t, w * strength * scale));   // the stronger of the two layers
                }
        }
    }
}
