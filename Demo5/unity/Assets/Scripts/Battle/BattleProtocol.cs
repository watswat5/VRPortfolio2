// Message types for the battle server (backend/PROTOCOL.md). Field names match the JSON exactly
// (snake_case) so Newtonsoft maps them without attributes. A few types (BattleState, ReactionMsg, TurnMsg,
// TurnEndMsg, ResultMsg) are built by this client, from its own game rules, for its events.
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Freestyle.Battle
{
    // ---------- HTTP: GET /songs, /mcs, /health ----------

    /// <summary>A song and its timing, from GET /songs or a local song list (tools/export_songs.py).</summary>
    public class SongInfo
    {
        public string id, file, title, url;   // url: on the server (null for a local song)
        public bool local;                    // the clip is in the project (not part of the JSON)
        public float bpm;
        public int beats_per_bar = 4;
        public float first_downbeat_s, loop_start_s, loop_end_s, duration_s;
        public float? drop_s;   // where the beat kicks in after an intro: the count-in ends here
        public List<List<float>> breaks = new();   // [start_s, end_s] where the beat drops out; no turn starts in one
        public List<PhraseInfo> phrases = new();   // the slots turns are made of
        public bool marked;                        // phrases marked by hand: each one is a whole turn
    }

    /// <summary>A phrase of a song (seconds into the file); `section` numbers the beat between breaks.</summary>
    public class PhraseInfo
    {
        public float start, end;
        public int section;
    }

    public class McStats
    {
        public float wordplay, flow, composure, presence, rep;
    }

    public class McInfo
    {
        public string id, name, difficulty, blurb, background;
        public bool @explicit;
        public List<string> traits = new(), topics = new(), catchphrases = new();
        public McStats stats;
        public int sore_spots_total;
        public List<string> sore_spots_found = new();
        public McTuning tuning = new();                          // for the game rules
        public Dictionary<string, float> mood = new();          // starting mood, each 0-10
    }

    /// <summary>How the MC's stats bend the rules (backend/freestyle/mcs.py).</summary>
    public class McTuning
    {
        public float crowd_offset;    // added to the crowd's reaction to the user's chunks
        public float crowd_home;      // ...and to the MC's own
        public float opp_gain = 1;    // how far each user chunk moves the MC's mood
        public float resilience;      // share of the way the mood drifts back to its start after each chunk
    }

    public class HealthInfo
    {
        public bool ok;
        public string transcriber, rater, opponent;
        public string voice;          // "qwen" (a server voice is loaded, whatever the engine), "loading", or "client" (none)
        public string voice_engine;   // qwen, indextts, breeze, or none
        public string mouth;          // "rhubarb": the server makes mouth cues; null: it doesn't
    }

    // ---------- state (built by the client's rules) ----------

    public class CrowdState
    {
        public float level;    // 0 dead .. 11 raise the roof; = base + reaction
        public float @base;    // where the room sits
        public float reaction; // the instant pop for the last chunk, fades over ~2 s
        public string mood;    // dead, silent, restless, polite, warming up, nodding, into it, cheering, hyped, going wild, losing it, raise the roof
    }

    public class OpponentState
    {
        public string mood;     // cocky, calm, amused, respectful, heated, rattled
        public string gesture;  // laughs, shrugs, nods, winces, glares, steps_back
        public float composure, intimidated, entertained, confident, annoyed, impressed;   // each 0-10
    }

    public class Score
    {
        public float user, opponent;
    }

    public class BattleState
    {
        public int round;
        public string turn;     // user, judging, opponent, idle
        public CrowdState crowd;
        public OpponentState opponent;
        public Score score;
        public McInfo mc;
        public bool assist;
        public List<string> riled = new();
        public string last_event;
    }

    // ---------- server to client ----------

    public class ReadyMsg
    {
        public int sample_rate;
        public string rater, opponent, transcriber;
        public string voice;                                  // "qwen": the server voices the opponent (any engine)
        public Dictionary<string, string> adlibs = new();     // ad-lib text -> clip URL (server generator only)
        public string mouth;                                  // "rhubarb": opponent_line carries mouth cues; null: it won't
        public McInfo mc;                                      // the opponent's sheet, with its tuning
    }

    /// <summary>A turn begins (built by the client).</summary>
    public class TurnMsg
    {
        public string turn;     // user / opponent
        public int round;
        public double t;        // user turns: start on the mic timeline (a bar line)
        public int bars;
        public double? length_s;
        public BattleState state;
    }

    public class Word
    {
        public string w;
        public double s, e;
    }

    public class TranscriptMsg
    {
        public double t;
        public List<Word> committed = new();
        public string partial = "";
    }

    public class FlowMetrics
    {
        public float? syllable_rate, syllables_per_beat, rate_consistency, speech_ratio, longest_pause_s, dead_air_s,
                      loudness_db, dynamics_db, on_beat, pocket, downbeat_hits;
        public int? pause_count;
    }

    public class LyricMetrics
    {
        public int words, syllables, multi_rhymes;
        public float rhyme_density, end_rhyme_rate, unique_ratio, repeat_ratio;
    }

    public class MetricsMsg
    {
        public double t;
        public FlowMetrics window;   // last 6 s
        public FlowMetrics turn;     // whole turn so far
        public LyricMetrics lyrics;
        public float silence_s;      // trailing silence: dead air drains the room
    }

    public class Chunk
    {
        public string text;
        public double? start, end;   // user chunks: on the mic timeline
        public List<int> lines;      // opponent chunks: verse lines [a, b)
    }

    /// <summary>One chunk rated by the server. The client's rules turn it into crowd, mood and points.</summary>
    public class RatingMsg
    {
        public string performer;   // user / opponent
        public int round;
        public double t;
        public Chunk chunk;
        public bool stall;         // user: no words (a pause), moves the crowd but earns nothing
        public CrowdRating crowd;
        public OpponentRating opponent;
        public List<string> word_drops = new();   // used in this chunk
    }

    public class ListenDoneMsg
    {
        public int round;
        public string verse;       // the user's whole turn
    }

    public class CrowdRating
    {
        public float reaction, punchline, fell_off, flow, lyricism, confidence;
        public string source;
        public int latency_ms;
    }

    public class OpponentRating
    {
        public Dictionary<string, float> dims = new();
        public string gesture;
        public Dictionary<string, float> gesture_probs = new();
        public string trigger;   // sore spot hit, or null
        public float trigger_p, confidence;
        public string source;
        public int latency_ms;
    }

    /// <summary>A rating as the rules applied it (built by the client).</summary>
    public class ReactionMsg
    {
        public string performer;   // user / opponent
        public double t;
        public Chunk chunk;
        public CrowdRating crowd;
        public OpponentRating opponent;
        public float points;
        public List<string> word_drops = new();
        public BattleState state;

        public bool IsUser => performer == "user";
        public bool Punchline => crowd != null && crowd.punchline > 0.6f;
        public bool FellOff => crowd != null && crowd.fell_off > 0.6f;
    }

    public class WordDropsMsg
    {
        public List<string> words = new();
        public float bonus;        // crowd levels each is worth (filled in by the client's rules)
    }

    public class HintsMsg
    {
        public string word;
        public List<string> rhymes = new();
    }

    /// <summary>The user's turn is over (built by the client).</summary>
    public class TurnEndMsg
    {
        public int round;
        public string verse;
        public BattleState state;
    }

    public class OpponentVerseMsg
    {
        public int round;
        public List<string> lines = new();
        public double bar_s;
        public JToken audio;   // "stream" (lines follow as opponent_line), null (client voices it)
        public string interpretation, source;
    }

    public class OpponentLineMsg
    {
        public int round, line;
        public string audio;   // URL of a WAV fitted to one bar, or null
        public JToken mouth;   // [[start_s, shape], ...] from the start of the WAV (Rhubarb's A-H, X), or null

        /// <summary>The mouth cues, or null when the server sent none.</summary>
        public List<(float t, string shape)> Cues => MouthCues.Parse(mouth);
    }

    /// <summary>Mouth cues as the server sends them (opponent_line "mouth", POST /lipsync): [[start_s, shape], ...].</summary>
    public static class MouthCues
    {
        /// <summary>The cues in order, or null for none (no Rhubarb on the server, or it failed on this line).</summary>
        public static List<(float t, string shape)> Parse(JToken mouth)
        {
            if (mouth is not JArray a) return null;
            var cues = new List<(float t, string shape)>(a.Count);
            foreach (var c in a)
                if (c is JArray p && p.Count >= 2 && p[0].Type is JTokenType.Float or JTokenType.Integer && p[1].Type == JTokenType.String)
                    cues.Add(((float)p[0], (string)p[1]));
            return cues.Count > 0 ? cues : null;
        }
    }

    /// <summary>A clip of the opponent's voice, scheduled on one of the coordinator's AudioSources: its lines and ad-libs.
    /// Lip sync follows the source once the clip starts (it can be scheduled a few bars ahead).</summary>
    public class OpponentVoiceClip
    {
        public AudioSource source;
        public AudioClip clip;
        public double start;                        // dsp time it starts playing (AudioSource.PlayScheduled)
        public List<(float t, string shape)> mouth; // its mouth cues, or null: lip-sync from the audio
        public int line = -1;                       // the verse line, or -1 for an ad-lib
        public string text;
    }

    /// <summary>The battle is over (built by the client).</summary>
    public class ResultMsg
    {
        public string winner;  // user / opponent / draw
        public BattleState state;
    }

    public class ErrorMsg
    {
        public string message;
    }
}
