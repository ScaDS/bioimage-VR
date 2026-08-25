using System;
using UnityEngine;

namespace BioimageVR
{
    // laedt ein .ply mesh (siehe PlyMeshLoader), zentriert und normalisiert es -
    // gleiches prinzip wie VolumeView.cs beim volumen: rohe koordinaten bleiben
    // rohe koordinaten in der datei, unity macht die normalisierung beim laden
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class MeshView : MonoBehaviour
    {
        [Tooltip("Path to a .ply file, e.g. produced by preprocessing/decimate_mesh.py")]
        public string MeshFilePath;

        public Mesh LoadedMesh { get; private set; }

        public event Action<Mesh> OnMeshLoaded;

        private void Start()
        {
            if (!string.IsNullOrEmpty(MeshFilePath)) LoadMesh(MeshFilePath);
        }

        public void LoadMesh(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                Debug.LogError("MeshView: kein pfad angegeben.", this);
                return;
            }

            Mesh mesh;
            try
            {
                mesh = PlyMeshLoader.Load(rawPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"MeshView: failed to load '{rawPath}': {e.Message}", this);
                return;
            }

            // um den ursprung zentrieren - rohe connectomics koordinaten (z.b. nanometer
            // bei microns-explorer.org daten) liegen sonst weit ausserhalb der szene
            Vector3 center = mesh.bounds.center;
            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] -= center;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();

            // laengste dimension auf 1 unity-einheit normalisieren, gleiches prinzip wie
            // VolumeView.cs's physicalSize/maxDimension
            Vector3 size = mesh.bounds.size;
            float maxDimension = Mathf.Max(size.x, size.y, size.z);
            if (maxDimension <= 0f) maxDimension = 1f;
            transform.localScale = Vector3.one / maxDimension;

            GetComponent<MeshFilter>().mesh = mesh;

            MeshFilePath = rawPath;
            LoadedMesh = mesh;
            OnMeshLoaded?.Invoke(mesh);
        }
    }
}
