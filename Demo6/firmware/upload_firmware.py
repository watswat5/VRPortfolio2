#!/usr/bin/env python3
"""Select a connected board, compile WiFiProbe, and upload using Arduino CLI."""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


SKETCH = Path(__file__).resolve().parent / "WiFiProbe"


def choose(items, prompt):
    for index, (label, _) in enumerate(items, 1):
        print(f"  {index}. {label}")
    while True:
        answer = input(f"{prompt} [1-{len(items)}] (q to cancel): ").strip()
        if answer.lower() == "q":
            raise KeyboardInterrupt
        if answer.isdigit() and 1 <= int(answer) <= len(items):
            return items[int(answer) - 1][1]
        print("Enter one of the listed numbers.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", help="Serial port; otherwise select from detected ports")
    parser.add_argument("--fqbn", help="Exact Arduino board ID, including optional board options")
    parser.add_argument("--usb-cdc", action="store_true", help="Enable native USB serial (CDCOnBoot=cdc)")
    parser.add_argument("--partition", help="Board partition scheme, e.g. huge_app (3 MB app, no OTA, requires 4 MB flash)")
    parser.add_argument("--list", action="store_true", help="List connected ports without uploading")
    parser.add_argument("--cli", help="Path to arduino-cli")
    args = parser.parse_args()

    cli = args.cli or shutil.which("arduino-cli")
    local_cli = Path.home() / ".local" / "bin" / "arduino-cli"
    if not cli and local_cli.is_file():
        cli = str(local_cli)
    if not cli:
        parser.exit(1, "Arduino CLI is required: https://arduino.github.io/arduino-cli/latest/installation/\n")

    def json_command(*command):
        result = subprocess.run([cli, *command, "--json"], capture_output=True, text=True, check=True)
        return json.loads(result.stdout)

    result = json_command("board", "list")
    detected = result.get("detected_ports", []) if isinstance(result, dict) else result
    ports = [entry for entry in detected
             if entry.get("port", {}).get("protocol") == "serial"]

    def port_label(entry):
        port = entry["port"]
        names = ", ".join(board["name"] for board in entry.get("matching_boards", []))
        return f"{port['address']} — {names or port.get('label', 'Unidentified serial device')}"

    if args.list:
        for entry in ports:
            print(port_label(entry))
        if not ports:
            print("No serial boards detected. Check the USB cable and board power.")
        return 0

    if args.port:
        selected = next((entry for entry in ports if entry["port"]["address"] == args.port), None)
        if selected is None:
            parser.exit(1, f"Serial port {args.port!r} was not detected. Run with --list.\n")
    else:
        if not ports:
            parser.exit(1, "No serial boards detected. Check the USB data cable and board power.\n")
        print("Connected serial devices (an unidentified port may be a USB-to-UART adapter):")
        selected = choose([(port_label(entry), entry) for entry in ports], "Select device")
    port = selected["port"]["address"]

    fqbn = args.fqbn
    if not fqbn:
        # Shared USB IDs can produce misleading matches (including the hidden
        # esp32_family placeholder). Ask for the exact model before compilation.
        result = json_command("board", "listall")
        boards = [b for b in result.get("boards", [])
                  if b.get("fqbn", "").startswith("esp32:") and not b.get("is_hidden")]
        search = input("Board model to search installed ESP32 boards (e.g. c3 or c5): ").strip()
        if not search:
            parser.exit(1, "A board model is required; alternatively pass --fqbn.\n")
        boards = [b for b in boards if search.lower() in (b["name"] + " " + b["fqbn"]).lower()]
        if not boards:
            parser.exit(1, "No matching installed ESP32 board. Install the ESP32 core or pass --fqbn.\n")
        fqbn = choose([(f"{b['name']} ({b['fqbn']})", b["fqbn"]) for b in boards], "Select exact board type")

    if not fqbn.startswith("esp32:"):
        parser.exit(1, "WiFiProbe requires an Arduino-ESP32 board profile.\n")
    details = json_command("board", "details", "--fqbn", fqbn)
    cdc_option = next((option for option in details.get("config_options", [])
                       if option["option"] == "CDCOnBoot"), None)
    use_cdc = args.usb_cdc
    if not args.fqbn and cdc_option:
        use_cdc = args.usb_cdc or choose([
            ("Native USB connected directly to the ESP32 (enable USB serial)", True),
            ("USB-to-UART bridge / external serial adapter (use UART serial)", False),
        ], "Select serial connection")
    if use_cdc:
        if not cdc_option:
            parser.exit(1, "This board profile has no CDCOnBoot option; omit --usb-cdc.\n")
        if "CDCOnBoot=" in fqbn:
            parser.exit(1, "Set CDCOnBoot in --fqbn or use --usb-cdc, rather than both.\n")
        fqbn += ("," if fqbn.count(":") > 2 else ":") + "CDCOnBoot=cdc"
    partition_option = next((option for option in details.get("config_options", [])
                             if option["option"] == "PartitionScheme"), None)
    partition = args.partition
    if not args.fqbn and not partition and partition_option:
        print("Wi-Fi + BLE may exceed the default app partition. Match the layout to your board's flash size.")
        partition = choose([(value["value_label"], value["value"])
                            for value in partition_option["values"]
                            if value["value"] != "custom"], "Select partition scheme")
    if partition:
        if not partition_option or partition not in {v["value"] for v in partition_option["values"]}:
            parser.exit(1, f"Partition scheme {partition!r} is not supported by this board profile.\n")
        if "PartitionScheme=" in fqbn:
            parser.exit(1, "Set PartitionScheme in --fqbn or use --partition, rather than both.\n")
        fqbn += ("," if fqbn.count(":") > 2 else ":") + f"PartitionScheme={partition}"
    print(f"\nCompiling {SKETCH.name} for {fqbn}, then uploading to {port}.", flush=True)
    print("Close Serial Monitor and disconnect the desktop tester from this port first.", flush=True)
    # A fresh build directory prevents upload of binaries from another board.
    with tempfile.TemporaryDirectory(prefix="wifiprobe-build-") as build:
        subprocess.run([cli, "compile", "--fqbn", fqbn, "--build-path", build, str(SKETCH)], check=True)
        subprocess.run([cli, "upload", "--port", port, "--fqbn", fqbn, "--input-dir", build, str(SKETCH)], check=True)
    print("Upload complete. Open test_gui.py to test the firmware.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (KeyboardInterrupt, EOFError):
        print("\nCancelled.", file=sys.stderr)
        sys.exit(130)
    except subprocess.CalledProcessError as exc:
        print(f"Arduino CLI failed (exit {exc.returncode}).", file=sys.stderr)
        if exc.stderr:
            print(exc.stderr.strip(), file=sys.stderr)
        sys.exit(1)
    except (OSError, ValueError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        sys.exit(1)
