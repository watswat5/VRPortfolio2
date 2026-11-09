"""Test laser shows as ILDA .ild files, for the stage lasers (unity/Assets/Venue/Lasers/Shows; LaserProjector plays them).

    python3 art/lasers/make_ild.py

Covers the format's variants, so the reader is tested on each:
  fan_sweep.ild       format 5 (2D, true colour): a rainbow line, a fan of light through the haze, sweeping up and down
  circle_spin.ild     format 5: a circle, its rainbow spinning
  tunnel.ild          format 4 (3D, true colour): a green circle opening up, a cone tunnel over the crowd
  dot_beams.ild       format 5: eight beams on dots (each a blanked jump, then the dot held for a few points) going round
  star_palette.ild    format 2 palette + format 1 (2D, palette colour): a star in the file's own palette's colours
  star_default.ild    format 0 (3D, palette colour), no palette: a star in ILDA's default palette (index 0 red, 16
                      yellow, 24 green, 31 cyan, 40 blue, 48 magenta)
Each frame starts with a blanked point (the beam off on the way to where it starts); the last point of each frame is
marked last. Loops are 30 or 60 frames (LaserProjector stretches a loop over a bar of the beat).
"""
import colorsys
import math
import os
import struct

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
                   "unity", "Assets", "Venue", "Lasers", "Shows")


def header(fmt, name, count, number, total):
    return (b"ILDA" + bytes(3) + bytes([fmt]) + name.encode("ascii")[:8].ljust(8, b"\0")
            + b"Freestyl" + struct.pack(">HHH", count, number, total) + bytes([0, 0]))


def coord(v):
    return max(-32768, min(32767, int(round(v * 32767))))


def frame(fmt, name, number, total, points):
    """points: (x, y, z, (r, g, b) or palette index, blanked), x and y -1..1."""
    out = [header(fmt, name, len(points), number, total)]
    for i, (x, y, z, colour, blank) in enumerate(points):
        status = (0x80 if i == len(points) - 1 else 0) | (0x40 if blank else 0)
        if fmt in (0, 4):
            xyz = struct.pack(">hhh", coord(x), coord(y), coord(z))
        else:
            xyz = struct.pack(">hh", coord(x), coord(y))
        if fmt in (0, 1):
            out.append(xyz + bytes([status, colour]))
        else:
            r, g, b = colour
            out.append(xyz + bytes([status, b, g, r]))
    return b"".join(out)


def rainbow(t):
    r, g, b = colorsys.hsv_to_rgb(t % 1.0, 1.0, 1.0)
    return int(r * 255), int(g * 255), int(b * 255)


def write(name, sections):
    os.makedirs(OUT, exist_ok=True)
    data = b"".join(sections) + header(0, name, 0, 0, 0)   # the end
    with open(os.path.join(OUT, name + ".ild"), "wb") as f:
        f.write(data)
    print(f"wrote {name}.ild: {len(data)} bytes")


def blanked_start(x, y, z=0.0):
    return [(x, y, z, (0, 0, 0), True)]


def fan_sweep(frames=60, n=48):
    out = []
    for f in range(frames):
        y = 0.3 * math.sin(2 * math.pi * f / frames) - 0.05
        pts = blanked_start(-0.8, y)
        pts += [(-0.8 + 1.6 * i / (n - 1), y, 0.0, rainbow(i / n), False) for i in range(n)]
        out.append(frame(5, "fan", f, frames, pts))
    return out


def circle_spin(frames=30, n=48):
    out = []
    for f in range(frames):
        pts = blanked_start(0.45, 0.1)
        for i in range(n + 1):
            a = 2 * math.pi * i / n
            pts.append((0.45 * math.cos(a), 0.1 + 0.45 * math.sin(a), 0.0, rainbow(i / n + f / frames), False))
        out.append(frame(5, "circle", f, frames, pts))
    return out


def tunnel(frames=30, n=40):
    out = []
    for f in range(frames):
        r = 0.08 + 0.72 * f / frames
        pts = blanked_start(r, 0.0, 0.2)
        for i in range(n + 1):
            a = 2 * math.pi * i / n
            pts.append((r * math.cos(a), r * math.sin(a), 0.2, (0, 255, 60), False))
        out.append(frame(4, "tunnel", f, frames, pts))
    return out


def dot_beams(frames=40, dots=8, hold=4):
    out = []
    for f in range(frames):
        pts = []
        for d in range(dots):
            a = 2 * math.pi * (d / dots + f / frames)
            x, y = 0.7 * math.cos(a), 0.25 + 0.2 * math.sin(a)
            pts += blanked_start(x, y)
            pts += [(x, y, 0.0, rainbow(d / dots), False)] * hold
        out.append(frame(5, "beams", f, frames, pts))
    return out


def star_points(radius=0.5, inner=0.2):
    pts = []
    for i in range(11):
        r = radius if i % 2 == 0 else inner
        a = math.pi / 2 + i * math.pi / 5
        pts.append((r * math.cos(a), r * math.sin(a)))
    return pts


def star_palette(frames=30):
    palette = [(255, 0, 80), (255, 200, 0), (0, 220, 255), (180, 0, 255)]
    pal = header(2, "palette", len(palette), 0, 1) + b"".join(bytes(c) for c in palette)
    out = [pal]
    for f in range(frames):
        s = 0.8 + 0.2 * math.sin(2 * math.pi * f / frames)
        corners = star_points(0.5 * s, 0.2 * s)
        pts = [(corners[0][0], corners[0][1], 0.0, 0, True)]
        pts += [(x, y, 0.0, (i // 3 + f // 8) % len(palette), False) for i, (x, y) in enumerate(corners)]
        out.append(frame(1, "star", f, frames, pts))
    return out


def star_default(frames=30):
    colours = [0, 16, 24, 31, 40, 48]   # red, yellow, green, cyan, blue, magenta in ILDA's default palette
    out = []
    for f in range(frames):
        a0 = 2 * math.pi * f / frames
        corners = [(x * math.cos(a0) - y * math.sin(a0), x * math.sin(a0) + y * math.cos(a0)) for x, y in star_points()]
        pts = [(corners[0][0], corners[0][1], 0.0, 0, True)]
        pts += [(x, y, 0.0, colours[i % len(colours)], False) for i, (x, y) in enumerate(corners)]
        out.append(frame(0, "star", f, frames, pts))
    return out


if __name__ == "__main__":
    write("fan_sweep", fan_sweep())
    write("circle_spin", circle_spin())
    write("tunnel", tunnel())
    write("dot_beams", dot_beams())
    write("star_palette", star_palette())
    write("star_default", star_default())
