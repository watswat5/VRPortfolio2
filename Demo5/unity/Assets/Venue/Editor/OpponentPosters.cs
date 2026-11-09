// The opponents' posters in the dressing room: one per MC (Assets/Venue/Posters/<id>.png, made by
// art/posters/make_posters.py), side by side on the right wall, easiest by the mirror to the champ by the door.
// Works on the open scene in place, nothing else in it is touched: the old gig posters (Venue.prefab's Poster*/PosterBand*)
// are switched off, the "Opponent Posters" group is (re)made. Undoable.
// Freestyle > Hang Opponent Posters.
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Freestyle.Venue
{
    public static class OpponentPosters
    {
        const string Dir = "Assets/Venue/Posters", Group = "Opponent Posters";
        // left to right as you face the wall (the back wall, by the mirror, to the door)
        internal static readonly string[] Cast = { "lil_nervous", "mc_static", "loose_cannon", "kid_venom", "the_champ" };
        const float Width = 0.56f, Height = 0.84f, Gap = 0.1f, CentreY = 1.5f;
        internal const float OffWall = 0.0005f;   // flat on the wall: just enough not to z-fight it (the paint goes over them)

        [MenuItem("Freestyle/Hang Opponent Posters")]
        public static void Hang()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Hang opponent posters", "Stop Play mode first.", "OK");
                return;
            }
            var scene = EditorSceneManager.GetActiveScene();
            var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToList();

            foreach (var t in all.Where(t => t.name.StartsWith("Poster") && InDressingRoom(t) && t.gameObject.activeSelf))
            {
                Undo.RecordObject(t.gameObject, "Hang opponent posters");
                t.gameObject.SetActive(false);
            }
            foreach (var old in all.Where(t => t && t.parent == null && t.name == Group).ToList())
                Undo.DestroyObjectImmediate(old.gameObject);

            var group = new GameObject(Group);
            Undo.RegisterCreatedObjectUndo(group, "Hang opponent posters");
            // the right wall, facing into the room
            float x = VenueBuilder.BX1 - OffWall, mid = (VenueBuilder.BZ0 + VenueBuilder.BZ1) / 2;
            group.transform.SetPositionAndRotation(new Vector3(x, CentreY, mid), Quaternion.LookRotation(Vector3.right));
            for (int i = 0; i < Cast.Length; i++)
            {
                var mat = Material(Cast[i]);
                if (!mat) { Debug.LogWarning($"No poster for {Cast[i]} in {Dir}: run art/posters/make_posters.py"); continue; }
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(q.GetComponent<Collider>());
                q.name = Cast[i];
                q.transform.SetParent(group.transform, false);
                // the quad faces -Z, into the room; +X along the wall is the room's -Z, so the first is at the back
                q.transform.localPosition = new Vector3((i - (Cast.Length - 1) / 2f) * (Width + Gap), 0, 0);
                q.transform.localScale = new Vector3(Width, Height, 1);
                var r = q.GetComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = group;
            Debug.Log($"Hung {Cast.Length} opponent posters in {scene.name}; save the scene to keep them.");
        }

        static bool InDressingRoom(Transform t) =>
            t.TryGetComponent<MeshRenderer>(out var r) && r.bounds.center.x > VenueBuilder.BX0 && r.bounds.center.x < VenueBuilder.BX1 + 0.1f;

        /// <summary>The poster's material (made once, next to its image), the image imported sharp for reading at an angle.</summary>
        static Material Material(string id)
        {
            string png = $"{Dir}/{id}.png", path = $"{Dir}/{id}.mat";
            if (AssetImporter.GetAtPath(png) is TextureImporter imp)
            {
                if (imp.anisoLevel != 8 || imp.textureCompression != TextureImporterCompression.CompressedHQ || !imp.mipmapEnabled)
                {
                    imp.mipmapEnabled = true; imp.anisoLevel = 8; imp.filterMode = FilterMode.Trilinear;
                    imp.textureCompression = TextureImporterCompression.CompressedHQ; imp.maxTextureSize = 2048;
                    imp.wrapMode = TextureWrapMode.Clamp;
                    imp.SaveAndReimport();
                }
            }
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(png);
            if (!tex) return null;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0.25f);   // a little gloss, like printed paper
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }
    }
}
