using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace BioimageVR
{
    // holt rag kontext fuers gerade geladene bild vom lokalen preprocessing/upload_server.py
    // gleiches muster wie IdrClient.cs, nur ein endpunkt der einen fertigen text liefert
    public class RagClient : MonoBehaviour
    {
        [Tooltip("wlan adresse von preprocessing/upload_server.py, siehe dessen konsolenausgabe beim start")]
        [SerializeField] private string serverBaseUrl = "http://192.168.1.23:8000";

        [Serializable] private class RagQueryResponse { public string context; public string error; }

        public void FetchContext(string question, int currentImageId, Action<string, string> callback)
        {
            StartCoroutine(FetchContextCoroutine(question, currentImageId, callback));
        }

        private IEnumerator FetchContextCoroutine(string question, int currentImageId, Action<string, string> callback)
        {
            string url = $"{serverBaseUrl}/rag_query?question={UnityWebRequest.EscapeURL(question)}&current_image_id={currentImageId}";
            using var request = UnityWebRequest.Get(url);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                callback?.Invoke(null, $"{request.error}: {request.downloadHandler?.text}");
                yield break;
            }

            RagQueryResponse parsed;
            try
            {
                parsed = JsonUtility.FromJson<RagQueryResponse>(request.downloadHandler.text);
            }
            catch (Exception e)
            {
                callback?.Invoke(null, $"Antwort konnte nicht verarbeitet werden: {e.Message}");
                yield break;
            }

            if (!string.IsNullOrEmpty(parsed?.error))
            {
                callback?.Invoke(null, parsed.error);
                yield break;
            }

            callback?.Invoke(parsed?.context ?? "", null);
        }
    }
}
