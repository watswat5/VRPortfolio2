// Lip sync from mouth cues: timed shapes for a line, from the server with its audio (opponent_line "mouth",
// POST /lipsync, or baked into a .json by the tools). Either Meta's 15 visemes (sil PP FF TH DD kk CH SS nn RR aa
// E ih oh ou: the forced aligner, from the known text) or Rhubarb's shapes (A-H, X). Shown on a Rocketbox face's
// viseme blendshapes, in time with the AudioSource playing the line, crossfading between shapes.
// Or live: Meta's visemes weighted as Oculus Lipsync hears them (a VisemeTap on the playing source, set by MouthDriver).
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Freestyle.Avatar
{
    public class CueLipSync : MonoBehaviour
    {
        public AudioSource source;
        public SkinnedMeshRenderer face;
        [Tooltip("Baked cues for the source's clip: {\"mouth\": [[start_s, shape], ...]}. " +
                 "Or call SetCues with the server's opponent_line \"mouth\".")]
        public TextAsset cueFile;
        [Range(0f, 1f)] public float intensity = 0.85f;
        [Tooltip("Seconds to blend from one mouth shape to the next.")]
        public float blendTime = 0.06f;
        [Tooltip("Show each shape this much before it's heard (the mouth moves a little ahead of the sound).")]
        public float lead = 0.03f;
        [Tooltip("The viseme blendshapes' names, before the number: Rocketbox's are blendShape1.AA_VI_00_Sil ... 14_U.")]
        public string prefix = "blendShape1.AA_VI_";

        // each cue's shape as Rocketbox visemes: Meta's 15 directly, Rhubarb's (Preston Blair) some blended
        static readonly Dictionary<string, (string viseme, float w)[]> Shapes = new()
        {
            ["sil"] = new (string, float)[0], ["PP"] = new[] { ("01_PP", 1f) }, ["FF"] = new[] { ("02_FF", 1f) },
            ["TH"] = new[] { ("03_TH", 1f) }, ["DD"] = new[] { ("04_DD", 1f) }, ["kk"] = new[] { ("05_KK", 1f) },
            ["CH"] = new[] { ("06_CH", 1f) }, ["SS"] = new[] { ("07_SS", 1f) }, ["nn"] = new[] { ("08_nn", 1f) },
            ["RR"] = new[] { ("09_RR", 1f) }, ["aa"] = new[] { ("10_aa", 1f) }, ["E"] = new[] { ("11_E", 1f) },
            ["ih"] = new[] { ("12_I", 1f) }, ["oh"] = new[] { ("13_O", 1f) }, ["ou"] = new[] { ("14_U", 1f) },
            ["A"] = new[] { ("01_PP", 1f) },                     // Rhubarb: closed: P B M
            ["B"] = new[] { ("07_SS", 0.8f), ("04_DD", 0.3f) },  // teeth together: K S T, EE
            ["C"] = new[] { ("11_E", 1f) },                      // open: EH AE
            ["D"] = new[] { ("10_aa", 1f) },                     // wide open: AA
            ["F"] = new[] { ("14_U", 1f) },                      // puckered: UW OW W
            ["G"] = new[] { ("02_FF", 1f) },                     // F V
            ["H"] = new[] { ("08_nn", 1f) },                     // L, tongue up
            ["X"] = new (string, float)[0],                      // rest
        };
        // Meta's visemes in OVRLipSync.Viseme order (a VisemeTap's weights)
        static readonly string[] Meta = { "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou" };
        // Rhubarb's E (rounded: AO ER) isn't Meta's E (EH): in a list of Rhubarb cues (with X or A) it means this
        static readonly (string viseme, float w)[] RhubarbE = { ("13_O", 0.8f), ("09_RR", 0.3f) };

        readonly List<(float t, string shape)> cues = new();
        VisemeTap live;
        readonly float[] heard = new float[Meta.Length];
        bool rhubarbCues;
        readonly Dictionary<string, int> index = new();
        float[] weights = new float[0];
        int[] shapes = new int[0];

        void Awake()
        {
            if (cueFile) SetCuesJson(cueFile.text);
            FindShapes();
        }

        /// <summary>Cues for what the source plays next: [[start_s, shape], ...] as the server sends them.</summary>
        public void SetCues(IEnumerable<(float t, string shape)> next)
        {
            cues.Clear();
            cues.AddRange(next);
            rhubarbCues = cues.Exists(c => c.shape == "X" || c.shape == "A");   // Rhubarb's set (it always ends on X)
        }

        /// <summary>Follows what Oculus Lipsync hears on tap (null: back to cues).</summary>
        public void SetLive(VisemeTap tap) => live = tap;

        /// <summary>Cues from JSON: {"mouth": [[0.0, "sil"], [0.12, "kk"], ...]} or just the array.</summary>
        public void SetCuesJson(string json) => SetCues(ParseJson(json));

        /// <summary>The cues in JSON ({"mouth": [[start_s, shape], ...]} or just the array), in order.</summary>
        public static List<(float t, string shape)> ParseJson(string json)
        {
            var list = new List<(float, string)>();
            foreach (Match m in Regex.Matches(json, @"\[\s*([-0-9.eE+]+)\s*,\s*""([A-Za-z]+)""\s*\]"))
                list.Add((float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value));
            return list;
        }

        (string viseme, float w)[] Recipe(string shape) =>
            rhubarbCues && shape == "E" ? RhubarbE : Shapes.TryGetValue(shape, out var r) ? r : Shapes["sil"];

        void FindShapes()
        {
            if (!face) return;
            var mesh = face.sharedMesh;
            var list = new List<int>();
            foreach (var recipe in new List<(string, float)[]>(Shapes.Values) { RhubarbE })
                foreach (var (viseme, _) in recipe)
                {
                    int i = mesh.GetBlendShapeIndex(prefix + viseme);
                    if (i < 0) { Debug.LogWarning($"{name}: no blendshape {prefix + viseme} on {face.name}", this); continue; }
                    index[viseme] = i;
                    if (!list.Contains(i)) list.Add(i);
                }
            shapes = list.ToArray();
            weights = new float[shapes.Length];
        }

        /// <summary>The shape heard at clip time t (rest before the first cue).</summary>
        string ShapeAt(float t)
        {
            int lo = 0, hi = cues.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cues[mid].t <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found < 0 ? "sil" : cues[found].shape;
        }

        void OnDisable()
        {
            if (!face) return;
            foreach (int i in shapes) face.SetBlendShapeWeight(i, 0);
            System.Array.Clear(weights, 0, weights.Length);
        }

        void LateUpdate()
        {
            if (!face || shapes.Length == 0) return;
            var target = new Dictionary<int, float>();
            void Add(string shape, float amount)
            {
                foreach (var (viseme, w) in live ? Shapes[shape] : Recipe(shape))   // live: Meta's, never Rhubarb's E
                    if (index.TryGetValue(viseme, out int i)) target[i] = (target.TryGetValue(i, out var have) ? have : 0) + w * amount * 100f * intensity;
            }
            if (live)
            {
                if (live.Read(heard))
                    for (int v = 1; v < Meta.Length; v++) Add(Meta[v], heard[v]);   // sil (0) is the rest
            }
            else Add(source && source.isPlaying && cues.Count > 0 ? ShapeAt(source.time + lead) : "sil", 1f);
            float k = blendTime > 0 ? 1f - Mathf.Exp(-Time.deltaTime * 4.6f / blendTime) : 1f;
            for (int s = 0; s < shapes.Length; s++)
            {
                weights[s] = Mathf.Lerp(weights[s], target.TryGetValue(shapes[s], out var goal) ? goal : 0f, k);
                face.SetBlendShapeWeight(shapes[s], weights[s]);
            }
        }
    }
}
