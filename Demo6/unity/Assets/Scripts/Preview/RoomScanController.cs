using System;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using WifiHeatmap.Probe;
using WifiHeatmap.Recording;

namespace WifiHeatmap.Preview
{
    /// <summary>Guided system capture is a session boundary, never a background geometry update.</summary>
    public sealed class RoomScanController : MonoBehaviour
    {
        [SerializeField] private SessionRecorder recorder;
        [SerializeField] private Esp32BleSignalTester probe;
        [SerializeField] private ProbeMount mount;
        public bool Busy { get; private set; }
        public string Status { get; private set; } = "Scan room opens Quest Space Setup and ends the current recording.";
        public void ScanRoom() { if (!Busy) _ = Run(true); }
        public void ReloadRoom() { if (!Busy) _ = Run(false); }
        private async Task Run(bool capture)
        {
            if (Application.platform != RuntimePlatform.Android || MRUK.Instance == null)
            { Status = "Room scan/reload requires the standalone Quest app and Scene permission."; return; }
            Busy = true;
            try
            {
                // Do not let a late START reply begin measurements while Space Setup has focus.
                // A user can retry after a pending network operation finishes.
                if (probe.IsConnected && probe.State != Esp32BleSignalTester.SessionState.Ready &&
                    probe.State != Esp32BleSignalTester.SessionState.Streaming)
                { Status = "Wait for the probe's current operation to finish, then try again."; return; }
                mount.CancelFrozenAlignment();
                recorder.StopRecording(capture ? "Room scan requested." : "Room reload requested.");
                probe.StopMeasuring();
                Status = "Finishing measurement before loading room data...";
                double deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (probe.IsConnected && probe.State != Esp32BleSignalTester.SessionState.Ready)
                {
                    if (this == null) return;
                    if (Time.realtimeSinceStartupAsDouble > deadline)
                    { Status = "Probe did not stop. Disconnect it before scanning the room."; return; }
                    await Task.Delay(50);
                }
                if (capture)
                {
                    Status = "Complete Quest Space Setup, then return to the app.";
                    bool completed = await OVRScene.RequestSpaceSetup();
                    if (this == null) return;
                    if (!completed) { Status = "Space Setup cancelled or unavailable. Existing recordings retained."; return; }
                }
                // A successful request is not proof that room data is available or localized.
                deadline = Time.realtimeSinceStartupAsDouble + 60;
                while (!Application.isFocused || OVRManager.hasInputFocus == false)
                {
                    if (this == null) return;
                    if (Time.realtimeSinceStartupAsDouble > deadline)
                    { Status = "Return to the app, then press Reload room."; return; }
                    await Task.Delay(100);
                }
                Status = "Loading room data...";
                var result = await MRUK.Instance.LoadSceneFromDevice(false);
                if (this == null) return;
                Status = $"Room load: {result}. Preview requires a localized room. Select a network again to resume measuring.";
            }
            catch (Exception exception)
            { if (this != null) { Status = $"Room setup failed: {exception.Message}"; Debug.LogWarning(Status,this); } }
            finally { if (this != null) Busy = false; }
        }
    }
}
