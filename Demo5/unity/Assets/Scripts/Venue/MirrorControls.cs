// Arrows on the mirror's left and right edges to step through the robot's looks with the ray pointer (point, pull the
// trigger): left for the one before, right for the next. At chest height the body types, at head height the heads;
// the name of the one that's on shows under each pair.
// Adds the sticky notes of tips (WeakSpotNotes) if the scene has none. Built at Start beside the mirror's glass (the StereoMirror), on the UI layer (the mirror doesn't reflect it).
using Freestyle.Player;
using Freestyle.UI;
using TMPro;
using UnityEngine;

namespace Freestyle.Venue
{
    public class MirrorControls : MonoBehaviour
    {
        [Tooltip("The mirror's glass. Empty: the scene's StereoMirror.")]
        public StereoMirror mirror;
        [Tooltip("The player's robot. Empty: found when needed.")]
        public RobotLooks looks;
        [Tooltip("How high the body arrows are over the floor (m).")]
        public float height = 1.25f;
        [Tooltip("How high the head arrows are over the floor (m).")]
        public float headHeight = 1.62f;
        public float inset = 0.13f;   // from the glass's edges, inward

        TextMeshProUGUI leftName, rightName, leftHead, rightHead;

        void Start()
        {
            if (!mirror) mirror = FindAnyObjectByType<StereoMirror>();
            if (!mirror) { Debug.LogWarning("MirrorControls: no mirror in the scene."); return; }
            if (!FindAnyObjectByType<WeakSpotNotes>()) gameObject.AddComponent<WeakSpotNotes>().mirror = mirror;   // the losses' tips
            var glass = mirror.transform;
            float half = mirror.size.x / 2f;
            // the glass's forward points into it: the arrows sit just in front, facing the room the same way
            Vector3 front = -glass.forward * 0.015f;
            Vector3 at(float side, float y) => new Vector3(glass.position.x, y, glass.position.z) + glass.right * (side * (half - inset)) + front;
            leftName = Arrow("Previous body", at(-1, height), glass.rotation, "<", "BODY", () => Looks()?.PreviousBody());
            rightName = Arrow("Next body", at(1, height), glass.rotation, ">", "BODY", () => Looks()?.NextBody());
            leftHead = Arrow("Previous head", at(-1, headHeight), glass.rotation, "<", "HEAD", () => Looks()?.PreviousHead());
            rightHead = Arrow("Next head", at(1, headHeight), glass.rotation, ">", "HEAD", () => Looks()?.NextHead());
        }

        RobotLooks Looks() => looks ? looks : looks = FindAnyObjectByType<RobotLooks>();

        TextMeshProUGUI Arrow(string name, Vector3 pos, Quaternion rot, string glyph, string what, UnityEngine.Events.UnityAction onClick)
        {
            var (canvas, panel) = UIKit.Canvas(name, transform, new Vector2(170, 300), 0.001f);
            canvas.transform.SetPositionAndRotation(pos, rot);
            panel.GetComponent<UnityEngine.UI.Image>().color = new Color(0, 0, 0, 0.35f);
            var v = panel.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            v.padding = new RectOffset(10, 10, 14, 10); v.spacing = 8;
            var b = UIKit.Size(UIKit.Button(panel, glyph, onClick, 120), 200);
            UIKit.LabelOf(b).color = UIKit.Accent;
            UIKit.Size(UIKit.Label(panel, what, 26, UIKit.Dim, TextAlignmentOptions.Center, true), 32);
            var label = UIKit.Size(UIKit.Label(panel, "", 22, UIKit.TextColour, TextAlignmentOptions.Center), 30);
            UIKit.OnUILayer(canvas.gameObject);
            return label;
        }

        void Update()
        {
            var l = Looks();
            if (!l || !leftName) return;
            static string Short(string s) => s.Replace("a ", "").Replace("an ", "").Replace(" with ear pods", "");
            leftName.text = rightName.text = Short(l.BodyName);
            if (leftHead) leftHead.text = rightHead.text = Short(l.HeadName);
        }
    }
}
