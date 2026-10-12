using UnityEngine;

namespace BioimageVR
{
    // dreht sich jeden frame zur kamera, text bleibt lesbar
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
