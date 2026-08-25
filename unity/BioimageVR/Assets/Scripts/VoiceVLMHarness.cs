using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BioimageVR
{
    // push to talk, trigger halten und sprechen, loslassen zum stoppen
    // aufnahme geht an sttclient, text plus screenshot an vlmclient
    // gleiches muster wie vlmtestharness aber index trigger statt a knopf
    // per tool-calling kann eine gesprochene anweisung ("mach mal heller") auch direkt
    // die render-regler bedienen statt nur eine textantwort zu bekommen, siehe
    // VLMToolDispatcher.cs und CHATMICROSCOPY.md abschnitt 3.2
    // rag kontext (RagClient.cs) fuers gerade geladene bild wird vor der vlm anfrage
    // geholt und als zusaetzlicher context string mitgegeben, siehe plan zum rag umbau
    [RequireComponent(typeof(VLMClient))]
    [RequireComponent(typeof(STTClient))]
    [RequireComponent(typeof(RagClient))]
    public class VoiceVLMHarness : MonoBehaviour
    {
        [SerializeField] private KeyCode triggerKey = KeyCode.V;
        [SerializeField] private Text responseText;
        [SerializeField] private ChatPanel chatPanel;
        [SerializeField] private VLMToolDispatcher toolDispatcher;
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private int maxRecordSeconds = 10;
        [SerializeField] private int sampleRate = 16000;

        private static readonly Regex IdrIdPattern = new Regex(@"idr_(\d+)");

        private VLMClient vlmClient;
        private STTClient sttClient;
        private RagClient ragClient;
        private InputAction controllerRecordAction;
        private AudioClip recordingClip;
        private bool isRecording;
        private bool requestInFlight;

        private void Awake()
        {
            vlmClient = GetComponent<VLMClient>();
            sttClient = GetComponent<STTClient>();
            ragClient = GetComponent<RagClient>();
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

            string context = null;
            int? currentImageId = CurrentIdrImageId();
            if (currentImageId.HasValue)
            {
                SetStatus($"Frage: \"{question}\" - hole Kontext ...");
                bool ragDone = false;
                ragClient.FetchContext(question, currentImageId.Value, (result, error) =>
                {
                    if (error != null) Debug.LogWarning($"[VoiceVLM] RAG kontext fehlgeschlagen: {error}");
                    else context = result;
                    ragDone = true;
                });
                yield return new WaitUntil(() => ragDone);
            }

            SetStatus($"Frage: \"{question}\" - nehme Screenshot auf ...");
            // frame warten, sonst landet der statustext im screenshot
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();

            SetStatus($"Frage: \"{question}\" - frage das VLM ...");
            vlmClient.AskAboutImage(screenshot, question, context, VLMToolDispatcher.Tools,
                onTextAnswer: answer =>
                {
                    SetStatus($"F: {question}\nA: {answer}");
                    chatPanel?.AddAssistantMessage(answer);
                    Destroy(screenshot);
                    requestInFlight = false;
                },
                onToolCalls: calls =>
                {
                    var confirmations = new List<string>(calls.Count);
                    foreach (ToolCall call in calls)
                        confirmations.Add(toolDispatcher != null ? toolDispatcher.Execute(call) : $"({call.Name} nicht verkabelt)");
                    string summary = string.Join(" ", confirmations);
                    SetStatus($"F: {question}\nA: {summary}");
                    chatPanel?.AddAssistantMessage(summary);
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

        // liest die idr bild-id aus dem dateinamen des gerade geladenen volumens,
        // gleiche idr_<id>.nii.gz konvention wie fetch_from_idr.fetch_one/IdrClient
        private int? CurrentIdrImageId()
        {
            if (volumeView == null || string.IsNullOrEmpty(volumeView.VolumeFilePath)) return null;
            Match match = IdrIdPattern.Match(System.IO.Path.GetFileName(volumeView.VolumeFilePath));
            return match.Success ? int.Parse(match.Groups[1].Value) : (int?)null;
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
