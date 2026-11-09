// The interviewer's props: a clipboard carried on the left forearm, and a pencil pinched in the right hand. The left
// arm is posed the way people carry one: the upper arm hanging by the side, the elbow bent about square, the forearm
// out in front and rising, the hand tipped back, palm toward her. The board stands on the flat fingers, held near its
// bottom, its left edge in the web of the thumb and the thumb over it pinning it; its face to her, its back to whoever
// she's talking to. The arm is placed by the Animator's hand IK (the directions turn with the chest,
// so a leaning or turning clip carries it along) and the hand turned here; the right hand keeps gesturing from the
// clip, the pencil along its index finger. Both props are made from primitives at Start unless assigned.
// Needs the Animator's layer to have IK Pass on.
using System.Linq;
using UnityEngine;

namespace Freestyle.Avatar
{
    [RequireComponent(typeof(Animator))]
    public class HoldClipboard : MonoBehaviour
    {
        [Tooltip("The clipboard. Empty: a simple one is made. Local axes: +X across (from the held edge), +Y out of " +
                 "its face, +Z toward the clip; its origin the middle of the held (left) edge.")]
        public Transform clipboard;
        [Tooltip("The pencil. Empty: a simple one is made. Local +Y toward the point; its origin where the fingers pinch.")]
        public Transform pencil;
        [Range(0f, 1f)] public float weight = 1f;

        [Header("Left arm (directions in the character's right / up / forward)")]
        [Tooltip("Shoulder to elbow: hanging by the side, a little out and forward.")]
        public Vector3 upperArmDir = new(-0.2f, -1f, 0.2f);
        [Tooltip("Elbow to wrist: out in front, rising, only a little in toward the middle (the board stays clear of the body).")]
        public Vector3 forearmDir = new(0.12f, 0.45f, 1f);
        [Header("Clipboard")]
        [Tooltip("The board on the hand, in its axes (m): x how far its held (left) edge sits inside the thumb's middle joint " +
                 "(so the thumb comes up beside the edge and folds over it); y how far its middle sits above the highest finger " +
                 "joint (a finger's half thickness and half the board's); z along the fingers from the middle knuckle (held " +
                 "near its bottom: most of it stands above the hand).")]
        public Vector3 boardOnHand = new(0.012f, 0.012f, 0.1f);
        [Tooltip("How far the hand (and the board on it) tips back up from the line of the forearm (degrees): the board " +
                 "stands up facing her, its back to whoever she's talking to.")]
        public float wristBack = 30f;
        [Tooltip("How far the face turns in toward her middle (degrees about its length).")]
        public float roll = 20f;

        [Header("Pencil")]
        [Tooltip("How far the point sticks out past the index fingertip (m).")]
        public float pointPastFinger = 0.025f;

        public const float BoardW = 0.23f, BoardL = 0.32f, PencilL = 0.16f;

        Animator anim;
        Transform handL, middleL, middleTipL, thumbL, elbowL;
        Transform[] fingersL;
        Transform shoulderL, chest, indexL, littleL, thumbR, indexR1, indexR3;
        Vector3 acrossLocal, fingersLocal;
        float upperLen = 0.28f, foreLen = 0.25f;
        Quaternion chestRestRot;
        HandGrip gripL, pinchR;

        void Start()
        {
            anim = GetComponent<Animator>();
            handL = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            shoulderL = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            elbowL = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            if (shoulderL && elbowL && handL)
            {
                upperLen = Vector3.Distance(shoulderL.position, elbowL.position);
                foreLen = Vector3.Distance(elbowL.position, handL.position);
            }
            chest = anim.GetBoneTransform(HumanBodyBones.UpperChest);
            if (!chest) chest = anim.GetBoneTransform(HumanBodyBones.Chest);
            if (chest) chestRestRot = Quaternion.Inverse(transform.rotation) * chest.rotation;
            indexL = anim.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
            littleL = anim.GetBoneTransform(HumanBodyBones.LeftLittleProximal);
            middleL = anim.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            middleTipL = anim.GetBoneTransform(HumanBodyBones.LeftMiddleDistal);
            thumbL = anim.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate);
            fingersL = new[]
            {
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
                HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
                HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
                HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
            }.Select(b => anim.GetBoneTransform(b)).Where(t => t).ToArray();
            if (handL && indexL && littleL && middleL)
            {
                acrossLocal = handL.InverseTransformDirection(indexL.position - littleL.position).normalized;
                fingersLocal = handL.InverseTransformDirection(middleL.position - handL.position).normalized;
            }
            thumbR = anim.GetBoneTransform(HumanBodyBones.RightThumbDistal);
            indexR1 = anim.GetBoneTransform(HumanBodyBones.RightIndexProximal);
            indexR3 = anim.GetBoneTransform(HumanBodyBones.RightIndexDistal);

            // left: the fingers flat under the board, the thumb curled up over its edge, pinning it; right: a writing pinch
            // (a Humanoid finger muscle at 0 is half bent; +1 is straight)
            var flat = new HandGrip.Finger(1f, 1f, 1f);
            gripL = new HandGrip(anim, false, new HandGrip.Finger(-1f, -1f, -1f, -0.8f), flat, flat, flat, flat);
            var curled = new HandGrip.Finger(-0.8f, -1f, -0.8f);
            pinchR = new HandGrip(anim, true, new HandGrip.Finger(-0.3f, -0.4f, -0.3f, -0.2f),
                                  new HandGrip.Finger(-0.4f, -0.5f, -0.4f), new HandGrip.Finger(-0.6f, -0.7f, -0.5f), curled, curled);
            if (!clipboard) clipboard = MakeClipboard();
            if (!pencil) pencil = MakePencil();
        }

        Quaternion ChestTurn() => chest ? chest.rotation * Quaternion.Inverse(transform.rotation * chestRestRot) : Quaternion.identity;

        Quaternion Body() => ChestTurn() * transform.rotation;

        /// <summary>Where the elbow and wrist go now, and the forearm's direction.</summary>
        void Arm(out Vector3 elbow, out Vector3 wrist, out Vector3 fore)
        {
            var body = Body();
            elbow = shoulderL.position + body * upperArmDir.normalized * upperLen;
            fore = body * forearmDir.normalized;
            wrist = elbow + fore * foreLen;
        }

        /// <summary>The board along the forearm (+Z forward along it, to the clip), its face up, tipped in toward her.</summary>
        Quaternion BoardRotation(Vector3 fore)
        {
            var up = Vector3.ProjectOnPlane(Body() * Vector3.up, fore);
            var b = Quaternion.LookRotation(fore, up) * Quaternion.Euler(-wristBack, 0, 0);   // tipped up from the forearm
            return Quaternion.AngleAxis(-roll, b * Vector3.forward) * b;   // the right side down: facing her
        }

        /// <summary>The left hand flat under the board, in line with the forearm: the fingers along it, palm up (so the
        /// index is on the outside, little to index toward the board's -X).</summary>
        bool HandRotation(Vector3 fore, out Quaternion rot)
        {
            rot = Quaternion.identity;
            var b = BoardRotation(fore);
            var localFingers = Vector3.ProjectOnPlane(fingersLocal, acrossLocal).normalized;
            if (localFingers.sqrMagnitude < 0.5f) return false;
            rot = Quaternion.LookRotation(b * Vector3.forward, b * Vector3.left) * Quaternion.Inverse(Quaternion.LookRotation(localFingers, acrossLocal));
            return true;
        }

        void OnAnimatorIK(int layer)
        {
            if (!handL || !shoulderL) return;
            Arm(out var elbow, out var wrist, out _);
            anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, weight);
            anim.SetIKPosition(AvatarIKGoal.LeftHand, wrist);
            anim.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, weight);
            anim.SetIKHintPosition(AvatarIKHint.LeftElbow, elbow + Body() * new Vector3(-0.1f, 0f, -0.1f));   // out and back
        }

        void LateUpdate()
        {
            if (handL && shoulderL && clipboard)
            {
                gripL?.Apply(weight);   // (null after a script reload in Play mode: not serialized)
                Arm(out _, out _, out var fore);
                var board = BoardRotation(fore);
                if (HandRotation(fore, out var rot))
                {
                    handL.rotation = Quaternion.Slerp(handL.rotation, rot, weight);
                    // the straight fingers don't run quite along wrist -> knuckle: turn the hand so they lie along the board
                    if (middleL && middleTipL)
                    {
                        var fix = Quaternion.FromToRotation(middleTipL.position - middleL.position, board * Vector3.forward);
                        handL.rotation = Quaternion.Slerp(Quaternion.identity, fix, weight) * handL.rotation;
                    }
                }
                // lying on the fingers as they are now (after IK): just above the highest finger joint, its left edge in the
                // web of the thumb
                var knuckles = middleL ? middleL.position : handL.position;
                Vector3 right = board * Vector3.right, up = board * Vector3.up;
                float edgeX = (thumbL ? Vector3.Dot(thumbL.position - knuckles, right) : -0.05f) + boardOnHand.x;
                float top = 0;
                foreach (var f in fingersL) top = Mathf.Max(top, Vector3.Dot(f.position - knuckles, up));
                clipboard.SetPositionAndRotation(knuckles + right * edgeX + up * (top + boardOnHand.y) + board * Vector3.forward * boardOnHand.z, board);
            }
            if (pencil && thumbR && indexR1 && indexR3)
            {
                pinchR?.Apply(1f);
                var along = (indexR3.position - indexR1.position).normalized;
                var pinch = (thumbR.position + indexR3.position) / 2;
                var side = Vector3.Cross(along, pinch - indexR1.position);
                pencil.SetPositionAndRotation(pinch, side.sqrMagnitude > 1e-6f
                    ? Quaternion.LookRotation(side, along) : Quaternion.FromToRotation(Vector3.up, along));
                // the point a little past the fingertip
                pencil.position += along * (Vector3.Dot(indexR3.position - pinch, along) + pointPastFinger - PencilL * 0.3f);
            }
        }

        Transform MakeClipboard()
        {
            var root = new GameObject("Clipboard").transform;
            root.SetParent(transform, false);
            // origin: the middle of the held (left) edge; the board runs +X across and +Z to the clip
            Part(PrimitiveType.Cube, root, new Vector3(BoardW / 2, 0, 0), new Vector3(BoardW, 0.006f, BoardL), new Color(0.45f, 0.3f, 0.17f), 0.2f, 0f);
            Part(PrimitiveType.Cube, root, new Vector3(BoardW / 2, 0.0045f, -0.012f), new Vector3(BoardW - 0.02f, 0.002f, BoardL - 0.05f), new Color(0.93f, 0.93f, 0.9f), 0.1f, 0f);
            Part(PrimitiveType.Cube, root, new Vector3(BoardW / 2, 0.009f, BoardL / 2 - 0.025f), new Vector3(0.1f, 0.012f, 0.035f), new Color(0.7f, 0.7f, 0.72f), 0.6f, 1f);
            // a few ruled lines of notes
            for (int i = 0; i < 6; i++)
                Part(PrimitiveType.Cube, root, new Vector3(BoardW / 2 - 0.01f * (i % 2), 0.0058f, 0.08f - i * 0.035f),
                     new Vector3(BoardW - 0.06f - 0.03f * (i % 3), 0.0005f, 0.003f), new Color(0.25f, 0.25f, 0.3f), 0.1f, 0f);
            return root;
        }

        Transform MakePencil()
        {
            var root = new GameObject("Pencil").transform;
            root.SetParent(transform, false);
            // along +Y: the eraser end at -0.7 L, the point at +0.3 L (the fingers pinch nearer the point)
            float lo = -PencilL * 0.7f, hi = PencilL * 0.3f;
            void Seg(float a, float b, float radius, Color col, float smooth, float metal) =>
                Part(PrimitiveType.Cylinder, root, new Vector3(0, (a + b) / 2, 0), new Vector3(radius * 2, (b - a) / 2, radius * 2), col, smooth, metal);
            Seg(lo, lo + 0.01f, 0.0037f, new Color(0.9f, 0.5f, 0.55f), 0.1f, 0f);            // eraser
            Seg(lo + 0.01f, lo + 0.016f, 0.0039f, new Color(0.75f, 0.75f, 0.7f), 0.6f, 1f);   // ferrule
            Seg(lo + 0.016f, hi - 0.022f, 0.0037f, new Color(0.95f, 0.75f, 0.1f), 0.3f, 0f);  // yellow wood
            Seg(hi - 0.022f, hi - 0.005f, 0.0024f, new Color(0.85f, 0.7f, 0.5f), 0.2f, 0f);   // sharpened wood
            Seg(hi - 0.005f, hi, 0.001f, new Color(0.15f, 0.15f, 0.15f), 0.4f, 0f);           // graphite
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
