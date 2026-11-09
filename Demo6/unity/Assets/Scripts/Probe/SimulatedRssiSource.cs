using UnityEngine;

namespace WifiHeatmap.Probe
{
    /// <summary>
    /// Stand-in for the ESP32 probe when testing the meter and haptics without hardware: a virtual
    /// access point placed in front of the viewer at startup, read through a log-distance path-loss
    /// model with Gaussian noise. Raises the same event type as Esp32BleSignalTester.
    /// Never mix these readings with real measurements.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimulatedRssiSource : MonoBehaviour
    {
        [SerializeField] private Transform antenna;
        [Tooltip("The access point is placed this far in front of (and at the height of) this transform at startup.")]
        [SerializeField] private Transform placeRelativeTo;
        [SerializeField, Min(0.1f)] private float placeDistance = 2f;
        [Tooltip("RSSI at 1 m from the virtual access point.")]
        [SerializeField] private float dbmAtOneMetre = -38f;
        [SerializeField, Range(1.5f, 4f)] private float pathLossExponent = 2.2f;
        [SerializeField, Min(0f)] private float noiseDb = 2f;
        [SerializeField, Range(0.02f, 2f)] private float intervalSeconds = 0.1f;
        [SerializeField] private bool showAccessPoint = true;
        [SerializeField] private Esp32BleSignalTester.SignalStrengthEvent onSignalStrengthDbm = new Esp32BleSignalTester.SignalStrengthEvent();

        private Vector3 accessPoint;
        private GameObject marker;
        private float nextSampleAt;
        private bool placed;

        public Esp32BleSignalTester.SignalStrengthEvent OnSignalStrengthDbm => onSignalStrengthDbm;
        public Vector3 AccessPointPosition => accessPoint;

        /// <summary>Moves the virtual access point; called automatically on the first frame.</summary>
        public void PlaceAccessPoint(Vector3 worldPosition)
        {
            accessPoint = worldPosition;
            placed = true;
            if (showAccessPoint)
            {
                if (marker == null)
                {
                    marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    marker.name = "Simulated Access Point";
                    Destroy(marker.GetComponent<Collider>());
                    marker.transform.localScale = Vector3.one * 0.06f;
                }
                marker.transform.position = accessPoint;
            }
        }

        private void OnDisable()
        {
            if (marker != null)
            {
                Destroy(marker);
                marker = null;
            }
            placed = false;
        }

        private void Update()
        {
            if (antenna == null)
            {
                return;
            }
            if (!placed)
            {
                // Wait one frame so the tracked rig has a real pose before placing the access point.
                if (placeRelativeTo == null || Time.frameCount < 3)
                {
                    return;
                }
                Vector3 forward = Vector3.ProjectOnPlane(placeRelativeTo.forward, Vector3.up);
                forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
                PlaceAccessPoint(placeRelativeTo.position + forward * placeDistance);
            }

            if (Time.time < nextSampleAt)
            {
                return;
            }
            nextSampleAt = Time.time + intervalSeconds;

            float distance = Mathf.Max(0.05f, Vector3.Distance(antenna.position, accessPoint));
            float dbm = dbmAtOneMetre - 10f * pathLossExponent * Mathf.Log10(distance) + Gaussian() * noiseDb;
            onSignalStrengthDbm.Invoke(Mathf.RoundToInt(Mathf.Clamp(dbm, -100f, -10f)));
        }

        private static float Gaussian()
        {
            float u1 = 1f - Random.value;
            float u2 = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }
    }
}
