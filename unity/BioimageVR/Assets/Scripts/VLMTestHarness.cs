using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // manueller test fuer vlmclient, screenshot plus feste frage
    [RequireComponent(typeof(VLMClient))]
    public class VLMTestHarness : MonoBehaviour
    {
        [SerializeField] private KeyCode triggerKey = KeyCode.Space;
        [SerializeField] private string question =
            "Was siehst du in diesem Mikroskopiebild? Beschreibe sichtbare Strukturen.";
        [SerializeField] private Text responseText;
        [SerializeField] private ChatPanel chatPanel;

        private VLMClient client;
        private bool requestInFlight;

        private void Awake()
        {
            client = GetComponent<VLMClient>();
        }

        private void Update()
        {
            if (requestInFlight || !Input.GetKeyDown(triggerKey)) return;
            StartCoroutine(CaptureAndAsk());
        }

        private IEnumerator CaptureAndAsk()
        {
            requestInFlight = true;
            Debug.Log("[VLM] Nehme Screenshot auf ...");
            chatPanel?.AddUserMessage(question);
            // status text leeren, sonst landet er selbst im screenshot
            if (responseText != null) responseText.text = string.Empty;

            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D screenshot = SceneScreenshot.Capture(Camera.main);

            SetStatus("Asking the assistant ...");
            client.AskAboutImage(screenshot, question,
                onSuccess: answer =>
                {
                    SetStatus(answer);
                    chatPanel?.AddAssistantMessage(answer);
                    Destroy(screenshot);
                    requestInFlight = false;
                },
                onError: error =>
                {
                    SetStatus($"Assistant error: {error}");
                    chatPanel?.AddSystemMessage($"Assistant error: {error}");
                    Destroy(screenshot);
                    requestInFlight = false;
                });
        }

        private void SetStatus(string text)
        {
            Debug.Log($"[VLM] {text}");
            if (responseText != null) responseText.text = text;
        }
    }
}
