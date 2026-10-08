using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // laufzeit-regler fuer den raymarch schwellwert, damit man in vr ohne editor-zugriff
    // nachjustieren kann wenn ein volumen zu blass/milchig aussieht
    // regler kommt vom SidePanelController, display tab
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

        // VolumeView.LoadVolume setzt den threshold bei jedem laden auf den default zurueck,
        // regler muss mitziehen, sonst zeigt er noch die feinjustierung vom vorherigen bild
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

        // fuer aufrufer ausserhalb des reglers, z.b. VLMToolDispatcher
        public void SetThresholdExternal(float value)
        {
            value = Mathf.Clamp01(value);
            SetThreshold(value);
            if (thresholdSlider != null) thresholdSlider.SetValueWithoutNotify(value);
        }
    }
}
