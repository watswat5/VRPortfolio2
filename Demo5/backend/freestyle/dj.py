"""The DJ: the club's host, who opens each battle. Before the beat: a greeting to the room, the MC's introduction (the
crowd cheers), the player's (the crowd cheers), a "let's go" as the beat starts, then the count into the player's
first turn on its last bar. Then the mic goes down and the DJ works the decks. When the battle's over the mic comes
back up for the winner (the crowd roars).

Every line is pre-rendered (tools/render_dj.py, from LINES below) except the player's rapper name, which only the
headset knows (its save file): it asks GET /dj/name for it, voiced here in the same voice (name_clip) and cached, so a
name is only ever paid for once. Both use voice_for(), so the baked lines and the live name match.

The DJ talks about the MCs by name and never with gendered pronouns or terms (the player is a robot of no gender).
"""
from __future__ import annotations

import asyncio
import hashlib
import io
import logging
import re
import wave
from pathlib import Path

import numpy as np

log = logging.getLogger("freestyle.dj")

NAME = "DJ Crossfade"
CACHE = Path(__file__).resolve().parents[1] / "out" / "dj_cache"

# id -> what the DJ says. Several of a kind: one is picked at random each battle.
GREETINGS = {
    "greet_1": f"What's good, everybody! Welcome to battle night! It's {NAME} on the decks, and you already know how we do it in here!",
    "greet_2": "Make some noise! It's battle night, and tonight somebody's going home humbled!",
    "greet_3": "Yo, yo, yo! Can I get some energy in here? Welcome out to battle night!",
}
# mc_<character id>: one per character sheet; mc_any for anyone without their own
MC_INTROS = {
    "mc_big_poopy_pants": "Uh... somebody wandered in from the birthday party next door. Give it up for... Poop McPants!",
    "mc_lil_nervous": "In this corner: first battle ever, so be nice... or don't! Lil Nervous!",
    "mc_mc_static": "In this corner: five years running the Thursday cypher, a legend in their own mind... MC Static!",
    "mc_loose_cannon": "Security, stay close. Banned from two leagues and back for more... Loose Cannon!",
    "mc_kid_venom": "In this corner: undefeated this season, and the homework's already done on you... Kid Venom!",
    "mc_the_champ": "Everybody on your feet. Twelve-time champion, battle rap royalty... The Champ!",
    "mc_any": "In this corner: tonight's house MC! Give it up!",
}
# the challenger: a lead-in, then the player's name (live); or, with no name, a whole line instead
CHALLENGERS = {
    "ch_1": "And their challenger tonight, put your hands together for...",
    "ch_2": "And stepping up to the mic, the challenger! Make some noise for...",
    "ch_3": "And in the other corner, straight out of the dressing room, it's...",
}
UNNAMED = {
    "anon_1": "And in the other corner, a brand new face! Show the challenger some love!",
    "anon_2": "And their challenger, stepping up for the very first time! Make some noise!",
}
BEGINS = {   # said as the beat comes in
    "go_1": "Alright, alright! Let's get it on!",
    "go_2": "Let's begin! Run that beat!",
    "go_3": "Okay! Here we go!",
}
COUNT = {"count_4": "Four!", "count_3": "Three!", "count_2": "Two!", "count_1": "One!"}   # the last bar before the player
# the result: a lead-in, then the winner (an MC's line, the player's name live, or a challenger line without one);
# a draw is a whole line
WIN_LEADS = {
    "win_lead_1": "Alright, alright, settle down! The crowd has spoken... and your winner is...",
    "win_lead_2": "Okay, okay! What a battle! Your winner tonight...",
    "win_lead_3": "Make some noise one more time! And the winner is...",
}
MC_WINS = {
    "win_mc_big_poopy_pants": "Poop McPants?! Somebody call the birthday party!",
    "win_mc_lil_nervous": "Lil Nervous! First battle ever, and the nerves held up!",
    "win_mc_mc_static": "MC Static! The Thursday cypher legend lives on!",
    "win_mc_loose_cannon": "Loose Cannon! Somebody check on the challenger!",
    "win_mc_kid_venom": "Kid Venom! Still undefeated!",
    "win_mc_the_champ": "Still the champ... The Champ!",
    "win_mc_any": "The house MC! Holding it down!",
}
CHALLENGER_WINS = {   # without the player's name
    "win_you_1": "The challenger! What an upset!",
    "win_you_2": "The new face! Straight out of the dressing room and straight to the top!",
}
DRAWS = {
    "draw_1": "Hold up... it's a tie! Too close to call! Give it up for both of them!",
    "draw_2": "I can't split them! It's a draw! Make some noise for both of these MCs!",
}
LINES = {**GREETINGS, **MC_INTROS, **CHALLENGERS, **UNNAMED, **BEGINS, **COUNT, **WIN_LEADS, **MC_WINS,
         **CHALLENGER_WINS, **DRAWS}
SCRIPT = {"greetings": list(GREETINGS), "mcs": list(MC_INTROS), "challengers": list(CHALLENGERS),
          "unnamed": list(UNNAMED), "begins": list(BEGINS), "count": list(COUNT), "win_leads": list(WIN_LEADS),
          "mc_wins": list(MC_WINS), "challenger_wins": list(CHALLENGER_WINS), "draws": list(DRAWS)}

DIRECTION = ("an energetic, charismatic hip-hop club DJ hosting battle night on the mic, loud and warm, hyping up the "
             "crowd")
SHORT = "excited"   # ElevenLabs v3 reads long directions out loud: a short audio tag instead
NAME_DIRECTION = DIRECTION + "; announcing the name like a ring announcer, drawn out, building to a shout"
FALLBACK = ("elevenlabs/eleven-v3", "TxGEqnHWrfWFTfGW9XjX")   # ElevenLabs "Josh": a known voice, always the same
LEVEL_DB = -16.0   # RMS of the speech, dBFS: every line the same loudness
MAX_NAME = 40


def clean_name(name: str) -> str:
    """A rapper name fit to say: letters, digits, spaces and a little punctuation, one line, cut to size."""
    s = re.sub(r"[^\w .'&$!?-]", " ", str(name or ""), flags=re.UNICODE)
    s = " ".join(s.split())[:MAX_NAME].strip(" .-")
    return s


def voice_for(settings, model: str | None = None):
    """(OpenRouterVoice, voice id, direction, name direction): the DJ's fixed voice, [dj] in voices.toml for the TTS
    model's provider; else ElevenLabs' "Josh" (a provider with no fixed voice picks a new one every line)."""
    from . import mcs
    from .or_voice import OpenRouterVoice
    v = OpenRouterVoice(settings.openrouter_api_key, model or settings.tts_model)
    vid = mcs.load_voice_ids().get("dj", {}).get(v.family)
    if not vid:
        if v.family != "elevenlabs":
            log.warning("no [dj] %s voice in voices.toml: the DJ uses %s (Josh)", v.family, FALLBACK[0])
            v = OpenRouterVoice(settings.openrouter_api_key, FALLBACK[0])
        vid = FALLBACK[1]
    if v.family == "elevenlabs":
        return v, vid, SHORT, SHORT
    return v, vid, DIRECTION, NAME_DIRECTION


def level(x: np.ndarray, target_db: float = LEVEL_DB) -> np.ndarray:
    """To target_db RMS over the speech (the quiet left out), with a soft limit."""
    x = np.asarray(x, np.float32)
    if not len(x):
        return x
    frame = 480
    n = len(x) // frame
    if n:
        rms = np.sqrt((x[:n * frame].reshape(n, frame) ** 2).mean(axis=1))
        loud = rms[rms > rms.max() * 0.1]
        r = float(np.sqrt((loud ** 2).mean())) if len(loud) else float(rms.max())
    else:
        r = float(np.sqrt((x ** 2).mean()))
    if r <= 1e-6:
        return x
    y = x * (10 ** (target_db / 20) / r)
    return np.tanh(y * 1.1) / np.tanh(1.1)   # soft limit: peaks round off instead of clipping


def render_cloud(settings, text: str, direction: str | None = None, model: str | None = None) -> bytes:
    """One line in the DJ's voice (the billed call), trimmed and levelled, as a WAV."""
    from .qwen_voice import to_wav, trim
    v, vid, d, _ = voice_for(settings, model)
    x, sr = v._speech(direction or d, text, vid)
    return to_wav(level(trim(x, sr=sr)), sr)


def _wav_level(wav: bytes) -> bytes:
    with wave.open(io.BytesIO(wav)) as w:
        sr, ch, raw = w.getframerate(), w.getnchannels(), w.readframes(w.getnframes())
    x = np.frombuffer(raw, np.int16).astype(np.float32) / 32768
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1)
    from .qwen_voice import to_wav
    return to_wav(level(x), sr)


async def name_clip(settings, name: str, cache: Path | None = CACHE) -> bytes | None:
    """The player's rapper name, announced in the DJ's voice: the cloud voice with an OpenRouter key, else espeak-ng,
    else None. Cached on disk by name and voice."""
    from . import voice
    text = clean_name(name)
    if not text:
        return None
    cloud = bool(settings.openrouter_api_key)
    if cloud:
        v, vid, _, nd = voice_for(settings)
        key = f"{v.model}|{vid}|{nd}|{text}"
    else:
        key = f"espeak|{text}"
    p = cache / f"{hashlib.sha1(key.encode()).hexdigest()[:20]}.wav" if cache else None
    if p and p.exists():
        return p.read_bytes()
    try:
        if cloud:
            wav = await asyncio.to_thread(render_cloud, settings, f"{text}!", nd)
        elif voice.available:
            wav = _wav_level(await voice.say(f"{text}!"))
        else:
            return None
    except Exception:
        log.exception("the DJ couldn't say %r", text)
        return None
    if p:
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(wav)
    return wav
