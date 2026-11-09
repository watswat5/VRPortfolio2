// Builds Assets/Scenes/LipSyncTest.unity: the Rocketbox cast in a row, each idling (Rocketbox motion capture),
// eyes on the camera and blinking (LookAndBlink), and lip-syncing to a voice clip: Rhubarb's cues (CueLipSync) or
// uLipSync, switched by the test controls. Freestyle > Build Lip Sync Test. Also sets up the avatars' import
// (Humanoid rig, its eyes left out, normal maps), the idle clips' import, and URP materials.
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class LipSyncTestBuilder
{
    const string Avatars = "Assets/Avatars/Rocketbox", Dir = "Assets/LipSyncTest";

    // avatar, who it's for, its voice clip, female (for uLipSync's profile and the idle's skeleton), its idle
    static readonly (string avatar, string role, string clip, bool female, string idle)[] Cast =
    {
        ("Male_Adult_20", "Lil Nervous", "voice_male_a", false, "m_idle_breathe_01"),
        ("Male_Adult_17", "MC Static", "voice_male_b", false, "m_idle_neutral_01"),
        ("Male_Adult_10", "Loose Cannon", "voice_male_c", false, "m_idle_neutral_02"),
        ("Female_Adult_12", "Kid Venom", "voice_female_a", true, "f_idle_neutral_01"),
        ("Male_Adult_18", "The Champ", "voice_male_a", false, "m_idle_breathe_02"),
    };
    const string Anims = Avatars + "/Animations";   // Rocketbox idles (motion extracted: in place)

    [MenuItem("Freestyle/Build Lip Sync Test")]
    public static void Build()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Build lip sync test", "Stop Play mode first: the builder makes a new scene.", "OK");
            return;
        }
        foreach (var c in Cast) RocketboxImport.SetUpImport(c.avatar);
        foreach (var c in Cast) SetUpIdle(c.idle, c.female ? "Female_Adult_12" : "Male_Adult_20");
        AssetDatabase.Refresh();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);   // camera + sun
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.localScale = Vector3.one * 2;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.48f);

        float spacing = 1.4f, x0 = -spacing * (Cast.Length - 1) / 2;
        var controls = new GameObject("Test Controls").AddComponent<LipSyncTestControls>();
        controls.cam = Camera.main;
        for (int i = 0; i < Cast.Length; i++) controls.characters.Add(Place(Cast[i], new Vector3(x0 + i * spacing, 0, 0)));

        var cam = Camera.main.transform;
        cam.SetPositionAndRotation(new Vector3(0, 1.5f, -5.2f), Quaternion.Euler(4, 0, 0));
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/LipSyncTest.unity");
        Debug.Log("Lip sync test built: Assets/Scenes/LipSyncTest.unity");
    }

    /// <summary>An idle clip on the avatar's rig (Humanoid, looping, in place), and a controller that plays it with IK on.</summary>
    static void SetUpIdle(string idle, string rigFrom)
    {
        string fbx = $"{Anims}/{idle}.fbx";
        var imp = (ModelImporter)AssetImporter.GetAtPath(fbx);
        imp.animationType = ModelImporterAnimationType.Human;
        imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
        imp.sourceAvatar = AssetDatabase.LoadAllAssetsAtPath($"{Avatars}/{rigFrom}/{rigFrom}_facial.fbx").OfType<UnityEngine.Avatar>().First();
        imp.importBlendShapes = false;
        imp.materialImportMode = ModelImporterMaterialImportMode.None;
        var clips = imp.defaultClipAnimations;
        foreach (var c in clips)
        {
            c.loopTime = true; c.loopPose = true;
            c.lockRootRotation = c.lockRootHeightY = c.lockRootPositionXZ = true;   // stays on its spot
            c.keepOriginalOrientation = c.keepOriginalPositionY = c.keepOriginalPositionXZ = true;
        }
        imp.clipAnimations = clips;
        imp.SaveAndReimport();

        string path = $"{Dir}/Idle_{idle}.controller";
        if (AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(path)) AssetDatabase.DeleteAsset(path);
        var ctrl = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(path);
        var layers = ctrl.layers;
        layers[0].iKPass = true;   // LookAndBlink's look-at
        ctrl.layers = layers;
        var clip = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>().First(a => !a.name.StartsWith("__preview"));
        ctrl.layers[0].stateMachine.AddState("Idle").motion = clip;
    }

    static LipSyncTestControls.Character Place((string avatar, string role, string clip, bool female, string idle) c, Vector3 at)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{Avatars}/{c.avatar}/{c.avatar}_facial.fbx");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
        go.name = $"{c.role} ({c.avatar})";
        go.transform.SetPositionAndRotation(at, Quaternion.Euler(0, 180, 0));   // facing the camera
        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
        var anim = go.GetComponent<Animator>();
        anim.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>($"{Dir}/Idle_{c.idle}.controller");
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        var life = go.AddComponent<Freestyle.Avatar.LookAndBlink>();
        life.face = smr;
        smr.sharedMaterials = smr.sharedMaterials.Select(m => RocketboxImport.Material(c.avatar, RocketboxImport.Part(m.name))).ToArray();   // the 3ds Max ones don't render in URP
        smr.updateWhenOffscreen = true;

        // the voice at the head: an AudioSource, uLipSync listening to it, uLipSyncBlendShape moving the face
        var head = go.GetComponentsInChildren<Transform>().First(t => t.name == "Bip01 Head");
        var voice = new GameObject("Voice");
        voice.transform.SetParent(head, false);
        var src = voice.AddComponent<AudioSource>();
        src.clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Dir}/Audio/{c.clip}.wav");
        src.loop = false; src.playOnAwake = false;   // the test controls play one at a time
        src.spatialBlend = 1f; src.minDistance = 1f; src.maxDistance = 8f; src.rolloffMode = AudioRolloffMode.Linear;
        var (_, face) = RocketboxImport.AddULipSync(voice, smr, c.female);
        // Rhubarb: the clip's cues, baked by backend/tools/mouth_cues.py (the server sends the same with each line)
        var rhubarb = voice.AddComponent<Freestyle.Avatar.CueLipSync>();
        rhubarb.source = src; rhubarb.face = smr;
        rhubarb.cueFile = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Dir}/Audio/{c.clip}.json");

        var label = new GameObject("Label").AddComponent<TextMesh>();
        label.transform.SetParent(go.transform, false);
        label.transform.localPosition = new Vector3(0, 2.0f, 0);
        label.transform.localRotation = Quaternion.Euler(0, 180, 0);
        label.text = c.role; label.characterSize = 0.02f; label.fontSize = 64; label.anchor = TextAnchor.MiddleCenter;
        label.color = Color.black;
        return new LipSyncTestControls.Character { name = c.role, voice = src, face = smr, head = head, rhubarb = rhubarb, ulipsync = face };
    }

    // ---------------------------------------------------------------- checks (batch mode)

    /// <summary>Unity -batchmode -executeMethod LipSyncTestBuilder.BuildAndVerify (no -quit): builds the scene, renders
    /// it, then plays it muted: Next through every character (logging their visemes' peak weights and rendering their
    /// face mid-line), then every expression on the last one. Writes into $LIPSYNC_OUT, then quits.</summary>
    public static void BuildAndVerify()
    {
        Build();
        string outDir = System.Environment.GetEnvironmentVariable("LIPSYNC_OUT") ?? "Temp";
        Directory.CreateDirectory(outDir);
        Shot(outDir + "/overview.png", Camera.main);
        SessionState.SetString("lipsync_verify", outDir);
        EditorApplication.EnterPlaymode();
    }

    // entering play mode reloads the scripts, so the check picks itself up again here
    [InitializeOnLoadMethod]
    static void Resume()
    {
        if (SessionState.GetString("lipsync_verify", "") == "") return;
        EditorApplication.playModeStateChanged += s =>
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            // silent: uLipSync hears each source before its own output gain (muting the listener starves it instead)
            foreach (var l in Object.FindObjectsByType<uLipSync.uLipSync>(FindObjectsSortMode.None)) l.outputSoundGain = 0;
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Step;
        };
    }

    static double started;
    static int step = -1;
    static readonly System.Collections.Generic.List<string> report = new();
    static readonly float[] peaks = new float[15];

    static void Step()
    {
        var c = Object.FindFirstObjectByType<LipSyncTestControls>();
        string outDir = SessionState.GetString("lipsync_verify", "Temp");
        double t = EditorApplication.timeSinceStartup - started;
        int n = c.characters.Count, wanted = (int)(t / 2.5);   // a character every 2.5 s, then expressions every 0.6 s
        if (wanted < n)
        {
            if (wanted != step)
            {
                if (step >= 0) Report(c.characters[step]);
                step = wanted; System.Array.Clear(peaks, 0, 15); c.Next(); shot = false;
            }
            var face = c.characters[step].face;
            for (int i = 0; i < 15; i++) peaks[i] = Mathf.Max(peaks[i], face.GetBlendShapeWeight(i));
            if (!shot && t - step * 2.5 > 1.1) { Shot($"{outDir}/face_{step}.png", c.cam); shot = true; }
            return;
        }
        if (step == n - 1) { Report(c.characters[step]); step = n; exprStart = t; }
        int e = (int)((t - exprStart) / 0.6);
        if (e < 8)
        {
            if (e != lastExpr) { if (lastExpr >= 0) Shot($"{outDir}/expression_{lastExpr}.png", c.cam); if (e > 0) c.NextExpression(); lastExpr = e; }
            return;
        }
        Shot($"{outDir}/expression_7.png", c.cam);
        report.Add($"audio output: {AudioSettings.outputSampleRate} Hz");
        File.WriteAllLines(outDir + "/report.txt", report);
        SessionState.EraseString("lipsync_verify");
        EditorApplication.update -= Step;
        EditorApplication.Exit(0);
    }

    static bool shot;
    static double exprStart;
    static int lastExpr = -1;

    static void Report(LipSyncTestControls.Character ch)
    {
        var mesh = ch.face.sharedMesh;
        report.Add($"{ch.name}: " + string.Join(", ", Enumerable.Range(0, 15).Where(i => peaks[i] > 1)
            .Select(i => $"{mesh.GetBlendShapeName(i).Replace("blendShape1.AA_VI_", "")} {peaks[i]:0}")));
    }

    static void Shot(string file, Camera cam)
    {
        var rt = new RenderTexture(1280, 720, 24);
        var old = cam.targetTexture;
        cam.targetTexture = rt; cam.Render(); cam.targetTexture = old;
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
        File.WriteAllBytes(file, tex.EncodeToPNG());
        RenderTexture.active = null;
        rt.Release();
    }
}
