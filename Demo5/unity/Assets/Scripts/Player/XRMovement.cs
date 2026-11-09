// Smooth movement and snap turning for the XR Origin, cut down from the class demo's XRMovement (Demo4_Hunt): one stick
// walks where you're looking, the other snaps you round. A CharacterController on the XR Origin keeps you out of the
// walls and on the floor; its capsule follows the headset (over your feet when standing, under your head while walking).
// Anything else that moves the rig (StageDoor's teleport, FlatControls) is picked up before the next move.
// In a MoveArea (the stage, in front of the DJ table) the stick can't take your head out of it; outside one, it's free.
// While you move the view narrows (ComfortVignette, a global Volume), stronger the further the stick is pushed.
using UnityEngine;
using UnityEngine.InputSystem;
using Freestyle.Wardrobe;

namespace Freestyle.Player
{
    [RequireComponent(typeof(CharacterController))]
    public class XRMovement : MonoBehaviour
    {
        [Tooltip("Left stick walks, right stick turns (off: the other way round).")]
        public bool moveWithLeftStick = true;
        public float moveSpeed = 2f;         // metres per second
        public float snapAngle = 45f;        // degrees
        [Range(0f, 1f)] public float deadzone = 0.1f;
        public float minHeight = 0.5f, maxHeight = 2.5f;
        [Tooltip("The headset. Defaults to the main camera.")]
        public Transform head;
        [Tooltip("How much the view narrows while you move with the stick (a global Volume's vignette); 0: never.")]
        [Range(0, 1)] public float vignette = 0.8f;

        public InputActionProperty leftStick = new(new InputAction("Left Stick", InputActionType.Value, "<XRController>{LeftHand}/{Primary2DAxis}", expectedControlType: "Vector2"));
        public InputActionProperty rightStick = new(new InputAction("Right Stick", InputActionType.Value, "<XRController>{RightHand}/{Primary2DAxis}", expectedControlType: "Vector2"));

        CharacterController body;
        ComfortVignette comfort;
        float fall;
        Vector3 lastGround;
        const float FellOut = 25f;   // this far below where it last stood: fell out of the world
        Vector3 lastPosition;
        Quaternion lastRotation;
        MoveArea area;                       // the one you're in (picked again after a teleport), or none

        InputAction MoveAction => moveWithLeftStick ? leftStick.action : rightStick.action;
        InputAction TurnAction => moveWithLeftStick ? rightStick.action : leftStick.action;

        void Awake()
        {
            body = GetComponent<CharacterController>();
            if (!head && Camera.main) head = Camera.main.transform;
        }

        void Start()
        {
            // what you pick up mustn't push you around: the accessories and the spray can pass through your capsule
            foreach (var a in FindObjectsByType<Accessory>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Ignore(a);
            foreach (var s in FindObjectsByType<SprayCan>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Ignore(s);
            PickArea();
            Remember();
            lastGround = transform.position;
            // the sticks always move and turn, menus or not: lists scroll by dragging with the trigger, so the menu
            // pointers' stick scrolling is switched off (for a player built before it was left out)
            foreach (var r in GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor>(true))
                r.uiScrollInput.inputSourceMode = UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputValueReader.InputSourceMode.Unused;
        }

        /// <summary>The MoveArea your head is over, if any.</summary>
        void PickArea()
        {
            area = null;
            if (!head) return;
            Vector3 at = new(head.position.x, transform.position.y + 0.1f, head.position.z);
            foreach (var a in FindObjectsByType<MoveArea>(FindObjectsSortMode.None))
                if (a.Contains(at, 0.5f)) { area = a; break; }
        }

        void Ignore(Component c)
        {
            foreach (var col in c.GetComponentsInChildren<Collider>(true)) Physics.IgnoreCollision(body, col);
        }

        void OnEnable() { leftStick.action?.Enable(); rightStick.action?.Enable(); }
        void OnDisable() { leftStick.action?.Disable(); rightStick.action?.Disable(); }

        void Update()
        {
            // moved by something else (a teleport): the capsule goes along before it moves again
            if (transform.position != lastPosition || transform.rotation != lastRotation)
            {
                Physics.SyncTransforms();
                if ((transform.position - lastPosition).sqrMagnitude > 1f) { PickArea(); fall = 0f; lastGround = transform.position; }   // (a fresh start: no fall carried over; a fall comes back here)
            }

            Vector2 move = MoveAction?.ReadValue<Vector2>() ?? Vector2.zero;
            bool moving = move.sqrMagnitude > deadzone * deadzone;
            if (!comfort && vignette > 0f && head && head.TryGetComponent<Camera>(out var cam)) comfort = ComfortVignette.For(cam);
            if (comfort) comfort.Set(moving ? vignette * Mathf.Clamp01(move.magnitude) : 0f);   // stronger the faster you go
            FitCapsule(moving);

            Vector3 velocity = Vector3.zero;
            if (moving)
            {
                // where you're looking, flattened (looking straight up or down: where the rig faces)
                Transform from = head ? head : transform;
                Vector3 forward = Vector3.ProjectOnPlane(from.forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                forward.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                velocity = (right * move.x + forward * move.y) * moveSpeed;
                if (area) velocity = KeepIn(velocity);
            }
            if (body.enabled)
            {
                fall = body.isGrounded && fall <= 0f ? -2f : fall + Physics.gravity.y * Time.deltaTime;
                velocity.y = fall;
                body.Move(velocity * Time.deltaTime);
                if (body.isGrounded) lastGround = transform.position;
                else if (transform.position.y < lastGround.y - FellOut)
                {
                    // fell out of the world (off an edge, through a gap): back to the last place it stood
                    body.enabled = false;
                    transform.position = lastGround;
                    body.enabled = true;
                    fall = 0f;
                    Physics.SyncTransforms();
                }
            }

            // a snap each time the stick is pushed over sideways (it has to come back before the next)
            Vector2 turn = TurnAction?.ReadValue<Vector2>() ?? Vector2.zero;
            if (TurnAction != null && TurnAction.WasPressedThisFrame() && Mathf.Abs(turn.x) > deadzone && Mathf.Abs(turn.x) > Mathf.Abs(turn.y))
                Turn(Mathf.Sign(turn.x) * snapAngle);

            Remember();
        }

        /// <summary>The walk cut so your head stays in the area. Already out of it (you stepped out for real): only moves
        /// that bring you back.</summary>
        Vector3 KeepIn(Vector3 velocity)
        {
            if (!head || Time.deltaTime <= 0f) return velocity;
            Vector3 from = head.position, to = from + velocity * Time.deltaTime;
            Vector3 outside = Flat(area.Clamp(from) - from);
            if (outside.sqrMagnitude < 1e-6f) return Flat(area.Clamp(to) - from) / Time.deltaTime;
            return Flat(area.Clamp(to) - to).sqrMagnitude < outside.sqrMagnitude ? velocity : Vector3.zero;
        }

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        void Remember() { lastPosition = transform.position; lastRotation = transform.rotation; }

        /// <summary>The capsule as tall as your head; over your feet when standing still (leaning doesn't shove you), under
        /// your head while walking.</summary>
        void FitCapsule(bool moving)
        {
            if (!head) return;
            Vector3 local = transform.InverseTransformPoint(head.position);
            float h = Mathf.Clamp(local.y, minHeight, maxHeight);
            body.height = h;
            body.center = new Vector3(moving ? local.x : 0f, h / 2f, moving ? local.z : 0f);
        }

        /// <summary>Turns round your head, not the rig's origin, so you stay where you are.</summary>
        void Turn(float degrees)
        {
            Vector3 pivot = head ? head.position : transform.position;
            pivot.y = transform.position.y;
            transform.RotateAround(pivot, Vector3.up, degrees);
            Physics.SyncTransforms();
        }
    }
}
