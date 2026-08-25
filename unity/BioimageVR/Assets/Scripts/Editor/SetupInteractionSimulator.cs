using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace BioimageVR.EditorSetup
{
    // bringt das xr interaction simulator sample prefab in die szene
    // ersatz fuer den alten legacy xr device simulator, der kein point and click aufs ui kann
    // simuliert kopf und beide controller per maus und tastatur, gleiche xrcontroller
    // lefthand righthand geraeteschicht wie unsere bindings, laeuft daher ohne aenderung
    // auch ohne quest
    // startet deaktiviert, im hierarchy fenster einschalten fuer tests ohne brille
    // fuer echte headset tests wieder aus, sonst konkurriert er um dieselben bindings
    // menu: BioimageVR Setup Interaction Simulator
    public static class SetupInteractionSimulator
    {
        private const string ScenePath = "Assets/Scenes/Phase1.unity";
        private const string PackageId = "com.unity.xr.interaction.toolkit";
        private const string SampleName = "XR Interaction Simulator";
        private const string PrefabFileName = "XR Interaction Simulator.prefab";
        private const string GoName = "XR Interaction Simulator";

        [MenuItem("BioimageVR/Setup Interaction Simulator")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath && File.Exists(ScenePath))
                scene = EditorSceneManager.OpenScene(ScenePath);

            string prefabPath = EnsureSampleImported();
            if (prefabPath == null) return;

            if (GameObject.Find(GoName) == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    Debug.LogError($"BioimageVR: Prefab nicht gefunden unter {prefabPath}.");
                    return;
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.name = GoName;
                instance.SetActive(false);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: XR Interaction Simulator in der Szene, aber deaktiviert. " +
                      "Fuer Tests ohne Brille im Hierarchy-Fenster einschalten (rechte Maustaste " +
                      "haelt Kopf/Controller fest, Package-Doku 'xr-interaction-simulator' fuer die " +
                      "volle Tastenbelegung), fuer echte Headset-Tests wieder ausschalten.");
        }

        private static string EnsureSampleImported()
        {
            PackageInfo packageInfo = PackageInfo.FindForPackageName(PackageId);
            if (packageInfo == null)
            {
                Debug.LogError($"BioimageVR: Package {PackageId} nicht gefunden.");
                return null;
            }

            Sample sample = Sample.FindByPackage(PackageId, packageInfo.version)
                .FirstOrDefault(s => s.displayName == SampleName);
            if (sample.displayName == null)
            {
                Debug.LogError($"BioimageVR: Sample '{SampleName}' nicht im Package {PackageId} {packageInfo.version} gefunden.");
                return null;
            }

            if (!sample.isImported && !sample.Import())
            {
                Debug.LogError($"BioimageVR: Import von Sample '{SampleName}' fehlgeschlagen.");
                return null;
            }

            string prefabPath = $"Assets/Samples/{packageInfo.displayName}/{packageInfo.version}/{SampleName}/{PrefabFileName}";
            if (!File.Exists(prefabPath))
            {
                Debug.LogError($"BioimageVR: erwartetes Prefab nicht gefunden unter {prefabPath}.");
                return null;
            }

            return prefabPath;
        }
    }
}
