using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // knopf schaltet ein ziel-gameobject an/aus, sonst nichts - fuers hud-chat-panel
    // (siehe SetupSidePanel.CreateHudTextPanel), gleiches toggle-prinzip wie
    // VolumeMetadataPanel.Toggle, aber ohne die metadaten-spezifische refresh-logik
    public class PanelToggleButton : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private GameObject panelGO;

        private void Awake()
        {
            if (button != null) button.onClick.AddListener(Toggle);
        }

        private void Toggle()
        {
            if (panelGO != null) panelGO.SetActive(!panelGO.activeSelf);
        }
    }
}
