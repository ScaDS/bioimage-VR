using System;
using UnityEngine;

namespace BioimageVR
{
    // welches werkzeug gerade aktiv ist, siehe werkzeugleiste links am volumen
    // View schaut nur, Measure misst, Segment korrigiert, Assistant fragt das vlm
    // schaltet mess tool und korrektur modi passend mit, damit nie zwei werkzeuge
    // gleichzeitig auf denselben klick reagieren
    public class ToolController : MonoBehaviour
    {
        public enum Tool { View, Measure, Segment, Assistant }

        [SerializeField] private MeasureTool measureTool;
        [SerializeField] private LabelEditor labelEditor;

        public Tool Current { get; private set; } = Tool.View;
        public event Action<Tool> ToolChanged;

        // beim zurueckkommen zum messen wieder derselbe modus
        private MeasureTool.Mode lastMeasureMode = MeasureTool.Mode.Ruler;

        public void SetTool(Tool tool)
        {
            if (tool == Current) return;

            if (Current == Tool.Measure && measureTool != null && measureTool.CurrentMode != MeasureTool.Mode.Off)
                lastMeasureMode = measureTool.CurrentMode;
            if (Current == Tool.Segment && labelEditor != null)
            {
                labelEditor.CancelMerge();
                labelEditor.CancelSplit();
            }

            Current = tool;
            if (measureTool != null)
                measureTool.SetMode(tool == Tool.Measure ? lastMeasureMode : MeasureTool.Mode.Off);
            ToolChanged?.Invoke(tool);
        }

        public void SetMeasureMode(MeasureTool.Mode mode)
        {
            lastMeasureMode = mode;
            if (Current != Tool.Measure) SetTool(Tool.Measure);
            else if (measureTool != null) measureTool.SetMode(mode);
        }
    }
}
