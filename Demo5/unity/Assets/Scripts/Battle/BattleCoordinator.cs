// Runs a battle: the Unity port of backend/web/app.js.
//
// The game runs here. It plays the beat (a song from /songs, or a metronome), streams the mic over /ws/battle on
// the same clock, decides the turns, applies the game rules (BattleRules) to the server's ratings, voices the
// opponent (ad-libs while it writes, then its verse one line per bar), and raises events for everything that
// happens. The server only transcribes, rates and writes the opponent's verse. Scene objects (crowd, opponent
// avatar, effects, text) subscribe to the events or read the properties; nothing here touches them.
//
// Clock: every time below is AudioSettings.dspTime when the sound is *heard* (scheduled time + output
// latency), which is also the time the mic picks it up. The mic timeline (what the server calls t) is
// mapped onto it with micT0: dsp = micT0 + t.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Events;
using UnityEngine.Networking;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Freestyle.Battle
{
    public enum BattlePhase { Idle, Starting, UserTurn, Judging, OpponentTurn, Finished }

    public enum OpponentVoice
    {
        // The server's own MC voice, whichever engine it runs (Qwen, IndexTTS, Breeze), following the LLM's delivery
        // directions. Falls back to ServerSmooth when the server has no generator or it is still loading.
        [InspectorName("Server generator")] ServerGenerator,
        ServerSmooth,   // espeak-ng, whole line fitted to the bar
        ServerGrid,     // espeak-ng, each word placed on the 16th grid
        TextOnly,       // no audio: the events still fire (subtitles, lip flaps)
    }

    [Serializable] public class StringEvent : UnityEvent<string> { }
    [Serializable] public class FloatEvent : UnityEvent<float> { }
    [Serializable] public class IntEvent : UnityEvent<int> { }

    public class BattleCoordinator : MonoBehaviour
    {
        [Header("Server")]
        [Tooltip("Base URL of the Freestyle server, e.g. http://192.168.1.20:8000/")]
        public string serverUrl = "http://localhost:8000/";

        [Header("Battle")]
        [Tooltip("Opponent id from GET /mcs")]
        public string mcId = "mc_static";
        [Tooltip("Backing beat: song id from GET /songs. Empty = the first song.")]
        public string songId = "";
        [Tooltip("Off: a metronome at metronomeBpm instead of a song.")]
        public bool useSong = true;
        public float metronomeBpm = 90;
        public int rounds = 2;
        [Tooltip("Turn length with the metronome. With a song, each turn is one of its phrases.")]
        public int barsPerTurn = 8;
        public int countInBars = 2;
        [Tooltip("The tug of war's dead zone: a battle that ends with the lead this small a share of all the points (either " +
                 "way) is a draw. The scoreboard shows it as the band in the middle.")]
        [Range(0f, 0.25f)] public float drawMargin = 0.06f;
        [Tooltip("Rhyme hints, word drops and a kinder crowd.")]
        public bool assist = false;
        [Tooltip("What the player is wearing: its accessories' descriptions go to the server, and the opponent may clown them.")]
        public Wardrobe.Outfit outfit;

        [Header("Opponent voice")]
        public OpponentVoice voice = OpponentVoice.ServerGenerator;
        [Tooltip("Ad-libs on the beat while the opponent writes its comeback.")]
        public bool adlibs = true;
        [Tooltip("Where Oculus Lipsync runs (the Quest), the opponent's mouth follows its audio on the device and the server " +
                 "makes no mouth cues (Rhubarb, slow on the cloud server's CPU). Off: always ask for the server's cues.")]
        public bool lipSyncOnDevice = true;
        [Tooltip("Like a mic'd MC in a real room, the opponent's voice comes out of the PA with the beat (Beat Anchor, as " +
                 "spatial as the beat) and, quieter, straight from its mouth (Opponent Voice Anchor). Off: the mouth only.")]
        public bool voiceThroughPA = true;
        [Tooltip("Where the opponent's own voice comes from (its avatar's head). Defaults to this object.")]
        public Transform opponentVoiceAnchor;
        [Tooltip("The voice from the opponent's mouth: how spatial, and how loud next to the PA (with the PA off, it's at " +
                 "Voice Volume).")]
        [Range(0, 1)] public float voiceSpatialBlend = 1f;
        [Range(0, 1)] public float directVoice = 0.5f;
        [Range(0, 1)] public float voiceVolume = 1f;
        public AudioMixerGroup voiceMixerGroup;

        [Header("Beat")]
        [Tooltip("Where the beat plays from (speakers). Defaults to this object.")]
        public Transform beatAnchor;
        [Range(0, 1)] public float beatSpatialBlend = 0f;
        [Range(0, 1)] public float beatVolume = 0.8f;
        public AudioMixerGroup beatMixerGroup;
        [Tooltip("Songs in the project. Set their Load Type to Decompress On Load or Compressed In Memory (not Streaming). " +
                 "Their timing comes from Local Song Info, or from the server's /songs (matched by file name).")]
        public List<AudioClip> localSongs = new();
        [Tooltip("Timing for Local Songs: the JSON backend/tools/export_songs.py writes (the same format as /songs). " +
                 "With it, these songs need no server; the server's songs are added when it's reachable.")]
        public TextAsset localSongInfo;

        [Header("Levels")]
        [Tooltip("Bring every opponent line and ad-lib to the same loudness, and set the beat under it (each song measured " +
                 "once): the voices and the songs come from different places and vary by 10 dB or more.")]
        public bool levelVoiceAndBeat = true;
        [Tooltip("The loudness the opponent's voice is brought to (RMS of its speech, dBFS).")]
        public float voiceLevelDb = -16f;
        [Tooltip("How far the voice sits above the beat, dB (Beat Volume stays the beat's ceiling).")]
        public float voiceOverBeatDb = 3f;

        [Header("Mic")]
        [Tooltip("Empty = the default mic.")]
        public string micDevice = "";
        public int micSampleRate = 16000;
        [Tooltip("Instead of the mic: this clip is played and fed to the server at the start of each of your turns, which " +
                 "still run their whole phrase (like the web GUI's ?clip=). Load Type must be Decompress On Load.")]
        public AudioClip testClip;

        [Header("Latency")]
        [Tooltip("Added to the audio buffer latency (e.g. Bluetooth headphones). Raise it if the server hears you late.")]
        public float extraOutputLatencyMs = 0;
        [Tooltip("Time from the voice reaching the mic to the samples showing up in Unity.")]
        public float inputLatencyMs = 0;

        [Tooltip("When the battle's over the beat plays on (and the beat clock keeps going) until FadeOutBeat. Off: it stops " +
                 "with the battle.")]
        public bool keepBeatAfterBattle = true;

        [Header("Startup")]
        public bool loadLobbyOnStart = true;
        public bool startBattleOnStart = false;

        [Header("Inspector hooks (the C# events below carry more detail)")]
        public UnityEvent onUserTurnLive;        // the count-in is over: rap now
        public UnityEvent onOpponentTurn;
        public UnityEvent onPunchline;
        public UnityEvent onFellOff;
        public FloatEvent onCrowdLevel;          // 0-11, on every state update
        public StringEvent onCrowdMood;          // when it changes
        public StringEvent onOpponentGesture;    // after each rated chunk
        public StringEvent onOpponentLine;       // the verse line now being rapped
        public StringEvent onAdlib;
        public IntEvent onBeat;                  // beat in bar, 0 = downbeat
        public StringEvent onResult;             // user / opponent / draw

        // ---------- events ----------
        public event Action<string> StatusChanged;
        public event Action LobbyLoaded;                       // Songs, Mcs, Health are filled in
        public event Action<BattlePhase> PhaseChanged;
        public event Action<ReadyMsg> Ready;
        public event Action<BattleState> StateChanged;         // crowd, opponent mood, score: several times a second
        public event Action<string> CrowdMoodChanged;
        public event Action<string> OpponentMoodChanged;
        public event Action<TurnMsg> TurnStarted;              // user turns: the count-in starts now, the rap at UserTurnStart
        public event Action<int> UserTurnLive;                 // round; the user's bars begin
        public event Action<TranscriptMsg> TranscriptChanged;  // read CommittedText / PartialText
        public event Action<MetricsMsg> MetricsUpdated;
        public event Action<ReactionMsg> Reaction;             // one rated chunk (user or opponent)
        public event Action<ReactionMsg> Punchline;
        public event Action<ReactionMsg> FellOff;
        public event Action<string> OpponentGesture;           // laughs, shrugs, nods, winces, glares, steps_back
        public event Action<string> SoreSpotHit;
        public event Action<WordDropsMsg> WordDropsReceived;
        public event Action<string> WordDropUsed;
        public event Action<HintsMsg> HintsReceived;
        public event Action<TurnEndMsg> UserTurnEnded;
        public event Action<OpponentVerseMsg> OpponentVerseReceived;   // lyrics known, audio may still be coming
        public event Action<double> OpponentVerseScheduled;    // dsp time the first line will be heard
        public event Action<int, string> OpponentLineStarted;  // index, line
        public event Action OpponentVerseFinished;
        public event Action<string> OpponentAdlib;
        public event Action<OpponentVoiceClip> OpponentVoiceScheduled;   // each line or ad-lib clip, with its source and mouth cues
        public event Action<int, int> Beat;                    // beat in bar, bar index (from the first beat)
        public event Action<ResultMsg> BattleFinished;
        public event Action<string> ServerError;

        // ---------- read-only state ----------
        /// <summary>The server's password (FREESTYLE_PASSWORD), typed in by the player each session. Deliberately a
        /// property, not a field: it is never serialized into a scene or prefab, and nothing here saves it. It lives in
        /// memory for the app's run, so a scene reload (GameReset: Delete Save) keeps it; quitting forgets it.</summary>
        public string Password { get => sessionPassword; set => sessionPassword = value ?? ""; }
        static string sessionPassword = "";
        /// <summary>A server answered earlier in this run (before a scene reload): the new scene reconnects to it.</summary>
        static string connectedServer;
        /// <summary>The server asked for a password and didn't get the right one (or this address is locked out
        /// after too many wrong ones).</summary>
        public bool PasswordRejected { get; private set; }

        public BattlePhase Phase { get; private set; } = BattlePhase.Idle;
        /// <summary>The server sends mouth cues with the opponent's lines (ready.mouth, and asked for). Off: lip-sync from
        /// the audio.</summary>
        public bool ServerMouthCues { get; private set; }
        bool WantMouthCues => !(lipSyncOnDevice && Avatar.VisemeTap.Available);
        public string Status { get; private set; } = "";
        public List<SongInfo> Songs { get; private set; } = new();
        public List<McInfo> Mcs { get; private set; } = new();
        public HealthInfo Health { get; private set; }
        /// <summary>Display name of the server's voice generator ("Qwen", "IndexTTS", "Breeze"), or null when it has none.</summary>
        public string VoiceEngineName => Health == null || Health.voice == "client" ? null
            : Health.voice_engine switch { "qwen" => "Qwen", "indextts" => "IndexTTS", "breeze" => "Breeze", null or "" => "server", var e => e };
        public McInfo Opponent => Mcs.FirstOrDefault(m => m.id == mcId);
        public string OpponentName => CurrentState?.mc?.name ?? Opponent?.name ?? "Opponent";
        public BattleState CurrentState { get; private set; }
        public ResultMsg Result { get; private set; }
        public MetricsMsg LastMetrics { get; private set; }
        /// <summary>The crowd, 0-11, live every frame (CurrentState is a snapshot, refreshed ~10 times a second).</summary>
        public float CrowdLevel => rules?.Level ?? CurrentState?.crowd?.level ?? 0;
        /// <summary>The game rules for this battle (null before the server is ready).</summary>
        public BattleRules Rules => rules;

        public float Bpm { get; private set; } = 90;
        public int BeatsPerBar { get; private set; } = 4;
        public double BeatSeconds => 60.0 / Bpm;
        public double BarSeconds => BeatsPerBar * BeatSeconds;
        /// <summary>Smoothed dspTime, for visuals (dspTime itself moves in audio-buffer steps).</summary>
        public double Now { get; private set; }
        /// <summary>Beats since the first beat (negative before it), and the position inside the beat (0-1).</summary>
        public int BeatIndex { get; private set; } = -1;
        public float BeatPhase { get; private set; }
        public int BeatInBar => ((BeatIndex % BeatsPerBar) + BeatsPerBar) % BeatsPerBar;

        public int Round { get; private set; }
        public double UserTurnStart { get; private set; }     // dsp time the user's bars begin
        public double UserTurnLength { get; private set; }
        public int UserTurnBars { get; private set; }
        public bool UserTurnIsLive => Phase == BattlePhase.UserTurn && Now >= UserTurnStart;
        /// <summary>Count-in beats left before the user's turn (0 once it is live).</summary>
        public int CountInBeatsLeft => Phase == BattlePhase.UserTurn && Now < UserTurnStart ? (int)Math.Ceiling((UserTurnStart - Now) / BeatSeconds) : 0;
        /// <summary>1-based bar of the current turn (user, or the opponent's verse), 0 when none.</summary>
        public int CurrentBar { get; private set; }
        public int TurnBars { get; private set; }
        public float TurnProgress { get; private set; }       // 0-1

        public IReadOnlyList<string> CommittedWords => committed;
        public string CommittedText => string.Join(" ", committed);
        public string PartialText { get; private set; } = "";
        public IReadOnlyList<string> WordDrops => wordDrops;
        public IReadOnlyCollection<string> WordDropsUsed => wordDropsUsed;
        public HintsMsg Hints { get; private set; }

        /// <summary>Seconds from AudioSource.PlayScheduled's time to the sound being heard (every time here is a heard
        /// time: schedule at heard - this).</summary>
        public double HeardLatency => outLat > 0 ? outLat : OutputLatency();

        public IReadOnlyList<string> VerseLines => verse?.lines ?? (IReadOnlyList<string>)Array.Empty<string>();
        public int VerseLine => verse?.current ?? -1;          // line being rapped, -1 before the verse
        public bool VerseWaiting => Phase == BattlePhase.OpponentTurn && (verse == null || double.IsNaN(verse.start) || Now < verse.start);
        /// <summary>While the MC gets ready (VerseWaiting): when its verse will start (dsp time): the bar line it's been
        /// scheduled on, else the slot it was written for. NaN when there's no estimate (that slot has passed, still
        /// waiting on lines).</summary>
        public double OpponentStartsAt
        {
            get
            {
                if (!VerseWaiting) return double.NaN;
                if (verse != null && !double.IsNaN(verse.start)) return verse.start;
                return opp != null && opp.plan.bars > 0 && opp.plan.start > Now ? opp.plan.start : double.NaN;
            }
        }
        public string Interpretation { get; private set; } = "";
        public string LastAdlib { get; private set; } = "";
        public float MicLevel { get; private set; }            // 0-1 peak

        // ---------- internals ----------
        // Nulls in the JSON (e.g. a metric with nothing to measure yet) leave the field at its default.
        static readonly JsonSerializer Json = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        static readonly string[] Openers = { "my turn", "aight, my turn", "okay, okay, my turn" };
        static readonly string[] Adlibs = { "uh huh", "yeah, yeah", "hold up", "okay", "check it", "you done?", "aight, aight",
                                            "mm hmm", "let's go", "watch this", "uh", "yeah" };

        int gen;                                 // bumped per battle; async work from an old battle drops its result
        BattleSocket socket;
        double outLat;                           // seconds from PlayScheduled time to heard
        double beat0;                            // dsp time the first beat is heard
        double beatStartDsp;                     // dsp time the first beat is scheduled
        double micT0 = double.NaN;               // dsp time of mic timeline 0
        bool generatorLive;                      // this battle: the server renders the opponent's audio
        BattleRules rules;
        bool userTurnOpen;                       // the user's bars are on (until EndTurn)
        bool listenDone;                         // the server finished the user's turn
        string lastUserVerse = "";
        float lastSilence;                       // trailing silence in the latest metrics (dead air)
        float stateTimer;
        OppTurn opp;                             // the opponent's turn: its ratings wait for their lines to play
        Dictionary<string, string> adlibUrls = new();

        readonly List<string> committed = new();
        readonly List<string> wordDrops = new();
        readonly HashSet<string> wordDropsUsed = new();
        string lastCrowdMood, lastOppMood;
        bool userLiveFired;
        int lastBeatFired = int.MinValue;

        readonly List<(double t, Action fn)> timeline = new();
        Coroutine beatFade;
        float beatLevel = 1f;                                        // the beat's share of beatVolume after levelling
        float BeatGain => beatVolume * beatLevel;
        readonly HashSet<AudioClip> levelled = new();
        readonly Dictionary<AudioClip, float> songLoudness = new();
        readonly List<AudioClip> battleClips = new();          // downloaded this battle; destroyed on stop
        readonly Dictionary<string, AudioClip> songClips = new();
        readonly Dictionary<string, AudioClip> adlibCache = new();
        readonly List<Voice> voicePool = new();
        double lastDsp, lastDspReal;

        // beat
        AudioSource[] beatSources;
        SongInfo song;
        AudioClip songClip;
        double songFrom;                         // where in the file the song starts (heard at beat0)
        double nextSegment;
        int segment;
        AudioClip clickHi, clickLo;
        int metroBeat;

        // mic
        AudioClip micClip;
        string micName;
        int micRate, micPos;
        bool streaming;
        byte[] chunk;
        int chunkN, chunkSamples;
        float chunkPeak;
        float[] readBuf = Array.Empty<float>();
        // test clip in place of the mic
        float[] testSamples;
        long virtualSent;
        double testClipAt = double.NaN;          // mic time the clip starts

        Verse verse;
        Filler filler;

        /// <summary>A voice playing: through the PA and from the mouth, the same clip at the same time. `src` is the one
        /// the mouth follows (the PA's, or the mouth's with the PA off).</summary>
        class Voice
        {
            public AudioSource pa, mouth;
            public AudioSource src;
            public double busyUntil;
            public IEnumerable<AudioSource> Both { get { yield return pa; yield return mouth; } }   // (either may be destroyed: check)
        }

        class OppTurn
        {
            public readonly List<RatingMsg> ratings = new();
            public readonly HashSet<RatingMsg> applied = new();
            public int lines = -1;
            public bool finished;
            public Slot plan;           // the slot its verse was written for
            public double asked;        // dsp time the verse was asked for
            public bool measured;
            public bool received;       // its verse came (opponent_verse)
        }

        /// <summary>When a turn starts (dsp time, heard) and how many bars it runs.</summary>
        struct Slot { public double start; public int bars; }

        double prepS = 4;               // seconds from asking for the opponent's verse to it being ready (learned)
        const double VerseTimeoutS = 30; // no verse by then: the server's gone quiet (RunBattle)
        const int LineTimeoutS = 8;      // a verse line's clip: past this it's skipped (FetchClip)

        class Verse
        {
            public List<string> lines;
            public double barS, start = double.NaN, sentAt;
            public bool stream;
            public AudioClip[] clips;
            public List<(float t, string shape)>[] mouth;   // each line's mouth cues from the server, or null
            public bool[] arrived;
            public double[] arrivedAt;
            public int current = -1;
        }

        class Filler
        {
            public List<string> openers, adlibs;
            public bool generated;
            public int n;
            public bool first = true, done;
            public double busyUntil, until = double.PositiveInfinity;
            public readonly List<(Voice v, AudioClip clip)> playing = new();
        }

        // ================= lifecycle =================

        void Awake()
        {
            if (!opponentVoiceAnchor) opponentVoiceAnchor = transform;
            if (!beatAnchor) beatAnchor = transform;
            LoadLocalSongs();
            Songs = MergeSongs(null);
        }

        async void Start()
        {
            bool reconnect = !string.IsNullOrEmpty(connectedServer) && connectedServer == serverUrl;
            if (loadLobbyOnStart || startBattleOnStart || reconnect) await LoadLobby();
            if (startBattleOnStart) StartBattle();
        }

        void OnDestroy() => StopBattle(null);

        void OnApplicationPause(bool paused)
        {
            if (paused && Phase != BattlePhase.Idle && Phase != BattlePhase.Finished) StopBattle("Paused.");
        }

        // ================= public API =================

        /// <summary>Fetches the songs, the opponents and the server's health. Safe to call again to refresh.</summary>
        public async Awaitable LoadLobby()
        {
            SetStatus("Contacting server…");
            PasswordRejected = false;
            var songs = await GetJson<List<SongInfo>>("songs");
            var mcs = await GetJson<List<McInfo>>("mcs");
            var health = await GetJson<HealthInfo>("health");
            Songs = MergeSongs(songs);   // local songs first, then the server's (when it answered)
            if (mcs != null) Mcs = mcs;
            Health = health;
            connectedServer = health != null && !PasswordRejected ? serverUrl : null;
            SetStatus(PasswordRejected ? (string.IsNullOrEmpty(Password) ? "This server needs a password" : "Wrong password")
                      : health == null ? $"Server not reachable at {serverUrl}"
                      : $"Server up · voice: {health.voice} · rater: {health.rater}");
            Raise(LobbyLoaded);
        }

        public async void StartBattle()
        {
            try { await StartBattleAsync(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                StopBattle("Couldn't start: " + e.Message);
            }
        }

        /// <summary>Ends the user's turn early (otherwise it ends after barsPerTurn).</summary>
        public void EndTurn()
        {
            if (Phase != BattlePhase.UserTurn || !userTurnOpen) return;
            userTurnOpen = false;
            Send(new { type = "listen_end" });
            if (rules != null) rules.Turn = "judging";
            SetPhase(BattlePhase.Judging);
        }

        public void StopBattle() => StopBattle("Stopped.");

        class RhymesMsg { public string word; public List<string> rhymes = new(); }
        readonly Dictionary<string, List<string>> rhymeCache = new();

        /// <summary>Common words that rhyme with `word`, best first (GET /rhymes, the assist's rhymes; cached). Empty when
        /// the server can't be reached or has none.</summary>
        public async Awaitable<List<string>> Rhymes(string word)
        {
            word = (word ?? "").Trim().ToLowerInvariant();
            if (word.Length == 0) return new List<string>();
            if (rhymeCache.TryGetValue(word, out var known)) return known;
            bool expl = Opponent?.@explicit ?? false;
            var msg = await GetJson<RhymesMsg>($"rhymes?word={Uri.EscapeDataString(word)}&n=8&explicit={(expl ? "true" : "false")}");
            var list = msg?.rhymes ?? new List<string>();
            if (msg != null) rhymeCache[word] = list;   // (not when it failed: try again next time)
            return list;
        }

        public class TipMsg { public string mc, name, spot; public int left, total; }

        /// <summary>One of the MC's sore spots the player hasn't been told (GET /mcs/{id}/tip): never one in `known`, and
        /// one in `avoid` (found in the battle) only once the rest are told. Null when the server can't be reached; its
        /// `spot` is null when every one is known.</summary>
        public async Awaitable<TipMsg> Tip(string mc, IEnumerable<string> known, IEnumerable<string> avoid = null)
        {
            var q = (known ?? Enumerable.Empty<string>()).Select(k => "known=" + Uri.EscapeDataString(k))
                .Concat((avoid ?? Enumerable.Empty<string>()).Select(k => "avoid=" + Uri.EscapeDataString(k)));
            return await GetJson<TipMsg>($"mcs/{Uri.EscapeDataString(mc)}/tip?{string.Join("&", q)}");
        }

        class TextMsg { public string text; }
        public class InterviewSummary { public string name = "", summary = "", source = ""; }

        /// <summary>One spoken interview answer (a 16-bit WAV) heard by the server (POST /interview/transcribe): what
        /// they said, or null when the server can't be reached.</summary>
        public async Awaitable<string> Transcribe(byte[] wav)
        {
            var msg = await PostJson<TextMsg>("interview/transcribe", wav, "audio/wav", 30);
            return msg?.text;
        }

        /// <summary>The interview's answers summed up for the opponents (POST /interview/summary), or null when the
        /// server can't be reached.</summary>
        public async Awaitable<InterviewSummary> Summarize(List<Game.InterviewAnswer> answers)
        {
            var body = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { answers }));
            return await PostJson<InterviewSummary>("interview/summary", body, "application/json", 30);
        }

        async Awaitable<T> PostJson<T>(string path, byte[] body, string contentType, int timeout) where T : class
        {
            using var req = new UnityWebRequest(Url(path), "POST")
            {
                uploadHandler = new UploadHandlerRaw(body) { contentType = contentType },
                downloadHandler = new DownloadHandlerBuffer(), timeout = timeout,
            };
            Authorize(req);
            await req.SendWebRequest();
            if (req.responseCode == 401) PasswordRejected = true;
            if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning($"[Battle] /{path}: {req.error}"); return null; }
            try { return JToken.Parse(req.downloadHandler.text).ToObject<T>(Json); }
            catch (Exception e) { Debug.LogWarning($"[Battle] bad /{path}: {e.Message}"); return null; }
        }

        /// <summary>The beat is playing (during a battle, and after one until FadeOutBeat when keepBeatAfterBattle).</summary>
        public bool BeatPlaying { get; private set; }

        /// <summary>Fades the beat out over `seconds` and stops it (after a battle: once the player has left the stage).</summary>
        public void FadeOutBeat(float seconds = 2f)
        {
            if (!BeatPlaying) return;
            if (beatFade != null) StopCoroutine(beatFade);
            beatFade = StartCoroutine(FadeBeat(seconds));
        }

        System.Collections.IEnumerator FadeBeat(float seconds)
        {
            for (float t = 0; t < seconds && beatSources != null; t += Time.unscaledDeltaTime)
            {
                foreach (var s in beatSources) if (s) s.volume = BeatGain * (1f - t / seconds);
                yield return null;
            }
            StopBeat();
            beatFade = null;
        }

        void StopBeat()
        {
            BeatPlaying = false;
            if (beatSources != null) foreach (var s in beatSources) if (s) { s.Stop(); s.volume = BeatGain; }
        }

        public void SetBeatVolume(float v)
        {
            beatVolume = v;
            if (beatSources != null) foreach (var s in beatSources) s.volume = BeatGain;
        }

        /// <summary>The song that will be (or is being) played, or null for the metronome.</summary>
        public SongInfo SelectedSong => !useSong || Songs.Count == 0 ? null
            : Songs.FirstOrDefault(s => s.id == songId) ?? (string.IsNullOrEmpty(songId) ? Songs[0] : null);

        // ================= start / stop =================

        async Awaitable StartBattleAsync()
        {
            if (Phase != BattlePhase.Idle && Phase != BattlePhase.Finished) return;
            int g = ++gen;
            ResetBattleState();
            SetPhase(BattlePhase.Starting);
            if (Songs.Count == 0 || Mcs.Count == 0 || PasswordRejected)
            {
                await LoadLobby();
                if (g != gen) return;
                if (PasswordRejected) { StopBattle(Status); return; }
            }

            // Mic first: on Quest this may show the permission prompt.
            if (testClip == null && !await StartMic())
            {
                if (g == gen) StopBattle("Mic unavailable" + (Microphone.devices.Length == 0 ? ": no microphone found" : ""));
                return;
            }
            if (g != gen) return;
            if (testClip != null && !LoadTestClip()) { StopBattle($"Couldn't read the test clip {testClip.name} (set Load Type to Decompress On Load)"); return; }

            song = SelectedSong;
            if (song != null)
            {
                SetStatus($"Loading {song.title}…");
                songClip = await LoadSong(song);
                if (g != gen) return;
                if (songClip == null) { StopBattle($"Couldn't load the beat {song.file}"); return; }
                Bpm = song.bpm;
                BeatsPerBar = song.beats_per_bar > 0 ? song.beats_per_bar : 4;
            }
            else
            {
                Bpm = metronomeBpm;
                BeatsPerBar = 4;
            }

            outLat = OutputLatency();
            beatStartDsp = AudioSettings.dspTime + 0.3;
            beat0 = beatStartDsp + outLat;   // when the first beat is heard
            StartBeat();

            SetStatus("Connecting…");
            socket = new BattleSocket();
            try
            {
                await socket.ConnectAsync(WsUri(), Password);
            }
            catch (Exception e)
            {
                // A refused handshake (403) is the password: the server checks it before accepting the socket.
                if (e.Message.Contains("403")) PasswordRejected = true;
                if (g == gen) StopBattle(PasswordRejected ? "Wrong password" : $"Couldn't connect to {WsUri()}: {e.Message}");
                return;
            }
            if (g != gen) return;

            // Mic timeline 0 is the next sample captured; the start message says where the beat sits on it.
            if (testClip != null)
            {
                micRate = 16000;
                micT0 = AudioSettings.dspTime;
                virtualSent = 0;
            }
            else
            {
                micPos = Microphone.GetPosition(micName);
                micT0 = AudioSettings.dspTime - inputLatencyMs / 1000.0;
            }
            chunkSamples = Math.Max(1, micRate / 50);   // 20 ms
            chunk = new byte[chunkSamples * 2];
            chunkN = 0;
            Send(new
            {
                type = "start", sample_rate = micRate, mc = mcId, assist,
                mouth = WantMouthCues,   // false: no Rhubarb on the server, the device lip-syncs
                outfit = (outfit ? outfit : FindFirstObjectByType<Wardrobe.Outfit>()) is { } o ? o.Descriptions() : new List<string>(),
                profile = Game.SaveFile.Profile,   // the pre-show interview, summed up: the MCs use it
                beat = new { bpm = Bpm, offset_ms = (beat0 - micT0) * 1000.0, beats_per_bar = BeatsPerBar },
            });
            streaming = true;
            SetStatus("Waiting for the server…");
            RunBattle(g);
        }

        // ================= the battle: rounds of the user's turn, then the opponent's =================

        async void RunBattle(int g)
        {
            try
            {
                if (!await Until(() => rules != null, g)) return;   // the server is ready
                double bar = BarSeconds;
                for (int r = 1; r <= rounds; r++)
                {
                    // The user's turn is the song's next phrase: right after the count-in in round one, else the next
                    // one, which can be the one starting as the opponent's verse ends (the user is ready the moment it is).
                    double now = AudioSettings.dspTime;
                    BeginUserTurn(r, NextSlot(r == 1 ? Math.Max(beat0 + countInBars * bar, now + 0.5) : now - 0.15, barsPerTurn));
                    if (!await Until(() => !userTurnOpen && listenDone, g)) return;
                    if (Debug.isDebugBuild)   // (the headset's log: did the server hear the turn?)
                        Debug.Log($"[Battle] round {r}: heard {(lastUserVerse ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words: \"{lastUserVerse}\"" +
                                  (wordDrops.Count > 0 ? $" | word drops: {string.Join(", ", wordDrops.Select(d => wordDropsUsed.Contains(d) ? d + " (used)" : d))}" : ""));
                    Raise(UserTurnEnded, new TurnEndMsg { round = r, verse = lastUserVerse, state = PublishState() });

                    BeginOpponentTurn(r);
                    var o = opp;
                    // A server that never answers (hung, or its instance restarting) mustn't leave the MC ad-libbing
                    // forever: the battle stops, saying why.
                    if (!await Until(() => o.finished || !o.received && AudioSettings.dspTime > o.asked + VerseTimeoutS, g)) return;
                    if (!o.finished)
                    {
                        Debug.LogWarning($"[Battle] no verse from the server in {VerseTimeoutS:F0} s (round {r})");
                        StopBattle("The MC never answered: the server stopped responding. Try again in a minute.");
                        return;
                    }
                    // Its last ratings may still be on the way: wait a little for them.
                    double deadline = Time.realtimeSinceStartupAsDouble + 5;
                    if (!await Until(() => o.applied.Count >= (o.lines + 1) / 2 || Time.realtimeSinceStartupAsDouble > deadline, g)) return;
                }
                rules.Turn = "idle";
                Result = new ResultMsg { winner = rules.Winner, state = PublishState() };
                SetPhase(BattlePhase.Finished);
                StopBattle("Battle over.", keepBeat: keepBeatAfterBattle);   // the beat plays on until FadeOutBeat
                Raise(BattleFinished, Result);
                onResult?.Invoke(Result.winner);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (g == gen) StopBattle("Battle failed: " + e.Message);
            }
        }

        /// <summary>Waits a frame at a time until done; false if the battle was stopped meanwhile.</summary>
        async Awaitable<bool> Until(Func<bool> done, int g)
        {
            while (!done())
            {
                if (g != gen) return false;
                await Awaitable.NextFrameAsync();
            }
            return g == gen;
        }

        void BeginUserTurn(int r, Slot slot)
        {
            Round = rules.Round = r;
            rules.Turn = "user";
            UserTurnStart = slot.start;
            UserTurnBars = slot.bars;
            rules.TurnBars("user", slot.bars, barsPerTurn);
            UserTurnLength = slot.bars * BarSeconds;
            userLiveFired = false;
            userTurnOpen = true;
            listenDone = false;
            lastSilence = 0;
            committed.Clear();
            PartialText = "";
            Interpretation = "";
            verse = null;
            double t = slot.start - micT0;
            Send(rules.Listen(t));
            SetPhase(BattlePhase.UserTurn);
            if (testClip != null) PlayTestClip(t);
            Raise(TurnStarted, new TurnMsg { turn = "user", round = r, t = t, bars = slot.bars, length_s = UserTurnLength, state = PublishState() });
        }

        void BeginOpponentTurn(int r)
        {
            rules.Turn = "opponent";
            wordDrops.Clear();
            Hints = null;
            LastAdlib = "";
            // Its verse fills the slot it will be ready for, so it is asked for that many lines.
            opp = new OppTurn { asked = AudioSettings.dspTime, plan = NextSlot(AudioSettings.dspTime + prepS, barsPerTurn, true) };
            SetPhase(BattlePhase.OpponentTurn);
            if (adlibs) StartFiller();
            rules.TurnBars("opponent", opp.plan.bars, barsPerTurn);
            Send(rules.VerseRequest(opp.plan.bars));
            Raise(TurnStarted, new TurnMsg { turn = "opponent", round = r, bars = opp.plan.bars, state = PublishState() });
            onOpponentTurn?.Invoke();
        }

        /// <summary>A rating through the rules, then the events.</summary>
        void ApplyRating(RatingMsg m)
        {
            float points = rules.ApplyRating(m, BarSeconds);
            OnReaction(new ReactionMsg
            {
                performer = m.performer, t = m.t, chunk = m.chunk, crowd = m.crowd, opponent = m.opponent, points = points,
                word_drops = m.word_drops ?? new List<string>(), state = PublishState(),
            });
        }

        /// <summary>Opponent ratings land when their lines have played (or right away, if they arrive late).</summary>
        void ApplyOpponentRatings()
        {
            var o = opp;
            if (o == null || rules == null) return;
            int played = o.finished ? int.MaxValue
                : verse != null && !double.IsNaN(verse.start) ? (int)Math.Floor((AudioSettings.dspTime - verse.start) / verse.barS + 1e-3) : 0;
            foreach (var m in o.ratings)
            {
                if (o.applied.Contains(m) || (m.chunk.lines?[1] ?? 0) > played) continue;
                o.applied.Add(m);
                ApplyRating(m);
            }
        }

        /// <summary>The rules' state as a snapshot: CurrentState, StateChanged and the mood events.</summary>
        BattleState PublishState()
        {
            if (rules != null) ApplyState(rules.State());
            return CurrentState;
        }

        void StopBattle(string msg, bool keepBeat = false)
        {
            // why a battle stopped short (the HUD shows it too; the log has who stopped it)
            if (Phase is not (BattlePhase.Idle or BattlePhase.Finished) && msg != "Battle over.")
                Debug.LogWarning($"[Battle] stopped ({Phase}): {msg ?? "(no reason)"}");
            gen++;
            if (socket != null)
            {
                Send(new { type = "stop" });
                socket.Dispose();
                socket = null;
            }
            streaming = false;
            if (micClip != null)
            {
                Audio.SharedMic.Release();
                micClip = null;
            }
            if (!keepBeat)
            {
                if (beatFade != null) { StopCoroutine(beatFade); beatFade = null; }
                StopBeat();
            }
            // (on the way out of a scene the voices can be gone already: they live under other objects)
            foreach (var v in voicePool) { foreach (var a in v.Both) if (a) { a.Stop(); a.clip = null; } v.busyUntil = 0; }
            timeline.Clear();
            verse = null;
            filler = null;
            foreach (var c in battleClips) if (c) Destroy(c);
            battleClips.Clear();
            levelled.RemoveWhere(c => !c);
            adlibCache.Clear();
            MicLevel = 0;
            CurrentBar = 0;
            TurnProgress = 0;
            if (Phase != BattlePhase.Finished) SetPhase(BattlePhase.Idle);
            if (msg != null) SetStatus(msg);
        }

        void ResetBattleState()
        {
            committed.Clear();
            PartialText = "";
            wordDrops.Clear();
            wordDropsUsed.Clear();
            Hints = null;
            Interpretation = "";
            LastAdlib = "";
            Result = null;
            LastMetrics = null;
            Round = 0;
            micT0 = double.NaN;
            testClipAt = double.NaN;
            lastBeatFired = int.MinValue;
            lastCrowdMood = lastOppMood = null;
            beat0 = beatStartDsp = 0;
            song = null;
            songClip = null;
            generatorLive = false;
            adlibUrls = new();
            rules = null;
            opp = null;
            userTurnOpen = listenDone = false;
            lastUserVerse = "";
            lastSilence = 0;
        }

        // ================= per frame =================

        void Update()
        {
            UpdateClock();
            socket?.Drain(OnText, reason => { if (Phase != BattlePhase.Finished) StopBattle("Disconnected: " + reason); });
            if (streaming) PumpMic();
            TickBeat();
            TickFiller();
            RunTimeline();
            UpdateTurnInfo();
            TickRules();
        }

        /// <summary>The pop fades and dead air drains the room; the user's bars run out; the opponent's ratings land.</summary>
        void TickRules()
        {
            if (rules == null || Phase == BattlePhase.Idle || Phase == BattlePhase.Finished) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            rules.Tick(dt, UserTurnIsLive ? lastSilence : 0);
            if (userTurnOpen && Now >= UserTurnStart + UserTurnLength + 0.5) EndTurn();
            if (Phase == BattlePhase.OpponentTurn) ApplyOpponentRatings();
            if ((stateTimer += dt) >= 0.1f)
            {
                stateTimer = 0;
                PublishState();
            }
        }

        void UpdateClock()
        {
            double d = AudioSettings.dspTime, real = Time.realtimeSinceStartupAsDouble;
            if (d != lastDsp) { lastDsp = d; lastDspReal = real; }
            Now = Math.Max(Now, lastDsp + Math.Min(real - lastDspReal, 0.1));
        }

        void UpdateTurnInfo()
        {
            // the beat clock runs while the beat plays (after the battle too); the turns only during one
            if (beat0 == 0 || !BeatPlaying && (Phase == BattlePhase.Idle || Phase == BattlePhase.Finished)) return;
            double t = (Now - beat0) / BeatSeconds;
            BeatIndex = (int)Math.Floor(t);
            BeatPhase = (float)(t - BeatIndex);
            if (BeatIndex >= 0 && BeatIndex != lastBeatFired)
            {
                lastBeatFired = BeatIndex;
                Raise(Beat, BeatInBar, BeatIndex / BeatsPerBar);
                onBeat?.Invoke(BeatInBar);
            }

            if (Phase == BattlePhase.UserTurn && !double.IsNaN(micT0))
            {
                double rel = Now - UserTurnStart;
                TurnBars = UserTurnBars;
                CurrentBar = rel < 0 ? 0 : Math.Min(UserTurnBars, (int)(rel / BarSeconds) + 1);
                TurnProgress = rel < 0 ? 0 : (float)Math.Min(1, rel / Math.Max(UserTurnLength, 0.001));
                if (rel >= 0 && !userLiveFired)
                {
                    userLiveFired = true;
                    Raise(UserTurnLive, Round);
                    onUserTurnLive?.Invoke();
                }
            }
            else if (Phase == BattlePhase.OpponentTurn && verse != null && !double.IsNaN(verse.start) && Now >= verse.start)
            {
                int n = verse.lines.Count;
                TurnBars = n;
                CurrentBar = Math.Min(n, (int)((Now - verse.start) / verse.barS) + 1);
                TurnProgress = (float)Math.Min(1, (Now - verse.start) / (n * verse.barS));
            }
            else if (Phase == BattlePhase.OpponentTurn)
            {
                CurrentBar = 0;
                TurnProgress = 0;
            }
        }

        void At(double t, Action fn) => timeline.Add((t, fn));

        void RunTimeline()
        {
            double now = AudioSettings.dspTime;
            for (int i = 0; i < timeline.Count;)
            {
                if (timeline[i].t > now) { i++; continue; }
                var fn = timeline[i].fn;
                timeline.RemoveAt(i);
                try { fn(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // ================= server messages =================

        void OnText(string text)
        {
            JObject m;
            try { m = JObject.Parse(text); }
            catch (Exception e) { Debug.LogWarning($"[Battle] bad message: {e.Message}"); return; }
            try { OnMessage((string)m["type"], m); }
            catch (Exception e) { Debug.LogException(e); }
        }

        void OnMessage(string type, JObject m)
        {
            switch (type)
            {
                case "ready":
                {
                    var r = m.ToObject<ReadyMsg>(Json);
                    generatorLive = r.voice == "qwen";   // "qwen" means any server-rendered voice, whatever the engine
                    ServerMouthCues = r.mouth != null && WantMouthCues;
                    adlibUrls = r.adlibs ?? new();
                    rules = new BattleRules(r.mc ?? Opponent, assist);
                    rules.DrawMargin = drawMargin;
                    SetStatus($"rater: {r.rater} · opponent: {r.opponent} · stt: {r.transcriber} · voice: {(generatorLive ? VoiceEngineName ?? "server" : "client")}");
                    PublishState();
                    Raise(Ready, r);
                    break;
                }
                case "word_drops":
                {
                    var d = m.ToObject<WordDropsMsg>(Json);
                    d.bonus = BattleRules.WordDropBonus;
                    wordDrops.Clear();
                    wordDrops.AddRange(d.words);
                    wordDropsUsed.Clear();
                    Raise(WordDropsReceived, d);
                    break;
                }
                case "hints":
                    Hints = m.ToObject<HintsMsg>(Json);
                    Raise(HintsReceived, Hints);
                    break;
                case "transcript":
                {
                    var t = m.ToObject<TranscriptMsg>(Json);
                    committed.AddRange(t.committed.Select(w => w.w));
                    PartialText = t.partial ?? "";
                    Raise(TranscriptChanged, t);
                    break;
                }
                case "metrics":
                    LastMetrics = m.ToObject<MetricsMsg>(Json);
                    lastSilence = LastMetrics.silence_s;
                    Raise(MetricsUpdated, LastMetrics);
                    break;
                case "rating":   // one chunk rated: the rules turn it into crowd, mood and points
                {
                    var r = m.ToObject<RatingMsg>(Json);
                    if (rules == null) break;
                    if (r.performer == "user")
                    {
                        ApplyRating(r);
                        Send(rules.Context());   // the next rating sees the room as it is now
                    }
                    else if (opp != null)
                    {
                        opp.ratings.Add(r);     // applied when its lines have played
                        ApplyOpponentRatings();
                    }
                    break;
                }
                case "listen_done":
                    lastUserVerse = m.ToObject<ListenDoneMsg>(Json).verse ?? "";
                    listenDone = true;
                    break;
                case "opponent_verse":
                {
                    var v = m.ToObject<OpponentVerseMsg>(Json);
                    if (opp != null) opp.received = true;
                    Interpretation = v.interpretation ?? "";
                    Raise(OpponentVerseReceived, v);
                    if (v.audio?.Type == JTokenType.String && (string)v.audio == "stream" && UseGenerator) StreamVerse(v.lines);
                    else PlayVerse(v.lines, v.audio is JArray a ? a.Select(x => (string)x).ToList() : null);
                    break;
                }
                case "opponent_line":
                {
                    var l = m.ToObject<OpponentLineMsg>(Json);
                    OnVerseLine(l.line, l.audio, l.Cues);
                    break;
                }
                case "error":
                {
                    var msg = (string)m["message"];
                    SetStatus("Server: " + msg);
                    Raise(ServerError, msg);
                    break;
                }
            }
        }

        void OnReaction(ReactionMsg r)
        {
            foreach (var w in r.word_drops ?? new List<string>())
                if (wordDropsUsed.Add(w)) Raise(WordDropUsed, w);
            Raise(Reaction, r);
            if (r.Punchline) { Raise(Punchline, r); onPunchline?.Invoke(); }
            if (r.FellOff) { Raise(FellOff, r); onFellOff?.Invoke(); }
            if (!string.IsNullOrEmpty(r.opponent?.trigger)) Raise(SoreSpotHit, r.opponent.trigger);
            if (!string.IsNullOrEmpty(r.opponent?.gesture))
            {
                Raise(OpponentGesture, r.opponent.gesture);
                onOpponentGesture?.Invoke(r.opponent.gesture);
            }
        }

        void ApplyState(BattleState s)
        {
            if (s == null) return;
            CurrentState = s;
            Raise(StateChanged, s);
            if (s.crowd != null)
            {
                onCrowdLevel?.Invoke(s.crowd.level);
                if (s.crowd.mood != lastCrowdMood)
                {
                    lastCrowdMood = s.crowd.mood;
                    Raise(CrowdMoodChanged, s.crowd.mood);
                    onCrowdMood?.Invoke(s.crowd.mood);
                }
            }
            if (s.opponent != null && s.opponent.mood != lastOppMood)
            {
                lastOppMood = s.opponent.mood;
                Raise(OpponentMoodChanged, s.opponent.mood);
            }
        }

        // ================= opponent: ad-libs while it writes =================

        bool UseGenerator => voice == OpponentVoice.ServerGenerator && generatorLive;
        bool UseServerVoice => voice != OpponentVoice.TextOnly;

        void StartFiller()
        {
            bool generated = UseGenerator;
            var f = new Filler
            {
                generated = generated,
                openers = generated ? Openers.Where(adlibUrls.ContainsKey).ToList() : Openers.ToList(),
                // From the generator: every ad-lib the server rendered for this MC (its own extras included).
                adlibs = generated ? adlibUrls.Keys.Where(t => !Openers.Contains(t)).ToList() : Adlibs.ToList(),
            };
            if (f.openers.Count == 0 || f.adlibs.Count == 0) return;
            filler = f;
            if (UseServerVoice)
                foreach (var t in f.openers.Concat(f.adlibs))
                    if (!adlibCache.ContainsKey(AdlibKey(t, generated))) CacheAdlib(t, generated);
            // First line on the next beat, then one ad-lib per bar (on beat 1, sometimes beat 3).
            f.n = (int)Math.Ceiling((AudioSettings.dspTime + 0.25 - beat0) / BeatSeconds);
        }

        static string AdlibKey(string text, bool generated) => (generated ? "g:" : "e:") + text;

        async void CacheAdlib(string text, bool generated)
        {
            int g = gen;
            var clip = generated ? await FetchClip(adlibUrls[text]) : await FetchClip(TtsUrl(text, line: false));
            if (g == gen && clip != null) adlibCache[AdlibKey(text, generated)] = clip;
        }

        void TickFiller()
        {
            var f = filler;
            if (f == null || f.done) return;
            double now = AudioSettings.dspTime, t = beat0 + f.n * BeatSeconds;
            // Decide well ahead (a slow frame can come late); the clip is scheduled on the audio clock, so it still
            // starts exactly on the beat.
            if (now < t - 0.3) return;
            string text = Pick(f.first ? f.openers : f.adlibs);
            adlibCache.TryGetValue(AdlibKey(text, f.generated), out var clip);
            double dur = clip ? clip.length : 0.9;
            if (t + dur > f.until - 0.15) { f.done = true; return; }   // would run into the verse
            bool said = !UseServerVoice;   // text only: the event is the ad-lib
            if (clip && UseServerVoice && t - outLat > now + 0.005)   // too late for this beat: skip it, don't play off it
            {
                var voiced = PlayVoice(clip, t - outLat);
                f.playing.Add((voiced, clip));
                Raise(OpponentVoiceScheduled, new OpponentVoiceClip { source = voiced.src, clip = clip, start = t - outLat, text = text });
                f.busyUntil = t + dur;
                said = true;
            }
            if (said)
            {
                LastAdlib = text;
                Raise(OpponentAdlib, text);
                onAdlib?.Invoke(text);
                f.first = false;
            }
            int beatInBar = ((f.n % BeatsPerBar) + BeatsPerBar) % BeatsPerBar;
            f.n += beatInBar == 0 && UnityEngine.Random.value < 0.4f ? 2 : BeatsPerBar - beatInBar;   // beat 3 or the next bar line
        }

        void StopFiller()
        {
            if (filler == null) return;
            double now = AudioSettings.dspTime;
            foreach (var (v, clip) in filler.playing)   // let a started ad-lib finish; skip voices reused since
                if (v.src && v.src.clip == clip && v.busyUntil > now) foreach (var a in v.Both) if (a) a.SetScheduledEndTime(now + 0.6);
            filler = null;
        }

        static string Pick(List<string> xs) => xs[UnityEngine.Random.Range(0, xs.Count)];

        // ================= opponent verse: one line per bar =================

        /// <summary>Whole verse at once (no streaming): fetch every line, then come in on the next bar.</summary>
        async void PlayVerse(List<string> lines, List<string> urls)
        {
            int g = gen;
            var clips = new AudioClip[lines.Count];
            if (UseGenerator && urls != null)
                for (int i = 0; i < lines.Count && i < urls.Count; i++) clips[i] = urls[i] != null ? await FetchClip(urls[i], timeoutS: LineTimeoutS) : null;
            else if (UseServerVoice)
                for (int i = 0; i < lines.Count; i++) clips[i] = await FetchClip(TtsUrl(lines[i], line: true), timeoutS: LineTimeoutS);
            if (g != gen) return;

            double busyUntil = filler?.busyUntil ?? 0;
            StopFiller();
            // Come in on the next bar line, after any ad-lib that is still being said.
            double barLen = BarSeconds;
            double earliest = Math.Max(AudioSettings.dspTime + 0.4, busyUntil + 0.1);
            var v = verse = new Verse
            {
                lines = lines, barS = barLen, clips = clips, mouth = new List<(float, string)>[lines.Count],
                arrived = Enumerable.Repeat(true, lines.Count).ToArray(), arrivedAt = new double[lines.Count],
                start = VerseStart(earliest),
            };
            for (int i = 0; i < lines.Count; i++) ScheduleVerseLine(v, i);
            ScheduleVerseTimeline(v);
        }

        /// <summary>Streamed verse: lines arrive one by one (opponent_line) while the ad-libs play.</summary>
        void StreamVerse(List<string> lines)
        {
            int n = lines.Count;
            verse = new Verse
            {
                lines = lines, barS = BarSeconds, stream = true, sentAt = AudioSettings.dspTime,
                clips = new AudioClip[n], mouth = new List<(float, string)>[n], arrived = new bool[n], arrivedAt = new double[n],
            };
        }

        async void OnVerseLine(int i, string url, List<(float t, string shape)> mouth)
        {
            var v = verse;
            if (v == null || !v.stream || i < 0 || i >= v.lines.Count) return;
            // No audio for this line (the generator failed): espeak, when it's there.
            // (a line that doesn't come in time is skipped: the verse goes on without it)
            var clip = url != null ? await FetchClip(url, timeoutS: LineTimeoutS) : await FetchClip(TtsUrl(v.lines[i], line: true), timeoutS: LineTimeoutS);
            if (verse != v) return;
            v.clips[i] = clip;
            v.mouth[i] = url != null ? mouth : null;   // the cues are for the server's WAV, not an espeak stand-in
            v.arrived[i] = true;
            v.arrivedAt[i] = AudioSettings.dspTime;
            if (double.IsNaN(v.start)) MaybeStartVerse(v);
            else ScheduleVerseLine(v, i);
        }

        // The verse starts on a bar line once enough lines are in that, at the measured render rate, every
        // later line arrives before its bar.
        void MaybeStartVerse(Verse v)
        {
            int n = v.lines.Count, have = 0;
            double b = v.barS, now = AudioSettings.dspTime;
            while (have < n && v.arrived[have]) have++;
            if (have == 0) return;
            // Seconds per line so far (from the verse message to the newest line), with a safety margin.
            double rate = 1.2 * (v.arrivedAt[have - 1] - v.sentAt) / have;
            double earliest = now + 0.4;
            for (int k = have; k < n; k++)
                earliest = Math.Max(earliest, v.arrivedAt[have - 1] + (k - have + 1) * rate + 0.3 - k * b);
            if (have < n && earliest > now + 2 * b)   // not close yet: keep the ad-libs going, decide on a later line
            {
                if (filler != null) filler.until = double.PositiveInfinity;
                return;
            }
            if (filler != null) earliest = Math.Max(earliest, filler.busyUntil + 0.1);
            v.start = VerseStart(earliest);
            if (filler != null) filler.until = v.start;
            At(v.start - 0.05, StopFiller);
            for (int i = 0; i < n; i++) if (v.arrived[i]) ScheduleVerseLine(v, i);
            ScheduleVerseTimeline(v);
        }

        void ScheduleVerseLine(Verse v, int i)
        {
            double t = v.start + i * v.barS, now = AudioSettings.dspTime;
            if (t - now < -0.4) return;   // arrived too late for its bar: skip it, keep the beat
            if (!v.clips[i]) return;
            double at = Math.Max(now, t - outLat);
            var said = PlayVoice(v.clips[i], at);
            Raise(OpponentVoiceScheduled, new OpponentVoiceClip
            {
                source = said.src, clip = v.clips[i], start = at, mouth = v.mouth[i], line = i, text = v.lines[i],
            });
        }

        // Line cues (the opponent's ratings land as their lines finish), and the end.
        void ScheduleVerseTimeline(Verse v)
        {
            int n = v.lines.Count;
            Raise(OpponentVerseScheduled, v.start);
            for (int i = 0; i < n; i++)
            {
                int line = i;
                At(v.start + i * v.barS, () =>
                {
                    if (verse != v) return;
                    v.current = line;
                    ApplyOpponentRatings();
                    Raise(OpponentLineStarted, line, v.lines[line]);
                    onOpponentLine?.Invoke(v.lines[line]);
                });
            }
            At(v.start + n * v.barS, () =>
            {
                if (verse != v) return;
                v.current = n;
                if (opp != null)
                {
                    opp.lines = n;
                    opp.finished = true;
                    ApplyOpponentRatings();
                }
                Raise(OpponentVerseFinished);
            });
        }

        // ================= audio helpers =================

        Voice PlayVoice(AudioClip clip, double dspAt, bool level = true)
        {
            if (level) Level(clip);
            double now = AudioSettings.dspTime;
            voicePool.RemoveAll(x => !x.pa || !x.mouth);   // its anchor went (destroyed with another object): make a new one
            var v = voicePool.FirstOrDefault(x => x.busyUntil < now && !x.pa.isPlaying && !x.mouth.isPlaying);
            if (v == null)
            {
                AudioSource Source(string name, Transform at)
                {
                    var go = new GameObject($"OpponentVoice {voicePool.Count} {name}");
                    go.transform.SetParent(at, false);
                    var a = go.AddComponent<AudioSource>();
                    a.playOnAwake = false;
                    return a;
                }
                v = new Voice { pa = Source("PA", beatAnchor), mouth = Source("Mouth", opponentVoiceAnchor) };
                voicePool.Add(v);
            }
            v.pa.spatialBlend = beatSpatialBlend;
            v.pa.volume = voiceVolume;
            v.mouth.spatialBlend = voiceSpatialBlend;
            v.mouth.volume = voiceVolume * (voiceThroughPA ? directVoice : 1f);
            v.src = voiceThroughPA ? v.pa : v.mouth;
            foreach (var a in v.Both)
            {
                a.outputAudioMixerGroup = voiceMixerGroup;
                a.clip = clip;
                a.Stop();
            }
            v.src.PlayScheduled(dspAt);
            if (voiceThroughPA && directVoice > 0f) v.mouth.PlayScheduled(dspAt);
            v.busyUntil = dspAt + clip.length + 0.1;
            return v;
        }

        double OutputLatency()
        {
            AudioSettings.GetDSPBufferSize(out int len, out int num);
            return (double)len * Math.Max(1, num) / AudioSettings.outputSampleRate + extraOutputLatencyMs / 1000.0;
        }

        // ================= levels =================

        /// <summary>Brings a voice clip (a line or ad-lib, downloaded) to voiceLevelDb, measured over its speech (the
        /// pauses left out), with a soft limit so a boosted clip doesn't clip. Once per clip.</summary>
        void Level(AudioClip clip)
        {
            if (!levelVoiceAndBeat || !clip || !levelled.Add(clip)) return;
            try
            {
                var data = new float[clip.samples * clip.channels];
                if (!clip.GetData(data, 0)) return;
                float db = SpeechLoudness(data, clip.frequency * clip.channels / 20);   // 50 ms frames
                if (float.IsNaN(db)) return;
                float g = Mathf.Clamp(Mathf.Pow(10f, (voiceLevelDb - db) / 20f), 0.1f, 8f);
                if (Mathf.Abs(g - 1f) < 0.05f) return;
                for (int i = 0; i < data.Length; i++)
                {
                    float y = data[i] * g, a = Mathf.Abs(y);
                    if (a > 0.8f) y = Mathf.Sign(y) * (0.8f + 0.12f * (float)Math.Tanh((a - 0.8f) / 0.12f));   // soft limit, under 0.92
                    data[i] = y;
                }
                clip.SetData(data, 0);
            }
            catch (Exception e) { Debug.LogWarning($"[Battle] couldn't level {clip.name}: {e.Message}"); }
        }

        /// <summary>The loudness (dBFS RMS) of the frames above -45 dB: speech without its pauses. NaN if all quiet.</summary>
        static float SpeechLoudness(float[] data, int frame)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i + frame <= data.Length; i += frame)
            {
                double p = 0;
                for (int k = i; k < i + frame; k++) p += data[k] * data[k];
                p /= frame;
                if (p > 3.2e-5) { sum += p; n++; }   // -45 dB
            }
            return n == 0 ? float.NaN : (float)(10 * Math.Log10(sum / n));
        }

        /// <summary>The beat's share of beatVolume so it sits voiceOverBeatDb under the levelled voice: the song measured
        /// once (thirty 1 s windows across it). 1 when levelling is off or the clip can't be read (compressed in memory).</summary>
        float BeatLevel(AudioClip clip)
        {
            if (!levelVoiceAndBeat || beatVolume <= 0f) return 1f;
            if (!songLoudness.TryGetValue(clip, out float db))
            {
                db = float.NaN;
                try
                {
                    int win = clip.frequency, len = clip.samples;
                    var buf = new float[win * clip.channels];
                    double sum = 0; int n = 0;
                    for (int w = 0; w < 30 && len > win; w++)
                    {
                        int at = (int)((long)(len - win) * w / 29);
                        if (!clip.GetData(buf, at)) break;
                        double p = 0;
                        foreach (var x in buf) p += x * x;
                        p /= buf.Length;
                        if (p > 1e-5) { sum += p; n++; }   // skip silence (-50 dB)
                    }
                    if (n > 0) db = (float)(10 * Math.Log10(sum / n));
                }
                catch (Exception e) { Debug.LogWarning($"[Battle] couldn't measure {clip.name}: {e.Message}"); }
                songLoudness[clip] = db;
            }
            if (float.IsNaN(db)) return 1f;
            float want = Mathf.Pow(10f, (voiceLevelDb - voiceOverBeatDb - db) / 20f);
            return Mathf.Clamp01(want / beatVolume);
        }

        // ================= beat =================

        void StartBeat()
        {
            if (beatFade != null) { StopCoroutine(beatFade); beatFade = null; }
            BeatPlaying = true;
            if (beatSources == null)
            {
                beatSources = new AudioSource[4];
                for (int i = 0; i < beatSources.Length; i++)
                {
                    var go = new GameObject("Beat " + i);
                    go.transform.SetParent(beatAnchor, false);
                    beatSources[i] = go.AddComponent<AudioSource>();
                    beatSources[i].playOnAwake = false;
                }
            }
            beatLevel = song != null && songClip != null ? BeatLevel(songClip) : 1f;
            foreach (var s in beatSources)
            {
                s.Stop();
                s.loop = false;
                s.volume = BeatGain;
                s.spatialBlend = beatSpatialBlend;
                s.outputAudioMixerGroup = beatMixerGroup;
            }
            segment = 0;
            nextSegment = beatStartDsp;
            // The song starts countInBars before the first phrase it has room to count in to (the drop, after an intro),
            // so the count-in ends as the first turn begins; always on a bar line.
            var firstPhrase = song?.phrases?.FirstOrDefault(p => p.start - countInBars * BarSeconds >= song.first_downbeat_s - 0.01);
            songFrom = song == null ? 0
                : firstPhrase != null ? firstPhrase.start - countInBars * BarSeconds
                : Math.Max(song.first_downbeat_s, (song.drop_s ?? song.first_downbeat_s) - countInBars * BarSeconds);
            metroBeat = 0;
            if (song == null && clickHi == null)
            {
                clickHi = MakeClick(1500, 1f);
                clickLo = MakeClick(950, 0.55f);
            }
            TickBeat();
        }

        void TickBeat()
        {
            if (!BeatPlaying || beatSources == null || beatStartDsp == 0) return;
            double now = AudioSettings.dspTime;
            if (song != null && songClip != null)
            {
                // The song starts at songFrom and loops whole bars, so the grid never drifts. Two sources alternate:
                // each plays one pass, the next is queued a second before it is needed.
                if (now < nextSegment - 1.0) return;
                double from = segment == 0 ? songFrom : song.loop_start_s;
                double to = song.loop_end_s > from + 1 ? song.loop_end_s : songClip.length;
                var src = beatSources[segment % 2];
                src.clip = songClip;
                src.timeSamples = Mathf.Clamp((int)(from * songClip.frequency), 0, songClip.samples - 1);
                src.PlayScheduled(nextSegment);
                src.SetScheduledEndTime(nextSegment + (to - from));
                nextSegment += to - from;
                segment++;
            }
            else if (clickHi != null)
            {
                double spb = BeatSeconds;
                while (beatStartDsp + metroBeat * spb < now + 0.2)
                {
                    var src = beatSources[metroBeat % beatSources.Length];
                    src.clip = metroBeat % BeatsPerBar == 0 ? clickHi : clickLo;
                    src.PlayScheduled(beatStartDsp + metroBeat * spb);
                    metroBeat++;
                }
            }
        }

        /// <summary>Where the song is (seconds into the file) when dsp time t is heard.</summary>
        double SongPosition(double t)
        {
            double x = t - beat0, first = song.loop_end_s - songFrom, loop = song.loop_end_s - song.loop_start_s;
            return x < first || loop <= 0 ? songFrom + x : song.loop_start_s + (x - first) % loop;
        }

        /// <summary>The next turn slot at or after dsp time t. With the song's phrases (marked by hand or analysed), a turn
        /// is a whole phrase, to its end: the user's comes in on the phrase's start (or, when late, 4 bars in, or for its
        /// last 4 bars); the opponent's (`opponent`) on the first bar line it's ready for, with at least 4 bars left, so its
        /// writing time eats the phrase's first bar or two (ad-libs) instead of the next phrase. Breaks are nobody's:
        /// whoever is next waits for the drop.
        /// Without phrases (the metronome, older song data): the next bar line, out of any break.
        /// The same rule as backend/web/app.js nextSlot.</summary>
        Slot NextSlot(double t, int want, bool opponent = false)
        {
            double barLen = BarSeconds;
            var ph = song != null && songClip != null ? song.phrases : null;
            if (ph == null || ph.Count == 0)
                return new Slot { start = AfterBreak(beat0 + Math.Ceiling((t - beat0) / barLen - 1e-6) * barLen), bars = want };
            double p = SongPosition(t);
            var entries = new List<double>();
            foreach (var x in ph)
            {
                int len = (int)Math.Round((x.end - x.start) / barLen);
                for (int k = 0; k <= Math.Max(0, len - 4); k += opponent ? 1 : 4) entries.Add(x.start + k * barLen);
                if (!opponent && len % 4 != 0 && len > 4) entries.Add(x.end - 4 * barLen);
            }
            entries.Sort();
            double at, start;
            int i = entries.FindIndex(x => x >= p - 0.02);
            if (i >= 0)
            {
                at = entries[i];
                start = t + (at - p);
            }
            else   // past the last one: the first one on the loop's next pass
            {
                at = entries.First(x => x >= song.loop_start_s - 0.02);
                start = t + (song.loop_end_s - p) + (at - song.loop_start_s);
            }
            var phrase = ph.First(x => x.start <= at + 0.02 && at < x.end - 0.02);
            int bars = (int)Math.Round((phrase.end - at) / barLen);   // to the end of the phrase
            return new Slot { start = beat0 + Math.Round((start - beat0) / barLen) * barLen, bars = bars };
        }

        /// <summary>When the opponent's verse comes in, once it can start at `earliest`: the slot planned for it, or the
        /// next one if it was late. Also learns how long verses take to be ready, for planning the next one.</summary>
        double VerseStart(double earliest)
        {
            var o = opp;
            if (o != null && !o.measured)
            {
                o.measured = true;
                prepS = 0.7 * prepS + 0.3 * Math.Max(1, earliest - o.asked);
            }
            if (o != null && o.plan.bars > 0 && o.plan.start >= earliest - 0.01) return o.plan.start;
            return NextSlot(earliest, barsPerTurn, true).start;
        }

        /// <summary>A turn starting on bar line t waits out a break in the song (an interlude, a refrain): it starts
        /// where the beat drops again.</summary>
        double AfterBreak(double t)
        {
            if (song == null || songClip == null || song.breaks == null) return t;
            double p = SongPosition(t);
            foreach (var b in song.breaks)
                if (b.Count >= 2 && p >= b[0] - 0.05 && p < b[1] - 0.05)
                    return t + Math.Round((b[1] - p) / BarSeconds) * BarSeconds;   // whole bars: stays exactly on the grid
            return t;
        }

        static AudioClip MakeClick(float freq, float gain)
        {
            int sr = AudioSettings.outputSampleRate, n = (int)(sr * 0.07);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float env = t < 0.002f ? t / 0.002f : Mathf.Exp(-(t - 0.002f) / 0.012f);
                data[i] = gain * env * Mathf.Sin(2 * Mathf.PI * freq * t);
            }
            var clip = AudioClip.Create("click" + freq, n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        readonly List<SongInfo> localSongList = new();

        /// <summary>Local Songs with their timing from Local Song Info; a clip without timing can't be used.</summary>
        void LoadLocalSongs()
        {
            localSongList.Clear();
            if (localSongInfo == null) return;
            List<SongInfo> list;
            try { list = JToken.Parse(localSongInfo.text).ToObject<List<SongInfo>>(Json); }
            catch (Exception e) { Debug.LogWarning($"[Battle] Local Song Info isn't a song list: {e.Message}"); return; }
            foreach (var s in list ?? new List<SongInfo>())
            {
                if (LocalClip(s) == null)
                {
                    Debug.LogWarning($"[Battle] {s.file} is in Local Song Info but not in Local Songs");
                    continue;
                }
                s.local = true;
                s.url = null;
                localSongList.Add(s);
            }
            foreach (var c in localSongs)
                if (c && !localSongList.Any(s => LocalClip(s) == c))
                    Debug.Log($"[Battle] {c.name} has no timing in Local Song Info; it is used only if the server's /songs has it");
        }

        AudioClip LocalClip(SongInfo s) =>
            localSongs.FirstOrDefault(c => c && c.name == Path.GetFileNameWithoutExtension(s.file ?? ""));

        /// <summary>The local songs, then the server's (a server song with a local clip plays the clip).</summary>
        List<SongInfo> MergeSongs(List<SongInfo> server)
        {
            var all = new List<SongInfo>(localSongList);
            foreach (var s in server ?? new List<SongInfo>())
                if (!all.Any(l => l.id == s.id || l.file == s.file))
                {
                    s.local = LocalClip(s) != null;
                    all.Add(s);
                }
            return all;
        }

        /// <summary>The song's clip, loaded (a server song downloaded) ahead of the battle and kept: the dressing room's
        /// boombox previews it, and the battle then starts without waiting for it. Of the downloaded server songs only
        /// this one, the battle's pick and the one playing are kept (each is a whole song in memory). Null when it
        /// can't be loaded.</summary>
        public Awaitable<AudioClip> PreloadSong(SongInfo s)
        {
            foreach (var id in songClips.Keys.ToList())
            {
                var c = songClips[id];
                if (id == s?.id || id == SelectedSong?.id || id == song?.id || (c && c == songClip)) continue;
                if (c && !localSongs.Contains(c)) Destroy(c);
                songClips.Remove(id);
            }
            return LoadSong(s);
        }

        /// <summary>The song is loaded (in the project and loaded, or downloaded): a battle with it starts right away.</summary>
        public bool SongReady(SongInfo s) => s != null && songClips.TryGetValue(s.id, out var c) && c;

        readonly HashSet<string> songsLoading = new();

        async Awaitable<AudioClip> LoadSong(SongInfo s)
        {
            if (s == null) return null;
            // the same song asked for twice (the boombox's preview, then the battle): one download, both get it
            while (songsLoading.Contains(s.id)) await Awaitable.NextFrameAsync();
            if (songClips.TryGetValue(s.id, out var cached) && cached) return cached;
            songsLoading.Add(s.id);
            try
            {
                var local = LocalClip(s);
                if (local)
                {
                    local.LoadAudioData();
                    while (local.loadState == AudioDataLoadState.Loading) await Awaitable.NextFrameAsync();
                    return songClips[s.id] = local.loadState == AudioDataLoadState.Loaded ? local : null;
                }
                if (string.IsNullOrEmpty(s.url)) return null;
                var clip = await FetchClip(s.url, keep: true);
                if (clip) songClips[s.id] = clip;
                return clip;
            }
            finally { songsLoading.Remove(s.id); }
        }

        // ================= mic =================

        async Awaitable<bool> StartMic()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                SetStatus("Waiting for mic permission…");
                Permission.RequestUserPermission(Permission.Microphone);
                for (float t = 0; t < 30 && !Permission.HasUserAuthorizedPermission(Permission.Microphone); t += Time.unscaledDeltaTime)
                    await Awaitable.NextFrameAsync();
                if (!Permission.HasUserAuthorizedPermission(Permission.Microphone)) return false;
            }
#endif
            if (Microphone.devices.Length == 0) return false;
            micName = string.IsNullOrEmpty(micDevice) ? null : micDevice;
            Microphone.GetDeviceCaps(micName, out int minF, out int maxF);
            int rate = minF == 0 && maxF == 0 ? micSampleRate : Mathf.Clamp(micSampleRate, minF, maxF);
            micClip = Audio.SharedMic.Acquire(micName, rate);   // shared: the robot's lip sync may be listening too
            if (micClip == null) return false;
            micName = Audio.SharedMic.Device;
            // Recording, not just started: the position has to move. A mic someone else started can have stalled (on
            // Quest, one started just after it was ended, as on a reload, can sit still): then once more, from scratch.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    Debug.LogWarning("[Battle] the mic isn't recording: restarting it");
                    micClip = Audio.SharedMic.Restart();
                    if (micClip == null) return false;
                }
                int from = Microphone.GetPosition(micName);
                for (float t = 0; t < 1.5f && Microphone.GetPosition(micName) == from; t += Time.unscaledDeltaTime)
                    await Awaitable.NextFrameAsync();
                if (Microphone.GetPosition(micName) != from) { micRate = micClip.frequency; return true; }   // the server resamples whatever rate
            }
            return false;
        }

        void PumpMic()
        {
            if (socket == null || !socket.IsOpen) return;
            if (testClip != null) { PumpVirtualMic(); return; }
            if (micClip == null) return;
            int pos = Microphone.GetPosition(micName), len = micClip.samples;
            int n = (pos - micPos + len) % len;
            while (n > 0)
            {
                int take = Math.Min(n, len - micPos);   // don't read across the wrap
                if (readBuf.Length != take) readBuf = new float[take];   // GetData fills the whole array
                micClip.GetData(readBuf, micPos);
                for (int i = 0; i < take; i++) PushSample(readBuf[i]);
                micPos = (micPos + take) % len;
                n -= take;
            }
        }

        // Test clip: a silent 16 kHz stream paced by the audio clock, with the clip mixed in during the user's turn.
        void PumpVirtualMic()
        {
            long target = (long)((AudioSettings.dspTime - micT0) * micRate);
            for (; virtualSent < target; virtualSent++)
            {
                float v = 0;
                double rel = (double)virtualSent / micRate - testClipAt;
                if (rel >= 0 && testSamples != null)
                {
                    double x = rel * testClip.frequency;
                    int i = (int)x;
                    if (i + 1 < testSamples.Length) v = Mathf.Lerp(testSamples[i], testSamples[i + 1], (float)(x - i));
                }
                PushSample(v);
            }
        }

        bool LoadTestClip()
        {
            if (testClip.loadState != AudioDataLoadState.Loaded && !testClip.LoadAudioData()) return false;
            var all = new float[testClip.samples * testClip.channels];
            if (!testClip.GetData(all, 0)) return false;
            testSamples = new float[testClip.samples];
            for (int i = 0; i < testSamples.Length; i++)
            {
                float s = 0;
                for (int c = 0; c < testClip.channels; c++) s += all[i * testClip.channels + c];
                testSamples[i] = s / testClip.channels;
            }
            return true;
        }

        void PlayTestClip(double turnT)
        {
            testClipAt = turnT;
            double heard = micT0 + turnT;
            PlayVoice(testClip, Math.Max(AudioSettings.dspTime, heard - outLat), level: false);   // the turn still runs its whole phrase
        }

        void PushSample(float v)
        {
            v = Mathf.Clamp(v, -1f, 1f);
            short s = (short)(v < 0 ? v * 32768f : v * 32767f);
            chunk[chunkN * 2] = (byte)s;
            chunk[chunkN * 2 + 1] = (byte)(s >> 8);
            chunkPeak = Math.Max(chunkPeak, Math.Abs(v));
            if (++chunkN < chunkSamples) return;
            socket.SendBinary(chunk);
            chunk = new byte[chunkSamples * 2];
            chunkN = 0;
            MicLevel = Mathf.Min(1f, chunkPeak * 1.6f);
            chunkPeak = 0;
        }

        // ================= networking =================

        Uri BaseUri
        {
            get
            {
                var b = new UriBuilder(serverUrl.EndsWith("/") ? serverUrl : serverUrl + "/");
                // 0.0.0.0 is where a server listens (uvicorn prints it), not an address to reach: plain requests to it
                // happen to work, the websocket refuses it. It means this machine.
                if (b.Host is "0.0.0.0" or "[::]") b.Host = "127.0.0.1";
                return b.Uri;
            }
        }
        string Url(string path) => new Uri(BaseUri, path).ToString();

        Uri WsUri()
        {
            var b = new UriBuilder(new Uri(BaseUri, "ws/battle"));
            b.Scheme = b.Scheme == "https" ? "wss" : "ws";
            return b.Uri;
        }

        string TtsUrl(string text, bool line)
        {
            string q = !line ? "" : voice == OpponentVoice.ServerGrid ? $"bpm={Bpm:0.###}&" : $"seconds={BarSeconds * 0.95:0.00}&";
            return Url($"tts?{q}text={Uri.EscapeDataString(text)}");
        }

        void Send(object msg)
        {
            if (socket != null && socket.IsOpen) socket.SendText(JsonConvert.SerializeObject(msg));
        }

        void Authorize(UnityWebRequest req)
        {
            if (!string.IsNullOrEmpty(Password)) req.SetRequestHeader("Authorization", "Bearer " + Password);
        }

        async Awaitable<T> GetJson<T>(string path) where T : class
        {
            using var req = UnityWebRequest.Get(Url(path));
            req.timeout = 5;
            Authorize(req);
            await req.SendWebRequest();
            if (req.responseCode == 401) PasswordRejected = true;
            if (req.result != UnityWebRequest.Result.Success) return null;
            try { return JToken.Parse(req.downloadHandler.text).ToObject<T>(Json); }
            catch (Exception e) { Debug.LogWarning($"[Battle] bad /{path}: {e.Message}"); return null; }
        }

        /// <summary>GET a path on the server (with the password): the body, or null (no server, an error, a timeout).</summary>
        public async Awaitable<byte[]> GetBytes(string path, int timeout = 20)
        {
            using var req = UnityWebRequest.Get(Url(path));
            req.timeout = timeout;
            Authorize(req);
            await req.SendWebRequest();
            if (req.responseCode == 401) PasswordRejected = true;
            return req.result == UnityWebRequest.Result.Success ? req.downloadHandler.data : null;
        }

        /// <summary>Downloads audio (path relative to the server, or a full URL). Clips are destroyed at the end of
        /// the battle unless keep is set.</summary>
        /// <summary>A clip from the server, or null. timeoutS bounds the wait: on Cloud Run a request can land on an
        /// instance that's still starting (two run side by side while one replaces the other) and sit there, or get a
        /// 404 for a clip only the other one has.</summary>
        async Awaitable<AudioClip> FetchClip(string pathOrUrl, bool keep = false, int timeoutS = 20)
        {
            string url = pathOrUrl.StartsWith("http") ? pathOrUrl : Url(pathOrUrl);
            string ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            var type = ext == ".mp3" ? AudioType.MPEG : ext == ".ogg" ? AudioType.OGGVORBIS : AudioType.WAV;
            int g = gen;
            using var req = UnityWebRequestMultimedia.GetAudioClip(url, type);
            ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
            Authorize(req);
            req.timeout = timeoutS;
            await req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Battle] {url}: {req.error} ({req.responseCode})");
                return null;
            }
            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip == null) return null;
            if (!keep)
            {
                if (g != gen) { Destroy(clip); return null; }
                battleClips.Add(clip);
            }
            return clip;
        }

        // ================= small helpers =================

        void SetStatus(string s)
        {
            Status = s;
            Raise(StatusChanged, s);
        }

        void SetPhase(BattlePhase p)
        {
            if (Phase == p) return;
            Phase = p;
            Raise(PhaseChanged, p);
        }

        // A subscriber that throws shouldn't stop the battle.
        static void Raise(Action e) { try { e?.Invoke(); } catch (Exception x) { Debug.LogException(x); } }
        static void Raise<T>(Action<T> e, T a) { try { e?.Invoke(a); } catch (Exception x) { Debug.LogException(x); } }
        static void Raise<T1, T2>(Action<T1, T2> e, T1 a, T2 b) { try { e?.Invoke(a, b); } catch (Exception x) { Debug.LogException(x); } }
    }
}
