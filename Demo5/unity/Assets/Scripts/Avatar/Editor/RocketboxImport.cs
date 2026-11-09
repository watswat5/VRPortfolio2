// Rocketbox avatars set up for the game: the model imported as a Humanoid with its blendshapes (the eyes left out of
// the rig), its textures typed, and URP materials (the FBX's 3ds Max ones don't render in URP).
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class RocketboxImport
{
    public const string Avatars = "Assets/Avatars/Rocketbox";

    // uLipSync's sample profiles hear these phonemes; each drives one of Rocketbox's 15 viseme blendshapes
    public static readonly (string phoneme, string shape)[] ULipSyncVisemes =
    {
        ("A", "AA_VI_10_aa"), ("I", "AA_VI_12_I"), ("U", "AA_VI_14_U"), ("E", "AA_VI_11_E"), ("O", "AA_VI_13_O"),
        ("S", "AA_VI_07_SS"), ("-", "AA_VI_00_Sil"),
    };

    /// <summary>uLipSync on the voice's GameObject (it hears that AudioSource), moving the face; returns the pair.</summary>
    public static (uLipSync.uLipSync, uLipSync.uLipSyncBlendShape) AddULipSync(GameObject voice, SkinnedMeshRenderer face, bool female)
    {
        var lipSync = voice.AddComponent<uLipSync.uLipSync>();
        lipSync.profile = AssetDatabase.LoadAssetAtPath<uLipSync.Profile>(
            $"Packages/com.hecomi.ulipsync/Assets/Profiles/uLipSync-Profile-Sample-{(female ? "Female" : "Male")}.asset");
        var shapes = voice.AddComponent<uLipSync.uLipSyncBlendShape>();
        shapes.skinnedMeshRenderer = face;
        foreach (var (phoneme, shape) in ULipSyncVisemes)
            shapes.AddBlendShape(phoneme, "blendShape1." + shape);
        UnityEditor.Events.UnityEventTools.AddPersistentListener(lipSync.onLipSyncUpdate, shapes.OnLipSyncUpdate);
        return (lipSync, shapes);
    }

    /// <summary>The model as a Humanoid with its blendshapes; its textures typed.</summary>
    public static void SetUpImport(string avatar)
    {
        string fbx = $"{Avatars}/{avatar}/{avatar}_facial.fbx";
        var imp = (ModelImporter)AssetImporter.GetAtPath(fbx);
        imp.animationType = ModelImporterAnimationType.Human;
        imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        imp.importBlendShapes = true;
        imp.SaveAndReimport();
        // the eyes stay out of the Humanoid rig: Rocketbox's eye bones don't match Unity's eye axes, so the animation
        // would roll the eyeballs back. LookAndBlink aims them itself.
        var hd = imp.humanDescription;
        if (hd.human.Any(h => h.humanName is "LeftEye" or "RightEye"))
        {
            hd.human = hd.human.Where(h => h.humanName is not ("LeftEye" or "RightEye")).ToArray();
            imp.humanDescription = hd;
            imp.SaveAndReimport();
        }
        foreach (var tex in Directory.GetFiles($"{Avatars}/{avatar}/Textures").Where(f => !f.EndsWith(".meta")))
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(tex);
            if (tex.Contains("_normal")) ti.textureType = TextureImporterType.NormalMap;
            if (tex.Contains("_opacity")) ti.alphaIsTransparency = true;
            ti.SaveAndReimport();
        }
    }

    /// <summary>A URP material for one part of the avatar: body, head, or opacity (hair and lashes).</summary>
    public static Material Material(string avatar, string part)
    {
        string path = $"{Avatars}/{avatar}/{avatar}_{part}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat) return mat;
        mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = $"{avatar}_{part}" };
        string tex = Directory.GetFiles($"{Avatars}/{avatar}/Textures").First(f => f.Contains($"_{part}_color") && !f.EndsWith(".meta"));
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(tex));
        mat.SetFloat("_Smoothness", 0.25f);
        var normal = Directory.GetFiles($"{Avatars}/{avatar}/Textures").FirstOrDefault(f => f.Contains($"_{part}_normal") && !f.EndsWith(".meta"));
        if (normal != null)
        {
            mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normal));
            mat.EnableKeyword("_NORMALMAP");
        }
        if (part == "opacity")   // hair cards and lashes: cut out by the texture's alpha, both sides
        {
            mat.SetFloat("_AlphaClip", 1); mat.SetFloat("_Cutoff", 0.4f); mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetFloat("_Cull", 0); mat.renderQueue = (int)RenderQueue.AlphaTest;
        }
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    public static string Part(string materialName) =>
        materialName.Contains("opacity") ? "opacity" : materialName.Contains("head") ? "head" : "body";
}
