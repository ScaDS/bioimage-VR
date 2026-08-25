using System.Collections.Generic;
using UnityEngine;

namespace BioimageVR
{
    // farben und formen fuer die vr oberflaeche, ein ort fuer alle stile
    // laeuft zur laufzeit UND im editor, deshalb hier und nicht unter Editor/
    public static class UITheme
    {
        public static readonly Color Background = new Color(0.07f, 0.08f, 0.10f, 0.90f);
        public static readonly Color Surface = new Color(1f, 1f, 1f, 0.06f);
        public static readonly Color SurfaceHover = new Color(1f, 1f, 1f, 0.14f);
        public static readonly Color SurfacePressed = new Color(0.30f, 0.80f, 0.74f, 0.35f);
        public static readonly Color Accent = new Color(0.30f, 0.80f, 0.74f);
        public static readonly Color AccentSoft = new Color(0.30f, 0.80f, 0.74f, 0.18f);
        public static readonly Color AccentSecondary = new Color(0.66f, 0.56f, 0.95f);
        public static readonly Color Warning = new Color(0.95f, 0.55f, 0.35f);
        public static readonly Color TextPrimary = Color.white;
        public static readonly Color TextSecondary = new Color(1f, 1f, 1f, 0.65f);
        public static readonly Color RayColor = new Color(0.92f, 0.92f, 0.95f);

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        private static readonly Dictionary<int, Sprite> RoundedSprites = new Dictionary<int, Sprite>();

        // 9 gesliceter weisser rundrand fuer beliebige radien, einmal pro radius gebaut
        public static Sprite RoundedSprite(int radius = 20)
        {
            if (RoundedSprites.TryGetValue(radius, out Sprite cached) && cached != null) return cached;

            int size = radius * 2 + 4;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = new Color(1f, 1f, 1f, CornerAlpha(x, y, size, radius));

            texture.SetPixels32(pixels);
            texture.Apply();

            var sprite = Sprite.Create(
                texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f,
                0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            sprite.name = $"RoundedPanel_{radius}";

            RoundedSprites[radius] = sprite;
            return sprite;
        }

        // nur in den vier ecken tatsaechlich rund, kanten und mitte bleiben voll deckend
        private static float CornerAlpha(int x, int y, int size, int radius)
        {
            bool left = x < radius;
            bool right = x >= size - radius;
            bool bottom = y < radius;
            bool top = y >= size - radius;
            if (!(left || right) || !(top || bottom)) return 1f;

            float cx = left ? radius : size - radius;
            float cy = bottom ? radius : size - radius;
            float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
            return Mathf.Clamp01(radius - dist + 0.5f);
        }
    }
}
