// Fades the view to a colour and back: a box around the camera, drawn over everything (Freestyle/ScreenFade), works
// for the headset and the flat screen alike. ScreenFade.For(camera, shader) gets the camera's, making it if needed.
using System.Collections;
using UnityEngine;

namespace Freestyle.Venue
{
    public class ScreenFade : MonoBehaviour
    {
        public Color color = Color.black;

        Material mat;
        Renderer box;
        float alpha;

        /// <summary>The camera's fade (made the first time, with this shader).</summary>
        public static ScreenFade For(Camera cam, Shader shader)
        {
            var f = cam.GetComponentInChildren<ScreenFade>();
            if (f) return f;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // around the eyes: covers every direction
            go.name = "ScreenFade";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(cam.transform, false);
            go.transform.localScale = Vector3.one * Mathf.Max(0.3f, cam.nearClipPlane * 4f);
            f = go.AddComponent<ScreenFade>();
            f.box = go.GetComponent<Renderer>();
            f.box.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            f.box.receiveShadows = false;
            f.mat = new Material(shader ? shader : Shader.Find("Freestyle/ScreenFade"));
            f.box.sharedMaterial = f.mat;
            f.Apply();
            return f;
        }

        /// <summary>Fades to alpha (0 clear, 1 the colour) over seconds.</summary>
        public IEnumerator To(float target, float seconds)
        {
            float from = alpha;
            for (float t = 0; t < seconds; t += Time.unscaledDeltaTime)
            {
                alpha = Mathf.Lerp(from, target, t / seconds);
                Apply();
                yield return null;
            }
            alpha = target;
            Apply();
        }

        void Apply()
        {
            if (!mat) return;
            mat.SetColor("_Color", new Color(color.r, color.g, color.b, alpha));
            box.enabled = alpha > 0.001f;
        }
    }
}
