using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace BioimageVR.EditorSetup
{
    // installiert phase 1 packages ueber die package manager api
    // nicht selbst quit aufrufen, laeuft async und beendet sich selbst
    public static class InstallPackages
    {
        private static AddAndRemoveRequest _request;

        public static void Run()
        {
            string[] toAdd =
            {
                "com.unity.render-pipelines.universal",
                "com.unity.xr.openxr",
                "com.unity.xr.interaction.toolkit",
                "com.unity.inputsystem",
            };

            _request = Client.AddAndRemove(toAdd);
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (!_request.IsCompleted) return;

            EditorApplication.update -= Poll;

            if (_request.Status == StatusCode.Success)
            {
                Debug.Log("BioimageVR: packages installed successfully.");
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError($"BioimageVR: package install failed: {_request.Error?.message}");
                EditorApplication.Exit(1);
            }
        }
    }
}
