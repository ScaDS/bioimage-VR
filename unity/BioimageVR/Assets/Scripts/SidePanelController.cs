using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace BioimageVR
{
    // seitenpanel mit tabs Images, Cells, Display, Chat, abgestimmtes mockup vom 30.09.
    // baut seinen inhalt beim start selbst mit UIKit und reicht die teile an die komponenten
    // auf demselben objekt weiter, das setup legt nur die huelle und die verweise an
    // griffleiste oben zum verschieben, dreht sich waehrend des greifens zur kamera
    public class SidePanelController : MonoBehaviour
    {
        // einzige quelle fuer die hoehe der griffleiste, geschlossen groesser, sonst zu duenn zum treffen
        public const float OpenDragBarHeight = 40f;
        public const float ClosedDragBarHeight = 90f;
        private const float DragBarGap = 10f;
        private const float StickScrollSensitivity = 25f;

        public enum Page { Images, Cells, Display, Chat }

        [SerializeField] private VolumeView volumeView;
        [SerializeField] private CellInspector cellInspector;
        [SerializeField] private LabelEditor labelEditor;
        [SerializeField] private ChatPanel chatPanel;
        [SerializeField] private VolumeGalleryMenu gallery;
        [SerializeField] private VolumeContrastControl contrast;
        [SerializeField] private VolumeRenderControls render;
        [SerializeField] private SegmentationControls segmentation;
        [SerializeField] private VolumeMetadataPanel metadata;
        [SerializeField] private RenderQuality renderQuality;
        [SerializeField] private BoxCollider grabZoneCollider;

        private RectTransform canvasRect;
        private GameObject panelBody;
        private RectTransform dragBar;
        private Image dragBarImage;
        private RectTransform closeIcon;
        private UISegmented tabs;
        private readonly Dictionary<Page, GameObject> pages = new Dictionary<Page, GameObject>();
        private readonly List<GameObject> channelRows = new List<GameObject>();
        private readonly List<TMP_Text> channelLabels = new List<TMP_Text>();
        private Page currentPage = Page.Images;
        private bool isOpen = true;
        private XRGrabInteractable grabInteractable;

        private void Awake()
        {
            canvasRect = GetComponent<RectTransform>();
            Build();

            grabInteractable = GetComponent<XRGrabInteractable>();
            if (grabInteractable != null)
            {
                // leiste hellt auf sobald der strahl draufzeigt, sonst sieht man nicht dass sie greifbar ist
                grabInteractable.hoverEntered.AddListener(_ => dragBarImage.color = UITheme.AccentSoft);
                grabInteractable.hoverExited.AddListener(_ => dragBarImage.color = UITheme.Track);
            }
            if (volumeView != null) volumeView.OnVolumeLoaded += _ => RefreshChannels();
            SelectPage(Page.Images);
            ApplyOpen();
        }

        private void Update()
        {
            // waehrend des greifens jeden frame zur kamera drehen, nicht erst beim loslassen
            if (grabInteractable != null && grabInteractable.isSelected) FaceCamera();
        }

        public void ShowChat()
        {
            if (!isOpen) ToggleOpen();
            SelectPage(Page.Chat);
        }

        public void SelectPage(Page page)
        {
            currentPage = page;
            if (tabs != null) tabs.SetWithoutNotify((int)page);
            foreach (var pair in pages) pair.Value.SetActive(pair.Key == page);
            // neu gepushte dateien landen sofort in der liste
            if (page == Page.Images && gallery != null) gallery.Refresh();
        }

        // aufbau

        private void Build()
        {
            // reste eines aelteren panel aufbaus weg, sonst liegen alter und neuer stil uebereinander
            // greifzone und haltepunkt kommen vom setup und bleiben
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name == "GrabZone" || child.name == "GrabAttachPoint") continue;
                Destroy(child.gameObject);
            }

            // griffleiste, bleibt auch zugeklappt sichtbar, sonst findet man das panel nicht wieder
            dragBarImage = UIKit.Panel(transform, "DragBar", UITheme.Track, 18f);
            dragBarImage.raycastTarget = false; // greifen laeuft ueber den collider, nicht ueber ui
            dragBar = dragBarImage.rectTransform;
            dragBar.anchorMin = new Vector2(0f, 1f);
            dragBar.anchorMax = new Vector2(1f, 1f);
            dragBar.pivot = new Vector2(0.5f, 1f);
            dragBar.anchoredPosition = Vector2.zero;
            Image indicator = UIKit.Panel(dragBar, "Indicator", UITheme.TextSecondary, 3f);
            indicator.raycastTarget = false;
            indicator.rectTransform.sizeDelta = new Vector2(80f, 6f);

            Button close = UIKit.IconButton(dragBar, Icons.Close, "CloseButton", UIKit.ButtonStyle.Borderless, OpenDragBarHeight - 4f);
            var closeRect = close.GetComponent<RectTransform>();
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 0.5f);
            closeRect.pivot = new Vector2(1f, 0.5f);
            closeRect.anchoredPosition = new Vector2(-4f, 0f);
            closeRect.sizeDelta = new Vector2(OpenDragBarHeight - 4f, OpenDragBarHeight - 4f);
            closeIcon = close.transform.Find("Icon") as RectTransform;
            close.onClick.AddListener(ToggleOpen);

            panelBody = new GameObject("PanelBody", typeof(RectTransform));
            panelBody.transform.SetParent(transform, false);
            var bodyRect = panelBody.GetComponent<RectTransform>();
            UIKit.Stretch(bodyRect);
            bodyRect.offsetMax = new Vector2(0f, -(OpenDragBarHeight + DragBarGap));

            // raycast target, sonst faellt der strahl zwischen den zeilen durchs panel und dreht das volumen
            Image background = UIKit.Panel(bodyRect, "Background", UITheme.Background, UIKit.PanelRadius);
            UIKit.Stretch(background.rectTransform);
            background.raycastTarget = true;

            RectTransform column = UIKit.Stack(bodyRect, "Column", true, 16f, new RectOffset(20, 20, 20, 20));
            UIKit.Stretch(column);
            column.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;

            tabs = UIKit.Segmented(column, "Tabs", "Images", "Cells", "Display", "Chat");
            FillWidth(tabs);
            tabs.SelectionChanged += index => SelectPage((Page)index);

            var pageArea = new GameObject("Pages", typeof(RectTransform)).GetComponent<RectTransform>();
            pageArea.SetParent(column, false);
            UIKit.Size(pageArea, -1, -1);
            pageArea.GetComponent<LayoutElement>().flexibleHeight = 1f;

            BuildImagesPage(pageArea);
            BuildCellsPage(pageArea);
            BuildDisplayPage(pageArea);
            BuildChatPage(pageArea);
        }

        // segmente auf volle breite ziehen, gleich breit
        private static void FillWidth(UISegmented segmented)
        {
            var layout = segmented.GetComponent<HorizontalLayoutGroup>();
            layout.childForceExpandWidth = true;
            foreach (Transform child in segmented.transform) UIKit.Size(child, -1, -1, 1f);
        }

        private RectTransform ScrollPage(RectTransform parent, Page page)
        {
            var pageGO = new GameObject($"{page}Page", typeof(RectTransform), typeof(ScrollRect));
            pageGO.transform.SetParent(parent, false);
            UIKit.Stretch(pageGO.GetComponent<RectTransform>());

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D)).GetComponent<RectTransform>();
            viewport.SetParent(pageGO.transform, false);
            UIKit.Stretch(viewport);
            var viewportImage = viewport.GetComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.001f);

            RectTransform content = UIKit.Stack(viewport, "Content", true, 10f);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;
            content.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = pageGO.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.scrollSensitivity = StickScrollSensitivity;

            pages[page] = pageGO;
            return content;
        }

        private void BuildImagesPage(RectTransform area)
        {
            RectTransform content = ScrollPage(area, Page.Images);
            RectTransform current = UIKit.Surface(content, "CurrentImage", true, UITheme.Track, 24f,
                new RectOffset(22, 22, 18, 20), 6f, fit: false);
            UIKit.SectionLabel(current, "Current image");
            TextMeshProUGUI title = UIKit.Text(current, "", UIKit.HeadingSize, UITheme.FontRole.Bold);
            TextMeshProUGUI details = UIKit.Text(current, "", UIKit.LabelSize, color: UITheme.TextSecondary);
            details.lineSpacing = 10f;
            metadata?.Bind(title, details);

            UIKit.SectionLabel(content, "Library");
            RectTransform list = UIKit.Stack(content, "Library", true, 6f);
            list.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
            gallery?.Bind(list);
        }

        private void BuildCellsPage(RectTransform area)
        {
            RectTransform content = ScrollPage(area, Page.Cells);
            content.GetComponent<VerticalLayoutGroup>().spacing = 4f;
            var list = pages[Page.Cells].AddComponent<CellListPanel>();
            list.Init(volumeView, cellInspector, labelEditor, content);
        }

        private void BuildDisplayPage(RectTransform area)
        {
            RectTransform content = ScrollPage(area, Page.Display);
            content.GetComponent<VerticalLayoutGroup>().spacing = 14f;

            UIKit.SectionLabel(content, "Intensity");
            Slider threshold = SliderRow(content, "Threshold", 0f, 1f, 0.12f, "0.00");
            Slider density = SliderRow(content, "Density", 0.01f, 5f, 1f, "0.00");
            Slider cutMax = SliderRow(content, "Cut range max", 0f, 1f, 1f, "0.00");
            contrast?.Bind(threshold);

            UIKit.SectionLabel(content, "Channels");
            var channelSwitches = new List<UISwitch>();
            Color[] swatches = { new Color(1f, 0.42f, 0.42f), new Color(0.24f, 0.84f, 0.55f), new Color(0.42f, 0.6f, 1f) };
            for (int i = 0; i < 3; i++)
            {
                RectTransform row = UIKit.Stack(content, $"Channel{i}", false, 14f, align: TextAnchor.MiddleLeft, expandCross: false);
                UIKit.Size(row, -1, UIKit.SmallControlHeight);
                Image swatch = UIKit.Panel(row, "Swatch", swatches[i], 5f);
                UIKit.Size(swatch, 18f, 18f);
                TextMeshProUGUI label = UIKit.Text(row, $"Channel {i + 1}", UIKit.BodySize, UITheme.FontRole.SemiBold);
                UIKit.Size(label, -1, -1, 1f);
                channelSwitches.Add(UIKit.Switch(row, true));
                channelRows.Add(row.gameObject);
                channelLabels.Add(label);
            }

            // eigener abschnitt, blendet sich ohne maske aus, siehe SegmentationControls
            RectTransform segmentationSection = UIKit.Stack(content, "Segmentation", true, 14f);
            segmentationSection.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
            UIKit.SectionLabel(segmentationSection, "Segmentation");
            RectTransform showRow = UIKit.Stack(segmentationSection, "ShowLabels", false, 14f, align: TextAnchor.MiddleLeft, expandCross: false);
            RectTransform showTexts = UIKit.Stack(showRow, "Texts", true, 2f, expandCross: false);
            UIKit.Size(showTexts, -1, -1, 1f);
            UIKit.Text(showTexts, "Show labels", UIKit.BodySize, UITheme.FontRole.SemiBold);
            TextMeshProUGUI count = UIKit.Text(showTexts, "", UIKit.CaptionSize, color: UITheme.TextSecondary);
            UISwitch showLabels = UIKit.Switch(showRow, true);
            RectTransform strengthRow = UIKit.Stack(segmentationSection, "Strength", true, 6f);
            strengthRow.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
            Slider strength = SliderRow(strengthRow, "Label strength", 0f, 1f, 0.85f, "0.00");
            segmentation?.Bind(segmentationSection.gameObject, showLabels, count, strength, strengthRow.gameObject);

            UIKit.SectionLabel(content, "Rendering");
            UISegmented mode = UIKit.Segmented(content, "RenderMode", "Volume", "MIP", "Surface");
            FillWidth(mode);
            RectTransform shadingRow = UIKit.Stack(content, "Shading", false, 14f, align: TextAnchor.MiddleLeft, expandCross: false);
            TextMeshProUGUI shadingLabel = UIKit.Text(shadingRow, "Shading", UIKit.BodySize, UITheme.FontRole.SemiBold);
            UIKit.Size(shadingLabel, -1, -1, 1f);
            UISwitch shading = UIKit.Switch(shadingRow, true);

            // hoehere aufloesung ist schaerfer, kostet aber gpu zeit, zum vergleichen in der brille
            if (renderQuality != null)
            {
                TextMeshProUGUI qualityLabel = UIKit.Text(content, "Render quality · Sharp is crisper but may stutter",
                    UIKit.CaptionSize, color: UITheme.TextSecondary);
                qualityLabel.textWrappingMode = TextWrappingModes.Normal;
                UISegmented quality = UIKit.Segmented(content, "RenderQuality", "Standard", "Sharp");
                FillWidth(quality);
                quality.SetWithoutNotify((int)renderQuality.Current);
                quality.SelectionChanged += index => renderQuality.Apply((RenderQuality.Level)index);
            }

            render?.Bind(density, cutMax, mode, shading, channelSwitches);
            RefreshChannels();
        }

        // label und zahl in einer zeile, darunter der regler
        private Slider SliderRow(RectTransform parent, string label, float min, float max, float value, string format)
        {
            RectTransform block = UIKit.Stack(parent, label, true, 6f);
            block.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
            RectTransform top = UIKit.Stack(block, "Top", false, 8f, align: TextAnchor.MiddleLeft, expandCross: false);
            TextMeshProUGUI name = UIKit.Text(top, label, UIKit.LabelSize, color: new Color(0.79f, 0.81f, 0.84f));
            UIKit.Size(name, -1, -1, 1f);
            TextMeshProUGUI readout = UIKit.Text(top, "", UIKit.LabelSize, UITheme.FontRole.Mono, align: TextAlignmentOptions.Right);
            UIKit.Size(readout, 110f);
            Slider slider = UIKit.Slider(block, min, max, value, label);
            block.gameObject.AddComponent<SliderReadout>().Init(slider, readout, format);
            return slider;
        }

        private void BuildChatPage(RectTransform area)
        {
            RectTransform content = ScrollPage(area, Page.Chat);
            TextMeshProUGUI text = UIKit.Text(content, "", UIKit.BodySize, name: "Messages");
            text.lineSpacing = 6f;
            text.overflowMode = TextOverflowModes.Overflow;
            chatPanel?.Bind(text, pages[Page.Chat].GetComponent<ScrollRect>());
        }

        // kanal zeilen nur bei echten getrennten kanaelen, namen aus metadata.json wenn vorhanden
        private void RefreshChannels()
        {
            NiftiVolumeLoader.Volume vol = volumeView != null ? volumeView.LoadedVolume : null;
            string[] names = metadata != null ? metadata.ChannelNames : null;
            int count = vol != null && vol.IsLiveChannels ? (names != null && names.Length > 0 ? Mathf.Min(names.Length, 3) : 3) : 0;
            for (int i = 0; i < channelRows.Count; i++)
            {
                channelRows[i].SetActive(i < count);
                if (names != null && i < names.Length) channelLabels[i].text = names[i];
                else channelLabels[i].text = $"Channel {i + 1}";
            }
        }

        // auf und zu

        private void ToggleOpen()
        {
            isOpen = !isOpen;
            ApplyOpen();
        }

        private void ApplyOpen()
        {
            if (panelBody != null) panelBody.SetActive(isOpen);
            // zugeklappt wird das x um 45 grad zum plus
            if (closeIcon != null) closeIcon.localRotation = Quaternion.Euler(0f, 0f, isOpen ? 0f : 45f);

            float height = isOpen ? OpenDragBarHeight : ClosedDragBarHeight;
            if (dragBar != null) dragBar.sizeDelta = new Vector2(0f, height);

            // GrabZone haengt am oberen rand, muss beim wachsen und schrumpfen der leiste mitwandern
            if (grabZoneCollider != null && canvasRect != null)
            {
                Vector3 center = grabZoneCollider.center;
                center.y = canvasRect.sizeDelta.y / 2f - height / 2f;
                grabZoneCollider.center = center;
                Vector3 size = grabZoneCollider.size;
                size.y = height;
                grabZoneCollider.size = size;
            }
        }

        private void FaceCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 toCam = cam.transform.position - transform.position;
            toCam.y = 0f; // nur um die hochachse drehen, panel bleibt aufrecht
            if (toCam.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
        }
    }
}
