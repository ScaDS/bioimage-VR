using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BioimageVR
{
    // baut fuer jedes gefundene volumen eine klickbare zeile in listContainer
    // klicken laedt es direkt in volumeView, kein umweg ueber tasten
    // zwei quellen umschaltbar oben in der liste: lokal (VolumeLibrary.Discover) oder
    // direkt aus dem idr katalog durchklicken (IdrClient, gleicher server wie
    // preprocessing/upload_server.py, nur ohne adb/usb - siehe IdrClient.cs)
    public class VolumeGalleryMenu : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private RectTransform listContainer;
        [SerializeField] private IdrClient idrClient;

        private enum Source { Local, Idr }
        private Source source = Source.Local;

        // idrLevel ist "projects"/"datasets"/"images", gleiche drei ebenen wie
        // upload_server.py's idr_list - zurueck geht immer nur eine ebene hoch,
        // kein eigener verlaufsstapel noetig
        private string idrLevel = "projects";
        private int? idrProjectId;
        private string idrProjectName;
        private int? idrDatasetId;
        private string idrDatasetName;
        private int idrOffset;
        private int idrTotal;
        private const int IdrPageSize = 30;

        private void Start()
        {
            Refresh();
        }

        public void Refresh()
        {
            if (listContainer == null) return;
            Clear();
            CreateSourceSwitchRow();

            if (source == Source.Local) RefreshLocal();
            else RefreshIdr();
        }

        private void Clear()
        {
            foreach (Transform child in listContainer) Destroy(child.gameObject);
        }

        private void CreateSourceSwitchRow()
        {
            var rowGO = new GameObject("SourceSwitch", typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            rowGO.transform.SetParent(listContainer, false);
            rowGO.GetComponent<LayoutElement>().minHeight = 48f;
            var layout = rowGO.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            CreateSwitchButton(rowGO.transform, "Lokal", source == Source.Local, () => SwitchSource(Source.Local));
            CreateSwitchButton(rowGO.transform, "Aus IDR", source == Source.Idr, () => SwitchSource(Source.Idr));
        }

        private void CreateSwitchButton(Transform parent, string label, bool active, UnityAction onClick)
        {
            var go = new GameObject(label + "Button", typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = UITheme.RoundedSprite(10);
            image.type = Image.Type.Sliced;
            image.color = Color.white;

            var text = CreateLabel(label, go.transform);
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 20;

            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            var colors = button.colors;
            colors.normalColor = active ? UITheme.AccentSoft : UITheme.Surface;
            colors.highlightedColor = UITheme.SurfaceHover;
            colors.pressedColor = UITheme.SurfacePressed;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        private void SwitchSource(Source newSource)
        {
            if (source == newSource) return;
            source = newSource;
            if (source == Source.Idr)
            {
                idrLevel = "projects";
                idrProjectId = null;
                idrDatasetId = null;
                idrOffset = 0;
            }
            Refresh();
        }

        private void RefreshLocal()
        {
            var entries = VolumeLibrary.Discover();
            if (entries.Count == 0)
            {
                CreateLabel("(keine volumen gefunden)");
                return;
            }
            foreach (var entry in entries) CreateLocalRow(entry);
        }

        private void CreateLocalRow(VolumeLibrary.Entry entry)
        {
            var rowGO = new GameObject(entry.DisplayName, typeof(Image), typeof(Button), typeof(LayoutElement));
            rowGO.transform.SetParent(listContainer, false);
            rowGO.GetComponent<LayoutElement>().minHeight = 56f;

            var image = rowGO.GetComponent<Image>();
            image.sprite = UITheme.RoundedSprite(12);
            image.type = Image.Type.Sliced;
            image.color = Color.white; // colorblock unten setzt die eigentliche farbe

            CreateLabel(entry.DisplayName, rowGO.transform);

            var button = rowGO.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => volumeView.LoadVolume(entry.Path));

            var colors = button.colors;
            colors.normalColor = UITheme.Surface;
            colors.highlightedColor = UITheme.SurfaceHover;
            colors.pressedColor = UITheme.SurfacePressed;
            colors.selectedColor = UITheme.Surface;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        private void RefreshIdr()
        {
            if (idrClient == null)
            {
                CreateLabel("(kein IdrClient verkabelt)");
                return;
            }

            idrOffset = 0;
            CreateBreadcrumbRow();
            CreateLabel("Lade ...");
            idrClient.ListLevel(idrLevel, idrProjectId, idrDatasetId, idrOffset, IdrPageSize,
                (items, total, error) => OnIdrListLoaded(items, total, error, append: false));
        }

        // haengt weitere eintraege HINTEN an statt die schon gezeigten wegzuwerfen -
        // ListLevel liefert bei jedem aufruf nur die eine angefragte seite, nicht alles bisherige
        private void LoadMoreIdr()
        {
            idrClient.ListLevel(idrLevel, idrProjectId, idrDatasetId, idrOffset, IdrPageSize,
                (items, total, error) => OnIdrListLoaded(items, total, error, append: true));
        }

        private void CreateBreadcrumbRow()
        {
            string crumb = "Studien";
            if (idrProjectId.HasValue) crumb += " > " + (idrProjectName ?? ("#" + idrProjectId));
            if (idrDatasetId.HasValue) crumb += " > " + (idrDatasetName ?? ("#" + idrDatasetId));

            var textRowGO = new GameObject("Breadcrumb", typeof(LayoutElement));
            textRowGO.transform.SetParent(listContainer, false);
            textRowGO.GetComponent<LayoutElement>().minHeight = 34f;
            var text = CreateLabel(crumb, textRowGO.transform);
            text.color = UITheme.TextSecondary;
            text.fontSize = 18;

            if (idrLevel == "projects") return;

            var backRowGO = new GameObject("BackRow", typeof(Image), typeof(Button), typeof(LayoutElement));
            backRowGO.transform.SetParent(listContainer, false);
            backRowGO.GetComponent<LayoutElement>().minHeight = 44f;
            var backImage = backRowGO.GetComponent<Image>();
            backImage.sprite = UITheme.RoundedSprite(10);
            backImage.type = Image.Type.Sliced;
            backImage.color = Color.white;
            CreateLabel("< Zurueck", backRowGO.transform);

            var backButton = backRowGO.GetComponent<Button>();
            backButton.targetGraphic = backImage;
            backButton.onClick.AddListener(GoUpOneLevel);
            var backColors = backButton.colors;
            backColors.normalColor = UITheme.Surface;
            backColors.highlightedColor = UITheme.SurfaceHover;
            backColors.pressedColor = UITheme.SurfacePressed;
            backColors.fadeDuration = 0.08f;
            backButton.colors = backColors;
        }

        private void GoUpOneLevel()
        {
            if (idrLevel == "images") { idrLevel = "datasets"; idrDatasetId = null; }
            else if (idrLevel == "datasets") { idrLevel = "projects"; idrProjectId = null; }
            idrOffset = 0;
            Refresh();
        }

        private void OnIdrListLoaded(List<IdrItem> items, int total, string error, bool append)
        {
            if (this == null) return; // panel evtl. neu gebaut worden waehrend die anfrage lief

            if (append)
            {
                // die gerade geklickte "Mehr laden" zeile entfernen, sonst bleibt sie
                // mitten in der liste liegen statt ganz unten neu zu erscheinen
                Transform oldLoadMore = listContainer.Find("LoadMore");
                if (oldLoadMore != null) Destroy(oldLoadMore.gameObject);
            }
            else
            {
                Clear();
                CreateSourceSwitchRow();
                CreateBreadcrumbRow();
            }

            if (!string.IsNullOrEmpty(error))
            {
                CreateLabel("Fehler: " + error);
                return;
            }

            idrTotal = total;
            idrOffset += items.Count;

            if (!append && items.Count == 0)
            {
                CreateLabel("(keine Eintraege)");
                return;
            }

            foreach (var item in items) CreateIdrRow(item);
            if (idrOffset < idrTotal) CreateLoadMoreRow();
        }

        private void CreateIdrRow(IdrItem item)
        {
            string baseName = item.name ?? ("#" + item.id);
            var rowGO = new GameObject(baseName, typeof(Image), typeof(Button), typeof(LayoutElement));
            rowGO.transform.SetParent(listContainer, false);
            rowGO.GetComponent<LayoutElement>().minHeight = 56f;

            var image = rowGO.GetComponent<Image>();
            image.sprite = UITheme.RoundedSprite(12);
            image.type = Image.Type.Sliced;
            image.color = Color.white;

            bool noZarr = idrLevel == "images" && !item.has_zarr;
            var text = CreateLabel(noZarr ? baseName + "  (kein Volumen)" : baseName, rowGO.transform);
            if (noZarr) text.color = UITheme.TextSecondary;

            var button = rowGO.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => OnIdrItemClicked(item));

            var colors = button.colors;
            colors.normalColor = UITheme.Surface;
            colors.highlightedColor = UITheme.SurfaceHover;
            colors.pressedColor = UITheme.SurfacePressed;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        private void OnIdrItemClicked(IdrItem item)
        {
            if (idrLevel == "projects")
            {
                idrProjectId = item.id;
                idrProjectName = item.name;
                idrLevel = "datasets";
                idrOffset = 0;
                Refresh();
            }
            else if (idrLevel == "datasets")
            {
                idrDatasetId = item.id;
                idrDatasetName = item.name;
                idrLevel = "images";
                idrOffset = 0;
                Refresh();
            }
            else
            {
                DownloadAndLoad(item);
            }
        }

        private void DownloadAndLoad(IdrItem item)
        {
            Clear();
            CreateSourceSwitchRow();
            CreateBreadcrumbRow();
            CreateLabel($"Laedt {(item.name ?? ("#" + item.id))} ... (kann etwas dauern)");

            idrClient.FetchVolume(item.id, (path, error) =>
            {
                if (this == null) return;
                if (!string.IsNullOrEmpty(error))
                {
                    Clear();
                    CreateSourceSwitchRow();
                    CreateBreadcrumbRow();
                    CreateLabel("Fehler beim Laden: " + error);
                    return;
                }
                volumeView.LoadVolume(path);
            });
        }

        private void CreateLoadMoreRow()
        {
            var rowGO = new GameObject("LoadMore", typeof(Image), typeof(Button), typeof(LayoutElement));
            rowGO.transform.SetParent(listContainer, false);
            rowGO.GetComponent<LayoutElement>().minHeight = 48f;
            var image = rowGO.GetComponent<Image>();
            image.sprite = UITheme.RoundedSprite(10);
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            var text = CreateLabel("Mehr laden", rowGO.transform);
            text.alignment = TextAnchor.MiddleCenter;

            var button = rowGO.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() =>
            {
                button.interactable = false;
                text.text = "Lade ...";
                LoadMoreIdr();
            });
            var colors = button.colors;
            colors.normalColor = UITheme.AccentSoft;
            colors.highlightedColor = UITheme.SurfaceHover;
            colors.pressedColor = UITheme.SurfacePressed;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        private void CreateLabel(string message)
        {
            CreateLabel(message, listContainer);
        }

        private Text CreateLabel(string message, Transform parent)
        {
            var go = new GameObject("Text", typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(16f, 0f);
            rect.offsetMax = new Vector2(-16f, 0f);

            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 22;
            text.color = UITheme.TextPrimary;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = message;
            return text;
        }
    }
}
