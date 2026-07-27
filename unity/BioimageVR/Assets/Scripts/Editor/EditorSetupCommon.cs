using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR.EditorSetup
{
    // gemeinsame helfer fuer die setup skripte
    public static class EditorSetupCommon
    {
        // findet oder baut den geteilten canvas, erzwingt world space
        // overlay taugt nicht fuer vr, alte setups hatten das noch
        public static Canvas FindOrCreateWorldSpaceCanvas()
        {
            Canvas canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                var canvasGO = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
                canvas = canvasGO.GetComponent<Canvas>();
            }

            if (canvas.renderMode != RenderMode.WorldSpace)
            {
                canvas.renderMode = RenderMode.WorldSpace;
                var rect = canvas.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(1920, 1080);
                // etwa 1.6m breit, unterhalb vom volumen platziert, verdeckt es nicht
                rect.localScale = Vector3.one * 0.00085f;
                rect.position = new Vector3(0f, 1.3f, 1.0f);
                rect.rotation = Quaternion.identity;
            }

            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam != null) canvas.worldCamera = cam;

            return canvas;
        }
    }
}
