using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // "i" knopf unten links (siehe SetupSidePanel.CreateCornerIcons) togglet ein text-
    // overlay mit dem rohen inhalt von metadata.json fuers gerade geladene volumen.
    // roh statt in einzelne felder geparst, weil JsonUtility keine dictionaries kann
    // (key_values ist ein freies key-value-objekt, siehe fetch_from_idr.fetch_metadata)
    // und die datei ohnehin schon huebsch mit indent=2 formatiert ist
    public class VolumeMetadataPanel : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private Button infoButton;
        [SerializeField] private GameObject panelGO;
        [SerializeField] private Text metadataText;

        private void Awake()
        {
            if (infoButton != null) infoButton.onClick.AddListener(Toggle);
        }

        private void OnEnable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded += HandleVolumeLoaded;
        }

        private void OnDisable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        // neues volumen waehrend die tafel offen ist -> gleich mit aktualisieren, sonst
        // zeigt sie noch die metadaten vom vorherigen bild
        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume)
        {
            if (panelGO != null && panelGO.activeSelf) Refresh();
        }

        private void Toggle()
        {
            if (panelGO == null) return;
            bool willOpen = !panelGO.activeSelf;
            panelGO.SetActive(willOpen);
            if (willOpen) Refresh();
        }

        // <name>_metadata.json zuerst (android, flach im persistentDataPath gepusht,
        // siehe fetch_from_idr.push_one), sonst metadata.json im selben ordner
        // (editor/standalone, ein ordner pro bild, siehe fetch_from_idr.fetch_one)
        private void Refresh()
        {
            if (metadataText == null) return;
            string volumePath = volumeView != null ? volumeView.VolumeFilePath : null;
            if (string.IsNullOrEmpty(volumePath))
            {
                metadataText.text = "(kein Volumen geladen)";
                return;
            }

            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string namedPath = Path.Combine(dir, $"{stem}_metadata.json");
            string sharedPath = Path.Combine(dir, "metadata.json");
            string path = File.Exists(namedPath) ? namedPath : File.Exists(sharedPath) ? sharedPath : null;

            metadataText.text = path != null
                ? File.ReadAllText(path)
                : "(keine Metadaten fuer dieses Bild gefunden)";
        }
    }
}
