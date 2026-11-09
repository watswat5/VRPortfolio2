# AGENTS.md — Quest Mixed-Reality Wi-Fi Heatmap

## Purpose

This guide describes the architecture and implementation boundaries for the Wi-Fi Heatmap prototype in this repository. The current Quest application uses an ESP32 probe clipped to the **left or right Touch Plus controller** (chosen in the app), or optionally an M5StickC Plus2 worn on either wrist and tracked by hand tracking, communicates with it over **BLE**, and associates timestamped RSSI reports with the tracked controller pose and a calibrated antenna offset. The probe is not head-mounted. The current app renders a live voxel cloud and supports recording/replay and room anchors; a Quest Android USB bridge and several verification goals below remain future work. See [README.md](README.md) and the [full guide](docs/GUIDE.md) for the operator guide and current limitations.

The sections below mix implemented behavior with proposed architecture and hardware verification gates. Treat the repository and its current guides as the evidence for what exists. Do not assume a proposed feature or a listed target is implemented or verified merely because it appears here.

## Product goals

- Let the user discover nearby access points and select a specific radio by BSSID and channel; where several radios of one SSID share a channel, select them together (protocol 1.2), each reading the strongest, as a roaming client would connect.
- Measure the selected radio without joining the network or requesting its password.
- Time-align RSSI observations with the tracked probe-controller pose and the calibrated probe-to-antenna offset; keep the headset tracking origin for room/world alignment.
- Accumulate spatial samples and display surface and volumetric heatmaps with visible data confidence.
- Restore room-relative data after relaunch when persistent spatial alignment is available.
- Keep measurement transport simple, inspectable, versioned, and robust to disconnects.
- Leave clean extension points for a separately localized robot probe later; do not build robot support into the first milestone.

## Non-goals for the initial release

- Network authentication, traffic decryption, payload capture, or packet-content inspection.
- Identifying people or performing camera-based tracking.
- Claiming calibrated RF field strength or laboratory-grade accuracy.
- Combining observations from independently moving probes before their coordinate frames and clocks are reconciled.
- Treating SSID as a unique access-point identity.

## Verification gates — do not treat these as established facts

Hardware, OS, SDK, and store-policy capabilities can vary by firmware, board revision, Unity/Meta SDK version, Android permission policy, and USB topology. Before committing the architecture to a release, verify each item on the exact target devices and supported software versions:

1. **Future Quest USB host path:** The current Quest app uses BLE; USB serial currently serves desktop tooling and firmware flashing. Before replacing or supplementing BLE, verify Quest 3/3S USB host mode, power, enumeration, permissions, detach/reconnect, and lifecycle behavior on the exact hardware.
2. **USB device enumeration:** For a Quest USB implementation, record the actual VID, PID, interfaces, endpoints, and permission flow. Some ESP32 variants expose USB differently or use an external USB-to-UART bridge.
3. **Android/Unity bridge:** No Quest Android USB bridge is implemented in this snapshot. If added, verify Android USB Host APIs, permission callbacks, transfers, lifecycle handling, and reconnect behavior without unsupported hidden APIs.
4. **ESP32-C5 availability and SDK support:** Verify the exact silicon/module/dev-board, production availability, USB capability, toolchain version, and Espressif SDK support. Do not infer features from the family name.
5. **2.4/5 GHz scan and monitor support:** Verify the exact device can scan and enter passive/promiscuous monitor mode on every required 2.4 GHz and 5 GHz channel. Verify regulatory-domain, DFS, channel-width, packet-filter, and channel-switching behavior. Report measured capabilities through `CAPS`; never hard-code them from a board name.
6. **RSSI and packet metadata:** Verify which received frame types provide trustworthy BSSID/channel/RSSI/timestamp metadata and the resolution, range, and semantics of firmware timestamps.
7. **Meta XR/MRUK persistence:** Verify the installed Meta XR SDK and MR Utility Kit APIs for passthrough, scene/room access, anchors, permissions, relocalization, and persistent identifiers on both headset models.
8. **Background and lifecycle behavior:** Verify BLE, probe-controller tracking, passthrough, and recording behavior when the app loses focus, tracking is lost, or the OS suspends the process. Include USB only if the future Quest USB path is implemented.

Record results, device/OS/SDK versions, and minimal reproductions in project documentation or tests. If a gate fails, revise the design instead of silently substituting assumptions.

## System context

```text
Nearby Wi-Fi APs
        |
        | passive 802.11 reception / active scan only when requested
        v
+-------------------------------------+
| ESP32 probe in controller clip      |
| scan/filter/stats                   |
+-------------------------------------+
        | BLE commands and timestamped RSSI notifications
        v
+----------------------+       tracked probe-controller pose
| Quest 3 / 3S         |<--------------------------------+
| BLE plugin + Unity 6 |                                 |
| Meta XR + MRUK       |                                 |
+----------+-----------+                                 |
           | calibrated controller-to-pod-to-antenna transform
           v
   clock/pose fusion -> room anchor -> live voxel cloud

USB serial is used by desktop tools. Quest Android USB host is future work.
```

## Component boundaries

### Probe firmware

Owns:

- Capability discovery for the exact hardware/firmware build.
- Nearby AP scanning and normalized AP records.
- Selection by BSSID plus channel/band; SSID is display metadata, except that 1.2's `group=ssid` uses it to gather a network's radios on the chosen channel (the selection is still named by the chosen BSSID).
- Passive monitor configuration and filtering for the selected AP.
- Measurement timestamping as close to reception as the SDK permits.
- Rolling quality counters and aggregate statistics.
- WiFiProbe v1 command parsing, output framing, validation, and errors.
- Safe recovery after malformed input, radio errors, or USB reconnect.

Does not own Quest pose, room coordinates, antenna world position, heatmap interpolation, room persistence, or user-interface state.

### Android USB host bridge (future; not implemented in this snapshot)

Owns:

- USB discovery, user permission, open/close, interface claim/release, and transfers.
- Byte buffering and line framing across arbitrarily split/coalesced USB reads.
- A thread-safe, lifecycle-aware interface to Unity.
- Device attach/detach and recoverable error notifications.
- Bounded queues and diagnostics for overruns, parse failures, and disconnects.

It must not interpret room poses or render UI. Protocol parsing may live here or in a platform-neutral Unity layer, but there must be exactly one authoritative parser.

### Unity application core

Owns:

- Connection and measurement session orchestration.
- WiFiProbe message validation and mapping into typed domain events.
- AP selection UI, including duplicate SSIDs, band/channel, BSSID, and current RSSI.
- Clock synchronization and uncertainty estimation.
- Timestamped pose history for the tracked controller (left or right) that carries the probe.
- Interpolation of pose at measurement time.
- Applying the calibrated controller-to-pod-to-antenna transform to estimate antenna pose.
- Sample acceptance, rejection, storage, aggregation, and persistence.
- Heatmap generation and confidence visualization.
- Explicit handling of tracking loss, room relocalization failure, and stale measurements.

### Meta XR / MRUK integration

Owns the adapter to passthrough, tracking origin, room/scene data, anchors, surface semantics, and persistence. Keep vendor SDK types behind this boundary so most domain logic and tests do not require a headset or Meta runtime.

### Visualization

Consumes immutable or snapshot-like spatial datasets. It must not own serial commands or mutate raw measurements. Support at least:

- **Surface heatmap:** project or aggregate samples near MRUK surfaces, then color a mesh/decal/texture representation.
- **Volumetric heatmap:** represent 3D cells/voxels or sampled points in room space.
- **Confidence:** encode sample count, recency, spatial coverage, pose/time uncertainty, and variance separately from RSSI. Low-confidence regions should fade, stipple, desaturate, or disappear—not look like measured weak signal.

## Dependency direction

```text
UI / Visualization  --->  Application services  --->  Domain models
        |                        |                       ^
        v                        v                       |
Meta XR adapter           USB/protocol adapter --------+
        |                        |
 external SDK              Android + firmware
```

Domain models must not depend on Unity, Android, Meta XR, or Espressif SDK types. External APIs should be wrapped behind narrow interfaces and simulated in automated tests.

## WiFiProbe v1 transport

### Framing and encoding

- UTF-8/ASCII-compatible text, one logical message per line, terminated by `\n`; accept optional preceding `\r`.
- Tokens are separated by one or more spaces.
- Commands are uppercase ASCII. Unknown commands return `ERR` and never crash or reset the probe.
- Strings such as SSIDs are JSON-style quoted UTF-8 with `\\`, `\"`, and escaped control characters. The parser must enforce a maximum line length.
- Numeric units are explicit in the specification below: time in microseconds unless named otherwise; RSSI in signed dBm.
- Output may be asynchronous. A measurement can arrive while another command is pending. Therefore correlate replies with a command sequence ID.
- Host sends a monotonically increasing unsigned command ID as the second token. The probe echoes it in `OK`, `ERR`, and command-specific terminal replies.
- Protocol version is negotiated through `HELLO`; incompatible major versions must fail clearly.
- Serial baud is relevant only for UART-backed adapters. Native USB CDC does not derive its link speed from `115200`, though hosts commonly set that nominal value for compatibility.

Recommended limits—confirm and codify in shared tests: 512-byte lines, bounded scan results, bounded outbound queue, and explicit behavior when measurement output cannot keep up.

### Commands

```text
HELLO <id> <host_protocol>
CAPS <id>
SCAN <id> [band=2.4|5|all]
SELECT <id> <bssid> <channel> [group=ssid]   (group: 1.2)
START <id> [interval_ms=<n>]
STOP <id>
STATUS <id>
STATS <id>
PING <id> <host_us>
RESET <id>
BUZZ <id> <duration_ms> [freq=<hz>]          (1.1)
BUZZ_OFF <id>                                (1.1)
```

Semantics:

- `HELLO` begins every connection and returns identity, firmware version, boot/session ID, and negotiated protocol.
- `CAPS` reports observed firmware/hardware capabilities, supported bands/channels, monitor ability, timestamp resolution, and relevant limits.
- `SCAN` is a finite operation. It emits zero or more `AP` records followed by `SCAN_DONE`. Define whether scanning temporarily stops monitoring; v1 should reject `SCAN` while streaming unless verified hardware supports both safely.
- `SELECT` binds monitoring to a BSSID and channel. Validate BSSID format and advertised channel support. With `group=ssid` (1.2) it also binds every other radio the last scan found with that BSSID's SSID on the channel (not hidden networks); each `SAMPLE` window then reports the strongest of them by mean, as a roaming client would connect, naming it in `from=`.
- `START` begins a new stream/session after selection. The actual sampling semantics must be reported; `interval_ms` may be a reporting/aggregation interval rather than radio sampling cadence.
- `STOP` is idempotent and ends streaming.
- `STATUS` returns current protocol/radio state and selected target.
- `STATS` returns counters/aggregates for the current or most recent session.
- `PING` supports clock mapping. The response includes receive/send probe timestamps so the host can estimate offset and round-trip uncertainty.
- `RESET` returns protocol/radio state to idle and clears selection/session statistics. It should not reboot by default; name a separate future command if hardware reboot becomes necessary.
- `BUZZ` (1.1) sounds the probe's buzzer for 1-10000 ms, optionally at 100-10000 Hz, replacing any tone already playing; it answers `OK <id> BUZZ duration_ms=<n> freq=<hz>` at once and the tone stops by itself. `BUZZ_OFF` (1.1) silences it. Without a buzzer both answer `ERR <id> NOT_SUPPORTED`. The firmware also silences the buzzer when the BLE host disconnects.

#### Version negotiation (1.0, 1.1 and 1.2)

The host sends the highest version it speaks (`HELLO <id> 1.2`). The probe accepts any `1.x` and answers the lower of the host's minor and its own (`protocol=1.0`, `1.1` or `1.2`), per link, so mismatched app and firmware settle on the earliest version both support:

- 1.0 firmware answers `protocol=1.0` to any `1.x`; a 1.1 host then uses only 1.0 features.
- Newer firmware answering an older host behaves exactly as that version: no new fields, events or commands (`BUZZ` is `UNKNOWN_COMMAND` at 1.0, `group=ssid` is `BAD_ARGS` below 1.2).
- A host never uses more than it asked for, whatever the reply claims. If a probe refuses the requested minor with `ERR`, the host retries `HELLO <id> 1.0`.

1.1 additions, only on links negotiated at 1.1: `BUZZ` / `BUZZ_OFF`; `CAPS` fields `protocol_max=`, `buzzer=true|false` and `buttons="A,B"` (the buttons the build has); a `buttons=<mask>` field (A = 1, B = 2, debounced) on `SAMPLE` and `STATUS`, plus `STATUS` `buzzing=`; and a `BUTTONS` event on every change of button state.

1.2 additions (firmware 0.4.0), only on links negotiated at 1.2: `SELECT ... group=ssid`; `aps=<n>` (radios measured) on `SELECTED` and `STATUS`; `from=<bssid>` and `heard=<n>` on `SAMPLE`; `CAPS` `select_group="ssid" group_reading="strongest-ap"`.

### Responses and events

```text
OK <id> <command> [key=value ...]
ERR <id> <code> "message" [key=value ...]
HELLO <id> protocol=1.0 device="..." fw="..." boot=<u64>
CAPS <id> bands=2.4,5 channels="..." monitor=true usb="cdc" ts_hz=<u64> max_line=<n>
AP <id> bssid=<mac> channel=<n> band=2.4|5 rssi=<dbm> ssid="..." security="..."
SCAN_DONE <id> count=<n>
SELECTED <id> bssid=<mac> channel=<n> band=2.4|5 [aps=<n>]
STARTED <id> session=<u64> probe_us=<u64>
STOPPED <id> session=<u64> samples=<u64> probe_us=<u64>
STATUS <id> state=<state> [key=value ...]
STATS <id> session=<u64> frames=<u64> reports=<u64> dropped=<u64> mean=<dbm> min=<dbm> max=<dbm> stddev=<db>
PONG <id> host_us=<u64> probe_rx_us=<u64> probe_tx_us=<u64> boot=<u64>
SAMPLE session=<u64> seq=<u64> probe_us=<u64> bssid=<mac> channel=<n> rssi=<dbm> frames=<n> mean=<dbm> min=<dbm> max=<dbm> stddev=<db> [buttons=<mask>] [from=<mac> heard=<n>]
EVENT code=<code> probe_us=<u64> "message"
BUTTONS buttons=<mask> probe_us=<u64>        (1.1)
```

Exact fields are normative only after implementation tests agree on a grammar. Parsers must tolerate unknown trailing `key=value` fields for forward-compatible minor versions, but reject missing required fields, invalid numeric ranges, and mismatched session/BSSID/channel.

### Example exchange

```text
> HELLO 1 1.2
< HELLO 1 protocol=1.2 device="WiFiProbe" fw="0.4.0" boot=9842331
> CAPS 2
< CAPS 2 bands=2.4,5 channels="1-11,36-48,149-165" monitor=true usb="cdc" ts_hz=1000000 max_line=512 protocol_max=1.2 buzzer=true buttons="A,B" select_group="ssid" group_reading="strongest-ap"
> SCAN 3 band=all
< AP 3 bssid=00:11:22:33:44:55 channel=6 band=2.4 rssi=-42 ssid="Home WiFi" security="WPA2"
< AP 3 bssid=AA:BB:CC:DD:EE:FF channel=36 band=5 rssi=-61 ssid="Home WiFi" security="WPA2"
< SCAN_DONE 3 count=2
> SELECT 4 AA:BB:CC:DD:EE:FF 36
< SELECTED 4 bssid=AA:BB:CC:DD:EE:FF channel=36 band=5 aps=1
> PING 5 3810042000
< PONG 5 host_us=3810042000 probe_rx_us=9211000 probe_tx_us=9211068 boot=9842331
> START 6 interval_ms=100
< STARTED 6 session=17 probe_us=9220000
< SAMPLE session=17 seq=1 probe_us=9320000 bssid=AA:BB:CC:DD:EE:FF channel=36 rssi=-58 frames=13 mean=-59 min=-64 max=-55 stddev=2.4 buttons=0 from=AA:BB:CC:DD:EE:FF heard=1
> STATS 7
< STATS 7 session=17 frames=126 reports=10 dropped=0 mean=-60 min=-69 max=-54 stddev=3.1
> STOP 8
< STOPPED 8 session=17 samples=10 probe_us=10221000
< BUTTONS buttons=1 probe_us=10530000
> BUZZ 9 35 freq=3000
< OK 9 BUZZ duration_ms=35 freq=3000
```

The `CAPS` values above are illustrative, not claims about ESP32-C5 or regulatory support.

## State machines

### Probe protocol/radio state

```text
              HELLO/CAPS
 [BOOT] ----------------------> [IDLE]
                                   |
                         SCAN      | SELECT
                           v       v
                       [SCANNING] [SELECTED]
                           |         |
                    done/error      | START
                           v         v
                         [IDLE]   [STREAMING]
                                      |
                               STOP / fault
                                      v
                                  [SELECTED]

 RESET from any non-boot state -> IDLE
 USB detach -> stop streaming safely; retain or clear selection by documented policy
 fatal radio error -> ERROR; STATUS remains available; RESET attempts recovery
```

`SCAN`, `SELECT`, and `START` must define legal source states and return `ERR ... BAD_STATE` otherwise. Never silently retune while streaming.

### Quest session state

```text
[NO_DEVICE] -> [USB_PERMISSION] -> [CONNECTING] -> [READY]
                                                /    |    \
                                           [SCANNING]| [SYNCING_CLOCK]
                                                     v
                                                 [CONFIGURED]
                                                     |
                                        tracking + alignment valid
                                                     v
                                                [RECORDING]
                                                     |
                                  stop/detach/tracking or alignment loss
                                                     v
                                          [CONFIGURED or DEGRADED]
```

Recording requires a compatible protocol, selected BSSID/channel, acceptable clock uncertainty, valid tracking, known antenna calibration, and a room/world alignment policy. If tracking is lost, either pause ingestion or store samples as rejected/unlocalized; never attach them to the latest known pose as if current.

## Time and pose synchronization

There are at least three time domains: probe clock, Android/Unity monotonic clock, and pose-sampling time. Wall-clock time is metadata only and must not drive fusion.

1. Capture probe-controller poses into a bounded history keyed by a monotonic timestamp from the host clock domain used around BLE receives (or USB reads if a future USB path is added).
2. Run repeated `PING` exchanges. Use host send/receive times and probe receive/send times to estimate clock offset and drift. Prefer low-round-trip samples and retain an uncertainty estimate.
3. Convert each `probe_us` into estimated host monotonic time. Reject or flag samples when the mapping is stale, the probe boot ID changes, sequence numbers jump, or uncertainty exceeds a tested threshold.
4. Interpolate position and orientation between bracketing poses. Do not extrapolate beyond a short, explicit limit.
5. Compute antenna pose from the tracked probe-controller pose and the calibrated controller-to-pod-to-antenna transform, then express it in the resolved room frame. Be explicit about transform direction, handedness, units, and tracking-origin changes.
6. Store raw inputs and derived antenna position so algorithms can be re-run when calibration or alignment improves.

USB arrival time alone is not measurement time. Batched reports must include enough firmware timing information to locate their observation window; if only aggregates are available, store window start/end or midpoint and duration.

## Antenna calibration

Represent the probe-controller-to-antenna relationship, separately for each side, as a rigid transform with a calibration version and hardware mount ID. At minimum, measure the antenna position offset; retain orientation because antenna radiation patterns are directional even if v1 heatmapping initially uses position only.

Calibration guidance:

- Define one canonical controller-local coordinate convention and document it next to serialized transforms.
- Provide a guided calibration or a carefully measured mount profile; never bury offsets in rendering code.
- Treat a changed mount, cable routing, enclosure, probe board, or headset model as a potentially different calibration.
- Store confidence/uncertainty and calibration ID with every recording session.

## Domain data models

Names are conceptual; adapt them to the language and existing code style.

```text
DeviceCapabilities
  protocolVersion, deviceId, firmwareVersion, bootId
  bands[], channelsByBand, monitorSupported, usbMode
  timestampFrequencyHz, limits, rawCapabilityFields

AccessPoint
  bssid, ssidBytes/displaySsid, channel, band
  scanRssiDbm, securityLabel, lastSeenHostTime

ClockMapping
  bootId, probeToHostOffset, drift, referenceTime
  roundTrip, uncertainty, lastUpdatedHostTime, sampleCount

PoseSample
  hostMonotonicTime, trackingFromController
  trackingState, positionValid, rotationValid

RawRadioSample
  sessionId, sequence, probeTime, bssid, channel
  rssiDbm, frameCount, mean/min/max/stddev, receiveHostTime

SpatialSample
  stableId, roomId/alignmentId, sessionId
  estimatedHostTime, roomFromAntenna
  rssi statistics, observationDuration
  timeUncertainty, poseUncertainty, calibrationId
  acceptance flags, rejection reason, createdWallTime

HeatmapCell
  room-space bounds/center, estimateDbm
  sampleCount, effectiveWeight, variance, confidence
  newestSampleTime, algorithmVersion

RecordingSession
  sessionId, AP identity, capabilities snapshot
  room/alignment ID, calibration ID, clock mapping history
  start/end times, software/firmware versions, settings, quality summary
```

Preserve BSSID as six raw bytes or a validated canonical value, not free-form display text. Preserve SSID bytes when possible because SSIDs are not guaranteed to be valid UTF-8. Define serialization versions and migration behavior before persisting user data.

## Spatial store and alignment

- Store accepted raw spatial samples separately from derived heatmap cells. Derived data is rebuildable and carries an algorithm version.
- Choose a canonical room coordinate frame backed by the verified Meta spatial-anchor/room mechanism.
- Save the anchor or stable room identifier plus the transform used during capture. On load, resolve/relocalize it before showing data as aligned.
- If relocalization fails or the room has materially changed, show the dataset as unavailable/unresolved and offer an explicit realignment workflow. Do not silently place old samples in the current tracking origin.
- Transactionally commit session metadata and samples; recover incomplete sessions after a crash.
- Bound storage and provide intentional export/delete behavior. Avoid storing unrelated network traffic or device identifiers beyond what the feature needs.

## Heatmap and confidence model

Begin with interpretable algorithms before more sophisticated RF reconstruction:

- Quantize room-space samples into configurable surface bins or 3D voxels.
- Use robust weighted aggregation (for example, median or trimmed/weighted mean) so bursts of frames at one pose do not dominate.
- Weight by spatial distance, observation count/duration, recency if appropriate, pose/time uncertainty, and within-window RSSI variance.
- Cap the effective contribution of repeated samples from nearly the same position/time.
- Do not average dBm as if it were linear power without documenting the intended estimator. If combining power physically, convert dBm to mW, aggregate, then convert back; if showing median observed RSSI, say so.
- Keep the color scale stable and labeled. Changing min/max automatically can create misleading comparisons.
- Compute confidence independently. A plausible starter confidence model combines local effective sample count, neighbor geometry/coverage, age, RSSI variance, and synchronization/tracking uncertainty.
- Make extrapolation visually distinct from interpolation and measured locations. Never paint unsampled areas with full confidence.

## Error handling and observability

- Every protocol error has a stable machine-readable code and human-readable message.
- Log lifecycle transitions, firmware/SDK versions, USB identifiers, negotiated capabilities, clock quality, dropped lines/samples, tracking state, alignment state, and heatmap algorithm version.
- Rate-limit repetitive logs and never log packet payloads. SSID/BSSID can be sensitive location data; keep logs local, minimize retention, and redact them from routine diagnostics when possible.
- Show actionable user states: permission needed, probe disconnected, unsupported firmware, 5 GHz unavailable, AP no longer visible, clock sync poor, tracking lost, room unresolved, or insufficient coverage.
- On reconnect, perform `HELLO`, compare boot ID, re-read `CAPS`, reselect deliberately, resynchronize clocks, and start a new measurement session. Do not append to the old session invisibly.

## Testing strategy and milestones

### Milestone 0 — capability spikes

- On both Quest 3 and 3S, prove USB host enumeration, permission, sustained reads, detach/reattach, power behavior, and app lifecycle recovery.
- On the exact probe board/SDK, prove AP scan, selected-channel monitor mode, BSSID filtering, RSSI metadata, timestamp behavior, and required 2.4/5 GHz channels.
- Prove MRUK room alignment persistence/relocalization with a tiny test dataset.
- Exit criterion: verification-gate results are documented; architecture is updated for failures.

### Milestone 1 — protocol and simulators

- Implement a strict WiFiProbe v1 parser plus golden transcript tests and fuzz/property tests for malformed, truncated, oversized, and interleaved lines.
- Build a desktop/fake probe stream and a firmware host-side test harness.
- Test state transitions, idempotent stop/reset, sequence gaps, boot changes, queue overflow, and unknown fields.
- Exit criterion: Unity/application logic can complete a simulated scan-select-stream-stop session without hardware.

### Milestone 2 — hardware vertical slice

- Connect probe to Quest, show capabilities, scan APs, distinguish duplicate SSIDs, select BSSID/channel, stream samples, and display live quality statistics.
- Measure sustained throughput, latency distributions, disconnect recovery, battery impact, and thermal behavior.
- Exit criterion: a repeatable on-headset session works on both target models or support is explicitly narrowed.

### Milestone 3 — time/pose fusion

- Implement clock offset/drift estimation and timestamped pose history.
- Validate with synthetic clock drift/delay and recorded pose traces.
- Bench-test antenna offset and end-to-end spatial lag while moving at known speeds.
- Exit criterion: measured spatial/time error falls within a documented product tolerance and degraded data is rejected or marked.

### Milestone 4 — spatial maps

- Persist raw sessions and room alignment.
- Render surface and volumetric heatmaps with legends and independent confidence display.
- Test sparse, clustered, contradictory, stale, and relocalized datasets.
- Exit criterion: users can tell measured, interpolated, low-confidence, and unresolved data apart.

### Milestone 5 — robustness and field validation

- Exercise suspend/resume, USB pulls, probe reboot, AP channel changes, tracking loss, room updates, storage migration, long sessions, and corrupt data.
- Compare repeated walks and known reference locations; quantify repeatability rather than relying on visual plausibility.
- Complete privacy, permissions, accessibility, and platform-distribution review.

### Test layers

- **Unit:** parsers, validators, state reducers, clock fit, pose interpolation, transforms, aggregation, confidence, serialization migration.
- **Contract:** shared protocol transcripts executed against firmware and host parser.
- **Integration:** Android bridge with a fake USB device and actual probe; Meta adapter with recorded fixtures where possible.
- **Hardware-in-loop:** exact board, cables/mount, Quest 3, Quest 3S, supported OS versions, 2.4/5 GHz AP matrix.
- **Visual:** deterministic datasets with screenshot/image-diff tests plus on-headset legibility checks.

## Primary risks and mitigations

| Risk | Impact | Guidance |
|---|---|---|
| Quest USB host/power behavior differs by model or lifecycle | Core path fails or disconnects | Validate first; test powered hub only as a diagnostic; design explicit reconnect flow |
| ESP32-C5/SDK lacks required 5 GHz monitor behavior | Cannot measure advertised targets | Capability-report at runtime; gate UI; support a verified alternative probe if needed |
| Regulatory/DFS restrictions | Missing channels or unsafe assumptions | Use SDK regulatory configuration; report allowed channels; never promise universal channel coverage |
| Android USB permission or Unity plug-in issues | Device inaccessible | Isolate bridge, test IL2CPP/build variants early, handle permission denial and activity recreation |
| USB/buffering latency corrupts spatial association | Heatmap displaced while moving | Firmware timestamps, clock sync, pose history, uncertainty thresholds, measured latency tests |
| RSSI varies by orientation/body occlusion/hardware | Misleading map | Store orientation and mount/calibration; aggregate robustly; communicate relative measurement limits |
| Persistent room relocalization drifts or fails | Old map appears in wrong place | Stable anchor metadata, quality checks, unresolved state, explicit realignment |
| Dense sampling creates false confidence | Visually persuasive but weak map | Cap correlated samples; base confidence on coverage geometry and uncertainty, not raw count alone |
| Protocol evolution breaks host/firmware compatibility | Field failures | Major/minor negotiation, tolerant trailing fields, shared transcript/contract tests |
| Sensitive location/network metadata leaks | Privacy harm | Minimize collection, local-first storage, redacted logs, explicit export/delete controls |

## Guidance for coding agents

1. Inspect the repository before proposing files. Preserve existing architecture and conventions unless the task explicitly authorizes a migration.
2. Separate verified facts, assumptions, and proposed behavior in code comments and documentation. Link capability decisions to a test result and exact device/SDK version.
3. Do not add a dependency merely to parse this small protocol or implement basic math unless the existing project already standardizes on it and the tradeoff is justified.
4. Keep platform and vendor APIs behind adapters. Make the protocol, clock, transform, aggregation, and persistence logic testable without Unity scenes or a headset.
5. Prefer immutable domain events and explicit state transitions. Avoid global singletons tying UI, USB, tracking, and storage together.
6. Treat all serial input as untrusted: bound memory, validate lengths/ranges, reject invalid state, escape display strings, and survive arbitrary byte fragmentation.
7. Use monotonic clocks for measurement fusion. Never synchronize by wall-clock timestamps or serial arrival time alone.
8. Never use SSID alone as identity. Bind sessions and samples to validated BSSID plus channel/band, and detect target/channel changes.
9. Preserve raw observations and provenance. Derived heatmaps must be reproducible and versioned.
10. Make uncertainty visible in types, persistence, tests, and UI. Do not collapse “unknown,” “not measured,” and “weak signal” into one color.
11. Keep changes small and vertically testable. Include tests for any protocol, time, transform, persistence, or state-machine change.
12. Do not claim support for Quest models, Wi-Fi bands/channels, USB modes, Meta APIs, or ESP features until hardware/API verification passes.
13. If specifications conflict with measured capability, stop and document the evidence and proposed architecture change before building around a workaround.

## Future robot localization extension

Add robot support only after the headset pipeline is stable. A robot becomes another `MeasurementSource`, but its pose and clock are not interchangeable with the Quest's. It needs:

- A verified localization transform into the same persistent room frame.
- Its own time synchronization and uncertainty model.
- Source/device/calibration identity on every sample.
- Cross-source validation and conflict handling.
- Safety and command boundaries if the application controls motion.

Do not route robot samples through a fake headset pose or merge them solely by wall-clock time. The spatial store and heatmap builder should accept multiple sources only when each sample has a resolved room transform and quantified timing/localization confidence.

## Definition of done for the architecture

The architecture is realized when the system can prove, on supported Quest and probe hardware, that it discovers and selects a specific AP, collects password-free passive RSSI observations with known timing quality, maps them through calibrated 6DOF poses into a persistently aligned room, renders surface and volumetric estimates without hiding uncertainty, survives expected lifecycle failures, and reproduces its results from stored raw data. Anything less should be labeled a prototype capability, not silently presented as complete support.
