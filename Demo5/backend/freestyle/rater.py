"""Rating each chunk of the freestyle, separately for the crowd and the opponent.

A chunk is a few words or a phrase ending in a pause (see session.py), so the
room reacts while the rapper is still going. Each chunk gets two Jev calls in
parallel, each with its own narrow typed questions:

* crowd: how hard the crowd reacts to this chunk on the 0-11 scale, plus
  punchline / fell-off checks and flow and lyric scores.
* opponent: how intimidated, entertained, confident, annoyed and impressed the
  opponent is after this chunk, plus a gesture for the avatar.

Jev is called through OpenRouter's System One endpoint by default (same key as
the opponent LLM); point JEV_BASE_URL at https://api.typesafe.ai to call
TypeSafe directly. `HeuristicRater` answers the same questions from the
metrics alone, so everything runs and is testable without network or keys.
"""
from __future__ import annotations

import math
import re
import time
from dataclasses import dataclass, field
from typing import Protocol

import httpx

# Jev allows at most 10 score levels, so the rubric is 0..9 and gets stretched onto the
# game's 0..11 crowd scale (level i -> i * 11/9).
CROWD_LEVELS = [
    "Dead silent: no reaction at all, people checking their phones.",
    "Restless: murmuring, a few groans.",
    "Polite: scattered claps.",
    "Warming up: some heads start to move.",
    "Nodding: the room is with the rapper.",
    "Into it: audible 'ayy's and nods all around.",
    "Cheering: clear cheers for a good line.",
    "Hyped: loud cheers, hands in the air.",
    "Going wild: shouting, people jumping and turning to each other.",
    "Raise the roof: total pandemonium, the battle might be over.",
]
CROWD_SCALE = 11 / (len(CROWD_LEVELS) - 1)
FIVE = ["not at all", "slightly", "moderately", "very", "extremely"]
# Every question is about MC Static, the AI rival, never the human. Saying which way each kind of
# bar moves the feeling keeps Jev from rating the human's confidence instead.
OPP = "MC Static (the AI rival rapper, not the human user)"
OPPONENT_DIMS = {
    "intimidated": f"How intimidated is {OPP} right now? Hard-hitting, skillful bars from the human raise it; "
                   "stumbles, filler and weak lines from the human lower it.",
    "entertained": f"How entertained or amused is {OPP} right now (laughing at or with the human)? "
                   "Clumsy, silly or corny bars from the human make it laugh at them.",
    "confident": f"How confident is {OPP} that IT will win this battle? When the human stalls, mumbles, uses filler "
                 "or writes weak bars, MC Static gets MORE confident; when the human lands strong hits it gets less "
                 "confident; its own good bars make it more confident.",
    "annoyed": f"How annoyed or stung is {OPP} by what the human has said? Personal shots and lines that land raise it.",
    "impressed": f"How impressed is {OPP} by the human's skill, even if it won't admit it?",
}
GESTURES = {
    "laughs": "Laughs it off or points and laughs at the rapper.",
    "shrugs": "Shrugs, unbothered, maybe checks a watch.",
    "nods": "A small respectful nod: that one was good.",
    "winces": "Winces or looks away: that one hurt.",
    "glares": "Steps up and glares, getting heated.",
    "steps_back": "Steps back, visibly rattled.",
}

CROWD_QUESTIONS = {
    "reaction": {"type": "score", "criteria": CROWD_LEVELS,
                 "instructions": "How hard does a live battle-rap crowd react to the latest chunk, given everything said so far? Judge the chunk itself, not the crowd's current level. "
                                 "A jab at what the other side is really wearing (see the outfits) counts like any personal shot; one about clothes they aren't wearing falls flat."},
    "punchline": {"type": "noul", "instructions": "The latest chunk lands a punchline, clever wordplay, or a devastating line."},
    "fell_off": {"type": "noul", "instructions": "In the latest chunk the rapper lost the flow: stalling, mumbling, silence, or filler."},
    "flow": {"type": "score", "criteria": ["off beat and stumbling", "shaky", "steady", "locked in the pocket", "effortless"],
             "instructions": "Rate the rapper's flow in the latest chunk using the transcript and the timing metrics."},
    "lyricism": {"type": "score", "criteria": ["incoherent", "basic", "decent", "clever", "elite"],
                 "instructions": "Rate the lyricism of the latest chunk in the context of the verse."},
}
OPPONENT_QUESTIONS = {
    **{k: {"type": "score", "criteria": FIVE,
           "instructions": v + " Start from MC Static's state before this chunk (opponent_state_before_chunk_0_to_10) and adjust for the latest chunk."}
       for k, v in OPPONENT_DIMS.items()},
    "gesture": {"type": "choice", "criteria": GESTURES, "instructions": f"What does {OPP} do right after the latest chunk?"},
}
TRIGGER_MIN_P = 0.5  # how sure the rater must be that a chunk hit a sore spot


def opponent_questions(spots: list) -> dict:
    """OPPONENT_QUESTIONS plus, when the MC has sore spots, which one (if any) the latest chunk hit."""
    if not spots:
        return OPPONENT_QUESTIONS
    criteria = {"none": "The latest chunk doesn't touch any of these sore spots."}
    criteria.update({f"s{i}": f"It goes after this sore spot: {s.spot}" for i, s in enumerate(spots)})
    return {**OPPONENT_QUESTIONS, "trigger": {
        "type": "choice", "criteria": criteria,
        "instructions": f"Did the human's latest chunk hit one of {OPP}'s sore spots? Judge by meaning, not exact "
                        "words (a jab about the same thing counts); a passing mention that isn't a jab doesn't."}}


@dataclass
class RatingInput:
    chunk: str                    # the words just said
    verse_so_far: str
    flow: dict                    # metrics for the time the chunk covers
    lyrics: dict                  # metrics for the verse so far
    performer: str = "user"       # "user" or "opponent" (the AI's own verse)
    crowd_level: float = 3.0
    opponent_state: dict = field(default_factory=dict)
    other_verse: str = ""         # what the other side said last
    round: int = 1
    elapsed_s: float = 0.0
    opponent_profile: dict = field(default_factory=dict)   # the MC's character sheet (name, persona, traits, sore spots)
    sore_spots: list = field(default_factory=list)         # mcs.SoreSpot for the trigger question (user chunks only)
    user_outfit: list = field(default_factory=list)        # what the human wears (the MC's own is in the profile)
    user_profile: str = ""                                 # the human's pre-show interview, summed up

    def as_state(self) -> dict:
        who = "the user (human challenger)" if self.performer == "user" else "the AI opponent"
        return {
            "event": "live rap battle over a beat",
            "performer": who,
            "round": self.round,
            "seconds_into_verse": round(self.elapsed_s, 1),
            "latest_chunk": self.chunk,
            "verse_so_far": self.verse_so_far[-1500:],
            "other_side_last_verse": self.other_verse[-800:],
            "crowd_level_before_chunk_0_to_11": round(self.crowd_level, 1),
            "opponent_state_before_chunk_0_to_10": self.opponent_state,
            "flow_metrics": self.flow,
            "lyric_metrics": self.lyrics,
            **({"ai_opponent_profile": self.opponent_profile} if self.opponent_profile else {}),
            **({"human_user_outfit": self.user_outfit} if self.user_outfit else {}),
            **({"human_user_interview": self.user_profile} if self.user_profile else {}),
        }


@dataclass
class CrowdRating:
    reaction: float               # 0..11 expected
    punchline: float
    fell_off: float
    flow: float                   # 0..1
    lyricism: float               # 0..1
    confidence: float = 0.7
    probabilities: dict[str, float] = field(default_factory=dict)  # "0".."11"
    source: str = "heuristic"
    latency_ms: int = 0

    def as_dict(self) -> dict:
        return {"reaction": round(self.reaction, 2), "punchline": round(self.punchline, 3),
                "fell_off": round(self.fell_off, 3), "flow": round(self.flow, 3), "lyricism": round(self.lyricism, 3),
                "confidence": round(self.confidence, 3), "source": self.source, "latency_ms": self.latency_ms}


@dataclass
class OpponentRating:
    dims: dict[str, float]        # each 0..10
    gesture: str
    gesture_probs: dict[str, float]
    confidence: float = 0.7
    source: str = "heuristic"
    latency_ms: int = 0
    trigger: str | None = None    # the sore spot this chunk hit, if any
    trigger_p: float = 0.0        # how sure the rater is that it hit

    def as_dict(self) -> dict:
        return {"dims": {k: round(v, 2) for k, v in self.dims.items()}, "gesture": self.gesture,
                "trigger": self.trigger, "trigger_p": round(self.trigger_p, 3),
                "gesture_probs": {k: round(v, 3) for k, v in self.gesture_probs.items()},
                "confidence": round(self.confidence, 3), "source": self.source, "latency_ms": self.latency_ms}


class Rater(Protocol):
    async def rate_crowd(self, inp: RatingInput) -> CrowdRating: ...
    async def rate_opponent(self, inp: RatingInput) -> OpponentRating: ...


# ---- Jev -------------------------------------------------------------------
def _probs(raw, labels: list[str]) -> dict[str, float]:
    if isinstance(raw, dict):
        out = {str(k): float(v) for k, v in raw.items()}
    elif isinstance(raw, list):
        out = {labels[i] if i < len(labels) else str(i): float(v) for i, v in enumerate(raw)}
    else:
        out = {}
    total = sum(out.values()) or 1.0
    return {k: v / total for k, v in out.items()}


def _expected(ans: dict, levels: int) -> float:
    """Probability-weighted level (0..levels-1); falls back to the argmax score."""
    p = _probs(ans.get("probabilities"), [str(i) for i in range(levels)]) if ans.get("probabilities") else {}
    if p:
        return sum(int(k) * v for k, v in p.items() if k.isdigit())
    return float(ans.get("score", 0))


class JevRater:
    def __init__(self, base_url: str, api_key: str, model: str, timeout_s: float = 3.0, transport=None):
        self.url = base_url.rstrip("/") + "/v1/systemone"
        self.model = model
        self.client = httpx.AsyncClient(timeout=timeout_s, headers={"Authorization": f"Bearer {api_key}"},
                                        transport=transport)

    async def _ask(self, state: dict, questions: dict) -> tuple[dict, int]:
        t0 = time.perf_counter()
        resp = await self.client.post(self.url, json={"model": self.model, "state": state, "questions": questions})
        resp.raise_for_status()
        return resp.json(), int((time.perf_counter() - t0) * 1000)

    @staticmethod
    def parse_crowd(body: dict) -> CrowdRating:
        a = body["answers"]
        r = a["reaction"]
        return CrowdRating(
            reaction=_expected(r, len(CROWD_LEVELS)) * CROWD_SCALE,
            punchline=float(a["punchline"]["noul"]),
            fell_off=float(a["fell_off"]["noul"]),
            flow=_expected(a["flow"], 5) / 4,
            lyricism=_expected(a["lyricism"], 5) / 4,
            confidence=float(r.get("confidence", 0.7)),
            probabilities=_probs(r.get("probabilities"), [str(i) for i in range(len(CROWD_LEVELS))]),
            source=f"jev:{body.get('model', '?')}",
        )

    @staticmethod
    def parse_opponent(body: dict, spots: list | None = None) -> OpponentRating:
        a = body["answers"]
        dims = {k: _expected(a[k], 5) / 4 * 10 for k in OPPONENT_DIMS}
        confs = [float(a[k]["confidence"]) for k in OPPONENT_DIMS if "confidence" in a[k]]
        g = a["gesture"]
        gp = _probs(g.get("probabilities") or {g["choice"]: 1.0}, list(GESTURES))
        r = OpponentRating(dims, g.get("choice") or max(gp, key=gp.get), gp,
                           sum(confs) / len(confs) if confs else 0.7, f"jev:{body.get('model', '?')}")
        t = a.get("trigger")
        if spots and t:
            labels = ["none"] + [f"s{i}" for i in range(len(spots))]
            tp = _probs(t.get("probabilities") or {t.get("choice", "none"): 1.0}, labels)
            best = max((k for k in tp if k != "none"), key=tp.get, default=None)
            if best is not None and best[1:].isdigit() and int(best[1:]) < len(spots):
                r.trigger_p = tp[best]
                if tp[best] >= TRIGGER_MIN_P:
                    r.trigger = spots[int(best[1:])].spot
        return r

    async def rate_crowd(self, inp: RatingInput) -> CrowdRating:
        body, ms = await self._ask(inp.as_state(), CROWD_QUESTIONS)
        r = self.parse_crowd(body)
        r.latency_ms = ms
        return r

    async def rate_opponent(self, inp: RatingInput) -> OpponentRating:
        spots = inp.sore_spots if inp.performer == "user" else []
        body, ms = await self._ask(inp.as_state(), opponent_questions(spots))
        r = self.parse_opponent(body, spots)
        r.latency_ms = ms
        return r


# ---- offline stand-in --------------------------------------------------------
def _clip(x: float, lo: float = 0.0, hi: float = 1.0) -> float:
    return max(lo, min(hi, x))


def _bump(labels: list[str], center: float, width: float = 0.8) -> dict[str, float]:
    pos = center * (len(labels) - 1)
    w = [math.exp(-((i - pos) ** 2) / (2 * width**2)) for i in range(len(labels))]
    s = sum(w)
    return {l: v / s for l, v in zip(labels, w)}


class HeuristicRater:
    """Same outputs as Jev, from metrics only. Offline stand-in and fallback."""

    def _quality(self, inp: RatingInput) -> tuple[float, float, float, float]:
        f, l = inp.flow, inp.lyrics
        pocket = f.get("pocket") if f.get("pocket") is not None else 0.4
        on_beat = f.get("on_beat") if f.get("on_beat") is not None else 0.5
        speech = f.get("speech_ratio", 0.0)
        dead = f.get("dead_air_s", 0.0)
        flow = _clip(0.35 * pocket + 0.25 * on_beat + 0.2 * f.get("rate_consistency", 0) + 0.2 * min(speech / 0.6, 1) - 0.15 * dead)
        n_words = len(inp.chunk.split()) if inp.chunk != "(silence)" else 0
        lyric = _clip(0.5 * min(l.get("rhyme_density", 0) / 0.5, 1) + 0.15 * min(l.get("multi_rhymes", 0) / 3, 1)
                      + 0.25 * l.get("unique_ratio", 0) - 0.4 * l.get("repeat_ratio", 0) + (0.1 if n_words >= 3 else -0.3))
        fell = _clip(0.6 * min(dead / 2, 1) + 0.4 * (1 - min(speech / 0.4, 1)) + (0.4 if n_words == 0 else 0))
        punch = _clip(0.1 + 0.5 * max(0.0, lyric - 0.5) + 0.3 * min(l.get("multi_rhymes", 0) / 4, 1))
        return flow, lyric, fell, punch

    async def rate_crowd(self, inp: RatingInput) -> CrowdRating:
        flow, lyric, fell, punch = self._quality(inp)
        overall = _clip(0.45 * flow + 0.45 * lyric - 0.4 * fell + 0.3 * punch)
        probs = _bump([str(i) for i in range(12)], overall)
        reaction = sum(int(k) * v for k, v in probs.items())
        return CrowdRating(reaction, punch, fell, flow, lyric, 0.6, probs)

    async def rate_opponent(self, inp: RatingInput) -> OpponentRating:
        flow, lyric, fell, punch = self._quality(inp)
        q = _clip(0.5 * flow + 0.5 * lyric - 0.4 * fell + 0.2 * punch)
        if inp.performer == "opponent":  # their own verse going well makes them confident
            dims = {"intimidated": 2 * (1 - q), "entertained": 4 + 3 * q, "confident": 4 + 6 * q, "annoyed": 1, "impressed": 2}
        else:
            dims = {"intimidated": 10 * q**1.5, "entertained": 8 * (1 - q) + 2 * punch, "confident": 10 * (1 - q),
                    "annoyed": 8 * q * (0.5 + punch), "impressed": 10 * q * flow}
        dims = {k: _clip(v, 0, 10) for k, v in dims.items()}
        gp = _bump(["laughs", "shrugs", "nods", "glares", "winces", "steps_back"], q)
        r = OpponentRating(dims, max(gp, key=gp.get), gp, 0.6)
        if inp.performer == "user":   # sore spots by keyword: the offline stand-in for Jev's judgement
            text = " " + " ".join(re.findall(r"[a-z']+", inp.chunk.lower())) + " "
            for s in inp.sore_spots:
                if any(f" {k} " in text for k in s.keywords):
                    r.trigger, r.trigger_p = s.spot, 0.8
                    break
        return r


def make(settings) -> Rater:
    if settings.resolved_rater() == "jev":
        return JevRater(settings.jev_base_url, settings.jev_api_key, settings.jev_model)
    return HeuristicRater()
