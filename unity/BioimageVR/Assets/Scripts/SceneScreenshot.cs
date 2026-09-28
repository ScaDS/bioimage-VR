using UnityEngine;

namespace BioimageVR
{
    // ScreenCapture.CaptureScreenshotAsTexture liest vom normalen screen-backbuffer -
    // auf der quest rendert openxr aber per single pass stereo direkt in den xr
    // compositor, dadurch kam beim vlm nur die leere kamera-hintergrundfarbe an statt
    // der echten szene (siehe VLMTestHarness alter kommentar "screenshot faengt
    // spectator fenster ein, nicht das echte augenbild"). rendert die kamera stattdessen
    // manuell in eine eigene rendertexture - das umgeht den xr swapchain komplett und
    // liefert ein normales mono bild, im editor wie auf dem geraet gleich
    public static class SceneScreenshot
    {
        public static Texture2D Capture(Camera camera)
        {
            if (camera == null) camera = Camera.main;
            if (camera == null) return null;

            int width = Screen.width;
            int height = Screen.height;

            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            camera.targetTexture = renderTexture;
            camera.Render();
            RenderTexture.active = renderTexture;

            var screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
            screenshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            screenshot.Apply();

            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(renderTexture);

            return screenshot;
        }
    }
}
