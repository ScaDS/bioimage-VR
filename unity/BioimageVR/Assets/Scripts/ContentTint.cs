using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // unity faerbt von sich aus nur den hintergrund
    public class ContentTint : MonoBehaviour
    {
        public Color Enabled = Color.white;

        private Selectable selectable;
        private TMP_Text[] texts;
        private bool lastInteractable;
        private Color lastColor;

        private void Awake() => Collect();

        public void Collect()
        {
            selectable = GetComponent<Selectable>();
            texts = GetComponentsInChildren<TMP_Text>(true);
            lastColor = default;
        }

        private void LateUpdate()
        {
            if (selectable == null) return;
            bool interactable = selectable.IsInteractable();
            if (interactable == lastInteractable && lastColor == Enabled) return;
            lastInteractable = interactable;
            lastColor = Enabled;
            Color color = interactable ? Enabled : UITheme.TextDisabled;
            foreach (TMP_Text text in texts)
                if (text != null) text.color = color;
        }
    }
}
