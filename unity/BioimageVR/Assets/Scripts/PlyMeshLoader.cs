using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace BioimageVR
{
    // binaeres ply laden, wie es preprocessing/decimate_mesh.py (trimesh) oder rohe
    // connectomics-exporte (z.b. microns-explorer.org) liefern - nur dreiecksmeshes,
    // nur x/y/z positionen im file, keine farben/normalen (RecalculateNormals stattdessen)
    public static class PlyMeshLoader
    {
        public static Mesh Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int headerEnd = FindHeaderEnd(bytes, path);
            string header = Encoding.ASCII.GetString(bytes, 0, headerEnd);
            string[] lines = header.Split('\n');

            if (lines.Length == 0 || lines[0].Trim() != "ply")
                throw new InvalidDataException($"'{path}' ist keine gueltige .ply datei (fehlende magic zeile).");

            bool foundBinaryFormat = false;
            int vertexCount = -1;
            int faceCount = -1;
            string faceCountType = null;
            int currentElement = 0; // 0 = noch keins, 1 = im vertex element, 2 = im face element
            int vertexPropertyCount = 0;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("format"))
                {
                    if (!line.Contains("binary_little_endian"))
                        throw new NotSupportedException(
                            $"'{path}': nur binary_little_endian .ply wird unterstuetzt, gefunden: '{line}'. " +
                            "preprocessing/decimate_mesh.py exportiert immer in diesem format.");
                    foundBinaryFormat = true;
                }
                else if (line.StartsWith("element vertex"))
                {
                    vertexCount = int.Parse(line.Split(' ')[2], CultureInfo.InvariantCulture);
                    currentElement = 1;
                }
                else if (line.StartsWith("element face"))
                {
                    faceCount = int.Parse(line.Split(' ')[2], CultureInfo.InvariantCulture);
                    currentElement = 2;
                }
                else if (line.StartsWith("property list") && currentElement == 2)
                {
                    // "property list uchar int vertex_indices" (trimesh) oder
                    // "property list int int vertex_indices" (rohe microns-exporte)
                    faceCountType = line.Split(' ')[2];
                }
                else if (line.StartsWith("property") && currentElement == 1)
                {
                    vertexPropertyCount++;
                }
            }

            if (!foundBinaryFormat || vertexCount < 0 || faceCount < 0)
                throw new InvalidDataException($"'{path}': ply header unvollstaendig (format/vertex/face element fehlt).");
            if (vertexPropertyCount != 3)
                throw new NotSupportedException(
                    $"'{path}': erwartet genau 3 vertex properties (x,y,z), gefunden {vertexPropertyCount}. " +
                    "farben/normalen im file werden nicht unterstuetzt.");

            int offset = headerEnd;
            Vector3[] vertices = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                float x = BitConverter.ToSingle(bytes, offset); offset += 4;
                float y = BitConverter.ToSingle(bytes, offset); offset += 4;
                float z = BitConverter.ToSingle(bytes, offset); offset += 4;
                vertices[i] = new Vector3(x, y, z);
            }

            int[] triangles = new int[faceCount * 3];
            for (int i = 0; i < faceCount; i++)
            {
                int indexCount;
                if (faceCountType == "uchar")
                {
                    indexCount = bytes[offset];
                    offset += 1;
                }
                else
                {
                    indexCount = BitConverter.ToInt32(bytes, offset);
                    offset += 4;
                }

                if (indexCount != 3)
                    throw new NotSupportedException(
                        $"'{path}': face {i} hat {indexCount} indizes, nur dreiecke (3) werden unterstuetzt.");

                triangles[i * 3] = BitConverter.ToInt32(bytes, offset); offset += 4;
                triangles[i * 3 + 1] = BitConverter.ToInt32(bytes, offset); offset += 4;
                triangles[i * 3 + 2] = BitConverter.ToInt32(bytes, offset); offset += 4;
            }

            Mesh mesh = new Mesh();
            mesh.indexFormat = vertexCount > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static int FindHeaderEnd(byte[] bytes, string path)
        {
            byte[] marker = Encoding.ASCII.GetBytes("end_header\n");
            for (int i = 0; i < bytes.Length - marker.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (bytes[i + j] != marker[j]) { match = false; break; }
                }
                if (match) return i + marker.Length;
            }
            throw new InvalidDataException($"'{path}': 'end_header' nicht gefunden, keine gueltige .ply datei.");
        }
    }
}
