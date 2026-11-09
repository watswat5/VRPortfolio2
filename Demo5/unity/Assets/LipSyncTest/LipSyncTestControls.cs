// Test controls for the lip sync scene: nothing plays on start.
//   Next (N): the camera goes to the next character's face and their voice plays (everyone else stops).
//   Reset audio (R): the current character's line again, from the start.
//   Expression (E): the current face's next expression (the game's opponent moods), on top of the lip sync.
//   Lip sync (L): Rhubarb (the server's mouth cues, baked here for each clip) or uLipSync (hears the audio live).
// The lip sync itself is those components on each character's Voice object; this only picks who talks.
using System.Collections.Generic;
using UnityEngine;

public class LipSyncTestControls : MonoBehaviour
{
    [System.Serializable]
    public class Character
    {
        public string name;
        public AudioSource voice;
        public SkinnedMeshRenderer face;
        public Transform head;
        public Freestyle.Avatar.CueLipSync rhubarb;
        public uLipSync.uLipSyncBlendShape ulipsync;
    }

    public enum LipSync { Rhubarb, uLipSync }
    public LipSync lipSync = LipSync.Rhubarb;

    public Camera cam;
    public List<Character> characters = new();
    [Tooltip("How far in front of the face the camera sits (m).")]
    public float faceDistance = 0.55f;
    [Tooltip("Seconds to blend into an expression.")]
    public float blendTime = 0.25f;

    // ARKit blendshape names (Rocketbox's AK_ shapes) and weights 0..100, per expression
    static readonly (string name, (string shape, float w)[] shapes)[] Expressions =
    {
        ("Calm", new (string, float)[0]),
        ("Amused (laughs)", new[] { ("MouthSmileLeft", 90f), ("MouthSmileRight", 90f), ("CheekSquintLeft", 60f), ("CheekSquintRight", 60f), ("EyeSquintLeft", 45f), ("EyeSquintRight", 45f), ("JawOpen", 25f) }),
        ("Cocky (smirk)", new[] { ("MouthSmileRight", 75f), ("CheekSquintRight", 35f), ("EyeSquintLeft", 35f), ("EyeSquintRight", 35f), ("BrowOuterUpRight", 40f), ("BrowDownLeft", 30f) }),
        ("Heated", new[] { ("BrowDownLeft", 95f), ("BrowDownRight", 95f), ("NoseSneerLeft", 60f), ("NoseSneerRight", 60f), ("MouthFrownLeft", 50f), ("MouthFrownRight", 50f), ("MouthPressLeft", 40f), ("MouthPressRight", 40f), ("EyeSquintLeft", 40f), ("EyeSquintRight", 40f) }),
        ("Rattled", new[] { ("BrowInnerUp", 90f), ("MouthFrownLeft", 55f), ("MouthFrownRight", 55f), ("MouthStretchLeft", 30f), ("MouthStretchRight", 30f), ("EyeLookDownLeft", 35f), ("EyeLookDownRight", 35f) }),
        ("Respectful (impressed)", new[] { ("BrowOuterUpLeft", 60f), ("BrowOuterUpRight", 60f), ("BrowInnerUp", 40f), ("MouthPucker", 45f), ("MouthShrugLower", 40f) }),
        ("Glares", new[] { ("BrowDownLeft", 80f), ("BrowDownRight", 80f), ("EyeSquintLeft", 70f), ("EyeSquintRight", 70f), ("NoseSneerLeft", 40f), ("NoseSneerRight", 40f), ("MouthPressLeft", 50f), ("MouthPressRight", 50f) }),
        ("Surprised (steps back)", new[] { ("BrowInnerUp", 80f), ("BrowOuterUpLeft", 90f), ("BrowOuterUpRight", 90f), ("EyeWideLeft", 80f), ("EyeWideRight", 80f), ("JawOpen", 35f) }),
    };

    int current = -1, expression;
    readonly Dictionary<SkinnedMeshRenderer, Dictionary<string, int>> shapeIndex = new();
    readonly Dictionary<SkinnedMeshRenderer, Dictionary<int, float>> shapeWeight = new();
    Vector3 overviewPos; Quaternion overviewRot;

    public string Status => current < 0
        ? "Overview: press Next"
        : $"{characters[current].name}: {(characters[current].voice.isPlaying ? "talking" : "quiet")} · {Expressions[expression].name}";
    string LipSyncName => lipSync.ToString();

    void Start()
    {
        if (!cam) cam = Camera.main;
        overviewPos = cam.transform.position; overviewRot = cam.transform.rotation;
        foreach (var c in characters) c.voice.Stop();   // nothing plays on start
        ApplyLipSync();
    }

    /// <summary>One of the two drives the mouths (never both: they move the same shapes).</summary>
    public void ToggleLipSync() { lipSync = lipSync == LipSync.Rhubarb ? LipSync.uLipSync : LipSync.Rhubarb; ApplyLipSync(); }

    void ApplyLipSync()
    {
        foreach (var c in characters)
        {
            if (c.rhubarb) c.rhubarb.enabled = lipSync == LipSync.Rhubarb;
            if (c.ulipsync) c.ulipsync.enabled = lipSync == LipSync.uLipSync;
            for (int i = 0; i < 15; i++) c.face.SetBlendShapeWeight(i, 0);   // the visemes come first on Rocketbox
        }
    }

    /// <summary>The next character: the camera on their face, their voice from the start, everyone else quiet.</summary>
    public void Next()
    {
        foreach (var c in characters) c.voice.Stop();
        current = (current + 1) % characters.Count;
        var ch = characters[current];
        var look = ch.head.position + Vector3.up * 0.07f;                    // the head bone is low in the skull
        var from = look + ch.head.root.forward * faceDistance;               // in front of the character
        cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(look - from));
        ch.voice.Play();
    }

    /// <summary>The current character's line again from the start.</summary>
    public void ResetAudio()
    {
        foreach (var c in characters) c.voice.Stop();
        if (current >= 0) { characters[current].voice.time = 0; characters[current].voice.Play(); }
    }

    /// <summary>The next expression, on the current character (or everyone, from the overview).</summary>
    public void NextExpression() => expression = (expression + 1) % Expressions.Length;

    public void Overview()
    {
        foreach (var c in characters) c.voice.Stop();
        current = -1;
        cam.transform.SetPositionAndRotation(overviewPos, overviewRot);
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null)
        {
            if (kb.nKey.wasPressedThisFrame) Next();
            if (kb.rKey.wasPressedThisFrame) ResetAudio();
            if (kb.eKey.wasPressedThisFrame) NextExpression();
            if (kb.oKey.wasPressedThisFrame) Overview();
            if (kb.lKey.wasPressedThisFrame) ToggleLipSync();
        }
#else
        if (Input.GetKeyDown(KeyCode.N)) Next();
        if (Input.GetKeyDown(KeyCode.R)) ResetAudio();
        if (Input.GetKeyDown(KeyCode.E)) NextExpression();
        if (Input.GetKeyDown(KeyCode.O)) Overview();
        if (Input.GetKeyDown(KeyCode.L)) ToggleLipSync();
#endif
    }

    // after uLipSync's viseme shapes (it only touches those; expressions are the AK_ shapes)
    void LateUpdate()
    {
        var target = new Dictionary<string, float>();
        foreach (var (shape, w) in Expressions[expression].shapes) target[shape] = w;
        float k = blendTime > 0 ? 1f - Mathf.Exp(-Time.deltaTime * 4.6f / blendTime) : 1f;
        for (int i = 0; i < characters.Count; i++)
        {
            var smr = characters[i].face;
            bool shows = current < 0 || i == current;   // from the overview, everyone shows it
            foreach (var (shape, index) in Index(smr))
            {
                float goal = shows && target.TryGetValue(shape, out var w) ? w : 0f;
                var weights = shapeWeight[smr];
                weights[index] = Mathf.Lerp(weights.TryGetValue(index, out var now) ? now : 0f, goal, k);
                smr.SetBlendShapeWeight(index, weights[index]);
            }
        }
    }

    /// <summary>This face's index for each ARKit shape the expressions use ("blendShape1.AK_25_JawOpen" -> JawOpen).</summary>
    Dictionary<string, int> Index(SkinnedMeshRenderer smr)
    {
        if (shapeIndex.TryGetValue(smr, out var map)) return map;
        map = new Dictionary<string, int>();
        var used = new HashSet<string>();
        foreach (var (_, shapes) in Expressions) foreach (var (shape, _) in shapes) used.Add(shape);
        var mesh = smr.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string n = mesh.GetBlendShapeName(i);
            if (!n.Contains(".AK_")) continue;
            string arkit = n.Substring(n.LastIndexOf('_') + 1);
            if (used.Contains(arkit) && !map.ContainsKey(arkit)) map[arkit] = i;
        }
        shapeIndex[smr] = map;
        shapeWeight[smr] = new Dictionary<int, float>();
        return map;
    }

    void OnGUI()
    {
        const int w = 200, h = 44;
        var big = new GUIStyle(GUI.skin.button) { fontSize = 18 };
        GUILayout.BeginArea(new Rect(16, 16, w + 40, 5 * (h + 8) + 80));
        if (GUILayout.Button("Next  (N)", big, GUILayout.Height(h))) Next();
        if (GUILayout.Button("Reset audio  (R)", big, GUILayout.Height(h))) ResetAudio();
        if (GUILayout.Button("Expression  (E)", big, GUILayout.Height(h))) NextExpression();
        if (GUILayout.Button("Overview  (O)", big, GUILayout.Height(h))) Overview();
        if (GUILayout.Button($"Lip sync: {LipSyncName}  (L)", big, GUILayout.Height(h))) ToggleLipSync();
        GUILayout.Label(Status, new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, normal = { textColor = Color.black } });
        GUILayout.EndArea();
    }
}
