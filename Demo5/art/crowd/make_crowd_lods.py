"""Distance tiers for a stadium crowd, from the same drawings as the near-tier sheets.

    python art/crowd/make_crowd_lods.py        (needs Inkscape; run make_crowd_sprites.py first for the near tier)

mid/crowd_mid_atlas.png   every type x mood x frame in one texture for one instanced draw call:
                          columns = mood 0..11, rows = type x frame (beanie A, beanie B, afro A, ...), MID_W x MID_H cells.
far/strip_<set>_<A|B>.png horizontally tileable strips of tiny silhouettes in two staggered rows, for the upper
                          stands. Three pose sets (calm / into_it / wild); bounce them in a shader with amplitude from
                          the crowd level, phase per slot (see far/strips.json).
"""
from __future__ import annotations

import json
import random
import subprocess
from pathlib import Path

import make_crowd_sprites as base

base.EXTRAS = False   # decorations become noise at these sizes

OUT = Path(__file__).resolve().parent
MID_W, MID_H = 64, 96
STRIP_W, STRIP_H = 2048, 192
SLOT = 32                                   # front-row figure slot width (px): shader phase per slot
ROWS = [  # (baseline y, figure height px, slot width, x offset): back row higher, smaller, offset half a slot
    dict(y=128, h=70, slot=SLOT, dx=SLOT / 2),   # back row: its bodies sit behind the front row's heads
    dict(y=184, h=84, slot=SLOT, dx=0),
]
SETS = {  # pose set -> moods drawn (mixed so a section never looks cloned)
    "calm": [0, 1, 1, 2, 3, 3, 4, 5],
    "into_it": [4, 5, 5, 6, 6, 7, 7, 8],
    "wild": [7, 8, 8, 9, 9, 10, 11, 11],
}


def placed(c, mood: int, frame: int, x: float, y: float, h: float, mirror: bool = False) -> str:
    """One crowd member with its stand foot at (x, y), `h` px tall (the cell's full height)."""
    s = h / base.CELL_H
    px, py = base.PIVOT
    flip = f" translate({px},0) scale(-1,1) translate({-px},0)" if mirror else ""
    inner = (f'<g transform="translate({px},{py}) scale({base.SCALE}) translate({-px},{-py}){flip}">'
             f"{base.cell(c, mood, frame)}</g>")
    return f'<g transform="translate({x - px * s:.2f},{y - py * s:.2f}) scale({s:.4f})">{inner}</g>'


def svg(w: int, h: int, body: str) -> str:
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">\n{body}\n</svg>\n'


def rasterize(src: Path, dst: Path):
    subprocess.run(["inkscape", str(src), "--export-type=png", f"--export-filename={dst}"], check=True, capture_output=True)


def mid_atlas():
    out = OUT / "mid"
    out.mkdir(exist_ok=True)
    cells = []
    for t, c in enumerate(base.CHARACTERS):
        for frame in range(2):
            row = t * 2 + frame
            for mood in range(12):
                cells.append(placed(c, mood, frame, mood * MID_W + MID_W / 2, row * MID_H + MID_H * base.PIVOT[1] / base.CELL_H,
                                    MID_H))
    w, h = 12 * MID_W, len(base.CHARACTERS) * 2 * MID_H
    (out / "crowd_mid_atlas.svg").write_text(svg(w, h, "\n".join(cells)))
    rasterize(out / "crowd_mid_atlas.svg", out / "crowd_mid_atlas.png")
    meta = {"cell": [MID_W, MID_H], "columns": 12, "rows": len(base.CHARACTERS) * 2, "moods": base.MOODS,
            "row_order": [f"{c.name}_{f}" for c in base.CHARACTERS for f in "AB"],
            "pivot": [MID_W / 2, MID_H * base.PIVOT[1] / base.CELL_H],
            "use": "column = round(crowd level); row = type*2 + frame; flip frame on the beat with a per-instance offset"}
    (out / "atlas.json").write_text(json.dumps(meta, indent=1))
    print("wrote mid/crowd_mid_atlas.png", f"{w}x{h}")


def strips():
    out = OUT / "far"
    out.mkdir(exist_ok=True)
    meta = {"size": [STRIP_W, STRIP_H], "tileable": "horizontally", "slot_px": SLOT,
            "rows": [{"baseline_y": r["y"], "figure_px": r["h"], "x_offset": r["dx"]} for r in ROWS],
            "sets": {}, "shader": (
                "Tile along the stands. Pick the set from the crowd level (calm 0-3, into_it 4-7, wild 8-11) and swap "
                "A/B on the beat. Bounce: slot = floor(uv.x * size.x / slot_px); phase = hash(slot + section_id); "
                "offset_y = amplitude(crowd level) * max(0, sin(beat_phase * 2pi + phase * 2pi)). Phone lights and "
                "waves can be layered in the same shader.")}
    for name, moods in SETS.items():
        for frame in range(2):
            rng = random.Random(f"{name}")           # same layout for A and B, so only the poses change
            body = []
            for r in ROWS:
                n = int(STRIP_W / r["slot"])
                for i in range(n):
                    c = rng.choice(base.CHARACTERS)
                    mood = rng.choice(moods)
                    own = (frame + rng.randrange(2)) % 2  # half the figures are on the other frame: no lockstep
                    mirror = rng.random() < 0.5
                    x = r["dx"] + i * r["slot"] + r["slot"] / 2 + rng.uniform(-4, 4)
                    y = r["y"] + rng.uniform(-3, 3)
                    h = r["h"] * rng.uniform(0.92, 1.08)
                    for wrap in (-STRIP_W, 0, STRIP_W):   # draw across the seam so the strip tiles
                        if -60 < x + wrap < STRIP_W + 60:
                            body.append(placed(c, mood, own, x + wrap, y, h, mirror))
            src = out / f"strip_{name}_{'AB'[frame]}.svg"
            src.write_text(svg(STRIP_W, STRIP_H, "\n".join(body)))
            rasterize(src, src.with_suffix(".png"))
            src.unlink()   # the PNG is the asset; regenerate from this script
            meta["sets"].setdefault(name, {"moods": moods, "frames": []})["frames"].append(src.with_suffix(".png").name)
            print("wrote", f"far/{src.with_suffix('.png').name}")
    (out / "strips.json").write_text(json.dumps(meta, indent=1))


if __name__ == "__main__":
    mid_atlas()
    strips()
