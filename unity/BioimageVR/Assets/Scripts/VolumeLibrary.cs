using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BioimageVR
{
    // findet alle verfuegbaren nii.gz volumen fuer die galerie
    // android: schon gepushte dateien in persistentDataPath
    // editor und standalone: lokaler data ordner im projekt
    public static class VolumeLibrary
    {
        public struct Entry
        {
            public string Path;
            public string DisplayName;
            // leer wenn keine vorschau daneben liegt (siehe fetch_from_idr.thumbnail_path_for)
            public string ThumbnailPath;
        }

        [Serializable] private class Metadata { public string name; public int image_id; }

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

            // masken gehoeren zu einem bild, keine eigenen galerie eintraege
            var entries = paths
                .Where(p => !p.EndsWith("_labels.nii.gz", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .Select(p => new Entry
                {
                    Path = p,
                    DisplayName = ResolveDisplayName(p),
                    ThumbnailPath = ResolveThumbnailPath(p)
                })
                .ToList();
            return entries;
        }

        // <name>_thumbnail.jpg statt fix "thumbnail.jpg" (siehe fetch_from_idr.
        // thumbnail_path_for) - kollisionsfrei falls mehrere volumen im selben ordner
        // liegen, z.b. flach in persistentDataPath auf android
        private static string ResolveThumbnailPath(string volumePath)
        {
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string thumbnailPath = Path.Combine(Path.GetDirectoryName(volumePath) ?? "", $"{stem}_thumbnail.jpg");
            return File.Exists(thumbnailPath) ? thumbnailPath : "";
        }

        // sucht ein metadata.json daneben (siehe fetch_from_idr.py), sonst nur dateiname
        private static string ResolveDisplayName(string volumePath)
        {
            string metadataPath = Path.Combine(Path.GetDirectoryName(volumePath) ?? "", "metadata.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    var metadata = JsonUtility.FromJson<Metadata>(File.ReadAllText(metadataPath));
                    if (!string.IsNullOrEmpty(metadata?.name))
                        return $"{metadata.name} ({metadata.image_id})";
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"VolumeLibrary: konnte {metadataPath} nicht lesen: {e.Message}");
                }
            }

            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
        }
    }
}
