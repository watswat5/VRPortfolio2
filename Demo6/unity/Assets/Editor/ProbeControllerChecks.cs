using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WifiHeatmap.Meter;
using WifiHeatmap.Probe;

namespace WifiHeatmap.Editor
{
    /// <summary>Checks saved sensor wiring, and moves to the right controller, either wrist and back, without saving either scene.</summary>
    public static class ProbeControllerChecks
    {
        [MenuItem("WiFi Heatmap/Validate Probe Controller Mount")]
        public static void Validate()
        {
            foreach (string path in new[] { ProjectSetup.ScenePath, ProjectSetup.HeatmapScenePath })
            {
                var scene = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    var roots = scene.GetRootGameObjects();
                    var mount = roots.SelectMany(root => root.GetComponentsInChildren<ProbeMount>(true)).Single();
                    var meter = roots.SelectMany(root => root.GetComponentsInChildren<EmfMeter>(true)).Single();
                    roots.SelectMany(root => root.GetComponentsInChildren<EmfHaptics>(true)).Single();
                    Require(!mount.IsRightHanded && mount.Controller == OVRInput.Controller.LTouch,
                        path + ": the scene default must be the left controller.");
                    Require(mount.transform.parent != null && mount.transform.parent.name == "LeftControllerAnchor",
                        path + ": probe must be parented to LeftControllerAnchor.");
                    Require(new SerializedObject(meter).FindProperty("follow").objectReferenceValue == mount.transform.parent,
                        path + ": meter must follow the same controller as the probe.");
                    Require(mount.MountId == "probe_mount_left (ring clip pod)" && !mount.IsCalibrated,
                        path + ": identify the left mount and keep its unmeasured calibration marked as an estimate.");
                    Require(Vector3.Distance(mount.transform.localPosition, ProbeMount.EstimatedPodPosition) < 0.000001f,
                        path + ": pod transform does not match the left-hand estimate.");
                    Require(Vector3.Distance(mount.Antenna.localPosition, ProbeMount.EstimatedAntennaOffset) < 0.000001f,
                        path + ": antenna transform does not match the left-hand estimate.");

                    // The meter's aligned placement is saved beside the probe's calibration for the same carrier.
                    Require(meter.Mount != mount || (meter.OffsetPath.EndsWith("emf-meter-left.json") && !meter.HasUnsavedOffset),
                        path + ": the meter's placement must be saved per carrier, beside the probe calibration.");

                    // The right side mirrors the estimate across controller X; switching back restores it.
                    Require(mount.SetRightHanded(true, false), path + ": could not move the probe to the right controller.");
                    Vector3 pod = ProbeMount.EstimatedPodPosition, antenna = ProbeMount.EstimatedAntennaOffset;
                    Require(mount.transform.parent.name == "RightControllerAnchor" && mount.Controller == OVRInput.Controller.RTouch &&
                            mount.MountId == "probe_mount_right (ring clip pod)",
                        path + ": right-hand probe must use RightControllerAnchor, RTouch and the right mount.");
                    Require(Vector3.Distance(mount.transform.localPosition, new Vector3(-pod.x, pod.y, pod.z)) < 0.000001f &&
                            Vector3.Distance(mount.Antenna.localPosition, new Vector3(-antenna.x, antenna.y, antenna.z)) < 0.000001f,
                        path + ": right-hand estimate must mirror the left one across controller X.");
                    Require(mount.SetRightHanded(false, false) && mount.transform.parent.name == "LeftControllerAnchor" &&
                            Vector3.Distance(mount.transform.localPosition, pod) < 0.000001f,
                        path + ": switching back must restore the left-hand estimate.");

                    // The wrist probe (hand tracking) rides the hand anchor with its own mount and size; leaving it
                    // restores the controller estimate. No hand is tracked here, so its estimate stays pending.
                    Require(mount.SetOnHand(true, false) && mount.IsOnHand && mount.transform.parent.name == "LeftHandAnchor" &&
                            mount.Controller == OVRInput.Controller.LHand && mount.MountId == "m5stickc_plus2_wrist_left" &&
                            !mount.IsCalibrated && mount.ProbeSize == ProbeMount.WristProbeSize && !mount.ProbeTracked(),
                        path + ": the left wrist probe must use LeftHandAnchor, LHand, its own mount and size, untracked until a hand is seen.");
                    Require(mount.SetRightHanded(true, false) && mount.transform.parent.name == "RightHandAnchor" &&
                            mount.Controller == OVRInput.Controller.RHand && mount.MountId == "m5stickc_plus2_wrist_right",
                        path + ": the right wrist probe must use RightHandAnchor, RHand and its own mount.");
                    Require(mount.SetCarrier(false, false, false) && !mount.IsOnHand && mount.transform.parent.name == "LeftControllerAnchor" &&
                            mount.ProbeSize == ProbeMount.PodSize && Vector3.Distance(mount.transform.localPosition, pod) < 0.000001f,
                        path + ": returning to the left controller must restore its estimate and pod size.");
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                }
            }
            Debug.Log("Probe controller checks passed for ProbeTester and HeatmapBuilder.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
