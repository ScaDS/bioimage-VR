using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BioimageVR
{
    // push to talk, aufnahme an stt, text plus napari bild plus zeiger und rag kontext ans vlm
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
        private VrBridge bridge;
        private CellInspector inspector;

        // was beim druecken der sprechtaste zu sehen war und worauf gezeigt wurde
        private class Moment
        {
            public int Pointed;
            public Texture2D Shot;
            public int NapariPointed;
            public bool ShotDone;
            public bool Dropped;
        }
        private Moment moment;

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
            bridge = FindFirstObjectByType<VrBridge>();
            if (volumeView != null) inspector = volumeView.GetComponent<CellInspector>();
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
                SetStatus("No microphone found.");
                return;
            }

            isRecording = true;
            SetStatus("Listening ...");
            recordingClip = Microphone.Start(null, false, maxRecordSeconds, sampleRate);
            moment = Capture();
        }

        // napari laedt sein bild waehrend gesprochen wird, die frage wartet danach kaum noch
        private Moment Capture()
        {
            var m = new Moment { Pointed = inspector != null ? inspector.PointedLabel() : 0 };
            if (bridge == null)
            {
                m.ShotDone = true;
                return m;
            }
            StartCoroutine(bridge.NapariShot((shot, pointed) =>
            {
                if (m.Dropped && shot != null) Destroy(shot);
                else m.Shot = shot;
                m.NapariPointed = pointed;
                m.ShotDone = true;
            }));
            return m;
        }

        private void Drop(Moment m)
        {
            m.Dropped = true;
            if (m.Shot != null) Destroy(m.Shot);
            m.Shot = null;
        }

        private void StopRecordingAndAsk()
        {
            isRecording = false;
            int recordedSamples = Microphone.GetPosition(null);
            Microphone.End(null);

            if (recordingClip == null || recordedSamples <= 0)
            {
                SetStatus("Nothing recorded, too short or no microphone.");
                Drop(moment);
                return;
            }

            StartCoroutine(TranscribeCaptureAndAsk(TrimClip(recordingClip, recordedSamples), moment));
        }

        private IEnumerator TranscribeCaptureAndAsk(AudioClip clip, Moment m)
        {
            requestInFlight = true;
            SetStatus("Transcribing ...");

            string question = null;
            string sttError = null;
            sttClient.Transcribe(clip,
                onSuccess: text => question = text,
                onError: err => sttError = err);

            yield return new WaitUntil(() => question != null || sttError != null);
            Destroy(clip);

            if (sttError != null)
            {
                SetStatus($"Speech recognition failed: {sttError}");
                chatPanel?.AddSystemMessage($"Speech recognition failed: {sttError}");
                Drop(m);
                requestInFlight = false;
                yield break;
            }
            if (string.IsNullOrWhiteSpace(question))
            {
                SetStatus("No speech recognised.");
                chatPanel?.AddSystemMessage("No speech recognised.");
                Drop(m);
                requestInFlight = false;
                yield break;
            }

            chatPanel?.AddUserMessage(question);

            string context = null;
            int? currentImageId = CurrentIdrImageId();
            if (currentImageId.HasValue)
            {
                SetStatus($"\"{question}\" · looking up context ...");
                bool ragDone = false;
                ragClient.FetchContext(question, currentImageId.Value, (result, error) =>
                {
                    if (error != null) Debug.LogWarning($"[VoiceVLM] RAG kontext fehlgeschlagen: {error}");
                    else context = result;
                    ragDone = true;
                });
                yield return new WaitUntil(() => ragDone);
            }

            SetStatus($"\"{question}\" · capturing the view ...");
            yield return new WaitUntil(() => m.ShotDone);
            Texture2D screenshot = m.Shot;
            bool fromNapari = screenshot != null;
            if (!fromNapari)
            {
                // napari laeuft nicht, dann das bild aus der app. frame warten, sonst landet der statustext darin
                yield return null;
                yield return new WaitForEndOfFrame();
                screenshot = SceneScreenshot.Capture(Camera.main);
            }
            string situation = Describe(m, fromNapari);
            context = string.IsNullOrEmpty(context) ? situation : situation + "\n\n" + context;

            SetStatus($"\"{question}\" · asking the assistant ...");
            vlmClient.AskAboutImage(screenshot, question, context, VLMToolDispatcher.Tools,
                onTextAnswer: answer =>
                {
                    SetStatus("Answer is in the chat");
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
                    SetStatus("Answer is in the chat");
                    chatPanel?.AddAssistantMessage(summary);
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

        // bildquelle, zeiger und auswahl, steht immer vor dem rag kontext
        private string Describe(Moment m, bool fromNapari)
        {
            var lines = new List<string>
            {
                fromNapari
                    ? "The image is the napari canvas on the PC, showing the same volume as the user's VR view."
                    : "The image is the user's view in VR."
            };
            if (volumeView != null && volumeView.HasLabels)
            {
                lines.Add(m.Pointed > 0
                    ? $"The user is pointing at {Cell(m.Pointed)} in the 3D view."
                    : "The user is not pointing at a segmented cell in the 3D view.");
                if (fromNapari && m.NapariPointed > 0 && m.NapariPointed != m.Pointed)
                    lines.Add($"The mouse in napari is on {Cell(m.NapariPointed)}.");
                if (volumeView.SelectedLabel > 0)
                    lines.Add($"Selected: {Cell(volumeView.SelectedLabel)}.");
            }
            return string.Join("\n", lines);
        }

        private string Cell(int id) => $"{volumeView.LabelName(id)} (label {id})";

        // idr bild id aus dem dateinamen idr_<id>.nii.gz
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

        // letzte statusmeldung, die Assistant hinweiszeile am volumen zeigt sie an
        public string Status { get; private set; } = "";
        public float StatusTime { get; private set; }

        private void SetStatus(string text)
        {
            Status = text;
            StatusTime = Time.time;
            Debug.Log($"[VoiceVLM] {text}");
            if (responseText != null) responseText.text = text;
        }
    }
}
