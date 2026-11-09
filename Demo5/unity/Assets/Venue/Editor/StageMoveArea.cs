// The stage's MoveArea: where the stick can take you once you're on the stage (XRMovement), the front of the stage up to
// the DJ table, wall to wall, short of the front edge so you don't step off. Works on the open scene in place: the
// "Stage Move Area" object is (re)made, nothing else is touched. Undoable. Resize it in the Inspector (MoveArea.size).
// Freestyle > Add Stage Move Area.
using System.Linq;
using Freestyle.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Freestyle.Venue
{
    public static class StageMoveArea
    {
        const string Name = "Stage Move Area";
        const float EdgeMargin = 0.35f, WallMargin = 0.25f, TableMargin = 0.2f;

        [MenuItem("Freestyle/Add Stage Move Area")]
        public static void Add()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Add stage move area", "Stop Play mode first.", "OK");
                return;
            }
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == Name).ToList())
                Undo.DestroyObjectImmediate(old);

            float x0 = VenueBuilder.StageX0 + WallMargin, x1 = VenueBuilder.X1 - WallMargin;
            float z0 = VenueBuilder.StageZ0 + EdgeMargin, z1 = VenueBuilder.DJTableZ - VenueBuilder.DJTableD / 2 - TableMargin;
            var go = new GameObject(Name);
            Undo.RegisterCreatedObjectUndo(go, "Add stage move area");
            go.transform.position = new Vector3((x0 + x1) / 2, VenueBuilder.StageH, (z0 + z1) / 2);
            go.AddComponent<MoveArea>().size = new Vector3(x1 - x0, 2.5f, z1 - z0);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = go;
            Debug.Log($"{Name} added to {scene.name}; save the scene to keep it.");
        }
    }
}
