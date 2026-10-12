using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // regler fuer den raymarch schwellwert im display tab
    public class VolumeContrastControl : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;

        private Slider thresholdSlider;
        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        private Renderer volumeRenderer;

        public void Bind(Slider slider)
        {
            thresholdSlider = slider;
            thresholdSlider.onValueChanged.AddListener(SetThreshold);
            SyncSliderFromMaterial();
        }

        private void OnEnable()
        {
            if (volumeView == null) return;
            volumeRenderer = volumeView.GetComponent<Renderer>();
            volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            SyncSliderFromMaterial();
        }

        private void OnDisable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        // LoadVolume setzt den threshold zurueck, regler zieht mit
        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => SyncSliderFromMaterial();

        private void SyncSliderFromMaterial()
        {
            if (volumeRenderer == null && volumeView != null) volumeRenderer = volumeView.GetComponent<Renderer>();
            if (volumeRenderer != null && thresholdSlider != null)
                thresholdSlider.SetValueWithoutNotify(volumeRenderer.material.GetFloat(ThresholdId));
        }

        private void SetThreshold(float value)
        {
            if (volumeRenderer == null && volumeView != null) volumeRenderer = volumeView.GetComponent<Renderer>();
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(ThresholdId, value);
        }

        public float CurrentThreshold
        {
            get
            {
                if (volumeRenderer == null && volumeView != null) volumeRenderer = volumeView.GetComponent<Renderer>();
                return volumeRenderer != null ? volumeRenderer.material.GetFloat(ThresholdId) : 0f;
            }
        }

        // fuer aufrufer ausserhalb des reglers, z.b. VLMToolDispatcher
        public void SetThresholdExternal(float value)
        {
            value = Mathf.Clamp01(value);
            SetThreshold(value);
            if (thresholdSlider != null) thresholdSlider.SetValueWithoutNotify(value);
        }
    }
}
