using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;
using UnityEngine.XR.OpenXR.Features.Interactions;
using BioimageVR;

namespace BioimageVR.EditorSetup
{
    // headless setup fuer phase 1, urp, volumen material, minimalszene, openxr
    // einmal nach projekterstellung laufen lassen, kein teil der laufenden app
    public static class SetupPhase1
    {
        private const string VolumeFilePath = @"C:\Users\PC\Documents\ecip\data\sample\idr_6001240.nii.gz";

        [MenuItem("BioimageVR/Setup Phase 1 Scene")]
        public static void Run()
        {
            AssignUniversalRenderPipeline();
            EnableOpenXR();
            Material volumeMaterial = CreateVolumeMaterial();
            BuildScene(volumeMaterial);

            AssetDatabase.SaveAssets();
            Debug.Log("BioimageVR Phase 1 setup complete.");
        }

        private static void AssignUniversalRenderPipeline()
        {
            const string rendererAssetPath = "Assets/Settings/Phase1_Renderer.asset";
            const string pipelineAssetPath = "Assets/Settings/Phase1_URP.asset";
            Directory.CreateDirectory("Assets/Settings");

            // create ohne renderer gibt leere renderer liste, immer beide frisch bauen
            AssetDatabase.DeleteAsset(pipelineAssetPath);
            AssetDatabase.DeleteAsset(rendererAssetPath);

            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, rendererAssetPath);

            var urpAsset = UniversalRenderPipelineAsset.Create(rendererData);
            AssetDatabase.CreateAsset(urpAsset, pipelineAssetPath);

            GraphicsSettings.defaultRenderPipeline = urpAsset;
            QualitySettings.renderPipeline = urpAsset;
        }

        // standalone fuer editor und air link, android fuer die echte apk
        // ohne loader hier bleibt openxr am geraet aus, nur flaches bild
        private static readonly BuildTargetGroup[] OpenXRBuildTargetGroups =
        {
            BuildTargetGroup.Standalone,
            BuildTargetGroup.Android,
        };

        [MenuItem("BioimageVR/Enable OpenXR For All Targets")]
        public static void EnableOpenXRForAllTargets()
        {
            EnableOpenXR();
            EnableOpenXRFeaturesForAndroid();
            EnableOpenXRControllerProfileForStandalone();
            AssetDatabase.SaveAssets();
            Debug.Log("BioimageVR: OpenXR enable pass complete.");
        }

        private static void EnableOpenXR()
        {
            // macht dasselbe wie das haekchen in project settings xr plugin management
            // falls api sich mal aendert, dann manuell in unity readme schritt 2
            try
            {
                if (!EditorBuildSettings.TryGetConfigObject(
                        XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget buildTargetSettings))
                {
                    Debug.LogWarning("BioimageVR: no XRGeneralSettingsPerBuildTarget config object found - enable " +
                                      "OpenXR manually under Project Settings -> XR Plug-in Management.");
                    return;
                }

                foreach (var group in OpenXRBuildTargetGroups)
                {
                    EnableOpenXRForBuildTarget(buildTargetSettings, group);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"BioimageVR: could not enable OpenXR automatically ({e.Message}) - " +
                                  "enable it manually under Project Settings -> XR Plug-in Management.");
            }
        }

        private static void EnableOpenXRForBuildTarget(XRGeneralSettingsPerBuildTarget buildTargetSettings, BuildTargetGroup group)
        {
            if (!buildTargetSettings.HasManagerSettingsForBuildTarget(group))
                buildTargetSettings.CreateDefaultManagerSettingsForBuildTarget(group);

            var generalSettings = buildTargetSettings.SettingsForBuildTarget(group);
            var settingsManager = generalSettings.AssignedSettings;

            // defaults sind false, entspricht initialize xr on startup haekchen
            // ohne das startet der loader nie, bild bleibt flach
            settingsManager.automaticLoading = true;
            settingsManager.automaticRunning = true;
            EditorUtility.SetDirty(settingsManager);
            EditorUtility.SetDirty(generalSettings);

            bool alreadyAssigned = settingsManager.activeLoaders.Any(l => l is OpenXRLoader);
            if (alreadyAssigned)
            {
                Debug.Log($"BioimageVR: OpenXR loader already assigned for {group}.");
            }
            else
            {
                bool assigned = XRPackageMetadataStore.AssignLoader(
                    settingsManager, typeof(OpenXRLoader).FullName, group);
                Debug.Log(assigned
                    ? $"BioimageVR: OpenXR loader assigned for {group}."
                    : $"BioimageVR: failed to assign the OpenXR loader for {group} - enable manually under " +
                      "Project Settings -> XR Plug-in Management.");
            }

            generalSettings.InitManagerOnStart = true;
        }

        // noetig fuer echte quest apk, manifest und controller, nicht nur stereo bild
        private static void EnableOpenXRFeaturesForAndroid()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null)
            {
                Debug.LogWarning("BioimageVR: no OpenXR settings found for Android - enable 'Meta Quest Support' " +
                                  "and a controller profile manually under Project Settings -> XR Plug-in " +
                                  "Management -> OpenXR (Android tab).");
                return;
            }

            EnableFeature<MetaQuestFeature>(settings, "Meta Quest Support");
            EnableFeature<OculusTouchControllerProfile>(settings, "Oculus Touch Controller Profile");
        }

        // air link laeuft als windows standalone, eigener openxr tab
        // meta quest support gibts da nicht, controller profil aber schon
        // war vorher nie aktiviert, deshalb kein controller input ueber link
        private static void EnableOpenXRControllerProfileForStandalone()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            if (settings == null)
            {
                Debug.LogWarning("BioimageVR: no OpenXR settings found for Standalone - enable 'Oculus Touch " +
                                  "Controller Profile' manually under Project Settings -> XR Plug-in " +
                                  "Management -> OpenXR (PC tab), otherwise controller input won't work over " +
                                  "Quest Link/Air Link.");
                return;
            }

            EnableFeature<OculusTouchControllerProfile>(settings, "Oculus Touch Controller Profile (Standalone)");
        }

        private static void EnableFeature<T>(OpenXRSettings settings, string label) where T : OpenXRFeature
        {
            var features = settings.GetFeatures<T>();
            if (features.Length == 0)
            {
                Debug.LogWarning($"BioimageVR: '{label}' feature not found in OpenXR settings for Android.");
                return;
            }

            foreach (var feature in features)
                feature.enabled = true;
            Debug.Log($"BioimageVR: '{label}' enabled for Android.");
        }

        private static Material CreateVolumeMaterial()
        {
            const string assetPath = "Assets/Materials/VolumeMaterial.mat";
            Directory.CreateDirectory("Assets/Materials");

            var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null) return existing;

            Shader shader = Shader.Find("BioimageVR/VolumeRaymarch");
            if (shader == null)
            {
                Debug.LogError("BioimageVR: shader 'BioimageVR/VolumeRaymarch' not found - " +
                                "make sure unity/Shaders/VolumeRaymarch.shader was copied to Assets/Shaders.");
                return null;
            }

            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, assetPath);
            return material;
        }

        private static void BuildScene(Material volumeMaterial)
        {
            Directory.CreateDirectory("Assets/Scenes");
            const string scenePath = "Assets/Scenes/Phase1.unity";

            // emptyscene statt default, sonst doppeltes maincamera tag mit xr origin
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightGO = new GameObject("Directional Light");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // erst xr origin vr menu item versuchen, macht head tracking automatisch
            // sonst fallback auf normale camera, dann kein head tracking
            bool xrOriginCreated = TryExecuteMenuItem("GameObject/XR/XR Origin (VR)")
                                   || TryExecuteMenuItem("GameObject/XR/Room-Scale XR Origin");

            if (!xrOriginCreated)
            {
                Debug.LogWarning("BioimageVR: could not create an XR Origin via menu item - falling back to a " +
                                  "plain Main Camera. Add an XR Origin manually (see unity/README.md step 5) " +
                                  "for actual headset head tracking.");
            }

            GameObject volumeGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
            volumeGO.name = "Volume";
            volumeGO.transform.position = new Vector3(0f, 1.5f, 2f);

            var renderer = volumeGO.GetComponent<MeshRenderer>();
            if (volumeMaterial != null)
                renderer.sharedMaterial = volumeMaterial;

            var volumeView = volumeGO.AddComponent<VolumeView>();
            volumeView.VolumeFilePath = VolumeFilePath;

            EditorSceneManager.SaveScene(scene, scenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
        }

        private static bool TryExecuteMenuItem(string menuPath)
        {
            try
            {
                return EditorApplication.ExecuteMenuItem(menuPath);
            }
            catch
            {
                return false;
            }
        }
    }
}
