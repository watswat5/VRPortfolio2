"""LED eyes, to match the LED mouth: two 8 x 8 panels per frame (16 x 8 in all, the mouth's size), for blinks, glances
and the opponent's moods and gestures.

    python art/led_mouth/make_led_eyes.py [--color "#38e8ff"]

Each frame is drawn for the eye on the viewer's left (the robot's right) and mirrored for the other, except glances
(both eyes look the same way) and one-eyed frames (skeptical, wink), which give both. Brightness as in the mouth:
'#' full, '+' half, 'o' a third, '.' off.

Writes (next to this script):
  led_eyes_mask.png  one pixel per LED: 5 x 4 frames of 16 x 8 (80 x 32, greyscale = brightness); in each frame the
                     viewer's left eye is the left 8 x 8, the right eye the right 8 x 8
  led_eyes.png       the frames drawn as LED dots, 5 x 4 cells of 256 x 128 px (each eye a 128 x 128 half), white on
                     transparent, unlit dots faint
  eyes.json          layout, frame names, each eye's pixels, and which eyes + mouth to show for each mood and gesture
  eyes_preview.png   every frame, lit, labelled, with a gap between the eyes as on the robot
  face_preview.png   the moods and gestures as whole faces (eyes over mouth)
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = Path(__file__).resolve().parent
E = 8                        # LEDs per eye, each way
COLS, ROWS = 5, 4
PITCH, DOT = 16, 12
LEVEL = {".": 0.0, "#": 1.0, "+": 0.55, "o": 0.3}

NEUTRAL = """
........
..####..
.#....#.
.#.##.#.
.#.##.#.
.#....#.
..####..
........"""

# (name, use, art): art is one eye (mirrored for the other), or ("copy", eye) for both the same, or (left, right).
FRAMES = [
    ("neutral", "calm; idle; listening", NEUTRAL),
    ("half_blink", "blink (in and out)", """
........
........
........
.######.
.#.##.#.
.#....#.
..####..
........"""),
    ("blink", "blink (closed)", """
........
........
........
........
.#....#.
..####..
........
........"""),
    ("look_left", "glance to the viewer's left", ("copy", """
........
..####..
.#....#.
.##...#.
.##...#.
.#....#.
..####..
........""")),
    ("look_right", "glance to the viewer's right", ("copy", """
........
..####..
.#....#.
.#...##.
.#...##.
.#....#.
..####..
........""")),
    ("look_up", "thinking (writing the comeback)", ("copy", """
........
..####..
.#.##.#.
.#.##.#.
.#....#.
.#....#.
..####..
........""")),
    ("look_down", "checking the lyrics; ashamed", ("copy", """
........
..####..
.#....#.
.#....#.
.#.##.#.
.#.##.#.
..####..
........""")),
    ("happy", "amused; laughs", """
........
........
...##...
..#..#..
.#....#.
.#....#.
........
........"""),
    ("cocky", "cocky: half-lidded, looking askance", ("copy", """
........
........
........
.######.
.#....#.
.#..###.
..####..
........""")),
    ("heated", "heated: brows down toward the middle", """
##......
..##....
....##..
........
..####..
.#.##.#.
.#.##.#.
..####.."""),
    ("glare", "glares: brows down, eyes narrowed", """
##......
..##....
....##..
........
.######.
.#.##.#.
..####..
........"""),
    ("rattled", "rattled: brows up in the middle, looking down", """
......##
....##..
..##....
........
..####..
.#....#.
.#.##.#.
..####.."""),
    ("wince", "winces", """
........
.##.....
...##...
.....##.
...##...
.##.....
........
........"""),
    ("surprised", "steps back", """
..####..
.#....#.
#......#
#..##..#
#..##..#
#......#
.#....#.
..####.."""),
    ("impressed", "respectful: wide, pupils big", """
..####..
.#....#.
#.####.#
#.####.#
#.####.#
#.####.#
.#....#.
..####.."""),
    ("skeptical", "shrugs; unimpressed", ("""
........
..####..
.#....#.
.#.##.#.
.#.##.#.
.#....#.
..####..
........""", """
........
........
........
........
.######.
.#.##.#.
..####..
........""")),
    ("wink", "wink (after a punchline)", ("""
........
..####..
.#....#.
.#.##.#.
.#.##.#.
.#....#.
..####..
........""", """
........
........
...##...
..#..#..
.#....#.
........
........
........""")),
    ("focused", "rapping; nods", """
........
........
.######.
.#.##.#.
.#.##.#.
.######.
........
........"""),
    ("defeated", "lost the battle", """
........
.#....#.
..#..#..
...##...
...##...
..#..#..
.#....#.
........"""),
    ("processing", "loading; boot", """
........
........
........
.######.
.++++++.
........
........
........"""),
]

# what to show for the game's opponent moods and gestures: eyes (this sheet) and mouth (led_mouth.png, when not talking)
MOODS = {"calm": ("neutral", "sil"), "cocky": ("cocky", "grin"), "rattled": ("rattled", "frown"),
         "heated": ("heated", "grr"), "respectful": ("impressed", "smile"), "amused": ("happy", "smile")}
GESTURES = {"laughs": ("happy", "grin"), "shrugs": ("skeptical", "smirk"), "nods": ("focused", "smile"),
            "glares": ("glare", "grr"), "winces": ("wince", "frown"), "steps_back": ("surprised", "ou")}


def rows(art: str) -> list[str]:
    r = art.strip("\n").split("\n")
    assert len(r) == E and all(len(x) == E and set(x) <= set(LEVEL) for x in r), r
    return r


def eyes(art) -> tuple[list[str], list[str]]:
    """(viewer's left eye, right eye) rows."""
    if isinstance(art, str):
        left = rows(art)
        return left, [x[::-1] for x in left]
    if art[0] == "copy":
        return rows(art[1]), rows(art[1])
    return rows(art[0]), rows(art[1])


def hex_rgb(s: str) -> tuple[int, int, int]:
    s = s.lstrip("#")
    return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))


def dots(grid: list[str], color=(255, 255, 255), unlit=0.1, glow=False) -> Image.Image:
    """A grid of LEDs as dots, PITCH px apart, on transparent."""
    img = Image.new("RGBA", (len(grid[0]) * PITCH, len(grid) * PITCH), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = (PITCH - DOT) / 2
    for y, row in enumerate(grid):
        for x, c in enumerate(row):
            v = LEVEL[c] or unlit
            if v:
                box = [x * PITCH + pad, y * PITCH + pad, x * PITCH + pad + DOT - 1, y * PITCH + pad + DOT - 1]
                d.rounded_rectangle(box, radius=3, fill=(*color, round(255 * v)))
    if glow:
        img = Image.alpha_composite(img.filter(ImageFilter.GaussianBlur(6)), img)
    return img


def fonts():
    try:
        return ImageFont.truetype("DejaVuSans-Bold.ttf", 20), ImageFont.truetype("DejaVuSans.ttf", 15)
    except OSError:
        return ImageFont.load_default(), ImageFont.load_default()


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--color", default="#38e8ff", help="LED colour for the previews (the sheets are white, to tint)")
    color = hex_rgb(ap.parse_args().color)
    frames = [(name, use, *eyes(art)) for name, use, art in FRAMES]
    assert len(frames) == COLS * ROWS
    names = [f[0] for f in frames]
    mouth = json.loads((OUT / "sheet.json").read_text())
    mouth_names = [f["name"] for f in mouth["frames"]]
    for table in (MOODS, GESTURES):
        assert all(e in names and m in mouth_names for e, m in table.values())

    # sheets
    mask = Image.new("L", (COLS * 2 * E, ROWS * E), 0)
    sheet = Image.new("RGBA", (COLS * 2 * E * PITCH, ROWS * E * PITCH), (0, 0, 0, 0))
    for i, (_, _, left, right) in enumerate(frames):
        cx, cy = i % COLS, i // COLS
        both = [l + r for l, r in zip(left, right)]
        for y, row in enumerate(both):
            for x, c in enumerate(row):
                mask.putpixel((cx * 2 * E + x, cy * E + y), round(255 * LEVEL[c]))
        sheet.alpha_composite(dots(both), (cx * 2 * E * PITCH, cy * E * PITCH))
    mask.save(OUT / "led_eyes_mask.png")
    sheet.save(OUT / "led_eyes.png")

    font, small = fonts()

    # every eye frame, with a gap between the eyes
    ew, gap_eyes, label, gap = E * PITCH, 40, 52, 24
    cw, ch = 2 * ew + gap_eyes, E * PITCH
    prev = Image.new("RGB", (COLS * (cw + gap) + gap, ROWS * (ch + label + gap) + gap), (18, 18, 22))
    d = ImageDraw.Draw(prev)
    for i, (name, use, left, right) in enumerate(frames):
        x = gap + (i % COLS) * (cw + gap)
        y = gap + (i // COLS) * (ch + label + gap)
        for k, eye in enumerate((left, right)):
            ex = x + k * (ew + gap_eyes)
            d.rounded_rectangle([ex - 6, y - 6, ex + ew + 5, y + ch + 5], radius=10, fill=(6, 6, 8))
            img = dots(eye, color, unlit=0.07, glow=True)
            prev.paste(img, (ex, y), img)
        d.text((x + cw / 2, y + ch + 16), f"{i} {name}", font=font, fill=(235, 235, 235), anchor="mm")
        d.text((x + cw / 2, y + ch + 38), use, font=small, fill=(150, 150, 160), anchor="mm")
    prev.save(OUT / "eyes_preview.png")

    # whole faces for the moods and gestures: eyes over the mouth on a face plate
    faces = [("mood", k, *v) for k, v in MOODS.items()] + [("gesture", k, *v) for k, v in GESTURES.items()]
    mw = 16 * PITCH
    fw, fh = 2 * ew + gap_eyes + 40, ch + 30 + 8 * PITCH + 40
    fcols = 6
    face = Image.new("RGB", (fcols * (fw + gap) + gap, 2 * (fh + label + gap) + gap), (18, 18, 22))
    d = ImageDraw.Draw(face)
    for j, (kind, key, en, mn) in enumerate(faces):
        x = gap + (j % fcols) * (fw + gap)
        y = gap + (j // fcols) * (fh + label + gap)
        d.rounded_rectangle([x, y, x + fw, y + fh], radius=26, fill=(6, 6, 8))
        _, _, left, right = frames[names.index(en)]
        for k, eye in enumerate((left, right)):
            img = dots(eye, color, unlit=0.06, glow=True)
            face.paste(img, (x + 20 + k * (ew + gap_eyes), y + 20), img)
        img = dots(mouth["frames"][mouth_names.index(mn)]["pixels"], color, unlit=0.06, glow=True)
        face.paste(img, (x + (fw - mw) // 2, y + 20 + ch + 30), img)
        d.text((x + fw / 2, y + fh + 16), f"{key}", font=font, fill=(235, 235, 235), anchor="mm")
        d.text((x + fw / 2, y + fh + 38), f"{kind}: eyes {en}, mouth {mn}", font=small, fill=(150, 150, 160), anchor="mm")
    face.save(OUT / "face_preview.png")

    (OUT / "eyes.json").write_text(json.dumps({
        "sheet": "led_eyes.png", "mask": "led_eyes_mask.png",
        "eye": [E, E], "grid": [2 * E, E], "columns": COLS, "rows": ROWS,
        "cell": [2 * E * PITCH, E * PITCH], "mask_cell": [2 * E, E],
        "layout": f"frame i is at column i % {COLS}, row i // {COLS} (row 0 at the top); in each frame the viewer's left "
                  "eye (the robot's right) is the left half",
        "levels": {"#": 1.0, "+": 0.55, "o": 0.3, ".": 0.0},
        "frames": [{"index": i, "name": name, "use": use,
                    "rect": [(i % COLS) * 2 * E * PITCH, (i // COLS) * E * PITCH, 2 * E * PITCH, E * PITCH],
                    "mask_rect": [(i % COLS) * 2 * E, (i // COLS) * E, 2 * E, E], "left": left, "right": right}
                   for i, (name, use, left, right) in enumerate(frames)],
        "blink": ["half_blink", "blink", "half_blink"],
        "moods": {k: {"eyes": names.index(e), "mouth": mouth_names.index(m)} for k, (e, m) in MOODS.items()},
        "gestures": {k: {"eyes": names.index(e), "mouth": mouth_names.index(m)} for k, (e, m) in GESTURES.items()},
    }, indent=1))
    print("wrote led_eyes_mask.png, led_eyes.png, eyes.json, eyes_preview.png, face_preview.png")


if __name__ == "__main__":
    main()
