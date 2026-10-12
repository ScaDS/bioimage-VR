using UnityEngine;
using UnityEngine.EventSystems;

namespace BioimageVR
{
    // liegt auf dem bild der leinwand, gibt den controller strahl als maus an ScreenMirror weiter
    // zeigen bewegt die maus in napari, trigger druecken und loslassen ist die linke taste
    public class ScreenPointer : MonoBehaviour, IPointerMoveHandler, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        private ScreenMirror mirror;
        private RectTransform rect;
        private Vector2 last;

        public void Init(ScreenMirror screenMirror)
        {
            mirror = screenMirror;
            rect = (RectTransform)transform;
        }

        // treffpunkt des strahls als anteil des bilds, v von oben wie am bildschirm
        private Vector2 Uv(PointerEventData data)
        {
            RaycastResult hit = data.pointerCurrentRaycast;
            // beim ziehen ueber den rand hinaus gibt es keinen treffer, dann bleibt die maus wo sie war
            if (!hit.isValid) return last;
            Vector3 local = rect.InverseTransformPoint(hit.worldPosition);
            Rect r = rect.rect;
            last = new Vector2(Mathf.Clamp01((local.x - r.xMin) / r.width), Mathf.Clamp01(1f - (local.y - r.yMin) / r.height));
            return last;
        }

        public void OnPointerMove(PointerEventData data) => mirror.Pointer("move", Uv(data));

        public void OnDrag(PointerEventData data) => mirror.Pointer("move", Uv(data));

        public void OnPointerDown(PointerEventData data)
        {
            if (data.button == PointerEventData.InputButton.Left) mirror.Pointer("down", Uv(data));
        }

        public void OnPointerUp(PointerEventData data)
        {
            if (data.button == PointerEventData.InputButton.Left) mirror.Pointer("up", Uv(data));
        }
    }
}
