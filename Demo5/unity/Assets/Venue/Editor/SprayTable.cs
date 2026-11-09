// A small table against the dressing room wall under the song list (the SongSelector's TV), with a row of spray cans
// on it in the rainbow: red, orange, yellow, green, blue, violet (art/spraycan's can; each a SprayCan whose colour is
// its label's material, Models/spray_can/Labels/Label <Colour>.mat, made once and then left as you set it).
// Works on the open scene in place: finds the song list wherever it's been moved, (re)makes the "Spray Table" group beside
// it (table and cans; not under it: the list puts everything under it on the UI layer). Undoable.
// Freestyle > Build Spray Table.
using System.Linq;
using Freestyle.Wardrobe;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Freestyle.Venue
{
    public static class SprayTable
    {
        const string Group = "Spray Table", Undo_ = "Build spray table", Can = "Assets/Models/spray_can/spray_can.fbx";
        static readonly (string name, Color colour)[] Rainbow =
        {
            ("Red", new Color(0.9f, 0.08f, 0.1f)), ("Orange", new Color(1f, 0.48f, 0.05f)), ("Yellow", new Color(1f, 0.86f, 0.1f)),
            ("Green", new Color(0.12f, 0.72f, 0.25f)), ("Blue", new Color(0.1f, 0.33f, 0.95f)), ("Violet", new Color(0.55f, 0.18f, 0.85f)),
        };
        const float TopY = 0.70f, Depth = 0.42f, Width = 0.9f, Thick = 0.03f, Leg = 0.035f, CanGap = 0.115f;

        [MenuItem("Freestyle/Build Spray Table")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Build spray table", "Stop Play mode first.", "OK"); return; }
            var scene = EditorSceneManager.GetActiveScene();
            var songs = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<SongSelector>(true)).FirstOrDefault();
            if (!songs) { EditorUtility.DisplayDialog("Build spray table", "No song list (SongSelector) in the open scene: run Set Up Battle.", "OK"); return; }
            var canModel = AssetDatabase.LoadAssetAtPath<GameObject>(Can);
            if (!canModel) { EditorUtility.DisplayDialog("Build spray table", $"No {Can}: run art/spraycan/make_spray_can.py.", "OK"); return; }

            foreach (var old in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                         .Where(t => t && t.name == Group).ToList())
                if (old) Undo.DestroyObjectImmediate(old.gameObject);

            // the song list faces out of the wall along -forward (its canvas reads from in front); the table stands on the
            // floor below it, its back to the wall
            var wall = songs.transform;
            Vector3 outOf = -Vector3.ProjectOnPlane(wall.forward, Vector3.up).normalized;
            var table = new GameObject(Group).transform;
            Undo.RegisterCreatedObjectUndo(table.gameObject, Undo_);
            table.SetParent(wall.parent, false);
            table.SetPositionAndRotation(new Vector3(wall.position.x, 0, wall.position.z) + outOf * (0.04f + Depth / 2),
                                         Quaternion.LookRotation(outOf));   // its +z out into the room, +x along the wall

            var wood = AssetDatabase.LoadAssetAtPath<Material>("Assets/Venue/Generated/BarTop.mat");
            var steel = AssetDatabase.LoadAssetAtPath<Material>("Assets/Venue/Generated/SteelBlack.mat")
                        ?? AssetDatabase.LoadAssetAtPath<Material>("Assets/Venue/Generated/Steel.mat");
            Block(table, "Top", new Vector3(0, TopY - Thick / 2, 0), new Vector3(Width, Thick, Depth), wood, true);
            Block(table, "Apron", new Vector3(0, TopY - Thick - 0.04f, Depth / 2 - 0.03f), new Vector3(Width - 0.1f, 0.08f, 0.02f), wood, false);
            foreach (float x in new[] { -1f, 1f })
                foreach (float z in new[] { -1f, 1f })
                    Block(table, "Leg", new Vector3(x * (Width / 2 - 0.05f), (TopY - Thick) / 2, z * (Depth / 2 - 0.05f)),
                          new Vector3(Leg, TopY - Thick, Leg), steel, false);

            // the cans: a row along the table, in a gentle arc toward the front, nozzles to the room
            for (int i = 0; i < Rainbow.Length; i++)
            {
                var (name, colour) = Rainbow[i];
                float x = ((Rainbow.Length - 1) / 2f - i) * CanGap;   // the table's +x is your right-to-left: red on the left
                float z = 0.02f + 0.05f * (1 - Mathf.Pow(x / (CanGap * 3), 2));
                var go = (GameObject)PrefabUtility.InstantiatePrefab(canModel, scene);
                Undo.RegisterCreatedObjectUndo(go, Undo_);
                go.name = $"Spray Can {name}";
                go.transform.SetParent(table, false);
                go.transform.localPosition = new Vector3(x, TopY, z);
                go.transform.localRotation = Quaternion.Euler(0, (i - 2.5f) * 4f, 0);   // fanned a little toward the middle
                AccessoryTools.MakeSprayCan(go, Label(name, colour));
            }
            var boombox = BoomboxBuilder.Build();   // (it stood on the old table)
            if (boombox != null) Debug.LogWarning("No boombox on the spray table: " + boombox);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = table.gameObject;
            Debug.Log("Spray table built under the song list. Save the scene.");
        }

        /// <summary>The colour's label material: made the first time (from the model's), afterwards whatever it's been set to.</summary>
        static Material Label(string name, Color colour)
        {
            string path = $"{AccessoryTools.LabelFolder()}/Label {name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat) return mat;
            var model = AssetDatabase.LoadAllAssetsAtPath(Can).OfType<Material>().FirstOrDefault(m => m.name == "Label");
            mat = model ? new Material(model) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.name = $"Label {name}";
            mat.SetColor("_BaseColor", colour);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void Block(Transform parent, string name, Vector3 at, Vector3 size, Material mat, bool collider)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (!collider) Object.DestroyImmediate(b.GetComponent<Collider>());   // the top holds; the rest is for show
            b.name = name;
            b.transform.SetParent(parent, false);
            b.transform.localPosition = at;
            b.transform.localScale = size;
            if (mat) b.GetComponent<MeshRenderer>().sharedMaterial = mat;
            Undo.RegisterCreatedObjectUndo(b, Undo_);
        }
    }
}
