// The dressing room's door to the stage. Grab it (its XR simple interactable; on the flat screen, look at it and press
// E): the view fades to black, the player is moved to the destination (Spots/PlayerSpot, facing its way), and it fades
// back in (Teleport.WithFade). onDark fires while it's black, once the player is there (OpponentStage.Prepare: the
// opponent appears), onArrived once the view is back (OpponentStage.Begin: the battle starts).
// With no save file yet (the first trip), the pre-show interview comes first (InterviewDirector: a spotlit void, a
// few questions), then the trip to the stage.
// Reaching for it shows a hover label (XRHoverLabel, added if there isn't one): hoverText, which OpponentStage keeps
// saying who's next ("Battle Kid Venom").
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Freestyle.Venue
{
    [RequireComponent(typeof(XRSimpleInteractable))]
    public class StageDoor : MonoBehaviour
    {
        [Tooltip("Where the player ends up, facing its forward (the venue's Spots/PlayerSpot).")]
        public Transform destination;
        [Tooltip("What to move without an XR Origin (the flat-screen player). Empty: the main camera's root.")]
        public Transform rig;
        [Tooltip("The flat-screen camera's eye height (m), when it is the thing moved.")]
        public float flatEyeHeight = 1.65f;
        public float fadeOut = 0.4f, hold = 0.2f, fadeIn = 0.5f;
        public Shader fadeShader;
        [Tooltip("While the view is black, the player already on the stage: spawn the opponent here.")]
        public UnityEvent onDark;
        [Tooltip("Once the view is back: start the battle here.")]
        public UnityEvent onArrived;

        [Tooltip("The pre-show interview, run before the first trip (no save yet). Empty: the scene's, if any.")]
        public Freestyle.Game.InterviewDirector interview;
        [Tooltip("What the hover label says when a hand reaches for the door.")]
        public string hoverText = "Go On Stage";

        bool busy;

        /// <summary>The door's hover label.</summary>
        public Freestyle.UI.XRHoverLabel Label { get; private set; }

        void Awake()
        {
            GetComponent<XRSimpleInteractable>().selectEntered.AddListener(_ => Go());
            Label = GetComponent<Freestyle.UI.XRHoverLabel>();
            if (!Label)
            {
                Label = gameObject.AddComponent<Freestyle.UI.XRHoverLabel>();
                Label.Text = hoverText;   // (its Awake has already run: through Text, so the label gets it too)
            }
        }

        /// <summary>Through the door: fade out, move, fade in.</summary>
        public void Go()
        {
            if (!busy && destination && Camera.main) StartCoroutine(Travel());
        }

        IEnumerator Travel()
        {
            busy = true;
            var iv = interview ? interview : FindAnyObjectByType<Freestyle.Game.InterviewDirector>();
            if (iv && iv.Needed) yield return iv.Run();   // the first time: the interview, then on to the stage
            void Dark()
            {
                if (iv) iv.Leave();
                onDark?.Invoke();
            }
            yield return Teleport.WithFade(destination, fadeShader, Dark, fadeOut, hold, fadeIn, rig, flatEyeHeight);
            busy = false;
            onArrived?.Invoke();
        }

#if ENABLE_INPUT_SYSTEM
        void Update()
        {
            // flat screen: look at the door and press E
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || !kb.eKey.wasPressedThisFrame || !Camera.main || Freestyle.UI.VRKeyboard.Typing) return;
            var cam = Camera.main.transform;
            if (Physics.Raycast(cam.position, cam.forward, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore) && hit.collider.transform.IsChildOf(transform)) Go();
        }
#endif
    }
}
