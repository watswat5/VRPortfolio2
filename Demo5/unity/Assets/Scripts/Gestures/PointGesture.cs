// Pointing: an arm out (the hand at least Min Reach from the head) and aimed (the controller's aim pose) within Max
// Angle of the target, held for Hold Time. A character target is aimed at by its head. No target: pointing anywhere,
// with the direction in the event. Ends when the arm drops or turns away.
using UnityEngine;

namespace Freestyle.Gestures
{
    public class PointGesture : Gesture
    {
        [Tooltip("Which hand may point.")]
        public Hand hand = Hand.Either;
        [Tooltip("What to point at (a character: its head). Set it from a script for something spawned (the MC).")]
        public Transform target;
        [Tooltip("No target assigned: the nearest object with this tag.")]
        public string targetTag = "";
        [Tooltip("How far off the target the point may be, degrees.")]
        [Range(2, 45)] public float maxAngle = 15f;
        [Tooltip("How far out the hand has to be from the head, metres (a straight-ish arm).")]
        public float minReach = 0.4f;

        public PointGesture() { holdTime = 0.35f; cooldown = 3f; }

        protected override bool Check(GestureRig rig, GestureEvent e)
        {
            var t = TargetOf(target, targetTag);
            if (!t && (target || !string.IsNullOrEmpty(targetTag))) return false;   // its target isn't there
            foreach (var h in HandsFor(hand))
            {
                if (!rig.Has(h) || (rig.HandPosition(h) - rig.head.position).magnitude < minReach) continue;
                var ray = rig.Aim(h);
                float strength = 1f;
                if (t)
                {
                    float angle = Vector3.Angle(ray.direction, AimPoint(t) - ray.origin);
                    if (angle > maxAngle) continue;
                    strength = 1f - angle / maxAngle;
                }
                e.hand = h; e.strength = strength; e.target = t;
                e.position = ray.origin; e.direction = ray.direction;
                return true;
            }
            return false;
        }
    }
}
