"""The opponents' posters for the dressing room: one per MC, its face shot (faces/<id>.png, a head-and-shoulders render
of the character prefab with alpha), the name, a two-line description and the difficulty.
Written to unity/Assets/Venue/Posters/<id>.png (1024 x 1536, for a 0.56 x 0.84 m poster).

Made to be read in VR from a couple of metres away: the name ~9 cm tall, the description ~5 cm, a few words per line,
white on dark, nothing small.

    python3 art/posters/make_posters.py
"""
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "unity", "Assets", "Venue", "Posters")
BOLD = "/usr/share/fonts/truetype/ibm-plex/IBMPlexSansCondensed-Bold.ttf"
SEMI = "/usr/share/fonts/truetype/ibm-plex/IBMPlexSansCondensed-SemiBold.ttf"
W, H = 1024, 1536

# id, name, difficulty, description (two short lines), accent
CAST = [
    ("lil_nervous", "LIL NERVOUS", "EASY", ("First battle ever.", "Rattles easily."), (79, 209, 197)),
    ("mc_static", "MC STATIC", "NORMAL", ("Local regular.", "Cocky, beatable."), (59, 130, 246)),
    ("loose_cannon", "LOOSE CANNON", "NORMAL", ("Hot-headed.", "Foul-mouthed."), (239, 68, 68)),
    ("kid_venom", "KID VENOM", "HARD", ("Undefeated.", "Goes personal."), (132, 204, 22)),
    ("the_champ", "THE CHAMP", "LEGENDARY", ("Battle-rap royalty.", "The crowd is theirs."), (245, 184, 46)),
]


def fit(text, path, size, width):
    """The font at `size`, smaller until `text` fits in `width`."""
    while True:
        f = ImageFont.truetype(path, size)
        if f.getlength(text) <= width or size < 20:
            return f
        size -= 4


def poster(cid, name, level, lines, accent):
    dark = tuple(int(c * 0.22) for c in accent)
    img = Image.new("RGB", (W, H))
    d = ImageDraw.Draw(img)
    for y in range(H):                                       # the accent at the top fading to near black
        t = min(1.0, y / (H * 0.75))
        d.line([(0, y), (W, y)], fill=tuple(int(dark[i] * (1 - t) + 10 * t) for i in range(3)))
    glow = Image.new("L", (W, H), 0)                         # a halo behind the head
    ImageDraw.Draw(glow).ellipse((170, 140, 854, 824), fill=150)
    glow = glow.filter(ImageFilter.GaussianBlur(90))
    img.paste(Image.new("RGB", (W, H), accent), (0, 0), glow)

    face = Image.open(os.path.join(HERE, "faces", f"{cid}.png")).convert("RGBA").resize((1000, 1000), Image.LANCZOS)
    img.paste(face, ((W - 1000) // 2, 150), face)
    d = ImageDraw.Draw(img)

    # the difficulty, a tag at the top
    f = ImageFont.truetype(BOLD, 72)
    tw = f.getlength(level)
    d.rounded_rectangle(((W - tw) / 2 - 36, 44, (W + tw) / 2 + 36, 140), radius=14, fill=accent)
    d.text((W / 2, 92), level, font=f, fill=(12, 12, 12), anchor="mm")

    # the name and the description in a dark panel at the bottom (over the shirt)
    shade = Image.new("L", (W, H), 0)
    ImageDraw.Draw(shade).rectangle((0, 1060, W, H), fill=235)
    shade = shade.filter(ImageFilter.GaussianBlur(30))
    img.paste(Image.new("RGB", (W, H), (8, 8, 10)), (0, 0), shade)
    d = ImageDraw.Draw(img)
    f = fit(name, BOLD, 190, W - 90)
    d.text((W / 2, 1150), name, font=f, fill=(255, 255, 255), anchor="mm", stroke_width=4, stroke_fill=(0, 0, 0))
    d.rectangle((W / 2 - 220, 1252, W / 2 + 220, 1264), fill=accent)
    f = ImageFont.truetype(SEMI, 96)
    for i, line in enumerate(lines):
        d.text((W / 2, 1338 + i * 104), line, font=fit(line, SEMI, 96, W - 90), fill=(235, 235, 235), anchor="mm")

    d.rectangle((0, 0, W - 1, H - 1), outline=accent, width=18)   # the border
    img.save(os.path.join(OUT, f"{cid}.png"))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for c in CAST:
        poster(*c)
    print(f"{len(CAST)} posters -> {os.path.normpath(OUT)}")
