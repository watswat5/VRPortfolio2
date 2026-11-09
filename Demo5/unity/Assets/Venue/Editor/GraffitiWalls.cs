// Makes the dressing room's four walls taggable with the spray cans: a GraffitiSurface laid just in front of each, floor
// to ceiling. Behind the mirror and shelf (they stay in front of any paint); on the right wall it's a hair in front of the
// opponents' posters, so the wall and the posters on it are sprayed as one. They're on the Ignore Raycast
// layer (only the spray cans look for them) with a thin trigger box each, and share Assets/Venue/Graffiti/GraffitiPaint.mat
// (see-through URP Lit, so the paint takes the room's light).
// Works on the open scene in place: (re)makes the "Graffiti Walls" group, nothing else is touched. Undoable.
// Freestyle > Make Dressing Room Walls Paintable.
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Freestyle.Venue
{
    public static class GraffitiWalls
    {
        const string Group = "Graffiti Walls", Undo_ = "Make walls paintable", Dir = "Assets/Venue/Graffiti";
        const float Off = 0.004f;   // in front of the wall

        [MenuItem("Freestyle/Make Dressing Room Walls Paintable")]
        public static void Make()
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Paintable walls", "Stop Play mode first.", "OK"); return; }
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == Group).ToList()) Undo.DestroyObjectImmediate(old);

            var shader = AssetDatabase.LoadAssetAtPath<Shader>($"{Dir}/GraffitiStamp.shader");
            var mat = PaintMaterial();
            var group = new GameObject(Group).transform;
            Undo.RegisterCreatedObjectUndo(group.gameObject, Undo_);

            float x0 = VenueBuilder.BX0, x1 = VenueBuilder.BX1, z0 = VenueBuilder.BZ0, z1 = VenueBuilder.BZ1, h = VenueBuilder.BH;
            float mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
            // each faces into its wall (the quad shows its -Z side, toward the room)
            Wall(group, "Left Wall", new Vector3(x0 + Off, h / 2, mz), Vector3.left, new Vector2(z1 - z0, h), mat, shader);
            Wall(group, "Right Wall", new Vector3(x1 - OverPosters(scene, x1), h / 2, mz), Vector3.right, new Vector2(z1 - z0, h), mat, shader);
            Wall(group, "Back Wall", new Vector3(mx, h / 2, z1 - Off), Vector3.forward, new Vector2(x1 - x0, h), mat, shader);
            Wall(group, "Front Wall", new Vector3(mx, h / 2, z0 + Off), Vector3.back, new Vector2(x1 - x0, h), mat, shader);

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = group.gameObject;
            Debug.Log("The dressing room's walls are paintable (spray cans, within 0.6 m). Save the scene.");
        }

        /// <summary>How far off the right wall its paint goes: just in front of the opponents' posters (however far out they
        /// hang now), so paint lands on the posters and the wall alike. Half a millimetre: just enough to never z-fight
        /// (24-bit depth with the 3 cm near plane resolves ~0.07 mm at the far side of the room) and no visible gap.</summary>
        static float OverPosters(UnityEngine.SceneManagement.Scene scene, float wallX)
        {
            var posters = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Opponent Posters");
            float out_ = OpponentPosters.OffWall;   // where OpponentPosters hangs them
            if (posters)
                foreach (var r in posters.GetComponentsInChildren<Renderer>())
                    out_ = Mathf.Max(out_, wallX - r.bounds.min.x);
            return out_ + 0.0005f;
        }

        static void Wall(Transform group, string name, Vector3 at, Vector3 into, Vector2 size, Material mat, Shader shader)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Undo.RegisterCreatedObjectUndo(q, Undo_);
            Object.DestroyImmediate(q.GetComponent<Collider>());
            q.name = name;
            q.layer = 2;   // Ignore Raycast: only the spray cans look for it
            q.transform.SetParent(group, false);
            q.transform.SetPositionAndRotation(at, Quaternion.LookRotation(into));
            q.transform.localScale = new Vector3(size.x, size.y, 1);
            var box = q.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(1, 1, 0.01f / 1f);
            var r = q.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var s = q.AddComponent<GraffitiSurface>();
            s.stampShader = shader;
        }

        /// <summary>The paint's material: URP Lit, see-through (straight alpha), a little satin; made once.</summary>
        static Material PaintMaterial()
        {
            string path = $"{Dir}/GraffitiPaint.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "GraffitiPaint" };
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.25f);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Surface", 1f);   // transparent
            m.SetFloat("_Blend", 0f);     // alpha
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_ReceiveShadows", 1f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
