using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BioimageVR
{
    // gegenstueck zu preprocessing/vr_bridge.py, verbindung zum napari plugin
    // fragt alle paar sekunden nach, ob aus napari etwas neues kam, und oeffnet es direkt
    // schickt ein paar mal pro sekunde blick und auswahl, napari spiegelt die ansicht
    // server adresse kommt aus IdrClient, nur dort eintragen
    // klappt die nicht, wird localhost probiert, das geht per usb kabel mit adb reverse
    // (richtet upload_server.py beim start ein oder der usb knopf im napari plugin)
    public class VrBridge : MonoBehaviour
    {
        [SerializeField] private IdrClient idrClient;
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private ChatPanel chatPanel;
        [SerializeField] private float pollInterval = 2f;
        [SerializeField] private float viewInterval = 0.1f;
        // server nicht erreichbar, dann seltener probieren statt jede zehntelsekunde
        [SerializeField] private float retryInterval = 5f;

        // was napari zuletzt geschickt hat, beim start nur merken statt altes zu oeffnen
        private long lastSeq = -1;

        [Serializable] private class PendingResponse
        {
            public long seq;
            public string name;
            public bool has_labels;
        }

        // positionen und richtungen im volumen raum, wuerfel von -0.5 bis 0.5 je achse
        // so passt es unabhaengig von zoom und drehung auf die daten in napari
        [Serializable] private class ViewState
        {
            public string name;
            public Vector3 camPos;
            public Vector3 camForward;
            public Vector3 camUp;
            public int hover;
            public int selected;
            public bool labelsShown;
        }

        private const string UsbUrl = "http://localhost:8000";

        // welche adresse gerade antwortet, 0 wlan aus IdrClient, 1 usb
        private int activeCandidate;
        private bool connected;

        private string Candidate(int index)
        {
            string wlan = idrClient != null ? idrClient.ServerBaseUrl : null;
            if (index == 0 && !string.IsNullOrEmpty(wlan)) return wlan;
            return UsbUrl;
        }

        private string BaseUrl => connected ? Candidate(activeCandidate) : null;

        private void OnEnable()
        {
            StartCoroutine(PollLoop());
            StartCoroutine(ViewLoop());
        }

        // beide adressen reihum probieren bis eine antwortet, dann bei der bleiben
        private IEnumerator PollLoop()
        {
            int failuresInRow = 0;
            while (true)
            {
                string url = Candidate(activeCandidate);
                using (var request = UnityWebRequest.Get($"{url}/vr/pending"))
                {
                    request.timeout = 3;
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        if (!connected)
                        {
                            connected = true;
                            string via = url == UsbUrl ? "USB cable" : "Wi-Fi";
                            chatPanel?.AddSystemMessage($"Connected to the PC via {via}");
                        }
                        failuresInRow = 0;
                        yield return HandlePending(request.downloadHandler.text);
                        yield return new WaitForSeconds(pollInterval);
                        continue;
                    }
                }

                connected = false;
                activeCandidate = 1 - activeCandidate;
                failuresInRow++;
                // erst nach einem vollen durchlauf ueber beide adressen laenger warten
                yield return new WaitForSeconds(failuresInRow % 2 == 0 ? retryInterval : 0.5f);
            }
        }

        private IEnumerator HandlePending(string json)
        {
            PendingResponse pending;
            try
            {
                pending = JsonUtility.FromJson<PendingResponse>(json);
            }
            catch (Exception)
            {
                yield break;
            }
            if (pending == null) yield break;

            if (lastSeq < 0)
            {
                lastSeq = pending.seq;
                yield break;
            }
            if (pending.seq <= lastSeq || string.IsNullOrEmpty(pending.name)) yield break;
            lastSeq = pending.seq;

            chatPanel?.AddSystemMessage($"From napari: loading {pending.name} ...");

            // flach in persistentDataPath, auf android sucht VolumeView nur dort
            // und die galerie listet es danach auch
            string dir = Application.persistentDataPath;
            string volumePath = Path.Combine(dir, $"{pending.name}.nii.gz");
            string labelsPath = Path.Combine(dir, $"{pending.name}_labels.nii.gz");
            string metadataPath = Path.Combine(dir, $"{pending.name}_metadata.json");

            string error = null;
            yield return Download(pending.name, "volume", volumePath, e => error = e);
            if (error == null) yield return Download(pending.name, "metadata", metadataPath, _ => { });
            if (error == null && pending.has_labels) yield return Download(pending.name, "labels", labelsPath, e => error = e);
            // alte maske gleichen namens darf nicht an einem bild ohne maske haengen bleiben
            if (error == null && !pending.has_labels && File.Exists(labelsPath)) File.Delete(labelsPath);

            if (error != null)
            {
                chatPanel?.AddSystemMessage($"Loading from napari failed: {error}");
                yield break;
            }

            volumeView.LoadVolume(volumePath);
        }

        private IEnumerator Download(string name, string kind, string targetPath, Action<string> onError)
        {
            string url = $"{BaseUrl}/vr/file?name={UnityWebRequest.EscapeURL(name)}&kind={kind}";
            using var request = UnityWebRequest.Get(url);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                onError($"{kind}: {request.error}");
                yield break;
            }
            File.WriteAllBytes(targetPath, request.downloadHandler.data);
        }

        // korrigierte maske als neue version an den pc, napari holt sie mit From VR
        // callback bekommt null bei erfolg, sonst die fehlermeldung
        public void UploadLabels(string name, byte[] niftiGz, Action<string> done)
        {
            if (string.IsNullOrEmpty(BaseUrl))
            {
                done?.Invoke("PC not reachable, neither Wi-Fi nor USB");
                return;
            }
            StartCoroutine(UploadLabelsCoroutine(name, niftiGz, done));
        }

        private IEnumerator UploadLabelsCoroutine(string name, byte[] niftiGz, Action<string> done)
        {
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", niftiGz, $"{name}_labels.nii.gz", "application/octet-stream")
            };
            using var request = UnityWebRequest.Post($"{BaseUrl}/vr/labels?name={UnityWebRequest.EscapeURL(name)}", form);
            request.timeout = 60;
            yield return request.SendWebRequest();
            done?.Invoke(request.result == UnityWebRequest.Result.Success ? null : request.error);
        }

        private IEnumerator ViewLoop()
        {
            while (true)
            {
                bool ok = true;
                Camera cam = Camera.main;
                if (!string.IsNullOrEmpty(BaseUrl) && cam != null && volumeView.LoadedVolume != null)
                {
                    using var request = new UnityWebRequest($"{BaseUrl}/vr/view", "POST");
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(CurrentView(cam))));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = 2;
                    yield return request.SendWebRequest();
                    ok = request.result == UnityWebRequest.Result.Success;
                }
                yield return new WaitForSeconds(ok ? viewInterval : retryInterval);
            }
        }

        private ViewState CurrentView(Camera cam)
        {
            Transform volume = volumeView.transform;
            string path = volumeView.VolumeFilePath ?? "";
            // richtungen mit InverseTransformVector, damit die achsen skalierung
            // wie bei den positionen mit rausgerechnet wird
            return new ViewState
            {
                name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)),
                camPos = volume.InverseTransformPoint(cam.transform.position),
                camForward = volume.InverseTransformVector(cam.transform.forward).normalized,
                camUp = volume.InverseTransformVector(cam.transform.up).normalized,
                hover = volumeView.HoverLabel,
                selected = volumeView.SelectedLabel,
                labelsShown = volumeView.ShowLabels,
            };
        }
    }
}
