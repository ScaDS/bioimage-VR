using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace BioimageVR
{
    // laedt nii volumen und setzt es aufs material, LoadVolume auch spaeter aufrufbar
    [RequireComponent(typeof(MeshRenderer))]
    public class VolumeView : MonoBehaviour
    {
        [Tooltip("Path to a .nii or .nii.gz file produced by preprocessing/convert_to_volume.py")]
        public string VolumeFilePath;

        // aktuell geladenes volumen, null bis zum ersten erfolgreichen laden
        public NiftiVolumeLoader.Volume LoadedVolume { get; private set; }

        // false wenn kein metadata.json, dann ist VoxelSize nur pixdim ohne echte einheit
        public bool HasPhysicalVoxelSize { get; private set; }

        // einheit von VoxelSize, µm, mm oder px ohne voxelgroesse
        public string Unit => HasPhysicalVoxelSize ? unit : "px";
        private string unit = "µm";

        // true wenn eine <name>_labels.nii.gz mitgeladen wurde
        public bool HasLabels => LoadedVolume?.Labels != null;

        // namen je id aus <name>_labels_names.json, sonst heissen zellen nach ihrer nummer
        public bool HasLabelNames => labelNames != null && labelNames.Count > 0;
        private Dictionary<int, string> labelNames;

        public string LabelName(int id) =>
            labelNames != null && labelNames.TryGetValue(id, out string name) ? Capitalize(name) : $"Cell {id}";

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // label an einer stelle im objekt raum, 0 fuer hintergrund oder ausserhalb
        public int LabelAtLocal(Vector3 local)
        {
            NiftiVolumeLoader.Volume vol = LoadedVolume;
            if (vol?.Labels == null) return 0;
            Vector3 uvw = local + Vector3.one * 0.5f;
            if (uvw.x < 0f || uvw.y < 0f || uvw.z < 0f || uvw.x >= 1f || uvw.y >= 1f || uvw.z >= 1f) return 0;
            int x = (int)(uvw.x * vol.SizeX), y = (int)(uvw.y * vol.SizeY), z = (int)(uvw.z * vol.SizeZ);
            return vol.Labels[x + vol.SizeX * (y + vol.SizeY * z)];
        }

        // overlay an aus, nur wirksam wenn labels da sind
        public bool ShowLabels
        {
            get => HasLabels && GetComponent<MeshRenderer>().material.GetFloat(ShowLabelsId) > 0.5f;
            set
            {
                GetComponent<MeshRenderer>().material.SetFloat(ShowLabelsId, value && HasLabels ? 1f : 0f);
                LabelsDisplayChanged?.Invoke();
            }
        }

        // wie stark die zellfarben ueber das rohbild gemischt werden, 0 bis 1
        public float LabelStrength
        {
            get => GetComponent<MeshRenderer>().material.GetFloat(LabelStrengthId);
            set => GetComponent<MeshRenderer>().material.SetFloat(LabelStrengthId, Mathf.Clamp01(value));
        }

        // ausgewaehlte zelle, 0 fuer keine, bleibt stehen bis neu gewaehlt wird
        public int SelectedLabel
        {
            get => selectedLabel;
            set
            {
                if (value == selectedLabel) return;
                selectedLabel = value;
                GetComponent<MeshRenderer>().material.SetFloat(SelectedLabelId, value);
                SelectionChanged?.Invoke(value);
            }
        }

        // zelle unter dem zeiger, leuchtet nur solange drauf gezeigt wird
        public int HoverLabel
        {
            get => hoverLabel;
            set
            {
                if (value == hoverLabel) return;
                hoverLabel = value;
                GetComponent<MeshRenderer>().material.SetFloat(HoverLabelId, value);
            }
        }

        public event Action<int> SelectionChanged;
        // segmentierung ein oder ausgeblendet, egal von wo aus
        public event Action LabelsDisplayChanged;
        // labels wurden bearbeitet, siehe LabelEditor
        public event Action LabelsChanged;

        public void NotifyLabelsChanged() => LabelsChanged?.Invoke();

        // form analyse fertig oder nach einer korrektur aktualisiert
        public event Action SplitHintsChanged;

        public SplitHint GetSplitHint(int id)
        {
            SplitHint[] hints = LoadedVolume?.SplitHints;
            return hints != null && id > 0 && id < hints.Length ? hints[id] : default;
        }

        // nach einer korrektur nur die betroffenen zellen neu pruefen, dauert pro zelle ms
        public void RefreshSplitHints(params int[] ids)
        {
            NiftiVolumeLoader.Volume vol = LoadedVolume;
            if (vol?.SplitHints == null || vol.Cells == null) return;
            if (vol.SplitHints.Length < vol.Cells.Length)
            {
                SplitHint[] grown = vol.SplitHints;
                Array.Resize(ref grown, vol.Cells.Length);
                vol.SplitHints = grown;
            }
            foreach (int id in ids)
                if (id > 0 && id < vol.Cells.Length) vol.SplitHints[id] = CellShapeAnalysis.Analyze(vol, vol.Cells[id]);
            SplitHintsChanged?.Invoke();
        }

        // alle zellen im hintergrund, das bild ist dann schon sichtbar
        private async void AnalyzeShapes(NiftiVolumeLoader.Volume volume)
        {
            SplitHint[] hints;
            try
            {
                hints = await Task.Run(() => CellShapeAnalysis.AnalyzeAll(volume));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VolumeView: formanalyse fehlgeschlagen: {e.Message}");
                return;
            }
            // inzwischen anderes bild geladen
            if (volume != LoadedVolume) return;
            volume.SplitHints = hints;
            SplitHintsChanged?.Invoke();
        }

        // nach einem kompletten neubau der label textur, siehe LabelEditor.RebuildTexture
        public void RebindLabelTexture()
        {
            if (LoadedVolume?.LabelTexture != null)
                GetComponent<MeshRenderer>().material.SetTexture(LabelTexId, LoadedVolume.LabelTexture);
        }

        // tatsaechlicher pfad auf dem geraet, auf android in persistentDataPath
        public string ResolvedVolumePath { get; private set; }
        // eine zelle wurde aus oder eingeblendet
        public event Action CellVisibilityChanged;

        private int selectedLabel;
        private int hoverLabel;
        // 256 mal 256 reicht fuer alle uint16 ids, texel id%256 und id/256
        private Texture2D visibilityTexture;
        private byte[] visibility;
        private const int VisibilitySize = 256;

        public bool IsCellVisible(int id) =>
            visibility == null || id <= 0 || id >= visibility.Length || visibility[id] > 0;

        public void SetCellVisible(int id, bool visible)
        {
            if (visibility == null || id <= 0 || id >= visibility.Length) return;
            visibility[id] = visible ? (byte)255 : (byte)0;
            if (!visible && selectedLabel == id) SelectedLabel = 0;
            UploadVisibility();
        }

        public void ShowAllCells()
        {
            if (visibility == null) return;
            for (int i = 0; i < visibility.Length; i++) visibility[i] = 255;
            UploadVisibility();
        }

        private void UploadVisibility()
        {
            visibilityTexture.SetPixelData(visibility, 0);
            visibilityTexture.Apply(false);
            CellVisibilityChanged?.Invoke();
        }

        // erster sichtbarer voxel mit label entlang des strahls, ausgeblendete zellen durchschaut
        public bool TryPickCell(Ray worldRay, out int id, out Vector3 hitLocal)
        {
            id = 0;
            hitLocal = default;
            NiftiVolumeLoader.Volume vol = LoadedVolume;
            if (vol?.Labels == null) return false;

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
                int label = vol.Labels[x + vol.SizeX * (y + vol.SizeY * z)];
                if (label > 0 && IsCellVisible(label))
                {
                    id = label;
                    hitLocal = p;
                    return true;
                }
            }
            return false;
        }

        // gleiche box wie IntersectBox im shader
        public static bool IntersectUnitBox(Vector3 o, Vector3 d, out float tNear, out float tFar)
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
        public Vector3 VoxelToLocal(Vector3 voxel)
        {
            NiftiVolumeLoader.Volume vol = LoadedVolume;
            return new Vector3(voxel.x / vol.SizeX - 0.5f, voxel.y / vol.SizeY - 0.5f, voxel.z / vol.SizeZ - 0.5f);
        }

        // feuert nach jedem erfolgreichen laden, auch beim nachladen
        public event Action<NiftiVolumeLoader.Volume> OnVolumeLoaded;

        private static readonly int VolumeTexId = Shader.PropertyToID("_VolumeTex");
        private static readonly int IsColorId = Shader.PropertyToID("_IsColor");
        private static readonly int IsLiveChannelsId = Shader.PropertyToID("_IsLiveChannels");
        private static readonly int Channel0OnId = Shader.PropertyToID("_Channel0On");
        private static readonly int Channel1OnId = Shader.PropertyToID("_Channel1On");
        private static readonly int Channel2OnId = Shader.PropertyToID("_Channel2On");
        private static readonly int VolumeTexelSizeId = Shader.PropertyToID("_VolumeTexelSize");
        private static readonly int VolumeSizeId = Shader.PropertyToID("_VolumeSize");
        private static readonly int BrickTexId = Shader.PropertyToID("_BrickTex");
        private static readonly int BrickCountId = Shader.PropertyToID("_BrickCount");
        private static readonly int DensityId = Shader.PropertyToID("_Density");
        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        private static readonly int ThresholdMaxId = Shader.PropertyToID("_ThresholdMax");
        private static readonly int RenderModeId = Shader.PropertyToID("_RenderMode");
        private static readonly int EnableShadingId = Shader.PropertyToID("_EnableShading");
        private static readonly int LabelTexId = Shader.PropertyToID("_LabelTex");
        private static readonly int HasLabelsId = Shader.PropertyToID("_HasLabels");
        private static readonly int ShowLabelsId = Shader.PropertyToID("_ShowLabels");
        private static readonly int SelectedLabelId = Shader.PropertyToID("_SelectedLabel");
        private static readonly int LabelStrengthId = Shader.PropertyToID("_LabelStrength");
        private static readonly int HoverLabelId = Shader.PropertyToID("_HoverLabel");
        private static readonly int LabelVisTexId = Shader.PropertyToID("_LabelVisTex");
        private static readonly int LampPosId = Shader.PropertyToID("_LampPosWS");
        private static readonly int LampRangeId = Shader.PropertyToID("_LampRange");

        // shader defaults fest verdrahtet, jedes bild startet mit denselben reglern
        private const float DefaultDensity = 1.0f;
        private const float DefaultThreshold = 0.12f;
        private const float DefaultThresholdMax = 1.0f;
        private const float DefaultRenderMode = 0f;
        private const float DefaultEnableShading = 1f;

        // im editor voller pfad, auf android derselbe dateiname aus persistentDataPath
        private static string ResolvePlatformPath(string rawPath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // GetFileName trennt auf android nur bei slash, backslash pfad vorher normalisieren
            string normalized = rawPath.Replace('\\', '/');
            return System.IO.Path.Combine(Application.persistentDataPath, System.IO.Path.GetFileName(normalized));
#else
            return rawPath;
#endif
        }

        private void Start()
        {
            // haelt die bildrate ueber die schrittweite statt ueber die aufloesung
            if (GetComponent<VolumeStepGovernor>() == null) gameObject.AddComponent<VolumeStepGovernor>();
            // collider des wuerfels weg, der strahl endet sonst an der wuerfelwand
            Collider box = GetComponent<Collider>();
            if (box != null) Destroy(box);
            if (!string.IsNullOrEmpty(VolumeFilePath)) LoadVolume(VolumeFilePath);
        }

        // kopflampe fuer den surface modus, reichweite in metern im raum
        [SerializeField] private float lampRangeInside = 0.6f;
        private MeshRenderer lampRenderer;

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null || LoadedVolume == null) return;
            if (lampRenderer == null) lampRenderer = GetComponent<MeshRenderer>();

            Vector3 head = cam.transform.position;
            Vector3 local = transform.InverseTransformPoint(head);
            Vector3 nearest = new Vector3(
                Mathf.Clamp(local.x, -0.5f, 0.5f), Mathf.Clamp(local.y, -0.5f, 0.5f), Mathf.Clamp(local.z, -0.5f, 0.5f));
            float outside = Vector3.Distance(transform.TransformPoint(nearest), head);

            Material mat = lampRenderer.material;
            mat.SetVector(LampPosId, head);
            mat.SetFloat(LampRangeId, lampRangeInside + 1.5f * outside);
        }

        // zaehlt je LoadVolume hoch, ueberholte antworten erkennen sich daran
        private int loadToken;

        public async void LoadVolume(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                Debug.LogError("VolumeView: kein pfad angegeben.", this);
                return;
            }

            string resolvedPath = ResolvePlatformPath(rawPath);
            int myToken = ++loadToken;

            // parsen im background thread, sonst friert der hauptthread ein
            NiftiVolumeLoader.RawVolumeData data;
            NiftiVolumeLoader.RawLabelData labelData = null;
            CellInfo[] cells = null;
            NiftiVolumeLoader.BrickMap bricks = null;
            Dictionary<int, string> names = null;
            try
            {
                data = await Task.Run(() =>
                {
                    var parsed = NiftiVolumeLoader.ParseFile(resolvedPath);
                    bricks = NiftiVolumeLoader.ComputeBricks(parsed);
                    labelData = TryParseLabels(resolvedPath, parsed);
                    if (labelData != null) names = TryReadLabelNames(resolvedPath);
                    if (labelData != null) cells = CellStats.Compute(labelData, parsed.Voxels, parsed.IsColor);
                    return parsed;
                });
            }
            catch (System.Exception e)
            {
                Debug.LogError($"VolumeView: failed to load '{resolvedPath}': {e.Message}", this);
                return;
            }

            // inzwischen ein neueres volumen angefordert, antwort ueberholt
            if (myToken != loadToken) return;

            // texture3d bauen muss auf dem hauptthread laufen (unity api)
            NiftiVolumeLoader.Volume volume = NiftiVolumeLoader.BuildTexture(data);
            volume.BrickTexture = NiftiVolumeLoader.BuildBrickTexture(bricks);
            if (labelData != null)
            {
                volume.Labels = labelData.Labels;
                volume.LabelTexture = NiftiVolumeLoader.BuildLabelTexture(labelData);
                volume.MaxLabel = labelData.MaxLabel;
                volume.LabelCount = labelData.LabelCount;
                volume.Cells = cells;
            }

            // voxelgroesse aus metadata.json, nifti pixdim ist immer 1,1,1
            Vector3? voxelSize = TryReadVoxelSizeFromMetadata(resolvedPath, out string voxelUnit);
            if (voxelSize.HasValue) volume.VoxelSize = voxelSize.Value;
            HasPhysicalVoxelSize = voxelSize.HasValue;
            unit = voxelUnit;

            if (LoadedVolume != null)
            {
                Destroy(LoadedVolume.Texture);
                if (LoadedVolume.LabelTexture != null) Destroy(LoadedVolume.LabelTexture);
                if (LoadedVolume.BrickTexture != null) Destroy(LoadedVolume.BrickTexture);
            }

            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.material.SetTexture(VolumeTexId, volume.Texture);
            // leere bloecke ueberspringt der shader, kostet beim laden einen bruchteil einer sekunde
            meshRenderer.material.SetTexture(BrickTexId, volume.BrickTexture);
            meshRenderer.material.SetVector(BrickCountId, new Vector4(
                volume.BrickTexture.width, volume.BrickTexture.height, volume.BrickTexture.depth, NiftiVolumeLoader.BrickSize));
            meshRenderer.material.SetFloat(IsColorId, volume.IsColor ? 1f : 0f);
            meshRenderer.material.SetFloat(IsLiveChannelsId, volume.IsLiveChannels ? 1f : 0f);
            // neues volumen startet mit allen kanaelen an
            meshRenderer.material.SetFloat(Channel0OnId, 1f);
            meshRenderer.material.SetFloat(Channel1OnId, 1f);
            meshRenderer.material.SetFloat(Channel2OnId, 1f);
            // regler auf startwerte, nicht die vom vorigen bild
            meshRenderer.material.SetFloat(DensityId, DefaultDensity);
            meshRenderer.material.SetFloat(ThresholdId, DefaultThreshold);
            meshRenderer.material.SetFloat(ThresholdMaxId, DefaultThresholdMax);
            meshRenderer.material.SetFloat(RenderModeId, DefaultRenderMode);
            meshRenderer.material.SetFloat(EnableShadingId, DefaultEnableShading);
            // segmentierung startet sichtbar wenn vorhanden
            bool hasLabels = volume.LabelTexture != null;
            if (hasLabels) meshRenderer.material.SetTexture(LabelTexId, volume.LabelTexture);
            meshRenderer.material.SetFloat(HasLabelsId, hasLabels ? 1f : 0f);
            meshRenderer.material.SetFloat(ShowLabelsId, hasLabels ? 1f : 0f);
            meshRenderer.material.SetFloat(SelectedLabelId, 0f);
            meshRenderer.material.SetFloat(HoverLabelId, 0f);
            selectedLabel = 0;
            hoverLabel = 0;
            if (hasLabels) ResetVisibility(meshRenderer.material);
            meshRenderer.material.SetVector(VolumeTexelSizeId,
                new Vector4(1.5f / volume.SizeX, 1.5f / volume.SizeY, 1.5f / volume.SizeZ, 0f));
            // echte texelanzahl fuer SampleVoxelCubic
            meshRenderer.material.SetVector(VolumeSizeId,
                new Vector4(volume.SizeX, volume.SizeY, volume.SizeZ, 0f));

            // wuerfel skalieren gegen verzerrung bei nicht kubischem voxel abstand
            Vector3 physicalSize = new Vector3(
                volume.SizeX * volume.VoxelSize.x,
                volume.SizeY * volume.VoxelSize.y,
                volume.SizeZ * volume.VoxelSize.z);
            float maxDimension = Mathf.Max(physicalSize.x, physicalSize.y, physicalSize.z);
            transform.localScale = physicalSize / maxDimension;

            VolumeFilePath = rawPath;
            ResolvedVolumePath = resolvedPath;
            LoadedVolume = volume;
            labelNames = names;
            OnVolumeLoaded?.Invoke(volume);
            // "evtl. 2 zellen" ergibt bei organen keinen sinn
            if (volume.Cells != null && !HasLabelNames) AnalyzeShapes(volume);
            SelectionChanged?.Invoke(0);
        }

        // neues bild, alle zellen wieder sichtbar
        private void ResetVisibility(Material material)
        {
            if (visibilityTexture == null)
            {
                visibilityTexture = new Texture2D(VisibilitySize, VisibilitySize, TextureFormat.R8, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                visibility = new byte[VisibilitySize * VisibilitySize];
            }
            for (int i = 0; i < visibility.Length; i++) visibility[i] = 255;
            visibilityTexture.SetPixelData(visibility, 0);
            visibilityTexture.Apply(false);
            material.SetTexture(LabelVisTexId, visibilityTexture);
        }

        // <name>_labels.nii.gz im selben ordner, falsche groesse nur warnen
        private static NiftiVolumeLoader.RawLabelData TryParseLabels(string volumePath, NiftiVolumeLoader.RawVolumeData volume)
        {
            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string labelsPath = Path.Combine(dir, $"{stem}_labels.nii.gz");
            // korrektur die nicht ueber die aufgespielte datei geschrieben werden durfte, siehe LabelEditor.Save
            string editedPath = Path.Combine(dir, $"{stem}_labels_edited.nii.gz");
            if (File.Exists(editedPath) && (!File.Exists(labelsPath)
                || File.GetLastWriteTimeUtc(editedPath) > File.GetLastWriteTimeUtc(labelsPath))) labelsPath = editedPath;
            if (!File.Exists(labelsPath)) return null;

            try
            {
                var labels = NiftiVolumeLoader.ParseLabels(labelsPath);
                if (labels.SizeX != volume.SizeX || labels.SizeY != volume.SizeY || labels.SizeZ != volume.SizeZ)
                {
                    Debug.LogWarning($"VolumeView: '{labelsPath}' hat {labels.SizeX}x{labels.SizeY}x{labels.SizeZ}, " +
                                     $"volumen {volume.SizeX}x{volume.SizeY}x{volume.SizeZ}, labels ignoriert");
                    return null;
                }
                return labels;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VolumeView: labels '{labelsPath}' nicht lesbar: {e.Message}");
                return null;
            }
        }

        [Serializable] private class LabelNamesFile { public string kind; public LabelNameEntry[] labels; }
        [Serializable] private class LabelNameEntry { public int id; public string name; }

        // <name>_labels_names.json neben der maske, fehlt bei zell segmentierungen
        private static Dictionary<int, string> TryReadLabelNames(string volumePath)
        {
            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string path = Path.Combine(dir, $"{stem}_labels_names.json");
            if (!File.Exists(path)) return null;
            try
            {
                var parsed = JsonUtility.FromJson<LabelNamesFile>(File.ReadAllText(path));
                if (parsed?.labels == null) return null;
                var names = new Dictionary<int, string>();
                foreach (LabelNameEntry entry in parsed.labels)
                    if (entry.id > 0 && !string.IsNullOrEmpty(entry.name)) names[entry.id] = entry.name;
                return names;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VolumeView: namen '{path}' nicht lesbar: {e.Message}");
                return null;
            }
        }

        // neu: voxel_size plus unit, alt und von idr: nur voxel_size_um
        [Serializable] private class VoxelSizeMetadata
        {
            public Vec voxel_size;
            public string unit;
            public Vec voxel_size_um;
        }
        [Serializable] private class Vec { public float x, y, z; }

        private static bool Valid(Vec v) => v != null && v.x > 0 && v.y > 0 && v.z > 0;

        // <name>_metadata.json zuerst, sonst metadata.json im selben ordner
        private static Vector3? TryReadVoxelSizeFromMetadata(string volumePath, out string unit)
        {
            unit = "µm";
            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string namedPath = Path.Combine(dir, $"{stem}_metadata.json");
            string sharedPath = Path.Combine(dir, "metadata.json");
            string path = File.Exists(namedPath) ? namedPath : File.Exists(sharedPath) ? sharedPath : null;
            if (path == null) return null;

            try
            {
                var parsed = JsonUtility.FromJson<VoxelSizeMetadata>(File.ReadAllText(path));
                if (parsed == null) return null;
                // jsonutility legt fehlende objekte mit nullen an statt null, deshalb Valid
                if (Valid(parsed.voxel_size) && !string.IsNullOrEmpty(parsed.unit))
                {
                    unit = parsed.unit == "um" ? "µm" : parsed.unit;
                    return new Vector3(parsed.voxel_size.x, parsed.voxel_size.y, parsed.voxel_size.z);
                }
                if (!Valid(parsed.voxel_size_um)) return null;
                return new Vector3(parsed.voxel_size_um.x, parsed.voxel_size_um.y, parsed.voxel_size_um.z);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VolumeView: konnte voxel_size_um aus '{path}' nicht lesen: {e.Message}");
                return null;
            }
        }
    }
}
