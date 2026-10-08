using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace BioimageVR
{
    // farben und formen fuer die vr oberflaeche, ein ort fuer alle stile
    // laeuft zur laufzeit UND im editor, deshalb hier und nicht unter Editor/
    // 30.09.: palette aus dem abgestimmten mockup, angelehnt an meta horizon os.
    // grau fuer die oberflaeche, blau nur fuer aktionen, orange nur fuer bitte pruefen
    public static class UITheme
    {
        private static Color Rgb(int hex, float alpha = 1f) =>
            new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);

        // flaechen, von hinten nach vorne
        public static readonly Color Background = Rgb(0x1D2025, 0.97f);
        public static readonly Color Track = Rgb(0x15171B);
        public static readonly Color Surface = Rgb(0x2B2F36);
        public static readonly Color SurfaceHover = Rgb(0x353A42);
        public static readonly Color SurfacePressed = Rgb(0x0866FF, 0.35f);
        public static readonly Color SurfaceDisabled = Rgb(0x22252A);
        public static readonly Color Tooltip = Rgb(0x14161A, 0.92f);
        public static readonly Color Divider = new Color(1f, 1f, 1f, 0.10f);

        // aktionen und status
        public static readonly Color Accent = Rgb(0x0866FF);
        public static readonly Color AccentHover = Rgb(0x2B7BFF);
        public static readonly Color AccentSoft = Rgb(0x0866FF, 0.18f);
        public static readonly Color AccentText = Rgb(0x9CC2FF);
        public static readonly Color AccentSecondary = Rgb(0x9CC2FF);
        public static readonly Color Warning = Rgb(0xF5A524);
        public static readonly Color WarningSoft = Rgb(0xF5A524, 0.14f);
        public static readonly Color Danger = Rgb(0xFF8A8A);
        public static readonly Color Success = Rgb(0x6FE3A9);

        // text
        public static readonly Color TextPrimary = Rgb(0xF2F4F7);
        public static readonly Color TextSecondary = Rgb(0xA9B0BB);
        public static readonly Color TextDisabled = Rgb(0x6B7280);
        public static readonly Color TextOnAccent = Color.white;

        public static readonly Color RayColor = new Color(0.92f, 0.92f, 0.95f);

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        // gleiche formel wie LabelColor im VolumeRaymarch shader, damit karte, liste und volumen passen
        public static Color LabelColor(int id)
        {
            float h = Mathf.Repeat(id * 0.61803398875f, 1f);
            var rgb = new Color(
                Mathf.Clamp01(Mathf.Abs(Mathf.Repeat(h, 1f) * 6f - 3f) - 1f),
                Mathf.Clamp01(Mathf.Abs(Mathf.Repeat(h + 2f / 3f, 1f) * 6f - 3f) - 1f),
                Mathf.Clamp01(Mathf.Abs(Mathf.Repeat(h + 1f / 3f, 1f) * 6f - 3f) - 1f));
            return Color.Lerp(Color.white, rgb, 0.9f);
        }

        // schriften, als tmp font assets unter Resources/Fonts & Materials, siehe Editor/SetupFonts
        public enum FontRole { Regular, SemiBold, Bold, Mono, MonoMedium, Icons }

        private static readonly Dictionary<FontRole, TMP_FontAsset> Fonts = new Dictionary<FontRole, TMP_FontAsset>();

        public static string FontAssetName(FontRole role) => role switch
        {
            FontRole.Regular => "Figtree-Regular SDF",
            FontRole.SemiBold => "Figtree-SemiBold SDF",
            FontRole.Bold => "Figtree-Bold SDF",
            FontRole.Mono => "IBMPlexMono-Regular SDF",
            FontRole.MonoMedium => "IBMPlexMono-Medium SDF",
            _ => "BioimageVRIcons SDF",
        };

        // null wenn SetupFonts noch nicht lief, dann nimmt tmp seine standardschrift
        public static TMP_FontAsset Font(FontRole role)
        {
            if (Fonts.TryGetValue(role, out TMP_FontAsset cached) && cached != null) return cached;
            var asset = Resources.Load<TMP_FontAsset>("Fonts & Materials/" + FontAssetName(role));
            Fonts[role] = asset;
            return asset;
        }

        private static readonly Dictionary<int, Sprite> RoundedSprites = new Dictionary<int, Sprite>();

        // 9 gesliceter weisser rundrand fuer beliebige radien, einmal pro radius gebaut
        public static Sprite RoundedSprite(int radius = 20)
        {
            if (RoundedSprites.TryGetValue(radius, out Sprite cached) && cached != null) return cached;

            int size = radius * 2 + 4;
            // mipmaps und trilinear, sonst flimmern die runden kanten auf entfernung
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
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
