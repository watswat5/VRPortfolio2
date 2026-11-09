package com.wifiheatmap.ble;

import android.Manifest;
import android.app.Activity;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanFilter;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelUuid;
import android.os.SystemClock;
import android.util.Base64;

import java.util.ArrayDeque;
import java.util.Collections;
import java.util.Queue;
import java.util.UUID;

/** Small Android BLE GATT bridge used by Esp32BleSignalTester. */
public final class Esp32BleBridge {
    private static final UUID RX_UUID = UUID.fromString("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    private static final UUID TX_UUID = UUID.fromString("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
    private static final UUID CLIENT_CONFIGURATION_UUID = UUID.fromString("00002902-0000-1000-8000-00805F9B34FB");
    private static final int WRITE_CHUNK_BYTES = 20;
    // Matches the probe's BLEDevice::setMTU(247). Firmware 0.3.1+ sizes its notifications to the negotiated MTU;
    // older firmware keeps sending 20-byte chunks, which arrive the same way at any MTU.
    private static final int REQUESTED_MTU = 247;
    private static final long MTU_REPLY_TIMEOUT_MS = 3000;

    public interface Listener {
        void onState(String state);
        void onBytes(String base64, long receivedUs);
        void onError(String error);
    }

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Queue<byte[]> pendingWrites = new ArrayDeque<>();

    private Context context;
    private Listener listener;
    private BluetoothAdapter adapter;
    private BluetoothLeScanner scanner;
    private BluetoothGatt gatt;
    private BluetoothDevice device;
    private BluetoothGattCharacteristic rxCharacteristic;
    private BluetoothGattCharacteristic txCharacteristic;
    private UUID serviceUuid;
    private String requestedDeviceName;
    private boolean receiverRegistered;
    private boolean servicesDiscovered;
    private boolean descriptorWriteInFlight;
    private boolean mtuRequestInFlight;
    private int mtuRequestGeneration; // a timeout from an earlier connection's request must not end a newer one
    private boolean ready;
    private boolean writeInFlight;
    private boolean closing;
    private Runnable scanTimeout;

    public Esp32BleBridge() {
    }

    /** Monotonic microseconds, same source for native callbacks and the Unity HostClock adapter.
     *  Monotonic time since boot, including deep sleep; never wall time. */
    public static long monotonicTimeUs() {
        return SystemClock.elapsedRealtimeNanos() / 1000L;
    }

    public void connect(
            Activity activity,
            String deviceName,
            String serviceUuidText,
            int scanTimeoutSeconds,
            Listener listener) {
        if (activity == null || listener == null) {
            throw new IllegalArgumentException("Activity and listener are required");
        }

        this.listener = listener;
        this.context = activity.getApplicationContext();
        this.requestedDeviceName = deviceName == null ? "" : deviceName;
        this.serviceUuid = UUID.fromString(serviceUuidText);
        this.closing = false;

        mainHandler.post(() -> startScan(Math.max(1, scanTimeoutSeconds)));
    }

    public void write(byte[] bytes) {
        if (bytes == null || bytes.length == 0) {
            return;
        }

        byte[] copy = bytes.clone();
        mainHandler.post(() -> {
            if (!ready) {
                reportError("BLE write requested before the probe was ready");
                return;
            }

            for (int offset = 0; offset < copy.length; offset += WRITE_CHUNK_BYTES) {
                int length = Math.min(WRITE_CHUNK_BYTES, copy.length - offset);
                byte[] chunk = new byte[length];
                System.arraycopy(copy, offset, chunk, 0, length);
                pendingWrites.add(chunk);
            }
            writeNextChunk();
        });
    }

    public void disconnect() {
        mainHandler.post(() -> {
            if (closing) {
                return;
            }
            closing = true;
            stopScan();
            closeGatt();
            reportState("DISCONNECTED");
        });
    }

    private void startScan(int timeoutSeconds) {
        closeGatt();
        closing = false;

        BluetoothManager manager = (BluetoothManager) context.getSystemService(Context.BLUETOOTH_SERVICE);
        if (manager == null) {
            reportError("Android Bluetooth service is unavailable");
            return;
        }
        adapter = manager.getAdapter();
        if (adapter == null || !adapter.isEnabled()) {
            reportError("Bluetooth is disabled or unavailable");
            return;
        }
        if (!hasRuntimePermissions()) {
            reportError("Android Bluetooth scan/connect permission is missing");
            return;
        }

        scanner = adapter.getBluetoothLeScanner();
        if (scanner == null) {
            reportError("Android BLE scanner is unavailable");
            return;
        }

        ScanFilter filter = new ScanFilter.Builder()
                .setServiceUuid(new ParcelUuid(serviceUuid))
                .build();
        ScanSettings settings = new ScanSettings.Builder()
                .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                .build();

        reportState("SCANNING");
        try {
            scanner.startScan(Collections.singletonList(filter), settings, scanCallback);
        } catch (SecurityException exception) {
            reportError("BLE scan permission denied: " + exception.getMessage());
            return;
        }

        scanTimeout = () -> {
            if (scanner != null) {
                stopScan();
                reportError("Timed out looking for " + requestedDeviceName);
            }
        };
        mainHandler.postDelayed(scanTimeout, timeoutSeconds * 1000L);
    }

    private final ScanCallback scanCallback = new ScanCallback() {
        @Override
        public void onScanResult(int callbackType, ScanResult result) {
            BluetoothDevice found = result.getDevice();
            String name;
            try {
                name = found.getName();
            } catch (SecurityException exception) {
                reportError("BLE device-name permission denied: " + exception.getMessage());
                return;
            }

            if (!requestedDeviceName.isEmpty() && !requestedDeviceName.equals(name)) {
                return;
            }
            connectGatt(found);
        }

        @Override
        public void onScanFailed(int errorCode) {
            stopScan();
            reportError("Android BLE scan failed with code " + errorCode);
        }
    };

    private void connectGatt(BluetoothDevice found) {
        if (gatt != null || closing) {
            return;
        }
        stopScan();
        device = found;
        registerBondReceiver();
        reportState("CONNECTING");
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                gatt = found.connectGatt(context, false, gattCallback, BluetoothDevice.TRANSPORT_LE);
            } else {
                gatt = found.connectGatt(context, false, gattCallback);
            }
        } catch (SecurityException exception) {
            reportError("BLE connect permission denied: " + exception.getMessage());
        }
    }

    private final BluetoothGattCallback gattCallback = new BluetoothGattCallback() {
        @Override
        public void onConnectionStateChange(BluetoothGatt callbackGatt, int status, int newState) {
            if (closing) {
                return;
            }
            if (status != BluetoothGatt.GATT_SUCCESS) {
                reportError("BLE connection failed with GATT status " + status);
                mainHandler.post(Esp32BleBridge.this::closeGatt);
                return;
            }

            if (newState == BluetoothProfile.STATE_CONNECTED) {
                reportState("CONNECTED");
                try {
                    callbackGatt.discoverServices();
                    beginBondIfNeeded();
                } catch (SecurityException exception) {
                    reportError("BLE service discovery permission denied: " + exception.getMessage());
                }
            } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                mainHandler.post(() -> {
                    closeGatt();
                    reportState("DISCONNECTED");
                });
            }
        }

        @Override
        public void onServicesDiscovered(BluetoothGatt callbackGatt, int status) {
            if (status != BluetoothGatt.GATT_SUCCESS) {
                reportError("BLE service discovery failed with GATT status " + status);
                return;
            }

            BluetoothGattService service = callbackGatt.getService(serviceUuid);
            if (service == null) {
                reportError("WiFiProbe BLE service was not found");
                return;
            }
            rxCharacteristic = service.getCharacteristic(RX_UUID);
            txCharacteristic = service.getCharacteristic(TX_UUID);
            if (rxCharacteristic == null || txCharacteristic == null) {
                reportError("WiFiProbe RX/TX characteristics were not found");
                return;
            }

            servicesDiscovered = true;
            mainHandler.post(Esp32BleBridge.this::requestLargerMtu);
        }

        @Override
        public void onMtuChanged(BluetoothGatt callbackGatt, int mtu, int status) {
            // Success or not, the link works at whatever MTU it has; carry on with notifications.
            mainHandler.post(() -> {
                mtuRequestInFlight = false;
                enableNotificationsWhenBonded();
            });
        }

        @Override
        public void onDescriptorWrite(BluetoothGatt callbackGatt, BluetoothGattDescriptor descriptor, int status) {
            descriptorWriteInFlight = false;
            if (status == BluetoothGatt.GATT_SUCCESS) {
                ready = true;
                reportState("READY");
                mainHandler.post(Esp32BleBridge.this::writeNextChunk);
                return;
            }

            if (device != null && device.getBondState() != BluetoothDevice.BOND_BONDED) {
                reportState("PAIRING");
                beginBondIfNeeded();
                return;
            }
            reportError("Enabling BLE notifications failed with GATT status " + status);
        }

        @Override
        public void onCharacteristicWrite(BluetoothGatt callbackGatt, BluetoothGattCharacteristic characteristic, int status) {
            writeInFlight = false;
            if (status != BluetoothGatt.GATT_SUCCESS) {
                pendingWrites.clear();
                reportError("BLE command write failed with GATT status " + status);
                return;
            }
            mainHandler.post(Esp32BleBridge.this::writeNextChunk);
        }

        @Override
        public void onCharacteristicChanged(BluetoothGatt callbackGatt, BluetoothGattCharacteristic characteristic) {
            long receivedUs = monotonicTimeUs();
            forwardBytes(characteristic.getValue(), receivedUs);
        }

        @Override
        public void onCharacteristicChanged(
                BluetoothGatt callbackGatt,
                BluetoothGattCharacteristic characteristic,
                byte[] value) {
            long receivedUs = monotonicTimeUs();
            forwardBytes(value, receivedUs);
        }
    };

    private void beginBondIfNeeded() {
        if (device == null) {
            return;
        }
        try {
            int bondState = device.getBondState();
            if (bondState == BluetoothDevice.BOND_BONDED) {
                reportState("BONDED");
                mainHandler.post(this::enableNotificationsWhenBonded);
            } else if (bondState == BluetoothDevice.BOND_BONDING) {
                reportState("PAIRING");
            } else {
                reportState("PAIRING");
                if (!device.createBond()) {
                    reportError("Android could not start BLE pairing");
                }
            }
        } catch (SecurityException exception) {
            reportError("BLE pairing permission denied: " + exception.getMessage());
        }
    }

    private final BroadcastReceiver bondReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context receiverContext, Intent intent) {
            if (!BluetoothDevice.ACTION_BOND_STATE_CHANGED.equals(intent.getAction()) || device == null) {
                return;
            }
            BluetoothDevice changed = intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE);
            if (changed == null || !changed.getAddress().equals(device.getAddress())) {
                return;
            }

            int state = intent.getIntExtra(BluetoothDevice.EXTRA_BOND_STATE, BluetoothDevice.BOND_NONE);
            int previous = intent.getIntExtra(BluetoothDevice.EXTRA_PREVIOUS_BOND_STATE, BluetoothDevice.BOND_NONE);
            if (state == BluetoothDevice.BOND_BONDED) {
                reportState("BONDED");
                mainHandler.postDelayed(Esp32BleBridge.this::enableNotificationsWhenBonded, 250L);
            } else if (state == BluetoothDevice.BOND_NONE && previous == BluetoothDevice.BOND_BONDING) {
                reportError("BLE pairing was rejected or failed");
            }
        }
    };

    private void registerBondReceiver() {
        if (receiverRegistered) {
            return;
        }
        IntentFilter filter = new IntentFilter(BluetoothDevice.ACTION_BOND_STATE_CHANGED);
        if (Build.VERSION.SDK_INT >= 33) {
            context.registerReceiver(bondReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            context.registerReceiver(bondReceiver, filter);
        }
        receiverRegistered = true;
    }

    /** Asks for a larger MTU before enabling notifications, so long replies need fewer packets. Optional: if
     *  Android refuses or never answers, notifications are enabled at the current MTU. */
    private void requestLargerMtu() {
        if (gatt == null || mtuRequestInFlight) {
            return;
        }
        try {
            mtuRequestInFlight = gatt.requestMtu(REQUESTED_MTU);
        } catch (SecurityException exception) {
            mtuRequestInFlight = false;
        }
        if (!mtuRequestInFlight) {
            enableNotificationsWhenBonded();
            return;
        }
        int generation = ++mtuRequestGeneration;
        mainHandler.postDelayed(() -> {
            if (mtuRequestInFlight && generation == mtuRequestGeneration) {
                mtuRequestInFlight = false;
                enableNotificationsWhenBonded();
            }
        }, MTU_REPLY_TIMEOUT_MS);
    }

    private void enableNotificationsWhenBonded() {
        if (!servicesDiscovered || ready || descriptorWriteInFlight || mtuRequestInFlight || gatt == null || device == null) {
            return;
        }
        if (device.getBondState() != BluetoothDevice.BOND_BONDED) {
            beginBondIfNeeded();
            return;
        }

        if (!gatt.setCharacteristicNotification(txCharacteristic, true)) {
            reportError("Android rejected BLE notification setup");
            return;
        }
        BluetoothGattDescriptor descriptor = txCharacteristic.getDescriptor(CLIENT_CONFIGURATION_UUID);
        if (descriptor == null) {
            reportError("BLE notification descriptor was not found");
            return;
        }

        descriptorWriteInFlight = true;
        boolean started;
        if (Build.VERSION.SDK_INT >= 33) {
            started = gatt.writeDescriptor(descriptor, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE) == 0;
        } else {
            descriptor.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
            started = gatt.writeDescriptor(descriptor);
        }
        if (!started) {
            descriptorWriteInFlight = false;
            reportError("Android could not write the BLE notification descriptor");
        }
    }

    private void writeNextChunk() {
        if (!ready || writeInFlight || gatt == null || rxCharacteristic == null) {
            return;
        }
        byte[] chunk = pendingWrites.poll();
        if (chunk == null) {
            return;
        }

        writeInFlight = true;
        boolean started;
        if (Build.VERSION.SDK_INT >= 33) {
            started = gatt.writeCharacteristic(
                    rxCharacteristic,
                    chunk,
                    BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT) == 0;
        } else {
            rxCharacteristic.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT);
            rxCharacteristic.setValue(chunk);
            started = gatt.writeCharacteristic(rxCharacteristic);
        }
        if (!started) {
            writeInFlight = false;
            pendingWrites.clear();
            reportError("Android could not start a BLE command write");
        }
    }

    private void forwardBytes(byte[] bytes, long receivedUs) {
        if (bytes == null || bytes.length == 0 || listener == null) {
            return;
        }
        String encoded = Base64.encodeToString(bytes, Base64.NO_WRAP);
        mainHandler.post(() -> {
            if (listener != null) {
                // Preserve callback arrival time across Base64 encoding, the Android main queue,
                // the JNI proxy and Unity's next frame. A fragmented line uses its final chunk time.
                listener.onBytes(encoded, receivedUs);
            }
        });
    }

    private void stopScan() {
        if (scanTimeout != null) {
            mainHandler.removeCallbacks(scanTimeout);
            scanTimeout = null;
        }
        if (scanner != null) {
            try {
                scanner.stopScan(scanCallback);
            } catch (SecurityException ignored) {
                // Permission may have been revoked while scanning.
            }
            scanner = null;
        }
    }

    private void closeGatt() {
        ready = false;
        servicesDiscovered = false;
        descriptorWriteInFlight = false;
        mtuRequestInFlight = false;
        writeInFlight = false;
        pendingWrites.clear();
        rxCharacteristic = null;
        txCharacteristic = null;

        if (gatt != null) {
            try {
                gatt.disconnect();
            } catch (SecurityException ignored) {
                // Closing must still proceed if permission changed.
            }
            gatt.close();
            gatt = null;
        }
        if (receiverRegistered) {
            try {
                context.unregisterReceiver(bondReceiver);
            } catch (IllegalArgumentException ignored) {
                // The receiver was already removed by Android.
            }
            receiverRegistered = false;
        }
        device = null;
    }

    private boolean hasRuntimePermissions() {
        if (Build.VERSION.SDK_INT >= 31) {
            return context.checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED &&
                    context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED;
        }
        return context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED;
    }

    private void reportState(String state) {
        mainHandler.post(() -> {
            if (listener != null) {
                listener.onState(state);
            }
        });
    }

    private void reportError(String error) {
        mainHandler.post(() -> {
            if (listener != null) {
                listener.onError(error);
            }
        });
    }
}
