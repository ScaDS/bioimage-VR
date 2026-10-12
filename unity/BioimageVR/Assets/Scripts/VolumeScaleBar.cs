using TMPro;
using UnityEngine;

namespace BioimageVR
{
    // massstabsbalken fester bildschirmgroesse, nur die beschriftung aendert sich beim zoomen
    public class VolumeScaleBar : MonoBehaviour
    {
        [SerializeField] private Transform volumeTransform;
        [SerializeField] private VolumeView volumeView;
        [SerializeField] private TMP_Text label;
        [SerializeField] private float barWorldLength = 0.12f;

        private Vector3 baseScaleAtLoad = Vector3.one;
        private float maxDimensionUm;
        private bool ready;

        private void OnEnable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded += HandleVolumeLoaded;
        }

        private void OnDisable()
        {
            if (volumeView != null) volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume)
        {
            // groesse beim laden merken, daraus den aktuellen zoom faktor rechnen
            baseScaleAtLoad = volumeTransform.localScale;
            maxDimensionUm = Mathf.Max(volume.SizeX * volume.VoxelSize.x,
                Mathf.Max(volume.SizeY * volume.VoxelSize.y, volume.SizeZ * volume.VoxelSize.z));
            ready = true;
            Refresh();
        }

        private void Update()
        {
            if (ready) Refresh();
        }

        private void Refresh()
        {
            if (volumeTransform == null || label == null || Mathf.Approximately(baseScaleAtLoad.x, 0f)) return;

            float currentZoomFactor = volumeTransform.localScale.x / baseScaleAtLoad.x;
            float unitsPerWorldUnit = maxDimensionUm / Mathf.Max(currentZoomFactor, 0.0001f);
            // maxDimensionUm ist in der einheit des bilds, siehe VolumeView.Unit
            label.text = Units.Length(barWorldLength * unitsPerWorldUnit, volumeView != null ? volumeView.Unit : "µm", "0.#");
        }
    }
}
