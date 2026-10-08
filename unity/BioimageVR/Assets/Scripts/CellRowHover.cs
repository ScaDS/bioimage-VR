using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BioimageVR
{
    // nur enter und exit, EventTrigger wuerde auch scroll und drag schlucken
    // dann scrollt die zellliste nicht mehr wenn der strahl auf einer zeile liegt
    public class CellRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Action Entered;
        public Action Exited;

        public void OnPointerEnter(PointerEventData eventData) => Entered?.Invoke();

        public void OnPointerExit(PointerEventData eventData) => Exited?.Invoke();
    }
}
