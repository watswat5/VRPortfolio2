// Moves the mouth for whatever the character is saying: clips played or scheduled on any AudioSource (its own voice,
// or the battle's pooled voices, scheduled a few bars ahead), each followed from the moment it starts. A clip with
// mouth cues (Rhubarb's, from the server, or baked) is shown by CueLipSync; one without (the game asks the server for
// none where Oculus Lipsync runs, the server has no Rhubarb, or it failed on that line; ad-libs) is lip-synced from the
// clip's audio: by Oculus Lipsync (a VisemeTap on the playing source, CueLipSync showing its visemes) on the Quest,
// else by uLipSync when the character has one and fallback is on, else the mouth stays shut.
// uLipSync stays enabled while fallback is on (disabling it frees the buffers an audio thread may still be filling);
// only its face is switched. It hears another AudioSource through a uLipSyncAudioSource on that source, connected and
// disconnected here (not through its audioSourceProxy field, which it only updates when its analysis job is idle).
using System.Collections.Generic;
using UnityEngine;

namespace Freestyle.Avatar
{
    public class MouthDriver : MonoBehaviour
    {
        [Tooltip("Shows mouth cues (found in the children if empty).")]
        public CueLipSync cues;
        [Tooltip("Lip sync from the audio, for clips without cues (found in the children if empty; optional).")]
        public uLipSync.uLipSync fallback;
        public uLipSync.uLipSyncBlendShape fallbackFace;
        [Tooltip("Off: clips without cues get no lip sync.")]
        public bool useFallback = true;
        [Tooltip("Clips without cues: Oculus Lipsync where it runs (the Quest), before uLipSync.")]
        public bool useOculusLipSync = true;

        /// <summary>A clip is playing now.</summary>
        public bool Speaking => active != null;
        /// <summary>The clip playing now is lip-synced from its audio (uLipSync), not cues.</summary>
        public bool UsingFallback { get; private set; }
        /// <summary>Clips without cues are lip-synced by Oculus Lipsync here.</summary>
        public bool UsesOculusLipSync => useOculusLipSync && cues && VisemeTap.Available;

        class Said
        {
            public AudioSource src;
            public AudioClip clip;
            public double start;
            public List<(float t, string shape)> mouth;
        }

        readonly List<Said> queue = new();
        Said active;
        uLipSync.uLipSyncAudioSource heard;   // the proxy uLipSync is listening to, if any
        static readonly List<(float t, string shape)> Shut = new();

        void Awake()
        {
            if (!cues) cues = GetComponentInChildren<CueLipSync>(true);
            if (!fallback) fallback = GetComponentInChildren<uLipSync.uLipSync>(true);
            if (!fallbackFace) fallbackFace = GetComponentInChildren<uLipSync.uLipSyncBlendShape>(true);
        }

        void OnEnable()
        {
            if (fallback) fallback.enabled = useFallback;
            SetFallback(false);
        }

        /// <summary>A clip scheduled on a source (AudioSource.PlayScheduled at dsp time start), with its mouth cues
        /// ([[start_s, shape], ...] from the clip's start) or null for none.</summary>
        public void Schedule(AudioSource src, AudioClip clip, double start, List<(float t, string shape)> mouth)
        {
            if (src && clip) queue.Add(new Said { src = src, clip = clip, start = start, mouth = mouth });
        }

        /// <summary>Plays a clip on a source now, with its mouth cues (or null).</summary>
        public void Play(AudioSource src, AudioClip clip, List<(float t, string shape)> mouth)
        {
            if (!src || !clip) return;
            src.Stop();
            src.clip = clip;
            src.Play();
            Schedule(src, clip, AudioSettings.dspTime, mouth);
        }

        void OnDisable()
        {
            queue.Clear();
            active = null;
            if (cues) cues.SetLive(null);
            SetFallback(false);
            Listen(null);   // before uLipSync frees its buffers (despawning): a pooled source outlives the character
        }

        void OnDestroy() => Listen(null);

        void Update()
        {
            double now = AudioSettings.dspTime;
            // done: stopped, finished, or the source moved on to another clip
            queue.RemoveAll(s => !s.src || now >= s.start + s.clip.length + 0.1 ||
                                 now >= s.start + 0.1 && (!s.src.isPlaying || s.src.clip != s.clip));
            Said next = null;
            foreach (var s in queue)
                if (s.start <= now && (next == null || s.start > next.start)) next = s;   // the newest that has started
            if (next == active) return;
            active = next;
            Apply();
        }

        void Apply()
        {
            if (cues) cues.SetLive(null);
            if (active != null && active.mouth == null && UsesOculusLipSync)
            {
                SetFallback(false);
                Listen(null);
                if (!active.src.TryGetComponent(out VisemeTap tap)) tap = active.src.gameObject.AddComponent<VisemeTap>();
                tap.Restart();
                cues.enabled = true;
                cues.source = active.src;
                cues.SetCues(Shut);
                cues.SetLive(tap);
                return;
            }
            bool viaAudio = active != null && active.mouth == null && useFallback && fallback && fallbackFace;
            if (viaAudio)
            {
                if (cues) cues.enabled = false;
                // uLipSync hears its own GameObject's AudioSource; any other through a proxy on that source
                var src = active.src;
                uLipSync.uLipSyncAudioSource proxy = null;
                if (src.gameObject != fallback.gameObject && !src.TryGetComponent(out proxy))
                    proxy = src.gameObject.AddComponent<uLipSync.uLipSyncAudioSource>();
                if (!fallback.enabled) fallback.enabled = true;
                Listen(proxy);
                SetFallback(true);
                return;
            }
            SetFallback(false);
            Listen(null);
            if (!cues) return;
            cues.enabled = true;
            if (active != null) cues.source = active.src;
            cues.SetCues(active?.mouth ?? Shut);   // no cues and no fallback: the mouth rests
        }

        /// <summary>uLipSync hears this proxy's source (null: only its own GameObject's AudioSource).</summary>
        void Listen(uLipSync.uLipSyncAudioSource proxy)
        {
            if (heard == proxy) return;
            if (heard && fallback) heard.onAudioFilterRead.RemoveListener(fallback.OnDataReceived);
            heard = proxy;
            if (heard && fallback) heard.onAudioFilterRead.AddListener(fallback.OnDataReceived);
        }

        void SetFallback(bool on)
        {
            UsingFallback = on;
            if (!fallbackFace) return;
            if (!on && fallbackFace.enabled && fallbackFace.skinnedMeshRenderer)   // leave the face as it was before
                foreach (var b in fallbackFace.blendShapes)
                    if (b.index >= 0) fallbackFace.skinnedMeshRenderer.SetBlendShapeWeight(b.index, 0);
            fallbackFace.enabled = on;
        }
    }
}
