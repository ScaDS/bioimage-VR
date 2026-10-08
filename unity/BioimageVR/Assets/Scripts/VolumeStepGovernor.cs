using UnityEngine;
using UnityEngine.XR;

namespace BioimageVR
{
    // haelt die bildrate, indem der raymarch groeber abtastet statt die aufloesung zu senken
    // aufloesung bleibt dadurch scharf, auch die schrift, siehe RenderQuality fuer die untergrenze
    // misst die frame zeit, zu langsam heisst groebere schritte, laenger ruhig wieder feiner
    [RequireComponent(typeof(MeshRenderer))]
    public class VolumeStepGovernor : MonoBehaviour
    {
        // q 0 feinste stufe, q 1 groebste
        [SerializeField] private float fineStepScale = 0.04f;
        [SerializeField] private float coarseStepScale = 0.14f;
        [SerializeField] private float fineMinStepVoxels = 0.5f;
        [SerializeField] private float coarseMinStepVoxels = 1.5f;
        // wie lange es ruhig laufen muss, bevor es wieder feiner wird
        [SerializeField] private float calmSeconds = 2f;

        private static readonly int StepScaleId = Shader.PropertyToID("_StepScale");
        private static readonly int MinStepVoxelsId = Shader.PropertyToID("_MinStepVoxels");

        private MeshRenderer meshRenderer;
        private float quality;
        private float smoothedFrame;
        private float calm;
        private float applied = -1f;

        // 0 fein bis 1 grob, fuer anzeige und tests
        public float Coarseness => quality;

        private void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
        }

        private void Update()
        {
            float refresh = XRDevice.refreshRate > 1f ? XRDevice.refreshRate : 72f;
            float target = 1f / refresh;
            float frame = Time.unscaledDeltaTime;
            // ladehaenger nicht als dauerlast werten
            if (frame > 0.25f) return;
            smoothedFrame = smoothedFrame <= 0f ? frame : Mathf.Lerp(smoothedFrame, frame, 0.1f);

            if (smoothedFrame > target * 1.1f)
            {
                quality = Mathf.Min(quality + 1.5f * Time.unscaledDeltaTime, 1f);
                calm = 0f;
            }
            else if (smoothedFrame < target * 1.03f)
            {
                calm += Time.unscaledDeltaTime;
                if (calm > calmSeconds) quality = Mathf.Max(quality - 0.15f * Time.unscaledDeltaTime, 0f);
            }

            if (Mathf.Abs(quality - applied) < 0.01f) return;
            applied = quality;
            Material mat = meshRenderer.material;
            mat.SetFloat(StepScaleId, Mathf.Lerp(fineStepScale, coarseStepScale, quality));
            mat.SetFloat(MinStepVoxelsId, Mathf.Lerp(fineMinStepVoxels, coarseMinStepVoxels, quality));
        }
    }
}
