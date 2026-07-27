using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BioimageVR
{
    // ruft scads stt api auf, gleiches api key schema wie vlmclient
    public class STTClient : MonoBehaviour
    {
        private const string ApiUrl = "https://llm.scads.ai/v1/audio/transcriptions";
        private const string Model = "alias-stt";

        private string apiKey;

        private void Awake()
        {
            apiKey = ApiKeyLoader.Load(nameof(STTClient));
        }

        // transkribiert clip zu text, laeuft als coroutine
        public void Transcribe(AudioClip clip, Action<string> onSuccess, Action<string> onError)
        {
            StartCoroutine(TranscribeCoroutine(clip, onSuccess, onError));
        }

        private IEnumerator TranscribeCoroutine(AudioClip clip, Action<string> onSuccess, Action<string> onError)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                onError?.Invoke("Kein API-Key geladen (siehe ~/.scadsai-api-key).");
                yield break;
            }

            byte[] wavBytes = WavEncode(clip);

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", wavBytes, "question.wav", "audio/wav"),
                new MultipartFormDataSection("model", Model),
            };

            using var request = UnityWebRequest.Post(ApiUrl, form);
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"{request.error}: {request.downloadHandler?.text}");
                yield break;
            }

            onSuccess?.Invoke(ExtractText(request.downloadHandler.text));
        }

        [Serializable] private class TranscriptionResponse { public string text; }

        private static string ExtractText(string responseJson)
        {
            try
            {
                var parsed = JsonUtility.FromJson<TranscriptionResponse>(responseJson);
                if (!string.IsNullOrEmpty(parsed?.text)) return parsed.text.Trim();
            }
            catch (Exception e)
            {
                Debug.LogError($"STTClient: konnte Antwort nicht parsen: {e.Message}\n{responseJson}");
            }
            return string.Empty;
        }

        // clip zu 16bit pcm wav, mono durch mitteln der kanaele
        // samplerate im header, muss nicht exakt 16khz sein
        private static byte[] WavEncode(AudioClip clip)
        {
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);

            int channels = clip.channels;
            int sampleCount = clip.samples;
            short[] pcm = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float mixed = 0f;
                for (int c = 0; c < channels; c++)
                    mixed += samples[i * channels + c];
                mixed /= channels;
                pcm[i] = (short)Mathf.Clamp(mixed * short.MaxValue, short.MinValue, short.MaxValue);
            }

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                int byteRate = clip.frequency * 2; // mono 16bit
                int dataSize = pcm.Length * 2;

                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));

                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16); // PCM fmt chunk size
                writer.Write((short)1); // PCM
                writer.Write((short)1); // mono
                writer.Write(clip.frequency);
                writer.Write(byteRate);
                writer.Write((short)2); // block align
                writer.Write((short)16); // bits per sample

                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);
                foreach (short sample in pcm)
                    writer.Write(sample);
            }

            return stream.ToArray();
        }
    }
}
