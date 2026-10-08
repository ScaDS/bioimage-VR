using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BioimageVR
{
    // volumen frei drehen, linker thumbstick, sonst tut der gerade nichts
    // x achse gibt yaw, y achse gibt pitch, beides weltraum um den eigenen mittelpunkt
    // pausiert wenn der linke strahl gerade aufs panel zeigt, gleiches muster wie VolumeZoom
    // im fly modus ebenfalls, dann gehoert der stick VolumeFly
    [RequireComponent(typeof(VolumeView))]
    public class VolumeRotate : MonoBehaviour
    {
        [SerializeField] private float mouseRotateSpeed = 90f;
        [SerializeField] private float controllerRotateSpeed = 120f;
        [SerializeField] private float controllerDeadzone = 0.15f;
        [SerializeField] private XRRayInteractor leftHandRay;

        private InputAction controllerRotateAction;
        private VolumeFly fly;
        private Quaternion initialRotation;

        private void Awake()
        {
            initialRotation = transform.rotation;
            fly = GetComponent<VolumeFly>();
            controllerRotateAction = new InputAction(
                type: InputActionType.Value,
                binding: "<XRController>{LeftHand}/primary2DAxis",
                expectedControlType: "Vector2");
        }

        private void OnEnable()
        {
            controllerRotateAction.Enable();
        }

        private void OnDisable()
        {
            controllerRotateAction.Disable();
        }

        // drehung zurueck wie beim start, View werkzeug
        public void ResetView() => transform.rotation = initialRotation;

        public XRRayInteractor LeftHandRay => leftHandRay;

        private void OnDestroy()
        {
            controllerRotateAction?.Dispose();
        }

        private void Update()
        {
            if (fly == null) fly = GetComponent<VolumeFly>();
            if (fly != null && fly.Active) return;
            bool pointingAtPanel = leftHandRay != null && leftHandRay.TryGetCurrentUIRaycastResult(out _);
            if (pointingAtPanel) return;

            Vector2 stick = controllerRotateAction.ReadValue<Vector2>();
            float yawInput = 0f;
            float pitchInput = 0f;

            if (Mathf.Abs(stick.x) > controllerDeadzone || Mathf.Abs(stick.y) > controllerDeadzone)
            {
                yawInput = stick.x * controllerRotateSpeed * Time.deltaTime;
                pitchInput = -stick.y * controllerRotateSpeed * Time.deltaTime;
            }
            else if (Input.GetMouseButton(1))
            {
                // rechte maustaste halten + ziehen, nur am desktop zum testen ohne controller
                yawInput = Input.GetAxis("Mouse X") * mouseRotateSpeed * Time.deltaTime;
                pitchInput = -Input.GetAxis("Mouse Y") * mouseRotateSpeed * Time.deltaTime;
            }

            if (Mathf.Approximately(yawInput, 0f) && Mathf.Approximately(pitchInput, 0f)) return;

            transform.Rotate(Vector3.up, yawInput, Space.World);
            transform.Rotate(Vector3.right, pitchInput, Space.Self);
        }
    }
}
