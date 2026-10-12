using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BioimageVR
{
    // messtool, lineal mit zwei klicks, auto per flood fill oder zelle, ins leere loescht
    [RequireComponent(typeof(VolumeView))]
    public class MeasureTool : MonoBehaviour
    {
        public enum Mode { Off, Ruler, Blob }

        [SerializeField] private XRRayInteractor leftHandRay;
        [SerializeField] private XRRayInteractor rightHandRay;
        [SerializeField] private Material lineMaterial;
        [SerializeField] private Color accentColor = new Color(0.30f, 0.80f, 0.74f);
        [SerializeField] private float markerWorldSize = 0.012f;
        [SerializeField] private int maxBlobVoxels = 4_000_000;

        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        private static readonly int ThresholdMaxId = Shader.PropertyToID("_ThresholdMax");
        private static readonly int Channel0OnId = Shader.PropertyToID("_Channel0On");
        private static readonly int Channel1OnId = Shader.PropertyToID("_Channel1On");
        private static readonly int Channel2OnId = Shader.PropertyToID("_Channel2On");

        public struct BlobResult
        {
            public int VoxelCount;
            public bool Truncated;
            public Vector3Int Min;
            public Vector3Int Max;
            public float MeanIntensity;
            // segmentierte zelle, 0 wenn threshold blob
            public int Label;
        }

        public Mode CurrentMode { get; private set; } = Mode.Off;
        // modus oder lineal schritt geaendert, fuer toolbar und hinweis text
        public event System.Action StateChanged;
        public BlobResult? LastBlob { get; private set; }
        // letzte fertige lineal distanz in der einheit des bilds, siehe VolumeView.Unit
        public float? LastDistance { get; private set; }

        private VolumeView volumeView;
        private Renderer volumeRenderer;
        private InputAction leftClickAction;
        private InputAction rightClickAction;
        private InputAction mouseClickAction;
        // welcher zeiger zuletzt geklickt hat, der gilt auch fuer die vorschau
        private XRRayInteractor activeRay;
        private bool lastInputWasMouse;

        // lineal punkte im volumen raum, bleiben beim drehen und zoomen dran
        private Vector3? rulerStart;
        private Vector3? rulerEnd;
        private Vector3? rulerPreview;

        private LineRenderer boxLines;
        private LineRenderer rulerLine;
        private Transform startMarker;
        private Transform endMarker;
        private WorldTooltip label;
        private string labelText = "";
        private Vector3 labelAnchorLocal;
        private int measureToken;
        // true wenn die aktuelle auswahl vom mess tool kommt, nur dann beim loeschen zuruecksetzen
        private bool selectionFromMeasure;

        private void Awake()
        {
            volumeView = GetComponent<VolumeView>();
            volumeRenderer = GetComponent<Renderer>();
            // gleicher knopf wie der ui klick in SetupSidePanel.EnsureHandRay
            leftClickAction = new InputAction(type: InputActionType.Button, binding: "<XRController>{LeftHand}/primaryButton");
            rightClickAction = new InputAction(type: InputActionType.Button, binding: "<XRController>{RightHand}/primaryButton");
            mouseClickAction = new InputAction(type: InputActionType.Button, binding: "<Mouse>/middleButton");
        }

        private void OnEnable()
        {
            leftClickAction.Enable();
            rightClickAction.Enable();
            mouseClickAction.Enable();
            volumeView.OnVolumeLoaded += HandleVolumeLoaded;
        }

        private void OnDisable()
        {
            leftClickAction.Disable();
            rightClickAction.Disable();
            mouseClickAction.Disable();
            volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        private void OnDestroy()
        {
            leftClickAction?.Dispose();
            rightClickAction?.Dispose();
            mouseClickAction?.Dispose();
            if (label != null) Destroy(label.gameObject);
            if (rulerLine != null) Destroy(rulerLine.gameObject);
            if (startMarker != null) Destroy(startMarker.gameObject);
            if (endMarker != null) Destroy(endMarker.gameObject);
        }

        // neues bild, alte messung passt nicht mehr
        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => Clear();

        public void SetMode(Mode mode)
        {
            CurrentMode = mode;
            Clear();
        }

        // kurzer text was der naechste klick macht
        public string Hint
        {
            get
            {
                switch (CurrentMode)
                {
                    case Mode.Ruler when !rulerStart.HasValue:
                        return "Point at the cell · A sets the start point";
                    case Mode.Ruler when !rulerEnd.HasValue:
                        return "A sets the end point";
                    case Mode.Ruler:
                        return "A starts a new measurement · click into empty space to clear";
                    case Mode.Blob when UseLabels && !LastBlob.HasValue:
                        return "Point at a coloured cell · A measures it";
                    case Mode.Blob when UseLabels:
                        return "A measures the next cell · click into empty space to clear";
                    case Mode.Blob when !LastBlob.HasValue:
                        return "Point at a blob · A measures it";
                    case Mode.Blob:
                        return "Blobs stick together? Raise the threshold in Display";
                    default:
                        return "";
                }
            }
        }

        private void Update()
        {
            if (CurrentMode == Mode.Off || volumeView.LoadedVolume == null) return;

            XRRayInteractor clicked = null;
            bool click = false;
            if (mouseClickAction.WasPressedThisFrame())
            {
                click = true;
                lastInputWasMouse = true;
            }
            else if (leftClickAction.WasPressedThisFrame())
            {
                click = true;
                clicked = leftHandRay;
            }
            else if (rightClickAction.WasPressedThisFrame())
            {
                click = true;
                clicked = rightHandRay;
            }

            if (click)
            {
                if (clicked != null)
                {
                    activeRay = clicked;
                    lastInputWasMouse = false;
                }
                if (TryGetPointerRay(out Ray ray))
                {
                    if (CurrentMode == Mode.Ruler) RulerClick(ray);
                    else MeasureBlob(ray);
                }
            }

            // gummiband vorschau solange nur der start gesetzt ist
            if (CurrentMode == Mode.Ruler && rulerStart.HasValue && !rulerEnd.HasValue)
            {
                rulerPreview = TryGetPointerRay(out Ray hoverRay) && TryPick(hoverRay, out Vector3 hit, out _) ? hit : (Vector3?)null;
            }
        }

        private void LateUpdate()
        {
            UpdateRulerVisuals();

            if (label == null || !label.gameObject.activeSelf) return;
            label.Show(labelText, transform.TransformPoint(labelAnchorLocal) + Vector3.up * 0.03f);
            // konstante weltgroesse egal wie gezoomt
        }

        private bool TryGetPointerRay(out Ray ray)
        {
            ray = default;
            if (lastInputWasMouse)
            {
                if (Mouse.current == null || Camera.main == null) return false;
                ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
                return true;
            }

            if (activeRay == null) return false;
            // strahl auf dem panel, dann ist der klick fuers panel gemeint
            if (activeRay.TryGetCurrentUIRaycastResult(out _)) return false;

            Transform origin = activeRay.rayOriginTransform != null ? activeRay.rayOriginTransform : activeRay.transform;
            ray = new Ray(origin.position, origin.forward);
            return true;
        }

        // lineal

        private void RulerClick(Ray ray)
        {
            if (!TryPick(ray, out Vector3 hit, out _))
            {
                Clear();
                return;
            }

            if (!rulerStart.HasValue || rulerEnd.HasValue)
            {
                Clear();
                rulerStart = hit;
                StateChanged?.Invoke();
                return;
            }

            rulerEnd = hit;
            rulerPreview = null;
            LastDistance = PhysicalDistance(rulerStart.Value, hit);
            StateChanged?.Invoke();
        }

        private void UpdateRulerVisuals()
        {
            Vector3? end = rulerEnd ?? rulerPreview;
            bool hasStart = rulerStart.HasValue;
            bool hasLine = hasStart && end.HasValue;

            if (hasStart) PlaceMarker(ref startMarker, "RulerStart", rulerStart.Value);
            else if (startMarker != null) startMarker.gameObject.SetActive(false);

            if (rulerEnd.HasValue) PlaceMarker(ref endMarker, "RulerEnd", rulerEnd.Value);
            else if (endMarker != null) endMarker.gameObject.SetActive(false);

            if (!hasLine)
            {
                if (rulerLine != null) rulerLine.gameObject.SetActive(false);
                if (CurrentMode == Mode.Ruler && label != null) label.Hide();
                return;
            }

            Vector3 a = transform.TransformPoint(rulerStart.Value);
            Vector3 b = transform.TransformPoint(end.Value);
            if (rulerLine == null)
            {
                rulerLine = CreateLine("RulerLine", null, true);
                rulerLine.widthMultiplier = 0.002f;
            }
            rulerLine.gameObject.SetActive(true);
            rulerLine.positionCount = 2;
            rulerLine.SetPosition(0, a);
            rulerLine.SetPosition(1, b);

            // vorschau blasser als die fertige messung
            Color c = rulerEnd.HasValue ? accentColor : new Color(accentColor.r, accentColor.g, accentColor.b, 0.5f);
            rulerLine.startColor = c;
            rulerLine.endColor = c;

            labelAnchorLocal = (rulerStart.Value + end.Value) * 0.5f;
            ShowLabel(WorldTooltip.Mono(FormatDistance(PhysicalDistance(rulerStart.Value, end.Value))));
        }

        private void PlaceMarker(ref Transform marker, string name, Vector3 local)
        {
            if (marker == null)
            {
                // eigenes root objekt, sonst verzerrt die volumen skalierung die kugel
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = name;
                Destroy(go.GetComponent<Collider>());
                var r = go.GetComponent<MeshRenderer>();
                if (lineMaterial != null) r.material = lineMaterial;
                r.material.color = accentColor;
                marker = go.transform;
            }
            marker.gameObject.SetActive(true);
            marker.position = transform.TransformPoint(local);
            marker.localScale = Vector3.one * markerWorldSize;
        }

        // lokale differenz mal physikalische volumen groesse, unabhaengig vom zoom
        private float PhysicalDistance(Vector3 a, Vector3 b)
        {
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            Vector3 d = b - a;
            return new Vector3(
                d.x * vol.SizeX * vol.VoxelSize.x,
                d.y * vol.SizeY * vol.VoxelSize.y,
                d.z * vol.SizeZ * vol.VoxelSize.z).magnitude;
        }

        private string FormatDistance(float d)
        {
            return Units.Length(d, volumeView.Unit);
        }

        // auto blob

        // segmentierung nur wenn sie auch sichtbar ist, sonst misst man was man nicht sieht
        private bool UseLabels => volumeView.HasLabels && volumeView.ShowLabels;

        private async void MeasureBlob(Ray worldRay)
        {
            if (UseLabels)
            {
                MeasureLabel(worldRay);
                return;
            }

            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            if (!TryPick(worldRay, out _, out int seed))
            {
                Clear();
                return;
            }

            IntensityRule rule = CurrentRule(vol);
            int myToken = ++measureToken;
            labelAnchorLocal = VoxelToLocal(vol, seed % vol.SizeX + 0.5f, seed / vol.SizeX % vol.SizeY + 1f, seed / (vol.SizeX * vol.SizeY) + 0.5f);
            ShowLabel("measuring ...");

            // flood fill kann bei grossen blobs dauern, nicht im main thread
            BlobResult result = await Task.Run(() => FloodFill(vol, rule, seed, maxBlobVoxels));

            // ueberholt durch neuen klick, modus wechsel oder neues bild
            if (myToken != measureToken || vol != volumeView.LoadedVolume) return;

            LastBlob = result;
            StateChanged?.Invoke();
            labelAnchorLocal = VoxelToLocal(vol, (result.Min.x + result.Max.x + 1) * 0.5f, result.Max.y + 1f, (result.Min.z + result.Max.z + 1) * 0.5f);
            DrawBox(vol, result);
            ShowLabel(FormatBlob(vol, result));
        }

        private void MeasureLabel(Ray worldRay)
        {
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            if (!volumeView.TryPickCell(worldRay, out int id, out _))
            {
                Clear();
                return;
            }

            // werte kommen fertig aus dem laden, siehe CellStats
            CellInfo cell = vol.Cells[id];
            BlobResult result = new BlobResult
            {
                VoxelCount = cell.VoxelCount,
                Min = cell.Min,
                Max = cell.Max,
                MeanIntensity = cell.MeanIntensity,
                Label = id,
            };
            measureToken++;
            LastBlob = result;
            volumeView.SelectedLabel = id;
            selectionFromMeasure = true;
            StateChanged?.Invoke();
            labelAnchorLocal = VoxelToLocal(vol, (result.Min.x + result.Max.x + 1) * 0.5f, result.Max.y + 1f, (result.Min.z + result.Max.z + 1) * 0.5f);
            DrawBox(vol, result);
            ShowLabel(FormatBlob(vol, result));
        }

        private static BlobResult FloodFill(NiftiVolumeLoader.Volume vol, IntensityRule rule, int seed, int maxVoxels)
        {
            int sx = vol.SizeX, sy = vol.SizeY, sz = vol.SizeZ;
            int sliceSize = sx * sy;
            // bitset statt bool array, bei grossen volumen sonst zig mb pro klick
            var visited = new ulong[(sx * sy * sz + 63) / 64];
            var stack = new Stack<int>();
            stack.Push(seed);
            visited[seed >> 6] |= 1UL << (seed & 63);

            int count = 0;
            double intensitySum = 0;
            int minX = sx, minY = sy, minZ = sz, maxX = -1, maxY = -1, maxZ = -1;
            bool truncated = false;

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                rule.Inside(vol.Voxels, i, out float intensity);
                count++;
                intensitySum += intensity;

                int z = i / sliceSize;
                int rem = i - z * sliceSize;
                int y = rem / sx;
                int x = rem - y * sx;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;

                if (count >= maxVoxels)
                {
                    truncated = true;
                    break;
                }

                if (x > 0) TryPush(i - 1);
                if (x < sx - 1) TryPush(i + 1);
                if (y > 0) TryPush(i - sx);
                if (y < sy - 1) TryPush(i + sx);
                if (z > 0) TryPush(i - sliceSize);
                if (z < sz - 1) TryPush(i + sliceSize);
            }

            return new BlobResult
            {
                VoxelCount = count,
                Truncated = truncated,
                Min = new Vector3Int(minX, minY, minZ),
                Max = new Vector3Int(maxX, maxY, maxZ),
                MeanIntensity = count > 0 ? (float)(intensitySum / count) : 0f,
            };

            void TryPush(int n)
            {
                ulong bit = 1UL << (n & 63);
                if ((visited[n >> 6] & bit) != 0) return;
                visited[n >> 6] |= bit;
                if (rule.Inside(vol.Voxels, n, out _)) stack.Push(n);
            }
        }

        private string FormatBlob(NiftiVolumeLoader.Volume vol, BlobResult r)
        {
            Vector3 vs = vol.VoxelSize;
            string unit = volumeView.Unit;

            float volume = r.VoxelCount * vs.x * vs.y * vs.z;
            // durchmesser einer kugel mit gleichem volumen
            float eqDiameter = Mathf.Pow(6f * volume / Mathf.PI, 1f / 3f);
            float ex = (r.Max.x - r.Min.x + 1) * vs.x;
            float ey = (r.Max.y - r.Min.y + 1) * vs.y;
            float ez = (r.Max.z - r.Min.z + 1) * vs.z;

            string text =
                WorldTooltip.Title(r.Label > 0 ? volumeView.LabelName(r.Label) : "Blob") + "\n" +
                "Volume  " + WorldTooltip.Mono(Units.Volume(volume, unit)) + "\n" +
                "Eq. diameter  " + WorldTooltip.Mono(Units.Length(eqDiameter, unit, "0.0")) + "\n" +
                "Extent  " + WorldTooltip.Mono($"{Units.Plain(ex, unit)} · {Units.Plain(ey, unit)} · {Units.Plain(ez, unit)} {unit}");
            if (r.Truncated) text += "\n" + WorldTooltip.Warn("Stopped early · raise the threshold");
            return text;
        }

        // bounding box als linien im volumen raum, skaliert und dreht automatisch mit
        private void DrawBox(NiftiVolumeLoader.Volume vol, BlobResult r)
        {
            if (boxLines == null)
            {
                boxLines = CreateLine("BlobMeasureBox", transform, false);
                boxLines.widthMultiplier = 0.003f;
            }
            boxLines.startColor = accentColor;
            boxLines.endColor = accentColor;

            Vector3 lo = VoxelToLocal(vol, r.Min.x, r.Min.y, r.Min.z);
            Vector3 hi = VoxelToLocal(vol, r.Max.x + 1f, r.Max.y + 1f, r.Max.z + 1f);

            // alle 12 kanten als ein linienzug, manche kanten doppelt
            Vector3[] path =
            {
                new(lo.x, lo.y, lo.z), new(hi.x, lo.y, lo.z), new(hi.x, hi.y, lo.z), new(lo.x, hi.y, lo.z), new(lo.x, lo.y, lo.z),
                new(lo.x, lo.y, hi.z), new(hi.x, lo.y, hi.z), new(hi.x, hi.y, hi.z), new(lo.x, hi.y, hi.z), new(lo.x, lo.y, hi.z),
                new(lo.x, hi.y, hi.z), new(lo.x, hi.y, lo.z), new(hi.x, hi.y, lo.z), new(hi.x, hi.y, hi.z), new(hi.x, lo.y, hi.z), new(hi.x, lo.y, lo.z),
            };
            boxLines.positionCount = path.Length;
            boxLines.SetPositions(path);
            boxLines.gameObject.SetActive(true);
        }

        // picking

        private IntensityRule CurrentRule(NiftiVolumeLoader.Volume vol)
        {
            Material mat = volumeRenderer.material;
            return new IntensityRule
            {
                Threshold = mat.GetFloat(ThresholdId),
                ThresholdMax = mat.GetFloat(ThresholdMaxId),
                Channel0On = mat.GetFloat(Channel0OnId) > 0.5f,
                Channel1On = mat.GetFloat(Channel1OnId) > 0.5f,
                Channel2On = mat.GetFloat(Channel2OnId) > 0.5f,
                IsColor = vol.IsColor,
                IsLiveChannels = vol.IsLiveChannels,
            };
        }

        // strahl durch die box, erster voxel im threshold bereich
        private bool TryPick(Ray worldRay, out Vector3 hitLocal, out int voxel)
        {
            hitLocal = default;
            voxel = -1;
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            if (vol == null) return false;

            IntensityRule rule = CurrentRule(vol);
            Vector3 o = transform.InverseTransformPoint(worldRay.origin);
            Vector3 d = transform.InverseTransformDirection(worldRay.direction).normalized;
            if (!VolumeView.IntersectUnitBox(o, d, out float tNear, out float tFar)) return false;
            tNear = Mathf.Max(tNear, 0f);

            // halber voxel schritt, reicht um keinen voxel zu ueberspringen
            float step = 0.5f / Mathf.Max(vol.SizeX, Mathf.Max(vol.SizeY, vol.SizeZ));
            for (float t = tNear; t <= tFar; t += step)
            {
                Vector3 p = o + d * t;
                Vector3 uvw = p + Vector3.one * 0.5f;
                int x = Mathf.Clamp((int)(uvw.x * vol.SizeX), 0, vol.SizeX - 1);
                int y = Mathf.Clamp((int)(uvw.y * vol.SizeY), 0, vol.SizeY - 1);
                int z = Mathf.Clamp((int)(uvw.z * vol.SizeZ), 0, vol.SizeZ - 1);
                int index = x + vol.SizeX * (y + vol.SizeY * z);
                if (rule.Inside(vol.Voxels, index, out _))
                {
                    hitLocal = p;
                    voxel = index;
                    return true;
                }
            }
            return false;
        }

        // voxel koordinate auf objekt raum, gleich wie im shader
        private static Vector3 VoxelToLocal(NiftiVolumeLoader.Volume vol, float x, float y, float z) =>
            new Vector3(x / vol.SizeX - 0.5f, y / vol.SizeY - 0.5f, z / vol.SizeZ - 0.5f);

        private struct IntensityRule
        {
            public float Threshold;
            public float ThresholdMax;
            public bool Channel0On, Channel1On, Channel2On;
            public bool IsColor, IsLiveChannels;

            // spiegel von SampleVoxel im shader, nur nearest statt trilinear
            public bool Inside(byte[] voxels, int index, out float intensity)
            {
                if (IsColor)
                {
                    int b = index * 3;
                    float r = voxels[b] / 255f, g = voxels[b + 1] / 255f, bl = voxels[b + 2] / 255f;
                    if (IsLiveChannels)
                    {
                        if (!Channel0On) r = 0f;
                        if (!Channel1On) g = 0f;
                        if (!Channel2On) bl = 0f;
                    }
                    intensity = Mathf.Max(r, Mathf.Max(g, bl));
                }
                else
                {
                    intensity = voxels[index] / 255f;
                }
                return intensity > Threshold && intensity <= ThresholdMax;
            }
        }

        // anzeige

        private LineRenderer CreateLine(string name, Transform parent, bool worldSpace)
        {
            var go = new GameObject(name, typeof(LineRenderer));
            if (parent != null) go.transform.SetParent(parent, false);
            var line = go.GetComponent<LineRenderer>();
            line.useWorldSpace = worldSpace;
            line.loop = false;
            if (lineMaterial != null) line.sharedMaterial = lineMaterial;
            return line;
        }

        private void ShowLabel(string text)
        {
            // eigenes root objekt, sonst verzerrt die volumen skalierung den text
            if (label == null) label = WorldTooltip.Create("MeasureLabel");
            labelText = text;
            label.Show(text, transform.TransformPoint(labelAnchorLocal) + Vector3.up * 0.03f);
        }

        public void Clear()
        {
            measureToken++;
            LastBlob = null;
            LastDistance = null;
            if (selectionFromMeasure && volumeView != null) volumeView.SelectedLabel = 0;
            selectionFromMeasure = false;
            rulerStart = null;
            rulerEnd = null;
            rulerPreview = null;
            if (boxLines != null) boxLines.gameObject.SetActive(false);
            if (label != null) label.Hide();
            StateChanged?.Invoke();
        }
    }
}
