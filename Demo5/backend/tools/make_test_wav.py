"""Synthesize a test "freestyle" WAV plus its word timings, no TTS needed.

Each syllable of the lyric becomes a short voiced burst (a buzz through two
vowel formants) placed on a 16th-note grid at the given BPM. --sloppy adds
timing jitter and stalls, so you can check the metrics tell the two apart.

    python tools/make_test_wav.py out/tight.wav --bpm 90
    python tools/make_test_wav.py out/sloppy.wav --bpm 90 --sloppy

Writes out/tight.wav and out/tight.words.json (for TRANSCRIBER=scripted).
"""
from __future__ import annotations

import argparse
import json
import sys
import wave
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from freestyle.lyrics import syllables  # noqa: E402

LYRIC = """
i step up to the mic and the crowd starts to rise
you can see it in my eyes that i came for the prize
every bar that i spit is a strike to your pride
got a flow so cold that you running to hide
you talk a lot of game but you never deliver
my rhymes cut deep like the cold in december
you a rookie in the ring and i'm the main event
every word that you said was a waste of my rent
"""
FORMANTS = [(730, 1090), (270, 2290), (530, 1840), (570, 840), (300, 870), (660, 1720)]


def burst(sr: int, dur: float, f0: float, formants: tuple[int, int], rng) -> np.ndarray:
    n = int(sr * dur)
    t = np.arange(n) / sr
    # Glottal-ish pulse train, then two resonances via a cheap sum of sines at harmonics.
    out = np.zeros(n)
    for h in range(1, 30):
        f = f0 * h
        if f > sr / 2:
            break
        gain = sum(np.exp(-((f - F) ** 2) / (2 * 120.0**2)) for F in formants) + 0.02
        out += gain * np.sin(2 * np.pi * f * t + rng.uniform(0, 6.28))
    env = np.minimum(1, t / 0.015) * np.minimum(1, (dur - t) / 0.04).clip(0)
    out = out * env
    return out / (np.abs(out).max() + 1e-9)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--bpm", type=float, default=90)
    ap.add_argument("--sr", type=int, default=16000)
    ap.add_argument("--sloppy", action="store_true")
    ap.add_argument("--beat-bleed", type=float, default=0.0, help="mix in a quiet kick/hat (0..1)")
    ap.add_argument("--seed", type=int, default=7)
    a = ap.parse_args()
    rng = np.random.default_rng(a.seed)
    sr, beat = a.sr, 60 / a.bpm
    slot = beat / 4
    lead_in = 2 * beat  # rapper comes in on beat 3 of a count-in bar... close enough
    lines = [l.split() for l in LYRIC.strip().splitlines()]
    bar = 4 * beat
    total_s = np.ceil((lead_in + len(lines) * bar + 1) / bar) * bar  # whole bars, so the grid stays aligned
    audio = np.zeros(int(total_s * sr))
    words = []
    for li, line in enumerate(lines):
        bar_t = lead_in + li * 4 * beat
        k = 0  # 16th slot within the bar
        if a.sloppy and rng.random() < 0.35:
            k += int(rng.integers(3, 7))  # late entrance / stall
        for w in line:
            n = syllables(w)
            start = None
            for _ in range(n):
                t = bar_t + k * slot
                if a.sloppy:
                    t += rng.normal(0, slot * 0.3)
                dur = slot * rng.uniform(0.55, 0.8)
                seg = burst(sr, dur, rng.uniform(110, 140), FORMANTS[rng.integers(len(FORMANTS))], rng)
                i = int(t * sr)
                if i < 0 or i + len(seg) >= len(audio):
                    continue
                audio[i : i + len(seg)] += 0.5 * seg
                start = t if start is None else start
                end = t + dur
                k += 1
            if start is not None:
                words.append({"w": w, "s": round(start, 3), "e": round(end, 3)})
            if k >= 16:
                break
    if a.beat_bleed:
        for b in range(int(total_s / beat)):
            i = int(b * beat * sr)
            n = int(0.12 * sr)
            t = np.arange(n) / sr
            kick = np.sin(2 * np.pi * (50 + 80 * np.exp(-t * 30)) * t) * np.exp(-t * 18)
            audio[i : i + n] += a.beat_bleed * 0.4 * kick[: len(audio) - i]
    audio += rng.normal(0, 0.003, len(audio))  # room noise
    pcm = (np.clip(audio, -1, 1) * 32767).astype("<i2")
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(out), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(sr)
        f.writeframes(pcm.tobytes())
    out.with_suffix(".words.json").write_text(json.dumps({"duration": round(float(total_s), 3), "words": words}))
    print(f"wrote {out} ({total_s:.1f}s, {len(words)} words), beat offset 0 ms at {a.bpm} bpm")


if __name__ == "__main__":
    main()
