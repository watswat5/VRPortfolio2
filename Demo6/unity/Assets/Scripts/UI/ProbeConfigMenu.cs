using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WifiHeatmap.Meter;
using WifiHeatmap.Core;
using WifiHeatmap.Probe;

namespace WifiHeatmap.UI
{
    /// <summary>
    /// World-space settings menu: pair/connect the probe, scan and pick the network to measure,
    /// record and load sessions, manage anchors, and adjust the meter. The probe controller's lower face button (X on the left,
    /// A on the right, so either side has the same layout) or the hand menu gesture shows and hides it; it opens in front of
    /// the viewer. Point with a controller or hand ray, or poke.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProbeConfigMenu : MonoBehaviour
    {
        [Serializable]
        public sealed class NetworkRow
        {
            public Button button;
            public TMP_Text name;
            public TMP_Text detail;
            public TMP_Text rssi;
        }

        [Header("Systems")]
        [SerializeField] private Esp32BleSignalTester probe;
        [SerializeField] private SimulatedRssiSource simulated;
        [SerializeField] private EmfMeter meter;
        [SerializeField] private EmfHaptics haptics;
        [SerializeField] private ProbeMount mount;
        [SerializeField] private Transform head;

        [Header("Placement")]
        [SerializeField] private float distance = 0.5f;
        [SerializeField] private float dropBelowEyes = 0.15f;
        [SerializeField] private bool showOnStart = true;

        [Header("Tabs (Probe, Network, Sessions, Anchors, Meter, Mount, Cloud)")]
        [SerializeField] private Button[] tabButtons = new Button[0];
        [SerializeField] private GameObject[] tabPanels = new GameObject[0];
        [SerializeField] private Button closeButton;

        [Header("Probe tab")]
        [SerializeField] private TMP_Text connectionText;
        [SerializeField] private TMP_Text deviceText;
        [SerializeField] private TMP_Text receiveTimingText;
        [SerializeField] private Button connectButton;
        [SerializeField] private Button disconnectButton;

        [Header("Network tab")]
        [SerializeField] private TMP_Text targetText;
        [SerializeField] private TMP_Text listHint;
        [SerializeField] private TMP_Text pageText;
        [SerializeField] private Button scanButton;
        [SerializeField] private Button stopButton;
        [SerializeField] private Button previousPageButton;
        [SerializeField] private Button nextPageButton;
        [SerializeField] private NetworkRow[] rows = new NetworkRow[0];
        [SerializeField] private Button[] intervalButtons = new Button[0];
        [SerializeField] private float[] intervalSeconds = { 0.1f, 0.25f, 0.5f, 1f };

        [Header("Meter tab")]
        [SerializeField] private Button hapticsButton;
        [SerializeField] private Button simulatedButton;
        [SerializeField] private Button outlineButton;
        [SerializeField] private TMP_Text meterInfo;

        private readonly List<Esp32BleSignalTester.AccessPointInfo> networks = new List<Esp32BleSignalTester.AccessPointInfo>();
        private int page;
        private int tab;
        private bool visible;

        private void Awake()
        {
            for (int i = 0; i < tabButtons.Length; i++)
            {
                int index = i;
                tabButtons[i].onClick.AddListener(() => ShowTab(index));
            }
            for (int i = 0; i < rows.Length; i++)
            {
                int index = i;
                rows[i].button.onClick.AddListener(() => PickRow(index));
            }
            for (int i = 0; i < intervalButtons.Length && i < intervalSeconds.Length; i++)
            {
                float seconds = intervalSeconds[i];
                intervalButtons[i].onClick.AddListener(() => SetInterval(seconds));
            }

            closeButton.onClick.AddListener(Hide);
            connectButton.onClick.AddListener(Connect);
            disconnectButton.onClick.AddListener(() => probe.Disconnect());
            scanButton.onClick.AddListener(() => probe.Scan());
            stopButton.onClick.AddListener(() => probe.StopMeasuring());
            previousPageButton.onClick.AddListener(() => TurnPage(-1));
            nextPageButton.onClick.AddListener(() => TurnPage(1));
            hapticsButton.onClick.AddListener(() => haptics.HapticsEnabled = !haptics.HapticsEnabled);
            simulatedButton.onClick.AddListener(ToggleSimulated);
            outlineButton.onClick.AddListener(() => mount.ShowPlaceholder = !mount.ShowPlaceholder);
        }

        private void OnEnable()
        {
            probe.ScanCompleted += OnScanCompleted;
            probe.ButtonsChanged += OnProbeButtons;
        }

        private void OnDisable()
        {
            probe.ScanCompleted -= OnScanCompleted;
            probe.ButtonsChanged -= OnProbeButtons;
        }

        /// <summary>The probe's own buttons (protocol 1.1, e.g. the wrist probe): a press of A captures and B cancels
        /// a frozen alignment, so the hand wearing the probe can hold still.</summary>
        private void OnProbeButtons(int previous, int current)
        {
            int pressed = current & ~previous;
            if (mount == null) return;
            if ((pressed & ProbeProtocol.ButtonB) != 0) mount.ProbeButtonPressed('B');
            else if ((pressed & ProbeProtocol.ButtonA) != 0) mount.ProbeButtonPressed('A');
        }

        private void Start()
        {
            ShowTab(0);
            if (showOnStart)
            {
                // Wait a moment for the head pose, then open in front of the viewer.
                Invoke(nameof(Show), 0.5f);
            }
            else
            {
                SetVisible(false);
            }
        }

        private bool wasAligning;

        private void Update()
        {
            // The menu hides while the probe is fitted into its frozen target; it comes back with the outcome.
            if (wasAligning && !mount.IsFrozenAlignment && !visible) Show();
            wasAligning = mount.IsFrozenAlignment;
            if (OVRInput.GetDown(OVRInput.Button.One, mount.Controller) || OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.Hands))
            {
                if (visible)
                {
                    Hide();
                }
                else
                {
                    Show();
                }
            }
            if (visible)
            {
                Refresh();
            }
        }

        public void Show()
        {
            if (head != null)
            {
                Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
                transform.position = head.position + forward * distance + Vector3.down * dropBelowEyes;
                // A world-space canvas reads from its -Z side, so +Z points away from the viewer.
                transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            }
            SetVisible(true);
        }

        public void Hide()
        {
            SetVisible(false);
        }

        private void SetVisible(bool value)
        {
            visible = value;
            foreach (Transform child in transform)
            {
                child.gameObject.SetActive(value);
            }
        }

        private void ShowTab(int index)
        {
            tab = Mathf.Clamp(index, 0, tabPanels.Length - 1);
            for (int i = 0; i < tabPanels.Length; i++)
            {
                tabPanels[i].SetActive(i == tab);
                MenuStyle.Tint(tabButtons[i], i == tab);
            }
        }

        private void Connect()
        {
            if (simulated != null && simulated.enabled)
            {
                ToggleSimulated();
            }
            probe.Connect();
        }

        private void ToggleSimulated()
        {
            bool on = !simulated.enabled;
            if (on)
            {
                // Never mix simulated and real readings.
                probe.StopMeasuring();
            }
            simulated.enabled = on;
            meter.SetStatus(on ? "SIMULATED signal (not real)" : "Simulated signal off");
        }

        private void SetInterval(float seconds)
        {
            probe.ReportIntervalSeconds = seconds;
        }

        private void OnScanCompleted(IReadOnlyList<Esp32BleSignalTester.AccessPointInfo> results)
        {
            networks.Clear();
            networks.AddRange(results);
            page = 0;
            ShowTab(1);
        }

        private void TurnPage(int delta)
        {
            int pages = Mathf.Max(1, Mathf.CeilToInt(networks.Count / (float)Mathf.Max(1, rows.Length)));
            page = Mathf.Clamp(page + delta, 0, pages - 1);
        }

        private void PickRow(int rowIndex)
        {
            int index = page * rows.Length + rowIndex;
            if (index < networks.Count)
            {
                if (simulated != null && simulated.enabled)
                {
                    ToggleSimulated();
                }
                probe.Measure(networks[index]);
            }
        }

        private void Refresh()
        {
            Esp32BleSignalTester.SessionState state = probe.State;

            // Probe tab
            connectionText.text = state switch
            {
                Esp32BleSignalTester.SessionState.Idle => "Not connected",
                Esp32BleSignalTester.SessionState.Connecting => "Searching / pairing...",
                Esp32BleSignalTester.SessionState.AwaitingHello => "Negotiating...",
                Esp32BleSignalTester.SessionState.AwaitingCaps => "Negotiating...",
                Esp32BleSignalTester.SessionState.Streaming => "Connected, measuring",
                _ => "Connected",
            };
            deviceText.text = probe.IsConnected
                ? $"{probe.DeviceName}\n{probe.DeviceInfo}\n{probe.Capabilities}"
                : $"Probe: {probe.DeviceName}\nOn first connect, Android asks for the probe's pairing PIN " +
                  "(set in the firmware, see arduino/WiFiProbe/README.md).";
            if (receiveTimingText != null) receiveTimingText.text = probe.ReceiveTimingSummary;
            connectButton.interactable = state == Esp32BleSignalTester.SessionState.Idle;
            disconnectButton.interactable = state != Esp32BleSignalTester.SessionState.Idle;

            // Network tab
            Esp32BleSignalTester.AccessPointInfo target = probe.Target;
            string radios = probe.MeasuredRadioCount <= 1 ? target?.Bssid
                : probe.LatestRadiosHeard > 0 ? $"strongest of {probe.MeasuredRadioCount} APs: {probe.LatestReportingBssid} ({probe.LatestRadiosHeard} heard)"
                : $"strongest of {probe.MeasuredRadioCount} APs";
            targetText.text = target == null
                ? "No network selected"
                : $"{(probe.IsStreaming ? "Measuring" : "Selected")}: {target.DisplayName}  ch {target.Channel} ({target.Band} GHz)  {radios}";
            bool ready = state == Esp32BleSignalTester.SessionState.Ready || state == Esp32BleSignalTester.SessionState.Streaming;
            scanButton.interactable = ready;
            stopButton.interactable = probe.IsStreaming;
            listHint.text = !probe.IsConnected ? "Connect the probe first."
                : state == Esp32BleSignalTester.SessionState.AwaitingScan ? "Scanning..."
                : networks.Count == 0 ? "Scan to list nearby access points." : string.Empty;

            int pages = Mathf.Max(1, Mathf.CeilToInt(networks.Count / (float)Mathf.Max(1, rows.Length)));
            page = Mathf.Clamp(page, 0, pages - 1);
            pageText.text = networks.Count == 0 ? string.Empty : $"{page + 1}/{pages}";
            previousPageButton.interactable = page > 0;
            nextPageButton.interactable = page < pages - 1;
            for (int i = 0; i < rows.Length; i++)
            {
                int index = page * rows.Length + i;
                bool has = index < networks.Count;
                rows[i].button.gameObject.SetActive(has);
                if (!has)
                {
                    continue;
                }
                Esp32BleSignalTester.AccessPointInfo ap = networks[index];
                rows[i].name.text = ap.DisplayName;
                // A network with several access points on the channel is one row, measured as its strongest.
                rows[i].detail.text = $"ch {ap.Channel} · {ap.Band} GHz · {(ap.IsGroup ? $"{ap.RadioCount} APs" : ap.Bssid)} · {ap.Security}";
                rows[i].rssi.text = $"{ap.RssiDbm} dBm";
                rows[i].button.interactable = ready;
                MenuStyle.Tint(rows[i].button, target != null && target.Bssid == ap.Bssid && target.Channel == ap.Channel);
            }
            for (int i = 0; i < intervalButtons.Length && i < intervalSeconds.Length; i++)
            {
                MenuStyle.Tint(intervalButtons[i], Mathf.Approximately(probe.ReportIntervalSeconds, intervalSeconds[i]));
            }

            // Meter tab
            MenuStyle.SetLabel(hapticsButton, $"Movement haptics: {(haptics.HapticsEnabled ? "On" : "Off")}");
            MenuStyle.Tint(hapticsButton, haptics.HapticsEnabled);
            MenuStyle.SetLabel(simulatedButton, $"Simulated signal: {(simulated.enabled ? "On" : "Off")}");
            MenuStyle.Tint(simulatedButton, simulated.enabled);
            MenuStyle.SetLabel(outlineButton, $"Probe target: {(mount.ShowPlaceholder ? "Always" : "Calibrating")}");
            MenuStyle.Tint(outlineButton, mount.ShowPlaceholder);
            meterInfo.text = $"Scale {meter.WeakestDbm} to {meter.StrongestDbm} dBm (Cloud tab).  Latest: {(meter.HasSignal ? meter.LatestDbm + " dBm" : "--")}\n" +
                             $"Mount: {mount.MountId}\nCalibration: {mount.CalibrationId}\n{haptics.Status}";
        }
    }
}
