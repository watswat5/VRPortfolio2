// A label that shows while an XR interactable is what a hand would select if it grabbed now (the ladder's hover prompt,
// made reusable): hovered, not already selected, and the interaction manager's highest-priority target for some
// interactor. An interactor that doesn't track priority targets (Target Priority Mode: None, the default) counts as soon
// as it hovers. While it shows, the label stays at the player's head height and turns to face them.
// Give it a TMP text of your own, or leave it empty and one is made: white with a dark outline, a little off the
// interactable's colliders on one side, the side the player is on when it starts. `Text` can be changed at any time ("Battle Kid Venom").
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Freestyle.UI
{
    public class XRHoverLabel : MonoBehaviour
    {
        [Tooltip("What to watch. Empty: the interactable on this object.")]
        public XRBaseInteractable interactable;
        [Tooltip("What the label says.")]
        public string text = "Select";
        [Tooltip("The label. Empty: one is made in front of the interactable.")]
        public TMP_Text label;
        [Tooltip("The player's head. Empty: the main camera.")]
        public Transform head;

        [Header("Placement")]
        [Tooltip("Keep the label at the player's head height (plus this offset, m) while it shows.")]
        public bool followHeadHeight = true;
        public float heightOffset = -0.12f;
        [Tooltip("Turn the label to face the player (upright).")]
        public bool faceHead = true;
        [Tooltip("A made label: how far in front of the interactable, toward the player (m).")]
        public float standOff = 0.12f;
        [Tooltip("A made label: its text size (TMP units; 1 is about 10 cm tall).")]
        public float fontSize = 0.7f;

        /// <summary>What the label says.</summary>
        public string Text
        {
            get => text;
            set { text = value; if (label) label.text = value; }
        }

        /// <summary>The label is showing (the interactable is the one a hand would grab).</summary>
        public bool Showing { get; private set; }

        Bounds bounds;        // a made label: the interactable's colliders, in this transform's space
        Vector3 axis;         // ...and the way it stands off them: local x or z, whichever side is thinner

        void Awake()
        {
            if (!interactable) interactable = GetComponent<XRBaseInteractable>();
            if (!head && Camera.main) head = Camera.main.transform;
            if (!label) label = Make();
            label.text = text;
            Show(false);
        }

        void OnDisable() => Show(false);

        void LateUpdate()
        {
            if (!head && Camera.main) head = Camera.main.transform;
            bool on = Targeted(interactable);
            Show(on);
            if (!on || !label || !head) return;
            var t = label.transform;
            if (followHeadHeight)
            {
                Vector3 p = t.position;
                p.y = head.position.y + heightOffset;
                t.position = p;
            }
            if (faceHead)
            {
                Vector3 away = t.position - head.position;   // TMP reads from behind its forward
                away.y = 0;
                if (away.sqrMagnitude > 1e-4f) t.rotation = Quaternion.LookRotation(away);
            }
        }

        static readonly List<IXRTargetPriorityInteractor> scratch = new();

        /// <summary>Hovered, not selected, and what a hand would select now (when the hand keeps track of that).</summary>
        public static bool Targeted(XRBaseInteractable target)
        {
            if (!target || !target.isActiveAndEnabled || !target.isHovered || target.isSelected) return false;
            var manager = target.interactionManager;
            if (manager && manager.IsHighestPriorityTarget(target, scratch)) return true;
            foreach (var h in target.interactorsHovering)   // hands that don't track priority: hovering is enough
                if (h is not IXRTargetPriorityInteractor p || p.targetPriorityMode == TargetPriorityMode.None) return true;
            return false;
        }

        void Show(bool on)
        {
            Showing = on;
            if (label && label.enabled != on) label.enabled = on;
        }

        /// <summary>A world-space label off one side of the interactable's colliders.</summary>
        TMP_Text Make()
        {
            var go = new GameObject("Hover Label");
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = Color.white;
            tmp.outlineWidth = 0.25f;
            tmp.outlineColor = new Color32(0, 0, 0, 220);
            tmp.rectTransform.sizeDelta = new Vector2(3f, 0.6f);
            go.transform.SetParent(transform, true);   // (world scale 1 whatever this is scaled to)

            // the colliders' box in this transform's space, and its thinnest way through (the way a label stands off)
            var cols = interactable ? interactable.colliders : null;
            bool any = false;
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (cols != null)
                foreach (var c in cols)
                {
                    if (!c) continue;
                    var wb = c.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = transform.InverseTransformPoint(wb.center + Vector3.Scale(wb.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                        if (any) bounds.Encapsulate(corner); else { bounds = new Bounds(corner, Vector3.zero); any = true; }
                    }
                }
            // stand off sideways (never up), out of whichever of its sides is thinner at world scale
            float thickX = transform.TransformVector(new Vector3(bounds.extents.x, 0, 0)).magnitude;
            float thickZ = transform.TransformVector(new Vector3(0, 0, bounds.extents.z)).magnitude;
            axis = thickX < thickZ ? Vector3.right : Vector3.forward;
            Place(tmp.transform);
            return tmp;
        }

        /// <summary>A made label: just off the interactable's thinner side, the side the player is on (once, at the start).</summary>
        void Place(Transform t)
        {
            Vector3 centre = transform.TransformPoint(bounds.center);
            Vector3 out_ = transform.TransformDirection(axis).normalized;
            Vector3 toward = head ? head.position - centre : -transform.forward;
            if (Vector3.Dot(toward, out_) < 0) out_ = -out_;
            float depth = transform.TransformVector(Vector3.Scale(bounds.extents, axis)).magnitude;
            Vector3 p = centre + out_ * (depth + standOff);
            if (followHeadHeight && head) p.y = head.position.y + heightOffset;
            t.position = p;
            if (!faceHead) t.rotation = Quaternion.LookRotation(-out_);
        }
    }
}
