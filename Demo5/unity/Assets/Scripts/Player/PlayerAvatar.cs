// Drives a Humanoid model as the player's body from the headset and the two controllers. Adapted from the class's
// MultiInteractionExample2026 (AvatarDemo's HalfBodyAvatar), controllers only. Every frame, in LateUpdate:
//   1. reset the driven bones to their bind pose (so IK twist never accumulates),
//   2. hips: turn toward where the player faces (the head, pulled toward the hands when they're spread in front),
//      with a dead zone and lag,
//   3. chest: twist part of the way toward the head, so the torso turns,
//   4. move the whole avatar so its head bone sits where the headset is,
//   5. head bone copies the headset's rotation,
//   6. two-bone IK for each arm toward the controller's wrist, elbows bending toward a pole out and back,
//   7. fingers: relaxed, closing into a fist with the grip.
// The first time the headset is up off the floor, the avatar is scaled so its eyes are at the player's eye height.
// The model's Animator is disabled: the bones are posed here.
using UnityEngine;
using UnityEngine.InputSystem;
using Freestyle.Avatar;

namespace Freestyle.Player
{
    public class PlayerAvatar : MonoBehaviour
    {
        [Header("Inputs")]
        public Transform hmd;
        [Tooltip("The XR Origin: the floor, and the space the controllers' aim poses are in.")]
        public Transform trackingSpace;
        [Tooltip("The controllers' grip poses (TrackedPoseDriver).")]
        public Transform leftController, rightController;

        [Header("Bones")]
        public Transform head, chest;
        [Tooltip("Spine bones between the hips and the head; the torso twist is shared among them")]
        public Transform[] spine = new Transform[0];
        public Transform leftUpper, leftLower, leftHand, rightUpper, rightLower, rightHand;
        /// <summary>The middle of a closed mitten fist, in the hand's space: under the palm, between wrist and knuckles
        /// (where a held mic or spray can's handle goes).</summary>
        public static readonly Vector3 FistLocal = new(0f, -0.03f, 0.055f);

        /// <summary>The middle of that hand's fist, in the world.</summary>
        public Vector3 FistPoint(bool left)
        {
            var h = left ? leftHand : rightHand;
            return h ? h.TransformPoint(left ? new Vector3(-FistLocal.x, FistLocal.y, FistLocal.z) : FistLocal) : transform.position;
        }

        /// <summary>The hand is holding something (PlayerMic): its fist stays closed, whatever the grip.</summary>
        [System.NonSerialized] public bool holdLeft, holdRight;
        [Tooltip("Thighs: scaled to zero when legs are hidden")]
        public Transform[] legs = new Transform[0];
        public bool hideLegs;
        [Tooltip("Per finger (thumb, index, middle, ring, little), three bones each. Optional.")]
        public Transform[] leftFingers = new Transform[15], rightFingers = new Transform[15];
        [Header("Mitten hands (instead of a Humanoid's fingers)")]
        [Tooltip("The mitten's finger joints, knuckle first: each curls about its own X (toward the palm, -Y) with the grip.")]
        public Transform[] leftMitten = new Transform[0], rightMitten = new Transform[0];
        public Transform leftThumb, rightThumb;
        [Tooltip("Degrees each mitten joint bends: open, and with the grip fully squeezed.")]
        public float mittenOpen = 8f, mittenClosed = 65f, thumbClosed = 35f;

        [Header("Fit")]
        public Vector3 hmdToHead = new(0f, -0.08f, -0.09f);
        public Vector3 elbowPole = new(0.25f, -0.5f, -0.35f);
        [Tooltip("Controller aim pose -> wrist (the wrist sits behind the grip)")]
        public Vector3 wristOffset = new(0f, -0.02f, -0.08f);
        [Tooltip("Scale the avatar to the player's eye height the first time the headset is tracked")]
        public bool fitHeight = true;
        [Tooltip("The arms' length next to the avatar's own (upper arm and forearm both scaled): longer when your hands reach " +
                 "further than its do. The settings menu sets it.")]
        [Range(0.7f, 1.4f)] public float armLength = 1f;
        [Tooltip("Learn the arms' length: when a hand reaches further than the arm can (held a moment), the arm grows to fit; " +
                 "it never shrinks, and a reach too long to be real (lost tracking) is ignored. Setting Arm Length in the " +
                 "menu turns it off.")]
        public bool autoArmLength = true;
        const float LearnAfter = 0.2f, MaxLearned = 1.4f;
        float stretched;   // how long a hand has been reaching past the arm

        [Header("Torso turning")]
        public float deadZone = 10f, slowDegPerSec = 60f, fastDegPerSec = 360f, fastAfter = 60f;
        [Range(0f, 1f)] public float chestTwist = 0.6f;
        public float maxTwist = 50f;
        [Range(0f, 1f)] public float handsWeight = 0.6f;

        [Header("Untracked hands")]
        [Tooltip("An arm whose controller isn't tracked holds its last pose this long, then eases down to the side (s)")]
        public float holdTime = 0.4f, easeTime = 0.5f;

        /// <summary>Eye height above the avatar's feet at the current scale (m).</summary>
        public float EyeHeight => avatarEyeHeight * transform.localScale.y;

        /// <summary>Sizes the avatar for this eye height (m) instead of measuring it (the settings menu's Height).</summary>
        public void SetEyeHeight(float eye)
        {
            pendingEye = eye;
            if (avatarEyeHeight <= 0) return;   // (not started yet: done in Start)
            transform.localScale = Vector3.one * Mathf.Clamp(eye / avatarEyeHeight, 0.6f, 1.6f);
            fitted = true;
            pendingEye = 0f;
        }

        struct HandPose { public bool tracked; public Vector3 wrist, fingers, palm; public float grip; }
        class ArmState { public Vector3 wrist, fingers, palm; public bool seen; public float lost = 999f, rest = 1f; }
        readonly ArmState leftState = new(), rightState = new();

        // controller inputs, bound inline: aim (pointer) rotation, tracked, grip
        static InputAction Bind(string path) => new(binding: path);
        readonly InputAction[] aimRot = { Bind("<XRController>{LeftHand}/pointerRotation"), Bind("<XRController>{RightHand}/pointerRotation") };
        readonly InputAction[] isTracked = { Bind("<XRController>{LeftHand}/isTracked"), Bind("<XRController>{RightHand}/isTracked") };
        readonly InputAction[] grip = { Bind("<XRController>{LeftHand}/grip"), Bind("<XRController>{RightHand}/grip") };

        Transform[] driven;
        Quaternion[] bindLocal;
        Quaternion headRestRelRoot, lHandBind, rHandBind;
        float bindArmLength, hipsYaw, avatarEyeHeight, pendingEye;
        Transform[] armBones;
        Vector3[] armBind;   // the elbows' and wrists' bind positions (armLength scales them)
        bool yawInit, fitted;
        HandGrip lRelaxed, rRelaxed, lFist, rFist;
        Quaternion[] lMittenBind, rMittenBind;
        Quaternion lThumbBind, rThumbBind;

        void OnEnable() { foreach (var a in aimRot) a.Enable(); foreach (var a in isTracked) a.Enable(); foreach (var a in grip) a.Enable(); }
        void OnDisable() { foreach (var a in aimRot) a.Disable(); foreach (var a in isTracked) a.Disable(); foreach (var a in grip) a.Disable(); }

        void Start()
        {
            var anim = GetComponentInChildren<Animator>();
            if (anim && anim.isHuman)   // finger poses, made once from the bind pose
            {
                var relaxed = new HandGrip.Finger(0.35f, 0.35f, 0.35f);
                var fist = new HandGrip.Finger(-0.8f, -1f, -0.8f);
                lRelaxed = new HandGrip(anim, false, new HandGrip.Finger(0.3f, 0.3f, 0.3f), relaxed, relaxed, relaxed, relaxed);
                rRelaxed = new HandGrip(anim, true, new HandGrip.Finger(0.3f, 0.3f, 0.3f), relaxed, relaxed, relaxed, relaxed);
                lFist = new HandGrip(anim, false, new HandGrip.Finger(-0.2f, -0.6f, -0.6f, -0.3f), fist, fist, fist, fist);
                rFist = new HandGrip(anim, true, new HandGrip.Finger(-0.2f, -0.6f, -0.6f, -0.3f), fist, fist, fist, fist);
                anim.enabled = false;   // the bones are posed here
            }
            SetLegs();
            lMittenBind = System.Array.ConvertAll(leftMitten, t => t ? t.localRotation : Quaternion.identity);
            rMittenBind = System.Array.ConvertAll(rightMitten, t => t ? t.localRotation : Quaternion.identity);
            if (leftThumb) lThumbBind = leftThumb.localRotation;
            if (rightThumb) rThumbBind = rightThumb.localRotation;
            var list = new System.Collections.Generic.List<Transform> { head, chest, leftUpper, leftLower, leftHand, rightUpper, rightLower, rightHand };
            list.AddRange(spine);
            driven = new System.Collections.Generic.HashSet<Transform>(list.FindAll(t => t)).ToArrayFast();
            bindLocal = new Quaternion[driven.Length];
            for (int i = 0; i < driven.Length; i++) bindLocal[i] = driven[i].localRotation;
            headRestRelRoot = Quaternion.Inverse(transform.rotation) * head.rotation;
            lHandBind = HandFrame(leftHand, leftFingers, true);
            rHandBind = HandFrame(rightHand, rightFingers, false);
            Vector3 eye = head.position - transform.rotation * hmdToHead;
            avatarEyeHeight = transform.InverseTransformPoint(eye).y;   // at scale 1
            if (rightUpper) bindArmLength = (rightLower.position - rightUpper.position).magnitude + (rightHand.position - rightLower.position).magnitude;
            armBones = new[] { leftLower, leftHand, rightLower, rightHand };
            armBind = System.Array.ConvertAll(armBones, t => t ? t.localPosition : Vector3.zero);
            if (pendingEye > 0f) SetEyeHeight(pendingEye);
        }

        /// <summary>Shows or hides the legs (hideLegs).</summary>
        public void SetLegs()
        {
            foreach (var t in legs) if (t) t.localScale = hideLegs ? Vector3.zero : Vector3.one;
        }

        // the hand's frame in its own axes: fingers forward (wrist -> middle knuckle), palm up
        static Quaternion HandFrame(Transform hand, Transform[] fingers, bool isLeft)
        {
            if (hand == null) return Quaternion.identity;
            if (fingers[6] && fingers[3] && fingers[12])
            {
                Vector3 fw = fingers[6].position - hand.position;
                Vector3 across = fingers[3].position - fingers[12].position;
                Vector3 pw = Vector3.Cross(fw, across) * (isLeft ? -1f : 1f);
                return Quaternion.LookRotation(hand.InverseTransformDirection(fw), hand.InverseTransformDirection(pw));
            }
            return Quaternion.LookRotation(Vector3.forward, Vector3.down);
        }

        /// <summary>Make the whole avatar taller or shorter (the head stays pinned to the headset).</summary>
        public void ScaleBy(float factor) => transform.localScale = Vector3.one * Mathf.Clamp(transform.localScale.x * factor, 0.6f, 1.6f);

        /// <summary>Scale the avatar so its eyes are at the headset's height over the floor now (stand up straight).</summary>
        public void FitHeight()
        {
            if (!hmd || avatarEyeHeight <= 0) return;
            float floor = trackingSpace ? trackingSpace.position.y : 0f;
            float eye = hmd.position.y - floor;
            if (eye < 1.0f) return;   // not tracked yet, or sitting
            transform.localScale = Vector3.one * Mathf.Clamp(eye / avatarEyeHeight, 0.6f, 1.6f);
            fitted = true;
        }

        static float Yaw(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero; }

        HandPose Controller(int i, Transform controller, bool isLeft)
        {
            // a hand holding something of the game's (the mic on stage) keeps its fist closed round it
            float squeeze = (isLeft ? holdLeft : holdRight) ? 1f : grip[i].ReadValue<float>();
            var p = new HandPose { tracked = controller && isTracked[i].ReadValue<float>() > 0.5f, grip = squeeze };
            if (!p.tracked) return p;
            // the aim pose's forward is where the controller points, close to the knuckles' direction
            Quaternion q = aimRot[i].ReadValue<Quaternion>();
            Quaternion aim = Mathf.Abs(q.x) + Mathf.Abs(q.y) + Mathf.Abs(q.z) + Mathf.Abs(q.w) < 0.5f
                ? controller.rotation : trackingSpace ? trackingSpace.rotation * q : q;
            p.wrist = controller.position + aim * wristOffset;
            p.fingers = aim * Vector3.forward;
            Vector3 r = aim * Vector3.right, d = aim * Vector3.down;
            p.palm = ((isLeft ? r : -r) + d * 0.35f).normalized;   // palm in toward the middle, a little down
            return p;
        }

        void LateUpdate()
        {
            if (hmd == null || head == null || driven == null) return;
            if (fitHeight && !fitted) FitHeight();
            for (int i = 0; i < driven.Length; i++) driven[i].localRotation = bindLocal[i];
            if (armBones != null)
                for (int i = 0; i < armBones.Length; i++) if (armBones[i]) armBones[i].localPosition = armBind[i] * armLength;
            var l = Controller(0, leftController, true);
            var r = Controller(1, rightController, false);

            // 2. which way the body faces
            float headYaw = Yaw(Flat(hmd.forward) == Vector3.zero ? transform.forward : Flat(hmd.forward));
            Vector3 f = Flat(hmd.forward);
            if (l.tracked && r.tracked)
            {
                Vector3 handsFwd = Flat(Vector3.Cross(Flat(r.wrist - l.wrist), Vector3.up));
                // only trust the hands when they are spread and in front of the head
                if (handsFwd != Vector3.zero && (r.wrist - l.wrist).magnitude > 0.25f && Vector3.Dot(handsFwd, f) > 0f)
                    f = Flat(Vector3.Lerp(f, handsFwd, handsWeight));
            }
            float facing = f == Vector3.zero ? headYaw : Yaw(f);
            if (!yawInit) { hipsYaw = facing; yawInit = true; }
            float err = Mathf.Abs(Mathf.DeltaAngle(hipsYaw, facing));
            float rate = err <= deadZone ? 0f : err >= fastAfter ? fastDegPerSec : slowDegPerSec;
            hipsYaw = Mathf.MoveTowardsAngle(hipsYaw, facing, rate * Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, hipsYaw, 0f);

            // 3. torso twist: the spine turns part of the way toward the head
            float twist = Mathf.Clamp(Mathf.DeltaAngle(hipsYaw, headYaw) * chestTwist, -maxTwist, maxTwist);
            int n = spine.Length + (chest && System.Array.IndexOf(spine, chest) < 0 ? 1 : 0);
            if (n > 0)
            {
                var per = Quaternion.AngleAxis(twist / n, Vector3.up);
                foreach (var s in spine) if (s) s.rotation = per * s.rotation;
                if (chest && System.Array.IndexOf(spine, chest) < 0) chest.rotation = per * chest.rotation;
            }

            // 4. pin the head bone to the headset; 5. head orientation
            Vector3 headTarget = hmd.position + hmd.rotation * (hmdToHead * transform.localScale.x);
            transform.position += headTarget - head.position;
            head.rotation = hmd.rotation * headRestRelRoot;

            // 6-7. arms and fingers
            Arm(l, leftState, leftUpper, leftLower, leftHand, lHandBind, -1f);
            Arm(r, rightState, rightUpper, rightLower, rightHand, rHandBind, 1f);
            Fingers(lRelaxed, lFist, l.grip);
            Fingers(rRelaxed, rFist, r.grip);
            Mitten(leftMitten, lMittenBind, leftThumb, lThumbBind, l.grip);
            Mitten(rightMitten, rMittenBind, rightThumb, rThumbBind, r.grip);
        }

        void Mitten(Transform[] joints, Quaternion[] bind, Transform thumb, Quaternion thumbBind, float grip)
        {
            float g = Mathf.Clamp01(grip), bend = Mathf.Lerp(mittenOpen, mittenClosed, g);
            for (int i = 0; i < joints.Length; i++)
                if (joints[i]) joints[i].localRotation = bind[i] * Quaternion.Euler(bend, 0f, 0f);
            if (thumb) thumb.localRotation = thumbBind * Quaternion.Euler(thumbClosed * g, 0f, 0f);
        }

        static void Fingers(HandGrip relaxed, HandGrip fist, float grip)
        {
            relaxed?.Apply(1f);
            fist?.Apply(Mathf.Clamp01(grip));
        }

        /// <summary>The arm fits a hand reaching past it: armLength grows (smoothly) to the shoulder-to-hand distance.</summary>
        void Learn(Transform upper, Vector3 wrist, float scale)
        {
            if (!autoArmLength || bindArmLength <= 0f) return;
            float need = Vector3.Distance(upper.position, wrist) / (bindArmLength * scale);
            if (need > armLength * 1.02f && need < MaxLearned * 1.05f)
            {
                stretched += Time.deltaTime;
                if (stretched > LearnAfter) armLength = Mathf.MoveTowards(armLength, Mathf.Min(need, MaxLearned), Time.deltaTime * 0.6f);
            }
            else stretched = Mathf.Max(0f, stretched - Time.deltaTime * 0.5f);
        }

        void Arm(HandPose h, ArmState st, Transform upper, Transform lower, Transform hand, Quaternion handBind, float side)
        {
            if (upper == null || lower == null || hand == null) return;
            // untracked: keep the last pose for holdTime, then ease to hanging at the side; ease back in when tracked
            Vector3 lateral = transform.right * side;
            float s = transform.localScale.x, len = bindArmLength * s * armLength;
            Vector3 restWrist = upper.position - transform.up * (len * 0.95f) + transform.forward * (0.08f * s) + lateral * (0.04f * s);
            Vector3 restFingers = -transform.up, restPalm = -lateral;
            if (h.tracked) { st.wrist = h.wrist; st.fingers = h.fingers; st.palm = h.palm; st.seen = true; st.lost = 0f; Learn(upper, h.wrist, s); }
            else st.lost += Time.deltaTime;
            if (!st.seen) { st.wrist = restWrist; st.fingers = restFingers; st.palm = restPalm; }
            float target = h.tracked ? 0f : st.lost > holdTime ? 1f : st.rest;
            st.rest = Mathf.MoveTowards(st.rest, target, Time.deltaTime / Mathf.Max(0.01f, easeTime));
            float k = Mathf.SmoothStep(0f, 1f, st.rest);
            Vector3 wrist = Vector3.Lerp(st.wrist, restWrist, k);
            Vector3 fwd = Vector3.Slerp(st.fingers, restFingers, k), palm = Vector3.Slerp(st.palm, restPalm, k);

            Vector3 pole = upper.position + transform.rotation * Vector3.Scale(elbowPole, new Vector3(side, 1f, 1f)) * s;
            TwoBoneIK.Solve(upper, lower, hand, wrist, pole);
            hand.rotation = Quaternion.LookRotation(fwd, palm) * Quaternion.Inverse(handBind);
        }
    }

    static class HashSetExt
    {
        public static T[] ToArrayFast<T>(this System.Collections.Generic.HashSet<T> s) { var a = new T[s.Count]; s.CopyTo(a); return a; }
    }
}
