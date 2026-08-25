using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BioimageVR
{
    // openai-kompatibles tool/function-calling schema, siehe CHATMICROSCOPY.md 3.2 -
    // ParametersJson ist rohes json-schema (nur ein paar simple flache tools, kein
    // generischer serializer noetig)
    [Serializable]
    public class ToolDefinition
    {
        public string Name;
        public string Description;
        public string ParametersJson;
    }

    // ergebnis eines vom vlm ausgeloesten tool-calls, ArgumentsJson noch roh (der
    // aufrufer/dispatcher kennt die konkrete argument-struktur pro tool-name)
    public class ToolCall
    {
        public string Name;
        public string ArgumentsJson;
    }

    // ruft scads vlm api auf, frage plus bild rein, text ODER tool-calls raus
    public class VLMClient : MonoBehaviour
    {
        private const string ApiUrl = "https://llm.scads.ai/v1/chat/completions";
        private const string Model = "alias-vision";

        private string apiKey;

        private void Awake()
        {
            apiKey = ApiKeyLoader.Load(nameof(VLMClient));
        }

        // einfache variante ohne tools, bisheriges verhalten unveraendert
        public void AskAboutImage(Texture2D image, string question, Action<string> onSuccess, Action<string> onError)
        {
            AskAboutImage(image, question, null, null, onSuccess, null, onError);
        }

        // mit tools, ohne rag kontext - bisheriges verhalten unveraendert
        public void AskAboutImage(Texture2D image, string question, IReadOnlyList<ToolDefinition> tools,
            Action<string> onTextAnswer, Action<List<ToolCall>> onToolCalls, Action<string> onError)
        {
            AskAboutImage(image, question, null, tools, onTextAnswer, onToolCalls, onError);
        }

        // mit optionalem rag kontext (siehe RagClient.cs) als zusaetzliche system-message
        // vor der eigentlichen frage, plus tools: modell antwortet entweder in text
        // (onTextAnswer) oder mit tool-calls (onToolCalls) - tool_choice "auto" laesst
        // das modell selbst entscheiden je nach frage, kein extra modus-umschalter noetig
        public void AskAboutImage(Texture2D image, string question, string context, IReadOnlyList<ToolDefinition> tools,
            Action<string> onTextAnswer, Action<List<ToolCall>> onToolCalls, Action<string> onError)
        {
            StartCoroutine(AskAboutImageCoroutine(image, question, context, tools, onTextAnswer, onToolCalls, onError));
        }

        private IEnumerator AskAboutImageCoroutine(Texture2D image, string question, string context, IReadOnlyList<ToolDefinition> tools,
            Action<string> onTextAnswer, Action<List<ToolCall>> onToolCalls, Action<string> onError)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                onError?.Invoke("Kein API-Key geladen (siehe ~/.scadsai-api-key).");
                yield break;
            }

            byte[] jpegBytes = image.EncodeToJPG(90);
            string dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(jpegBytes)}";
            string json = BuildRequestJson(Model, question, context, dataUrl, tools);

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

            ParseResponse(request.downloadHandler.text, onTextAnswer, onToolCalls, onError);
        }

        // json von hand gebaut, feste form, kein package noetig
        // image_url ist hier ein reiner data url string, nicht das offizielle objekt
        private static string BuildRequestJson(string model, string question, string context, string dataUrl, IReadOnlyList<ToolDefinition> tools)
        {
            string toolsJson = "";
            if (tools != null && tools.Count > 0)
            {
                var entries = new string[tools.Count];
                for (int i = 0; i < tools.Count; i++)
                {
                    ToolDefinition t = tools[i];
                    entries[i] = "{\"type\":\"function\",\"function\":{"
                        + $"\"name\":\"{JsonEscape(t.Name)}\","
                        + $"\"description\":\"{JsonEscape(t.Description)}\","
                        + $"\"parameters\":{t.ParametersJson}"
                        + "}}";
                }
                toolsJson = $",\"tools\":[{string.Join(",", entries)}],\"tool_choice\":\"auto\"";
            }

            // rag kontext (siehe RagClient.cs) als eigene system-message vor der frage,
            // damit das modell zwischen hintergrundwissen und eigentlicher frage trennt
            string systemMessage = string.IsNullOrEmpty(context)
                ? ""
                : "{\"role\":\"system\",\"content\":\"" + JsonEscape(context) + "\"},";

            return "{"
                + $"\"model\":\"{JsonEscape(model)}\","
                + "\"messages\":[" + systemMessage + "{\"role\":\"user\",\"content\":["
                + $"{{\"type\":\"text\",\"text\":\"{JsonEscape(question)}\"}},"
                + $"{{\"type\":\"image_url\",\"image_url\":\"{JsonEscape(dataUrl)}\"}}"
                + "]}]"
                + toolsJson
                + "}";
        }

        private static string JsonEscape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                     .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        [Serializable] private class ChatResponse { public Choice[] choices; }
        [Serializable] private class Choice { public ChatMessage message; }
        [Serializable] private class ChatMessage { public string content; public ToolCallJson[] tool_calls; }
        [Serializable] private class ToolCallJson { public string id; public string type; public FunctionCallJson function; }
        [Serializable] private class FunctionCallJson { public string name; public string arguments; }

        private static void ParseResponse(string responseJson, Action<string> onTextAnswer,
            Action<List<ToolCall>> onToolCalls, Action<string> onError)
        {
            try
            {
                var parsed = JsonUtility.FromJson<ChatResponse>(responseJson);
                ChatMessage message = parsed?.choices != null && parsed.choices.Length > 0 ? parsed.choices[0].message : null;

                if (message?.tool_calls != null && message.tool_calls.Length > 0)
                {
                    var calls = new List<ToolCall>(message.tool_calls.Length);
                    foreach (ToolCallJson tc in message.tool_calls)
                        calls.Add(new ToolCall { Name = tc.function.name, ArgumentsJson = tc.function.arguments });
                    onToolCalls?.Invoke(calls);
                    return;
                }

                onTextAnswer?.Invoke(message?.content ?? "(keine Antwort erhalten)");
            }
            catch (Exception e)
            {
                Debug.LogError($"VLMClient: konnte Antwort nicht parsen: {e.Message}\n{responseJson}");
                onError?.Invoke("Antwort konnte nicht verarbeitet werden.");
            }
        }
    }
}
