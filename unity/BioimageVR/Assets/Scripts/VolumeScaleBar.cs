using TMPro;
using UnityEngine;

namespace BioimageVR
{
    // massstabsbalken im kartenstil: feste, dezente bildschirmgroesse (haengt an der
    // kamera, siehe SetupSidePanel.CreateScaleBar), nur die BESCHRIFTUNG aendert sich
    // live beim zoomen - nicht die balkenlaenge selbst. (23.09.: vorherige version
    // zeigte eine feste reale laenge und haengte am volumen, wurde dadurch beim
    // reinzoomen riesig - das hier ist die "wie bei interaktiven karten"-variante)
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
            // volume-groesse zum ladezeitpunkt merken - VolumeZoom skaliert danach
            // relativ dazu (baseScale * scaleMultiplier), diese referenz brauchen wir
            // um aus der AKTUELLEN skalierung auf den aktuellen zoom-faktor zurueckzurechnen
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
