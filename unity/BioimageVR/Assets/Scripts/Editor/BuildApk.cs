using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BioimageVR.EditorSetup
{
    // apk im batchmode bauen, executeMethod BioimageVR.EditorSetup.BuildApk.Build
    public static class BuildApk
    {
        private const string OutputPath = "Builds/bioimagevr.apk";

        [MenuItem("BioimageVR/Build APK")]
        public static void Build()
        {
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };
            BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"BioimageVR: apk gebaut, {OutputPath}");
                return;
            }
            Debug.LogError($"BioimageVR: build fehlgeschlagen, {summary.totalErrors} fehler");
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
