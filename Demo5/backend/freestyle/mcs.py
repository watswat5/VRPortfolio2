"""The opponents you can pick: one character sheet per MC in characters/<id>.toml.

A sheet holds the MC's background, traits, topics, catchphrases, outfit (what
they wear in the game, so jabs at it can be judged) and sore spots (what riles
them up), plus five 1-10 stats that set the mechanics:

* rep        -> crowd_offset: added to the crowd's reaction to every one of the
                user's chunks (a nobody's crowd gives you the benefit of the doubt,
                a champ's doesn't).
* presence   -> crowd_home: added to the crowd's reaction to the MC's own bars.
* composure  -> opp_gain (how far each user chunk moves the MC's mood),
                resilience (share of the way the mood drifts back to its baseline
                after every chunk) and choke_below (confidence under which the MC's
                verse starts falling apart).
* wordplay   -> skill: how well the MC writes, told to the LLM.
* flow       -> how cleanly the MC's verse rides the beat, as the rater hears it.

A [tuning] table in a sheet overrides any derived number directly.
See characters/_TEMPLATE.toml for the format.
"""
from __future__ import annotations

import tomllib
from dataclasses import dataclass, field
from pathlib import Path

CHARACTERS = Path(__file__).resolve().parent / "characters"
VOICES = Path(__file__).resolve().parents[1] / "voices.toml"   # cloud voice IDs per MC, edited in one place
SKILLS = [  # by wordplay
    (3, "basic: simple one-syllable rhymes, some clumsy lines, leans on cliches"),
    (6, "solid: clean end rhymes, a few multisyllable rhymes, decent punchlines"),
    (8, "sharp: dense multisyllable rhymes, internal rhymes, hard-hitting punchlines"),
    (10, "elite: layered wordplay, long multisyllable chains, flips the user's lines effortlessly"),
]
DEFAULT_MOOD = {"intimidated": 1, "entertained": 4, "confident": 7, "annoyed": 1, "impressed": 2}


@dataclass(frozen=True)
class SoreSpot:
    spot: str
    keywords: tuple[str, ...] = ()


@dataclass(frozen=True)
class MC:
    id: str
    name: str
    difficulty: str
    blurb: str                 # shown in the dropdown
    persona: str               # for the LLM
    skill: str                 # for the LLM: how well it writes at its best
    speaker: str               # Qwen3-TTS voice
    crowd_offset: float
    crowd_home: float
    opp_gain: float
    resilience: float
    choke_below: float
    baseline: dict[str, float] = field(default_factory=dict)   # starting mood, 0-10
    explicit: bool = False
    gendered_insults: bool = False   # may call the user bitch, hoe... (otherwise those are swapped out of its verse)
    voice_style: str = ""
    extra_adlibs: tuple[str, ...] = ()
    voice_description: str = ""   # what the voice sounds like (Breeze TTS designs the MC's voice from it)
    cloud_voices: dict[str, str] = field(default_factory=dict)   # provider -> voice ID ("elevenlabs", "fish")
    # character sheet
    background: str = ""
    traits: tuple[str, ...] = ()
    topics: tuple[str, ...] = ()
    catchphrases: tuple[str, ...] = ()
    sore_spots: tuple[SoreSpot, ...] = ()
    outfit: tuple[str, ...] = ()   # what the MC wears in the game (the user can clown it; the rater knows it's true)
    stats: dict[str, float] = field(default_factory=dict)
    order: int = 50

    @property
    def voice_key(self) -> str:
        """MCs that sound the same share pre-rendered ad-libs."""
        return f"{self.speaker}|{self.voice_style}|{'|'.join(self.extra_adlibs)}"

    @property
    def flow_profile(self) -> dict:
        """Timing metrics the rater sees for the MC's own (synthesized) verse, from the flow stat."""
        f = self.stats.get("flow", 6) / 10
        return {"speech_ratio": 0.85, "pocket": round(0.35 + 0.55 * f, 2), "on_beat": round(0.4 + 0.5 * f, 2),
                "rate_consistency": round(0.35 + 0.55 * f, 2), "dead_air_s": 0.0}

    def public(self, reveal: tuple[str, ...] | list[str] = ()) -> dict:
        """What a client may show: the sheet without the sore spots (those stay hidden until hit), plus the
        numbers the client's game rules need (`tuning`, and `mood`, the MC's starting mood)."""
        return {"id": self.id, "name": self.name, "difficulty": self.difficulty, "blurb": self.blurb,
                "explicit": self.explicit, "background": self.background, "traits": list(self.traits),
                "topics": list(self.topics), "catchphrases": list(self.catchphrases), "outfit": list(self.outfit), "stats": dict(self.stats),
                "sore_spots_total": len(self.sore_spots),
                "sore_spots_found": [s.spot for s in self.sore_spots if s.spot in reveal],
                "tuning": {"crowd_offset": self.crowd_offset, "crowd_home": self.crowd_home,
                           "opp_gain": self.opp_gain, "resilience": self.resilience},
                "mood": dict(self.baseline)}


def _clamp(x: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, x))


def from_sheet(mc_id: str, d: dict) -> MC:
    st = {"wordplay": 5, "flow": 6, "composure": 4, "presence": 5, "rep": 5, **d.get("stats", {})}
    sheet, voice, tune = d.get("sheet", {}), d.get("voice", {}), d.get("tuning", {})
    comp = st["composure"]
    derived = {
        "crowd_offset": round((5 - st["rep"]) / 3, 2),
        "crowd_home": round((st["presence"] - 5) / 5, 2),
        "opp_gain": round(_clamp(1.53 - 0.13 * comp, 0.35, 1.6), 2),
        "resilience": round(_clamp(0.01 + 0.012 * comp, 0.0, 0.3), 3),
        "choke_below": round(_clamp(5.5 - 0.45 * comp, 1.0, 6.0), 2),
    }
    derived.update({k: float(v) for k, v in tune.items() if k in derived})
    skill = sheet.get("skill") or next(text for top, text in SKILLS if st["wordplay"] <= top)
    return MC(
        id=mc_id, name=d["name"], difficulty=d.get("difficulty", ""), blurb=d.get("blurb", ""),
        persona=sheet.get("persona", "a battle rapper"), skill=skill, speaker=voice.get("speaker", "Ryan"),
        baseline={**DEFAULT_MOOD, **{k: float(v) for k, v in d.get("mood", {}).items()}},
        explicit=bool(d.get("explicit", False)), gendered_insults=bool(d.get("gendered_insults", False)), voice_style=voice.get("style", ""),
        voice_description=voice.get("description", ""),
        cloud_voices={k: str(voice[k]) for k in ("elevenlabs", "fish") if voice.get(k)},
        extra_adlibs=tuple(voice.get("adlibs", ())),
        background=sheet.get("background", ""), traits=tuple(sheet.get("traits", ())),
        topics=tuple(sheet.get("topics", ())), catchphrases=tuple(sheet.get("catchphrases", ())),
        sore_spots=tuple(SoreSpot(s["spot"], tuple(k.lower() for k in s.get("keywords", ())))
                         if isinstance(s, dict) else SoreSpot(str(s)) for s in sheet.get("sore_spots", ())),
        outfit=tuple(str(o) for o in sheet.get("outfit", ())),
        stats={k: float(v) for k, v in st.items()}, order=int(d.get("order", 50)), **derived,
    )


def load_voice_ids(path: Path = VOICES) -> dict[str, dict[str, str]]:
    """{mc id: {"elevenlabs": id, "fish": id}} from voices.toml (empty values ignored)."""
    if not path.exists():
        return {}
    with open(path, "rb") as f:
        data = tomllib.load(f)
    return {mc: {k: str(v) for k, v in ids.items() if v} for mc, ids in data.items() if isinstance(ids, dict)}


def load(folder: Path = CHARACTERS, voices: Path = VOICES) -> list[MC]:
    out = []
    voice_ids = load_voice_ids(voices)
    for p in sorted(folder.glob("*.toml")):
        if p.name.startswith("_"):
            continue
        with open(p, "rb") as f:
            try:
                d = tomllib.load(f)
                if p.stem in voice_ids:   # voices.toml wins over IDs written in the sheet itself
                    d.setdefault("voice", {}).update(voice_ids[p.stem])
                out.append(from_sheet(p.stem, d))
            except (tomllib.TOMLDecodeError, KeyError) as e:
                raise ValueError(f"bad character sheet {p.name}: {e}") from e
    return sorted(out, key=lambda m: (m.order, m.name))


ROSTER = load()
BY_ID = {m.id: m for m in ROSTER}
DEFAULT = "mc_static" if "mc_static" in BY_ID else ROSTER[0].id


def get(mc: str | dict | MC | None) -> MC:
    """By id, or from a state dict's {"id": ...}, or an MC passed straight through."""
    if isinstance(mc, MC):
        return mc
    if isinstance(mc, dict):
        mc = mc.get("id")
    return BY_ID.get(mc or DEFAULT, BY_ID[DEFAULT])


def sheet_prompt(mc: MC, riled: list[str] | tuple[str, ...] = ()) -> str:
    """The character sheet, for the opponent LLM."""
    parts = []
    if mc.background:
        parts.append(f"Your background: {mc.background}")
    if mc.traits:
        parts.append(f"Your traits: {', '.join(mc.traits)}.")
    if mc.topics:
        parts.append(f"You usually rap about: {', '.join(mc.topics)}. Weave in at least one of these.")
    if mc.catchphrases:
        parts.append(f"Your catchphrases (use one if it fits naturally): {'; '.join(mc.catchphrases)}.")
    if mc.outfit:
        parts.append(f"What you're wearing on stage: {'; '.join(mc.outfit)}. The user can see it and may clown it; "
                     "if they do, brush it off or flip it back, in character.")
    if mc.sore_spots:
        parts.append(f"Your sore spots (never admit them): {'; '.join(s.spot for s in mc.sore_spots)}.")
    if riled:
        parts.append(f"The user just hit your sore spots: {'; '.join(riled)}. You are riled up about it: "
                     "address it head-on, heated and defensive, and hit back twice as hard.")
    return "\n".join(parts) + ("\n" if parts else "")


def performance_note(dims: dict[str, float], mc: MC, bars: int) -> str:
    """How the MC's current mood should show in the writing itself."""
    conf, intim = dims.get("confident", 5), dims.get("intimidated", 0)
    if conf < mc.choke_below:
        severity = (mc.choke_below - conf) / mc.choke_below          # 0..1
        n = max(1, min(bars // 2, round(1 + severity * bars / 3)))
        return (f"You are rattled and losing your footing, and it must show in the writing: {n} of your {bars} bars "
                "fall apart. Use a weak or forced rhyme, a line that trails off, or a whole bar of stalling filler "
                "like \"uhh... yeah... hold up, uh\". Give those bars the delivery \"hesitant, stumbling\". "
                "The rest of the verse is defensive and less sharp than your best.")
    if conf < mc.choke_below + 2 or intim >= 6:
        return ("You are shaken: one or two rhymes come out a little forced and you repeat an idea once. "
                "You recover by the end.")
    if conf >= 8:
        return "You are in total control: write at the top of your skill and play to the crowd."
    return ""
