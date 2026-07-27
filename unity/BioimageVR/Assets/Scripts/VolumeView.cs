using System;
using UnityEngine;

namespace BioimageVR
{
    // laedt nii volumen beim start, setzt es aufs material fuer den shader
    // phase 1, keine interaktion, nur anzeigen
    [RequireComponent(typeof(MeshRenderer))]
    public class VolumeView : MonoBehaviour
    {
        [Tooltip("Path to a .nii or .nii.gz file produced by preprocessing/convert_to_volume.py")]
        public string VolumeFilePath;

        // volumen nach start geladen, sonst null
        public NiftiVolumeLoader.Volume LoadedVolume { get; private set; }

        // feuert einmal nach erfolgreichem laden
        public event Action<NiftiVolumeLoader.Volume> OnVolumeLoaded;

        private static readonly int VolumeTexId = Shader.PropertyToID("_VolumeTex");

        // im editor voller windows pfad, auf android geht das nicht
        // dort denselben dateinamen aus persistentDataPath nehmen
        private string ResolveVolumeFilePath()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // GetFileName trennt auf android nur bei slash, backslash pfad vorher normalisieren
            string normalized = VolumeFilePath.Replace('\\', '/');
            return System.IO.Path.Combine(Application.persistentDataPath, System.IO.Path.GetFileName(normalized));
#else
            return VolumeFilePath;
#endif
        }

        private void Start()
        {
            if (string.IsNullOrEmpty(VolumeFilePath))
            {
                Debug.LogError("VolumeView: VolumeFilePath is not set.", this);
                return;
            }

            string resolvedPath = ResolveVolumeFilePath();
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

            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.material.SetTexture(VolumeTexId, volume.Texture);

            // wuerfel skalieren gegen verzerrung bei nicht kubischem voxel abstand
            Vector3 physicalSize = new Vector3(
                volume.SizeX * volume.VoxelSize.x,
                volume.SizeY * volume.VoxelSize.y,
                volume.SizeZ * volume.VoxelSize.z);
            float maxDimension = Mathf.Max(physicalSize.x, physicalSize.y, physicalSize.z);
            transform.localScale = physicalSize / maxDimension;

            LoadedVolume = volume;
            OnVolumeLoaded?.Invoke(volume);
        }
    }
}
