using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // kleines schild im raum, dunkle flaeche mit tmp text, dreht sich zur kamera
    // zur laufzeit gebaut, ersetzt die alten TextMesh schilder von mess tool und hover
    // erste zeile fett, zahlen in der monospace schrift ueber <font> tags im text
    public class WorldTooltip : MonoBehaviour
    {
        private TextMeshProUGUI text;
        private RectTransform rect;

        // welt groesse gleich wie die panels, 0.00085 m pro canvas einheit
        private const float Scale = 0.00085f;

        public static WorldTooltip Create(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // ueber dem volumen zeichnen, das hat eine eigene queue unter der ui
            canvas.sortingOrder = 10;
            go.transform.localScale = Vector3.one * Scale;
            var tooltip = go.AddComponent<WorldTooltip>();
            tooltip.Build();
            return tooltip;
        }

        private void Build()
        {
            rect = GetComponent<RectTransform>();
            rect.pivot = new Vector2(0.5f, 0f);
            RectTransform surface = UIKit.Surface(transform, "Surface", true, UITheme.Tooltip, 18f,
                new RectOffset(18, 18, 12, 12), 2f);
            surface.pivot = new Vector2(0.5f, 0f);
            surface.anchorMin = surface.anchorMax = new Vector2(0.5f, 0f);
            surface.anchoredPosition = Vector2.zero;
            text = UIKit.Text(surface, "", UIKit.LabelSize, UITheme.FontRole.Regular, name: "Text");
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.lineSpacing = 8f;
        }

        public void Show(string content, Vector3 worldPosition)
        {
            if (text.text != content) text.text = content;
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            transform.position = worldPosition;
            Camera cam = Camera.main;
            if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, Vector3.up);
        }

        public void Hide()
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        // hilfen fuer den inhalt
        public static string Title(string s) => $"<font=\"{UITheme.FontAssetName(UITheme.FontRole.SemiBold)}\">{s}</font>";
        public static string Mono(string s) => $"<font=\"{UITheme.FontAssetName(UITheme.FontRole.Mono)}\"><color=#C9CED6>{s}</color></font>";
        public static string Warn(string s) => $"<color=#{UITheme.Hex(UITheme.Warning)}>{s}</color>";
    }
}
