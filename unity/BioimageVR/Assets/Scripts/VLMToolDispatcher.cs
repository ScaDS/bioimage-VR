using System;
using System.Collections.Generic;
using UnityEngine;

namespace BioimageVR
{
    // definiert die werkzeuge, die das vlm per tool-calling aufrufen kann (siehe
    // CHATMICROSCOPY.md abschnitt 3.2), und fuehrt sie aus - bindet an die schon
    // vorhandenen regler (VolumeContrastControl, VolumeRenderControls), damit die
    // slider im panel sich mitbewegen wenn das vlm einen wert setzt. kein zweiter
    // api-rundtrip fuer eine bestaetigung - Execute() gibt direkt einen kurzen
    // deutschen bestaetigungstext zurueck, den der aufrufer ins chat-panel haengt
    public class VLMToolDispatcher : MonoBehaviour
    {
        [SerializeField] private VolumeContrastControl contrastControl;
        [SerializeField] private VolumeRenderControls renderControls;

        [Serializable] private class ThresholdArgs { public float value; }
        [Serializable] private class DensityArgs { public float value; }
        [Serializable] private class CutRangeMaxArgs { public float value; }
        [Serializable] private class RenderModeArgs { public string mode; }
        [Serializable] private class ShadingArgs { public bool on; }
        [Serializable] private class ChannelArgs { public int channel; public bool on; }

        public static readonly IReadOnlyList<ToolDefinition> Tools = new List<ToolDefinition>
        {
            new ToolDefinition
            {
                Name = "set_threshold",
                Description = "Setzt den unteren Intensitaets-Schwellwert (Kontrast) fuer die 3D-Darstellung. " +
                              "Ein hoeherer Wert blendet schwaches Signal/Hintergrundrauschen aus.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\"," +
                                  "\"description\":\"Wert zwischen 0 und 1\"}},\"required\":[\"value\"]}"
            },
            new ToolDefinition
            {
                Name = "set_density",
                Description = "Setzt wie dicht/deckend (opak) das Volumen dargestellt wird.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\"," +
                                  "\"description\":\"Wert zwischen 0.01 und 5\"}},\"required\":[\"value\"]}"
            },
            new ToolDefinition
            {
                Name = "set_cut_range_max",
                Description = "Setzt den oberen Intensitaets-Schwellwert, blendet sehr helle/ueberbelichtete Bereiche aus.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\"," +
                                  "\"description\":\"Wert zwischen 0 und 1\"}},\"required\":[\"value\"]}"
            },
            new ToolDefinition
            {
                Name = "set_render_mode",
                Description = "Wechselt den Render-Modus zwischen durchscheinender Darstellung (translucent) " +
                              "und Maximum-Intensity-Projection (mip, zeigt pro Sichtstrahl nur den hellsten Punkt).",
                ParametersJson = "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\"," +
                                  "\"enum\":[\"translucent\",\"mip\"]}},\"required\":[\"mode\"]}"
            },
            new ToolDefinition
            {
                Name = "set_shading",
                Description = "Schaltet das Gradient-Shading (Tiefen-/Beleuchtungswirkung) an oder aus.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{\"on\":{\"type\":\"boolean\"}},\"required\":[\"on\"]}"
            },
            new ToolDefinition
            {
                Name = "toggle_channel",
                Description = "Schaltet einen einzelnen Fluoreszenz-Farbkanal (1, 2 oder 3) im 3D-Volumen an oder " +
                              "aus. Nur wirksam wenn das geladene Bild mehrere getrennte Kanaele hat (Kanal 1 ist " +
                              "rot, Kanal 2 gruen, Kanal 3 blau eingefaerbt).",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                                  "\"channel\":{\"type\":\"integer\",\"enum\":[1,2,3]}," +
                                  "\"on\":{\"type\":\"boolean\"}}," +
                                  "\"required\":[\"channel\",\"on\"]}"
            },
        };

        public string Execute(ToolCall call)
        {
            try
            {
                switch (call.Name)
                {
                    case "set_threshold":
                    {
                        var args = JsonUtility.FromJson<ThresholdArgs>(call.ArgumentsJson);
                        contrastControl?.SetThresholdExternal(args.value);
                        return $"Threshold auf {args.value:0.00} gesetzt.";
                    }
                    case "set_density":
                    {
                        var args = JsonUtility.FromJson<DensityArgs>(call.ArgumentsJson);
                        renderControls?.SetDensityExternal(args.value);
                        return $"Density auf {args.value:0.00} gesetzt.";
                    }
                    case "set_cut_range_max":
                    {
                        var args = JsonUtility.FromJson<CutRangeMaxArgs>(call.ArgumentsJson);
                        renderControls?.SetCutRangeMaxExternal(args.value);
                        return $"Cut Range Max auf {args.value:0.00} gesetzt.";
                    }
                    case "set_render_mode":
                    {
                        var args = JsonUtility.FromJson<RenderModeArgs>(call.ArgumentsJson);
                        bool mip = args.mode == "mip";
                        renderControls?.SetRenderModeExternal(mip);
                        return $"Render Mode: {(mip ? "MIP" : "Translucent")}.";
                    }
                    case "set_shading":
                    {
                        var args = JsonUtility.FromJson<ShadingArgs>(call.ArgumentsJson);
                        renderControls?.SetShadingExternal(args.on);
                        return $"Shading: {(args.on ? "an" : "aus")}.";
                    }
                    case "toggle_channel":
                    {
                        var args = JsonUtility.FromJson<ChannelArgs>(call.ArgumentsJson);
                        int index = args.channel - 1; // vlm zaehlt 1..3, intern 0..2
                        if (index < 0 || index > 2) return $"Ungueltiger Kanal: {args.channel}";
                        renderControls?.SetChannelExternal(index, args.on);
                        return $"Kanal {args.channel}: {(args.on ? "an" : "aus")}.";
                    }
                    default:
                        return $"Unbekanntes Werkzeug: {call.Name}";
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"VLMToolDispatcher: Fehler bei {call.Name}: {e.Message}");
                return $"Fehler beim Ausfuehren von {call.Name}.";
            }
        }
    }
}
