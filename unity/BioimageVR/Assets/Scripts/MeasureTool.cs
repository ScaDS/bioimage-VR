using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BioimageVR
{
    // messtool, modus kommt von MeasureToolbar im hud, startet aus
    // gemessen wird mit X oder A wie ein ui klick, nur wenn der strahl nicht aufs panel zeigt
    // desktop: mittlere maustaste an der mausposition
    // lineal: erster klick start, zweiter klick ende, dritter klick neuer start
    // auto: klick auf blob, flood fill im aktuellen threshold bereich, 6er nachbarschaft
    // punkte landen auf der ersten sichtbaren struktur entlang des strahls
    // gleiche intensitaets regel wie SampleVoxel im shader, also misst was man sieht
    // ins leere klicken loescht die messung
    // mit sichtbarer segmentierung misst auto die angeklickte zelle statt threshold blob
    [RequireComponent(typeof(VolumeView))]
    public class MeasureTool : MonoBehaviour
    {
        public enum Mode { Off, Ruler, Blob }

        [SerializeField] private XRRayInteractor leftHandRay;
        [SerializeField] private XRRayInteractor rightHandRay;
        [SerializeField] private Material lineMaterial;
        [SerializeField] private Color accentColor = new Color(0.30f, 0.80f, 0.74f);
        [SerializeField] private float labelWorldScale = 0.012f;
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
        // letzte fertige lineal distanz in µm bzw px
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
        private TextMesh label;
        private Vector3 labelAnchorLocal;
        private int measureToken;
        // statistik pro label, einmal pro volumen beim ersten klick berechnet
        private BlobResult[] labelStats;
        private NiftiVolumeLoader.Volume labelStatsVolume;

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
                        return "Auf die Zelle zeigen, X oder A setzt den Startpunkt";
                    case Mode.Ruler when !rulerEnd.HasValue:
                        return "X oder A setzt den Endpunkt";
                    case Mode.Ruler:
                        return "X oder A startet eine neue Messung, ins Leere klicken loescht";
                    case Mode.Blob when UseLabels && !LastBlob.HasValue:
                        return "Auf eine farbige Zelle zeigen, X oder A misst sie";
                    case Mode.Blob when UseLabels:
                        return "X oder A misst die naechste Zelle, ins Leere klicken loescht";
                    case Mode.Blob when !LastBlob.HasValue:
                        return "Auf einen Blob zeigen, X oder A misst ihn";
                    case Mode.Blob:
                        return "Blobs kleben zusammen? Threshold hoeher stellen";
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
            // konstante weltgroesse egal wie gezoomt
            label.transform.position = transform.TransformPoint(labelAnchorLocal) + Vector3.up * 0.03f;
            label.transform.localScale = Vector3.one * labelWorldScale;
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
                if (CurrentMode == Mode.Ruler && label != null) label.gameObject.SetActive(false);
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
            ShowLabel(FormatDistance(PhysicalDistance(rulerStart.Value, end.Value)));
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
            if (!volumeView.HasPhysicalVoxelSize) return $"{d:0.#} px";
            if (d >= 1000f) return $"{d / 1000f:0.##} mm";
            if (d >= 1f) return $"{d:0.##} µm";
            return $"{d * 1000f:0} nm";
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
            ShowLabel("misst ...");

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

        private async void MeasureLabel(Ray worldRay)
        {
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            if (!TryPickLabel(worldRay, out int id))
            {
                Clear();
                return;
            }

            int myToken = ++measureToken;
            if (labelStatsVolume != vol)
            {
                ShowLabel("misst ...");
                BlobResult[] stats = await Task.Run(() => ComputeLabelStats(vol));
                if (vol != volumeView.LoadedVolume) return;
                labelStats = stats;
                labelStatsVolume = vol;
                if (myToken != measureToken) return;
            }

            BlobResult result = labelStats[id];
            LastBlob = result;
            volumeView.SetSelectedLabel(id);
            StateChanged?.Invoke();
            labelAnchorLocal = VoxelToLocal(vol, (result.Min.x + result.Max.x + 1) * 0.5f, result.Max.y + 1f, (result.Min.z + result.Max.z + 1) * 0.5f);
            DrawBox(vol, result);
            ShowLabel(FormatBlob(vol, result));
        }

        // ein durchlauf ueber alle voxel, anzahl box und mittlere intensitaet pro id
        private static BlobResult[] ComputeLabelStats(NiftiVolumeLoader.Volume vol)
        {
            int sx = vol.SizeX, sy = vol.SizeY;
            var stats = new BlobResult[vol.MaxLabel + 1];
            var sums = new double[vol.MaxLabel + 1];
            for (int id = 0; id < stats.Length; id++)
            {
                stats[id].Label = id;
                stats[id].Min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
                stats[id].Max = new Vector3Int(-1, -1, -1);
            }

            ushort[] labels = vol.Labels;
            int i = 0;
            for (int z = 0; z < vol.SizeZ; z++)
            for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++, i++)
            {
                int id = labels[i];
                if (id == 0) continue;
                ref BlobResult r = ref stats[id];
                r.VoxelCount++;
                r.Min = Vector3Int.Min(r.Min, new Vector3Int(x, y, z));
                r.Max = Vector3Int.Max(r.Max, new Vector3Int(x, y, z));
                sums[id] += vol.IsColor
                    ? Mathf.Max(vol.Voxels[i * 3], Mathf.Max(vol.Voxels[i * 3 + 1], vol.Voxels[i * 3 + 2])) / 255f
                    : vol.Voxels[i] / 255f;
            }

            for (int id = 1; id < stats.Length; id++)
                if (stats[id].VoxelCount > 0) stats[id].MeanIntensity = (float)(sums[id] / stats[id].VoxelCount);
            return stats;
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
            string unit = volumeView.HasPhysicalVoxelSize ? "µm" : "px";

            float volume = r.VoxelCount * vs.x * vs.y * vs.z;
            // durchmesser einer kugel mit gleichem volumen
            float eqDiameter = Mathf.Pow(6f * volume / Mathf.PI, 1f / 3f);
            float ex = (r.Max.x - r.Min.x + 1) * vs.x;
            float ey = (r.Max.y - r.Min.y + 1) * vs.y;
            float ez = (r.Max.z - r.Min.z + 1) * vs.z;

            string text =
                (r.Label > 0 ? $"Zelle {r.Label}\n" : "") +
                $"V {volume:0.##} {unit}³\n" +
                $"Ø {eqDiameter:0.##} {unit} (kugel aequiv.)\n" +
                $"X {ex:0.#}  Y {ey:0.#}  Z {ez:0.#} {unit}\n" +
                $"{r.VoxelCount} voxel, I {r.MeanIntensity:0.00}";
            if (r.Truncated) text += "\nabgebrochen, threshold hoeher";
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

        // strahl durch die box marschieren, erster voxel im threshold bereich
        // hit ist der genaue punkt auf dem strahl im volumen raum, voxel der index dazu
        private bool TryPick(Ray worldRay, out Vector3 hitLocal, out int voxel)
        {
            hitLocal = default;
            voxel = -1;
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            if (vol == null) return false;

            IntensityRule rule = CurrentRule(vol);
            Vector3 o = transform.InverseTransformPoint(worldRay.origin);
            Vector3 d = transform.InverseTransformDirection(worldRay.direction).normalized;
            if (!IntersectUnitBox(o, d, out float tNear, out float tFar)) return false;
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

        // erster voxel mit label ungleich 0 entlang des strahls, unabhaengig vom threshold
        private bool TryPickLabel(Ray worldRay, out int id)
        {
            id = 0;
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            if (vol?.Labels == null) return false;

            Vector3 o = transform.InverseTransformPoint(worldRay.origin);
            Vector3 d = transform.InverseTransformDirection(worldRay.direction).normalized;
            if (!IntersectUnitBox(o, d, out float tNear, out float tFar)) return false;
            tNear = Mathf.Max(tNear, 0f);

            float step = 0.5f / Mathf.Max(vol.SizeX, Mathf.Max(vol.SizeY, vol.SizeZ));
            for (float t = tNear; t <= tFar; t += step)
            {
                Vector3 uvw = o + d * t + Vector3.one * 0.5f;
                int x = Mathf.Clamp((int)(uvw.x * vol.SizeX), 0, vol.SizeX - 1);
                int y = Mathf.Clamp((int)(uvw.y * vol.SizeY), 0, vol.SizeY - 1);
                int z = Mathf.Clamp((int)(uvw.z * vol.SizeZ), 0, vol.SizeZ - 1);
                int label = vol.Labels[x + vol.SizeX * (y + vol.SizeY * z)];
                if (label > 0)
                {
                    id = label;
                    return true;
                }
            }
            return false;
        }

        // gleiche box wie IntersectBox im shader
        private static bool IntersectUnitBox(Vector3 o, Vector3 d, out float tNear, out float tFar)
        {
            tNear = float.NegativeInfinity;
            tFar = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(d[axis]) < 1e-8f)
                {
                    if (o[axis] < -0.5f || o[axis] > 0.5f) return false;
                    continue;
                }
                float t0 = (-0.5f - o[axis]) / d[axis];
                float t1 = (0.5f - o[axis]) / d[axis];
                tNear = Mathf.Max(tNear, Mathf.Min(t0, t1));
                tFar = Mathf.Min(tFar, Mathf.Max(t0, t1));
            }
            return tFar > Mathf.Max(tNear, 0f);
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

        private TextMesh CreateText(string name, int fontSize, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(TextMesh));
            var text = go.GetComponent<TextMesh>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            go.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
            text.fontSize = fontSize;
            text.characterSize = 0.1f;
            text.anchor = anchor;
            text.alignment = TextAlignment.Center;
            text.color = Color.white;
            go.AddComponent<BillboardText>();
            return text;
        }

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
            if (label == null) label = CreateText("MeasureLabel", 48, TextAnchor.LowerCenter);
            label.text = text;
            label.gameObject.SetActive(true);
        }

        public void Clear()
        {
            measureToken++;
            LastBlob = null;
            LastDistance = null;
            if (volumeView != null && volumeView.LoadedVolume != null) volumeView.SetSelectedLabel(0);
            rulerStart = null;
            rulerEnd = null;
            rulerPreview = null;
            if (boxLines != null) boxLines.gameObject.SetActive(false);
            if (label != null) label.gameObject.SetActive(false);
            StateChanged?.Invoke();
        }
    }
}
