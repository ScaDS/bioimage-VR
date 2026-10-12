using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BioimageVR
{
    // findet alle nii.gz volumen, android in persistentDataPath, sonst im data ordner
    public static class VolumeLibrary
    {
        public struct Entry
        {
            public string Path;
            public string DisplayName;
            // leer wenn keine vorschau daneben liegt (siehe fetch_from_idr.thumbnail_path_for)
            public string ThumbnailPath;
            // eine <name>_labels.nii.gz liegt daneben
            public bool HasLabels;
        }

        [Serializable] private class Metadata { public string name; public int image_id; }

        private static readonly System.Text.RegularExpressions.Regex LabelsFilePattern =
            new System.Text.RegularExpressions.Regex(@"_labels(_v\d+|_edited)?\.nii\.gz$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static List<Entry> Discover()
        {
            var paths = new List<string>();

#if UNITY_ANDROID && !UNITY_EDITOR
            if (Directory.Exists(Application.persistentDataPath))
                paths.AddRange(Directory.GetFiles(Application.persistentDataPath, "*.nii.gz"));
#else
            string dataRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "data"));
            if (Directory.Exists(dataRoot))
                paths.AddRange(Directory.GetFiles(dataRoot, "*.nii.gz", SearchOption.AllDirectories));
#endif

            // masken sind keine eigenen galerie eintraege, auch versionen wie _labels_v002
            var entries = paths
                .Where(p => !LabelsFilePattern.IsMatch(p))
                .OrderBy(p => p)
                .Select(p => new Entry
                {
                    Path = p,
                    DisplayName = ResolveDisplayName(p),
                    ThumbnailPath = ResolveThumbnailPath(p),
                    HasLabels = HasLabels(p)
                })
                .ToList();
            return entries;
        }

        // <name>_thumbnail.jpg, kollisionsfrei wenn alles flach in einem ordner liegt
        private static string ResolveThumbnailPath(string volumePath)
        {
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string thumbnailPath = Path.Combine(Path.GetDirectoryName(volumePath) ?? "", $"{stem}_thumbnail.jpg");
            return File.Exists(thumbnailPath) ? thumbnailPath : "";
        }

        // name aus metadata.json, suffix der variante anhaengen, sonst heissen alle gleich
        private static string ResolveDisplayName(string volumePath)
        {
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string name = stem;

            string metadataPath = Path.Combine(Path.GetDirectoryName(volumePath) ?? "", "metadata.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    var metadata = JsonUtility.FromJson<Metadata>(File.ReadAllText(metadataPath));
                    if (!string.IsNullOrEmpty(metadata?.name))
                    {
                        // bilder aus napari haben keine idr id
                        name = metadata.image_id > 0 ? $"{metadata.name} ({metadata.image_id})" : metadata.name;
                        string prefix = $"idr_{metadata.image_id}_";
                        if (stem.StartsWith(prefix)) name += $" {stem.Substring(prefix.Length)}";
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"VolumeLibrary: konnte {metadataPath} nicht lesen: {e.Message}");
                }
            }

            return name;
        }

        // gleiche namensregel wie VolumeView.TryParseLabels
        private static bool HasLabels(string volumePath)
        {
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            return File.Exists(Path.Combine(Path.GetDirectoryName(volumePath) ?? "", $"{stem}_labels.nii.gz"));
        }
    }
}
