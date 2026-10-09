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
        [SerializeField] private float viewInterval = 0.05f;
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
            // welche ansicht aus napari schon uebernommen ist, siehe ApplyNapariView
            public long napariSeq;
        }

        // antwort auf den view post, napari schickt richtungen im selben volumen raum zurueck
        [Serializable] private class ViewResponse { public NapariView napari; }
        [Serializable] private class NapariView
        {
            public long seq;
            public Vector3 camForward;
            public Vector3 camUp;
            // unter 0 heisst auswahl nicht anfassen
            public int selected = -1;
            // alle zoom schritte in napari seit zoomId aufmultipliziert, 0 heisst groesse nicht anfassen
            public float zoom;
            public long zoomId;
        }

        private long appliedNapariSeq;
        // zieldrehung aus napari, wird weich angefahren statt gesprungen
        private Quaternion napariTarget;
        private bool hasNapariTarget;
        // letzter zoom stand aus napari, nur das verhaeltnis zum neuen wird angewendet.
        // so bleibt zoomen in der brille erhalten und verlorene nachrichten schaden nicht
        private long napariZoomId;
        private float lastNapariZoom;
        // faktor der noch aussteht, wird wie die drehung weich angefahren
        private float napariZoomLeft = 1f;

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
                    if (ok) ApplyNapariView(request.downloadHandler.text, cam);
                }
                yield return new WaitForSeconds(ok ? viewInterval : retryInterval);
            }
        }

        private void Update()
        {
            float blend = 1f - Mathf.Exp(-14f * Time.deltaTime);
            if (!Mathf.Approximately(napariZoomLeft, 1f))
            {
                float step = Mathf.Abs(napariZoomLeft - 1f) < 0.002f ? napariZoomLeft : Mathf.Pow(napariZoomLeft, blend);
                volumeView.GetComponent<VolumeZoom>()?.ZoomBy(step);
                napariZoomLeft /= step;
            }

            if (!hasNapariTarget) return;
            Transform volume = volumeView.transform;
            volume.rotation = Quaternion.Slerp(volume.rotation, napariTarget, blend);
            if (Quaternion.Angle(volume.rotation, napariTarget) < 0.2f)
            {
                volume.rotation = napariTarget;
                hasNapariTarget = false;
            }
        }

        // in napari gedreht: volumen um die eigene mitte drehen bis man es aus derselben
        // richtung sieht. in napari gezoomt: um denselben faktor skalieren. der ort bleibt,
        // dort gewaehlte zelle auch hier waehlen
        private void ApplyNapariView(string json, Camera cam)
        {
            NapariView view;
            try
            {
                view = JsonUtility.FromJson<ViewResponse>(json)?.napari;
            }
            catch (Exception)
            {
                return;
            }
            if (view == null || view.seq <= appliedNapariSeq) return;
            appliedNapariSeq = view.seq;
            if (view.selected >= 0 && volumeView.HasLabels) volumeView.SelectedLabel = view.selected;

            // beim fliegen gehoert die lage dem flug
            var fly = volumeView.GetComponent<VolumeFly>();
            bool flying = fly != null && fly.Active;
            if (view.zoom > 0f)
            {
                // neue zoomId heisst napari zaehlt von vorn, dann nur den stand merken
                if (!flying && view.zoomId == napariZoomId && lastNapariZoom > 0f)
                    napariZoomLeft *= view.zoom / lastNapariZoom;
                napariZoomId = view.zoomId;
                lastNapariZoom = view.zoom;
            }
            if (flying)
            {
                hasNapariTarget = false;
                napariZoomLeft = 1f;
                return;
            }
            if (view.camForward.sqrMagnitude < 1e-6f || view.camUp.sqrMagnitude < 1e-6f) return;

            Transform volume = volumeView.transform;
            // richtungen kommen im einheitswuerfel, erst die achsen skalierung zurueckrechnen
            Vector3 scale = volume.localScale;
            Vector3 forwardLocal = Vector3.Scale(view.camForward, scale).normalized;
            Vector3 upLocal = Vector3.Scale(view.camUp, scale);
            upLocal = (upLocal - forwardLocal * Vector3.Dot(upLocal, forwardLocal)).normalized;

            Vector3 forwardWorld = volume.position - cam.transform.position;
            if (forwardWorld.sqrMagnitude < 1e-6f) forwardWorld = cam.transform.forward;
            forwardWorld.Normalize();
            Vector3 upWorld = Vector3.up - forwardWorld * Vector3.Dot(Vector3.up, forwardWorld);
            if (upWorld.sqrMagnitude < 1e-6f) upWorld = cam.transform.up;

            napariTarget = Quaternion.LookRotation(forwardWorld, upWorld.normalized)
                           * Quaternion.Inverse(Quaternion.LookRotation(forwardLocal, upLocal));
            hasNapariTarget = true;
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
                napariSeq = appliedNapariSeq,
            };
        }
    }
}
