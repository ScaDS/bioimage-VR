using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;

namespace BioimageVR
{
    // pointerposition und pointerrotation gibt es nur bei echten openxr controllern
    // der xr interaction simulator hat nur deviceposition und devicerotation
    // schaltet den trackedposedriver auf die passende bindung um je nachdem ob
    // das XR Interaction Simulator objekt in der szene gerade aktiv ist
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
            // GameObject.Find uebergeht inaktive objekte, das simulator objekt ist aber
            // meistens inaktiv (headset test) - deshalb ueber die root objekte suchen
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
