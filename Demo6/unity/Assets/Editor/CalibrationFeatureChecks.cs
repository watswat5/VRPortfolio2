using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using WifiHeatmap.Probe;
using WifiHeatmap.Recording;
using WifiHeatmap.UI;

namespace WifiHeatmap.Editor
{
    public static class CalibrationFeatureChecks
    {
        [MenuItem("WiFi Heatmap/Validate Calibration Controls")]
        public static void Validate()
        {
            foreach (string path in new[] { ProjectSetup.ScenePath, ProjectSetup.HeatmapScenePath })
            {
                var scene = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    var roots = scene.GetRootGameObjects();
                    var panel = roots.SelectMany(r => r.GetComponentsInChildren<ProbeCalibrationPanel>(true)).Single();
                    var mount = roots.SelectMany(r => r.GetComponentsInChildren<ProbeMount>(true)).Single();
                    var recorder = roots.SelectMany(r => r.GetComponentsInChildren<SessionRecorder>(true)).Single();
                    var menu = roots.SelectMany(r => r.GetComponentsInChildren<ProbeConfigMenu>(true)).Single();
                    var data = new SerializedObject(panel);
                    foreach (string field in new[] { "mount", "recorder", "sideButton", "carrierButton", "freezeButton", "saveButton", "loadButton", "resetButton", "info" })
                        Require(data.FindProperty(field).objectReferenceValue != null, path + ": missing " + field);
                    Require(data.FindProperty("mount").objectReferenceValue == mount, "Calibration must adjust the recorded mount.");
                    Require(data.FindProperty("recorder").objectReferenceValue == recorder, "Calibration must use the scene recorder's lock.");
                    var menuData = new SerializedObject(menu);
                    Require(menuData.FindProperty("tabButtons").arraySize == 8 && menuData.FindProperty("tabPanels").arraySize == 8,
                        "Calibration tab is missing.");
                    foreach (var go in roots)
                        foreach (var component in go.GetComponentsInChildren<MonoBehaviour>(true))
                            Require(component != null, "Missing script in " + path);

                    // Verify a reset stays unsaved, and that it and recording locks block changing controllers.
                    mount.ResetCalibration();
                    Require(mount.HasUnsavedChanges && !mount.IsCalibrated, "Edits must remain unsaved until explicitly saved.");
                    Require(!mount.SetRightHanded(true, false) && !mount.IsRightHanded,
                        "Switching controllers must not discard an unsaved offset.");
                    mount.CalibrationLocked = true;
                    var pose = mount.transform.localPosition; var rotation = mount.transform.localRotation;
                    mount.ResetCalibration();
                    Require(!mount.SetRightHanded(true, false) && pose == mount.transform.localPosition &&
                            rotation == mount.transform.localRotation, "Recording lock failed.");
                    Require(!mount.SaveCalibration() && !mount.LoadCalibration(), "File actions must be blocked while recording.");
                    mount.CalibrationLocked = false;
                }
                finally { EditorSceneManager.ClosePreviewScene(scene); }
            }
            Debug.Log("Calibration control wiring, unsaved state, side switching, and recording lock checks passed.");
        }

        public static void UpdateAndValidate()
        {
            ProjectSetup.UpdateCalibrationMenus();
            ProbeControllerChecks.Validate();
            Validate();
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
