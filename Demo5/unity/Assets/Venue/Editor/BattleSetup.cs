// Sets up the battle in the open scene, in place (nothing else in it is touched; run it again to refresh):
//   Battle: a BattleCoordinator (the one already in the scene if there is one), BattleDebugHud (the flat-screen
//     stand-in for a HUD; F1 hides it) and OpponentStage, with the lineup (Assets/Characters, easiest first: the
//     posters' order), the opponent's spot and the dressing room's arrival spot from the venue's Spots.
//   TurnCues on it: the stage's key spot on each MC's mark (the spot light aimed closest at it) and the controllers'
//     haptics show whose turn it is and the count-in.
//   TurnDisplay on it: the player's floor monitor (PlayerMonitor/Screen) shows the bars left (yours turquoise, theirs
//     orange).
//   Crowd: the cutouts on the floor in front of the stage (Assets/Venue/Crowd: a material per type, CrowdCutout;
//     made once, move or resize it and a rerun keeps it).
//   CrowdAudio on the Crowd: the cheering layers mixed by crowd level and the roars for a good bar
//     (Assets/Audio/Crowd, see CREDITS.md).
//   Stage light: the key spots aimed at the MCs' heads (not their chests), wider in the middle, without shadows (caps,
//     hoods and the mic hand were shading the faces); and a soft face light for the opponent from the player's side,
//     on its own light layer, so it shows the face without lighting up the stage (OpponentStage puts each opponent on
//     that layer). The face light is made once: move or tune it and a rerun keeps it.
//   Scoreboard: over the DJ on the stage's back wall, facing the room (made once; move it and a rerun keeps it).
//   Song List: the beats to pick from, on the dressing room's left wall (made once; move it and a rerun keeps it).
//   Mirror Controls: arrows on the mirror's edges for the robot's body (made once), and the mirror stops reflecting
//     the UI layer (the menus would show backwards).
//   Floating Words: the words to use, over the crowd (made once; move or resize it, a rerun keeps it).
//   The stage door: onDark -> OpponentStage.Prepare (the opponent appears while it's black), onArrived ->
//     OpponentStage.Begin (the battle starts).
// Undoable. Freestyle > Set Up Battle.
using System.Linq;
using Freestyle.Avatar;
using Freestyle.Battle;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering.Universal;

namespace Freestyle.Venue
{
    public static class BattleSetup
    {
        const string Undo_ = "Set up battle";

        [MenuItem("Freestyle/Set Up Battle")]
        public static void SetUp()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Set up battle", "Stop Play mode first.", "OK");
                return;
            }
            var scene = EditorSceneManager.GetActiveScene();
            var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToList();
            Transform Spot(string name) => all.FirstOrDefault(t => t.name == name && t.parent && t.parent.name == "Spots");

            var battle = all.Select(t => t.GetComponent<BattleCoordinator>()).FirstOrDefault(b => b);
            if (!battle)
            {
                var go = new GameObject("Battle");
                Undo.RegisterCreatedObjectUndo(go, Undo_);
                battle = go.AddComponent<BattleCoordinator>();
            }
            var host = battle.gameObject;
            Undo.RecordObject(battle, Undo_);
            battle.loadLobbyOnStart = true;      // the songs and opponents are ready by the time you're through the door
            battle.startBattleOnStart = false;   // the door starts it
            if (!host.GetComponent<BattleDebugHud>()) Undo.AddComponent<BattleDebugHud>(host).battle = battle;

            var stage = host.GetComponent<OpponentStage>();
            if (!stage) stage = Undo.AddComponent<OpponentStage>(host);   // (not ??: a missing component is a fake null in the editor)
            Undo.RecordObject(stage, Undo_);
            stage.battle = battle;
            stage.lineup = OpponentPosters.Cast
                .Select(id => AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Characters/{id}.prefab"))
                .Where(p => p).Select(p => p.GetComponent<Performer>()).Where(p => p).ToArray();
            stage.opponentSpot = Spot("OpponentSpot");
            stage.dressingRoom = Spot("BackstageArrival");
            stage.fadeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Venue/ScreenFade.shader");

            var cues = host.GetComponent<TurnCues>();
            if (!cues) cues = Undo.AddComponent<TurnCues>(host);
            Undo.RecordObject(cues, Undo_);
            cues.battle = battle;
            var spots = all.Select(t => t.GetComponent<Light>()).Where(l => l && l.type == LightType.Spot).ToList();
            cues.playerKey = AimedAt(spots, Spot("PlayerSpot"));
            cues.opponentKey = AimedAt(spots, Spot("OpponentSpot"));
            OnTheHead(cues.playerKey, Spot("PlayerSpot"));
            OnTheHead(cues.opponentKey, Spot("OpponentSpot"));
            var faceLight = FaceLight(all, Spot("OpponentSpot"), stage.faceLightLayer);
            stage.faceLight = faceLight;

            var display = host.GetComponent<TurnDisplay>();
            if (!display) display = Undo.AddComponent<TurnDisplay>(host);
            Undo.RecordObject(display, Undo_);
            display.battle = battle;
            display.screen = all.FirstOrDefault(t => t.name == "Screen" && t.parent && t.parent.name == "PlayerMonitor");

            var crowd = all.Select(t => t.GetComponent<Crowd>()).FirstOrDefault(c => c);
            if (!crowd)
            {
                var go = new GameObject("Crowd");
                Undo.RegisterCreatedObjectUndo(go, Undo_);
                // the floor between the bar end of the room and the stage, short of the PA stacks at the stage front
                go.transform.position = new Vector3(0.3f, 0f, 3.6f);
                crowd = go.AddComponent<Crowd>();
                crowd.size = new Vector3(7f, 0f, 6f);   // z 0.6 to 6.6: the front row 1.5 m back from the stage
            }
            Undo.RecordObject(crowd, Undo_);
            crowd.battle = battle;
            crowd.types = CrowdMaterials();
            crowd.headTopPx = 110f;   // the club sheets (art/crowd/club, people-proportioned, no stand): where a head tops out
            if (crowd.height == new Vector2(1.4f, 1.7f)) crowd.height = new Vector2(1.6f, 1.85f);   // (the cartoon's default: now adults)
            var noise = crowd.GetComponent<CrowdAudio>();
            if (!noise) noise = Undo.AddComponent<CrowdAudio>(crowd.gameObject);
            Undo.RecordObject(noise, Undo_);
            noise.battle = battle;
            var layerClips = new[] { "crowd_room", "crowd_soft", "crowd_strong" };
            for (int i = 0; i < noise.layers.Length && i < layerClips.Length; i++)
                if (!noise.layers[i].clip) noise.layers[i].clip = CrowdClip(layerClips[i], loop: true);
            if (noise.roars.Length == 0)
                noise.roars = new[] { CrowdClip("crowd_roar_1", false), CrowdClip("crowd_roar_2", false) }.Where(c => c).ToArray();

            var board = all.Select(t => t.GetComponent<Scoreboard>()).FirstOrDefault(b => b);
            if (!board)
            {
                var go = new GameObject("Scoreboard");
                Undo.RegisterCreatedObjectUndo(go, Undo_);
                // centred over the DJ (who stands behind the table, a little right of the stage's middle), clear of their
                // head and under the curtain track, a little in front of the back curtain
                float djX = (VenueBuilder.StageX0 + VenueBuilder.X1) / 2 + 0.2f;
                go.transform.position = new Vector3(djX, VenueBuilder.StageH + 2.6f, VenueBuilder.StageZ1 - 0.35f);
                board = go.AddComponent<Scoreboard>();
            }
            Undo.RecordObject(board, Undo_);
            board.battle = battle;

            var songs = all.Select(t => t.GetComponent<SongSelector>()).FirstOrDefault(x => x);
            if (!songs)
            {
                var go = new GameObject("Song List");
                Undo.RegisterCreatedObjectUndo(go, Undo_);
                // the left wall (the door, the posters and the mirror have the others), at eye height, facing into the room
                go.transform.SetPositionAndRotation(new Vector3(VenueBuilder.BX0 + 0.03f, 1.45f, 1.75f), Quaternion.LookRotation(Vector3.left));
                songs = go.AddComponent<SongSelector>();
            }
            Undo.RecordObject(songs, Undo_);
            songs.battle = battle;

            var mirror = all.Select(t => t.GetComponent<StereoMirror>()).FirstOrDefault(m => m);
            if (mirror)
            {
                Undo.RecordObject(mirror, Undo_);
                mirror.reflect &= ~(1 << Freestyle.UI.UIKit.Layer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(mirror);
                if (!all.Any(t => t.GetComponent<MirrorControls>()))
                {
                    var go = new GameObject("Mirror Controls");
                    Undo.RegisterCreatedObjectUndo(go, Undo_);
                    go.AddComponent<MirrorControls>().mirror = mirror;
                }
            }

            var floating = all.Select(t => t.GetComponent<FloatingWords>()).FirstOrDefault(f => f);
            if (!floating)
            {
                var go = new GameObject("Floating Words");
                Undo.RegisterCreatedObjectUndo(go, Undo_);
                // over the crowd's heads, between the stage and the middle of the room, under the ducts (3.3 m)
                go.transform.position = new Vector3(0.6f, 2.6f, 3.5f);
                floating = go.AddComponent<FloatingWords>();
                floating.size = new Vector3(6f, 1.1f, 4f);
            }
            Undo.RecordObject(floating, Undo_);
            floating.battle = battle;

            var door = all.Select(t => t.GetComponent<StageDoor>()).FirstOrDefault(d => d);
            if (door)
            {
                Undo.RecordObject(door, Undo_);
                Hook(door.onDark, stage.Prepare);
                Hook(door.onArrived, stage.Begin);
                PrefabUtility.RecordPrefabInstancePropertyModifications(door);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = host;
            string missing = string.Join(", ", new[]
            {
                stage.lineup.Length < OpponentPosters.Cast.Length ? "some character prefabs (Build Character Prefabs)" : null,
                !stage.opponentSpot ? "Spots/OpponentSpot" : null, !stage.dressingRoom ? "Spots/BackstageArrival" : null,
                !door ? "the stage door" : null,
                !cues.playerKey || !cues.opponentKey ? "a key light on each MC" : null,
                !display.screen ? "PlayerMonitor/Screen" : null,
                !mirror ? "the dressing room's mirror" : null,
            }.Where(s => s != null));
            Debug.Log($"Battle set up in {scene.name}: {stage.lineup.Length} opponents" +
                      (missing.Length > 0 ? $"; missing {missing}" : "") + ". Save the scene to keep it.");
        }

        /// <summary>A crowd sound (Assets/Audio/Crowd/name.ogg), imported to play without hitches: the loops kept compressed
        /// in memory, the short roars decompressed.</summary>
        static AudioClip CrowdClip(string name, bool loop)
        {
            string path = $"Assets/Audio/Crowd/{name}.ogg";
            if (AssetImporter.GetAtPath(path) is AudioImporter imp)
            {
                var want = imp.defaultSampleSettings;
                var load = loop ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
                if (want.loadType != load || imp.loadInBackground != loop)
                {
                    want.loadType = load;
                    want.compressionFormat = AudioCompressionFormat.Vorbis;
                    want.quality = 0.6f;
                    imp.defaultSampleSettings = want;
                    imp.loadInBackground = loop;
                    imp.SaveAndReimport();
                }
            }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        /// <summary>A material per crowd type (Assets/Venue/Crowd/crowd_*.png) on Freestyle/CrowdCutout (unlit,
        /// alpha-clipped, hazed with distance), the sheets imported so the figures keep their shape when they're small.</summary>
        static Material[] CrowdMaterials()
        {
            const string dir = "Assets/Venue/Crowd";
            var mats = new System.Collections.Generic.List<Material>();
            foreach (var type in new[] { "beanie", "afro", "bucket", "spiky" })
            {
                string png = $"{dir}/crowd_{type}.png", path = $"{dir}/Crowd_{type}.mat";
                if (AssetImporter.GetAtPath(png) is TextureImporter imp &&
                    (imp.maxTextureSize != 4096 || !imp.mipMapsPreserveCoverage || imp.wrapMode != TextureWrapMode.Clamp))
                {
                    imp.textureType = TextureImporterType.Default;
                    imp.alphaIsTransparency = true;
                    imp.mipmapEnabled = true;
                    imp.mipMapsPreserveCoverage = true;   // alpha-clipped: the figures don't thin out in the distance
                    imp.alphaTestReferenceValue = 0.5f;
                    imp.wrapMode = TextureWrapMode.Clamp;
                    imp.maxTextureSize = 4096;            // the sheets are 3072 wide
                    imp.SaveAndReimport();
                }
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(png);
                if (!tex) continue;
                var shader = AssetDatabase.LoadAssetAtPath<Shader>($"{dir}/CrowdCutout.shader");
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!mat)
                {
                    mat = new Material(shader);
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.shader = shader;
                mat.SetTexture("_MainTex", tex);
                EditorUtility.SetDirty(mat);
                mats.Add(mat);
            }
            AssetDatabase.SaveAssets();
            return mats.ToArray();
        }

        const float Head = 1.6f;   // a standing MC's face, above their mark

        /// <summary>The spot light pointing most nearly at a standing MC on this mark, at their chest (as the venue hangs
        /// them) or their head (as OnTheHead re-aims them), within 15 degrees.</summary>
        static Light AimedAt(System.Collections.Generic.List<Light> spots, Transform mark)
        {
            if (!mark) return null;
            float Off(Light l, float up) => Vector3.Angle(l.transform.forward, mark.position + Vector3.up * up - l.transform.position);
            return spots.Select(l => (l, a: Mathf.Min(Off(l, 0.9f), Off(l, Head))))
                        .Where(x => x.a < 15f).OrderBy(x => x.a).Select(x => x.l).FirstOrDefault();
        }

        /// <summary>The key spot re-aimed at the MC's face, its bright middle widened, its shadows off.</summary>
        static void OnTheHead(Light key, Transform mark)
        {
            if (!key || !mark) return;
            Undo.RecordObject(key.transform, Undo_);
            Undo.RecordObject(key, Undo_);
            key.transform.rotation = Quaternion.LookRotation(mark.position + Vector3.up * Head - key.transform.position);
            key.innerSpotAngle = Mathf.Max(key.innerSpotAngle, 40f);
            key.shadows = LightShadows.None;
            PrefabUtility.RecordPrefabInstancePropertyModifications(key.transform);
            PrefabUtility.RecordPrefabInstancePropertyModifications(key);
        }

        /// <summary>A soft spot on the opponent's face from in front of them, toward the player and the room, only for things
        /// on `layer` (the opponent, which OpponentStage puts there). Made once; a rerun only keeps its layer right.</summary>
        static Light FaceLight(System.Collections.Generic.List<Transform> all, Transform mark, uint layer)
        {
            const string name = "Opponent Face Light";
            var light = all.Where(t => t && t.parent == null && t.name == name).Select(t => t.GetComponent<Light>()).FirstOrDefault(l => l);
            if (!light)
            {
                if (!mark) return null;
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, Undo_);
                Vector3 head = mark.position + Vector3.up * Head;
                Vector3 ahead = Vector3.ProjectOnPlane(mark.forward, Vector3.up).normalized;
                go.transform.position = head + ahead * 1.8f + Vector3.up * 0.4f;   // a little above eye level, in front
                go.transform.rotation = Quaternion.LookRotation(head - go.transform.position);
                light = go.AddComponent<Light>();
                light.type = LightType.Spot;
                light.color = new Color(1f, 0.9f, 0.8f);
                light.intensity = 3f;   // about 40% of the key on the face: a fill, not a second key
                light.range = 5f;
                light.spotAngle = 50f; light.innerSpotAngle = 25f;
                light.shadows = LightShadows.None;
            }
            Undo.RecordObject(light, Undo_);
            var data = light.GetUniversalAdditionalLightData();
            Undo.RecordObject(data, Undo_);
            data.renderingLayers = layer;
            light.renderingLayerMask = (int)layer;
            EditorUtility.SetDirty(data);
            return light;
        }

        /// <summary>The event calls `call` (once: an earlier hook to the same method is replaced).</summary>
        static void Hook(UnityEvent e, UnityAction call)
        {
            for (int i = e.GetPersistentEventCount() - 1; i >= 0; i--)
                if (e.GetPersistentTarget(i) is OpponentStage && e.GetPersistentMethodName(i) == call.Method.Name)
                    UnityEventTools.RemovePersistentListener(e, i);
            UnityEventTools.AddPersistentListener(e, call);
        }
    }
}
