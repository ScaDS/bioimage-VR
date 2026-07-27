using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BioimageVR
{
    // ruft scads vlm api auf, frage plus bild rein, text raus
    public class VLMClient : MonoBehaviour
    {
        private const string ApiUrl = "https://llm.scads.ai/v1/chat/completions";
        private const string Model = "alias-vision";

        private string apiKey;

        private void Awake()
        {
            apiKey = ApiKeyLoader.Load(nameof(VLMClient));
        }

        // ein bild, eine frage, laeuft als coroutine
        public void AskAboutImage(Texture2D image, string question, Action<string> onSuccess, Action<string> onError)
        {
            StartCoroutine(AskAboutImageCoroutine(image, question, onSuccess, onError));
        }

        private IEnumerator AskAboutImageCoroutine(Texture2D image, string question, Action<string> onSuccess, Action<string> onError)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                onError?.Invoke("Kein API-Key geladen (siehe ~/.scadsai-api-key).");
                yield break;
            }

            byte[] jpegBytes = image.EncodeToJPG(90);
            string dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(jpegBytes)}";
            string json = BuildRequestJson(Model, question, dataUrl);

            using var request = new UnityWebRequest(ApiUrl, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"{request.error}: {request.downloadHandler?.text}");
                yield break;
            }

            onSuccess?.Invoke(ExtractAnswer(request.downloadHandler.text));
        }

        // json von hand gebaut, feste form, kein package noetig
        // image_url ist hier ein reiner data url string, nicht das offizielle objekt
        private static string BuildRequestJson(string model, string question, string dataUrl)
        {
            return "{"
                + $"\"model\":\"{JsonEscape(model)}\","
                + "\"messages\":[{\"role\":\"user\",\"content\":["
                + $"{{\"type\":\"text\",\"text\":\"{JsonEscape(question)}\"}},"
                + $"{{\"type\":\"image_url\",\"image_url\":\"{JsonEscape(dataUrl)}\"}}"
                + "]}]}";
        }

        private static string JsonEscape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                     .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        [Serializable] private class ChatResponse { public Choice[] choices; }
        [Serializable] private class Choice { public ChatMessage message; }
        [Serializable] private class ChatMessage { public string content; }

        private static string ExtractAnswer(string responseJson)
        {
            try
            {
                var parsed = JsonUtility.FromJson<ChatResponse>(responseJson);
                if (parsed?.choices != null && parsed.choices.Length > 0)
                    return parsed.choices[0].message.content;
            }
            catch (Exception e)
            {
                Debug.LogError($"VLMClient: konnte Antwort nicht parsen: {e.Message}\n{responseJson}");
            }
            return "(keine Antwort erhalten)";
        }
    }
}
