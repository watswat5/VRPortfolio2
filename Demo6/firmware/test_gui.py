"""Desktop USB serial and BLE tester for the WiFiProbe Arduino firmware.

Run with: python firmware/test_gui.py
Install dependencies with: python -m pip install -r firmware/requirements.txt
BLE pairing uses an in-app passkey dialog on Linux and the OS dialog elsewhere.

Speaks protocol 1.1 (firmware 0.3.0+: buzzer, buttons) and falls back to 1.0 with older firmware, using only
what both sides negotiated.
"""

from __future__ import annotations

import json
import queue
import re
import time
import tkinter as tk
from tkinter import messagebox, simpledialog, ttk

from probe_transport import TransportWorker, list_ports


MAC_RE = re.compile(r"^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$")
FIELD_RE = re.compile(r'([A-Za-z_][\w.-]*)=("(?:\\.|[^"\\])*"|\S+)')


# The newest protocol minor this tester speaks, and the first with BUZZ/BUZZ_OFF, CAPS buzzer=/buttons= and button state.
HOST_MINOR = 1
BUZZER_AND_BUTTONS_MINOR = 1
# A scan's first AP line comes after the probe has scanned every channel; later lines follow quickly. Past these, a
# lost SCAN_DONE (as on a congested BLE link) ends the wait with the results received.
SCAN_FIRST_REPLY_S = 30.0
SCAN_GAP_S = 5.0


def fields(line: str) -> dict[str, str]:
    """Extract key=value fields from protocol records, including quoted text."""
    return {key: json.loads(value) if value.startswith('"') else value
            for key, value in FIELD_RE.findall(line)}


def negotiated_minor(hello_line: str, requested: int) -> int | None:
    """The minor version to use after a HELLO reply: the reply's, never above what was requested.
    None when the major version isn't 1. A reply without a minor counts as 1.0."""
    major, _, minor = fields(hello_line).get("protocol", "").partition(".")
    if major != "1":
        return None
    return min(int(minor) if minor.isdigit() else 0, requested)


def format_buttons(mask: str | int | None) -> str:
    """A button bitmask (A = 1, B = 2) as text."""
    try:
        value = int(mask)
    except (TypeError, ValueError):
        return "?"
    names = [name for bit, name in ((1, "A"), (2, "B")) if value & bit]
    return "+".join(names) if names else "none"


def scan_deadline(sent_at: float, last_line_at: float | None) -> float:
    """When to stop waiting for a SCAN reply (monotonic seconds); mirrors the Quest app."""
    return last_line_at + SCAN_GAP_S if last_line_at is not None else sent_at + SCAN_FIRST_REPLY_S


class ProbeTester(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("WiFiProbe Desktop Tester")
        self.geometry("980x650")
        self.minsize(780, 500)
        self.worker = TransportWorker()
        self.generation = 0
        self.discovery_generation = 0
        self.command_id = 0
        self.hello_id = None
        self.requested_minor = HOST_MINOR
        self.minor: int | None = None
        self.caps: dict[str, str] = {}
        self.scan_id: str | None = None
        self.scan_sent_at = 0.0
        self.scan_last_line_at: float | None = None
        self.scan_lines = 0
        self.connected = False
        self.connecting = False
        self.devices = {}
        self.reported_drops = 0
        self.last_error = None
        self.access_points: dict[str, dict[str, str]] = {}
        self._build_ui()
        self.refresh_ports()
        self.after(50, self._drain_lines)
        self.after(500, self._check_scan)
        self.protocol("WM_DELETE_WINDOW", self.close)

    def _build_ui(self) -> None:
        connection = ttk.LabelFrame(self, text="Connection")
        connection.pack(fill="x", padx=10, pady=(10, 5))
        self.transport_var = tk.StringVar(value="USB serial")
        self.transport_box = ttk.Combobox(connection, textvariable=self.transport_var,
                                         values=("USB serial", "BLE"), width=11, state="readonly")
        self.transport_box.pack(side="left", padx=6, pady=8)
        self.transport_box.bind("<<ComboboxSelected>>", lambda _: self.refresh_ports())
        self.port_var = tk.StringVar()
        self.port_box = ttk.Combobox(connection, textvariable=self.port_var, width=38, state="readonly")
        self.port_box.pack(side="left", padx=4, pady=8)
        self.refresh_button = ttk.Button(connection, text="Refresh", command=self.refresh_ports)
        self.refresh_button.pack(side="left", padx=4)
        self.connect_button = ttk.Button(connection, text="Connect", command=self.toggle_connection)
        self.connect_button.pack(side="left", padx=4)
        self.connection_status = tk.StringVar(value="Disconnected")
        ttk.Label(connection, textvariable=self.connection_status).pack(side="left", padx=12)

        controls = ttk.LabelFrame(self, text="Probe controls")
        controls.pack(fill="x", padx=10, pady=5)
        self.scan_band = tk.StringVar(value="all")
        ttk.Label(controls, text="Scan band").pack(side="left", padx=(8, 3), pady=8)
        self.band_box = ttk.Combobox(controls, textvariable=self.scan_band, values=("all", "2.4", "5"), width=7, state="readonly")
        self.band_box.pack(side="left", padx=3)
        ttk.Button(controls, text="Scan", command=self.scan).pack(side="left", padx=4)
        ttk.Label(controls, text="Report interval (ms)").pack(side="left", padx=(15, 3))
        self.interval_var = tk.StringVar(value="100")
        ttk.Entry(controls, textvariable=self.interval_var, width=8).pack(side="left", padx=3)
        ttk.Button(controls, text="Select AP", command=self.select_ap).pack(side="left", padx=(12, 4))
        ttk.Button(controls, text="Start", command=self.start_stream).pack(side="left", padx=4)
        ttk.Button(controls, text="Stop", command=self.stop_stream).pack(side="left", padx=4)
        ttk.Button(controls, text="Status", command=lambda: self.send("STATUS")).pack(side="left", padx=(12, 4))
        ttk.Button(controls, text="Stats", command=lambda: self.send("STATS")).pack(side="left", padx=4)
        ttk.Button(controls, text="Reset", command=self.reset_probe).pack(side="left", padx=4)

        extras = ttk.LabelFrame(self, text="Protocol 1.1 (firmware 0.3.0+)")
        extras.pack(fill="x", padx=10, pady=5)
        ttk.Label(extras, text="Buzz (ms)").pack(side="left", padx=(8, 3), pady=6)
        self.buzz_ms_var = tk.StringVar(value="200")
        ttk.Entry(extras, textvariable=self.buzz_ms_var, width=7).pack(side="left", padx=3)
        ttk.Label(extras, text="Hz").pack(side="left", padx=(8, 3))
        self.buzz_hz_var = tk.StringVar(value="4000")
        ttk.Entry(extras, textvariable=self.buzz_hz_var, width=7).pack(side="left", padx=3)
        self.buzz_button = ttk.Button(extras, text="Buzz", command=self.buzz, state="disabled")
        self.buzz_button.pack(side="left", padx=4)
        self.buzz_off_button = ttk.Button(extras, text="Buzz off", command=lambda: self.send("BUZZ_OFF"), state="disabled")
        self.buzz_off_button.pack(side="left", padx=4)
        self.buttons_var = tk.StringVar(value="Buttons: -")
        ttk.Label(extras, textvariable=self.buttons_var).pack(side="left", padx=(16, 4))
        self.caps_var = tk.StringVar(value="Capabilities: connect to read CAPS")
        ttk.Label(extras, textvariable=self.caps_var).pack(side="left", padx=(16, 4))

        ap_frame = ttk.LabelFrame(self, text="Nearby access points (select by BSSID + channel)")
        ap_frame.pack(fill="both", expand=True, padx=10, pady=5)
        cols = ("ssid", "bssid", "band", "channel", "rssi", "security")
        self.ap_table = ttk.Treeview(ap_frame, columns=cols, show="headings", height=8, selectmode="browse")
        for col, title, width in (("ssid", "SSID", 240), ("bssid", "BSSID", 180), ("band", "Band", 70), ("channel", "Channel", 80), ("rssi", "RSSI (dBm)", 100), ("security", "Security", 130)):
            self.ap_table.heading(col, text=title)
            self.ap_table.column(col, width=width, anchor="w")
        yscroll = ttk.Scrollbar(ap_frame, orient="vertical", command=self.ap_table.yview)
        self.ap_table.configure(yscrollcommand=yscroll.set)
        self.ap_table.pack(side="left", fill="both", expand=True, padx=(6, 0), pady=6)
        yscroll.pack(side="right", fill="y", padx=(0, 6), pady=6)
        self.ap_table.bind("<Double-1>", lambda _event: self.select_ap())

        bottom = ttk.Panedwindow(self, orient="vertical")
        bottom.pack(fill="both", expand=True, padx=10, pady=(5, 10))
        sample_frame = ttk.LabelFrame(bottom, text="Live samples / device responses")
        log_frame = ttk.LabelFrame(bottom, text="Protocol log")
        bottom.add(sample_frame, weight=1)
        bottom.add(log_frame, weight=2)
        self.sample_text = self._text_area(sample_frame, height=5)
        self.log_text = self._text_area(log_frame, height=7)
        self.log_text.tag_configure("tx", foreground="#2457a7")
        self.log_text.tag_configure("error", foreground="#a32020")

    @staticmethod
    def _text_area(parent: ttk.LabelFrame, height: int) -> tk.Text:
        area = tk.Text(parent, height=height, wrap="none", state="disabled", font=("TkFixedFont", 9))
        scroll = ttk.Scrollbar(parent, orient="vertical", command=area.yview)
        area.configure(yscrollcommand=scroll.set)
        area.pack(side="left", fill="both", expand=True, padx=(6, 0), pady=5)
        scroll.pack(side="right", fill="y", padx=(0, 6), pady=5)
        return area

    def _append(self, widget: tk.Text, text: str, tag: str | None = None) -> None:
        widget.configure(state="normal")
        widget.insert("end", text + "\n", tag or "")
        # Bound display history during long streaming sessions.
        if int(widget.index("end-1c").split(".")[0]) > 1500:
            widget.delete("1.0", "101.0")
        widget.see("end")
        widget.configure(state="disabled")

    def refresh_ports(self) -> None:
        if self.connected or self.connecting:
            return
        self.discovery_generation += 1
        self.devices.clear()
        self.port_box["values"] = ()
        self.port_var.set("")
        if self.transport_var.get() == "BLE":
            self.refresh_button.configure(state="disabled", text="Scanning…")
            self.connect_button.configure(state="disabled")
            self.connection_status.set("Discovering BLE probes (6 seconds)…")
            self.worker.submit(self.worker.discover(self.discovery_generation))
            return
        self.refresh_button.configure(text="Refresh", state="normal")
        self.connect_button.configure(state="normal")
        if list_ports is None:
            self.connection_status.set("Install pyserial to use serial ports")
            return
        self.port_box["values"] = [p.device for p in list_ports.comports()]
        if self.port_box["values"]:
            self.port_box.current(0)
        self.connection_status.set("Disconnected")

    def _connection_controls(self, busy: bool) -> None:
        self.transport_box.configure(state="disabled" if busy else "readonly")
        self.port_box.configure(state="disabled" if busy else "readonly")
        self.refresh_button.configure(state="disabled" if busy else "normal")
        self.connect_button.configure(text="Disconnect" if busy else "Connect", state="normal")

    def toggle_connection(self) -> None:
        if self.connected or self.connecting:
            self.disconnect()
            return
        choice = self.port_var.get()
        device = self.devices.get(choice) if self.transport_var.get() == "BLE" else choice
        if not device:
            messagebox.showinfo("Select a device", "Refresh and choose a probe first.")
            return
        self.generation += 1
        self.connecting = True
        self.last_error = None
        self.hello_id = None
        self._reset_session()
        self._connection_controls(True)
        self.connection_status.set("Connecting… Approve Bluetooth pairing if prompted.")
        self.worker.submit(self.worker.connect(self.generation, self.transport_var.get(), device))

    def _reset_session(self) -> None:
        """Forget everything negotiated with the last probe connection."""
        self.minor = None
        self.caps = {}
        self.scan_id = None
        self.access_points.clear()
        self._render_access_points()
        self.band_box.configure(values=("all", "2.4", "5"))
        self._update_extras()

    def _update_extras(self) -> None:
        on_11 = self.minor is not None and self.minor >= BUZZER_AND_BUTTONS_MINOR
        buzzer = on_11 and self.caps.get("buzzer") == "true"
        for button in (self.buzz_button, self.buzz_off_button):
            button.configure(state="normal" if buzzer else "disabled")
        if self.minor is None:
            self.buttons_var.set("Buttons: -")
            self.caps_var.set("Capabilities: connect to read CAPS")
        elif not on_11:
            self.buttons_var.set("Buttons: n/a (protocol 1.0)")

    def disconnect(self) -> None:
        self.generation += 1  # Ignore queued results from the previous connection.
        self.connected = self.connecting = False
        self.hello_id = None
        self.scan_id = None
        self.minor = None
        self._update_extras()
        self.worker.submit(self.worker.disconnect())
        self.connection_status.set("Disconnected")
        self._connection_controls(False)

    def _drain_lines(self) -> None:
        # Limit work per tick so a burst of samples cannot freeze Tk.
        for _ in range(200):
            try:
                generation, kind, payload = self.worker.events.get_nowait()
            except queue.Empty:
                break
            if kind in ("devices", "scan_error"):
                if generation != self.discovery_generation or self.transport_var.get() != "BLE":
                    continue
                self.refresh_button.configure(state="normal", text="Refresh")
                self.connect_button.configure(state="normal")
                if kind == "devices":
                    self.devices = {
                        f"{adv.local_name or device.name or 'BLE probe'} · {device.address}": device
                        for device, adv in payload
                    }
                    self.port_box["values"] = list(self.devices)
                    if self.devices:
                        self.port_box.current(0)
                    self.connection_status.set(f"Found {len(self.devices)} BLE probe(s)" if self.devices else
                                               "No probes found — check power and other BLE connections, then Refresh")
                else:
                    self.connection_status.set("BLE discovery failed")
                    messagebox.showerror("BLE discovery failed", payload)
                continue
            if generation != self.generation:
                continue
            if kind == "connected":
                self.connected = True
                self.connecting = False
                self.connection_status.set(f"Connected: {payload}")
                self.requested_minor = HOST_MINOR
                self.hello_id = self.send_raw("HELLO", f"1.{HOST_MINOR}")
                self.after(10000, lambda g=generation: self._check_hello(g))
            elif kind == "disconnected":
                self.connected = self.connecting = False
                self.hello_id = None
                self.scan_id = None
                self.minor = None
                self._update_extras()
                self._connection_controls(False)
                self.connection_status.set(self.last_error or "Disconnected — reconnect to start a new session")
            elif kind == "passkey":
                self.connection_status.set("Enter the probe's six-digit BLE passkey")
                value = simpledialog.askstring("WiFiProbe BLE pairing",
                                              "Enter the six-digit passkey configured in the ESP32 firmware:",
                                              show="*", parent=self)
                self.worker.answer_passkey(payload, value)
            elif kind == "status":
                self._append(self.log_text, payload)
                self.connection_status.set(payload)
            elif kind == "line":
                try:
                    self._handle_line(payload)
                except (ValueError, TypeError) as exc:
                    self._append(self.log_text, f"Invalid response: {exc}", "error")
            elif kind in ("error", "warning"):
                if kind == "error":
                    self.last_error = payload
                self._append(self.log_text, payload, "error")
                self.connection_status.set(payload)
        if self.worker.dropped_events != self.reported_drops:
            self.reported_drops = self.worker.dropped_events
            self._append(self.log_text, f"GUI queue overflow: {self.reported_drops} events dropped", "error")
        self.after(50, self._drain_lines)

    def _check_hello(self, generation: int) -> None:
        if generation == self.generation and self.hello_id is not None:
            self._append(self.log_text, "HELLO timed out; check firmware and BLE authentication.", "error")
            self.disconnect()

    def _handle_line(self, line: str) -> None:
        self._append(self.log_text, f"RX  {line}", "error" if line.startswith("ERR") or "error" in line.lower() else None)
        parts = line.split()
        if not parts:
            return
        reply_id = parts[1] if len(parts) > 1 else None
        if parts[0] == "AP":
            # Lines from an abandoned (timed-out) scan carry an older id.
            if self.scan_id is None or reply_id != self.scan_id:
                return
            self.scan_lines += 1
            self.scan_last_line_at = time.monotonic()
            data = fields(line)
            data["id"] = reply_id
            bssid = data.get("bssid", "")
            if MAC_RE.fullmatch(bssid) and len(self.access_points) < 512:
                self.access_points[bssid.lower()] = data
                self._render_access_points()
        elif parts[0] == "SCAN_DONE":
            if self.scan_id is None or reply_id != self.scan_id:
                return
            self.scan_id = None
            count = fields(line).get("count", "?")
            # Fewer AP lines than the probe counted means some were lost on the way (e.g. a congested BLE link).
            missing = f"; only {self.scan_lines} received" if count.isdigit() and int(count) > self.scan_lines else ""
            self.connection_status.set(f"Scan complete: {count} AP(s){missing}")
        elif parts[0] == "SAMPLE":
            data = fields(line)
            self._show_buttons(data.get("buttons"))
            self._append(self.sample_text, f"{data.get('probe_us', '?')} µs  {data.get('bssid', '?')} ch {data.get('channel', '?')}  RSSI {data.get('rssi', '?')} dBm  ({data.get('frames', '?')} frames; mean {data.get('mean', '?')})")
        elif parts[0] == "BUTTONS":
            self._show_buttons(fields(line).get("buttons"))
        elif parts[0] == "HELLO":
            data = fields(line)
            if reply_id != str(self.hello_id):
                return
            self.hello_id = None
            minor = negotiated_minor(line, self.requested_minor)
            if minor is None:
                self._append(self.log_text, "Incompatible protocol version", "error")
                self.disconnect()
                return
            self.minor = minor
            self._update_extras()
            self.connection_status.set(f"Ready · firmware {data.get('fw', '?')} · protocol 1.{minor}")
            self.send("CAPS")
        elif parts[0] == "ERR" and reply_id is not None and reply_id == str(self.hello_id) and self.requested_minor > 0:
            # A probe that refuses the minor we asked for gets the earliest protocol instead.
            self.requested_minor = 0
            self.hello_id = self.send_raw("HELLO", "1.0")
        elif parts[0] == "CAPS":
            self.caps = fields(line)
            bands = [band for band in self.caps.get("bands", "").split(",") if band]
            if bands:
                self.band_box.configure(values=["all", *bands])
                if self.scan_band.get() not in ("all", *bands):
                    self.scan_band.set("all")
            summary = f"Capabilities: bands {', '.join(bands) or '?'}"
            if self.minor is not None and self.minor >= BUZZER_AND_BUTTONS_MINOR:
                summary += f" · buzzer {self.caps.get('buzzer', '?')} · buttons {self.caps.get('buttons') or 'none'}"
            self.caps_var.set(summary)
            self._update_extras()
        elif parts[0] == "STATUS":
            data = fields(line)
            self._show_buttons(data.get("buttons"))
            self.connection_status.set(f"Probe state: {data.get('state', 'unknown')}")

    def _show_buttons(self, mask: str | None) -> None:
        # buttons= only appears on links negotiated at 1.1.
        if mask is not None and self.minor is not None and self.minor >= BUZZER_AND_BUTTONS_MINOR:
            self.buttons_var.set(f"Buttons: {format_buttons(mask)}")

    def _check_scan(self) -> None:
        if self.scan_id is not None and time.monotonic() > scan_deadline(self.scan_sent_at, self.scan_last_line_at):
            self.scan_id = None
            text = (f"Scan reply incomplete: {self.scan_lines} AP line(s) received, no SCAN_DONE"
                    if self.scan_lines else "No scan reply from the probe")
            self._append(self.log_text, text, "error")
            self.connection_status.set(text + " — scan again")
        self.after(500, self._check_scan)

    def _render_access_points(self) -> None:
        for item in self.ap_table.get_children():
            self.ap_table.delete(item)
        for bssid, ap in sorted(self.access_points.items(), key=lambda item: (item[1].get("ssid", "").lower(), item[0])):
            ssid = ap.get("ssid", "") or "<hidden SSID>"
            self.ap_table.insert("", "end", iid=bssid, values=(ssid, ap.get("bssid", bssid), ap.get("band", ""), ap.get("channel", ""), ap.get("rssi", ""), ap.get("security", "")))

    def send_raw(self, command: str, *args: str) -> int | None:
        """Sends one command line; returns its command id, or None when not connected."""
        if not self.connected:
            messagebox.showinfo("Not connected", "Connect to the probe first.")
            return None
        self.command_id += 1
        line = f"{command} {self.command_id}" + (" " + " ".join(args) if args else "")
        self.worker.submit(self.worker.write(self.generation, (line + "\n").encode("ascii")))
        self._append(self.log_text, f"TX  {line}", "tx")
        return self.command_id

    def send(self, command: str, *args: str) -> int | None:
        return self.send_raw(command, *args)

    def scan(self) -> None:
        if not self.connected:
            self.send("SCAN")
            return
        self.access_points.clear()
        self._render_access_points()
        scan_id = self.send("SCAN", f"band={self.scan_band.get()}")
        self.scan_id = str(scan_id)
        self.scan_sent_at = time.monotonic()
        self.scan_last_line_at = None
        self.scan_lines = 0
        self.connection_status.set("Scanning…")

    def buzz(self) -> None:
        try:
            duration = int(self.buzz_ms_var.get())
            frequency = int(self.buzz_hz_var.get())
            if not (1 <= duration <= 10000 and 100 <= frequency <= 10000):
                raise ValueError
        except ValueError:
            messagebox.showerror("Invalid buzz", "Enter 1-10000 ms and 100-10000 Hz.")
            return
        self.send("BUZZ", str(duration), f"freq={frequency}")

    def select_ap(self) -> None:
        selected = self.ap_table.selection()
        if not selected:
            messagebox.showinfo("Select an access point", "Choose an AP from the scan results first.")
            return
        ap = self.access_points.get(selected[0])
        if not ap:
            return
        self.send("SELECT", ap.get("bssid", ""), ap.get("channel", ""))

    def start_stream(self) -> None:
        try:
            interval = int(self.interval_var.get())
            if not 20 <= interval <= 60000:
                raise ValueError
        except ValueError:
            messagebox.showerror("Invalid interval", "Enter a whole number from 20 to 60000 ms.")
            return
        self.send("START", f"interval_ms={interval}")

    def stop_stream(self) -> None:
        self.send("STOP")

    def reset_probe(self) -> None:
        if messagebox.askyesno("Reset probe", "Reset protocol state and clear the selected AP and session statistics?"):
            self.send("RESET")

    def close(self) -> None:
        self.worker.submit(self.worker.shutdown())
        self.destroy()


if __name__ == "__main__":
    ProbeTester().mainloop()
