"""The opponent's comeback verse.

`OpenRouterOpponent` asks an LLM on OpenRouter to interpret the user's verse and
answer it in character, with its mood (from the scoreboard) shaping the tone.
`TemplateOpponent` is the offline stand-in.
"""
from __future__ import annotations

import json
import random
import re
from typing import Protocol

import httpx

from . import mcs

SYLLABLES_PER_S = 4.8  # what the voice can rap cleanly; sets the per-bar syllable budget

SYSTEM = (
    "You are {name}, {persona}, in a live freestyle battle against the user. Your skill at your best: {skill}. "
    "Write your comeback verse. Rules: exactly {bars} bars, one bar per line, at most {max_syl} syllables per bar "
    "(each bar is rapped in {bar_s:.1f} s, so longer lines won't fit), "
    "end rhymes in couplets, flip at least one of the user's lines back at them, {content}.\n"
    "How you feel right now, each 0-10 (play it, don't state the numbers): {dims}. Overall you are {mood}.\n"
    "If you're intimidated or annoyed, it shows: you get defensive, louder, or go harder at them. "
    "If you're confident or entertained, you're dismissive and play to the crowd. If impressed, you may give "
    "grudging respect before flipping it.\n"
    "The crowd is at {crowd_level:.0f}/11 ({crowd_mood}). Score so far: you {opp_score:.0f}, them {user_score:.0f}.\n"
    "{sheet}"
    "{performance}"
    "{hits}"
    "{outfit}"
    "{profile}"
    "The user has no gender: never use he, him, his, she, her or hers for them, nor boy, girl, son or sis "
    "(bro, man and dude are fine); say you, their name, or rookie, chump and the like.{insults}\n"
    "They show up as a robot, but that's just their look tonight: you're battling the rapper, not a machine. Roast "
    "the person (their bars, their delivery, what they told the interviewer, what they're wearing). At most one "
    "robot or machine jab in the whole verse, only if it's a sharp one, and never call them bot, robot or the like.\n"
    "Your verse is performed by a voice actor (text-to-speech). Give every line a short delivery direction "
    "(2-6 words, tone of voice only, never words to say and never sounds like laughing, sighing or scoffing), "
    "and let it follow your mood and change across the verse: "
    "e.g. \"aggressive, leaning in\", \"dismissive, cold\", \"mocking, sing-song\", \"slow and menacing\", "
    "\"hyped, shouting to the crowd\".\n"
    'Reply with JSON only: {{"interpretation": "<one sentence on what the user said and how well>", '
    '"lines": [{{"text": "<the bar>", "delivery": "<direction>"}}, ...]}}'
)


def _system(ctx: dict, name: str | None = None) -> str:
    """ctx may carry `mc` (mcs.MC); its mood-driven performance note shapes how well the verse is written."""
    mc = mcs.get(ctx.get("mc"))
    note = mcs.performance_note(ctx["opponent"], mc, ctx["bars"])
    hits = ctx.get("hits") or []
    hit_txt = ("Their lines that got to you most: " + " | ".join(f'"{h}"' for h in hits[:3]) + "\n") if hits else ""
    outfit = ctx.get("outfit") or []
    outfit_txt = ("What they're wearing (fair game: clown it if it gives you material, but don't make it the whole "
                  "verse): " + "; ".join(outfit) + ".\n") if outfit else ""
    profile = ctx.get("profile") or ""
    profile_txt = (f"From their pre-show interview (true things about them: use the specifics, a name, a place, a "
                   f"hero, a weak spot, to make it personal): {profile}\n") if profile else ""
    bar_s = ctx.get("bar_s", 2.67)
    return SYSTEM.format(
        sheet=mcs.sheet_prompt(mc, ctx.get("riled") or []),
        insults="" if mc.gendered_insults else (" No gendered insults either: never bitch, hoe, thot, whore, slut, skank, "
                                                 "pussy, cunt, twat or dick."),
        content=("explicit: swear freely and often (fuck, shit, damn...), but no slurs and nothing about race, "
                 "religion, gender, sexuality or disability" if mc.explicit else "PG-13, no slurs"),
        name=name or mc.name, persona=mc.persona, skill=mc.skill, performance=note + "\n" if note else "",
        bars=ctx["bars"], mood=ctx["opponent"]["mood"], bar_s=bar_s, max_syl=max(6, round(bar_s * SYLLABLES_PER_S)),
        dims=", ".join(f"{k} {ctx['opponent'][k]:.0f}" for k in ("intimidated", "entertained", "confident", "annoyed", "impressed")),
        crowd_level=ctx["crowd"]["level"], crowd_mood=ctx["crowd"]["mood"],
        opp_score=ctx["score"]["opponent"], user_score=ctx["score"]["user"], hits=hit_txt, outfit=outfit_txt, profile=profile_txt,
    )


# The gendered insults, swapped for plain ones if the LLM uses them anyway (all but MCs with gendered_insults).
_INSULTS = {"bitch": "chump", "bitches": "chumps", "bitchass": "chump", "hoe": "clown", "hoes": "clowns",
            "thot": "clown", "thots": "clowns", "whore": "clown", "whores": "clowns", "slut": "clown", "sluts": "clowns",
            "skank": "clown", "skanks": "clowns", "pussy": "punk", "pussies": "punks", "cunt": "punk", "cunts": "punks",
            "twat": "punk", "twats": "punks", "dick": "jerk", "dicks": "jerks", "dickhead": "jerk", "dickheads": "jerks"}
_INSULT_RE = re.compile(r"\b(" + "|".join(sorted(_INSULTS, key=len, reverse=True)) + r")\b", re.I)


def clean(lines: list[str], mc: mcs.MC) -> list[str]:
    """The verse without gendered insults, unless the MC is one who uses them."""
    if mc.gendered_insults:
        return lines

    def swap(m: re.Match) -> str:
        w = _INSULTS[m.group(0).lower()]
        return w.upper() if m.group(0).isupper() else w.capitalize() if m.group(0)[0].isupper() else w
    return [_INSULT_RE.sub(swap, l) for l in lines]


class Opponent(Protocol):
    async def respond(self, user_verse: str, ctx: dict) -> dict:
        """ctx is Game.state() plus `bars` and `hits` (the user's lines that rattled the opponent most)."""
        ...


def _parse(text: str, bars: int) -> dict:
    """{"interpretation", "lines": [str], "deliveries": [str]}; lines may come back as strings or {text, delivery}."""
    m = re.search(r"\{.*\}", text, re.S)
    if m:
        try:
            data = json.loads(m.group(0))
            lines, deliveries = [], []
            for item in data.get("lines", []):
                line = item.get("text", "") if isinstance(item, dict) else item
                line = str(line).strip()
                if line:
                    lines.append(line)
                    deliveries.append(str(item.get("delivery", "")).strip() if isinstance(item, dict) else "")
            if lines:
                return {"interpretation": data.get("interpretation", ""), "lines": lines[:bars], "deliveries": deliveries[:bars]}
        except (json.JSONDecodeError, AttributeError):
            pass
    lines = [l.strip(" -*\"") for l in text.splitlines() if l.strip()][:bars]
    return {"interpretation": "", "lines": lines, "deliveries": [""] * len(lines)}


class OpenRouterOpponent:
    def __init__(self, api_key: str, model: str, name: str | None = None, timeout_s: float = 20.0):
        self.model = model
        self.name = name
        self.client = httpx.AsyncClient(
            base_url="https://openrouter.ai/api/v1", timeout=timeout_s,
            headers={"Authorization": f"Bearer {api_key}", "X-Title": "Freestyle Simulator"},
        )

    async def respond(self, user_verse: str, ctx: dict) -> dict:
        bars = ctx["bars"]
        body = {
            "model": self.model,
            "messages": [
                {"role": "system", "content": _system(ctx, self.name)},
                {"role": "user", "content": f"My verse:\n{user_verse or '(I said nothing, I froze.)'}"},
            ],
            "temperature": 0.9,
            "reasoning": {"enabled": False},  # thinking adds 10+ s and doesn't help a verse
        }
        resp = await self.client.post("/chat/completions", json=body)
        resp.raise_for_status()
        out = _parse(resp.json()["choices"][0]["message"]["content"], bars)
        out["source"] = f"openrouter:{self.model}"
        return out


_DELIVERY = {"cocky": "cocky, smirking", "amused": "mocking, amused", "calm": "calm and steady",
             "respectful": "measured, grudging respect", "heated": "aggressive, leaning in", "rattled": "defensive, louder"}
_TEMPLATES = {
    "cocky": ["you came up here shaking, I can hear it in your voice", "I ain't even trying and you're still out of choice"],
    "amused": ["I'm laughing so hard that I can barely breathe", "you should take that act and go perform it on the street"],
    "calm": ["alright you got a line in, I'll give you that one", "but the battle's a marathon and I've barely begun"],
    "respectful": ["ok I respect it, you came with some heat", "but respect don't mean nothing when you're under my feet"],
    "heated": ["you wanna get personal? step a little closer", "I'll fold you like a letter and I'll stamp you like a poster"],
    "rattled": ["hold up, hold up, let me get my thoughts straight", "you caught me slipping but it's never too late"],
}


class TemplateOpponent:
    async def respond(self, user_verse: str, ctx: dict) -> dict:
        mood, bars = ctx["opponent"]["mood"], ctx["bars"]
        pool = _TEMPLATES.get(mood, _TEMPLATES["calm"])
        words = [w for w in re.findall(r"[a-z']{4,}", user_verse.lower())]
        callback = f"you said '{random.choice(words)}' like that's supposed to hurt" if words else "you froze up on the mic, now that's gotta hurt"
        lines = [callback, "I'm the one they came to see, I'm the one that does the work"]
        mc = mcs.get(ctx.get("mc"))
        choking = ctx["opponent"]["confident"] < mc.choke_below
        if choking:
            lines.insert(1, "uhh... yeah... hold up, uh")
        while len(lines) < bars:
            lines.extend(pool)
        return {"interpretation": f"template reply, mood {mood}", "lines": lines[:bars],
                "deliveries": ["hesitant, stumbling" if l.startswith("uhh") else _DELIVERY.get(mood, "") for l in lines[:bars]],
                "source": "template"}


def make(settings) -> Opponent:
    if settings.resolved_opponent() == "openrouter":
        return OpenRouterOpponent(settings.openrouter_api_key, settings.opponent_model)
    return TemplateOpponent()
