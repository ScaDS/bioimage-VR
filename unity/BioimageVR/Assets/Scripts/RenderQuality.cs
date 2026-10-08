using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace BioimageVR
{
    // renderaufloesung, schaerfung und foveated rendering, umschaltbar im display tab
    // metas dynamische aufloesung darf nur zwischen min und max schwanken, min nie unter 1.0
    // 30.09.: vorher bis 0.7 erlaubt, beim segmentierungsbild sprang es auf 0.8 und alles
    // wurde koernig inkl. schrift. last faengt jetzt VolumeStepGovernor im volumen ab
    // metas schaerfung (quality) laeuft im compositor und ist fast umsonst, deshalb immer an
    public class RenderQuality : MonoBehaviour
    {
        public enum Level { Standard, Sharp }

        private const float StandardMin = 1.0f;
        private const float StandardMax = 1.3f;
        private const float SharpMin = 1.2f;
        private const float SharpMax = 1.6f;
        // 0 aus bis 1 stark, rand des blickfelds mit weniger pixeln, mitte bleibt voll
        private const float FoveationLevel = 1f;
        private const string PrefKey = "BioimageVR.RenderQuality";

        public Level Current { get; private set; } = Level.Standard;

        private void Start()
        {
            Level saved = Level.Standard;
            try
            {
                saved = (Level)PlayerPrefs.GetInt(PrefKey, 0);
            }
            catch (System.Exception)
            {
                // ohne gespeicherten wert bleibt Standard
            }
            Apply(saved);
            ApplySharpening();
            ApplyFoveation();
        }

        public void Apply(Level level)
        {
            Current = level;
            float min = level == Level.Sharp ? SharpMin : StandardMin;
            float max = level == Level.Sharp ? SharpMax : StandardMax;
#if UNITY_ANDROID && !UNITY_EDITOR
            OVRManager ovr = OVRManager.instance;
            if (ovr != null)
            {
                ovr.minDynamicResolutionScale = min;
                ovr.maxDynamicResolutionScale = max;
                ovr.quest2MinDynamicResolutionScale = ovr.quest3MinDynamicResolutionScale = min;
                ovr.quest2MaxDynamicResolutionScale = ovr.quest3MaxDynamicResolutionScale = max;
            }
#endif
            // augenbild in voller groesse anlegen, die dynamische aufloesung nutzt davon min bis max
            XRSettings.eyeTextureResolutionScale = max;
            PlayerPrefs.SetInt(PrefKey, (int)level);
        }

        private static void ApplySharpening()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (OVRManager.instance != null) OVRManager.instance.sharpenType = OVRPlugin.LayerSharpenType.Quality;
#endif
        }

        private static void ApplyFoveation()
        {
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (XRDisplaySubsystem display in displays)
            {
                display.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.GazeAllowed;
                display.foveatedRenderingLevel = FoveationLevel;
            }
        }
    }
}
