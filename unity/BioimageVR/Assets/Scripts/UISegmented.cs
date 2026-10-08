using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // mehrere knoepfe auf einer schiene, genau einer ist ausgewaehlt
    public class UISegmented : MonoBehaviour
    {
        [SerializeField] private List<Button> buttons = new List<Button>();
        [SerializeField] private int selected;

        public event Action<int> SelectionChanged;

        public int Selected
        {
            get => selected;
            set
            {
                if (selected == value) return;
                selected = value;
                Apply();
                SelectionChanged?.Invoke(selected);
            }
        }

        public void SetWithoutNotify(int index)
        {
            selected = index;
            Apply();
        }

        public void Init(List<Button> segmentButtons)
        {
            buttons = segmentButtons;
            Wire();
            Apply();
        }

        // awake laeuft beim AddComponent schon vor Init, deshalb auch aus Init heraus
        private bool wired;

        private void Wire()
        {
            if (wired || buttons.Count == 0) return;
            for (int i = 0; i < buttons.Count; i++)
            {
                int index = i;
                buttons[i].onClick.AddListener(() => Selected = index);
            }
            wired = true;
        }

        private void Awake()
        {
            Wire();
            Apply();
        }

        private void Apply()
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i] == null) continue;
                UIKit.SetStyle(buttons[i], i == selected ? UIKit.ButtonStyle.Secondary : UIKit.ButtonStyle.Borderless);
                var tint = buttons[i].GetComponent<ContentTint>();
                if (tint != null) tint.Enabled = i == selected ? UITheme.TextPrimary : UITheme.TextSecondary;
            }
        }
    }
}
