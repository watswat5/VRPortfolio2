# WiFiProbe Arduino firmware

This sketch implements the newline-delimited WiFiProbe v1 protocol described in
the repository `AGENTS.md` for Arduino-ESP32 boards. It scans without joining an
AP, selects by BSSID and channel, uses ESP-IDF promiscuous receive metadata, and
emits interval RSSI aggregates over USB serial, Bluetooth Low Energy, or both.

## Probe boards

The same sketch runs on either probe; pick by how you want to carry it:

| Board | Carried | Bands | Extras | Board profile |
|---|---|---|---|---|
| **Seeed Studio XIAO ESP32-C5** (21 x 17.8 mm, U.FL dual-band antenna, LiPo charger) | In the printed clip on a Touch Plus controller | 2.4 and 5 GHz | none (the controller vibrates) | `XIAO_ESP32C5`, Arduino-ESP32 3.3.5 or newer |
| **M5Stack M5StickC Plus2** (classic ESP32, screen, battery) | On the wrist (Watch Kit strap, hand tracking) or on a controller (`hardware/m5_mount.py` clip, via the Watch Kit's LEGO plate) | 2.4 GHz only | status screen, buzzer, buttons A/B, power button | `m5stack_stickc_plus2` with Huge APP, plus the M5GFX library; see [below](#m5stickc-plus2-wrist-or-controller-probe) |

Both advertise as `WiFiProbe-C5` over BLE unless `BLE_DEVICE_NAME` is changed. The C5 is the intended
measurement board; the Plus2 trades 5 GHz for a self-contained unit (screen, buttons, battery) that goes on either the wrist or a controller. Neither entry replaces the
monitor-mode, channel, USB, power, and Quest verification required by `AGENTS.md`. An ESP32-C3 works for
bench tests (see the parent README).

## Build and upload

1. Install a current Arduino-ESP32 core and select the exact ESP32 board.
2. Set the transport and BLE configuration macros described below. If serial is
   enabled and the board has native USB, enable USB CDC on boot. Otherwise the
   serial transport uses the board's USB-to-UART adapter at 115200 baud.
3. Open `WiFiProbe.ino`, build, and upload it.
4. Begin every serial or BLE connection with `HELLO 1 1.2` (or `1.1`, or `1.0` for
   the original protocol), then request `CAPS 2`. Commands must end in `\n`; an
   optional preceding `\r` is accepted.

Complete board capability verification has not been established. Limited C3 BLE recovery and
compilation results are recorded in the parent README; they do not certify the full radio,
power or Quest integration matrix. In particular, 5 GHz is exposed
only when the selected Arduino target declares `SOC_WIFI_SUPPORT_5G`; channel
acceptance is still checked by the radio at `SELECT`. The `usb` capability is
reported conservatively as `arduino-serial`, because whether that is native CDC
or a UART bridge depends on the selected board and build settings.

## Protocol 1.1: buzzer and buttons

Firmware 0.3.0 and later speak protocol 1.1 and still speak 1.0 (0.3.1 only changes BLE packet sizing,
which is negotiated by BLE itself; see the BLE steps below). Each serial or BLE link
negotiates the lower of the host's requested minor and the firmware's own, so mismatched app and
firmware settle on the earliest version both support:

| Host asks for | 0.2.0 firmware (1.0) | 0.3.x firmware (1.1) | 0.4.0 firmware (1.2) |
|---|---|---|---|
| `HELLO <id> 1.0` (older apps) | `protocol=1.0` | `protocol=1.0`, exactly the 1.0 protocol | `protocol=1.0`, exactly 1.0 |
| `HELLO <id> 1.1` (desktop tester) | `protocol=1.0`; the host uses only 1.0 | `protocol=1.1` | `protocol=1.1`, exactly 1.1 |
| `HELLO <id> 1.2` (current Quest app) | `protocol=1.0`; the app uses only 1.0 | `protocol=1.1`; the app uses only 1.1 | `protocol=1.2` |

Only on links negotiated at 1.1:

- `BUZZ <id> <duration_ms> [freq=<hz>]` sounds the buzzer for 1-10000 ms
  (100-10000 Hz, default 4000), replacing any tone already playing, and answers
  `OK <id> BUZZ duration_ms=<n> freq=<hz>` at once; the tone stops by itself.
  `BUZZ_OFF <id>` silences it (`OK <id> BUZZ_OFF`). Without a buzzer both answer
  `ERR <id> NOT_SUPPORTED`. On a 1.0 link they are `UNKNOWN_COMMAND`, as before.
  A BLE disconnect silences the buzzer.
- Button state as a bitmask (A = 1, B = 2, debounced 20 ms): a `buttons=` field on
  every `SAMPLE` and `STATUS`, and a `BUTTONS buttons=<mask> probe_us=<u64>` event
  on each change, sent to every link negotiated at 1.1.
- `CAPS` adds `protocol_max=` (the firmware's newest, 1.2 from 0.4.0) `buzzer=true|false buttons="A,B"` (the buttons the
  build has), and `STATUS` adds `buzzing=true|false`.

The Quest app uses the buzzer for movement warnings when the probe is worn on the
wrist (no controller to vibrate), and buttons A and B to capture and cancel a
frozen alignment. With 1.0 firmware it falls back to a tone in the headset and to
hand pinches.

## Protocol 1.2: a network's access points together

Firmware 0.4.0 speaks protocol 1.2 (and 1.1 and 1.0, negotiated as above). An
institution's network (eduroam, a campus guest network) has dozens of access points
under one SSID, several of them on each channel. Version 1.2 measures them together:

- `SELECT <id> <bssid> <channel> group=ssid` selects the given radio and every other
  radio of its SSID on that channel that the last `SCAN` found (up to 64; the probe
  remembers 128 radios from a scan). A hidden network (empty SSID), or a radio the
  last scan didn't find, is measured alone. Without `group=ssid`, `SELECT` measures
  one radio exactly as in 1.0.
- `SELECTED` and `STATUS` add `aps=<n>`, the number of radios measured.
- Each `SAMPLE` window keeps separate statistics for every radio, and reports the
  strongest by its mean in that window: the access point a device roaming on that
  network would connect to there. All of `rssi`, `frames`, `mean`, `min`, `max`
  and `stddev` are that radio's. A 1.2 `SAMPLE` adds `from=<bssid>` (that radio)
  and `heard=<n>` (how many of the group were heard in the window); `bssid=` is
  still the radio named in `SELECT`, so hosts match samples to their selection as
  before. With several radios on the channel, fewer windows go without any frames.
  `STATS` covers the frames of the reported radios.
- `CAPS` adds `select_group="ssid" group_reading="strongest-ap"`.

On a 1.0 or 1.1 link, `group=ssid` is `BAD_ARGS`. The Quest app sends it only when
1.2 is negotiated; with older firmware it measures the group's strongest radio from
the scan alone.

## M5StickC Plus2 (wrist or controller probe)

Select **M5StickCPlus2** (`esp32:esp32:m5stack_stickc_plus2`) with **Huge APP
(3MB No OTA/1MB SPIFFS)**: like other classic ESP32s, Wi-Fi plus Bluedroid BLE
doesn't fit the default partition. On 2026-11-03 the sketch, with the status screen,
compiled for it with Arduino-ESP32 3.3.12 and M5GFX 0.2.20 at 1,788,723 bytes (56%),
and was flashed to a Plus2. Over USB serial, protocol 1.1 and 1.0 negotiation, scan,
select, streaming and `BUZZ` all worked on the device. On 2026-11-04 firmware 0.4.0
(1,790,235 bytes, 56%) was flashed to two Plus2s: over USB serial, protocol 1.2, `CAPS`,
a `group=ssid` select, `SAMPLE` `from=`/`heard=` and the 1.1 refusal of `group=ssid` all
worked, and the Quest app connects to it over BLE at protocol 1.2. The buttons haven't
been confirmed on the device yet.

```bash
python3 firmware/upload_firmware.py --fqbn esp32:esp32:m5stack_stickc_plus2:PartitionScheme=huge_app
```

For this board the sketch enables, from M5Stack's published Plus2 pinout (check
against your unit):

| Function | Pin | Notes |
|---|---|---|
| Power hold | G4 | Driven high at boot: on battery the Plus2 switches itself off otherwise |
| Buzzer | G2 | Passive buzzer, driven with `ledcWriteTone` |
| Button A | G37 | The large front button, active low |
| Button B | G39 | The side button, active low |

### Screen, LED and power button

On the Plus2 the sketch also runs a status screen through M5Stack's **M5GFX** library
(install it from the Library Manager; tested with M5GFX 0.2.20). Build with
`-DPROBE_M5_UI=0` to leave it out. M5Unified, which wraps M5GFX, doesn't fit: with
Wi-Fi and Bluedroid BLE its speaker and power drivers overflow the ESP32's IRAM by
about 4 KB, so the sketch drives the LED, buzzer, power hold and button itself.

- **Screen** (landscape, `PROBE_M5_ROTATION` 3; 1 turns it the other way up):
  - BLE status: waiting for headset, pairing with the PIN shown, connected,
    or pairing failed
  - the network being measured (its name from the last scan) and channel, or
    "Scanning…"
  - the probe state
  - the latest signal strength in dBm, with a red-to-green bar from −90 to −30 dBm
  - battery charge
- **Red LED (G19):** the pairing pattern: a short blip every second while
  advertising, fast blinks while pairing, steady when paired, three flashes after
  a failed pairing.
- **C (the power button, G35):** hold it for 1.5 s to switch off
  (`PROBE_M5_POWER_OFF_MS`). It stops measuring, says so on screen and releases the
  power hold. On USB power, which can't be cut, it deep-sleeps instead, and pressing
  C wakes it, which restarts the firmware. A hold that started while switching it on
  doesn't count.

Any board can set these with build flags instead: `PROBE_POWER_HOLD_PIN`,
`PROBE_BUZZER_PIN`, `PROBE_BUTTON_A_PIN`, `PROBE_BUTTON_B_PIN` (-1 turns a feature
off, the default elsewhere) and `PROBE_BUTTONS_ACTIVE_LOW`. The Plus2's ESP32 is
2.4 GHz only, so `CAPS` reports `bands=2.4` and the app lists only 2.4 GHz
networks. Its antenna is inside the case. On the wrist it sits against your arm, and your arm and
body weaken readings depending on which way you face; on the controller mount it is held out in front of the
controller, as the XIAO pod is.

## Transport selection

The default is `WIFI_PROBE_TRANSPORT_BOTH`: USB serial remains available for
development and BLE is available to the Quest. Override the macro with a build
flag or above its guarded definition:

```cpp
// Keep exactly one of these values.
#define WIFI_PROBE_TRANSPORT WIFI_PROBE_TRANSPORT_SERIAL
#define WIFI_PROBE_TRANSPORT WIFI_PROBE_TRANSPORT_BLE
#define WIFI_PROBE_TRANSPORT WIFI_PROBE_TRANSPORT_BOTH
```

The sketch also compiles for ESP32-C3 and classic ESP32 targets for bench
testing. On a classic ESP32, Wi-Fi plus the Bluedroid BLE stack does not fit the
Arduino core's default 1.25 MB application partition; select **Huge APP** (or a
custom partition with at least 1.7 MB for the application). This is a build
portability result, not proof that those boards support the C5's intended
dual-band measurement behavior.

The generic **ESP32C5 Dev Module** Wi-Fi + BLE build can also exceed its default
1.25 MB application partition. On hardware with at least 4 MB flash, select
**Huge APP (3MB No OTA/1MB SPIFFS)**, or use `--partition huge_app` with the
[desktop uploader](../README.md). This layout trades OTA update slots for a
larger application partition; confirm the board's physical flash capacity.

Negotiation and input line buffers are independent per transport. A command's
reply returns only to the transport that sent it. `SAMPLE` events return to the
transport that successfully issued `START`. When a BLE-started stream loses its
connection, monitoring stops and the probe returns to `SELECTED`; reconnecting
requires a new `HELLO` and clock synchronization before another `START`.

## BLE service and pairing

The firmware advertises as `WiFiProbe-C5` and uses Nordic UART Service-compatible
UUIDs:

| Role | UUID | Operation |
|---|---|---|
| Service | `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` | Discover this service |
| RX (probe input) | `6E400002-B5A3-F393-E0A9-E50E24DCCA9E` | Host writes command bytes |
| TX (probe output) | `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` | Host enables notifications |

RX and TX require an encrypted, bonded connection with MITM protection and
LE Secure Connections. The default prototype passkey is `123456`. Change it for
each field device; it is intentionally a compile-time setting rather than a
protocol command:

```cpp
#define BLE_PAIRING_PASSKEY 135790  // six digits, use a device-specific value
```

Pairing sequence:

1. Power the probe and discover `WiFiProbe-C5` from the host.
2. Connect and enter the configured six-digit passkey when prompted.
3. Discover the service and enable notifications on TX.
4. Write `HELLO 1 1.2\n` (or `1.1`, or `1.0`) to RX. Reassemble TX notification chunks until `\n`;
   chunk boundaries carry no meaning. From firmware 0.3.1 each chunk is the
   negotiated ATT MTU minus 3 (at most 244, as the probe offers MTU 247), so a
   host that never requests a larger MTU still gets the BLE minimum of 20 bytes,
   as all earlier firmware sent. A notification the BLE stack can't queue is
   retried for up to 500 ms instead of being dropped. None of this changes the
   protocol version; it is ordinary BLE MTU negotiation.
5. The bond is retained by the ESP32 BLE stack. If the host deletes its bond,
   delete the matching bond on the probe before pairing again. The firmware does
   not erase all saved bonds on boot.

BLE availability, bonding behavior, coexistence with Wi-Fi scanning/monitoring,
throughput, and Quest lifecycle handling still require measurement on the exact
XIAO ESP32-C5, Arduino core, Quest model, and OS build. `CAPS` reports configured
transport/security behavior, not proof that a hardware verification gate passed.

## Optional pairing LED

The LED pin is assignable and disabled by default so the sketch does not assume
which XIAO pins remain free in the enclosure. For an external active-high LED,
connect the GPIO through an appropriate series resistor to the LED, then to
ground, and set:

```cpp
#define BLE_PAIRING_LED_PIN D4      // replace with the chosen free Arduino pin
#define BLE_PAIRING_LED_ACTIVE_HIGH 1
```

Set `BLE_PAIRING_LED_ACTIVE_HIGH` to `0` if the LED circuit turns on when the
GPIO is low. Prefer the board's Arduino pin name (`D0` through `D10`) because a
silkscreen pin number is not necessarily the same as its raw ESP32 GPIO number.
Confirm the chosen pin against the exact XIAO ESP32-C5 pinout and assembled
hardware before wiring it.

| LED pattern | Meaning |
|---|---|
| Short pulse once per second | Advertising, no client connected |
| Fast blink | Connected and pairing/authenticating |
| Solid on | Authenticated encrypted connection |
| Three short flashes | Pairing/authentication failed |
| Off | BLE disabled or LED pin set to `-1` |

## Measurement semantics

`START interval_ms=n` enables promiscuous reception on the selected channel.
Frames are counted when any of the three ordinary 802.11 header addresses equals
the selected BSSID (with 1.2 `group=ssid`, any of the selected BSSIDs: see above). No payload is retained or emitted. Each `SAMPLE` is an
arithmetic aggregation of RSSI values in dBm over the reporting interval; this
is described by `aggregation="arithmetic-dbm"` in `CAPS` and is an observational
summary, not a physical power average. `probe_us` is the midpoint between the
first and last observation in the aggregate; the forward-compatible trailing
fields `window_start_us` and `window_end_us` preserve the full observation
window for host-side time/pose fusion.

Scanning retunes the radio and therefore clears selection. `SCAN` is rejected
while streaming. `STOP` is idempotent while selected. `RESET` stops monitoring,
clears selection and statistics, and does not reboot the board.

## Hardware validation still required

- Exact board, Arduino-ESP32/ESP-IDF versions, VID/PID and USB topology.
- Supported regulatory domain, bands/channels, DFS behavior, and monitor mode.
- Which frame classes provide reliable RSSI/channel/timestamps.
- USB power, detach/reconnect, sustained throughput, and Quest lifecycle behavior.

The firmware intentionally does not claim those results before testing on the
target probe and Quest hardware.
