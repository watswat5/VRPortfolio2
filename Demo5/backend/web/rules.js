// The game rules: crowd mood, opponent mood and points, driven by the server's per-chunk ratings.
// A port of backend/freestyle/game.py (the reference); the Unity client's BattleRules.cs is another.
// Change all three together.
"use strict";

const Rules = (() => {
  const CROWD_MAX = 11;
  const CROWD_NAMES = ["dead", "silent", "restless", "polite", "warming up", "nodding", "into it",
                       "cheering", "hyped", "going wild", "losing it", "raise the roof"];
  const BASE_UP = 0.30;          // share of the gap the base closes after a better-than-base chunk
  const BASE_DOWN = 0.12;        // ...and after a worse one
  const REACTION_HALF_LIFE_S = 2.0;
  const DEAD_AIR_DRAIN = 0.25;   // base levels lost per second of dead air
  const DEAD_AIR_S = 1.5;        // trailing silence (metrics.silence_s) that counts as dead air
  const OPP_GAIN = 0.45;
  const USER_POINTS = 1.3, OPPONENT_POINTS = 0.85;
  const ASSIST_BUMP = 0.6, ASSIST_DOWN = 0.4, ASSIST_DRAIN = 0.4;
  const WORD_DROP_BONUS = 1.5;
  const SORE_ANNOYED = 3.0, SORE_CONFIDENT = 1.2;
  const DIMS = ["intimidated", "entertained", "confident", "annoyed", "impressed"];
  const clamp = (x, lo, hi) => Math.max(lo, Math.min(hi, x));

  class Game {
    /** mc: the MC's public sheet (GET /mcs or ready.mc), with `tuning` and `mood`. */
    constructor(mc, assist = false) {
      this.mc = mc;
      this.tuning = mc.tuning || { crowd_offset: 0, crowd_home: 0, opp_gain: 1, resilience: 0 };
      this.baseline = { intimidated: 1, entertained: 4, confident: 7, annoyed: 1, impressed: 2, ...(mc.mood || {}) };
      this.assist = assist;
      this.riled = [];      // sore spots hit since the MC's last verse (for its comeback)
      this.found = [];      // sore spots found this battle (revealed in the sheet)
      this.base = 3.0;
      this.reaction = 0.0;
      this.dims = { ...this.baseline };
      this.gesture = "shrugs";
      this.userScore = 0;
      this.opponentScore = 0;
      this.round = 1;
      this.turn = "user";   // user | judging | opponent | idle
      this.lastEvent = "";
      this.hits = [];       // [how much a user chunk got to the opponent, its text]
    }

    get level() { return clamp(this.base + this.reaction, 0, CROWD_MAX); }
    get crowdMood() { return CROWD_NAMES[Math.round(this.level)]; }
    get composure() {
      const d = this.dims;
      return clamp(0.5 * d.confident + 0.25 * (10 - d.intimidated) + 0.25 * (10 - d.annoyed), 0, 10);
    }
    get mood() {
      const d = this.dims;
      const top = ["intimidated", "annoyed", "impressed", "entertained"].reduce((a, b) => (d[b] > d[a] ? b : a));
      if (d.confident >= 7 && d[top] < 5) return "cocky";
      if (d[top] < 4) return "calm";
      return { intimidated: "rattled", annoyed: "heated", impressed: "respectful", entertained: "amused" }[top];
    }
    get winner() {
      return this.userScore > this.opponentScore ? "user" : this.opponentScore > this.userScore ? "opponent" : "draw";
    }

    // ---- messages to and from the server ----
    /** One `rating` message; returns the points. */
    applyRating(m, barS) {
      const crowd = m.crowd, opp = m.opponent, chunk = m.chunk;
      if (m.performer === "user") {
        const before = { ...this.dims };
        const drops = m.word_drops || [];
        const points = this.applyCrowd(crowd, chunk.end - chunk.start, "user", !m.stall, WORD_DROP_BONUS * drops.length);
        this.applyOpponent(opp, "user");
        if (!m.stall) {
          const sting = Math.max(opp.dims.intimidated - before.intimidated, 0) + Math.max(opp.dims.annoyed - before.annoyed, 0);
          this.hits.push([sting + crowd.punchline * 3, chunk.text]);
        }
        return points;
      }
      const [a, b] = chunk.lines;
      const points = this.applyCrowd(crowd, (b - a) * barS, "opponent");
      this.applyOpponent(opp, "opponent");
      return points;
    }

    /** The `context` message: the room as the rater should see it. */
    context() {
      return { type: "context", crowd_level: +this.level.toFixed(2), opponent: this.roundedDims() };
    }

    /** The `verse` message asking for the comeback. The sore spots it answers are cleared. */
    verseRequest(bars) {
      const msg = {
        type: "verse", round: this.round, bars,
        crowd: { level: +this.level.toFixed(2), mood: this.crowdMood },
        opponent: { mood: this.mood, ...this.roundedDims() },
        score: { user: +this.userScore.toFixed(1), opponent: +this.opponentScore.toFixed(1) },
        riled: [...this.riled],
        hits: [...this.hits].sort((x, y) => y[0] - x[0]).slice(0, 3).map((h) => h[1]),
      };
      this.riled = [];
      return msg;
    }

    roundedDims() {
      return Object.fromEntries(DIMS.map((k) => [k, +this.dims[k].toFixed(2)]));
    }

    // ---- the rules ----
    applyCrowd(r, chunkS, performer = "user", award = true, bonus = 0) {
      const weight = 0.5 + 0.5 * r.confidence;
      let target = r.reaction + (r.punchline > 0.6 ? 1.5 * (r.punchline - 0.6) / 0.4 : 0) + bonus;
      // Difficulty: a tougher MC's crowd gives the user less and the MC more.
      target += performer === "user" ? this.tuning.crowd_offset : this.tuning.crowd_home;
      const kind = this.assist && performer === "user";
      if (kind) target += ASSIST_BUMP;
      target = clamp(target, 0, CROWD_MAX);
      const gap = target - this.base;
      const down = BASE_DOWN * (kind ? ASSIST_DOWN : 1);
      this.base = clamp(this.base + (gap > 0 ? BASE_UP : down) * weight * gap, 0, CROWD_MAX);
      // Instant response on top of the new base: pop up to the rated reaction, or a smaller groan below.
      this.reaction = gap > 0 ? target - this.base : (kind ? 0 : 0.5 * (target - this.base));

      let points = (0.6 * target + 0.4 * this.level) * Math.max(chunkS, 0.5) + (r.punchline > 0.6 ? 10 * r.punchline : 0);
      points = award ? Math.max(points, 0) : 0;   // a stall moves the crowd but earns nothing
      points *= performer === "user" ? USER_POINTS : OPPONENT_POINTS;
      if (performer === "user") this.userScore += points; else this.opponentScore += points;
      this.lastEvent = bonus ? "word drop" : r.punchline > 0.7 ? "punchline" : r.fell_off > 0.7 ? "fell_off" : this.crowdMood;
      return points;
    }

    applyOpponent(r, performer = "user") {
      // Ease toward the rating; the user's bars move it by the MC's opp_gain; then drift back to the baseline.
      const k = Math.min(0.95, OPP_GAIN * (0.5 + 0.5 * r.confidence) * (performer === "user" ? this.tuning.opp_gain : 1));
      for (const [name, v] of Object.entries(r.dims)) {
        let cur = this.dims[name] ?? v;
        cur += k * (v - cur);
        const base = this.baseline[name] ?? cur;
        this.dims[name] = cur + this.tuning.resilience * (base - cur);
      }
      this.gesture = r.gesture;
      if (r.trigger && performer === "user") {   // hit a nerve
        const kk = r.trigger_p * this.tuning.opp_gain, d = this.dims;
        d.annoyed = Math.min(10, d.annoyed + SORE_ANNOYED * kk);
        d.confident = Math.max(0, d.confident - SORE_CONFIDENT * kk);
        this.gesture = "glares";
        this.lastEvent = "hit a nerve";
        for (const lst of [this.riled, this.found]) if (!lst.includes(r.trigger)) lst.push(r.trigger);
      }
    }

    /** Every frame: the pop fades; dead air during the user's turn drains the room. */
    tick(dt, silenceS) {
      this.reaction *= Math.exp(-Math.LN2 * dt / REACTION_HALF_LIFE_S);
      if (silenceS >= DEAD_AIR_S && this.turn === "user") {
        const drain = DEAD_AIR_DRAIN * (this.assist ? ASSIST_DRAIN : 1);
        this.base = Math.max(0, this.base - drain * dt);
      }
    }

    /** The same shape the server's `state` had, for rendering. */
    state() {
      return {
        round: this.round, turn: this.turn,
        crowd: { level: this.level, base: this.base, reaction: this.reaction, mood: this.crowdMood },
        opponent: { mood: this.mood, composure: this.composure, gesture: this.gesture, ...this.dims },
        score: { user: this.userScore, opponent: this.opponentScore },
        mc: { ...this.mc, sore_spots_found: [...this.found] }, assist: this.assist, riled: [...this.riled],
        last_event: this.lastEvent,
      };
    }
  }

  return { Game, WORD_DROP_BONUS };
})();
