using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace BioimageVR
{
    // laedt nii volumen, setzt es aufs material fuer den shader
    // LoadVolume kann auch spaeter nochmal aufgerufen werden, fuer die galerie
    [RequireComponent(typeof(MeshRenderer))]
    public class VolumeView : MonoBehaviour
    {
        [Tooltip("Path to a .nii or .nii.gz file produced by preprocessing/convert_to_volume.py")]
        public string VolumeFilePath;

        // aktuell geladenes volumen, null bis zum ersten erfolgreichen laden
        public NiftiVolumeLoader.Volume LoadedVolume { get; private set; }

        // false wenn kein metadata.json, dann ist VoxelSize nur pixdim ohne echte einheit
        public bool HasPhysicalVoxelSize { get; private set; }

        // true wenn eine <name>_labels.nii.gz mitgeladen wurde
        public bool HasLabels => LoadedVolume?.Labels != null;

        // overlay an aus, nur wirksam wenn labels da sind
        public bool ShowLabels
        {
            get => HasLabels && GetComponent<MeshRenderer>().material.GetFloat(ShowLabelsId) > 0.5f;
            set => GetComponent<MeshRenderer>().material.SetFloat(ShowLabelsId, value && HasLabels ? 1f : 0f);
        }

        // hebt eine zelle hervor, 0 fuer keine
        public void SetSelectedLabel(int label) =>
            GetComponent<MeshRenderer>().material.SetFloat(SelectedLabelId, label);

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
        private static readonly int DensityId = Shader.PropertyToID("_Density");
        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        private static readonly int ThresholdMaxId = Shader.PropertyToID("_ThresholdMax");
        private static readonly int RenderModeId = Shader.PropertyToID("_RenderMode");
        private static readonly int EnableShadingId = Shader.PropertyToID("_EnableShading");
        private static readonly int LabelTexId = Shader.PropertyToID("_LabelTex");
        private static readonly int HasLabelsId = Shader.PropertyToID("_HasLabels");
        private static readonly int ShowLabelsId = Shader.PropertyToID("_ShowLabels");
        private static readonly int SelectedLabelId = Shader.PropertyToID("_SelectedLabel");

        // shader-defaults aus VolumeRaymarch.shader, gleiche werte hier fest verdrahtet -
        // jedes volumen bekommt seinen eigenen kontrast/threshold schon beim konvertieren
        // (perzentil-streckung), diese regler sollen also bei jedem bild gleich starten
        // statt vom vorher geladenen bild uebernommen zu werden
        private const float DefaultDensity = 1.0f;
        private const float DefaultThreshold = 0.12f;
        private const float DefaultThresholdMax = 1.0f;
        private const float DefaultRenderMode = 0f;
        private const float DefaultEnableShading = 1f;

        // im editor voller windows pfad, auf android geht das nicht
        // dort denselben dateinamen aus persistentDataPath nehmen
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
            if (!string.IsNullOrEmpty(VolumeFilePath)) LoadVolume(VolumeFilePath);
        }

        // zaehlt hoch bei jedem LoadVolume aufruf, eine spaeter ueberholte antwort
        // (schneller doppelklick in der galerie) erkennt sich daran und wird verworfen
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

            // dekomprimieren/parsen ist reine cpu arbeit und kostet bei grossen dateien
            // spuerbar zeit, deshalb auf einen background thread ausgelagert, damit der
            // hauptthread (und damit frame/xr compositor) dabei nicht einfriert
            NiftiVolumeLoader.RawVolumeData data;
            NiftiVolumeLoader.RawLabelData labelData = null;
            try
            {
                data = await Task.Run(() =>
                {
                    var parsed = NiftiVolumeLoader.ParseFile(resolvedPath);
                    labelData = TryParseLabels(resolvedPath, parsed);
                    return parsed;
                });
            }
            catch (System.Exception e)
            {
                Debug.LogError($"VolumeView: failed to load '{resolvedPath}': {e.Message}", this);
                return;
            }

            // waehrend des wartens wurde ein neueres volumen angefordert, diese
            // antwort ist ueberholt
            if (myToken != loadToken) return;

            // texture3d bauen muss auf dem hauptthread laufen (unity api)
            NiftiVolumeLoader.Volume volume = NiftiVolumeLoader.BuildTexture(data);
            if (labelData != null)
            {
                volume.Labels = labelData.Labels;
                volume.LabelTexture = NiftiVolumeLoader.BuildLabelTexture(labelData);
                volume.MaxLabel = labelData.MaxLabel;
                volume.LabelCount = labelData.LabelCount;
            }

            // nifti pixdim ist aktuell immer 1,1,1 (siehe convert_to_volume.py, identitaets
            // affine) - die echte, von idr gemessene voxelgroesse liegt stattdessen im
            // metadata.json neben der datei. ohne das waere jeder wuerfel faelschlich
            // isotrop, und massstab/messtool haetten keine echte grundlage
            Vector3? voxelSizeUm = TryReadVoxelSizeFromMetadata(resolvedPath);
            if (voxelSizeUm.HasValue) volume.VoxelSize = voxelSizeUm.Value;
            HasPhysicalVoxelSize = voxelSizeUm.HasValue;

            if (LoadedVolume != null)
            {
                Destroy(LoadedVolume.Texture);
                if (LoadedVolume.LabelTexture != null) Destroy(LoadedVolume.LabelTexture);
            }

            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.material.SetTexture(VolumeTexId, volume.Texture);
            meshRenderer.material.SetFloat(IsColorId, volume.IsColor ? 1f : 0f);
            meshRenderer.material.SetFloat(IsLiveChannelsId, volume.IsLiveChannels ? 1f : 0f);
            // frisch geladenes volumen startet mit allen kanaelen an, sonst bleibt ein
            // "kanal aus" von einem vorherigen volumen faelschlich haengen
            meshRenderer.material.SetFloat(Channel0OnId, 1f);
            meshRenderer.material.SetFloat(Channel1OnId, 1f);
            meshRenderer.material.SetFloat(Channel2OnId, 1f);
            // sonst haengen threshold/density/etc vom zuletzt geladenen bild noch dran -
            // jedes bild ist eigenstaendig perzentil-gestreckt, braucht also dieselben
            // startwerte, nicht die von einem anderen bild feinjustierten
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
            meshRenderer.material.SetVector(VolumeTexelSizeId,
                new Vector4(1.5f / volume.SizeX, 1.5f / volume.SizeY, 1.5f / volume.SizeZ, 0f));
            // fuer SampleVoxelCubic (tricubic filterung), echte texelanzahl statt
            // der 1.5x-skalierten VolumeTexelSize
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
            LoadedVolume = volume;
            OnVolumeLoaded?.Invoke(volume);
        }

        // <name>_labels.nii.gz im selben ordner, siehe preprocessing/convert_labels.py
        // falsche groesse nur warnen, bild laedt trotzdem
        private static NiftiVolumeLoader.RawLabelData TryParseLabels(string volumePath, NiftiVolumeLoader.RawVolumeData volume)
        {
            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string labelsPath = Path.Combine(dir, $"{stem}_labels.nii.gz");
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

        [Serializable] private class VoxelSizeMetadata { public VoxelSizeUm voxel_size_um; }
        [Serializable] private class VoxelSizeUm { public float x, y, z; }

        // <name>_metadata.json zuerst (android, flach im persistentDataPath gepusht),
        // sonst metadata.json im selben ordner (editor/standalone, ein ordner pro bild) -
        // gleiches schema wie VolumeMetadataPanel.Refresh
        private static Vector3? TryReadVoxelSizeFromMetadata(string volumePath)
        {
            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string namedPath = Path.Combine(dir, $"{stem}_metadata.json");
            string sharedPath = Path.Combine(dir, "metadata.json");
            string path = File.Exists(namedPath) ? namedPath : File.Exists(sharedPath) ? sharedPath : null;
            if (path == null) return null;

            try
            {
                var parsed = JsonUtility.FromJson<VoxelSizeMetadata>(File.ReadAllText(path));
                if (parsed?.voxel_size_um == null) return null;
                var v = parsed.voxel_size_um;
                if (v.x <= 0 || v.y <= 0 || v.z <= 0) return null;
                return new Vector3(v.x, v.y, v.z);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VolumeView: konnte voxel_size_um aus '{path}' nicht lesen: {e.Message}");
                return null;
            }
        }
    }
}
