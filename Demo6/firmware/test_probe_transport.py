"""Hardware-free transport checks: python3 -m unittest discover -s firmware -p 'test_probe_transport.py'."""

import asyncio
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

import probe_transport as transport
from test_gui import (SCAN_FIRST_REPLY_S, SCAN_GAP_S, fields, format_buttons, negotiated_minor,
                      scan_deadline)


class FramingTests(unittest.TestCase):
    def test_connected_bluez_probe_does_not_need_advertising(self):
        def record(connected, uuids):
            return {'org.bluez.Device1': {
                key: SimpleNamespace(value=value) for key, value in {
                    'Address': 'AA:BB:CC:DD:EE:FF', 'Alias': 'WiFiProbe-C5',
                    'Connected': connected, 'UUIDs': uuids,
                }.items()
            }}
        fake_device = lambda address, name, details: SimpleNamespace(address=address, name=name, details=details)
        with patch.object(transport, 'BLEDevice', fake_device):
            results = transport.connected_bluez_probes({
                '/org/bluez/hci0/dev_probe': record(True, [transport.SERVICE_UUID.upper()]),
                '/org/bluez/hci0/dev_offline': record(False, [transport.SERVICE_UUID]),
                '/org/bluez/hci0/dev_unrelated': record(True, []),
            })
        self.assertEqual(len(results), 1)
        device, label = results[0]
        self.assertEqual(device.details['path'], '/org/bluez/hci0/dev_probe')
        self.assertIn('already connected', label.local_name)

    def test_every_byte_boundary_and_coalesced_utf8(self):
        raw = 'AP 3 ssid="Café \\"WiFi\\""\r\nSCAN_DONE 3 count=1\n'.encode()
        expected = [('line', 'AP 3 ssid="Café \\"WiFi\\""'), ('line', 'SCAN_DONE 3 count=1')]
        for split in range(len(raw) + 1):
            framer = transport.LineFramer()
            self.assertEqual(list(framer.feed(raw[:split])) + list(framer.feed(raw[split:])), expected)
        self.assertEqual(fields(expected[0][1])['ssid'], 'Café "WiFi"')

    def test_oversize_drops_suffix_and_recovers(self):
        framer = transport.LineFramer(8)
        self.assertEqual(list(framer.feed(b'x' * 10000)), [])
        self.assertLessEqual(len(framer.buffer), 8)
        result = list(framer.feed(b'fake\n12345678\nOK\r\n'))
        self.assertEqual([item for item in result if item[0] == 'line'], [('line', '12345678'), ('line', 'OK')])
        self.assertEqual(result[0][0], 'warning')


class FakeClient:
    instance = None

    def __init__(self, device, **kwargs):
        FakeClient.instance = self
        self.device = device
        self.options = kwargs
        self.is_connected = False
        self.writes = []
        self.exited = False

    async def __aenter__(self):
        self.is_connected = True
        return self

    async def __aexit__(self, *_):
        self.is_connected = False
        self.exited = True

    async def start_notify(self, uuid, callback):
        self.notify_uuid = uuid
        self.notify = callback
        callback(None, b'HELLO 1 protocol=')
        callback(None, b'1.0\nSAMPLE seq=1\n')

    async def write_gatt_char(self, uuid, data, response):
        self.writes.append((uuid, data, response))
        await asyncio.sleep(0)  # Let competing command writes try to interleave.


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.patcher = patch.object(transport, 'BleakClient', FakeClient)
        self.patcher.start()
        self.pairing_patcher = patch.object(transport, 'prepare_ble_pairing', new_callable=AsyncMock)
        self.pairing_patcher.start()
        self.worker = transport.TransportWorker()
        self.device = SimpleNamespace(address='test-device')

    def tearDown(self):
        self.worker.submit(self.worker.shutdown()).result(timeout=5)
        self.worker.thread.join(timeout=5)
        self.assertFalse(self.worker.thread.is_alive())
        self.patcher.stop()
        self.pairing_patcher.stop()

    def event(self, kind):
        for _ in range(20):
            event = self.worker.events.get(timeout=3)
            if event[1] == kind:
                return event
        self.fail(f'No {kind} event')

    def connect(self, generation=1):
        self.worker.submit(self.worker.connect(generation, 'BLE', self.device)).result(timeout=3)
        self.assertEqual(self.event('connected')[0], generation)
        return FakeClient.instance

    def test_pair_notifications_and_noninterleaved_chunk_writes(self):
        client = self.connect()
        self.assertEqual(client.options['pair'], transport.sys.platform != 'linux')
        self.assertEqual(client.notify_uuid, transport.TX_UUID)
        first = b'SELECT 2 AA:BB:CC:DD:EE:FF 36\n'
        second = b'HELLO 3 1.0\n'
        a = self.worker.submit(self.worker.write(1, first))
        b = self.worker.submit(self.worker.write(1, second))
        a.result(timeout=3)
        b.result(timeout=3)
        self.assertEqual(b''.join(item[1] for item in client.writes), first + second)
        self.assertTrue(all(uuid == transport.RX_UUID and len(data) <= 20 and response
                            for uuid, data, response in client.writes))

    def test_disconnect_and_reconnect_ignore_old_writes(self):
        old = self.connect()
        self.worker.submit(self.worker.disconnect()).result(timeout=3)
        self.assertTrue(old.exited)
        new = self.connect(2)
        self.worker.submit(self.worker.write(1, b'START 9\n')).result(timeout=3)
        self.assertEqual(new.writes, [])
        self.worker.loop.call_soon_threadsafe(new.options['disconnected_callback'], new)
        self.assertEqual(self.event('error')[0], 2)
        self.event('disconnected')
        self.assertTrue(new.exited)

    def test_queue_overflow_is_bounded_and_reported(self):
        for i in range(2100):
            self.worker.emit(1, 'line', str(i))
        self.assertEqual(self.worker.events.qsize(), 2048)
        self.assertEqual(self.worker.dropped_events, 52)

    def test_serial_uses_same_framing_and_closes(self):
        class FakeSerial:
            def __init__(self, *args, **kwargs):
                self.chunks = iter([b'HELLO 1 prot', b'ocol=1.0\r\n'])
                self.closed = False
                self.writes = []
            def read(self, _):
                return next(self.chunks, b'')
            def write(self, data):
                self.writes.append(data)
                return len(data)
            def close(self):
                self.closed = True
        port = FakeSerial()
        with patch.object(transport, 'serial', SimpleNamespace(Serial=lambda *a, **k: port)):
            self.worker.submit(self.worker.connect(1, 'USB serial', '/dev/fake')).result(timeout=3)
            self.event('connected')
            self.assertEqual(self.event('line')[2], 'HELLO 1 protocol=1.0')
            self.worker.submit(self.worker.write(1, b'CAPS 2\n')).result(timeout=3)
            self.assertEqual(port.writes, [b'CAPS 2\n'])
            self.worker.submit(self.worker.disconnect()).result(timeout=3)
            self.assertTrue(port.closed)



class ProtocolHelperTests(unittest.TestCase):
    """The GUI's protocol 1.1 negotiation and scan timeout, against the firmware's exact reply formats."""

    def test_negotiates_the_lower_minor_and_never_more_than_requested(self):
        reply11 = 'HELLO 1 protocol=1.1 device="WiFiProbe" fw="0.3.1" boot=8526666017888104035'
        reply10 = 'HELLO 1 protocol=1.0 device="WiFiProbe" fw="0.2.0" boot=7'
        self.assertEqual(negotiated_minor(reply11, 1), 1)
        self.assertEqual(negotiated_minor(reply10, 1), 0)   # 0.2.0 firmware: the tester falls back to 1.0
        self.assertEqual(negotiated_minor(reply11, 0), 0)   # a reply never raises what we asked for
        self.assertEqual(negotiated_minor('HELLO 1 protocol=1 fw="x"', 1), 0)
        self.assertIsNone(negotiated_minor('HELLO 1 protocol=2.0', 1))
        self.assertIsNone(negotiated_minor('HELLO 1 fw="x"', 1))

    def test_button_masks(self):
        self.assertEqual(format_buttons('0'), 'none')
        self.assertEqual(format_buttons('1'), 'A')
        self.assertEqual(format_buttons('2'), 'B')
        self.assertEqual(format_buttons('3'), 'A+B')
        self.assertEqual(format_buttons('x'), '?')
        self.assertEqual(format_buttons(fields('BUTTONS buttons=3 probe_us=10530000').get('buttons')), 'A+B')

    def test_scan_waits_long_for_the_first_line_then_short_between_lines(self):
        self.assertEqual(scan_deadline(100.0, None), 100.0 + SCAN_FIRST_REPLY_S)
        self.assertEqual(scan_deadline(100.0, 112.0), 112.0 + SCAN_GAP_S)


if __name__ == '__main__':
    unittest.main()
