"""Checks for the Linux pairing agent, without changing any Bluetooth bonds."""

import asyncio
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from dbus_fast import DBusError
from ble_pairing import PasskeyAgent, pair_linux_probe


class PairingTests(unittest.IsolatedAsyncioTestCase):
    async def test_requested_passkey_preserves_leading_zero_value(self):
        prompt = AsyncMock(return_value='012345')
        agent = PasskeyAgent('/probe', prompt)
        self.assertEqual(await agent.get_passkey('/probe'), 12345)
        prompt.assert_awaited_once()

    async def test_cancel_invalid_or_unrelated_request_rejected(self):
        for value in (None, '123', 'abcdef', '１２３４５６'):
            with self.subTest(value=value):
                with self.assertRaises(DBusError):
                    await PasskeyAgent('/probe', AsyncMock(return_value=value)).get_passkey('/probe')
        prompt = AsyncMock(return_value='123456')
        with self.assertRaises(DBusError):
            await PasskeyAgent('/probe', prompt).get_passkey('/other')
        prompt.assert_not_awaited()

    async def test_agent_cancel_unblocks_pending_request(self):
        async def prompt():
            await asyncio.Future()
        agent = PasskeyAgent('/probe', prompt)
        task = asyncio.create_task(agent.get_passkey('/probe'))
        await asyncio.sleep(0)
        agent.cancel()
        with self.assertRaises(DBusError):
            await task

    async def test_pair_uses_agent_bus_and_unregisters(self):
        class Bus:
            def __init__(self):
                self.agent = None
                self.disconnected = False
            async def connect(self):
                return self
            def export(self, path, agent):
                self.agent = agent
            def unexport(self, path):
                pass
            def disconnect(self):
                self.disconnected = True
        for already_paired in (False, True):
            bus = Bus()
            methods = []
            async def fake_call(caller_bus, path, interface, member, *args):
                self.assertIs(caller_bus, bus)
                methods.append(member)
                if member == 'Get':
                    return [SimpleNamespace(value=already_paired)]
                if member == 'Pair':
                    self.assertEqual(await bus.agent.get_passkey('/probe'), 123456)
                return []
            with patch('ble_pairing.MessageBus', return_value=bus), patch('ble_pairing.call', side_effect=fake_call):
                await pair_linux_probe(SimpleNamespace(details={'path': '/probe'}),
                                       AsyncMock(return_value='123456'), lambda _: None)
            self.assertTrue(bus.disconnected)
            self.assertEqual(methods, ['Get'] if already_paired else
                             ['Get', 'RegisterAgent', 'Pair', 'UnregisterAgent'])


if __name__ == '__main__':
    unittest.main()
