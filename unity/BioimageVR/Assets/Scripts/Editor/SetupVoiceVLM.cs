using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BioimageVR.EditorSetup
{
    // setup fuer voice vlm, haengt sttclient und voicevlmharness ans vlmtest objekt
    // gleicher response text wie beim normalen vlm test, sicher erneut ausfuehrbar
    public static class SetupVoiceVLM
    {
        private const string ScenePath = "Assets/Scenes/Phase1.unity";

        [MenuItem("BioimageVR/Setup Voice VLM")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath && File.Exists(ScenePath))
                scene = EditorSceneManager.OpenScene(ScenePath);

            var harness = Object.FindFirstObjectByType<VLMTestHarness>();
            if (harness == null)
            {
                Debug.LogError("BioimageVR: no VLMTestHarness found in the scene - run " +
                                "BioimageVR/Setup VLM Test first.");
                return;
            }

            GameObject go = harness.gameObject;
            if (go.GetComponent<STTClient>() == null)
                go.AddComponent<STTClient>();

            VoiceVLMHarness voiceHarness = go.GetComponent<VoiceVLMHarness>();
            if (voiceHarness == null)
                voiceHarness = go.AddComponent<VoiceVLMHarness>();

            Text responseText = GameObject.Find("VLMResponseText")?.GetComponent<Text>();
            if (responseText != null)
            {
                var so = new SerializedObject(voiceHarness);
                so.FindProperty("responseText").objectReferenceValue = responseText;
                so.ApplyModifiedProperties();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: Voice VLM setup complete - press Play, hold V (or right controller " +
                      "index trigger) while speaking, then release.");
        }
    }
}
