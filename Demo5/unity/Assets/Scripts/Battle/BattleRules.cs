// The game rules: crowd mood, opponent mood and points, driven by the server's per-chunk ratings.
// A port of backend/freestyle/game.py (the reference); the web GUI's rules.js is another. Change all three together.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Freestyle.Battle
{
    public class BattleRules
    {
        public const float CrowdMax = 11;
        public static readonly string[] CrowdNames = { "dead", "silent", "restless", "polite", "warming up", "nodding",
            "into it", "cheering", "hyped", "going wild", "losing it", "raise the roof" };
        public static readonly string[] Dims = { "intimidated", "entertained", "confident", "annoyed", "impressed" };

        const float BaseUp = 0.30f;              // share of the gap the base closes after a better-than-base chunk
        const float BaseDown = 0.12f;            // ...and after a worse one (crowds hold their hype a bit)
        const float ReactionHalfLifeS = 2.0f;
        const float DeadAirDrain = 0.25f;        // base levels lost per second of dead air
        public const float DeadAirS = 1.5f;      // trailing silence (metrics.silence_s) that counts as dead air
        const float OppGain = 0.45f;
        const float UserPoints = 1.3f;           // the player's edge: live freestyling is harder than a written verse
        const float OpponentPoints = 0.85f;
        const float AssistBump = 0.6f;           // assist: added to the crowd's reaction to every user chunk
        const float AssistDown = 0.4f;           // ...weak user chunks lower the base this much as often, and draw no groan
        const float AssistDrain = 0.4f;          // ...dead air drains the room this much as fast
        public const float WordDropBonus = 1.5f; // crowd levels for working a word drop into a chunk
        const float SoreAnnoyed = 3.0f;          // hitting a sore spot: annoyance added
        const float SoreConfident = 1.2f;        // ...and confidence lost

        public readonly McInfo Mc;
        public readonly bool Assist;
        public readonly List<string> Riled = new();    // sore spots hit since the MC's last verse (for its comeback)
        public readonly List<string> Found = new();    // sore spots found this battle (revealed in the sheet)
        public readonly Dictionary<string, float> Mood;
        public float Base = 3, Reaction;
        public string Gesture = "shrugs";
        public float UserScore, OpponentScore;
        // Each turn is scored as if it were the standard length (barsPerTurn): with a song, a turn runs to the end of its
        // phrase, so the two sides can get different numbers of bars, and points come by the second. Set at each turn's start.
        public float UserTurnScale = 1, OpponentTurnScale = 1;
        public int Round = 1;
        public string Turn = "user";             // user | judging | opponent | idle
        public string LastEvent = "";
        readonly Dictionary<string, float> baseline;
        readonly McTuning tuning;
        readonly List<(float sting, string text)> hits = new();

        public BattleRules(McInfo mc, bool assist)
        {
            Mc = mc;
            Assist = assist;
            tuning = mc?.tuning ?? new McTuning();
            baseline = new Dictionary<string, float>
                { ["intimidated"] = 1, ["entertained"] = 4, ["confident"] = 7, ["annoyed"] = 1, ["impressed"] = 2 };
            if (mc?.mood != null) foreach (var kv in mc.mood) baseline[kv.Key] = kv.Value;
            Mood = new Dictionary<string, float>(baseline);
        }

        public float Level => Math.Clamp(Base + Reaction, 0, CrowdMax);
        public string CrowdMood => CrowdNames[(int)Math.Round(Level, MidpointRounding.ToEven)];
        float D(string k) => Mood.TryGetValue(k, out var v) ? v : 0;
        public float Composure => Math.Clamp(0.5f * D("confident") + 0.25f * (10 - D("intimidated")) + 0.25f * (10 - D("annoyed")), 0, 10);

        public string OpponentMood
        {
            get
            {
                string top = new[] { "intimidated", "annoyed", "impressed", "entertained" }.Aggregate((a, b) => D(b) > D(a) ? b : a);
                if (D("confident") >= 7 && D(top) < 5) return "cocky";
                if (D(top) < 4) return "calm";
                return top switch { "intimidated" => "rattled", "annoyed" => "heated", "impressed" => "respectful", _ => "amused" };
            }
        }

        /// <summary>The tug of war: -1 (all the opponent's) to 1 (all yours), the lead as a share of all the points (at
        /// least MinPull of them, so the first few points don't swing it end to end).</summary>
        public static float PullOf(float user, float opponent) => (user - opponent) / Math.Max(user + opponent, MinPull);
        public const float MinPull = 20f;
        public float Pull => PullOf(UserScore, OpponentScore);
        /// <summary>The dead zone in the middle of the tug of war: ending with the pull this close to 0 is a draw.</summary>
        public float DrawMargin = 0.06f;

        public string Winner => Math.Abs(Pull) <= DrawMargin ? "draw" : Pull > 0 ? "user" : "opponent";

        // ---- messages to and from the server ----

        /// <summary>One `rating` message; returns the points.</summary>
        public float ApplyRating(RatingMsg m, double barS)
        {
            if (m.performer == "user")
            {
                var before = new Dictionary<string, float>(Mood);
                float points = ApplyCrowd(m.crowd, (float)((m.chunk.end ?? 0) - (m.chunk.start ?? 0)), "user", !m.stall,
                                          WordDropBonus * (m.word_drops?.Count ?? 0));
                ApplyOpponent(m.opponent, "user");
                if (!m.stall)
                {
                    float Rated(string k) => m.opponent.dims.TryGetValue(k, out var v) ? v : 0;
                    float sting = Math.Max(Rated("intimidated") - before["intimidated"], 0) + Math.Max(Rated("annoyed") - before["annoyed"], 0);
                    hits.Add((sting + m.crowd.punchline * 3, m.chunk.text));
                }
                return points;
            }
            int a = m.chunk.lines?[0] ?? 0, b = m.chunk.lines?[1] ?? a;
            float pts = ApplyCrowd(m.crowd, (float)((b - a) * barS), "opponent");
            ApplyOpponent(m.opponent, "opponent");
            return pts;
        }

        /// <summary>A turn of `bars` bars for `performer`: its points scaled to a turn of `standard` bars.</summary>
        public void TurnBars(string performer, int bars, int standard)
        {
            float k = bars > 0 && standard > 0 ? Math.Clamp((float)standard / bars, 0.25f, 4f) : 1f;
            if (performer == "user") UserTurnScale = k; else OpponentTurnScale = k;
        }

        /// <summary>The `listen` message: the user's bars start at mic time t (with the room, for the rater).</summary>
        public object Listen(double t) => new { type = "listen", t, round = Round, crowd_level = Math.Round(Level, 2), opponent = RoundedMood() };

        /// <summary>The `context` message: the room as the rater should see it.</summary>
        public object Context() => new { type = "context", crowd_level = Math.Round(Level, 2), opponent = RoundedMood() };

        /// <summary>The `verse` message asking for the comeback. The sore spots it answers are cleared.</summary>
        public object VerseRequest(int bars)
        {
            var opponent = RoundedMood().ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            opponent["mood"] = OpponentMood;
            var msg = new
            {
                type = "verse", round = Round, bars,
                crowd = new { level = Math.Round(Level, 2), mood = CrowdMood },
                opponent,
                score = new { user = Math.Round(UserScore, 1), opponent = Math.Round(OpponentScore, 1) },
                riled = Riled.ToList(),
                hits = hits.OrderByDescending(h => h.sting).Take(3).Select(h => h.text).ToList(),
            };
            Riled.Clear();
            return msg;
        }

        Dictionary<string, double> RoundedMood() => Dims.ToDictionary(k => k, k => Math.Round(D(k), 2));

        // ---- the rules ----

        public float ApplyCrowd(CrowdRating r, float chunkS, string performer = "user", bool award = true, float bonus = 0)
        {
            float weight = 0.5f + 0.5f * r.confidence;
            float target = r.reaction + (r.punchline > 0.6f ? 1.5f * (r.punchline - 0.6f) / 0.4f : 0) + bonus;
            // Difficulty: a tougher MC's crowd gives the user less and the MC more.
            target += performer == "user" ? tuning.crowd_offset : tuning.crowd_home;
            bool kind = Assist && performer == "user";
            if (kind) target += AssistBump;
            target = Math.Clamp(target, 0, CrowdMax);
            float gap = target - Base;
            float down = BaseDown * (kind ? AssistDown : 1);
            Base = Math.Clamp(Base + (gap > 0 ? BaseUp : down) * weight * gap, 0, CrowdMax);
            // Instant response on top of the new base: pop up to the rated reaction, or a smaller groan below.
            Reaction = gap > 0 ? target - Base : kind ? 0 : 0.5f * (target - Base);

            float points = (0.6f * target + 0.4f * Level) * Math.Max(chunkS, 0.5f) + (r.punchline > 0.6f ? 10 * r.punchline : 0);
            points = award ? Math.Max(points, 0) : 0;   // a stall moves the crowd but earns nothing
            points *= performer == "user" ? UserPoints * UserTurnScale : OpponentPoints * OpponentTurnScale;
            if (performer == "user") UserScore += points; else OpponentScore += points;
            LastEvent = bonus > 0 ? "word drop" : r.punchline > 0.7f ? "punchline" : r.fell_off > 0.7f ? "fell_off" : CrowdMood;
            return points;
        }

        public void ApplyOpponent(OpponentRating r, string performer = "user")
        {
            // Ease toward the rating; the user's bars move it by the MC's opp_gain; then drift back to the baseline.
            float k = Math.Min(0.95f, OppGain * (0.5f + 0.5f * r.confidence) * (performer == "user" ? tuning.opp_gain : 1));
            foreach (var (name, v) in r.dims)
            {
                float cur = Mood.TryGetValue(name, out var c) ? c : v;
                cur += k * (v - cur);
                float b = baseline.TryGetValue(name, out var bb) ? bb : cur;
                Mood[name] = cur + tuning.resilience * (b - cur);
            }
            Gesture = r.gesture;
            if (!string.IsNullOrEmpty(r.trigger) && performer == "user")   // hit a nerve
            {
                float kk = r.trigger_p * tuning.opp_gain;
                Mood["annoyed"] = Math.Min(10, D("annoyed") + SoreAnnoyed * kk);
                Mood["confident"] = Math.Max(0, D("confident") - SoreConfident * kk);
                Gesture = "glares";
                LastEvent = "hit a nerve";
                if (!Riled.Contains(r.trigger)) Riled.Add(r.trigger);
                if (!Found.Contains(r.trigger)) Found.Add(r.trigger);
            }
        }

        /// <summary>Every frame: the pop fades; dead air during the user's turn drains the room.</summary>
        public void Tick(float dt, float silenceS)
        {
            Reaction *= (float)Math.Exp(-Math.Log(2) * dt / ReactionHalfLifeS);
            if (silenceS >= DeadAirS && Turn == "user")
                Base = Math.Max(0, Base - DeadAirDrain * (Assist ? AssistDrain : 1) * dt);
        }

        /// <summary>A snapshot for the coordinator's events and properties.</summary>
        public BattleState State() => new()
        {
            round = Round, turn = Turn,
            crowd = new CrowdState { level = Level, @base = Base, reaction = Reaction, mood = CrowdMood },
            opponent = new OpponentState
            {
                mood = OpponentMood, gesture = Gesture, composure = Composure, intimidated = D("intimidated"),
                entertained = D("entertained"), confident = D("confident"), annoyed = D("annoyed"), impressed = D("impressed"),
            },
            score = new Score { user = UserScore, opponent = OpponentScore },
            mc = Mc == null ? null : new McInfo
            {
                id = Mc.id, name = Mc.name, difficulty = Mc.difficulty, blurb = Mc.blurb, background = Mc.background,
                @explicit = Mc.@explicit, traits = Mc.traits, topics = Mc.topics, catchphrases = Mc.catchphrases,
                stats = Mc.stats, sore_spots_total = Mc.sore_spots_total, sore_spots_found = Found.ToList(),
                tuning = Mc.tuning, mood = Mc.mood,
            },
            assist = Assist, riled = Riled.ToList(), last_event = LastEvent,
        };
    }
}
