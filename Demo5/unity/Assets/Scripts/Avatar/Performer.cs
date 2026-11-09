// Runs a rapper's body from game events, on Rapper.controller (Moves lists the clips). What they're doing sets the
// loop underneath: idle (by mood: rattled and heated have their own), listening, rapping, hype, or the DJ's hosting
// (talking to the room) and working (at the decks); every so often it
// switches to another clip in the same slot so it doesn't repeat. Gesture("laughs") plays a random clip from that slot
// once (never the same one twice running when there's a choice) and goes back to the loop. While rapping, an accent
// (a pointing or presenting move) is thrown in now and then.
// Also holds the mic (HoldMic, if there is one): at the mouth while rapping or speaking, let go for hype (big moves),
// else ready. Say() plays a line on its own voice, SayScheduled() follows one the battle scheduled on its own
// AudioSource; MouthDriver lip-syncs either from the server's mouth cues, or from the audio (uLipSync) without them.
// The face shows the mood and flashes each gesture (FaceExpression, added at Awake when the face has the shapes).
// The character prefabs (Assets/Characters, Freestyle > Build Character Prefabs) come wired; spawn one and call these.
using System.Collections.Generic;
using UnityEngine;

namespace Freestyle.Avatar
{
    [RequireComponent(typeof(Animator))]
    public class Performer : MonoBehaviour
    {
        public enum Activity { Idle, Listening, Rapping, Hype, Hosting, Working }
        [Tooltip("The server's character id (backend/freestyle/characters/<id>.toml), e.g. mc_static.")]
        public string characterId;
        public string displayName;
        public Activity activity = Activity.Idle;
        [Tooltip("The opponent's mood (BattleRules.OpponentMood): rattled and heated get their own idles.")]
        public string mood = "calm";

        [Tooltip("Seconds to cross-fade between clips.")]
        public float fade = 0.25f;
        [Tooltip("Seconds (random in this range) before a loop changes to another clip in its slot.")]
        public Vector2 varyEvery = new(8f, 15f);
        [Tooltip("Only these clips (Moves' clip names), where a slot has any of them: a character's own subset (the " +
                 "interviewer's calm talk and listen). Empty: every clip.")]
        public string[] onlyClips = new string[0];
        [Tooltip("Seconds (random in this range) between accents while rapping. Zero: none.")]
        public Vector2 accentEvery = new(5f, 10f);

        [Header("Optional")]
        public HoldMic mic;
        [Tooltip("Off: leave the mic's pose to someone else.")]
        public bool driveMic = true;
        public AudioSource voice;
        public MouthDriver mouth;

        /// <summary>The clip playing (or fading in) now.</summary>
        public Moves.Move Current { get; private set; }
        /// <summary>A gesture just started (its slot: laughs, glares, ...): FaceExpression flashes it on the face.</summary>
        public event System.Action<string> Gestured;
        public bool InGesture => oneShot;

        Animator anim;
        string loopSlot;
        bool oneShot;
        float nextVary, nextAccent;
        readonly Dictionary<string, string> last = new();   // slot -> last clip played from it

        void Awake()
        {
            anim = GetComponent<Animator>();
            if (!mic) mic = GetComponent<HoldMic>();
            if (!mouth) mouth = GetComponent<MouthDriver>();
            if (!voice && mouth && mouth.cues) voice = mouth.cues.source;
            // a face that shows the mood (Rocketbox's ARKit shapes), if there's none yet and the face has them
            if (!GetComponent<FaceExpression>())
                foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (FaceExpression.CanShow(r)) { gameObject.AddComponent<FaceExpression>(); break; }
        }

        void OnEnable() => PlayLoop(true);   // spawned, or taken out of a pool

        /// <summary>A one-shot from this slot (a server gesture: laughs, shrugs, nods, winces, glares, steps_back;
        /// or any slot in Moves; a looping clip plays through once). False for an unknown slot.</summary>
        public bool Gesture(string slot)
        {
            var m = Pick(slot);
            if (m.clip == null) return false;
            Play(m);
            oneShot = true;   // a looping clip plays through once
            try { Gestured?.Invoke(slot); } catch (System.Exception e) { Debug.LogException(e); }
            return true;
        }

        /// <summary>Changes what they're doing; the loop changes now unless a gesture is playing (then after it).</summary>
        public void Do(Activity a)
        {
            if (a == activity) return;
            activity = a;
            nextAccent = Time.time + Random.Range(accentEvery.x, accentEvery.y);
            if (!oneShot) PlayLoop(false);
        }

        /// <summary>A line on its own voice, now, lip-synced from its mouth cues ([[start_s, shape], ...] JSON), or from
        /// the audio when there are none.</summary>
        public void Say(AudioClip clip, string cuesJson = null)
        {
            if (!voice) return;
            var cues = cuesJson != null ? CueLipSync.ParseJson(cuesJson) : null;
            if (mouth) mouth.Play(voice, clip, cues is { Count: > 0 } ? cues : null);
            else { voice.Stop(); voice.clip = clip; voice.Play(); }
        }

        /// <summary>A clip someone else scheduled (AudioSource.PlayScheduled at dsp time start): the battle's lines and
        /// ad-libs. Lip-synced once it starts, from its mouth cues or, when null, from the audio.</summary>
        public void SayScheduled(AudioSource source, AudioClip clip, double start, List<(float t, string shape)> cues)
        {
            if (mouth) mouth.Schedule(source, clip, start, cues);
        }

        /// <summary>Saying something now (a line or ad-lib is playing).</summary>
        public bool Speaking => mouth ? mouth.Speaking : voice && voice.isPlaying;

        string LoopSlot => activity switch
        {
            Activity.Listening => Moves.Listening,
            Activity.Rapping => Moves.Rapping,
            Activity.Hype => Moves.Hype,
            // The DJ hosting, mic in the right hand: the MCs' talk loops, whose free left hand gestures on its own. (The
            // host's m_moderate clips work both hands together, so with one on the mic the other pinched at nothing.)
            Activity.Hosting => Moves.Rapping,
            Activity.Working => Moves.Working,
            _ => mood == "rattled" ? Moves.IdleRattled : mood == "heated" ? Moves.IdleHeated : Moves.Idle,
        };

        void PlayLoop(bool randomStart)
        {
            loopSlot = LoopSlot;
            var m = Pick(loopSlot);
            if (m.clip == null) return;
            Play(m, randomStart ? Random.value : 0f);
            oneShot = !m.loop;   // hype has one-shot cheers: they play out, then another hype clip
            nextVary = Time.time + Random.Range(varyEvery.x, varyEvery.y);
        }

        void Play(Moves.Move m, float at = 0f)
        {
            Current = m;
            last[m.slot] = m.clip;
            if (at > 0) anim.Play(m.Path, 0, at);
            else anim.CrossFadeInFixedTime(m.Path, fade, 0);
        }

        /// <summary>A random clip from the slot, not the one it played last if there's another.</summary>
        Moves.Move Pick(string slot)
        {
            var all = Moves.InSlot(slot);
            if (onlyClips != null && onlyClips.Length > 0)
            {
                var mine = System.Array.FindAll(all, m => System.Array.IndexOf(onlyClips, m.clip) >= 0);
                if (mine.Length > 0) all = mine;   // (a slot with none of them keeps all its clips)
            }
            if (all.Length == 0) return default;
            if (all.Length == 1) return all[0];
            last.TryGetValue(slot, out var prev);
            Moves.Move m;
            do m = all[Random.Range(0, all.Length)]; while (m.clip == prev);
            return m;
        }

        void Update()
        {
            if (oneShot)
            {
                var st = anim.GetCurrentAnimatorStateInfo(0);
                if (st.fullPathHash == Animator.StringToHash(Current.Path) && st.normalizedTime >= 1f && !anim.IsInTransition(0))
                    PlayLoop(false);
            }
            else if (loopSlot != LoopSlot || Time.time >= nextVary)   // the mood changed the idle, or time for variety
                PlayLoop(false);
            else if (activity == Activity.Rapping && accentEvery.y > 0 && Time.time >= nextAccent)
            {
                Gesture(Moves.RappingAccents);
                nextAccent = Time.time + Random.Range(accentEvery.x, accentEvery.y);
            }

            if (mic && driveMic)
                mic.pose = activity == Activity.Rapping || Speaking ? HoldMic.Pose.Rapping
                    : activity is Activity.Hype or Activity.Working ? HoldMic.Pose.Standing
                    : HoldMic.Pose.Ready;
        }
    }
}
