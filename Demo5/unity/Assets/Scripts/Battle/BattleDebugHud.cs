// On-screen stand-in for the web GUI, for testing BattleCoordinator in the editor (flat screen, not VR).
// Also a reference for reading the coordinator: everything here comes from its properties and events.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Freestyle.Battle
{
    public class BattleDebugHud : MonoBehaviour
    {
        public BattleCoordinator battle;
        [Tooltip("F1 toggles it.")]
        public bool visible = true;

        readonly List<string> feed = new();
        Vector2 scroll;
        GUIStyle wrap, big;

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
        }

        void OnEnable()
        {
            if (!battle) return;
            battle.Reaction += OnReaction;
            battle.TurnStarted += t => feed.Insert(0, t.turn == "user" ? $"— round {t.round}: your turn —" : $"— {battle.OpponentName}'s turn —");
            battle.BattleFinished += r => feed.Insert(0, $"RESULT: {r.winner}  {r.state.score.user:0} – {r.state.score.opponent:0}");
        }

        void OnDisable()
        {
            if (battle) battle.Reaction -= OnReaction;
        }

        void OnReaction(ReactionMsg r)
        {
            var tags = new List<string>();
            if (!string.IsNullOrEmpty(r.opponent?.trigger)) tags.Add("hit a nerve: " + r.opponent.trigger);
            if (r.word_drops?.Count > 0) tags.Add("word drop: " + string.Join(", ", r.word_drops));
            if (r.Punchline) tags.Add("PUNCHLINE");
            if (r.FellOff) tags.Add("fell off");
            feed.Insert(0, $"{(r.IsUser ? "you" : "opp")} “{r.chunk?.text}”  crowd {r.crowd.reaction:0.0} · +{r.points:0} {string.Join(" ", tags.Select(t => $"[{t}]"))}");
            if (feed.Count > 30) feed.RemoveAt(feed.Count - 1);
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (UnityEngine.InputSystem.Keyboard.current?.f1Key.wasPressedThisFrame == true) visible = !visible;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.F1)) visible = !visible;
#endif
        }

        void OnGUI()
        {
            if (!visible || !battle) return;
            wrap ??= new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
            big ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, richText = true };

            GUILayout.BeginArea(new Rect(10, 10, 520, Screen.height - 20), GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);
            var b = battle;
            bool idle = b.Phase == BattlePhase.Idle || b.Phase == BattlePhase.Finished;

            // setup
            GUILayout.Label($"<b>Freestyle</b> · {b.Status}", wrap);
            if (idle)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Server", GUILayout.Width(60));
                b.serverUrl = GUILayout.TextField(b.serverUrl);
                if (GUILayout.Button("Refresh", GUILayout.Width(70))) _ = b.LoadLobby();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Password", GUILayout.Width(60));
                b.Password = GUILayout.PasswordField(b.Password ?? "", '*');   // in memory only; Refresh to log in
                GUILayout.EndHorizontal();
                Cycle("Opponent", b.Mcs.Select(m => m.id).ToList(), ref b.mcId, id => b.Mcs.First(m => m.id == id) is var m ? $"{m.name} ({m.difficulty})" : id);
                var songIds = b.Songs.Select(s => s.id).Append("").ToList();
                string songSel = b.useSong ? b.SelectedSong?.id ?? "" : "";
                Cycle("Beat", songIds, ref songSel, id => id == "" ? $"Metronome {b.metronomeBpm:0} BPM" : b.Songs.First(s => s.id == id) is var s ? $"{s.title} · {s.bpm:0.0} BPM{(s.local ? " · on device" : "")}" : id);
                b.useSong = songSel != "";
                if (b.useSong) b.songId = songSel;
                GUILayout.BeginHorizontal();
                b.rounds = Stepper("Rounds", b.rounds, 1, 5);
                b.barsPerTurn = Stepper("Bars", b.barsPerTurn, 4, 32, 4);
                b.assist = GUILayout.Toggle(b.assist, "Assist");
                b.adlibs = GUILayout.Toggle(b.adlibs, "Ad-libs");
                GUILayout.EndHorizontal();
                b.voice = (OpponentVoice)GUILayout.Toolbar((int)b.voice, new[] { b.VoiceEngineName ?? "Server", "espeak", "espeak grid", "Text" });
                if (GUILayout.Button("Start battle", GUILayout.Height(32))) b.StartBattle();
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = b.UserTurnIsLive;
                if (GUILayout.Button("End my turn", GUILayout.Height(32))) b.EndTurn();
                GUI.enabled = true;
                if (GUILayout.Button("Stop", GUILayout.Height(32), GUILayout.Width(80))) b.StopBattle();
                GUILayout.EndHorizontal();
            }

            if (b.Result != null)
            {
                string who = b.Result.winner == "user" ? "You win!" : b.Result.winner == "opponent" ? $"{b.OpponentName} wins" : "Draw";
                GUILayout.Label(who, big);
            }

            // turn + beat
            string beatDots = string.Concat(Enumerable.Range(0, b.BeatsPerBar).Select(i => i == b.BeatInBar && b.BeatPhase < 0.35f ? "●" : "○"));
            GUILayout.Label($"{TurnLabel(b)}   {beatDots}", big);
            GUILayout.Label(TurnSub(b), wrap);
            Bar(b.TurnProgress, b.Phase == BattlePhase.OpponentTurn ? Color.magenta : Color.cyan);
            GUILayout.Label("mic");
            Bar(b.MicLevel, Color.green);

            // crowd, score, opponent
            var s = b.CurrentState;
            if (s != null)
            {
                GUILayout.Label($"<b>Score</b>  you {s.score.user:0} – {s.score.opponent:0} {b.OpponentName}", wrap);
                GUILayout.Label($"<b>Crowd</b>  {s.crowd.mood}  (level {s.crowd.level:0.0}/11 · base {s.crowd.@base:0.0})", wrap);
                Bar(s.crowd.level / 11f, Color.Lerp(Color.blue, Color.red, s.crowd.level / 11f));
                var o = s.opponent;
                GUILayout.Label($"<b>{b.OpponentName}</b>  {o.mood} · {o.gesture} · composure {o.composure:0.0}", wrap);
                GUILayout.Label($"intimidated {o.intimidated:0.0} · entertained {o.entertained:0.0} · confident {o.confident:0.0} · annoyed {o.annoyed:0.0} · impressed {o.impressed:0.0}", wrap);
                if (s.mc?.sore_spots_total > 0)
                    GUILayout.Label($"sore spots: {string.Join(", ", s.mc.sore_spots_found)} {string.Concat(Enumerable.Repeat("? ", s.mc.sore_spots_total - s.mc.sore_spots_found.Count))}", wrap);
            }
            var w = b.LastMetrics?.window;
            if (w != null)
                GUILayout.Label($"flow: {w.syllable_rate:0.0} syl/s · {w.syllables_per_beat:0.0} syl/beat · pocket {w.pocket:0.00} · on beat {w.on_beat:0.00} · " +
                                $"rhyme density {b.LastMetrics.lyrics?.rhyme_density:0.00} · multis {b.LastMetrics.lyrics?.multi_rhymes}", wrap);

            // assist
            if (b.WordDrops.Count > 0)
                GUILayout.Label("Word drops: " + string.Join("  ", b.WordDrops.Select(d => b.WordDropsUsed.Contains(d) ? $"<color=lime>{d}</color>" : d)), wrap);
            if (b.Hints != null)
                GUILayout.Label(b.Hints.rhymes.Count > 0 ? $"Rhymes with <b>{b.Hints.word}</b>: {string.Join(", ", b.Hints.rhymes)}" : $"No common rhymes for <b>{b.Hints.word}</b>", wrap);

            // stage: your words, or the opponent's verse
            GUILayout.Space(6);
            if (b.Phase == BattlePhase.OpponentTurn && b.VerseLines.Count > 0)
            {
                for (int i = 0; i < b.VerseLines.Count; i++)
                    GUILayout.Label(i == b.VerseLine ? $"<b><color=yellow>{b.VerseLines[i]}</color></b>" : i < b.VerseLine ? $"<color=grey>{b.VerseLines[i]}</color>" : b.VerseLines[i], wrap);
                if (b.Interpretation != "") GUILayout.Label($"<i>“{b.Interpretation}”</i>", wrap);
            }
            else
                GUILayout.Label($"{b.CommittedText} <color=grey>{b.PartialText}</color>", wrap);

            GUILayout.Space(6);
            foreach (var line in feed) GUILayout.Label(line, wrap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static string TurnLabel(BattleCoordinator b) => b.Phase switch
        {
            BattlePhase.UserTurn when b.CountInBeatsLeft > 0 => $"Your turn in {b.CountInBeatsLeft}",
            BattlePhase.UserTurn => "Your turn — spit!",
            BattlePhase.Judging => "Judging…",
            BattlePhase.OpponentTurn => $"{b.OpponentName}'s turn",
            BattlePhase.Starting => "Starting…",
            _ => "",
        };

        static string TurnSub(BattleCoordinator b) => b.Phase switch
        {
            BattlePhase.UserTurn when b.CountInBeatsLeft > 0 => $"Round {b.Round} · {b.UserTurnBars} bars at {b.Bpm:0.#} BPM",
            BattlePhase.UserTurn => $"Round {b.Round} · bar {b.CurrentBar} of {b.TurnBars}",
            BattlePhase.OpponentTurn when !b.VerseWaiting => $"Round {b.Round} · bar {b.CurrentBar} of {b.TurnBars}",
            BattlePhase.OpponentTurn => (b.VerseLines.Count > 0 ? "Coming in on the next bar…" : "Writing the comeback…") +
                                        (b.LastAdlib != "" ? $"  “{b.LastAdlib}”" : ""),
            _ => "",
        };

        static void Bar(float v, Color c)
        {
            var r = GUILayoutUtility.GetRect(100, 8, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(r, Texture2D.grayTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, c, 0, 0);
        }

        static int Stepper(string label, int v, int min, int max, int step = 1)
        {
            GUILayout.Label($"{label} {v}", GUILayout.Width(70));
            if (GUILayout.Button("-", GUILayout.Width(22))) v = Mathf.Max(min, v - step);
            if (GUILayout.Button("+", GUILayout.Width(22))) v = Mathf.Min(max, v + step);
            return v;
        }

        static void Cycle(string label, List<string> ids, ref string sel, System.Func<string, string> name)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(60));
            int i = Mathf.Max(0, ids.IndexOf(sel));
            if (ids.Count > 0)
            {
                if (GUILayout.Button("<", GUILayout.Width(22))) sel = ids[(i + ids.Count - 1) % ids.Count];
                GUILayout.Label(name(ids[Mathf.Max(0, ids.IndexOf(sel))]));
                if (GUILayout.Button(">", GUILayout.Width(22))) sel = ids[(i + 1) % ids.Count];
            }
            else GUILayout.Label("(server not reached)");
            GUILayout.EndHorizontal();
        }
    }
}
