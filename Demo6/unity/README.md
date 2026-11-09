<a id="wifi-heatmap--quest-3-mixed-reality-project"></a>
# WiFi Heatmap — Quest 3 mixed-reality project

Unity **6.3 LTS (6000.3.22f1)** with URP, set up for Meta Quest 3 / 3S mixed reality.
Open this `unity` directory from Unity Hub.

## What's installed

| Package | Version | Why |
|---|---|---|
| `com.meta.xr.sdk.core` | 207.0.1 | OVRCameraRig, passthrough, controller input and haptics, spatial anchors, Project Setup Tool |
| `com.meta.xr.mrutilitykit` (MRUK) | 207.0.1 | The headset's room scan: walls, floor and furniture as labelled surfaces, raycasts against them, room anchors |
| `com.meta.xr.sdk.interaction.ovr` (+ `interaction`) | 207.0.1 | Controller/hand ray, poke and grab interactors for menus and tools |
| `com.unity.xr.openxr` | 1.19.0 | OpenXR runtime; the Meta SDK runs on it through the Meta XR feature |
| `com.unity.xr.meta-openxr` | 2.6.1 | Unity OpenXR: Meta. Its session and occlusion features are how the Meta SDK's Depth API gets environment depth (real-world occlusion); it pulls in AR Foundation 6.6 |
| `com.unity.xr.management` | 4.7.0 | XR plug-in management (OpenXR loader on Android) |
| `com.unity.xr.hands` | 1.10.0 | Hand-tracking subsystem used by the Meta SDKs |

Meta packages come from Meta's scoped registry (`https://npm.developer.oculus.com`, scope
`com.meta.xr`), declared in `Packages/manifest.json`. The template's packages (URP 17.3, Input
System, Test Framework, …) are unchanged.

## Project configuration

All of this is applied by **WiFi Heatmap > Configure Quest 3 MR Project**
(`Assets/Editor/ProjectSetup.cs`). It rebuilds the two scenes and generated prefabs, replacing manual edits; use it deliberately.

- **Android:** ARM64, IL2CPP, minimum API 32, target API 34, Vulkan only, linear colour, ASTC,
  GameActivity entry, Input System handling (`activeInputHandler: 1` in the saved settings).
  The setup generator initially requests both backends; SDK fixes/manual settings can change it.
  App ID `com.wifiheatmap.prototype`.
- **OpenXR (Android):** Meta XR feature, Meta Quest Support, Touch Plus and Touch controller
  profiles, the OpenXR Meta session and occlusion features (Depth API), single-pass instanced
  rendering, 16-bit depth submission.
- **Meta project config** (`Assets/Oculus/OculusProjectConfig.asset`):
  - target devices: Quest 3 and Quest 3S
  - input: controllers plus hands
  - passthrough: required
  - scene (room model): required (the Depth API needs it)
  - spatial anchors: enabled
- **URP (the Mobile quality level Android uses):** MSAA 4×, no HDR, no depth or opaque copies.
- **Meta's Project Setup Tool:** finally, every outstanding fix it offers is applied. Its 16 fixes
  include:
  - Application SpaceWarp and dynamic resolution
  - the scene-permission request on startup
  - a passthrough splash screen
  - an Android manifest with the scene, anchor and hand-tracking permissions

  Your BLE plug-in's Bluetooth permissions merge into that manifest at build time.
  **Meta XR > Project Setup Tool** shows anything left.

## Scenes

Both scenes share the same rig. **OVRCameraRig** has floor-level tracking, a transparent
camera background and a passthrough underlay, so you see your room. Meta's comprehensive
**Interaction SDK rig** sits under it, giving rays, pokes and grabs for both controllers and
both hands. One controller carries the probe (left by default, or right), a meter beside it shows its signal, and a
config menu sets everything up.

| Scene | Adds |
|---|---|
| `ProbeTester` (disabled build-settings entry) | Development scene with the probe, menu, MRUK, recording and cloud systems |
| `HeatmapBuilder` (only enabled scene; player startup) | Current room-scanning application, including MRUK, recording and live voxel cloud |

### Probe on the controller (`ProbeMount`)

The ESP32 pod from `hardware/probe_mount.py` clips around the controller's face and sits in
front of it, over the trigger. An M5StickC Plus2 can take its place on the clip from `hardware/m5_mount.py`
(same ring, a deck for the Watch Kit's LEGO plate); the app treats it the same way (**On: controller**), but the
default estimate below is the XIAO pod's, so align and save it with freeze-and-fit. The pod and the Plus2 share
each side's controller calibration file, so recalibrate after swapping one for the other. In the scene it's a `ProbeMount` under `LeftControllerAnchor` at
the pod's centre, with an `Antenna` child on the pod's top face, where the stick-on antenna goes.
**Mount > Side: left/right** moves it to `RightControllerAnchor` and back, and **On: wrist (hands)**
to the hand anchors for a wrist-worn probe ([below](#probe-on-the-wrist-hand-tracking)); both choices
are remembered across launches (`PlayerPrefs`).

- **Where the numbers come from:** the offsets (pod at 58 mm forward, 17 mm up; antenna 11 mm
  above that) were measured on Meta's Touch Plus model. That model is byte-identical to
  `hardware/MetaQuestTouchPlus_Right.fbx` and sits at identity under the controller anchor. They
  were originally a right-hand **estimate**. The left-hand estimate mirrors controller-local X
  (pod +2 mm, antenna +1 mm), and the right-hand one mirrors it back (pod -2 mm, antenna -1 mm);
  Y/Z and identity rotation are retained. Neither is verified on a printed adapter, so both stay
  `calibrationId = estimate`, with mount IDs `probe_mount_left (ring clip pod)` and
  `probe_mount_right (ring clip pod)`.
- **Which controller is the probe's:** `ProbeMount.Controller` (LTouch or RTouch). Recorder and preview
  tracking checks, the frozen-target buttons, the menu button and meter haptics all follow it.
  Each side has its own calibration file; never reuse one side's calibration for the other.
- **Checking it in the headset:** the target, built at runtime, shows where the app thinks the probe is: a
  translucent cyan ball (16 mm) centred on the antenna, with arrows for the probe's axes (X red across, Y green up
  from the controller face or out of the M5's screen, Z blue along the probe toward the trigger, or toward the
  fingers on the wrist). It's the same for every board and mount. If it doesn't sit on the real antenna, calibrate
  it (below). It shows while the **Mount** tab is open and while the meter is being aligned, then hides again;
  **Meter > Probe target** (`ProbeMount.ShowPlaceholder`, remembered across restarts, off by default) keeps it shown
  all the time. The scene's pod box is no longer shown, only its material is used.
- **Use for heatmapping:** `ProbeMount.AntennaPose` is the pose to sample against.

The printed left-hand adapter is the current sensor mount; `hardware/out/` also has the other
clip variant for the right controller. **WiFi Heatmap > Validate Probe Controller Mount** checks
both saved scenes without rebuilding them: left-controller parenting by default, meter follow
target, mount identity, the estimated pod/antenna transforms, and a move to the right controller
(mirrored estimate) and back. Re-running project setup generates the left-hand arrangement.
Reload an already-open scene after pulling these asset changes so its in-memory references match
the saved version. Existing recordings keep their original mount/calibration provenance.

### Probe on the wrist (hand tracking)

A wrist-worn probe, an **M5StickC Plus2** strapped on top of the forearm with its long side
toward the fingers (screen up, read in landscape), is tracked by hand tracking instead of a
controller. **Mount > On: controller / On: wrist (hands)** chooses the carrier and **Side: left /
right** the hand, so there are four carriers, each with its own mount ID
(`m5stickc_plus2_wrist_left` / `_right`), calibration file (`probe-calibration-left-wrist.json`,
`-right-wrist.json`) and outline box (25 × 13.5 × 48 mm). Both choices are remembered.

- **Pose:** the probe rides `LeftHandAnchor` / `RightHandAnchor`, which in hand mode follow the
  tracked hand's root (the wrist) through the same late-latched rig path as a controller, so the
  recorder's timing and pose history are unchanged. Put the controllers down: by default Quest
  tracks either controllers or hands, not both.
- **Tracked** means the hand is tracked with high confidence (`OVRPlugin` hand state). Recorder,
  haptics, the preview arrow and freeze-and-fit all use `ProbeMount.ProbeTracked()`, so low-
  confidence or lost hands flag or drop samples like a lost controller.
- **Estimate:** placed the first time the hand is seen, from its joints (wrist to middle knuckle
  for the long axis, the index and little knuckles for the back of the hand, mirrored by
  handedness), so it doesn't depend on the runtime's hand-root axis convention: the device's centre
  4.5 cm back from the wrist joint and 3 cm out from the back of the wrist. Freeze-and-fit
  refines it.
- **Accuracy:** the hand root follows the hand, not the forearm the device is strapped to, so
  bending the wrist moves the estimated antenna by a few centimetres: keep the wrist straight
  while scanning. Hand tracking is also noisier than a controller and drops when the hand leaves
  the cameras' view. The Plus2's antenna sits against your arm, which weakens readings depending
  on which way you face, and it is 2.4 GHz only.
- **Without a controller:** the hand menu gesture opens the menu; menus work with hand rays and
  pokes. Freeze-and-fit captures with a pinch of the *other* hand's index finger and cancels with a
  middle-finger pinch, or with the probe's buttons **A** and **B** (firmware protocol 1.1).
  Movement warnings use the probe's buzzer (protocol 1.1), else a tone in the headset.

### Calibrating and saving the probe offset

1. In **Mount**, set **Side** and **On** to where the probe is: the left or right controller it
   is clipped to, or the wrist it is strapped to. Switching is blocked while recording, during
   alignment, or with an unsaved offset; it loads that carrier's saved offset, or its estimate if
   there is none.
2. Hold the probe somewhere comfortable and choose **Align probe**. The menu hides,
   leaving the target (ball and arrows) fixed at its current position and rotation in the room.
3. Move and rotate the actual probe until its antenna is centred in the ball and its axes line up
   with the arrows. Check the depth from two angles. Release the probe controller's trigger if it
   was held when starting.
4. Press the probe controller's **index trigger**. The target reattaches to the controller using
   the newly calculated controller-to-probe transform. The antenna keeps its existing offset inside
   the probe, so the ball lands where the real antenna is.
5. Open **Mount > Save offset** to persist it. Startup loads the saved offset for the chosen side.

**Placing the EMF meter (Align meter):** the same freeze-and-fit for the meter. The meter stays where it is in the
room while you move the probe until it sits where you want it, then the same capture (trigger, or the other hand's
pinch or the probe's **A** on the wrist) fixes it there, relative to the probe, so a later probe calibration
carries it along. **Save offset** keeps it (`emf-meter-<side>[-wrist].json`, beside the probe's calibration file,
checked against the mount ID); **Load saved** restores it; **Reset estimate** puts both the probe and the meter
back to their defaults. Saving after only a meter alignment leaves the probe's offset as it was. An unsaved meter
place blocks moving the probe to another carrier, as an unsaved probe offset does, but not measuring or recording.
Readings pause during the alignment itself, as for the probe's. Scenes built before this get the button at runtime.

The probe controller's upper face button (**Y** on the left, **B** on the right) cancels without
changing the previous offset. You can also reopen Mount and choose **Cancel frozen alignment**.
Losing controller tracking, focus, pausing, disabling the mount or changing tracking origin cancels
the frozen target. Save and recording are blocked until capture or cancellation. Only the probe's
controller needs to be tracked. The target is a visual copy, so the actual antenna transform
remains attached to the controller throughout; measurement placement pauses while alignment is
active. Capture clears pose history and live preview data so observations from different offsets
are not mixed. Calibration files identify this method as `frozen-target-visual-alignment`.

This is a visual enclosure alignment, not an independent measurement of the antenna phase center
or a guarantee of passthrough accuracy. The pod-to-antenna estimate is preserved.

- **Save offset:** atomically writes the current pod and antenna rigid transforms
  and a new manual calibration ID.
- **Load saved:** reloads the saved transform and discards an unsaved alignment.
- **Reset estimate:** restores this side's estimated transform in memory.
  Use Save to replace the file with that pose, or Load to undo the reset.

The files are `Application.persistentDataPath/probe-calibration-left.json`,
`probe-calibration-right.json`, and for the wrist `probe-calibration-left-wrist.json` and
`probe-calibration-right-wrist.json` (Quest: `/sdcard/Android/data/com.wifiheatmap.prototype/files/`).
The chosen carrier's file loads automatically at startup when present. Version, coordinate convention,
mount ID, controller identity, headset model, finite values, transform bounds, and
normalized rotations are checked before applying it. Missing files leave the
side's estimate active; corrupt or incompatible files leave the current pose
unchanged and show a load error in the Mount tab. The passkey and network data
are not part of this file. App data deletion/uninstall can remove this file.

Save/Load/Reset and the controller choice are locked while recording. Recording cannot
start during alignment or with unsaved edits; Save or Load first. Changes clear
pose history to avoid mixing measurements across calibration versions. Each new
session stores a full snapshot under `mount.calibration`, in addition to its mount
and calibration IDs (and the controller); older sessions with only the IDs remain readable.
A manual visual alignment is not a measured accuracy guarantee.

**WiFi Heatmap > Update Cloud and Calibration Menus** updates the menu prefab and both
existing scenes while retaining their rigs and recording components. Normal
project setup also generates the tab. **Validate Calibration Controls**
checks scene references, unsaved state, side-switch refusal and recording
locks. Offset file validation/round-trip and left/right mirroring checks live in the Core EditMode suite.

On 2026-11-03, Unity 6000.3.22f1 rebuilt the menu prefab and both scenes with
**Update Cloud and Calibration Menus**. Both scenes then passed the probe-controller and
calibration-control checks, all 72 Core EditMode tests passed, and the Android player scripts
compiled. The right-hand clip's fit, the mirrored right-hand estimate, X/A as the menu button
alongside Meta's interaction rig, and persistence across headset relaunch still need an on-device check.

### EMF meter (`EmfMeter` + `EmfHaptics`)

- **Look:** a ghost-hunter-style handset with an arc of 10 LEDs from red (weak) to green (strong),
  a dBm readout and a status line. It is fixed to the controller like a handheld scanner's screen:
  just beyond the probe pod's front edge (along the controller's forward), 1.5 cm out so it never
  covers the probe, square to the controller, with its screen facing the same way as the pod's top
  (what you look down at in a normal grip) and its up along the controller's forward. It moves
  rigidly with the controller (`podGap` on `EmfMeter` sets the distance). With no probe mount, it
  falls back to hovering above the controller and turning to face you.
- **Display range:** defaults to −90 to −30 dBm; the Cloud tab's Low and High sliders also
  update the meter range. Raw dBm readouts and recorded observations do not change. The arc rises fast and falls more
  slowly, like a real meter; the last LED flickers when pinned. A large bold center readout shows
  the latest actual integer dBm value, or `-- dBm` after readings become stale. The number is
  independent of LED smoothing and auto scaling. The scale-end labels follow the current range.
- **Movement haptics:** pulses on the probe's controller (on the wrist: the probe's buzzer with
  firmware protocol 1.1, else a tone in the headset) indicate antenna speed relative to clock uncertainty
  and voxel width. Estimated displacement is speed × time uncertainty. Warnings start at 10% of a
  voxel and reach a sustained buzz around 50%; finer voxels require slower motion. A distinct
  double tap means clock quality is unknown/stale. The Meter tab shows speed, uncertainty and
  displacement. The toggle disables vibration. See [inputs and limitations](#unity-assets-scripts-meter).

### Config menu (`ProbeConfigMenu`)

The menu opens in front of you a moment after launch. **X** on the probe's controller (**A** when the
probe is on the right; the hand menu gesture also works) hides it or calls it back in front of you. Point with either controller's
ray (trigger to click) or a hand ray (pinch), or poke the buttons with a fingertip. It's a normal
uGUI canvas, made interactable with Meta's `QuickActionsAPI` ray- and poke-canvas actions.

| Tab | What it does |
|---|---|
| **Probe** | Connection state, plus the probe's HELLO and CAPS details (firmware, bands, BLE security). **Connect** and **Disconnect**. On first connect, Android asks for the probe's pairing PIN, which is set in the firmware (`BLE_PAIRING_PASSKEY`, see `../firmware/WiFiProbe/README.md`) |
| **Network** | **Scan** lists nearby networks, strongest first, five per page. A network with one access point on a channel is a row showing SSID, channel, band, BSSID, security and RSSI; tap it to measure that radio. A network with several on one channel (an institution's eduroam, say) is one row per channel showing "N APs" and the strongest one's RSSI; tapping it measures them all (protocol 1.2, firmware 0.4.0+), each reading the strongest access point heard, as a roaming device would connect. The line above the list then names the radio the latest reading came from. With older firmware a group row measures its strongest radio from the scan. Hidden networks are never grouped. **Stop** ends the measurement. 0.1 / 0.25 / 0.5 / 1 s sets the probe's report interval |
| **Cloud** | Live translucent voxel cloud, voxel size and opacity, separate low/high dBm sliders, one gradient bar with two handles, and three colors with RGB controls |
| **Meter** | Haptics on/off, simulated signal on/off, and the probe target: **Always** or only while **Calibrating** (the default). Also shows the selected scale, the latest reading, and the mount and calibration IDs |
| **Sessions**, **Anchors** | Recording and saved sessions; room and manual anchors. See [Recording sessions](#recording-sessions) |
| **Mount** | Which controller carries the probe, and freeze-and-fit calibration. See [Calibrating and saving the probe offset](#calibrating-and-saving-the-probe-offset) |
| **Preview** | Room miniature, fog settings and in-app Space Setup. See [Room miniature](#room-miniature-and-in-app-space-setup) |

Behind the menu, `Esp32BleSignalTester` gained an on-demand API: `Scan()`, `Measure(ap)`,
`StopMeasuring()`, `ReportIntervalSeconds`, `State`, `Target`, the `ScanCompleted` /
`StateChanged` / `TargetChanged` events, and parsed `AccessPointInfo` records (quoted SSIDs with
escapes). Things to know about it:

- **It lists networks, not just radios.** `ScanCompleted` and `LastScan` give one entry per radio, or per SSID and
  channel where a network has several radios there (`AccessPointGroups`; `IsGroup`, `RadioCount`, `Bssids`, the
  strongest radio's BSSID and RSSI). Measuring a group sends `SELECT ... group=ssid` when protocol 1.2 is
  negotiated, else selects its strongest radio. `MeasuredRadioCount` is the probe's `aps=`;
  `LatestReportingBssid` and `LatestRadiosHeard` come from each 1.2 SAMPLE's `from=` and `heard=`.

- **It stops at Ready.** In these scenes *Auto Start On Connect* is off, so after connecting the
  client waits for a network to be picked. Turn the option on to get the old one-shot behaviour:
  the target BSSID/channel, or else the strongest AP.
- **It manages the stream for you.** The firmware refuses SCAN and SELECT while streaming, so
  scanning, picking a network or changing the interval stops the stream first and restarts it.
- **Rejected commands keep the link.** An `ERR` reply (bad state, unsupported channel, …) is shown
  on the meter and in the menu, and the client goes back to Ready. Only handshake and transport
  failures disconnect.

### Real-world occlusion

Real things hide the voxel cloud: your hand and arm, people, furniture and walls. An
`EnvironmentDepthManager` (Meta XR Core's Depth API, under **Environment Depth** in both scenes)
reads the headset's depth sensing and turns on soft occlusion. The cloud, the room miniature
(`VoxelCloud`, `RoomPreview`, `MiniatureFog`) and the EMF meter (body and LEDs on Meta's
`EnvironmentDepth/OcclusionLit` and `OcclusionUnlit`, text on `MeterText`) compile the occlusion
keywords; the menu and the cyan calibration target don't, so they always stay visible. The meter
uses plain occlusion with no bias: the hand holding the controller sits behind and below it, and
anything real passing in front of it, such as the other hand, hides it. Hands
are kept in the depth map on purpose: the cloud forms right where you hold the probe. The room
miniature is occluded too: its room model and voxels per pixel, its fog with one depth test per
pixel where the fog starts, so fog partly behind a hand is shown or hidden as a whole.

Behind that, `RoomOccluders` (on **Recording**) draws invisible, depth-only copies of each local
room's walls, floor, ceiling and furniture from the MRUK room scan (`RoomOccluder.shader`: depth
only, no colour, so passthrough stays visible). These give sharp, steady edges where the depth
sensing is soft or noisy, and keep cells measured in the next room from floating through the
wall. Each copy is a child of its room anchor, so it follows world-lock moves, and stays off until
the room is localised, so an unaligned room never hides cells in the wrong place.

- Surfaces are set back 3 cm (furniture boxes shrunk 3 cm), so cells measured right against a
  wall or tabletop aren't cut in half.
- Doors and windows are cut out of their walls, so cells in the next room show through a doorway;
  a closed door is left to the depth sensing.
- Invisible walls (open-plan boundaries), lamps and plants don't occlude: there is nothing solid
  there, or their boxes are mostly air. Tables occlude only a 4 cm top, not the space underneath.
- They only know the room as scanned, as boxes and rectangles: moved furniture, people and hands
  are the depth sensing's job. Anything virtual behind them is hidden too, including the room
  miniature where it intersects a scanned table or couch.

It needs a Quest 3 or 3S and the spatial-data (scene) permission; until depth arrives, or on any
other device, the room occluders still apply once a room is loaded and localised. **WiFi Heatmap > Enable Real-World Occlusion**
turns it on in an existing project without rebuilding scenes (OpenXR Meta session and occlusion
features, Scene support required, the depth manager and room occluders in both scenes);
**Configure** does the same.

Depth is lower resolution than the display, so occluded edges are soft and can shimmer at a
distance and around thin objects. It adds a depth-texture sample to every cloud fragment, on top
of the cloud's existing overdraw. Neither the look nor the frame-rate cost has been checked on a
headset yet.

### Live voxel cloud

Open **Cloud** after selecting a radio in **Network**. The cloud grows while the probe is
measuring, whether or not **Record** is enabled. A localised room or manual anchor, valid
controller tracking and probe-to-host clock mapping are required. This uses measurement-time
pose interpolation, not the controller position when a BLE packet arrives. Simulated meter
readings are not added to the cloud. Save or load any pending mount adjustment before scanning.

- **Voxel size:** 2–50 cm, default 12 cm. A sparse 3D grid bins each reading in its anchor frame.
  Changing resolution rebins retained raw observations; changing it back restores their detail.
- **Opacity:** 5–80%, default 35%. A URP stereo-compatible billboard shader draws soft translucent
  cells with depth writes disabled. Passthrough remains visible. Cell edges overlap for a cloud
  appearance; empty cells are not inferred or filled.
- **Low / High dBm:** separate sliders spanning −150 to +10 dBm (the preview's accepted numeric limits), constrained to a minimum 5 dB gap.
  Defaults are −90 / −30. These set the full displayed range and the meter's normalization.
- **Auto scale:** defaults Off. When On, fit the low/high endpoints to the lowest/highest retained,
  accepted window-mean RSSI samples in the active cloud's resolved anchors. Endpoints round
  outward to whole dBm and maintain at least a 5 dB span. Live scans update as new extremes arrive;
  loading a saved session fits that dataset instead. Hidden live data does not override replay.
  With no resolved samples the previous range is retained. Manual dBm sliders are disabled while
  Auto is On; switching Off keeps the fitted range for manual adjustment. Color selections and
  gradient-handle positions stay as selected. Auto scaling changes color meaning as the range
  expands; use the labeled endpoints when comparing readings across scans.
- **Gradient bar:** drag the two white handles to set where the low and high colors anchor within
  that range. The middle color is halfway between those two positions. Labels show all three
  anchor values in dBm. Values outside the anchors clamp to the end colors. Moving the dBm
  endpoints preserves the handles' relative positions.
- **Choose 3 colors:** select Low, Middle or High and adjust its red, green and blue sliders.
  The entire cloud and legend recolor immediately. Display settings currently reset on launch.
- **Clear live cloud:** clears the preview; recorded files remain available. Starting another
  measurement stream (including a report-interval change), selecting a radio, reconnecting, or
  changing mount calibration starts a fresh live cloud. Loading a saved session temporarily
  hides the live preview; beginning a new stream returns to the live preview.

Estimator **v1** uses mean observed dBm with at most one contribution per cell per 250 ms,
not a linear-power average. All retained reports remain available for resolution changes.
Opacity is additionally reduced by a separate heuristic for contribution count, within-cell
position coverage and RSSI variance; stationary repetition cannot earn full confidence. Short
pose extrapolation reduces confidence further. Timing uncertainty above 15 ms remains flagged,
but can appear as a timing-degraded preview with proportionally lower confidence. A separate
100 ms uncertainty budget excludes live observations beyond that bound. This budget is a
prototype display policy, not a verified product accuracy tolerance. Degraded tracking and
unlocalised-anchor samples remain excluded; raw recording flags are preserved.

The live status now reports the current blocking condition (missing clock, missing pose history,
tracking loss, missing/unlocalised anchor, or excessive timing uncertainty) instead of one generic
waiting message. Preview opacity has a 20%-of-slider floor for occupied cells, so low-confidence
measured cells remain visible; this does not populate empty cells. Format-1 saved recordings
retain only a clock-quality flag, not the numeric per-sample uncertainty. Such flagged readings
can be viewed with a low confidence weight and an explicit "bound unavailable" replay status;
their precision is unknown. This confidence is a prototype heuristic, not measured RF or spatial
accuracy. Raw RSSI and flags are unchanged by display changes.

Each live or loaded display retains up to 100,000 accepted raw observations and draws up to
20,000 occupied cells. The Cloud/status text reports omitted data. Raw recording files are
not limited by these preview caps. New reports update bins immediately; mesh refreshes are
coalesced to 10 Hz live / 4 Hz replay. Very fine voxels and overlapping transparent clouds
increase headset rendering cost; alpha sorting is per anchor mesh, not per voxel.

Both saved scenes and the menu prefab include these controls. To regenerate their menus while
preserving the rest of the scenes, use **WiFi Heatmap > Update Cloud and Calibration Menus**.
Source compilation and menu generation completed with Unity 6000.3.22f1 targeting Android in an
isolated project copy. Quest visual quality, slider interaction and performance still need an
on-headset check; no APK was built for this change.

### Recording sessions

`Recording` (in both scenes) holds the recording and cloud components:
- **`AnchorRegistry`:** rooms from the headset's room scan, plus manual spatial anchors.
- **`SessionRecorder`:** writes sessions.
- **`SessionViewer`:** shows a saved session as a voxel cloud in the room.
- **`CloudSettings` / `LiveVoxelCloud`:** shared display settings and a live cloud fed by the
  recorder's timestamp-aligned sample event, even when disk recording is off.

Samples are stored relative to their anchor. A room anchor can turn or move when the room is edited or re-scanned
in Space Setup (old sessions then replay rotated, or not at all after a re-scan), so record surveys you want to
replay against a manual anchor. See [Room anchors vs. manual anchors](../docs/GUIDE.md#room-anchors-vs-manual-anchors).

Sessions are saved in `persistentDataPath/sessions/<id>/` (on the Quest:
`/sdcard/Android/data/com.wifiheatmap.prototype/files/sessions/`). The format is in
`../docs/session-writer-plan.md`. Copy them off with `adb pull`.

**Menu, Sessions tab**
- **Record / Stop recording:** works while the probe is measuring a real network (not the
  simulated signal). The status line shows duration, samples written, anchors used, and samples
  flagged or dropped. Dropped means no clock mapping yet, no tracked pose at that time, or no
  anchor.
- **Saved sessions** (newest first): **Load** bins a session's samples into a translucent cloud
  using the current Cloud settings. The live cloud is hidden while viewing a saved session;
  **Hide** restores it. **Delete** needs a second tap.
- **Unfinished sessions** (app killed mid-recording) are recovered on the next launch and marked
  "recovered".

**Menu, Anchors tab**
- **Auto (rooms):** the default. Each sample is stored relative to the room it was taken in,
  using the room scan from Space Setup. That's what lets one session span several rooms.
- **Place anchor at probe:** creates a manual spatial anchor where the probe is (kept level) and
  saves it on the headset. Use it where there's no room scan, or for a fixed reference point.
- **Use:** records every sample against that manual anchor. **Delete** (second tap) erases it from
  the headset; sessions recorded against it can't be shown afterwards.
- **Behaviour:** manual anchors reload on launch and show as orange markers. The anchor mode can't
  change while recording.

### Probe samples and clock sync

For recording (see `../docs/session-writer-plan.md`), the probe client also publishes:

- **`SampleReceived`** (`ProbeSample`): every SAMPLE field (session, sequence, window timing,
  BSSID/channel, rounded and mean RSSI, min/max, std-dev, frame count), the headset time the line
  arrived, and the measurement midpoint mapped onto the headset clock with an error bound
  (`HasHostTime`, `HostTimeUs`, `HostTimeUncertaintyUs`).
- **`Clock`** (`ClockMapping`): the probe-to-headset clock fit from PING/PONG, PINGed about once
  a second while connected. **`ClockExchanged`** reports each PING/PONG exchange.
- **Rejection:** samples from another stream session or another network are dropped and counted
  (`RejectedSampleCount`).

The parsing and clock code lives in the platform-neutral `WifiHeatmap.Core` assembly
(`Assets/Scripts/Core`). Its EditMode tests are in `Assets/Tests/EditMode`; run them from
**Window > General > Test Runner**, or headless:

```bash
~/Unity/Hub/Editor/6000.3.22f1/Editor/Unity -batchmode -nographics -projectPath "$PWD/unity" -runTests -testPlatform EditMode -assemblyNames WifiHeatmap.Core.Tests -testResults /tmp/editmode.xml -logFile -
```

### Signal sources

- **Real probe:** the `WiFiProbe` object's `Esp32BleSignalTester` connects over BLE on start
  (*Connect On Start*). `OnSignalStrengthDbm`, `OnStatus` and `OnError` are wired to the meter.
  With *Auto Start On Connect* turned on (off in both scenes), a blank target BSSID/channel locks
  onto the strongest AP.
- **No probe:** turn on **Simulated signal** in the menu's Meter tab, or enable the
  `SimulatedRssiSource` component on `WiFiProbe` (it's off by default). This also stops any real
  measurement. A virtual access point (a small sphere) appears 2 m in front of you. RSSI then follows a path-loss model from the antenna's
  position, so walking toward it lights the arc and speeds up the pulses. Never mix simulated
  and real readings.

## Building

**WiFi Heatmap > Build Quest APK** builds every scene enabled in the build settings (as checked in,
only `HeatmapBuilder`) to `Builds/WifiHeatmap.apk` (ignored by git). Running **Configure** resets the
build settings to enable both scenes, with `ProbeTester` first; disable it again before building. Install with:

```bash
adb install -r unity/Builds/WifiHeatmap.apk
```

From a terminal, close the editor first (a second Unity can't open the same project), then
run from the `Demo6/` directory:

```bash
~/Unity/Hub/Editor/6000.3.22f1/Editor/Unity -batchmode -nographics -quit -projectPath "$PWD/unity" -buildTarget Android -executeMethod WifiHeatmap.Editor.ProjectSetup.Configure -logFile -
```

```bash
~/Unity/Hub/Editor/6000.3.22f1/Editor/Unity -batchmode -nographics -quit -projectPath "$PWD/unity" -buildTarget Android -executeMethod WifiHeatmap.Editor.ProjectSetup.BuildQuestApk -logFile -
```

On first launch, the headset asks for Bluetooth and spatial-data (scene) permission. MRUK needs
a room scan; if the headset has none, it prompts you to scan.

## Not yet verified

- **Headset:** the app is used on a personal Quest 3 (BLE pairing, the menu, MRUK room loading, calibration,
  recording and the live cloud, with the probe on a controller and on the wrist, including for the demo video), but
  passthrough alignment, tracking accuracy and haptics haven't been measured systematically.
- **Network groups:** measuring a network's radios together (protocol 1.2) has only been tried where each network
  has one radio per channel; it hasn't been tried on campus Wi-Fi yet.
- **Probe offset:** an estimate until it's calibrated against the printed pod.
- **Menu ergonomics:** used in the headset, but text size, row spacing and reach haven't been tuned (the panel is
  52 × 36 cm at about 0.5 m).
- **Controller and hand visuals:** the interaction rig draws them over the real ones in
  passthrough. Turn them off on the rig if you'd rather see only the real controllers.
- **Firmware:** the BLE client still targets the firmware's Nordic-UART-style service with a
  bonded, passkey-authenticated link; see `../firmware/WiFiProbe/README.md`.

### BLE cloud timing investigation — 2026-10-29

On the connected Quest 3, screenshot `com.wifiheatmap.prototype-20261029-162624.jpg` showed
0 voxels / 0 samples while the meter was receiving measurements. A read-only snapshot of session
`20261029T202626Z-5c14` subsequently contained 1,452 CRC-validated records: 625 had flag 4
(ClockUncertain), and 827 had flag 5 (ClockUncertain + TrackingDegraded). No inspected record
had an unresolved-anchor flag. The recorded PING/PONG exchanges had round trips of
46.703 ms minimum, 81.464 ms median and 169.260 ms maximum. The previous renderer rejected
all ClockUncertain records, leaving zero visible cells even where tracking and alignment were valid.

The existing firmware-timestamp/clock-mapping/pose-history compensation remains in use. BLE
arrival delay is not used as measurement time, and RTT is not treated as a known one-way delay.
The fix distinguishes compensated sample timing from residual clock uncertainty, retains the
15 ms quality flag, shows a faded timing-degraded preview within a separate 100 ms budget,
and explains the current rejection cause. The cloud still rejects invalid tracking/alignment.
Signed Java byte-array writes also replace obsolete Unity JNI byte[] writes, which were flooding
logcat on every PING. The new preview policy requires headset validation; no spatial accuracy
claim is inferred from these transport measurements.

### Android BLE callback timestamps

Both GATT `onCharacteristicChanged` overloads timestamp entry immediately using
`SystemClock.elapsedRealtimeNanos() / 1000`. Each notification carries that timestamp with its
Base64 payload through the Android main Handler, JNI callback proxy, C# inbound queue and Unity
line framing. A line spanning several notifications uses the timestamp of the final chunk
containing its newline. This receive time feeds both PONG synchronization and raw radio samples;
measurements still use their firmware timestamp and interpolated historical antenna pose.

`AndroidMonotonicClock` installs the bridge's same Android boot-time monotonic clock into
`HostClock` before scenes load. PING send times and pose-history timestamps therefore share the
callback's origin and microsecond units, including across deep sleep. No assumption is made that
Java and C# Stopwatch clocks share an epoch. Editor/desktop code retains the Stopwatch fallback.
The time source is installed once during player startup, never changed on BLE reconnect.

The **Probe** tab's RX diagnostics show:

- **C#:** elapsed time from GATT callback entry until the C# proxy receives the payload. This
  includes encoding, Android main-queue scheduling and JNI dispatch.
- **frame:** elapsed time from GATT callback entry until Unity dequeues the notification.
- **peak C#:** maximum callback-to-C# dispatch delay since the current connection started.

The earlier C# timestamp was already captured before Unity's Update processing, but after the
Android Handler queue. The new timestamp removes that earlier queue/encoding/dispatch wait from
receive timing; it does not remove radio latency, outbound command queueing, notification
fragmentation or clock-synchronization asymmetry. The common-clock adapter adds a cached JNI
clock query when capturing poses/PING times; its runtime cost and improvement need on-device
measurement. Original quality flags and timing-degraded preview policy remain in effect.

The updated Java bridge compiled against Android API 36, and Unity 6000.3.22f1 compiled Android
player scripts, including the device-only JNI branches. Both saved scenes and the menu prefab
include the new diagnostics. This change has not been deployed or measured on the headset:
rebuild the whole APK so its Java listener and C# proxy signatures are updated together.

### Auto range and meter readout update — 2026-10-29

Added the Cloud **Auto scale** toggle for live and loaded datasets, using retained accepted sample
extrema with a minimum 5 dB display span. Range fitting precedes mesh recoloring, so cells and the
legend use the same range in a refresh. Ranges use the complete retained data in resolved groups,
including any cells omitted by the preview drawing cap; recordings remain unchanged. The range
supports the preview's −150 to +10 dBm numeric validation limits without asserting these are
verified hardware RF capabilities.

The meter already had an RSSI text binding, but its saved labels lacked explicit font/material
references and its central readout used a small ellipsis container. The update assigns the TMP
font/material, enlarges the center readout, makes it bold and removes clipping truncation. The
meter prefab and both scenes retain their original following/placement settings. On-headset
readability and auto-range behavior still need checking after rebuilding the APK.

## Room miniature and in-app Space Setup

Open **Preview** in the menu. Show the model, switch **Rooms & boxes / Scanned mesh**, hide the
ceiling, and optionally overlay the live or loaded heatmap. Grip near it with one controller, or
pinch near it with one tracked hand, to move/rotate; use both to scale. A slider and Recenter / fit
are available. The original room anchors stay unchanged.

Two arrows mark where things are in the room: a **yellow** one at your head, pointing where you look, and a smaller
**cyan** one at the probe's antenna, pointing where its controller points (shown while the controller is tracked).

**Scan room** opens Quest's guided system Space Setup. **Reload room** reads the existing room.
Both finish the current recording and stop measuring before loading room data; explicitly select
a network again to resume. A heatmap can keep building against an already loaded room while you
view the model, but this does not scan/rebuild the environment continuously in the background.
Missing mesh data and unresolved rooms are reported. On-device scan/interaction verification is
still required; see [Preview implementation and limitations](#unity-assets-scripts-preview).

### Experimental miniature fog

Preview's Heatmap button now cycles **Fog / Voxels / Hidden**, starting in Fog. Fog quality cycles
16 / 24 / 48 samples per ray. It blends nearby measured cell estimates through a bounded 3D
texture with front-to-back ray marching. Its signal colors and dBm range follow the Cloud tab; its
**density** is its own (Preview → Fog settings), so the Cloud tab's opacity sets only the full-size
cloud, which retains soft voxels. This is a rendering prototype with unmeasured headset
GPU cost; see [method and limitations](#unity-assets-scripts-preview).

**Preview → Fog settings** sets the fog's **Density** (0.05 to 4, on a curve that keeps the low end
fine; the old ceiling, through the Cloud opacity, was 0.8) and adds optional density weighting by
relative signal strength. Enable
Signal density and adjust Signal effect: 0% preserves the base density; 100% weights the low
dBm endpoint at 0.2× density and the high endpoint at 2×. Cloud low/high endpoints (including
auto scaling) define relative strength. Coverage/confidence and the fog's density still apply. A
menu built before the density slider existed gets it added at runtime; **Update Cloud and
Calibration Menus** builds it into the prefab.

## Detailed reference

This reference consolidates the former folder-level Unity READMEs. The source path is shown at each section; paths in code ticks are relative to that source folder unless stated otherwise.

- [Assets/Editor — Unity Editor setup and migrations](#unity-assets-editor)
- [Assets/Materials — Shared materials](#unity-assets-materials)
- [Assets/Oculus — Meta project configuration](#unity-assets-oculus)
- [Assets/Plugins/Android — Android manifests and BLE plugin](#unity-assets-plugins-android)
- [Assets/Plugins/Android/WiFiProbeBle.androidlib — WiFiProbe native BLE bridge](#unity-assets-plugins-android-wifiprobeble-androidlib)
- [Assets/Plugins — Platform integration](#unity-assets-plugins)
- [Assets/Prefabs — Application prefabs](#unity-assets-prefabs)
- [Assets — Unity assets](#unity-assets)
- [Assets/Resources — Runtime Meta and input settings](#unity-assets-resources)
- [Assets/Scenes — Saved scenes](#unity-assets-scenes)
- [Assets/Scripts/Core — Platform-neutral core](#unity-assets-scripts-core)
- [Assets/Scripts/Core/Sessions — Recording data and pose history](#unity-assets-scripts-core-sessions)
- [Assets/Scripts/Heatmap — Translucent voxel-cloud preview](#unity-assets-scripts-heatmap)
- [Assets/Scripts/Meter — Live signal meter and movement haptics](#unity-assets-scripts-meter)
- [Assets/Scripts/Preview — Room miniature and guided scan](#unity-assets-scripts-preview)
- [Assets/Scripts/Probe — Probe mounting, clock adapter and simulation](#unity-assets-scripts-probe)
- [Assets/Scripts — Runtime application modules](#unity-assets-scripts)
- [Assets/Scripts/Recording — Spatial placement, capture and replay](#unity-assets-scripts-recording)
- [Assets/Scripts/UI — World-space menu controls](#unity-assets-scripts-ui)
- [Assets/Settings — URP and rendering configuration](#unity-assets-settings)
- [Assets/Shaders — Rendering shaders](#unity-assets-shaders)
- [Assets/StreamingAssets — Streaming assets](#unity-assets-streamingassets)
- [Assets/Tests/EditMode — Core EditMode cases](#unity-assets-tests-editmode)
- [Assets/Tests — Unity checks](#unity-assets-tests)
- [Assets/XR — OpenXR loader and feature assets](#unity-assets-xr)
- [Packages — Unity package declarations](#unity-packages)
- [ProjectSettings — Shared Unity project settings](#unity-projectsettings)

<a id="unity-assets-editor"></a>
## Unity Editor setup and migrations

*Source folder: `Assets/Editor`*

`ProjectSetup.cs` creates project configuration, generated materials/prefabs, MR rigs and menus.
It includes Android player/build settings, OpenXR/Meta configuration, URP and scene construction.

#### Menu commands

| Command under WiFi Heatmap | Effect |
|---|---|
| Configure Quest 3 MR Project | Rebuild configuration, generated prefabs and both authored scenes; can replace manual edits |
| Update Cloud and Calibration Menus | Refresh menu/readout/room-preview wiring in both saved scenes while retaining rigs and recording systems; also adds the depth manager if missing |
| Enable Real-World Occlusion | OpenXR Meta session/occlusion features, Scene support required, the meter's occluding materials, and an `EnvironmentDepthManager` plus `RoomOccluders` in both saved scenes, without rebuilding them |
| Build Quest APK | Build the enabled build-settings scenes to `Builds/WifiHeatmap.apk` |
| Validate Probe Controller Mount | Left-controller default parenting, meter target, mount identity, estimated transforms, and the mirrored move to the right controller and back, in both saved scenes |
| Validate Calibration Controls | Panel references, unsaved state, side-switch refusal and recording-lock checks |

`ProbeControllerChecks.cs` holds the probe-controller scene assertions. `CalibrationFeatureChecks.cs`
holds the calibration assertions and combined update/check entry point. These are development
checks, not hardware validation or a guarantee about manually changed scenes.

#### Batch entry points

- `UpdateCloudMenusBatch`: migrate saved menus/readout and exit a batch Editor.
- `EnableRealWorldOcclusionBatch`: apply **Enable Real-World Occlusion** and exit a batch Editor.
- `CompileBleCallbackTimingBatch`: migrate and compile Android **player** scripts, including
  device-only JNI branches, without making an APK. The name originated with the timing work;
  it currently exercises compilation of the whole player script set.

Never run a second Editor against a project already open in an Editor. Earlier migrations used
an isolated Assets/Packages/ProjectSettings copy, then copied only changed saved assets back
after confirming originals had not changed. Preserve unsaved interactive work and prefab GUIDs.

Adding fields to a menu script does not wire old saved objects automatically. Update the builder
and migration, and retain `.meta` files when copying assets. No tests/builds run merely because
these commands are documented here.


<a id="unity-assets-materials"></a>
## Shared materials

*Source folder: `Assets/Materials`*

`EmfBody`, `EmfFace`, `EmfMetal` (Meta's `EnvironmentDepth/OcclusionLit`) and `EmfLed`
(`EnvironmentDepth/URP/OcclusionUnlit`) style the meter, so real hands and people hide it; the LED
colors are applied at runtime using material property blocks (`_BaseColor`). `MeterText` is the TMP
font material for the meter's labels on the occluded `MeterText` shader; the menu keeps the stock
font material. `ProbePlaceholder` is the base material for the probe target
(ball and arrows, tinted per part at runtime). `AnchorMarker` styles manual anchor indicators.

`VoxelCloud.mat` references the custom [voxel-cloud shader](#unity-assets-shaders) and receives
per-cell mesh colors/opacity. Keeping this material as a referenced asset makes the custom shader
available to player builds. `SessionPoints.mat` is retained from the earlier opaque-marker viewer;
the current replay path uses the voxel cloud.

`RoomPreview.mat` references the translucent vertex-color room miniature shader. Its saved
reference keeps that shader available in player builds.

TextMesh Pro font materials live with the imported font resources rather than this directory.
Keep material `.meta` files and shader GUID references together. Editor project setup/migrations
create or update shared materials; inspect intentional visual changes before regenerating.

`MiniatureFog.mat` retains the miniature volume shader. Each runtime miniature creates a material
instance for its own 3D field; source materials are not mutated.


<a id="unity-assets-oculus"></a>
## Meta project configuration

*Source folder: `Assets/Oculus`*

`OculusProjectConfig.asset` contains Meta project/device/feature declarations used by the SDK
and build tooling: intended Quest 3/3S targets, passthrough, controller/hand input, scene support
and spatial-anchor requirements.

This is configuration, not a list of hardware gates that have passed. Keep the asset's identity
and compare changes with the Android manifest and XR settings. Meta's Project Setup Tool can
modify related assets; full project Configure applies setup fixes and regenerates content.

Platform/runtime optional-tool settings also appear in [Resources](#unity-assets-resources).
Local credentials are excluded from the repository. See the [root architecture](../AGENTS.md)
for the remaining exact-device verification work.


<a id="unity-assets-plugins-android"></a>
## Android manifests and BLE plugin

*Source folder: `Assets/Plugins/Android`*

| Path | Purpose |
|---|---|
| `AndroidManifest.xml` | Application activity, Quest passthrough/scene/anchor/hand requirements and current boundaryless feature declaration |
| [WiFiProbeBle.androidlib/](#unity-assets-plugins-android-wifiprobeble-androidlib) | Native Java BLE bridge and Bluetooth permissions merged during the build |

The saved manifest includes `com.oculus.feature.BOUNDARYLESS_APP` as required. This records the
current configuration; platform availability and behavior still need exact OS/device verification.
A future full Configure run may regenerate manifests/settings, so preserve intentional overrides.

The app uses `com.wifiheatmap.prototype`, GameActivity, and Android OpenXR/Meta configuration
from ProjectSettings. Bluetooth permission handling is performed in the Unity client and merged
plugin manifest. No signing keys or machine-specific SDK properties belong in this directory.

Use Unity's Android build for normal packaging. Standalone Java compilation was previously done
against API 36 as a source check; the project's application target API is a separate setting.
See [the Unity build guide](#wifi-heatmap--quest-3-mixed-reality-project).


<a id="unity-assets-plugins-android-wifiprobeble-androidlib"></a>
## WiFiProbe native BLE bridge

*Source folder: `Assets/Plugins/Android/WiFiProbeBle.androidlib`*

Source: `src/main/java/com/wifiheatmap/ble/Esp32BleBridge.java`.
The Android library manifest contributes Bluetooth permissions and the source is packaged by Unity.

#### Transport behavior

The bridge discovers the requested device/service, coordinates bonding, opens GATT, discovers
characteristics, requests an ATT MTU of 247 (optional: after a refusal or 3 s without a reply it carries on at
the current MTU), and enables TX notifications. Firmware 0.3.1+ then sends notifications of up to 244 bytes;
older firmware keeps 20-byte ones, which arrive the same way. RX writes are queued in ordered 20-byte chunks.
A SCAN that stalls (30 s with no result, or 5 s after the last one without `SCAN_DONE`, as when a lost BLE
packet drops the final line) ends with the results received and the probe back at Ready, instead of leaving
the menu on "Scanning..."; late lines from that scan are ignored by command id.
It exposes listener state/error events plus received bytes encoded as Base64 for the JNI proxy.
The UUIDs match the firmware's Nordic-UART-style service. Pairing and disconnect failures must
remain visible; do not silently erase bonds or weaken authentication.

#### Receive timing contract

Both notification callback overloads call `monotonicTimeUs()` immediately on entry. That method
returns `SystemClock.elapsedRealtimeNanos() / 1000`, a monotonic boot-time clock including sleep.
The listener signature is `onBytes(String base64, long receivedUs)`. The timestamp is retained
through Base64 encoding and Android main-Handler dispatch instead of being replaced at dispatch.

Unity's Android clock bootstrap reads this same static method for PING send times and antenna
pose history, so no Java/Stopwatch epoch assumption is needed. C# reassembles lines and uses the
final notification's timestamp for a complete line. The Probe tab exposes callback-to-C# and
callback-to-frame delays. Radio delay, outbound queueing, fragmentation and PING asymmetry remain.

Do not parse WiFiProbe or attach poses in Java. Do not touch Unity scene objects in JNI callbacks.
The Java listener and C# proxy must be rebuilt together after interface changes. Static source
compilation is not a hardware pairing, lifecycle or throughput validation.


<a id="unity-assets-plugins"></a>
## Platform integration

*Source folder: `Assets/Plugins`*

The current native integration is [Android/](#unity-assets-plugins-android). It contains the Quest Android
manifest and a Java BLE GATT bridge packaged as an `.androidlib` source plugin.

The bridge is a transport adapter. It owns discovery, permissions/bonding/GATT state, ordered
writes, notifications and native callback timestamps. Unity owns authoritative protocol parsing,
session validation, clock mapping, pose fusion and visualization.

There is no Android USB-host bridge in the current source snapshot. The architecture's USB
pipeline is a target extension with explicit verification gates, not implemented functionality.
Keep native API types behind this boundary and rebuild the whole APK for Java/C# signature changes.


<a id="unity-assets-prefabs"></a>
## Application prefabs

*Source folder: `Assets/Prefabs`*

| Prefab | Responsibility |
|---|---|
| `WiFiProbe.prefab` | BLE client configuration and meter event source |
| `ProbeMount.prefab` | Probe pod placeholder and antenna child (scenes place it on the left controller) |
| `EmfMeter.prefab` | LED arc, bold dBm readout, scale/status/recording labels and haptics |
| `ProbeConfigMenu.prefab` | Eight-tab world-space menu with serialized UI control references |

Scene setup supplies systems such as the active controller/head transform, recorder, anchors and
viewer. Prefab references alone do not establish device compatibility or current room alignment.
The mount's default pose is an estimate and can be replaced by a validated local calibration.

The meter number displays latest RSSI independently of LED smoothing. Its font/material and
readout bounds are explicit; range-end labels follow CloudSettings. The menu includes manual and
auto range controls and BLE receive-delay diagnostics.

Editor setup generates these assets. The narrower migration updates menus/readouts in existing
scenes; full Configure can replace more content. Preserve existing `.meta` identities and review
scene overrides when regenerating. See [Editor tools](#unity-assets-editor).


<a id="unity-assets"></a>
## Unity assets

*Source folder: `Assets`*

Authored application assets and imported resources for the Quest prototype. Keep every tracked
asset with its `.meta` file: GUIDs connect scenes, prefabs, scripts, materials and configuration.

| Directory | Responsibility |
|---|---|
| [Scripts/](#unity-assets-scripts) | Runtime protocol, timing, probe, recording, visualization and menu modules |
| [Editor/](#unity-assets-editor) | Project/prefab/scene setup and migration commands |
| [Scenes/](#unity-assets-scenes) | Saved application and development scenes |
| [Prefabs/](#unity-assets-prefabs) | Probe mount, BLE probe client, meter and setup menu |
| [Materials/](#unity-assets-materials) | Meter, mount, anchor and cloud materials |
| [Shaders/](#unity-assets-shaders) | Voxel-cloud, room-preview, miniature-fog, room-occluder and meter-text shaders |
| [Plugins/](#unity-assets-plugins) | Android native BLE integration and manifest |
| [Tests/](#unity-assets-tests) | Existing Editor-only Core checks |
| [Resources/](#unity-assets-resources) | Runtime Meta/input settings |
| [Settings/](#unity-assets-settings) | URP renderer/pipeline and volume settings |
| [XR/](#unity-assets-xr), [Oculus/](#unity-assets-oculus) | OpenXR and Meta project configuration |

`TextMesh Pro/` contains imported fonts/shaders/resources with bundled attribution. `TutorialInfo/`,
`Readme.asset`, the sample scene and template input actions are retained template assets.
These vendor/template trees do not need duplicate project module guides. Package source belongs
in the package cache, not here; package versions live in `../Packages`.

See [the Unity operating guide](#wifi-heatmap--quest-3-mixed-reality-project) before regenerating scenes. Generated imports,
editor caches and player output are excluded from Git; required source assets are not.


<a id="unity-assets-resources"></a>
## Runtime Meta and input settings

*Source folder: `Assets/Resources`*

This folder contains Meta/Oculus runtime, overlay, build/platform-tool and immersive-debugger
settings plus the imported/generated input-action asset. Unity Resources assets can be loaded
by SDK code using their asset names; rename/delete them deliberately.

The local `DevAgentSettings.asset` and its `.meta` are ignored because the existing file contains
a device-specific debugger access token and local endpoint. They are not needed as a shared
credential for the Wi-Fi probe. Configure optional development tooling locally if it is used.
Do not copy live tokens into shared configuration or documentation.

Project-wide XR and device declarations also live in [XR/](#unity-assets-xr),
[Oculus/](#unity-assets-oculus) and [ProjectSettings](#unity-projectsettings).
These files describe configuration, not hardware validation results.


<a id="unity-assets-scenes"></a>
## Saved scenes

*Source folder: `Assets/Scenes`*

| Scene | Current role |
|---|---|
| `HeatmapBuilder.unity` | Main mixed-reality app; the only enabled scene in current build settings |
| `ProbeTester.unity` | Retained development scene, currently disabled in build settings |
| `SampleScene.unity` | Original template/sample asset; not an application entry point |

Both authored scenes contain the probe mount (on the left controller by default), meter, BLE probe client, MRUK/anchor
registry, recorder/viewer, live cloud and world-space configuration menu. Scene names alone do
not imply different feature completeness.

`ProjectSettings/EditorBuildSettings.asset` determines the actual enabled scenes. Full project
setup recreates both authored scenes and re-enables both build entries. The custom APK builder
builds whichever scenes are enabled there.

Prefer the menu migration for control/readout changes, and keep manual rig/room changes intact.
Do not hand-assign arbitrary anchor transforms to make unresolved saved data appear aligned.
Preserve scene `.meta` GUIDs, and reload externally updated saved scenes before rebuilding.

Both scenes also include the detached room-preview components under the recording systems and
a **Preview** menu tab. Scan room launches guided Quest Space Setup after ending any current
recording; Reload room refreshes existing data. Runtime miniature objects never replace tracked
room anchors. See [Preview](#unity-assets-scripts-preview).


<a id="unity-assets-scripts-core"></a>
## Platform-neutral core

*Source folder: `Assets/Scripts/Core`*

Assembly: **WifiHeatmap.Core**. Its asmdef disables engine references and explicitly references
Newtonsoft.Json. This layer must not depend on Unity, Android, Meta SDK or ESP-IDF types.

| File | Responsibility |
|---|---|
| `ProbeFields.cs` | Token/field parsing and protocol validation helpers |
| `ProbeSample.cs` | Immutable radio samples, PING exchanges and line parsers |
| `ClockMapping.cs` | Repeated-exchange offset/drift mapping and uncertainty estimates |
| `HostClock.cs` | Monotonic microseconds, platform time-source hook and Stopwatch fallback |
| `ProbeCalibration.cs` | Versioned rigid-transform documents, validation and atomic file persistence |
| `VoxelGrid.cs` | Sparse measured cells and bounded-rate observed-dBm aggregation |
| `SpatialDisplayQuality.cs` | Allowed quality flags, timing budget and confidence weighting |
| `SignalDisplayRange.cs` | Whole-dBm extrema fitting with a minimum 5 dB span |
| [Sessions/](#unity-assets-scripts-core-sessions) | Pose history, recording schema, block storage and recovery |

#### Timing

ClockMapping ignores round trips over 250 ms, uses low-round-trip exchanges from a 120 s window,
and fits drift after a sufficient span. The uncertainty includes round-trip bounds, fit residual
and extrapolation allowance. A mapping is not a calibrated claim of spatial accuracy.

HostClock is configured before Android scenes load to use the same monotonic source as the
GATT callback. Do not change its provider midway through a session. Editor/desktop callers use
Stopwatch. Wall-clock UTC is metadata, never a pose-fusion clock.

#### Grid and display policy

Grid coordinates use floor division, including negative positions. Estimator v1 averages observed
dBm with at most one contribution per cell per 250 ms; it is not a linear-power average. Confidence
is heuristic and separate from RSSI. Current live display allows faded timing-degraded samples
up to 100 ms known uncertainty, while retaining the 15 ms precision flag. Tracking/alignment
failure remains excluded. Old recordings lack a numeric per-record timing bound.

Existing tests are in [Tests/EditMode](#unity-assets-tests-editmode). Newer range, voxel and
callback-adapter behavior needs additional coverage and exact-device validation.

#### Fog display field

`FogField` builds a bounded (maximum 64 texels per axis) miniature display field from existing
cell estimates. It uses compact interpolation support, keeps confidence independent from RSSI,
and supports cancellation. Its result is rebuildable display data; it does not replace raw
recordings, the measured voxel estimator, or room alignment. See [miniature fog](#unity-assets-scripts-preview).

`MotionTimingRisk` provides the finite/range-checked speed × timing uncertainty calculation for
movement haptics. The Unity adapter normalizes it against voxel size; this does not change clock
mapping or raw sample acceptance. See [movement guidance](#unity-assets-scripts-meter).


<a id="unity-assets-scripts-core-sessions"></a>
## Recording data and pose history

*Source folder: `Assets/Scripts/Core/Sessions`*

Pure Core code for file-format version 1 and timestamped antenna-pose lookup.

| File | Responsibility |
|---|---|
| `PoseHistory.cs` | Bounded monotonic pose history, interpolation and short bounded extrapolation; also the legacy three-band SignalScale type |
| `SampleRecord.cs` | 48-byte record, quality flags, numeric conversions and CRC-32 |
| `SessionHeader.cs` | Session/AP/probe/anchor/mount metadata and optional full calibration snapshot |
| `SessionFiles.cs` | Writer, reader, block checks, atomic header updates, listing, recovery and deletion |

Each session contains `session.json`, `samples.bin` and `clock.jsonl`. The binary file begins
with a 32-byte header; each block contains a count/reserved header, up to 64 records and CRC-32.
Readers ignore an incomplete/damaged tail rather than treating it as observations.

A record stores measurement host time, anchor-relative antenna position/orientation, RSSI mean,
within-window deviation, frame count, sequence, anchor index and flags. Positions use metres,
Unity axes and System.Numerics types. Anchor UUIDs live in the session header's indexed table.
Format 1 stores a clock-quality flag but not its numeric uncertainty in every record.

PoseHistory interpolates between surrounding timestamps and permits only the caller's explicit
short extrapolation limit. It carries tracking degradation through lookup. Clear history after
calibration/lifecycle discontinuities; a current transform is not a substitute for a missing pose.

The runtime [recording adapter](#unity-assets-scripts-recording) owns Unity transforms and anchor
resolution. See [file-format notes](../docs/session-writer-plan.md) for the full
layout and the distinction between original proposals and implemented behavior.


<a id="unity-assets-scripts-heatmap"></a>
## Translucent voxel-cloud preview

*Source folder: `Assets/Scripts/Heatmap`*

| Type | Responsibility |
|---|---|
| `CloudSettings` | Shared voxel size, opacity, colors, gradient anchors, manual/auto dBm range and meter scale |
| `LiveVoxelCloud` | Subscribe to spatial observations, handle stream/calibration resets, choose preview visibility |
| `VoxelCloudLayer` | Retain bounded observations, bin by anchor, rebin resolution changes and generate cloud meshes |

The preview is a sparse **3D measured-cell cloud**, not a finished MRUK surface heatmap or RF
reconstruction. Every displayed cell comes from observations. Soft billboard skirts overlap for
appearance; empty voxels are not filled with inferred weak signal.

Resolution is 2–50 cm (12 cm default). Changing it rebins retained raw observations. Opacity is
5–80% (35% default), modulated by an independent confidence heuristic and a visibility floor for
occupied cells. The shader uses alpha blending and no depth writes. Sorting is per anchor mesh.

#### Range and colors

Three RGB colors blend through low/middle/high anchors. Two handles set the low/high color
anchor positions within the dBm range; the middle is halfway between. Separate endpoint sliders
set the full range. **Auto scale** uses the active resolved dataset's lowest/highest accepted
window-mean samples, rounded outward with at least a 5 dB span. It preserves colors/handle
positions, disables manual endpoints while on, and retains the fitted range when switched off.
Hidden live data cannot override a loaded session's range.

#### Bounds and quality

Each layer retains at most 100,000 accepted observations and draws at most 20,000 occupied cells.
Limits do not truncate raw disk recordings. Mesh changes are coalesced to 10 Hz live / 4 Hz replay.
Resolution, color/range and anchor changes dirty/rebuild meshes without altering measurements.

Tracking/anchor failure is excluded. Known timing uncertainty above 100 ms is excluded live;
less precise timing remains flagged and reduces confidence. Replay of older timing-flagged records
has an explicitly unknown bound. Dense stationary repetition cannot establish full confidence.
See [Core policies](#unity-assets-scripts-core) and [shader details](#unity-assets-shaders).

Miniature fog reads resolved `CloudCell` display snapshots (mean observed dBm, confidence and
anchor-local center), capped at the same 20,000-cell limit. `DatasetIdentity` changes whenever a
layer is cleared/replaced, allowing asynchronous visual builders to discard stale results.
See [Preview](#unity-assets-scripts-preview) for the independent volume renderer.


<a id="unity-assets-scripts-meter"></a>
## Live signal meter and movement haptics

*Source folder: `Assets/Scripts/Meter`*

`EmfMeter` presents the latest integer RSSI as a bold numeric **dBm** readout plus a ten-LED arc.
It is fixed just beyond the probe pod's front, square to the controller, screen facing the pod's top (see [EMF meter](#emf-meter-emfmeter--emfhaptics)),
and shows status and recording indicators.
The number is the latest incoming RSSI, independent of LED smoothing. When data is stale (default
2.5 s), it becomes `-- dBm`. Simulation can drive the visual display.

`CloudSettings` supplies the display range, including auto scaling. Endpoint labels follow that
range; the raw dBm and recorded observations stay unchanged.

#### Movement guidance

`EmfHaptics` now warns about moving too quickly for the **clock uncertainty and voxel width**.
Its pulse strength/rate do not depend on RSSI or the selected signal color/dBm range.

```text
estimated timing displacement = antenna speed [m/s] × time uncertainty [s]
voxel fraction = estimated timing displacement / current voxel width [m]
```

- Below **10% of a voxel**: silent.
- From **10% to 50%**: pulses rise from 1.5 to 14 per second, with increasing amplitude/frequency.
- Around **50% or greater**: a sustained warning buzz. Slow down to reduce it.
- Fresh measurements but missing/stale clock mapping: a distinct **double tap every 1.5 s**.
  Unknown accuracy is not treated as zero error. Meter-menu diagnostics explain the state.
- No active stream, stale readings, missing tracking/input focus, or unfinished/unsaved calibration:
  haptics stop. Simulated RSSI alone cannot trigger movement warnings.

Defaults are initial guidance heuristics, configurable as fractions on `EmfHaptics`. Thresholds
track `CloudSettings.Resolution` immediately, including while moving. For example:

| Voxel width | Clock uncertainty | Pulses begin at approximately | Full warning at approximately |
|---|---|---|---|
| 12 cm | ±50 ms | 0.24 m/s | 1.20 m/s |
| 4 cm | ±50 ms | 0.08 m/s | 0.40 m/s |
| 12 cm | ±10 ms | 1.20 m/s | 6.00 m/s |

The existing Meter-tab toggle enables/disables movement haptics. That tab also shows smoothed
antenna speed, current clock uncertainty, estimated displacement, and its percentage of a voxel.

#### Timing and pose inputs

Antenna positions are sampled in the rig's **tracking space** using `HostClock`, not the
meter. This includes motion of the offset antenna during controller rotation, while canceling
MRUK's world-lock changes to the rig. Signed velocity is exponentially smoothed over 80 ms before
its magnitude is taken, reducing stationary jitter bias. Focus loss, calibration changes, tracking
loss, tracking-origin changes, and pose gaps/jumps reset the estimator and stop vibration.

The uncertainty is recomputed for the latest report from the current `ClockMapping` fit and aged
conservatively by its drift allowance. Mapping age over 5 s, a boot-ID mismatch or failed mapping
is unknown. Samples older than 2.5 s are stale. BLE/Unity dispatch delay is not substituted for
clock uncertainty. Display and pulse timing use monotonic/unscaled time.

`Core/MotionTimingRisk` contains the finite/range-checked scalar calculation without Unity or Meta
types. These values guide scanning; they are **not a calibrated bound on total position error**.
Acceleration, pose uncertainty and movement during an RSSI aggregation window are additional
sources of error. Existing sample acceptance, confidence and recording rules remain authoritative.

The prefab's TextMeshPro readout explicitly references its font/material. Editor migration wires
probe, mount, tracking-space and shared settings references into both saved scenes. Android script
compilation is a software check; vibration feel, thresholds, jitter and lifecycle behavior still
need on-device verification. No automated tests were added or run for this change.


<a id="unity-assets-scripts-preview"></a>
## Room miniature and guided scan

*Source folder: `Assets/Scripts/Preview`*

This is a prototype display of the room model exposed by MRUK **207.0.1**. The Preview tab is
wired into both authored scenes. Its objects are independent visual copies; recording anchors,
raw observations, and calibration transforms are never moved or rescaled.

#### Controls

- **Show preview:** place a fitted model about 85 cm forward and 45 cm below the viewer's eyes.
- **Rooms & boxes / Scanned mesh:** switch between labeled plane rectangles/furniture volume
  bounds and a copy of the room's global mesh. Missing global geometry is reported explicitly.
  Plane rectangles are semantic bounds, not a reconstructed polygon mesh or holes in walls.
- **Ceiling:** hide semantic ceiling anchors. The raw-mesh cutaway omits triangles entirely
  within 15 cm of the room's top; it is approximate and may cut high objects as well.
- **Heatmap:** cycle Fog / Voxels / Hidden for the active live cloud or loaded saved session using the same colors,
  opacity and confidence fade. Unresolved samples remain hidden. Only geometry intersecting
  the current room bounds is copied into the model; a dataset is never silently realigned.
- **Grip near the model with one controller:** move and rotate it. **Grip with both:** scale
  around the two-controller midpoint. Release before grabbing again after focus/tracking loss,
  recentering, or changing the slider. Hand-tracking pinch gestures are not implemented here.
- **Scale:** uniform scale from 1:50 to 1:1. **Recenter / fit:** restore a model about 65 cm wide
  (or tall, if height is the largest dimension). The yellow arrow marks the tracked headset.

The model stays visible when the menu is closed. Measurements can continue to accumulate while
the model is being manipulated; only display-copy transforms change.

#### Room scanning

**Scan room** invokes `OVRScene.RequestSpaceSetup()` on the standalone Quest application.
This is Quest's guided system Space Setup, not a continuously updating environmental mesh.
The system owns its UI, boundary rules, and capture behavior. It can take focus away from Unity.
**Reload room** reads existing room data without launching another capture flow.

Both actions cancel active mount adjustment, finish the current recording, stop probe streaming,
and require the probe to be idle before room loading. A pending probe operation must finish
before starting. After capture returns and focus is restored, the app calls
`MRUK.LoadSceneFromDevice(false)`; cancellation/errors are shown. No recording is automatically
resumed. Select a network again to measure and start a new recording when ready.

Existing session files are retained. Room UUID changes or localization failure can leave older
sessions unresolved; the app does not move them onto a replacement room. Displaying the model
requires a room resolved through `AnchorRegistry`, including the existing world-lock policy.

This does not bypass Quest boundary setup or permit a room scan to run unobtrusively during
heatmap capture. A heatmap can build against an already loaded room. Continuous reconstruction
would need a separate verified spatial-mapping capability and alignment policy.

#### Implementation boundaries

| Type | Responsibility |
|---|---|
| `MrukPreviewSource` | Read MRUK semantic bounds/global mesh; own geometry copies; observe room changes |
| `PreviewGrabInput` | Touch controller tracking, grip and world poses |
| `MiniatureFog` | Bounded 3D field build/upload, proxy volume and runtime fog resources |
| `RoomPreview` | Detached model, controller manipulation, borrowed cloud display meshes, headset marker |
| `RoomScanController` | Stop measurement/recording, invoke system capture and reload, report lifecycle/errors |
| `UI/RoomPreviewPanel` | Buttons, slider, and user-visible state |

Cloud meshes and their materials are borrowed read-only from `VoxelCloudLayer.CopyVisuals` and
refreshed at 10 Hz. They are destroyed only by their owning cloud layer. The miniature owns and
disposes its semantic/global mesh copies and marker mesh. The cloud shader applies object scale
to billboard width so miniature voxels scale together with their positions.

#### Verification status

Installed package source confirms these APIs and data fields. Android player compilation is a
software check only. Space Setup return/focus behavior, permissions, raw mesh availability,
visual legibility, controller acquisition, and relocalization still need on-device verification
on each supported Quest/OS version. No claim of continuous live scene reconstruction is made.

API reference: [Meta MRUK scene data](https://developers.meta.com/vr/documentation/unity/unity-mr-utility-kit-manage-scene-data/).

#### Miniature fog prototype

The **Heatmap** button cycles **Fog → Voxels → Hidden**, starting in Fog. **Fog quality** cycles
16, 24 (default), and 48 steps per ray. Cloud-tab opacity, three colors, gradient anchors, dBm
range and automatic scaling also control the miniature fog. The full-size room cloud keeps its
existing billboard renderer.

##### Method: texture-based volume ray marching

1. Copy up to the existing 20,000-cell display limit from the active live/saved dataset, resolving
   each anchor again. Capture cell positions into the current room's coordinates on Unity's thread.
2. On a worker, scatter the measured mean RSSI and independent confidence into a bounded 3D field.
   A compact smooth kernel has support of twice the chosen voxel resolution, or 1.5 texture cells
   if the grid must be coarser. Only neighboring support blends; distant gaps stay empty. This is
   display interpolation, not an RF reconstruction or measurement of the intervening air. It can
   blend across thin walls inside that radius; it does not implement wall-aware propagation.
3. Store normalized RSSI and confidence premultiplied by coverage. This prevents filtering against
   empty texels from turning the edge into a false weak-signal reading. Coverage uses maximum
   kernel support; overlapping cells do not increase coverage or confidence through raw counts.
4. Upload a linear-filtered RGBAHalf `Texture3D` with at most 64 texels per axis (2 MiB at 64³).
   Field rebuilding/upload is limited to five starts per second and skipped when estimates and
   relative transforms are unchanged. Worker/staging arrays have additional temporary memory cost.
5. Draw one cube around the miniature room. For each eye/pixel covered by its exit faces, intersect
   the viewing ray with the cube, sample the field, map RSSI to the current three-color gradient,
   and integrate translucent color front-to-back using exponential absorption. Empty samples are
   skipped; rays terminate above 97% accumulated opacity. There are no lights or shadows.

Base density uses measured support and confidence. Optional signal weighting also varies density
with relative RSSI, as described below. An observed weak signal still produces fog in the weak color. Optical step length is in room meters so changing
miniature scale does not change its optical thickness. It is clipped to the room's bounding box;
semantic floor polygons/doors and scanned surfaces do not constrain the interpolation kernel.

The prototype renders at the normal eye-buffer resolution **only over the proxy cube's screen
area**. It does not implement a half-resolution render target, temporal accumulation, shadowing,
or empty-space acceleration between samples. Lower quality reduces samples per covered pixel.
A viewer inside the model can cover most of the display, increasing cost.

`Core/FogField` contains the bounded field math without Unity/Meta types. `MiniatureFog` owns the
runtime texture, material, proxy object and cancellable build. Dataset identity changes clear stale
fog after target/stream resets or replay loads; results from old room/dataset generations are
ignored. Raw samples and room anchors are untouched. Textures/materials are released on destroy.

Status reports texture dimensions, support radius, and **worker build time**, not GPU frame time.
Android compilation does not establish fog performance or appearance on Quest. Stereo blending,
inside-volume rendering, moving/scaling, sparse/dense coverage, target/replay changes, memory and
sustained GPU cost need device profiling. Transparent room faces and fog may have sorting artifacts;
opaque geometry inside the proxy can incorrectly reject exit-face fragments with the prototype's
simple depth test. No new automated tests were added or run for this prototype.

##### Optional density from relative signal strength

Open **Preview → Fog settings** and enable **Signal density**. It starts off; **Signal effect**
remembers its slider value while toggling, starting at 50%. These are runtime controls, not
persisted user preferences.

Relative signal uses the Cloud tab's full low/high dBm endpoints:

```text
relative = clamp((RSSI - low dBm) / (high dBm - low dBm), 0, 1)
multiplier = lerp(1, lerp(0.2, 2.0, relative), effect)
final density = coverage × confidence fade × multiplier
```

- **Off or 0%:** the original coverage/confidence density.
- **100%:** the low endpoint has 0.2× density, and the high endpoint has 2× density.
- Intermediate slider values blend toward that contrast; at 50%, endpoints are 0.6× and 1.5×.

Cloud opacity continues to scale overall absorption. Density multipliers do not multiply final
pixel opacity directly; the ray marcher integrates optical thickness exponentially. Unknown
areas remain empty, and confidence still fades the field. The weighting is a visual emphasis,
not a calibrated RF-power measurement or extra evidence of coverage.

Auto scaling changes these endpoints with the accepted dataset and therefore changes density
weighting too. The gradient's two color-stop handles affect color only. This adjustment happens
in the fragment shader, so slider/toggle changes do not rebuild or upload the 3D field.


<a id="unity-assets-scripts-probe"></a>
## Probe mounting, clock adapter and simulation

*Source folder: `Assets/Scripts/Probe`*

| Component | Responsibility |
|---|---|
| `ProbeMount` | Probe controller side, pod/antenna transforms, outline visibility, calibration edits/files and recording lock |
| `AndroidMonotonicClock` | Install the Java bridge's boot-time monotonic clock before scene capture starts |
| `SimulatedRssiSource` | Synthetic meter signal for UI development; never a real spatial survey source |

The mount starts under `LeftControllerAnchor`; `SetRightHanded` moves it to `RightControllerAnchor` with
mirrored estimates, and `ProbeMount.Controller` (LTouch or RTouch) is the controller everything else tracks.
The scene's pod and antenna values are estimates until visually calibrated. A different board,
mount, enclosure, cable route or controller/headset arrangement can require a new calibration.

#### Calibration

Freeze-and-fit: copy the pod visual into a fixed world target, fit the physical enclosure,
release/press the probe controller's trigger, and compute the controller-to-pod transform. Its
upper face button (Y or B) cancels.

The actual antenna remains controller-parented while a target is frozen. Recording/placement is
blocked while aligning. Tracking loss, focus/pause or a tracking-origin change cancels frozen
alignment. Capture is unsaved until **Save offset**; startup loads the validated persistent file.
This aligns the enclosure visually, not an independently measured RF phase center.

The Android clock adapter and BLE callback share `elapsedRealtimeNanos` microseconds, including
across sleep. Its cached Java reference lasts for the player lifetime; desktop/editor time stays
on Stopwatch. See [Android bridge details](#unity-assets-plugins-android-wifiprobeble-androidlib).


<a id="unity-assets-scripts"></a>
## Runtime application modules

*Source folder: `Assets/Scripts`*

The current transport client is `Esp32BleSignalTester.cs` (namespace `WifiHeatmap.Probe`).
It negotiates WiFiProbe, scans/selects an AP, manages BLE state, parses messages through the
Core parser, maps firmware time and publishes typed events plus the integer meter event.

#### Data flow

```text
Android GATT callback -> inbound queue -> bounded line framing -> Core parser
    -> session/AP validation -> ClockMapping -> ProbeSample
    -> SessionRecorder pose interpolation -> anchor-relative SampleRecord
    -> disk writer and/or LiveVoxelCloud -> VoxelCloudLayer
```

| Module | Boundary |
|---|---|
| [Core/](#unity-assets-scripts-core) | No-engine parsing, timing, calibration, storage and grid/range math |
| [Probe/](#unity-assets-scripts-probe) | Controller mount/calibration, Android clock bootstrap and simulation |
| [Recording/](#unity-assets-scripts-recording) | Meta room/anchor adapter, pose fusion, capture and replay |
| [Heatmap/](#unity-assets-scripts-heatmap) | Sparse cloud layers and shared display settings |
| [Preview/](#unity-assets-scripts-preview) | Detached room miniature, controller manipulation and guided room capture |
| [Meter/](#unity-assets-scripts-meter) | Live dBm/LED/haptic presentation |
| [UI/](#unity-assets-scripts-ui) | World-space menus, calibration controls and gradient slider |

`WifiHeatmap.Core` has its own assembly definition and no Unity references. Other runtime scripts
use the project's main Unity assembly. Keep Android and Meta types out of Core. Queue callbacks
for scene work on the Unity thread; retain the original transport timestamp rather than using
frame arrival time. Changing mounts, targets or stream identity must not mix incompatible data.


<a id="unity-assets-scripts-recording"></a>
## Spatial placement, capture and replay

*Source folder: `Assets/Scripts/Recording`*

| Component | Responsibility |
|---|---|
| `AnchorRegistry` | MRUK rooms, manual OVR spatial anchors, selection and localization state |
| `SessionRecorder` | Timestamped antenna history, measurement-time placement, quality flags and disk lifecycle |
| `SessionViewer` | Read a saved session and rebuild its cloud against resolved anchors |
| `RoomOccluders` | Depth-only room walls/floor/ceiling/furniture under each localised MRUK room's anchors, with door/window openings, so the cloud hides behind them |

SessionRecorder captures antenna poses on HostClock every frame. A mapped firmware timestamp
selects/interpolates a historical pose. The chosen anchor converts that pose to local coordinates.
Typed `SpatialSampleReceived` events feed the live cloud even when no disk recording is active.
The event includes numeric timing uncertainty for the live display policy.

Recording requires a real selected streaming probe, room/anchor availability and a settled mount.
Unsaved/active mount adjustment blocks recording. Simulation is excluded. Target/stream changes,
detach and recorder shutdown end the recording. Dropped/flagged samples are counted and the
current placement blocker is exposed to the cloud UI.

#### Persistence

Application files: `persistentDataPath/sessions/<id>/`. Header metadata, CRC-checked sample
blocks and clock exchanges are handled by the [Core session layer](#unity-assets-scripts-core-sessions).
Mount calibration snapshots identify the transform used for that recording. Incomplete sessions
are recovered on startup. Calibration and raw samples are not rewritten by color/voxel settings.

Manual anchors persist via the platform and `anchors.json`; MRUK rooms use their room UUIDs.
Replay requires a currently localized matching anchor. Unresolved groups are hidden and counted,
then retried as localization changes. Automatic realignment is a design extension, not a
completed workflow. Lost tracking is not rendered as a valid measurement at the last pose.

Loading a session hides the live preview; starting a new stream returns to live view. Format-1
clock-flagged records can appear faded with their unknown timing bound explicitly reported.


<a id="unity-assets-scripts-ui"></a>
## World-space menu controls

*Source folder: `Assets/Scripts/UI`*

The root `ProbeConfigMenu` positions/toggles the menu in front of the viewer and coordinates
Probe, Network, Sessions, Anchors, Meter, Mount, Cloud and Preview tabs. X or A on the probe's controller toggles it.
Meta ray/poke canvas integration is added by the Editor setup code.

| Type | Responsibility |
|---|---|
| `ProbeConfigMenu` | BLE connect/disconnect, AP rows/paging, interval selection, meter/movement-haptic toggles, speed/error guidance and RX timing diagnostics |
| `SessionsPanel` | Recording control, saved-session paging/load/hide and confirmed deletion |
| `AnchorsPanel` | Rooms/manual anchors, placement, selection and confirmed removal |
| `ProbeCalibrationPanel` | Probe controller choice, freeze-and-fit entry/cancellation and save/load/reset |
| `CloudPanel` | Voxel/opacity/endpoints, auto-scale toggle, RGB selector pages and live-cloud clearing |
| `RoomPreviewPanel` | Miniature visibility/geometry, Fog/Voxels/Hidden, fog quality, optional signal-density toggle/slider page, ceiling, zoom/recenter and guided scan/reload |
| `CloudRangeSlider` | One gradient bar with two independently dragged handles and a runtime legend texture |
| `MenuStyle` | Shared button label/tint helpers and second-tap destructive-action confirmation |

Controls change application services; UI should not parse radio bytes, infer room alignment or
rewrite raw observations. Use SetValueWithoutNotify when refreshing sliders to prevent feedback
loops. World-space dragging uses the pointer event camera, not assumptions about a flat display.

Automatic range endpoints follow active accepted data and are disabled for manual dragging until
Auto is switched off. Gradient handles/colors remain editable. A frozen calibration target can
stay active with the menu hidden; its trigger/cancel handling belongs to ProbeMount.

The generated menu is serialized in the [prefab](#unity-assets-prefabs) and both authored
scenes. See [Editor migrations](#unity-assets-editor) before adding serialized control fields.


<a id="unity-assets-settings"></a>
## URP and rendering configuration

*Source folder: `Assets/Settings`*

This folder holds mobile/PC URP pipeline and renderer assets, global pipeline settings and volume
profiles retained from the project template/configuration. The Android project uses the mobile
quality/pipeline path; the Editor may use a different quality level.

The current setup guide describes 4x MSAA, no HDR, and no depth/opaque texture copies for the
mobile pipeline. Check the actual asset and QualitySettings before changing rendering behavior.
Cloud transparency is defined by its own shader/material, not by enabling opaque texture copies.

Do not assume an Editor screenshot proves Quest stereo, passthrough compositing, overdraw or
thermal performance. Preserve GUIDs when adjusting settings so ProjectSettings references remain
valid. See [the Unity guide](#wifi-heatmap--quest-3-mixed-reality-project) for project configuration and limitations.


<a id="unity-assets-shaders"></a>
## Rendering shaders

*Source folder: `Assets/Shaders`*

`VoxelCloud.shader` defines **WiFiHeatmap/VoxelCloud**, used by `Materials/VoxelCloud.mat`.
It renders a camera-facing soft disk at each measured voxel center in URP with stereo/instancing
support. Per-vertex color carries RSSI gradient color and confidence-adjusted opacity; UV1 carries
billboard size. The vertex shader applies object scale to disk width, including miniature copies.

The pass uses transparent alpha blending, no depth writes and no culling. It retains depth testing
against opaque geometry. With the Depth API's `SOFT_OCCLUSION` or `HARD_OCCLUSION` keyword on, each
fragment's alpha is also scaled by Meta's environment-depth occlusion value, tested at the billboard
corner's world position (not the cell centre); see [Real-world occlusion](#real-world-occlusion). Radial falloff softens cell edges. Geometry exists only for occupied
cells; the shader is not a volume ray marcher or an unsampled-field estimator.

Each mesh is parented to its resolved room/anchor. Bounds include billboard extent. Sorting is
per anchor mesh, so intersecting transparent cells can show sorting artifacts; fine resolution and
heavy overlap also increase overdraw on Quest. Confirm stereo appearance and cost on-device.
The mesh/data producer is [VoxelCloudLayer](#unity-assets-scripts-heatmap).

`RoomPreview.shader` defines **WiFiHeatmap/RoomPreview**, referenced by `Materials/RoomPreview.mat`.
It draws vertex-colored semantic bounds and copied global geometry with translucent, double-sided
URP rendering and stereo instancing, with its alpha scaled by the Depth API's occlusion value when
that is on. It does not render passthrough occlusion or alter SDK meshes.
Like other transparent rendering, intersecting room faces can have sorting artifacts.

`MeterText.shader` defines **WiFiHeatmap/MeterText**, used by `Materials/MeterText.mat`: TextMesh
Pro's *Mobile/Distance Field* shader with Meta's Depth API occlusion added (changes marked
"Occlusion"), so the meter's labels hide behind real things like its body. It includes TMP's
`TMPro_Properties.cginc` from `Assets/TextMesh Pro/Shaders` by absolute path.

`RoomOccluder.shader` defines **WiFiHeatmap/RoomOccluder**, used by `Materials/RoomOccluder.mat`.
It writes depth only (`ColorMask 0`, no culling) in the queue just before opaque geometry, so the
transparent cloud, which depth-tests, is hidden behind the room's scanned surfaces while the
passthrough shows through. See [Real-world occlusion](#real-world-occlusion).

`MiniatureFog.shader` defines **WiFiHeatmap/MiniatureFog**. It ray-marches a bounded, linear-filtered
3D RSSI/coverage field per eye over the miniature's proxy cube, using 24 samples by default and
premultiplied front-to-back color/opacity integration. It is unlit and has no shadows. With the
Depth API on, the result is scaled by the occlusion value at the first sample containing fog
(one depth test per pixel, not per sample). The saved
material reference retains the shader for player builds; runtime instances own texture bindings.
See [method, controls and prototype limitations](#unity-assets-scripts-preview).

The fog shader can optionally multiply density by relative RSSI within the current Cloud low/high
range. A 0–1 influence blends from uniform signal weighting to 0.2× at the low endpoint and 2×
at the high endpoint. Base coverage/confidence and overall absorption remain separate factors.
This uses shader uniforms and does not require rebaking the volume texture.


<a id="unity-assets-streamingassets"></a>
## Streaming assets

*Source folder: `Assets/StreamingAssets`*

No streaming content is tracked, so a fresh Git checkout has no `StreamingAssets/` folder. No
measurement recordings or calibration files belong here; those use application persistent storage.
Generated Addressables `aa/` output and its metadata are ignored. See the [asset map](#unity-assets)
for authored content.


<a id="unity-assets-tests-editmode"></a>
## Core EditMode cases

*Source folder: `Assets/Tests/EditMode`*

Assembly: `WifiHeatmap.Core.Tests`, Editor-only and enabled with `UNITY_INCLUDE_TESTS`.
Its asmdef references Core, NUnit, Unity test runners and Newtonsoft.Json.

| Suite | Existing scope |
|---|---|
| `ProbeProtocolTests.cs` | Protocol fields, line/sample/PONG parsing and invalid input |
| `ClockMappingTests.cs` | Offset/drift estimates and uncertainty behavior |
| `SessionFileTests.cs` | Record serialization, block/CRC/session storage, recovery and pose/range helpers |
| `ProbeCalibrationTests.cs` | Calibration round trips/validation, atomic replacement and left/right mirroring |
| `AccessPointGroupsTests.cs` | Grouping scan results by SSID and channel, `SELECT ... group=ssid` and the `aps=` count |

Use the actual case definitions as the authority for coverage. On 2026-11-04 all 95 cases passed (Unity
6000.3.22f1, batch mode). Extend the relevant cases for new logic;
do not infer native BLE or Quest support from these no-hardware cases.

For an intentional run, open **Window > General > Test Runner > EditMode** and select this
assembly, or use Unity CLI against a suitable non-open project copy. Do not close an interactive
Editor or discard unsaved scenes just to run a batch job. Generated results are ignored.


<a id="unity-assets-tests"></a>
## Unity checks

*Source folder: `Assets/Tests`*

Existing automated coverage lives in [EditMode/](#unity-assets-tests-editmode). The tests target the
platform-neutral Core assembly and use the Unity Test Framework, NUnit and Newtonsoft.Json.
They do not require a live ESP32 or headset for those Core cases.

The latest run (2026-11-04) passed all 95 Core cases. They cover Core logic only, not the Android callback,
visualization or UI code.

Run checks deliberately via Unity's Test Runner or a requested CLI test run. The initial
repository/documentation task did not run or add tests. Keep generated results outside version
control unless a sanitized, intentional fixture/report is being reviewed.

Coverage still needed includes newer voxel/range/display policy behavior, native timestamp
integration, disconnect/lifecycle paths, corrupted/unresolved room scenarios, and on-headset
readability/performance. Architecture milestones and the hardware matrix are in the root
[AGENTS.md](../AGENTS.md).


<a id="unity-assets-xr"></a>
## OpenXR loader and feature assets

*Source folder: `Assets/XR`*

The loaders, XR general settings and OpenXR package/editor settings connect Android to OpenXR
and configure the Meta feature/controller profiles. Relevant package versions are pinned by
`Packages/manifest.json` and `packages-lock.json`.

Current intended input includes Touch Plus/Touch controller profiles and XR hands. The app mounts
the probe under the left or right controller. Device/feature declarations are configuration, not proof that
Quest 3S or every runtime revision behaves identically.

Edit through Unity XR Plug-in Management and the project's Editor setup where possible. Keep
these assets and their `.meta` files with matching ProjectSettings. A loader/profile change may
affect controller pose conventions; recheck calibration and model alignment afterward.


<a id="unity-packages"></a>
## Unity package declarations

*Source folder: `Packages`*

`manifest.json` declares direct dependencies and Meta's scoped registry.
`packages-lock.json` records resolved packages and transitive dependency versions. Commit both
when intentionally changing dependencies; the generated package cache is ignored.

This snapshot declares Meta XR Core, MRUK and Interaction OVR 207.0.1; OpenXR 1.19.0; OpenXR Meta 2.6.1;
URP 17.3.0; Input System 1.20.0; XR Management 4.7.0; XR Hands 1.10.0; and Unity Test Framework
1.6.0, alongside retained template/editor packages. The manifest is the authoritative list.

The optional `com.unity.pipeline` package is not installed. Earlier attempts encountered package
authentication problems, so live Unity CLI scene control should not be assumed available.
Opening the project normally restores dependencies through Unity Package Manager.

Avoid editing package-cache copies as application source. Keep SDK-specific code in adapters
and verify behavior after upgrades rather than treating a new package version as certification of
hardware support. Open the project with the Editor version recorded in ProjectSettings.


<a id="unity-projectsettings"></a>
## Shared Unity project settings

*Source folder: `ProjectSettings`*

Commit these settings with the matching Assets/Packages snapshot. `ProjectVersion.txt` selects
Unity 6000.3.22f1. `EditorBuildSettings.asset` currently disables ProbeTester and enables
HeatmapBuilder as the application startup scene.

Other files configure Android/IL2CPP/ARM64, XR loaders/features, input handling, render/quality
paths, package management, physics and template defaults. Application ID:
`com.wifiheatmap.prototype`. Consult the actual serialized settings when diagnosing a build;
Editor setup documents its generated defaults but manual changes may differ.

The custom full Configure command can rewrite these settings and regenerate scene/build entries.
The saved Android manifest also carries an intentional boundaryless feature declaration, whose
exact-runtime behavior remains a validation item. Machine-specific Editor layouts/preferences
belong in the ignored `UserSettings/` directory instead.

Do not commit Android signing keys or passwords. Player output, generated C# project files,
Library/Temp and other caches are excluded by `Demo6/.gitignore` and `unity/.gitignore`.
