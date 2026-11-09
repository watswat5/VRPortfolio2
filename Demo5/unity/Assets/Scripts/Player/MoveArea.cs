// A box the player's stick movement keeps them in (XRMovement): while you're in one, walking with the stick stops at its
// sides. Your head is what's kept in (across the floor; the box's height only says who's in it). Outside every area
// (the dressing room) movement is free. The box is this transform's position and rotation with `size`, its pivot on
// the floor in the middle.
using UnityEngine;

namespace Freestyle.Player
{
    public class MoveArea : MonoBehaviour
    {
        public Vector3 size = new(2f, 2.5f, 2f);

        /// <summary>Whether a point is in the box, `margin` metres of slack around it.</summary>
        public bool Contains(Vector3 world, float margin = 0f)
        {
            Vector3 p = transform.InverseTransformPoint(world);
            return Mathf.Abs(p.x) <= size.x / 2 + margin && Mathf.Abs(p.z) <= size.z / 2 + margin && p.y >= -margin && p.y <= size.y + margin;
        }

        /// <summary>The nearest point in the box across the floor (the height left as it is).</summary>
        public Vector3 Clamp(Vector3 world)
        {
            Vector3 p = transform.InverseTransformPoint(world);
            p.x = Mathf.Clamp(p.x, -size.x / 2, size.x / 2);
            p.z = Mathf.Clamp(p.z, -size.z / 2, size.z / 2);
            return transform.TransformPoint(p);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.8f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(new Vector3(0, size.y / 2, 0), size);
        }
    }
}
