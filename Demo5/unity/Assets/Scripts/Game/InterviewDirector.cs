// The pre-show interview, the first time the player heads for the stage (no save file yet: SaveFile). The stage door
// asks Needed; if so it runs this before its own trip. The view fades and the player is standing in a void, in a
// spotlight, across from the interviewer in hers (the "Interview Void" built by Freestyle > Build Interview Void). She
// says her intro, then asks each question; the player answers out loud (recorded until they stop talking, or a time
// limit: at a pause the answer so far is heard, and an "umm", or a trailing "so..." or "and...", waits longer for the
// rest), she says a generic affirmation, and on to the next. Each answer goes to the server to be heard while the next
// is asked; at the end they're summed up (BattleCoordinator.Summarize) and kept in the new save, which every battle
// then sends the MCs (and the interview is skipped from then on). Without a server the answers are kept as they are.
// Her lines are pre-rendered clips (Characters/Interviewer/Lines, from backend/tools/render_interviewer.py); a line
// without a clip is shown as a subtitle only. Every line is subtitled over her head.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Freestyle.Avatar;
using Freestyle.Battle;
using Freestyle.Venue;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Freestyle.Game
{
    public class InterviewDirector : MonoBehaviour
    {
        [Serializable]
        public class Line
        {
            public string id;
            [TextArea] public string text;
            public AudioClip clip;
            [Tooltip("Mouth cues for the clip (its .json beside it), for the lip sync.")]
            public TextAsset cues;
        }

        public BattleCoordinator battle;
        [Tooltip("Where the player stands, facing the interviewer.")]
        public Transform playerSpot;
        [Tooltip("Where she stands, facing the player.")]
        public Transform interviewerSpot;
        [Tooltip("Her character (Assets/Characters/interviewer.prefab).")]
        public Performer interviewer;
        public Shader fadeShader;

        [Header("Her lines (Freestyle > Build Interview Void fills them from Characters/Interviewer/Lines)")]
        public Line intro;
        public Line[] questions = new Line[0];
        public Line[] affirmations = new Line[0];
        public Line outro;

        [Header("Listening")]
        [Tooltip("An answer ends after this much quiet once they've said something (s).")]
        public float endSilence = 1.6f;
        [Tooltip("The longest an answer can run (s).")]
        public float maxAnswer = 25f;
        [Tooltip("Nothing said for this long: on to the next question (s).")]
        public float giveUp = 8f;
        [Tooltip("At the pause, the answer so far is heard (the server): if it's only a filler (\"umm\"), or trails off " +
                 "(\"so...\", \"and...\"), they're thinking: wait this much longer for the rest (s). 0: off.")]
        public float thinkingWait = 4f;
        [Tooltip("How many times one answer can be given more time.")]
        public int maxThinkingWaits = 2;
        public int sampleRate = 16000;

        /// <summary>The interview is due: there's no save yet (and there's somewhere to hold it).</summary>
        public bool Needed => !SaveFile.Exists && playerSpot && interviewerSpot;
        public bool Running { get; private set; }
        /// <summary>"Listening", "Thinking", or "" (for the status line).</summary>
        public string State { get; private set; } = "";

        Performer her;
        TextMeshPro subtitle, prompt;

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
        }

        /// <summary>The whole interview: into the void, the questions, the save. Ends with the player still in the void
        /// (the door's own trip takes them on to the stage, and calls Leave while the view is black).</summary>
        public IEnumerator Run()
        {
            if (Running) yield break;
            Running = true;
            yield return Teleport.WithFade(playerSpot, fadeShader, Arrive, 0.5f, 0.4f, 0.8f);
            yield return new WaitForSeconds(0.6f);

            var answers = new List<InterviewAnswer>();
            pending = 0;
            yield return Say(intro);
            var rng = new System.Random();
            Line lastOk = null;
            foreach (var q in questions)
            {
                yield return Say(q);
                byte[] wav = null;
                string heard = null;
                yield return Listen((w, h) => { wav = w; heard = h; });
                var a = new InterviewAnswer { question = q.text, answer = heard ?? "" };
                answers.Add(a);
                if (wav != null && heard == null && battle) Hear(a, wav);   // heard while the next question's asked
                // a generic affirmation (not the same one twice running)
                var pool = affirmations.Where(x => x != null && x != lastOk).ToArray();
                if (pool.Length > 0) { lastOk = pool[rng.Next(pool.Length)]; yield return Say(lastOk); }
            }

            State = "Thinking";
            var sayOutro = StartCoroutine(Say(outro));
            for (float w = 0; pending > 0 && w < 40; w += Time.unscaledDeltaTime) yield return null;   // the answers heard
            summary = null;
            if (battle && answers.Any(x => x.answer.Length > 0))
            {
                pending = 1;
                Sum(answers);
                for (float w = 0; pending > 0 && w < 40; w += Time.unscaledDeltaTime) yield return null;
            }
            yield return sayOutro;
            var record = new InterviewRecord
            {
                answers = answers,
                name = summary?.name ?? "",
                summary = summary?.summary ?? string.Join(" ", answers.Where(x => x.answer.Length > 0).Select(x => $"{x.question} {x.answer}")),
                source = summary?.source ?? "offline",
            };
            SaveFile.Create(record);
            Debug.Log($"[Interview] saved ({record.source}): {record.summary}");
            State = "";
            Running = false;
        }

        int pending;   // server calls still out
        BattleCoordinator.InterviewSummary summary;

        async void Hear(InterviewAnswer a, byte[] wav)
        {
            pending++;
            try { a.answer = await battle.Transcribe(wav) ?? ""; }
            catch (Exception e) { Debug.LogWarning($"[Interview] couldn't hear an answer: {e.Message}"); }
            finally { pending--; }
        }

        async void Sum(List<InterviewAnswer> answers)
        {
            try { summary = await battle.Summarize(answers); }
            catch (Exception e) { Debug.LogWarning($"[Interview] couldn't sum up: {e.Message}"); }
            finally { pending = 0; }
        }

        /// <summary>The player's arrived in the void (the view's black): she appears in her spotlight.</summary>
        void Arrive()
        {
            Leave();
            if (!interviewer || !interviewerSpot) return;
            var rot = playerSpot ? Quaternion.LookRotation(Flat(playerSpot.position - interviewerSpot.position)) : interviewerSpot.rotation;
            her = Instantiate(interviewer, interviewerSpot.position, rot);
            her.name = "Interviewer";
            // no props for now: her clipboard and pencil (HoldClipboard) don't sit in her hands yet
            if (her.TryGetComponent<HoldClipboard>(out var props))
            {
                if (props.clipboard) Destroy(props.clipboard.gameObject);
                if (props.pencil) Destroy(props.pencil.gameObject);
                Destroy(props);
            }
            // a professional's manner: calm talking while she speaks, a nod on each affirmation, calm listening
            her.onlyClips = new[]
            {
                "m_gestic_talk_neutral_01", "m_gestic_talk_neutral_02",            // talking (the Rapping slot)
                "m_gestic_listen_neutral_01", "m_gestic_listen_relaxed_01",        // listening (not the excited one)
                "m_gestic_listen_accept_01", "m_gestic_listen_accept_03",          // nods
            };
            her.accentEvery = Vector2.zero;   // (no presentation flourishes between lines)
            her.Do(Performer.Activity.Idle);
            subtitle = Text("Subtitle", her.transform.position + Vector3.up * 2.05f, 0.45f, Color.white);
            prompt = Text("Prompt", her.transform.position + Vector3.up * 1.92f, 0.3f, new Color(0.2f, 0.95f, 0.9f));
        }

        /// <summary>She and her subtitles go (the door's trip calls this while the view is black).</summary>
        public void Leave()
        {
            if (her) Destroy(her.gameObject);
            if (subtitle) Destroy(subtitle.gameObject);
            if (prompt) Destroy(prompt.gameObject);
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-6f ? v : Vector3.forward; }

        TextMeshPro Text(string name, Vector3 at, float size, Color colour)
        {
            var go = new GameObject("Interview " + name);
            go.transform.SetParent(transform, true);
            go.transform.position = at;
            var t = go.AddComponent<TextMeshPro>();
            t.fontSize = size; t.color = colour; t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.rectTransform.sizeDelta = new Vector2(2.2f, 0.6f);
            t.outlineWidth = 0.2f; t.outlineColor = new Color32(0, 0, 0, 200);
            return t;
        }

        void LateUpdate()
        {
            // the subtitles face the player
            var cam = Camera.main ? Camera.main.transform : null;
            if (!cam) return;
            foreach (var t in new[] { subtitle, prompt })
                if (t) t.transform.rotation = Quaternion.LookRotation(Flat(t.transform.position - cam.position));
        }

        IEnumerator Say(Line line)
        {
            if (line == null || string.IsNullOrEmpty(line.text) && !line.clip) yield break;
            if (subtitle) subtitle.text = line.text;
            if (prompt) prompt.text = "";
            if (her)
            {
                if (Array.IndexOf(affirmations, line) >= 0) { her.Do(Performer.Activity.Idle); her.Gesture("nods"); }   // "Very good." with a nod
                else her.Do(Performer.Activity.Rapping);   // talking: the calm talk loops (onlyClips)
            }
            if (line.clip && her)
            {
                her.Say(line.clip, line.cues ? line.cues.text : null);
                yield return new WaitForSeconds(line.clip.length + 0.25f);
            }
            else yield return new WaitForSeconds(Mathf.Clamp(line.text.Length * 0.06f, 1.2f, 6f));   // a subtitle only
            if (her && Array.IndexOf(affirmations, line) < 0) her.Do(Performer.Activity.Idle);
        }

        /// <summary>Records one answer: until they've talked and then gone quiet, or the limits. The WAV, or null when
        /// nothing was said (or there's no mic).</summary>
        IEnumerator Listen(Action<byte[], string> done)
        {
            State = "Listening";
            if (her) her.Do(Performer.Activity.Listening);
            if (prompt) prompt.text = "(answer out loud)";
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                for (float w = 0; w < 30 && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone); w += Time.unscaledDeltaTime)
                    yield return null;
            }
#endif
            var clip = Audio.SharedMic.Acquire(battle && !string.IsNullOrEmpty(battle.micDevice) ? battle.micDevice : null, sampleRate);
            if (!clip) { State = ""; done(null, null); yield break; }
            int rate = clip.frequency, pos = Audio.SharedMic.Position;
            var samples = new List<float>(rate * 10);
            var buf = new float[0];
            float t = 0, spoke = 0, quiet = 0, noise = -60f;
            bool started = false;
            // the pause check: the answer so far, heard while the mic keeps listening (Thinking())
            int checks = 0;
            bool checkedPause = false;   // this pause has been heard (once per pause; speaking again starts a new one)
            float wait = endSilence;
            Awaitable<string> check = null;
            string checkedText = null;
            while (t < maxAnswer)
            {
                yield return null;
                t += Time.deltaTime;
                int now = Audio.SharedMic.Position, len = clip.samples, n = (now - pos + len) % len;
                float peakDb = -100f;
                while (n > 0)
                {
                    int take = Math.Min(n, len - pos);
                    if (buf.Length != take) buf = new float[take];
                    clip.GetData(buf, pos);
                    samples.AddRange(buf);
                    double sum = 0;
                    foreach (var v in buf) sum += v * v;
                    peakDb = Mathf.Max(peakDb, 10f * Mathf.Log10((float)(sum / Math.Max(1, take)) + 1e-10f));
                    pos = (pos + take) % len;
                    n -= take;
                }
                bool heardAny = peakDb > -99f;
                if (heardAny && t < 0.3f) noise = Mathf.Max(noise, peakDb);   // the room, before they start
                bool voiced = heardAny && t >= 0.3f && peakDb > Mathf.Max(noise + 12f, -45f);
                if (voiced) { spoke += Time.deltaTime; quiet = 0; if (spoke > 0.3f) started = true; }
                else quiet += Time.deltaTime;   // (a frame with no new samples, or silence, counts as quiet)
                if (prompt) prompt.text = !started ? "(answer out loud)" : voiced ? "(listening...)"
                                          : wait > endSilence ? "(take your time)" : "(listening)";
                if (!started && t >= giveUp) break;
                if (!started) continue;
                if (voiced) { check = null; checkedText = null; checkedPause = false; wait = endSilence; }   // they went on
                if (quiet < endSilence) continue;
                // The pause: hear what's been said, once per pause, while still listening.
                if (check == null && !checkedPause && battle && thinkingWait > 0 && checks < maxThinkingWaits)
                {
                    checkedPause = true;
                    check = battle.Transcribe(Wav(samples, rate));
                }
                if (check != null && check.GetAwaiter().IsCompleted)
                {
                    try { checkedText = check.GetAwaiter().GetResult(); } catch { checkedText = null; }
                    check = null;
                    if (Thinking(checkedText, spoke)) { checks++; wait = endSilence + thinkingWait; checkedText = null; }
                    else if (quiet >= wait) break;
                }
                if (check == null && quiet >= wait) break;
                if (check != null && quiet >= endSilence + 3f) { check = null; break; }   // the server's slow: as before
            }
            Audio.SharedMic.Release();
            if (prompt) prompt.text = "";
            State = "";
            // what the last check heard is the answer, if nothing was said after it
            done(started ? Wav(samples, rate) : null, started ? checkedText : null);
        }

        static readonly HashSet<string> Fillers = new(("um umm ummm uh uhh uhm er erm ah ahh hmm hm mm mmm eh oh " +
                                                       "well so like okay ok").Split(' '));
        static readonly HashSet<string> TrailsOff = new(("and but so or because cause like um umm uh uhh er erm hmm " +
                                                         "well the a an my i i'm").Split(' '));
        static readonly string[] TrailingPhrases = { "i mean", "you know", "let me think", "let's see", "i guess", "kind of", "sort of" };

        /// <summary>The answer so far sounds unfinished: only fillers, or ending on one ("so...", "and..."); or nothing
        /// heard from a short sound (transcribers often drop a lone "umm"). Null (the server didn't answer): finished.</summary>
        static bool Thinking(string text, float spoke)
        {
            if (text == null) return false;
            var words = new string(text.ToLowerInvariant().Select(c => char.IsLetter(c) || c == '\'' || c == ' ' ? c : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return spoke < 1.5f;   // a long answer heard as nothing: the transcriber, not a pause
            if (words.All(Fillers.Contains)) return true;
            string tail = string.Join(" ", words.Skip(Math.Max(0, words.Length - 3)));
            return TrailsOff.Contains(words[^1]) || TrailingPhrases.Any(p => tail.EndsWith(p));
        }

        static byte[] Wav(List<float> x, int rate)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int bytes = x.Count * 2;
            w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(bytes);
            foreach (var v in x) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32768, 32767));
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>The lines from Characters/Interviewer/Lines/lines.json (ids in order, and their text), for the
        /// editor tool; clips are matched by id.</summary>
        public static (string[] intro, string[] questions, string[] affirmations, string[] outro, Dictionary<string, string> text) ReadScript(string json)
        {
            var o = JObject.Parse(json);
            string[] Ids(string k) => o[k]?.ToObject<string[]>() ?? new string[0];
            return (Ids("intro"), Ids("questions"), Ids("affirmations"), Ids("outro"),
                    o["text"]?.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>());
        }
    }
}
