// Where you're standing in your room shouldn't decide where you are in the game. On launch (and after the scene is
// reloaded), once the headset is tracking, the play space is moved so your head is over the XR Origin's spot in the
// scene, facing its way: starting off to one side of your room can't put you through a wall or out over the void.
// The XR Origin itself goes under your head too (the Camera Offset takes up the difference): your body's capsule stands
// on the rig's origin, so wherever the play space's own origin is in your room, the capsule starts under you, on the floor.
// The headset's own recenter (holding the Meta button) keeps you where you are in the game instead of jumping you to
// the play space's middle. On the flat screen (no headset) it does nothing.
// Added to the XR Origin by itself when the scene loads (no rebuild needed).
using System.Collections;
using System.Collections.Generic;
using Freestyle.Venue;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;

namespace Freestyle.Player
{
    [RequireComponent(typeof(XROrigin))]
    public class PlaySpaceRecenter : MonoBehaviour
    {
        [Tooltip("The longest to wait for the headset to start tracking (s).")]
        public float waitForTracking = 10f;

        XROrigin origin;
        Vector3 spawn, spawnForward;
        Vector3 head, headForward;      // where your head was last frame, in the game
        bool placed, recentred, haveHead;
        readonly List<XRInputSubsystem> inputs = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, _) => AddTo();
            AddTo();
        }

        static void AddTo()
        {
            var o = FindAnyObjectByType<XROrigin>();
            if (o && !o.GetComponent<PlaySpaceRecenter>()) o.gameObject.AddComponent<PlaySpaceRecenter>();
        }

        void Awake()
        {
            origin = GetComponent<XROrigin>();
            spawn = transform.position;
            spawnForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        }

        void OnEnable()
        {
            SubsystemManager.GetSubsystems(inputs);
            foreach (var s in inputs) s.trackingOriginUpdated += OnRecentred;
        }

        void OnDisable()
        {
            foreach (var s in inputs) s.trackingOriginUpdated -= OnRecentred;
        }

        void OnRecentred(XRInputSubsystem _) => recentred = true;

        IEnumerator Start()
        {
            for (float t = 0; t < waitForTracking && !Tracking(); t += Time.unscaledDeltaTime) yield return null;
            if (!Tracking()) yield break;   // the flat screen, or no headset after all
            yield return null;              // a frame of real head poses
            OriginUnderHead();
            Teleport.To(spawn, spawnForward);
            recentred = false;   // (the play space's own setup at launch isn't a recenter)
            placed = true;
        }

        /// <summary>Shifts the Camera Offset (the head and hands with it) so the head is straight above the XR Origin.</summary>
        void OriginUnderHead()
        {
            if (!origin.CameraFloorOffsetObject) return;
            Vector3 fromOrigin = origin.Camera.transform.position - transform.position;
            fromOrigin.y = 0f;
            origin.CameraFloorOffsetObject.transform.position -= fromOrigin;
        }

        bool Tracking()
        {
            if (!XRSettings.isDeviceActive || !origin.Camera) return false;
            var hmd = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return hmd.isValid && hmd.TryGetFeatureValue(CommonUsages.isTracked, out bool on) && on
                   && origin.Camera.transform.localPosition != Vector3.zero;
        }

        void LateUpdate()
        {
            if (!placed || !origin.Camera) return;
            var cam = origin.Camera.transform;
            if (recentred && haveHead)
            {
                // the headset moved the play space under you: back to where you were standing in the game
                recentred = false;
                OriginUnderHead();
                Teleport.To(new Vector3(head.x, transform.position.y, head.z), headForward);
                return;
            }
            recentred = false;
            head = cam.position;
            haveHead = true;
            var f = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            if (f.sqrMagnitude > 0.01f) headForward = f.normalized;
        }
    }
}
