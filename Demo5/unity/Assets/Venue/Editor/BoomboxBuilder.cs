// Puts the boombox (art/boombox's model, Models/boombox/boombox.fbx) on the dressing room's spray table, at the back
// behind the cans and under the song list's screen, with its Boombox (the song list's previews), a collider (cans
// land on it) and its display in an unlit material asset (so it glows, and its shader ships in builds). The song
// list is pointed at it.
// Works on the open scene in place: (re)makes the "Boombox" under the "Spray Table". Build Spray Table calls it too
// (rebuilding the table takes it away). Undoable. Freestyle > Build Boombox.
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Freestyle.Venue
{
    public static class BoomboxBuilder
    {
        const string Name = "Boombox", Undo_ = "Build boombox", Model = "Assets/Models/boombox/boombox.fbx",
                     DisplayMat = "Assets/Models/boombox/Boombox Display.mat";
        // on the table's top (SprayTable.TopY), its back half: the cans stand in the front half
        static readonly Vector3 OnTable = new(0f, 0.70f, -0.13f);

        [MenuItem("Freestyle/Build Boombox")]
        public static void BuildMenu()
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Build boombox", "Stop Play mode first.", "OK"); return; }
            var error = Build();
            if (error != null) EditorUtility.DisplayDialog("Build boombox", error, "OK");
        }

        /// <summary>Builds it in the open scene; the reason it couldn't, or null.</summary>
        public static string Build()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToList();
            var table = all.FirstOrDefault(t => t.name == "Spray Table");
            if (!table) return "No \"Spray Table\" in the open scene: run Build Spray Table.";
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Model);
            if (!model) return $"No {Model}: run art/boombox/make_boombox.py.";

            foreach (var old in all.Where(t => t && t.GetComponent<Boombox>()).ToList())
                if (old) Undo.DestroyObjectImmediate(old.gameObject);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
            Undo.RegisterCreatedObjectUndo(go, Undo_);
            go.name = Name;
            go.transform.SetParent(table, false);
            go.transform.SetLocalPositionAndRotation(OnTable, Quaternion.identity);   // the table's +z is the room

            var body = go.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == "Body");
            var box = go.AddComponent<BoxCollider>();
            if (body)
            {
                var b = body.bounds;   // (the handle and antenna included: it's a box to land on)
                box.center = go.transform.InverseTransformPoint(b.center);
                box.size = go.transform.InverseTransformVector(b.size);
                box.size = new Vector3(Mathf.Abs(box.size.x), Mathf.Abs(box.size.y), Mathf.Abs(box.size.z));
            }

            var display = go.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == "Display");
            if (display) display.sharedMaterial = Material();

            var boombox = go.AddComponent<Boombox>();
            boombox.battle = all.Select(t => t.GetComponent<Battle.BattleCoordinator>()).FirstOrDefault(c => c);
            var songs = all.Select(t => t.GetComponent<SongSelector>()).FirstOrDefault(s => s);
            if (songs)
            {
                Undo.RecordObject(songs, Undo_);
                songs.boombox = boombox;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("Boombox built on the spray table. Save the scene.");
            return null;
        }

        /// <summary>The display's material: made the first time, afterwards whatever it's been set to.</summary>
        static Material Material()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(DisplayMat);
            if (mat) return mat;
            mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Boombox Display" };
            mat.SetColor("_BaseColor", new Color(0.03f, 0.06f, 0.06f));
            AssetDatabase.CreateAsset(mat, DisplayMat);
            return mat;
        }
    }
}
