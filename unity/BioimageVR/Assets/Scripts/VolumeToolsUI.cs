using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace BioimageVR
{
    // oberflaeche rund ums volumen, abgestimmtes mockup vom 30.09.
    // links die werkzeugleiste, unten die optionen des aktiven werkzeugs mit einer hinweiszeile,
    // rechts die karte zur ausgewaehlten zelle. zur laufzeit gebaut, das setup verdrahtet nur
    // die referenzen, aussehen aendern braucht also kein neues Setup Side Panel
    public class VolumeToolsUI : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private ToolController tools;
        [SerializeField] private MeasureTool measureTool;
        [SerializeField] private LabelEditor labelEditor;
        [SerializeField] private VolumeRenderControls renderControls;
        [SerializeField] private VolumeZoom volumeZoom;
        [SerializeField] private VolumeRotate volumeRotate;
        [SerializeField] private SidePanelController sidePanel;
        [SerializeField] private ChatPanel chatPanel;
        [SerializeField] private VoiceVLMHarness voice;

        private const float CanvasScale = 0.00085f;
        private static readonly int RenderModeId = Shader.PropertyToID("_RenderMode");

        private readonly Dictionary<ToolController.Tool, Button> toolButtons = new Dictionary<ToolController.Tool, Button>();
        private readonly Dictionary<ToolController.Tool, GameObject> optionGroups = new Dictionary<ToolController.Tool, GameObject>();
        private TextMeshProUGUI hint;

        private UISegmented renderSegmented;
        private Button flyButton;
        private UISegmented flyModeSegmented;
        private Button previousStationButton, nextStationButton;
        private UISegmented flySpeedSegmented;
        private static readonly float[] FlySpeeds = { 0.4f, 1f, 2.2f };
        private VolumeFly volumeFly;
        private Button labelsButton;
        private Button screenButton;
        private ScreenMirror screen;
        private UISegmented measureSegmented;
        private Button deleteButton, mergeButton, splitButton, undoButton, saveButton;

        private GameObject card;
        private Image cardDot;
        private TextMeshProUGUI cardTitle;
        private RectTransform cardWarning;
        private TextMeshProUGUI cardWarningText;
        private readonly List<TextMeshProUGUI> cardValues = new List<TextMeshProUGUI>();
        private Button splitAtNeckButton;
        private Button isolateButton;
        private int isolated;

        // meldungen vom korrektur werkzeug bleiben kurz stehen
        private const float MessageSeconds = 4f;
        private float messageUntil;
        private float noPathUntil;

        private void Start()
        {
            volumeFly = volumeView.GetComponent<VolumeFly>();
            // gehoert zur napari verbindung, ohne die gibt es den knopf nicht
            var bridge = FindFirstObjectByType<VrBridge>();
            screen = bridge != null ? bridge.Mirror : null;
            DisableStickNavigation();
            BuildRail();
            BuildOptions();
            BuildCard();
            if (tools != null) tools.ToolChanged += _ => RefreshTools();
            if (labelEditor != null) labelEditor.Changed += () => messageUntil = Time.time + MessageSeconds;
            if (volumeView != null) volumeView.OnVolumeLoaded += _ => isolated = 0;
            RefreshTools();
        }

        // quest meldet controller auch als gamepad, ui modul verschiebt damit den zuletzt
        // angeklickten regler. sticks gehoeren dem volumen, ui nur per strahl
        private static void DisableStickNavigation()
        {
            var eventSystem = EventSystem.current;
            var module = eventSystem != null ? eventSystem.GetComponent<XRUIInputModule>() : null;
            if (module == null) return;
            module.enableGamepadInput = false;
            module.enableJoystickInput = false;
        }

        // canvas neben dem volumen, pivot an der seite die zum volumen zeigt
        private RectTransform CreateCanvas(string name, Vector2 size, VolumeAnchor.Side side, Vector2 pivot)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(TrackedDeviceGraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.pivot = pivot;
            rect.localScale = Vector3.one * CanvasScale;
            go.AddComponent<VolumeAnchor>().Init(volumeView.transform, side, 0.05f);
            return rect;
        }

        private static RectTransform Pin(RectTransform child, Vector2 anchor)
        {
            child.anchorMin = child.anchorMax = anchor;
            child.pivot = anchor;
            child.anchoredPosition = Vector2.zero;
            return child;
        }

        // werkzeugleiste

        private void BuildRail()
        {
            RectTransform canvas = CreateCanvas("ToolRail", new Vector2(140f, 480f), VolumeAnchor.Side.Left, new Vector2(1f, 0.5f));
            RectTransform rail = UIKit.Surface(canvas, "Rail", true, UITheme.Background, UIKit.PanelRadius,
                new RectOffset(10, 10, 10, 10), 8f, TextAnchor.MiddleCenter);
            Pin(rail, new Vector2(1f, 0.5f));

            AddTool(rail, ToolController.Tool.View, Icons.View, "View");
            AddTool(rail, ToolController.Tool.Measure, Icons.Measure, "Measure");
            AddTool(rail, ToolController.Tool.Segment, Icons.Segment, "Segment");
            AddTool(rail, ToolController.Tool.Assistant, Icons.Chat, "Assistant");
        }

        private void AddTool(Transform rail, ToolController.Tool tool, string icon, string label)
        {
            Button button = UIKit.ToolButton(rail, icon, label);
            button.onClick.AddListener(() => tools.SetTool(tool));
            toolButtons[tool] = button;
        }

        // optionen unten

        private void BuildOptions()
        {
            RectTransform canvas = CreateCanvas("ToolOptions", new Vector2(1300f, 220f), VolumeAnchor.Side.Bottom, new Vector2(0.5f, 1f));
            RectTransform column = UIKit.Stack(canvas, "Column", true, 14f, align: TextAnchor.UpperCenter, expandCross: false);
            Pin(column, new Vector2(0.5f, 1f));
            column.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            column.GetComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform bar = UIKit.Surface(column, "Bar", false, UITheme.Background, UIKit.ControlHeight / 2f + 10f,
                new RectOffset(10, 10, 10, 10), 8f, TextAnchor.MiddleCenter, fit: false);

            // view
            RectTransform view = Group(bar, ToolController.Tool.View);
            renderSegmented = UIKit.Segmented(view, "RenderMode", "Volume", "MIP", "Surface");
            renderSegmented.SelectionChanged += index => renderControls?.SetRenderModeExternal((VolumeRenderControls.Mode)index);
            flyButton = UIKit.Button(view, "Fly", UIKit.ButtonStyle.Secondary);
            flyButton.onClick.AddListener(() => volumeFly?.SetActive(!volumeFly.Active));
            // nur beim fliegen sichtbar, Orient legt das volumen zurecht statt zu fliegen
            // Path faehrt den vorberechneten weg ab, im surface modus sieht man dabei die waende
            flyModeSegmented = UIKit.Segmented(view, "FlyMode", "Move", "Orient", "Path");
            flyModeSegmented.SelectionChanged += index =>
            {
                if (volumeFly == null) return;
                volumeFly.SetMode((VolumeFly.FlyMode)index);
                if (volumeFly.Mode == VolumeFly.FlyMode.Path)
                    renderControls?.SetRenderModeExternal(VolumeRenderControls.Mode.Surface);
                else if (index == (int)VolumeFly.FlyMode.Path)
                    noPathUntil = Time.time + MessageSeconds;
            };
            flySpeedSegmented = UIKit.Segmented(view, "FlySpeed", "Slow", "Normal", "Fast");
            flySpeedSegmented.SetWithoutNotify(1);
            flySpeedSegmented.SelectionChanged += index =>
            {
                if (volumeFly != null) volumeFly.SpeedFactor = FlySpeeds[index];
            };
            previousStationButton = UIKit.Button(view, "Back", UIKit.ButtonStyle.Secondary);
            previousStationButton.onClick.AddListener(() => volumeFly?.Path.RideToStation(-1));
            nextStationButton = UIKit.Button(view, "Next stop", UIKit.ButtonStyle.Secondary);
            nextStationButton.onClick.AddListener(() => volumeFly?.Path.RideToStation(1));
            labelsButton = UIKit.Button(view, "Labels on", UIKit.ButtonStyle.Secondary, Icons.Layers);
            labelsButton.onClick.AddListener(() => volumeView.ShowLabels = !volumeView.ShowLabels);
            Button reset = UIKit.Button(view, "Reset view", UIKit.ButtonStyle.Secondary, Icons.Reset);
            reset.onClick.AddListener(() =>
            {
                volumeZoom?.ResetView();
                volumeRotate?.ResetView();
            });
            UIKit.IconButton(view, Icons.Camera, "Screenshot").onClick.AddListener(TakeScreenshot);
            // napari fenster vom pc als leinwand, auch wenn der echte monitor nicht im blick ist
            screenButton = UIKit.Button(view, "PC screen", UIKit.ButtonStyle.Secondary);
            screenButton.onClick.AddListener(() => screen?.SetActive(!screen.Active));
            screenButton.gameObject.SetActive(screen != null);

            // measure
            RectTransform measure = Group(bar, ToolController.Tool.Measure);
            measureSegmented = UIKit.Segmented(measure, "MeasureMode", "Ruler", "Auto");
            measureSegmented.SelectionChanged += index =>
                tools.SetMeasureMode(index == 0 ? MeasureTool.Mode.Ruler : MeasureTool.Mode.Blob);
            UIKit.Button(measure, "Clear", UIKit.ButtonStyle.Secondary, Icons.Close).onClick.AddListener(() => measureTool?.Clear());

            // segment
            RectTransform segment = Group(bar, ToolController.Tool.Segment);
            deleteButton = UIKit.Button(segment, "Delete", UIKit.ButtonStyle.Destructive, Icons.Delete);
            deleteButton.onClick.AddListener(() => labelEditor.DeleteSelected());
            mergeButton = UIKit.Button(segment, "Merge", UIKit.ButtonStyle.Secondary, Icons.Merge);
            mergeButton.onClick.AddListener(() =>
            {
                if (labelEditor.MergeSource > 0) labelEditor.CancelMerge();
                else labelEditor.StartMerge();
            });
            splitButton = UIKit.Button(segment, "Split", UIKit.ButtonStyle.Secondary, Icons.Split);
            splitButton.onClick.AddListener(() =>
            {
                if (labelEditor.SplitSource > 0) labelEditor.CancelSplit();
                else labelEditor.StartSplit();
            });
            UIKit.Divider(segment, true);
            undoButton = UIKit.IconButton(segment, Icons.Undo, "Undo");
            undoButton.onClick.AddListener(() => labelEditor.Undo());
            saveButton = UIKit.Button(segment, "Save", UIKit.ButtonStyle.Primary, Icons.Save);
            saveButton.onClick.AddListener(() => labelEditor.Save());

            // assistant
            RectTransform assistant = Group(bar, ToolController.Tool.Assistant);
            UIKit.Button(assistant, "Open chat", UIKit.ButtonStyle.Secondary, Icons.Forum)
                .onClick.AddListener(() => sidePanel?.ShowChat());

            hint = UIKit.Text(column, "", UIKit.BodySize, color: UITheme.TextSecondary, align: TextAlignmentOptions.Center, name: "Hint");
            hint.textWrappingMode = TextWrappingModes.NoWrap;
            hint.overflowMode = TextOverflowModes.Overflow;
        }

        private RectTransform Group(Transform bar, ToolController.Tool tool)
        {
            RectTransform group = UIKit.Stack(bar, $"{tool}Options", false, 8f, align: TextAnchor.MiddleCenter, expandCross: false);
            optionGroups[tool] = group.gameObject;
            return group;
        }

        // karte zur auswahl

        private void BuildCard()
        {
            RectTransform canvas = CreateCanvas("SelectionCard", new Vector2(420f, 700f), VolumeAnchor.Side.Right, new Vector2(0f, 0.5f));
            RectTransform surface = UIKit.Surface(canvas, "Card", true, UITheme.Background, UIKit.CardRadius,
                new RectOffset(26, 26, 24, 24), 18f);
            Pin(surface, new Vector2(0f, 0.5f));
            card = surface.gameObject;

            RectTransform header = UIKit.Stack(surface, "Header", false, 12f, align: TextAnchor.MiddleLeft, expandCross: false);
            cardDot = UIKit.Panel(header, "Dot", Color.white, 9f);
            UIKit.Size(cardDot, 18f, 18f);
            cardTitle = UIKit.Text(header, "Cell", UIKit.HeadingSize, UITheme.FontRole.Bold, name: "Title");
            UIKit.Size(cardTitle, 300f, -1);

            cardWarning = UIKit.Surface(surface, "Warning", true, UITheme.WarningSoft, 16f, new RectOffset(16, 16, 12, 12), fit: false);
            cardWarningText = UIKit.Text(cardWarning, "", UIKit.LabelSize, color: UITheme.Warning, name: "Text");
            UIKit.Size(cardWarningText, 300f, -1);

            string[] rows = { "Volume", "Eq. diameter", "Extent X·Y·Z", "Voxels" };
            foreach (string row in rows)
            {
                RectTransform line = UIKit.Stack(surface, row, false, 8f, align: TextAnchor.MiddleLeft, expandCross: false);
                TextMeshProUGUI label = UIKit.Text(line, row, UIKit.LabelSize, color: UITheme.TextSecondary);
                UIKit.Size(label, 150f, -1);
                TextMeshProUGUI value = UIKit.Text(line, "", UIKit.LabelSize, UITheme.FontRole.Mono,
                    align: TextAlignmentOptions.Right);
                UIKit.Size(value, 170f, -1);
                cardValues.Add(value);
            }

            splitAtNeckButton = UIKit.Button(surface, "Split at neck", UIKit.ButtonStyle.Primary, Icons.Split);
            splitAtNeckButton.onClick.AddListener(() => labelEditor.SplitAtHint(volumeView.SelectedLabel));

            RectTransform actions = UIKit.Stack(surface, "Actions", false, 8f, align: TextAnchor.MiddleCenter);
            Button hide = UIKit.Button(actions, "Hide", UIKit.ButtonStyle.Secondary, Icons.Hidden);
            hide.onClick.AddListener(() => volumeView.SetCellVisible(volumeView.SelectedLabel, false));
            isolateButton = UIKit.Button(actions, "Isolate", UIKit.ButtonStyle.Secondary, Icons.Focus);
            isolateButton.onClick.AddListener(ToggleIsolate);
            card.SetActive(false);
        }

        // nur die ausgewaehlte zelle zeigen, nochmal druecken zeigt wieder alle
        private void ToggleIsolate()
        {
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            int id = volumeView.SelectedLabel;
            if (vol?.Cells == null) return;
            if (isolated > 0)
            {
                volumeView.ShowAllCells();
                isolated = 0;
                return;
            }
            for (int i = 1; i < vol.Cells.Length; i++)
                if (i != id && vol.Cells[i].Exists) volumeView.SetCellVisible(i, false);
            isolated = id;
        }

        private void TakeScreenshot()
        {
            Texture2D shot = SceneScreenshot.Capture(Camera.main);
            if (shot == null) return;
            string dir = Path.Combine(Application.persistentDataPath, "screenshots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"view_{System.DateTime.Now:yyyyMMdd_HHmmss}.png");
            File.WriteAllBytes(path, shot.EncodeToPNG());
            Destroy(shot);
            chatPanel?.AddSystemMessage($"Screenshot saved: {Path.GetFileName(path)}");
        }

        // laufend

        private void Update()
        {
            if (tools == null || volumeView == null) return;
            RefreshOptions();
            RefreshCard();
        }

        private void RefreshTools()
        {
            foreach (var pair in toolButtons)
            {
                bool active = pair.Key == tools.Current;
                UIKit.SetStyle(pair.Value, active ? UIKit.ButtonStyle.Primary : UIKit.ButtonStyle.Borderless);
                var tint = pair.Value.GetComponent<ContentTint>();
                if (tint != null && !active) tint.Enabled = UITheme.TextSecondary;
            }
            foreach (var pair in optionGroups) pair.Value.SetActive(pair.Key == tools.Current);
        }

        private void RefreshOptions()
        {
            switch (tools.Current)
            {
                case ToolController.Tool.View:
                    Material mat = volumeView.GetComponent<MeshRenderer>().material;
                    int mode = Mathf.Clamp(Mathf.RoundToInt(mat.GetFloat(RenderModeId)), 0, 2);
                    renderSegmented.SetWithoutNotify(mode);
                    bool flying = volumeFly != null && volumeFly.Active;
                    flyButton.gameObject.SetActive(volumeFly != null);
                    UIKit.SetStyle(flyButton, flying ? UIKit.ButtonStyle.Selected : UIKit.ButtonStyle.Secondary);
                    UIKit.SetLabel(flyButton, flying ? "Flying" : "Fly");
                    bool orienting = flying && volumeFly.Mode == VolumeFly.FlyMode.Orient;
                    if (flyModeSegmented.gameObject.activeSelf != flying) flyModeSegmented.gameObject.SetActive(flying);
                    if (flying) flyModeSegmented.SetWithoutNotify((int)volumeFly.Mode);
                    bool onPath = flying && volumeFly.Mode == VolumeFly.FlyMode.Path;
                    // beim zurechtlegen gibt es kein tempo
                    bool speeds = flying && !orienting;
                    if (flySpeedSegmented.gameObject.activeSelf != speeds) flySpeedSegmented.gameObject.SetActive(speeds);
                    // bild ohne segmentierung braucht den knopf nicht, die leiste ist beim fliegen voll genug
                    bool labelsShown = volumeView.HasLabels || !flying;
                    if (labelsButton.gameObject.activeSelf != labelsShown) labelsButton.gameObject.SetActive(labelsShown);
                    bool stops = onPath;
                    if (previousStationButton.gameObject.activeSelf != stops) previousStationButton.gameObject.SetActive(stops);
                    if (nextStationButton.gameObject.activeSelf != stops) nextStationButton.gameObject.SetActive(stops);
                    bool screenOn = screen != null && screen.Active;
                    // beim fliegen ist die leiste voll, an bleibt er sichtbar zum ausschalten
                    bool screenShown = screen != null && (!flying || screenOn);
                    if (screenButton.gameObject.activeSelf != screenShown) screenButton.gameObject.SetActive(screenShown);
                    UIKit.SetStyle(screenButton, screenOn ? UIKit.ButtonStyle.Selected : UIKit.ButtonStyle.Secondary);
                    labelsButton.interactable = volumeView.HasLabels;
                    UIKit.SetLabel(labelsButton, !volumeView.HasLabels ? "No labels" : volumeView.ShowLabels ? "Labels on" : "Labels off");
                    if (flying && Time.time < noPathUntil)
                        SetHint("No path for this image yet · create one on the PC with find_fly_path.py");
                    else if (onPath)
                        SetHint(volumeFly.Path.Entering
                            ? "Flying to the start of the path"
                            : volumeFly.Path.Leaving
                            ? "End of the path · flying back out to the overview"
                            : volumeFly.Path.AutoRiding
                            ? $"Look around, the stick takes over any time · {volumeFly.Path.Describe()}"
                            : $"Right stick rides along the path · Next stop takes you there slowly · {volumeFly.Path.Describe()}");
                    else if (orienting)
                        SetHint("Left stick turns and tilts the volume · right stick rolls it, up and down changes size · Move flies again");
                    else if (flying)
                        SetHint(mode == 2
                            ? "Right stick flies where you point, sideways turns · left stick changes size · Orient lays the volume out"
                            : "Right stick flies where you point · left stick changes size · Surface shows the walls");
                    else if (screenOn)
                        SetHint(screen.Receiving
                            ? "The napari window is on your left · point at it and pull the trigger to click · the buttons below it resize it"
                            : "Waiting for napari on the PC · open Plugins > Bioimage VR there");
                    else if (mode == 2)
                        SetHint("Surface shows the first wall above the threshold · Fly takes you inside");
                    else
                        SetHint("Left stick rotates · right stick zooms · point at a cell to see its size");
                    break;

                case ToolController.Tool.Measure:
                    measureSegmented.SetWithoutNotify(measureTool != null && measureTool.CurrentMode == MeasureTool.Mode.Blob ? 1 : 0);
                    SetHint(measureTool != null ? measureTool.Hint : "");
                    break;

                case ToolController.Tool.Segment:
                    RefreshSegment();
                    break;

                case ToolController.Tool.Assistant:
                    // aufnahme und fragen melden sich hier, danach wieder der normale hinweis
                    bool recent = voice != null && !string.IsNullOrEmpty(voice.Status) && Time.time - voice.StatusTime < 6f;
                    SetHint(recent ? voice.Status : "Hold the right trigger and speak · the current view is sent along");
                    break;
            }
        }

        private void RefreshSegment()
        {
            bool available = labelEditor != null && labelEditor.Available && volumeView.ShowLabels;
            int selected = volumeView.SelectedLabel;
            bool merging = available && labelEditor.MergeSource > 0;
            bool splitting = available && labelEditor.SplitSource > 0;
            bool busy = merging || splitting;

            deleteButton.interactable = available && selected > 0 && !busy;
            mergeButton.interactable = available && ((selected > 0 && !splitting) || merging);
            splitButton.interactable = available && ((selected > 0 && !merging) || splitting);
            undoButton.interactable = available && labelEditor.CanUndo;
            saveButton.interactable = available && labelEditor.UnsavedChanges > 0;

            UIKit.SetLabel(mergeButton, merging ? "Cancel merge" : "Merge");
            UIKit.SetStyle(mergeButton, merging ? UIKit.ButtonStyle.Selected : UIKit.ButtonStyle.Secondary);
            UIKit.SetLabel(splitButton, splitting ? "Cancel split" : "Split");
            UIKit.SetStyle(splitButton, splitting ? UIKit.ButtonStyle.Selected : UIKit.ButtonStyle.Secondary);
            UIKit.SetLabel(saveButton, available && labelEditor.UnsavedChanges > 0 ? $"Save · {labelEditor.UnsavedChanges}" : "Save");

            if (!volumeView.HasLabels) SetHint("This image has no segmentation · load one with labels to correct it");
            else if (!volumeView.ShowLabels) SetHint("Labels are hidden · turn them on in the View tool");
            else if (busy || Time.time < messageUntil) SetHint(labelEditor.LastMessage);
            else if (selected > 0) SetHint($"{volumeView.LabelName(selected)} selected · choose an action above");
            else SetHint("Point at a cell and press A to select it");
        }

        private void SetHint(string text)
        {
            if (hint != null && hint.text != text) hint.text = text;
        }

        private void RefreshCard()
        {
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            int id = volumeView.SelectedLabel;
            bool show = vol?.Cells != null && id > 0 && id < vol.Cells.Length && vol.Cells[id].Exists && volumeView.ShowLabels;
            if (card.activeSelf != show) card.SetActive(show);
            if (!show) return;

            CellInfo cell = vol.Cells[id];
            Vector3 vs = vol.VoxelSize;
            string unit = volumeView.Unit;
            cardDot.color = UITheme.LabelColor(id);
            SetText(cardTitle, volumeView.LabelName(id));

            SplitHint splitHint = volumeView.GetSplitHint(id);
            if (cardWarning.gameObject.activeSelf != splitHint.Found) cardWarning.gameObject.SetActive(splitHint.Found);
            if (splitHint.Found) SetText(cardWarningText, splitHint.Describe());
            bool canSplit = splitHint.Found && labelEditor != null && labelEditor.Available;
            if (splitAtNeckButton.gameObject.activeSelf != canSplit) splitAtNeckButton.gameObject.SetActive(canSplit);

            SetText(cardValues[0], Units.Volume(CellStats.Volume(cell, vs), unit));
            SetText(cardValues[1], Units.Length(CellStats.EquivalentDiameter(cell, vs), unit, "0.0"));
            Vector3Int size = cell.Max - cell.Min + Vector3Int.one;
            SetText(cardValues[2], $"{Units.Plain(size.x * vs.x, unit)}·{Units.Plain(size.y * vs.y, unit)}·{Units.Plain(size.z * vs.z, unit)}");
            SetText(cardValues[3], $"{cell.VoxelCount:N0}");
            UIKit.SetLabel(isolateButton, isolated > 0 ? "Show all" : "Isolate");
        }

        private static void SetText(TMP_Text text, string value)
        {
            if (text.text != value) text.text = value;
        }
    }
}
