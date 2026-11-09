using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Meta.XR.EnvironmentDepth;
using Oculus.Interaction.Editor.QuickActions;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using WifiHeatmap.Meter;
using WifiHeatmap.Core;
using WifiHeatmap.Heatmap;
using WifiHeatmap.Probe;
using WifiHeatmap.Preview;
using WifiHeatmap.Recording;
using WifiHeatmap.UI;

namespace WifiHeatmap.Editor
{
    /// <summary>
    /// Reproducible Quest 3 / 3S mixed-reality setup: Android player settings, OpenXR with the Meta
    /// XR feature group, Meta project config (passthrough, scene, anchors, hands), URP for mobile
    /// MR, Depth API occlusion of the voxel cloud, the EMF meter, probe-mount and config-menu prefabs,
    /// the Interaction SDK rig, and the ProbeTester / HeatmapBuilder scenes.
    /// Finishes by running Meta's Project Setup Tool fixes. Safe to re-run; scenes are rebuilt.
    /// </summary>
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/Scenes/ProbeTester.unity";
        public const string HeatmapScenePath = "Assets/Scenes/HeatmapBuilder.unity";
        public const string PrefabPath = "Assets/Prefabs/WiFiProbe.prefab";
        public const string MeterPrefabPath = "Assets/Prefabs/EmfMeter.prefab";
        public const string MountPrefabPath = "Assets/Prefabs/ProbeMount.prefab";
        public const string MenuPrefabPath = "Assets/Prefabs/ProbeConfigMenu.prefab";
        private const string MaterialsFolder = "Assets/Materials";
        private const string TmpFontPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
        private const BuildTargetGroup Android = BuildTargetGroup.Android;

        // OpenXR features for Quest 3 MR through the Meta XR SDK.
        private static readonly string[] EnabledOpenXrFeatures =
        {
            "com.meta.openxr.feature.metaxr",                 // Meta XR feature group (OVRPlugin over OpenXR)
            "com.unity.openxr.feature.metaquest",             // Meta Quest Support (Android manifest, devices)
            "com.unity.openxr.feature.input.metaquestplus",   // Touch Plus controller profile
            "com.unity.openxr.feature.input.oculustouch",     // Touch controller profile (fallback)
        };

        // The Depth API reads environment depth through Unity's OpenXR Meta package (com.unity.xr.meta-openxr),
        // which needs its session and occlusion features. Matched by type so this file compiles without that package.
        private static readonly string[] DepthOpenXrFeatureTypes =
        {
            "UnityEngine.XR.OpenXR.Features.Meta.ARSessionFeature",
            "UnityEngine.XR.OpenXR.Features.Meta.AROcclusionFeature",
        };

        private static readonly string[] DisabledOpenXrFeatures =
        {
            "com.unity.openxr.feature.oculusquest",           // legacy Quest support; superseded by metaquest
        };

        [MenuItem("WiFi Heatmap/Configure Quest 3 MR Project")]
        public static void Configure()
        {
            ConfigurePlayer();
            ConfigureXr();
            ConfigureMetaProjectConfig();
            ConfigureUrp();
            ImportTmpEssentials();

            (Material led, Material body, Material face, Material metal) = CreateMeterMaterials();
            Material ghost = CreateTransparentMaterial("ProbePlaceholder", new Color(0.2f, 0.9f, 1f, 0.22f));
            Material points = CreateMaterial("SessionPoints", "Universal Render Pipeline/Particles/Unlit", Color.white);
            Material marker = CreateMaterial("AnchorMarker", "Universal Render Pipeline/Unlit", new Color(1f, 0.55f, 0.1f));
            var sceneMaterials = new SceneMaterials { Points = points, AnchorMarker = marker };

            GameObject meterPrefab = BuildMeterPrefab(led, body, face, metal);
            GameObject mountPrefab = BuildMountPrefab(ghost);
            GameObject probePrefab = EnsureProbePrefab();
            GameObject menuPrefab = BuildMenuPrefab();

            // Both scenes load the room model: recording anchors samples to MRUK rooms.
            BuildMrScene(ScenePath, meterPrefab, mountPrefab, probePrefab, menuPrefab, sceneMaterials);
            BuildMrScene(HeatmapScenePath, meterPrefab, mountPrefab, probePrefab, menuPrefab, sceneMaterials);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true),
                new EditorBuildSettingsScene(HeatmapScenePath, true),
            };
            AssetDatabase.SaveAssets();

            RunMetaProjectSetupFixes();
            AssetDatabase.SaveAssets();
            Debug.Log("WiFi Heatmap: Quest 3 MR project configured (OpenXR + Meta XR, passthrough, MRUK, " +
                      "EMF meter). Check Meta XR > Project Setup Tool for anything left outstanding.");
        }

        /// <summary>Builds every enabled build scene into Builds/WifiHeatmap.apk.</summary>
        [MenuItem("WiFi Heatmap/Build Quest APK")]
        public static void BuildQuestApk()
        {
            Directory.CreateDirectory("Builds");
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = "Builds/WifiHeatmap.apk",
                target = BuildTarget.Android,
                targetGroup = Android,
                options = BuildOptions.None,
            };
            UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
            UnityEditor.Build.Reporting.BuildSummary summary = report.summary;
            Debug.Log($"WiFi Heatmap build: {summary.result}, {summary.totalSize / 1e6:F1} MB, " +
                      $"{summary.totalErrors} errors, {summary.totalWarnings} warnings, {summary.totalTime.TotalMinutes:F1} min");
            if (Application.isBatchMode && summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }

        // ------------------------------------------------------------------ player
        private static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "WifiHeatmap";
            PlayerSettings.productName = "WiFi Heatmap";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.wifiheatmap.prototype");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.colorSpace = ColorSpace.Linear;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

            // Both input backends: the Meta SDKs and the template's Input System actions coexist.
            UnityEngine.Object playerSettings = Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings");
            SerializedObject serialized = new SerializedObject(playerSettings);
            SerializedProperty inputHandler = serialized.FindProperty("activeInputHandler");
            if (inputHandler != null && inputHandler.intValue != 2)
            {
                inputHandler.intValue = 2;
                serialized.ApplyModifiedProperties();
            }
        }

        // ------------------------------------------------------------------ XR
        private static void ConfigureXr()
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget) ||
                perTarget == null)
            {
                Directory.CreateDirectory("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            }
            if (!perTarget.HasSettingsForBuildTarget(Android))
            {
                perTarget.CreateDefaultSettingsForBuildTarget(Android);
            }
            if (!perTarget.HasManagerSettingsForBuildTarget(Android))
            {
                perTarget.CreateDefaultManagerSettingsForBuildTarget(Android);
            }

            XRGeneralSettings general = perTarget.SettingsForBuildTarget(Android);
            general.InitManagerOnStart = true;
            XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", Android);
            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(perTarget);

            FeatureHelpers.RefreshFeatures(Android);
            foreach (string id in EnabledOpenXrFeatures)
            {
                SetOpenXrFeature(id, true);
            }
            foreach (string id in DisabledOpenXrFeatures)
            {
                SetOpenXrFeature(id, false);
            }

            OpenXRSettings openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(Android);
            if (openXr != null)
            {
                EnableDepthOpenXrFeatures(openXr);
                openXr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                openXr.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.Depth16Bit;
                EditorUtility.SetDirty(openXr);
            }
        }

        private static void EnableDepthOpenXrFeatures(OpenXRSettings openXr)
        {
            foreach (string type in DepthOpenXrFeatureTypes)
            {
                var feature = openXr.GetFeatures().FirstOrDefault(f => f.GetType().FullName == type);
                if (feature == null)
                {
                    Debug.LogWarning($"WiFi Heatmap setup: OpenXR feature {type} is not installed (com.unity.xr.meta-openxr); " +
                                     "the voxel cloud will not be occluded by the real world.");
                    continue;
                }
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
            }
        }

        private static void SetOpenXrFeature(string id, bool enabled)
        {
            var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(Android, id);
            if (feature == null)
            {
                if (enabled)
                {
                    Debug.LogWarning($"WiFi Heatmap setup: OpenXR feature {id} is not installed.");
                }
                return;
            }
            feature.enabled = enabled;
            EditorUtility.SetDirty(feature);
        }

        private static OVRProjectConfig LoadMetaProjectConfig()
        {
            OVRProjectConfig config = OVRProjectConfig.CachedProjectConfig;
            if (config == null)
            {
                // CachedProjectConfig waits for Meta's "editor ready" callback, which a fresh batch
                // import may not have reached yet; the underlying factory does the same work.
                MethodInfo create = typeof(OVRProjectConfig).GetMethod("GetOrCreateProjectConfig",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                config = create?.Invoke(null, null) as OVRProjectConfig;
            }
            return config;
        }

        private static void ConfigureMetaProjectConfig()
        {
            OVRProjectConfig config = LoadMetaProjectConfig();
            if (config == null)
            {
                Debug.LogError("WiFi Heatmap setup: could not load the Meta project config (Assets/Oculus).");
                return;
            }

            config.targetDeviceTypes = new List<OVRProjectConfig.DeviceType>
            {
                OVRProjectConfig.DeviceType.Quest3,
                OVRProjectConfig.DeviceType.Quest3S,
            };
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            config.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Required;   // the Depth API requires it
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            OVRProjectConfig.CommitProjectConfig(config);
        }

        // ------------------------------------------------------------------ rendering
        private static void ConfigureUrp()
        {
            // Android uses the template's "Mobile" quality level and its URP asset; tune it for MR:
            // MSAA instead of post effects, no HDR, no depth/opaque copies.
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset");
            if (asset == null)
            {
                Debug.LogWarning("WiFi Heatmap setup: Assets/Settings/Mobile_RPAsset.asset not found; URP left as is.");
                return;
            }
            asset.msaaSampleCount = 4;
            asset.supportsHDR = false;
            asset.supportsCameraDepthTexture = false;
            asset.supportsCameraOpaqueTexture = false;
            asset.renderScale = 1f;
            EditorUtility.SetDirty(asset);
        }

        private static void ImportTmpEssentials()
        {
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TmpFontPath) != null)
            {
                return;
            }
            TMP_PackageResourceImporter.ImportResources(true, false, false);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        // ------------------------------------------------------------------ assets
        private static Material CreateMaterial(string name, string shaderName, Color color)
        {
            Directory.CreateDirectory(MaterialsFolder);
            string path = $"{MaterialsFolder}/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find(shaderName));
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = Shader.Find(shaderName);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);              // Meta's OcclusionLit
            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.45f);
            }
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.45f);   // Meta's OcclusionLit
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material CreateTransparentMaterial(string name, Color color)
        {
            Material material = CreateMaterial(name, "Universal Render Pipeline/Unlit", color);
            material.SetFloat("_Surface", 1f);   // transparent
            material.SetFloat("_Blend", 0f);     // alpha
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 localPosition,
            Vector3 localScale, Material material, Quaternion? localRotation = null)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = localRotation ?? Quaternion.identity;
            go.transform.localScale = localScale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        private static TextMeshPro Label(string name, Transform parent, Vector3 localPosition, float fontSize,
            Color color, string text, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            // The meter faces -Z; text renders toward -Z of its own transform when unrotated.
            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TmpFontPath);
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.rectTransform.sizeDelta = size;
            return tmp;
        }

        /// <summary>
        /// The meter's materials, on Meta's Depth API occlusion shaders so real hands and people hide it (the LEDs keep
        /// _BaseColor, which EmfMeter sets per LED). Its text uses MeterTextMaterial.
        /// </summary>
        private static (Material Led, Material Body, Material Face, Material Metal) CreateMeterMaterials()
        {
            const string lit = "EnvironmentDepth/OcclusionLit", unlit = "EnvironmentDepth/URP/OcclusionUnlit";
            return (CreateMaterial("EmfLed", unlit, Color.white),
                CreateMaterial("EmfBody", lit, new Color(0.09f, 0.1f, 0.1f)),
                CreateMaterial("EmfFace", lit, new Color(0.02f, 0.02f, 0.025f)),
                CreateMaterial("EmfMetal", lit, new Color(0.6f, 0.6f, 0.62f)));
        }

        /// <summary>The TMP font material on the occluded MeterText shader, for the meter's labels only (the menu keeps TMP's).</summary>
        private static Material MeterTextMaterial(TMP_FontAsset font)
        {
            const string path = MaterialsFolder + "/MeterText.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(font.material) { name = "MeterText" };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = Shader.Find("WiFiHeatmap/MeterText");
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>Ghost-hunter meter: dark handset body, LED arc red to green, dBm readout, stub antenna.</summary>
        private static GameObject BuildMeterPrefab(Material led, Material body, Material face, Material metal)
        {
            GameObject root = new GameObject("EmfMeter");
            Transform t = root.transform;

            Primitive(PrimitiveType.Cube, "Body", t, new Vector3(0f, -0.004f, 0f), new Vector3(0.078f, 0.054f, 0.016f), body);
            Primitive(PrimitiveType.Cube, "FacePlate", t, new Vector3(0f, -0.002f, -0.0081f), new Vector3(0.07f, 0.046f, 0.001f), face);
            Primitive(PrimitiveType.Cylinder, "Antenna", t, new Vector3(-0.03f, 0.036f, 0.002f), new Vector3(0.004f, 0.017f, 0.004f), metal);
            Primitive(PrimitiveType.Sphere, "AntennaTip", t, new Vector3(-0.03f, 0.053f, 0.002f), Vector3.one * 0.007f, metal);

            // LED arc: weak (red) on the left to strong (green) on the right, as seen from the front (-Z).
            const int count = 10;
            const float radius = 0.031f;
            Vector3 centre = new Vector3(0f, -0.019f, -0.0088f);
            Renderer[] leds = new Renderer[count];
            for (int i = 0; i < count; i++)
            {
                float angle = Mathf.Lerp(160f, 20f, i / (float)(count - 1)) * Mathf.Deg2Rad;
                // The viewer looks at the -Z face, so -X is on their left: red at 160° lands on the left.
                Vector3 position = centre + new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
                GameObject socket = Primitive(PrimitiveType.Cylinder, $"Socket{i}", t, position + new Vector3(0f, 0f, 0.0006f),
                    new Vector3(0.0085f, 0.0005f, 0.0085f), metal, Quaternion.Euler(90f, 0f, 0f));
                socket.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                GameObject bulb = Primitive(PrimitiveType.Sphere, $"Led{i}", t, position, Vector3.one * 0.0068f, led);
                bulb.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                leds[i] = bulb.GetComponent<Renderer>();
            }

            TextMeshPro readout = Label("Readout", t, new Vector3(0f, -0.0105f, -0.0092f), 0.11f,
                new Color(0.75f, 1f, 0.75f), "-- dBm", new Vector2(0.05f, 0.012f));
            TextMeshPro status = Label("Status", t, new Vector3(0f, -0.0235f, -0.0092f), 0.045f,
                new Color(0.75f, 0.85f, 0.75f), "", new Vector2(0.066f, 0.007f));
            Label("Title", t, new Vector3(0f, 0.0155f, -0.0092f), 0.045f, new Color(0.55f, 0.6f, 0.55f),
                "WI-FI  FIELD  METER", new Vector2(0.066f, 0.006f));
            Label("ScaleWeak", t, new Vector3(-0.031f, -0.0275f, -0.0092f), 0.035f, new Color(1f, 0.45f, 0.4f),
                "-90", new Vector2(0.012f, 0.005f));
            Label("ScaleStrong", t, new Vector3(0.031f, -0.0275f, -0.0092f), 0.035f, new Color(0.45f, 1f, 0.45f),
                "-30", new Vector2(0.012f, 0.005f));

            TextMeshPro recording = Label("Recording", t, new Vector3(0f, 0.0205f, -0.0092f), 0.04f, new Color(1f, 0.3f, 0.25f),
                "REC", new Vector2(0.066f, 0.006f));

            EmfMeter meter = root.AddComponent<EmfMeter>();
            EmfHaptics haptics = root.AddComponent<EmfHaptics>();
            SerializedObject meterData = new SerializedObject(meter);
            SerializedProperty ledArray = meterData.FindProperty("leds");
            ledArray.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                ledArray.GetArrayElementAtIndex(i).objectReferenceValue = leds[i];
            }
            meterData.FindProperty("readout").objectReferenceValue = readout;
            meterData.FindProperty("status").objectReferenceValue = status;
            meterData.FindProperty("recordingLabel").objectReferenceValue = recording;
            meterData.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject hapticsData = new SerializedObject(haptics);
            hapticsData.FindProperty("meter").objectReferenceValue = meter;
            hapticsData.ApplyModifiedPropertiesWithoutUndo();

            ConfigureMeterReadout(meter);
            Directory.CreateDirectory(Path.GetDirectoryName(MeterPrefabPath));
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, MeterPrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        private static void ConfigureMeterReadout(EmfMeter meter)
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TmpFontPath);
            if (font == null) throw new InvalidOperationException("Meter requires the imported TMP font.");
            Material text = MeterTextMaterial(font);
            foreach (TextMeshPro label in meter.GetComponentsInChildren<TextMeshPro>(true))
            {
                // Old meter labels had no serialized font/material; make the build reference explicit.
                label.font = font; label.fontSharedMaterial = text;
                label.renderer.shadowCastingMode = ShadowCastingMode.Off;
                label.renderer.receiveShadows = false;
            }
            var readout = FindChild(meter.transform, "Readout").GetComponent<TextMeshPro>();
            readout.rectTransform.sizeDelta = new Vector2(.06f, .02f);
            readout.transform.localPosition = new Vector3(0, -.0105f, -.0096f);
            readout.fontSize = .095f;
            readout.fontStyle = FontStyles.Bold;
            readout.color = new Color(.8f, 1, .8f, 1);
            readout.textWrappingMode = TextWrappingModes.NoWrap;
            // A small ellipsis container can suppress the whole first line; the number must stay visible.
            readout.overflowMode = TextOverflowModes.Overflow;
            readout.text = "-- dBm";
            readout.gameObject.SetActive(true);
            SetFields(meter, ("readout", readout),
                ("weakScaleLabel", FindChild(meter.transform, "ScaleWeak").GetComponent<TMP_Text>()),
                ("strongScaleLabel", FindChild(meter.transform, "ScaleStrong").GetComponent<TMP_Text>()));
        }

        private static void UpdateMeterPrefabReadout()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(MeterPrefabPath);
            try
            {
                ConfigureMeterReadout(root.GetComponent<EmfMeter>());
                PrefabUtility.SaveAsPrefabAsset(root, MeterPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static GameObject BuildMountPrefab(Material ghost)
        {
            GameObject root = new GameObject("ProbeMount");
            GameObject pod = Primitive(PrimitiveType.Cube, "PodPlaceholder", root.transform, Vector3.zero, ProbeMount.PodSize, ghost);
            pod.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            GameObject antenna = new GameObject("Antenna");
            antenna.transform.SetParent(root.transform, false);
            antenna.transform.localPosition = ProbeMount.EstimatedAntennaOffset;

            ProbeMount mount = root.AddComponent<ProbeMount>();
            SerializedObject data = new SerializedObject(mount);
            data.FindProperty("antenna").objectReferenceValue = antenna.transform;
            data.FindProperty("placeholder").objectReferenceValue = pod.GetComponent<Renderer>();
            data.ApplyModifiedPropertiesWithoutUndo();

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, MountPrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        private static GameObject EnsureProbePrefab()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab != null)
            {
                return prefab;
            }
            GameObject probe = new GameObject("WiFiProbe");
            probe.AddComponent<Esp32BleSignalTester>();
            prefab = PrefabUtility.SaveAsPrefabAsset(probe, PrefabPath);
            UnityEngine.Object.DestroyImmediate(probe);
            return prefab;
        }

        // ------------------------------------------------------------------ scenes
        private static GameObject LoadPackagePrefab(string name, string packageFolder)
        {
            string guid = AssetDatabase.FindAssets($"{name} t:Prefab", new[] { packageFolder })
                .FirstOrDefault(g => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == name);
            if (guid == null)
            {
                throw new InvalidOperationException($"Prefab {name} not found under {packageFolder}.");
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
        }

        private sealed class SceneMaterials
        {
            public Material Points;
            public Material AnchorMarker;
        }

        private static void BuildMrScene(string path, GameObject meterPrefab, GameObject mountPrefab, GameObject probePrefab,
            GameObject menuPrefab, SceneMaterials materials)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject light = new GameObject("Directional Light");
            Light sun = light.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.None;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // Camera rig with passthrough underneath everything.
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(LoadPackagePrefab("OVRCameraRig", "Packages/com.meta.xr.sdk.core"));
            OVRManager manager = rig.GetComponent<OVRManager>();
            manager.isInsightPassthroughEnabled = true;
            SerializedObject managerData = new SerializedObject(manager);
            SerializedProperty origin = managerData.FindProperty("_trackingOriginType");
            int floor = Array.IndexOf(origin.enumNames, "FloorLevel");
            if (floor >= 0)
            {
                origin.enumValueIndex = floor;
            }
            managerData.ApplyModifiedPropertiesWithoutUndo();
            // OVRPassthroughLayer composites as an underlay behind the scene by default.
            rig.AddComponent<OVRPassthroughLayer>();

            // Anchors are nested (e.g. TrackingSpace/LeftHandAnchor/LeftControllerAnchor), so search by name.
            Transform centerEye = FindChild(rig.transform, "CenterEyeAnchor");
            Transform sensorController = FindChild(rig.transform, "LeftControllerAnchor");
            Camera eye = centerEye.GetComponent<Camera>();
            eye.clearFlags = CameraClearFlags.SolidColor;
            eye.backgroundColor = new Color(0f, 0f, 0f, 0f);

            // Probe pod on the left controller, above the trigger; the Mount tab can move it to the right one.
            var mount = (GameObject)PrefabUtility.InstantiatePrefab(mountPrefab, sensorController);
            mount.transform.localPosition = ProbeMount.EstimatedPodPosition;
            mount.transform.localRotation = Quaternion.identity;
            ProbeMount probeMount = mount.GetComponent<ProbeMount>();

            // Meter fixed just beyond the probe pod's front, square to the controller, screen facing the pod's top.
            var meterObject = (GameObject)PrefabUtility.InstantiatePrefab(meterPrefab);
            EmfMeter meter = meterObject.GetComponent<EmfMeter>();
            SerializedObject meterData = new SerializedObject(meter);
            meterData.FindProperty("follow").objectReferenceValue = sensorController;
            meterData.FindProperty("faceTowards").objectReferenceValue = centerEye;
            meterData.FindProperty("mount").objectReferenceValue = probeMount;
            meterData.ApplyModifiedPropertiesWithoutUndo();

            // BLE probe client plus an (initially disabled) simulated source, both driving the meter.
            var probeObject = (GameObject)PrefabUtility.InstantiatePrefab(probePrefab);
            Esp32BleSignalTester tester = probeObject.GetComponent<Esp32BleSignalTester>();
            UnityEventTools.AddPersistentListener(tester.OnSignalStrengthDbm, meter.SetRssi);
            UnityEventTools.AddPersistentListener(tester.OnStatus, meter.SetStatus);
            UnityEventTools.AddPersistentListener(tester.OnError, meter.SetError);

            SimulatedRssiSource simulated = probeObject.AddComponent<SimulatedRssiSource>();
            SerializedObject simulatedData = new SerializedObject(simulated);
            simulatedData.FindProperty("antenna").objectReferenceValue = probeMount.Antenna;
            simulatedData.FindProperty("placeRelativeTo").objectReferenceValue = centerEye;
            simulatedData.ApplyModifiedPropertiesWithoutUndo();
            UnityEventTools.AddPersistentListener(simulated.OnSignalStrengthDbm, meter.SetRssi);
            simulated.enabled = false;

            // The menu picks the network, so the probe connects on start and then waits at Ready.
            SerializedObject testerData = new SerializedObject(tester);
            testerData.FindProperty("autoStartOnConnect").boolValue = false;
            testerData.ApplyModifiedPropertiesWithoutUndo();

            // Interaction SDK rig (controller + hand rays and pokes) and the config menu.
            AddInteractionRig(rig.GetComponent<OVRCameraRig>());
            var menuObject = (GameObject)PrefabUtility.InstantiatePrefab(menuPrefab);
            PrefabUtility.UnpackPrefabInstance(menuObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            SerializedObject menuData = new SerializedObject(menuObject.GetComponent<ProbeConfigMenu>());
            menuData.FindProperty("probe").objectReferenceValue = tester;
            menuData.FindProperty("simulated").objectReferenceValue = simulated;
            menuData.FindProperty("meter").objectReferenceValue = meter;
            menuData.FindProperty("haptics").objectReferenceValue = meterObject.GetComponent<EmfHaptics>();
            menuData.FindProperty("mount").objectReferenceValue = probeMount;
            menuData.FindProperty("head").objectReferenceValue = centerEye;
            menuData.ApplyModifiedPropertiesWithoutUndo();
            GameObject canvas = menuObject.GetComponentInChildren<Canvas>(true).gameObject;
            QuickActionsAPI.AddRayCanvasInteraction(canvas);
            QuickActionsAPI.AddPokeCanvasInteraction(canvas);

            // MR Utility Kit loads the headset's room scan (walls, floor, furniture) on start.
            PrefabUtility.InstantiatePrefab(LoadPackagePrefab("MRUK", "Packages/com.meta.xr.mrutilitykit"));
            AddEnvironmentDepth();

            // Recording: anchors (rooms + manual), the session writer, and the saved-session viewer.
            GameObject recording = new GameObject("Recording");
            AnchorRegistry registry = recording.AddComponent<AnchorRegistry>();
            SessionRecorder recorder = recording.AddComponent<SessionRecorder>();
            SessionViewer viewer = recording.AddComponent<SessionViewer>();
            SetFields(registry, ("markerMaterial", materials.AnchorMarker));
            SetFields(recorder, ("probe", tester), ("mount", probeMount), ("anchors", registry), ("simulated", simulated), ("meter", meter));
            ConfigureCloudSystem(recording, viewer, registry, tester, probeMount, meter, menuObject);
            SetFields(menuObject.GetComponentInChildren<ProbeCalibrationPanel>(true), ("mount", probeMount), ("recorder", recorder));
            SetFields(menuObject.GetComponentInChildren<SessionsPanel>(true), ("recorder", recorder), ("viewer", viewer));
            SetFields(menuObject.GetComponentInChildren<AnchorsPanel>(true), ("anchors", registry), ("mount", probeMount), ("recorder", recorder));

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            EditorSceneManager.SaveScene(scene, path);
        }

        /// <summary>
        /// Real-world occlusion: the headset's depth sensing hides the voxel cloud behind hands, people and
        /// furniture. The manager sets the SOFT_OCCLUSION keyword globally once depth arrives (Quest 3 / 3S,
        /// after the scene permission); only shaders compiled with it are affected, so the menu, meter and
        /// calibration target stay visible. Hands are kept in the depth so they occlude the cloud too.
        /// </summary>
        private static void AddEnvironmentDepth()
        {
            if (UnityEngine.Object.FindAnyObjectByType<EnvironmentDepthManager>(FindObjectsInactive.Include) != null) return;
            var depth = new GameObject("Environment Depth").AddComponent<EnvironmentDepthManager>();
            depth.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
            depth.RemoveHands = false;
        }

        /// <summary>
        /// Adds Meta's comprehensive interaction rig (rays, pokes, grabs for controllers and hands)
        /// under the camera rig, the same as Meta XR > Interaction SDK > Add Interaction Rig.
        /// The wizard is internal to the SDK, so it is driven by reflection.
        /// </summary>
        private static void AddInteractionRig(OVRCameraRig cameraRig)
        {
            Type wizardType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Oculus.Interaction.OVR.Editor.QuickActions.OVRComprehensiveInteractionRigWizard", false))
                .FirstOrDefault(type => type != null);
            if (wizardType == null)
            {
                Debug.LogWarning("WiFi Heatmap setup: Interaction SDK rig wizard not found; add the rig from the Meta XR menu.");
                return;
            }

            ScriptableObject wizard = ScriptableObject.CreateInstance(wizardType);
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                wizardType.GetMethod("InjectCameraRig", any).Invoke(wizard, new object[] { cameraRig });
                // Use the package prefab directly instead of writing an editable copy into Assets.
                wizardType.GetMethod("InjectOptionalGenerateAsEditableCopy", any).Invoke(wizard, new object[] { false });
                wizardType.GetMethod("CreateInteractionRig", any).Invoke(wizard, null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(wizard);
            }
        }

        // ------------------------------------------------------------------ config menu
        private static readonly Color PanelColor = new Color(0.05f, 0.06f, 0.07f, 0.92f);
        private static readonly Color ButtonColor = new Color(0.16f, 0.18f, 0.2f, 1f);
        private static readonly Color TextColor = new Color(0.88f, 0.94f, 0.9f);
        private static readonly Color DimText = new Color(0.62f, 0.7f, 0.66f);

        /// <summary>World-space settings panel, 520 x 360 canvas units at 1 mm each (52 x 36 cm).</summary>
        private static GameObject BuildMenuPrefab()
        {
            GameObject root = new GameObject("ProbeConfigMenu");
            GameObject canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(root.transform, false);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasObject.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 4f;
            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(520f, 360f);
            canvasRect.localScale = Vector3.one * 0.001f;
            Sprite rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

            Image background = AddImage(canvasRect, "Background", Vector2.zero, canvasRect.sizeDelta, PanelColor, rounded);
            RectTransform ui = background.rectTransform;
            AddText(ui, "Title", new Vector2(-80f, 152f), new Vector2(330f, 36f), 26f, "Wi-Fi Probe Setup", TextColor, TextAlignmentOptions.Left);
            Button close = AddButton(ui, "Close", new Vector2(228f, 152f), new Vector2(44f, 36f), "X", 22f, rounded);

            string[] tabNames = { "Probe", "Network", "Sessions", "Anchors", "Meter", "Mount", "Cloud", "Preview" };
            Button[] tabs = new Button[tabNames.Length];
            GameObject[] panels = new GameObject[tabNames.Length];
            for (int i = 0; i < tabNames.Length; i++)
            {
                tabs[i] = AddButton(ui, $"Tab{tabNames[i]}", new Vector2(-224f + 64f * i, 108f), new Vector2(60f, 38f), tabNames[i], 11f, rounded);
                RectTransform panel = NewRect(ui, $"{tabNames[i]}Panel", new Vector2(0f, -40f), new Vector2(500f, 260f));
                panels[i] = panel.gameObject;
            }

            // Probe tab
            RectTransform probeTab = (RectTransform)panels[0].transform;
            TMP_Text connection = AddText(probeTab, "Connection", new Vector2(0f, 95f), new Vector2(480f, 40f), 28f, "Not connected", TextColor, TextAlignmentOptions.Center);
            TMP_Text device = AddText(probeTab, "Device", new Vector2(0f, 15f), new Vector2(480f, 110f), 14f, "", DimText, TextAlignmentOptions.Center);
            device.textWrappingMode = TextWrappingModes.Normal;
            TMP_Text receiveTiming = AddText(probeTab, "ReceiveTiming", new Vector2(0f, -52f), new Vector2(480f, 22f), 10.5f,
                "BLE RX: waiting for a notification.", DimText, TextAlignmentOptions.Center);
            Button connect = AddButton(probeTab, "Connect", new Vector2(-120f, -90f), new Vector2(200f, 52f), "Connect", 22f, rounded);
            Button disconnect = AddButton(probeTab, "Disconnect", new Vector2(120f, -90f), new Vector2(200f, 52f), "Disconnect", 22f, rounded);

            // Network tab
            RectTransform networkTab = (RectTransform)panels[1].transform;
            TMP_Text target = AddText(networkTab, "Target", new Vector2(0f, 115f), new Vector2(490f, 26f), 14f, "No network selected", TextColor, TextAlignmentOptions.Center);
            Button scan = AddButton(networkTab, "Scan", new Vector2(-185f, 82f), new Vector2(120f, 34f), "Scan", 18f, rounded);
            Button stop = AddButton(networkTab, "Stop", new Vector2(-60f, 82f), new Vector2(110f, 34f), "Stop", 18f, rounded);
            string[] intervalLabels = { "0.1 s", "0.25 s", "0.5 s", "1 s" };
            Button[] intervals = new Button[intervalLabels.Length];
            for (int i = 0; i < intervals.Length; i++)
            {
                intervals[i] = AddButton(networkTab, $"Interval{i}", new Vector2(62f + 60f * i, 82f), new Vector2(56f, 34f), intervalLabels[i], 14f, rounded);
            }

            var rows = new ProbeConfigMenu.NetworkRow[5];
            for (int i = 0; i < rows.Length; i++)
            {
                Button row = AddButton(networkTab, $"Network{i}", new Vector2(0f, 42f - 36f * i), new Vector2(490f, 32f), "", 14f, rounded);
                UnityEngine.Object.DestroyImmediate(row.GetComponentInChildren<TMP_Text>().gameObject);
                RectTransform r = (RectTransform)row.transform;
                rows[i] = new ProbeConfigMenu.NetworkRow
                {
                    button = row,
                    name = AddText(r, "Name", new Vector2(-148f, 0f), new Vector2(180f, 30f), 16f, "", TextColor, TextAlignmentOptions.Left),
                    detail = AddText(r, "Detail", new Vector2(57f, 0f), new Vector2(225f, 30f), 10.5f, "", DimText, TextAlignmentOptions.Left),
                    rssi = AddText(r, "Rssi", new Vector2(205f, 0f), new Vector2(70f, 30f), 15f, "", TextColor, TextAlignmentOptions.Right),
                };
            }
            TMP_Text hint = AddText(networkTab, "Hint", new Vector2(0f, -20f), new Vector2(480f, 40f), 18f, "", DimText, TextAlignmentOptions.Center);
            Button previous = AddButton(networkTab, "PreviousPage", new Vector2(-60f, -128f), new Vector2(60f, 26f), "<", 18f, rounded);
            TMP_Text pageLabel = AddText(networkTab, "Page", new Vector2(0f, -128f), new Vector2(60f, 26f), 14f, "", DimText, TextAlignmentOptions.Center);
            Button next = AddButton(networkTab, "NextPage", new Vector2(60f, -128f), new Vector2(60f, 26f), ">", 18f, rounded);

            BuildSessionsPanel((RectTransform)panels[2].transform, rounded);
            BuildAnchorsPanel((RectTransform)panels[3].transform, rounded);

            // Meter tab
            RectTransform meterTab = (RectTransform)panels[4].transform;
            Button hapticsToggle = AddButton(meterTab, "Haptics", new Vector2(0f, 95f), new Vector2(320f, 42f), "Haptics", 20f, rounded);
            Button simulatedToggle = AddButton(meterTab, "Simulated", new Vector2(0f, 45f), new Vector2(320f, 42f), "Simulated signal", 20f, rounded);
            Button outlineToggle = AddButton(meterTab, "Outline", new Vector2(0f, -5f), new Vector2(320f, 42f), "Probe target", 20f, rounded);
            TMP_Text meterInfo = AddText(meterTab, "Info", new Vector2(0f, -80f), new Vector2(480f, 100f), 12.5f, "", DimText, TextAlignmentOptions.Center);
            meterInfo.textWrappingMode = TextWrappingModes.Normal;

            BuildCalibrationPanel((RectTransform)panels[5].transform, rounded);
            BuildCloudPanel((RectTransform)panels[6].transform, rounded);
            BuildPreviewPanel((RectTransform)panels[7].transform, rounded);

            ProbeConfigMenu menu = root.AddComponent<ProbeConfigMenu>();
            SerializedObject data = new SerializedObject(menu);
            SetArray(data.FindProperty("tabButtons"), tabs);
            SetArray(data.FindProperty("tabPanels"), panels);
            SetArray(data.FindProperty("intervalButtons"), intervals);
            data.FindProperty("closeButton").objectReferenceValue = close;
            data.FindProperty("connectionText").objectReferenceValue = connection;
            data.FindProperty("deviceText").objectReferenceValue = device;
            data.FindProperty("receiveTimingText").objectReferenceValue = receiveTiming;
            data.FindProperty("connectButton").objectReferenceValue = connect;
            data.FindProperty("disconnectButton").objectReferenceValue = disconnect;
            data.FindProperty("targetText").objectReferenceValue = target;
            data.FindProperty("listHint").objectReferenceValue = hint;
            data.FindProperty("pageText").objectReferenceValue = pageLabel;
            data.FindProperty("scanButton").objectReferenceValue = scan;
            data.FindProperty("stopButton").objectReferenceValue = stop;
            data.FindProperty("previousPageButton").objectReferenceValue = previous;
            data.FindProperty("nextPageButton").objectReferenceValue = next;
            data.FindProperty("hapticsButton").objectReferenceValue = hapticsToggle;
            data.FindProperty("simulatedButton").objectReferenceValue = simulatedToggle;
            data.FindProperty("outlineButton").objectReferenceValue = outlineToggle;
            data.FindProperty("meterInfo").objectReferenceValue = meterInfo;
            SerializedProperty rowArray = data.FindProperty("rows");
            rowArray.arraySize = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                SerializedProperty row = rowArray.GetArrayElementAtIndex(i);
                row.FindPropertyRelative("button").objectReferenceValue = rows[i].button;
                row.FindPropertyRelative("name").objectReferenceValue = rows[i].name;
                row.FindPropertyRelative("detail").objectReferenceValue = rows[i].detail;
                row.FindPropertyRelative("rssi").objectReferenceValue = rows[i].rssi;
            }
            data.ApplyModifiedPropertiesWithoutUndo();

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, MenuPrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        private static void ConfigureCloudSystem(GameObject systems, SessionViewer viewer, AnchorRegistry anchors,
            Esp32BleSignalTester probe, ProbeMount mount, EmfMeter meter, GameObject menu)
        {
            var settings = systems.GetComponent<CloudSettings>() ?? systems.AddComponent<CloudSettings>();
            var live = systems.GetComponent<LiveVoxelCloud>() ?? systems.AddComponent<LiveVoxelCloud>();
            Material material = CreateMaterial("VoxelCloud", "WiFiHeatmap/VoxelCloud", Color.white);
            SetFields(settings, ("meter", meter));
            SetFields(viewer, ("anchors", anchors), ("settings", settings), ("cloudMaterial", material));
            SetFields(live, ("recorder", systems.GetComponent<SessionRecorder>()), ("viewer", viewer), ("anchors", anchors),
                ("probe", probe), ("mount", mount), ("settings", settings), ("cloudMaterial", material));
            SetFields(menu.GetComponentInChildren<CloudPanel>(true), ("settings", settings), ("live", live));
            var source = systems.GetComponent<MrukPreviewSource>() ?? systems.AddComponent<MrukPreviewSource>();
            var scan = systems.GetComponent<RoomScanController>() ?? systems.AddComponent<RoomScanController>();
            var preview = systems.GetComponent<RoomPreview>() ?? systems.AddComponent<RoomPreview>();
            var head = (Transform)new SerializedObject(menu.GetComponent<ProbeConfigMenu>()).FindProperty("head").objectReferenceValue;
            var rig = head.GetComponentInParent<OVRCameraRig>();
            SetFields(meter.GetComponent<EmfHaptics>(), ("meter", meter), ("probe", probe), ("mount", mount),
                ("trackingSpace", rig.transform.Find("TrackingSpace")), ("settings", settings));
            Material roomMaterial = CreateMaterial("RoomPreview", "WiFiHeatmap/RoomPreview", Color.white);
            Material fogMaterial = CreateMaterial("MiniatureFog", "WiFiHeatmap/MiniatureFog", Color.white);
            SetFields(source, ("anchors", anchors));
            SetFields(scan, ("recorder", systems.GetComponent<SessionRecorder>()), ("probe", probe), ("mount", mount));
            SetFields(preview, ("source", source), ("scan", scan), ("live", live), ("viewer", viewer),
                ("mount", mount), ("head", head), ("rig", head.GetComponentInParent<OVRCameraRig>()), ("roomMaterial", roomMaterial), ("fogMaterial", fogMaterial), ("settings", settings));
            SetFields(menu.GetComponentInChildren<RoomPreviewPanel>(true), ("preview", preview), ("scan", scan));
            AddRoomOccluders(systems, anchors);
        }

        /// <summary>Depth-only copies of the room's walls, floor, ceiling and furniture that hide the cloud behind them.</summary>
        private static void AddRoomOccluders(GameObject systems, AnchorRegistry anchors)
        {
            var occluders = systems.GetComponent<RoomOccluders>() ?? systems.AddComponent<RoomOccluders>();
            SetFields(occluders, ("anchors", anchors),
                ("occluderMaterial", CreateMaterial("RoomOccluder", "WiFiHeatmap/RoomOccluder", Color.white)));
        }

        private static void BuildPreviewPanel(RectTransform panel, Sprite rounded)
        {
            var main = NewRect(panel,"PreviewMain",Vector2.zero,panel.sizeDelta);
            var fogPage = NewRect(panel,"FogSettings",Vector2.zero,panel.sizeDelta);
            var show = AddButton(main, "ShowPreview", new Vector2(-125,108), new Vector2(230,34), "Show preview",16,rounded);
            var geometry = AddButton(main, "Geometry", new Vector2(125,108), new Vector2(230,34), "Rooms & boxes",16,rounded);
            var ceiling = AddButton(main, "Ceiling", new Vector2(-125,67), new Vector2(230,32), "Ceiling: hidden",15,rounded);
            var cloud = AddButton(main, "Cloud", new Vector2(125,67), new Vector2(230,32), "Heatmap: Fog",15,rounded);
            var zoom = AddCloudSlider(main,"Scale",28,.02f,1,.1f,out var zoomLabel);
            var recenter = AddButton(main,"Recenter",new Vector2(-166,-12),new Vector2(150,32),"Recenter / fit",14,rounded);
            var scan = AddButton(main,"ScanRoom",new Vector2(0,-12),new Vector2(150,32),"Scan room",14,rounded);
            var reload = AddButton(main,"ReloadRoom",new Vector2(166,-12),new Vector2(150,32),"Reload room",14,rounded);
            var fogQuality = AddButton(main,"FogQuality",new Vector2(-125,-50),new Vector2(230,28),"Fog quality: 24 steps",12.5f,rounded);
            var fogOptions = AddButton(main,"FogOptions",new Vector2(125,-50),new Vector2(230,28),"Fog settings",13,rounded);
            AddText(fogPage,"Title",new Vector2(0,108),new Vector2(480,30),21,"Preview fog",TextColor,TextAlignmentOptions.Center);
            // the fog's own density (RoomPreview.FogDensity), separate from the Cloud tab's opacity (the full-size cloud's)
            var fogDensity = AddCloudSlider(fogPage,"Density",72,0,1,.28f,out var fogDensityLabel);
            var signalDensity = AddButton(fogPage,"SignalDensity",new Vector2(0,34),new Vector2(360,30),"Signal density: Off",15,rounded);
            var densityEffect = AddCloudSlider(fogPage,"Signal effect",-2,0,1,.5f,out var densityLabel);
            var explanation = AddText(fogPage,"Explanation",new Vector2(0,-54),new Vector2(480,62),12,
                "Stronger readings become denser using the Cloud low/high dBm range.\n0%: uniform signal weighting. 100%: weak 0.2x / strong 2x.\nCoverage and confidence still control visibility.",DimText,TextAlignmentOptions.Center);
            explanation.textWrappingMode = TextWrappingModes.Normal;
            var fogBack = AddButton(fogPage,"Back",new Vector2(0,-108),new Vector2(240,34),"Back to preview",16,rounded);
            fogPage.gameObject.SetActive(false);
            var status = AddText(main,"Status",new Vector2(0,-101),new Vector2(480,67),10.5f,
                "Scan room opens Quest Space Setup and ends the current recording.",DimText,TextAlignmentOptions.Center);
            status.textWrappingMode = TextWrappingModes.Normal;
            SetFields(panel.gameObject.AddComponent<RoomPreviewPanel>(), ("show",show), ("geometry",geometry),
                ("ceiling",ceiling), ("cloud",cloud), ("zoom",zoom), ("zoomLabel",zoomLabel), ("recenter",recenter),
                ("scanRoom",scan), ("reload",reload), ("status",status), ("fogQuality",fogQuality), ("mainPage",main.gameObject), ("fogPage",fogPage.gameObject),
                ("fogOptions",fogOptions), ("fogBack",fogBack), ("signalDensity",signalDensity),
                ("densityEffect",densityEffect), ("densityLabel",densityLabel),
                ("fogDensity",fogDensity), ("fogDensityLabel",fogDensityLabel));
        }

        private static Slider AddCloudSlider(RectTransform parent, string name, float y, float min, float max, float value,
            out TMP_Text label)
        {
            label = AddText(parent, name + "Label", new Vector2(-165, y), new Vector2(155, 28), 15, name, TextColor, TextAlignmentOptions.Left);
            var root = AddImage(parent, name, new Vector2(80, y), new Vector2(310, 28), new Color(.12f, .14f, .16f), null);
            var bar = AddImage(root.rectTransform, "Track", Vector2.zero, new Vector2(290, 5), new Color(.35f, .4f, .45f), null);
            bar.raycastTarget = false;
            var area = NewRect(root.rectTransform, "HandleArea", Vector2.zero, new Vector2(286, 28));
            var handle = AddImage(area, "Handle", Vector2.zero, new Vector2(24, 28), new Color(.8f, .95f, .9f), null);
            var slider = root.gameObject.AddComponent<Slider>();
            slider.targetGraphic = handle; slider.handleRect = handle.rectTransform;
            slider.direction = Slider.Direction.LeftToRight; slider.minValue = min; slider.maxValue = max; slider.value = value;
            return slider;
        }

        private static void BuildCloudPanel(RectTransform panel, Sprite rounded)
        {
            var main = NewRect(panel, "CloudDisplay", Vector2.zero, panel.sizeDelta);
            var colors = NewRect(panel, "CloudColors", Vector2.zero, panel.sizeDelta);
            var resolution = AddCloudSlider(main, "Voxel size", 110, .02f, .5f, .12f, out var resolutionLabel);
            var opacity = AddCloudSlider(main, "Opacity", 76, .05f, .8f, .35f, out var opacityLabel);
            var low = AddCloudSlider(main, "Low dBm", 42, SignalDisplayRange.MinimumDbm,
                SignalDisplayRange.MaximumDbm - SignalDisplayRange.MinimumSpanDb, -90, out var lowLabel);
            var high = AddCloudSlider(main, "High dBm", 8, SignalDisplayRange.MinimumDbm + SignalDisplayRange.MinimumSpanDb,
                SignalDisplayRange.MaximumDbm, -30, out var highLabel);
            low.wholeNumbers = high.wholeNumbers = true;
            var rangeRoot = NewRect(main, "GradientRange", new Vector2(0, -32), new Vector2(460, 32));
            var raw = rangeRoot.gameObject.AddComponent<RawImage>(); raw.color = Color.white;
            var lowHandle = AddImage(rangeRoot, "LowHandle", Vector2.zero, new Vector2(20, 36), Color.white, null);
            var highHandle = AddImage(rangeRoot, "HighHandle", Vector2.zero, new Vector2(20, 36), Color.white, null);
            lowHandle.raycastTarget = highHandle.raycastTarget = false;
            var range = rangeRoot.gameObject.AddComponent<CloudRangeSlider>();
            SetFields(range, ("lowHandle", lowHandle.rectTransform), ("highHandle", highHandle.rectTransform), ("gradient", raw));
            var rangeLabel = AddText(main, "ColorAnchors", new Vector2(0, -61), new Vector2(490, 22), 14,
                "Drag the two handles to move color anchors", TextColor, TextAlignmentOptions.Center);
            var colorButton = AddButton(main, "Colors", new Vector2(-164, -88), new Vector2(154, 30), "Choose colors", 14, rounded);
            var autoScale = AddButton(main, "AutoScale", new Vector2(0, -88), new Vector2(154, 30), "Auto scale: Off", 14, rounded);
            var clear = AddButton(main, "ClearLive", new Vector2(164, -88), new Vector2(154, 30), "Clear live cloud", 14, rounded);
            var status = AddText(main, "CloudStatus", new Vector2(0, -119), new Vector2(490, 35), 10.5f, "", DimText, TextAlignmentOptions.Center);
            status.textWrappingMode = TextWrappingModes.Normal;
            Button[] colorButtons = new Button[3];
            string[] labels = { "Low color", "Middle color", "High color" };
            for (int i = 0; i < 3; i++)
                colorButtons[i] = AddButton(colors, "Color" + i, new Vector2(-160 + 160 * i, 100), new Vector2(145, 40), labels[i], 17, rounded);
            var colorLabel = AddText(colors, "ColorLabel", new Vector2(0, 59), new Vector2(490, 25), 14, "Select a color, then adjust RGB", TextColor, TextAlignmentOptions.Center);
            var red = AddCloudSlider(colors, "Red", 20, 0, 1, 1, out _);
            var green = AddCloudSlider(colors, "Green", -20, 0, 1, .2f, out _);
            var blue = AddCloudSlider(colors, "Blue", -60, 0, 1, .1f, out _);
            var back = AddButton(colors, "Back", new Vector2(0, -110), new Vector2(280, 36), "Back to cloud settings", 17, rounded);
            var component = panel.gameObject.AddComponent<CloudPanel>();
            SetFields(component, ("resolution", resolution), ("opacity", opacity), ("lowDbm", low), ("highDbm", high),
                ("resolutionLabel", resolutionLabel), ("opacityLabel", opacityLabel), ("lowLabel", lowLabel), ("highLabel", highLabel),
                ("rangeLabel", rangeLabel), ("status", status), ("range", range), ("colorsButton", colorButton), ("clearButton", clear),
                ("autoScaleButton", autoScale),
                ("backButton", back), ("mainPage", main.gameObject), ("colorsPage", colors.gameObject),
                ("red", red), ("green", green), ("blue", blue), ("colorLabel", colorLabel));
            var data = new SerializedObject(component); SetArray(data.FindProperty("colorButtons"), colorButtons); data.ApplyModifiedPropertiesWithoutUndo();
            colors.gameObject.SetActive(false);
        }

        private static void BuildCalibrationPanel(RectTransform panel, Sprite rounded)
        {
            var side = AddButton(panel, "ProbeSide", new Vector2(-117f, 100f), new Vector2(230f, 38f), "Side: left", 18f, rounded);
            var carrier = AddButton(panel, "ProbeCarrier", new Vector2(117f, 100f), new Vector2(230f, 38f), "On: controller", 18f, rounded);
            var freeze = AddButton(panel, "FreezeAlign", new Vector2(-117f, 55f), new Vector2(230f, 36f), "Align probe", 17f, rounded);
            var meterAlign = AddButton(panel, "MeterAlign", new Vector2(117f, 55f), new Vector2(230f, 36f), "Align meter", 17f, rounded);
            var info = AddText(panel, "CalibrationInfo", new Vector2(0f, -20f), new Vector2(480f, 110f), 13f,
                "", DimText, TextAlignmentOptions.Center);
            info.textWrappingMode = TextWrappingModes.Normal;
            var save = AddButton(panel, "SaveOffset", new Vector2(-165f, -105f), new Vector2(150f, 38f), "Save offset", 17f, rounded);
            var load = AddButton(panel, "LoadOffset", new Vector2(0f, -105f), new Vector2(150f, 38f), "Load saved", 17f, rounded);
            var reset = AddButton(panel, "ResetOffset", new Vector2(165f, -105f), new Vector2(150f, 38f), "Reset estimate", 16f, rounded);
            SetFields(panel.gameObject.AddComponent<ProbeCalibrationPanel>(),
                ("sideButton", side), ("carrierButton", carrier), ("freezeButton", freeze), ("meterButton", meterAlign), ("info", info),
                ("saveButton", save), ("loadButton", load), ("resetButton", reset));
        }

        /// <summary>CLI entry point; explicitly exits after saving to avoid unrelated SDK background operations
        /// keeping a batch menu migration alive. Never closes an interactive Editor.</summary>
        public static void UpdateCloudMenusBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the menu in an interactive Editor.");
            try { UpdateCalibrationMenus(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        /// <summary>Regenerate menu diagnostics and compile Android player branches (including JNI code)
        /// without building an APK or closing an interactive Editor.</summary>
        public static void CompileBleCallbackTimingBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Batch-only entry point.");
            try
            {
                UpdateCalibrationMenus();
                var compilation = new UnityEditor.Build.Player.ScriptCompilationSettings
                {
                    group = BuildTargetGroup.Android, target = BuildTarget.Android
                };
                string output = "Temp/BleCallbackTimingScripts";
                Directory.CreateDirectory(output);
                var result = UnityEditor.Build.Player.PlayerBuildInterface.CompilePlayerScripts(compilation, output);
                if (result.assemblies == null || result.assemblies.Count == 0)
                    throw new InvalidOperationException("Android player script compilation produced no assemblies.");
                Debug.Log("BLE callback timing: Android player scripts compiled successfully.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        /// <summary>
        /// Turns on real-world occlusion in an already configured project without rebuilding its scenes: the OpenXR
        /// Meta session and occlusion features, Scene support set to required, the meter's occluding materials, and an
        /// EnvironmentDepthManager (Depth API) and RoomOccluders (depth-only room surfaces) in both saved scenes.
        /// Configure does the same as part of a full setup.
        /// </summary>
        [MenuItem("WiFi Heatmap/Enable Real-World Occlusion")]
        public static void EnableRealWorldOcclusion()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            FeatureHelpers.RefreshFeatures(Android);
            OpenXRSettings openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(Android);
            if (openXr != null) EnableDepthOpenXrFeatures(openXr);
            OVRProjectConfig config = LoadMetaProjectConfig();
            if (config != null)
            {
                config.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
                OVRProjectConfig.CommitProjectConfig(config);
            }
            else Debug.LogError("WiFi Heatmap setup: could not load the Meta project config; set Scene support to Required.");
            CreateMeterMaterials();
            UpdateMeterPrefabReadout();

            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (string path in new[] { ScenePath, HeatmapScenePath })
                {
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    AddEnvironmentDepth();
                    var registry = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<AnchorRegistry>(true)).Single();
                    AddRoomOccluders(registry.gameObject, registry);
                    ConfigureMeterReadout(scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<EmfMeter>(true)).Single());
                    EditorSceneManager.SaveScene(scene, path);
                }
            }
            finally
            {
                if (previous.Length > 0 && !string.IsNullOrEmpty(previous[0].path))
                    EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("WiFi Heatmap: real-world occlusion enabled (OpenXR Meta occlusion, Scene required, occluded meter, depth " +
                      "manager and room occluders in both scenes). Check Meta XR > Project Setup Tool for anything left outstanding.");
        }

        /// <summary>CLI entry point for <see cref="EnableRealWorldOcclusion"/>; exits when done.</summary>
        public static void EnableRealWorldOcclusionBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the menu in an interactive Editor.");
            try { EnableRealWorldOcclusion(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        /// <summary>Updates the menus in existing scenes, preserving their rigs and recording systems.</summary>
        [MenuItem("WiFi Heatmap/Update Cloud and Calibration Menus")]
        public static void UpdateCalibrationMenus()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            UpdateMeterPrefabReadout();
            GameObject prefab = BuildMenuPrefab();
            try
            {
                foreach (string path in new[] { ScenePath, HeatmapScenePath })
                {
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    var roots = scene.GetRootGameObjects();
                    var old = roots.SelectMany(r => r.GetComponentsInChildren<ProbeConfigMenu>(true)).Single();
                    var recorder = roots.SelectMany(r => r.GetComponentsInChildren<SessionRecorder>(true)).Single();
                    var viewer = roots.SelectMany(r => r.GetComponentsInChildren<SessionViewer>(true)).Single();
                    var anchors = roots.SelectMany(r => r.GetComponentsInChildren<AnchorRegistry>(true)).Single();
                    var saved = new SerializedObject(old);
                    ConfigureMeterReadout((EmfMeter)saved.FindProperty("meter").objectReferenceValue);
                    SetFields(saved.FindProperty("meter").objectReferenceValue, ("mount", saved.FindProperty("mount").objectReferenceValue));
                    var replacement = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    PrefabUtility.UnpackPrefabInstance(replacement, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    replacement.transform.SetPositionAndRotation(old.transform.position, old.transform.rotation);
                    var menu = replacement.GetComponent<ProbeConfigMenu>();
                    foreach (string field in new[] { "probe", "simulated", "meter", "haptics", "mount", "head" })
                        SetFields(menu, (field, saved.FindProperty(field).objectReferenceValue));
                    SetFields(replacement.GetComponentInChildren<ProbeCalibrationPanel>(true),
                        ("mount", saved.FindProperty("mount").objectReferenceValue), ("recorder", recorder));
                    SetFields(replacement.GetComponentInChildren<SessionsPanel>(true), ("recorder", recorder), ("viewer", viewer));
                    SetFields(replacement.GetComponentInChildren<AnchorsPanel>(true), ("anchors", anchors),
                        ("mount", saved.FindProperty("mount").objectReferenceValue), ("recorder", recorder));
                    ConfigureCloudSystem(recorder.gameObject, viewer, anchors,
                        (Esp32BleSignalTester)saved.FindProperty("probe").objectReferenceValue,
                        (ProbeMount)saved.FindProperty("mount").objectReferenceValue,
                        (EmfMeter)saved.FindProperty("meter").objectReferenceValue, replacement);
                    var canvas = replacement.GetComponentInChildren<Canvas>(true).gameObject;
                    QuickActionsAPI.AddRayCanvasInteraction(canvas);
                    QuickActionsAPI.AddPokeCanvasInteraction(canvas);
                    UnityEngine.Object.DestroyImmediate(old.gameObject);
                    AddEnvironmentDepth();
                    EditorSceneManager.SaveScene(scene, path);
                }
            }
            finally
            {
                if (previous.Length > 0 && !string.IsNullOrEmpty(previous[0].path))
                    EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("Menus, voxel clouds and environment-depth occlusion updated in both saved scenes.");
        }

        /// <summary>A list row: dark background, two-line label on the left, two buttons on the right.</summary>
        private static (GameObject Root, TMP_Text Label, Button First, Button Second) ListRow(RectTransform parent, string name,
            float y, float height, string first, string second, float labelWidth, Sprite sprite)
        {
            Image background = AddImage(parent, name, new Vector2(0f, y), new Vector2(490f, height), new Color(0.1f, 0.11f, 0.12f, 1f), sprite);
            RectTransform r = background.rectTransform;
            TMP_Text label = AddText(r, "Label", new Vector2(-240f + 8f + labelWidth / 2f, 0f), new Vector2(labelWidth, height), 13f,
                "", TextColor, TextAlignmentOptions.Left);
            Button a = AddButton(r, first, new Vector2(132f, 0f), new Vector2(76f, height - 6f), first, 14f, sprite);
            Button b = AddButton(r, second, new Vector2(205f, 0f), new Vector2(66f, height - 6f), second, 14f, sprite);
            return (background.gameObject, label, a, b);
        }

        private static void BuildSessionsPanel(RectTransform panel, Sprite sprite)
        {
            Button record = AddButton(panel, "Record", new Vector2(-172f, 110f), new Vector2(146f, 36f), "Record", 18f, sprite);
            TMP_Text recordStatus = AddText(panel, "RecordStatus", new Vector2(80f, 110f), new Vector2(340f, 40f), 11f, "", DimText,
                TextAlignmentOptions.Left);
            recordStatus.textWrappingMode = TextWrappingModes.Normal;

            SessionsPanel component = panel.gameObject.AddComponent<SessionsPanel>();
            SerializedObject data = new SerializedObject(component);
            SerializedProperty rows = data.FindProperty("rows");
            rows.arraySize = 4;
            for (int i = 0; i < 4; i++)
            {
                var row = ListRow(panel, $"Session{i}", 62f - 42f * i, 38f, "Load", "Delete", 340f, sprite);
                SerializedProperty element = rows.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("root").objectReferenceValue = row.Root;
                element.FindPropertyRelative("label").objectReferenceValue = row.Label;
                element.FindPropertyRelative("load").objectReferenceValue = row.First;
                element.FindPropertyRelative("delete").objectReferenceValue = row.Second;
            }
            data.FindProperty("recordButton").objectReferenceValue = record;
            data.FindProperty("recordStatus").objectReferenceValue = recordStatus;
            data.FindProperty("unloadButton").objectReferenceValue = AddButton(panel, "Unload", new Vector2(-195f, -104f), new Vector2(96f, 26f), "Unload", 14f, sprite);
            data.FindProperty("previousPageButton").objectReferenceValue = AddButton(panel, "PreviousPage", new Vector2(-60f, -104f), new Vector2(56f, 26f), "<", 18f, sprite);
            data.FindProperty("pageText").objectReferenceValue = AddText(panel, "Page", new Vector2(0f, -104f), new Vector2(64f, 26f), 13f, "", DimText, TextAlignmentOptions.Center);
            data.FindProperty("nextPageButton").objectReferenceValue = AddButton(panel, "NextPage", new Vector2(60f, -104f), new Vector2(56f, 26f), ">", 18f, sprite);
            TMP_Text viewerStatus = AddText(panel, "ViewerStatus", new Vector2(0f, -124f), new Vector2(490f, 16f), 10.5f, "", DimText, TextAlignmentOptions.Center);
            data.FindProperty("viewerStatus").objectReferenceValue = viewerStatus;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildAnchorsPanel(RectTransform panel, Sprite sprite)
        {
            Button auto = AddButton(panel, "Auto", new Vector2(-170f, 110f), new Vector2(150f, 34f), "Auto (rooms)", 15f, sprite);
            Button place = AddButton(panel, "Place", new Vector2(10f, 110f), new Vector2(200f, 34f), "Place anchor at probe", 15f, sprite);
            TMP_Text status = AddText(panel, "Status", new Vector2(0f, 72f), new Vector2(490f, 38f), 11.5f, "", DimText, TextAlignmentOptions.Left);
            status.textWrappingMode = TextWrappingModes.Normal;

            AnchorsPanel component = panel.gameObject.AddComponent<AnchorsPanel>();
            SerializedObject data = new SerializedObject(component);
            SerializedProperty rows = data.FindProperty("rows");
            rows.arraySize = 4;
            for (int i = 0; i < 4; i++)
            {
                var row = ListRow(panel, $"Anchor{i}", 30f - 38f * i, 34f, "Use", "Delete", 330f, sprite);
                SerializedProperty element = rows.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("root").objectReferenceValue = row.Root;
                element.FindPropertyRelative("label").objectReferenceValue = row.Label;
                element.FindPropertyRelative("use").objectReferenceValue = row.First;
                element.FindPropertyRelative("delete").objectReferenceValue = row.Second;
            }
            data.FindProperty("autoButton").objectReferenceValue = auto;
            data.FindProperty("placeButton").objectReferenceValue = place;
            data.FindProperty("status").objectReferenceValue = status;
            data.FindProperty("previousPageButton").objectReferenceValue = AddButton(panel, "PreviousPage", new Vector2(-60f, -118f), new Vector2(56f, 24f), "<", 18f, sprite);
            data.FindProperty("pageText").objectReferenceValue = AddText(panel, "Page", new Vector2(0f, -118f), new Vector2(64f, 24f), 13f, "", DimText, TextAlignmentOptions.Center);
            data.FindProperty("nextPageButton").objectReferenceValue = AddButton(panel, "NextPage", new Vector2(60f, -118f), new Vector2(56f, 24f), ">", 18f, sprite);
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetArray(SerializedProperty property, UnityEngine.Object[] values)
        {
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        private static RectTransform NewRect(RectTransform parent, string name, Vector2 position, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        private static Image AddImage(RectTransform parent, string name, Vector2 position, Vector2 size, Color color, Sprite sprite)
        {
            Image image = NewRect(parent, name, position, size).gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.color = color;
            return image;
        }

        private static TMP_Text AddText(RectTransform parent, string name, Vector2 position, Vector2 size, float fontSize,
            string text, Color color, TextAlignmentOptions alignment)
        {
            TextMeshProUGUI label = NewRect(parent, name, position, size).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TmpFontPath);
            label.fontSize = fontSize;
            label.color = color;
            label.text = text;
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            return label;
        }

        private static Button AddButton(RectTransform parent, string name, Vector2 position, Vector2 size, string text,
            float fontSize, Sprite sprite)
        {
            Image image = AddImage(parent, name, position, size, ButtonColor, sprite);
            Button button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            AddText(image.rectTransform, "Label", Vector2.zero, size, fontSize, text, TextColor, TextAlignmentOptions.Center);
            return button;
        }

        private static void SetFields(UnityEngine.Object target, params (string Name, UnityEngine.Object Value)[] fields)
        {
            SerializedObject data = new SerializedObject(target);
            foreach ((string name, UnityEngine.Object value) in fields)
            {
                SerializedProperty property = data.FindProperty(name);
                if (property == null)
                {
                    throw new InvalidOperationException($"{target.GetType().Name} has no serialized field {name}.");
                }
                property.objectReferenceValue = value;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Transform FindChild(Transform root, string name)
        {
            Transform found = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
            if (found == null)
            {
                throw new InvalidOperationException($"{root.name} has no child named {name}.");
            }
            return found;
        }

        // ------------------------------------------------------------------ Meta Project Setup Tool
        /// <summary>
        /// Applies every outstanding Meta Project Setup Tool fix for Android. The public FixAllAsync
        /// depends on editor ticks that batch mode does not run, so the blocking internal fixer is
        /// called by reflection; fixes can uncover new tasks, so it runs a few passes.
        /// </summary>
        private static void RunMetaProjectSetupFixes()
        {
            Type setup = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("OVRProjectSetup", false))
                .FirstOrDefault(type => type != null);
            MethodInfo fix = setup?.GetMethod("FixTasks", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (fix == null)
            {
                Debug.LogWarning("WiFi Heatmap setup: Meta Project Setup Tool fixer not found; run Meta XR > Project Setup Tool > Fix All.");
                return;
            }

            ParameterInfo[] parameters = fix.GetParameters();
            for (int pass = 0; pass < 3; pass++)
            {
                object[] args = parameters.Select(p => DefaultArgument(p)).ToArray();
                fix.Invoke(null, args);
            }
        }

        private static object DefaultArgument(ParameterInfo parameter)
        {
            switch (parameter.Name)
            {
                case "buildTargetGroup":
                    return Android;
                case "blocking":
                    return true;
                case "logMessages":
                    return Enum.Parse(parameter.ParameterType, "Summary");
            }
            return parameter.HasDefaultValue ? parameter.DefaultValue : null;
        }
    }
}
