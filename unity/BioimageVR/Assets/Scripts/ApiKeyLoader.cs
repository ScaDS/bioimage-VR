using System;
using System.IO;
using UnityEngine;

namespace BioimageVR
{
    // api key laden fuer vlm und stt
    // quest hat keinen home ordner, dort persistentDataPath nutzen
    internal static class ApiKeyLoader
    {
        private const string FileName = ".scadsai-api-key";

        public static string Load(string clientName)
        {
            string path = ResolvePath();
            if (!File.Exists(path))
            {
                Debug.LogError($"{clientName}: no key file at {path}. Create it with your " +
                                "ScaDS.AI LLM API key (same file the company's Python examples use).");
                return null;
            }

            string key = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(key) ? null : key;
        }

        private static string ResolvePath()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Path.Combine(Application.persistentDataPath, FileName);
#else
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FileName);
#endif
        }
    }
}
