using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace BioimageVR
{
    // ein panel, drei ansichten: chat verlauf, volumen liste, render-einstellungen
    // umschalten ueber ein hamburger menu links oben, klappt ein linkes ausklapp-menu auf
    // wie bei handy-apps, chat ist die standardseite
    // dreht sich waehrend des greifens live zur kamera, nicht erst beim loslassen
    public class SidePanelController : MonoBehaviour
    {
        // einzige quelle fuer die dragbar-hoehe, SetupSidePanel baut danach -
        // geschlossen deutlich groesser, sonst ist die leiste zu duenn zum treffen
        public const float OpenDragBarHeight = 34f;
        public const float ClosedDragBarHeight = 90f;

        private enum Page { Chat, Gallery, Settings }

        [SerializeField] private GameObject panelBody;
        [SerializeField] private GameObject chatPage;
        [SerializeField] private GameObject galleryPage;
        [SerializeField] private GameObject settingsPage;
        [SerializeField] private VolumeGalleryMenu galleryMenu;
        [SerializeField] private Button menuButton;
        [SerializeField] private Text titleLabel;
        [SerializeField] private GameObject drawer;
        [SerializeField] private Button drawerScrimButton;
        [SerializeField] private Button chatMenuButton;
        [SerializeField] private Button galleryMenuButton;
        [SerializeField] private Button settingsMenuButton;
        [SerializeField] private Image grabHandleImage;
        [SerializeField] private Button closeButton;
        [SerializeField] private Text closeButtonLabel;
        [SerializeField] private RectTransform dragBarRect;
        [SerializeField] private BoxCollider grabZoneCollider;

        private Page currentPage = Page.Chat;
        private bool isOpen = true;
        private XRGrabInteractable grabInteractable;
        private RectTransform canvasRect;

        private void Awake()
        {
            canvasRect = GetComponent<RectTransform>();
            if (menuButton != null) menuButton.onClick.AddListener(OpenMenu);
            if (drawerScrimButton != null) drawerScrimButton.onClick.AddListener(CloseMenu);
            if (chatMenuButton != null) chatMenuButton.onClick.AddListener(() => SelectPage(Page.Chat));
            if (galleryMenuButton != null) galleryMenuButton.onClick.AddListener(() => SelectPage(Page.Gallery));
            if (settingsMenuButton != null) settingsMenuButton.onClick.AddListener(() => SelectPage(Page.Settings));
            if (closeButton != null) closeButton.onClick.AddListener(ToggleOpen);
            Apply();
            ApplyOpen();
            CloseMenu();

            grabInteractable = GetComponent<XRGrabInteractable>();
            if (grabInteractable != null)
            {
                // hellt die griffleiste auf sobald der strahl draufzeigt, auch vor dem druecken -
                // ohne das ist gar nicht erkennbar dass man da was greifen kann
                grabInteractable.hoverEntered.AddListener(OnHandleHoverEntered);
                grabInteractable.hoverExited.AddListener(OnHandleHoverExited);
            }
        }

        private void OnDestroy()
        {
            if (grabInteractable == null) return;
            grabInteractable.hoverEntered.RemoveListener(OnHandleHoverEntered);
            grabInteractable.hoverExited.RemoveListener(OnHandleHoverExited);
        }

        private void Update()
        {
            // waehrend des greifens jeden frame zur kamera drehen, nicht erst beim loslassen
            if (grabInteractable != null && grabInteractable.isSelected) FaceCamera();
        }

        private void OnHandleHoverEntered(HoverEnterEventArgs args)
        {
            if (grabHandleImage != null) grabHandleImage.color = UITheme.Accent;
        }

        private void OnHandleHoverExited(HoverExitEventArgs args)
        {
            if (grabHandleImage != null) grabHandleImage.color = UITheme.AccentSoft;
        }

        private void FaceCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 toCam = cam.transform.position - transform.position;
            toCam.y = 0f; // nur um die hochachse drehen, panel bleibt aufrecht
            if (toCam.sqrMagnitude < 0.0001f) return;

            transform.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
        }

        private void OpenMenu()
        {
            if (drawer != null) drawer.SetActive(true);
        }

        private void CloseMenu()
        {
            if (drawer != null) drawer.SetActive(false);
        }

        private void SelectPage(Page page)
        {
            currentPage = page;
            Apply();
            CloseMenu();
        }

        private void Apply()
        {
            if (chatPage != null) chatPage.SetActive(currentPage == Page.Chat);
            if (galleryPage != null) galleryPage.SetActive(currentPage == Page.Gallery);
            if (settingsPage != null) settingsPage.SetActive(currentPage == Page.Settings);

            // neu gepushte dateien landen sofort in der liste, kein app-neustart mehr noetig
            if (currentPage == Page.Gallery && galleryMenu != null) galleryMenu.Refresh();
            if (titleLabel != null)
            {
                titleLabel.text = currentPage switch
                {
                    Page.Chat => "Chat",
                    Page.Gallery => "Bilder",
                    _ => "Einstellungen",
                };
            }
        }

        // DragBar mit dem X/+ knopf bleibt immer da - nur so findet man das panel
        // nach dem schliessen ueberhaupt wieder
        private void ToggleOpen()
        {
            isOpen = !isOpen;
            ApplyOpen();
        }

        private void ApplyOpen()
        {
            if (panelBody != null) panelBody.SetActive(isOpen);
            if (closeButtonLabel != null) closeButtonLabel.text = isOpen ? "X" : "+";

            float height = isOpen ? OpenDragBarHeight : ClosedDragBarHeight;
            if (dragBarRect != null)
                dragBarRect.sizeDelta = new Vector2(dragBarRect.sizeDelta.x, height);

            // GrabZone haengt am oberen canvas-rand, muss beim wachsen/schrumpfen
            // der dragbar mitwandern (siehe SetupSidePanel.MakeGrabbable fuer die
            // urspruengliche platzierungslogik)
            if (grabZoneCollider != null && canvasRect != null)
            {
                Vector3 center = grabZoneCollider.center;
                center.y = canvasRect.sizeDelta.y / 2f - height / 2f;
                grabZoneCollider.center = center;

                Vector3 size = grabZoneCollider.size;
                size.y = height;
                grabZoneCollider.size = size;
            }
        }
    }
}
