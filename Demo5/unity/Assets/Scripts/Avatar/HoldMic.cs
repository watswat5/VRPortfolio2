// A handheld mic in a Humanoid's right hand, the fingers closed around it. Listening: it rides in the fist wherever
// the animation puts the arm. Rapping: the fist comes up in front of the mouth (the Animator's hand IK places it,
// with the elbow kept out to the side so the arm clears the chest; the hand is then turned here so the mic points
// at the lips: the IK's own hand rotation is in the avatar's normalized space, not the bone's);
// the other hand keeps gesturing from the clip. Blends between the two. Set rapping from the game (the opponent's
// turn) or a test. With no mic assigned it builds a simple one (black handle, silver grille).
// Needs the Animator's layer to have IK Pass on.
using UnityEngine;

namespace Freestyle.Avatar
{
    [RequireComponent(typeof(Animator))]
    public class HoldMic : MonoBehaviour
    {
        public enum Pose { Standing, Ready, Rapping }
        [Tooltip("Rapping: at the mouth. Ready: lowered in front of the chest (listening, between lines). " +
                 "Standing: the arm hangs, the animation moves it (out of a battle).")]
        public Pose pose = Pose.Ready;
        /// <summary>The old switch: rapping, or ready.</summary>
        public bool rapping { get => pose == Pose.Rapping; set => pose = value ? Pose.Rapping : Pose.Ready; }
        [Tooltip("The mic. Empty: a simple one is made. Its local +Y should point from the handle to the grille.")]
        public Transform mic;
        [Tooltip("Off: the mic is put down (the DJ's, on the table): the hand is the animation's, open, and the mic is " +
                 "left where it is.")]
        public bool held = true;
        [Tooltip("Seconds to bring the mic up or down.")]
        public float blendTime = 0.35f;

        [Header("Rapping (m and directions in the character's right / up / forward)")]
        [Tooltip("Where the mic's grille goes, from the mouth: just in front of the lips.")]
        public Vector3 grilleFromMouth = new(0f, -0.02f, 0.06f);
        [Tooltip("Which way the mic points while rapping: up and back, at the mouth.")]
        public Vector3 rappingMicDir = new(0f, 0.55f, -0.85f);
        [Header("Ready (m, in the character's right / up / forward)")]
        [Tooltip("Where the fist waits, from the mouth as the character stands at rest: lowered in front of the stomach. " +
                 "It moves with the chest (so a leaning or twisting clip carries it along), and the wrist stays locked at " +
                 "the angle it had at the mouth, so the mic follows the forearm down and back up.")]
        public Vector3 readyFromMouth = new(0.04f, -0.5f, 0.12f);
        [Tooltip("Where the elbow is steered while ready, from the right shoulder: relaxed, down by the side.")]
        public Vector3 readyElbowFromShoulder = new(0.12f, -0.3f, -0.08f);
        [Tooltip("The mouth, from the head bone (Rocketbox's sits low in the skull).")]
        public Vector3 mouthFromHead = new(0f, 0.02f, 0.1f);
        [Tooltip("Where the elbow is steered while rapping, from the right shoulder: out and down, so the forearm clears the chest.")]
        public Vector3 elbowFromShoulder = new(0.3f, -0.3f, 0.05f);
        [Tooltip("Which way the curled fingers point while rapping: mostly across the body (-right), a little forward.")]
        public Vector3 fingersWhileRapping = new(-0.85f, 0f, 0.5f);

        [Header("Grip")]
        [Tooltip("How far the fingers close from the animation's hand to the grip (1: the full grip).")]
        [Range(0f, 1f)] public float curl = 0.8f;
        [Tooltip("Nudges the mic inside the fist (m, along the hand's palm-to-knuckle, across, and fingertip directions).")]
        public Vector3 gripOffset = Vector3.zero;
        [Tooltip("The handle's centre to the grille's centre (m).")]
        public float handleToGrille = 0.1f;

        Animator anim;
        Transform hand, head, shoulder, indexK, littleK;
        Transform[] ring;                       // the middle three fingers' joints the handle sits inside
        Vector3 acrossLocal, fingersLocal;      // in the hand's own axes: little -> index knuckle, wrist -> knuckles
        float w, k;   // how far the hand is held up (Ready or Rapping vs Standing)
        float r, kr;  // how far from Ready to Rapping
        Vector3 grilleLocal = new(0, 0.09f, 0);   // the grille, from the hand bone in its own axes (measured each frame)
        Transform forearm, chest;
        Vector3 chestRestPos, mouthRestPos;        // at rest (the bind pose), in the character's own space
        Quaternion chestRestRot;
        Quaternion wristLock;                      // the hand in the forearm's axes, as it was at the mouth
        bool locked;                               // ...once it has been there

        void Start()
        {
            anim = GetComponent<Animator>();
            hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            head = anim.GetBoneTransform(HumanBodyBones.Head);
            shoulder = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            forearm = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            chest = anim.GetBoneTransform(HumanBodyBones.UpperChest);
            if (!chest) chest = anim.GetBoneTransform(HumanBodyBones.Chest);   // not every rig maps an upper chest
            if (chest && head)
            {
                chestRestPos = transform.InverseTransformPoint(chest.position);
                chestRestRot = Quaternion.Inverse(transform.rotation) * chest.rotation;
                mouthRestPos = transform.InverseTransformPoint(Mouth);
            }
            indexK = anim.GetBoneTransform(HumanBodyBones.RightIndexProximal);
            littleK = anim.GetBoneTransform(HumanBodyBones.RightLittleProximal);
            var middleK = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            ring = new[]
            {
                indexK, anim.GetBoneTransform(HumanBodyBones.RightIndexIntermediate), anim.GetBoneTransform(HumanBodyBones.RightIndexDistal),
                middleK, anim.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate), anim.GetBoneTransform(HumanBodyBones.RightMiddleDistal),
                anim.GetBoneTransform(HumanBodyBones.RightRingProximal), anim.GetBoneTransform(HumanBodyBones.RightRingIntermediate),
                anim.GetBoneTransform(HumanBodyBones.RightRingDistal),
            };
            if (hand && indexK && littleK && middleK)
            {
                acrossLocal = hand.InverseTransformDirection(indexK.position - littleK.position).normalized;
                fingersLocal = hand.InverseTransformDirection(middleK.position - hand.position).normalized;
            }
            SetUpFingers();
            if (!mic) mic = MakeMic();
        }

        Vector3 Mouth => head.position + transform.rotation * mouthFromHead;

        void OnAnimatorIK(int layer)
        {
            if (!hand || !head) return;
            if (!held)
            {
                w = r = 0;   // picked up again: up from the side, as from standing
                anim.SetIKPositionWeight(AvatarIKGoal.RightHand, 0);
                anim.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0);
                return;
            }
            float step = Time.deltaTime / Mathf.Max(blendTime, 0.01f);
            w = Mathf.MoveTowards(w, pose == Pose.Standing ? 0f : 1f, step);
            r = Mathf.MoveTowards(r, pose == Pose.Rapping ? 1f : 0f, step);
            k = Mathf.SmoothStep(0, 1, w); kr = Mathf.SmoothStep(0, 1, r);
            // rapping: the wrist goes where it puts the grille (not itself) just in front of the lips, the hand turned as
            // LateUpdate turns it; ready: the fist itself, close to the chest
            var ready = ReadySpot();
            var grille = Mouth + transform.rotation * grilleFromMouth;
            var atMouth = RappingRotation(out var rot) ? grille - rot * grilleLocal : grille;
            anim.SetIKPositionWeight(AvatarIKGoal.RightHand, k);
            anim.SetIKPosition(AvatarIKGoal.RightHand, Vector3.Lerp(ready, atMouth, kr));
            if (shoulder)
            {
                anim.SetIKHintPositionWeight(AvatarIKHint.RightElbow, k);
                anim.SetIKHintPosition(AvatarIKHint.RightElbow,
                    shoulder.position + Lerp(ChestTurn() * transform.rotation * readyElbowFromShoulder, transform.rotation * elbowFromShoulder, kr));
            }
        }

        // after the animation and IK: the fingers closed; while rapping the fist turned upright (the mic, along the
        // knuckles, toward the mouth; the curled fingers across the body); then the mic inside the fingers, the grille
        // out of the thumb side
        void LateUpdate()
        {
            if (!mic || !hand || !held) return;
            CloseFingers();
            if (k > 0 && head)
            {
                // ready: the wrist as it was at the mouth (the animation's, until it has been there); rapping: at the lips
                var ready = locked && forearm ? forearm.rotation * wristLock : hand.rotation;
                var target = RappingRotation(out var rot) ? Quaternion.Slerp(ready, rot, kr) : ready;
                hand.rotation = Quaternion.Slerp(hand.rotation, target, k);
                if (kr > 0.95f && forearm) { wristLock = Quaternion.Inverse(forearm.rotation) * hand.rotation; locked = true; }
            }
            Vector3 c = Vector3.zero; int n = 0;
            foreach (var t in ring) if (t) { c += t.position; n++; }
            var across = indexK && littleK ? (indexK.position - littleK.position).normalized : hand.right;
            var toKnuckles = n > 0 ? (c / n - hand.position) : hand.up * 0.08f;
            var tips = Vector3.Cross(across, toKnuckles).normalized;
            var grip = (n > 0 ? c / n : hand.position) + toKnuckles.normalized * gripOffset.x + across * gripOffset.y + tips * gripOffset.z;
            mic.SetPositionAndRotation(grip, Quaternion.FromToRotation(Vector3.up, across));
            grilleLocal = Quaternion.Inverse(hand.rotation) * (grip + across * handleToGrille - hand.position);
        }

        /// <summary>How far the chest has turned from rest (a leaning or twisting clip), in world space.</summary>
        Quaternion ChestTurn() => chest ? chest.rotation * Quaternion.Inverse(transform.rotation * chestRestRot) : Quaternion.identity;

        static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Vector3.Lerp(a, b, t);

        /// <summary>The ready spot: readyFromMouth on the character at rest, carried along by the chest as it moves now.</summary>
        Vector3 ReadySpot()
        {
            if (!chest) return Mouth + transform.rotation * readyFromMouth;
            var restSpot = mouthRestPos + readyFromMouth - chestRestPos;   // from the chest at rest, in the character's space
            return chest.position + ChestTurn() * (transform.rotation * restSpot);
        }

        /// <summary>The hand's rotation at the mouth: the mic (along the knuckles) pointing at the lips, the curled fingers
        /// across the body.</summary>
        bool RappingRotation(out Quaternion rot)
        {
            rot = Quaternion.identity;
            var micDir = (transform.rotation * rappingMicDir).normalized;
            var fingers = Vector3.ProjectOnPlane(transform.rotation * fingersWhileRapping, micDir).normalized;
            var localFingers = Vector3.ProjectOnPlane(fingersLocal, acrossLocal).normalized;
            if (fingers.sqrMagnitude < 0.5f || localFingers.sqrMagnitude < 0.5f) return false;
            rot = Quaternion.LookRotation(fingers, micDir) * Quaternion.Inverse(Quaternion.LookRotation(localFingers, acrossLocal));
            return true;
        }

        // ---- fingers: closed around the handle (made once at Start, see HandGrip) ---------------------------------
        HandGrip fist;

        void SetUpFingers()
        {
            var f = new HandGrip.Finger(-0.8f, -1f, -0.8f);
            fist = new HandGrip(anim, true, new HandGrip.Finger(-0.2f, -0.6f, -0.6f, -0.3f), f, f, f, f);
        }

        void CloseFingers() => fist?.Apply(curl);

        Transform MakeMic()
        {
            var root = new GameObject("Mic").transform;
            root.SetParent(transform, false);
            Part(PrimitiveType.Cylinder, root, new Vector3(0, -0.01f, 0), new Vector3(0.032f, 0.075f, 0.032f), new Color(0.05f, 0.05f, 0.05f), 0.4f, 0f);
            Part(PrimitiveType.Cylinder, root, new Vector3(0, handleToGrille - 0.035f, 0), new Vector3(0.04f, 0.012f, 0.04f), new Color(0.15f, 0.15f, 0.15f), 0.6f, 0.8f);
            Part(PrimitiveType.Sphere, root, new Vector3(0, handleToGrille, 0), Vector3.one * 0.055f, new Color(0.7f, 0.7f, 0.72f), 0.55f, 1f);
            return root;
        }

        static void Part(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Color color, float smooth, float metal)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(pos, Quaternion.identity);
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader)
            {
                var m = new Material(shader);
                m.SetColor("_BaseColor", color); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", metal);
                r.sharedMaterial = m;
            }
        }
    }
}
