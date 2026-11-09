using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using WifiHeatmap.Core;
using static WifiHeatmap.Core.ProbeFields;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace WifiHeatmap.Probe
{
    /// <summary>
    /// Android/Quest BLE client for the WiFiProbe firmware. With Auto Start On Connect it
    /// connects, negotiates and measures the configured (or strongest) access point on its own;
    /// otherwise it stops at Ready and a UI drives it through Scan, Measure and StopMeasuring.
    /// Bind OnSignalStrengthDbm in the Inspector to receive readings.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Esp32BleSignalTester : MonoBehaviour
    {
        private const string BleServiceUuid = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E";
        private const int MaximumProtocolLineBytes = 512;

        [Serializable]
        public sealed class SignalStrengthEvent : UnityEvent<int>
        {
        }

        [Serializable]
        public sealed class TextEvent : UnityEvent<string>
        {
        }

        /// <summary>
        /// One network to measure from a SCAN: a radio (BSSID plus channel identifies it; SSID is display text), or the
        /// radios of one SSID on one channel (<see cref="RadioCount"/> above 1), which protocol 1.2 measures together,
        /// each reading the strongest of them. A group's Bssid, RssiDbm and Security are its strongest radio's.
        /// </summary>
        public sealed class AccessPointInfo
        {
            public AccessPointInfo(string bssid, int channel, string band, int rssiDbm, string ssid, string security,
                IReadOnlyList<string> bssids = null)
            {
                Bssid = bssid;
                Channel = channel;
                Band = band;
                RssiDbm = rssiDbm;
                Ssid = ssid;
                Security = security;
                Bssids = bssids ?? new[] { bssid };
            }

            /// <summary>The scanned radios this is (strongest first; just Bssid for a single radio).</summary>
            public IReadOnlyList<string> Bssids { get; }
            public int RadioCount => Bssids.Count;
            public bool IsGroup => RadioCount > 1;

            public string Bssid { get; }
            public int Channel { get; }
            public string Band { get; }
            public int RssiDbm { get; }
            public string Ssid { get; }
            public string Security { get; }
            public string DisplayName => string.IsNullOrEmpty(Ssid) ? "<hidden network>" : Ssid;
        }

        public enum SessionState
        {
            Idle,
            Connecting,
            AwaitingHello,
            AwaitingCaps,
            Ready,
            AwaitingScan,
            AwaitingSelection,
            AwaitingStart,
            Streaming,
            AwaitingStop
        }

        [Header("BLE probe")]
        [SerializeField] private string deviceName = "WiFiProbe-C5";
        [SerializeField, Min(1f)] private float scanTimeoutSeconds = 15f;
        [Tooltip("How long a Wi-Fi SCAN may take to send its first result (the probe scans every channel first).")]
        [SerializeField, Min(1f)] private float scanFirstReplySeconds = 30f;
        [Tooltip("How long after the last result to stop waiting for SCAN_DONE and keep the results received.")]
        [SerializeField, Min(.5f)] private float scanGapSeconds = 5f;
        [SerializeField] private bool connectOnStart = true;
        [Tooltip("After connecting, measure Target BSSID/channel (or the strongest AP if blank) without a UI. " +
                 "Turn off when a menu drives scanning and selection.")]
        [SerializeField] private bool autoStartOnConnect = true;

        [Header("Wi-Fi target")]
        [Tooltip("Leave both fields empty/zero to scan and use the strongest visible BSSID.")]
        [SerializeField] private string targetBssid = string.Empty;
        [SerializeField, Range(0, 177)] private int targetChannel;

        [Header("Measurement")]
        [Tooltip("The ESP32 aggregation/reporting interval. Each SAMPLE raises the Unity event.")]
        [SerializeField, Range(0.02f, 60f)] private float pollIntervalSeconds = 0.5f;
        [Tooltip("PING period while connected. Each PONG refines the probe-to-headset clock mapping and checks the link.")]
        [SerializeField, Min(0.2f)] private float pingIntervalSeconds = 1f;
        [SerializeField] private bool logProtocolLines;

        [Header("Events")]
        [SerializeField] private SignalStrengthEvent onSignalStrengthDbm = new SignalStrengthEvent();
        [SerializeField] private TextEvent onStatus = new TextEvent();
        [SerializeField] private TextEvent onError = new TextEvent();

        public SignalStrengthEvent OnSignalStrengthDbm => onSignalStrengthDbm;
        public TextEvent OnStatus => onStatus;
        public TextEvent OnError => onError;
        public int LatestSignalStrengthDbm { get; private set; }
        public bool IsStreaming => sessionState == SessionState.Streaming;
        public SessionState State => sessionState;
        public bool IsConnected => sessionState >= SessionState.Ready;
        public bool IsBusy => sessionState == SessionState.AwaitingScan || sessionState == SessionState.AwaitingSelection ||
                              sessionState == SessionState.AwaitingStart || sessionState == SessionState.AwaitingStop;
        public string DeviceName => deviceName;
        /// <summary>Fields of the probe's HELLO reply (device, firmware, boot), once negotiated.</summary>
        public string DeviceInfo { get; private set; } = string.Empty;
        /// <summary>Fields of the probe's CAPS reply (bands, monitor support, limits), once negotiated.</summary>
        public string Capabilities { get; private set; } = string.Empty;
        /// <summary>The protocol minor agreed in HELLO (0 for 1.0 firmware); 1.1 features need at least 1.</summary>
        public int NegotiatedMinor { get; private set; }
        /// <summary>True when the probe negotiated 1.1 and reports a buzzer (BUZZ / BUZZ_OFF).</summary>
        public bool SupportsBuzzer { get; private set; }
        /// <summary>The probe's latest button state (ProbeProtocol.ButtonA | ButtonB); 0 without 1.1 buttons.</summary>
        public int Buttons { get; private set; }
        /// <summary>The network being measured, or selected for measurement; null when none.</summary>
        public AccessPointInfo Target { get; private set; }
        /// <summary>The last scan's networks, as the menu lists them (each SSID's radios on a channel together).</summary>
        public IReadOnlyList<AccessPointInfo> LastScan => scanNetworks;
        /// <summary>How many radios the probe measures for Target (its SELECTED aps=); 1 for a single radio or 1.1 firmware.</summary>
        public int MeasuredRadioCount { get; private set; } = 1;
        /// <summary>The radio the latest 1.2 SAMPLE reported (the strongest heard), or empty.</summary>
        public string LatestReportingBssid { get; private set; } = string.Empty;
        /// <summary>How many of the measured radios the latest 1.2 SAMPLE heard; 0 before one.</summary>
        public int LatestRadiosHeard { get; private set; }

        public float ReportIntervalSeconds
        {
            get => pollIntervalSeconds;
            set
            {
                pollIntervalSeconds = Mathf.Clamp(value, 0.02f, 60f);
                if (sessionState == SessionState.Streaming)
                {
                    // The interval is fixed per stream on the probe, so restart the stream.
                    afterStop = SendSelect;
                    SendStop();
                }
            }
        }

        /// <summary>Probe clock to headset clock; refined by every PONG, reset when the probe reboots.</summary>
        public ClockMapping Clock { get; } = new ClockMapping();
        /// <summary>The probe's stream session from the last STARTED reply; 0 before the first stream.</summary>
        public ulong StreamSessionId { get; private set; }
        /// <summary>SAMPLE lines dropped as malformed or not matching the current stream or target.</summary>
        public int RejectedSampleCount { get; private set; }
        public long ReceivedNotificationCount { get; private set; }
        public long LatestReceiveDispatchUs { get; private set; }
        public long PeakReceiveDispatchUs { get; private set; }
        public long LatestReceiveProcessingUs { get; private set; }
        public string ReceiveTimingSummary => ReceivedNotificationCount == 0 ? "BLE RX: waiting for a notification."
            : $"RX callback: C# {LatestReceiveDispatchUs / 1000f:F1} ms, " +
              $"frame {LatestReceiveProcessingUs / 1000f:F1} ms, peak C# {PeakReceiveDispatchUs / 1000f:F1} ms";

        /// <summary>Every accepted SAMPLE with its full statistics and, once the clock is mapped, headset time.</summary>
        public event Action<ProbeSample> SampleReceived;
        /// <summary>Every accepted PING/PONG exchange, for logging alongside recorded samples.</summary>
        public event Action<PingExchange> ClockExchanged;
        public event Action<SessionState> StateChanged;
        /// <summary>The probe's buttons changed (previous, current), from a BUTTONS event or a SAMPLE's buttons= field.</summary>
        public event Action<int, int> ButtonsChanged;
        public event Action<IReadOnlyList<AccessPointInfo>> ScanCompleted;
        public event Action<AccessPointInfo> TargetChanged;
        private int ScanTimeoutWholeSeconds => Mathf.CeilToInt(scanTimeoutSeconds);

        private enum InboundKind
        {
            State,
            Bytes,
            Error
        }

        private readonly struct InboundMessage
        {
            public InboundMessage(InboundKind kind, string text, byte[] bytes = null, long receivedUs = 0, long dispatchDelayUs = 0)
            {
                Kind = kind;
                Text = text;
                Bytes = bytes;
                ReceivedUs = receivedUs;
                DispatchDelayUs = dispatchDelayUs;
            }

            public InboundKind Kind { get; }
            public string Text { get; }
            public byte[] Bytes { get; }
            /// <summary>HostClock time at entry to Android's GATT notification callback.</summary>
            public long ReceivedUs { get; }
            public long DispatchDelayUs { get; }
        }

        private readonly ConcurrentQueue<InboundMessage> inboundMessages = new ConcurrentQueue<InboundMessage>();
        private readonly List<byte> lineBytes = new List<byte>(MaximumProtocolLineBytes);

        private SessionState sessionState = SessionState.Idle;
        private bool discardingLongLine;
        private bool disconnectRequested;
        private ulong nextCommandId = 1;
        private int requestedMinor = ProbeProtocol.HostMinor;
        // Ids of BUZZ / BUZZ_OFF commands awaiting OK or ERR: a refused buzz must not interrupt a measurement.
        private readonly HashSet<ulong> pendingBuzzIds = new HashSet<ulong>();
        private double nextPingAt;
        private int burstPingsRemaining;
        private const int BurstPings = 6;
        private const double BurstPingSeconds = 0.15;
        private string strongestBssid;
        private int strongestScanRssi = int.MinValue;
        private readonly List<AccessPointInfo> scanResults = new List<AccessPointInfo>();   // radios, as the probe lists them
        private readonly List<AccessPointInfo> scanNetworks = new List<AccessPointInfo>();
        private bool autoSelectAfterScan;
        private ulong scanCommandId;
        private double scanSentAt;
        private double? scanLastLineAt;
        private Action afterStop;
        private Coroutine connectRoutine;

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject bleBridge;
        private BleListenerProxy bleListener;
#endif

        private void Start()
        {
            if (connectOnStart)
            {
                Connect();
            }
        }

        private void Update()
        {
            while (inboundMessages.TryDequeue(out InboundMessage message))
            {
                switch (message.Kind)
                {
                    case InboundKind.State:
                        HandleTransportState(message.Text);
                        break;
                    case InboundKind.Bytes:
                        ReceivedNotificationCount++;
                        LatestReceiveDispatchUs = message.DispatchDelayUs;
                        PeakReceiveDispatchUs = Math.Max(PeakReceiveDispatchUs, message.DispatchDelayUs);
                        LatestReceiveProcessingUs = Math.Max(0, HostClock.NowMicroseconds - message.ReceivedUs);
                        AcceptBytes(message.Bytes, message.ReceivedUs);
                        break;
                    case InboundKind.Error:
                        Fail(message.Text);
                        break;
                }
            }

            if (sessionState == SessionState.AwaitingScan && Time.realtimeSinceStartupAsDouble >
                ProbeProtocol.ScanReplyDeadline(scanSentAt, scanLastLineAt, scanFirstReplySeconds, scanGapSeconds))
            {
                FinishScan(complete: false);
            }

            // The probe's SCAN blocks for seconds, so a PING during it would only measure the scan.
            if (IsConnected && sessionState != SessionState.AwaitingScan &&
                Time.realtimeSinceStartupAsDouble >= nextPingAt)
            {
                double gap = burstPingsRemaining > 0 ? BurstPingSeconds : pingIntervalSeconds;
                burstPingsRemaining = Math.Max(0, burstPingsRemaining - 1);
                nextPingAt = Time.realtimeSinceStartupAsDouble + gap;
                SendCommand($"PING {TakeCommandId()} {HostClock.NowMicroseconds}");
            }
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        /// <summary>Starts scanning for and connecting to the configured ESP32.</summary>
        public void Connect()
        {
            if (connectRoutine != null || sessionState != SessionState.Idle)
            {
                return;
            }

            ResetProtocolSession();
            disconnectRequested = false;
            SetState(SessionState.Connecting);
            AnnounceStatus($"Looking for BLE device {deviceName}...");
            connectRoutine = StartCoroutine(ConnectAfterPermissions());
        }

        /// <summary>Stops the test session and closes the BLE connection.</summary>
        public void Disconnect()
        {
            disconnectRequested = true;
            if (connectRoutine != null)
            {
                StopCoroutine(connectRoutine);
                connectRoutine = null;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            ReleaseBleBridge();
#endif

            SetState(SessionState.Idle);
            lineBytes.Clear();
            discardingLongLine = false;
        }

        /// <summary>Lists nearby access points; stops a running measurement first.</summary>
        public void Scan()
        {
            if (sessionState == SessionState.Streaming)
            {
                afterStop = () => BeginScan(autoSelect: false);
                SendStop();
                return;
            }
            if (sessionState != SessionState.Ready)
            {
                onError.Invoke($"Cannot scan while {sessionState}.");
                return;
            }
            BeginScan(autoSelect: false);
        }

        /// <summary>Selects a network from the scan (a radio by BSSID and channel, or a group of them) and starts
        /// streaming its RSSI.</summary>
        public void Measure(AccessPointInfo accessPoint)
        {
            if (accessPoint == null || !TryNormalizeBssid(accessPoint.Bssid, out _))
            {
                onError.Invoke("Pick an access point with a valid BSSID.");
                return;
            }
            SetTarget(accessPoint);
            if (sessionState == SessionState.Streaming)
            {
                afterStop = SendSelect;
                SendStop();
                return;
            }
            if (sessionState != SessionState.Ready)
            {
                onError.Invoke($"Cannot select a network while {sessionState}.");
                return;
            }
            SendSelect();
        }

        /// <summary>Stops streaming; the probe keeps its selection.</summary>
        public void StopMeasuring()
        {
            if (sessionState == SessionState.Streaming)
            {
                afterStop = null;
                SendStop();
            }
        }

        private void SetState(SessionState next)
        {
            if (sessionState == next)
            {
                return;
            }
            sessionState = next;
            StateChanged?.Invoke(next);
        }

        private void SetTarget(AccessPointInfo accessPoint)
        {
            Target = accessPoint;
            MeasuredRadioCount = 1;
            LatestReportingBssid = string.Empty;
            LatestRadiosHeard = 0;
            TargetChanged?.Invoke(accessPoint);
        }

        private void SendSelect()
        {
            if (Target == null)
            {
                SetState(SessionState.Ready);
                return;
            }
            bool group = Target.IsGroup && NegotiatedMinor >= ProbeProtocol.NetworkGroupMinor;
            SetState(SessionState.AwaitingSelection);
            if (Target.IsGroup && !group)
            {
                AnnounceStatus($"This probe's firmware measures one radio at a time: selecting the strongest of {Target.DisplayName}'s " +
                               $"{Target.RadioCount} on channel {Target.Channel}. Firmware 0.4.0 measures them all.");
            }
            else
            {
                AnnounceStatus(group
                    ? $"Selecting {Target.DisplayName}'s {Target.RadioCount} access points on channel {Target.Channel}..."
                    : $"Selecting {Target.Bssid} on channel {Target.Channel}...");
            }
            SendCommand(ProbeProtocol.SelectCommand(TakeCommandId(), Target.Bssid, Target.Channel, group));
        }

        private void SendStop()
        {
            SetState(SessionState.AwaitingStop);
            SendCommand($"STOP {TakeCommandId()}");
        }

        private IEnumerator ConnectAfterPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            yield return RequestBluetoothPermissions();
            if (!HaveBluetoothPermissions())
            {
                connectRoutine = null;
                Fail("Bluetooth permission was not granted.");
                yield break;
            }

            try
            {
                bleListener = new BleListenerProxy(inboundMessages);
                bleBridge = new AndroidJavaObject("com.wifiheatmap.ble.Esp32BleBridge");
                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    bleBridge.Call(
                        "connect",
                        activity,
                        deviceName,
                        BleServiceUuid,
                        ScanTimeoutWholeSeconds,
                        bleListener);
                }
            }
            catch (Exception exception)
            {
                Fail($"Could not start Android BLE: {exception.Message}");
            }
#else
            yield return null;
            Fail("ESP32 BLE testing requires an Android device build; it cannot connect in the Unity Editor.");
#endif
            connectRoutine = null;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private IEnumerator RequestBluetoothPermissions()
        {
            string[] permissions = RequiredBluetoothPermissions();
            List<string> missing = new List<string>();
            foreach (string permission in permissions)
            {
                if (!Permission.HasUserAuthorizedPermission(permission))
                {
                    missing.Add(permission);
                }
            }

            if (missing.Count == 0)
            {
                yield break;
            }

            HashSet<string> resolved = new HashSet<string>();
            PermissionCallbacks callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += permission => resolved.Add(permission);
            callbacks.PermissionDenied += permission => resolved.Add(permission);
            Permission.RequestUserPermissions(missing.ToArray(), callbacks);

            double deadline = Time.realtimeSinceStartupAsDouble + 30.0;
            while (resolved.Count < missing.Count && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }
        }

        private static string[] RequiredBluetoothPermissions()
        {
            using (AndroidJavaClass version = new AndroidJavaClass("android.os.Build$VERSION"))
            {
                int sdk = version.GetStatic<int>("SDK_INT");
                return sdk >= 31
                    ? new[] { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT" }
                    : new[] { Permission.FineLocation };
            }
        }

        private static bool HaveBluetoothPermissions()
        {
            foreach (string permission in RequiredBluetoothPermissions())
            {
                if (!Permission.HasUserAuthorizedPermission(permission))
                {
                    return false;
                }
            }
            return true;
        }

        private sealed class BleListenerProxy : AndroidJavaProxy
        {
            private readonly ConcurrentQueue<InboundMessage> messages;

            public BleListenerProxy(ConcurrentQueue<InboundMessage> messages)
                : base("com.wifiheatmap.ble.Esp32BleBridge$Listener")
            {
                this.messages = messages;
            }

            // Java forwards these through Android's main Handler. Never touch scene objects here;
            // bytes carry the original GATT callback timestamp, before that Handler's queue.
            public void onState(string state)
            {
                messages.Enqueue(new InboundMessage(InboundKind.State, state));
            }

            public void onBytes(string base64, long receivedUs)
            {
                try
                {
                    long dispatchDelayUs = HostClock.NowMicroseconds - receivedUs;
                    if (dispatchDelayUs < 0)
                    {
                        messages.Enqueue(new InboundMessage(InboundKind.Error, "BLE callback timestamp is ahead of HostClock; clock domains do not match."));
                        return;
                    }
                    messages.Enqueue(new InboundMessage(InboundKind.Bytes, null, Convert.FromBase64String(base64), receivedUs, dispatchDelayUs));
                }
                catch (FormatException exception)
                {
                    messages.Enqueue(new InboundMessage(InboundKind.Error, $"Invalid BLE payload: {exception.Message}"));
                }
            }

            public void onError(string error)
            {
                messages.Enqueue(new InboundMessage(InboundKind.Error, error));
            }
        }

        private void ReleaseBleBridge()
        {
            if (bleBridge == null)
            {
                return;
            }

            try
            {
                bleBridge.Call("disconnect");
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"BLE disconnect failed: {exception.Message}", this);
            }
            finally
            {
                bleBridge.Dispose();
                bleBridge = null;
                bleListener = null;
            }
        }
#endif

        private void HandleTransportState(string transportState)
        {
            if (logProtocolLines)
            {
                UnityEngine.Debug.Log($"BLE: {transportState}", this);
            }

            switch (transportState)
            {
                case "READY":
                    BeginProtocolSession();
                    break;
                case "DISCONNECTED":
                    SetState(SessionState.Idle);
                    if (!disconnectRequested)
                    {
                        Fail("ESP32 BLE connection closed.");
                    }
                    else
                    {
                        AnnounceStatus("ESP32 disconnected.");
                    }
                    break;
                default:
                    AnnounceStatus(transportState);
                    break;
            }
        }

        private void BeginProtocolSession()
        {
            nextCommandId = 1;
            requestedMinor = ProbeProtocol.HostMinor;
            SetState(SessionState.AwaitingHello);
            AnnounceStatus("BLE ready; negotiating WiFiProbe v1...");
            SendCommand(ProbeProtocol.HelloCommand(TakeCommandId(), requestedMinor));
        }

        private void AcceptBytes(byte[] bytes, long receivedUs)
        {
            if (bytes == null)
            {
                return;
            }

            foreach (byte value in bytes)
            {
                if (value == (byte)'\n')
                {
                    if (discardingLongLine)
                    {
                        Fail($"ESP32 sent a line longer than {MaximumProtocolLineBytes} bytes.");
                    }
                    else
                    {
                        int length = lineBytes.Count;
                        if (length > 0 && lineBytes[length - 1] == (byte)'\r')
                        {
                            length--;
                        }
                        string line = Encoding.UTF8.GetString(lineBytes.ToArray(), 0, length);
                        HandleProtocolLine(line, receivedUs);
                    }
                    lineBytes.Clear();
                    discardingLongLine = false;
                }
                else if (!discardingLongLine)
                {
                    if (lineBytes.Count < MaximumProtocolLineBytes)
                    {
                        lineBytes.Add(value);
                    }
                    else
                    {
                        discardingLongLine = true;
                    }
                }
            }
        }

        private void HandleProtocolLine(string line, long receivedUs)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (logProtocolLines)
            {
                UnityEngine.Debug.Log($"ESP32 < {line}", this);
            }

            if (line.StartsWith("ERR ", StringComparison.Ordinal))
            {
                if (sessionState == SessionState.AwaitingHello && requestedMinor > 0)
                {
                    // Fall back to the earliest protocol (1.0 accepts any 1.x, so this is only for firmware that
                    // rejects a minor it doesn't know).
                    UnityEngine.Debug.LogWarning($"Probe refused protocol 1.{requestedMinor}; retrying 1.0. {line}", this);
                    requestedMinor = 0;
                    SendCommand(ProbeProtocol.HelloCommand(TakeCommandId(), 0));
                    return;
                }
                if (sessionState == SessionState.AwaitingHello || sessionState == SessionState.AwaitingCaps)
                {
                    Fail(line);
                    return;
                }
                if (TryReadCommandId(line, out ulong refusedId) && pendingBuzzIds.Remove(refusedId))
                {
                    UnityEngine.Debug.LogWarning($"Probe buzzer command refused: {line}", this);
                    return;
                }
                // A rejected command (bad state, unsupported channel, ...) keeps the link up.
                afterStop = null;
                autoSelectAfterScan = false;
                SetState(SessionState.Ready);
                onError.Invoke(line);
                UnityEngine.Debug.LogWarning(line, this);
                return;
            }

            if (line.StartsWith("PONG ", StringComparison.Ordinal))
            {
                HandlePong(line, receivedUs);
                return;
            }

            if (line.StartsWith("OK ", StringComparison.Ordinal) && TryReadCommandId(line, out ulong okId) && pendingBuzzIds.Remove(okId))
            {
                return;
            }

            if (line.StartsWith("BUTTONS ", StringComparison.Ordinal))
            {
                if (NegotiatedMinor >= ProbeProtocol.BuzzerAndButtonsMinor && ProbeProtocol.TryParseButtons(line, out int pressed))
                {
                    SetButtons(pressed);
                }
                return;
            }

            if (sessionState == SessionState.AwaitingHello && line.StartsWith("HELLO ", StringComparison.Ordinal))
            {
                DeviceInfo = FieldsAfterId(line);
                // The probe answers the lower of our requested minor and its own; a reply without one is 1.0.
                if (!ProbeProtocol.TryParseProtocol(line, out int major, out int minor) || major != ProbeProtocol.Major)
                {
                    Fail($"Probe speaks an unsupported protocol: {line}");
                    return;
                }
                NegotiatedMinor = ProbeProtocol.Negotiate(requestedMinor, minor);
                // A new boot means a new probe clock; any earlier mapping is meaningless.
                TryReadULongField(line, "boot", out ulong boot);
                if (boot != Clock.BootId)
                {
                    Clock.Reset(boot);
                }
                SetState(SessionState.AwaitingCaps);
                SendCommand($"CAPS {TakeCommandId()}");
                return;
            }

            if (sessionState == SessionState.AwaitingCaps && line.StartsWith("CAPS ", StringComparison.Ordinal))
            {
                Capabilities = FieldsAfterId(line);
                SupportsBuzzer = NegotiatedMinor >= ProbeProtocol.BuzzerAndButtonsMinor &&
                                 TryReadField(line, "buzzer", out string buzzer) && buzzer == "true";
                burstPingsRemaining = BurstPings;
                nextPingAt = Time.realtimeSinceStartupAsDouble;
                if (autoStartOnConnect)
                {
                    BeginTargetSelection();
                }
                else
                {
                    SetState(SessionState.Ready);
                    AnnounceStatus("Connected. Scan for networks.");
                }
                return;
            }

            if (sessionState == SessionState.AwaitingStop && line.StartsWith("STOPPED ", StringComparison.Ordinal))
            {
                SetState(SessionState.Ready);
                AnnounceStatus("Measurement stopped.");
                Action next = afterStop;
                afterStop = null;
                next?.Invoke();
                return;
            }

            // Lines from an abandoned scan (one that timed out) carry an older id and are ignored.
            if (sessionState == SessionState.AwaitingScan && TryReadCommandId(line, out ulong scanId) && scanId == scanCommandId)
            {
                if (line.StartsWith("AP ", StringComparison.Ordinal))
                {
                    scanLastLineAt = Time.realtimeSinceStartupAsDouble;
                    ConsiderScanResult(line);
                    return;
                }
                if (line.StartsWith("SCAN_DONE ", StringComparison.Ordinal))
                {
                    FinishScan(complete: true);
                    return;
                }
            }

            if (sessionState == SessionState.AwaitingSelection && line.StartsWith("SELECTED ", StringComparison.Ordinal))
            {
                MeasuredRadioCount = ProbeProtocol.MeasuredRadioCount(line);
                SetState(SessionState.AwaitingStart);
                int intervalMilliseconds = Mathf.Clamp(Mathf.RoundToInt(pollIntervalSeconds * 1000f), 20, 60000);
                SendCommand($"START {TakeCommandId()} interval_ms={intervalMilliseconds}");
                return;
            }

            if (sessionState == SessionState.AwaitingStart && line.StartsWith("STARTED ", StringComparison.Ordinal))
            {
                TryReadULongField(line, "session", out ulong session);
                StreamSessionId = session;
                SetState(SessionState.Streaming);
                AnnounceStatus(Target == null ? "Streaming Wi-Fi signal strength."
                    : MeasuredRadioCount > 1 ? $"Measuring {Target.DisplayName} (ch {Target.Channel}, strongest of {MeasuredRadioCount} access points)."
                    : $"Measuring {Target.DisplayName} (ch {Target.Channel}).");
                return;
            }

            if (line.StartsWith("SAMPLE ", StringComparison.Ordinal))
            {
                HandleSample(line, receivedUs);
                // 1.1 SAMPLEs also carry the button state, which catches a press whose BUTTONS event was lost.
                if (NegotiatedMinor >= ProbeProtocol.BuzzerAndButtonsMinor && ProbeProtocol.TryParseButtons(line, out int pressed))
                {
                    SetButtons(pressed);
                }
            }
        }

        private void SetButtons(int pressed)
        {
            if (pressed == Buttons)
            {
                return;
            }
            int previous = Buttons;
            Buttons = pressed;
            ButtonsChanged?.Invoke(previous, pressed);
        }

        private static bool TryReadCommandId(string line, out ulong id)
        {
            id = 0;
            string[] parts = line.Split(' ');
            return parts.Length > 1 && ulong.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out id);
        }

        /// <summary>Sounds the probe's buzzer for durationMs (1-10000), optionally at frequencyHz (100-10000).
        /// Does nothing (false) unless the probe negotiated 1.1 and reports a buzzer.</summary>
        public bool Buzz(int durationMs, int frequencyHz = 0)
        {
            if (!SupportsBuzzer || !IsConnected)
            {
                return false;
            }
            ulong id = TakeCommandId();
            pendingBuzzIds.Add(id);
            int duration = Mathf.Clamp(durationMs, 1, 10000);
            SendCommand(frequencyHz > 0
                ? $"BUZZ {id} {duration} freq={Mathf.Clamp(frequencyHz, 100, 10000)}"
                : $"BUZZ {id} {duration}");
            return true;
        }

        /// <summary>Silences the probe's buzzer (protocol 1.1 with a buzzer only).</summary>
        public bool BuzzOff()
        {
            if (!SupportsBuzzer || !IsConnected)
            {
                return false;
            }
            ulong id = TakeCommandId();
            pendingBuzzIds.Add(id);
            SendCommand($"BUZZ_OFF {id}");
            return true;
        }

        private void HandlePong(string line, long receivedUs)
        {
            if (!ProbeLineParser.TryParsePong(line, receivedUs, out PingExchange exchange))
            {
                UnityEngine.Debug.LogWarning($"Malformed PONG ignored: {line}", this);
                return;
            }
            if (Clock.Add(exchange))
            {
                ClockExchanged?.Invoke(exchange);
            }
        }

        private void HandleSample(string line, long receivedUs)
        {
            if (!ProbeLineParser.TryParseSample(line, receivedUs, out ProbeSample sample))
            {
                RejectSample($"Malformed SAMPLE: {line}");
                return;
            }
            // Only samples from the stream we started, for the radio we selected, count. Samples that
            // were already in flight when we asked the probe to stop are still valid measurements.
            bool streamOpen = sessionState == SessionState.Streaming || sessionState == SessionState.AwaitingStop;
            if (!streamOpen || sample.SessionId != StreamSessionId)
            {
                RejectSample($"SAMPLE from session {sample.SessionId} while expecting {StreamSessionId} ({sessionState}).");
                return;
            }
            if (Target != null && (sample.Bssid != Target.Bssid || sample.Channel != Target.Channel))
            {
                RejectSample($"SAMPLE for {sample.Bssid} ch {sample.Channel} does not match the selected network.");
                return;
            }

            if (Clock.TryProbeToHost(sample.ProbeTimeUs, out long hostUs, out long uncertaintyUs))
            {
                sample = sample.WithHostTime(hostUs, uncertaintyUs);
            }

            // 1.2: which of a group's radios this reading is (the strongest heard), and how many were heard.
            if (TryReadField(line, "from", out string from) && TryNormalizeBssid(from, out string reporting))
            {
                LatestReportingBssid = reporting;
                LatestRadiosHeard = TryReadIntField(line, "heard", out int heard) ? heard : 1;
            }

            LatestSignalStrengthDbm = sample.RssiDbm;
            onSignalStrengthDbm.Invoke(sample.RssiDbm);
            SampleReceived?.Invoke(sample);
        }

        private void RejectSample(string reason)
        {
            RejectedSampleCount++;
            // Rate-limit: a mismatch tends to repeat for every sample until the stream changes.
            if (RejectedSampleCount <= 5 || RejectedSampleCount % 100 == 0)
            {
                UnityEngine.Debug.LogWarning($"{reason} (rejected so far: {RejectedSampleCount})", this);
            }
        }

        private void BeginTargetSelection()
        {
            bool hasBssid = !string.IsNullOrWhiteSpace(targetBssid);
            bool hasChannel = targetChannel > 0;
            if (hasBssid || hasChannel)
            {
                if (!hasBssid || !hasChannel || !TryNormalizeBssid(targetBssid, out string normalizedBssid))
                {
                    Fail("Set both a valid BSSID and channel, or leave both blank/zero for strongest-AP mode.");
                    return;
                }
                SetTarget(new AccessPointInfo(normalizedBssid, targetChannel, targetChannel > 14 ? "5" : "2.4", 0, string.Empty, string.Empty));
                SendSelect();
                return;
            }

            BeginScan(autoSelect: true);
        }

        private void BeginScan(bool autoSelect)
        {
            strongestBssid = null;
            strongestScanRssi = int.MinValue;
            scanResults.Clear();
            scanNetworks.Clear();
            autoSelectAfterScan = autoSelect;
            SetState(SessionState.AwaitingScan);
            AnnounceStatus(autoSelect ? "Scanning for the strongest visible access point..." : "Scanning for networks...");
            scanCommandId = TakeCommandId();
            scanSentAt = Time.realtimeSinceStartupAsDouble;
            scanLastLineAt = null;
            SendCommand($"SCAN {scanCommandId} band=all");
        }

        /// <summary>Ends a scan on SCAN_DONE, or (incomplete) when its reply stalls; either way the results so far
        /// are kept and the probe is Ready for the next command.</summary>
        private void FinishScan(bool complete)
        {
            // The firmware drops its selection when it scans.
            SetState(SessionState.Ready);
            scanResults.Sort((a, b) => b.RssiDbm.CompareTo(a.RssiDbm));
            scanNetworks.Clear();
            foreach (List<AccessPointInfo> radios in AccessPointGroups.Group(scanResults, r => r.Ssid, r => r.Channel, r => r.RssiDbm))
            {
                AccessPointInfo strongest = radios[0];
                scanNetworks.Add(radios.Count == 1 ? strongest : new AccessPointInfo(strongest.Bssid, strongest.Channel,
                    strongest.Band, strongest.RssiDbm, strongest.Ssid, strongest.Security, radios.ConvertAll(r => r.Bssid)));
            }
            ScanCompleted?.Invoke(scanNetworks);
            string found = $"{scanResults.Count} access point{(scanResults.Count == 1 ? "" : "s")}";
            if (scanNetworks.Count < scanResults.Count)
            {
                found += $" ({scanNetworks.Count} network{(scanNetworks.Count == 1 ? "" : "s")} by channel)";
            }
            if (complete)
            {
                AnnounceStatus($"Found {found}.");
            }
            else
            {
                string message = scanResults.Count == 0
                    ? "No scan reply from the probe. Scan again."
                    : $"Scan reply incomplete: showing the {found} received. Scan again for the full list.";
                UnityEngine.Debug.LogWarning(message, this);
                AnnounceStatus(message);
            }
            if (!autoSelectAfterScan)
            {
                return;
            }
            autoSelectAfterScan = false;
            if (strongestBssid == null)
            {
                Fail("WiFiProbe scan returned no access points.");
                return;
            }
            // The strongest radio's network (its group, when it has one).
            SetTarget(scanNetworks[0]);
            SendSelect();
        }

        private void ConsiderScanResult(string line)
        {
            if (!TryReadField(line, "bssid", out string bssid) ||
                !TryNormalizeBssid(bssid, out string normalizedBssid) ||
                !TryReadIntField(line, "channel", out int channel) ||
                !TryReadIntField(line, "rssi", out int rssi))
            {
                return;
            }

            TryReadQuotedField(line, "ssid", out string ssid);
            TryReadQuotedField(line, "security", out string security);
            TryReadField(line, "band", out string band);
            scanResults.Add(new AccessPointInfo(normalizedBssid, channel, band ?? string.Empty, rssi, ssid ?? string.Empty, security ?? string.Empty));

            if (rssi > strongestScanRssi)
            {
                strongestScanRssi = rssi;
                strongestBssid = normalizedBssid;
            }
        }

        private void SendCommand(string command)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (bleBridge == null)
            {
                Fail("BLE bridge is not connected.");
                return;
            }

            if (logProtocolLines)
            {
                UnityEngine.Debug.Log($"ESP32 > {command}", this);
            }
            byte[] bytes = Encoding.ASCII.GetBytes(command + "\n");
            // Java byte[] is signed; using sbyte[] avoids a JNI warning and stack trace on every PING.
            var signedBytes = new sbyte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, signedBytes, 0, bytes.Length);
            bleBridge.Call("write", signedBytes);
#else
            Fail("Cannot send BLE commands outside an Android device build.");
#endif
        }

        private ulong TakeCommandId()
        {
            ulong result = nextCommandId;
            nextCommandId++;
            return result;
        }

        private void ResetProtocolSession()
        {
            while (inboundMessages.TryDequeue(out _))
            {
            }
            lineBytes.Clear();
            discardingLongLine = false;
            nextCommandId = 1;
            strongestBssid = null;
            strongestScanRssi = int.MinValue;
            scanResults.Clear();
            scanNetworks.Clear();
            autoSelectAfterScan = false;
            afterStop = null;
            DeviceInfo = string.Empty;
            Capabilities = string.Empty;
            NegotiatedMinor = 0;
            SupportsBuzzer = false;
            pendingBuzzIds.Clear();
            SetButtons(0);
            StreamSessionId = 0;
            ReceivedNotificationCount = LatestReceiveDispatchUs = PeakReceiveDispatchUs = LatestReceiveProcessingUs = 0;
            burstPingsRemaining = 0;
        }

        private void AnnounceStatus(string message)
        {
            onStatus.Invoke(message);
            if (logProtocolLines)
            {
                UnityEngine.Debug.Log(message, this);
            }
        }

        private void Fail(string message)
        {
            SetState(SessionState.Idle);
            onError.Invoke(message);
            UnityEngine.Debug.LogError(message, this);
#if UNITY_ANDROID && !UNITY_EDITOR
            disconnectRequested = true;
            ReleaseBleBridge();
#endif
        }
    }
}
