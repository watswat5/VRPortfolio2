// A boombox on the dressing room's spray table, under the song list: pick a beat on the list and it plays a preview
// of it, from the drop (where the battle's count-in ends), for previewSeconds, then fades out. Pick the same beat
// again while it plays to stop it. Loading the preview is BattleCoordinator.PreloadSong, so a server song is
// downloaded here, while you listen, and the battle with it starts without waiting.
// The preview stops when a battle starts and when a stage door goes dark (on the way out of the room). Its speakers
// thump on the beat and its display glows while it plays, blinking while the song loads.
// The model is art/boombox/make_boombox.py's (Models/boombox/boombox.fbx): its Cone L and Cone R thump, its Display
// lights. Freestyle > Build Boombox puts it on the spray table in the open scene (Build Spray Table does too).
using Freestyle.Battle;
using UnityEngine;

namespace Freestyle.Venue
{
    public class Boombox : MonoBehaviour
    {
        public BattleCoordinator battle;
        [Tooltip("How long a preview plays before it fades out (s).")]
        public float previewSeconds = 30f;
        public float fadeIn = 0.3f, fadeOut = 2f;
        [Tooltip("The preview's loudness next to the battle's Beat Volume.")]
        [Range(0, 1)] public float volume = 0.7f;
        [Tooltip("Heard up to this far away (m): about the dressing room.")]
        public float maxDistance = 9f;

        /// <summary>The song it's playing or loading, or null.</summary>
        public SongInfo Song { get; private set; }
        public bool Loading { get; private set; }
        public bool Playing => src && src.isPlaying && !Loading;
        /// <summary>For the song list: "Loading X…", "Preview: X", or null when idle.</summary>
        public string StatusText => Song == null ? null
            : Loading ? $"Loading {Title(Song)}…" : Playing ? $"Preview: {Title(Song)}  (pick it again to stop)" : null;

        AudioSource src;
        int request;   // bumped per Preview / Stop: a load that comes back for an older one is dropped
        float startedAt, fadeFrom = -1f, fadeStart, fadeLen;
        [Tooltip("The speakers' cones, pushed out on the beat. Empty: the children named Cone L and Cone R.")]
        public Transform[] cones = new Transform[0];
        [Tooltip("The display strip, lit while it plays (its material's _BaseColor). Empty: the child named Display.")]
        public Renderer display;
        [Tooltip("How far a cone pushes out on a beat (its scale along the box's front).")]
        public float thump = 0.6f;
        static readonly Color Lit = new(0.2f, 0.95f, 0.9f), Dark = new(0.03f, 0.06f, 0.06f);
        Vector3[] coneRest;
        int coneAxis = 2;   // the cones' local axis that points out of the box's front
        MaterialPropertyBlock block;

        static string Title(SongInfo s) => string.IsNullOrEmpty(s.title) ? s.id : s.title;

        void Start()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = true;   // (previewSeconds is shorter than any song: this only covers a short one)
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 1f;
            src.maxDistance = maxDistance;
            src.dopplerLevel = 0f;
            if (battle) src.outputAudioMixerGroup = battle.beatMixerGroup;
            FindParts();
            if (battle) battle.PhaseChanged += OnPhase;
            foreach (var door in FindObjectsByType<StageDoor>(FindObjectsSortMode.None)) door.onDark.AddListener(Silence);
        }

        void OnDestroy()
        {
            if (battle) battle.PhaseChanged -= OnPhase;
            foreach (var door in FindObjectsByType<StageDoor>(FindObjectsSortMode.None)) if (door) door.onDark.RemoveListener(Silence);
        }

        void OnPhase(BattlePhase p)
        {
            if (p != BattlePhase.Idle && p != BattlePhase.Finished) Stop(0.5f);
        }

        void Silence() => Stop(0.3f);

        /// <summary>Plays a preview of the song (loading it first), or stops it when it's the one playing.</summary>
        public async void Toggle(SongInfo s)
        {
            if (s == null) return;
            if (Song != null && Song.id == s.id && (Playing || Loading)) { Stop(0.4f); return; }
            await Preview(s);
        }

        /// <summary>Loads the song (kept for the battle) and plays it from its drop.</summary>
        public async Awaitable Preview(SongInfo s)
        {
            if (!battle || s == null) return;
            if (battle.Phase is not (BattlePhase.Idle or BattlePhase.Finished)) return;   // (a battle's on: not over it)
            int r = ++request;
            src.Stop();
            src.clip = null;
            Song = s;
            Loading = true;
            AudioClip clip;
            try { clip = await battle.PreloadSong(s); }
            catch (System.Exception e) { Debug.LogException(e); clip = null; }
            if (r != request) return;   // another song was picked, or it was stopped, while this one loaded
            Loading = false;
            if (!clip || battle.Phase is not (BattlePhase.Idle or BattlePhase.Finished))
            {
                if (!clip) Debug.LogWarning($"[Boombox] couldn't load {s.file}");
                Song = null;
                return;
            }
            src.clip = clip;
            src.time = Mathf.Clamp(StartAt(s), 0f, Mathf.Max(0f, clip.length - 1f));
            src.volume = 0f;
            src.Play();
            startedAt = Time.unscaledTime;
            fadeFrom = -1f;
        }

        /// <summary>Fades the preview out over `seconds` (0: at once) and stops it.</summary>
        public void Stop(float seconds = 0f)
        {
            request++;
            Loading = false;
            if (!src || !src.isPlaying || seconds <= 0f) { if (src) src.Stop(); Song = null; return; }
            fadeFrom = src.volume;
            fadeStart = Time.unscaledTime;
            fadeLen = seconds;
        }

        /// <summary>Where the preview starts: the drop (the beat in full), else the first downbeat, else the loop.</summary>
        static float StartAt(SongInfo s) => s.drop_s ?? (s.first_downbeat_s > 0 ? s.first_downbeat_s : s.loop_start_s);

        float Full => (battle ? battle.beatVolume : 0.8f) * volume;

        void Update()
        {
            if (!src) return;
            float now = Time.unscaledTime;
            if (src.isPlaying)
            {
                if (fadeFrom >= 0f)   // stopping
                {
                    float k = Mathf.Clamp01((now - fadeStart) / Mathf.Max(0.01f, fadeLen));
                    src.volume = fadeFrom * (1f - k);
                    if (k >= 1f) { src.Stop(); fadeFrom = -1f; Song = null; }
                }
                else
                {
                    float played = now - startedAt;
                    src.volume = Full * Mathf.Clamp01(played / Mathf.Max(0.01f, fadeIn));
                    if (played >= previewSeconds) Stop(fadeOut);
                }
            }
            Animate(now);
        }

        /// <summary>The speakers push out on each beat; the display glows (blinks while loading).</summary>
        void Animate(float now)
        {
            float pump = 0f;
            if (src.isPlaying && Song != null && Song.bpm > 0)
            {
                float beat = (src.time - Song.first_downbeat_s) * Song.bpm / 60f;
                float phase = beat - Mathf.Floor(beat);
                pump = Mathf.Exp(-phase * 7f) * (src.volume / Mathf.Max(0.01f, Full));
            }
            for (int i = 0; i < cones.Length; i++)
            {
                if (!cones[i]) continue;
                var s = coneRest[i];
                s[coneAxis] *= 1f + thump * pump;
                cones[i].localScale = s;
            }
            if (display)
            {
                float glow = Loading ? (Mathf.Repeat(now * 2f, 1f) < 0.5f ? 0.6f : 0.1f) : src.isPlaying ? 0.55f + 0.45f * pump : 0f;
                block ??= new MaterialPropertyBlock();
                display.GetPropertyBlock(block);
                block.SetColor("_BaseColor", Color.Lerp(Dark, Lit, glow));
                display.SetPropertyBlock(block);
            }
        }

        void FindParts()
        {
            Transform Child(string name) =>
                System.Array.Find(GetComponentsInChildren<Transform>(true), t => t != transform && t.name == name);
            if (cones.Length == 0) cones = new[] { Child("Cone L"), Child("Cone R") };
            if (!display) display = Child("Display")?.GetComponent<Renderer>();
            coneRest = System.Array.ConvertAll(cones, c => c ? c.localScale : Vector3.one);
            // the cones' axis out of the front, whatever the import turned them to
            var first = System.Array.Find(cones, c => c);
            if (first)
            {
                Vector3 d = first.InverseTransformDirection(transform.forward);
                d = new Vector3(Mathf.Abs(d.x), Mathf.Abs(d.y), Mathf.Abs(d.z));
                coneAxis = d.x > d.y && d.x > d.z ? 0 : d.y > d.z ? 1 : 2;
            }
        }
    }
}
