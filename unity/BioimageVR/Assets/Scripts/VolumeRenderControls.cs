using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // laufzeit-regler fuer density/cut-range-max/render-modus/shading/kanal-an-aus,
    // ergaenzt VolumeContrastControl (threshold) um die uebrigen shader-parameter -
    // eigener "Einstellungen" tab im seitenpanel
    public class VolumeRenderControls : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private Slider densitySlider;
        [SerializeField] private Slider cutRangeMaxSlider;
        [SerializeField] private Button renderModeButton;
        [SerializeField] private Text renderModeLabel;
        [SerializeField] private Button shadingButton;
        [SerializeField] private Text shadingLabel;
        [SerializeField] private Button channel0Button;
        [SerializeField] private Text channel0Label;
        [SerializeField] private Button channel1Button;
        [SerializeField] private Text channel1Label;
        [SerializeField] private Button channel2Button;
        [SerializeField] private Text channel2Label;

        private static readonly int DensityId = Shader.PropertyToID("_Density");
        private static readonly int ThresholdMaxId = Shader.PropertyToID("_ThresholdMax");
        private static readonly int RenderModeId = Shader.PropertyToID("_RenderMode");
        private static readonly int EnableShadingId = Shader.PropertyToID("_EnableShading");
        private static readonly int Channel0OnId = Shader.PropertyToID("_Channel0On");
        private static readonly int Channel1OnId = Shader.PropertyToID("_Channel1On");
        private static readonly int Channel2OnId = Shader.PropertyToID("_Channel2On");

        private Renderer volumeRenderer;
        private bool isMip;
        private bool shadingOn;
        private bool channel0On = true;
        private bool channel1On = true;
        private bool channel2On = true;

        private void Awake()
        {
            if (densitySlider != null) densitySlider.onValueChanged.AddListener(SetDensity);
            if (cutRangeMaxSlider != null) cutRangeMaxSlider.onValueChanged.AddListener(SetCutRangeMax);
            if (renderModeButton != null) renderModeButton.onClick.AddListener(ToggleRenderMode);
            if (shadingButton != null) shadingButton.onClick.AddListener(ToggleShading);
            if (channel0Button != null) channel0Button.onClick.AddListener(() => ToggleChannel(0));
            if (channel1Button != null) channel1Button.onClick.AddListener(() => ToggleChannel(1));
            if (channel2Button != null) channel2Button.onClick.AddListener(() => ToggleChannel(2));
        }

        private void OnEnable()
        {
            EnsureRenderer();
            if (volumeView != null) volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            // regler starten auf dem tatsaechlichen materialwert, nicht auf einem
            // slider-default - sonst ueberschreibt das erste OnEnable den shader-wert
            SyncFromMaterial();
        }

        private void OnDisable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        // VolumeView.LoadVolume setzt kanal-flags UND density/threshold-max/render-modus/
        // shading bei jedem (auch erneuten) laden auf die defaults zurueck - lokalen
        // ui-zustand synchron halten, sonst zeigen regler noch die feinjustierung vom
        // vorherigen bild statt der werte die gerade tatsaechlich gerendert werden
        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => SyncFromMaterial();

        private void SyncFromMaterial()
        {
            if (volumeRenderer == null) return;
            Material mat = volumeRenderer.material;
            if (densitySlider != null) densitySlider.SetValueWithoutNotify(mat.GetFloat(DensityId));
            if (cutRangeMaxSlider != null) cutRangeMaxSlider.SetValueWithoutNotify(mat.GetFloat(ThresholdMaxId));

            isMip = mat.GetFloat(RenderModeId) > 0.5f;
            shadingOn = mat.GetFloat(EnableShadingId) > 0.5f;
            channel0On = mat.GetFloat(Channel0OnId) > 0.5f;
            channel1On = mat.GetFloat(Channel1OnId) > 0.5f;
            channel2On = mat.GetFloat(Channel2OnId) > 0.5f;
            UpdateLabels();
        }

        private void SetDensity(float value)
        {
            EnsureRenderer();
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(DensityId, value);
        }

        private void SetCutRangeMax(float value)
        {
            EnsureRenderer();
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(ThresholdMaxId, value);
        }

        private void ToggleRenderMode()
        {
            EnsureRenderer();
            isMip = !isMip;
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(RenderModeId, isMip ? 1f : 0f);
            UpdateLabels();
        }

        private void ToggleShading()
        {
            EnsureRenderer();
            shadingOn = !shadingOn;
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(EnableShadingId, shadingOn ? 1f : 0f);
            UpdateLabels();
        }

        private void ToggleChannel(int index) => SetChannelExternal(index, !GetChannelOn(index));

        private bool GetChannelOn(int index) => index switch { 0 => channel0On, 1 => channel1On, _ => channel2On };

        // fuer aufrufer ausserhalb der ui (z.b. VLMToolDispatcher) - setzt material UND
        // slider/label, damit die regler im panel sichtbar mitwandern
        public void SetDensityExternal(float value)
        {
            SetDensity(value);
            if (densitySlider != null) densitySlider.SetValueWithoutNotify(value);
        }

        public void SetCutRangeMaxExternal(float value)
        {
            value = Mathf.Clamp01(value);
            SetCutRangeMax(value);
            if (cutRangeMaxSlider != null) cutRangeMaxSlider.SetValueWithoutNotify(value);
        }

        public void SetRenderModeExternal(bool mip)
        {
            EnsureRenderer();
            isMip = mip;
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(RenderModeId, isMip ? 1f : 0f);
            UpdateLabels();
        }

        public void SetShadingExternal(bool on)
        {
            EnsureRenderer();
            shadingOn = on;
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(EnableShadingId, shadingOn ? 1f : 0f);
            UpdateLabels();
        }

        // index 0/1/2 - unbekannter index wird ignoriert (kein 4. kanal vorgesehen)
        public void SetChannelExternal(int index, bool on)
        {
            EnsureRenderer();
            int propId;
            switch (index)
            {
                case 0: channel0On = on; propId = Channel0OnId; break;
                case 1: channel1On = on; propId = Channel1OnId; break;
                case 2: channel2On = on; propId = Channel2OnId; break;
                default: return;
            }
            if (volumeRenderer != null) volumeRenderer.material.SetFloat(propId, on ? 1f : 0f);
            UpdateLabels();
        }

        private void UpdateLabels()
        {
            if (renderModeLabel != null) renderModeLabel.text = isMip ? "MIP" : "Translucent";
            if (shadingLabel != null) shadingLabel.text = shadingOn ? "Shading: An" : "Shading: Aus";
            if (channel0Label != null) channel0Label.text = channel0On ? "Kanal 1: An" : "Kanal 1: Aus";
            if (channel1Label != null) channel1Label.text = channel1On ? "Kanal 2: An" : "Kanal 2: Aus";
            if (channel2Label != null) channel2Label.text = channel2On ? "Kanal 3: An" : "Kanal 3: Aus";
        }

        private void EnsureRenderer()
        {
            if (volumeRenderer == null && volumeView != null) volumeRenderer = volumeView.GetComponent<Renderer>();
        }
    }
}
