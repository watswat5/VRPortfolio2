// Builds Assets/Scenes/GestureTest.unity: MC Static (Rocketbox Male_Adult_17) on a floor, on the game's Rapper.controller
// (rebuilt first: every clip in Moves, grouped by what the game uses it for), nine lines
// in his voice (backend/out via OpenRouter, Rhubarb cues baked), and GestureTestControls to cycle them.
// Freestyle > Build Gesture Test.
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class GestureTestBuilder
{
    const string Avatars = "Assets/Avatars/Rocketbox", Dir = "Assets/GestureTest";
    const string Avatar = "Male_Adult_17";

    [MenuItem("Freestyle/Build Gesture Test")]
    public static void Build()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Build gesture test", "Stop Play mode first: the builder makes a new scene.", "OK");
            return;
        }
        RocketboxImport.SetUpImport(Avatar);
        var ctrl = RapperAnimatorBuilder.Build();   // the game's controller: every clip, by slot
        AssetDatabase.DeleteAsset(Dir + "/GestureTest.controller");   // the old one-scene copy
        foreach (var f in Directory.GetFiles(Dir + "/Audio", "*.wav"))
        {
            var ti = (AudioImporter)AssetImporter.GetAtPath(f.Replace('\\', '/'));
            ti.forceToMono = true; ti.SaveAndReimport();
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.48f);

        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>($"{Avatars}/{Avatar}/{Avatar}_facial.fbx"));
        go.name = $"MC Static ({Avatar})";
        go.transform.rotation = Quaternion.Euler(0, 180, 0);   // facing the camera
        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
        smr.sharedMaterials = smr.sharedMaterials.Select(m => RocketboxImport.Material(Avatar, RocketboxImport.Part(m.name))).ToArray();
        smr.updateWhenOffscreen = true;
        var anim = go.GetComponent<Animator>();
        anim.runtimeAnimatorController = ctrl;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        var life = go.AddComponent<Freestyle.Avatar.LookAndBlink>();
        life.face = smr;
        var mic = go.AddComponent<Freestyle.Avatar.HoldMic>();   // builds its mic at Start

        var head = go.GetComponentsInChildren<Transform>().First(t => t.name == "Bip01 Head");
        var voiceGo = new GameObject("Voice");
        voiceGo.transform.SetParent(head, false);
        var voice = voiceGo.AddComponent<AudioSource>();
        voice.playOnAwake = false; voice.spatialBlend = 0.6f;
        var lip = voiceGo.AddComponent<Freestyle.Avatar.CueLipSync>();
        lip.source = voice; lip.face = smr;

        var controls = new GameObject("Gesture Test Controls").AddComponent<GestureTestControls>();
        controls.anim = anim; controls.voice = voice; controls.lipSync = lip; controls.cam = Camera.main; controls.head = head;
        controls.mic = mic;
        controls.clips = Freestyle.Avatar.Moves.All.Select(m => new GestureTestControls.Clip { slot = m.slot, state = m.clip, path = m.Path, loop = m.loop }).ToList();
        controls.idleState = Freestyle.Avatar.Moves.InSlot(Freestyle.Avatar.Moves.Idle)[0].Path;
        var meta = JsonUtility.FromJson<LineList>("{\"lines\":" + File.ReadAllText(Dir + "/Audio/lines.json") + "}");
        controls.lines = meta.lines.Select(l => new GestureTestControls.Line
        {
            slot = l.@for, text = l.text,
            audio = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Dir}/Audio/{l.file}.wav"),
            cues = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Dir}/Audio/{l.file}.json"),
        }).ToList();

        Camera.main.transform.SetPositionAndRotation(new Vector3(-1.2f, 1.25f, -2.6f), Quaternion.LookRotation(new Vector3(0, 1.0f, 0) - new Vector3(-1.2f, 1.25f, -2.6f)));
        Camera.main.fieldOfView = 45;
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/GestureTest.unity");
        Debug.Log("Gesture test built: Assets/Scenes/GestureTest.unity");
    }

    [System.Serializable] class LineList { public LineInfo[] lines; }
    [System.Serializable] class LineInfo { public string file, @for, text; }
}
