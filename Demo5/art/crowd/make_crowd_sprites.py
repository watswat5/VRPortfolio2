"""Crowd sprite sheets: faceless solid-black cutouts on stands, PaRappa-style flat shapes.

    python art/crowd/make_crowd_sprites.py            faceless black silhouettes (default)
    python art/crowd/make_crowd_sprites.py --color    flat-colour cardboard cutouts with faces
    python art/crowd/make_crowd_sprites.py --club     for the club venue: near-silhouettes, faceless, with only a hint
                                                      of each figure's colours and a thin dull edge so people in a
                                                      packed room don't merge into one shape
    --human   people-proportioned figures (a head about a sixth of their height, longer body and legs) instead of
              the cartoon ones (a head half their height); combines with the styles above
    --out DIR writes somewhere else (the club sheets live in art/crowd/club: --club --out art/crowd/club)

Needs Inkscape to rasterize. Mood reads from pose alone in the silhouette style: slumped on a
phone (dead) through arms crossed, clapping, nodding, one arm up, both up, jumping, to raise the roof.

For each character: one sheet, 12 columns (crowd mood 0 dead .. 11 raise the roof, the game's crowd
scale) x 2 rows (frames A and B, flipped on the beat), each cell CELL_W x CELL_H with transparency.
Writes <name>.svg (editable source), <name>.png, sheet.json (grid + mood names) and preview.png.
"""
from __future__ import annotations

import json
import math
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

OUT = Path(sys.argv[sys.argv.index("--out") + 1]).resolve() if "--out" in sys.argv else Path(__file__).resolve().parent
CELL_W, CELL_H = 256, 384
MOODS = ["dead", "silent", "restless", "polite", "warming up", "nodding", "into it",
         "cheering", "hyped", "going wild", "losing it", "raise the roof"]

CLUB = "--club" in sys.argv
HUMAN = "--human" in sys.argv
SILHOUETTE = "--color" not in sys.argv and not CLUB
EXTRAS = True            # stars, motion lines, sweat, "!" (off for the distance tiers)
BLACK = "#0b0b0d"
RIM_LIGHT = "#3a3a46"    # thin edge around limbs in the silhouette style
INK = BLACK if SILHOUETTE or CLUB else "#2b1d14"   # outlines (and everything, in the silhouette style)
CLUB_DIM = 0.13          # club style: how much of the clothes' colour shows in the dark (a hint, not a cartoon)
CLUB_SAT = 0.3           # club style: how much of that colour's saturation is left
CLUB_RIM = 3             # club style: a thin dull edge, so people in front of each other don't merge
CLUB_RIM_COLOR = "#26282f"
CARD = "#c9965a"         # cardboard rim
CARD_DARK = "#8a6034"    # cardboard edge / stand
OUTLINE = 5
RIM = 22                 # cardboard margin around the drawing
SCALE = 0.86             # character size in the cell (headroom for jumps, raised arms and stars)
PIVOT = (128, 336)       # stand foot: the sprite's anchor point


@dataclass
class Character:
    name: str
    skin: str
    shirt: str
    pants: str
    accent: str
    hair: str            # hairstyle id
    hair_color: str
    rim: str = "#c9a6ff"  # club style: the colour of the light catching them


CHARACTERS = [
    Character("beanie", "#f3c690", "#e94f37", "#3b4cca", "#ffd23f", "beanie", "#ff8c42", "#ff7ad9"),
    Character("afro", "#8d5524", "#3fa7d6", "#2e2e3a", "#f8f4e3", "afro", "#2b1d14", "#7ae8ff"),
    Character("bucket", "#c68642", "#59cd90", "#5c4033", "#ee6c4d", "bucket", "#f6d55c", "#ffc46b"),
    Character("spiky", "#ffdbac", "#fac05e", "#7b2d8b", "#3fa7d6", "spiky", "#7b2d8b", "#b98cff"),
]


# ---- shapes: drawn three times (cardboard edge, cardboard rim, art) ----------------------------
class Shapes:
    def __init__(self):
        self.items: list[tuple[str, dict]] = []   # (kind, attrs); kind: fill | line | detail

    def fill(self, el: str, color: str, **a):
        self.items.append(("fill", {"el": el, "fill": color, **a}))

    def line(self, pts, color: str, width: float):
        self.items.append(("line", {"pts": pts, "color": color, "width": width}))

    def detail(self, svg: str, sil: str | None = None):
        """Face lines etc., drawn only in the art pass; `sil` is what to draw in the silhouette style (or nothing)."""
        self.items.append(("detail", {"svg": svg, "sil": sil}))


def _attrs(a: dict) -> str:
    return " ".join(f'{k.replace("_", "-")}="{v}"' for k, v in a.items() if k != "el")


def _poly(pts) -> str:
    return " ".join(f"{x:.1f},{y:.1f}" for x, y in pts)


def _dim(color: str, k: float = None) -> str:
    """A colour as it shows in the dark club: much darker, mostly grey, a touch bluer."""
    k = CLUB_DIM if k is None else k
    if not color.startswith("#"):
        return color
    h = color[1:]
    if len(h) == 3:
        h = "".join(ch * 2 for ch in h)
    r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
    grey = (r + g + b) / 3
    r, g, b = (grey + (x - grey) * CLUB_SAT for x in (r, g, b))
    r, g, b = r * k, g * k, b * k * 1.06 + 4
    return "#%02x%02x%02x" % tuple(max(0, min(255, int(v))) for v in (r, g, b))


def render(sh: Shapes, rim: str = "#c9a6ff") -> str:
    rim = CLUB_RIM_COLOR   # one dull edge for everyone (the per-type colours were too loud)
    if CLUB:   # the shapes in their colours, dimmed, a rim of light round the whole figure; no faces
        out = []
        for name in ("rim", "art"):
            g = ["<g>"]
            for kind, a in sh.items:
                if kind == "fill":
                    if name == "rim":
                        g.append(f'<{a["el"]} {_attrs({**a, "fill": rim})} stroke="{rim}" stroke-width="{CLUB_RIM * 2}" '
                                 f'stroke-linejoin="round"/>')
                    else:
                        g.append(f'<{a["el"]} {_attrs({**a, "fill": _dim(a["fill"])})} stroke="{INK}" stroke-width="3" '
                                 f'stroke-linejoin="round"/>')
                elif kind == "line":
                    w = a["width"]
                    if name == "rim":
                        g.append(f'<polyline points="{_poly(a["pts"])}" fill="none" stroke="{rim}" '
                                 f'stroke-width="{w + CLUB_RIM * 2}" stroke-linecap="round" stroke-linejoin="round"/>')
                    else:
                        g.append(f'<polyline points="{_poly(a["pts"])}" fill="none" stroke="{INK}" stroke-width="{w + 6}" '
                                 f'stroke-linecap="round" stroke-linejoin="round"/>')
                        g.append(f'<polyline points="{_poly(a["pts"])}" fill="none" stroke="{_dim(a["color"])}" '
                                 f'stroke-width="{w}" stroke-linecap="round" stroke-linejoin="round"/>')
                elif kind == "detail" and name == "art":
                    # stars, "!", motion lines (there for the movement) and the face, dimmed with the rest
                    k = 0.5   # the eye whites a little less again: any brighter and they stare out of the dark
                    g.append(re.sub(r"#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b",
                                    lambda m: m.group(0) if m.group(0) == INK
                                    else _dim(m.group(0), k * (0.75 if m.group(0).lower() in ("#fff", "#ffffff") else 1)),
                                    a["svg"]))
            g.append("</g>")
            out.append("\n".join(g))
        return "\n".join(out)
    if SILHOUETTE:   # one solid black shape; only details marked for silhouettes survive
        g = ["<g>"]
        for kind, a in sh.items:
            if kind == "fill":
                g.append(f'<{a["el"]} {_attrs({**a, "fill": BLACK})} stroke="{BLACK}" stroke-width="2" stroke-linejoin="round"/>')
            elif kind == "line":   # limbs get a thin rim so arms in front of the body still read
                for color, extra in ((RIM_LIGHT, 10), (BLACK, 3)):
                    g.append(f'<polyline points="{_poly(a["pts"])}" fill="none" stroke="{color}" '
                             f'stroke-width="{a["width"] + extra}" stroke-linecap="round" stroke-linejoin="round"/>')
            elif kind == "detail" and a.get("sil"):
                g.append(a["sil"])
        return "\n".join(g + ["</g>"])
    out = []
    for name, color, grow, dx in (("edge", CARD_DARK, RIM + 8, 5), ("rim", CARD, RIM, 0), ("art", None, 0, 0)):
        g = [f'<g transform="translate({dx},{dx})">'] if dx else ["<g>"]
        for kind, a in sh.items:
            if kind == "fill":
                if name == "art":
                    g.append(f'<{a["el"]} {_attrs({**a})} stroke="{INK}" stroke-width="{OUTLINE}" stroke-linejoin="round"/>')
                else:
                    g.append(f'<{a["el"]} {_attrs({**a, "fill": color})} stroke="{color}" stroke-width="{grow * 2}" '
                             f'stroke-linejoin="round"/>')
            elif kind == "line":
                w = a["width"]
                if name == "art":
                    g.append(f'<polyline points="{_poly(a["pts"])}" fill="none" stroke="{INK}" stroke-width="{w + OUTLINE * 2}" '
                             f'stroke-linecap="round" stroke-linejoin="round"/>')
                    g.append(f'<polyline points="{_poly(a["pts"])}" fill="none" stroke="{a["color"]}" stroke-width="{w}" '
                             f'stroke-linecap="round" stroke-linejoin="round"/>')
                else:
                    g.append(f'<polyline points="{_poly(a["pts"])}" fill="none" stroke="{color}" '
                             f'stroke-width="{w + grow * 2}" stroke-linecap="round" stroke-linejoin="round"/>')
            elif kind == "detail" and name == "art":
                g.append(a["svg"])
        g.append("</g>")
        out.append("\n".join(g))
    return "\n".join(out)


# ---- poses ----------------------------------------------------------------------------------
def arm(shoulder, angles, lengths=(40, 38)):
    """Shoulder -> elbow -> hand; angles in degrees, 0 = straight down, positive = outward."""
    (x, y), pts = shoulder, [shoulder]
    side = -1 if shoulder[0] < CELL_W / 2 else 1
    for ang, ln in zip(angles, lengths):
        r = math.radians(ang)
        x, y = x + side * math.sin(r) * ln, y + math.cos(r) * ln
        pts.append((x, y))
    return pts


# per mood, per frame: jump (px up), head tilt (deg), left arm angles, right arm angles, face, extras
POSES = {
    0: [dict(jump=0, tilt=14, la=(10, 120), ra=(5, 0), face="bored", phone=True, slump=8),
        dict(jump=0, tilt=16, la=(10, 125), ra=(5, 0), face="yawn", phone=True, slump=8)],
    1: [dict(jump=0, tilt=0, la=(6, 4), ra=(6, 4), face="blank"),
        dict(jump=0, tilt=0, la=(6, 4), ra=(6, 4), face="blink")],
    2: [dict(jump=0, tilt=-6, la=(25, 120), ra=(25, 120), face="frown", crossed=True),
        dict(jump=0, tilt=6, la=(25, 120), ra=(25, 120), face="frown2", crossed=True, grumble=True)],
    3: [dict(jump=0, tilt=0, la=(20, 110), ra=(20, 110), face="small_smile", clap=True),
        dict(jump=0, tilt=0, la=(30, 80), ra=(30, 80), face="small_smile")],
    4: [dict(jump=0, tilt=-8, la=(10, 6), ra=(30, 130), face="smile"),
        dict(jump=0, tilt=8, la=(10, 6), ra=(30, 135), face="smile")],
    5: [dict(jump=0, tilt=0, nod=10, la=(15, 10), ra=(15, 10), face="happy_closed"),
        dict(jump=0, tilt=0, nod=-4, la=(15, 10), ra=(15, 10), face="happy_closed")],
    6: [dict(jump=0, tilt=-5, la=(15, 10), ra=(150, 175), face="grin"),
        dict(jump=4, tilt=5, la=(15, 10), ra=(120, 150), face="grin")],
    7: [dict(jump=6, tilt=0, la=(145, 165), ra=(145, 165), face="open"),
        dict(jump=2, tilt=0, la=(125, 150), ra=(125, 150), face="open_wide")],
    8: [dict(jump=18, tilt=-6, la=(155, 175), ra=(110, 60), face="yell", lines=True),
        dict(jump=6, tilt=6, la=(110, 60), ra=(155, 175), face="yell", lines=True)],
    9: [dict(jump=22, tilt=-10, la=(165, 180), ra=(95, 120), face="yell_wide", bang=True),
        dict(jump=10, tilt=10, la=(95, 120), ra=(165, 180), face="yell_wide", bang=True)],
    10: [dict(jump=34, tilt=-12, la=(150, 200), ra=(150, 200), face="wild", sweat=True, stars=True),
         dict(jump=14, tilt=12, la=(140, 210), ra=(140, 210), face="wild2", sweat=True, stars=True, head_hands=True)],
    11: [dict(jump=34, tilt=0, face="roar", stars=True, roof=True, lines=True, push=0),
         dict(jump=18, tilt=0, face="roar", stars=True, roof=True, lines=True, push=22)],
}


def face_svg(kind: str, cx: float, cy: float) -> str:
    e = []
    L, R = cx - 20, cx + 20
    ey, my = cy - 6, cy + 24
    dot = lambda x, y, r=6: f'<circle cx="{x}" cy="{y}" r="{r}" fill="{INK}"/>'
    eyeball = lambda x, y: (f'<ellipse cx="{x}" cy="{y}" rx="11" ry="13" fill="#fff" stroke="{INK}" stroke-width="4"/>'
                            + dot(x + 1, y + 2, 5))
    path = lambda d, w=5, fill="none": (f'<path d="{d}" fill="{fill}" stroke="{INK}" stroke-width="{w}" '
                                        'stroke-linecap="round" stroke-linejoin="round"/>')
    mouth_open = lambda w, h, tongue=True: (
        f'<ellipse cx="{cx}" cy="{my + h / 4}" rx="{w}" ry="{h}" fill="#5a1a1a" stroke="{INK}" stroke-width="5"/>'
        + (f'<ellipse cx="{cx}" cy="{my + h * 0.75}" rx="{w * 0.6}" ry="{h * 0.35}" fill="#ff7b7b"/>' if tongue else ""))
    if kind == "bored":
        e += [path(f"M{L-10},{ey} h20"), path(f"M{R-10},{ey} h20"), path(f"M{cx-12},{my} h24")]
    elif kind == "yawn":
        e += [path(f"M{L-10},{ey} h20"), path(f"M{R-10},{ey} h20"), mouth_open(9, 12, False)]
    elif kind == "blank":
        e += [dot(L, ey), dot(R, ey), path(f"M{cx-8},{my} h16")]
    elif kind == "blink":
        e += [path(f"M{L-8},{ey} h16"), path(f"M{R-8},{ey} h16"), path(f"M{cx-8},{my} h16")]
    elif kind in ("frown", "frown2"):
        e += [dot(L, ey + 2), dot(R, ey + 2), path(f"M{L-12},{ey-16} L{L+8},{ey-10}"), path(f"M{R+12},{ey-16} L{R-8},{ey-10}"),
              path(f"M{cx-14},{my+6} Q{cx},{my-6} {cx+14},{my+6}")]
    elif kind == "small_smile":
        e += [dot(L, ey), dot(R, ey), path(f"M{cx-12},{my-2} Q{cx},{my+8} {cx+12},{my-2}")]
    elif kind == "smile":
        e += [eyeball(L, ey), eyeball(R, ey), path(f"M{cx-16},{my-4} Q{cx},{my+12} {cx+16},{my-4}")]
    elif kind == "happy_closed":
        e += [path(f"M{L-10},{ey+2} Q{L},{ey-10} {L+10},{ey+2}"), path(f"M{R-10},{ey+2} Q{R},{ey-10} {R+10},{ey+2}"),
              path(f"M{cx-18},{my-6} Q{cx},{my+14} {cx+18},{my-6}")]
    elif kind == "grin":
        e += [eyeball(L, ey), eyeball(R, ey),
              path(f"M{cx-20},{my-6} Q{cx},{my+20} {cx+20},{my-6} Z", 5, "#fff")]
    elif kind in ("open", "open_wide"):
        e += [eyeball(L, ey - 2), eyeball(R, ey - 2), mouth_open(14 if kind == "open" else 17, 14 if kind == "open" else 18)]
    elif kind in ("yell", "yell_wide"):
        e += [path(f"M{L-10},{ey-4} L{L+8},{ey+2} L{L-10},{ey+8}"), path(f"M{R+10},{ey-4} L{R-8},{ey+2} L{R+10},{ey+8}"),
              mouth_open(19 if kind == "yell" else 22, 20 if kind == "yell" else 24)]
    elif kind in ("wild", "wild2"):
        star = lambda x, y: path(f"M{x},{y-11} L{x+4},{y-3} L{x+12},{y-3} L{x+6},{y+3} L{x+8},{y+11} L{x},{y+6} "
                                 f"L{x-8},{y+11} L{x-6},{y+3} L{x-12},{y-3} L{x-4},{y-3} Z", 3, "#ffd23f")
        e += [star(L, ey), star(R, ey), mouth_open(22, 24)]
    elif kind == "roar":
        e += [path(f"M{L-12},{ey-2} Q{L},{ey-14} {L+12},{ey-2}", 6), path(f"M{R-12},{ey-2} Q{R},{ey-14} {R+12},{ey-2}", 6),
              mouth_open(26, 28)]
    return "\n".join(e)


def hair(sh: Shapes, c: Character, hx: float, hy: float, r: float, tr: str = ""):
    if c.hair == "beanie":
        sh.fill("path", c.hair_color, d=f"M{hx-r-4},{hy-8} Q{hx},{hy-r-92} {hx+r+4},{hy-8} Z", transform=tr)   # slouchy dome
        sh.fill("rect", c.accent, x=hx - r - 8, y=hy - 20, width=2 * r + 16, height=20, rx=8, transform=tr)
        sh.fill("circle", c.accent, cx=hx, cy=hy - r - 36, r=13, transform=tr)
    elif c.hair == "afro":
        for dx, dy, rr in ((-34, -30, 34), (0, -48, 36), (34, -30, 34), (-48, 0, 28), (48, 0, 28)):
            sh.fill("circle", c.hair_color, cx=hx + dx, cy=hy + dy, r=rr, transform=tr)
    elif c.hair == "bucket":
        sh.fill("path", c.hair_color, d=f"M{hx-r+4},{hy-26} Q{hx},{hy-r-46} {hx+r-4},{hy-26} Z", transform=tr)
        sh.fill("ellipse", c.accent, cx=hx, cy=hy - 26, rx=r + 22, ry=11, transform=tr)
    elif c.hair == "spiky":
        pts = []
        for i in range(7):
            a = math.radians(-160 + i * 23.3)
            pts += [(hx + math.cos(a) * (r - 6), hy + math.sin(a) * (r - 6)),
                    (hx + math.cos(a + 0.2) * (r + 30), hy + math.sin(a + 0.2) * (r + 30))]
        sh.fill("polygon", c.hair_color, points=_poly(pts + [(hx + r - 6, hy - 6), (hx - r + 6, hy - 6)]), transform=tr)


def cell(c: Character, mood: int, frame: int) -> str:
    p = POSES[mood][frame]
    sh = Shapes()
    # proportions: the cartoon (a head half the height) or people (--human: a head about a sixth of it)
    if HUMAN:
        leg, torso, sw, hw, limb, foot, arm_len, hk = 104, 96, 30, 24, 14, 12, (52, 48), 0.42
    else:
        leg, torso, sw, hw, limb, foot, arm_len, hk = 44, 80, 46, 52, 20, 18, (40, 38), 1.0
    jump = p.get("jump", 0) * (0.5 if HUMAN else 1)   # (people's arms reach higher: less room to jump in the cell)
    base = (326 if CLUB else 300) - jump   # feet line (the club style has no stand: the feet are on the floor)
    hip = base - leg
    bx, top = CELL_W / 2, hip - torso + 4  # body top
    slump = p.get("slump", 0)
    # legs
    sh.line([(bx - hw * 0.42, hip), (bx - hw * 0.5 - (8 if jump else 0), base)], c.pants, limb + 2)
    sh.line([(bx + hw * 0.42, hip), (bx + hw * 0.5 + (8 if jump else 0), base)], c.pants, limb + 2)
    for s in (-1, 1):
        sh.fill("ellipse", "#222", cx=bx + s * (hw * 0.58 + (8 if jump else 0)), cy=base + 4, rx=foot, ry=foot * 0.55)
    # body
    sh.fill("path", c.shirt, d=f"M{bx-sw},{top+12+slump} Q{bx},{top-4+slump} {bx+sw},{top+12+slump} "
                              f"L{bx+hw},{hip+4} Q{bx},{hip+16} {bx-hw},{hip+4} Z")
    if not HUMAN:   # (on a person-sized chest the stripe reads as a grin)
        sh.detail(f'<path d="M{bx-sw*0.65},{top+torso*0.55} Q{bx},{top+torso*0.55+14} {bx+sw*0.65},{top+torso*0.55}" '
                  f'fill="none" stroke="{c.accent}" stroke-width="8" stroke-linecap="round"/>')
    # arms (drawn after the body so raised arms read)
    shL, shR = (bx - sw + 2, top + 22 + slump), (bx + sw - 2, top + 22 + slump)
    ak = arm_len[0] / 40   # how much longer people's arms are than the cartoon's
    if p.get("clap"):
        la = [shL, (bx - sw - 4, top + 40 + 20 * ak), (bx - 6, top + 26 + 20 * ak)]
        ra = [shR, (bx + sw + 4, top + 40 + 20 * ak), (bx + 6, top + 26 + 20 * ak)]
    elif p.get("crossed"):
        la = [shL, (bx - sw + 6, top + 40 + 22 * ak), (bx + sw * 0.65, top + 30 + 20 * ak)]
        ra = [shR, (bx + sw - 6, top + 40 + 22 * ak), (bx - sw * 0.65, top + 38 + 20 * ak)]
    else:
        la, ra = arm(shL, p.get("la", (10, 5)), arm_len), arm(shR, p.get("ra", (10, 5)), arm_len)   # special poses below
    r = 54                                   # the head is drawn at the cartoon's size and scaled by hk onto the neck
    neck = top + 8 + p.get("nod", 0) * hk + slump
    hx, hy = bx, neck - r                    # where the head is drawn (before scaling)
    hys = neck - r * hk                      # where it ends up
    if "push" in p:   # raise the roof: elbows out, flat palms pushing up above the head
        up = p["push"] * (0.5 if HUMAN else 1)
        reach = r * hk * 2 + 30
        la = [shL, (bx - sw - 36, top - 34 * ak + up), (bx - sw * 0.9, top - reach - 10 * ak + up)]
        ra = [shR, (bx + sw + 36, top - 34 * ak + up), (bx + sw * 0.9, top - reach - 10 * ak + up)]
    if p.get("head_hands"):
        la = [shL, (bx - sw - 28, top - 20), (hx - 46 * hk, hys - 30 * hk)]
        ra = [shR, (bx + sw + 28, top - 20), (hx + 46 * hk, hys - 30 * hk)]
    for a in (la, ra):
        sh.line(a, c.shirt, limb)
        sh.line(a[1:], c.skin, limb - 4)
        if "push" in p:   # flat palm facing up
            sh.fill("rect", c.skin, x=a[-1][0] - 20 * ak * 0.7, y=a[-1][1] - 9, width=40 * ak * 0.7, height=14, rx=6)
        else:
            sh.fill("circle", c.skin, cx=a[-1][0], cy=a[-1][1], r=limb * 0.65)
    if p.get("phone"):
        x, y = la[-1]
        sh.fill("rect", "#3a3a4a", x=x - 12, y=y - 30, width=24, height=40, rx=4)
        glow = f'<rect x="{x-8}" y="{y-26}" width="16" height="28" rx="2" fill="#9be7ff"/>'
        sh.detail(glow, sil=glow)
    # head (tilted as a group in the art pass via transform on details only; outline shapes stay upright)
    # The head and hair turn as one (tilt; a nod tips it forward), so the pose reads without a face.
    rot = p.get("tilt", 0) + (p.get("nod", 0) * 1.6 if SILHOUETTE else 0)
    shrink = f" translate({hx} {neck}) scale({hk}) translate({-hx} {-neck})" if hk != 1 else ""
    tr = f"rotate({rot} {hx} {neck}){shrink}"
    hair_back = c.hair == "afro"
    if hair_back:
        hair(sh, c, hx, hy, r, tr)
    sh.fill("circle", c.skin, cx=hx, cy=hy, r=r, transform=tr)
    if not SILHOUETTE:   # ears read as Mickey-Mouse bumps on a silhouette
        sh.fill("ellipse", c.skin, cx=hx - r + 2, cy=hy + 6, rx=10, ry=14, transform=tr)
        sh.fill("ellipse", c.skin, cx=hx + r - 2, cy=hy + 6, rx=10, ry=14, transform=tr)
    nw = 28 * (0.55 if HUMAN else 1)
    sh.fill("rect", c.skin, x=hx - nw / 2, y=neck - 10 * hk, width=nw, height=24 * hk)   # neck
    if not hair_back:
        hair(sh, c, hx, hy, r, tr)
    sh.detail(f'<g transform="rotate({p.get("tilt", 0)} {hx} {neck}){shrink}">{face_svg(p["face"], hx, hy)}</g>')
    hy = hys   # the extras go round the head where it ended up
    # extras
    extras = []
    if p.get("grumble"):
        extras.append(f'<path d="M{hx+60},{hy-60} q8,-10 16,0 q8,10 16,0 q8,-10 16,0" fill="none" stroke="{INK}" stroke-width="4"/>')
    if p.get("lines"):
        for dx in (-90, 90):
            extras.append(f'<path d="M{bx+dx},{base-10} v26 M{bx+dx*0.85},{base-4} v20" stroke="{INK}" stroke-width="4" '
                          'stroke-linecap="round"/>')
    if p.get("bang"):
        extras.append(f'<text x="{hx+64}" y="{hy-48}" font-family="Arial Black,sans-serif" font-size="44" '
                      f'fill="#ff3b3b" stroke="{INK}" stroke-width="3">!</text>')
    if p.get("sweat"):
        for sx, sy in ((hx - 72, hy - 40), (hx + 70, hy - 50)):
            extras.append(f'<path d="M{sx},{sy} q-10,16 0,22 q10,-6 0,-22 z" fill="#7fd3ff" stroke="{INK}" stroke-width="3"/>')
    if p.get("stars"):
        for sx, sy, s in ((bx - 100, hy - 80, 1.0), (bx + 96, hy - 92, 0.8), (bx + 104, top + 30, 0.6)):
            extras.append(f'<path transform="translate({sx},{sy}) scale({s})" d="M0,-16 L5,-5 L16,-5 L7,2 L10,14 L0,7 '
                          f'L-10,14 L-7,2 L-16,-5 L-5,-5 Z" fill="#ffd23f" stroke="{INK}" stroke-width="3"/>')
    if p.get("roof") and not HUMAN:   # (above people's raised palms there's no room left in the cell)
        for dx in (-50, 50):   # push lines above each palm
            extras.append(f'<path d="M{bx+dx-14},{top-150+p["push"]} v-18 M{bx+dx},{top-156+p["push"]} v-22 '
                          f'M{bx+dx+14},{top-150+p["push"]} v-18" stroke="{INK}" stroke-width="4" stroke-linecap="round"/>')
    import re as _re
    if not EXTRAS:   # distance tiers: stars, lines and "!" turn into shimmering pixels
        extras = []
    for x in extras:
        sil = _re.sub(r'fill="#(?!000)[0-9a-fA-F]{6}"', f'fill="{BLACK}"', x)
        sh.detail(x, sil=sil)
    # stand: the cardboard strut and a ground shadow, behind everything (none in the club style: people, not cutouts)
    if CLUB:
        return render(sh, c.rim)
    stand = (f'<ellipse cx="{bx}" cy="{340}" rx="70" ry="12" fill="#000" opacity="0.18"/>'
             f'<path d="M{bx-16},{base-30} L{bx-34},{336} L{bx+34},{336} L{bx+16},{base-30} Z" '
             f'fill="{BLACK if SILHOUETTE or CLUB else CARD_DARK}" stroke="{INK}" stroke-width="4" stroke-linejoin="round"/>')
    return stand + render(sh, c.rim)


def sheet_svg(c: Character) -> str:
    cells = []
    px, py = PIVOT
    for frame in range(2):
        for mood in range(12):
            cells.append(f'<g transform="translate({mood * CELL_W},{frame * CELL_H}) translate({px},{py}) '
                         f'scale({SCALE}) translate({-px},{-py})">{cell(c, mood, frame)}</g>')
    w, h = 12 * CELL_W, 2 * CELL_H
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">\n'
            + "\n".join(cells) + "\n</svg>\n")


def main():
    meta = {"cell": [CELL_W, CELL_H], "columns": 12, "rows": 2, "moods": MOODS,
            "layout": "column = crowd mood 0..11 (round(level)), row = frame A/B (flip on the beat)",
            "pivot": [CELL_W / 2, 336], "characters": []}
    for c in CHARACTERS:
        svg = OUT / f"crowd_{c.name}.svg"
        png = OUT / f"crowd_{c.name}.png"
        svg.write_text(sheet_svg(c))
        subprocess.run(["inkscape", str(svg), "--export-type=png", f"--export-filename={png}"],
                       check=True, capture_output=True)
        meta["characters"].append({"name": c.name, "sheet": png.name, "source": svg.name})
        print("wrote", png.name)
    (OUT / "sheet.json").write_text(json.dumps(meta, indent=1))
    # contact sheet: every character, frame A
    from PIL import Image
    sheets = [Image.open(OUT / f"crowd_{c.name}.png") for c in CHARACTERS]
    prev = Image.new("RGBA", (12 * CELL_W, len(sheets) * CELL_H), (245, 236, 220, 255))
    for i, im in enumerate(sheets):
        prev.alpha_composite(im.crop((0, 0, 12 * CELL_W, CELL_H)), (0, i * CELL_H))
    prev.convert("RGB").resize((12 * CELL_W // 2, len(sheets) * CELL_H // 2)).save(OUT / "preview.png")
    print("wrote preview.png")


if __name__ == "__main__":
    main()
