using UnityEngine;

namespace BioimageVR
{
    // haengt ein welt canvas neben das volumen, steckt die kamera drin schwebt es vor dem nutzer
    public class VolumeAnchor : MonoBehaviour
    {
        public enum Side { Left, Bottom, Right }

        [SerializeField] private Transform volume;
        [SerializeField] private Side side = Side.Left;
        [SerializeField] private float gap = 0.06f;
        [SerializeField] private float maxRadius = 0.45f;
        [SerializeField] private float minDistance = 0.55f;
        [SerializeField] private float maxDistance = 1.6f;
        [SerializeField] private float followSpeed = 6f;

        [SerializeField] private float insideRecenterDegrees = 40f;

        private Renderer volumeRenderer;
        private bool placed;
        private bool wasInside;
        private Vector3 insideForward;

        public void Init(Transform volumeTransform, Side anchorSide, float anchorGap)
        {
            volume = volumeTransform;
            side = anchorSide;
            gap = anchorGap;
        }

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (volume == null || cam == null) return;
            if (volumeRenderer == null) volumeRenderer = volume.GetComponent<Renderer>();

            Vector3 camPos = cam.transform.position;
            Vector3 center = volumeRenderer != null ? volumeRenderer.bounds.center : volume.position;
            float radius = volumeRenderer != null ? volumeRenderer.bounds.extents.magnitude * 0.75f : 0.3f;
            radius = Mathf.Min(radius, maxRadius);

            // anker in greifweite halten, auch wenn das volumen sehr weit weg oder um den kopf ist
            Vector3 toCenter = center - camPos;
            float distance = toCenter.magnitude;
            if (distance < 1e-3f) toCenter = cam.transform.forward;

            // im volumen zeigt die mitte irgendwohin, oft hinter den nutzer
            Vector3 local = volume.InverseTransformPoint(camPos);
            bool inside = Mathf.Abs(local.x) <= 0.5f && Mathf.Abs(local.y) <= 0.5f && Mathf.Abs(local.z) <= 0.5f;
            if (inside)
            {
                Vector3 look = cam.transform.forward;
                look.y = 0f;
                if (look.sqrMagnitude > 1e-4f)
                {
                    look.Normalize();
                    if (!wasInside || Vector3.Angle(insideForward, look) > insideRecenterDegrees) insideForward = look;
                }
                toCenter = insideForward;
                distance = minDistance;
                radius = 0.25f;
            }
            wasInside = inside;
            float clamped = Mathf.Clamp(distance, minDistance, maxDistance);
            center = camPos + toCenter.normalized * clamped;
            if (distance < radius + minDistance * 0.5f) radius = Mathf.Min(radius, 0.25f);

            Vector3 forward = center - camPos;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = cam.transform.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);

            Vector3 target = side switch
            {
                Side.Left => center - right * (radius + gap),
                Side.Right => center + right * (radius + gap),
                _ => center - Vector3.up * (radius * 0.8f + gap),
            };
            Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);

            if (!placed)
            {
                transform.SetPositionAndRotation(target, rotation);
                placed = true;
                return;
            }
            float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, target, t),
                Quaternion.Slerp(transform.rotation, rotation, t));
        }
    }
}
