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

        // einmalige weltraum platzierung relativ zur kamera, KEIN dauerhaftes parenting
        // greifbare panels muessen dort bleiben wo man sie hingreift, nicht dem kopf folgen
        public static void PlaceInFrontOfCamera(RectTransform rect, Vector3 localOffset)
        {
            var cam = Object.FindFirstObjectByType<Camera>();
            rect.localScale = Vector3.one * 0.00085f;
            if (cam != null)
            {
                rect.position = cam.transform.TransformPoint(localOffset);
                FaceCamera(rect, cam.transform.position);
            }
            else
            {
                rect.position = localOffset;
                rect.rotation = Quaternion.identity;
            }
        }

        // gleiche formel wie SidePanelController.OnReleased zur laufzeit - nur um die
        // hochachse drehen, sonst gilt dasselbe egal ob links, rechts oder dahinter platziert
        public static void FaceCamera(Transform rect, Vector3 cameraPosition)
        {
            Vector3 toCam = cameraPosition - rect.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 0.0001f) return;
            rect.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
        }

        public static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
