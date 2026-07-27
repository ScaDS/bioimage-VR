using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BioimageVR
{
    // manueller test fuer vlmclient, taste oder controller button
    // macht screenshot, feste frage, antwort in text
    // beide eingaben gleichzeitig aktiv, desktop und headset gleiche szene
    //
    // screenshot faengt spectator fenster ein, nicht das echte augenbild
    // bei falscher antwort auf dem geraet stattdessen rendertexture nutzen
    [RequireComponent(typeof(VLMClient))]
    public class VLMTestHarness : MonoBehaviour
    {
        [SerializeField] private KeyCode triggerKey = KeyCode.Space;
        [SerializeField] private string question =
            "Was siehst du in diesem Mikroskopiebild? Beschreibe sichtbare Strukturen.";
        [SerializeField] private Text responseText;
        [SerializeField] private ChatPanel chatPanel;

        private VLMClient client;
        private InputAction controllerAskAction;
        private bool requestInFlight;

        private void Awake()
        {
            client = GetComponent<VLMClient>();
            // rechter primaerknopf, generischer xrcontroller pfad, funktioniert ueberall
            controllerAskAction = new InputAction(
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/primaryButton");
            controllerAskAction.Enable();
        }

        private void OnDestroy()
        {
            controllerAskAction?.Disable();
            controllerAskAction?.Dispose();
        }

        private void Update()
        {
            bool triggered = Input.GetKeyDown(triggerKey)
                              || (controllerAskAction != null && controllerAskAction.WasPressedThisFrame());
            if (requestInFlight || !triggered) return;
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
            Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();

            SetStatus("Frage das VLM ...");
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
                    SetStatus($"Fehler: {error}");
                    chatPanel?.AddSystemMessage($"Fehler: {error}");
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
