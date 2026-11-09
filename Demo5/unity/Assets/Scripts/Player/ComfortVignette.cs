// Narrows the view while you move with the stick (XRMovement sets its target): a global URP Volume with a Vignette,
// its intensity easing up and back down, which keeps smooth movement comfortable. Made at runtime (a profile of its
// own, top priority so it wins over the scene's), and it turns post-processing on for the camera that shows it.
// ComfortVignette.For(camera) gets the one for that camera, making it if needed.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Freestyle.Player
{
    public class ComfortVignette : MonoBehaviour
    {
        [Tooltip("Seconds to darken, and to clear again.")]
        public float fadeIn = 0.15f, fadeOut = 0.35f;
        [Tooltip("The vignette's intensity at full strength (URP Vignette: 0 none, 1 the most).")]
        [Range(0, 1)] public float maxIntensity = 0.55f;
        [Range(0.01f, 1)] public float smoothness = 0.9f;

        Volume volume;
        VolumeProfile profile;
        Vignette vignette;
        float strength, target;

        /// <summary>The vignette for this camera (made the first time, as a global Volume beside it).</summary>
        public static ComfortVignette For(Camera cam)
        {
            var v = FindAnyObjectByType<ComfortVignette>();
            if (v) return v;
            var go = new GameObject("Comfort Vignette (global volume)");
            go.transform.SetParent(cam.transform.root, false);
            v = go.AddComponent<ComfortVignette>();
            v.Make(cam);
            return v;
        }

        void Make(Camera cam)
        {
            volume = gameObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 100f;
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Comfort Vignette";
            volume.sharedProfile = profile;
            vignette = profile.Add<Vignette>(true);
            vignette.color.Override(Color.black);
            vignette.center.Override(new Vector2(0.5f, 0.5f));
            vignette.smoothness.Override(smoothness);
            vignette.rounded.Override(true);
            vignette.intensity.Override(0f);
            volume.weight = 0f;
            // post-processing has to be on for the camera to show a Volume's effects
            if (cam && cam.TryGetComponent<UniversalAdditionalCameraData>(out var data)) data.renderPostProcessing = true;
            else if (cam) cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        }

        /// <summary>How strong it should be (0-1); it eases there.</summary>
        public void Set(float to) => target = Mathf.Clamp01(to);

        void Update()
        {
            if (!vignette) return;
            float time = target > strength ? fadeIn : fadeOut;
            strength = Mathf.MoveTowards(strength, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, time));
            vignette.intensity.Override(strength * maxIntensity);
            vignette.smoothness.Override(smoothness);
            volume.weight = strength > 0.001f ? 1f : 0f;   // (off entirely when clear)
        }

        void OnDestroy()
        {
            if (profile) Destroy(profile);
        }
    }
}
