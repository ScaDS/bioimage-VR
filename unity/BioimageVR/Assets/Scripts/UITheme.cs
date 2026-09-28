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

        // eigenstaendige icons statt text-glyphen ("i"/"...") fuer die ecken-buttons
        // (siehe SetupSidePanel.CreateCornerIcon) - gleiches prinzip wie RoundedSprite
        // (per-pixel alpha-maske, weiss+alpha, faerbung passiert ueber Image.color),
        // nur als frei geformtes icon statt 9-slice-panel. feste 96x96 generierungs-
        // aufloesung, das Button-Image skaliert das ueber preserveAspect passend runter
        private const int IconTextureSize = 96;
        private static readonly Dictionary<string, Sprite> IconSprites = new Dictionary<string, Sprite>();

        // kreis-ring + punkt + balken, klassisches "info" symbol - ring unabhaengig vom
        // (kreisrunden) button-hintergrund gezeichnet, damit das icon auch fuer sich
        // alleine als "info" erkennbar bleibt, falls es mal woanders eingesetzt wird
        public static Sprite InfoIconSprite()
        {
            if (IconSprites.TryGetValue("info", out Sprite cached) && cached != null) return cached;

            const float size = IconTextureSize;
            const float centerX = size * 0.5f;
            const float centerY = size * 0.5f;
            const float ringRadius = size * 0.40f;
            const float ringThickness = size * 0.075f;
            const float dotRadius = size * 0.075f;
            const float dotCenterY = centerY + size * 0.19f;
            const float barHalfWidth = size * 0.075f;
            const float barBottom = centerY - size * 0.27f;
            const float barTop = centerY + size * 0.04f;

            var pixels = new Color32[IconTextureSize * IconTextureSize];
            for (int y = 0; y < IconTextureSize; y++)
            {
                for (int x = 0; x < IconTextureSize; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float alpha = RingAlpha(px, py, centerX, centerY, ringRadius, ringThickness);
                    alpha = Mathf.Max(alpha, CircleAlpha(px, py, centerX, dotCenterY, dotRadius));
                    alpha = Mathf.Max(alpha, RoundedRectAlpha(
                        px, py, centerX - barHalfWidth, barBottom, centerX + barHalfWidth, barTop, barHalfWidth));
                    pixels[y * IconTextureSize + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            return BuildIconSprite("info", pixels);
        }

        // sprechblase mit drei punkten (typing-indicator-stil), klassisches "chat" symbol -
        // ersetzt das bisherige woertliche "..." text-glyph
        public static Sprite ChatIconSprite()
        {
            if (IconSprites.TryGetValue("chat", out Sprite cached) && cached != null) return cached;

            const float size = IconTextureSize;
            const float left = size * 0.104f;
            const float right = size * 0.896f;
            const float bottom = size * 0.313f;
            const float top = size * 0.833f;
            const float bodyRadius = size * 0.156f;
            Vector2 tailA = new Vector2(size * 0.25f, bottom);
            Vector2 tailB = new Vector2(size * 0.135f, size * 0.135f);
            Vector2 tailC = new Vector2(size * 0.417f, bottom);
            float dotsY = (top + bottom) / 2f;
            float dotSpacing = size * 0.146f;
            float dotRadius = size * 0.052f;

            var pixels = new Color32[IconTextureSize * IconTextureSize];
            for (int y = 0; y < IconTextureSize; y++)
            {
                for (int x = 0; x < IconTextureSize; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float alpha = RoundedRectAlpha(px, py, left, bottom, right, top, bodyRadius);
                    alpha = Mathf.Max(alpha, TriangleAlpha(px, py, tailA, tailB, tailC));

                    // punkte aus der ausgefuellten blase ausstanzen (alpha 0) statt
                    // draufzusetzen - wirkt wie ein "loch", guter kontrast unabhaengig
                    // von der button-farbe dahinter, kein zweiter tint-durchlauf noetig
                    for (int i = -1; i <= 1; i++)
                    {
                        float dotCenterX = size * 0.5f + i * dotSpacing;
                        if (CircleAlpha(px, py, dotCenterX, dotsY, dotRadius) > 0.5f) alpha = 0f;
                    }

                    pixels[y * IconTextureSize + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            return BuildIconSprite("chat", pixels);
        }

        private static Sprite BuildIconSprite(string key, Color32[] pixels)
        {
            var texture = new Texture2D(IconTextureSize, IconTextureSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixels32(pixels);
            texture.Apply();

            var sprite = Sprite.Create(
                texture, new Rect(0, 0, IconTextureSize, IconTextureSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = $"Icon_{key}";

            IconSprites[key] = sprite;
            return sprite;
        }

        private static float CircleAlpha(float px, float py, float cx, float cy, float radius)
        {
            float dist = Vector2.Distance(new Vector2(px, py), new Vector2(cx, cy));
            return Mathf.Clamp01(radius - dist + 0.5f);
        }

        // ring = grosse kreisflaeche minus kleinere kreisflaeche, beide ueber CircleAlpha
        // (gleiche antialiasing-logik), statt eines eigenen sdf-pfads fuer ringe
        private static float RingAlpha(float px, float py, float cx, float cy, float radius, float thickness)
        {
            float outer = CircleAlpha(px, py, cx, cy, radius);
            float inner = CircleAlpha(px, py, cx, cy, radius - thickness);
            return Mathf.Clamp01(outer - inner);
        }

        // wie CornerAlpha, aber fuer ein eigenstaendiges icon statt ein 9-slice-panel -
        // rundet alle vier ecken UND antialiast die geraden kanten (bei RoundedSprite
        // unnoetig, da die kanten durch den 9-slice-stretch sowieso ausserhalb des
        // sichtbaren texturbereichs liegen; hier ist die ganze textur das icon selbst)
        private static float RoundedRectAlpha(float px, float py, float x0, float y0, float x1, float y1, float radius)
        {
            bool left = px < x0 + radius;
            bool right = px > x1 - radius;
            bool bottom = py < y0 + radius;
            bool top = py > y1 - radius;
            if (!(left || right) || !(top || bottom))
            {
                float edgeDist = Mathf.Min(Mathf.Min(px - x0, x1 - px), Mathf.Min(py - y0, y1 - py));
                return Mathf.Clamp01(edgeDist + 0.5f);
            }

            float cx = left ? x0 + radius : x1 - radius;
            float cy = bottom ? y0 + radius : y1 - radius;
            float dist = Vector2.Distance(new Vector2(px, py), new Vector2(cx, cy));
            return Mathf.Clamp01(radius - dist + 0.5f);
        }

        // standard "gleiche seite aller drei kanten" punkt-in-dreieck test, harte kante
        // (kein extra antialiasing) - bei 96px generierungsgroesse und bilinearer
        // texturfilterung durch UI-Skalierung ausreichend glatt
        private static float TriangleAlpha(float px, float py, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = TriangleSign(px, py, a, b);
            float d2 = TriangleSign(px, py, b, c);
            float d3 = TriangleSign(px, py, c, a);
            bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
            return hasNeg && hasPos ? 0f : 1f;
        }

        private static float TriangleSign(float px, float py, Vector2 p1, Vector2 p2) =>
            (px - p2.x) * (p1.y - p2.y) - (p1.x - p2.x) * (py - p2.y);
    }
}
