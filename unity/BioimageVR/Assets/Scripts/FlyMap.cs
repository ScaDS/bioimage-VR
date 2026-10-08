using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace BioimageVR
{
    // kleine karte beim fliegen, ein schnitt durchs volumen mit einem punkt wo man gerade ist
    // die schnittebene ist die, in der der flugweg am meisten strecke macht, ohne weg die
    // mit der groessten flaeche. der schnitt wandert mit, er liegt immer auf hoehe des nutzers
    // weg und stationen sind eingezeichnet, Rotate dreht die karte in vierteldrehungen
    // schwebt rechts unten vor dem nutzer und ist ihm zugewandt, folgt dem kopf traege
    // an der leiste oben greifen und verschieben, der neue platz bleibt relativ zum kopf
    // zur laufzeit gebaut, nur sichtbar solange Fly an ist
    [RequireComponent(typeof(VolumeView))]
    public class FlyMap : MonoBehaviour
    {
        private const float CanvasScale = 0.00085f;
        private const float MapSize = 270f;
        private const float DragBarHeight = 30f;
        // mrt und mikroskopie nutzen selten den ganzen wertebereich, etwas aufhellen
        private const float Gain = 1.6f;

        private VolumeView volumeView;
        private VolumeFly fly;
        private FlyPath path;

        private GameObject root;
        private RectTransform mapRect;
        private RawImage mapImage;
        private RectTransform marker;
        private TextMeshProUGUI caption;
        private int quarterTurns;

        // platz relativ zum kopf: rechts, hoch, vor. gedreht mit der blickrichtung beim letzten nachholen
        [SerializeField] private Vector3 offset = new Vector3(0.36f, -0.2f, 0.55f);
        // erst ab diesem winkel zur seite holt die karte nach
        [SerializeField] private float recenterDegrees = 50f;
        private XRGrabInteractable grab;
        private Image dragBarImage;
        private float yawReference;
        private bool placed;
        private bool catchingUp;
        private bool wasGrabbed;

        private Texture2D texture;
        private Color32[] pixels;
        // achse senkrecht zur karte, die beiden anderen spannen sie auf
        private int normalAxis = -1;
        private int axisA, axisB;
        private int shownSlice = -1;
        private bool dirty = true;
        private string sliceText = "";

        private void Awake()
        {
            volumeView = GetComponent<VolumeView>();
        }

        private void OnEnable()
        {
            volumeView.OnVolumeLoaded += HandleVolumeLoaded;
        }

        private void OnDisable()
        {
            volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        private void OnDestroy()
        {
            if (root != null) Destroy(root);
            if (texture != null) Destroy(texture);
        }

        // erst in LateUpdate auswerten, FlyPath laedt seinen weg im selben event
        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => dirty = true;

        private void LateUpdate()
        {
            if (fly == null) fly = GetComponent<VolumeFly>();
            if (path == null) path = GetComponent<FlyPath>();
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            bool show = fly != null && fly.Active && vol?.Voxels != null;
            if (!show)
            {
                if (root != null && root.activeSelf) root.SetActive(false);
                placed = false;
                return;
            }
            if (root == null) Build();
            if (!root.activeSelf) root.SetActive(true);
            if (Camera.main != null) Place(Camera.main.transform, Time.deltaTime);

            if (dirty)
            {
                ChooseAxes(vol);
                shownSlice = -1;
                dirty = false;
            }

            // am weg zaehlt der wegpunkt, sonst der kopf
            Camera cam = Camera.main;
            Vector3 local = path != null && path.Riding ? path.CurrentLocal
                : cam != null ? transform.InverseTransformPoint(cam.transform.position) : Vector3.zero;
            Vector3 size = new Vector3(vol.SizeX, vol.SizeY, vol.SizeZ);
            Vector3 voxel = Vector3.Scale(local + Vector3.one * 0.5f, size);

            int slice = Mathf.Clamp(Mathf.FloorToInt(voxel[normalAxis]), 0, (int)size[normalAxis] - 1);
            if (slice != shownSlice)
            {
                Render(vol, slice);
                shownSlice = slice;
                sliceText = $"Slice {"xyz"[normalAxis]} {slice + 1} of {(int)size[normalAxis]}";
            }
            // benannte segmentierung, z.b. organe: sagen worin man gerade steckt
            int label = volumeView.HasLabelNames ? volumeView.LabelAtLocal(local) : 0;
            string where = label > 0 ? $"In: {volumeView.LabelName(label)}\n{sliceText}" : sliceText;
            if (caption.text != where) caption.text = where;

            Vector2 at = new Vector2(Mathf.Clamp01(voxel[axisA] / size[axisA]), Mathf.Clamp01(voxel[axisB] / size[axisB]));
            marker.anchorMin = marker.anchorMax = at;
            marker.anchoredPosition = Vector2.zero;
        }

        private void Place(Transform head, float dt)
        {
            Vector3 flat = head.forward;
            flat.y = 0f;
            float yaw = flat.sqrMagnitude > 1e-4f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : yawReference;

            bool grabbed = grab != null && grab.isSelected;
            if (grabbed)
            {
                wasGrabbed = true;
            }
            else if (wasGrabbed)
            {
                // losgelassen, den platz relativ zum kopf merken
                wasGrabbed = false;
                yawReference = yaw;
                catchingUp = false;
                offset = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f)) * (root.transform.position - head.position);
            }
            else
            {
                if (!placed) yawReference = yaw;
                if (Mathf.Abs(Mathf.DeltaAngle(yawReference, yaw)) > recenterDegrees) catchingUp = true;
                if (catchingUp)
                {
                    yawReference = Mathf.MoveTowardsAngle(yawReference, yaw, 140f * dt);
                    if (Mathf.Abs(Mathf.DeltaAngle(yawReference, yaw)) < 3f) catchingUp = false;
                }
                Vector3 target = head.position + Quaternion.Euler(0f, yawReference, 0f) * offset;
                root.transform.position = placed
                    ? Vector3.Lerp(root.transform.position, target, 1f - Mathf.Exp(-8f * dt))
                    : target;
                placed = true;
            }

            // immer dem nutzer zugewandt, auch waehrend des greifens
            Vector3 away = root.transform.position - head.position;
            if (away.sqrMagnitude > 1e-4f) root.transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        private void ChooseAxes(NiftiVolumeLoader.Volume vol)
        {
            Vector3 physical = new Vector3(vol.SizeX * vol.VoxelSize.x, vol.SizeY * vol.VoxelSize.y, vol.SizeZ * vol.VoxelSize.z);
            // ausdehnung in echten einheiten, beim weg die strecke je achse, sonst die volumengroesse
            Vector3 extent = physical;
            if (path != null && path.HasPath)
            {
                Vector3 lo = path.LocalPoints[0], hi = lo;
                foreach (Vector3 p in path.LocalPoints)
                {
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
                extent = Vector3.Scale(hi - lo, physical);
            }
            normalAxis = extent.x <= extent.y && extent.x <= extent.z ? 0 : extent.y <= extent.z ? 1 : 2;
            axisA = normalAxis == 0 ? 1 : 0;
            axisB = normalAxis == 2 ? 1 : 2;

            int width = Size(vol, axisA), height = Size(vol, axisB);
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null) Destroy(texture);
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                pixels = new Color32[width * height];
                mapImage.texture = texture;
            }

            // seitenverhaeltnis in echten einheiten, laengere seite fuellt den rahmen
            float w = physical[axisA], h = physical[axisB];
            float fit = MapSize / Mathf.Max(w, h);
            mapRect.sizeDelta = new Vector2(w * fit, h * fit);
        }

        private static int Size(NiftiVolumeLoader.Volume vol, int axis) => axis == 0 ? vol.SizeX : axis == 1 ? vol.SizeY : vol.SizeZ;

        private void Render(NiftiVolumeLoader.Volume vol, int slice)
        {
            int width = texture.width, height = texture.height;
            int stride = vol.IsColor ? 3 : 1;
            int[] step = { 1, vol.SizeX, vol.SizeX * vol.SizeY };
            int stepA = step[axisA], stepB = step[axisB];
            int origin = slice * step[normalAxis];
            byte[] voxels = vol.Voxels;

            for (int b = 0; b < height; b++)
            {
                int row = origin + b * stepB;
                for (int a = 0; a < width; a++)
                {
                    int i = (row + a * stepA) * stride;
                    byte r = Bright(voxels[i]);
                    pixels[b * width + a] = vol.IsColor
                        ? new Color32(r, Bright(voxels[i + 1]), Bright(voxels[i + 2]), 255)
                        : new Color32(r, r, r, 255);
                }
            }

            if (path != null && path.HasPath) DrawPath(vol, width, height);
            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        private static byte Bright(byte v) => (byte)Mathf.Min(v * Gain, 255f);

        // weg auf die karte projiziert, egal in welcher tiefe er gerade liegt
        private void DrawPath(NiftiVolumeLoader.Volume vol, int width, int height)
        {
            Color32 lineColor = UITheme.AccentSecondary;
            Color32 stationColor = UITheme.Warning;
            Vector3[] points = path.LocalPoints;
            Vector2 previous = default;
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 p = new Vector2((points[i][axisA] + 0.5f) * width, (points[i][axisB] + 0.5f) * height);
                if (i > 0)
                {
                    int steps = Mathf.Max(Mathf.CeilToInt((p - previous).magnitude), 1);
                    for (int k = 0; k <= steps; k++) Dot(Vector2.Lerp(previous, p, k / (float)steps), 0, lineColor, width, height);
                }
                previous = p;
            }
            for (int i = 0; i < path.StationCount; i++)
            {
                Vector3 station = points[Mathf.Clamp(path.StationPointIndex(i), 0, points.Length - 1)];
                Dot(new Vector2((station[axisA] + 0.5f) * width, (station[axisB] + 0.5f) * height), 2, stationColor, width, height);
            }
        }

        private void Dot(Vector2 center, int radius, Color32 color, int width, int height)
        {
            int cx = Mathf.RoundToInt(center.x), cy = Mathf.RoundToInt(center.y);
            for (int y = cy - radius; y <= cy + radius; y++)
                for (int x = cx - radius; x <= cx + radius; x++)
                    if (x >= 0 && x < width && y >= 0 && y < height) pixels[y * width + x] = color;
        }

        private void Build()
        {
            root = new GameObject("FlyMap", typeof(RectTransform), typeof(Canvas), typeof(TrackedDeviceGraphicRaycaster));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            var rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(MapSize + 40f, MapSize + 190f);
            rect.localScale = Vector3.one * CanvasScale;

            RectTransform surface = UIKit.Surface(rect, "Card", true, UITheme.Background, UIKit.CardRadius,
                new RectOffset(20, 20, 14, 18), 10f, TextAnchor.UpperCenter);
            surface.anchorMin = surface.anchorMax = surface.pivot = new Vector2(0.5f, 0.5f);
            surface.anchoredPosition = Vector2.zero;

            // griffleiste, greifen laeuft ueber den collider, nicht ueber ui
            dragBarImage = UIKit.Panel(surface, "DragBar", UITheme.Track, DragBarHeight / 2f);
            dragBarImage.raycastTarget = false;
            UIKit.Size(dragBarImage, MapSize, DragBarHeight);
            var zone = new GameObject("GrabZone", typeof(BoxCollider));
            zone.transform.SetParent(dragBarImage.transform, false);
            zone.GetComponent<BoxCollider>().size = new Vector3(MapSize, DragBarHeight + 16f, 60f);

            // fester quadratischer rahmen, die karte liegt frei darin und darf sich drehen
            var frame = new GameObject("Frame", typeof(RectTransform));
            frame.transform.SetParent(surface, false);
            UIKit.Size(frame.transform, MapSize, MapSize);

            var mapGO = new GameObject("Map", typeof(RectTransform), typeof(RawImage));
            mapGO.transform.SetParent(frame.transform, false);
            mapRect = mapGO.GetComponent<RectTransform>();
            mapRect.anchorMin = mapRect.anchorMax = mapRect.pivot = new Vector2(0.5f, 0.5f);
            mapImage = mapGO.GetComponent<RawImage>();
            mapImage.raycastTarget = false;

            Image ring = UIKit.Panel(mapRect, "Marker", Color.white, 9f);
            ring.raycastTarget = false;
            marker = ring.rectTransform;
            marker.sizeDelta = new Vector2(18f, 18f);
            marker.pivot = new Vector2(0.5f, 0.5f);
            Image core = UIKit.Panel(marker, "Core", UITheme.Accent, 6f);
            core.raycastTarget = false;
            core.rectTransform.anchorMin = core.rectTransform.anchorMax = core.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            core.rectTransform.sizeDelta = new Vector2(12f, 12f);

            caption = UIKit.Text(surface, "", UIKit.CaptionSize, color: UITheme.TextSecondary,
                align: TextAlignmentOptions.Center, name: "Caption");
            Button rotate = UIKit.Button(surface, "Rotate", UIKit.ButtonStyle.Secondary, Icons.Reset, UIKit.SmallControlHeight);
            rotate.onClick.AddListener(() =>
            {
                quarterTurns = (quarterTurns + 1) % 4;
                mapRect.localRotation = Quaternion.Euler(0f, 0f, 90f * quarterTurns);
            });

            // erst jetzt, der grab sammelt seine collider beim anlegen ein
            grab = root.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackRotation = false;
            grab.throwOnDetach = false;
            // eigener haltepunkt an der leiste, sonst springt die karte beim greifen auf ihre mitte
            grab.attachTransform = dragBarImage.transform;
            var body = root.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true;
                body.useGravity = false;
            }
            // leiste hellt auf sobald der strahl draufzeigt, sonst sieht man nicht dass sie greifbar ist
            grab.hoverEntered.AddListener(_ => dragBarImage.color = UITheme.AccentSoft);
            grab.hoverExited.AddListener(_ => dragBarImage.color = UITheme.Track);
        }
    }
}
