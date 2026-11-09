using UnityEngine;
using WifiHeatmap.Core;

namespace WifiHeatmap.Probe
{
    /// <summary>Install one clock for Android BLE callbacks, PING timestamps and Unity pose history.
    /// Runs before any scene captures poses. The cached Java reference lives for the player lifetime.</summary>
    public static class AndroidMonotonicClock
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaClass bridgeClass;
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass = new AndroidJavaClass("com.wifiheatmap.ble.Esp32BleBridge");
            // Read exactly the same clock as onCharacteristicChanged. Do not assume Stopwatch and
            // Android's uptime clock have the same origin or suspend behaviour on a particular runtime.
            HostClock.ConfigureTimeSource(() => bridgeClass.CallStatic<long>("monotonicTimeUs"));
#endif
        }
    }
}
