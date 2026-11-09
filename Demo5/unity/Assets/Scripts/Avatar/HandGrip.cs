// A hand closed into a grip on a Humanoid, on top of whatever the animation does with the fingers. The grip is made
// once: the hand's finger muscles set on the bind pose (negative "Stretched" closes a finger, checked on Rocketbox), the
// finger bones' local rotations kept, the skeleton put back. Apply() then blends only those bones toward the grip,
// each frame in LateUpdate (writing the whole Humanoid pose every frame turns a rotated character round).
using System.Collections.Generic;
using UnityEngine;

namespace Freestyle.Avatar
{
    public class HandGrip
    {
        /// <summary>How far each finger closes: base, middle and tip joints (0 open, -1 shut), and its spread.</summary>
        public struct Finger
        {
            public float j1, j2, j3, spread;
            public Finger(float j1, float j2, float j3, float spread = 0f) { this.j1 = j1; this.j2 = j2; this.j3 = j3; this.spread = spread; }
        }

        static readonly string[] Names = { "Thumb", "Index", "Middle", "Ring", "Little" };
        static readonly string[] Joints = { "Proximal", "Intermediate", "Distal" };

        readonly Transform[] bones;
        readonly Quaternion[] rotations;

        /// <summary>The grip with these fingers, in the order thumb, index, middle, ring, little.</summary>
        public HandGrip(Animator anim, bool right, params Finger[] fingers)
        {
            var bs = new List<Transform>(); var rs = new List<Quaternion>();
            bones = new Transform[0]; rotations = new Quaternion[0];
            if (!anim.avatar || !anim.avatar.isHuman) return;
            var root = anim.transform;
            var all = root.GetComponentsInChildren<Transform>();
            var saved = new (Vector3 p, Quaternion r)[all.Length];
            for (int i = 0; i < all.Length; i++) saved[i] = (all[i].localPosition, all[i].localRotation);

            string side = right ? "Right" : "Left";
            using (var poser = new HumanPoseHandler(anim.avatar, root))
            {
                var hp = new HumanPose();
                poser.GetHumanPose(ref hp);
                var names = HumanTrait.MuscleName;
                void Set(string muscle, float v) { int i = System.Array.IndexOf(names, muscle); if (i >= 0) hp.muscles[i] = v; }
                for (int f = 0; f < Names.Length && f < fingers.Length; f++)
                {
                    Set($"{side} {Names[f]} 1 Stretched", fingers[f].j1);
                    Set($"{side} {Names[f]} 2 Stretched", fingers[f].j2);
                    Set($"{side} {Names[f]} 3 Stretched", fingers[f].j3);
                    Set($"{side} {Names[f]} Spread", fingers[f].spread);
                }
                poser.SetHumanPose(ref hp);
            }
            foreach (var n in Names)
                foreach (var j in Joints)
                {
                    var t = anim.GetBoneTransform((HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), $"{side}{n}{j}"));
                    if (t) { bs.Add(t); rs.Add(t.localRotation); }
                }
            bones = bs.ToArray(); rotations = rs.ToArray();

            for (int i = 0; i < all.Length; i++) all[i].SetLocalPositionAndRotation(saved[i].p, saved[i].r);   // as it was
        }

        /// <summary>Blends the fingers toward the grip (0: the animation's, 1: the grip). Call in LateUpdate.</summary>
        public void Apply(float amount)
        {
            if (amount <= 0) return;
            for (int i = 0; i < bones.Length; i++)
                bones[i].localRotation = Quaternion.Slerp(bones[i].localRotation, rotations[i], amount);
        }
    }
}
