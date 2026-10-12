using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BioimageVR
{
    // nur enter und exit, EventTrigger wuerde scroll und drag schlucken
    public class CellRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Action Entered;
        public Action Exited;

        public void OnPointerEnter(PointerEventData eventData) => Entered?.Invoke();

        public void OnPointerExit(PointerEventData eventData) => Exited?.Invoke();
    }
}
