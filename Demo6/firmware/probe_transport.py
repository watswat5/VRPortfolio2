"""Serial/BLE byte transport for the desktop tester; no Tk calls in this module."""

import asyncio
import queue
import sys
import threading
from types import SimpleNamespace

try:
    import serial
    from serial.tools import list_ports
except ImportError:
    serial = None
    list_ports = None

try:
    from bleak import BleakClient, BleakScanner
    from bleak.backends.device import BLEDevice
except ImportError:
    BleakClient = BleakScanner = None
    BLEDevice = None

SERVICE_UUID = '6e400001-b5a3-f393-e0a9-e50e24dcca9e'
RX_UUID = '6e400002-b5a3-f393-e0a9-e50e24dcca9e'
TX_UUID = '6e400003-b5a3-f393-e0a9-e50e24dcca9e'


async def prepare_ble_pairing(device, request_passkey, status):
    if sys.platform == 'linux':
        from ble_pairing import pair_linux_probe
        await pair_linux_probe(device, request_passkey, status)


def connected_bluez_probes(objects):
    """Convert BlueZ Device1 records into selectable, currently connected probes.

    BlueZ's public object manager supplies path/properties. The details shape
    matches Bleak's BlueZ backend (checked with Bleak 3.0.2); keeping it here
    avoids an implicit advertisement scan when connecting to a cached device.
    """
    devices = []
    for path, interfaces in objects.items():
        values = interfaces.get('org.bluez.Device1', {})
        props = {name: value.value for name, value in values.items()}
        uuids = [uuid.lower() for uuid in props.get('UUIDs', [])]
        if not props.get('Connected') or SERVICE_UUID not in uuids or not props.get('Address'):
            continue
        name = props.get('Alias') or props.get('Name') or 'BLE probe'
        device = BLEDevice(props['Address'], name, {'path': path, 'props': props})
        devices.append((device, SimpleNamespace(local_name=f'{name} (already connected)')))
    return devices


async def linux_connected_probes():
    if sys.platform != 'linux':
        return []
    # dbus-fast is installed by Bleak on Linux. Do not invoke bluetoothctl or
    # disconnect an existing OS connection just to make the probe advertise.
    from dbus_fast import BusType, Message, MessageType
    from dbus_fast.aio import MessageBus

    bus = await MessageBus(bus_type=BusType.SYSTEM).connect()
    try:
        reply = await bus.call(Message(destination='org.bluez', path='/',
                                       interface='org.freedesktop.DBus.ObjectManager',
                                       member='GetManagedObjects'))
        if reply.message_type == MessageType.ERROR:
            raise RuntimeError(f'BlueZ device lookup failed: {reply.error_name}')
        return connected_bluez_probes(reply.body[0])
    finally:
        bus.disconnect()


class LineFramer:
    """Bound lines and discard an entire oversized record, including its suffix."""

    def __init__(self, limit=512):
        self.limit = limit
        self.buffer = bytearray()
        self.discarding = False

    def feed(self, data):
        for byte in data:
            if byte == 10:
                if self.discarding:
                    yield 'warning', 'Discarded an oversized response.'
                else:
                    yield 'line', self.buffer.rstrip(b'\r').decode('utf-8', errors='replace')
                self.buffer.clear()
                self.discarding = False
            elif not self.discarding:
                if len(self.buffer) >= self.limit:
                    self.buffer.clear()
                    self.discarding = True
                else:
                    self.buffer.append(byte)


class TransportWorker:
    """One persistent asyncio loop for discovery, pairing, and ordered writes.

    Events carry a GUI generation ID so results from an old connection cannot
    change a later session. Memory is bounded even if the GUI cannot keep up.
    """

    def __init__(self):
        self.events = queue.Queue(maxsize=2048)
        self.dropped_events = 0
        self.generation = None
        self.passkey_future = None
        self.loop = asyncio.new_event_loop()
        self.session_task = None
        self.scan_task = None
        self.client = None
        self.serial_port = None
        self.write_lock = None
        self.lifecycle_lock = None
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self):
        asyncio.set_event_loop(self.loop)
        self.write_lock = asyncio.Lock()
        self.lifecycle_lock = asyncio.Lock()
        self.loop.run_forever()
        pending = asyncio.all_tasks(self.loop)
        for task in pending:
            task.cancel()
        if pending:
            self.loop.run_until_complete(asyncio.gather(*pending, return_exceptions=True))
        self.loop.close()

    def emit(self, generation, kind, payload):
        try:
            self.events.put_nowait((generation, kind, payload))
        except queue.Full:
            self.dropped_events += 1
            # Prefer fresh diagnostics and connection events over old samples.
            try:
                self.events.get_nowait()
            except queue.Empty:
                pass
            self.events.put_nowait((generation, kind, payload))

    def submit(self, coroutine):
        return asyncio.run_coroutine_threadsafe(coroutine, self.loop)

    async def discover(self, generation):
        if self.scan_task:
            self.scan_task.cancel()
            await asyncio.gather(self.scan_task, return_exceptions=True)
        self.scan_task = asyncio.create_task(self._discover(generation))

    async def _discover(self, generation):
        try:
            if BleakScanner is None:
                raise RuntimeError('Install BLE support: python3 -m pip install "bleak>=1.0,<4"')
            # Preserve BLEDevice objects: connecting by address alone can trigger
            # a second discovery and behaves differently across OS backends.
            async with BleakScanner(service_uuids=[SERVICE_UUID]) as scanner:
                await asyncio.sleep(6)
                devices = list(scanner.discovered_devices_and_advertisement_data.values())
            # A bonded probe can remain connected through desktop Bluetooth
            # settings and stop advertising. Merge it with the live scan.
            try:
                connected = await linux_connected_probes()
            except Exception as exc:
                connected = []
                self.emit(generation, 'warning', f'Could not check connected BLE probes: {exc}')
            merged = {device.address: (device, adv) for device, adv in devices}
            merged.update({device.address: (device, adv) for device, adv in connected})
            self.emit(generation, 'devices', list(merged.values())[:256])
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            self.emit(generation, 'scan_error', str(exc))

    async def disconnect(self):
        async with self.lifecycle_lock:
            self.generation = None
            await self._disconnect()

    async def _disconnect(self):
        if self.session_task:
            self.session_task.cancel()
            await asyncio.gather(self.session_task, return_exceptions=True)
            self.session_task = None

    async def connect(self, generation, transport, device):
        async with self.lifecycle_lock:
            self.generation = generation
            if self.scan_task:
                self.scan_task.cancel()
                await asyncio.gather(self.scan_task, return_exceptions=True)
                self.scan_task = None
            await self._disconnect()
            self.session_task = asyncio.create_task(self._session(generation, transport, device))

    async def _session(self, generation, transport, device):
        framer = LineFramer()

        def receive(data):
            for kind, payload in framer.feed(data):
                self.emit(generation, kind, payload)

        try:
            if transport == 'USB serial':
                if serial is None:
                    raise RuntimeError('Install serial support: python3 -m pip install pyserial')
                self.serial_port = serial.Serial(device, 115200, timeout=0, write_timeout=1)
                # Some USB serial boards reset on open. Retain incoming bytes
                # while waiting to negotiate with the newly booted firmware.
                await asyncio.sleep(1.2)
                self.emit(generation, 'connected', str(device))
                while True:
                    receive(self.serial_port.read(256))
                    await asyncio.sleep(0.01)
            else:
                if BleakClient is None:
                    raise RuntimeError('Install BLE support: python3 -m pip install "bleak>=1.0,<4"')
                await prepare_ble_pairing(
                    device, lambda: self._request_passkey(generation),
                    lambda text: self.emit(generation, 'status', text))
                lost = asyncio.Event()
                async with BleakClient(device, pair=sys.platform != 'linux', timeout=90,
                                       disconnected_callback=lambda _: lost.set()) as client:
                    self.client = client
                    await client.start_notify(TX_UUID, lambda _, data: receive(data))
                    self.emit(generation, 'connected', device.address)
                    # The OS negotiates the ATT MTU; firmware 0.3.1+ sends notifications of up to MTU - 3 bytes.
                    mtu = getattr(client, 'mtu_size', None)
                    if isinstance(mtu, int):
                        self.emit(generation, 'status', f'BLE MTU {mtu} (notifications up to {mtu - 3} bytes)')
                    await lost.wait()
                    raise ConnectionError('BLE device disconnected. Reconnect to start a new session.')
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            error = str(exc)
            if 'failed to discover services' in error.lower():
                error += (' — Disconnected before GATT setup. A saved bond can suppress the passkey prompt; '
                          'this error alone does not identify the cause. Check both devices\' bonding state and firmware logs.')
            self.emit(generation, 'error', error)
        finally:
            self.client = None
            if self.serial_port:
                self.serial_port.close()
                self.serial_port = None
            self.emit(generation, 'disconnected', None)

    async def _request_passkey(self, generation):
        future = self.loop.create_future()
        self.passkey_future = future
        self.emit(generation, 'passkey', future)
        try:
            return await asyncio.wait_for(future, 90)
        finally:
            self.passkey_future = None

    def answer_passkey(self, future, value):
        def resolve():
            if not future.done():
                future.set_result(value)
        self.loop.call_soon_threadsafe(resolve)

    async def write(self, generation, data):
        try:
            async with self.write_lock:
                if generation != self.generation:
                    return
                if self.client and self.client.is_connected:
                    client = self.client
                    # Firmware accepts writes with response; stay within the
                    # minimum ATT payload and keep all chunks of a line ordered.
                    for offset in range(0, len(data), 20):
                        if generation != self.generation:
                            return
                        await client.write_gatt_char(RX_UUID, data[offset:offset + 20], response=True)
                elif self.serial_port:
                    if self.serial_port.write(data) != len(data):
                        raise IOError('Incomplete serial write')
                else:
                    raise ConnectionError('Probe is disconnected')
        except Exception as exc:
            self.emit(generation, 'error', str(exc))
            await self.disconnect()

    async def shutdown(self):
        if self.scan_task:
            self.scan_task.cancel()
            await asyncio.gather(self.scan_task, return_exceptions=True)
        await self.disconnect()
        self.loop.call_soon(self.loop.stop)
