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
        // zweite, unabhaengige anzeige desselben verlaufs (siehe SetupSidePanel.
        // CreateHudTextPanel) - der kamera-fixierte chat-kurzzugriff unten rechts zeigt
        // denselben inhalt wie die chat-seite im seitenpanel, beide bleiben synchron,
        // ohne dass man dafuer zwischen zwei ChatPanel-instanzen hin und her muesste.
        // optional, darf leer bleiben (aeltere szenen ohne hud-chat-panel)
        [SerializeField] private Text secondaryContentText;
        [SerializeField] private ScrollRect secondaryScrollRect;
        [SerializeField] private int maxMessages = 40;

        private readonly List<string> messages = new List<string>();

        public void AddUserMessage(string text) => Add($"<b><color=#{UITheme.Hex(UITheme.Accent)}>Du</color></b>  {text}");
        public void AddAssistantMessage(string text) => Add($"<b><color=#{UITheme.Hex(UITheme.AccentSecondary)}>VLM</color></b>  {text}");
        public void AddSystemMessage(string text) => Add($"<color=#{UITheme.Hex(UITheme.Warning)}>{text}</color>");

        private void Add(string line)
        {
            messages.Add(line);
            while (messages.Count > maxMessages) messages.RemoveAt(0);

            string joined = string.Join("\n\n", messages);
            if (contentText != null) contentText.text = joined;
            if (secondaryContentText != null) secondaryContentText.text = joined;
            if (scrollRect != null || secondaryScrollRect != null) StartCoroutine(ScrollToBottomNextFrame());
        }

        // layout braucht einen frame, sonst scrollt er zur alten hoehe
        private IEnumerator ScrollToBottomNextFrame()
        {
            yield return null;
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 0f;
            if (secondaryScrollRect != null) secondaryScrollRect.verticalNormalizedPosition = 0f;
        }
    }
}
