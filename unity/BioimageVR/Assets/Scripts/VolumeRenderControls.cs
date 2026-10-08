using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // laufzeit-regler fuer density, cut range max, render modus, shading und kanaele
    // ergaenzt VolumeContrastControl (threshold), anzeige kommt vom SidePanelController
    // display tab, dieselben External methoden nutzt der VLMToolDispatcher
    public class VolumeRenderControls : MonoBehaviour
    {
        // reihenfolge wie im shader, _RenderMode 0 1 2
        public enum Mode { Volume, Mip, Surface }

        [SerializeField] private VolumeView volumeView;

        private Slider densitySlider;
        private Slider cutRangeMaxSlider;
        private UISegmented renderMode;
        private UISwitch shading;
        private readonly List<UISwitch> channelSwitches = new List<UISwitch>();

        private static readonly int DensityId = Shader.PropertyToID("_Density");
        private static readonly int ThresholdMaxId = Shader.PropertyToID("_ThresholdMax");
        private static readonly int RenderModeId = Shader.PropertyToID("_RenderMode");
        private static readonly int EnableShadingId = Shader.PropertyToID("_EnableShading");
        private static readonly int[] ChannelIds =
        {
            Shader.PropertyToID("_Channel0On"),
            Shader.PropertyToID("_Channel1On"),
            Shader.PropertyToID("_Channel2On"),
        };

        private Renderer volumeRenderer;

        public void Bind(Slider density, Slider cutRangeMax, UISegmented mode, UISwitch shadingSwitch, IList<UISwitch> channels)
        {
            densitySlider = density;
            cutRangeMaxSlider = cutRangeMax;
            renderMode = mode;
            shading = shadingSwitch;
            channelSwitches.Clear();
            channelSwitches.AddRange(channels);

            densitySlider.onValueChanged.AddListener(v => SetFloat(DensityId, v));
            cutRangeMaxSlider.onValueChanged.AddListener(v => SetFloat(ThresholdMaxId, v));
            renderMode.SelectionChanged += index => SetFloat(RenderModeId, index);
            shading.ValueChanged += on => SetFloat(EnableShadingId, on ? 1f : 0f);
            for (int i = 0; i < channelSwitches.Count && i < ChannelIds.Length; i++)
            {
                int id = ChannelIds[i];
                channelSwitches[i].ValueChanged += on => SetFloat(id, on ? 1f : 0f);
            }
            SyncFromMaterial();
        }

        private void OnEnable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            SyncFromMaterial();
        }

        private void OnDisable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        // VolumeView.LoadVolume setzt alles auf die defaults zurueck, anzeige mitziehen
        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume) => SyncFromMaterial();

        private Material Mat
        {
            get
            {
                if (volumeRenderer == null && volumeView != null) volumeRenderer = volumeView.GetComponent<Renderer>();
                return volumeRenderer != null ? volumeRenderer.material : null;
            }
        }

        private void SetFloat(int id, float value)
        {
            Material mat = Mat;
            if (mat != null) mat.SetFloat(id, value);
        }

        private void SyncFromMaterial()
        {
            Material mat = Mat;
            if (mat == null || densitySlider == null) return;
            densitySlider.SetValueWithoutNotify(mat.GetFloat(DensityId));
            cutRangeMaxSlider.SetValueWithoutNotify(mat.GetFloat(ThresholdMaxId));
            renderMode.SetWithoutNotify((int)CurrentMode);
            shading.SetWithoutNotify(mat.GetFloat(EnableShadingId) > 0.5f);
            for (int i = 0; i < channelSwitches.Count && i < ChannelIds.Length; i++)
                channelSwitches[i].SetWithoutNotify(mat.GetFloat(ChannelIds[i]) > 0.5f);
        }

        // fuer aufrufer ausserhalb der ui, z.b. VLMToolDispatcher und die View werkzeugleiste,
        // setzt material UND anzeige, damit die regler im panel sichtbar mitwandern
        public void SetDensityExternal(float value)
        {
            SetFloat(DensityId, value);
            if (densitySlider != null) densitySlider.SetValueWithoutNotify(value);
        }

        public void SetCutRangeMaxExternal(float value)
        {
            value = Mathf.Clamp01(value);
            SetFloat(ThresholdMaxId, value);
            if (cutRangeMaxSlider != null) cutRangeMaxSlider.SetValueWithoutNotify(value);
        }

        public Mode CurrentMode
        {
            get
            {
                Material mat = Mat;
                return mat != null ? (Mode)Mathf.Clamp(Mathf.RoundToInt(mat.GetFloat(RenderModeId)), 0, 2) : Mode.Volume;
            }
        }

        public void SetRenderModeExternal(Mode mode)
        {
            SetFloat(RenderModeId, (int)mode);
            if (renderMode != null) renderMode.SetWithoutNotify((int)mode);
        }

        public void SetShadingExternal(bool on)
        {
            SetFloat(EnableShadingId, on ? 1f : 0f);
            if (shading != null) shading.SetWithoutNotify(on);
        }

        // index 0, 1 oder 2, unbekannter index wird ignoriert
        public void SetChannelExternal(int index, bool on)
        {
            if (index < 0 || index >= ChannelIds.Length) return;
            SetFloat(ChannelIds[index], on ? 1f : 0f);
            if (index < channelSwitches.Count) channelSwitches[index].SetWithoutNotify(on);
        }
    }
}
