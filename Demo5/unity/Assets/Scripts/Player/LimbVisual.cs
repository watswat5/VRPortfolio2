// Keeps a capsule stretched between two joints, so the robot's arms always reach its hands whatever the player's
// reach. From the class's MultiInteractionExample2026 (AvatarDemo). Runs after the avatar has been posed.
using UnityEngine;

namespace Freestyle.Player
{
    [DefaultExecutionOrder(100)]
    public class LimbVisual : MonoBehaviour
    {
        public Transform from, to;
        public float thickness = 0.08f;

        void LateUpdate()
        {
            if (!from || !to) return;
            Vector3 a = from.position, b = to.position, d = b - a;
            if (d.sqrMagnitude < 1e-8f) return;
            transform.SetPositionAndRotation((a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, d));
            float ps = transform.parent ? transform.parent.lossyScale.x : 1f;
            transform.localScale = new Vector3(thickness, d.magnitude * 0.5f, thickness) / ps;
        }
    }
}
