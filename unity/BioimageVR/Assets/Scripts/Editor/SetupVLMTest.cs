using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BioimageVR.EditorSetup
{
    // setup fuer vlm test, vlmclient plus vlmtestharness, canvas text fuer antwort
    // sicher erneut ausfuehrbar
    public static class SetupVLMTest
    {
        private const string ScenePath = "Assets/Scenes/Phase1.unity";

        [MenuItem("BioimageVR/Setup VLM Test")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath && File.Exists(ScenePath))
                scene = EditorSceneManager.OpenScene(ScenePath);

            Text responseText = FindOrCreateCanvasText();
            VLMTestHarness harness = FindOrCreateTestObject();
            AssignResponseText(harness, responseText);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: VLM test setup complete - press Play, then Space.");
        }

        private static VLMTestHarness FindOrCreateTestObject()
        {
            var existing = Object.FindFirstObjectByType<VLMTestHarness>();
            if (existing != null) return existing;

            var go = new GameObject("VLMTest");
            go.AddComponent<VLMClient>();
            return go.AddComponent<VLMTestHarness>();
        }

        private static Text FindOrCreateCanvasText()
        {
            var existingGO = GameObject.Find("VLMResponseText");
            if (existingGO != null) return existingGO.GetComponent<Text>();

            Canvas canvas = EditorSetupCommon.FindOrCreateWorldSpaceCanvas();

            var textGO = new GameObject("VLMResponseText", typeof(Text));
            textGO.transform.SetParent(canvas.transform, false);

            var rect = textGO.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0.25f);
            rect.offsetMin = new Vector2(20f, 20f);
            rect.offsetMax = new Vector2(-20f, -20f);

            var text = textGO.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 28;
            text.color = Color.white;
            text.alignment = TextAnchor.LowerLeft;
            text.text = "(VLM: Leertaste druecken)";

            return text;
        }

        private static void AssignResponseText(VLMTestHarness harness, Text text)
        {
            var so = new SerializedObject(harness);
            so.FindProperty("responseText").objectReferenceValue = text;
            so.ApplyModifiedProperties();
        }
    }
}
