using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // phase 2 test, feste position als bruchteil je volumen dimension
    // zeigt 2d slice mit marker punkt und spawnt passenden 3d marker
    // klick platzierung kommt erst phase 3
    public class Phase2MarkerDemo : MonoBehaviour
    {
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private RawImage sliceImage;
        [SerializeField] private RectTransform sliceMarkerDot;

        [Tooltip("Where to place the test marker, as a fraction (0-1) of each volume dimension.")]
        [SerializeField] private Vector3 markerFraction = new Vector3(0.25f, 0.25f, 0.5f);

        [Tooltip("World-space diameter of the 3D marker sphere, independent of the volume's own (possibly non-uniform) scale.")]
        [SerializeField] private float markerWorldSize = 0.03f;

        private GameObject marker;

        private void Awake()
        {
            if (volumeView == null)
            {
                Debug.LogError("Phase2MarkerDemo: VolumeView not assigned.", this);
                return;
            }

            volumeView.OnVolumeLoaded += OnVolumeLoaded;
            if (volumeView.LoadedVolume != null)
                OnVolumeLoaded(volumeView.LoadedVolume);
        }

        private void OnVolumeLoaded(NiftiVolumeLoader.Volume volume)
        {
            int px = Mathf.Clamp(Mathf.RoundToInt(markerFraction.x * volume.SizeX), 0, volume.SizeX - 1);
            int py = Mathf.Clamp(Mathf.RoundToInt(markerFraction.y * volume.SizeY), 0, volume.SizeY - 1);
            int pz = Mathf.Clamp(Mathf.RoundToInt(markerFraction.z * volume.SizeZ), 0, volume.SizeZ - 1);

            ShowSlice(volume, pz, px, py);
            SpawnVolumeMarker(volume, px, py, pz);
        }

        private void ShowSlice(NiftiVolumeLoader.Volume volume, int z, int markerX, int markerY)
        {
            if (sliceImage == null) return;

            int w = volume.SizeX;
            int h = volume.SizeY;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };

            var pixels = new Color32[w * h];
            int sliceOffset = z * w * h;
            for (int i = 0; i < pixels.Length; i++)
            {
                byte v = volume.Voxels[sliceOffset + i];
                pixels[i] = new Color32(v, v, v, 255);
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            sliceImage.texture = tex;

            if (sliceMarkerDot != null)
            {
                // gleiche indizierung wie pixelpuffer oben, auf rect size gemappt
                Rect rect = sliceImage.rectTransform.rect;
                float u = (markerX + 0.5f) / w;
                float v = (markerY + 0.5f) / h;
                sliceMarkerDot.anchoredPosition = new Vector2(u * rect.width, v * rect.height);
            }
        }

        private void SpawnVolumeMarker(NiftiVolumeLoader.Volume volume, int x, int y, int z)
        {
            // gleiches mapping wie im shader, textur 0 bis 1 auf objekt raum minus 0.5 bis 0.5
            float u = (x + 0.5f) / volume.SizeX;
            float v = (y + 0.5f) / volume.SizeY;
            float w = (z + 0.5f) / volume.SizeZ;
            Vector3 localPos = new Vector3(u - 0.5f, v - 0.5f, w - 0.5f);

            if (marker != null) Destroy(marker);

            Transform volumeTransform = volumeView.transform;
            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Phase2TestMarker";
            Destroy(marker.GetComponent<Collider>());

            marker.transform.SetParent(volumeTransform, false);
            marker.transform.localPosition = localPos;

            // gegenskalieren, sonst wird die kugel durch die volumen skalierung verzerrt
            Vector3 volScale = volumeTransform.localScale;
            marker.transform.localScale = new Vector3(
                markerWorldSize / volScale.x,
                markerWorldSize / volScale.y,
                markerWorldSize / volScale.z);

            var renderer = marker.GetComponent<MeshRenderer>();
            renderer.material.color = Color.red;
        }
    }
}
