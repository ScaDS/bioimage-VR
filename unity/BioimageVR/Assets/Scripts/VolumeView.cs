using System;
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

        // feuert nach jedem erfolgreichen laden, auch beim nachladen
        public event Action<NiftiVolumeLoader.Volume> OnVolumeLoaded;

        private static readonly int VolumeTexId = Shader.PropertyToID("_VolumeTex");
        private static readonly int IsColorId = Shader.PropertyToID("_IsColor");
        private static readonly int IsLiveChannelsId = Shader.PropertyToID("_IsLiveChannels");
        private static readonly int Channel0OnId = Shader.PropertyToID("_Channel0On");
        private static readonly int Channel1OnId = Shader.PropertyToID("_Channel1On");
        private static readonly int Channel2OnId = Shader.PropertyToID("_Channel2On");
        private static readonly int VolumeTexelSizeId = Shader.PropertyToID("_VolumeTexelSize");
        private static readonly int DensityId = Shader.PropertyToID("_Density");
        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        private static readonly int ThresholdMaxId = Shader.PropertyToID("_ThresholdMax");
        private static readonly int RenderModeId = Shader.PropertyToID("_RenderMode");
        private static readonly int EnableShadingId = Shader.PropertyToID("_EnableShading");

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

        public void LoadVolume(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                Debug.LogError("VolumeView: kein pfad angegeben.", this);
                return;
            }

            string resolvedPath = ResolvePlatformPath(rawPath);
            NiftiVolumeLoader.Volume volume;
            try
            {
                volume = NiftiVolumeLoader.Load(resolvedPath);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"VolumeView: failed to load '{resolvedPath}': {e.Message}", this);
                return;
            }

            if (LoadedVolume != null) Destroy(LoadedVolume.Texture);

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
            meshRenderer.material.SetVector(VolumeTexelSizeId,
                new Vector4(1.5f / volume.SizeX, 1.5f / volume.SizeY, 1.5f / volume.SizeZ, 0f));

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
    }
}
