using UnityEngine;

namespace WifiHeatmap.Preview
{
    /// <summary>Grab input for the room miniature: a tracked controller's grip, or with controllers put down, a tracked
    /// hand's index pinch at its hand anchor (the wrist). Pinch-to-button mapping is off in this project, so pinches
    /// are read from the hand state directly.</summary>
    public static class PreviewGrabInput
    {
        private static OVRPlugin.HandState handState;

        public static bool Read(OVRCameraRig rig, bool left, out Pose pose, out bool grip)
        {
            var controller = left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
            Transform anchor = rig == null ? null : left ? rig.leftControllerAnchor : rig.rightControllerAnchor;
            if (anchor != null && OVRInput.IsControllerConnected(controller) &&
                OVRInput.GetControllerPositionTracked(controller) && OVRInput.GetControllerOrientationTracked(controller))
            {
                pose = new Pose(anchor.position, anchor.rotation);
                grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller) > .65f;
                return true;
            }

            Transform hand = rig == null ? null : left ? rig.leftHandAnchor : rig.rightHandAnchor;
            if (hand != null && OVRPlugin.GetHandState(OVRPlugin.Step.Render, left ? OVRPlugin.Hand.HandLeft : OVRPlugin.Hand.HandRight, ref handState) &&
                (handState.Status & OVRPlugin.HandStatus.HandTracked) != 0 &&
                (handState.Status & OVRPlugin.HandStatus.InputStateValid) != 0 &&
                handState.HandConfidence == OVRPlugin.TrackingConfidence.High)
            {
                pose = new Pose(hand.position, hand.rotation);
                grip = (handState.Pinches & OVRPlugin.HandFingerPinch.Index) != 0;
                return true;
            }

            pose = anchor == null ? Pose.identity : new Pose(anchor.position, anchor.rotation);
            grip = false;
            return false;
        }
    }
}
