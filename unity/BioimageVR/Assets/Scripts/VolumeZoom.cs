using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BioimageVR
{
    // zoomt durch skalieren, mausrad am desktop, rechter thumbstick am controller
    // wartet auf volumeview load event, sonst ist basescale noch falsch
    // pausiert wenn der rechte strahl gerade aufs panel zeigt, dann scrollt der stick da
    [RequireComponent(typeof(VolumeView))]
    public class VolumeZoom : MonoBehaviour
    {
        [SerializeField] private float zoomSpeed = 0.15f;
        [SerializeField] private float controllerZoomSpeed = 3f;
        [SerializeField] private float controllerDeadzone = 0.15f;
        [SerializeField] private float minScaleMultiplier = 0.2f;
        [SerializeField] private float maxScaleMultiplier = 20f;
        [SerializeField] private XRRayInteractor rightHandRay;

        private VolumeView volumeView;
        private InputAction controllerZoomAction;
        private Vector3 baseScale;
        private float scaleMultiplier = 1f;
        private bool ready;

        private void Awake()
        {
            volumeView = GetComponent<VolumeView>();
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

        private void CaptureBaseScale()
        {
            baseScale = transform.localScale;
            scaleMultiplier = 1f;
            ready = true;
        }

        private void Update()
        {
            if (!ready) return;

            bool pointingAtPanel = rightHandRay != null && rightHandRay.TryGetCurrentUIRaycastResult(out _);

            float zoomDelta = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(zoomDelta, 0f) && !pointingAtPanel)
            {
                float stickY = controllerZoomAction.ReadValue<Vector2>().y;
                if (Mathf.Abs(stickY) > controllerDeadzone)
                    zoomDelta = stickY * Time.deltaTime * controllerZoomSpeed;
            }

            if (Mathf.Approximately(zoomDelta, 0f)) return;

            scaleMultiplier = Mathf.Clamp(scaleMultiplier * (1f + zoomDelta * zoomSpeed), minScaleMultiplier, maxScaleMultiplier);
            transform.localScale = baseScale * scaleMultiplier;
        }
    }
}
