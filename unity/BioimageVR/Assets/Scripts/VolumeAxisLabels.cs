using UnityEngine;

namespace BioimageVR
{
    // achsen gizmo unten links im sichtfeld, rotation kommt jeden frame vom volumen
    public class VolumeAxisLabels : MonoBehaviour
    {
        [SerializeField] private Transform volumeTransform;

        private void LateUpdate()
        {
            if (volumeTransform == null) return;
            transform.rotation = volumeTransform.rotation;
        }
    }
}
