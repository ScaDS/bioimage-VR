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

            var entries = paths
                .OrderBy(p => p)
                .Select(p => new Entry { Path = p, DisplayName = ResolveDisplayName(p) })
                .ToList();
            return entries;
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
