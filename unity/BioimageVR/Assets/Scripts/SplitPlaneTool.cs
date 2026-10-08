using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BioimageVR
{
    // schnittebene zum teilen einer zelle, entscheidung 23.09.: ebene statt freihand
    // folgt dem controller der auf die zelle zeigt, die ebene enthaelt den strahl,
    // gekippt wird sie durchs drehen des handgelenks. wie ein messer von vorne
    // aktiv nur solange LabelEditor.SplitSource gesetzt ist
    [RequireComponent(typeof(VolumeView))]
    public class SplitPlaneTool : MonoBehaviour
    {
        [SerializeField] private XRRayInteractor leftHandRay;
        [SerializeField] private XRRayInteractor rightHandRay;
        [SerializeField] private Material planeMaterial;
        [SerializeField] private Color planeColor = new Color(0.30f, 0.80f, 0.74f, 0.35f);

        private VolumeView volumeView;
        private LabelEditor labelEditor;
        private Transform plane;

        private bool hasPlane;
        private Vector3 planePoint;
        private Vector3 planeNormal;
        private Vector3 planeAlongRay;

        private void Awake()
        {
            volumeView = GetComponent<VolumeView>();
            labelEditor = GetComponent<LabelEditor>();
        }

        private void OnDisable()
        {
            if (plane != null) plane.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (plane != null) Destroy(plane.gameObject);
        }

        // letzte gueltige ebene in weltkoordinaten
        public bool TryGetPlane(out Vector3 point, out Vector3 normal)
        {
            point = planePoint;
            normal = planeNormal;
            return hasPlane;
        }

        public void ResetPlane() => hasPlane = false;

        private void LateUpdate()
        {
            int source = labelEditor != null ? labelEditor.SplitSource : 0;
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            if (source <= 0 || vol?.Cells == null || source >= vol.Cells.Length)
            {
                if (plane != null) plane.gameObject.SetActive(false);
                return;
            }

            UpdatePlane(source, vol.Cells[source]);
            if (!hasPlane)
            {
                if (plane != null) plane.gameObject.SetActive(false);
                return;
            }
            ShowPlane(vol.Cells[source]);
        }

        // strahl der gerade auf die zelle zeigt, rechts zuerst, am desktop die maus
        private void UpdatePlane(int source, CellInfo cell)
        {
            if (TryRay(rightHandRay, source, cell) || TryRay(leftHandRay, source, cell)) return;
            if (!XRSettings.isDeviceActive && Mouse.current != null && Camera.main != null)
            {
                Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
                TryPlaneFromRay(ray, Camera.main.transform.up, source, cell);
            }
        }

        private bool TryRay(XRRayInteractor interactor, int source, CellInfo cell)
        {
            if (interactor == null || !interactor.isActiveAndEnabled) return false;
            if (interactor.TryGetCurrentUIRaycastResult(out _)) return false;
            Transform origin = interactor.rayOriginTransform != null ? interactor.rayOriginTransform : interactor.transform;
            return TryPlaneFromRay(new Ray(origin.position, origin.forward), origin.up, source, cell);
        }

        // ebene durch den trefferpunkt auf der zelle, oder den naechsten punkt zum schwerpunkt
        // normale steht senkrecht auf strahl und controller oben, ebene enthaelt also den strahl
        private bool TryPlaneFromRay(Ray ray, Vector3 up, int source, CellInfo cell)
        {
            Vector3 point;
            if (volumeView.TryPickCell(ray, out int id, out Vector3 hitLocal) && id == source)
            {
                point = transform.TransformPoint(hitLocal);
            }
            else
            {
                // strahl verfehlt die zelle knapp, dann am schwerpunkt bleiben solange er in der naehe ist
                Vector3 centroid = transform.TransformPoint(volumeView.VoxelToLocal(cell.Centroid + Vector3.one * 0.5f));
                Vector3 closest = ray.origin + ray.direction * Mathf.Max(0f, Vector3.Dot(centroid - ray.origin, ray.direction));
                if (Vector3.Distance(closest, centroid) > CellWorldSize(cell)) return false;
                point = centroid;
            }

            Vector3 normal = Vector3.Cross(ray.direction, up);
            if (normal.sqrMagnitude < 1e-6f) return false;
            planePoint = point;
            planeNormal = normal.normalized;
            planeAlongRay = ray.direction;
            hasPlane = true;
            return true;
        }

        private float CellWorldSize(CellInfo cell)
        {
            Vector3 a = transform.TransformPoint(volumeView.VoxelToLocal(cell.Min));
            Vector3 b = transform.TransformPoint(volumeView.VoxelToLocal(cell.Max + Vector3Int.one));
            return Vector3.Distance(a, b) * 0.6f;
        }

        private void ShowPlane(CellInfo cell)
        {
            if (plane == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "SplitPlane";
                Destroy(go.GetComponent<Collider>());
                var renderer = go.GetComponent<MeshRenderer>();
                if (planeMaterial != null) renderer.material = planeMaterial;
                renderer.material.color = planeColor;
                plane = go.transform;
            }
            plane.gameObject.SetActive(true);
            plane.position = planePoint;
            // quad liegt in seiner xy ebene, forward ist die normale, oben zeigt entlang des strahls
            plane.rotation = Quaternion.LookRotation(planeNormal, planeAlongRay);
            float size = CellWorldSize(cell) * 2.4f;
            plane.localScale = new Vector3(size, size, 1f);
        }
    }
}
