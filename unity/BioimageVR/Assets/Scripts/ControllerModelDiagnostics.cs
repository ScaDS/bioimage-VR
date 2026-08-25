using UnityEngine;

namespace BioimageVR
{
    // temporaeres debug-log fuer die OVRRuntimeController-kette (OVRManager init,
    // controller verbunden?, render model pfade verfuegbar?) - braucht keine
    // szenen-verkabelung, haengt sich beim start selbst ein. wieder entfernen sobald
    // die echten controller-modelle bestaetigt funktionieren
    public static class ControllerModelDiagnosticsBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Start()
        {
            var go = new GameObject("ControllerModelDiagnostics");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<ControllerModelDiagnosticsRunner>();
        }
    }

    public class ControllerModelDiagnosticsRunner : MonoBehaviour
    {
        private float timer;

        private void Update()
        {
            timer += Time.deltaTime;
            if (timer < 2f) return;
            timer = 0f;

            bool ovrManagerInit = OVRManager.OVRManagerinitialized;
            bool pluginInit = OVRPlugin.initialized;
            bool leftConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
            bool rightConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            string[] paths = pluginInit ? OVRPlugin.GetRenderModelPaths() : new string[0];

            Debug.Log($"[Diag] OVRManagerInit={ovrManagerInit} OVRPluginInit={pluginInit} " +
                      $"LTouchConnected={leftConnected} RTouchConnected={rightConnected} " +
                      $"RenderModelPaths=[{string.Join(",", paths)}]");
        }
    }
}
