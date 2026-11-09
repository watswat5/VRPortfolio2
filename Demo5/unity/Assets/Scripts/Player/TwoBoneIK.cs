// Analytic two-bone IK (shoulder, elbow, wrist), from the class's MultiInteractionExample2026 (AvatarDemo):
//   1. clamp the reach between |L1 - L2| and L1 + L2,
//   2. the law of cosines gives the angle at the shoulder,
//   3. the pole (a point the elbow should bend toward), made perpendicular to the shoulder-to-target line, picks which
//      way the elbow goes around that line,
//   4. rotate the upper arm to the elbow, then the forearm to the target.
// Bones are turned with world-space rotations, so it works on any rig whatever its bone axes. Call it on a pose that
// starts from the bind pose each frame, or twist builds up.
using UnityEngine;

namespace Freestyle.Player
{
    public static class TwoBoneIK
    {
        public static bool Solve(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float L1 = (b - a).magnitude, L2 = (c - b).magnitude;
            Vector3 toTarget = target - a;
            float dist = toTarget.magnitude;
            float d = Mathf.Clamp(dist, Mathf.Abs(L1 - L2) + 1e-4f, L1 + L2 - 1e-4f);
            Vector3 n = dist > 1e-6f ? toTarget / dist : Vector3.forward;

            float cosA = (L1 * L1 + d * d - L2 * L2) / (2f * L1 * d);               // law of cosines
            float A = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
            Vector3 u = Vector3.ProjectOnPlane(pole - a, n);                       // pole, perpendicular to n
            if (u.sqrMagnitude < 1e-8f) u = Vector3.ProjectOnPlane(Vector3.down, n);
            u.Normalize();
            Vector3 elbow = a + n * (L1 * Mathf.Cos(A)) + u * (L1 * Mathf.Sin(A));

            upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
            b = lower.position; c = end.position;
            Vector3 wrist = a + n * d;
            lower.rotation = Quaternion.FromToRotation(c - b, wrist - b) * lower.rotation;
            return dist <= L1 + L2;
        }
    }
}
