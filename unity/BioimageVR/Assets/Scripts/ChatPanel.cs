using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // haelt frage antwort verlauf fest, scrollt automatisch runter
    public class ChatPanel : MonoBehaviour
    {
        [SerializeField] private Text contentText;
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private int maxMessages = 40;

        private readonly List<string> messages = new List<string>();

        public void AddUserMessage(string text) => Add($"Du: {text}");
        public void AddAssistantMessage(string text) => Add($"VLM: {text}");
        public void AddSystemMessage(string text) => Add(text);

        private void Add(string line)
        {
            messages.Add(line);
            while (messages.Count > maxMessages) messages.RemoveAt(0);

            if (contentText != null) contentText.text = string.Join("\n\n", messages);
            if (scrollRect != null) StartCoroutine(ScrollToBottomNextFrame());
        }

        // layout braucht einen frame, sonst scrollt er zur alten hoehe
        private IEnumerator ScrollToBottomNextFrame()
        {
            yield return null;
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 0f;
        }
    }
}
