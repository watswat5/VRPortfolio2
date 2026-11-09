// Moving the player somewhere, and doing it behind a fade (StageDoor to the stage, OpponentStage back to the dressing
// room). Moves the XR Origin when there is one (your head over the spot, facing its way, at the same height over the
// floor), else the given rig, else the main camera's root.
using System;
using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace Freestyle.Venue
{
    public static class Teleport
    {
        /// <summary>Puts the player on `destination`, facing its forward.</summary>
        public static void To(Transform destination, Transform rig = null, float flatEyeHeight = 1.65f) =>
            To(destination.position, destination.forward, rig, flatEyeHeight);

        /// <summary>Puts the player on `position` (the floor under their head), facing `facing`.</summary>
        public static void To(Vector3 position, Vector3 facing, Transform rig = null, float flatEyeHeight = 1.65f)
        {
            var forward = Vector3.ProjectOnPlane(facing, Vector3.up).normalized;
            var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>();
            if (origin && origin.Camera)
            {
                origin.MatchOriginUpCameraForward(Vector3.up, forward);
                float eye = origin.Camera.transform.position.y - origin.Origin.transform.position.y;
                origin.MoveCameraToWorldLocation(position + Vector3.up * eye);
                return;
            }
            if (!Camera.main) return;
            var cam = Camera.main.transform;
            var r = rig ? rig : cam.root;
            r.SetPositionAndRotation(position + (r == cam ? Vector3.up * flatEyeHeight : Vector3.zero),
                                     Quaternion.LookRotation(forward));
        }

        /// <summary>Fades to black, moves the player, runs `whileDark` (still black: spawn or clear things there), holds,
        /// fades back in. Run it as a coroutine.</summary>
        public static IEnumerator WithFade(Transform destination, Shader fadeShader, Action whileDark,
                                           float fadeOut = 0.4f, float hold = 0.2f, float fadeIn = 0.5f,
                                           Transform rig = null, float flatEyeHeight = 1.65f)
        {
            if (!Camera.main) yield break;
            var fade = ScreenFade.For(Camera.main, fadeShader);
            yield return fade.To(1, fadeOut);
            To(destination, rig, flatEyeHeight);
            try { whileDark?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }   // never leave the player in the dark
            yield return new WaitForSecondsRealtime(hold);
            yield return fade.To(0, fadeIn);
        }
    }
}
