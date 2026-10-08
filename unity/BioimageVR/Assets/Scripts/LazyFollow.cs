using UnityEngine;

namespace BioimageVR
{
    // folgt dem kopf traege statt starr, fuer massstab und achsen
    // bleibt stehen solange man nur kurz zur seite schaut, holt erst ab einem winkel auf
    public class LazyFollow : MonoBehaviour
    {
        [SerializeField] private Vector3 offset = new Vector3(-0.28f, -0.34f, 0.9f);
        [SerializeField] private float deadZoneDegrees = 20f;
        [SerializeField] private float followSpeed = 3f;

        private Quaternion anchorYaw;
        private bool following;
        private bool placed;

        public void Init(Vector3 localOffset) => offset = localOffset;

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 flat = cam.transform.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f) return;
            Quaternion yaw = Quaternion.LookRotation(flat.normalized, Vector3.up);

            if (!placed)
            {
                anchorYaw = yaw;
                placed = true;
            }

            float angle = Quaternion.Angle(anchorYaw, yaw);
            if (angle > deadZoneDegrees) following = true;
            if (following)
            {
                anchorYaw = Quaternion.Slerp(anchorYaw, yaw, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
                if (Quaternion.Angle(anchorYaw, yaw) < 1f) following = false;
            }

            transform.position = cam.transform.position + anchorYaw * offset;
            transform.rotation = anchorYaw;
        }
    }
}
