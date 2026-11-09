// The player robot's looks: one of three bodies and one of two heads (boxy, round), chosen independently, all on the same
// skeleton (PlayerAvatar drives it). Each body sets where the shoulders are and how thick the arms are, and has its own
// necklace spot on its torso (sized to it) and wrist size; each head has its own hat, eyes and ears spots. The Outfit's
// locations move to the shapes that are on, taking anything worn there along. Each head has its own spots for the hat, the eyes (shades) and the ears, so the
// Outfit's locations move to the head that's on, taking anything worn there along. The robot's description (with the
// paint, and what's worn) goes to the opponent through the Outfit.
// Swap with the right controller's A (next body) and B (next head); B and H on the keyboard.
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using Freestyle.Wardrobe;

namespace Freestyle.Player
{
    public class RobotLooks : MonoBehaviour
    {
        [Serializable]
        public class Part
        {
            public string name;            // in the opponent's description: "a boxy body", "a TV head"
            public GameObject visual;
        }

        [Serializable]
        public class Body : Part
        {
            [Tooltip("The right shoulder, in the chest bone's space (the left is mirrored).")]
            public Vector3 shoulder = new(0.19f, 0.12f, 0f);
            public float upperArm = 0.085f, forearm = 0.075f;
            [Tooltip("Where a necklace goes on this body: a child of its torso, scaled to fit it.")]
            public Transform neckSpot;
            [Tooltip("How big a bracelet is on this body's wrist (its forearm thickness).")]
            public float wristScale = 1f;
        }

        [Serializable]
        public class Head : Part
        {
            [Tooltip("Where the outfit's hat, eyes (shades) and ears go on this head: children of it.")]
            public Transform hatSpot, eyesSpot, earsSpot;
        }

        public Body[] bodies = new Body[0];
        public Transform leftShoulder, rightShoulder;
        public LimbVisual[] upperArms = new LimbVisual[0], forearms = new LimbVisual[0];
        public Head[] heads = new Head[0];
        public int body, head;
        public Outfit outfit;

        readonly InputAction nextBody = new(binding: "<XRController>{RightHand}/primaryButton");
        readonly InputAction nextHead = new(binding: "<XRController>{RightHand}/secondaryButton");

        void OnEnable() { nextBody.Enable(); nextHead.Enable(); }
        void OnDisable() { nextBody.Disable(); nextHead.Disable(); }

        void Start()
        {
            if (!outfit) outfit = GetComponent<Outfit>();
            Apply();
        }

        void Update()
        {
            var kb = Freestyle.UI.VRKeyboard.Typing ? null : Keyboard.current;   // (not while typing into a field)
            if (nextBody.WasPressedThisFrame() || kb != null && kb.bKey.wasPressedThisFrame) NextBody();
            if (nextHead.WasPressedThisFrame() || kb != null && kb.hKey.wasPressedThisFrame) NextHead();
        }

        public void NextBody() { body = (body + 1) % Mathf.Max(1, bodies.Length); Apply(); }
        public void PreviousBody() { body = (body + bodies.Length - 1) % Mathf.Max(1, bodies.Length); Apply(); }
        public string BodyName => bodies.Length > body ? bodies[body].name : "";
        public void NextHead() { head = (head + 1) % Mathf.Max(1, heads.Length); Apply(); }
        public void PreviousHead() { head = (head + heads.Length - 1) % Mathf.Max(1, heads.Length); Apply(); }
        public string HeadName => heads.Length > head ? heads[head].name : "";

        public void Apply()
        {
            // the new shapes on, the outfit's sockets moved onto them, then the old shapes off: a socket that is switched
            // off (with its shape) drops what it holds
            if (bodies.Length > 0 && bodies[body].visual) bodies[body].visual.SetActive(true);
            if (heads.Length > 0 && heads[head].visual) heads[head].visual.SetActive(true);
            if (bodies.Length > 0)
            {
                var b = bodies[body];
                if (rightShoulder) rightShoulder.localPosition = b.shoulder;
                if (leftShoulder) leftShoulder.localPosition = new Vector3(-b.shoulder.x, b.shoulder.y, b.shoulder.z);
                foreach (var l in upperArms) if (l) l.thickness = b.upperArm;
                foreach (var l in forearms) if (l) l.thickness = b.forearm;
                if (outfit)
                {
                    if (b.neckSpot) outfit.SetLocation(BodyLocation.Neck, b.neckSpot);
                    if (outfit.wrist) outfit.wrist.localScale = Vector3.one * b.wristScale;
                }
            }
            if (outfit && heads.Length > 0)
            {
                var h = heads[head];
                if (h.hatSpot) outfit.SetLocation(BodyLocation.Hat, h.hatSpot);
                if (h.eyesSpot) outfit.SetLocation(BodyLocation.Eyes, h.eyesSpot);
                if (h.earsSpot) outfit.SetLocation(BodyLocation.Ears, h.earsSpot);
            }
            for (int i = 0; i < bodies.Length; i++) if (bodies[i].visual) bodies[i].visual.SetActive(i == body);
            for (int i = 0; i < heads.Length; i++) if (heads[i].visual) heads[i].visual.SetActive(i == head);
            if (!outfit) return;
            outfit.baseDescription = $"a hovering robot with {NameOf(bodies, body)} and {NameOf(heads, head)}";
            outfit.NotifyChanged();
        }

        static string NameOf<T>(T[] parts, int i) where T : Part => parts.Length > i ? parts[i].name : "";
    }
}
