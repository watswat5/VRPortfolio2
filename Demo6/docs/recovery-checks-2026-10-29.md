# Crash recovery verification — 2026-10-29

## Recovered state

The source for miniature fog, optional signal-density weighting and voxel-scaled movement haptics
survived the computer crash. The interrupted density-control migration had not reached the saved
scenes. Regenerated the menu and completed its wiring in **HeatmapBuilder** and **ProbeTester**,
including the Fog settings page, toggle and slider. Updated the generated meter prefab and fog
material as needed.

Before copying recovered assets back, verified their original source hashes had not changed.
Used atomic file replacement. Saved original asset backups and verification outputs under the
ignored local folder `diagnostics/crash-recovery-2026-10-29-b1ort7kb/`. This directory is local
only and is not included in Git.

## Software checks

Environment: Unity **6000.3.22f1**, Android script target, Meta XR/MRUK **207.0.1**.
Performed all Unity operations in an isolated Assets/Packages/ProjectSettings copy.

| Check | Result |
|---|---|
| Android player scripts, including device-only branches | Passed |
| Existing `WifiHeatmap.Core.Tests` EditMode suite | **72 passed, 0 failed, 0 skipped** |
| Required preview/density UI references in both saved scenes | Passed |
| Main page active / fog options initially closed, slider range 0–1 | Passed |
| Signal-density defaults: off, 50% effect | Passed |
| Haptics probe/mount/tracking-space/settings references and left-controller target | Passed |
| Haptics share preview voxel settings; warning/full fractions 10% / 50% | Passed |
| Additional scalar movement-risk checks: nominal, finer voxels, slow motion, unknown timing | Passed |
| Additional fog-field checks: bounded dimensions, local support, RSSI/confidence preservation | Passed |
| MiniatureFog, VoxelCloud and RoomPreview shader import error checks | No import errors |
| Git object integrity | No corrupt/missing objects reported; unreachable objects retained |
| Source/documentation whitespace checks | Passed |

The additional checks used a temporary Editor script, retained with the local diagnostics.
They were not added to the application's runtime or repository test suite. NUnit XML and compile
logs are also retained in that diagnostics folder. No new commit was created during recovery.

## Remaining device checks

These results do not establish GPU rendering correctness/performance, haptic feel, BLE behavior,
Space Setup return behavior or room relocalization on Quest. No APK was built or deployed during
recovery. Rebuild/install the application for on-device verification.

The density control is at **Preview → Fog settings**. Enable **Signal density**, then adjust
**Signal effect**. It follows the Cloud tab's low/high dBm endpoints, including auto scaling.
At 100%, low-end readings receive 0.2× base density and high-end readings receive 2×. Coverage
and confidence still fade the field. This changes rendering only.
