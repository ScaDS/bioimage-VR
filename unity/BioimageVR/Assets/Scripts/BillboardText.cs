using UnityEngine;

namespace BioimageVR
{
    // dreht sich jeden frame zur kamera, damit text lesbar bleibt egal wie das
    // eltern-objekt (z.b. ein gedrehtes volumen) gerade orientiert ist
    public class BillboardText : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            transform.rotation = cam.transform.rotation;
        }
    }
}
