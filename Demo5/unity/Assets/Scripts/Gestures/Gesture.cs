// A gesture the player makes, recognized from the GestureRig and announced: On Recognized (an Inspector event: wire it
// to Animator.SetTrigger, a sound, a script) and the C# events Fired (this gesture) and Gesture.Recognized (every
// gesture, for scripts that listen to them all). A held gesture (pointing) also says when it ends (On Ended).
// Subclasses only say whether the pose is there right now (Check); the base holds it for Hold Time before it counts,
// then won't fire again until it's let go and Cooldown has passed. Right-click > Fire (test) fires it without a
// headset, to try the wiring.
// The built-in gestures: PointGesture, HandsRaisedGesture, HeadGesture (nod, shake), ApproachGesture. Add one per
// gesture you want, anywhere in the scene; several of the same kind (e.g. pointing at two things) is fine.
using System;
using UnityEngine;
using UnityEngine.Events;

namespace Freestyle.Gestures
{
    /// <summary>What was recognized: which gesture, which hand, how clearly, where and at what.</summary>
    [Serializable]
    public class GestureEvent
    {
        public string name;
        public Gesture gesture;
        public Hand hand;
        [Tooltip("0-1: how cleanly it was made (how straight the point, how far past the threshold).")]
        public float strength = 1f;
        public Vector3 position, direction;
        public Transform target;
        public float time;

        public override string ToString() => $"{name} ({hand}, {strength:0.00}{(target ? ", at " + target.name : "")})";
    }

    [Serializable] public class GestureUnityEvent : UnityEvent<GestureEvent> { }

    public abstract class Gesture : MonoBehaviour
    {
        [Tooltip("The name it's announced with (GestureEvent.name). Empty: the component's kind, e.g. \"Point\".")]
        public string gestureName = "";
        [Tooltip("The rig to watch. Empty: the scene's (GestureRig.Main).")]
        public GestureRig rig;
        [Tooltip("Seconds the pose has to be held before it counts (0: at once).")]
        public float holdTime = 0.3f;
        [Tooltip("Seconds after it fires before it can fire again (it has to be let go first, too).")]
        public float cooldown = 2f;
        [Tooltip("Log each time it's recognized (the console, or the headset's log). Development builds always do.")]
        public bool log;

        [Header("Events")]
        public GestureUnityEvent onRecognized = new();
        [Tooltip("A held gesture was let go (after it was recognized).")]
        public GestureUnityEvent onEnded = new();

        /// <summary>This gesture was recognized.</summary>
        public event Action<GestureEvent> Fired;
        /// <summary>Any gesture was recognized.</summary>
        public static event Action<GestureEvent> Recognized;

        /// <summary>The pose is there now and has been recognized (until it's let go).</summary>
        public bool Active { get; private set; }
        public string Name => string.IsNullOrEmpty(gestureName) ? DefaultName : gestureName;
        protected virtual string DefaultName => GetType().Name.Replace("Gesture", "");

        float heldFor, nextAllowed;
        GestureEvent last;

        /// <summary>Is the pose there right now? Fill in what it is (hand, strength, position, direction, target);
        /// the base sets the name, gesture and time.</summary>
        protected abstract bool Check(GestureRig rig, GestureEvent e);

        protected virtual void Update()
        {
            if (!rig) rig = GestureRig.Main;
            var e = new GestureEvent();
            bool on = rig && rig.Ready && Check(rig, e);
            if (on)
            {
                heldFor += Time.deltaTime;
                if (!Active && heldFor >= holdTime && Time.time >= nextAllowed) Fire(e);
            }
            else
            {
                if (Active) End();
                heldFor = 0f;
            }
        }

        void OnDisable() { if (Active) End(); heldFor = 0f; }

        void Fire(GestureEvent e)
        {
            e.name = Name; e.gesture = this; e.time = Time.time;
            Active = true;
            last = e;
            nextAllowed = Time.time + cooldown;
            if (log || Debug.isDebugBuild) Debug.Log($"[Gesture] {e}", this);
            Raise(onRecognized, Fired, e);
            try { Recognized?.Invoke(e); } catch (Exception x) { Debug.LogException(x, this); }
        }

        void End()
        {
            Active = false;
            if (last != null) Raise(onEnded, null, last);
        }

        void Raise(GestureUnityEvent unity, Action<GestureEvent> cs, GestureEvent e)
        {
            try { unity?.Invoke(e); } catch (Exception x) { Debug.LogException(x, this); }
            try { cs?.Invoke(e); } catch (Exception x) { Debug.LogException(x, this); }
        }

        // ---- for subclasses ----

        /// <summary>The hands to try for a Hand setting (Either and Both: right first).</summary>
        protected static Hand[] HandsFor(Hand h) => h switch
        {
            Hand.Left => new[] { Hand.Left },
            Hand.Right => new[] { Hand.Right },
            _ => new[] { Hand.Right, Hand.Left },
        };

        Transform tagged;
        float nextTagSearch;

        /// <summary>The target: the assigned one, else the nearest object with the tag (looked up twice a second).</summary>
        protected Transform TargetOf(Transform assigned, string tag)
        {
            if (assigned) return assigned;
            if (string.IsNullOrEmpty(tag)) return null;
            if (Time.time >= nextTagSearch || !tagged)
            {
                nextTagSearch = Time.time + 0.5f;
                tagged = null;
                float best = float.MaxValue;
                var from = rig && rig.head ? rig.head.position : transform.position;
                GameObject[] found;
                try { found = GameObject.FindGameObjectsWithTag(tag); }
                catch (UnityException) { return null; }   // a tag the project doesn't have
                foreach (var go in found)
                {
                    float d = (go.transform.position - from).sqrMagnitude;
                    if (d < best) { best = d; tagged = go.transform; }
                }
            }
            return tagged;
        }

        /// <summary>The point on a target to aim at: a character's head (a humanoid's head bone), else the middle of
        /// what it draws, else the transform itself.</summary>
        protected static Vector3 AimPoint(Transform t)
        {
            var anim = t.GetComponentInChildren<Animator>();
            if (anim && anim.isHuman)
            {
                var head = anim.GetBoneTransform(HumanBodyBones.Head);
                if (head) return head.position;
            }
            var r = t.GetComponentInChildren<Renderer>();
            return r ? r.bounds.center : t.position;
        }

        /// <summary>Fires it now as if it had been made (testing the wiring; scripts that want to fake one).</summary>
        [ContextMenu("Fire (test)")]
        public void FireNow()
        {
            var e = new GestureEvent { hand = Hand.Right };
            if (rig && rig.head) { e.position = rig.head.position; e.direction = rig.Facing; }
            Fire(e);
            Active = false;   // (nothing to let go of)
        }
    }
}
