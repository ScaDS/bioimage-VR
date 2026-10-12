using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BioimageVR
{
    // gegenstueck zu vr_bridge.py, holt neues aus napari, schickt blick und auswahl, localhost als ausweg per usb
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
            // bildschirm modus ist an, napari soll sein fenster schicken
            public bool screenWanted;
            // groesse des volumens, napari zoomt im selben verhaeltnis mit
            public float volScale;
            // gewuenschte groesse des napari fensters, 1 klein bis 3 gross, windowSeq zaehlt je knopfdruck
            public int windowSize;
            public long windowSeq;
        }

        // antwort auf den view post, napari schickt richtungen im selben volumen raum zurueck
        [Serializable] private class ViewResponse
        {
            public NapariView napari;
            // fuer dieses bild liegen in napari gemalte ausschnitte der maske bis zu dieser nummer bereit
            public string patchName;
            public long patchSeq;
        }
        [Serializable] private class NapariView
        {
            public long seq;
            // steigt nur wenn sich die blickrichtung aendert, nicht beim mauszeiger allein
            public long camSeq;
            public Vector3 camForward;
            public Vector3 camUp;
            // unter 0 heisst auswahl nicht anfassen
            public int selected = -1;
            // alle zoom schritte in napari seit zoomId aufmultipliziert, 0 heisst groesse nicht anfassen
            public float zoom;
            public long zoomId;
            // mauszeiger in napari als strahl durchs volumen, anfang und ende im volumen raum
            public bool ray;
            public Vector3 rayStart;
            public Vector3 rayEnd;
        }

        private long appliedNapariSeq;
        private long appliedCamSeq;
        // in napari gemalte ausschnitte der maske, welche schon eingesetzt sind
        private long appliedPatchSeq;
        // frisch geladenes bild faengt beim stand des servers an, aeltere ausschnitte gehoeren nicht dazu
        private bool patchBaseline = true;
        private bool patchBusy;
        // zieldrehung aus napari, wird weich angefahren statt gesprungen
        private Quaternion napariTarget;
        private bool hasNapariTarget;
        // letzter zoom stand aus napari, nur das verhaeltnis zum neuen wird angewendet
        private long napariZoomId;
        private float lastNapariZoom;
        // faktor der noch aussteht, wird wie die drehung weich angefahren
        private float napariZoomLeft = 1f;

        // linie fuer den mauszeiger aus napari, verschwindet wenn die maus eine weile ruht
        private const float MouseRaySeconds = 4f;
        private const float MouseRayWidth = 0.003f;
        // die linie beginnt so weit vor dem kopf, wie bei einem controller in der hand
        private const float MouseRayHeadGap = 0.3f;
        private LineRenderer mouseRay;
        private Vector3 rayStartLocal, rayEndLocal;
        private float rayUntil;

        public ScreenMirror Mirror { get; private set; }
        public string ServerUrl => BaseUrl;

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

        private void Awake()
        {
            Mirror = gameObject.AddComponent<ScreenMirror>();
            Mirror.Init(this, volumeView.transform);
            volumeView.OnVolumeLoaded += _ => patchBaseline = true;
        }

        // im bildschirm modus zaehlt der feste standpunkt statt des kopfs, siehe ScreenMirror.ViewPoint
        private Vector3 ViewerPosition(Camera cam) =>
            Mirror != null && Mirror.Active ? Mirror.ViewPoint : cam.transform.position;

        private string VolumeName()
        {
            string path = volumeView.VolumeFilePath ?? "";
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
        }

        private void CheckPatches(ViewResponse response)
        {
            string name = VolumeName();
            if (response == null || string.IsNullOrEmpty(name) || response.patchName != name)
            {
                // fuer dieses bild liegt nichts vor, was spaeter kommt ist neu
                if (response != null)
                {
                    patchBaseline = false;
                    appliedPatchSeq = 0;
                }
                return;
            }
            if (patchBaseline)
            {
                patchBaseline = false;
                appliedPatchSeq = response.patchSeq;
                return;
            }
            if (response.patchSeq > appliedPatchSeq && !patchBusy) StartCoroutine(FetchPatches(name));
        }

        // der reihe nach holen bis der server nichts mehr hat
        private IEnumerator FetchPatches(string name)
        {
            patchBusy = true;
            var editor = volumeView.GetComponent<LabelEditor>();
            while (editor != null && VolumeName() == name && !patchBaseline)
            {
                string url = $"{BaseUrl}/vr/labels_patch?name={UnityWebRequest.EscapeURL(name)}&after={appliedPatchSeq}";
                using var request = UnityWebRequest.Get(url);
                request.timeout = 10;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200) break;
                string[] box = (request.GetResponseHeader("X-Box") ?? "").Split(',');
                if (!long.TryParse(request.GetResponseHeader("X-Seq"), out long seq) || box.Length != 6) break;
                var v = new int[6];
                bool parsed = true;
                for (int i = 0; i < 6; i++) parsed &= int.TryParse(box[i], out v[i]);
                appliedPatchSeq = seq;
                // bild wurde inzwischen gewechselt, dann gehoert der ausschnitt nicht mehr hierher
                if (parsed && VolumeName() == name)
                    editor.ApplyPatch(new Vector3Int(v[0], v[1], v[2]), new Vector3Int(v[3], v[4], v[5]), request.downloadHandler.data);
            }
            patchBusy = false;
        }

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

        // korrigierte maske als neue version an den pc, callback bekommt null bei erfolg
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

        // in napari gedreht oder gezoomt: volumen um die eigene mitte drehen und skalieren, ort bleibt
        private void ApplyNapariView(string json, Camera cam)
        {
            NapariView view;
            try
            {
                ViewResponse response = JsonUtility.FromJson<ViewResponse>(json);
                CheckPatches(response);
                view = response?.napari;
            }
            catch (Exception)
            {
                return;
            }
            if (view == null || view.seq <= appliedNapariSeq) return;
            appliedNapariSeq = view.seq;
            if (view.selected >= 0 && volumeView.HasLabels) volumeView.SelectedLabel = view.selected;
            rayStartLocal = view.rayStart;
            rayEndLocal = view.rayEnd;
            rayUntil = view.ray ? Time.time + MouseRaySeconds : 0f;

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
            // mauszeiger allein dreht nichts, sonst zieht jede kopfbewegung das volumen nach
            bool turned = view.camSeq > appliedCamSeq;
            appliedCamSeq = view.camSeq;
            if (flying)
            {
                hasNapariTarget = false;
                napariZoomLeft = 1f;
                return;
            }
            if (!turned) return;
            if (view.camForward.sqrMagnitude < 1e-6f || view.camUp.sqrMagnitude < 1e-6f) return;

            Transform volume = volumeView.transform;
            // richtungen kommen im einheitswuerfel, erst die achsen skalierung zurueckrechnen
            Vector3 scale = volume.localScale;
            Vector3 forwardLocal = Vector3.Scale(view.camForward, scale).normalized;
            Vector3 upLocal = Vector3.Scale(view.camUp, scale);
            upLocal = (upLocal - forwardLocal * Vector3.Dot(upLocal, forwardLocal)).normalized;

            Vector3 forwardWorld = volume.position - ViewerPosition(cam);
            if (forwardWorld.sqrMagnitude < 1e-6f) forwardWorld = cam.transform.forward;
            forwardWorld.Normalize();
            Vector3 upWorld = Vector3.up - forwardWorld * Vector3.Dot(Vector3.up, forwardWorld);
            if (upWorld.sqrMagnitude < 1e-6f) upWorld = cam.transform.up;

            napariTarget = Quaternion.LookRotation(forwardWorld, upWorld.normalized)
                           * Quaternion.Inverse(Quaternion.LookRotation(forwardLocal, upLocal));
            hasNapariTarget = true;
        }

        private void LateUpdate()
        {
            bool show = Time.time < rayUntil && volumeView.LoadedVolume != null;
            if (!show)
            {
                if (mouseRay != null && mouseRay.gameObject.activeSelf) mouseRay.gameObject.SetActive(false);
                return;
            }
            // eigenes root objekt in weltkoordinaten, sonst verzerrt die volumen skalierung die breite
            if (mouseRay == null)
            {
                var go = new GameObject("NapariMouseRay", typeof(LineRenderer));
                mouseRay = go.GetComponent<LineRenderer>();
                mouseRay.useWorldSpace = true;
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null) mouseRay.material = new Material(shader);
                // zum kopf hin ausblenden, die linie soll nicht vor dem gesicht anfangen
                Color c = UITheme.TextPrimary;
                var fade = new Gradient();
                fade.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                    new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.9f, 0.3f), new GradientAlphaKey(0.9f, 1f) });
                mouseRay.colorGradient = fade;
                mouseRay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mouseRay.positionCount = 2;
                mouseRay.widthMultiplier = MouseRayWidth;
            }
            if (!mouseRay.gameObject.activeSelf) mouseRay.gameObject.SetActive(true);
            Transform volume = volumeView.transform;
            Vector3 start = volume.TransformPoint(rayStartLocal);
            Vector3 end = volume.TransformPoint(rayEndLocal);
            // vom volumen rueckwaerts bis kurz vor den kopf verlaengern, wie ein strahl aus der hand
            Camera cam = Camera.main;
            Vector3 direction = end - start;
            if (cam != null && direction.sqrMagnitude > 1e-8f)
            {
                direction.Normalize();
                float back = Vector3.Dot(cam.transform.position - start, direction) + MouseRayHeadGap;
                if (back < 0f) start += direction * back;
            }
            mouseRay.SetPosition(0, start);
            mouseRay.SetPosition(1, end);
        }

        private ViewState CurrentView(Camera cam)
        {
            Transform volume = volumeView.transform;
            bool screen = Mirror != null && Mirror.Active;
            Vector3 eye = ViewerPosition(cam);
            Vector3 forward = cam.transform.forward;
            Vector3 up = cam.transform.up;
            if (screen && (volume.position - eye).sqrMagnitude > 1e-6f)
            {
                forward = (volume.position - eye).normalized;
                up = Vector3.up;
            }
            // richtungen mit InverseTransformVector, rechnet die achsen skalierung mit raus
            return new ViewState
            {
                name = VolumeName(),
                camPos = volume.InverseTransformPoint(eye),
                camForward = volume.InverseTransformVector(forward).normalized,
                camUp = volume.InverseTransformVector(up).normalized,
                volScale = volume.localScale.magnitude,
                hover = volumeView.HoverLabel,
                selected = volumeView.SelectedLabel,
                labelsShown = volumeView.ShowLabels,
                napariSeq = appliedNapariSeq,
                screenWanted = Mirror != null && Mirror.Active,
                windowSize = Mirror != null ? Mirror.WindowSize : 0,
                windowSeq = Mirror != null ? Mirror.WindowSeq : 0,
            };
        }
    }
}
