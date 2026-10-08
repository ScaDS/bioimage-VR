using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BioimageVR
{
    // durchs volumen fliegen, z.b. in die nase und den hals runter
    // eigentlich bewegt sich das volumen um den nutzer, die kamera bleibt wo sie ist
    // rechter stick vor zurueck fliegt dahin wo der rechte controller zeigt, zur seite dreht
    // linker stick hoch runter macht das volumen groesser oder kleiner, drehpunkt ist der kopf
    // solange aktiv pausieren VolumeZoom und VolumeRotate, an und aus im View werkzeug
    // zweiter modus Orient zum zurechtlegen, sticks drehen das volumen statt zu fliegen
    // linker stick dreht und kippt, rechter stick rollt zur seite und aendert die groesse
    // drehpunkt ist der kopf solange man drin steckt, die stelle vor einem bleibt also vor einem
    // dritter modus Path faehrt einen vorberechneten weg ab, siehe FlyPath
    // rechter stick vor und zurueck entlang des wegs, zur seite drehen, linker stick groesse
    // am desktop W S und A D rechter stick, R F und Q E linker stick
    [RequireComponent(typeof(VolumeView))]
    public class VolumeFly : MonoBehaviour
    {
        [SerializeField] private XRRayInteractor leftHandRay;
        [SerializeField] private XRRayInteractor rightHandRay;
        // meter pro sekunde bei vollem stick, vergroessertes volumen fuehlt sich dadurch langsamer an
        [SerializeField] private float flySpeed = 0.35f;
        [SerializeField] private float turnSpeed = 60f;
        [SerializeField] private float scaleSpeed = 1.2f;
        [SerializeField] private float orientSpeed = 70f;
        [SerializeField] private float deadzone = 0.15f;

        private VolumeZoom zoom;
        private FlyPath path;
        private InputAction leftStick;
        private InputAction rightStick;

        public enum FlyMode { Move, Orient, Path }

        public bool Active { get; private set; }
        public FlyMode Mode { get; private set; }
        public event Action<bool> ActiveChanged;

        private void Awake()
        {
            zoom = GetComponent<VolumeZoom>();
            // alte szenen haben noch kein FlyPath, dann hier nachruesten
            path = GetComponent<FlyPath>();
            if (path == null) path = gameObject.AddComponent<FlyPath>();
            if (GetComponent<FlyMap>() == null) gameObject.AddComponent<FlyMap>();
            // weg zu ende und wieder draussen, dort frei weiterfliegen
            path.Exited += () => Mode = FlyMode.Move;
            if (rightHandRay == null && zoom != null) rightHandRay = zoom.RightHandRay;
            var rotate = GetComponent<VolumeRotate>();
            if (leftHandRay == null && rotate != null) leftHandRay = rotate.LeftHandRay;

            leftStick = new InputAction(type: InputActionType.Value,
                binding: "<XRController>{LeftHand}/primary2DAxis", expectedControlType: "Vector2");
            rightStick = new InputAction(type: InputActionType.Value,
                binding: "<XRController>{RightHand}/primary2DAxis", expectedControlType: "Vector2");
        }

        private void OnEnable()
        {
            leftStick.Enable();
            rightStick.Enable();
        }

        private void OnDisable()
        {
            leftStick.Disable();
            rightStick.Disable();
        }

        private void OnDestroy()
        {
            leftStick?.Dispose();
            rightStick?.Dispose();
        }

        public void SetActive(bool on)
        {
            if (on == Active) return;
            Active = on;
            if (Mode == FlyMode.Path) path.End();
            Mode = FlyMode.Move;
            ActiveChanged?.Invoke(on);
        }

        public FlyPath Path => path;

        // tempo stufe aus der leiste, Slow Normal Fast, gilt fuers fliegen und fuer die fahrt am weg
        public float SpeedFactor
        {
            get => speedFactor;
            set
            {
                speedFactor = value;
                path.SpeedFactor = value;
            }
        }
        private float speedFactor = 1f;

        // Path ohne weg zum bild bleibt bei Move
        public void SetMode(FlyMode mode)
        {
            if (mode == FlyMode.Path && !path.HasPath) mode = FlyMode.Move;
            if (mode == Mode) return;
            if (Mode == FlyMode.Path) path.End();
            Mode = mode;
            if (mode == FlyMode.Path && Camera.main != null) path.Begin(Camera.main, CameraInside);
        }

        // kamera steckt gerade im volumen
        public bool CameraInside
        {
            get
            {
                Camera cam = Camera.main;
                if (cam == null) return false;
                Vector3 local = transform.InverseTransformPoint(cam.transform.position);
                return Mathf.Abs(local.x) <= 0.5f && Mathf.Abs(local.y) <= 0.5f && Mathf.Abs(local.z) <= 0.5f;
            }
        }

        private void Update()
        {
            if (!Active) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 head = cam.transform.position;

            Vector2 right = Stick(rightStick, rightHandRay);
            Vector2 left = Stick(leftStick, leftHandRay);
            if (Input.GetKey(KeyCode.W)) right.y += 1f;
            if (Input.GetKey(KeyCode.S)) right.y -= 1f;
            if (Input.GetKey(KeyCode.D)) right.x += 1f;
            if (Input.GetKey(KeyCode.A)) right.x -= 1f;
            if (Input.GetKey(KeyCode.R)) left.y += 1f;
            if (Input.GetKey(KeyCode.F)) left.y -= 1f;
            if (Input.GetKey(KeyCode.E)) left.x += 1f;
            if (Input.GetKey(KeyCode.Q)) left.x -= 1f;

            float dt = Time.deltaTime;
            if (Mode == FlyMode.Orient)
            {
                Orient(cam, head, left, right, dt);
                return;
            }
            if (Mode == FlyMode.Path)
            {
                if (right.y != 0f) path.Ride(Curve(right.y) * flySpeed * speedFactor * dt, dt);
                if (right.x != 0f) path.Turn(head, -Curve(right.x) * turnSpeed * dt);
                // groesse haelt FlyPath selbst nach, der stick stellt nur die wunschbreite
                if (left.y != 0f) path.ChangeWidth(1f + Curve(left.y) * scaleSpeed * dt);
                return;
            }

            if (right.y != 0f)
                transform.position -= FlyDirection(cam) * (Curve(right.y) * flySpeed * speedFactor * dt);

            // welt dreht gegenlaeufig, fuehlt sich an wie selbst drehen
            if (right.x != 0f)
                transform.RotateAround(head, Vector3.up, -Curve(right.x) * turnSpeed * dt);

            if (left.y != 0f && zoom != null)
                zoom.ScaleAround(head, 1f + Curve(left.y) * scaleSpeed * dt);
        }

        // volumen zurechtlegen, achsen aus sicht des nutzers und waagerecht gehalten
        // vorzeichen wie bei VolumeRotate, fuehlt sich drinnen gleich an wie von aussen
        private void Orient(Camera cam, Vector3 head, Vector2 left, Vector2 right, float dt)
        {
            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = cam.transform.up;
            forward.y = 0f;
            forward.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, forward);

            // von aussen um die eigene mitte, sonst schwenkt das ganze volumen um den nutzer herum
            Vector3 pivot = CameraInside ? head : transform.position;

            if (left.x != 0f) transform.RotateAround(pivot, Vector3.up, Curve(left.x) * orientSpeed * dt);
            if (left.y != 0f) transform.RotateAround(pivot, side, -Curve(left.y) * orientSpeed * dt);
            if (right.x != 0f) transform.RotateAround(pivot, forward, -Curve(right.x) * orientSpeed * dt);
            if (right.y != 0f && zoom != null)
                zoom.ScaleAround(pivot, 1f + Curve(right.y) * scaleSpeed * dt);
        }

        private Vector3 FlyDirection(Camera cam)
        {
            if (rightHandRay == null) return cam.transform.forward;
            Transform origin = rightHandRay.rayOriginTransform != null ? rightHandRay.rayOriginTransform : rightHandRay.transform;
            return origin.forward;
        }

        // zeigt der strahl aufs panel, gehoert der stick dem panel zum scrollen
        private Vector2 Stick(InputAction action, XRRayInteractor ray)
        {
            if (ray != null && ray.TryGetCurrentUIRaycastResult(out _)) return Vector2.zero;
            Vector2 v = action.ReadValue<Vector2>();
            if (Mathf.Abs(v.x) < deadzone) v.x = 0f;
            if (Mathf.Abs(v.y) < deadzone) v.y = 0f;
            return v;
        }

        // quadratisch, kleine ausschlaege fuer feines steuern in engen gaengen
        private static float Curve(float v) => Mathf.Sign(v) * v * v;
    }
}
