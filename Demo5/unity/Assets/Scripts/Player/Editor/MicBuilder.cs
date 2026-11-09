// The player's stage mic as a prefab: Assets/Models/mic/Mic.prefab, from art/mic/make_mic.py's model and textures.
// Sets the textures up (the mesh's normal map as one, its metallic map linear) and gives each part its URP material:
// a matte black handle, chrome, the woven wire grille (colour, normal and metallic/smoothness maps, tiled round the
// ball), a dark plastic switch, a glowing green power light. No colliders: it's part of the hand (PlayerMic).
// Freestyle > Build Mic (Build VR Player makes it too, if it isn't there).
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class MicBuilder
{
    const string Dir = "Assets/Models/mic", Mats = Dir + "/Materials";
    public const string PrefabPath = Dir + "/Mic.prefab";

    [MenuItem("Freestyle/Build Mic")]
    static void Build() => Ensure(true);

    /// <summary>The mic prefab, made first if it isn't there (or remade).</summary>
    public static GameObject Ensure(bool rebuild = false)
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (existing && !rebuild) return existing;
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/mic.fbx");
        if (!model) { Debug.LogWarning($"No {Dir}/mic.fbx: run art/mic/make_mic.py"); return existing; }

        var albedo = Tex("mic_mesh_albedo", srgb: true);
        var normal = Tex("mic_mesh_normal", normalMap: true);
        var metal = Tex("mic_mesh_metal", srgb: false);
        if (!AssetDatabase.IsValidFolder(Mats)) AssetDatabase.CreateFolder(Dir, "Materials");

        var body = Lit("Mic Body", new Color(0.05f, 0.05f, 0.055f), 0f, 0.35f);
        var chrome = Lit("Mic Chrome", new Color(0.82f, 0.82f, 0.85f), 1f, 0.88f);
        var grille = Lit("Mic Grille", Color.white, 1f, 1f);
        grille.SetTexture("_BaseMap", albedo);
        grille.SetTexture("_BumpMap", normal);
        grille.SetFloat("_BumpScale", 1f);
        grille.EnableKeyword("_NORMALMAP");
        grille.SetTexture("_MetallicGlossMap", metal);   // R metallic, A smoothness
        grille.EnableKeyword("_METALLICSPECGLOSSMAP");
        grille.SetTextureScale("_BaseMap", new Vector2(28, 14));   // fine mesh round the ball (URP tiles every map with it)
        var sw = Lit("Mic Switch", new Color(0.12f, 0.12f, 0.13f), 0f, 0.45f);
        var light = Lit("Mic Light", new Color(0.2f, 1f, 0.45f), 0f, 0.7f);
        light.EnableKeyword("_EMISSION");
        light.SetColor("_EmissionColor", new Color(0.12f, 1f, 0.3f) * 1.8f);   // (brighter washes it out to white)
        light.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        foreach (var m in new[] { body, chrome, grille, sw, light }) EditorUtility.SetDirty(m);

        var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
        {
            r.sharedMaterial = r.name switch { "Body" => body, "Chrome" => chrome, "Grille" => grille, "Switch" => sw, "Light" => light, _ => r.sharedMaterial };
            r.shadowCastingMode = ShadowCastingMode.On;
        }
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
        Object.DestroyImmediate(go);
        AssetDatabase.SaveAssets();
        Debug.Log($"Mic prefab: {PrefabPath}");
        return prefab;
    }

    static Texture2D Tex(string name, bool srgb = true, bool normalMap = false)
    {
        string path = $"{Dir}/{name}.png";
        if (AssetImporter.GetAtPath(path) is TextureImporter ti)
        {
            bool changed = false;
            if (normalMap && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; changed = true; }
            if (!normalMap && ti.sRGBTexture != srgb) { ti.sRGBTexture = srgb; changed = true; }
            if (ti.wrapMode != TextureWrapMode.Repeat) { ti.wrapMode = TextureWrapMode.Repeat; changed = true; }
            if (ti.anisoLevel < 4) { ti.anisoLevel = 4; changed = true; }   // a fine mesh seen at an angle
            if (changed) ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static Material Lit(string name, Color colour, float metallic, float smoothness)
    {
        string path = $"{Mats}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!m)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", colour);
        m.SetFloat("_Metallic", metallic);
        m.SetFloat("_Smoothness", smoothness);
        return m;
    }
}
