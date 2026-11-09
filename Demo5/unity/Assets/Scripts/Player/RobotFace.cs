// The player robot's LED face (art/led_mouth): two panels on its face screen, each showing one frame of a 5 x 4 sheet
// of LED masks (16 x 8 LEDs a frame). The eyes stay neutral and blink every few seconds; the mouth follows the
// player's voice (uLipSync listening to the shared mic, silently). The head is on the MirrorOnly layer, so all of
// this is only seen in a mirror.
using System.Collections.Generic;
using UnityEngine;
using Freestyle.Audio;

namespace Freestyle.Player
{
    public class RobotFace : MonoBehaviour
    {
        [Tooltip("The eye panels' renderers (one per head shape; the hidden ones don't matter).")]
        public List<Renderer> eyes = new();
        public List<Renderer> mouths = new();
        [Tooltip("Hears the voice: on a GameObject with an AudioSource, which plays the mic silently.")]
        public uLipSync.uLipSync lipSync;
        public Color ledColor = new(0.22f, 0.91f, 1f);

        [Header("Eyes")]
        public Vector2 blinkEvery = new(2.5f, 6f);
        public float blinkFrame = 0.04f;

        [Header("Mouth")]
        [Tooltip("The mic's loudness (log10 of uLipSync's raw volume) for a shut mouth and a fully open one.")]
        public float minVolume = -2.5f, maxVolume = -1.5f;
        [Range(0f, 1f)] public float openAt = 0.3f;
        [Tooltip("Hold each mouth frame at least this long, so fast rap doesn't flicker (s).")]
        public float minHold = 0.06f;
        public string micDevice = "";
        public int micRate = 16000;

        // uLipSync's phonemes (its sample profiles) -> mouth frames (sheet.json "ulipsync"; S -> SS)
        static readonly Dictionary<string, int> Mouth = new() { ["A"] = 10, ["I"] = 12, ["U"] = 14, ["E"] = 11, ["O"] = 13, ["N"] = 8, ["S"] = 7, ["-"] = 0 };
        static readonly int[] Blink = { 1, 2, 1 };   // half, closed, half (eyes.json "blink")

        MaterialPropertyBlock block;
        float nextBlink, blinkStart = -1, mouthSince, retryAt;
        int mouthFrame, wantMouth;
        AudioSource src;
        bool listening;

        void Awake()
        {
            block = new MaterialPropertyBlock();
            nextBlink = Time.time + Random.Range(blinkEvery.x, blinkEvery.y);
            if (lipSync)
            {
                lipSync.onLipSyncUpdate.AddListener(OnLipSync);
                lipSync.outputSoundGain = 0f;   // it hears the mic, nobody else does
                src = lipSync.GetComponent<AudioSource>();
            }
        }

        void OnEnable() => TryListen();

        /// <summary>Starts listening to the mic (on the headset, once the player has allowed it).</summary>
        void TryListen()
        {
            retryAt = Time.time + 2f;
            if (!src || listening) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                return;
            }
#endif
            var clip = SharedMic.Acquire(string.IsNullOrEmpty(micDevice) ? null : micDevice, micRate);
            listening = clip;
            if (!clip) return;
            src.clip = clip;
            src.loop = true;
        }

        void OnDisable()
        {
            if (src) src.Stop();
            if (listening) SharedMic.Release();
            listening = false;
        }

        void OnLipSync(uLipSync.LipSyncInfo info)
        {
            float v = info.rawVolume > 0 ? Mathf.InverseLerp(minVolume, maxVolume, Mathf.Log10(info.rawVolume)) : 0f;
            wantMouth = v < openAt || info.phoneme == null ? 0 : Mouth.TryGetValue(info.phoneme, out int f) ? f : 10;
        }

        void Update()
        {
            if (!listening && Time.time >= retryAt) TryListen();
            KeepPlaying();
            // eyes: neutral, a blink now and then
            int eyesFrame = 0;
            if (blinkStart < 0 && Time.time >= nextBlink) blinkStart = Time.time;
            if (blinkStart >= 0)
            {
                int i = (int)((Time.time - blinkStart) / blinkFrame);
                if (i >= Blink.Length) { blinkStart = -1; nextBlink = Time.time + Random.Range(blinkEvery.x, blinkEvery.y); }
                else eyesFrame = Blink[i];
            }
            // mouth: what uLipSync hears, each frame held a little while
            if (wantMouth != mouthFrame && Time.time - mouthSince >= minHold) { mouthFrame = wantMouth; mouthSince = Time.time; }
            foreach (var r in eyes) Show(r, eyesFrame);
            foreach (var r in mouths) Show(r, mouthFrame);
        }

        /// <summary>The mic plays just behind where it's recording (so there's always audio ahead of the playhead).</summary>
        void KeepPlaying()
        {
            var clip = SharedMic.Clip;
            if (!src || !clip) return;
            if (src.clip != clip) { src.clip = clip; src.loop = true; }
            int pos = SharedMic.Position, len = clip.samples, lag = clip.frequency / 20;   // 50 ms behind
            if (pos <= 0) return;
            int behind = (pos - src.timeSamples + len) % len;
            if (!src.isPlaying || behind > clip.frequency / 4 || behind < lag / 4)   // drifted: resync
            {
                src.timeSamples = (pos - lag + len) % len;
                if (!src.isPlaying) src.Play();
            }
        }

        void Show(Renderer r, int frame)
        {
            if (!r || !r.gameObject.activeInHierarchy) return;
            int col = frame % 5, row = frame / 5;
            r.GetPropertyBlock(block);
            block.SetVector("_BaseMap_ST", new Vector4(1f / 5, 1f / 4, col / 5f, 1f - (row + 1) / 4f));
            block.SetColor("_BaseColor", ledColor);
            r.SetPropertyBlock(block);
        }
    }
}
