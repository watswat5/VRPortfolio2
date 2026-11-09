// Keeps a Humanoid character alive on top of its idle animation: eyes on a target, the camera by default (the
// eye bones aimed here, after the animation, from their rest pose and capped, so they can't roll round; the head
// turns partway through the Animator's look-at IK); and blinks now and then, on the face's ARKit EyeBlinkLeft/Right
// blendshapes. Starts its idle at a random point so a row of characters doesn't move in step.
// Needs the Animator's layer to have IK Pass on, and the eyes left out of the Humanoid rig (Rocketbox's eye bones
// don't match Unity's eye axes: mapped, the animation rolls the eyeballs back).
using UnityEngine;

namespace Freestyle.Avatar
{
    [RequireComponent(typeof(Animator))]
    public class LookAndBlink : MonoBehaviour
    {
        [Tooltip("What to look at. Empty: the main camera.")]
        public Transform target;
        [Range(0f, 1f)] public float bodyWeight = 0.05f, headWeight = 0.45f;
        [Tooltip("The eye bones (found by name if empty: Rocketbox's Bip01 LEye / REye).")]
        public Transform eyeL, eyeR;
        [Tooltip("How far the eyes turn from straight ahead in the head (degrees).")]
        public float maxEyeAngle = 30f;
        [Tooltip("How far (0-1) the look can turn the head and body toward an extreme angle.")]
        [Range(0f, 1f)] public float clampWeight = 0.6f;
        [Tooltip("Past this angle from straight ahead (degrees), stop looking (the target is behind them).")]
        public float maxAngle = 80f;
        [Tooltip("Seconds to turn the look on or off.")]
        public float ease = 0.4f;

        [Header("Blinks")]
        public SkinnedMeshRenderer face;
        public Vector2 blinkEvery = new(2.5f, 6f);
        public float blinkTime = 0.14f;

        Animator anim;
        Transform head;
        float look, nextBlink, blinkStart = -1;
        Quaternion restL, restR;      // the eyes' local rotations at rest
        Vector3 aheadL, aheadR;       // straight ahead, in each eye's own axes
        int blinkL = -1, blinkR = -1;

        void Start()
        {
            anim = GetComponent<Animator>();
            head = anim.GetBoneTransform(HumanBodyBones.Head);
            foreach (var t in GetComponentsInChildren<Transform>())
            {
                if (!eyeL && t.name == "Bip01 LEye") eyeL = t;
                if (!eyeR && t.name == "Bip01 REye") eyeR = t;
            }
            // at rest the eyes look the way the character faces
            if (eyeL) { restL = eyeL.localRotation; aheadL = Quaternion.Inverse(eyeL.rotation) * transform.forward; }
            if (eyeR) { restR = eyeR.localRotation; aheadR = Quaternion.Inverse(eyeR.rotation) * transform.forward; }
            if (anim.runtimeAnimatorController) anim.Play(anim.GetCurrentAnimatorStateInfo(0).fullPathHash, 0, Random.value);
            if (face)
            {
                var mesh = face.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string n = mesh.GetBlendShapeName(i);
                    if (n.EndsWith("_EyeBlinkLeft")) blinkL = i;
                    if (n.EndsWith("_EyeBlinkRight")) blinkR = i;
                }
            }
            nextBlink = Time.time + Random.Range(0.5f, blinkEvery.y);
        }

        Transform Target => target ? target : Camera.main ? Camera.main.transform : null;

        void OnAnimatorIK(int layer)
        {
            if (!anim) return;   // spawned this frame: the animator can run before Start
            var t = Target;
            bool wants = t && head && Vector3.Angle(transform.forward, t.position - head.position) < maxAngle;
            look = Mathf.MoveTowards(look, wants ? 1f : 0f, Time.deltaTime / Mathf.Max(ease, 0.01f));
            anim.SetLookAtWeight(Mathf.SmoothStep(0, 1, look), bodyWeight, headWeight, 0f, clampWeight);
            if (t) anim.SetLookAtPosition(t.position);
        }

        void LateUpdate()
        {
            Aim(eyeL, restL, aheadL);
            Aim(eyeR, restR, aheadR);
            Blink();
        }

        /// <summary>The eye back to rest (the animation doesn't move it), then turned toward the target, capped.</summary>
        void Aim(Transform eye, Quaternion rest, Vector3 ahead)
        {
            if (!eye) return;
            eye.localRotation = rest;
            var t = Target;
            if (!t || look <= 0) return;
            Vector3 now = eye.rotation * ahead, want = (t.position - eye.position).normalized;
            var turn = Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(now, want), maxEyeAngle);
            eye.rotation = Quaternion.Slerp(Quaternion.identity, turn, Mathf.SmoothStep(0, 1, look)) * eye.rotation;
        }

        void Blink()
        {
            if (!face || blinkL < 0) return;
            if (blinkStart < 0 && Time.time >= nextBlink) blinkStart = Time.time;
            float w = 0;
            if (blinkStart >= 0)
            {
                float p = (Time.time - blinkStart) / blinkTime;
                if (p >= 1) { blinkStart = -1; nextBlink = Time.time + Random.Range(blinkEvery.x, blinkEvery.y); }
                else w = Mathf.Sin(p * Mathf.PI) * 100f;   // shut and open again
            }
            face.SetBlendShapeWeight(blinkL, w);
            if (blinkR >= 0) face.SetBlendShapeWeight(blinkR, w);
        }
    }
}
