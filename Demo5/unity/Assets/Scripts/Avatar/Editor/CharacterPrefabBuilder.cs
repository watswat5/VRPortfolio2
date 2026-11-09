// Builds a prefab per character in Assets/Characters (<id>.prefab), ready to drop in a scene or spawn: the Rocketbox
// model with URP materials, on Rapper.controller (in place, no root motion), LookAndBlink (eyes on the main camera,
// blinks), HoldMic (the mic in the right hand, made at Start; the interviewer gets HoldClipboard instead: a clipboard
// and pencil; the DJ also wears headphones, HeadphonesBuilder), a Voice at the head (a 3D AudioSource, CueLipSync for
// the server's mouth cues, and uLipSync for lines without them), MouthDriver choosing between the two, and Performer
// wired to all of them, with the character's server id and name.
// Facing +Z. Rebuilding overwrites the prefabs in place (same GUIDs), so references to them hold.
// Freestyle > Build Character Prefabs.
using System.Linq;
using Freestyle.Avatar;
using UnityEditor;
using UnityEngine;

public static class CharacterPrefabBuilder
{
    const string Dir = "Assets/Characters";

    // server id, name, Rocketbox avatar, female (uLipSync's profile)
    static readonly (string id, string name, string avatar, bool female)[] Cast =
    {
        ("lil_nervous", "Lil Nervous", "Male_Adult_20", false),
        ("mc_static", "MC Static", "Male_Adult_17", false),
        ("loose_cannon", "Loose Cannon", "Male_Adult_10", false),
        ("kid_venom", "Kid Venom", "Female_Adult_12", true),
        ("the_champ", "The Champ", "Male_Adult_18", false),
        ("interviewer", "Interviewer", "Business_Female_01", true),   // the pre-show interview (no mic: a clipboard)
        ("dj", "DJ Crossfade", "Male_Adult_11", false),               // hosts the battles (DJHost), then works the decks
    };

    [MenuItem("Freestyle/Build Character Prefabs")]
    public static void Build()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Build character prefabs", "Stop Play mode first.", "OK");
            return;
        }
        var ctrl = RapperAnimatorBuilder.Build();
        if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets", "Characters");
        foreach (var c in Cast) Make(c.id, c.name, c.avatar, c.female, ctrl);
        AssetDatabase.SaveAssets();
        Debug.Log($"Character prefabs built: {Dir} ({Cast.Length})");
    }

    static void Make(string id, string name, string avatar, bool female, RuntimeAnimatorController ctrl)
    {
        RocketboxImport.SetUpImport(avatar);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{RocketboxImport.Avatars}/{avatar}/{avatar}_facial.fbx");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            go.name = name;
            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            smr.sharedMaterials = smr.sharedMaterials.Select(m => RocketboxImport.Material(avatar, RocketboxImport.Part(m.name))).ToArray();
            smr.updateWhenOffscreen = true;   // the bounds don't follow the clips' arm moves

            var anim = go.GetComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;   // out of view: no IK or skinning work

            var look = go.AddComponent<LookAndBlink>();
            look.face = smr;
            HoldMic mic = null;
            if (id == "interviewer") go.AddComponent<HoldClipboard>();
            else mic = go.AddComponent<HoldMic>();

            var head = go.GetComponentsInChildren<Transform>().First(t => t.name == "Bip01 Head");
            if (id == "dj") HeadphonesBuilder.Wear(go, smr, head);   // on the head, over the ears (the decks' headphones moves)
            var voiceGo = new GameObject("Voice");
            voiceGo.transform.SetParent(head, false);
            var voice = voiceGo.AddComponent<AudioSource>();
            voice.playOnAwake = false; voice.loop = false;
            voice.spatialBlend = 1f; voice.minDistance = 1.5f; voice.maxDistance = 25f; voice.rolloffMode = AudioRolloffMode.Linear;
            var lip = voiceGo.AddComponent<CueLipSync>();
            lip.source = voice; lip.face = smr;
            // the fallback for lines without mouth cues: MouthDriver turns it on and switches its face
            var (uls, ulsFace) = RocketboxImport.AddULipSync(voiceGo, smr, female);
            uls.enabled = false; ulsFace.enabled = false;
            var mouth = go.AddComponent<MouthDriver>();
            mouth.cues = lip; mouth.fallback = uls; mouth.fallbackFace = ulsFace;

            var perf = go.AddComponent<Performer>();
            perf.characterId = id; perf.displayName = name;
            perf.mic = mic; perf.voice = voice; perf.mouth = mouth;

            PrefabUtility.SaveAsPrefabAsset(go, $"{Dir}/{id}.prefab");
        }
        finally { Object.DestroyImmediate(go); }
    }
}
