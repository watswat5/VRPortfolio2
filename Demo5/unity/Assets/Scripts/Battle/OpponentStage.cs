// The run of battles: the opponents in order (the lineup, easiest first), one at a time on the stage. The dressing
// room's door brings out the current one while the view is black (Prepare: spawned at the opponent's spot, its id given
// to the battle) and starts the battle once the view is back (Begin): after the DJ's introductions (DJHost), if there's
// a DJ. The battle drives the opponent: its lines and
// ad-libs with its mouth, its gestures, its mood, rapping on its verse and listening on yours.
// After the battle there's a moment to take it in (the opponent hypes if it won; the beat plays on; the DJ announces
// the winner), then the view
// fades, the player is back in the dressing room, the opponent is gone and the beat fades out. A win moves on to the next opponent; a loss or a draw (or a
// battle that stopped early: the server dropped, the mic failed) means the same one again.
// With a save file (SaveFile), each finished battle goes into its top scores and a win marks the MC beaten; the run
// picks up at the first MC not yet beaten.
using System;
using System.Collections;
using Freestyle.Avatar;
using Freestyle.Venue;
using UnityEngine;
using UnityEngine.Events;

namespace Freestyle.Battle
{
    public class OpponentStage : MonoBehaviour
    {
        public BattleCoordinator battle;
        [Tooltip("The opponents in order (the character prefabs, Assets/Characters): beat one to get the next.")]
        public Performer[] lineup = new Performer[0];
        [Tooltip("Who's next (an index into the lineup).")]
        public int current;
        [Tooltip("Where the opponent stands, facing its way (the venue's Spots/OpponentSpot).")]
        public Transform opponentSpot;
        [Tooltip("Where the player goes after the battle (the venue's Spots/BackstageArrival).")]
        public Transform dressingRoom;
        public Shader fadeShader;

        [Tooltip("The opponent's face light (a soft fill that lights only things on Face Light Layer).")]
        public Light faceLight;
        [Tooltip("The rendering layer the face light lights: each opponent is put on it (as well as Default) when it comes out.")]
        public uint faceLightLayer = 1u << 1;

        [Tooltip("The DJ, who introduces both MCs before the beat. Empty: the scene's, if any.")]
        public DJHost dj;

        [Tooltip("Seconds to take in the result before the fade back to the dressing room.")]
        public float resultHold = 5f;
        public float fadeOut = 0.8f, hold = 0.4f, fadeIn = 0.6f;
        [Tooltip("Seconds the beat (still playing after the battle) takes to fade out once you're back in the dressing room.")]
        public float beatFade = 2.5f;

        [Header("Events")]
        public UnityEvent onWin, onLoss;
        [Tooltip("The last opponent is beaten (the lineup stays on it).")]
        public UnityEvent onChampion;
        [Tooltip("The player is back in the dressing room.")]
        public UnityEvent onReturned;

        /// <summary>The result (user / opponent / draw, or "stopped"), as the result hold starts.</summary>
        public event Action<string> Finished;

        /// <summary>The opponent on the stage now, or null.</summary>
        public Performer Opponent { get; private set; }
        public Performer Next => lineup.Length > 0 ? lineup[Mathf.Clamp(current, 0, lineup.Length - 1)] : null;
        public bool Beaten { get; private set; }   // the whole lineup

        Transform voiceAnchor;
        Transform opponentHead;
        bool battling, returning;
        string lastMc;   // the opponent id we last gave the battle: anything else there was picked by hand

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            // the battle makes the opponent's voices under its anchor once and keeps them, so the anchor is ours (it
            // follows whoever is on the stage) rather than a head that goes away with its opponent
            voiceAnchor = new GameObject("Opponent Voice").transform;
            voiceAnchor.SetParent(transform, false);
            if (battle) battle.opponentVoiceAnchor = voiceAnchor;
        }

        void Start() => Resume();

        /// <summary>Picks up the run where the save leaves it: the first MC not beaten yet (again after the player sheet's
        /// beaten list is edited).</summary>
        public void Resume()
        {
            if (Game.SaveFile.Exists && lineup.Length > 0)
            {
                int first = Array.FindIndex(lineup, m => m && !Game.SaveFile.HasBeaten(m.characterId));
                if (first < 0) { current = lineup.Length - 1; Beaten = true; }
                else { current = first; Beaten = false; }
                if (battle && Next) battle.mcId = Next.characterId;
            }
            if (battle) lastMc = battle.mcId;
        }

        void OnEnable()
        {
            if (!battle) return;
            battle.PhaseChanged += OnPhase;
            battle.OpponentVoiceScheduled += OnVoice;
            battle.OpponentGesture += OnGesture;
            battle.OpponentMoodChanged += OnMood;
            battle.OpponentLineStarted += OnLine;
            battle.OpponentVerseFinished += OnVerseDone;
            battle.BattleFinished += OnFinished;
        }

        void OnDisable()
        {
            if (!battle) return;
            battle.PhaseChanged -= OnPhase;
            battle.OpponentVoiceScheduled -= OnVoice;
            battle.OpponentGesture -= OnGesture;
            battle.OpponentMoodChanged -= OnMood;
            battle.OpponentLineStarted -= OnLine;
            battle.OpponentVerseFinished -= OnVerseDone;
            battle.BattleFinished -= OnFinished;
        }

        StageDoor door;

        void LateUpdate()
        {
            if (opponentHead) voiceAnchor.position = opponentHead.position;
            // the door's hover label says who's next
            if (!door) door = FindAnyObjectByType<StageDoor>();
            if (door && door.Label && door.Label.Showing)
            {
                var next = Next;
                string who = next ? (string.IsNullOrEmpty(next.displayName) ? next.name : next.displayName) : null;
                door.Label.Text = Beaten ? "Victory Lap" : who != null ? $"Battle {who}" : door.hoverText;
            }
        }

        // ---------------------------------------------------------------- the door's hooks

        /// <summary>Brings out the next opponent (StageDoor.onDark: the view is black). An opponent picked by hand since
        /// (the debug HUD's Opponent) comes out instead, and the run carries on from them.</summary>
        public void Prepare()
        {
            if (battle && battle.mcId != lastMc)
            {
                int picked = System.Array.FindIndex(lineup, m => m && m.characterId == battle.mcId);
                if (picked >= 0) { current = picked; Beaten = false; }
            }
            Spawn(Next);
        }

        void Spawn(Performer who)
        {
            Clear();
            if (!who || !opponentSpot) { Debug.LogWarning("OpponentStage: no opponent or no spot to put it on."); return; }
            Opponent = Instantiate(who, opponentSpot.position, opponentSpot.rotation);
            Opponent.name = who.name;
            var anim = Opponent.GetComponent<Animator>();
            opponentHead = anim && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : Opponent.transform;
            voiceAnchor.position = opponentHead.position;
            foreach (var r in Opponent.GetComponentsInChildren<Renderer>(true)) r.renderingLayerMask |= faceLightLayer;   // in the face light
            Opponent.mood = "calm";
            Opponent.Do(Performer.Activity.Idle);
            if (battle) battle.mcId = lastMc = Opponent.characterId;
        }

        /// <summary>Starts the battle against them (StageDoor.onArrived: the view is back): the DJ introduces you both,
        /// and the beat comes in as they say "let's go".</summary>
        public void Begin()
        {
            if (!battle || !Opponent) return;
            battling = true;
            if (!dj) dj = FindAnyObjectByType<DJHost>();
            if (dj && dj.isActiveAndEnabled && dj.introduce && !dj.Introducing) StartCoroutine(dj.Introduce(Opponent, battle.StartBattle));
            else battle.StartBattle();
        }

        // ---------------------------------------------------------------- the battle, on the opponent

        void OnPhase(BattlePhase p)
        {
            if (p == BattlePhase.Starting && !battling)
            {
                // started some other way than the door (the debug HUD's Start battle): the opponent it picked comes out
                // now, and the battle ends like any other
                battling = true;
                if (!Opponent) Spawn(System.Array.Find(lineup, m => m && m.characterId == battle.mcId) ?? Next);
            }
            if (p == BattlePhase.Idle && battling) { Done("stopped"); return; }   // stopped without a result: the same one again
            if (!Opponent) return;
            switch (p)
            {
                case BattlePhase.UserTurn:
                case BattlePhase.Judging:
                    Opponent.Do(Performer.Activity.Listening);
                    break;
                case BattlePhase.OpponentTurn:   // writing its comeback (ad-libs on the beat) until its first line
                    Opponent.Do(Performer.Activity.Idle);
                    break;
            }
        }

        void OnVoice(OpponentVoiceClip c)
        {
            if (Opponent && c.source && c.clip) Opponent.SayScheduled(c.source, c.clip, c.start, c.mouth);
        }

        void OnGesture(string g) { if (Opponent) Opponent.Gesture(g); }
        void OnMood(string m) { if (Opponent) Opponent.mood = m; }
        void OnLine(int i, string line) { if (Opponent) Opponent.Do(Performer.Activity.Rapping); }
        void OnVerseDone() { if (Opponent) Opponent.Do(Performer.Activity.Idle); }

        void OnFinished(ResultMsg r) => Done(r?.winner ?? "draw");

        // ---------------------------------------------------------------- after the battle

        void Done(string winner)
        {
            if (!battling) return;
            battling = false;
            if (Opponent)
            {
                if (winner == "opponent") Opponent.Do(Performer.Activity.Hype);
                else { Opponent.mood = winner == "user" ? "rattled" : Opponent.mood; Opponent.Do(Performer.Activity.Idle); }
            }
            if (battle && battle.Rules != null && winner != "stopped")   // into the save: top scores, beaten MCs
            {
                string id = Opponent ? Opponent.characterId : battle.mcId;
                string name = Opponent && !string.IsNullOrEmpty(Opponent.displayName) ? Opponent.displayName : id;
                Game.SaveFile.RecordBattle(id, name, battle.Rules.UserScore, battle.Rules.OpponentScore, winner);
            }
            if (winner == "user")
            {
                if (current >= lineup.Length - 1) { Beaten = true; onChampion?.Invoke(); }
                else current++;
                onWin?.Invoke();
            }
            else onLoss?.Invoke();
            try { Finished?.Invoke(winner); } catch (Exception e) { Debug.LogException(e); }
            if (!returning) StartCoroutine(Return());
        }

        IEnumerator Return()
        {
            returning = true;
            yield return new WaitForSeconds(resultHold);
            var host = dj ? dj : FindAnyObjectByType<DJHost>();
            while (host && host.Announcing) yield return null;   // the DJ names the winner first
            // the beat has played on through the result; it fades once you're off the stage
            void Back() { Clear(); if (battle) battle.FadeOutBeat(beatFade); }
            if (dressingRoom) yield return Teleport.WithFade(dressingRoom, fadeShader, Back, fadeOut, hold, fadeIn);
            else Back();
            returning = false;
            onReturned?.Invoke();
        }

        /// <summary>Takes the opponent off the stage.</summary>
        public void Clear()
        {
            if (Opponent) Destroy(Opponent.gameObject);
            Opponent = null;
            opponentHead = null;
        }
    }
}
