using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // bausteine fuer die ganze oberflaeche, abgestimmtes mockup vom 30.09.
    // wird im editor setup UND zur laufzeit fuer listen benutzt, damit alles gleich aussieht
    // masse in canvas einheiten, alle vr canvases laufen mit 0.00085 m pro einheit
    public static class UIKit
    {
        public enum ButtonStyle { Primary, Secondary, Borderless, Destructive, Selected }

        // mockup mal etwa 1.3, in vr sonst zu klein zum lesen und treffen
        public const float ControlHeight = 58f;
        public const float SmallControlHeight = 46f;
        public const float ButtonPadding = 24f;
        public const float Gap = 8f;
        public const float PanelRadius = 36f;
        public const float CardRadius = 30f;

        public const float TitleSize = 34f;
        public const float HeadingSize = 26f;
        public const float BodySize = 21f;
        public const float LabelSize = 19f;
        public const float CaptionSize = 16f;
        public const float IconSize = 28f;

        // text

        public static TextMeshProUGUI Text(Transform parent, string text, float size = BodySize,
            UITheme.FontRole role = UITheme.FontRole.Regular, Color? color = null,
            TextAlignmentOptions align = TextAlignmentOptions.Left, string name = "Text")
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            TMP_FontAsset font = UITheme.Font(role);
            if (font != null) tmp.font = font;
            tmp.fontSize = size;
            tmp.color = color ?? UITheme.TextPrimary;
            tmp.alignment = align;
            tmp.text = text;
            tmp.raycastTarget = false;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        public static TextMeshProUGUI Icon(Transform parent, string glyph, float size = IconSize, Color? color = null)
        {
            TextMeshProUGUI icon = Text(parent, glyph, size, UITheme.FontRole.Icons, color, TextAlignmentOptions.Center, "Icon");
            icon.textWrappingMode = TextWrappingModes.NoWrap;
            icon.overflowMode = TextOverflowModes.Overflow;
            var layout = icon.gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = size;
            layout.preferredHeight = size;
            return icon;
        }

        // flaechen und stapel

        public static Image Panel(Transform parent, string name, Color color, float radius = PanelRadius)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = UITheme.RoundedSprite(Mathf.RoundToInt(radius));
            image.type = Image.Type.Sliced;
            image.color = color;
            return image;
        }

        public static RectTransform Stack(Transform parent, string name, bool vertical, float spacing = Gap,
            RectOffset padding = null, TextAnchor align = TextAnchor.UpperLeft, bool expandCross = true)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            HorizontalOrVerticalLayoutGroup layout = vertical
                ? go.AddComponent<VerticalLayoutGroup>()
                : (HorizontalOrVerticalLayoutGroup)go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = padding ?? new RectOffset(0, 0, 0, 0);
            layout.childAlignment = align;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = vertical && expandCross;
            layout.childForceExpandHeight = !vertical && expandCross;
            return go.GetComponent<RectTransform>();
        }

        // stapel mit hintergrund, groesse folgt dem inhalt
        // fit false wenn ein layout drumherum die groesse bestimmt, sonst warnt unity
        public static RectTransform Surface(Transform parent, string name, bool vertical, Color color, float radius,
            RectOffset padding, float spacing = Gap, TextAnchor align = TextAnchor.UpperLeft, bool fit = true)
        {
            Image image = Panel(parent, name, color, radius);
            HorizontalOrVerticalLayoutGroup layout = vertical
                ? image.gameObject.AddComponent<VerticalLayoutGroup>()
                : (HorizontalOrVerticalLayoutGroup)image.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = padding;
            layout.childAlignment = align;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = vertical;
            layout.childForceExpandHeight = false;
            if (fit)
            {
                var fitter = image.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            return image.rectTransform;
        }

        public static void Size(Component c, float width = -1, float height = -1, float flexibleWidth = -1)
        {
            var layout = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
            if (width >= 0)
            {
                layout.preferredWidth = width;
                layout.minWidth = width;
            }
            if (height >= 0)
            {
                layout.preferredHeight = height;
                layout.minHeight = height;
            }
            if (flexibleWidth >= 0) layout.flexibleWidth = flexibleWidth;
        }

        public static void Divider(Transform parent, bool vertical)
        {
            var go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = UITheme.Divider;
            image.raycastTarget = false;
            if (vertical) Size(image, 2f, SmallControlHeight - 16f);
            else Size(image, -1, 2f);
        }

        // knoepfe

        // pillenform, text und optional icon, breite folgt dem inhalt
        public static Button Button(Transform parent, string label, ButtonStyle style = ButtonStyle.Secondary,
            string icon = null, float height = ControlHeight, string name = null)
        {
            Image image = Panel(parent, name ?? $"{label}Button", Color.white, height / 2f);
            var row = image.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset((int)ButtonPadding, (int)ButtonPadding, 0, 0);
            row.spacing = 10f;
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            Size(image, -1, height);

            if (!string.IsNullOrEmpty(icon)) Icon(image.transform, icon, IconSize * 0.85f);
            if (!string.IsNullOrEmpty(label))
            {
                TextMeshProUGUI text = Text(image.transform, label, LabelSize, UITheme.FontRole.SemiBold,
                    align: TextAlignmentOptions.Center, name: "Label");
                text.textWrappingMode = TextWrappingModes.NoWrap;
            }

            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            SetStyle(button, style);
            return button;
        }

        public static Button IconButton(Transform parent, string icon, string name,
            ButtonStyle style = ButtonStyle.Secondary, float size = ControlHeight)
        {
            Image image = Panel(parent, name, Color.white, size / 2f);
            Size(image, size, size);
            TextMeshProUGUI glyph = Text(image.transform, icon, IconSize, UITheme.FontRole.Icons,
                align: TextAlignmentOptions.Center, name: "Icon");
            Stretch(glyph.rectTransform);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            SetStyle(button, style);
            return button;
        }

        // senkrechter werkzeug knopf, icon ueber kurzem text, fuer die leiste am volumen
        public static Button ToolButton(Transform parent, string icon, string label)
        {
            Image image = Panel(parent, $"{label}Tool", Color.white, 26f);
            Size(image, 92f, 84f);
            var column = image.gameObject.AddComponent<VerticalLayoutGroup>();
            column.childAlignment = TextAnchor.MiddleCenter;
            column.spacing = 2f;
            column.padding = new RectOffset(0, 0, 8, 8);
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            Icon(image.transform, icon, 32f);
            TextMeshProUGUI text = Text(image.transform, label, CaptionSize, UITheme.FontRole.SemiBold,
                align: TextAlignmentOptions.Center, name: "Label");
            text.textWrappingMode = TextWrappingModes.NoWrap;
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            SetStyle(button, ButtonStyle.Borderless);
            return button;
        }

        // farben fuer normal, zeigen, druecken, gesperrt, text und icon faerben mit
        public static void SetStyle(Button button, ButtonStyle style)
        {
            Color normal, hover, content;
            switch (style)
            {
                case ButtonStyle.Primary:
                    normal = UITheme.Accent; hover = UITheme.AccentHover; content = UITheme.TextOnAccent; break;
                case ButtonStyle.Selected:
                    normal = UITheme.TextPrimary; hover = Color.white; content = UITheme.Track; break;
                case ButtonStyle.Destructive:
                    normal = UITheme.Surface; hover = UITheme.SurfaceHover; content = UITheme.Danger; break;
                case ButtonStyle.Borderless:
                    normal = new Color(1f, 1f, 1f, 0f); hover = UITheme.Surface; content = UITheme.TextPrimary; break;
                default:
                    normal = UITheme.Surface; hover = UITheme.SurfaceHover; content = UITheme.TextPrimary; break;
            }

            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = hover;
            colors.pressedColor = style == ButtonStyle.Primary ? UITheme.Accent : UITheme.SurfacePressed;
            // sonst bleibt der knopf nach dem klick in unitys weissem selected haengen
            colors.selectedColor = normal;
            colors.disabledColor = UITheme.SurfaceDisabled;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            var tint = button.GetComponent<ContentTint>() ?? button.gameObject.AddComponent<ContentTint>();
            tint.Enabled = content;
        }

        public static void SetLabel(Button button, string label)
        {
            Transform child = button.transform.Find("Label");
            var text = child != null ? child.GetComponent<TMP_Text>() : null;
            if (text != null && text.text != label) text.text = label;
        }

        // regler, schalter, chips

        public static Slider Slider(Transform parent, float min, float max, float value, string name = "Slider")
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);
            Size(go.transform, -1, 36f);

            Image track = Panel(go.transform, "Track", UITheme.Surface, 5f);
            var trackRect = track.rectTransform;
            trackRect.anchorMin = new Vector2(0f, 0.5f);
            trackRect.anchorMax = new Vector2(1f, 0.5f);
            trackRect.sizeDelta = new Vector2(0f, 10f);
            track.raycastTarget = true;

            var fillArea = new GameObject("FillArea", typeof(RectTransform)).GetComponent<RectTransform>();
            fillArea.SetParent(go.transform, false);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.sizeDelta = new Vector2(0f, 10f);
            Image fill = Panel(fillArea, "Fill", UITheme.Accent, 5f);
            fill.rectTransform.sizeDelta = Vector2.zero;
            fill.raycastTarget = false;

            var handleArea = new GameObject("HandleArea", typeof(RectTransform)).GetComponent<RectTransform>();
            handleArea.SetParent(go.transform, false);
            Stretch(handleArea);
            handleArea.offsetMin = new Vector2(14f, 0f);
            handleArea.offsetMax = new Vector2(-14f, 0f);
            Image handle = Panel(handleArea, "Handle", Color.white, 14f);
            handle.rectTransform.sizeDelta = new Vector2(28f, 28f);

            var slider = go.GetComponent<Slider>();
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;
            ColorBlock colors = slider.colors;
            colors.highlightedColor = new Color(0.85f, 0.9f, 1f);
            colors.pressedColor = UITheme.AccentText;
            colors.selectedColor = Color.white;
            slider.colors = colors;
            return slider;
        }

        public static UISwitch Switch(Transform parent, bool value, string name = "Switch")
        {
            Image track = Panel(parent, name, UITheme.Surface, 18f);
            Size(track, 66f, 38f);
            Image knob = Panel(track.transform, "Knob", Color.white, 15f);
            knob.raycastTarget = false;
            var knobRect = knob.rectTransform;
            knobRect.anchorMin = knobRect.anchorMax = new Vector2(0f, 0.5f);
            knobRect.sizeDelta = new Vector2(30f, 30f);
            var button = track.gameObject.AddComponent<Button>();
            button.targetGraphic = track;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.9f, 0.9f, 0.9f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            colors.selectedColor = Color.white;
            button.colors = colors;
            var sw = track.gameObject.AddComponent<UISwitch>();
            sw.Init(track, knobRect, button, value);
            return sw;
        }

        // segmente auf einer schiene, genau eins ist aktiv
        public static UISegmented Segmented(Transform parent, string name, params string[] options)
        {
            RectTransform rail = Surface(parent, name, false, UITheme.Track, SmallControlHeight / 2f + 6f,
                new RectOffset(6, 6, 6, 6), 6f, TextAnchor.MiddleCenter, fit: false);
            var buttons = new List<Button>();
            foreach (string option in options)
                buttons.Add(Button(rail, option, ButtonStyle.Borderless, height: SmallControlHeight));
            var segmented = rail.gameObject.AddComponent<UISegmented>();
            segmented.Init(buttons);
            return segmented;
        }

        public static RectTransform Chip(Transform parent, string text, Color textColor, Color background, string name = "Chip")
        {
            RectTransform chip = Surface(parent, name, false, background, 16f, new RectOffset(14, 14, 6, 6), 6f,
                TextAnchor.MiddleCenter, fit: false);
            TextMeshProUGUI label = Text(chip, text, CaptionSize, UITheme.FontRole.SemiBold, textColor, name: "Label");
            label.textWrappingMode = TextWrappingModes.NoWrap;
            return chip;
        }

        // kleiner abschnitts titel in grossbuchstaben
        public static TextMeshProUGUI SectionLabel(Transform parent, string text)
        {
            TextMeshProUGUI label = Text(parent, text.ToUpperInvariant(), CaptionSize - 1f, UITheme.FontRole.SemiBold,
                UITheme.TextDisabled, name: "Section");
            label.characterSpacing = 8f;
            return label;
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
