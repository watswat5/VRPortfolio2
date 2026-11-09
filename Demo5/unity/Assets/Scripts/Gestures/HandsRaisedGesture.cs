// Hands up: both (or one) above the head. With Pumps above 0 they have to pump, up and down that many times within
// Pump Window ("raise the roof"), which a stretch or fixing the headset doesn't; with 0, just held up for Hold Time
// (a "hands up" or a victory pose).
using UnityEngine;

namespace Freestyle.Gestures
{
    public class HandsRaisedGesture : Gesture
    {
        [Tooltip("Both hands, or either one.")]
        public Hand hands = Hand.Both;
        [Tooltip("How far above the head the hands have to be, metres (pumping: the tops of the pumps).")]
        public float aboveHead = 0.1f;
        [Tooltip("Up strokes needed (0: hands held up, no pumping).")]
        [Range(0, 6)] public int pumps = 2;
        [Tooltip("Seconds the pumps have to come within.")]
        public float pumpWindow = 1.5f;
        [Tooltip("How far a pump has to travel, metres.")]
        public float pumpSize = 0.08f;

        public HandsRaisedGesture() { holdTime = 0f; cooldown = 3f; }

        protected override string DefaultName => pumps > 0 ? "RaiseTheRoof" : "HandsUp";

        protected override bool Check(GestureRig rig, GestureEvent e)
        {
            var which = hands == Hand.Both ? new[] { Hand.Left, Hand.Right } : HandsFor(hands);
            int up = 0;
            float strength = 1f;
            foreach (var h in which)
            {
                if (!rig.Has(h)) continue;
                float y = rig.HandPosition(h).y - rig.head.position.y;
                // pumping dips the hands a little each time: near enough the top counts
                bool high = y >= aboveHead - (pumps > 0 ? pumpSize * 1.5f : 0f);
                if (!high) continue;
                if (pumps > 0)
                {
                    int n = Strokes(rig, h);
                    if (n < pumps) continue;
                    strength = Mathf.Min(strength, Mathf.Clamp01(n / (pumps + 1f)) + 0.25f);
                }
                up++;
                e.hand = which.Length == 2 ? Hand.Both : h;
            }
            bool ok = hands == Hand.Both ? up == 2 : up >= 1;
            if (ok) { e.strength = Mathf.Clamp01(strength); e.position = rig.head.position + Vector3.up * aboveHead; e.direction = Vector3.up; }
            return ok;
        }

        /// <summary>Up strokes of at least pumpSize by that hand (against the head, so a bob or a step isn't one)
        /// within the window.</summary>
        int Strokes(GestureRig rig, Hand h)
        {
            var hist = rig.History;
            float since = Time.time - pumpWindow, low = float.MaxValue, high = float.MinValue;
            bool rising = true;   // looking for an up stroke (from the lowest point so far)
            int n = 0;
            for (int i = 0; i < hist.Count; i++)
            {
                if (hist[i].time < since) continue;
                float y = (h == Hand.Left ? hist[i].left.y : hist[i].right.y) - hist[i].head.y;
                if (rising)
                {
                    low = Mathf.Min(low, y);
                    if (y >= low + pumpSize) { n++; rising = false; high = y; }
                }
                else
                {
                    high = Mathf.Max(high, y);
                    if (y <= high - pumpSize) { rising = true; low = y; }
                }
            }
            return n;
        }
    }
}
