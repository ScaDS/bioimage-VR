using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BioimageVR.EditorSetup
{
    // baut chatcanvas an der camera, folgt dem kopf, zeigt frage antwort verlauf
    // verkabelt sich in vlmtestharness und voicevlmharness, sicher erneut ausfuehrbar
    // menu: BioimageVR Setup Chat Panel
    public static class SetupChatPanel
    {
        private const string ScenePath = "Assets/Scenes/Phase1.unity";

        [MenuItem("BioimageVR/Setup Chat Panel")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath && File.Exists(ScenePath))
                scene = EditorSceneManager.OpenScene(ScenePath);

            ChatPanel chatPanel = FindOrCreateChatPanel();
            WireHarness(Object.FindFirstObjectByType<VLMTestHarness>(), chatPanel);
            WireHarness(Object.FindFirstObjectByType<VoiceVLMHarness>(), chatPanel);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: Chat panel setup complete - ask/speak a question, the " +
                      "history now stays visible in the side panel instead of being overwritten.");
        }

        private static ChatPanel FindOrCreateChatPanel()
        {
            var existing = Object.FindFirstObjectByType<ChatPanel>();
            if (existing != null)
            {
                // falls noch von vorher statisch, jetzt neu an camera haengen
                ReanchorToCamera(existing.GetComponent<RectTransform>());
                return existing;
            }

            Canvas canvas = CreateChatCanvas();

            var background = new GameObject("Background", typeof(Image));
            background.transform.SetParent(canvas.transform, false);
            StretchFull(background.GetComponent<RectTransform>());
            var bgImage = background.GetComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0.65f);
            bgImage.raycastTarget = false;

            var title = new GameObject("Title", typeof(Text));
            title.transform.SetParent(canvas.transform, false);
            var titleRect = title.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = Vector2.zero;
            titleRect.sizeDelta = new Vector2(0f, 70f);
            var titleText = title.GetComponent<Text>();
            titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleText.fontSize = 32;
            titleText.fontStyle = FontStyle.Bold;
            titleText.color = Color.white;
            titleText.alignment = TextAnchor.MiddleLeft;
            titleText.text = "Chat";

            var scrollGO = new GameObject("ScrollView", typeof(ScrollRect));
            scrollGO.transform.SetParent(canvas.transform, false);
            var scrollRectTransform = scrollGO.GetComponent<RectTransform>();
            scrollRectTransform.anchorMin = new Vector2(0f, 0f);
            scrollRectTransform.anchorMax = new Vector2(1f, 1f);
            scrollRectTransform.offsetMin = new Vector2(20f, 20f);
            scrollRectTransform.offsetMax = new Vector2(-20f, -90f);

            var viewportGO = new GameObject("Viewport", typeof(Image), typeof(RectMask2D));
            viewportGO.transform.SetParent(scrollGO.transform, false);
            StretchFull(viewportGO.GetComponent<RectTransform>());
            var viewportImage = viewportGO.GetComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.001f); // fast unsichtbar, nur fuer die maske
            viewportImage.raycastTarget = false;

            var contentGO = new GameObject("Content", typeof(Text), typeof(ContentSizeFitter));
            contentGO.transform.SetParent(viewportGO.transform, false);
            var contentRect = contentGO.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 0f);
            var contentFitter = contentGO.GetComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var contentText = contentGO.GetComponent<Text>();
            contentText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            contentText.fontSize = 24;
            contentText.color = Color.white;
            contentText.alignment = TextAnchor.UpperLeft;
            contentText.horizontalOverflow = HorizontalWrapMode.Wrap;
            contentText.verticalOverflow = VerticalWrapMode.Overflow;
            contentText.text = "(noch keine Fragen gestellt)";

            var scrollRect = scrollGO.GetComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.viewport = viewportGO.GetComponent<RectTransform>();
            scrollRect.content = contentRect;

            ChatPanel panel = canvas.gameObject.AddComponent<ChatPanel>();
            var so = new SerializedObject(panel);
            so.FindProperty("contentText").objectReferenceValue = contentText;
            so.FindProperty("scrollRect").objectReferenceValue = scrollRect;
            so.ApplyModifiedProperties();

            return panel;
        }

        // rechts unten leicht vor der kamera, ausserhalb der bildmitte
        private static readonly Vector3 LocalOffset = new Vector3(0.6f, -0.15f, 1.0f);

        private static Canvas CreateChatCanvas()
        {
            var canvasGO = new GameObject("ChatCanvas", typeof(Canvas), typeof(CanvasScaler));
            Canvas canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam != null) canvas.worldCamera = cam;

            var rect = canvasGO.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(700f, 900f);
            ReanchorToCamera(rect);

            return canvas;
        }

        private static void ReanchorToCamera(RectTransform rect)
        {
            var cam = Object.FindFirstObjectByType<Camera>();
            rect.localScale = Vector3.one * 0.00085f;
            if (cam != null)
            {
                rect.SetParent(cam.transform, worldPositionStays: false);
                rect.localPosition = LocalOffset;
                rect.localRotation = Quaternion.identity;
            }
            else
            {
                // keine camera gefunden, fallback auf feste position
                rect.position = new Vector3(1.3f, 1.5f, 1.0f);
                rect.rotation = Quaternion.identity;
            }
        }

        private static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void WireHarness(MonoBehaviour harness, ChatPanel chatPanel)
        {
            if (harness == null) return;
            var so = new SerializedObject(harness);
            so.FindProperty("chatPanel").objectReferenceValue = chatPanel;
            so.ApplyModifiedProperties();
        }
    }
}
