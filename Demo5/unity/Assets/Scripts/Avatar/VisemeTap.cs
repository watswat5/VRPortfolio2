// Meta's Oculus Lipsync (Assets/Oculus/LipSync, only its runtime and the Quest's ARM64 library) on one AudioSource:
// it hears what the source plays (OnAudioFilterRead, on the audio thread) and keeps the 15 viseme weights (sil PP FF
// TH DD kk CH SS nn RR aa E ih oh ou, 0-1) for CueLipSync to show. On the device, so the server needn't make mouth
// cues (Rhubarb was the slowest step of voicing each line on the cloud server's one CPU).
// MouthDriver adds one to whichever source plays a line; a pooled source keeps its own. The library is only built for
// Android ARM64: elsewhere (the Linux editor) Available is false and lines fall back to cues or uLipSync.
using System;
using UnityEngine;

namespace Freestyle.Avatar
{
    [RequireComponent(typeof(AudioSource))]
    public class VisemeTap : MonoBehaviour
    {
        static bool? available;

        /// <summary>The lip sync library loads and starts on this device.</summary>
        public static bool Available
        {
            get
            {
                if (available == null)
                {
                    try { available = OVRLipSync.Initialize() == OVRLipSync.Result.Success; }
                    catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { available = false; }
                    Debug.Log($"[LipSync] Oculus Lipsync {(available.Value ? "on" : "unavailable here")}");
                }
                return available.Value;
            }
        }

        readonly OVRLipSync.Frame frame = new();
        uint context;   // 0: none

        void Awake()
        {
            if (Available &&
                OVRLipSync.CreateContext(ref context, OVRLipSync.ContextProviders.Enhanced, 0, true) != OVRLipSync.Result.Success)
            {
                Debug.LogWarning($"[LipSync] no lip sync context for {name}", this);
                context = 0;
            }
        }

        void OnDestroy()
        {
            lock (frame)
            {
                if (context != 0) OVRLipSync.DestroyContext(context);
                context = 0;
            }
        }

        /// <summary>Hears a new clip from the start (forgets the last one's tail).</summary>
        public void Restart()
        {
            lock (frame)
            {
                if (context != 0) OVRLipSync.ResetContext(context);
                frame.Reset();
            }
        }

        /// <summary>Copies the latest viseme weights (0-1, in OVRLipSync.Viseme order) into into; false without lip sync.</summary>
        public bool Read(float[] into)
        {
            lock (frame)
            {
                if (context == 0) return false;
                Array.Copy(frame.Visemes, into, Math.Min(into.Length, frame.Visemes.Length));
            }
            return true;
        }

        void OnAudioFilterRead(float[] data, int ch)
        {
            if (context == 0) return;
            if (ch != 1 && ch != 2)   // the library takes mono or stereo: hear the first two channels
            {
                var two = new float[data.Length / ch * 2];
                for (int i = 0, j = 0; i + 1 < data.Length; i += ch, j += 2) { two[j] = data[i]; two[j + 1] = data[i + 1]; }
                data = two; ch = 2;
            }
            lock (frame)
                if (context != 0) OVRLipSync.ProcessFrame(context, data, frame, ch == 2);
        }
    }
}
