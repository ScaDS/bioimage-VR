using System;
using UnityEngine;

namespace BioimageVR
{
    // aktives werkzeug, nie zwei gleichzeitig auf denselben klick
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
