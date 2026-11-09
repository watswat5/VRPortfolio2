// Every Rocketbox motion-capture clip the rappers use, grouped by what it's for. A slot holds several clips so the
// same gesture doesn't look the same twice. Slots: the server's six gestures (laughs, shrugs, nods, winces, glares,
// steps_back), idles (plain, and one each for a rattled or heated mood), rapping (loops) with one-shot accents,
// listening (loops, while the other rapper's turn), and hype (dancing, cheering). The DJ's own: working (at the decks:
// hands on the table, or a hand to the headphones), waves and claps; hosting (the intros) uses the rapping loops.
// Rapper.controller (Freestyle > Build Rapper Animator) has one state per clip, named after it, in a sub-state machine
// per slot; Performer picks among them.
using System.Collections.Generic;
using System.Linq;

namespace Freestyle.Avatar
{
    public static class Moves
    {
        public readonly struct Move
        {
            public readonly string slot, clip;   // the clip = its Animator state's name (<file>_mirror: mirrored)
            public readonly bool loop;
            public Move(string slot, string clip, bool loop) { this.slot = slot; this.clip = clip; this.loop = loop; }
            /// <summary>The state's full path in Rapper.controller, for Animator.Play / CrossFade.</summary>
            public string Path => $"Base Layer.{slot}.{clip}";
        }

        public const string Idle = "idle", IdleRattled = "idle_rattled", IdleHeated = "idle_heated",
            Rapping = "rapping", RappingAccents = "rapping_accents", Listening = "listening", Hype = "hype",
            Working = "working";

        public static readonly Move[] All =
        {
            new(Idle, "m_idle_neutral_01", true), new(Idle, "m_idle_breathe_02", true), new(Idle, "m_idle_look_around_01", true),
            new(IdleRattled, "m_idle_nervous_01", true), new(IdleHeated, "m_idle_angry_02", true),
            new("laughs", "m_gestic_laugh_low", false), new("laughs", "m_gestic_laugh_loud", false), new("laughs", "m_gestic_laugh_extreme", false),
            new("shrugs", "m_gestic_shrug_01", false), new("shrugs", "m_gestic_shrug_02", false),
            new("nods", "m_gestic_listen_accept_01", false), new("nods", "m_gestic_listen_accept_03", false),
            new("winces", "m_gestic_listen_sad_01", false), new("winces", "m_gestic_listen_nervous_01", false),
            new("glares", "m_gestic_listen_angry_01", false),
            new("steps_back", "m_gestic_listen_deny_01", false), new("steps_back", "m_gestic_listen_deny_03", false),
            new(Rapping, "m_gestic_talk_cool", true), new(Rapping, "m_gestic_talk_excited_01", true),
            new(Rapping, "m_gestic_talk_neutral_01", true), new(Rapping, "m_gestic_talk_neutral_02", true),
            new(RappingAccents, "m_gestic_presentation_right_01", false), new(RappingAccents, "m_gestic_presentation_left_01", false),
            new(RappingAccents, "m_gestic_talk_right_01", false),
            new(Listening, "m_gestic_listen_neutral_01", true), new(Listening, "m_gestic_listen_relaxed_01", true),
            new(Listening, "m_gestic_listen_excited_01", true),
            new(Hype, "m_dancing_cool", true), new(Hype, "m_dancing_aggressive", true), new(Hype, "m_cheer_01", false), new(Hype, "m_cheer_03", false),
            new(Hype, "m_dancing_neutral", true),
            new(Working, "m_work_table", true), new(Working, "m_work_mid", true), new(Working, "m_headphones_idle", true),
            new("waves", "m_wave_01_mirror", false), new("claps", "m_claphands_01", false),
        };

        static Dictionary<string, Move[]> bySlot;

        /// <summary>The clips in a slot (empty for an unknown one).</summary>
        public static Move[] InSlot(string slot)
        {
            bySlot ??= All.GroupBy(m => m.slot).ToDictionary(g => g.Key, g => g.ToArray());
            return slot != null && bySlot.TryGetValue(slot, out var m) ? m : System.Array.Empty<Move>();
        }

        /// <summary>The slots, in order.</summary>
        public static IEnumerable<string> Slots => All.Select(m => m.slot).Distinct();
    }
}
