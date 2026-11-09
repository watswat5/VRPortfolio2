"""The smoke the stage lasers light up: a real (if small) smoke simulation, in a box that wraps round on every side so
it tiles, recorded as a seamless loop and written for Unity (unity/Assets/Resources/LaserSmoke.bytes; SmokeVolume
loads it, the LaserBeam shader plays it).

    .venv/bin/python art/lasers/make_smoke.py            # about a minute
    .venv/bin/python art/lasers/make_smoke.py --preview  # also slices and see-through views to art/lasers/previews

How: Jos Stam's FFT "stable fluids" (Stam 2001), which is periodic by construction. Each step the velocity is
carried along by itself (semi-Lagrangian), stirred by a slowly changing large-scale force (so it stays turbulent
without anything at the edges), made incompressible and lightly damped in Fourier space. Smoke is fed in from a big
soft source that wanders, carried by the flow (which stretches and folds it into the sheets and filaments of real
smoke), and slowly thins out, so the amount in the box holds steady. After it settles, FRAMES frames are recorded and the end crossfaded into
the start, so the loop has no seam in time either.

File: "SMK1", N, FRAMES (int32, little-endian), then FRAMES frames of N+1 slices of N x N bytes (x fastest, then y,
then z; each frame's slice N repeats its slice 0, so filtering across the tile's z edge stays inside the frame).
"""
import os
import struct
import sys

import numpy as np
from scipy.ndimage import map_coordinates

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "unity", "Assets", "Resources", "LaserSmoke.bytes")
PREVIEWS = os.path.join(ROOT, "art", "lasers", "previews")

N = 64             # cells a side
FRAMES = 30        # frames in the loop ((N+1) x FRAMES slices must stay within 2048)
STEPS_PER_FRAME = 3
FADE = 8           # frames crossfaded at the loop point
WARMUP = 360       # steps to settle before recording
SEED = 5

VISCOSITY = 0.0003      # damps the smallest eddies (cells²/step): low, for crisp filaments
TARGET_SPEED = 0.9      # rms speed the stirring keeps up (cells/step)
STIR_MEMORY = 0.97      # how slowly the stirring force changes, per step
FEED = 0.04             # smoke fed in per step, from a big soft source that wanders (the flow folds it into filaments)
THINNING = 0.988        # the share of smoke left after each step


def main(preview: bool) -> None:
    rng = np.random.default_rng(SEED)
    k = np.fft.fftfreq(N) * N
    KX, KY, KZ = np.meshgrid(k, k, k, indexing="ij")
    K2 = KX ** 2 + KY ** 2 + KZ ** 2
    K2[0, 0, 0] = 1.0
    kmag = np.sqrt(K2)
    shell = ((kmag > 1.2) & (kmag < 3.2)).astype(np.float32)   # the big eddies the stirring drives
    damp = np.exp(-VISCOSITY * K2)
    grid = np.indices((N, N, N)).astype(np.float32)

    def project(uh):
        d = (KX * uh[0] + KY * uh[1] + KZ * uh[2]) / K2
        uh[0] -= KX * d
        uh[1] -= KY * d
        uh[2] -= KZ * d
        uh[:, 0, 0, 0] = 0
        return uh

    def noise_force():
        f = (rng.standard_normal((3, N, N, N)) + 1j * rng.standard_normal((3, N, N, N))) * shell
        return project(f)

    def advect(field, u):
        coords = grid - u
        return map_coordinates(field, coords, order=1, mode="grid-wrap")

    u = np.zeros((3, N, N, N), np.float32)
    smoke = np.zeros((N, N, N), np.float32)
    force = noise_force()
    source_shell = ((kmag > 0.5) & (kmag < 2.5)).astype(np.float32)

    def noise_source():
        return (rng.standard_normal((N, N, N)) + 1j * rng.standard_normal((N, N, N))) * source_shell

    source = noise_source()

    def step():
        nonlocal u, smoke, force, source
        # stir: a large-scale force that wanders slowly (an Ornstein-Uhlenbeck process in Fourier space)
        force = STIR_MEMORY * force + np.sqrt(1 - STIR_MEMORY ** 2) * noise_force()
        f = np.fft.ifftn(force, axes=(1, 2, 3)).real.astype(np.float32)
        f *= 0.05 / max(float(np.sqrt((f ** 2).mean())), 1e-6)
        u = u + f
        # carry the flow along itself, then incompressible and damped in Fourier space
        u = np.stack([advect(u[a], u) for a in range(3)])
        uh = project(np.fft.fftn(u, axes=(1, 2, 3)) * damp)
        u = np.fft.ifftn(uh, axes=(1, 2, 3)).real.astype(np.float32)
        rms = float(np.sqrt((u ** 2).sum(0).mean()))
        if rms > TARGET_SPEED:
            u *= TARGET_SPEED / rms
        # smoke: fed in where the big soft source is positive, carried by the flow, thinning out
        source = STIR_MEMORY * source + np.sqrt(1 - STIR_MEMORY ** 2) * noise_source()
        s = np.fft.ifftn(source).real.astype(np.float32)
        smoke = advect(smoke + FEED * np.maximum(s / max(float(s.std()), 1e-6), 0), u) * THINNING

    for s in range(WARMUP):
        step()
        if s % 60 == 0:
            print(f"settling {s}/{WARMUP}", flush=True)
    rec = []
    for fr in range(FRAMES + FADE):
        for _ in range(STEPS_PER_FRAME):
            step()
        rec.append(smoke.copy())
    print("recorded", len(rec), "frames")

    # the loop: the first FADE frames blend from where the recording ran on to where it started
    frames = []
    for i in range(FRAMES):
        if i < FADE:
            w = (i + 0.5) / FADE
            frames.append((1 - w) * rec[FRAMES + i] + w * rec[i])
        else:
            frames.append(rec[i])
    frames = np.stack(frames)
    lo, hi = np.percentile(frames, 3), np.percentile(frames, 99.7)   # from the thinnest smoke (clear air) to the thickest
    frames = np.clip((frames - lo) / (hi - lo), 0, 1) ** 1.5   # sharper: thin smoke darker, thick wisps stand out
    data = (frames * 255 + 0.5).astype(np.uint8)            # (F, x, y, z)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as fh:
        fh.write(b"SMK1" + struct.pack("<ii", N, FRAMES))
        for f in data:
            padded = np.concatenate([f, f[:, :, :1]], axis=2)   # slice N repeats slice 0
            fh.write(np.ascontiguousarray(padded.transpose(2, 1, 0)).tobytes())   # z, y, x: x fastest
    print(f"wrote {OUT}: {os.path.getsize(OUT) / 1e6:.1f} MB, {N}^3 x {FRAMES} frames, mean {data.mean():.0f}/255")

    if preview:
        from PIL import Image
        os.makedirs(PREVIEWS, exist_ok=True)
        tiles = []
        for i in (0, FRAMES // 3, 2 * FRAMES // 3):
            f = frames[i]
            mid = f[:, :, N // 2].T[::-1]                              # a slice through the middle
            seen = 1 - np.exp(-2.5 * f.mean(axis=2)).T[::-1]           # looking through the box
            tiles += [mid, seen]
        sheet = np.concatenate([np.concatenate(tiles[0::2], 1), np.concatenate(tiles[1::2], 1)], 0)
        img = Image.fromarray((np.clip(sheet, 0, 1) * 255).astype(np.uint8)).resize((sheet.shape[1] * 4, sheet.shape[0] * 4), Image.BICUBIC)
        img.save(os.path.join(PREVIEWS, "smoke.png"))
        print("preview:", os.path.join(PREVIEWS, "smoke.png"))


if __name__ == "__main__":
    main("--preview" in sys.argv)
