"""Quest 3 controller probe mount: original ring clip + ESP32 / LiPo pod.

Takes the two controller ring clips from Quest3.step (left/right hand
variants), removes the original angled cup, and adds a compact pod that holds:

  * Seeed XIAO ESP32 (C3 now, C5 planned; both 21 x 17.8 mm), USB-C exposed
  * LP102540 LiPo pouch, 25 x 42 x 10 mm, soldered to the XIAO's back pads
  * MTS-102 mini toggle (M6 bushing) as the battery power switch
  * u.FL lead out to an external antenna

Layout (ring-local frame, ring axis = Z, ring opening toward +Y):
  The pod lies flat on top of the ring at -Y, where the original cup was,
  parallel to the controller face.  Its inner end stays outside the ring's
  retaining lip, so nothing overhangs the face.  Battery lies flat on the
  bottom (42 tangential, 25 radial, 10 up); the XIAO sits above it behind a
  thin divider, components up, with the toggle switch in the free space past
  the XIAO's -X end (lever out of the -X end wall).  The antenna lead exits
  the top face; a snap lid closes the outboard end.

The pod is modelled in its own frame (x tangential, u = stacking direction,
z = depth toward the open end) and rotated into place by to_ring().

Run:  python probe_mount.py      (needs build123d; writes into ./out/)
All dimensions in mm.
"""

from pathlib import Path

from build123d import (
    Align, Axis, Box, Cylinder, Plane, Polygon, Pos, Rot,
    export_step, export_stl, extrude, fillet, import_step,
)

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"

# --- Source ring geometry (measured from Quest3.step) -----------------------
# solid index -> ring centre (outer-surface circle fit), top-face Z
# (named after the STEP bodies; the two are the left/right-hand variants)
RINGS = {
    "body5": dict(index=0, center=(-0.82, 1.68), top=8.400),
    "body9": dict(index=1, center=(-98.52, 30.10), top=8.377),
}
RING_CUT_R = 38.4        # keeps the whole ring (outer r <= 38.2), drops the cup
RING_INNER_MAX_R = 34.4  # controller-side surface never exceeds this near -Y
LIP_CLEAR_Y = -31.0      # pod inner face; ring lip edge at -Y is r ~28.6-30

# --- Parts ------------------------------------------------------------------
BATT = (42.0, 10.0, 25.0)   # L (tangential), thickness (radial), W (up)
BATT_TOL = (1.0, 0.8, 0.4)
LEAD_GAP = 3.0              # bend room for the lead at the -X end
XIAO_PCB = (20.95, 1.0, 17.78)  # length, thickness, width
XIAO_STACK = 4.21           # PCB bottom to top of USB-C shell
USB_OVERHANG = 1.51         # receptacle face beyond PCB edge
USB_CENTER_ABOVE_PCB = 2.11
USB_SLOT = (3.6, 9.4)       # radial x vertical opening for the receptacle
BACK_GAP = 1.5              # wiring room behind the XIAO (BAT pads)
UFL_FROM_PCB_EDGE = 1.63    # u.FL centre from the PCB's -X edge (C3 model)
ANT_HOLE_D = 3.2            # passes a u.FL plug on 1.13 mm coax

# MTS-102 toggle (vendor outline drawing): body 13.2 x 7.9 x 9.5, terminals 4
SWITCH_BODY = (9.5 + 4.0, 7.9, 13.2)   # depth below panel, thickness, width
SWITCH_HOLE_D = 6.2         # M6 bushing
SWITCH_KEY_D = 2.5          # anti-rotation washer tab, 6.4 from bushing axis
SWITCH_KEY_OFFSET = 6.4

# --- Enclosure --------------------------------------------------------------
WALL = 1.6
FLOOR = 1.6
DIVIDER = 1.0
FRONT_CLEAR = 0.4
LID_T = 1.6
LIP_T = 1.0
LIP_DEPTH = 1.5
LID_CLEAR = 0.15
CORNER_R = 2.0

# STL meshing: ~32 segments per hole (0.015 mm sag on the M6 hole). Finer
# settings only add sliver fans across the flat faces around each hole.
STL_MESH = dict(tolerance=0.05, angular_tolerance=0.2)

# Derived interior (pod frame: x tangential, u radial outward, z up)
IN_X = BATT[0] + BATT_TOL[0] + LEAD_GAP
BAY_U = BATT[1] + BATT_TOL[1]
PCB_BACK_U = BAY_U + DIVIDER + BACK_GAP
XIAO_BAY_U = BACK_GAP + XIAO_STACK + FRONT_CLEAR
SWITCH_BAY_U = SWITCH_BODY[1] + 0.4
IN_U = BAY_U + DIVIDER + max(XIAO_BAY_U, SWITCH_BAY_U)
IN_Z = BATT[2] + BATT_TOL[2] + LIP_DEPTH
OUT_X, OUT_U, OUT_Z = IN_X + 2 * WALL, IN_U + 2 * WALL, IN_Z + FLOOR


def box(x0, x1, u0, u1, z0, z1):
    """Axis-aligned box in pod coordinates (u mapped to +Y here)."""
    return Pos((x0 + x1) / 2, (u0 + u1) / 2, (z0 + z1) / 2) * Box(
        x1 - x0, u1 - u0, z1 - z0)


def pod_body():
    """Open-top pod. Pod frame: x centred, u=0 at inner outer face, z=0 bottom."""
    hx = OUT_X / 2
    body = box(-hx, hx, 0, OUT_U, 0, OUT_Z)
    body = fillet(body.edges().filter_by(Axis.Z), CORNER_R)

    ix = IN_X / 2
    u_in0, u_in1 = WALL, WALL + IN_U
    body -= box(-ix, ix, u_in0, u_in1, FLOOR, OUT_Z + 1)

    # Divider between battery and XIAO; open at -X end for the battery lead.
    div_u0 = WALL + BAY_U
    body += box(-ix + LEAD_GAP + 1.0, ix, div_u0, div_u0 + DIVIDER,
                FLOOR, FLOOR + BATT[2] + BATT_TOL[2] - 0.2)

    # XIAO placement: USB face 0.2 inside the +X outer surface.
    usb_face_x = hx - 0.2
    pcb_x1 = usb_face_x - USB_OVERHANG
    pcb_x0 = pcb_x1 - XIAO_PCB[0]
    pcb_back = WALL + PCB_BACK_U
    # Corner standoffs behind the PCB (keep the wiring gap).
    for xa in (pcb_x0 + 0.6, pcb_x1 - 2.6):
        for za in (FLOOR + 0.3, FLOOR + XIAO_PCB[2] - 2.3):
            body += box(xa, xa + 2.0, div_u0 + DIVIDER, pcb_back, za, za + 2.0)
    # -X stop rib beside the PCB edge.
    body += box(pcb_x0 - 1.2, pcb_x0 - 0.2, div_u0 + DIVIDER,
                pcb_back + XIAO_PCB[1] + 1.0, FLOOR, FLOOR + XIAO_PCB[2])

    # The outer bay is sized for the switch; pad the outer wall over the XIAO
    # so the board is still held 0.4 mm off its components.
    front = pcb_back + XIAO_STACK + FRONT_CLEAR
    if front < u_in1:
        body += box(pcb_x0 - 0.5, ix, front, u_in1 + 0.01,
                    FLOOR - 0.01, OUT_Z - LIP_DEPTH - 0.2)

    # USB-C slot through the +X end wall, open to the top so the board drops in.
    usb_u = pcb_back + USB_CENTER_ABOVE_PCB
    usb_z = FLOOR + XIAO_PCB[2] / 2
    body -= box(ix - 0.5, hx + 1, usb_u - USB_SLOT[0] / 2, usb_u + USB_SLOT[0] / 2,
                usb_z - USB_SLOT[1] / 2, OUT_Z + 1)
    # Shallow outside relief so cable overmolds seat against the wall.
    body -= box(hx - 0.4, hx + 1, usb_u - 3.4, usb_u + 3.4,
                usb_z - 6.0, usb_z + 6.0)

    # Antenna lead: hole through the outer wall just past the u.FL end of the
    # board (path ~5 mm); the plug threads through from outside.
    ant_x = pcb_x0 - 3.2
    body -= Pos(ant_x, u_in1 - 1, usb_z) * (Rot(-90, 0, 0) * Cylinder(
        ANT_HOLE_D / 2, OUT_U, align=(Align.CENTER, Align.CENTER, Align.MIN)))

    # Toggle switch in the -X end wall, body inside the outer bay.
    sw_u = div_u0 + DIVIDER + SWITCH_BAY_U / 2
    sw_z = FLOOR + (IN_Z - LIP_DEPTH) / 2
    for d, dz in ((SWITCH_HOLE_D, 0.0), (SWITCH_KEY_D, SWITCH_KEY_OFFSET)):
        body -= Pos(-hx - 1, sw_u, sw_z + dz) * (Rot(0, 90, 0) * Cylinder(
            d / 2, WALL + 2, align=(Align.CENTER, Align.CENTER, Align.MIN)))

    # Snap grooves for the lid bead on both long walls.
    gz = OUT_Z - LIP_DEPTH / 2
    for u_face in (u_in0, u_in1):
        g = Rot(0, 90, 0) * Cylinder(0.7, 16)
        body -= Pos(0, u_face, gz) * g
    # Pry notch at the -X end.
    body -= box(-hx - 1, -ix + 0.01, OUT_U / 2 - 4, OUT_U / 2 + 4,
                OUT_Z - 1.6, OUT_Z + 1)

    return body, dict(usb_u=usb_u, usb_z=usb_z, pcb_x0=pcb_x0, pcb_x1=pcb_x1,
                      pcb_back=pcb_back, sw_u=sw_u, sw_z=sw_z, ant_x=ant_x)


def pod_lid(info):
    hx = OUT_X / 2
    lid = box(-hx, hx, 0, OUT_U, OUT_Z, OUT_Z + LID_T)
    lid = fillet(lid.edges().filter_by(Axis.Z), CORNER_R)

    ix, c = IN_X / 2, LID_CLEAR
    u_in0, u_in1 = WALL, WALL + IN_U
    lip = box(-ix + c, ix - c, u_in0 + c, u_in1 - c, OUT_Z - LIP_DEPTH, OUT_Z)
    lip -= box(-ix + c + LIP_T, ix - c - LIP_T, u_in0 + c + LIP_T,
               u_in1 - c - LIP_T, OUT_Z - LIP_DEPTH - 1, OUT_Z + 1)
    lid += lip
    gz = OUT_Z - LIP_DEPTH / 2
    for u_face in (u_in0 + c, u_in1 - c):
        lid += Pos(0, u_face, gz) * (Rot(0, 90, 0) * Cylinder(0.45, 14))

    # Fill the top of the USB slot down to just above the receptacle.
    usb_u, usb_z = info["usb_u"], info["usb_z"]
    lid += box(ix + 0.05, hx - 0.05, usb_u - USB_SLOT[0] / 2 + c,
               usb_u + USB_SLOT[0] / 2 - c, usb_z + 4.8, OUT_Z)
    # Hold-down rib on the XIAO's top edge.
    lid += box(info["pcb_x0"] + 3, info["pcb_x1"] - 3, info["pcb_back"],
               info["pcb_back"] + XIAO_PCB[1], FLOOR + XIAO_PCB[2] + 0.3, OUT_Z)
    return lid


def gusset():
    """Web under the pod bonding to the ring's outer wall (ring-local frame)."""
    prof = Polygon((-30.0, -3.0), (-36.5, -3.0), (-46.0, 8.4), (-46.0, 9.0),
                   (-30.0, 9.0), align=None)
    web = extrude(Plane.YZ * prof, amount=12, both=True)  # x = -12..12
    web -= Pos(0, 0, -20) * Cylinder(RING_INNER_MAX_R, 60,
                                     align=(Align.CENTER, Align.CENTER, Align.MIN))
    return web


def to_ring(shape, top):
    """Pod frame -> ring-local frame, lying flat on the ring top.

    Rotating +90 deg about X maps u -> +Z (battery at the bottom, XIAO above)
    and z -> -Y (closed end at LIP_CLEAR_Y, lid outboard); x and the USB-C
    side are unchanged.
    """
    return Pos(0, LIP_CLEAR_Y, top - 0.2) * (Rot(90, 0, 0) * shape)


def main():
    OUT.mkdir(exist_ok=True)
    src = import_step(HERE / "Quest3.step").solids()
    body_p, info = pod_body()
    lid_p = pod_lid(info)
    print(f"pod outside: {OUT_X:.1f} x {OUT_U:.1f} x {OUT_Z + LID_T:.1f} mm "
          f"(tangential x radial x height incl. lid)")

    for name, r in RINGS.items():
        cx, cy = r["center"]
        to_global = Pos(cx, cy, 0)
        solid = src[r["index"]]
        keep = Pos(cx, cy, -20) * Cylinder(
            RING_CUT_R, r["top"] + 20,
            align=(Align.CENTER, Align.CENTER, Align.MIN))
        ring = solid & keep

        pod = to_global * to_ring(body_p, r["top"])
        web = to_global * gusset()
        mount = (ring + web + pod).clean()  # merge coplanar fragments
        # Back to a print-friendly origin: ring centre at XY 0, ring bottom on Z 0.
        zmin = mount.bounding_box().min.Z
        mount = Pos(-cx, -cy, -zmin) * mount

        print(f"ring {name}: solids={len(mount.solids())} "
              f"valid={mount.is_valid} vol={mount.volume:.0f} mm^3 "
              f"bbox={mount.bounding_box().size}")
        export_step(mount, OUT / f"probe_mount_{name}.step")
        export_stl(mount, OUT / f"probe_mount_{name}.stl", **STL_MESH)

    # The pod is identical on both hands, so one lid fits either mount.
    # Exported upside-down: flat face on the bed.
    lid_print = Rot(180, 0, 0) * lid_p
    lb = lid_print.bounding_box()
    lid_print = Pos(-lb.center().X, -lb.center().Y, -lb.min.Z) * lid_print
    export_step(lid_print, OUT / "probe_lid.step")
    export_stl(lid_print, OUT / "probe_lid.stl", **STL_MESH)


if __name__ == "__main__":
    main()
