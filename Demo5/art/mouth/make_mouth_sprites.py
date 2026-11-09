"""Mouth sprite sheet: a flat 2D cartoon mouth, one frame per viseme (mouth shape for speech).

    python art/mouth/make_mouth_sprites.py

Needs Inkscape to rasterize. The frames are Meta's 15 visemes (sil PP FF TH DD kk CH SS nn RR aa E ih oh ou),
the set Meta face tracking reports and AvatarFace already understands, in that order: frame i is viseme i.
sheet.json also maps Rhubarb Lip Sync's cues (A-H, X) and uLipSync's vowels (A I U E O N) onto these frames,
so any of the three can drive the same sheet.

Writes mouth.svg (editable source), mouth.png (5 x 3 cells of CELL px, transparent), sheet.json and
preview.png (labelled).
"""
from __future__ import annotations

import json
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

OUT = Path(__file__).resolve().parent
CELL = 256
COLS, ROWS = 5, 3
CX, CY = CELL / 2, CELL / 2          # mouth centre in each cell: the sprite's pivot

INK = "#2b1d14"        # outlines (same ink as the crowd's colour style)
LIP = "#c8605a"
LIP_DARK = "#a2443f"   # crease between pressed lips
INSIDE = "#4a1418"
TEETH = "#fbf7ef"
TEETH_LINE = "#d9d2c3"
TONGUE = "#e0707a"
OUTLINE = 4            # ink line width
LIP_W = 16             # lip band thickness (pursed shapes get more)


@dataclass
class Tongue:
    x: float           # centre, relative to the mouth centre
    y: float
    rx: float
    ry: float


@dataclass
class Shape:
    name: str
    sounds: str        # what it's for
    words: str         # example words (the sound in capitals)
    note: str          # how it's drawn
    w: float           # mouth width, corner to corner
    up: float          # opening above the corners (0 = closed)
    down: float        # opening below the corners
    round: bool = False                    # elliptical opening (rounded lips) instead of a wide one
    corner: float = 0.0                    # corner height (negative = up, a slight smile)
    lip: float = LIP_W
    top_teeth: float | None = None         # y of the upper teeth's edge, relative to the mouth centre
    bottom_teeth: float | None = None      # y of the lower teeth's edge
    tongue: Tongue | None = None
    lower_lip_extra: float = 0.0           # thicker lower lip (rolled under the teeth for F/V)
    tongue_out: bool = False               # the tongue tip shows between the teeth (TH)
    tags: list[str] = field(default_factory=list)


SHAPES = [
    Shape("sil", "silence, rest", "(between words)", "lips together, relaxed, corners slightly up",
          w=104, up=0, down=0, corner=-5),
    Shape("PP", "P, B, M", "Pop, Bob, Mom", "lips pressed together and pushed out a little",
          w=92, up=0, down=0, lip=22),
    Shape("FF", "F, V", "Fun, Very", "upper teeth resting on the lower lip",
          w=108, up=16, down=6, top_teeth=12, lower_lip_extra=7),
    Shape("TH", "TH", "THink, THis", "slightly open, tongue tip between the teeth",
          w=108, up=16, down=16, top_teeth=-2, bottom_teeth=4, tongue=Tongue(0, 2, 22, 8), tongue_out=True),
    Shape("DD", "T, D", "Top, Dog", "open, teeth apart, tongue tip up behind the top teeth",
          w=112, up=24, down=22, top_teeth=-6, bottom_teeth=9, tongue=Tongue(0, -1, 30, 10)),
    Shape("kk", "K, G", "Kick, Go", "open, tongue humped at the back, low",
          w=112, up=24, down=26, top_teeth=-6, bottom_teeth=16, tongue=Tongue(0, 10, 38, 10)),
    Shape("CH", "CH, J, SH", "CHurch, Jump, SHe", "lips pushed forward and squared, teeth together",
          w=86, up=22, down=22, round=True, lip=20, top_teeth=1, bottom_teeth=1),
    Shape("SS", "S, Z", "Sun, Zoo", "wide, teeth together (a clenched grin)",
          w=124, up=14, down=14, corner=-3, top_teeth=0, bottom_teeth=0),
    Shape("nn", "N, L", "No, Love", "slightly open, tongue up touching the top teeth",
          w=104, up=18, down=18, top_teeth=-3, bottom_teeth=7, tongue=Tongue(0, -1, 28, 8)),
    Shape("RR", "R", "Red, caR", "rounded, a little open, tongue raised in the middle",
          w=80, up=22, down=24, round=True, lip=20, top_teeth=-6, tongue=Tongue(0, 6, 22, 9)),
    Shape("aa", "AH (open vowels)", "fAther, hOt, cUp", "wide open jaw, upper teeth showing, tongue low",
          w=118, up=30, down=50, top_teeth=-10, tongue=Tongue(0, 34, 40, 14)),
    Shape("E", "EH, AE", "bEd, cAt", "open and wide, both rows of teeth",
          w=128, up=24, down=30, corner=-2, top_teeth=-6, bottom_teeth=14, tongue=Tongue(0, 10, 40, 8)),
    Shape("ih", "IH, EE", "bIt, sEE", "wide, slightly open, teeth close together",
          w=122, up=18, down=18, corner=-3, top_teeth=-2, bottom_teeth=5),
    Shape("oh", "OH, AW", "gO, sAW", "round open O",
          w=78, up=34, down=40, round=True, lip=20, top_teeth=-17, tongue=Tongue(0, 26, 24, 10)),
    Shape("ou", "OO, W", "fOOd, Win", "small tight pucker",
          w=48, up=20, down=22, round=True, lip=24),
]

# Other lip-sync sources, mapped onto the frames above.
RHUBARB = {  # Rhubarb Lip Sync's Preston Blair cues
    "A": "PP",   # closed: M B P
    "B": "SS",   # slightly open, clenched teeth: most consonants (K S T EE)
    "C": "E",    # open: EH AE
    "D": "aa",   # wide open: AA
    "E": "oh",   # slightly rounded: AO ER
    "F": "ou",   # puckered: UW OW W
    "G": "FF",   # F V
    "H": "nn",   # L (tongue up)
    "X": "sil",  # idle
}
ULIPSYNC = {"A": "aa", "I": "ih", "U": "ou", "E": "E", "O": "oh", "N": "nn", "-": "sil"}


# ---- drawing --------------------------------------------------------------------------------

def opening(s: Shape) -> str:
    """Path of the mouth opening (or, closed, the line between the lips)."""
    l, r, cy = CX - s.w / 2, CX + s.w / 2, CY + s.corner
    if s.up == 0 and s.down == 0:   # closed: one curve, dipping in the middle when the corners are up
        sag = -s.corner * 0.8
        return f"M {l:.1f} {cy:.1f} C {CX - s.w * .3:.1f} {CY + sag:.1f} {CX + s.w * .3:.1f} {CY + sag:.1f} {r:.1f} {cy:.1f}"
    if s.round:   # two half-ellipses: top and bottom can differ
        return (f"M {l:.1f} {cy:.1f} A {s.w / 2:.1f} {s.up:.1f} 0 0 1 {r:.1f} {cy:.1f} "
                f"A {s.w / 2:.1f} {s.down:.1f} 0 0 1 {l:.1f} {cy:.1f} Z")
    # wide: flatter curves that pinch into the corners (control points reach ~4/3 of the visible height)
    k = 4 / 3
    return (f"M {l:.1f} {cy:.1f} C {CX - s.w * .32:.1f} {CY - s.up * k:.1f} {CX + s.w * .32:.1f} {CY - s.up * k:.1f} {r:.1f} {cy:.1f} "
            f"C {CX + s.w * .32:.1f} {CY + s.down * k:.1f} {CX - s.w * .32:.1f} {CY + s.down * k:.1f} {l:.1f} {cy:.1f} Z")


def mouth_svg(s: Shape, i: int, ox: float, oy: float) -> str:
    """One frame, drawn in cell coordinates and moved to (ox, oy)."""
    d = opening(s)
    closed = s.up == 0 and s.down == 0
    clip = f"clip{i}"
    out = [f'<g id="{s.name}" transform="translate({ox},{oy})">']
    # lips: an ink band, then the lip colour on top, both following the opening
    out.append(f'<path d="{d}" fill="none" stroke="{INK}" stroke-width="{s.lip + 2 * OUTLINE}" '
               f'stroke-linejoin="round" stroke-linecap="round"/>')
    out.append(f'<path d="{d}" fill="none" stroke="{LIP}" stroke-width="{s.lip}" '
               f'stroke-linejoin="round" stroke-linecap="round"/>')
    if closed:
        out.append(f'<path d="{d}" fill="none" stroke="{LIP_DARK if s.name == "PP" else INK}" '
                   f'stroke-width="{OUTLINE}" stroke-linecap="round"/>')
        out.append("</g>")
        return "\n".join(out)
    # inside of the mouth, with teeth and tongue clipped to it
    out.append(f'<clipPath id="{clip}"><path d="{d}"/></clipPath>')
    out.append(f'<path d="{d}" fill="{INSIDE}"/>')
    out.append(f'<g clip-path="url(#{clip})">')
    if s.tongue:
        t = s.tongue
        out.append(f'<ellipse cx="{CX + t.x:.1f}" cy="{CY + t.y:.1f}" rx="{t.rx}" ry="{t.ry}" fill="{TONGUE}"/>')
    if s.top_teeth is not None:
        y = CY + s.top_teeth
        out.append(f'<rect x="0" y="0" width="{CELL}" height="{y:.1f}" fill="{TEETH}"/>')
        out.append(f'<line x1="0" y1="{y:.1f}" x2="{CELL}" y2="{y:.1f}" stroke="{TEETH_LINE}" stroke-width="2"/>')
    if s.bottom_teeth is not None:
        y = CY + s.bottom_teeth
        out.append(f'<rect x="0" y="{y:.1f}" width="{CELL}" height="{CELL - y:.1f}" fill="{TEETH}"/>')
        out.append(f'<line x1="0" y1="{y:.1f}" x2="{CELL}" y2="{y:.1f}" stroke="{TEETH_LINE}" stroke-width="2"/>')
    out.append("</g>")
    # the opening's ink edge, over the teeth
    out.append(f'<path d="{d}" fill="none" stroke="{INK}" stroke-width="{OUTLINE}" stroke-linejoin="round"/>')
    if s.tongue_out:        # TH: the tip pokes out over the lower teeth
        out.append(f'<ellipse cx="{CX}" cy="{CY + s.down * .55:.1f}" rx="{s.w * .17:.1f}" ry="9" '
                   f'fill="{TONGUE}" stroke="{INK}" stroke-width="{OUTLINE}"/>')
    if s.lower_lip_extra:   # F/V: the lower lip rolls up under the upper teeth
        ly = CY + s.down + s.lower_lip_extra * .4
        out.append(f'<ellipse cx="{CX}" cy="{ly:.1f}" rx="{s.w * .4:.1f}" ry="{s.lower_lip_extra + 5:.1f}" '
                   f'fill="{LIP}" stroke="{INK}" stroke-width="{OUTLINE}"/>')
    out.append("</g>")
    return "\n".join(out)


def sheet_svg(labels: bool = False, bg: str | None = None) -> str:
    label_h = 44 if labels else 0
    h = ROWS * (CELL + label_h)
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{COLS * CELL}" height="{h}" viewBox="0 0 {COLS * CELL} {h}">']
    if bg:
        out.append(f'<rect width="100%" height="100%" fill="{bg}"/>')
    for i, s in enumerate(SHAPES):
        ox, oy = (i % COLS) * CELL, (i // COLS) * (CELL + label_h)
        out.append(mouth_svg(s, i, ox, oy))
        if labels:
            out.append(f'<text x="{ox + CELL / 2}" y="{oy + CELL + 4}" text-anchor="middle" font-family="sans-serif" '
                       f'font-size="26" font-weight="bold" fill="{INK}">{i} {s.name}</text>')
            out.append(f'<text x="{ox + CELL / 2}" y="{oy + CELL + 32}" text-anchor="middle" font-family="sans-serif" '
                       f'font-size="20" fill="{INK}">{s.words}</text>')
    out.append("</svg>")
    return "\n".join(out)


def rasterize(svg: Path, png: Path) -> None:
    subprocess.run(["inkscape", str(svg), "--export-type=png", f"--export-filename={png}"],
                   check=True, capture_output=True)


def main():
    svg, png = OUT / "mouth.svg", OUT / "mouth.png"
    svg.write_text(sheet_svg())
    rasterize(svg, png)
    print("wrote", png.name)

    names = [s.name for s in SHAPES]
    meta = {
        "sheet": png.name, "source": svg.name,
        "cell": [CELL, CELL], "columns": COLS, "rows": ROWS,
        "layout": "frame i is at column i % 5, row i // 5 (row 0 at the top); i = Meta viseme index",
        "pivot": [CX, CY],
        "frames": [{"index": i, "name": s.name, "sounds": s.sounds, "words": s.words, "drawing": s.note,
                    "rect": [(i % COLS) * CELL, (i // COLS) * CELL, CELL, CELL]} for i, s in enumerate(SHAPES)],
        "rhubarb": {cue: names.index(v) for cue, v in RHUBARB.items()},
        "ulipsync": {ph: names.index(v) for ph, v in ULIPSYNC.items()},
    }
    (OUT / "sheet.json").write_text(json.dumps(meta, indent=1))
    print("wrote sheet.json")

    tmp = OUT / "_preview.svg"
    tmp.write_text(sheet_svg(labels=True, bg="#f5ecdc"))
    rasterize(tmp, OUT / "preview.png")
    tmp.unlink()
    print("wrote preview.png")


if __name__ == "__main__":
    main()
