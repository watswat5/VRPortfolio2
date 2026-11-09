// The DJ's headphones as a prefab: Assets/Models/headphones/Headphones.prefab, from art/headphones/make_headphones.py's
// model, each part given its URP material (matte black shell, dark leatherette pads, steel sliders and yokes, a red
// accent ring on each cup). No colliders. Wear(): on a character's head, fitted to its ears (Build Character Prefabs
// puts them on the DJ).
// Freestyle > Build Headphones (Build Character Prefabs makes them too, if they aren't there).
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class HeadphonesBuilder
{
    const string Dir = "Assets/Models/headphones", Mats = Dir + "/Materials";
    public const string PrefabPath = Dir + "/Headphones.prefab";
    const float ModelEar = 0.092f;   // the model's ears, either side of its middle (make_headphones.py EAR)

    [MenuItem("Freestyle/Build Headphones")]
    static void Build() => Ensure(true);

    /// <summary>The headphones prefab, made first if it isn't there (or remade).</summary>
    public static GameObject Ensure(bool rebuild = false)
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (existing && !rebuild) return existing;
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/headphones.fbx");
        if (!model) { Debug.LogWarning($"No {Dir}/headphones.fbx: run art/headphones/make_headphones.py"); return existing; }
        if (!AssetDatabase.IsValidFolder(Mats)) AssetDatabase.CreateFolder(Dir, "Materials");
        var shell = Lit("Headphones Shell", new Color(0.05f, 0.05f, 0.055f), 0f, 0.4f);
        var pad = Lit("Headphones Pad", new Color(0.11f, 0.11f, 0.12f), 0f, 0.2f);
        var metal = Lit("Headphones Metal", new Color(0.72f, 0.72f, 0.75f), 1f, 0.7f);
        var accent = Lit("Headphones Accent", new Color(0.95f, 0.15f, 0.2f), 0f, 0.55f);
        foreach (var m in new[] { shell, pad, metal, accent }) EditorUtility.SetDirty(m);

        var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
        {
            r.sharedMaterial = r.name switch { "Shell" => shell, "Pad" => pad, "Metal" => metal, "Accent" => accent, _ => r.sharedMaterial };
            r.shadowCastingMode = ShadowCastingMode.On;
        }
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
        Object.DestroyImmediate(go);
        AssetDatabase.SaveAssets();
        Debug.Log($"Headphones prefab: {PrefabPath}");
        return prefab;
    }

    /// <summary>The headphones on a character's head (a child of its head bone), over its ears: found from the head's
    /// own vertices in the bind pose (the widest part of the head, a little above halfway up it), scaled to the head's
    /// width. The character is upright, facing +Z.</summary>
    public static GameObject Wear(GameObject character, SkinnedMeshRenderer face, Transform head)
    {
        var prefab = Ensure();
        if (!prefab || !face || !head) return null;
        var mesh = new Mesh();
        face.BakeMesh(mesh, true);
        var weights = face.sharedMesh.boneWeights;
        int hi = System.Array.IndexOf(face.bones, head);
        var verts = mesh.vertices.Select((v, i) => (p: face.transform.TransformPoint(v), w: weights[i]))
            .Where(x => x.w.boneIndex0 == hi && x.w.weight0 > 0.5f).Select(x => character.transform.InverseTransformPoint(x.p)).ToArray();
        Object.DestroyImmediate(mesh);
        if (verts.Length < 50) { Debug.LogWarning($"{character.name}: couldn't find the head to put the headphones on"); return null; }
        float lo = verts.Min(v => v.y), top = verts.Max(v => v.y), earY = lo + (top - lo) * 0.55f;
        var band = verts.Where(v => Mathf.Abs(v.y - earY) < 0.02f).ToArray();
        float minX = band.Min(v => v.x), maxX = band.Max(v => v.x), half = (maxX - minX) / 2;
        var sides = band.Where(v => Mathf.Abs(v.x - (minX + maxX) / 2) > half * 0.9f).ToArray();   // the ears' side of the head
        var ears = new Vector3((minX + maxX) / 2, earY, sides.Average(v => v.z));

        var hp = (GameObject)PrefabUtility.InstantiatePrefab(prefab, head);
        hp.name = "Headphones";
        hp.transform.SetPositionAndRotation(character.transform.TransformPoint(ears), character.transform.rotation);
        hp.transform.localScale = Vector3.one * Mathf.Clamp(half / ModelEar, 0.85f, 1.2f) / Mathf.Max(0.001f, head.lossyScale.x);
        return hp;
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
