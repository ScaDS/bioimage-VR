using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // an aus schalter, blau wenn an, knopf wandert mit
    public class UISwitch : MonoBehaviour
    {
        [SerializeField] private Image track;
        [SerializeField] private RectTransform knob;
        [SerializeField] private Button button;
        [SerializeField] private bool isOn;

        public event Action<bool> ValueChanged;

        public bool IsOn
        {
            get => isOn;
            set
            {
                if (isOn == value) return;
                isOn = value;
                Apply();
                ValueChanged?.Invoke(isOn);
            }
        }

        public void SetWithoutNotify(bool value)
        {
            isOn = value;
            Apply();
        }

        public void Init(Image trackImage, RectTransform knobRect, Button trackButton, bool value)
        {
            track = trackImage;
            knob = knobRect;
            button = trackButton;
            isOn = value;
            Wire();
            Apply();
        }

        // awake laeuft beim AddComponent schon vor Init, deshalb auch aus Init heraus
        private bool wired;

        private void Wire()
        {
            if (wired || button == null) return;
            button.onClick.AddListener(() => IsOn = !IsOn);
            wired = true;
        }

        private void Awake()
        {
            Wire();
            Apply();
        }

        private void Apply()
        {
            if (track != null) track.color = isOn ? UITheme.Accent : UITheme.Surface;
            if (knob != null) knob.anchoredPosition = new Vector2(isOn ? 51f : 19f, 0f);
        }
    }
}
