using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // tab Cells, tabelle aller zellen, zeile hebt hervor, klick waehlt, auge blendet aus
    public class CellListPanel : MonoBehaviour
    {
        private VolumeView volumeView;
        private CellInspector cellInspector;
        private LabelEditor labelEditor;
        private RectTransform listContainer;

        // mehr zeilen machen das panel auf der quest zaeh, bei riesigen masken reicht der anfang
        private const int MaxRows = 400;
        private const float RowHeight = 58f;

        private enum Sort { Size, Id, Flagged }
        private Sort sort = Sort.Size;
        private readonly Dictionary<int, Button> rows = new Dictionary<int, Button>();
        private readonly Dictionary<int, Button> visibilityButtons = new Dictionary<int, Button>();
        private bool subscribed;

        public void Init(VolumeView view, CellInspector inspector, LabelEditor editor, RectTransform container)
        {
            volumeView = view;
            cellInspector = inspector;
            labelEditor = editor;
            listContainer = container;
            if (isActiveAndEnabled) OnEnable();
        }

        private void OnEnable()
        {
            if (volumeView == null || subscribed) return;
            volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            volumeView.SelectionChanged += HandleSelectionChanged;
            volumeView.CellVisibilityChanged += RefreshVisibility;
            volumeView.LabelsChanged += Rebuild;
            volumeView.SplitHintsChanged += Rebuild;
            subscribed = true;
            Rebuild();
        }

        private void OnDisable()
        {
            if (volumeView == null || !subscribed) return;
            volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
            volumeView.SelectionChanged -= HandleSelectionChanged;
            volumeView.CellVisibilityChanged -= RefreshVisibility;
            volumeView.LabelsChanged -= Rebuild;
            volumeView.SplitHintsChanged -= Rebuild;
            subscribed = false;
            SetListHover(0);
        }

        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => Rebuild();

        private void HandleSelectionChanged(int id) => RefreshRowStyles();

        private void Rebuild()
        {
            if (listContainer == null) return;
            foreach (Transform child in listContainer) Destroy(child.gameObject);
            rows.Clear();
            visibilityButtons.Clear();

            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            if (vol?.Cells == null)
            {
                TextMeshProUGUI empty = UIKit.Text(listContainer, "This image has no segmentation. Load one from the Images tab that shows a Segmentation tag.",
                    UIKit.LabelSize, color: UITheme.TextSecondary);
                UIKit.Size(empty, -1, 90f);
                return;
            }

            List<CellInfo> cells = vol.Cells.Where(c => c.Exists).ToList();
            int flagged = cells.Count(c => volumeView.GetSplitHint(c.Id).Found);
            cells = sort switch
            {
                Sort.Id => cells.OrderBy(c => c.Id).ToList(),
                Sort.Flagged => cells.OrderByDescending(c => volumeView.GetSplitHint(c.Id).Found)
                    .ThenByDescending(c => volumeView.GetSplitHint(c.Id).Score).ThenBy(c => c.Id).ToList(),
                _ => cells.OrderByDescending(c => c.VoxelCount).ToList(),
            };

            BuildHeader(cells.Count, flagged);
            foreach (CellInfo cell in cells.Take(MaxRows)) BuildRow(cell, vol);
            if (cells.Count > MaxRows)
                UIKit.Text(listContainer, $"… and {cells.Count - MaxRows} more", UIKit.CaptionSize, color: UITheme.TextSecondary);

            RefreshRowStyles();
            RefreshVisibility();
        }

        // organnamen brauchen mehr platz als eine nummer
        private const float NamedColumnWidth = 250f;

        private void BuildHeader(int count, int flagged)
        {
            RectTransform top = UIKit.Stack(listContainer, "Summary", false, 12f, align: TextAnchor.MiddleLeft, expandCross: false);
            RectTransform titles = UIKit.Stack(top, "Titles", true, 2f, expandCross: false);
            UIKit.Size(titles, -1, -1, 1f);
            bool named = volumeView.HasLabelNames;
            UIKit.Text(titles, named ? $"{count} structures" : $"{count} cells", UIKit.HeadingSize, UITheme.FontRole.Bold);
            if (flagged > 0)
                UIKit.Text(titles, $"{flagged} flagged as possibly 2 cells", UIKit.CaptionSize, color: UITheme.Warning);
            UIKit.Button(top, "Show all", UIKit.ButtonStyle.Secondary, Icons.View, UIKit.SmallControlHeight)
                .onClick.AddListener(() => volumeView.ShowAllCells());

            UISegmented sorting = UIKit.Segmented(listContainer, "Sort", "Size", "ID", "Flagged");
            sorting.SetWithoutNotify((int)sort);
            sorting.SelectionChanged += index =>
            {
                sort = (Sort)index;
                Rebuild();
            };

            RectTransform columns = UIKit.Stack(listContainer, "Columns", false, 8f, new RectOffset(16, 16, 6, 0),
                TextAnchor.MiddleLeft, false);
            UIKit.Size(UIKit.Text(columns, named ? "STRUCTURE" : "CELL", UIKit.CaptionSize - 1f, UITheme.FontRole.SemiBold,
                UITheme.TextDisabled), named ? NamedColumnWidth : 110f);
            UIKit.Size(UIKit.Text(columns, "VOLUME", UIKit.CaptionSize - 1f, UITheme.FontRole.SemiBold, UITheme.TextDisabled), -1, -1, 1f);
            UIKit.Size(UIKit.Text(columns, "Ø", UIKit.CaptionSize - 1f, UITheme.FontRole.SemiBold, UITheme.TextDisabled,
                TextAlignmentOptions.Right), 90f);
            UIKit.Size(UIKit.Text(columns, "", UIKit.CaptionSize), 90f);
        }

        private void BuildRow(CellInfo cell, NiftiVolumeLoader.Volume vol)
        {
            int id = cell.Id;
            Vector3 vs = vol.VoxelSize;
            string unit = volumeView.Unit;

            Button row = UIKit.Button(listContainer, "", UIKit.ButtonStyle.Borderless, height: RowHeight, name: $"Cell{id}");
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(16, 8, 0, 0);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            // zeile faerbt ihre texte selbst
            Destroy(row.GetComponent<ContentTint>());

            RectTransform idCell = UIKit.Stack(row.transform, "Id", false, 12f, align: TextAnchor.MiddleLeft, expandCross: false);
            bool named = volumeView.HasLabelNames;
            UIKit.Size(idCell, named ? NamedColumnWidth : 110f);
            Image dot = UIKit.Panel(idCell, "Dot", UITheme.LabelColor(id), 8f);
            dot.raycastTarget = false;
            UIKit.Size(dot, 16f, 16f);
            UIKit.Text(idCell, named ? volumeView.LabelName(id) : id.ToString(), UIKit.LabelSize, UITheme.FontRole.SemiBold);

            var numberColor = new Color(0.79f, 0.81f, 0.84f);
            UIKit.Size(UIKit.Text(row.transform, Units.Volume(CellStats.Volume(cell, vs), unit), UIKit.LabelSize,
                UITheme.FontRole.Mono, numberColor), -1, -1, 1f);
            UIKit.Size(UIKit.Text(row.transform, Units.Plain(CellStats.EquivalentDiameter(cell, vs), unit), UIKit.LabelSize,
                UITheme.FontRole.Mono, numberColor, TextAlignmentOptions.Right), 90f);

            // orange punkt fuer "evtl. 2 zellen", siehe CellShapeAnalysis
            RectTransform flagCell = UIKit.Stack(row.transform, "Flag", false, 0f, align: TextAnchor.MiddleCenter, expandCross: false);
            UIKit.Size(flagCell, 24f);
            if (volumeView.GetSplitHint(id).Found)
            {
                Image flag = UIKit.Panel(flagCell, "Warning", UITheme.Warning, 6f);
                flag.raycastTarget = false;
                UIKit.Size(flag, 12f, 12f);
            }

            Button eye = UIKit.IconButton(row.transform, Icons.View, "Visibility", UIKit.ButtonStyle.Borderless, UIKit.SmallControlHeight);
            eye.onClick.AddListener(() => volumeView.SetCellVisible(id, !volumeView.IsCellVisible(id)));
            visibilityButtons[id] = eye;

            row.onClick.AddListener(() =>
            {
                // im verbinden modus waehlt die zeile die zweite zelle
                if (labelEditor != null && labelEditor.TryHandleClick(id)) return;
                volumeView.SelectedLabel = volumeView.SelectedLabel == id ? 0 : id;
            });

            // zeile unter dem strahl, zelle im volumen leuchtet mit
            var hover = row.gameObject.AddComponent<CellRowHover>();
            hover.Entered = () => SetListHover(id);
            hover.Exited = () => SetListHover(0);
            rows[id] = row;
        }

        private void RefreshRowStyles()
        {
            foreach (var pair in rows)
            {
                if (pair.Value == null) continue;
                bool selected = pair.Key == volumeView.SelectedLabel;
                ColorBlock colors = pair.Value.colors;
                colors.normalColor = selected ? UITheme.AccentSoft : new Color(1f, 1f, 1f, 0f);
                colors.selectedColor = colors.normalColor;
                colors.highlightedColor = selected ? UITheme.SurfacePressed : UITheme.Surface;
                pair.Value.colors = colors;
            }
        }

        private void RefreshVisibility()
        {
            foreach (var pair in visibilityButtons)
            {
                if (pair.Value == null) continue;
                bool visible = volumeView.IsCellVisible(pair.Key);
                var glyph = pair.Value.GetComponentInChildren<TMP_Text>();
                if (glyph != null) glyph.text = visible ? Icons.View : Icons.Hidden;
                var tint = pair.Value.GetComponent<ContentTint>();
                if (tint != null) tint.Enabled = visible ? UITheme.TextSecondary : UITheme.TextDisabled;
            }
        }

        private void SetListHover(int id)
        {
            if (cellInspector != null) cellInspector.SetListHover(id);
        }
    }
}
