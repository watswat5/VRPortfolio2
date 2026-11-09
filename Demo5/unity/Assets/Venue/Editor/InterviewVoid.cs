// The pre-show interview's place: a void (a black floor under a black dome, so nothing else shows), well away from the
// venue. A spotlight on where the player stands and one on the interviewer 1.8 m in front of them, plus two soft fill
// lights (cool from one side, warm from the other) so the shadow sides of faces and bodies aren't lost. A MoveArea
// keeps the player in their light. Its InterviewDirector gets the interviewer prefab and her pre-rendered lines
// (Characters/Interviewer/Lines, from backend/tools/render_interviewer.py) and is given to the stage door.
// Works on the open scene in place: (re)makes the "Interview Void" group, nothing else is touched (but the door's
// interview reference). Undoable.
// Freestyle > Build Interview Void.
using System.Linq;
using Freestyle.Avatar;
using Freestyle.Game;
using Freestyle.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Freestyle.Venue
{
    public static class InterviewVoid
    {
        const string Group = "Interview Void", Undo_ = "Build interview void", Lines = "Assets/Characters/Interviewer/Lines";
        static readonly Vector3 Origin = new(-40f, 0f, 0f);   // where the player stands: far off to the venue's side
        const float Apart = 1.8f;                            // player to interviewer

        [MenuItem("Freestyle/Build Interview Void")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Build interview void", "Stop Play mode first.", "OK"); return; }
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == Group).ToList()) Undo.DestroyObjectImmediate(old);

            var root = new GameObject(Group).transform;
            Undo.RegisterCreatedObjectUndo(root.gameObject, Undo_);
            root.position = Origin;

            // the void: a black floor (the spotlights' pools show on it) under a black dome seen from inside
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            floor.name = "Floor";
            floor.transform.SetParent(root, false);
            floor.transform.localPosition = new Vector3(0, -0.01f, Apart / 2);
            floor.transform.localScale = new Vector3(24, 0.01f, 24);
            floor.GetComponent<Renderer>().sharedMaterial = Mat("InterviewFloor", new Color(0.035f, 0.035f, 0.04f), lit: true);
            // stand on it: a cylinder's own collider is a capsule, which squashed this flat becomes a 12 m ball (you'd be
            // inside it, with nothing to stand on); a box instead, its top flush with the floor's, 2 m deep
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            var ground = floor.AddComponent<BoxCollider>();
            ground.center = new Vector3(0, -99f, 0);    // (the cylinder's local top is y = 1; 0.01 m a unit here)
            ground.size = new Vector3(1, 200f, 1);
            var dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dome.name = "Dome";
            Object.DestroyImmediate(dome.GetComponent<Collider>());
            dome.transform.SetParent(root, false);
            dome.transform.localPosition = new Vector3(0, 0, Apart / 2);
            dome.transform.localScale = Vector3.one * 30;
            var d = dome.GetComponent<Renderer>();
            d.sharedMaterial = Mat("InterviewDome", Color.black, lit: false);
            d.shadowCastingMode = ShadowCastingMode.Off;

            var player = Spot(root, "Player Spot", Vector3.zero, Vector3.forward);
            var her = Spot(root, "Interviewer Spot", new Vector3(0, 0, Apart), Vector3.back);

            // a spotlight on each (from above and a little in front of them), and soft fill for the shadow sides
            SpotLight(root, "Player Spotlight", new Vector3(0, 4.2f, 0.7f), new Vector3(0, 1.1f, 0), new Color(1f, 0.95f, 0.86f));
            SpotLight(root, "Interviewer Spotlight", new Vector3(0, 4.2f, Apart - 0.7f), new Vector3(0, 1.1f, Apart), new Color(1f, 0.95f, 0.86f));
            Fill(root, "Fill (cool)", new Vector3(-2.0f, 1.7f, Apart / 2), new Color(0.55f, 0.66f, 1f), 0.9f);
            Fill(root, "Fill (warm)", new Vector3(2.0f, 1.4f, Apart / 2 + 0.4f), new Color(1f, 0.74f, 0.58f), 0.6f);

            var area = new GameObject("Interview Move Area");
            area.transform.SetParent(root, false);
            area.transform.localPosition = new Vector3(0, 1.2f, 0.1f);
            area.AddComponent<MoveArea>().size = new Vector3(1.8f, 2.5f, 1.4f);   // your pool of light, not hers

            var director = root.gameObject.AddComponent<InterviewDirector>();
            director.playerSpot = player;
            director.interviewerSpot = her;
            director.interviewer = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/interviewer.prefab")?.GetComponent<Performer>();
            director.fadeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Venue/ScreenFade.shader");
            director.battle = Object.FindAnyObjectByType<Freestyle.Battle.BattleCoordinator>();
            int lines = FillLines(director);
            if (!director.interviewer) Debug.LogWarning("No Assets/Characters/interviewer.prefab: run Freestyle > Build Character Prefabs");

            foreach (var door in Object.FindObjectsByType<StageDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Undo.RecordObject(door, Undo_);
                door.interview = director;
                PrefabUtility.RecordPrefabInstancePropertyModifications(door);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = root.gameObject;
            Debug.Log($"Interview void built at {Origin} with {lines} of her lines. Save the scene.");
        }

        /// <summary>Her lines from Characters/Interviewer/Lines: lines.json's ids and text, each id's .wav and .json.</summary>
        static int FillLines(InterviewDirector director)
        {
            var script = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Lines}/lines.json");
            if (!script) { Debug.LogWarning($"No {Lines}/lines.json: run backend/tools/render_interviewer.py (her lines will be subtitles only)"); return 0; }
            var (intro, questions, affirmations, outro, text) = InterviewDirector.ReadScript(script.text);
            int n = 0;
            InterviewDirector.Line L(string id)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Lines}/{id}.wav");
                if (clip) n++;
                return new InterviewDirector.Line
                {
                    id = id, text = text.TryGetValue(id, out var t) ? t : id, clip = clip,
                    cues = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Lines}/{id}.json"),
                };
            }
            director.intro = intro.Length > 0 ? L(intro[0]) : null;
            director.questions = questions.Select(L).ToArray();
            director.affirmations = affirmations.Select(L).ToArray();
            director.outro = outro.Length > 0 ? L(outro[0]) : null;
            return n;
        }

        static Transform Spot(Transform root, string name, Vector3 at, Vector3 facing)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root, false);
            t.localPosition = at;
            t.localRotation = Quaternion.LookRotation(facing);
            return t;
        }

        static void SpotLight(Transform root, string name, Vector3 at, Vector3 aim, Color colour)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = at;
            go.transform.rotation = Quaternion.LookRotation(aim - at);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.color = colour;
            l.intensity = 14f;
            l.range = 9f;
            l.spotAngle = 40f;
            l.innerSpotAngle = 18f;
            l.shadows = LightShadows.Soft;
            l.shadowStrength = 0.85f;
        }

        static void Fill(Transform root, string name, Vector3 at, Color colour, float intensity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = at;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = colour;
            l.intensity = intensity;
            l.range = 5f;
            l.shadows = LightShadows.None;
        }

        /// <summary>Venue/Generated/<name>.mat: lit (matte) or unlit; the dome's drawn from inside.</summary>
        static Material Mat(string name, Color colour, bool lit)
        {
            string path = $"Assets/Venue/Generated/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m) return m;
            m = new Material(Shader.Find(lit ? "Universal Render Pipeline/Lit" : "Universal Render Pipeline/Unlit")) { name = name };
            m.SetColor("_BaseColor", colour);
            if (lit) m.SetFloat("_Smoothness", 0.12f);
            else m.SetFloat("_Cull", (float)CullMode.Front);   // inside the sphere
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
