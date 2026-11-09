# Desktop firmware tools

Run these commands from the `Demo6/` project directory. Python 3 is required. This folder was formerly
named `arduino`; the current checkout uses `firmware/`.

## Files and installation

| File | Responsibility |
|---|---|
| `test_gui.py` | Tk desktop tester (protocol 1.1 with 1.0 fallback), AP selection, buzzer/buttons, sample/statistics views and protocol log |
| `probe_transport.py` | Serial/BLE workers, device discovery, ordered writes and bounded line/event queues |
| `ble_pairing.py` | Linux BlueZ passkey agent and bond-aware pairing |
| `upload_firmware.py` | Arduino CLI board/port selection, temporary compilation and upload |
| `requirements.txt` | pyserial and Bleak dependency ranges |
| `test_probe_transport.py`, `test_ble_pairing.py` | Existing fake-transport and pairing-agent checks |
| [WiFiProbe/](WiFiProbe/README.md) | Actual Arduino firmware sketch and configuration |

```bash
python3 -m venv firmware/.venv
firmware/.venv/bin/python -m pip install -r firmware/requirements.txt
firmware/.venv/bin/python firmware/test_gui.py
```

Tk is an OS/Python installation prerequisite; Bleak uses the platform Bluetooth stack. On Linux,
its D-Bus support is supplied by Bleak's dependencies. Windows environments use `Scripts/python.exe`.
The uploader uses Python's standard library, but requires Arduino CLI and the desired ESP32 core
to be installed already. It does not install tools or guess the silicon from a shared USB ID.

Tests below are documented commands, not a new verification result for the initial Git snapshot.

## Compile and upload

```bash
python3 firmware/upload_firmware.py
```

Select the connected serial port, search for your model (enter `c5` for the
XIAO controller probe, `plus2` for the M5StickC Plus2 (wrist or controller), or `c3` for a
bench board), then select the exact board profile. Where supported, choose whether serial uses native USB or a USB-to-UART
adapter. USB IDs can be shared across boards; detected names are hints, so the
script asks you to select the model explicitly.

The script compiles `WiFiProbe/WiFiProbe.ino` into a temporary directory and
uploads only if compilation succeeds. Compiler and uploader output appears in
the terminal. Close Arduino Serial Monitor and disconnect `test_gui.py` from
the selected port before uploading. If esptool cannot connect, check the exact
board's BOOT/reset procedure and serial-port permissions.

List ports without uploading:

```bash
python3 firmware/upload_firmware.py --list
```

For a generic C3 dev module using native USB, specify the port and profile
directly (replace the example port with the correct one):

```bash
python3 firmware/upload_firmware.py --port /dev/ttyACM0 --fqbn esp32:esp32:esp32c3 --usb-cdc
```

Omit `--usb-cdc` for a USB-to-UART connection. Board settings such as flash size
and partition scheme can be supplied through the full `--fqbn` string. For the
XIAO C5, select `XIAO_ESP32C5` when searching `c5`.

For the **M5StickC Plus2**, install the **M5GFX** library first (Arduino Library
Manager, or `arduino-cli lib install M5GFX`) for its status screen, and use the Huge
APP partition, since Wi-Fi plus BLE doesn't fit the classic ESP32's default one. Its
USB port is a USB-to-UART bridge, so no `--usb-cdc`:

```bash
python3 firmware/upload_firmware.py --fqbn esp32:esp32:m5stack_stickc_plus2:PartitionScheme=huge_app
```

The Plus2 is 2.4 GHz only. It goes on the wrist (the Watch Kit strap) or on a controller
(the `hardware/m5_mount.py` clip); pins, screen and buttons are in
[WiFiProbe/README.md](WiFiProbe/README.md#m5stickc-plus2-wrist-or-controller-probe). A successful upload or
build does not establish radio, USB, BLE, or Quest hardware compatibility;
request `CAPS` and follow the verification gates in the repository `AGENTS.md`.

### C5: sketch exceeds the default application partition

The Wi-Fi + BLE build can exceed the generic C5 profile's default 1,310,720-byte
application partition. For a board with **at least 4 MB flash**, select
**Huge APP (3MB No OTA/1MB SPIFFS)** in the uploader's partition menu, or run:

```bash
python3 firmware/upload_firmware.py --port /dev/ttyACM1 --fqbn esp32:esp32:esp32c5 --usb-cdc --partition huge_app
```

Use your actual port and exact board profile. `huge_app` allocates about 3 MB to
the application and removes OTA update slots; uploading by USB still works.
It does not increase physical flash or RAM. Match the partition layout and
flash-size board option to your actual hardware. The uploader validates the
scheme against the selected profile and uses identical options for compilation
and upload. It also accepts `PartitionScheme=huge_app` in a full FQBN; use one
method of setting the scheme.

Local compilation on 2026-10-28 with Arduino-ESP32 3.3.12 and
`esp32:esp32:esp32c5:CDCOnBoot=cdc,PartitionScheme=huge_app` succeeded:
1,433,207 of 3,145,728 application bytes (45%) and 53,048 bytes of global RAM.
Mocked uploader checks confirmed matching compile/upload options and rejection
of unsupported partition names. These checks did not flash a board or verify
C5 radio operation.

The uploader uses Python's standard library and
[Arduino CLI](https://arduino.github.io/arduino-cli/latest/installation/).
It also finds a CLI installed in `~/.local/bin`, or accepts `--cli /path/to/arduino-cli`.
Install the ESP32 core if needed:

```bash
arduino-cli core update-index --additional-urls https://espressif.github.io/arduino-esp32/package_esp32_index.json
arduino-cli core install esp32:esp32 --additional-urls https://espressif.github.io/arduino-esp32/package_esp32_index.json
```

## Test over USB serial or BLE

```bash
python3 -m venv firmware/.venv
firmware/.venv/bin/python -m pip install -r firmware/requirements.txt
firmware/.venv/bin/python firmware/test_gui.py
```

If the virtual environment already exists, only the last command is needed. On Windows, use
`firmware\.venv\Scripts\python.exe` instead of `firmware/.venv/bin/python`.

For USB, select **USB serial**, choose the port, and connect.

For Bluetooth:

1. Power the probe with firmware built for `BOTH` (the default) or `BLE`.
2. Enable Bluetooth on the computer. Select **BLE** in the tester; discovery
   runs for six seconds and lists devices advertising the firmware's UART service.
   On Linux, it also includes probes already connected to BlueZ, labeled
   **already connected**. Those probes may no longer advertise after pairing in
   desktop Bluetooth settings. The list includes them using their resolved UART
   service rather than guessing from their name.
3. Choose the probe (normally `WiFiProbe-C5`, including when running on a C3 or an M5StickC Plus2),
   then click **Connect**.
4. On Linux, enter the passkey in the tester's **WiFiProbe BLE pairing** dialog.
   On other platforms, use the operating system pairing dialog. The default
   firmware passkey is **123456**, unless changed in the sketch. The tester
   allows up to 90 seconds for pairing. A saved Linux bond is reused, so
   reconnecting does not ask for the passkey again. The protocol log reports
   whether a saved bond was used or fresh pairing was requested.
5. Once the log shows `HELLO` and `CAPS`, scan Wi-Fi access points, select a
   specific AP by BSSID/channel, then click **Start**. **Stop** ends reporting.

USB and BLE use the same controls. BLE commands are written in ordered 20-byte
chunks with acknowledgement; notifications are reassembled into bounded lines,
whatever their size. On BLE the log shows the MTU the OS negotiated: firmware
0.3.1+ sends notifications of up to MTU - 3 bytes (older firmware, 20).

The tester asks for protocol 1.1 and uses only what the probe agrees to: firmware
0.3.0+ answers 1.1, older firmware 1.0 (and a probe that refuses 1.1 is asked for
1.0). Firmware 0.4.0 answers it at 1.1 too: the tester measures one radio at a time and doesn't use 1.2's
network groups. The status bar shows the negotiated version. On 1.1:

- **Capabilities** summarises `CAPS`: bands, buzzer and buttons. The scan band list
  offers only the bands the probe reports (the Plus2 is 2.4 GHz only).
- **Buzz** sends `BUZZ` with the given duration (1-10000 ms) and pitch (100-10000 Hz);
  **Buzz off** sends `BUZZ_OFF`. Both are disabled when the probe has no buzzer.
- **Buttons** shows the probe's A/B state from `BUTTONS` events and the `buttons=`
  field on `SAMPLE` and `STATUS`.

On 1.0 these stay disabled and the rest works as before.

Scans match `AP`/`SCAN_DONE` lines to the SCAN command's id. If `SCAN_DONE` reports
more networks than arrived, the status bar says how many were received; if the reply
stalls (30 s with no result, or 5 s after the last one without `SCAN_DONE`), the
tester stops waiting and keeps what arrived, as the Quest app does.
Bluetooth work runs outside Tk's UI thread. Unexpected disconnects are logged;
reconnect deliberately to negotiate a new session. This app does not
automatically erase Bluetooth bonds or silently downgrade firmware security.
If discovery finds nothing, check that another client is not already connected
and that firmware BLE support is enabled. If authentication fails repeatedly,
follow the firmware README's guidance for mismatched bonds.

If you deliberately want a fresh passkey exchange, disconnect in the tester,
forget the probe in Linux Bluetooth settings, then refresh and connect in the
tester. Forgetting the host entry alone does not prove the ESP32 has removed its
copy of the bond. A `failed to discover services, device disconnected` error
means the link closed before GATT setup finished; it does not identify an
authentication failure by itself. Check probe-side logs if this persists.
The tester never silently removes bonds or changes the passkey.

The Linux agent registers with BlueZ and calls `Pair` on the same D-Bus
connection, so BlueZ uses the tester's passkey callback. It unregisters after
pairing and does not replace the desktop's default agent. This follows the
[BlueZ Agent API](https://bluez.readthedocs.io/en/latest/agent-api/).

The GUI's protocol log shows commands and firmware responses. The BLE backend
uses [Bleak's client pairing and GATT APIs](https://bleak.readthedocs.io/en/latest/api/client.html).
See
[the firmware README](WiFiProbe/README.md) for transport, pairing, and measurement
details.

Hardware-free transport checks:

```bash
firmware/.venv/bin/python -m unittest discover -s firmware -p 'test_probe_transport.py' -v
firmware/.venv/bin/python -m unittest discover -s firmware -p 'test_ble_pairing.py' -v
```

On 2026-10-28, seven checks passed covering fragmented UTF-8 and coalesced lines,
oversized-line recovery, pairing/notification setup and ordered BLE writes with a
fake client, stale writes after reconnect, bounded queue overflow, and the serial
transport, and inclusion of connected BlueZ probes while excluding offline and
unrelated cached devices. The Tk window also opened/closed cleanly. An initial
live discovery found one advertising device; after the probe was paired and
connected through the OS, the updated discovery also found it without requiring
an advertisement. Those initial checks did not exercise hardware pairing or
RSSI streaming.

Four additional pairing-agent checks passed: numeric passkey handling, rejection
of invalid/cancelled/unrelated requests, cancellation of a pending request, and
registration/pairing/cleanup on the same bus with saved-bond reuse. A real
connection attempt reproduced the reported service-discovery disconnect with a
saved Linux bond.

### BLE recovery observed on the C3 test probe

On 2026-10-28, the device identified by the user as an ESP32-C3 Super Mini,
reporting firmware `0.2.0`, repeatedly disconnected before GATT service discovery
while Linux reported it paired and bonded. Serial `HELLO`, `CAPS`, and `STATUS`
continued to work. The running firmware reported serial/BLE transports and
`bond-mitm-sc-passkey` security.

Removing only this probe's Linux pairing record and explicitly pairing again
through the tester's BlueZ agent caused BlueZ to request a six-digit passkey.
Using the configured prototype passkey completed pairing, resolved two GATT
services, enabled TX notifications, and returned a valid WiFiProbe `HELLO` reply
to an RX write. A subsequent reconnect through `TransportWorker` reused the new
saved bond and returned another valid `HELLO` reply without a passkey request.
These checks used Bleak 3.0.2 and BlueZ 5.72. No firmware reflash, firmware bond
wipe, or weakened security setting was needed. Replacing the Linux bond resolved
this observed failure; the specific reason the previous bond failed was not
determined. The agent's real callback was exercised by a diagnostic callback
supplying the configured passkey; entering it through the Tk dialog remains a
manual UI check. Wi-Fi RSSI streaming over BLE, C5 behavior, and Quest behavior
were not verified by this recovery check.

## Local build check

On 2026-10-28, Arduino CLI 1.5.1 and Arduino-ESP32 3.3.12 successfully compiled
the current sketch for `esp32:esp32:esp32c3` with default settings and with
`CDCOnBoot=cdc`. The native USB build used 1,233,441 bytes (94% of the default
application partition). Uploader checks also covered interactive C3/native USB
selection and stopping before upload when compilation fails using mocked CLI
responses. No device was flashed by these checks; radio and transport behavior
remain unverified on hardware.
