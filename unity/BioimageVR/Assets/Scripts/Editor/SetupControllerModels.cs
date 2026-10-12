using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace BioimageVR.EditorSetup
{
    // echte touch controller modelle ueber OVRRuntimeController, braucht meta xr core sdk
    public static class SetupControllerModels
    {
        private const string ScenePath = "Assets/Scenes/Phase1.unity";

        [MenuItem("BioimageVR/Setup Controller Models")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath && File.Exists(ScenePath))
                scene = EditorSceneManager.OpenScene(ScenePath);

            EnsureOVRManager();
            EnableRenderModelSupport();

            EnsureControllerModel("LeftHand Model", "LeftHand", OVRInput.Controller.LTouch);
            EnsureControllerModel("RightHand Model", "RightHand", OVRInput.Controller.RTouch);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: Controller models setup complete - zeigt die echten Touch " +
                      "Controller vom Geraet, laedt nur auf dem Headset (im Editor/Air Link ohne " +
                      "echte Quest-Runtime bleibt es unsichtbar, kein Fehler).");
        }

        private static void EnsureOVRManager()
        {
            if (Object.FindFirstObjectByType<OVRManager>() != null) return;
            new GameObject("OVRManager").AddComponent<OVRManager>();
        }

        // ohne diesen schalter liefert GetRenderModelPaths eine leere liste
        private static void EnableRenderModelSupport()
        {
            OVRProjectConfig config = OVRProjectConfig.CachedProjectConfig;
            if (config == null || config.renderModelSupport == OVRProjectConfig.RenderModelSupport.Enabled) return;
            config.renderModelSupport = OVRProjectConfig.RenderModelSupport.Enabled;
            OVRProjectConfig.CommitProjectConfig(config);
        }

        // eigenes objekt pro hand mit grip pose, sonst sitzt das modell schief
        private static void EnsureControllerModel(string goName, string handTag, OVRInput.Controller controller)
        {
            var go = GameObject.Find(goName);
            if (go == null)
            {
                var cameraOffset = GameObject.Find("Camera Offset");
                if (cameraOffset == null)
                {
                    Debug.LogError("BioimageVR: 'Camera Offset' nicht gefunden - erst Setup Phase 1 Scene laufen lassen.");
                    return;
                }

                go = new GameObject(goName);
                go.transform.SetParent(cameraOffset.transform, false);

                var pose = go.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
                pose.positionInput = new InputActionProperty(new InputAction(
                    type: InputActionType.Value, binding: $"<XRController>{{{handTag}}}/devicePosition", expectedControlType: "Vector3"));
                pose.rotationInput = new InputActionProperty(new InputAction(
                    type: InputActionType.Value, binding: $"<XRController>{{{handTag}}}/deviceRotation", expectedControlType: "Quaternion"));
            }

            var runtimeController = go.GetComponent<OVRRuntimeController>();
            if (runtimeController == null) runtimeController = go.AddComponent<OVRRuntimeController>();
            runtimeController.m_controller = controller;
        }
    }
}
