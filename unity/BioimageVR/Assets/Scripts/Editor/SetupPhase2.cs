using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BioimageVR.EditorSetup
{
    // setup fuer phase 2, slice panel mit marker punkt, phase2markerdemo, volumezoom
    // sicher erneut ausfuehrbar
    public static class SetupPhase2
    {
        private const string ScenePath = "Assets/Scenes/Phase1.unity";
        private const int PanelSize = 320;

        [MenuItem("BioimageVR/Setup Phase 2")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath && File.Exists(ScenePath))
                scene = EditorSceneManager.OpenScene(ScenePath);

            var volumeGO = GameObject.Find("Volume");
            if (volumeGO == null)
            {
                Debug.LogError("BioimageVR: no 'Volume' GameObject found in the scene - run " +
                                "BioimageVR/Setup Phase 1 Scene first.");
                return;
            }

            var volumeView = volumeGO.GetComponent<VolumeView>();
            if (volumeView == null)
            {
                Debug.LogError("BioimageVR: 'Volume' GameObject has no VolumeView component.");
                return;
            }

            if (volumeGO.GetComponent<VolumeZoom>() == null)
                volumeGO.AddComponent<VolumeZoom>();

            RawImage sliceImage = FindOrCreateSlicePanel(out RectTransform markerDot);
            Phase2MarkerDemo demo = FindOrCreateMarkerDemo();
            AssignReferences(demo, volumeView, sliceImage, markerDot);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: Phase 2 setup complete - press Play. Scroll wheel zooms the volume.");
        }

        private static RawImage FindOrCreateSlicePanel(out RectTransform markerDot)
        {
            var existing = GameObject.Find("SlicePreview");
            if (existing != null)
            {
                markerDot = existing.transform.Find("SliceMarkerDot")?.GetComponent<RectTransform>();
                return existing.GetComponent<RawImage>();
            }

            Canvas canvas = EditorSetupCommon.FindOrCreateWorldSpaceCanvas();

            var panelGO = new GameObject("SlicePreview", typeof(RawImage));
            panelGO.transform.SetParent(canvas.transform, false);

            var panelRect = panelGO.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(20f, -20f);
            panelRect.sizeDelta = new Vector2(PanelSize, PanelSize);

            var dotGO = new GameObject("SliceMarkerDot", typeof(Image));
            dotGO.transform.SetParent(panelGO.transform, false);
            var dotRect = dotGO.GetComponent<RectTransform>();
            dotRect.anchorMin = new Vector2(0f, 0f);
            dotRect.anchorMax = new Vector2(0f, 0f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.sizeDelta = new Vector2(10f, 10f);
            dotGO.GetComponent<Image>().color = Color.red;

            markerDot = dotRect;
            return panelGO.GetComponent<RawImage>();
        }

        private static Phase2MarkerDemo FindOrCreateMarkerDemo()
        {
            var existing = Object.FindFirstObjectByType<Phase2MarkerDemo>();
            if (existing != null) return existing;

            var go = new GameObject("Phase2Marker");
            return go.AddComponent<Phase2MarkerDemo>();
        }

        private static void AssignReferences(Phase2MarkerDemo demo, VolumeView volumeView, RawImage sliceImage, RectTransform markerDot)
        {
            var so = new SerializedObject(demo);
            so.FindProperty("volumeView").objectReferenceValue = volumeView;
            so.FindProperty("sliceImage").objectReferenceValue = sliceImage;
            so.FindProperty("sliceMarkerDot").objectReferenceValue = markerDot;
            so.ApplyModifiedProperties();
        }
    }
}
