using Oculus.Interaction.Locomotion;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WifiHeatmap.Recording
{
    /// <summary>
    /// Turns off the Interaction SDK rig's first-person locomotion in every scene. The comprehensive rig
    /// (ProjectSetup.AddInteractionRig) brings a Locomotor whose character falls under gravity; in passthrough there is
    /// no floor collider, so it keeps moving the tracking space down while MRUK's world lock moves it back each frame.
    /// The head and the controller anchors end up where MRUK puts them, but the hand and controller visuals, posed in
    /// between, jitter and sink through the floor. The survey is walked for real, so nothing needs the locomotor.
    /// </summary>
    public static class NoRigLocomotion
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded += (_, _) => DisableAll();
            DisableAll();
        }

        private static void DisableAll()
        {
            foreach (FirstPersonLocomotor locomotor in Object.FindObjectsByType<FirstPersonLocomotor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!locomotor.enabled) continue;
                locomotor.enabled = false;
                Debug.Log($"WiFi Heatmap: disabled the rig's locomotion ({locomotor.name}); it fights MRUK's world lock.");
            }
        }
    }
}
