// The club's DJ, who hosts each battle. When the player comes through the stage door (OpponentStage.Begin), before
// the beat: the DJ picks up the mic, greets the room (a wave), introduces the MC (the crowd cheers and the MC hypes),
// introduces the player by the rapper name in their save (the crowd cheers), and says "let's go" as the beat comes in;
// then counts the last bar into the player's first turn ("Four! Three! Two! One!" on the beats), puts the mic down on
// the table and works the decks for the rest of the battle (and between battles). When the battle's over the mic comes
// back up: "and your winner is..." and the winner (the MC, or the player by name), or a draw, the crowd roaring; then
// back to the decks (the stage waits for it before the trip back to the dressing room).
// The lines are pre-rendered (Characters/DJ/Lines, from backend/tools/render_dj.py; Freestyle > Set Up DJ fills them
// in) but for the player's name, which only the save knows: the server says it in the same voice (GET /dj/name), and
// it's kept on the headset (persistentDataPath/dj_names) so each name is fetched once. No name, or no server: a line
// for an unnamed challenger instead.
// The voice comes out of the PA (the battle's beat anchor) and, quieter, the DJ's own mouth (lip-synced). With no DJ
// character it's a voice on the PA alone. The mic is the MCs' (HoldMic): in hand while hosting, on the table (Mic Rest)
// while working.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Freestyle.Avatar;
using Freestyle.Game;
using Freestyle.Venue;
using UnityEngine;
using UnityEngine.Events;
using Line = Freestyle.Game.InterviewDirector.Line;

namespace Freestyle.Battle
{
    public class DJHost : MonoBehaviour
    {
        public BattleCoordinator battle;
        [Tooltip("The DJ (Assets/Characters/dj.prefab, behind the decks). Empty: a voice on the PA alone.")]
        public Performer dj;
        [Tooltip("What the DJ looks at while working: the decks.")]
        public Transform decks;
        [Tooltip("Where the mic lies while the DJ works: on the table (its up, lying along the table).")]
        public Transform micRest;
        public Crowd crowd;
        public CrowdAudio crowdAudio;

        [Header("What the DJ does")]
        [Tooltip("Introduce the MC and the player before each battle (from the stage door). Off: straight into the beat.")]
        public bool introduce = true;
        [Tooltip("Count the last bar into the player's first turn (after an introduction, mic in hand).")]
        public bool countIn = true;
        [Tooltip("Seconds the room cheers each introduction before the DJ goes on.")]
        public float cheerTime = 2.6f;
        [Tooltip("Seconds between lines.")]
        public float pause = 0.25f;
        [Tooltip("The longest to wait for the player's name from the server (s); then the unnamed line.")]
        public float nameTimeout = 6f;
        [Tooltip("Announce the winner when the battle's over.")]
        public bool announceWinner = true;
        [Tooltip("Seconds the room reacts to the last bar before the DJ picks the mic up for the result.")]
        public float resultPause = 1.5f;
        [Tooltip("How much louder the crowd roars for each introduction and the winner than for a punchline.")]
        [Range(1, 3)] public float roar = 2f;

        [Header("Lines (Freestyle > Set Up DJ fills them from Characters/DJ/Lines)")]
        public Line[] greetings = new Line[0];
        [Tooltip("One per MC, id mc_<character id>; mc_any for an MC without one.")]
        public Line[] mcIntros = new Line[0];
        [Tooltip("The lead-ins to the player's name.")]
        public Line[] challengers = new Line[0];
        [Tooltip("For a player with no rapper name: a whole line.")]
        public Line[] unnamed = new Line[0];
        [Tooltip("Said as the beat comes in.")]
        public Line[] begins = new Line[0];
        [Tooltip("count_4 .. count_1: the words on the last beats before the player's first turn.")]
        public Line[] count = new Line[0];
        [Tooltip("The lead-ins to the winner (\"...and your winner is...\").")]
        public Line[] winLeads = new Line[0];
        [Tooltip("One per MC, id win_mc_<character id>; win_mc_any for an MC without one.")]
        public Line[] mcWins = new Line[0];
        [Tooltip("The player won, with no rapper name to say: a challenger line.")]
        public Line[] challengerWins = new Line[0];
        [Tooltip("A draw: a whole line.")]
        public Line[] draws = new Line[0];

        [Header("Voice")]
        [Range(0, 1)] public float paVolume = 1f;
        [Tooltip("From the DJ's own mouth, under the PA.")]
        [Range(0, 1)] public float directVolume = 0.35f;

        [Header("Events")]
        public UnityEvent onMcIntroduced, onPlayerIntroduced, onBegin;
        [Tooltip("The winner's announced (user, opponent or draw), as the crowd roars.")]
        public StringEvent onWinnerAnnounced;

        /// <summary>The introductions are running.</summary>
        public bool Introducing { get; private set; }
        /// <summary>The winner is being announced (the stage waits for it).</summary>
        public bool Announcing { get; private set; }
        /// <summary>The mic is in the DJ's hand.</summary>
        public bool Holding { get; private set; }

        AudioSource pa, paCount, mouthCount;
        AudioClip nameClip;
        string nameFor;   // whose name nameClip says
        bool nameDone = true;
        int nameAsk;
        Coroutine setDown;
        Performer opponentNow;   // the MC being introduced (or who may win): the DJ looks at them while saying so

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            if (!crowd) crowd = FindAnyObjectByType<Crowd>();
            if (!crowdAudio) crowdAudio = FindAnyObjectByType<CrowdAudio>();
        }

        IEnumerator Start()
        {
            yield return null;   // the mic's made in HoldMic.Start
            PutDown(instant: true);
        }

        void OnEnable()
        {
            if (!battle) return;
            battle.TurnStarted += OnTurn;
            battle.UserTurnLive += OnLive;
            battle.PhaseChanged += OnPhase;
            battle.BattleFinished += OnFinished;
        }

        void OnDisable()
        {
            if (!battle) return;
            battle.TurnStarted -= OnTurn;
            battle.UserTurnLive -= OnLive;
            battle.PhaseChanged -= OnPhase;
            battle.BattleFinished -= OnFinished;
        }

        // ---------------------------------------------------------------- the introductions

        /// <summary>The introductions, then startBattle as the DJ says "let's go" (the beat comes in under it). The
        /// count-in and the mic going down follow from the battle's events.</summary>
        public IEnumerator Introduce(Performer opponent, Action startBattle)
        {
            Introducing = true;
            opponentNow = opponent;
            string name = PlayerName;
            AskName(name);
            PickUp();
            if (crowdAudio) crowdAudio.hushed = true;
            yield return new WaitForSeconds(0.5f);   // the mic comes up

            if (dj) dj.Gesture("waves");
            yield return Say(Pick(greetings));

            string id = opponent ? opponent.characterId : battle ? battle.mcId : "";
            LookAt(opponent);
            yield return Say(Find(mcIntros, "mc_" + id) ?? Find(mcIntros, "mc_any"));
            if (opponent) opponent.Do(Performer.Activity.Hype);
            Cheer();
            onMcIntroduced?.Invoke();
            yield return new WaitForSeconds(cheerTime);
            if (opponent) opponent.Do(Performer.Activity.Idle);
            LookAt(null);   // to the challenger

            for (float t = 0; !nameDone && t < nameTimeout; t += Time.deltaTime) yield return null;
            var lead = Pick(challengers);
            if (nameClip && lead != null)
            {
                yield return Say(lead, 0.05f);
                yield return Say(new Line { id = "name", text = name, clip = nameClip });
            }
            else yield return Say(Pick(unnamed));
            Cheer();
            onPlayerIntroduced?.Invoke();
            yield return new WaitForSeconds(cheerTime);

            try { startBattle?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
            onBegin?.Invoke();
            yield return Say(Pick(begins));
            if (crowdAudio) crowdAudio.hushed = false;
            Introducing = false;
            // not counting in (no battle after all): the mic goes back down
            if (!countIn || !battle || battle.Phase is BattlePhase.Idle or BattlePhase.Finished) PutDown();
        }

        /// <summary>The room erupts (an introduction, the winner): louder than a punchline (roar), the hush lifted.</summary>
        void Cheer(float seconds = -1f)
        {
            if (seconds < 0) seconds = cheerTime + 1.5f;
            if (crowd) crowd.CheerFor(seconds);
            if (crowdAudio) crowdAudio.CheerFor(seconds, roar);
        }

        static string PlayerName => SaveFile.Exists ? SaveFile.Data.interview?.name?.Trim() : null;

        /// <summary>The player's name in the DJ's voice, asked for now (kept: the winner may be them).</summary>
        void AskName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { nameClip = null; nameFor = null; nameDone = true; return; }
            if (nameFor == name && nameClip) { nameDone = true; return; }
            nameClip = null;
            nameFor = name;
            FetchName(name);
        }

        // ---------------------------------------------------------------- the result

        void OnFinished(ResultMsg r)
        {
            string winner = r?.winner ?? "draw";
            if (announceWinner && isActiveAndEnabled) StartCoroutine(Announce(winner));
            else if (dj && !Holding) dj.Gesture("claps");
        }

        /// <summary>The mic up, "and your winner is..." and the winner, the room roaring; the mic down again.</summary>
        IEnumerator Announce(string winner)
        {
            Announcing = true;
            if (winner == "user") AskName(PlayerName);
            yield return new WaitForSeconds(resultPause);   // the room takes in the last bar
            PickUp();
            yield return new WaitForSeconds(0.5f);
            if (winner == "draw") yield return Say(Pick(draws));
            else
            {
                for (float t = 0; winner == "user" && !nameDone && t < nameTimeout; t += Time.deltaTime) yield return null;
                yield return Say(Pick(winLeads), 0.1f);
                if (winner == "user")
                    yield return nameClip ? Say(new Line { id = "name", text = nameFor, clip = nameClip }) : Say(Pick(challengerWins));
                else
                {
                    string id = battle ? battle.mcId : "";
                    LookAt(opponentNow);
                    yield return Say(Find(mcWins, "win_mc_" + id) ?? Find(mcWins, "win_mc_any"));
                }
            }
            Cheer(cheerTime + 3f);
            try { onWinnerAnnounced?.Invoke(winner); } catch (Exception e) { Debug.LogException(e); }
            yield return new WaitForSeconds(cheerTime);
            PutDown();
            if (dj) dj.Gesture("claps");
            yield return new WaitForSeconds(1f);
            Announcing = false;
        }

        static Line Pick(Line[] lines)
        {
            var ok = lines?.Where(l => l != null && (l.clip || !string.IsNullOrEmpty(l.text))).ToArray();
            return ok is { Length: > 0 } ? ok[UnityEngine.Random.Range(0, ok.Length)] : null;
        }

        static Line Find(Line[] lines, string id) => lines?.FirstOrDefault(l => l != null && l.id == id);

        /// <summary>A line, now: through the PA and from the DJ's mouth; waits it out (a line without its clip waits
        /// as long as it would take to say).</summary>
        IEnumerator Say(Line line, float tail = -1f)
        {
            if (line == null) yield break;
            if (tail < 0) tail = pause;
            if (!line.clip)
            {
                Debug.Log($"[DJ] {line.text}");
                yield return new WaitForSeconds(Mathf.Clamp(line.text.Length * 0.06f, 1f, 5f) + tail);
                yield break;
            }
            if (crowdAudio) crowdAudio.hushed = true;
            double at = AudioSettings.dspTime + 0.05;
            Play(line.clip, at, line.cues ? CueLipSync.ParseJson(line.cues.text) : null);
            yield return new WaitForSeconds(line.clip.length + 0.05f + tail);
        }

        /// <summary>A clip scheduled at dsp time at (AudioSource.PlayScheduled's time) on the PA and at the mouth. The
        /// count-in has its own pair (count): it's scheduled ahead, while a line may still be playing.</summary>
        void Play(AudioClip clip, double at, List<(float t, string shape)> cues, bool count = false)
        {
            var src = count ? (paCount ? paCount : paCount = Source("DJ Count (PA)")) : pa ? pa : pa = Source("DJ Voice (PA)");
            src.Stop();
            src.clip = clip;
            src.volume = paVolume;
            src.PlayScheduled(at);
            var mouth = !dj || !dj.voice ? null : !count ? dj.voice : mouthCount ? mouthCount : mouthCount = MouthSource();
            if (mouth)
            {
                mouth.Stop();
                mouth.clip = clip;
                mouth.volume = directVolume;
                mouth.PlayScheduled(at);
                dj.SayScheduled(mouth, clip, at, cues is { Count: > 0 } ? cues : null);
            }
        }

        /// <summary>A voice on the PA (the battle's beat anchor: the speakers), as spatial as the beat.</summary>
        AudioSource Source(string name)
        {
            var anchor = battle && battle.beatAnchor ? battle.beatAnchor : transform;
            var go = new GameObject(name);
            go.transform.SetParent(anchor, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = battle ? battle.beatSpatialBlend : 0f;
            s.dopplerLevel = 0f;
            if (battle) s.outputAudioMixerGroup = battle.voiceMixerGroup;
            return s;
        }

        /// <summary>A second voice at the DJ's mouth, set like the first.</summary>
        AudioSource MouthSource()
        {
            var v = dj.voice;
            var go = new GameObject("Voice (count)");
            go.transform.SetParent(v.transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = v.spatialBlend; s.minDistance = v.minDistance; s.maxDistance = v.maxDistance;
            s.rolloffMode = v.rolloffMode; s.outputAudioMixerGroup = v.outputAudioMixerGroup;
            return s;
        }

        // ---------------------------------------------------------------- the player's name

        /// <summary>The player's name in the DJ's voice: from the headset's own copy, else the server (then kept).</summary>
        async void FetchName(string name)
        {
            int ask = ++nameAsk;
            nameDone = false;
            AudioClip clip = null;
            try
            {
                string file = Path.Combine(Application.persistentDataPath, "dj_names", $"{Hash(name)}.wav");
                byte[] wav = null;
                try { if (File.Exists(file)) wav = File.ReadAllBytes(file); } catch (Exception e) { Debug.LogWarning($"[DJ] {e.Message}"); }
                if (wav == null && battle)
                {
                    wav = await battle.GetBytes("dj/name?name=" + Uri.EscapeDataString(name));
                    if (wav != null)
                        try { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, wav); }
                        catch (Exception e) { Debug.LogWarning($"[DJ] couldn't keep the name: {e.Message}"); }
                }
                if (wav != null) clip = WavClip(wav, "DJ: " + name);
            }
            catch (Exception e) { Debug.LogException(e); }
            if (ask != nameAsk) return;   // a newer ask
            nameClip = clip;
            nameDone = true;
        }

        static string Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s.ToLowerInvariant()) h = (h ^ c) * 16777619;
            return h.ToString("x8");
        }

        /// <summary>A 16-bit PCM WAV as a clip (null if it isn't one).</summary>
        static AudioClip WavClip(byte[] b, string name)
        {
            if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') return null;
            int ch = 1, rate = 0, bits = 16, at = 12;
            while (at + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, at, 4);
                int len = BitConverter.ToInt32(b, at + 4);
                if (id == "fmt ") { ch = BitConverter.ToInt16(b, at + 10); rate = BitConverter.ToInt32(b, at + 12); bits = BitConverter.ToInt16(b, at + 22); }
                else if (id == "data")
                {
                    if (bits != 16 || rate <= 0 || ch <= 0) return null;
                    len = Math.Min(len, b.Length - at - 8);
                    int n = len / 2;
                    var x = new float[n];
                    for (int i = 0; i < n; i++) x[i] = BitConverter.ToInt16(b, at + 8 + i * 2) / 32768f;
                    var clip = AudioClip.Create(name, n / ch, ch, rate, false);
                    clip.SetData(x, 0);
                    return clip;
                }
                at += 8 + len + (len & 1);
            }
            return null;
        }

        // ---------------------------------------------------------------- the count-in, and the mic

        void OnTurn(TurnMsg t)
        {
            if (t.turn != "user" || t.round != 1 || !countIn || !Holding) return;
            ScheduleCount();
        }

        /// <summary>The words on the last beats before the player's first bar ("Four!" on the beat with four left, as
        /// the turn display counts), as one clip so the mouth follows all of it.</summary>
        void ScheduleCount()
        {
            var words = count.Where(l => l != null && l.clip).ToArray();
            int n = Mathf.Min(words.Length, battle.BeatsPerBar);
            if (n == 0) return;
            double beat = battle.BeatSeconds, lat = battle.HeardLatency, now = AudioSettings.dspTime + 0.05;
            while (n > 0 && battle.UserTurnStart - n * beat - lat < now) n--;   // too late for the first ones
            if (n == 0) return;
            var first = words[words.Length - n].clip;
            int rate = first.frequency, chans = first.channels;
            double t0 = battle.UserTurnStart - n * beat;   // heard time of the first word
            var all = new List<float>();
            var cues = new List<(float t, string shape)>();
            bool mouthCues = true;
            for (int k = n; k >= 1; k--)
            {
                var w = words[words.Length - k];
                if (w.clip.frequency != rate || w.clip.channels != chans) continue;
                float off = (float)((n - k) * beat);
                int at = Mathf.RoundToInt(off * rate) * chans;
                var x = new float[w.clip.samples * chans];
                if (!w.clip.GetData(x, 0)) { Debug.LogWarning("[DJ] count clips need Load Type Decompress On Load"); return; }
                if (k > 1)   // a word that rings on past its beat fades out before the next one
                {
                    int end = Math.Min(x.Length, Mathf.RoundToInt((float)beat * rate) * chans), fade = Mathf.RoundToInt(0.04f * rate) * chans;
                    for (int i = Math.Max(0, end - fade); i < x.Length; i++) x[i] *= i >= end ? 0f : (end - i) / (float)fade;
                }
                while (all.Count < at) all.Add(0f);
                for (int i = 0; i < x.Length; i++)
                {
                    if (at + i < all.Count) all[at + i] += x[i];
                    else all.Add(x[i]);
                }
                if (w.cues) foreach (var (ct, s) in CueLipSync.ParseJson(w.cues.text)) cues.Add((ct + off, s));
                else mouthCues = false;
            }
            var clip = AudioClip.Create("DJ count", all.Count / chans, chans, rate, false);
            clip.SetData(all.ToArray(), 0);
            Play(clip, t0 - lat, mouthCues ? cues : null, count: true);
        }

        void OnLive(int round)
        {
            if (Holding && !Introducing) PutDown();   // the player has the floor: the mic down, back to the decks
        }

        void OnPhase(BattlePhase p)
        {
            if (p is BattlePhase.Idle or BattlePhase.Finished)
            {
                if (crowdAudio) crowdAudio.hushed = false;
                if (Holding && !Introducing && !Announcing) PutDown();
            }
        }

        /// <summary>The mic from the table into the hand: hosting.</summary>
        void PickUp()
        {
            Holding = true;
            if (setDown != null) { StopCoroutine(setDown); setDown = null; }
            if (!dj) return;
            var hold = dj.mic;
            if (hold)
            {
                if (hold.mic) hold.mic.SetParent(dj.transform, true);
                hold.held = true;   // (it puts the mic in the fist every frame)
            }
            dj.driveMic = true;
            dj.Do(Performer.Activity.Hosting);
            LookAt(null);
        }

        /// <summary>The DJ's eyes on whoever he's talking about: an MC's head, or the player (null).</summary>
        void LookAt(Performer who)
        {
            if (!dj || !dj.TryGetComponent<LookAndBlink>(out var look)) return;
            var anim = who ? who.GetComponentInChildren<Animator>() : null;
            var head = anim && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
            look.target = head ? head : who ? who.transform : null;   // null: the camera, the player
        }

        /// <summary>The mic down on the table, and the DJ to the decks.</summary>
        void PutDown(bool instant = false)
        {
            Holding = false;
            if (!dj) return;
            dj.driveMic = false;
            if (dj.mic) dj.mic.pose = HoldMic.Pose.Standing;
            dj.Do(Performer.Activity.Working);
            if (dj.TryGetComponent<LookAndBlink>(out var look)) look.target = decks;
            if (setDown != null) StopCoroutine(setDown);
            setDown = StartCoroutine(SetDown(instant ? 0f : 0.7f));
        }

        IEnumerator SetDown(float after)
        {
            if (after > 0) yield return new WaitForSeconds(after);   // the hand comes down to the table
            setDown = null;
            if (Holding || !dj || !dj.mic) yield break;
            dj.mic.held = false;   // the fingers open, the hand's free
            var m = dj.mic.mic;
            if (m && micRest)
            {
                m.SetParent(micRest, false);
                m.SetLocalPositionAndRotation(new Vector3(0, 0.028f, 0), Quaternion.Euler(0, 0, 90));   // on its side, the grille's ball on the table
            }
        }
    }
}
