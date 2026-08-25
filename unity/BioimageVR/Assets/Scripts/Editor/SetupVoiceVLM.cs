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

            // dispatcher fuers tool-calling (siehe VLMToolDispatcher.cs, CHATMICROSCOPY.md
            // 3.2) - bindet an die heute gebauten regler, damit gesprochene befehle wie
            // "mach heller" die slider im panel direkt mitbewegen
            VLMToolDispatcher toolDispatcher = go.GetComponent<VLMToolDispatcher>();
            if (toolDispatcher == null)
                toolDispatcher = go.AddComponent<VLMToolDispatcher>();

            var dispatcherSo = new SerializedObject(toolDispatcher);
            dispatcherSo.FindProperty("contrastControl").objectReferenceValue = Object.FindFirstObjectByType<VolumeContrastControl>();
            dispatcherSo.FindProperty("renderControls").objectReferenceValue = Object.FindFirstObjectByType<VolumeRenderControls>();
            dispatcherSo.ApplyModifiedProperties();

            // rag kontext fuers gerade geladene bild (siehe RagClient.cs, CHATMICROSCOPY.md
            // rag umbau) - server adresse bleibt erhalten, dieses objekt wird hier nur
            // gefunden/ergaenzt, nie zerstoert/neu gebaut wie das seitenpanel
            RagClient ragClient = go.GetComponent<RagClient>();
            if (ragClient == null)
                ragClient = go.AddComponent<RagClient>();

            Text responseText = GameObject.Find("VLMResponseText")?.GetComponent<Text>();
            var so = new SerializedObject(voiceHarness);
            if (responseText != null)
                so.FindProperty("responseText").objectReferenceValue = responseText;
            so.FindProperty("toolDispatcher").objectReferenceValue = toolDispatcher;
            so.FindProperty("volumeView").objectReferenceValue = Object.FindFirstObjectByType<VolumeView>();
            so.ApplyModifiedProperties();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: Voice VLM setup complete - press Play, hold V (or right controller " +
                      "index trigger) while speaking, then release. Gesprochene Befehle wie 'erhoeh den " +
                      "Kontrast' steuern jetzt direkt die Render-Regler (Tool-Calling). Neu: RagClient " +
                      "Komponente am selben Objekt holt vorm Fragen Kontext zum aktuell geladenen Bild - " +
                      "'Server Base Url' dort auf die von preprocessing/upload_server.py angezeigte " +
                      "WLAN-Adresse setzen (Server muss laufen, sonst wird die Frage ohne Kontext gestellt).");
        }
    }
}
