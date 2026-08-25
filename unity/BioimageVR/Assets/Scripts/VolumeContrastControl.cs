using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // laufzeit-regler fuer den raymarch schwellwert, damit man in vr ohne editor-zugriff
    // nachjustieren kann wenn ein volumen zu blass/milchig aussieht
    public class VolumeContrastControl : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private Slider thresholdSlider;

        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        private Renderer volumeRenderer;

        private void Awake()
        {
            if (thresholdSlider != null) thresholdSlider.onValueChanged.AddListener(SetThreshold);
        }

        private void OnEnable()
        {
            if (volumeView == null || thresholdSlider == null) return;
            volumeRenderer = volumeView.GetComponent<Renderer>();
            volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            // slider startet auf dem materialwert (nicht auf slider default), sonst
            // ueberschreibt das erste OnEnable den im shader gesetzten threshold
            SyncSliderFromMaterial();
        }

        private void OnDisable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        // VolumeView.LoadVolume setzt den threshold bei jedem (auch erneuten) laden auf
        // den default zurueck - slider muss mitziehen, sonst zeigt er noch die feinjustierung
        // vom vorherigen bild
        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => SyncSliderFromMaterial();

        private void SyncSliderFromMaterial()
        {
            if (volumeRenderer != null)
                thresholdSlider.SetValueWithoutNotify(volumeRenderer.material.GetFloat(ThresholdId));
        }

        private void SetThreshold(float value)
        {
            if (volumeRenderer == null && volumeView != null) volumeRenderer = volumeView.GetComponent<Renderer>();
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(ThresholdId, value);
        }

        // fuer aufrufer ausserhalb des sliders (z.b. VLMToolDispatcher) - setzt material
        // UND slider-position, damit der regler sichtbar mitwandert statt nur unsichtbar
        // im hintergrund den wert zu aendern
        public void SetThresholdExternal(float value)
        {
            value = Mathf.Clamp01(value);
            SetThreshold(value);
            if (thresholdSlider != null) thresholdSlider.SetValueWithoutNotify(value);
        }
    }
}
