"""The opponent's voice: espeak-ng words placed on the beat grid.

Rendering a whole line with a TTS engine gives speech rhythm, not rap rhythm:
it rushes, then leaves a gap before the next bar. Instead each word is
synthesized on its own, its silence trimmed, and placed on 16th-note slots by
syllable count so the line's last syllable lands on beat 4 (long lines fill
the bar). A word that doesn't fit its slots is re-rendered faster.
"""
from __future__ import annotations

import asyncio
import io
import re
import shutil
import wave
from functools import lru_cache

import numpy as np

from . import lyrics

SR = 22_050               # espeak-ng output rate
SLOTS = 16                # 16th notes per 4/4 bar
LAND_SLOT = 12            # beat 4: where short lines put their last syllable
BASE_WPM = 175
MAX_WPM = 450
SILENCE = 0.015           # amplitude below this counts as silence when trimming

available = shutil.which("espeak-ng") is not None


async def _espeak(text: str, wpm: int, pitch: int = 40) -> np.ndarray:
    proc = await asyncio.create_subprocess_exec(
        "espeak-ng", "-v", "en-us", "-s", str(wpm), "-p", str(pitch), "--stdout", text,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
    out, _ = await proc.communicate()
    return np.frombuffer(out[44:], dtype="<i2").astype(np.float32) / 32768.0 if len(out) > 44 else np.zeros(0, np.float32)


def _trim(x: np.ndarray) -> np.ndarray:
    loud = np.nonzero(np.abs(x) > SILENCE)[0]
    return x[max(loud[0] - 40, 0) : loud[-1] + 200] if len(loud) else x[:0]


_cache: dict[tuple[str, int], np.ndarray] = {}


async def _word(word: str, wpm: int) -> np.ndarray:
    key = (word.lower(), wpm)
    if key not in _cache:
        _cache[key] = _trim(await _espeak(word, wpm))
    return _cache[key]


async def _fit(word: str, max_s: float) -> np.ndarray:
    """The word rendered fast enough to fit `max_s` (or as fast as espeak goes)."""
    x = await _word(word, BASE_WPM)
    if len(x) / SR <= max_s:
        return x
    wpm = BASE_WPM
    for _ in range(3):
        wpm = int(min(MAX_WPM, wpm * (len(x) / SR) / max_s * 1.05) // 25 * 25 + 25)  # bucket for the cache
        x = await _word(word, wpm)
        if len(x) / SR <= max_s or wpm >= MAX_WPM:
            break
    return x


@lru_cache(maxsize=4096)
def _syllables(word: str) -> int:
    return lyrics.syllables(word)


def place(n_syllables: list[int]) -> list[float]:
    """Start slot (0..16, fractional) for each word, given its syllable count."""
    total = sum(n_syllables)
    if total <= LAND_SLOT + 1:
        step = 1.0 if total > 7 else 2.0          # 8th notes for short lines, 16ths otherwise
        first = LAND_SLOT - step * (total - 1)
        first = max(0.0, first)
    else:                                         # long line: spread over the whole bar
        step, first = SLOTS / total, 0.0
    starts, pos = [], first
    for n in n_syllables:
        starts.append(pos)
        pos += n * step
    return starts


async def rap_bar(text: str, bpm: float, beats_per_bar: int = 4) -> bytes:
    """One bar of audio (WAV) with the line's words on the 16th grid."""
    bar_s = beats_per_bar * 60.0 / bpm
    slot_s = bar_s / SLOTS
    words = [w for w in re.findall(r"[A-Za-z0-9']+", text)]
    sylls = [_syllables(w) for w in words]
    starts = place(sylls) if words else []
    ends = starts[1:] + [SLOTS + 2]               # a word may ring a little past the bar
    clips = await asyncio.gather(*(_fit(w, max((e - s) * slot_s * 0.95, 0.06)) for w, s, e in zip(words, starts, ends)))
    out = np.zeros(int((bar_s + 0.3) * SR), np.float32)
    for s, x in zip(starts, clips):
        i = int(s * slot_s * SR)
        x = x[: max(len(out) - i, 0)]
        out[i : i + len(x)] += x
    return _wav(out)


async def fit_line(text: str, seconds: float) -> bytes:
    """The original voice: the whole line in one render, sped up or slowed to last about `seconds`."""
    x = await _espeak(text, BASE_WPM)
    if seconds and len(x):
        wpm = int(max(130, min(400, BASE_WPM * (len(x) / SR) / seconds)))
        if abs(wpm - BASE_WPM) > 10:
            x = await _espeak(text, wpm)
    return _wav(x)


async def say(text: str) -> bytes:
    """Natural-rhythm speech (ad-libs)."""
    return _wav(_trim(await _espeak(text, BASE_WPM)))


def _wav(x: np.ndarray) -> bytes:
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    return buf.getvalue()
