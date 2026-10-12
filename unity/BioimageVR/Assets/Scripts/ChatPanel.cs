using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // frage antwort verlauf, scrollt automatisch runter
    public class ChatPanel : MonoBehaviour
    {
        [SerializeField] private int maxMessages = 40;

        private TMP_Text contentText;
        private ScrollRect scrollRect;
        private readonly List<string> messages = new List<string>();

        public void Bind(TMP_Text content, ScrollRect scroll)
        {
            contentText = content;
            scrollRect = scroll;
            Render();
        }

        public void AddUserMessage(string text) => Add(Speaker("You", UITheme.AccentText) + text);
        public void AddAssistantMessage(string text) => Add(Speaker("Assistant", UITheme.Success) + text);
        public void AddSystemMessage(string text) => Add($"<color=#{UITheme.Hex(UITheme.TextSecondary)}>{text}</color>");

        private static string Speaker(string name, Color color) =>
            $"<font=\"{UITheme.FontAssetName(UITheme.FontRole.SemiBold)}\"><color=#{UITheme.Hex(color)}>{name}</color></font>\n";

        private void Add(string line)
        {
            messages.Add(line);
            while (messages.Count > maxMessages) messages.RemoveAt(0);
            Render();
        }

        private void Render()
        {
            if (contentText == null) return;
            contentText.text = messages.Count == 0
                ? $"<color=#{UITheme.Hex(UITheme.TextSecondary)}>Use the Assistant tool and hold the right trigger to ask about the current view.</color>"
                : string.Join("\n\n", messages);
            if (scrollRect != null && isActiveAndEnabled) StartCoroutine(ScrollToBottomNextFrame());
        }

        // layout braucht einen frame, sonst scrollt er zur alten hoehe
        private IEnumerator ScrollToBottomNextFrame()
        {
            yield return null;
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 0f;
        }
    }
}
