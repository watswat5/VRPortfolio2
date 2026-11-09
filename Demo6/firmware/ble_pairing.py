"""Linux pairing agent: request a passkey in the tester, on the Pair caller's bus.

No default agent is replaced and no bonds are removed. Other platforms use
Bleak's operating-system pairing UI.
"""

import asyncio
from dbus_fast import BusType, Message, MessageType, DBusError
from dbus_fast.aio import MessageBus
from dbus_fast.service import ServiceInterface, method


async def call(bus, path, interface, member, signature='', body=None):
    reply = await bus.call(Message(destination='org.bluez', path=path,
                                   interface=interface, member=member,
                                   signature=signature, body=body or []))
    if reply.message_type == MessageType.ERROR:
        raise DBusError(reply.error_name, str(reply.body[0]) if reply.body else member)
    return reply.body


class PasskeyAgent(ServiceInterface):
    def __init__(self, device_path, request_passkey):
        super().__init__('org.bluez.Agent1')
        self.device_path = device_path
        self.request_passkey = request_passkey
        self.pending = None

    async def get_passkey(self, device):
        if device != self.device_path:
            raise DBusError('org.bluez.Error.Rejected', 'Unexpected pairing device')
        self.pending = asyncio.create_task(self.request_passkey())
        try:
            value = await self.pending
            if value is None:
                raise DBusError('org.bluez.Error.Canceled', 'Pairing cancelled')
            if len(value) != 6 or not value.isascii() or not value.isdigit():
                raise DBusError('org.bluez.Error.Rejected', 'Passkey must contain six digits')
            return int(value)
        except asyncio.CancelledError:
            raise DBusError('org.bluez.Error.Canceled', 'Pairing cancelled') from None
        finally:
            self.pending = None

    def cancel(self):
        if self.pending:
            self.pending.cancel()

    @method()
    async def RequestPasskey(self, device: 'o') -> 'u':
        return await self.get_passkey(device)

    @method()
    async def RequestPinCode(self, device: 'o') -> 's':
        return f'{await self.get_passkey(device):06d}'

    @method()
    def RequestAuthorization(self, device: 'o'):
        # This firmware requires authenticated passkey pairing. Do not accept
        # a Just Works fallback when no passkey exchange was requested.
        raise DBusError('org.bluez.Error.Rejected', 'Probe requires passkey pairing')

    @method()
    def Cancel(self):
        self.cancel()

    @method()
    def Release(self):
        self.cancel()


async def pair_linux_probe(device, request_passkey, status):
    path = device.details['path']
    bus = await MessageBus(bus_type=BusType.SYSTEM).connect()
    agent_path = '/com/wifiheatmap/PasskeyAgent'
    agent = PasskeyAgent(path, request_passkey)
    registered = False
    try:
        paired = await call(bus, path, 'org.freedesktop.DBus.Properties', 'Get',
                            'ss', ['org.bluez.Device1', 'Paired'])
        if paired[0].value:
            status('Linux has a saved bond; reconnecting without a new passkey prompt.')
            return
        bus.export(agent_path, agent)
        await call(bus, '/org/bluez', 'org.bluez.AgentManager1', 'RegisterAgent',
                   'os', [agent_path, 'KeyboardOnly'])
        registered = True
        status('Pairing with probe; waiting for a passkey request…')
        # Pair and RegisterAgent must use this same D-Bus connection so BlueZ
        # selects our agent instead of an unrelated desktop default agent.
        try:
            await asyncio.wait_for(call(bus, path, 'org.bluez.Device1', 'Pair'), 90)
        except (asyncio.CancelledError, TimeoutError):
            try:
                await call(bus, path, 'org.bluez.Device1', 'CancelPairing')
            except Exception:
                pass
            raise
        status('Pairing finished; discovering BLE services…')
    finally:
        agent.cancel()
        if registered:
            try:
                await call(bus, '/org/bluez', 'org.bluez.AgentManager1', 'UnregisterAgent',
                           'o', [agent_path])
            except Exception:
                pass
        bus.unexport(agent_path)
        bus.disconnect()
