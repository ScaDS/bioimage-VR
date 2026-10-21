using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BioimageVR
{
    // segmentierte zellen anschauen, hover zeigt schild, X oder A waehlt aus
    [RequireComponent(typeof(VolumeView))]
    public class CellInspector : MonoBehaviour
    {
        [SerializeField] private XRRayInteractor leftHandRay;
        [SerializeField] private XRRayInteractor rightHandRay;

        private VolumeView volumeView;
        private MeasureTool measureTool;
        private LabelEditor labelEditor;
        private InputAction leftClickAction;
        private InputAction rightClickAction;
        private InputAction mouseClickAction;
        private WorldTooltip label;

        // zelle unter dem strahl und unter einer zeile der zellliste
        private int rayHover;
        private Vector3 rayHoverLocal;
        private int listHover;

        private void Awake()
        {
            volumeView = GetComponent<VolumeView>();
            measureTool = GetComponent<MeasureTool>();
            labelEditor = GetComponent<LabelEditor>();
            // gleicher knopf wie der ui klick in SetupSidePanel.EnsureHandRay
            leftClickAction = new InputAction(type: InputActionType.Button, binding: "<XRController>{LeftHand}/primaryButton");
            rightClickAction = new InputAction(type: InputActionType.Button, binding: "<XRController>{RightHand}/primaryButton");
            mouseClickAction = new InputAction(type: InputActionType.Button, binding: "<Mouse>/middleButton");
        }

        private void OnEnable()
        {
            leftClickAction.Enable();
            rightClickAction.Enable();
            mouseClickAction.Enable();
        }

        private void OnDisable()
        {
            leftClickAction.Disable();
            rightClickAction.Disable();
            mouseClickAction.Disable();
            volumeView.HoverLabel = 0;
            if (label != null) label.Hide();
        }

        private void OnDestroy()
        {
            leftClickAction?.Dispose();
            rightClickAction?.Dispose();
            mouseClickAction?.Dispose();
            if (label != null) Destroy(label.gameObject);
        }

        // von der zellliste, zeile unter dem strahl, 0 wenn keine
        public void SetListHover(int id) => listHover = id;

        // zelle unter strahl oder zeile fuers sprachmodell, auch bei ausgeblendeter maske
        public int PointedLabel()
        {
            if (!volumeView.HasLabels) return 0;
            if (listHover > 0) return listHover;
            Ray?[] rays = { HandRay(rightHandRay), HandRay(leftHandRay), XRSettings.isDeviceActive ? null : MouseRay() };
            foreach (Ray? ray in rays)
                if (ray.HasValue && volumeView.TryPickCell(ray.Value, out int id, out _)) return id;
            return 0;
        }

        private bool Active => volumeView.HasLabels && volumeView.ShowLabels;
        private bool MeasureOwnsClicks => measureTool != null && measureTool.CurrentMode != MeasureTool.Mode.Off;

        private void Update()
        {
            if (!Active)
            {
                rayHover = 0;
                listHover = 0;
                volumeView.HoverLabel = 0;
                return;
            }

            UpdateRayHover();
            volumeView.HoverLabel = listHover > 0 ? listHover : rayHover;

            if (MeasureOwnsClicks) return;
            if (mouseClickAction.WasPressedThisFrame()) Click(MouseRay());
            else if (leftClickAction.WasPressedThisFrame()) Click(HandRay(leftHandRay));
            else if (rightClickAction.WasPressedThisFrame()) Click(HandRay(rightHandRay));
        }

        // rechts zuerst, dann links, am desktop ohne headset die maus
        private void UpdateRayHover()
        {
            rayHover = 0;
            if (TryHover(HandRay(rightHandRay)) || TryHover(HandRay(leftHandRay))) return;
            if (!XRSettings.isDeviceActive) TryHover(MouseRay());
        }

        private bool TryHover(Ray? ray)
        {
            if (!ray.HasValue || !volumeView.TryPickCell(ray.Value, out int id, out Vector3 hit)) return false;
            rayHover = id;
            rayHoverLocal = hit;
            return true;
        }

        private void Click(Ray? ray)
        {
            // strahl auf dem panel, dann ist der klick fuers panel gemeint
            if (!ray.HasValue) return;
            int id = volumeView.TryPickCell(ray.Value, out int picked, out _) ? picked : 0;
            // im verbinden modus legt der klick die zweite zelle fest statt auszuwaehlen
            if (labelEditor != null && labelEditor.TryHandleClick(id)) return;
            volumeView.SelectedLabel = id;
        }

        // null wenn der strahl gerade aufs panel zeigt
        private static Ray? HandRay(XRRayInteractor ray)
        {
            if (ray == null || !ray.isActiveAndEnabled) return null;
            if (ray.TryGetCurrentUIRaycastResult(out _)) return null;
            Transform origin = ray.rayOriginTransform != null ? ray.rayOriginTransform : ray.transform;
            return new Ray(origin.position, origin.forward);
        }

        private static Ray? MouseRay()
        {
            if (Mouse.current == null || Camera.main == null) return null;
            return Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        }

        // schild nur fuers hovern, die auswahl zeigt die karte am volumen
        private void LateUpdate()
        {
            int shown = 0;
            Vector3 anchorLocal = default;
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;

            if (Active && vol?.Cells != null)
            {
                if (rayHover > 0)
                {
                    shown = rayHover;
                    anchorLocal = rayHoverLocal;
                }
                else if (listHover > 0 && listHover < vol.Cells.Length)
                {
                    shown = listHover;
                    CellInfo c = vol.Cells[shown];
                    anchorLocal = volumeView.VoxelToLocal(new Vector3(c.Centroid.x + 0.5f, c.Max.y + 1f, c.Centroid.z + 0.5f));
                }
            }

            if (shown <= 0 || shown >= vol.Cells.Length)
            {
                if (label != null) label.Hide();
                return;
            }

            if (label == null) label = WorldTooltip.Create("CellTooltip");
            CellInfo cell = vol.Cells[shown];
            string text = WorldTooltip.Title(volumeView.LabelName(shown)) + "\n" +
                          WorldTooltip.Mono(CellStats.Describe(cell, vol.VoxelSize, volumeView.Unit));
            SplitHint hint = volumeView.GetSplitHint(shown);
            if (hint.Found) text += "\n" + WorldTooltip.Warn(hint.Describe());
            label.Show(text, transform.TransformPoint(anchorLocal) + Vector3.up * 0.02f);
        }
    }
}
