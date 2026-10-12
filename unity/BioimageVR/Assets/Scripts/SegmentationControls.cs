using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // abschnitt segmentation im display tab, nur mit geladener _labels datei
    public class SegmentationControls : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;

        private GameObject section;
        private UISwitch showSwitch;
        private TMP_Text countLabel;
        private Slider strengthSlider;
        private GameObject strengthRow;

        public void Bind(GameObject sectionRoot, UISwitch toggle, TMP_Text count, Slider strength, GameObject strengthRowRoot)
        {
            section = sectionRoot;
            showSwitch = toggle;
            countLabel = count;
            strengthSlider = strength;
            strengthRow = strengthRowRoot;
            showSwitch.ValueChanged += on => volumeView.ShowLabels = on;
            strengthSlider.onValueChanged.AddListener(value =>
            {
                if (volumeView.HasLabels) volumeView.LabelStrength = value;
            });
            Refresh();
        }

        private void OnEnable()
        {
            if (volumeView == null) return;
            volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            volumeView.LabelsChanged += Refresh;
            volumeView.LabelsDisplayChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (volumeView == null) return;
            volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
            volumeView.LabelsChanged -= Refresh;
            volumeView.LabelsDisplayChanged -= Refresh;
        }

        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => Refresh();

        // fuer aufrufer ausserhalb der ui
        public void SetVisibleExternal(bool on)
        {
            if (volumeView != null) volumeView.ShowLabels = on;
        }

        private void Refresh()
        {
            if (section == null || volumeView == null) return;
            bool hasLabels = volumeView.HasLabels;
            if (section.activeSelf != hasLabels) section.SetActive(hasLabels);
            if (!hasLabels) return;

            showSwitch.SetWithoutNotify(volumeView.ShowLabels);
            if (strengthRow != null) strengthRow.SetActive(volumeView.ShowLabels);
            strengthSlider.SetValueWithoutNotify(volumeView.LabelStrength);
            countLabel.text = $"{volumeView.LoadedVolume.LabelCount} cells";
        }
    }
}
