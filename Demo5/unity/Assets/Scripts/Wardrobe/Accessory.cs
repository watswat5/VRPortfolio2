// Something the player can wear. Put it on any model: it gets an XR grab interactable (hands grab it wherever they
// touch it) and a kinematic Rigidbody. Let go of it at its location on the body and that location's socket (Outfit)
// snaps it on, attachPoint onto the location's transform; whatever was worn there is taken off and respawns where it
// started. Grab it again to take it off. Let go anywhere else and it respawns where it started too.
// description is all the game needs to know about it: a short plain phrase ("a red baseball cap", "gold aviator
// shades") that goes to the opponent, who may clown it.
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Freestyle.Wardrobe
{
    public enum BodyLocation { Hat, Eyes, Ears, Neck, Wrist }

    [RequireComponent(typeof(XRGrabInteractable))]
    public class Accessory : MonoBehaviour
    {
        [Tooltip("What it is, in a few plain words, as you'd describe it to someone: \"a red baseball cap\". " +
                 "The opponent hears this.")]
        public string description = "a hat";
        [Tooltip("Where on the body it goes. One accessory per location: putting on another takes this one off.")]
        public BodyLocation location = BodyLocation.Hat;
        [Tooltip("The point on the model that sits on the location's transform, rotated to match it (for a hat: inside " +
                 "the crown, up out of the top of the head, forward over the brim). Empty: the model's pivot.")]
        public Transform attachPoint;
        [Tooltip("Let go anywhere but on the body: back where it started. Off: it stays where it was let go.")]
        public bool respawnWhenDropped = true;

        /// <summary>The outfit wearing it, or null.</summary>
        public Outfit Wearer { get; internal set; }
        public XRGrabInteractable Grab { get; private set; }

        Transform startParent;
        Vector3 startPos;
        Quaternion startRot;

        void Awake()
        {
            Grab = GetComponent<XRGrabInteractable>();
            var rb = GetComponent<Rigidbody>();   // the grab interactable adds one
            rb.isKinematic = true;                // it stays put on the rack and on the body
            rb.useGravity = false;
            Grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            Grab.throwOnDetach = false;
            Grab.useDynamicAttach = true;         // hands hold it where they grab it; sockets use attachTransform
            if (attachPoint) Grab.attachTransform = attachPoint;
            startParent = transform.parent;
            startPos = transform.position;
            startRot = transform.rotation;
        }

        void OnEnable() => Grab.selectExited.AddListener(OnReleased);
        void OnDisable() => Grab.selectExited.RemoveListener(OnReleased);

        void OnReleased(SelectExitEventArgs e)
        {
            if (e.isCanceled || e.interactorObject is AccessorySocket) return;   // taken off the body, or a respawn
            var socket = Outfit.SocketHovering(this);
            if (socket) socket.Outfit.Clear(location, except: this);   // the socket takes it next frame
            else if (respawnWhenDropped) Respawn();
        }

        /// <summary>Back where it started in the world.</summary>
        public void Respawn()
        {
            if (Grab.isSelected && Grab.interactionManager)
                foreach (var who in Grab.interactorsSelecting.ToArray())
                    Grab.interactionManager.SelectExit(who, (IXRSelectInteractable)Grab);
            transform.SetParent(startParent, false);
            transform.SetPositionAndRotation(startPos, startRot);
        }
    }
}
