using System.IO;
using BioimageVR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace BioimageVR.EditorSetup
{
    // baut ein einziges seitenpanel (chat verlauf ODER volumen galerie, umschaltbar)
    // plus die technik damit beide controller draufzeigen, klicken und greifen koennen
    // menu: BioimageVR Setup Side Panel
    public static class SetupSidePanel
    {
        private const string ScenePath = "Assets/Scenes/Phase1.unity";
        private static readonly Vector3 LocalOffset = new Vector3(0.35f, -0.15f, 1.0f);

        [MenuItem("BioimageVR/Setup Side Panel")]
        public static void Run()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath && File.Exists(ScenePath))
                scene = EditorSceneManager.OpenScene(ScenePath);

            AllowInsecureHttp();
            EnsureEventSystem();
            EnsureLeftHandRay();
            XRRayInteractor rightHandRay = EnsureRightHandRay();
            RemoveStalePanels();

            SidePanelController controller = FindOrCreatePanel();
            WireHarness(Object.FindFirstObjectByType<VLMTestHarness>(), controller);
            WireHarness(Object.FindFirstObjectByType<VoiceVLMHarness>(), controller);
            WireVolumeZoom(rightHandRay);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: Side panel setup complete - beide Controller identisch belegt: " +
                      "zeigen und Primaerknopf (X links, A rechts) klickt UI ueberall, gehalten auf " +
                      "der DragBar zieht/verschiebt das Panel wie ein Mausklick. Stick scrollt wo der " +
                      "Strahl gerade zeigt. Hamburger-Knopf oben links im Panel oeffnet ein Ausklapp-" +
                      "menu (Chat/Bilder/Einstellungen), Chat ist die Standardseite. Im Bilder-Tab " +
                      "oben Lokal/Aus IDR umschaltbar - dafuer 'IdrClient' Komponente am " +
                      "SideCanvas-Objekt die 'Server Base Url' auf die von preprocessing/" +
                      "upload_server.py angezeigte WLAN-Adresse setzen (Server muss dafuer laufen).");
        }

        // ohne das blockiert UnityWebRequest jedes http:// (nur https) - IdrClient spricht
        // aber bewusst per klartext-http mit dem lokalen upload_server.py im wlan, kein
        // tls-zertifikat fuer eine dev-only-adresse noetig
        private static void AllowInsecureHttp()
        {
            if (PlayerSettings.insecureHttpOption == InsecureHttpOption.AlwaysAllowed) return;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            AssetDatabase.SaveAssets();
        }

        // reste von den alten getrennten panels vor dem merge, gab GameObject.Find("ChatCanvas")
        // und GameObject.Find("GalleryCanvas") als eigene canvases - sonst sieht man beides
        // gleichzeitig plus das neue zusammengelegte panel
        private static void RemoveStalePanels()
        {
            var oldChat = GameObject.Find("ChatCanvas");
            if (oldChat != null) Object.DestroyImmediate(oldChat);

            var oldGallery = GameObject.Find("GalleryCanvas");
            if (oldGallery != null) Object.DestroyImmediate(oldGallery);
        }

        private static void EnsureEventSystem()
        {
            var eventSystem = Object.FindFirstObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                var go = new GameObject("EventSystem", typeof(EventSystem));
                eventSystem = go.GetComponent<EventSystem>();
            }
            if (eventSystem.GetComponent<XRUIInputModule>() == null)
                eventSystem.gameObject.AddComponent<XRUIInputModule>();
        }

        // beide controller gleich belegt, siehe EnsureHandRay
        private static void EnsureLeftHandRay() => EnsureHandRay("LeftHand Ray", "LeftHand");

        private static XRRayInteractor EnsureRightHandRay() => EnsureHandRay("RightHand Ray", "RightHand");

        // ein knopf fuer alles, wie ein mausklick: primaerknopf klickt UI ueberall, gehalten
        // waehrend man auf die DragBar zeigt zieht/verschiebt er stattdessen das panel (das
        // greifen selbst laeuft ueber XRGrabInteractable+GrabZone in MakeGrabbable, hier wird
        // nur dieselbe taste als selectInput verkabelt). stick scrollt das scrollrect unter dem
        // strahl. rechts zusaetzlich frei fuer volumezoom.rightHandRay (stick zoomt, pausiert
        // automatisch sobald der strahl aufs panel zeigt)
        private static XRRayInteractor EnsureHandRay(string rayName, string handTag)
        {
            var rayGO = GameObject.Find(rayName);
            if (rayGO == null)
            {
                var cameraOffset = GameObject.Find("Camera Offset");
                if (cameraOffset == null)
                {
                    Debug.LogError("BioimageVR: 'Camera Offset' nicht gefunden - erst Setup Phase 1 Scene laufen lassen.");
                    return null;
                }

                rayGO = new GameObject(rayName, typeof(LineRenderer));
                rayGO.transform.SetParent(cameraOffset.transform, false);
                rayGO.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
                rayGO.AddComponent<XRInteractorLineVisual>();
            }

            // pointerposition gibts nur bei echten controllern, der simulator kennt nur
            // deviceposition, HandRayPointerFallback schaltet je nach aktivem simulator um
            var fallback = rayGO.GetComponent<HandRayPointerFallback>();
            if (fallback == null) fallback = rayGO.AddComponent<HandRayPointerFallback>();
            fallback.HandTag = handTag;

            // ohne material zeichnet ein LineRenderer pink, aeltere laeufe hatten das noch nicht gesetzt
            FixLineRendererMaterial(rayGO.GetComponent<LineRenderer>());
            FixLineVisualColors(rayGO.GetComponent<XRInteractorLineVisual>());

            var rayInteractor = rayGO.GetComponent<XRRayInteractor>();
            if (rayInteractor == null) rayInteractor = rayGO.AddComponent<XRRayInteractor>();

            // ein knopf fuer alles: auf einen button/zeile zeigen -> klickt, auf die DragBar
            // zeigen -> greift/verschiebt. kein zweiter knopf zum merken, beide haende gleich
            var clickAction = new InputAction(type: InputActionType.Button, binding: $"<XRController>{{{handTag}}}/primaryButton");
            rayInteractor.uiPressInput.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
            rayInteractor.uiPressInput.inputActionPerformed = clickAction;
            rayInteractor.uiPressInput.inputActionValue = clickAction;

            rayInteractor.selectInput.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
            rayInteractor.selectInput.inputActionPerformed = clickAction;
            rayInteractor.selectInput.inputActionValue = clickAction;

            // stick hoch/runter scrollt automatisch das scrollrect unter dem strahl
            var scrollAction = new InputAction(type: InputActionType.Value, binding: $"<XRController>{{{handTag}}}/primary2DAxis", expectedControlType: "Vector2");
            rayInteractor.uiScrollInput.inputSourceMode = XRInputValueReader.InputSourceMode.InputAction;
            rayInteractor.uiScrollInput.inputAction = scrollAction;

            return rayInteractor;
        }

        // erzeugt bei bedarf das material (ohne eins rendert LineRenderer pink) und
        // haelt die farbe synchron zu UITheme.RayColor, auch bei erneutem setup-lauf
        private static void FixLineRendererMaterial(LineRenderer lineRenderer)
        {
            if (lineRenderer == null) return;
            if (lineRenderer.sharedMaterial == null)
                lineRenderer.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            lineRenderer.sharedMaterial.color = UITheme.RayColor;
        }

        // XRInteractorLineVisual faerbt den strahl standardmaessig ROT sobald er nicht
        // gerade auf ein gueltiges ziel zeigt (invalidColorGradient default = Color.red) -
        // das ueberschreibt unsere material-farbe on-the-fly, daher hier separat fixen
        private static void FixLineVisualColors(XRInteractorLineVisual lineVisual)
        {
            if (lineVisual == null) return;
            var gradient = new Gradient
            {
                colorKeys = new[] { new GradientColorKey(UITheme.RayColor, 0f), new GradientColorKey(UITheme.RayColor, 1f) },
                alphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
            };
            lineVisual.validColorGradient = gradient;
            lineVisual.invalidColorGradient = gradient;
            lineVisual.blockedColorGradient = gradient;
        }

        private static SidePanelController FindOrCreatePanel()
        {
            Vector3? preservedPosition = null;
            Quaternion? preservedRotation = null;
            string preservedServerBaseUrl = null;

            // immer frisch bauen (deckt code aenderungen ab), aber an der aktuellen
            // stelle liegen lassen statt bei jedem lauf auf LocalOffset zurueckzusetzen
            var existingController = Object.FindFirstObjectByType<SidePanelController>();
            if (existingController != null)
            {
                var oldRect = existingController.GetComponent<RectTransform>();
                preservedPosition = oldRect.position;
                preservedRotation = oldRect.rotation;

                // sonst muesste die wlan-adresse von upload_server.py nach jedem
                // Setup Side Panel lauf von hand neu eingetragen werden
                var oldIdrClient = existingController.GetComponent<IdrClient>();
                if (oldIdrClient != null)
                    preservedServerBaseUrl = new SerializedObject(oldIdrClient).FindProperty("serverBaseUrl").stringValue;

                Object.DestroyImmediate(existingController.gameObject);
            }

            var canvasGO = new GameObject("SideCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));
            Canvas canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam != null) canvas.worldCamera = cam;

            var rect = canvasGO.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(700f, 900f);
            if (preservedPosition.HasValue)
            {
                rect.localScale = Vector3.one * 0.00085f;
                rect.position = preservedPosition.Value;
                rect.rotation = preservedRotation.Value;
            }
            else
            {
                EditorSetupCommon.PlaceInFrontOfCamera(rect, LocalOffset);
            }

            // alles ausser der DragBar liegt hier drin, damit "schliessen" das ganze
            // panel auf die duenne leiste zusammenklappen kann statt nur einzelne teile
            var panelBodyGO = new GameObject("PanelBody");
            panelBodyGO.transform.SetParent(canvas.transform, false);
            EditorSetupCommon.StretchFull(panelBodyGO.AddComponent<RectTransform>());

            var background = new GameObject("Background", typeof(Image));
            background.transform.SetParent(panelBodyGO.transform, false);
            EditorSetupCommon.StretchFull(background.GetComponent<RectTransform>());
            var backgroundImage = background.GetComponent<Image>();
            backgroundImage.sprite = UITheme.RoundedSprite(28);
            backgroundImage.type = Image.Type.Sliced;
            backgroundImage.color = UITheme.Background;
            // MUSS ein raycast target sein - sonst "sieht" der strahl zwischen den
            // galerie zeilen nichts, faellt durchs panel durch und zoomt/dreht dahinter
            backgroundImage.raycastTarget = true;

            Button menuButton = CreateTitleBar(canvas.transform, panelBodyGO.transform, out Text titleLabel,
                out Image dragBarHandle, out Button closeButton, out Text closeButtonLabel, out RectTransform dragBarRect);

            GameObject drawer = CreateDrawer(canvas.transform, out Button drawerScrimButton,
                out Button chatMenuButton, out Button galleryMenuButton, out Button settingsMenuButton);

            CreateContrastRow(panelBodyGO.transform, out Slider thresholdSlider);

            GameObject chatPage = CreateChatPage(panelBodyGO.transform, out Text chatContentText, out ScrollRect chatScrollRect);
            GameObject galleryPage = CreateGalleryPage(panelBodyGO.transform, out RectTransform galleryContent);
            GameObject settingsPage = CreateSettingsPage(panelBodyGO.transform, out Slider densitySlider,
                out Slider cutRangeMaxSlider, out Button renderModeButton, out Text renderModeLabel,
                out Button shadingButton, out Text shadingLabel,
                out Button channel0Button, out Text channel0Label,
                out Button channel1Button, out Text channel1Label,
                out Button channel2Button, out Text channel2Label);

            var volumeGO = GameObject.Find("Volume");
            VolumeView volumeView = volumeGO != null ? volumeGO.GetComponent<VolumeView>() : null;

            VolumeContrastControl contrastControl = canvasGO.AddComponent<VolumeContrastControl>();
            var contrastSo = new SerializedObject(contrastControl);
            contrastSo.FindProperty("volumeView").objectReferenceValue = volumeView;
            contrastSo.FindProperty("thresholdSlider").objectReferenceValue = thresholdSlider;
            contrastSo.ApplyModifiedProperties();

            VolumeRenderControls renderControls = canvasGO.AddComponent<VolumeRenderControls>();
            var renderControlsSo = new SerializedObject(renderControls);
            renderControlsSo.FindProperty("volumeView").objectReferenceValue = volumeView;
            renderControlsSo.FindProperty("densitySlider").objectReferenceValue = densitySlider;
            renderControlsSo.FindProperty("cutRangeMaxSlider").objectReferenceValue = cutRangeMaxSlider;
            renderControlsSo.FindProperty("renderModeButton").objectReferenceValue = renderModeButton;
            renderControlsSo.FindProperty("renderModeLabel").objectReferenceValue = renderModeLabel;
            renderControlsSo.FindProperty("shadingButton").objectReferenceValue = shadingButton;
            renderControlsSo.FindProperty("shadingLabel").objectReferenceValue = shadingLabel;
            renderControlsSo.FindProperty("channel0Button").objectReferenceValue = channel0Button;
            renderControlsSo.FindProperty("channel0Label").objectReferenceValue = channel0Label;
            renderControlsSo.FindProperty("channel1Button").objectReferenceValue = channel1Button;
            renderControlsSo.FindProperty("channel1Label").objectReferenceValue = channel1Label;
            renderControlsSo.FindProperty("channel2Button").objectReferenceValue = channel2Button;
            renderControlsSo.FindProperty("channel2Label").objectReferenceValue = channel2Label;
            renderControlsSo.ApplyModifiedProperties();

            ChatPanel chatPanel = canvasGO.AddComponent<ChatPanel>();
            var chatSo = new SerializedObject(chatPanel);
            chatSo.FindProperty("contentText").objectReferenceValue = chatContentText;
            chatSo.FindProperty("scrollRect").objectReferenceValue = chatScrollRect;
            chatSo.ApplyModifiedProperties();

            IdrClient idrClient = canvasGO.AddComponent<IdrClient>();
            if (!string.IsNullOrEmpty(preservedServerBaseUrl))
            {
                var idrClientSo = new SerializedObject(idrClient);
                idrClientSo.FindProperty("serverBaseUrl").stringValue = preservedServerBaseUrl;
                idrClientSo.ApplyModifiedProperties();
            }

            VolumeGalleryMenu gallery = canvasGO.AddComponent<VolumeGalleryMenu>();
            var gallerySo = new SerializedObject(gallery);
            gallerySo.FindProperty("volumeView").objectReferenceValue = volumeView;
            gallerySo.FindProperty("listContainer").objectReferenceValue = galleryContent;
            gallerySo.FindProperty("idrClient").objectReferenceValue = idrClient;
            gallerySo.ApplyModifiedProperties();

            SidePanelController controller = canvasGO.AddComponent<SidePanelController>();
            var controllerSo = new SerializedObject(controller);
            controllerSo.FindProperty("panelBody").objectReferenceValue = panelBodyGO;
            controllerSo.FindProperty("chatPage").objectReferenceValue = chatPage;
            controllerSo.FindProperty("galleryPage").objectReferenceValue = galleryPage;
            controllerSo.FindProperty("settingsPage").objectReferenceValue = settingsPage;
            controllerSo.FindProperty("galleryMenu").objectReferenceValue = gallery;
            controllerSo.FindProperty("menuButton").objectReferenceValue = menuButton;
            controllerSo.FindProperty("titleLabel").objectReferenceValue = titleLabel;
            controllerSo.FindProperty("drawer").objectReferenceValue = drawer;
            controllerSo.FindProperty("drawerScrimButton").objectReferenceValue = drawerScrimButton;
            controllerSo.FindProperty("chatMenuButton").objectReferenceValue = chatMenuButton;
            controllerSo.FindProperty("galleryMenuButton").objectReferenceValue = galleryMenuButton;
            controllerSo.FindProperty("settingsMenuButton").objectReferenceValue = settingsMenuButton;
            controllerSo.FindProperty("grabHandleImage").objectReferenceValue = dragBarHandle;
            controllerSo.FindProperty("closeButton").objectReferenceValue = closeButton;
            controllerSo.FindProperty("closeButtonLabel").objectReferenceValue = closeButtonLabel;
            controllerSo.FindProperty("dragBarRect").objectReferenceValue = dragBarRect;
            controllerSo.ApplyModifiedProperties();

            BoxCollider grabZoneCollider = MakeGrabbable(canvasGO);
            controllerSo.FindProperty("grabZoneCollider").objectReferenceValue = grabZoneCollider;
            controllerSo.ApplyModifiedProperties();

            return controller;
        }

        // greifzone nur ueber der titelleiste, sonst kollidiert das greifen mit dem
        // klicken auf buttons/galerie zeilen weiter unten im panel
        // groesse/position folgen SidePanelController.OpenDragBarHeight - beim
        // schliessen des panels vergroessert der controller sie selbst wieder (ApplyOpen)
        private static BoxCollider MakeGrabbable(GameObject canvasGO)
        {
            var grabInteractable = canvasGO.GetComponent<XRGrabInteractable>();
            if (grabInteractable == null) grabInteractable = canvasGO.AddComponent<XRGrabInteractable>();

            grabInteractable.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grabInteractable.trackRotation = false;
            grabInteractable.throwOnDetach = false;

            var rigidbody = canvasGO.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.isKinematic = true;
                rigidbody.useGravity = false;
            }

            float dragBarHeight = SidePanelController.OpenDragBarHeight;
            Vector3 grabCenterLocal = new Vector3(-CloseButtonReserve / 2f, 450f - dragBarHeight / 2f, 0f);

            // ohne eigenen attach-punkt snappt XRGrabInteractable beim greifen auf den
            // canvas-pivot (die MITTE des panels) statt auf die stelle, auf die man gezeigt
            // hat - sichtbarer "sprung" im moment des greifens. eigener punkt an der
            // dragbar-position (deckungsgleich mit der GrabZone) behebt das
            var existingAttach = canvasGO.transform.Find("GrabAttachPoint");
            Transform attachPoint = existingAttach != null ? existingAttach : new GameObject("GrabAttachPoint").transform;
            attachPoint.SetParent(canvasGO.transform, false);
            attachPoint.localPosition = grabCenterLocal;
            attachPoint.localRotation = Quaternion.identity;
            grabInteractable.attachTransform = attachPoint;

            var existingZone = canvasGO.transform.Find("GrabZone");
            if (existingZone != null) return existingZone.GetComponent<BoxCollider>();

            var grabZone = new GameObject("GrabZone", typeof(BoxCollider));
            grabZone.transform.SetParent(canvasGO.transform, false);
            var collider = grabZone.GetComponent<BoxCollider>();
            // deckt die DragBar ab, aber nicht den CloseButton ganz rechts (der braucht
            // klicken statt greifen) - siehe CloseButtonWidth in CreateTitleBar
            float grabWidth = 700f - CloseButtonReserve;
            collider.center = grabCenterLocal;
            collider.size = new Vector3(grabWidth, dragBarHeight, 80f);
            return collider;
        }

        private const float DragBarGap = 10f;
        private const float HeaderHeight = 90f;
        private const float ContentGap = 20f;
        private const float CloseButtonReserve = 44f; // platz rechts auf der DragBar fuer den X button
        private const float ContrastRowGap = 10f;
        private const float ContrastRowHeight = 50f;

        // dragbar (34) + luecke (10) + kopfzeile (90) + luecke (10) + kontrastzeile (50) +
        // luecke (20) - bleibt auf beiden seiten (chat/galerie) gleich sichtbar
        private const float ContentTopOffset = 214f;

        // DragBar/CloseButton bleiben immer sichtbar (canvasParent), rest liegt in
        // bodyParent und wird beim schliessen komplett ausgeblendet
        // deckungsgleich mit dem GrabZone collider in MakeGrabbable
        private static Button CreateTitleBar(Transform canvasParent, Transform bodyParent, out Text titleLabel,
            out Image dragBarHandle, out Button closeButton, out Text closeButtonLabel, out RectTransform dragBarRectOut)
        {
            // eigene duenne leiste ueber die volle breite ganz oben, nur zum verschieben -
            // getrennt von titel/umschalt-knopf, wie eine echte fenster-titelleiste bei
            // windows/macos statt an einen bestimmten text gebunden zu sein
            var dragBarGO = new GameObject("DragBar", typeof(Image));
            dragBarGO.transform.SetParent(canvasParent, false);
            var dragBarRect = dragBarGO.GetComponent<RectTransform>();
            dragBarRect.anchorMin = new Vector2(0f, 1f);
            dragBarRect.anchorMax = new Vector2(1f, 1f);
            dragBarRect.pivot = new Vector2(0.5f, 1f);
            dragBarRect.anchoredPosition = Vector2.zero;
            dragBarRect.sizeDelta = new Vector2(0f, SidePanelController.OpenDragBarHeight);
            dragBarRectOut = dragBarRect;
            var dragBarImage = dragBarGO.GetComponent<Image>();
            dragBarImage.sprite = UITheme.RoundedSprite(12);
            dragBarImage.type = Image.Type.Sliced;
            dragBarImage.color = UITheme.AccentSoft;
            dragBarImage.raycastTarget = false; // greifen laeuft ueber den 3d collider, nicht ui klick
            dragBarHandle = dragBarImage;

            // kleiner mittiger balken als griff symbol, wie der wisch griff unten bei ios sheets
            var indicatorGO = new GameObject("DragIndicator", typeof(Image));
            indicatorGO.transform.SetParent(dragBarGO.transform, false);
            var indicatorRect = indicatorGO.GetComponent<RectTransform>();
            indicatorRect.anchorMin = indicatorRect.anchorMax = new Vector2(0.5f, 0.5f);
            indicatorRect.pivot = new Vector2(0.5f, 0.5f);
            indicatorRect.anchoredPosition = Vector2.zero;
            indicatorRect.sizeDelta = new Vector2(70f, 6f);
            var indicatorImage = indicatorGO.GetComponent<Image>();
            indicatorImage.sprite = UITheme.RoundedSprite(3);
            indicatorImage.type = Image.Type.Sliced;
            indicatorImage.color = UITheme.Accent;
            indicatorImage.raycastTarget = false;

            // X zum schliessen, ganz rechts auf der DragBar - bleibt immer da, damit man
            // das panel nach dem schliessen ueber denselben knopf (dann als "+") wiederfindet
            var closeGO = new GameObject("CloseButton", typeof(Image), typeof(Button));
            closeGO.transform.SetParent(dragBarGO.transform, false);
            var closeRect = closeGO.GetComponent<RectTransform>();
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 0.5f);
            closeRect.pivot = new Vector2(1f, 0.5f);
            closeRect.anchoredPosition = new Vector2(-6f, 0f);
            closeRect.sizeDelta = new Vector2(SidePanelController.OpenDragBarHeight - 6f, SidePanelController.OpenDragBarHeight - 6f);
            var closeImage = closeGO.GetComponent<Image>();
            closeImage.sprite = UITheme.RoundedSprite(8);
            closeImage.type = Image.Type.Sliced;
            closeImage.color = Color.white;
            var closeBtn = closeGO.GetComponent<Button>();
            closeBtn.targetGraphic = closeImage;
            var closeColors = closeBtn.colors;
            closeColors.normalColor = UITheme.Surface;
            closeColors.highlightedColor = UITheme.SurfaceHover;
            closeColors.pressedColor = UITheme.SurfacePressed;
            closeColors.fadeDuration = 0.08f;
            closeBtn.colors = closeColors;
            closeButton = closeBtn;

            var closeLabelGO = new GameObject("Label", typeof(Text));
            closeLabelGO.transform.SetParent(closeGO.transform, false);
            EditorSetupCommon.StretchFull(closeLabelGO.GetComponent<RectTransform>());
            var closeLabelText = closeLabelGO.GetComponent<Text>();
            closeLabelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            closeLabelText.fontSize = 18;
            closeLabelText.fontStyle = FontStyle.Bold;
            closeLabelText.color = UITheme.TextPrimary;
            closeLabelText.alignment = TextAnchor.MiddleCenter;
            closeLabelText.text = "X";
            closeButtonLabel = closeLabelText;

            float headerTop = SidePanelController.OpenDragBarHeight + DragBarGap;

            // volle breite statt frueher nur bis zum toggle-button, der faellt jetzt weg
            // 20px marge links UND rechts, gleiche breite wie ContrastRow/Seiteninhalte
            var handleGO = new GameObject("GrabHandle", typeof(Image));
            handleGO.transform.SetParent(bodyParent, false);
            var handleRect = handleGO.GetComponent<RectTransform>();
            handleRect.anchorMin = new Vector2(0f, 1f);
            handleRect.anchorMax = new Vector2(0f, 1f);
            handleRect.pivot = new Vector2(0f, 1f);
            handleRect.anchoredPosition = new Vector2(20f, -headerTop);
            handleRect.sizeDelta = new Vector2(700f - 40f, HeaderHeight);
            var handleImage = handleGO.GetComponent<Image>();
            handleImage.sprite = UITheme.RoundedSprite(20);
            handleImage.type = Image.Type.Sliced;
            handleImage.color = UITheme.Surface;
            handleImage.raycastTarget = false; // rein dekorativ, kein greifen/klicken hier

            Button menuButton = CreateMenuButton(handleGO.transform, HeaderHeight);

            var titleGO = new GameObject("Title", typeof(Text));
            titleGO.transform.SetParent(handleGO.transform, false);
            var titleRect = titleGO.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(HeaderHeight + 6f, 0f); // platz fuer den menu knopf links
            titleRect.offsetMax = new Vector2(-10f, 0f);
            var titleText = titleGO.GetComponent<Text>();
            titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleText.fontSize = 26;
            titleText.fontStyle = FontStyle.Bold;
            titleText.color = UITheme.TextPrimary;
            titleText.alignment = TextAnchor.MiddleLeft;
            titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
            titleText.verticalOverflow = VerticalWrapMode.Truncate;
            titleText.text = "Chat"; // wird von SidePanelController.Apply() ueberschrieben
            titleLabel = titleText;

            return menuButton;
        }

        // hamburger knopf links in der kopfzeile, oeffnet das Drawer statt durchzuklicken
        // drei balken statt icon-font zeichen, kein font mit passenden glyphen noetig
        private static Button CreateMenuButton(Transform parent, float rowHeight)
        {
            var buttonGO = new GameObject("MenuButton", typeof(Image), typeof(Button));
            buttonGO.transform.SetParent(parent, false);
            var buttonRect = buttonGO.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0f, 0.5f);
            buttonRect.anchorMax = new Vector2(0f, 0.5f);
            buttonRect.pivot = new Vector2(0f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(6f, 0f);
            buttonRect.sizeDelta = new Vector2(rowHeight - 12f, rowHeight - 12f);
            var buttonImage = buttonGO.GetComponent<Image>();
            buttonImage.sprite = UITheme.RoundedSprite(14);
            buttonImage.type = Image.Type.Sliced;

            var button = buttonGO.GetComponent<Button>();
            button.targetGraphic = buttonImage;
            var colors = button.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0f);
            colors.highlightedColor = UITheme.SurfaceHover;
            colors.pressedColor = UITheme.SurfacePressed;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            float barWidth = buttonRect.sizeDelta.x * 0.6f;
            foreach (float y in new[] { 9f, 0f, -9f })
            {
                var barGO = new GameObject("Bar", typeof(Image));
                barGO.transform.SetParent(buttonGO.transform, false);
                var barRect = barGO.GetComponent<RectTransform>();
                barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 0.5f);
                barRect.pivot = new Vector2(0.5f, 0.5f);
                barRect.anchoredPosition = new Vector2(0f, y);
                barRect.sizeDelta = new Vector2(barWidth, 4f);
                var barImage = barGO.GetComponent<Image>();
                barImage.sprite = UITheme.RoundedSprite(2);
                barImage.type = Image.Type.Sliced;
                barImage.color = UITheme.TextPrimary;
                barImage.raycastTarget = false;
            }

            return button;
        }

        // linkes ausklapp-menu wie bei handy-apps, ersetzt den frueheren zyklischen
        // umschalt-knopf. scrim deckt das ganze panel ab, tippen ausserhalb schliesst
        private static GameObject CreateDrawer(Transform canvasParent, out Button scrimButton,
            out Button chatButton, out Button galleryButton, out Button settingsButton)
        {
            var drawerGO = new GameObject("Drawer");
            drawerGO.transform.SetParent(canvasParent, false);
            EditorSetupCommon.StretchFull(drawerGO.AddComponent<RectTransform>());

            var scrimGO = new GameObject("Scrim", typeof(Image), typeof(Button));
            scrimGO.transform.SetParent(drawerGO.transform, false);
            EditorSetupCommon.StretchFull(scrimGO.GetComponent<RectTransform>());
            var scrimImage = scrimGO.GetComponent<Image>();
            scrimImage.color = new Color(0f, 0f, 0f, 0.55f);
            scrimButton = scrimGO.GetComponent<Button>();
            scrimButton.targetGraphic = scrimImage;
            var scrimColors = scrimButton.colors;
            scrimColors.normalColor = Color.white;
            scrimColors.highlightedColor = Color.white;
            scrimColors.pressedColor = Color.white;
            scrimColors.fadeDuration = 0f;
            scrimButton.colors = scrimColors;

            var panelGO = new GameObject("DrawerPanel", typeof(Image));
            panelGO.transform.SetParent(drawerGO.transform, false);
            var panelRect = panelGO.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;
            panelRect.sizeDelta = new Vector2(340f, 0f);
            panelGO.GetComponent<Image>().color = UITheme.Background;

            var contentGO = new GameObject("Content", typeof(VerticalLayoutGroup));
            contentGO.transform.SetParent(panelGO.transform, false);
            EditorSetupCommon.StretchFull(contentGO.GetComponent<RectTransform>());
            var layout = contentGO.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 24, 16);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.UpperLeft;

            chatButton = CreateDrawerRow(contentGO.transform, "Chat");
            galleryButton = CreateDrawerRow(contentGO.transform, "Bilder");
            settingsButton = CreateDrawerRow(contentGO.transform, "Einstellungen");

            drawerGO.SetActive(false);
            return drawerGO;
        }

        private static Button CreateDrawerRow(Transform parent, string labelText)
        {
            var rowGO = new GameObject(labelText + "MenuRow", typeof(Image), typeof(Button), typeof(LayoutElement));
            rowGO.transform.SetParent(parent, false);
            rowGO.GetComponent<LayoutElement>().preferredHeight = 68f;
            var rowImage = rowGO.GetComponent<Image>();
            rowImage.sprite = UITheme.RoundedSprite(14);
            rowImage.type = Image.Type.Sliced;

            var labelGO = new GameObject("Label", typeof(Text));
            labelGO.transform.SetParent(rowGO.transform, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(22f, 0f);
            labelRect.offsetMax = new Vector2(-10f, 0f);
            var label = labelGO.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 24;
            label.fontStyle = FontStyle.Bold;
            label.color = UITheme.TextPrimary;
            label.alignment = TextAnchor.MiddleLeft;
            label.text = labelText;

            var button = rowGO.GetComponent<Button>();
            button.targetGraphic = rowImage;
            var colors = button.colors;
            colors.normalColor = UITheme.Surface;
            colors.highlightedColor = UITheme.SurfaceHover;
            colors.pressedColor = UITheme.SurfacePressed;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            return button;
        }

        // immer sichtbare zeile unter der kopfzeile (nicht teil von chatPage/galleryPage,
        // also auf beiden seiten sichtbar) - live-regler fuer den raymarch schwellwert,
        // damit man in vr ohne editor-zugriff nachjustieren kann (VolumeContrastControl.cs)
        private static void CreateContrastRow(Transform bodyParent, out Slider thresholdSlider)
        {
            float headerTop = SidePanelController.OpenDragBarHeight + DragBarGap;
            float rowTop = headerTop + HeaderHeight + ContrastRowGap;

            var rowGO = new GameObject("ContrastRow");
            rowGO.transform.SetParent(bodyParent, false);
            var rowRect = rowGO.AddComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(0f, 1f);
            rowRect.pivot = new Vector2(0f, 1f);
            rowRect.anchoredPosition = new Vector2(20f, -rowTop);
            rowRect.sizeDelta = new Vector2(700f - 40f, ContrastRowHeight);

            var labelGO = new GameObject("Label", typeof(Text));
            labelGO.transform.SetParent(rowGO.transform, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.anchoredPosition = Vector2.zero;
            labelRect.sizeDelta = new Vector2(120f, 0f);
            var label = labelGO.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 20;
            label.color = UITheme.TextSecondary;
            label.alignment = TextAnchor.MiddleLeft;
            label.text = "Kontrast";

            var sliderGO = new GameObject("ThresholdSlider", typeof(Slider));
            sliderGO.transform.SetParent(rowGO.transform, false);
            var sliderRect = sliderGO.GetComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, 0f);
            sliderRect.anchorMax = new Vector2(1f, 1f);
            sliderRect.offsetMin = new Vector2(130f, 12f);
            sliderRect.offsetMax = new Vector2(0f, -12f);

            var bgGO = new GameObject("Background", typeof(Image));
            bgGO.transform.SetParent(sliderGO.transform, false);
            EditorSetupCommon.StretchFull(bgGO.GetComponent<RectTransform>());
            var bgImage = bgGO.GetComponent<Image>();
            bgImage.sprite = UITheme.RoundedSprite(10);
            bgImage.type = Image.Type.Sliced;
            bgImage.color = UITheme.Surface;
            bgImage.raycastTarget = false;

            var fillGO = new GameObject("Fill", typeof(Image));
            fillGO.transform.SetParent(sliderGO.transform, false);
            var fillRect = fillGO.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            var fillImage = fillGO.GetComponent<Image>();
            fillImage.sprite = UITheme.RoundedSprite(10);
            fillImage.type = Image.Type.Sliced;
            fillImage.color = UITheme.Accent;
            fillImage.raycastTarget = false;

            var handleGO = new GameObject("Handle", typeof(Image));
            handleGO.transform.SetParent(sliderGO.transform, false);
            var handleRect = handleGO.GetComponent<RectTransform>();
            handleRect.anchorMin = new Vector2(0f, 0.5f);
            handleRect.anchorMax = new Vector2(0f, 0.5f);
            handleRect.pivot = new Vector2(0.5f, 0.5f);
            handleRect.sizeDelta = new Vector2(26f, 26f);
            var handleImage = handleGO.GetComponent<Image>();
            handleImage.sprite = UITheme.RoundedSprite(13);
            handleImage.type = Image.Type.Sliced;
            handleImage.color = Color.white;

            thresholdSlider = sliderGO.GetComponent<Slider>();
            thresholdSlider.targetGraphic = handleImage;
            thresholdSlider.fillRect = fillRect;
            thresholdSlider.handleRect = handleRect;
            thresholdSlider.direction = Slider.Direction.LeftToRight;
            thresholdSlider.minValue = 0f;
            thresholdSlider.maxValue = 1f;
            // tatsaechlicher startwert kommt vom material (VolumeContrastControl.OnEnable),
            // das hier ist nur der editor-default falls die kette mal nicht greift
            thresholdSlider.value = 0.12f;
        }

        private static GameObject CreateChatPage(Transform parent, out Text contentText, out ScrollRect scrollRect)
        {
            var pageGO = new GameObject("ChatPage");
            pageGO.transform.SetParent(parent, false);
            var pageRect = pageGO.AddComponent<RectTransform>();
            pageRect.anchorMin = new Vector2(0f, 0f);
            pageRect.anchorMax = new Vector2(1f, 1f);
            pageRect.offsetMin = new Vector2(20f, 20f);
            pageRect.offsetMax = new Vector2(-20f, -ContentTopOffset);

            var scrollGO = new GameObject("ScrollView", typeof(ScrollRect));
            scrollGO.transform.SetParent(pageGO.transform, false);
            EditorSetupCommon.StretchFull(scrollGO.GetComponent<RectTransform>());

            var viewportGO = new GameObject("Viewport", typeof(Image), typeof(RectMask2D));
            viewportGO.transform.SetParent(scrollGO.transform, false);
            EditorSetupCommon.StretchFull(viewportGO.GetComponent<RectTransform>());
            viewportGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.001f);
            viewportGO.GetComponent<Image>().raycastTarget = false;

            var contentGO = new GameObject("Content", typeof(Text), typeof(ContentSizeFitter));
            contentGO.transform.SetParent(viewportGO.transform, false);
            var contentRect = contentGO.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 0f);
            contentGO.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            contentText = contentGO.GetComponent<Text>();
            contentText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            contentText.fontSize = 24;
            contentText.color = UITheme.TextSecondary;
            contentText.alignment = TextAnchor.UpperLeft;
            contentText.horizontalOverflow = HorizontalWrapMode.Wrap;
            contentText.verticalOverflow = VerticalWrapMode.Overflow;
            contentText.supportRichText = true;
            contentText.lineSpacing = 1.15f;
            contentText.text = "(noch keine Fragen gestellt)";

            scrollRect = scrollGO.GetComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.viewport = viewportGO.GetComponent<RectTransform>();
            scrollRect.content = contentRect;

            return pageGO;
        }

        private static GameObject CreateGalleryPage(Transform parent, out RectTransform listContainer)
        {
            var pageGO = new GameObject("GalleryPage");
            pageGO.transform.SetParent(parent, false);
            pageGO.SetActive(false);
            var pageRect = pageGO.AddComponent<RectTransform>();
            pageRect.anchorMin = new Vector2(0f, 0f);
            pageRect.anchorMax = new Vector2(1f, 1f);
            pageRect.offsetMin = new Vector2(20f, 20f);
            pageRect.offsetMax = new Vector2(-20f, -ContentTopOffset);

            var scrollGO = new GameObject("ScrollView", typeof(ScrollRect));
            scrollGO.transform.SetParent(pageGO.transform, false);
            EditorSetupCommon.StretchFull(scrollGO.GetComponent<RectTransform>());

            var viewportGO = new GameObject("Viewport", typeof(Image), typeof(RectMask2D));
            viewportGO.transform.SetParent(scrollGO.transform, false);
            EditorSetupCommon.StretchFull(viewportGO.GetComponent<RectTransform>());
            viewportGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.001f);
            viewportGO.GetComponent<Image>().raycastTarget = false;

            var contentGO = new GameObject("Content", typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGO.transform.SetParent(viewportGO.transform, false);
            var contentRect = contentGO.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 0f);

            var layout = contentGO.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.spacing = 8f;

            contentGO.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollRect = scrollGO.GetComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.viewport = viewportGO.GetComponent<RectTransform>();
            scrollRect.content = contentRect;

            listContainer = contentRect;
            return pageGO;
        }

        private const float SettingsRowHeight = 60f;
        private const float SettingsLabelWidth = 160f;

        // rot/gruen/blau, muss exakt zu VolumeRaymarch.shader _ChannelColor0/1/2 defaults
        // und preprocessing/convert_to_volume.py kanal-reihenfolge passen
        private static readonly Color[] ChannelAccentColors =
        {
            new Color(0.90f, 0.30f, 0.30f),
            new Color(0.35f, 0.85f, 0.40f),
            new Color(0.35f, 0.55f, 0.95f),
        };

        // eigener dritter tab neben chat/bilder - regler fuer density/cut-range-max/
        // render-modus/shading/kanal-an-aus (VolumeRenderControls.cs), zusaetzlich zum
        // immer sichtbaren threshold-regler in CreateContrastRow. gleiche
        // VerticalLayoutGroup-technik wie CreateGalleryPage, nur statische zeilen
        private static GameObject CreateSettingsPage(Transform parent, out Slider densitySlider,
            out Slider cutRangeMaxSlider, out Button renderModeButton, out Text renderModeLabel,
            out Button shadingButton, out Text shadingLabel,
            out Button channel0Button, out Text channel0Label,
            out Button channel1Button, out Text channel1Label,
            out Button channel2Button, out Text channel2Label)
        {
            var pageGO = new GameObject("SettingsPage");
            pageGO.transform.SetParent(parent, false);
            pageGO.SetActive(false);
            var pageRect = pageGO.AddComponent<RectTransform>();
            pageRect.anchorMin = new Vector2(0f, 0f);
            pageRect.anchorMax = new Vector2(1f, 1f);
            pageRect.offsetMin = new Vector2(20f, 20f);
            pageRect.offsetMax = new Vector2(-20f, -ContentTopOffset);

            var scrollGO = new GameObject("ScrollView", typeof(ScrollRect));
            scrollGO.transform.SetParent(pageGO.transform, false);
            EditorSetupCommon.StretchFull(scrollGO.GetComponent<RectTransform>());

            var viewportGO = new GameObject("Viewport", typeof(Image), typeof(RectMask2D));
            viewportGO.transform.SetParent(scrollGO.transform, false);
            EditorSetupCommon.StretchFull(viewportGO.GetComponent<RectTransform>());
            viewportGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.001f);
            viewportGO.GetComponent<Image>().raycastTarget = false;

            var contentGO = new GameObject("Content", typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGO.transform.SetParent(viewportGO.transform, false);
            var contentRect = contentGO.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 0f);

            var layout = contentGO.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.spacing = 16f;

            contentGO.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollRect = scrollGO.GetComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.viewport = viewportGO.GetComponent<RectTransform>();
            scrollRect.content = contentRect;

            densitySlider = CreateSettingsSliderRow(contentGO.transform, "Density", 0.01f, 5f, 1f);
            cutRangeMaxSlider = CreateSettingsSliderRow(contentGO.transform, "Cut Range Max", 0f, 1f, 1f);
            renderModeButton = CreateSettingsToggleRow(contentGO.transform, "Render Mode", out renderModeLabel);
            shadingButton = CreateSettingsToggleRow(contentGO.transform, "Shading", out shadingLabel);
            // nur relevant wenn das geladene volumen echte getrennte kanaele hat
            // (VolumeView.IsLiveChannels) - bei graustufen/baked-rgb bleiben sie sichtbar,
            // aber ohne wirkung (siehe VolumeRaymarch.shader SampleVoxel)
            channel0Button = CreateSettingsToggleRow(contentGO.transform, "Kanal 1", out channel0Label, ChannelAccentColors[0]);
            channel1Button = CreateSettingsToggleRow(contentGO.transform, "Kanal 2", out channel1Label, ChannelAccentColors[1]);
            channel2Button = CreateSettingsToggleRow(contentGO.transform, "Kanal 3", out channel2Label, ChannelAccentColors[2]);

            return pageGO;
        }

        // gleicher visueller aufbau wie der threshold-slider in CreateContrastRow, aber
        // als eigenstaendige zeile mit LayoutElement statt fester anchoredPosition, damit
        // die VerticalLayoutGroup der Settings-seite die stapelung uebernimmt
        private static Slider CreateSettingsSliderRow(Transform parent, string labelText, float min, float max, float defaultValue)
        {
            var rowGO = new GameObject(labelText + "Row", typeof(LayoutElement));
            rowGO.transform.SetParent(parent, false);
            rowGO.GetComponent<LayoutElement>().preferredHeight = SettingsRowHeight;
            rowGO.AddComponent<RectTransform>();

            CreateRowLabel(rowGO.transform, labelText);

            var sliderGO = new GameObject("Slider", typeof(Slider));
            sliderGO.transform.SetParent(rowGO.transform, false);
            var sliderRect = sliderGO.GetComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, 0f);
            sliderRect.anchorMax = new Vector2(1f, 1f);
            sliderRect.offsetMin = new Vector2(SettingsLabelWidth, 12f);
            sliderRect.offsetMax = new Vector2(0f, -12f);

            var bgGO = new GameObject("Background", typeof(Image));
            bgGO.transform.SetParent(sliderGO.transform, false);
            EditorSetupCommon.StretchFull(bgGO.GetComponent<RectTransform>());
            var bgImage = bgGO.GetComponent<Image>();
            bgImage.sprite = UITheme.RoundedSprite(10);
            bgImage.type = Image.Type.Sliced;
            bgImage.color = UITheme.Surface;
            bgImage.raycastTarget = false;

            var fillGO = new GameObject("Fill", typeof(Image));
            fillGO.transform.SetParent(sliderGO.transform, false);
            var fillRect = fillGO.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            var fillImage = fillGO.GetComponent<Image>();
            fillImage.sprite = UITheme.RoundedSprite(10);
            fillImage.type = Image.Type.Sliced;
            fillImage.color = UITheme.Accent;
            fillImage.raycastTarget = false;

            var handleGO = new GameObject("Handle", typeof(Image));
            handleGO.transform.SetParent(sliderGO.transform, false);
            var handleRect = handleGO.GetComponent<RectTransform>();
            handleRect.anchorMin = new Vector2(0f, 0.5f);
            handleRect.anchorMax = new Vector2(0f, 0.5f);
            handleRect.pivot = new Vector2(0.5f, 0.5f);
            handleRect.sizeDelta = new Vector2(26f, 26f);
            var handleImage = handleGO.GetComponent<Image>();
            handleImage.sprite = UITheme.RoundedSprite(13);
            handleImage.type = Image.Type.Sliced;
            handleImage.color = Color.white;

            var slider = sliderGO.GetComponent<Slider>();
            slider.targetGraphic = handleImage;
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = defaultValue;
            return slider;
        }

        // an/aus knopf statt slider, fuer render mode/shading/kanal-toggle - label wird
        // zur laufzeit von VolumeRenderControls aktualisiert (zeigt den aktuellen zustand).
        // accentColor optional (kanal-knoepfe) faerbt den knopf statt dem theme-standard,
        // rein visuelle zuordnung "dieser knopf gehoert zu diesem kanal"
        private static Button CreateSettingsToggleRow(Transform parent, string labelText, out Text buttonLabel, Color? accentColor = null)
        {
            var rowGO = new GameObject(labelText + "Row", typeof(LayoutElement));
            rowGO.transform.SetParent(parent, false);
            rowGO.GetComponent<LayoutElement>().preferredHeight = SettingsRowHeight;
            rowGO.AddComponent<RectTransform>();

            CreateRowLabel(rowGO.transform, labelText);

            var buttonGO = new GameObject("ToggleButton", typeof(Image), typeof(Button));
            buttonGO.transform.SetParent(rowGO.transform, false);
            var buttonRect = buttonGO.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0f, 0f);
            buttonRect.anchorMax = new Vector2(1f, 1f);
            buttonRect.offsetMin = new Vector2(SettingsLabelWidth, 6f);
            buttonRect.offsetMax = new Vector2(0f, -6f);
            var buttonImage = buttonGO.GetComponent<Image>();
            buttonImage.sprite = UITheme.RoundedSprite(14);
            buttonImage.type = Image.Type.Sliced;
            buttonImage.color = Color.white;

            var buttonLabelGO = new GameObject("Label", typeof(Text));
            buttonLabelGO.transform.SetParent(buttonGO.transform, false);
            EditorSetupCommon.StretchFull(buttonLabelGO.GetComponent<RectTransform>());
            buttonLabel = buttonLabelGO.GetComponent<Text>();
            buttonLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            buttonLabel.fontSize = 20;
            buttonLabel.fontStyle = FontStyle.Bold;
            buttonLabel.color = UITheme.TextPrimary;
            buttonLabel.alignment = TextAnchor.MiddleCenter;

            Color baseColor = accentColor ?? UITheme.Accent;
            var button = buttonGO.GetComponent<Button>();
            button.targetGraphic = buttonImage;
            var colors = button.colors;
            colors.normalColor = new Color(baseColor.r, baseColor.g, baseColor.b, accentColor.HasValue ? 0.35f : UITheme.AccentSoft.a);
            colors.highlightedColor = baseColor * new Color(1f, 1f, 1f, 0.35f);
            colors.pressedColor = baseColor * new Color(1f, 1f, 1f, 0.55f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            return button;
        }

        private static void CreateRowLabel(Transform rowParent, string labelText)
        {
            var labelGO = new GameObject("Label", typeof(Text));
            labelGO.transform.SetParent(rowParent, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.anchoredPosition = Vector2.zero;
            labelRect.sizeDelta = new Vector2(SettingsLabelWidth, 0f);
            var label = labelGO.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 20;
            label.color = UITheme.TextSecondary;
            label.alignment = TextAnchor.MiddleLeft;
            label.text = labelText;
        }

        private static void WireHarness(MonoBehaviour harness, SidePanelController controller)
        {
            if (harness == null) return;
            ChatPanel chatPanel = controller.GetComponent<ChatPanel>();
            var so = new SerializedObject(harness);
            so.FindProperty("chatPanel").objectReferenceValue = chatPanel;
            so.ApplyModifiedProperties();
        }

        private static void WireVolumeZoom(XRRayInteractor rightHandRay)
        {
            var volumeZoom = Object.FindFirstObjectByType<VolumeZoom>();
            if (volumeZoom == null || rightHandRay == null) return;
            var so = new SerializedObject(volumeZoom);
            so.FindProperty("rightHandRay").objectReferenceValue = rightHandRay;
            so.ApplyModifiedProperties();
        }
    }
}
