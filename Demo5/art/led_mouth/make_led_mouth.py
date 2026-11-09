"""LED mouth: the robot's mouth as a 16 x 8 panel of pixels, one frame per viseme (mouth shape for speech) plus a few
expressions for its moods.

    python art/led_mouth/make_led_mouth.py [--color "#38e8ff"]

Frames 0-14 are Meta's 15 visemes in Meta's order, like the cartoon mouth (art/mouth), so frame i is viseme i and the
same Rhubarb / uLipSync mappings work. Frames 15-19 are expressions: smile, grin, frown, smirk, grr.

One colour, three brightnesses: lips full ('#'), teeth half ('+'), tongue a third ('o'); '.' is off.

Writes (next to this script):
  led_mouth_mask.png  the frames at one pixel per LED (5 x 4 frames of 16 x 8 = 80 x 32, greyscale = brightness),
                      for a panel material: point filtering, no mipmaps, no compression
  led_mouth.png       the frames drawn as LED dots, 5 x 4 cells of 256 x 128 px, white on transparent (tint it),
                      unlit dots faintly visible
  sheet.json          layout, frame names, every frame's pixels as text rows, the Rhubarb / uLipSync mappings
  preview.png         every frame, lit in --color on a dark panel, labelled
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = Path(__file__).resolve().parent
W, H = 16, 8                 # LEDs
COLS, ROWS = 5, 4            # frames in the sheets
PITCH = 16                   # px per LED in led_mouth.png
DOT = 12                     # lit dot size within the pitch (px)
LEVEL = {".": 0.0, "#": 1.0, "+": 0.55, "o": 0.3}
UNLIT = 0.1                  # how visible an off LED is in led_mouth.png (alpha)

# (name, sounds, example words, pixels). Lips '#', teeth '+', tongue 'o'.
FRAMES = [
    ("sil", "silence, rest", "(between words)", """
................
................
................
................
.##..........##.
...##########...
................
................"""),
    ("PP", "P, B, M", "Pop, Bob, Mom", """
................
................
................
....########....
....########....
................
................
................"""),
    ("FF", "F, V", "Fun, Very", """
................
................
...##########...
..#++++++++++#..
...##########...
................
................
................"""),
    ("TH", "TH", "THink, THis", """
................
................
..############..
.#+++++oo+++++#.
.#+++++oo+++++#.
..############..
................
................"""),
    ("DD", "T, D", "Top, Dog", """
................
..############..
.#++++++++++++#.
#......oo......#
#..............#
.#++++++++++++#.
..############..
................"""),
    ("kk", "K, G", "Kick, Go", """
................
..############..
.#++++++++++++#.
#..............#
#....oooooo....#
.#..oooooooo..#.
..############..
................"""),
    ("CH", "CH, J, SH", "CHurch, Jump, SHe", """
................
.....######.....
....#......#....
....#++++++#....
....#......#....
.....######.....
................
................"""),
    ("SS", "S, Z", "Sun, Zoo", """
................
................
.##############.
#++++++++++++++#
#++++++++++++++#
.##############.
................
................"""),
    ("nn", "N, L", "No, Love", """
................
................
..############..
.#++++++++++++#.
.#....oooo....#.
..############..
................
................"""),
    ("RR", "R", "Red, caR", """
................
.....######.....
....#++++++#....
...#..oooo..#...
...#........#...
....#......#....
.....######.....
................"""),
    ("aa", "AH (open vowels)", "fAther, hOt, cUp", """
..############..
.#++++++++++++#.
#..............#
#..............#
#..............#
.#...oooooo...#.
..#oooooooooo#..
...##########..."""),
    ("E", "EH, AE", "bEd, cAt", """
................
.##############.
#++++++++++++++#
#..............#
#.....oooo.....#
#++++++++++++++#
.##############.
................"""),
    ("ih", "IH, EE", "bIt, sEE", """
................
................
.##############.
#++++++++++++++#
#..............#
.##############.
................
................"""),
    ("oh", "OH, AW", "gO, sAW", """
.....######.....
....#++++++#....
...#........#...
...#........#...
...#..oooo..#...
...#.oooooo.#...
....#......#....
.....######....."""),
    ("ou", "OO, W", "fOOd, Win", """
................
................
......####......
.....#....#.....
.....#....#.....
......####......
................
................"""),
    # expressions
    ("smile", "expression: pleased", "", """
................
................
#..............#
.#............#.
..##........##..
....########....
................
................"""),
    ("grin", "expression: cocky, a punchline landed", "", """
................
################
#++++++++++++++#
.#............#.
..#..........#..
...##########...
................
................"""),
    ("frown", "expression: hurt, losing", "", """
................
................
....########....
..##........##..
.#............#.
#..............#
................
................"""),
    ("smirk", "expression: unimpressed", "", """
................
................
..............#.
.............#..
...##########...
.##.............
................
................"""),
    ("grr", "expression: annoyed, gritted teeth", "", """
................
.##############.
#+.+.+.+.+.+.+.#
#.+.+.+.+.+.+.+#
.##############.
................
................
................"""),
]


def pixels(art: str) -> list[str]:
    rows = [r for r in art.strip("\n").split("\n")]
    assert len(rows) == H and all(len(r) == W and set(r) <= set(LEVEL) for r in rows), rows
    return rows


def hex_rgb(s: str) -> tuple[int, int, int]:
    s = s.lstrip("#")
    return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))


def draw_frame(rows: list[str], color=(255, 255, 255), unlit=UNLIT, glow=False) -> Image.Image:
    """One frame as LED dots, PITCH px apart, on transparent."""
    cw, ch = W * PITCH, H * PITCH
    img = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = (PITCH - DOT) / 2
    for y, row in enumerate(rows):
        for x, c in enumerate(row):
            box = [x * PITCH + pad, y * PITCH + pad, x * PITCH + pad + DOT - 1, y * PITCH + pad + DOT - 1]
            v = LEVEL[c]
            if v > 0:
                d.rounded_rectangle(box, radius=3, fill=(*color, round(255 * v)))
            elif unlit:
                d.rounded_rectangle(box, radius=3, fill=(*color, round(255 * unlit)))
    if glow:   # a soft bloom, as on a real panel
        bloom = img.filter(ImageFilter.GaussianBlur(6))
        img = Image.alpha_composite(bloom, img)
    return img


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--color", default="#38e8ff", help="LED colour for preview.png (the sheets are white, to tint)")
    args = ap.parse_args()
    color = hex_rgb(args.color)
    frames = [(name, sounds, words, pixels(art)) for name, sounds, words, art in FRAMES]
    assert len(frames) == COLS * ROWS

    mask = Image.new("L", (COLS * W, ROWS * H), 0)
    sheet = Image.new("RGBA", (COLS * W * PITCH, ROWS * H * PITCH), (0, 0, 0, 0))
    for i, (_, _, _, rows) in enumerate(frames):
        cx, cy = i % COLS, i // COLS
        for y, row in enumerate(rows):
            for x, c in enumerate(row):
                mask.putpixel((cx * W + x, cy * H + y), round(255 * LEVEL[c]))
        sheet.alpha_composite(draw_frame(rows), (cx * W * PITCH, cy * H * PITCH))
    mask.save(OUT / "led_mouth_mask.png")
    sheet.save(OUT / "led_mouth.png")

    # preview: lit frames on a dark panel, labelled
    cw, ch, label = W * PITCH, H * PITCH, 52
    gap = 24
    prev = Image.new("RGB", (COLS * (cw + gap) + gap, ROWS * (ch + label + gap) + gap), (18, 18, 22))
    d = ImageDraw.Draw(prev)
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", 20)
        small = ImageFont.truetype("DejaVuSans.ttf", 15)
    except OSError:
        font = small = ImageFont.load_default()
    for i, (name, sounds, words, rows) in enumerate(frames):
        x = gap + (i % COLS) * (cw + gap)
        y = gap + (i // COLS) * (ch + label + gap)
        d.rounded_rectangle([x - 6, y - 6, x + cw + 5, y + ch + 5], radius=10, fill=(6, 6, 8))
        prev.paste(draw_frame(rows, color, unlit=0.07, glow=True), (x, y), draw_frame(rows, color, unlit=0.07, glow=True))
        d.text((x + cw / 2, y + ch + 16), f"{i} {name}", font=font, fill=(235, 235, 235), anchor="mm")
        d.text((x + cw / 2, y + ch + 38), words or sounds.replace("expression: ", ""), font=small, fill=(150, 150, 160), anchor="mm")
    prev.save(OUT / "preview.png")

    cartoon = json.loads((OUT.parent / "mouth" / "sheet.json").read_text())
    (OUT / "sheet.json").write_text(json.dumps({
        "sheet": "led_mouth.png", "mask": "led_mouth_mask.png",
        "grid": [W, H], "columns": COLS, "rows": ROWS,
        "cell": [W * PITCH, H * PITCH], "mask_cell": [W, H],
        "layout": f"frame i is at column i % {COLS}, row i // {COLS} (row 0 at the top); frames 0-14 = Meta viseme index",
        "levels": {"#": "lips, 1.0", "+": "teeth, 0.55", "o": "tongue, 0.3", ".": "off"},
        "frames": [{"index": i, "name": name, "sounds": sounds, "words": words,
                    "rect": [(i % COLS) * W * PITCH, (i // COLS) * H * PITCH, W * PITCH, H * PITCH],
                    "mask_rect": [(i % COLS) * W, (i // COLS) * H, W, H], "pixels": rows}
                   for i, (name, sounds, words, rows) in enumerate(frames)],
        "rhubarb": cartoon["rhubarb"], "ulipsync": cartoon["ulipsync"],
    }, indent=1))
    print("wrote", ", ".join(p.name for p in sorted(OUT.glob("*.png"))), "sheet.json")


if __name__ == "__main__":
    main()
