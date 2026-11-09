# Controller probe mount

`probe_mount.py` builds a Quest 3 controller mount that holds the XIAO ESP32, an
LP102540 LiPo (25 × 42 × 10 mm), and an MTS-102 mini toggle switch, with a cable path for an external antenna. It keeps the ring clips from `Quest3.step` unchanged and
replaces the original angled cup with a small pod lying flat on the ring.

| File in `out/` | Print orientation |
|---|---|
| `probe_mount_body5.*`, `probe_mount_body9.*` | Ring flat on the bed. Supports are needed under the pod floor. These are the two ring variants in `Quest3.step` (left- and right-hand versions). |
| `probe_lid.*` | Fits both mounts. Print it flat side down, as exported. |

To regenerate the files after changing a parameter:

```bash
python3 -m venv hardware/.venv
hardware/.venv/bin/python -m pip install build123d
hardware/.venv/bin/python hardware/probe_mount.py
```

## Geometry

- **Ring:** The ring is the original solid, cut at its own top face (z = 8.4) and at r = 38.4 mm. That removes the cup but leaves every clip surface as it was.
- **Pod:** The outside is 49.2 × 30.1 × 23.3 mm (tangential × radial × height, lid included). It lies flat on top of the ring where the cup was, parallel to the controller face, and its lid is on the outer end. Its top is 23.3 mm above the ring and about 16 mm lower than the original cup's. The switch's bushing, nut and lever stick out about 18 mm from the −X end.
- **Clearance over the controller face:** The pod's inner end is at r = 31 mm. The ring's retaining lip, which covers the edge of the face, reaches r ≈ 28.6–30 mm, so no part of the pod is over the face. The web under the pod is trimmed to r ≥ 34.4 mm, so it never reaches into the space the controller occupies.
- **Underside:** The web under the pod follows the original neck. The pod's flat underside sits at the ring-top height all the way out to r ≈ 61 mm. Near the outer end, that is up to about 10 mm lower than the original cup's underside, so check clearance against your hand and the grip.

## Assembly

1. Lay the battery flat in the bottom bay with the lead at the −X end (the switch end).
2. Mount the switch in the 6.2 mm hole in the −X end wall. The 2.5 mm hole above it takes the locking washer's tab. Tighten the outer nut. The switch body sits inside, with the lever moving toward and away from the controller.
3. Wire the battery to the XIAO's BAT+/BAT− pads on the back of the board. Run BAT+ through the switch. Route the wires over or under the switch body. There's about 8 mm of space between the switch terminals and the end of the board for the joints.
4. Thread the antenna's u.FL plug in through the 3.2 mm hole in the top face, then plug it into the XIAO. The hole is about 5 mm from the connector. The antenna can be stuck to the top face (about 49 × 27 mm flat).
5. Slide the XIAO in from the open end, above the divider with its components facing up, so the USB-C receptacle runs along the slot in the +X end wall. Corner standoffs keep a 1.5 mm wiring gap behind the board.
6. Snap the lid on. A tab on the lid closes the slot above the USB port, and a rib holds the board down. The notch at the −X end of the opening is for prying the lid off.

Reference photos: `716JreSNasL…jpg` (battery), `61Tk6TDz8XL…jpg` (MTS-102 switch drawing), `51c4xTsEAnL…jpg` (XIAO ESP32-C5 kit and antenna).

## Unverified

- The developer reports printing the left-hand adapter. Quantified fit, hand clearance and
  a verified `body5`/`body9` handedness mapping have not been recorded. Check the snap fit (0.15 mm clearance, 0.3 mm bead) and the USB slot on your printer first.
- The u.FL position comes from the C3 model: centred at the end opposite the USB port. Check where it is on the C5 before printing.
- The pod is sized from the XIAO ESP32-C3 model. The XIAO ESP32-C5 uses the same 21 × 17.8 mm form factor, but confirm its component heights (4.2 mm allowed) and its antenna connector.
- Left/right Touch Plus FBX references are present, but controller registration and physical
  hand/grip clearance still need validation. CAD model presence is not a fit measurement.

## M5StickC Plus2 mount

`m5_mount.py` builds a second mount, for an [M5StickC Plus2](https://docs.m5stack.com/en/core/M5StickC%20PLUS2)
on the LEGO adapter plate from the [StickC-Plus2 Watch Kit](https://docs.m5stack.com/en/accessory/StickC-Plus2_Watch_Kit).
It uses the same ring clips as `probe_mount.py`, but replaces the ESP32 pod with a flat deck the plate screws down to.

| File in `out/` | Print orientation |
|---|---|
| `m5_mount_body5.*`, `m5_mount_body9.*` | Ring flat on the bed, deck up. Supports are needed under the deck past the ring. The same two ring variants as the probe mount. |
| `preview_m5_body5.png` | Preview: the mount (blue) with the plate seated on it (orange) |

**The plate** (from M5Stack's drawing, `m5_lego.jpg`): 28 mm deep, 40 mm wide across two Ø8 bosses, 8 mm tall on the
boss side (8.4 mm to the tips of its strap clips). The bosses carry the LEGO pin holes (Ø5, through), centred 4 mm from
one end of the plate, which puts them 32 mm apart (four LEGO modules). The stick sits between the bosses (24 mm apart
inside), held by the clips.

**Layout:** the plate lies flat side down, bosses up, with its pin-hole end toward the controller (the plate's edge at r = 31 mm,
outside the ring's retaining lip, like the probe pod's inner end), so its long end and the stick's point away from
the controller.

- **Deck:** 5 mm thick, flat on the ring top, covering the plate's outline plus 1 mm.
- **Shoulders:** a 1.6 mm wall, 2 mm tall, around the outer half of each boss's base (0.2 mm clearance to the boss),
  past the plate's thickness and up the boss. The pair locates the plate in both directions, and they stay clear of
  the clips beside the bosses' inner sides.
- **Screws:** M3 down through each pin hole (head and washer on the boss top), threaded straight into the plastic: a
  Ø2.75 mm blind hole, 4.4 mm deep, in the deck (`SCREW_HOLE`). Use an M3 × 12 screw (8 mm through the boss, about
  4 mm of thread) with a washer: the Ø5 hole is too big for an M3 head alone, and the washer bears on the boss. Don't overtighten; the threads are in plastic.

To regenerate (same environment as the probe mount): `hardware/.venv/bin/python hardware/m5_mount.py`. It imports the
ring constants and the web from `probe_mount.py`, and prints a fit check: the volume where a model of the seated plate
(bosses, holes, plate, clips) overlaps the mount, which should be 0.

### Unverified

- Not printed yet. The plate's body width (27 mm), thickness and clip positions are scaled from the drawing, not
  measured; check the shoulder fit on the real plate first, and that its flat side is flat enough to sit on the
  deck (the drawing shows a shallow step in it).
- Where the stick sits along the plate isn't on the drawing; check that its overhang past the deck clears your hand.
- The deck is cantilevered about 22 mm past the ring's outer wall (supported by the same web as the probe pod).

### References

- M5Stack, StickC-Plus2 Watch Kit (K016-H2): https://docs.m5stack.com/en/accessory/StickC-Plus2_Watch_Kit (contents:
  StickC-Plus2, LEGO adapter, strap; assembled size 38.0 × 26.7 × 10.6 mm).
- M5Stack, M5StickC Plus2: https://docs.m5stack.com/en/core/M5StickC%20PLUS2 (48.0 × 24.0 × 13.5 mm, 16.7 g).
- `m5_lego.jpg`: M5Stack's mechanical drawing of the kit's LEGO adapter plate (units mm).
- LEGO Technic hole pitch: 8 mm per module, which the 32 mm hole spacing matches.

## Source and reference inventory

- `probe_mount.py`, `m5_mount.py`: parameterized sources, all dimensions in millimetres; resolves paths relative
  to the script and regenerates `out/`.
- `Quest3.step`: source geometry containing the two ring variants used by the generator.
- `Seeed-Studio-XIAO-ESP32-C3.step`: board-envelope reference; it does not certify C5 fit.
- `MetaQuestTouchPlus_Left.fbx`, `MetaQuestTouchPlus_Right.fbx`: controller visual references.
- `m5_lego.jpg`: M5Stack's drawing of the LEGO adapter plate (the M5 mount's dimensions).
- The three product JPGs document vendor part geometry/appearance; they are not measured tolerances.
- [out/README.md](out/README.md): curated printable/STEP outputs, regeneration policy and
  the unresolved source-body handedness mapping.

Reference model/photo license provenance is not fully captured. Preserve upstream notices and
record attribution when it becomes available. The initial repository retains these local design
inputs; it does not declare ownership of third-party models or certify the assembled electronics.
