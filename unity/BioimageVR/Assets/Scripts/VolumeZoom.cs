using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BioimageVR
{
    // zoomt durch skalieren, mausrad am desktop, rechter thumbstick am controller
    // wartet auf volumeview load event, sonst ist basescale noch falsch
    // pausiert wenn der rechte strahl gerade aufs panel zeigt, dann scrollt der stick da
    // im fly modus pausiert es ganz, dort skaliert VolumeFly ueber ScaleAround
    [RequireComponent(typeof(VolumeView))]
    public class VolumeZoom : MonoBehaviour
    {
        [SerializeField] private float zoomSpeed = 0.15f;
        [SerializeField] private float controllerZoomSpeed = 3f;
        [SerializeField] private float controllerDeadzone = 0.15f;
        [SerializeField] private float minScaleMultiplier = 0.2f;
        [SerializeField] private float maxScaleMultiplier = 20f;
        // zum reinfliegen deutlich groesser, ein nasengang ist nur ein paar voxel breit
        // 06.10.: 100 reichte an der engsten stelle im mrt nicht, 1.5 mm auf 0.7 m braucht ca. 120
        [SerializeField] private float flyMaxScaleMultiplier = 300f;
        [SerializeField] private XRRayInteractor rightHandRay;

        private VolumeView volumeView;
        private VolumeFly fly;
        private InputAction controllerZoomAction;
        private Vector3 baseScale;
        private Vector3 basePosition;
        private float scaleMultiplier = 1f;
        private bool ready;

        private void Awake()
        {
            volumeView = GetComponent<VolumeView>();
            basePosition = transform.position;
            // alte szenen haben noch kein VolumeFly, dann hier nachruesten
            fly = GetComponent<VolumeFly>();
            if (fly == null) fly = gameObject.AddComponent<VolumeFly>();
            controllerZoomAction = new InputAction(
                type: InputActionType.Value,
                binding: "<XRController>{RightHand}/primary2DAxis",
                expectedControlType: "Vector2");
        }

        private void OnEnable()
        {
            controllerZoomAction.Enable();

            if (volumeView.LoadedVolume != null)
                CaptureBaseScale();
            else
                volumeView.OnVolumeLoaded += HandleVolumeLoaded;
        }

        private void OnDisable()
        {
            volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
            controllerZoomAction.Disable();
        }

        private void OnDestroy()
        {
            controllerZoomAction?.Dispose();
        }

        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume)
        {
            CaptureBaseScale();
        }

        public XRRayInteractor RightHandRay => rightHandRay;

        // zoom und ort zurueck wie nach dem laden, View werkzeug
        public void ResetView()
        {
            if (!ready) return;
            scaleMultiplier = 1f;
            transform.localScale = baseScale;
            transform.position = basePosition;
        }

        // skalieren um einen festen punkt, der punkt bleibt an seiner stelle im volumen
        public void ScaleAround(Vector3 pivot, float factor)
        {
            if (!ready) return;
            float next = Mathf.Clamp(scaleMultiplier * factor, minScaleMultiplier, flyMaxScaleMultiplier);
            float applied = next / scaleMultiplier;
            transform.position = pivot + (transform.position - pivot) * applied;
            scaleMultiplier = next;
            transform.localScale = baseScale * scaleMultiplier;
        }

        private void CaptureBaseScale()
        {
            baseScale = transform.localScale;
            scaleMultiplier = 1f;
            ready = true;
        }

        private void Update()
        {
            if (!ready || (fly != null && fly.Active)) return;

            bool pointingAtPanel = rightHandRay != null && rightHandRay.TryGetCurrentUIRaycastResult(out _);

            float zoomDelta = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(zoomDelta, 0f) && !pointingAtPanel)
            {
                float stickY = controllerZoomAction.ReadValue<Vector2>().y;
                if (Mathf.Abs(stickY) > controllerDeadzone)
                    zoomDelta = stickY * Time.deltaTime * controllerZoomSpeed;
            }

            if (Mathf.Approximately(zoomDelta, 0f)) return;

            // nach dem fliegen darf es groesser als das normale maximum sein, nur nicht weiter wachsen
            float upper = Mathf.Max(maxScaleMultiplier, scaleMultiplier);
            scaleMultiplier = Mathf.Clamp(scaleMultiplier * (1f + zoomDelta * zoomSpeed), minScaleMultiplier, upper);
            transform.localScale = baseScale * scaleMultiplier;
        }
    }
}
