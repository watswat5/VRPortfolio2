# Wi-Fi Heatmap: full guide

> **⚠️ Use a personal or unmanaged Quest 3.** On headsets enrolled in device management (ArborXR, Meta Quest for
> Business), Horizon OS blocks this sideloaded app's room and anchor access (`ERROR_PACKAGE_UNTRUSTED`), so it
> can't load your room or save anchors. See the [project README](../README.md) for details and workarounds.

What's built, the repository's layout, requirements, building and flashing, how timing and placement work, data
and replay, and development notes. The [project README](../README.md) has the short version: what it is, how to run
it, controls, AI use and attributions.

A mixed-reality Wi-Fi survey prototype for Meta Quest. An ESP32 probe measures a selected
Wi-Fi radio while Unity associates timestamped RSSI reports with the probe's calibrated antenna
pose. The headset displays a translucent voxel cloud as the operator surveys the room.

The probe is **not head-mounted**. The current application expects it in a printed clip on the
**left or right Touch Plus controller** (chosen in **Mount**) and uses its tracked pose plus the calibrated offset from
controller to pod to antenna. Alternatively an M5StickC Plus2 running the same firmware can ride on a controller in its own printed clip
(`hardware/m5_mount.py`), or be worn on either wrist and tracked by hand tracking
([details](../unity/README.md#probe-on-the-wrist-hand-tracking)); the wrist pose is less precise. Probe-to-Quest communication uses **BLE**. USB serial is supported by the desktop
tester; a Quest Android USB bridge is not implemented. [AGENTS.md](../AGENTS.md) describes the
architecture and distinguishes the current path from proposed USB-host work.

This is a prototype. Configured targets, successful compilation and partial hardware checks
are distinct from verified device support. Quest 3S operation, complete C5 channel/monitor
coverage, reliable spatial persistence and measured spatial accuracy remain validation work.

## What is included

- **ESP32 firmware:** WiFiProbe v1 commands, capability discovery, AP scans, selection by
  BSSID/channel (or, protocol 1.2, a network's radios on one channel, reporting the strongest), passive receive filtering, timestamped RSSI aggregates, statistics, and
  serial/BLE transport. Measuring an AP does not require joining it or entering its password.
- **Desktop tools:** a Python/Tk tester for USB serial or BLE, Linux passkey pairing support,
  and an Arduino CLI uploader with explicit board, USB mode and partition selection.
- **Quest application:** passthrough, controller/hand menu interaction, AP selection (one row per access point, or
  per network and channel where a network has several), an LED
  meter with numeric dBm readout, movement haptics scaled by clock uncertainty and voxel size,
  live/replayed voxel clouds, recording, and anchors.
- **Calibration:** a freeze-and-fit workflow, kept separately for each controller.
  Fit the physical probe into a stationary target, press the probe controller's trigger, then save the offset.
- **Real-world occlusion:** Quest 3/3S depth sensing hides the voxel cloud behind your hand,
  people, furniture and walls (Meta Depth API, soft occlusion), backed by invisible depth-only
  copies of the scanned room's walls and furniture for sharp, steady edges.
- **Visualization controls:** voxel size, opacity, three RGB colors, a two-handle gradient bar,
  manual low/high dBm endpoints, and optional scaling from observed sample minima/maxima.
- **Room preview:** movable/scalable miniature, semantic walls/furniture bounds or scanned mesh,
  live/saved cloud overlay, arrows at your head and at the probe, and in-app guided Quest Space Setup (ends the
  current recording).
- **Experimental miniature fog:** a bounded 3D RSSI/coverage texture rendered with stereo ray
  marching; its own density (separate from the full-size cloud's opacity), selectable 16/24/48-step quality and a
  switch back to voxel billboards.
- **Mechanical design:** a parameterized controller clip/enclosure, CAD references, print
  exports for two ring variants, a shared lid, and design-history images.

## Repository map

| Directory | Contents and entry point |
|---|---|
| [firmware/](../firmware/README.md) | Desktop tester, transport/pairing helpers and uploader |
| [firmware/WiFiProbe/](../firmware/WiFiProbe/README.md) | Arduino sketch and firmware configuration |
| [hardware/](../hardware/README.md) | Parametric enclosure generator, CAD and reference models |
| [hardware/out/](../hardware/out/README.md) | Curated STEP/STL print exports and a preview |
| [unity/](../unity/README.md) | Unity 6.3 LTS Quest project and operating guide |
| [unity/Assets/Scripts/](../unity/README.md#unity-assets-scripts) | Application modules and data flow |
| [docs/](README.md) | Architecture/recording notes and concept-art index |
| [screenshots/](../screenshots/README.md) | Local headset captures; image files are ignored |
| [AGENTS.md](../AGENTS.md) | Architecture specification, boundaries and verification gates |

The firmware folder was previously called `arduino/`. Use `firmware/` in commands for this
repository. Historical notes may describe earlier versions or design proposals.

## Requirements

| Area | Requirement |
|---|---|
| Desktop tools | Python 3, Tk, dependencies in `firmware/requirements.txt`; BlueZ on Linux for BLE |
| Firmware | Arduino CLI or Arduino IDE with an Arduino-ESP32 core containing the exact board profile |
| Unity | **6000.3.22f1**, Android Build Support, Android SDK/NDK and OpenJDK |
| XR packages | Meta XR Core/Interaction/MRUK **207.0.1**, OpenXR **1.19.0**, OpenXR Meta **2.6.1** (Depth API); versions are in the package manifest/lock |
| CAD regeneration | Python environment with `build123d` |
| Hardware | **Personal or unmanaged** Quest 3 development setup (not enrolled in ArborXR or Quest for Business), compatible probe firmware, the probe clip for either controller (or an M5StickC Plus2 on the wrist or its own clip); data-capable USB cable for flashing and desktop serial tests |

The C3 Super Mini was used during initial development; the enclosure concept targets a XIAO
ESP32-C5. Do not infer radio bands, USB enumeration or physical fit from either family name.
Request `CAPS` and check the exact assembled hardware. The firmware currently declares version
`0.4.0` (protocol 1.2); the sketch is the authority for configuration defaults.

## Quick start

### 1. Prepare the desktop tester

Run from this `Demo6/` directory:

```bash
python3 -m venv firmware/.venv
firmware/.venv/bin/python -m pip install -r firmware/requirements.txt
firmware/.venv/bin/python firmware/test_gui.py
```

Tk may require a separate operating-system package. On Windows use the environment's
`Scripts/python.exe` instead of `bin/python`.

Select USB serial or BLE and refresh the device list. BLE normally advertises as
`WiFiProbe-C5`, including on development builds running on other ESP32 variants. Pair using
the passkey configured in `BLE_PAIRING_PASSKEY`; the current public prototype default is
`123456`. That BLE pairing PIN is unrelated to a Wi-Fi network password. Existing bonds can
suppress a new passkey prompt. Connect, inspect HELLO/CAPS, scan, select a specific BSSID and
channel, and start reporting.

### 2. Compile and flash the probe

Install Arduino CLI and the appropriate ESP32 board package first. The uploader does not
install them automatically:

```bash
python3 firmware/upload_firmware.py --list
python3 firmware/upload_firmware.py
```

Select the actual serial port and exact board profile; USB ID matches are only hints. Close
Serial Monitor and disconnect other clients from that port before uploading. The uploader
compiles into a temporary directory and uploads only after a successful build.

Wi-Fi plus BLE can exceed the default application partition, including on a generic C5
profile. `--partition huge_app` selects a larger app layout when supported; verify flash
capacity and the loss of OTA slots before choosing it. See the [firmware guide](../firmware/README.md)
for complete commands and board-specific caveats.

### 3. Open and build the Quest app

1. Open the `unity/` directory in Unity Hub with **6000.3.22f1** and Android support installed.
2. Allow package import. Meta packages resolve through the registry in `Packages/manifest.json`.
3. Open `Assets/Scenes/HeatmapBuilder.unity`. In the checked-in build settings it is the only
   enabled scene; `ProbeTester` is retained as a disabled development scene.
4. Enable headset developer mode and USB debugging, authorize the computer in-headset, and
   check `adb devices -l` if Unity does not list the device.
5. Build for Android using Unity's Build Profiles or **WiFi Heatmap > Build Quest APK**.
   The custom command writes `unity/Builds/WifiHeatmap.apk`; generated builds are ignored.

The application ID is `com.wifiheatmap.prototype`. Rebuild the complete APK after changing
native Java callbacks so Java listener and C# proxy signatures stay in sync.

**Do not run the full Configure command just to open the existing project:**
**WiFi Heatmap > Configure Quest 3 MR Project** rebuilds scenes and generated prefabs and
can replace manual edits. **Update Cloud and Calibration Menus** is the narrower migration
for the menus/readout while retaining scene rigs and recording components. Save work first.

### 4. Scan and build a cloud

1. Open the setup menu with **X** on the probe's controller (**A** if the probe is on the right).
2. Connect in **Probe**, then use **Network** to scan and choose the radio (or the network's radios on a channel) to
   measure.
3. Use a resolved MRUK room or place/select a manual anchor in **Anchors**.
4. In **Mount**, pick the controller the probe is on, then calibrate if needed. **Align probe** freezes a cyan ball
   with X/Y/Z arrows: centre the real antenna in it, line the probe up with the arrows, and press that controller's
   **index trigger**. **Align meter** works the same way for the EMF meter: it stays put while you move the probe
   until the meter sits where you want it. **Y** (left) or
   **B** (right) cancels. Save the offset.
5. The cloud grows while measuring; recording is not required for a live preview.
6. Adjust **Cloud** controls. **Auto scale** fits the active dataset's extrema; turn it off to
   retain that range for manual adjustment. Color meanings can change while auto scaling is on.
7. Use **Sessions > Record** to preserve observations, then load a saved session for replay.

Recording is blocked during mount alignment or while its offset is unsaved, and the probe controller can't change while recording. Simulated meter
readings are never recorded or added to the measured cloud. Unresolved alignment and tracking
loss must not create points at a stale pose.

## How timing and placement work

```text
ESP32 timestamped RSSI window
    -> BLE notification + Android receive timestamp
    -> protocol/session validation
    -> probe-to-headset clock mapping
    -> probe-controller pose plus calibrated antenna offset at measurement time
    -> anchor-relative observation
    -> raw recording and/or sparse voxel aggregation
    -> translucent cloud + confidence fade
```

Delivery delay is compensated using the measurement timestamp and pose history. It is not
corrected by moving a point backward along the current velocity. Residual timing uncertainty
remains because a round trip does not reveal the exact delay in each direction.

Android timestamps reception at GATT callback entry with `elapsedRealtimeNanos`. PING send
and pose-history timestamps use that same clock through a startup adapter. The Probe tab
reports dispatch/frame queue delays. Clock synchronization uses repeated PING/PONG exchanges,
low-round-trip selection and a drift fit. See the [core guide](../unity/README.md#unity-assets-scripts-core).

The current preview flags timing uncertainty above 15 ms, fades those observations, and
excludes live observations above a separate 100 ms uncertainty budget. This is a prototype
display policy, not a measured accuracy guarantee. Saved format-1 records retain a timing flag
but not the numeric per-observation uncertainty; replay labels that bound as unavailable.

## Data, calibration and replay

Quest application data lives under:

```text
/sdcard/Android/data/com.wifiheatmap.prototype/files/
  probe-calibration-left.json          # one per controller that has a saved offset
  probe-calibration-right.json
  anchors.json                         # after manual anchors are saved
  sessions/<session-id>/
    session.json
    samples.bin
    clock.jsonl
```

Calibration files contain versioned controller-to-pod and pod-to-antenna transforms plus mount,
controller and headset identity. Sessions preserve raw observations, quality flags, AP identity,
anchor identifiers, calibration provenance and clock exchanges. Derived cloud meshes are rebuilt
from observations. A missing/unresolved anchor prevents aligned replay.

### Room anchors vs. manual anchors

Samples are stored relative to the anchor they were recorded against, so a saved session is only
as stable as that anchor. **Room anchors** come from Quest Space Setup and belong to the system:
editing or re-scanning the room there can turn or move the room's anchor (one test room's old
sessions came back rotated about 45° while new ones lined up), and a re-scan gives the room a new
ID, so sessions recorded against the old one can no longer be placed at all. **Manual anchors**
(**Anchors > Place anchor at probe**, then **Use**) belong to the app and aren't touched by Space
Setup, so they're recommended for any survey you want to replay later. Place one near the middle
of the area: a small heading error grows with distance from it.

Session blocks are CRC-checked and unfinished recordings can be recovered. See
[recording details](../unity/README.md#unity-assets-scripts-recording) and the
[file-format notes](session-writer-plan.md). App uninstall/data deletion can remove local data.

RSSI, SSID/BSSID, spatial records and headset screenshots can expose room/network information.
They remain local by default. Raw captures, sessions, personal calibration and local debugger
credentials are excluded from the initial Git snapshot.

## Development and validation

- Read [AGENTS.md](../AGENTS.md) before implementation changes. It is a design specification;
  it does not certify every described capability as implemented or verified.
- Keep domain math/parsing/serialization in `WifiHeatmap.Core` without Unity or Meta types.
- Keep BLE/Android and Meta room integration behind their existing adapter boundaries.
- Preserve Unity `.meta` files and commit `Assets`, `Packages`, and `ProjectSettings` together.
- Python transport/pairing checks are described in [firmware/](../firmware/README.md); Core EditMode
  coverage and remaining gaps are described in [Unity tests](../unity/README.md#unity-assets-tests).
- Extend relevant coverage when changing protocol, timing, transforms, storage or state logic.

As of 2026-11-04: firmware 0.4.0 compiles for the M5StickC Plus2 and the XIAO ESP32-C5 and runs on two Plus2s
(protocol 1.2 checked over USB serial); 95 Core EditMode tests pass; the app is used on a Quest 3 over BLE with the
probe on a controller and on the wrist, including for the demo video. Measuring a network's radios together hasn't
yet been tried where a network has several on one channel, and the XIAO ESP32-C5 hasn't been flashed with 0.4.0.

The initial visualization is a measured-cell volumetric preview, not a completed MRUK surface
heatmap or an RF reconstruction. Confidence is heuristic. Quest USB host support, Quest 3S,
full C5 radio/regulatory coverage, persistence accuracy and lifecycle robustness require the
verification work listed in [AGENTS.md](../AGENTS.md).
