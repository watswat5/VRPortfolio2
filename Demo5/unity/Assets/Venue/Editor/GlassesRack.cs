// A sunglasses rack on the dressing room shelf's right side, by the mirror: a black board up the side panel with a rod per pair sticking out of it, each pair hung on its rod by the bridge, temples back toward the board,
// lenses facing out over the front of the mirror (which is flat on the wall, so there's room). The five from art/glasses/make_glasses.py (Assets/Models/Glasses), each an Eyes accessory.
// Works on the open scene in place: finds the shelf by its Accessories marker (wherever it's been moved to), (re)makes
// the "Glasses Rack" group under it, and moves glasses already in the scene onto it rather than adding more. Undoable.
// Freestyle > Build Glasses Rack.
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Freestyle.Venue
{
    public static class GlassesRack
    {
        const string Dir = "Assets/Models/Glasses", Group = "Glasses Rack", Undo_ = "Build glasses rack";
        // top to bottom, with where each one's bridge starts above its lenses' middle (the rod sits just under it)
        static readonly (string model, float bridge)[] Pairs =
        {
            ("Black_Sunglasses", 0.012f), ("Brown_Intellectual_Glasses", 0.010f), ("Purple_Shutter_Shades", 0.012f),
            ("Pink_Heart_Glasses", 0.006f), ("Gold_Star_Glasses", 0.003f),
        };
        const float ShelfW = 0.9f, ShelfD = 0.38f;  // as VenueBuilder's shelf: its width, and its depth front to wall
        const float TopRod = 1.48f, RodGap = 0.22f, RodLen = 0.17f, RodR = 0.006f;
        const float GlassesOut = 0.155f;            // the lenses' plane, out from the side panel (the temples reach back 0.135)

        [MenuItem("Freestyle/Build Glasses Rack")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Build glasses rack", "Stop Play mode first.", "OK"); return; }
            var scene = EditorSceneManager.GetActiveScene();
            var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToList();
            var shelf = all.FirstOrDefault(t => t.name == "Accessories" && t.Find("Board0"));
            if (!shelf) { EditorUtility.DisplayDialog("Build glasses rack", "No shelf (its Accessories marker) in the open scene.", "OK"); return; }

            // glasses already in the scene, by model: these go on the rack (the first of each)
            var existing = new Dictionary<string, GameObject>();
            foreach (var t in all)
            {
                if (!t || !PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
                if (path == null || !path.StartsWith(Dir + "/")) continue;
                string model = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!existing.ContainsKey(model)) existing[model] = t.gameObject;
            }
            foreach (var g in existing.Values)   // off the old rack (if they're on it) before it goes
                if (g.transform.parent && g.transform.parent.name == Group) Undo.SetTransformParent(g.transform, null, Undo_);
            foreach (var old in shelf.GetComponentsInChildren<Transform>(true).Where(t => t && t.name == Group).ToList())
                Undo.DestroyObjectImmediate(old.gameObject);

            var rack = new GameObject(Group).transform;
            Undo.RegisterCreatedObjectUndo(rack.gameObject, Undo_);
            rack.SetParent(shelf, false);
            // the shelf's frame: origin at its front left corner, +x toward the mirror, +z into the wall; the rack is on
            // its right end (x = ShelfW), halfway back, facing the mirror's side (its own -x is the shelf's +x)
            rack.localPosition = new Vector3(ShelfW, 0, ShelfD / 2);
            rack.localRotation = Quaternion.Euler(0, 180, 0);

            var black = AssetDatabase.LoadAssetAtPath<Material>("Assets/Venue/Generated/BarBlack.mat");
            var metal = AssetDatabase.LoadAssetAtPath<Material>("Assets/Venue/Generated/Steel.mat");
            float bottom = TopRod - (Pairs.Length - 1) * RodGap;
            Block(rack, "Board", PrimitiveType.Cube, new Vector3(-0.009f, (TopRod + bottom) / 2 + 0.02f, 0),
                  new Vector3(0.018f, TopRod - bottom + 0.22f, 0.13f), Quaternion.identity, black);
            Block(rack, "Cap", PrimitiveType.Cube, new Vector3(-0.012f, TopRod + 0.14f, 0), new Vector3(0.026f, 0.02f, 0.15f),
                  Quaternion.identity, black);

            for (int i = 0; i < Pairs.Length; i++)
            {
                var (model, bridge) = Pairs[i];
                float y = TopRod - i * RodGap;
                Block(rack, $"Rod {i}", PrimitiveType.Cylinder, new Vector3(-RodLen / 2, y, 0), new Vector3(RodR * 2, RodLen / 2, RodR * 2),
                      Quaternion.Euler(0, 0, 90), metal);
                Block(rack, $"Knob {i}", PrimitiveType.Sphere, new Vector3(-RodLen, y, 0), Vector3.one * 0.02f, Quaternion.identity, metal);

                if (!existing.TryGetValue(model, out var g))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/{model}.fbx");
                    if (!asset) { Debug.LogWarning($"No {Dir}/{model}.fbx: run art/glasses/make_glasses.py"); continue; }
                    g = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
                    Undo.RegisterCreatedObjectUndo(g, Undo_);
                }
                Undo.SetTransformParent(g.transform, rack, Undo_);
                Undo.RecordObject(g.transform, Undo_);
                // lenses out (-x), temples back toward the board, hung by the bridge on the rod
                g.transform.localPosition = new Vector3(-GlassesOut, y + RodR - bridge, 0);
                g.transform.localRotation = Quaternion.LookRotation(Vector3.left);
                g.transform.localScale = Vector3.one;
                AccessoryTools.MakeGlasses(g);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = rack.gameObject;
            Debug.Log($"Glasses rack built on {shelf.parent?.name}/{shelf.name}'s right side, by the mirror ({existing.Count} pairs moved from the scene). Save the scene.");
        }

        static void Block(Transform parent, string name, PrimitiveType type, Vector3 at, Vector3 size, Quaternion rot, Material mat)
        {
            var b = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(b.GetComponent<Collider>());   // the hands reach the glasses, not the rack
            b.name = name;
            b.transform.SetParent(parent, false);
            b.transform.localPosition = at;
            b.transform.localRotation = rot;
            b.transform.localScale = size;
            if (mat) b.GetComponent<MeshRenderer>().sharedMaterial = mat;
            Undo.RegisterCreatedObjectUndo(b, Undo_);
        }
    }
}
