"""The spray can's sounds, for SprayCan (unity/Assets/Resources/SprayCan/, loaded by name):

    python art/spraycan/make_sounds.py path/to/freesound_community-shake-spray-can-34899.mp3

  rattle_1..7.wav  single clacks of the mixing ball, cut from the first eight seconds of the shake recording (one
                   slow shake a second there, so each clack stands alone): the ball hitting an end of the can, with
                   its first quick rebounds.
                   SprayCan plays one each time its simulated ball hits an end, louder the faster it hits.
  spray.wav        the aerosol's hiss, a 2 s seamless loop: noise shaped in the frequency domain (so it loops
                   exactly), a broad hiss over 2.5-13 kHz with a little pink noise under it for body, and a slight
                   flutter.
  thunk.wav        the empty-ish aluminium can hitting the floor: a dull thump, the can's own ring (a few
                   inharmonic partials, fast decay) and the click of the impact.

Needs ffmpeg (to decode the mp3) and numpy. The recording: "Shake Spray Can" by freesound_community on Pixabay
(https://pixabay.com/sound-effects/film-special-effects-shake-spray-can-34899/), Pixabay Content License.
"""
import math
import os
import subprocess
import sys
import wave

import numpy as np

SR = 48000
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "unity", "Assets", "Resources", "SprayCan")
CLACKS = [0.57, 1.51, 2.63, 3.705, 4.73, 5.75, 7.785]   # onsets (s) of lone clacks in the recording
CLACK_S = 0.14     # the hit, its ring and the ball's own small rebounds (~80-110 ms on)
rng = np.random.default_rng(34899)


def write(name, x, peak_db):
    x = x / max(1e-9, np.abs(x).max()) * 10 ** (peak_db / 20)
    os.makedirs(OUT, exist_ok=True)
    with wave.open(os.path.join(OUT, name), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    print(f"{name}: {len(x) / SR * 1000:.0f} ms")


def decode(path):
    pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-ac", "1", "-ar", str(SR), "-f", "s16le", "-"],
                         check=True, capture_output=True).stdout
    return np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768


def rattles(x):
    n = int(CLACK_S * SR)
    for k, t in enumerate(CLACKS, 1):
        seg = x[int((t - 0.02) * SR):int((t + 0.05) * SR)]
        start = int((t - 0.02) * SR) + int(np.argmax(np.abs(seg) > 0.15 * np.abs(seg).max())) - int(0.002 * SR)
        c = x[start:start + n].copy()
        c[:48] *= np.linspace(0, 1, 48)                                   # 1 ms in
        tail = int(0.025 * SR)
        c[-tail:] *= 0.5 + 0.5 * np.cos(np.linspace(0, math.pi, tail))   # 25 ms out
        write(f"rattle_{k}.wav", c, -1)


def spray():
    n = 2 * SR
    f = np.fft.rfftfreq(n, 1 / SR)
    fs = np.maximum(f, 1)
    hiss = (fs / 2500) ** 2 / (1 + (fs / 2500) ** 2) / (1 + (fs / 13000) ** 4)   # 2nd-order high-pass, steep low-pass
    pink = 0.12 * np.sqrt(1000 / fs) / (1 + (fs / 9000) ** 2)
    mag = np.where(f < 80, 0, hiss + pink)
    x = np.fft.irfft(mag * np.exp(2j * np.pi * rng.random(len(f))), n)   # periodic: loops without a seam
    t = np.arange(n) / SR
    x *= 1 + 0.06 * np.sin(2 * np.pi * 7 * t) + 0.04 * np.sin(2 * np.pi * 11 * t + 1.3)   # whole cycles in 2 s
    write("spray.wav", x, -6)


def thunk():
    n = int(0.35 * SR)
    t = np.arange(n) / SR
    x = 1.0 * np.sin(2 * np.pi * 140 * t * (1 - 0.15 * t)) * np.exp(-t / 0.035)          # the thump, dropping a little
    for freq, amp, decay in [(690, 0.35, 0.09), (1630, 0.25, 0.07), (2870, 0.18, 0.05), (4310, 0.12, 0.035)]:
        x += amp * np.sin(2 * np.pi * freq * t + rng.random() * 6) * np.exp(-t / decay)     # the can's ring
    click = rng.standard_normal(n) * np.exp(-t / 0.0015)
    x += 0.5 * np.diff(click, prepend=0)                                                    # bright impact click
    x[:24] *= np.linspace(0, 1, 24)
    write("thunk.wav", x, -1)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    rattles(decode(sys.argv[1]))
    spray()
    thunk()
