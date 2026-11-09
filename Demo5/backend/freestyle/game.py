"""The game rules: crowd mood, opponent mood and points, driven by the server's per-chunk ratings.

The rules run in the client, not the server: the server only rates chunks (`rating` messages) and writes the
opponent's verse. This module is the reference version, used by tools/test_client.py and the tests; the Unity
client (BattleRules.cs) and the web GUI (web/rules.js) are ports of it, so change all three together.

Crowd (0 = dead .. 11 = raise the roof) has two parts:
* base: where the room sits. Good chunks raise it, weak ones lower it slowly,
  dead air drains it.
* reaction: the instant pop (or groan) for the chunk just rated. It jumps to
  the rated reaction and fades over a couple of seconds.
The level shown is base + reaction, clipped to 0..11.

Opponent: five 0..10 dimensions (intimidated, entertained, confident, annoyed,
impressed), each eased toward Jev's rating for the latest chunk. They are fed
to the LLM when it writes the comeback.

Points come from the crowd: each chunk scores its crowd reaction plus the
current crowd level, times the seconds it covered, plus a punchline bonus.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field

from . import mcs
from .rater import CrowdRating, OpponentRating

CROWD_MAX = 11.0
CROWD_NAMES = ["dead", "silent", "restless", "polite", "warming up", "nodding", "into it",
               "cheering", "hyped", "going wild", "losing it", "raise the roof"]

BASE_UP = 0.30          # share of the gap the base closes after a better-than-base chunk
BASE_DOWN = 0.12        # ...and after a worse one (crowds hold their hype a bit)
REACTION_HALF_LIFE_S = 2.0
DEAD_AIR_DRAIN = 0.25   # base levels lost per second of dead air
OPP_GAIN = 0.45

# The player gets an edge in scoring: freestyling live is much harder than the AI's written verse,
# so comparable bars should usually win the battle for the human.
USER_POINTS = 1.3       # multiplier on every point the user earns
OPPONENT_POINTS = 0.85  # ...and on every point the AI earns

DRAW_MARGIN = 0.06      # the tug of war's dead zone: a lead this small a share of all the points is a draw
MIN_PULL = 20.0         # ...the share is of at least this many points (the first few don't swing it end to end)

# Assist mode (opt-in): freestyling is hard, so the room is kinder to the user.
ASSIST_BUMP = 0.6       # added to the crowd's reaction to every user chunk
ASSIST_DOWN = 0.4       # weak user chunks lower the base this much as often (x BASE_DOWN), and draw no groan
ASSIST_DRAIN = 0.4      # dead air drains the room this much as fast
WORD_DROP_BONUS = 1.5   # crowd levels for working a word drop into a chunk

DEAD_AIR_S = 1.5        # trailing silence (metrics.silence_s) that counts as dead air

# Hitting one of the MC's sore spots (character sheet): scaled by how sure the rater is and the MC's composure.
SORE_ANNOYED = 3.0      # annoyance added
SORE_CONFIDENT = 1.2    # confidence lost


@dataclass
class Crowd:
    base: float = 3.0
    reaction: float = 0.0

    @property
    def level(self) -> float:
        return max(0.0, min(CROWD_MAX, self.base + self.reaction))

    @property
    def mood(self) -> str:
        return CROWD_NAMES[int(round(self.level))]


@dataclass
class OpponentMood:
    dims: dict[str, float] = field(default_factory=lambda: {
        "intimidated": 1.0, "entertained": 4.0, "confident": 7.0, "annoyed": 1.0, "impressed": 2.0})
    gesture: str = "shrugs"

    @property
    def composure(self) -> float:
        """0..10 summary for UI: how in control the opponent feels."""
        d = self.dims
        return max(0.0, min(10.0, 0.5 * d["confident"] + 0.25 * (10 - d["intimidated"]) + 0.25 * (10 - d["annoyed"])))

    @property
    def mood(self) -> str:
        d = self.dims
        top = max(("intimidated", "annoyed", "impressed", "entertained"), key=d.get)
        if d["confident"] >= 7 and d[top] < 5:
            return "cocky"
        if d[top] < 4:
            return "calm"
        return {"intimidated": "rattled", "annoyed": "heated", "impressed": "respectful", "entertained": "amused"}[top]


class Game:
    def __init__(self, mc: mcs.MC | None = None, assist: bool = False) -> None:
        self.mc = mc or mcs.get(None)
        self.assist = assist
        self.riled: list[str] = []      # sore spots hit since the MC's last verse (for its comeback)
        self.found: list[str] = []      # sore spots the user has found this battle (revealed in the sheet)
        self.crowd = Crowd()
        self.opponent = OpponentMood(dims=dict(self.mc.baseline))
        self.user_score = 0.0
        self.opponent_score = 0.0
        # Each turn is scored as if it were the standard length: with a song a turn runs to the end of its phrase, so
        # the sides can get different numbers of bars, and points come by the second. Set at each turn's start.
        self.turn_scale = {"user": 1.0, "opponent": 1.0}
        self.round = 1
        self.turn = "user"  # "user" | "judging" | "opponent" | "idle"
        self.last_event = ""
        self.hits: list[tuple[float, str]] = []   # (how much a user chunk got to the opponent, its text)

    # ---- messages to and from the server ----------------------------------------------------------------
    def apply_rating(self, m: dict, bar_s: float) -> float:
        """One `rating` message: the crowd, the opponent's mood, the points (returned)."""
        crowd, opp = CrowdRating(**m["crowd"]), OpponentRating(**m["opponent"])
        chunk = m["chunk"]
        if m["performer"] == "user":
            before = dict(self.opponent.dims)
            drops = m.get("word_drops") or []
            points = self.apply_crowd(crowd, chunk["end"] - chunk["start"], "user", award=not m.get("stall"),
                                      bonus=WORD_DROP_BONUS * len(drops))
            self.apply_opponent(opp)
            if not m.get("stall"):
                sting = (max(opp.dims["intimidated"] - before["intimidated"], 0)
                         + max(opp.dims["annoyed"] - before["annoyed"], 0))
                self.hits.append((sting + crowd.punchline * 3, chunk["text"]))
            return points
        a, b = chunk["lines"]
        points = self.apply_crowd(crowd, (b - a) * bar_s, "opponent")
        self.apply_opponent(opp, "opponent")
        return points

    def turn_bars(self, performer: str, bars: int, standard: int) -> None:
        """A turn of `bars` bars for `performer`: its points scaled to a turn of `standard` bars."""
        self.turn_scale[performer] = min(4.0, max(0.25, standard / bars)) if bars > 0 and standard > 0 else 1.0

    def context(self) -> dict:
        """The `context` message: the room as the rater should see it."""
        return {"type": "context", "crowd_level": round(self.crowd.level, 2),
                "opponent": {k: round(v, 2) for k, v in self.opponent.dims.items()}}

    def verse_request(self, bars: int) -> dict:
        """The `verse` message, asking for the comeback. The sore spots it answers are cleared."""
        msg = {"type": "verse", "round": self.round, "bars": bars,
               "crowd": {"level": round(self.crowd.level, 2), "mood": self.crowd.mood},
               "opponent": {"mood": self.opponent.mood, **{k: round(v, 2) for k, v in self.opponent.dims.items()}},
               "score": {"user": round(self.user_score, 1), "opponent": round(self.opponent_score, 1)},
               "riled": list(self.riled), "hits": [t for _, t in sorted(self.hits, reverse=True)[:3]]}
        self.riled.clear()
        return msg

    @property
    def pull(self) -> float:
        """The tug of war: -1 (all the opponent's) to 1 (all the user's), the lead as a share of all the points (at
        least MIN_PULL of them, so the first few points don't swing it end to end)."""
        u, o = self.user_score, self.opponent_score
        return (u - o) / max(u + o, MIN_PULL)

    @property
    def winner(self) -> str:
        """A draw when the pull ends in the dead zone (DRAW_MARGIN either side of the middle), as the headset scores it."""
        p = self.pull
        return "draw" if abs(p) <= DRAW_MARGIN else "user" if p > 0 else "opponent"

    # ---- the rules ----------------------------------------------------------------------------------------

    def apply_crowd(self, r: CrowdRating, chunk_s: float, performer: str = "user", award: bool = True,
                    bonus: float = 0.0) -> float:
        """Update the crowd from one chunk's rating; returns points awarded to the performer.

        bonus: extra crowd levels for this chunk (a word drop)."""
        c = self.crowd
        weight = 0.5 + 0.5 * r.confidence
        target = r.reaction + (1.5 * (r.punchline - 0.6) / 0.4 if r.punchline > 0.6 else 0.0) + bonus
        # Difficulty: a tougher MC's crowd gives the user less and the MC more.
        target += self.mc.crowd_offset if performer == "user" else self.mc.crowd_home
        kind = self.assist and performer == "user"
        if kind:
            target += ASSIST_BUMP
        target = max(0.0, min(CROWD_MAX, target))
        gap = target - c.base
        down = BASE_DOWN * (ASSIST_DOWN if kind else 1.0)
        c.base += (BASE_UP if gap > 0 else down) * weight * gap
        c.base = max(0.0, min(CROWD_MAX, c.base))
        # Instant response on top of the new base: pop up to the rated reaction, or a smaller groan below.
        c.reaction = target - c.base if gap > 0 else (0.0 if kind else 0.5 * (target - c.base))

        points = (0.6 * target + 0.4 * c.level) * max(chunk_s, 0.5) + (10 * r.punchline if r.punchline > 0.6 else 0.0)
        points = max(points, 0.0) if award else 0.0  # a stall moves the crowd but earns nothing
        points *= (USER_POINTS if performer == "user" else OPPONENT_POINTS) * self.turn_scale[performer]
        if performer == "user":
            self.user_score += points
        else:
            self.opponent_score += points
        self.last_event = ("word drop" if bonus else "punchline" if r.punchline > 0.7
                           else "fell_off" if r.fell_off > 0.7 else c.mood)
        return points

    def apply_opponent(self, r: OpponentRating, performer: str = "user") -> None:
        """Ease the MC's mood toward the rating; the user's bars move it by the MC's opp_gain, and it
        then drifts back toward the MC's baseline (resilience)."""
        k = min(0.95, OPP_GAIN * (0.5 + 0.5 * r.confidence) * (self.mc.opp_gain if performer == "user" else 1.0))
        for name, v in r.dims.items():
            cur = self.opponent.dims.get(name, v)
            cur += k * (v - cur)
            base = self.mc.baseline.get(name, cur)
            self.opponent.dims[name] = cur + self.mc.resilience * (base - cur)
        self.opponent.gesture = r.gesture
        if r.trigger and performer == "user":   # hit a nerve
            k = r.trigger_p * self.mc.opp_gain
            d = self.opponent.dims
            d["annoyed"] = min(10.0, d["annoyed"] + SORE_ANNOYED * k)
            d["confident"] = max(0.0, d["confident"] - SORE_CONFIDENT * k)
            self.opponent.gesture = "glares"
            self.last_event = "hit a nerve"
            for lst in (self.riled, self.found):
                if r.trigger not in lst:
                    lst.append(r.trigger)

    def tick(self, dt: float, dead_air: bool) -> None:
        """Reaction fades; dead air drains the room."""
        self.crowd.reaction *= math.exp(-math.log(2) * dt / REACTION_HALF_LIFE_S)
        if dead_air and self.turn == "user":
            drain = DEAD_AIR_DRAIN * (ASSIST_DRAIN if self.assist else 1.0)
            self.crowd.base = max(0.0, self.crowd.base - drain * dt)

    def state(self) -> dict:
        c, o = self.crowd, self.opponent
        return {
            "round": self.round, "turn": self.turn,
            "crowd": {"level": round(c.level, 2), "base": round(c.base, 2), "reaction": round(c.reaction, 2), "mood": c.mood},
            "opponent": {"mood": o.mood, "composure": round(o.composure, 2), "gesture": o.gesture,
                         **{k: round(v, 2) for k, v in o.dims.items()}},
            "score": {"user": round(self.user_score, 1), "opponent": round(self.opponent_score, 1)},
            "mc": self.mc.public(reveal=self.found), "assist": self.assist, "riled": list(self.riled),
            "last_event": self.last_event,
        }
