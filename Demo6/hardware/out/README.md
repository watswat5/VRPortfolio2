# Curated print and CAD exports

This directory intentionally tracks the current mechanical outputs so the initial repository
contains printable files as well as the generator.

| Files | Contents |
|---|---|
| `probe_mount_body5.stl`, `.step` | Controller-ring source variant `body5` with the probe pod |
| `probe_mount_body9.stl`, `.step` | Controller-ring source variant `body9` with the probe pod |
| `probe_lid.stl`, `.step` | Common enclosure lid |
| `preview_body5.png` | Preview of the `body5` assembly geometry |
| `m5_mount_body5.stl`, `.step`; `m5_mount_body9.stl`, `.step` | The same two ring variants with the M5StickC Plus2 LEGO-plate deck (`m5_mount.py`) |
| `preview_m5_body5.png` | Preview of the `body5` M5 mount with the plate seated |

The two body names preserve source STEP body identifiers. A verified left/right identifier
mapping has not been recorded; inspect the clip geometry against the actual controller before
choosing a print. The developer reports printing a left adapter, which does not certify every
export's fit, board clearance or snap tolerance.

STL is for slicing; STEP is for CAD inspection or downstream editing. Units are millimetres.
See the [parent guide](../README.md) for orientation, supports, parts and assembly.

## Regeneration

Run `hardware/probe_mount.py` (and `hardware/m5_mount.py` for the M5 mount) from the `Demo6/` directory using a Python environment with
`build123d`. The script resolves input paths relative to itself and writes this directory.
Modify the generator rather than treating a manually edited STL as the design authority.
Review and commit regenerated files together with the source change; old revision dumps and
slicer projects do not belong here by default. Reference-model provenance is described in the
[project guide](../../README.md).
