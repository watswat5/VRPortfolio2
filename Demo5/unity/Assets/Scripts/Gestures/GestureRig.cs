// What the gestures watch: the player's head and hands (the headset and the controllers), and the last couple of
// seconds of how they moved. Gestures (Gesture and its subclasses) read it; there's one per player. If none is in the
// scene the first gesture that needs it makes one on the XR Origin, finding the parts by the VR player's names:
// the Main Camera (head), Left/Right Controller (the grip poses: where the hands are) and Left/Right Pointer (the aim
// poses: where they point). Turn `listening` off to stop every gesture (e.g. on stage only).
using System.Collections.Generic;
using UnityEngine;

namespace Freestyle.Gestures
{
    public enum Hand { Left, Right, Either, Both }

    [DefaultExecutionOrder(-50)]
    public class GestureRig : MonoBehaviour
    {
        [Tooltip("The headset (the XR Origin's camera). Empty: the main camera.")]
        public Transform head;
        [Tooltip("Where the hands are: the controllers' grip poses.")]
        public Transform leftHand, rightHand;
        [Tooltip("Where the hands point: the controllers' aim poses. Empty: the hands' own forward.")]
        public Transform leftAim, rightAim;
        [Tooltip("Off: no gesture is recognized (and none in progress carries on).")]
        public bool listening = true;
        [Tooltip("Seconds of movement kept for gestures made over time (pumps, nods).")]
        public float historySeconds = 2f;

        /// <summary>One moment: where the head and hands were (world space) and which way the head faced.</summary>
        public struct Sample
        {
            public float time;
            public Vector3 head, left, right;
            public Quaternion headRotation;
        }

        readonly List<Sample> history = new();
        static GestureRig main;

        /// <summary>The scene's rig: the first one found, else one made on the XR Origin (or the main camera's root).</summary>
        public static GestureRig Main
        {
            get
            {
                if (main) return main;
                main = FindAnyObjectByType<GestureRig>();
                if (main) return main;
                var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
                var host = origin ? origin.gameObject : Camera.main ? Camera.main.transform.root.gameObject : null;
                if (host) main = host.AddComponent<GestureRig>();
                return main;
            }
        }

        /// <summary>The movement kept, oldest first.</summary>
        public IReadOnlyList<Sample> History => history;

        /// <summary>Gestures can be recognized now: listening, with a head to watch.</summary>
        public bool Ready => listening && head;

        void Awake()
        {
            if (!main) main = this;
            Find();
        }

        void OnDestroy() { if (main == this) main = null; }

        /// <summary>Fills in whatever isn't assigned, from the VR player's names.</summary>
        public void Find()
        {
            if (!head && Camera.main) head = Camera.main.transform;
            Transform Named(string n)
            {
                foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
                var go = GameObject.Find(n);
                return go ? go.transform : null;
            }
            if (!leftHand) leftHand = Named("Left Controller");
            if (!rightHand) rightHand = Named("Right Controller");
            if (!leftAim) leftAim = Named("Left Pointer");
            if (!rightAim) rightAim = Named("Right Pointer");
        }

        /// <summary>That hand is there and tracked (a controller that isn't sits at its origin's centre).</summary>
        public bool Has(Hand h) => h switch
        {
            Hand.Left => leftHand && leftHand.gameObject.activeInHierarchy && leftHand.localPosition != Vector3.zero,
            Hand.Right => rightHand && rightHand.gameObject.activeInHierarchy && rightHand.localPosition != Vector3.zero,
            Hand.Both => Has(Hand.Left) && Has(Hand.Right),
            _ => Has(Hand.Left) || Has(Hand.Right),
        };

        public Vector3 HandPosition(Hand h) => h == Hand.Left ? leftHand.position : rightHand.position;

        /// <summary>Where that hand points (its aim pose, else its own forward), from where.</summary>
        public Ray Aim(Hand h)
        {
            var aim = h == Hand.Left ? (leftAim ? leftAim : leftHand) : (rightAim ? rightAim : rightHand);
            return new Ray(aim.position, aim.forward);
        }

        /// <summary>The way the player faces, level.</summary>
        public Vector3 Facing
        {
            get
            {
                var f = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.ProjectOnPlane(head.up, Vector3.up).normalized;
            }
        }

        // after the tracked poses have moved this frame
        void LateUpdate()
        {
            if (!head) { Find(); if (!head) return; }
            float now = Time.time;
            history.Add(new Sample
            {
                time = now, head = head.position, headRotation = head.rotation,
                left = leftHand ? leftHand.position : head.position, right = rightHand ? rightHand.position : head.position,
            });
            int old = 0;
            while (old < history.Count && history[old].time < now - historySeconds) old++;
            if (old > 0) history.RemoveRange(0, old);
        }

        /// <summary>Forgets the movement so far (after a teleport, so a jump isn't read as a gesture).</summary>
        public void Clear() => history.Clear();
    }
}
