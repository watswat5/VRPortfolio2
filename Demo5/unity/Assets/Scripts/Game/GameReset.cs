// Starting over: the view fades, the mic is let go and the scene is loaded again, so everything (the run of opponents,
// the stage, the interview, the dressing room) starts fresh, as when the game launched. The player sheet's Delete Save
// does this once the save is gone. Settings (PlayerPrefs) are kept.
using System.Collections;
using Freestyle.Venue;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Freestyle.Game
{
    public static class GameReset
    {
        /// <summary>Fades out and reloads the scene.</summary>
        public static void Restart(float fade = 0.5f)
        {
            var go = new GameObject("Restarting");
            go.AddComponent<Runner>().fade = fade;
        }

        class Runner : MonoBehaviour
        {
            public float fade;

            IEnumerator Start()
            {
                var door = FindAnyObjectByType<StageDoor>();
                var shader = door ? door.fadeShader : null;
                if (shader && Camera.main) yield return ScreenFade.For(Camera.main, shader).To(1, fade);
                Audio.SharedMic.Reset();   // whoever had it (a battle, the interview) goes with the scene
                var scene = SceneManager.GetActiveScene();
                if (scene.buildIndex >= 0) SceneManager.LoadScene(scene.buildIndex);
                else SceneManager.LoadScene(scene.name);
            }
        }
    }
}
