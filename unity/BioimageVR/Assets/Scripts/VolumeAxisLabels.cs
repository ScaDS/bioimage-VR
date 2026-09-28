using UnityEngine;

namespace BioimageVR
{
    // haengt an einem kamera-fixierten hud-anker (siehe SetupSidePanel.CreateAxisGizmo),
    // dadurch immer an derselben stelle im sichtfeld (unten links) statt am volumen
    // selbst zu haengen. die rotation wird trotzdem jeden frame vom volumen
    // uebernommen, damit der gizmo zeigt wie die daten gerade tatsaechlich orientiert
    // sind (22.09. wunsch: soll immer unten links sein, nicht am volumen mitwandern)
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
