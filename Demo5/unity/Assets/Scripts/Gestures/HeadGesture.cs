// A nod (the head going down and up) or a shake (side to side): Swings changes of direction, each at least Min Angle,
// within Window seconds. From the headset alone, so it's the most reliable gesture there is.
using UnityEngine;

namespace Freestyle.Gestures
{
    public class HeadGesture : Gesture
    {
        public enum Kind { Nod, Shake }

        public Kind kind = Kind.Nod;
        [Tooltip("Swings needed, each the other way from the last (3: down-up-down, or left-right-left).")]
        [Range(2, 8)] public int swings = 3;
        [Tooltip("How far each swing has to go, degrees.")]
        public float minAngle = 6f;
        [Tooltip("Seconds the swings have to come within.")]
        public float window = 1.2f;

        public HeadGesture() { holdTime = 0f; cooldown = 2f; }

        protected override string DefaultName => kind.ToString();

        protected override bool Check(GestureRig rig, GestureEvent e)
        {
            var hist = rig.History;
            float since = Time.time - window;
            int first = 0;
            while (first < hist.Count && hist[first].time < since) first++;
            if (hist.Count - first < 4) return false;
            Vector3 refFacing = Flat(hist[first].headRotation * Vector3.forward);
            // legs of at least minAngle in alternating directions (down-up-down is 3), with hysteresis: a leg ends
            // when the head comes back minAngle from the furthest it got
            int dir = 0, legs = 0;
            float ext = 0f, peak = 0f;
            for (int i = first; i < hist.Count; i++)
            {
                var fwd = hist[i].headRotation * Vector3.forward;
                float a = kind == Kind.Nod
                    ? Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg
                    : Vector3.SignedAngle(refFacing, Flat(fwd), Vector3.up);
                if (i == first) { ext = a; continue; }
                if (dir == 0)
                {
                    if (Mathf.Abs(a - ext) >= minAngle) { dir = a > ext ? 1 : -1; peak = Mathf.Abs(a - ext); ext = a; legs = 1; }
                }
                else if ((a - ext) * dir > 0) ext = a;                      // still going: further
                else if ((ext - a) * dir >= minAngle)                        // came back far enough: a new leg
                {
                    peak = Mathf.Max(peak, (ext - a) * dir);
                    dir = -dir; ext = a; legs++;
                }
            }
            if (legs < swings) return false;
            e.hand = Hand.Either;
            e.strength = Mathf.Clamp01(peak / (minAngle * 3f));
            e.position = rig.head.position; e.direction = rig.Facing;
            return true;
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }
    }
}
