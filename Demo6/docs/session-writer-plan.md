# Session writer: plan

Persistent recording of probe measurements for the Quest 3 heatmap. Both heatmap views (the
surface projection on the room mesh and the voxel grid) are **derived** from these recordings,
so the grid size, colour scale, averaging method and confidence formula can all change later
without re-measuring.

Status: steps 1–5 of §10 are implemented and unit-tested (part of the 95-case EditMode suite). Recording and replay are in use on the headset (sessions recorded, then loaded after relaunching), but the planned two-room check (step 6) hasn't been done as written, and the Python reader (step 7) doesn't exist yet. See "Implementation notes" at the end for where the code differs from this plan.

## 1. Decisions so far

| Topic | Decision |
|---|---|
| What is stored | Raw samples (one per probe report window), never voxels |
| Format | Append-only binary record log plus a JSON session header; no database |
| Write strategy | Every sample goes into an in-memory buffer; flush to the OS about every 1 s; force to disk every few seconds, on stop, and on pause or focus loss |
| Coordinate frame | Each sample is stored relative to one anchor from the session's **anchor table**, chosen by a per-record **anchor index**. This is what makes multi-room sessions work |
| Anchor source | Automatic: MRUK room anchors from the headset's Space Setup scan. Manual: a user-placed spatial anchor when there's no scan, or by choice |
| One session per… | …access point (BSSID + channel), or network group (an SSID's radios on one channel, protocol 1.2; named by its strongest radio's BSSID). Picking another network starts a new session |
| Simulated readings | Never recorded |

## 2. Platform facts this relies on

All checked in the installed packages: MRUK 207.0.1 and Meta XR Core 207.0.1
(`unity/Library/PackageCache/...`).

| Fact | Where |
|---|---|
| `MRUK.Instance.GetRooms()` lists the loaded rooms; `GetCurrentRoom()` is the room the headset is in, else the last one it was in, else the first loaded; `null` if none | `MRUK.cs` |
| Each room's primary anchor is `MRUKRoom.Anchor` (an `OVRAnchor`) with a persistent `Guid Uuid` | `MRUKRoom.cs`, `OVRAnchor.cs` |
| `MRUKRoom.IsLocal` is true only for rooms loaded from the device; JSON or prefab test rooms have no real anchor | `MRUKRoom.cs` |
| A room's GameObject transform is set to the room anchor's pose | `MRUK.Shared.cs`, `UpdateRoomProperties` |
| `MRUKRoom.IsPositionInRoom(Vector3, testVerticalBounds)` tests whether a point is inside a room | `MRUKRoom.cs` |
| `RoomCreatedEvent`, `RoomUpdatedEvent`, `RoomRemovedEvent`, `SceneLoadedEvent` / `RegisterSceneLoadedCallback` | `MRUK.cs` |
| **World lock** is on by default (`EnableWorldLock = true`): MRUK moves the camera rig's TrackingSpace each frame to keep room anchors fixed in the Unity world. `IsWorldLockActive` goes false when the room can't be localised (e.g. after standby in another room) | `MRUK.cs` |
| `OVRSpatialAnchor`: `Uuid`, `Created`, `Localized`, `WhenLocalizedAsync()`, `SaveAnchorAsync()`, `EraseAnchorAsync()`, and loading via `LoadUnboundAnchorsAsync(uuids, …)` → `UnboundAnchor.LocalizeAsync()` → `BindTo()` | `OVRSpatialAnchor.cs` |
| `MRUK.SetCustomWorldLockAnchor(OVRSpatialAnchor, Pose)` world-locks to a user-placed anchor | `MRUK.cs` |

To verify on a headset, not in code:
- whether a room's anchor UUID stays the same across app launches and reboots (expected), and
  changes after a new Space Setup (expected);
- how far from its anchor a pose stays accurate in practice.

## 3. File format

One folder per session: `Application.persistentDataPath/sessions/<sessionId>/`, where
`sessionId` is a UTC timestamp plus a short random suffix.

### 3.1 `session.json` (header; written at start, rewritten on anchor-table change and on stop)

```json
{
  "format": "wifiheatmap.session",
  "formatVersion": 1,
  "sessionId": "20261029T141502Z-7f3a",
  "startedUtc": "2026-10-29T14:15:02Z",
  "endedUtc": null,
  "app": { "version": "0.1.0", "unity": "6000.3.22f1", "metaXr": "207.0.1" },
  "accessPoint": { "bssid": "AA:BB:CC:DD:EE:FF", "channel": 36, "band": "5", "ssid": "Home WiFi", "security": "WPA2",
                   "bssids": [], "measuredRadios": 1 },
  "probe": { "deviceName": "WiFiProbe-C5", "hello": "...", "caps": "...", "bootId": 9842331, "reportIntervalMs": 100 },
  "mount": { "mountId": "probe_mount_left (ring clip pod)", "calibrationId": "estimate" },
  "anchors": [
    { "index": 0, "kind": "mrukRoom",      "uuid": "…", "label": "Living room" },
    { "index": 1, "kind": "mrukRoom",      "uuid": "…", "label": "Kitchen" },
    { "index": 2, "kind": "spatialAnchor", "uuid": "…", "label": "Hallway marker" }
  ],
  "records": { "file": "samples.bin", "recordSize": 48, "count": 31877, "complete": true },
  "notes": ""
}
```

The anchor table only grows during a session. Indices never change once assigned.

### 3.2 `samples.bin`

- **File header** (32 bytes): magic `WHSB`, format version (uint16), record size (uint16),
  session ID hash (uint64), reserved.
- **Records**, packed little-endian, 48 bytes each:

| Field | Type | Bytes | Notes |
|---|---|---|---|
| sequence | uint32 | 4 | probe `seq`; detects drops and duplicates |
| time | int64 | 8 | measurement midpoint in headset monotonic µs (see §5) |
| position | float32 × 3 | 12 | antenna position relative to anchor `anchorIndex`, metres |
| rotation | float32 × 4 | 16 | antenna orientation relative to the anchor (quaternion) |
| rssi | int16 | 2 | window mean, dBm × 100 |
| stddev | uint16 | 2 | in-window spread, dB × 100 |
| frames | uint16 | 2 | Wi-Fi frames in the window |
| anchorIndex | uint8 | 1 | row in the `anchors` table |
| flags | uint8 | 1 | bit 0 tracking degraded, bit 1 pose extrapolated, bit 2 clock uncertain, bit 3 anchor not localised, bits 4–7 reserved |

- **Blocks** of at most 64 records: `[uint16 record count][uint16 reserved][records][uint32 CRC-32 of
  count..records]`. Every flush closes the current block, so blocks vary in length and every flushed
  sample is CRC-protected. A torn final block (a crash mid-write) is detected and dropped on load;
  complete blocks before it are kept.

### 3.3 `clock.jsonl`

One line per PING/PONG exchange: host send and receive times, probe receive and send times,
boot ID. The writer computes each sample's time from these (§5). Keeping the raw exchanges
means a better clock fit can be recomputed later.

Rough size: 48 B × 10 Hz ≈ 1.7 MB per hour.

## 4. Anchor table and per-sample anchor choice

**At session start**
1. Wait for MRUK's scene-loaded callback.
2. Add every local room (`IsLocal`) to the table as `mrukRoom`, using `room.Anchor.Uuid`.
   Rooms are added up front, so the index stays stable as you walk between them.
3. If there are no local rooms: ask the user to run Space Setup, or to place a manual anchor.
   For a manual anchor: create an `OVRSpatialAnchor` at the controller, wait for
   `WhenLocalizedAsync()`, `SaveAnchorAsync()`, then add it as `spatialAnchor`. Optionally hand it
   to `MRUK.SetCustomWorldLockAnchor`.
4. Rooms created during the session (`RoomCreatedEvent`) are appended. Rooms removed are kept in
   the table but no longer used for new samples.

**Per sample**, given the antenna's world pose at measurement time:
1. If the antenna is inside a room (`IsPositionInRoom`), use that room.
2. Otherwise use the nearest room or anchor, measured from its anchor pose. This covers doorways
   and space just outside a room's walls.
3. Store the pose relative to that anchor: `anchor.transform.InverseTransformPoint/Rotation`
   (rooms) or the spatial anchor's transform.
4. If the chosen anchor isn't localised, or `MRUK.IsWorldLockActive` is false, still record the
   sample but set *anchor not localised*. The heatmap ignores such samples by default.

**On load**
1. Wait for the room scene to load and/or resolve spatial anchors by UUID.
2. Each table row is either resolved (its world pose is known) or unresolved.
3. Samples whose anchor is unresolved aren't shown as aligned. The UI offers "realign": pick a
   current room or anchor to substitute, and save that mapping in `session.json`. The samples
   themselves are never rewritten.

## 5. Prerequisites in the probe client — implemented

Done, with these details (code in `unity/Assets/Scripts/Core/` and `Esp32BleSignalTester.cs`,
tests in `unity/Assets/Tests/EditMode/`):

- **Shared core:** `WifiHeatmap.Core` is a new platform-neutral assembly (no Unity references).
  It holds `ProbeFields` (the one field parser; the probe client now uses it), `ProbeSample`,
  `PingExchange`, `ProbeLineParser`, `ClockMapping` and `HostClock`.
- **Arrival times (current implementation):** Java stamps GATT notification callback entry
  using `SystemClock.elapsedRealtimeNanos() / 1000`, before encoding or main-Handler dispatch.
  An Android startup adapter makes PING send times and Unity pose history use that same clock.
  Editor/desktop code uses the `Stopwatch` fallback. A line takes the timestamp of the chunk
  containing its terminating newline. See the current Unity README for dispatch diagnostics.
- **PING:** a burst of 6 PINGs 150 ms apart after CAPS, then one every 1 s while connected
  (`pingIntervalSeconds`). No PINGs while the probe is running a blocking SCAN.
- **`ClockMapping`:**
  - each exchange gives an NTP-style offset and a wire round trip
  - exchanges over 250 ms are discarded
  - the fit uses the lowest-round-trip 30 % (at least 3) of the last 120 s
  - drift is fitted once the exchanges span 10 s, clamped to ±500 ppm
  - the uncertainty is the worst used round trip / 2, plus the fit residual, plus a drift
    allowance outside the fitted span
  - a different boot ID (from HELLO or PONG) resets it
- **`ProbeSample`:** every SAMPLE field, the receive time, and (once mapped) the measurement
  midpoint in headset time with its uncertainty.
- **Rejection:** samples are rejected unless they belong to the stream session from `STARTED`
  and the selected BSSID/channel. Samples still in flight after STOP is sent are kept.
- **Events:** `SampleReceived` and `ClockExchanged`. `RejectedSampleCount` counts rejects.

What remained the same: the `int` event the meter uses.

The original requirements, for reference:

1. **Full sample event.** Parse every SAMPLE field (session, seq, probe_us (window midpoint),
   window_start_us, window_end_us, bssid, channel, rssi, frames, mean, min, max, stddev) into a
   `ProbeSample` struct. Raise it as a C# event alongside the existing `int` event, which the meter
   keeps using. Stamp it with the headset monotonic receive time.
2. **Clock mapping.** Handle PONG: estimate the probe→headset clock offset from the
   lowest-round-trip PINGs, with drift if needed, plus an uncertainty. A new boot ID resets it.
   Raise exchanges for `clock.jsonl`. PING more often (about every 1 s) while recording.
3. **Target and session identity.** Expose the probe's session ID and the target's BSSID/channel
   with each sample, so the writer can reject mismatches.

## 6. Pose at measurement time

- **`PoseHistory`:** each frame, record (headset monotonic time, `ProbeMount.AntennaPose` in world
  space, tracking validity) in a ring buffer of about 3 s. Use the same `Stopwatch` clock as the
  probe client.
- **Interpolation:** at the sample's mapped time, interpolate position (lerp) and rotation (slerp)
  between the two bracketing frames.
- **Rejection rules:**
  - If the time is newer than the latest pose by no more than 30 ms, extrapolate and set the
    *extrapolated* flag. Beyond that, drop the sample and count it.
  - If tracking was invalid at either bracketing frame, set the *tracking degraded* flag.
  - If the clock mapping is stale or uncertain beyond a threshold (start at ±15 ms), set
    *clock uncertain*.

## 7. Components

| Component | Assembly | Job |
|---|---|---|
| `SampleRecord` / `SessionHeader` (plain C#) | platform-neutral (no Unity types) | The record struct, (de)serialisation, CRC, versioning |
| `SessionFileWriter` (plain C#) | platform-neutral | Opens the folder, writes the header and records, buffers, flushes, finalises; recovers torn files |
| `SessionFileReader` (plain C#) | platform-neutral | Validates and reads records; reports torn and corrupt blocks |
| `ClockMapping` (plain C#) | platform-neutral | Offset/drift fit and uncertainty from PING/PONG exchanges |
| `PoseHistory` | Unity | Ring buffer and interpolation |
| `AnchorRegistry` | Unity + MRUK/Meta | Builds and keeps the anchor table, picks the anchor per sample, resolves anchors on load |
| `SessionRecorder` (MonoBehaviour) | Unity | Glue: subscribes to `ProbeSample`, runs time and pose mapping, writes records; Start/Stop; handles pause and focus |

The platform-neutral parts sit in their own assembly, so EditMode tests run without a headset,
as `AGENTS.md` asks.

### `SessionRecorder` lifecycle
- **Start(target):** requires a connected probe streaming the target, a resolved anchor table,
  `ProbeMount` present, and simulated off. Creates the folder and writes the header.
- **On each `ProbeSample`:** check the session, BSSID and channel; map the time; look up the
  pose; choose the anchor; write the record; update counters.
- **Target change, disconnect or probe reboot (boot ID change):** finalise this session; start a
  new one only on the user's request.
- **Stop:** force to disk, then finalise the header (`endedUtc`, record count, complete flag).
- **`OnApplicationPause(true)` / focus loss:** force to disk. Recording resumes in the same
  session if the probe, target and tracking are still valid; otherwise it finalises.
- **Disk writes:** forcing data to disk runs on a background thread, so the render thread never
  waits on storage.

### Crash recovery
On app start, scan `sessions/`. Any session whose header says `complete: false` is recovered:
drop the torn final block, update the record count, mark it `recovered`, and keep it.

## 8. UI hooks

- **Network tab:** a **Record / Stop recording** button, enabled only while measuring a real
  network. Shows sample count, duration, and the anchors used (by room label).
- **Meter:** a small REC indicator while recording.
- **Later — a Sessions tab:** list, load, delete, export, and realign unresolved sessions.

## 9. Tests

- **EditMode (no headset):**
  - record round-trip
  - CRC failure detection
  - torn-tail recovery
  - format-version mismatch is refused
  - anchor index outside the table is refused
  - flush and finalise ordering
  - clock-fit accuracy against synthetic offset, drift and jitter
  - pose interpolation and extrapolation limits
- **PlayMode:** a fake probe and fake poses produce a session that the reader loads back to
  identical values.
- **PC reader:** a small Python script (`numpy.fromfile` with a structured dtype) that reads
  `samples.bin` and `session.json`, for checking exports and plotting.

## 10. Order of work

1. Probe client: `ProbeSample` event and PONG clock mapping (§5), with tests.
2. Platform-neutral format: record struct, writer, reader, CRC, recovery (§3, §7), with tests.
3. `PoseHistory` and the time/pose join (§6).
4. `AnchorRegistry` with MRUK rooms; manual spatial-anchor fallback second (§4).
5. `SessionRecorder` glue and the menu's Record button (§7, §8).
6. On-headset check: record a two-room walk, relaunch, load it, confirm the samples land where
   they were taken; reboot and repeat.
7. Python reader.

## 11. Open questions

- **Pinned access point:** should a session be allowed to follow an AP that changes channel, or
  stop? (Proposed: stop and prompt.)
- **Manual anchors:** keep them inside the app, or also allow MRUK's custom world-lock anchor
  mode for hallways?
- **Retention:** keep sessions forever, or cap total storage with a warning?
- **Export:** zip the session folder plus a CSV for spreadsheets?

## Implementation notes (as built)

Code: `unity/Assets/Scripts/Core/Sessions/` (platform-neutral, tested) and
`unity/Assets/Scripts/Recording/`. UI: `unity/Assets/Scripts/UI/SessionsPanel.cs`, `AnchorsPanel.cs`.

- **Variable-length blocks.** The plan's fixed 64-record blocks would have needed a partial block
  on every one-second flush. Instead each flush closes the block, with a record count in front;
  overhead is 8 bytes per block, about 1.6 % at 10 Hz.
- **`samples.bin` header.** The file header carries an FNV-1a hash of the session ID, so a
  `samples.bin` copied into the wrong folder is refused.
- **Atomic header rewrites.** `session.json` is rewritten through a temporary file plus
  `File.Replace`. Anchors added mid-session are written immediately, so a crash never leaves a
  record pointing at an unknown anchor.
- **`SessionRecorder`:**
  - pose history from `ProbeMount.Antenna` every frame, with tracking validity from
    `ProbeMount.ControllerTracked()` (the left or right Touch controller that carries the probe)
  - flush to the OS every 1 s; force to disk every 5 s on a background task, and on pause, focus
    loss and stop
  - recovery of unfinished sessions at startup
  - stops itself when the probe stops streaming or the network changes
  - refuses to record while the simulated signal is on
- **`AnchorRegistry`:**
  - **Rooms:** MRUK local rooms, labelled "Room 1, 2, …" in MRUK order.
  - **Manual anchors:** saved on the headset with `SaveAnchorAsync` and listed (UUID, label,
    created) in `persistentDataPath/anchors.json`, together with the mode and the chosen anchor.
    On start they are loaded with `LoadUnboundAnchorsAsync` → `LocalizeAsync` → `BindTo`.
  - **Auto mode:** the room containing the sample, else the nearest room; with no rooms, the
    nearest localised manual anchor.
  - **Manual mode:** the chosen anchor for every sample.
  - **Room localisation:** taken from `MRUK.IsWorldLockActive`.
- **`SessionViewer` (the "load" view):** bins the session's samples into the same translucent
  voxel cloud as the live preview (`VoxelCloudLayer`, current Cloud settings), parented to each
  sample's anchor. Samples whose anchor isn't found are counted, not drawn, and the view rebuilds
  when anchors finish localising. (The first version drew one octahedron per sample, coloured by
  the legacy `SignalScale`.)
- **Delete:** a session (folder) or a manual anchor (erased from the headset) needs a second tap
  within 3 s. Sessions recorded against an erased anchor can't be placed afterwards.
- **Not done yet:**
  - the realign workflow for unresolved sessions (§4 "On load", step 3)
  - export
  - the Python reader
  - storage limits



### Optional calibration snapshot

New sessions include `mount.calibration`, a snapshot of the version-1 probe
calibration document: mount/controller/headset identity, calibration ID, method,
UTC metadata, `PodPosition`/`PodRotation` (`controllerFromPod`) and
`AntennaPosition`/`AntennaRotation` (`podFromAntenna`). Coordinates use Unity's
left-handed controller-local axes, metres, and XYZW quaternions. The session's
existing camel-case serializer writes these as `podPosition`, `podRotation`, etc.
The separate per-side `probe-calibration-left.json` / `-right.json` files use the calibration file writer's
PascalCase properties. The snapshot is additive to session format 1: old sessions
have no snapshot and continue to load; old readers ignore the new property.
Changing the persistent offset does not rewrite session headers or old samples.
Manual visual adjustment has unquantified physical accuracy; its method is
explicitly recorded as `frozen-target-visual-alignment` (freeze-and-fit), or
`manual-visual-alignment` for a saved estimate and older joystick-adjusted files (the joystick
adjustment has since been removed).
