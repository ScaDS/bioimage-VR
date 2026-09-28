using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // Lineal unten mittig im hud, schaltet das messen an und aus
    // Auto erscheint erst daneben wenn Lineal an ist, wechselt zwischen hand und auto
    // hinweis zeile darueber sagt was der naechste klick macht
    public class MeasureToolbar : MonoBehaviour
    {
        [SerializeField] private MeasureTool measureTool;
        [SerializeField] private Button rulerButton;
        [SerializeField] private Button blobButton;
        [SerializeField] private Text hintText;

        private void Awake()
        {
            if (rulerButton != null) rulerButton.onClick.AddListener(OnRulerClicked);
            if (blobButton != null) blobButton.onClick.AddListener(OnAutoClicked);
        }

        private void OnEnable()
        {
            if (measureTool != null) measureTool.StateChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (measureTool != null) measureTool.StateChanged -= Refresh;
        }

        // an oder aus, egal ob gerade hand oder auto
        private void OnRulerClicked()
        {
            if (measureTool == null) return;
            measureTool.SetMode(measureTool.CurrentMode == MeasureTool.Mode.Off ? MeasureTool.Mode.Ruler : MeasureTool.Mode.Off);
        }

        private void OnAutoClicked()
        {
            if (measureTool == null) return;
            measureTool.SetMode(measureTool.CurrentMode == MeasureTool.Mode.Blob ? MeasureTool.Mode.Ruler : MeasureTool.Mode.Blob);
        }

        private void Refresh()
        {
            MeasureTool.Mode mode = measureTool != null ? measureTool.CurrentMode : MeasureTool.Mode.Off;
            Highlight(rulerButton, mode != MeasureTool.Mode.Off);
            Highlight(blobButton, mode == MeasureTool.Mode.Blob);
            if (blobButton != null) blobButton.gameObject.SetActive(mode != MeasureTool.Mode.Off);

            if (hintText == null) return;
            string hint = measureTool != null ? measureTool.Hint : "";
            hintText.text = hint;
            hintText.gameObject.SetActive(!string.IsNullOrEmpty(hint));
        }

        private static void Highlight(Button button, bool active)
        {
            if (button == null) return;
            ColorBlock colors = button.colors;
            colors.normalColor = active ? UITheme.Accent : UITheme.Surface;
            colors.selectedColor = colors.normalColor;
            colors.highlightedColor = active ? UITheme.Accent : UITheme.SurfaceHover;
            button.colors = colors;

            // dunkle schrift auf hellem akzent, sonst weiss
            Text label = button.GetComponentInChildren<Text>();
            if (label != null) label.color = active ? UITheme.Background : UITheme.TextPrimary;
        }
    }
}
