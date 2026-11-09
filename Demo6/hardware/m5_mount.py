"""Quest 3 controller mount for an M5StickC Plus2 on its LEGO adapter plate.

Same ring clips as probe_mount.py (from Quest3.step, both hand variants), but
the ESP32 pod is replaced by a flat deck that the M5Stack LEGO adapter plate
(from the StickC-Plus2 Watch Kit, drawing in m5_lego.jpg) screws down to.

The plate (m5_lego.jpg, mm):
  * body 28 deep, 40 wide across two Ø8 bosses, 8 tall on the boss side
    (8.4 to the tips of the strap clips)
  * the bosses carry the LEGO pin holes (Ø5, through), centres 4 mm from the
    plate's end edge, so 32 mm apart (40 - 8): one LEGO module spacing x 4
  * the StickC sits between the bosses (24 mm apart inside), held by the clips;
    the plate's flat side goes against the mount

Layout (ring-local frame, ring axis = Z, ring opening toward +Y, as in
probe_mount.py): the plate lies flat side down on the deck at -Y, where the
original cup was, bosses up, with its boss end toward the controller (inner
edge at PLATE_EDGE_Y, outside the ring's retaining lip) and its long end, and
the stick's, pointing away from it.

  * Shoulders: a C-shaped collar around the outer half of each boss's base
    (away from the other boss), so the pair locates the plate in X and Y
    without touching the clips beside the bosses' inner sides.
  * Screws: M3 down through each pin hole (head and washer on the boss top)
    threaded straight into the deck (SCREW_HOLE below).

Run:  python m5_mount.py      (needs build123d; writes into ./out/)
All dimensions in mm.
"""

from pathlib import Path

from build123d import (
    Align, Axis, Box, Cylinder, Pos, export_step, export_stl, fillet, import_step,
)

from probe_mount import RINGS, RING_CUT_R, RING_INNER_MAX_R, STL_MESH, gusset

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"

# --- M5Stack LEGO adapter plate (m5_lego.jpg) ------------------------------------
PLATE_DEPTH = 28.0         # along the stick
PLATE_BODY_W = 27.0        # between the bosses (scaled from the drawing)
BOSS_D = 8.0
BOSS_H = 8.0               # plate's flat face to the boss faces
CLIP_H = 8.4               # to the strap-clip tips
HOLE_D = 5.0               # LEGO pin hole
HOLE_PITCH = 40.0 - BOSS_D  # 32: centre to centre
HOLE_FROM_EDGE = 4.0       # hole centres from the plate's end edge
PLATE_T = 1.2              # flat plate thickness (scaled from the drawing)

# --- Mount ------------------------------------------------------------------
PLATE_EDGE_Y = -31.0       # plate's boss-end edge; ring lip edge at -Y is r ~28.6-30
FACE_CLEAR_R = 31.0        # nothing above the ring top inside this radius
DECK_T = 5.0
DECK_MARGIN = 1.0          # deck beyond the plate outline, outboard and at the sides
CORNER_R = 2.0
SEAT_CLEAR = 0.2           # radial clearance, boss to shoulder
SHOULDER_T = 1.6           # shoulder wall thickness
SHOULDER_H = 2.0           # above the deck: past the plate and up the boss base
# Screw: M3 threaded straight into the plastic (no insert).
SCREW_HOLE = (2.75, DECK_T - 0.6)  # hole diameter, depth below the deck top (blind)


def deck_top(top):
    return top - 0.2 + DECK_T


def hole_centres():
    y = PLATE_EDGE_Y - HOLE_FROM_EDGE
    return [(-HOLE_PITCH / 2, y), (HOLE_PITCH / 2, y)]


def deck(top):
    """Deck, shoulders and screw holes, in the ring-local frame."""
    z0, z1 = top - 0.2, deck_top(top)
    half_w = HOLE_PITCH / 2 + BOSS_D / 2 + SEAT_CLEAR + SHOULDER_T
    y_in = PLATE_EDGE_Y + SEAT_CLEAR + SHOULDER_T      # shoulders reach past the edge
    y_out = PLATE_EDGE_Y - PLATE_DEPTH - DECK_MARGIN
    part = Pos(0, (y_in + y_out) / 2, (z0 + z1) / 2) * Box(2 * half_w, y_in - y_out, z1 - z0)
    part = fillet(part.edges().filter_by(Axis.Z), CORNER_R)

    r_in = BOSS_D / 2 + SEAT_CLEAR
    for x, y in hole_centres():
        # Shoulder: annulus around the boss base, outer half only (away from
        # the other boss, where the plate's clips sit).
        ring = Pos(x, y, z1) * Cylinder(r_in + SHOULDER_T, SHOULDER_H,
                                        align=(Align.CENTER, Align.CENTER, Align.MIN))
        ring -= Pos(x, y, z1 - 1) * Cylinder(r_in, SHOULDER_H + 2,
                                             align=(Align.CENTER, Align.CENTER, Align.MIN))
        side = 1 if x > 0 else -1
        ring -= Pos(x - side * (r_in + SHOULDER_T + 1) / 2, y, z1) * Box(
            r_in + SHOULDER_T + 1, 2 * (r_in + SHOULDER_T) + 2, 20)
        part += ring
        part -= Pos(x, y, z1 - SCREW_HOLE[1]) * Cylinder(SCREW_HOLE[0] / 2, SCREW_HOLE[1] + 0.01,
                                                          align=(Align.CENTER, Align.CENTER, Align.MIN))

    # Nothing over the controller face above the ring top, nothing into the
    # controller below it.
    part -= Pos(0, 0, top - 0.25) * Cylinder(FACE_CLEAR_R, 40, align=(Align.CENTER, Align.CENTER, Align.MIN))
    part -= Pos(0, 0, -20) * Cylinder(RING_INNER_MAX_R, top + 20.05, align=(Align.CENTER, Align.CENTER, Align.MIN))
    return part


def plate_envelope(top):
    """Rough solid of the adapter plate seated on the mount (for the fit check)."""
    face = deck_top(top)                         # plate's flat face, on the deck
    y_edge = PLATE_EDGE_Y
    yc = y_edge - PLATE_DEPTH / 2
    plate = Pos(0, yc, face + PLATE_T / 2) * Box(PLATE_BODY_W, PLATE_DEPTH, PLATE_T)
    for x, y in hole_centres():
        plate += Pos(x, y, face) * Cylinder(BOSS_D / 2, BOSS_H, align=(Align.CENTER, Align.CENTER, Align.MIN))
        plate -= Pos(x, y, face - 1) * Cylinder(HOLE_D / 2, BOSS_H + 2, align=(Align.CENTER, Align.CENTER, Align.MIN))
        # clips beside each boss's inner side, up to 0.4 past the boss tops
        sx = 1 if x > 0 else -1
        plate += Pos(x - sx * (BOSS_D / 2 + 1.0), y, face + CLIP_H / 2) * Box(2.0, 3.4, CLIP_H)
    # the two outboard clips
    for sx in (-1, 1):
        plate += Pos(sx * (PLATE_BODY_W / 2 - 2.5), y_edge - PLATE_DEPTH + 3.4, face + CLIP_H / 2) * Box(2.0, 3.4, CLIP_H)
    return plate


def main():
    OUT.mkdir(exist_ok=True)
    src = import_step(HERE / "Quest3.step").solids()
    for name, r in RINGS.items():
        cx, cy = r["center"]
        solid = src[r["index"]]
        keep = Pos(cx, cy, -20) * Cylinder(RING_CUT_R, r["top"] + 20,
                                           align=(Align.CENTER, Align.CENTER, Align.MIN))
        ring = Pos(-cx, -cy, 0) * (solid & keep)       # ring-local frame
        web = gusset()
        mount = (ring + web + deck(r["top"])).clean()

        # Fit check: the seated plate must not overlap the mount.
        hit = mount & plate_envelope(r["top"])
        overlap = hit.volume if hit else 0.0
        zmin = mount.bounding_box().min.Z
        mount = Pos(0, 0, -zmin) * mount
        print(f"m5 mount {name}: solids={len(mount.solids())} valid={mount.is_valid} "
              f"vol={mount.volume:.0f} mm^3 bbox={mount.bounding_box().size} "
              f"plate overlap={overlap:.3f} mm^3")
        export_step(mount, OUT / f"m5_mount_{name}.step")
        export_stl(mount, OUT / f"m5_mount_{name}.stl", **STL_MESH)


if __name__ == "__main__":
    main()
