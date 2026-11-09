// Gesture test: one Rocketbox avatar, every candidate motion-capture clip listed by what it's for (the server's six
// gestures, rapping, listening, idles, hype). Click a name, or:
//   Next / Prev clip (D / A, or the arrow keys), Next slot (W), Replay (R)
//   Speak (S): the next line, lip-synced (Rhubarb cues), while the clip plays; Line for this slot (F)
//   Camera (C): whole body / face
//   Mic (M): auto (rapping while he speaks or a rapping clip plays, standing for hype clips, else ready) / rapping /
//   ready / standing
// Loops repeat; one-shots play once and the avatar goes back to idle.
using System.Collections.Generic;
using UnityEngine;

public class GestureTestControls : MonoBehaviour
{
    [System.Serializable]
    public class Clip
    {
        public string slot;     // what the game would use it for
        public string state;    // the Animator state (= the clip's name)
        public string path;     // its full path in the controller (Base Layer.slot.clip)
        public bool loop;
    }

    [System.Serializable]
    public class Line
    {
        public string slot, text;
        public AudioClip audio;
        public TextAsset cues;
    }

    public Animator anim;
    public AudioSource voice;
    public Freestyle.Avatar.CueLipSync lipSync;
    public Freestyle.Avatar.HoldMic mic;
    public enum MicMode { Auto, Rapping, Ready, Standing }
    public MicMode micMode = MicMode.Auto;
    public Camera cam;
    public Transform head;
    public string idleState = "Base Layer.idle.m_idle_neutral_01";
    public List<Clip> clips = new();
    public List<Line> lines = new();
    public float fade = 0.25f;

    int current = -1, line = -1;
    bool faceCam, returning;
    Vector3 bodyPos; Quaternion bodyRot;
    Vector2 scroll;

    void Start()
    {
        bodyPos = cam.transform.position; bodyRot = cam.transform.rotation;
        voice.Stop();
    }

    public void Play(int i)
    {
        current = (i + clips.Count) % clips.Count;
        anim.CrossFadeInFixedTime(clips[current].path, fade, 0, 0f);
        returning = false;
    }

    public void NextSlot()
    {
        string slot = current >= 0 ? clips[current].slot : null;
        int i = current;
        do i = (i + 1) % clips.Count; while (clips[i].slot == slot && i != current);
        Play(i);
    }

    public void Speak(int i)
    {
        if (lines.Count == 0) return;
        line = (i + lines.Count) % lines.Count;
        var l = lines[line];
        voice.Stop();
        voice.clip = l.audio;
        if (l.cues) lipSync.SetCuesJson(l.cues.text);
        voice.Play();
    }

    /// <summary>The line written for the current clip's slot, if there is one (else the next line).</summary>
    public void SpeakForSlot()
    {
        if (current >= 0)
            for (int k = 1; k <= lines.Count; k++)
            {
                int i = (line + k + lines.Count) % lines.Count;
                if (lines[i].slot == clips[current].slot) { Speak(i); return; }
            }
        Speak(line + 1);
    }

    public void ToggleCamera()
    {
        faceCam = !faceCam;
        if (!faceCam) { cam.transform.SetPositionAndRotation(bodyPos, bodyRot); return; }
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null)
        {
            if (kb.dKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame) Play(current + 1);
            if (kb.aKey.wasPressedThisFrame || kb.leftArrowKey.wasPressedThisFrame) Play(current - 1);
            if (kb.wKey.wasPressedThisFrame) NextSlot();
            if (kb.rKey.wasPressedThisFrame && current >= 0) Play(current);
            if (kb.sKey.wasPressedThisFrame) Speak(line + 1);
            if (kb.fKey.wasPressedThisFrame) SpeakForSlot();
            if (kb.cKey.wasPressedThisFrame) ToggleCamera();
            if (kb.mKey.wasPressedThisFrame) micMode = (MicMode)(((int)micMode + 1) % 4);
        }
#else
        if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) Play(current + 1);
        if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) Play(current - 1);
        if (Input.GetKeyDown(KeyCode.W)) NextSlot();
        if (Input.GetKeyDown(KeyCode.R) && current >= 0) Play(current);
        if (Input.GetKeyDown(KeyCode.S)) Speak(line + 1);
        if (Input.GetKeyDown(KeyCode.F)) SpeakForSlot();
        if (Input.GetKeyDown(KeyCode.C)) ToggleCamera();
        if (Input.GetKeyDown(KeyCode.M)) micMode = (MicMode)(((int)micMode + 1) % 4);
#endif
        if (mic)
            mic.pose = micMode switch
            {
                MicMode.Rapping => Freestyle.Avatar.HoldMic.Pose.Rapping,
                MicMode.Ready => Freestyle.Avatar.HoldMic.Pose.Ready,
                MicMode.Standing => Freestyle.Avatar.HoldMic.Pose.Standing,
                // auto: rapping while he speaks or raps; the arm let go for dancing and cheering (big moves); else ready
                _ => voice.isPlaying || current >= 0 && clips[current].slot.StartsWith("rapping")
                    ? Freestyle.Avatar.HoldMic.Pose.Rapping
                    : current >= 0 && clips[current].slot == "hype" ? Freestyle.Avatar.HoldMic.Pose.Standing
                    : Freestyle.Avatar.HoldMic.Pose.Ready,
            };
        // a one-shot that has played out: back to idle
        if (current >= 0 && !clips[current].loop && !returning)
        {
            var st = anim.GetCurrentAnimatorStateInfo(0);
            if (st.IsName(clips[current].path) && st.normalizedTime >= 1f && !anim.IsInTransition(0))
            {
                anim.CrossFadeInFixedTime(idleState, fade);
                returning = true;
            }
        }
    }

    void LateUpdate()
    {
        if (faceCam && head)   // follows the face as the body moves
        {
            var look = head.position + Vector3.up * 0.07f;
            var from = look + anim.transform.forward * 0.7f;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(look - from));
        }
    }

    void OnGUI()
    {
        var big = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        var title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        var text = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, normal = { textColor = Color.white } };

        // what's playing, top centre
        GUI.Box(new Rect(Screen.width / 2 - 330, 10, 660, 96), GUIContent.none);
        GUI.Label(new Rect(Screen.width / 2 - 320, 14, 640, 30), current < 0 ? "Pick a clip" :
            $"{clips[current].slot}:  {clips[current].state}   ({(clips[current].loop ? "loop" : "one-shot")}, {current + 1}/{clips.Count})", title);
        GUI.Label(new Rect(Screen.width / 2 - 320, 48, 640, 56), line < 0 ? "No line yet (S speaks)" :
            $"{(voice.isPlaying ? "Saying" : "Said")} ({lines[line].slot}): \"{lines[line].text}\"", text);

        // controls, bottom left
        GUILayout.BeginArea(new Rect(10, Screen.height - 160, 720, 150));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("◀ Prev (A)", big, GUILayout.Height(40))) Play(current - 1);
        if (GUILayout.Button("Next (D) ▶", big, GUILayout.Height(40))) Play(current + 1);
        if (GUILayout.Button("Next slot (W)", big, GUILayout.Height(40))) NextSlot();
        if (GUILayout.Button("Replay (R)", big, GUILayout.Height(40)) && current >= 0) Play(current);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Speak next line (S)", big, GUILayout.Height(40))) Speak(line + 1);
        if (GUILayout.Button("Line for this slot (F)", big, GUILayout.Height(40))) SpeakForSlot();
        if (GUILayout.Button(faceCam ? "Body cam (C)" : "Face cam (C)", big, GUILayout.Height(40))) ToggleCamera();
        if (GUILayout.Button($"Mic: {micMode} (M)", big, GUILayout.Height(40))) micMode = (MicMode)(((int)micMode + 1) % 4);
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        // every clip by slot, right: click one to play it
        float w = 330;
        GUILayout.BeginArea(new Rect(Screen.width - w - 10, 10, w, Screen.height - 20), GUI.skin.box);
        scroll = GUILayout.BeginScrollView(scroll);
        string slot = null;
        for (int i = 0; i < clips.Count; i++)
        {
            if (clips[i].slot != slot)
            {
                slot = clips[i].slot;
                GUILayout.Label(slot, new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold });
            }
            var style = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = 13 };
            if (i == current) style.normal.textColor = style.hover.textColor = Color.yellow;
            if (GUILayout.Button($"{(i == current ? "▶ " : "   ")}{clips[i].state}{(clips[i].loop ? "  ⟳" : "")}", style)) Play(i);
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
}
