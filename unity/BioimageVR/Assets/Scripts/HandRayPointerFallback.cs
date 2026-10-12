using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;

namespace BioimageVR
{
    // bindung je nach echtem controller oder xr interaction simulator umschalten
    [RequireComponent(typeof(TrackedPoseDriver))]
    public class HandRayPointerFallback : MonoBehaviour
    {
        public string HandTag;

        private TrackedPoseDriver _pose;
        private InputAction _pointerPositionAction;
        private InputAction _pointerRotationAction;
        private InputAction _devicePositionAction;
        private InputAction _deviceRotationAction;
        private GameObject _simulator;
        private bool _initialized;
        private bool _usingFallback;

        private void Awake()
        {
            _pose = GetComponent<TrackedPoseDriver>();

            _pointerPositionAction = new InputAction(type: InputActionType.Value,
                binding: $"<XRController>{{{HandTag}}}/pointerPosition", expectedControlType: "Vector3");
            _pointerRotationAction = new InputAction(type: InputActionType.Value,
                binding: $"<XRController>{{{HandTag}}}/pointerRotation", expectedControlType: "Quaternion");
            _devicePositionAction = new InputAction(type: InputActionType.Value,
                binding: $"<XRController>{{{HandTag}}}/devicePosition", expectedControlType: "Vector3");
            _deviceRotationAction = new InputAction(type: InputActionType.Value,
                binding: $"<XRController>{{{HandTag}}}/deviceRotation", expectedControlType: "Quaternion");
        }

        private void Update()
        {
            // ueber die root objekte suchen, GameObject.Find uebergeht inaktive
            if (_simulator == null)
            {
                foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                {
                    if (root.name != "XR Interaction Simulator") continue;
                    _simulator = root;
                    break;
                }
            }

            bool useFallback = _simulator != null && _simulator.activeInHierarchy;
            if (_initialized && useFallback == _usingFallback) return;

            _initialized = true;
            _usingFallback = useFallback;
            _pose.positionInput = new InputActionProperty(useFallback ? _devicePositionAction : _pointerPositionAction);
            _pose.rotationInput = new InputActionProperty(useFallback ? _deviceRotationAction : _pointerRotationAction);
        }
    }
}
