using UnityEngine;

namespace BioimageVR
{
    // kamera in eigene rendertexture, ScreenCapture liefert auf der quest nur den hintergrund
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
