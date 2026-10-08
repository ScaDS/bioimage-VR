using System.IO;
using BioimageVR;
using TMPro;
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
    // richtet die ganze oberflaeche ein, abgestimmtes mockup vom 30.09.
    // werkzeuge und karte am volumen (VolumeToolsUI), seitenpanel mit tabs (SidePanelController),
    // massstab und achsen die dem kopf traege folgen. die inhalte bauen die komponenten zur
    // laufzeit selbst, hier kommen nur huelle, strahlen und verweise dazu
    // menu: BioimageVR Setup Side Panel
    public static class SetupSidePanel
    {
        private const string ScenePath = "Assets/Scenes/Phase1.unity";
        private static readonly Vector3 LocalOffset = new Vector3(0.45f, -0.1f, 1.0f);
        private static readonly Vector2 PanelSize = new Vector2(700f, 900f);
        private const float CanvasScale = 0.00085f;

        [MenuItem("BioimageVR/Setup Side Panel")]
        public static void Run()
        {
            // schriften zuerst, beim allerersten lauf muss tmp noch importiert werden
            if (!SetupFonts.Run()) return;

            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath && File.Exists(ScenePath))
                scene = EditorSceneManager.OpenScene(ScenePath);

            var volumeView = Object.FindFirstObjectByType<VolumeView>();
            if (volumeView == null)
            {
                Debug.LogError("BioimageVR: kein VolumeView in der Szene, erst Setup Phase 1 laufen lassen.");
                return;
            }

            AllowInsecureHttp();
            EnsureEventSystem();
            XRRayInteractor leftHandRay = EnsureHandRay("LeftHand Ray", "LeftHand");
            XRRayInteractor rightHandRay = EnsureHandRay("RightHand Ray", "RightHand");
            RemoveStale();

            // werkzeuge am volumen
            WireRay(Ensure<VolumeZoom>(volumeView), "rightHandRay", rightHandRay);
            WireRay(Ensure<VolumeRotate>(volumeView), "leftHandRay", leftHandRay);
            WireRays(Ensure<VolumeFly>(volumeView), leftHandRay, rightHandRay);
            MeasureTool measureTool = WireMeasureTool(volumeView, leftHandRay, rightHandRay);
            CellInspector cellInspector = Ensure<CellInspector>(volumeView);
            WireRays(cellInspector, leftHandRay, rightHandRay);
            SplitPlaneTool splitPlaneTool = WireSplitPlaneTool(volumeView, leftHandRay, rightHandRay);
            LabelEditor labelEditor = Ensure<LabelEditor>(volumeView);
            ToolController tools = Ensure<ToolController>(volumeView);
            Set(tools, ("measureTool", measureTool), ("labelEditor", labelEditor));

            SidePanelController panel = BuildPanel(volumeView, cellInspector, labelEditor);
            var chatPanel = panel.GetComponent<ChatPanel>();
            Set(labelEditor, ("vrBridge", panel.GetComponent<VrBridge>()), ("splitPlaneTool", splitPlaneTool));

            VolumeToolsUI toolsUI = Ensure<VolumeToolsUI>(volumeView);
            Set(toolsUI,
                ("volumeView", volumeView),
                ("tools", tools),
                ("measureTool", measureTool),
                ("labelEditor", labelEditor),
                ("renderControls", panel.GetComponent<VolumeRenderControls>()),
                ("volumeZoom", volumeView.GetComponent<VolumeZoom>()),
                ("volumeRotate", volumeView.GetComponent<VolumeRotate>()),
                ("sidePanel", panel),
                ("chatPanel", chatPanel),
                ("voice", Object.FindFirstObjectByType<VoiceVLMHarness>()));

            // vlm: antworten in den chat tab, tool calls auf die neuen regler
            SetIfPresent(Object.FindFirstObjectByType<VLMTestHarness>(), ("chatPanel", chatPanel));
            SetIfPresent(Object.FindFirstObjectByType<VoiceVLMHarness>(), ("chatPanel", chatPanel));
            SetIfPresent(Object.FindFirstObjectByType<VLMToolDispatcher>(),
                ("contrastControl", panel.GetComponent<VolumeContrastControl>()),
                ("renderControls", panel.GetComponent<VolumeRenderControls>()));

            CreateHudStrip(volumeView);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("BioimageVR: UI setup complete. Tool rail left of the volume (View, Measure, Segment, Assistant), " +
                      "options below it, selection card on the right. Side panel with tabs Images, Cells, Display, Chat, " +
                      "grab the bar on top to move it. X or A clicks everywhere, the right stick zooms, the left stick rotates. " +
                      "Server address for IDR and napari: IdrClient on SideCanvas.");
        }

        // allgemeine helfer

        private static T Ensure<T>(Component host) where T : Component
        {
            var component = host.GetComponent<T>();
            return component != null ? component : host.gameObject.AddComponent<T>();
        }

        private static void Set(Object target, params (string name, Object value)[] fields)
        {
            if (target == null) return;
            var so = new SerializedObject(target);
            foreach (var (name, value) in fields)
            {
                SerializedProperty prop = so.FindProperty(name);
                if (prop == null) Debug.LogWarning($"BioimageVR: {target.GetType().Name}.{name} nicht gefunden");
                else prop.objectReferenceValue = value;
            }
            so.ApplyModifiedProperties();
        }

        private static void SetIfPresent(Object target, params (string name, Object value)[] fields)
        {
            if (target != null) Set(target, fields);
        }

        private static void WireRay(Object target, string field, XRRayInteractor ray)
        {
            if (ray != null) Set(target, (field, ray));
        }

        private static void WireRays(Object target, XRRayInteractor left, XRRayInteractor right)
        {
            if (left != null) Set(target, ("leftHandRay", left));
            if (right != null) Set(target, ("rightHandRay", right));
        }

        // szene grundlagen

        // ohne das blockiert UnityWebRequest jedes http://, IdrClient und VrBridge sprechen
        // aber bewusst klartext mit dem lokalen upload_server.py
        private static void AllowInsecureHttp()
        {
            if (PlayerSettings.insecureHttpOption == InsecureHttpOption.AlwaysAllowed) return;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            AssetDatabase.SaveAssets();
        }

        private static void EnsureEventSystem()
        {
            var eventSystem = Object.FindFirstObjectByType<EventSystem>();
            if (eventSystem == null) eventSystem = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
            if (eventSystem.GetComponent<XRUIInputModule>() == null) eventSystem.gameObject.AddComponent<XRUIInputModule>();
        }

        // reste aelterer setups, alte getrennte panels und das kamera hud mit ecken icons
        // "Canvas" ist der alte test canvas mit schnittbild vorschau und vlm antworttext
        // der phase 2 test setzt sonst bei jedem bild eine rote testkugel ins volumen,
        // ganzes objekt aus weil er in Awake arbeitet, das laeuft auch bei abgeschalteter
        // komponente. im inspector wieder anschaltbar
        private static void RemoveStale()
        {
            foreach (string name in new[] { "ChatCanvas", "GalleryCanvas", "HudCanvas", "ScaleBarHud", "AxisGizmo", "HudStrip", "Canvas" })
            {
                var go = GameObject.Find(name);
                if (go != null) Object.DestroyImmediate(go);
            }
            var markerDemo = Object.FindFirstObjectByType<Phase2MarkerDemo>();
            if (markerDemo != null) markerDemo.gameObject.SetActive(false);
        }

        // ein knopf fuer alles wie ein mausklick: primaerknopf klickt ui, gehalten auf der griffleiste
        // verschiebt er das panel. stick scrollt das scrollrect unter dem strahl
        private static XRRayInteractor EnsureHandRay(string rayName, string handTag)
        {
            var rayGO = GameObject.Find(rayName);
            if (rayGO == null)
            {
                var cameraOffset = GameObject.Find("Camera Offset");
                if (cameraOffset == null)
                {
                    Debug.LogError("BioimageVR: 'Camera Offset' nicht gefunden, erst Setup Phase 1 Scene laufen lassen.");
                    return null;
                }
                rayGO = new GameObject(rayName, typeof(LineRenderer));
                rayGO.transform.SetParent(cameraOffset.transform, false);
                rayGO.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
                rayGO.AddComponent<XRInteractorLineVisual>();
            }

            // pointerposition gibts nur bei echten controllern, der simulator kennt nur deviceposition
            var fallback = rayGO.GetComponent<HandRayPointerFallback>();
            if (fallback == null) fallback = rayGO.AddComponent<HandRayPointerFallback>();
            fallback.HandTag = handTag;

            var lineRenderer = rayGO.GetComponent<LineRenderer>();
            if (lineRenderer != null)
            {
                if (lineRenderer.sharedMaterial == null) lineRenderer.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
                lineRenderer.sharedMaterial.color = UITheme.RayColor;
            }
            // XRInteractorLineVisual faerbt den strahl sonst rot sobald er ins leere zeigt
            var lineVisual = rayGO.GetComponent<XRInteractorLineVisual>();
            if (lineVisual != null)
            {
                var gradient = new Gradient
                {
                    colorKeys = new[] { new GradientColorKey(UITheme.RayColor, 0f), new GradientColorKey(UITheme.RayColor, 1f) },
                    alphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
                };
                lineVisual.validColorGradient = gradient;
                lineVisual.invalidColorGradient = gradient;
                lineVisual.blockedColorGradient = gradient;
            }

            var rayInteractor = rayGO.GetComponent<XRRayInteractor>();
            if (rayInteractor == null) rayInteractor = rayGO.AddComponent<XRRayInteractor>();

            var clickAction = new InputAction(type: InputActionType.Button, binding: $"<XRController>{{{handTag}}}/primaryButton");
            rayInteractor.uiPressInput.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
            rayInteractor.uiPressInput.inputActionPerformed = clickAction;
            rayInteractor.uiPressInput.inputActionValue = clickAction;
            rayInteractor.selectInput.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
            rayInteractor.selectInput.inputActionPerformed = clickAction;
            rayInteractor.selectInput.inputActionValue = clickAction;

            var scrollAction = new InputAction(type: InputActionType.Value, binding: $"<XRController>{{{handTag}}}/primary2DAxis", expectedControlType: "Vector2");
            rayInteractor.uiScrollInput.inputSourceMode = XRInputValueReader.InputSourceMode.InputAction;
            rayInteractor.uiScrollInput.inputAction = scrollAction;
            return rayInteractor;
        }

        // werkzeuge

        // linienmaterial hier im editor erzeugt, damit es mit in der szene landet
        private static MeasureTool WireMeasureTool(VolumeView volumeView, XRRayInteractor left, XRRayInteractor right)
        {
            MeasureTool tool = Ensure<MeasureTool>(volumeView);
            var so = new SerializedObject(tool);
            if (so.FindProperty("lineMaterial").objectReferenceValue == null)
                so.FindProperty("lineMaterial").objectReferenceValue = new Material(Shader.Find("Sprites/Default")) { name = "MeasureLine" };
            so.FindProperty("accentColor").colorValue = UITheme.Accent;
            so.ApplyModifiedProperties();
            WireRays(tool, left, right);
            return tool;
        }

        private static SplitPlaneTool WireSplitPlaneTool(VolumeView volumeView, XRRayInteractor left, XRRayInteractor right)
        {
            SplitPlaneTool tool = Ensure<SplitPlaneTool>(volumeView);
            var so = new SerializedObject(tool);
            if (so.FindProperty("planeMaterial").objectReferenceValue == null)
                so.FindProperty("planeMaterial").objectReferenceValue = new Material(Shader.Find("Sprites/Default")) { name = "SplitPlane" };
            so.FindProperty("planeColor").colorValue = new Color(UITheme.Accent.r, UITheme.Accent.g, UITheme.Accent.b, 0.35f);
            so.ApplyModifiedProperties();
            WireRays(tool, left, right);
            return tool;
        }

        // seitenpanel

        private static SidePanelController BuildPanel(VolumeView volumeView, CellInspector cellInspector, LabelEditor labelEditor)
        {
            Vector3? preservedPosition = null;
            Quaternion? preservedRotation = null;
            string preservedServerBaseUrl = null;

            // immer frisch bauen, aber an der alten stelle lassen und die server adresse behalten
            var existing = Object.FindFirstObjectByType<SidePanelController>();
            if (existing != null)
            {
                preservedPosition = existing.transform.position;
                preservedRotation = existing.transform.rotation;
                var oldIdrClient = existing.GetComponent<IdrClient>();
                if (oldIdrClient != null)
                    preservedServerBaseUrl = new SerializedObject(oldIdrClient).FindProperty("serverBaseUrl").stringValue;
                Object.DestroyImmediate(existing.gameObject);
            }

            var canvasGO = new GameObject("SideCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam != null) canvas.worldCamera = cam;
            var rect = canvasGO.GetComponent<RectTransform>();
            rect.sizeDelta = PanelSize;
            rect.localScale = Vector3.one * CanvasScale;
            if (preservedPosition.HasValue)
            {
                rect.position = preservedPosition.Value;
                rect.rotation = preservedRotation.Value;
            }
            else
            {
                EditorSetupCommon.PlaceInFrontOfCamera(rect, LocalOffset);
            }

            var chatPanel = canvasGO.AddComponent<ChatPanel>();
            var idrClient = canvasGO.AddComponent<IdrClient>();
            if (!string.IsNullOrEmpty(preservedServerBaseUrl))
            {
                var idrSo = new SerializedObject(idrClient);
                idrSo.FindProperty("serverBaseUrl").stringValue = preservedServerBaseUrl;
                idrSo.ApplyModifiedProperties();
            }
            Set(canvasGO.AddComponent<VrBridge>(), ("idrClient", idrClient), ("volumeView", volumeView), ("chatPanel", chatPanel));
            var gallery = canvasGO.AddComponent<VolumeGalleryMenu>();
            Set(gallery, ("volumeView", volumeView), ("idrClient", idrClient));
            var contrast = canvasGO.AddComponent<VolumeContrastControl>();
            Set(contrast, ("volumeView", volumeView));
            var render = canvasGO.AddComponent<VolumeRenderControls>();
            Set(render, ("volumeView", volumeView));
            var segmentation = canvasGO.AddComponent<SegmentationControls>();
            Set(segmentation, ("volumeView", volumeView));
            var metadata = canvasGO.AddComponent<VolumeMetadataPanel>();
            Set(metadata, ("volumeView", volumeView));
            var renderQuality = canvasGO.AddComponent<RenderQuality>();

            var controller = canvasGO.AddComponent<SidePanelController>();
            BoxCollider grabZone = MakeGrabbable(canvasGO);
            Set(controller,
                ("volumeView", volumeView),
                ("cellInspector", cellInspector),
                ("labelEditor", labelEditor),
                ("chatPanel", chatPanel),
                ("gallery", gallery),
                ("contrast", contrast),
                ("render", render),
                ("segmentation", segmentation),
                ("metadata", metadata),
                ("renderQuality", renderQuality),
                ("grabZoneCollider", grabZone));
            return controller;
        }

        // greifzone nur ueber der griffleiste oben, sonst kollidiert greifen mit klicken im panel
        // groesse folgt SidePanelController.OpenDragBarHeight, zugeklappt vergroessert er sie selbst
        private static BoxCollider MakeGrabbable(GameObject canvasGO)
        {
            var grab = canvasGO.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackRotation = false;
            grab.throwOnDetach = false;
            var body = canvasGO.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true;
                body.useGravity = false;
            }

            float barHeight = SidePanelController.OpenDragBarHeight;
            float closeReserve = barHeight;
            var center = new Vector3(-closeReserve / 2f, PanelSize.y / 2f - barHeight / 2f, 0f);

            // eigener haltepunkt an der griffleiste, sonst springt das panel beim greifen auf seine mitte
            var attach = new GameObject("GrabAttachPoint").transform;
            attach.SetParent(canvasGO.transform, false);
            attach.localPosition = center;
            grab.attachTransform = attach;

            var zone = new GameObject("GrabZone", typeof(BoxCollider));
            zone.transform.SetParent(canvasGO.transform, false);
            var collider = zone.GetComponent<BoxCollider>();
            // spart den schliessen knopf rechts aus, der braucht klicken statt greifen
            collider.center = center;
            collider.size = new Vector3(PanelSize.x - closeReserve, barHeight, 80f);
            return collider;
        }

        // massstab und achsen

        // unten links im blickfeld, folgt dem kopf traege statt starr (LazyFollow)
        private static void CreateHudStrip(VolumeView volumeView)
        {
            var hud = new GameObject("HudStrip");
            hud.AddComponent<LazyFollow>().Init(new Vector3(-0.2f, -0.36f, 0.9f));

            var gizmo = new GameObject("AxisGizmo");
            gizmo.transform.SetParent(hud.transform, false);
            gizmo.transform.localPosition = new Vector3(-0.06f, 0.01f, 0f);
            gizmo.transform.localScale = Vector3.one * 0.045f;
            CreateAxis(gizmo.transform, "X", Vector3.right, new Color(1f, 0.42f, 0.42f));
            CreateAxis(gizmo.transform, "Y", Vector3.up, new Color(0.24f, 0.84f, 0.55f));
            CreateAxis(gizmo.transform, "Z", Vector3.forward, new Color(0.42f, 0.6f, 1f));
            Set(gizmo.AddComponent<VolumeAxisLabels>(), ("volumeTransform", volumeView.transform));

            CreateScaleBar(hud.transform, volumeView);
        }

        // pfeil aus zylindern, unity hat keinen kegel, ein dickerer kurzer zylinder als spitze
        private static void CreateAxis(Transform parent, string name, Vector3 direction, Color color)
        {
            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, direction);
            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            shaft.name = $"Axis{name}Shaft";
            Object.DestroyImmediate(shaft.GetComponent<Collider>());
            shaft.transform.SetParent(parent, false);
            shaft.transform.localPosition = direction * 0.4f;
            shaft.transform.localRotation = rotation;
            shaft.transform.localScale = new Vector3(0.05f, 0.4f, 0.05f);
            ApplyUnlitColor(shaft, color);

            var tip = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            tip.name = $"Axis{name}Tip";
            Object.DestroyImmediate(tip.GetComponent<Collider>());
            tip.transform.SetParent(parent, false);
            tip.transform.localPosition = direction * 0.9f;
            tip.transform.localRotation = rotation;
            tip.transform.localScale = new Vector3(0.12f, 0.1f, 0.12f);
            ApplyUnlitColor(tip, color);

            TextMeshPro label = CreateWorldText(parent, name, direction * 1.2f, 3.2f, color, UITheme.FontRole.Bold);
            label.gameObject.AddComponent<BillboardText>();
        }

        // balkenlaenge bleibt fix, nur die beschriftung aendert sich live beim zoomen
        private static void CreateScaleBar(Transform parent, VolumeView volumeView)
        {
            const float barLength = 0.1f;
            const int segments = 4;
            const float thickness = 0.005f;
            float segmentLength = barLength / segments;

            var bar = new GameObject("ScaleBar");
            bar.transform.SetParent(parent, false);
            bar.transform.localPosition = new Vector3(0.05f, 0f, 0f);

            for (int i = 0; i < segments; i++)
            {
                var seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seg.name = $"Segment{i}";
                Object.DestroyImmediate(seg.GetComponent<Collider>());
                seg.transform.SetParent(bar.transform, false);
                seg.transform.localPosition = new Vector3(-barLength / 2f + segmentLength * (i + 0.5f), 0f, 0f);
                seg.transform.localScale = new Vector3(segmentLength, thickness, thickness);
                ApplyUnlitColor(seg, i % 2 == 0 ? UITheme.TextPrimary : UITheme.Surface);
            }

            CreateWorldText(bar.transform, "0", new Vector3(-barLength / 2f, -0.014f, 0f), 0.14f, UITheme.TextSecondary, UITheme.FontRole.Mono);
            TextMeshPro value = CreateWorldText(bar.transform, "", new Vector3(barLength / 2f, -0.014f, 0f), 0.14f,
                UITheme.TextPrimary, UITheme.FontRole.MonoMedium);

            Set(bar.AddComponent<VolumeScaleBar>(),
                ("volumeTransform", volumeView.transform),
                ("volumeView", volumeView),
                ("label", value));
            var so = new SerializedObject(bar.GetComponent<VolumeScaleBar>());
            so.FindProperty("barWorldLength").floatValue = barLength;
            so.ApplyModifiedProperties();
        }

        // 3d tmp text, groesse in welteinheiten wie bei tmp ueblich (10 = eine einheit hoch)
        private static TextMeshPro CreateWorldText(Transform parent, string text, Vector3 localPosition, float fontSize,
            Color color, UITheme.FontRole role)
        {
            var go = new GameObject($"Label{text}", typeof(RectTransform), typeof(TextMeshPro));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            var tmp = go.GetComponent<TextMeshPro>();
            TMP_FontAsset font = UITheme.Font(role);
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.rectTransform.sizeDelta = new Vector2(fontSize * 0.6f, fontSize * 0.15f);
            return tmp;
        }

        private static void ApplyUnlitColor(GameObject go, Color color)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetColor("_BaseColor", color);
            go.GetComponent<Renderer>().sharedMaterial = mat;
        }
    }
}
