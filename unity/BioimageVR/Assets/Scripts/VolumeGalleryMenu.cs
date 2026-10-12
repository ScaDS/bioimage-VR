using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace BioimageVR
{
    // klickbare zeile je volumen, quelle geraet oder idr katalog
    public class VolumeGalleryMenu : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private IdrClient idrClient;

        private RectTransform listContainer;

        private enum Source { Local, Idr }
        private Source source = Source.Local;

        // idrLevel ist projects, datasets oder images, zurueck geht eine ebene hoch
        private string idrLevel = "projects";
        private int? idrProjectId;
        private string idrProjectName;
        private int? idrDatasetId;
        private string idrDatasetName;
        private int idrOffset;
        private const int IdrPageSize = 30;
        private const float RowHeight = 88f;
        private const float ThumbnailSize = 64f;

        public void Bind(RectTransform container)
        {
            listContainer = container;
            Refresh();
        }

        public void Refresh()
        {
            if (listContainer == null) return;
            Clear();
            CreateSourceSwitch();

            if (source == Source.Local) RefreshLocal();
            else RefreshIdr();
        }

        private void Clear()
        {
            foreach (Transform child in listContainer) Destroy(child.gameObject);
        }

        private void CreateSourceSwitch()
        {
            UISegmented segmented = UIKit.Segmented(listContainer, "Source", "On device", "IDR");
            segmented.SetWithoutNotify(source == Source.Local ? 0 : 1);
            segmented.SelectionChanged += index => SwitchSource(index == 0 ? Source.Local : Source.Idr);
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
                Message("No images on this device yet. Send one from napari or the upload page.");
                return;
            }
            string current = volumeView != null ? volumeView.VolumeFilePath : null;
            foreach (var entry in entries)
            {
                bool selected = !string.IsNullOrEmpty(current) && System.IO.Path.GetFileName(current) == System.IO.Path.GetFileName(entry.Path);
                Button row = Row(entry.DisplayName, null, entry.HasLabels ? "Segmentation" : null, selected,
                    () => volumeView.LoadVolume(entry.Path), out RawImage thumbnail);
                if (!string.IsNullOrEmpty(entry.ThumbnailPath))
                    StartCoroutine(LoadThumbnail(thumbnail, "file:///" + entry.ThumbnailPath.Replace('\\', '/')));
            }
        }

        // zeile mit vorschau, name, optionaler zweiter zeile und chip
        private Button Row(string title, string subtitle, string chip, bool selected, UnityAction onClick, out RawImage thumbnail)
        {
            Button row = UIKit.Button(listContainer, "", selected ? UIKit.ButtonStyle.Secondary : UIKit.ButtonStyle.Borderless,
                height: RowHeight, name: title);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(12, 16, 0, 0);
            layout.spacing = 16f;
            layout.childAlignment = TextAnchor.MiddleLeft;

            thumbnail = new GameObject("Thumbnail", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            thumbnail.transform.SetParent(row.transform, false);
            thumbnail.color = UITheme.Surface;
            thumbnail.raycastTarget = false;
            UIKit.Size(thumbnail, ThumbnailSize, ThumbnailSize);

            RectTransform text = UIKit.Stack(row.transform, "Text", true, 4f, align: TextAnchor.MiddleLeft, expandCross: false);
            UIKit.Size(text, -1, -1, 1f);
            TextMeshProUGUI name = UIKit.Text(text, title, UIKit.BodySize, UITheme.FontRole.SemiBold);
            name.textWrappingMode = TextWrappingModes.NoWrap;
            if (!string.IsNullOrEmpty(subtitle))
                UIKit.Text(text, subtitle, UIKit.CaptionSize, UITheme.FontRole.Mono, UITheme.TextSecondary);
            if (!string.IsNullOrEmpty(chip)) UIKit.Chip(text, chip, UITheme.AccentText, UITheme.AccentSoft);

            row.onClick.AddListener(onClick);
            // zeile faerbt ihre texte selbst, ContentTint wuerde unterzeile und chip ueberschreiben
            Destroy(row.GetComponent<ContentTint>());
            return row;
        }

        private void RefreshIdr()
        {
            if (idrClient == null)
            {
                Message("IDR browsing needs the upload server on the PC.");
                return;
            }

            idrOffset = 0;
            CreateBreadcrumb();
            Message("Loading ...");
            idrClient.ListLevel(idrLevel, idrProjectId, idrDatasetId, idrOffset, IdrPageSize,
                (items, nextOffset, hasMore, error) => OnIdrListLoaded(items, nextOffset, hasMore, error, append: false));
        }

        // weitere eintraege hinten anhaengen, ListLevel liefert nur eine seite
        private void LoadMoreIdr()
        {
            idrClient.ListLevel(idrLevel, idrProjectId, idrDatasetId, idrOffset, IdrPageSize,
                (items, nextOffset, hasMore, error) => OnIdrListLoaded(items, nextOffset, hasMore, error, append: true));
        }

        private void CreateBreadcrumb()
        {
            string crumb = "Studies";
            if (idrProjectId.HasValue) crumb += "  ›  " + (idrProjectName ?? ("#" + idrProjectId));
            if (idrDatasetId.HasValue) crumb += "  ›  " + (idrDatasetName ?? ("#" + idrDatasetId));

            RectTransform row = UIKit.Stack(listContainer, "Breadcrumb", false, 12f, align: TextAnchor.MiddleLeft, expandCross: false);
            if (idrLevel != "projects")
                UIKit.Button(row, "Back", UIKit.ButtonStyle.Secondary, height: UIKit.SmallControlHeight).onClick.AddListener(GoUpOneLevel);
            TextMeshProUGUI text = UIKit.Text(row, crumb, UIKit.LabelSize, color: UITheme.TextSecondary);
            UIKit.Size(text, -1, -1, 1f);
        }

        private void GoUpOneLevel()
        {
            if (idrLevel == "images") { idrLevel = "datasets"; idrDatasetId = null; }
            else if (idrLevel == "datasets") { idrLevel = "projects"; idrProjectId = null; }
            idrOffset = 0;
            Refresh();
        }

        private void OnIdrListLoaded(List<IdrItem> items, int nextOffset, bool hasMore, string error, bool append)
        {
            if (this == null || listContainer == null) return; // panel evtl. neu gebaut worden waehrend die anfrage lief

            if (append)
            {
                // geklickte Load more zeile entfernen, sonst bleibt sie mitten in der liste
                Transform oldLoadMore = listContainer.Find("LoadMore");
                if (oldLoadMore != null) Destroy(oldLoadMore.gameObject);
            }
            else
            {
                Clear();
                CreateSourceSwitch();
                CreateBreadcrumb();
            }

            if (!string.IsNullOrEmpty(error))
            {
                Message("Could not reach IDR: " + error);
                return;
            }

            // offset kommt vom server, bilder sind dort gefiltert
            idrOffset = nextOffset;

            if (!append && items.Count == 0)
            {
                Message("Nothing here.");
                return;
            }

            foreach (var item in items)
            {
                string title = item.name ?? ("#" + item.id);
                bool noZarr = idrLevel == "images" && !item.has_zarr;
                Row(title, noZarr ? "no 3D volume available" : $"#{item.id}", null, false, () => OnIdrItemClicked(item),
                    out RawImage thumbnail);
                if (item.preview_image_id > 0)
                    StartCoroutine(LoadThumbnail(thumbnail,
                        $"https://idr.openmicroscopy.org/webclient/render_thumbnail/{item.preview_image_id}/"));
            }
            if (hasMore)
            {
                Button more = UIKit.Button(listContainer, "Load more", UIKit.ButtonStyle.Secondary, name: "LoadMore");
                more.onClick.AddListener(() =>
                {
                    more.interactable = false;
                    UIKit.SetLabel(more, "Loading ...");
                    LoadMoreIdr();
                });
            }
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
            CreateSourceSwitch();
            CreateBreadcrumb();
            Message($"Loading {(item.name ?? ("#" + item.id))} ... this can take a while");

            idrClient.FetchVolume(item.id, (path, error) =>
            {
                if (this == null || listContainer == null) return;
                if (!string.IsNullOrEmpty(error))
                {
                    Clear();
                    CreateSourceSwitch();
                    CreateBreadcrumb();
                    Message("Download failed: " + error);
                    return;
                }
                volumeView.LoadVolume(path);
            });
        }

        // vorschau laedt asynchron und darf lautlos fehlschlagen
        private IEnumerator LoadThumbnail(RawImage target, string url)
        {
            using var request = UnityWebRequestTexture.GetTexture(url);
            yield return request.SendWebRequest();

            if (target == null) yield break; // zeile evtl. inzwischen neu gebaut worden
            if (request.result != UnityWebRequest.Result.Success) yield break;

            target.texture = DownloadHandlerTexture.GetContent(request);
            target.color = Color.white;
        }

        private void Message(string text)
        {
            TextMeshProUGUI label = UIKit.Text(listContainer, text, UIKit.LabelSize, color: UITheme.TextSecondary, name: "Message");
            UIKit.Size(label, -1, 60f);
        }
    }
}
