using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace BioimageVR
{
    // ein eintrag einer idr_list antwort, gleiches schema auf allen drei ebenen
    [Serializable]
    public class IdrItem
    {
        public int id;
        public string name;
        public bool has_zarr;

        // bild das als vorschau dient, 0 wenn keins gefunden
        public int preview_image_id;
    }

    // spricht mit upload_server.py im gleichen wlan, browsen und direkter download
    public class IdrClient : MonoBehaviour
    {
        [Tooltip("wlan adresse von preprocessing/upload_server.py, siehe dessen konsolenausgabe beim start")]
        [SerializeField] private string serverBaseUrl = "http://192.168.1.23:8000";

        // gleiche adresse nutzt auch VrBridge, nur an einer stelle eintragen
        public string ServerBaseUrl => serverBaseUrl.TrimEnd('/');

        [Serializable] private class ListResponse
        {
            public IdrItem[] items;
            public int total;
            // naechster offset kommt vom server, bilder sind dort gefiltert
            public int next_offset;
            public bool has_more;
            public string error;
        }

        public void ListLevel(string level, int? projectId, int? datasetId, int offset, int limit,
            Action<List<IdrItem>, int, bool, string> callback)
        {
            StartCoroutine(ListLevelCoroutine(level, projectId, datasetId, offset, limit, callback));
        }

        private IEnumerator ListLevelCoroutine(string level, int? projectId, int? datasetId, int offset, int limit,
            Action<List<IdrItem>, int, bool, string> callback)
        {
            string url = $"{serverBaseUrl}/idr_list?level={level}&offset={offset}&limit={limit}";
            if (projectId.HasValue) url += $"&project_id={projectId.Value}";
            if (datasetId.HasValue) url += $"&dataset_id={datasetId.Value}";

            using var request = UnityWebRequest.Get(url);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                callback?.Invoke(new List<IdrItem>(), offset, false, $"{request.error}: {request.downloadHandler?.text}");
                yield break;
            }

            ListResponse parsed;
            try
            {
                parsed = JsonUtility.FromJson<ListResponse>(request.downloadHandler.text);
            }
            catch (Exception e)
            {
                callback?.Invoke(new List<IdrItem>(), offset, false, $"Antwort konnte nicht verarbeitet werden: {e.Message}");
                yield break;
            }

            if (!string.IsNullOrEmpty(parsed?.error))
            {
                callback?.Invoke(new List<IdrItem>(), offset, false, parsed.error);
                yield break;
            }

            var items = parsed?.items != null ? new List<IdrItem>(parsed.items) : new List<IdrItem>();
            callback?.Invoke(items, parsed?.next_offset ?? offset, parsed?.has_more ?? false, null);
        }

        public void FetchVolume(int imageId, Action<string, string> callback)
        {
            StartCoroutine(FetchVolumeCoroutine(imageId, callback));
        }

        private IEnumerator FetchVolumeCoroutine(int imageId, Action<string, string> callback)
        {
            string url = $"{serverBaseUrl}/vr_idr_file?image_id={imageId}";
            using var request = UnityWebRequest.Get(url);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                callback?.Invoke(null, $"{request.error}: {request.downloadHandler?.text}");
                yield break;
            }

            // persistentDataPath ist ueberall beschreibbar
            string dir = Path.Combine(Application.persistentDataPath, "idr_downloads");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"idr_{imageId}.nii.gz");
            File.WriteAllBytes(path, request.downloadHandler.data);
            callback?.Invoke(path, null);
        }
    }
}
