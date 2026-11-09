// Turns the selected objects into accessories (Accessory: an XR grab interactable, a kinematic Rigidbody, its body
// location and the plain description the opponent hears). For the hats from art/hats/convert_hats.py the model's
// pivot is already the middle of the hat's opening, front forward, which is what a hat socket wants, so no attach
// point is made; anything else gets one at the bottom of its bounds to adjust. Undoable.
// Hats are single shells, so their materials are made double-sided (Render Face: Both) or the inside is see-through;
// a material embedded in its model is extracted next to it first (Materials/), so every copy of the model gets it.
// Freestyle > Make Selected Into Hats, Freestyle > Make Selected Into Glasses (art/glasses/make_glasses.py's: the pivot
// is the lenses' middle, which is what the Eyes spot wants), Freestyle > Make Selected Into Spray Cans (art/spraycan's
// can: a collider, a SprayCan, and its own label material to colour), Freestyle > Make Selected Double-Sided.
using System.Collections.Generic;
using System.Linq;
using Freestyle.Wardrobe;
using UnityEditor;
using UnityEngine;

public static class AccessoryTools
{
    // the converted hats, by model name: what the opponent will call them
    static readonly Dictionary<string, string> Hats = new()
    {
        ["tophat"] = "a black top hat",
        ["military_hat"] = "a military officer's cap",
        ["cowboy_hat"] = "a cowboy hat",
        ["hiphop_hat"] = "a flat-brim snapback cap",
        ["leaderhut"] = "a pirate tricorne hat",
        ["clown_wig"] = "a rainbow clown wig",   // art/hats/make_clown_wig.py: flat underneath, worn like a hat
    };

    internal static readonly Dictionary<string, string> Glasses = new()
    {
        ["black_sunglasses"] = "a pair of black sunglasses",
        ["pink_heart_glasses"] = "pink heart-shaped glasses",
        ["gold_star_glasses"] = "gold star-shaped glasses",
        ["purple_shutter_shades"] = "purple shutter shades",
        ["brown_intellectual_glasses"] = "brown round nerd glasses",
    };

    [MenuItem("Freestyle/Make Selected Into Glasses")]
    static void MakeGlasses()
    {
        foreach (var go in Selection.gameObjects) MakeGlasses(go);
    }

    /// <summary>One pair of glasses as an Eyes accessory (with its description, for ours). Undoable.</summary>
    internal static void MakeGlasses(GameObject go)
    {
        var model = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
        string key = (model ? model.name : go.name).ToLowerInvariant();
        if (!go.GetComponentInChildren<Collider>()) Undo.AddComponent<BoxCollider>(go);
        var rb = go.GetComponent<Rigidbody>();
        if (rb) { Undo.RecordObject(rb, "Make glasses"); rb.isKinematic = true; rb.useGravity = false; }
        var acc = go.GetComponent<Accessory>() ? go.GetComponent<Accessory>() : Undo.AddComponent<Accessory>(go);
        Undo.RecordObject(acc, "Make glasses");
        acc.location = BodyLocation.Eyes;
        if (Glasses.TryGetValue(key, out var d) && (acc.description == "a hat" || string.IsNullOrEmpty(acc.description))) acc.description = d;
        EditorUtility.SetDirty(acc);
        Debug.Log($"{go.name}: glasses, \"{acc.description}\"" + (Glasses.ContainsKey(key) ? "" : " (not one of ours: check its pivot sits on the eyes)"));
    }

    internal const string Labels = "Assets/Models/spray_can/Labels";

    [MenuItem("Freestyle/Make Selected Into Spray Cans")]
    static void MakeSprayCans()
    {
        foreach (var go in Selection.gameObjects) MakeSprayCan(go);
    }

    /// <summary>One can: a collider for the hands, a SprayCan wired to its Label and Nozzle, and a label material of its
    /// own (`label` if given, else a copy of the model's in Models/spray_can/Labels to recolour). Undoable.</summary>
    internal static SprayCan MakeSprayCan(GameObject go, Material label = null)
    {
        if (!go.GetComponentInChildren<Collider>())   // round the can, for the hands (the model is 6.6 cm x 21 cm)
        {
            var col = Undo.AddComponent<CapsuleCollider>(go);
            col.radius = 0.034f; col.height = 0.21f; col.center = new Vector3(0, 0.105f, 0);
        }
        var can = go.GetComponent<SprayCan>() ? go.GetComponent<SprayCan>() : Undo.AddComponent<SprayCan>(go);
        Undo.RecordObject(can, "Make spray cans");
        if (!can.label) can.label = go.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == "Label");
        if (!can.nozzle) can.nozzle = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Nozzle");
        can.mistMaterial = MistMaterial();
        if (can.label && (label || can.label.sharedMaterial && AssetDatabase.GetAssetPath(can.label.sharedMaterial).EndsWith(".fbx")))
        {
            // its own label material (one in the model can't be edited): set its Base Color to the paint's colour
            if (!label)
            {
                label = new Material(can.label.sharedMaterial);
                AssetDatabase.CreateAsset(label, AssetDatabase.GenerateUniqueAssetPath($"{LabelFolder()}/Label {go.name}.mat"));
                Debug.Log($"{go.name}: its label is {AssetDatabase.GetAssetPath(label)}: set its Base Color to the paint's colour", label);
            }
            Undo.RecordObject(can.label, "Make spray cans");
            can.label.sharedMaterial = label;
        }
        EditorUtility.SetDirty(can);
        Debug.Log($"{go.name}: a spray can, painting {ColorNames.Nearest(can.PaintColor)}", go);
        return can;
    }

    /// <summary>The spray's mist: URP's particle shader, see-through, a soft dot (Models/spray_can/SprayMist.mat, made
    /// once). An asset the cans point at, so the shader is in the build (made in code it's stripped: magenta).</summary>
    internal static Material MistMaterial()
    {
        const string path = "Assets/Models/spray_can/SprayMist.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")) { name = "SprayMist" };
        m.SetTexture("_BaseMap", AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd"));
        m.SetColor("_BaseColor", Color.white);
        m.SetFloat("_Surface", 1f);   // transparent
        m.SetFloat("_Blend", 0f);     // alpha
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    /// <summary>Models/spray_can/Labels, made if it isn't there.</summary>
    internal static string LabelFolder()
    {
        if (!AssetDatabase.IsValidFolder(Labels)) AssetDatabase.CreateFolder("Assets/Models/spray_can", "Labels");
        return Labels;
    }

    [MenuItem("Freestyle/Make Selected Into Spray Cans", true)]
    static bool CanMakeSprayCans() => Selection.gameObjects.Length > 0 && !EditorApplication.isPlaying;

    [MenuItem("Freestyle/Make Selected Into Hats")]
    static void MakeHats()
    {
        foreach (var go in Selection.gameObjects)
        {
            var model = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
            string key = (model ? model.name : go.name).ToLowerInvariant();
            bool converted = Hats.ContainsKey(key);

            if (!go.GetComponentInChildren<Collider>()) Undo.AddComponent<BoxCollider>(go);   // hands need something to touch
            var rb = go.GetComponent<Rigidbody>();
            if (rb)
            {
                Undo.RecordObject(rb, "Make hats");
                rb.isKinematic = true; rb.useGravity = false;   // (Accessory does this too, at runtime)
            }
            var acc = go.GetComponent<Accessory>() ? go.GetComponent<Accessory>() : Undo.AddComponent<Accessory>(go);
            Undo.RecordObject(acc, "Make hats");
            acc.location = BodyLocation.Hat;
            if (acc.description == "a hat" && converted) acc.description = Hats[key];
            if (!converted && !acc.attachPoint)
            {
                // not one of ours: an attach point at the bottom middle of it to line up by hand (inside the crown, up and
                // forward like the head)
                var b = go.GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((x, y) => { x.Encapsulate(y); return x; });
                var ap = new GameObject("Attach Point").transform;
                Undo.RegisterCreatedObjectUndo(ap.gameObject, "Make hats");
                ap.SetParent(go.transform, false);
                ap.position = new Vector3(b.center.x, b.min.y, b.center.z);
                acc.attachPoint = ap;
            }
            EditorUtility.SetDirty(acc);
            DoubleSided(go);
            Debug.Log($"{go.name}: a hat accessory, \"{acc.description}\"" + (converted ? "" : " (check its Attach Point)"));
        }
    }

    [MenuItem("Freestyle/Make Selected Into Hats", true)]
    [MenuItem("Freestyle/Make Selected Double-Sided", true)]
    [MenuItem("Freestyle/Make Selected Into Glasses", true)]
    static bool CanMakeHats() => Selection.gameObjects.Length > 0 && !EditorApplication.isPlaying;

    [MenuItem("Freestyle/Make Selected Double-Sided")]
    static void MakeDoubleSided()
    {
        foreach (var go in Selection.gameObjects) DoubleSided(go);
    }

    /// <summary>Every material on the object (and its children) drawn from both sides. Ones inside a model file are
    /// extracted into an editable .mat beside it first (the model then uses that one everywhere).</summary>
    static void DoubleSided(GameObject go)
    {
        var mats = go.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m).Distinct().ToList();
        foreach (var m in mats)
        {
            var mat = Editable(m);
            if (!mat) continue;
            if (!mat.HasProperty("_Cull")) { Debug.LogWarning($"{mat.name} ({mat.shader.name}) has no Render Face setting: not changed", mat); continue; }
            if (mat.GetFloat("_Cull") == 0f) continue;
            Undo.RecordObject(mat, "Make double-sided");
            mat.SetFloat("_Cull", 0f);   // UnityEngine.Rendering.CullMode.Off: Render Face "Both"
            mat.doubleSidedGI = true;
            EditorUtility.SetDirty(mat);
            Debug.Log($"{mat.name}: double-sided", mat);
        }
        AssetDatabase.SaveAssets();
    }

    /// <summary>The material as an editable asset: itself if it's a .mat, else extracted out of its model.</summary>
    static Material Editable(Material m)
    {
        string path = AssetDatabase.GetAssetPath(m);
        if (string.IsNullOrEmpty(path) || path.EndsWith(".mat")) return m;   // (a scene-only material is editable too)
        if (AssetImporter.GetAtPath(path) is not ModelImporter importer) { Debug.LogWarning($"{m.name}: can't edit (in {path})", m); return null; }
        string dir = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(dir + "/Materials")) AssetDatabase.CreateFolder(dir, "Materials");
        string name = string.Concat(m.name.Split(System.IO.Path.GetInvalidFileNameChars()));
        string to = $"{dir}/Materials/{System.IO.Path.GetFileNameWithoutExtension(path)}_{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(to);
        if (!existing)
        {
            string err = AssetDatabase.ExtractAsset(m, to);
            if (!string.IsNullOrEmpty(err)) { Debug.LogWarning($"{m.name}: couldn't extract from {path}: {err}", m); return null; }
            existing = AssetDatabase.LoadAssetAtPath<Material>(to);
        }
        // the model uses the extracted one from now on (in every scene and prefab)
        importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), m.name), existing);
        importer.SaveAndReimport();
        return existing;
    }
}
