// The DJ behind the decks: a "DJ" group at the venue's Spots/DJSpot with DJHost (the battles' host: the introductions,
// the count-in), the DJ character (Assets/Characters/dj.prefab, from Build Character Prefabs) standing at the table,
// the Decks (what the DJ looks at while working) and the Mic Rest on the table (where the mic lies meanwhile), and the
// lines from Characters/DJ/Lines (backend/tools/render_dj.py). The opponent stage gets the DJ.
// Works on the open scene in place: (re)makes the "DJ" group, nothing else is touched (but the stage's DJ reference).
// Move the parts and rerun: the group is made again from the spot, so tune Stand / the offsets here instead.
// Undoable. Freestyle > Set Up DJ.
using System.Linq;
using Freestyle.Avatar;
using Freestyle.Battle;
using Freestyle.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace Freestyle.Venue
{
    public static class DJSetup
    {
        const string Group = "DJ", Undo_ = "Set up DJ", Lines = "Assets/Characters/DJ/Lines", Prefab = "Assets/Characters/dj.prefab";
        // in DJSpot's frame (forward: toward the table and the room; the venue's DJ table, VenueBuilder)
        static readonly Vector3 Stand = new(0f, 0f, 0.36f);       // where the DJ stands: up to the table (the hands reach the controller)
        static readonly Vector3 Decks = new(0f, 0.98f, 0.80f);    // the controller's platters and mixer
        static readonly Vector3 MicRest = new(0.52f, 0.92f, 0.64f);   // the table top, on the DJ's right (the laptop's on the left)

        [MenuItem("Freestyle/Set Up DJ")]
        public static void SetUp()
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Set up DJ", "Stop Play mode first.", "OK"); return; }
            var scene = EditorSceneManager.GetActiveScene();
            var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToList();
            var spot = all.FirstOrDefault(t => t.name == "DJSpot" && t.parent && t.parent.name == "Spots");
            if (!spot) { EditorUtility.DisplayDialog("Set up DJ", "No Spots/DJSpot in the open scene (the venue's).", "OK"); return; }
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == Group).ToList()) Undo.DestroyObjectImmediate(old);

            var root = new GameObject(Group).transform;
            Undo.RegisterCreatedObjectUndo(root.gameObject, Undo_);
            root.SetPositionAndRotation(spot.position, spot.rotation);

            var decks = Child(root, "Decks", Decks, Quaternion.identity);
            var rest = Child(root, "Mic Rest", MicRest, Quaternion.Euler(0, -25, 0));
            var host = root.gameObject.AddComponent<DJHost>();
            host.decks = decks;
            host.micRest = rest;
            host.battle = Object.FindAnyObjectByType<BattleCoordinator>();
            host.crowd = Object.FindAnyObjectByType<Crowd>();
            host.crowdAudio = Object.FindAnyObjectByType<CrowdAudio>();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
            if (prefab)
            {
                var dj = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
                dj.transform.SetLocalPositionAndRotation(Stand, Quaternion.identity);
                host.dj = dj.GetComponent<Performer>();
            }
            else Debug.LogWarning($"No {Prefab}: run Freestyle > Build Character Prefabs (the DJ is a voice on the PA until then)");

            int clips = FillLines(host);
            foreach (var stage in Object.FindObjectsByType<OpponentStage>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Undo.RecordObject(stage, Undo_);
                stage.dj = host;
                PrefabUtility.RecordPrefabInstancePropertyModifications(stage);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = root.gameObject;
            Debug.Log($"DJ set up at {spot.position} with {clips} lines. Save the scene.");
        }

        static Transform Child(Transform root, string name, Vector3 at, Quaternion rot)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root, false);
            t.SetLocalPositionAndRotation(at, rot);
            return t;
        }

        /// <summary>The lines from Characters/DJ/Lines: lines.json's ids by kind and their text, each id's .wav and .json.</summary>
        static int FillLines(DJHost host)
        {
            var script = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Lines}/lines.json");
            if (!script) { Debug.LogWarning($"No {Lines}/lines.json: run backend/tools/render_dj.py (the DJ's lines will be in the log only)"); return 0; }
            var o = JObject.Parse(script.text);
            var text = o["text"]?.ToObject<System.Collections.Generic.Dictionary<string, string>>() ?? new();
            int n = 0;
            InterviewDirector.Line[] Kind(string k) => (o[k]?.ToObject<string[]>() ?? new string[0]).Select(id =>
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Lines}/{id}.wav");
                if (clip) n++;
                return new InterviewDirector.Line
                {
                    id = id, text = text.TryGetValue(id, out var t) ? t : id, clip = clip,
                    cues = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Lines}/{id}.json"),
                };
            }).ToArray();
            host.greetings = Kind("greetings");
            host.mcIntros = Kind("mcs");
            host.challengers = Kind("challengers");
            host.unnamed = Kind("unnamed");
            host.begins = Kind("begins");
            host.count = Kind("count");
            host.winLeads = Kind("win_leads");
            host.mcWins = Kind("mc_wins");
            host.challengerWins = Kind("challenger_wins");
            host.draws = Kind("draws");
            return n;
        }
    }
}
