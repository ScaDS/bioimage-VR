using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BioimageVR
{
    // push to talk, trigger halten und sprechen, loslassen zum stoppen
    // aufnahme geht an sttclient, text plus screenshot an vlmclient
    // gleiches muster wie vlmtestharness aber index trigger statt a knopf
    [RequireComponent(typeof(VLMClient))]
    [RequireComponent(typeof(STTClient))]
    public class VoiceVLMHarness : MonoBehaviour
    {
        [SerializeField] private KeyCode triggerKey = KeyCode.V;
        [SerializeField] private Text responseText;
        [SerializeField] private ChatPanel chatPanel;
        [SerializeField] private int maxRecordSeconds = 10;
        [SerializeField] private int sampleRate = 16000;

        private VLMClient vlmClient;
        private STTClient sttClient;
        private InputAction controllerRecordAction;
        private AudioClip recordingClip;
        private bool isRecording;
        private bool requestInFlight;

        private void Awake()
        {
            vlmClient = GetComponent<VLMClient>();
            sttClient = GetComponent<STTClient>();
            // index trigger, nicht a knopf, der ist schon fuer vlmtestharness belegt
            controllerRecordAction = new InputAction(
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/triggerButton");
            controllerRecordAction.Enable();
        }

        private void OnDestroy()
        {
            controllerRecordAction?.Disable();
            controllerRecordAction?.Dispose();
        }

        private void Update()
        {
            if (requestInFlight) return;

            bool down = Input.GetKeyDown(triggerKey)
                        || (controllerRecordAction != null && controllerRecordAction.WasPressedThisFrame());
            bool up = Input.GetKeyUp(triggerKey)
                      || (controllerRecordAction != null && controllerRecordAction.WasReleasedThisFrame());

            if (down && !isRecording) StartRecording();
            else if (up && isRecording) StopRecordingAndAsk();
        }

        private void StartRecording()
        {
            if (Microphone.devices.Length == 0)
            {
                SetStatus("Kein Mikrofon gefunden.");
                return;
            }

            isRecording = true;
            SetStatus("Aufnahme läuft ...");
            recordingClip = Microphone.Start(null, false, maxRecordSeconds, sampleRate);
        }

        private void StopRecordingAndAsk()
        {
            isRecording = false;
            int recordedSamples = Microphone.GetPosition(null);
            Microphone.End(null);

            if (recordingClip == null || recordedSamples <= 0)
            {
                SetStatus("Keine Aufnahme (zu kurz oder kein Mikrofon).");
                return;
            }

            StartCoroutine(TranscribeCaptureAndAsk(TrimClip(recordingClip, recordedSamples)));
        }

        private IEnumerator TranscribeCaptureAndAsk(AudioClip clip)
        {
            requestInFlight = true;
            SetStatus("Transkribiere Frage ...");

            string question = null;
            string sttError = null;
            sttClient.Transcribe(clip,
                onSuccess: text => question = text,
                onError: err => sttError = err);

            yield return new WaitUntil(() => question != null || sttError != null);
            Destroy(clip);

            if (sttError != null)
            {
                SetStatus($"STT-Fehler: {sttError}");
                chatPanel?.AddSystemMessage($"STT-Fehler: {sttError}");
                requestInFlight = false;
                yield break;
            }
            if (string.IsNullOrWhiteSpace(question))
            {
                SetStatus("Keine Sprache erkannt.");
                chatPanel?.AddSystemMessage("Keine Sprache erkannt.");
                requestInFlight = false;
                yield break;
            }

            chatPanel?.AddUserMessage(question);
            SetStatus($"Frage: \"{question}\" - nehme Screenshot auf ...");
            // frame warten, sonst landet der statustext im screenshot
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();

            SetStatus($"Frage: \"{question}\" - frage das VLM ...");
            vlmClient.AskAboutImage(screenshot, question,
                onSuccess: answer =>
                {
                    SetStatus($"F: {question}\nA: {answer}");
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

        // microphone reserviert volle laenge im voraus, hier auf echte samples kuerzen
        private static AudioClip TrimClip(AudioClip source, int samples)
        {
            samples = Mathf.Min(samples, source.samples);
            float[] data = new float[samples * source.channels];
            source.GetData(data, 0);

            AudioClip trimmed = AudioClip.Create(
                "question", samples, source.channels, source.frequency, false);
            trimmed.SetData(data, 0);
            return trimmed;
        }

        private void SetStatus(string text)
        {
            Debug.Log($"[VoiceVLM] {text}");
            if (responseText != null) responseText.text = text;
        }
    }
}
