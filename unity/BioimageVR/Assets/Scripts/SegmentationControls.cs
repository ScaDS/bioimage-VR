using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // segmentierung an aus, zeile in den einstellungen
    // nur sichtbar wenn zum aktuellen bild eine _labels datei geladen wurde
    public class SegmentationControls : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private Button toggleButton;
        [SerializeField] private Text toggleLabel;
        [SerializeField] private GameObject row;

        private void Awake()
        {
            if (toggleButton != null) toggleButton.onClick.AddListener(Toggle);
        }

        private void OnEnable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            Refresh();
        }

        private void OnDisable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => Refresh();

        private void Toggle()
        {
            if (volumeView == null) return;
            volumeView.ShowLabels = !volumeView.ShowLabels;
            Refresh();
        }

        // fuer aufrufer ausserhalb der ui, z.b. spaeter VLMToolDispatcher
        public void SetVisibleExternal(bool on)
        {
            if (volumeView == null) return;
            volumeView.ShowLabels = on;
            Refresh();
        }

        private void Refresh()
        {
            bool hasLabels = volumeView != null && volumeView.HasLabels;
            if (row != null) row.SetActive(hasLabels);
            if (!hasLabels || toggleLabel == null) return;

            int cells = volumeView.LoadedVolume.LabelCount;
            toggleLabel.text = volumeView.ShowLabels ? $"An ({cells} Zellen)" : "Aus";
        }
    }
}
