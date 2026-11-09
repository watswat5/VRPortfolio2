// Testing without a headset: the camera at standing eye height, hold the right mouse button to look around, WASD to
// walk (the XR Origin moves, the camera tilts). Off while a headset is in use.
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

namespace Freestyle.Player
{
    public class FlatControls : MonoBehaviour
    {
        public Transform cameraTransform;
        [Tooltip("The XR Origin's camera offset: raised to eye height without a headset (the XR Origin only does that with one).")]
        public Transform cameraOffset;
        public float eyeHeight = 1.65f;
        public float lookSpeed = 0.15f, walkSpeed = 1.5f;

        float pitch;

        void Update()
        {
            if (XRSettings.isDeviceActive || !cameraTransform) return;
            if (cameraOffset) cameraOffset.localPosition = new Vector3(0f, eyeHeight, 0f);
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue() * lookSpeed;
                transform.Rotate(0f, d.x, 0f, Space.World);
                pitch = Mathf.Clamp(pitch - d.y, -80f, 80f);
                cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }
            if (kb == null || Freestyle.UI.VRKeyboard.Typing) return;   // (the keys are typing into a field)
            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed) move += Vector3.forward;
            if (kb.sKey.isPressed) move += Vector3.back;
            if (kb.aKey.isPressed) move += Vector3.left;
            if (kb.dKey.isPressed) move += Vector3.right;
            transform.position += transform.rotation * move.normalized * (walkSpeed * Time.deltaTime);
        }
    }
}
