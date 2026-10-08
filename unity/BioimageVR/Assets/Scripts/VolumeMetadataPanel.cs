using System;
using System.IO;
using TMPro;
using UnityEngine;

namespace BioimageVR
{
    // karte "Current image" oben im images tab, das wichtigste aus metadata.json
    // name, groesse, voxelgroesse, kanaele. anzeige kommt vom SidePanelController
    public class VolumeMetadataPanel : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;

        private TMP_Text titleText;
        private TMP_Text detailText;

        [Serializable] private class Metadata
        {
            public string name;
            public int image_id;
            public Size size;
            public Voxel voxel_size_um;
            public string[] channels;
            public string source;
        }

        [Serializable] private class Size { public int x, y, z, c; }
        [Serializable] private class Voxel { public float x, y, z; }

        // kanalnamen aus metadata.json, frisch gelesen, null wenn keine da
        public string[] ChannelNames
        {
            get
            {
                string path = volumeView != null ? volumeView.ResolvedVolumePath ?? volumeView.VolumeFilePath : null;
                if (string.IsNullOrEmpty(path)) return null;
                string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
                return ReadMetadata(path, stem)?.channels;
            }
        }

        public void Bind(TMP_Text title, TMP_Text details)
        {
            titleText = title;
            detailText = details;
            Refresh();
        }

        private void OnEnable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            Refresh();
        }

        private void OnDisable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => Refresh();

        // <name>_metadata.json zuerst (android, flach im persistentDataPath gepusht),
        // sonst metadata.json im selben ordner (editor, ein ordner pro bild)
        private void Refresh()
        {
            if (titleText == null || volumeView == null) return;
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            string volumePath = volumeView.ResolvedVolumePath ?? volumeView.VolumeFilePath;
            if (vol == null || string.IsNullOrEmpty(volumePath))
            {
                titleText.text = "No image loaded";
                detailText.text = "Pick one below or send one from napari.";
                return;
            }

            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            Metadata meta = ReadMetadata(volumePath, stem);
            string mono = UITheme.FontAssetName(UITheme.FontRole.Mono);

            string title = meta?.name ?? stem;
            if (meta != null && meta.image_id > 0) title += $"  ·  IDR {meta.image_id}";
            titleText.text = title;

            string voxel = volumeView.HasPhysicalVoxelSize
                ? $"{vol.VoxelSize.x:0.###} × {vol.VoxelSize.y:0.###} × {vol.VoxelSize.z:0.###} {volumeView.Unit}"
                : "voxel size unknown";
            string channels = meta?.channels != null && meta.channels.Length > 0 ? string.Join(" · ", meta.channels)
                : vol.IsLiveChannels ? "3 channels" : vol.IsColor ? "colour" : "1 channel";
            string labels = vol.Labels != null ? $"\n{vol.LabelCount} segmented cells" : "";
            detailText.text = $"{channels}\n<font=\"{mono}\">{vol.SizeX} × {vol.SizeY} × {vol.SizeZ} · {voxel}</font>{labels}";
        }

        private static Metadata ReadMetadata(string volumePath, string stem)
        {
            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string namedPath = Path.Combine(dir, $"{stem}_metadata.json");
            string sharedPath = Path.Combine(dir, "metadata.json");
            string path = File.Exists(namedPath) ? namedPath : File.Exists(sharedPath) ? sharedPath : null;
            if (path == null) return null;
            try
            {
                return JsonUtility.FromJson<Metadata>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
