// Stepping up to something: the head within Distance of the target (level distance), e.g. right in the MC's face.
// Fires as you come in; On Ended as you back off.
using UnityEngine;

namespace Freestyle.Gestures
{
    public class ApproachGesture : Gesture
    {
        [Tooltip("What to step up to. Set it from a script for something spawned (the MC).")]
        public Transform target;
        [Tooltip("No target assigned: the nearest object with this tag.")]
        public string targetTag = "";
        [Tooltip("How close counts, metres (level distance from the head).")]
        public float distance = 1.0f;

        public ApproachGesture() { holdTime = 0.15f; cooldown = 4f; }

        protected override bool Check(GestureRig rig, GestureEvent e)
        {
            var t = TargetOf(target, targetTag);
            if (!t) return false;
            var to = t.position - rig.head.position;
            to.y = 0f;
            float d = to.magnitude;
            if (d > distance) return false;
            e.hand = Hand.Either; e.target = t;
            e.strength = Mathf.Clamp01(1f - d / distance);
            e.position = rig.head.position; e.direction = d > 1e-4f ? to / d : rig.Facing;
            return true;
        }
    }
}
