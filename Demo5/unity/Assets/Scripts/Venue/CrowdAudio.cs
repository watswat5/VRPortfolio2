// The crowd's sound: looping layers (room chatter, soft cheering, strong cheering...) mixed by the battle's crowd level,
// each layer's volume a curve over the level (0 dead .. 11 raise the roof), eased so it swells and settles rather than
// jumps. A good bar (a punchline, yours or the opponent's) sets off a roar: one of the roar clips (each a cheer cut to
// build, peak and die away; cut short at roarLength) and a swell of the loud layers for a moment.
// The sound comes from the crowd's floor (this transform), wide and mostly 3D. During your bars it's turned down
// (`yourBars`) so the crowd doesn't drown you out or leak into the mic. Before and after a battle: idleLevel.
// Loops cross-fade into themselves at the end, so a recording that wasn't cut to loop doesn't click.
using System.Collections.Generic;
using Freestyle.Battle;
using UnityEngine;
using UnityEngine.Audio;

namespace Freestyle.Venue
{
    public class CrowdAudio : MonoBehaviour
    {
        [System.Serializable]
        public class Layer
        {
            public string name;
            public AudioClip clip;
            [Tooltip("Volume (0-1) over the crowd level (0-11).")]
            public AnimationCurve volume = AnimationCurve.Linear(0, 0, 11, 1);
            [Tooltip("How much of a roar's swell this layer takes (0-1).")]
            [Range(0, 1)] public float swell;
        }

        public BattleCoordinator battle;
        public Layer[] layers =
        {
            new() { name = "Room (chatter, ambience)", volume = Curve((0, 0.55f), (6, 0.45f), (11, 0.3f)) },
            new() { name = "Soft cheering", volume = Curve((2, 0f), (5, 0.55f), (8, 0.5f), (11, 0.35f)), swell = 0.4f },
            new() { name = "Strong cheering", volume = Curve((6, 0f), (9, 0.6f), (11, 0.9f)), swell = 1f },
        };
        [Tooltip("One-shots for a good bar, one picked at random.")]
        public AudioClip[] roars = new AudioClip[0];
        [Tooltip("The longest a roar plays, seconds (it fades out by then).")]
        public float roarLength = 9f;
        [Range(0, 1)] public float roarVolume = 0.9f;
        [Tooltip("How long the loud layers swell after a roar, seconds.")]
        public float swellTime = 3f;

        [Tooltip("When a battle ends the room cheers: a roar, the level this many moods up and the whole crowd this much " +
                 "louder, for cheerTime seconds, the last cheerFade of them easing back (like the Crowd's cutouts).")]
        public float cheerBoost = 3f;
        [Range(1, 2)] public float cheerLouder = 1.35f;
        public float cheerTime = 8f, cheerFade = 3f;

        [Header("Mix")]
        [Tooltip("The whole crowd, under the beat and the voices.")]
        [Range(0, 1)] public float volume = 0.3f;
        [Tooltip("The crowd's volume while you're rapping (a share of the usual).")]
        [Range(0, 1)] public float yourBars = 0.45f;
        [Tooltip("The crowd while the DJ talks to the room (Hushed).")]
        [Range(0, 1)] public float underTalk = 0.6f;
        [Range(0, 11)] public float idleLevel = 4f;
        [Tooltip("The battle's crowd level is heard this many times livelier (capped at 11), like the Crowd's cutouts.")]
        public float liveliness = 1.5f;
        [Tooltip("Seconds for the mix to follow the crowd level.")]
        public float ease = 1.2f;
        [Range(0, 1)] public float spatialBlend = 0.7f;
        public AudioMixerGroup mixerGroup;
        [Tooltip("Seconds a loop cross-fades into its own start.")]
        public float loopFade = 2f;

        class Loop { public AudioSource a, b; public bool onB; public float gain; }

        readonly List<Loop> loops = new();
        readonly List<AudioSource> roarSources = new();
        float level, duck = 1f, swellUntil = -1f, cheerStart = -100f, cheerLen = 8f, cheerGain = 1f;
        BattlePhase lastPhase;

        /// <summary>Quieter while the DJ talks to the room (DJHost sets it).</summary>
        [System.NonSerialized] public bool hushed;

        /// <summary>How much of the end-of-battle cheer is on now, 0-1.</summary>
        float Cheer()
        {
            float t = Time.time - cheerStart, fadeLen = Mathf.Min(cheerFade, cheerLen * 0.5f);
            return t < 0f || t > cheerLen ? 0f : Mathf.Clamp01((cheerLen - t) / Mathf.Max(0.01f, fadeLen));
        }

        /// <summary>The room cheers now for this many seconds (the DJ's introductions and the winner): as at the end
        /// of a battle, shorter; louder by gain (the roar, and how much the loud layers come up). A cheer lifts the hush.</summary>
        public void CheerFor(float seconds, float gain = 1f)
        {
            cheerStart = Time.time;
            cheerLen = seconds;
            cheerGain = gain;
            hushed = false;
            duck = Mathf.Max(duck, 0.9f);   // straight up, not eased up from under the DJ
            Roar(gain);
        }

        static AnimationCurve Curve(params (float t, float v)[] keys)
        {
            var c = new AnimationCurve();
            foreach (var (t, v) in keys) c.AddKey(t, v);
            return c;
        }

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            level = idleLevel;
            foreach (var l in layers) loops.Add(new Loop { a = Source(l.name + " A"), b = Source(l.name + " B") });
            for (int i = 0; i < 2; i++) roarSources.Add(Source("Roar " + i));
        }

        AudioSource Source(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = spatialBlend;
            s.spread = 160f;                       // wide: a room full of people, not a point
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 6f; s.maxDistance = 40f;
            s.dopplerLevel = 0f;
            s.outputAudioMixerGroup = mixerGroup;
            s.volume = 0f;
            return s;
        }

        void Start()
        {
            for (int i = 0; i < layers.Length; i++)
            {
                var clip = layers[i].clip;
                if (!clip) continue;
                var lp = loops[i];
                lp.a.clip = lp.b.clip = clip;
                lp.a.time = Random.Range(0f, Mathf.Max(0f, clip.length - loopFade - 1f));   // not every layer from its top
                lp.a.Play();
            }
        }

        void OnEnable() { if (battle) battle.Punchline += OnPunchline; }
        void OnDisable() { if (battle) battle.Punchline -= OnPunchline; }

        void OnPunchline(ReactionMsg r) => Roar();

        /// <summary>A roar: a burst of cheering and a swell of the loud layers.</summary>
        public void Roar(float gain = 1f)
        {
            swellUntil = Time.time + swellTime;
            if (roars.Length == 0) return;
            var clip = roars[Random.Range(0, roars.Length)];
            if (!clip) return;
            var s = roarSources.Find(x => !x.isPlaying) ?? roarSources[0];
            s.clip = clip;
            s.pitch = Random.Range(0.95f, 1.05f);
            s.time = 0f;
            s.volume = 0f;
            s.Play();
            StartCoroutine(Envelope(s, roarVolume * volume * gain, Mathf.Min(roarLength, clip.length)));
        }

        System.Collections.IEnumerator Envelope(AudioSource s, float peak, float length)
        {
            float attack = 0.05f, release = Mathf.Min(1.5f, length * 0.4f);
            for (float t = 0; t < length && s; t += Time.deltaTime)
            {
                float k = t < attack ? t / attack : t > length - release ? (length - t) / release : 1f;
                s.volume = peak * Mathf.Clamp01(k) * duck;
                yield return null;
            }
            if (s) s.Stop();
        }

        bool Battling => battle && (battle.BeatPlaying || battle.Phase == BattlePhase.Finished);   // (not while it starts: no level yet)

        void Update()
        {
            if (battle && battle.Phase != lastPhase)
            {
                if (battle.Phase == BattlePhase.Finished) CheerFor(cheerTime);   // the battle's over: the room erupts
                lastPhase = battle.Phase;
            }
            float cheer = Cheer();
            float target = Mathf.Min(11f, (Battling ? battle.CrowdLevel * liveliness : idleLevel) + cheerBoost * cheer);
            level = Mathf.Lerp(level, target, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, ease)));
            float duckTo = battle && battle.Phase == BattlePhase.UserTurn && battle.UserTurnIsLive ? yourBars : hushed ? underTalk : 1f;
            duck = Mathf.MoveTowards(duck, duckTo, Time.deltaTime * 1.5f);
            float swell = Time.time < swellUntil ? Mathf.Sin(Mathf.Clamp01((swellUntil - Time.time) / swellTime) * Mathf.PI) : 0f;

            for (int i = 0; i < layers.Length; i++)
            {
                var l = layers[i];
                var lp = loops[i];
                if (!l.clip) continue;
                lp.gain = Mathf.Clamp01(l.volume.Evaluate(level) + l.swell * swell * 0.6f) * volume * duck * (1f + (cheerLouder - 1f) * cheer * cheerGain);
                var on = lp.onB ? lp.b : lp.a;
                var next = lp.onB ? lp.a : lp.b;
                // near the end: the other source starts from the top and they cross-fade
                float left = l.clip.length - on.time;
                if (on.isPlaying && left <= loopFade && !next.isPlaying)
                {
                    next.time = 0f;
                    next.Play();
                }
                if (next.isPlaying && on.isPlaying)
                {
                    float k = Mathf.Clamp01(1f - left / loopFade);
                    on.volume = lp.gain * (1f - k);
                    next.volume = lp.gain * k;
                    if (left <= 0.02f || !on.isPlaying) { on.Stop(); lp.onB = !lp.onB; }
                }
                else if (on.isPlaying) on.volume = lp.gain;
                else if (next.isPlaying) { lp.onB = !lp.onB; next.volume = lp.gain; }
                else { on.time = 0f; on.Play(); }   // (it ended between frames)
            }
        }
    }
}
