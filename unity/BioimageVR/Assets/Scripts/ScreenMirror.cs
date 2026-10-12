using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace BioimageVR
{
    // bildschirm modus, napari fenster als leinwand links vom volumen, controller strahl ist die maus
    public class ScreenMirror : MonoBehaviour
    {
        private const float CanvasScale = 0.00085f;
        private const float ScreenWidth = 1700f;
        // ein fensterbild dieser breite fuellt ScreenWidth, breitere machen die leinwand breiter
        private const float ReferencePixels = 1536f;
        private const float MinWidthFactor = 0.6f;
        private const float MaxWidthFactor = 1.7f;
        private const float Frame = 14f;
        // so weit links vom volumen, wie ein zweiter monitor
        private const float SideAngle = 50f;
        private const float Distance = 1.4f;
        // so lange haelt der server die anfrage offen bis ein neues bild da ist
        private const float WaitSeconds = 1f;
        private const float SizeStep = 1.2f;
        // so oft gehen zeiger ereignisse an den pc, bewegungen dazwischen fallen zusammen
        private const float PointerSeconds = 0.03f;
        private const float MinSize = 0.4f;
        private const float MaxSize = 2.6f;

        private VrBridge bridge;
        private Transform volume;
        private RectTransform canvas;
        private RawImage image;
        private TextMeshProUGUI waiting;
        private Texture2D frame;
        private Coroutine poll;
        private long seq;
        private float lastFrameTime = -100f;
        // vom nutzer eingestellte groesse, bleibt ueber aus und an erhalten
        private float size = 1f;
        private RectTransform bar;
        private Button smallerButton, largerButton;
        private readonly Button[] windowButtons = new Button[3];

        [System.Serializable] private class PointerEvent { public string t; public float u; public float v; }
        [System.Serializable] private class PointerBatch { public List<PointerEvent> events = new List<PointerEvent>(); }
        private readonly PointerBatch pointerQueue = new PointerBatch();
        private Coroutine pointerSend;

        // wunsch fuer das fenster auf dem pc, geht mit dem blick an napari
        public int WindowSize { get; private set; }
        public long WindowSeq { get; private set; }

        public bool Active { get; private set; }
        // fester standpunkt solange die leinwand an ist, kopfdrehen soll napari nicht mitziehen
        public Vector3 ViewPoint { get; private set; }
        // kommt gerade nichts, laeuft napari nicht oder das plugin fenster ist zu
        public bool Receiving => Active && Time.time - lastFrameTime < 3f;

        public void Init(VrBridge vrBridge, Transform volumeTransform)
        {
            bridge = vrBridge;
            volume = volumeTransform;
        }

        public void SetActive(bool on)
        {
            if (on == Active) return;
            Active = on;
            if (poll != null) StopCoroutine(poll);
            poll = null;
            if (pointerSend != null) StopCoroutine(pointerSend);
            pointerSend = null;
            pointerQueue.events.Clear();
            if (!on)
            {
                canvas.gameObject.SetActive(false);
                return;
            }
            if (canvas == null) Build();
            ViewPoint = Camera.main.transform.position;
            Place();
            canvas.gameObject.SetActive(true);
            // letztes bild vom server gleich zeigen, auch wenn es schon aelter ist
            seq = 0;
            poll = StartCoroutine(Poll());
            pointerSend = StartCoroutine(SendPointer());
        }

        // von ScreenPointer, u und v als anteil des bilds, v von oben
        public void Pointer(string kind, Vector2 uv)
        {
            List<PointerEvent> events = pointerQueue.events;
            // nur die letzte bewegung zaehlt, druecken und loslassen bleiben alle erhalten
            if (kind == "move" && events.Count > 0 && events[events.Count - 1].t == "move")
                events.RemoveAt(events.Count - 1);
            events.Add(new PointerEvent { t = kind, u = uv.x, v = uv.y });
        }

        private IEnumerator SendPointer()
        {
            while (Active)
            {
                string url = bridge.ServerUrl;
                if (pointerQueue.events.Count > 0 && !string.IsNullOrEmpty(url))
                {
                    byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(pointerQueue));
                    pointerQueue.events.Clear();
                    using var request = new UnityWebRequest($"{url}/vr/pointer", "POST");
                    request.uploadHandler = new UploadHandlerRaw(body);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = 2;
                    yield return request.SendWebRequest();
                }
                yield return new WaitForSeconds(PointerSeconds);
            }
        }

        // groesser oder kleiner um die eigene mitte, die leinwand bleibt wo sie steht
        public void Grow(bool larger)
        {
            size = Mathf.Clamp(size * (larger ? SizeStep : 1f / SizeStep), MinSize, MaxSize);
            if (canvas != null) ApplySize();
        }

        private void ApplySize()
        {
            canvas.localScale = Vector3.one * (CanvasScale * size);
            // die leiste waechst nicht mit, die knoepfe bleiben gleich gross
            bar.localScale = Vector3.one / size;
            smallerButton.interactable = CanGrow(false);
            largerButton.interactable = CanGrow(true);
        }

        private void SetWindow(int level)
        {
            WindowSize = level;
            WindowSeq = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < windowButtons.Length; i++)
                UIKit.SetStyle(windowButtons[i], i + 1 == level ? UIKit.ButtonStyle.Selected : UIKit.ButtonStyle.Secondary);
        }

        public bool CanGrow(bool larger) => larger ? size < MaxSize - 1e-3f : size > MinSize + 1e-3f;

        private void Build()
        {
            var go = new GameObject("ScreenMirror", typeof(RectTransform), typeof(Canvas), typeof(TrackedDeviceGraphicRaycaster));
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            go.GetComponent<Canvas>().worldCamera = Camera.main;
            canvas = go.GetComponent<RectTransform>();

            Image background = UIKit.Panel(canvas, "Background", UITheme.Background, UIKit.CardRadius);
            background.raycastTarget = false;
            UIKit.Stretch(background.rectTransform);

            var imageGo = new GameObject("Image", typeof(RectTransform), typeof(RawImage));
            imageGo.transform.SetParent(canvas, false);
            image = imageGo.GetComponent<RawImage>();
            image.enabled = false;
            imageGo.AddComponent<ScreenPointer>().Init(this);
            UIKit.Stretch(image.rectTransform);
            image.rectTransform.offsetMin = new Vector2(Frame, Frame);
            image.rectTransform.offsetMax = new Vector2(-Frame, -Frame);

            waiting = UIKit.Text(canvas, "Waiting for napari on the PC\nopen Plugins > Bioimage VR there",
                UIKit.HeadingSize, color: UITheme.TextSecondary, align: TextAlignmentOptions.Center, name: "Waiting");
            UIKit.Stretch(waiting.rectTransform);

            // haengt unter der leinwand
            bar = UIKit.Surface(canvas, "Controls", false, UITheme.Background, UIKit.CardRadius,
                new RectOffset(22, 22, 12, 12), UIKit.Gap, TextAnchor.MiddleCenter);
            bar.anchorMin = bar.anchorMax = new Vector2(0.5f, 0f);
            bar.pivot = new Vector2(0.5f, 1f);
            bar.anchoredPosition = new Vector2(0f, -14f);
            Caption("Screen");
            smallerButton = UIKit.Button(bar, "Smaller", UIKit.ButtonStyle.Secondary);
            smallerButton.onClick.AddListener(() => Grow(false));
            largerButton = UIKit.Button(bar, "Larger", UIKit.ButtonStyle.Secondary);
            largerButton.onClick.AddListener(() => Grow(true));
            UIKit.Divider(bar, true);
            Caption("Window on the PC");
            string[] levels = { "Small", "Medium", "Large" };
            for (int i = 0; i < levels.Length; i++)
            {
                int level = i + 1;
                windowButtons[i] = UIKit.Button(bar, levels[i], UIKit.ButtonStyle.Secondary);
                windowButtons[i].onClick.AddListener(() => SetWindow(level));
            }

            ApplySize();
            Resize(Mathf.RoundToInt(ReferencePixels), Mathf.RoundToInt(ReferencePixels * 9f / 16f));
        }

        private void Caption(string text)
        {
            TextMeshProUGUI label = UIKit.Text(bar, text, UIKit.LabelSize, color: UITheme.TextSecondary,
                align: TextAlignmentOptions.Center, name: $"{text}Caption");
            label.textWrappingMode = TextWrappingModes.NoWrap;
        }

        private void Resize(int pixelWidth, int pixelHeight)
        {
            float width = ScreenWidth * Mathf.Clamp(pixelWidth / ReferencePixels, MinWidthFactor, MaxWidthFactor);
            canvas.sizeDelta = new Vector2(width + 2f * Frame, width * pixelHeight / pixelWidth + 2f * Frame);
        }

        // einmal beim einschalten hinstellen, danach bleibt die leinwand im raum stehen
        private void Place()
        {
            Transform cam = Camera.main.transform;
            Vector3 toVolume = volume.position - cam.position;
            toVolume.y = 0f;
            if (toVolume.sqrMagnitude < 1e-4f) toVolume = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            Vector3 direction = Quaternion.AngleAxis(-SideAngle, Vector3.up) * toVolume.normalized;
            canvas.SetPositionAndRotation(cam.position + direction * Distance, Quaternion.LookRotation(direction, Vector3.up));
        }

        private IEnumerator Poll()
        {
            while (Active)
            {
                string url = bridge.ServerUrl;
                if (string.IsNullOrEmpty(url))
                {
                    yield return new WaitForSeconds(1f);
                    continue;
                }
                // jpeg neben dem hauptthread entpacken, server antwortet sobald ein neues bild da ist
                bool failed;
                string wait = WaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using (UnityWebRequest request = UnityWebRequestTexture.GetTexture($"{url}/vr/screen?after={seq}&wait={wait}", true))
                {
                    request.timeout = 4;
                    yield return request.SendWebRequest();
                    failed = request.result != UnityWebRequest.Result.Success;
                    // 204 heisst nichts neues
                    if (!failed && request.responseCode == 200)
                    {
                        long.TryParse(request.GetResponseHeader("X-Seq"), out seq);
                        Show(DownloadHandlerTexture.GetContent(request));
                    }
                }
                if (failed) yield return new WaitForSeconds(1f);
                else yield return null;
            }
        }

        private void Show(Texture2D next)
        {
            if (next == null) return;
            next.wrapMode = TextureWrapMode.Clamp;
            image.texture = next;
            image.enabled = true;
            waiting.gameObject.SetActive(false);
            if (frame == null || frame.width != next.width || frame.height != next.height)
                Resize(next.width, next.height);
            if (frame != null) Destroy(frame);
            frame = next;
            lastFrameTime = Time.time;
        }

        private void Update()
        {
            if (!Active || waiting == null) return;
            // bild bleibt stehen wenn napari weg ist, dann wieder den hinweis zeigen
            bool stale = !Receiving;
            if (waiting.gameObject.activeSelf != stale)
            {
                waiting.gameObject.SetActive(stale);
                image.enabled = !stale && frame != null;
            }
        }
    }
}
