using UnityEngine;

namespace BioimageVR
{
    // laengen und volumen passend formatieren, egal ob zellen in µm oder ein kopf in mm
    // werte kommen in der einheit des geladenen bilds, siehe VolumeView.Unit
    public static class Units
    {
        public static float ToMicrometers(string unit) => unit switch
        {
            "nm" => 0.001f,
            "mm" => 1000f,
            "cm" => 10000f,
            "m" => 1000000f,
            _ => 1f,
        };

        // laenge in der naechst passenden einheit, 0.8 mm bleibt 0.8 mm, 1250 µm wird 1.25 mm
        public static string Length(float value, string unit, string format = "0.##")
        {
            if (unit == "px") return value.ToString(format) + " px";
            float um = value * ToMicrometers(unit);
            if (um < 1f) return (um * 1000f).ToString("0") + " nm";
            if (um < 1000f) return um.ToString(format) + " µm";
            if (um < 1000000f) return (um / 1000f).ToString(format) + " mm";
            return (um / 1000000f).ToString(format) + " m";
        }

        // volumen bleibt in der bildeinheit, µm³ fuer zellen, mm³ fuer organe
        public static string Volume(float value, string unit)
        {
            // organe: ein gehirn waeren sonst 1.871.800 mm³
            if (unit == "mm" && value >= 1000f) return $"{(value / 1000f).ToString(value >= 100000f ? "N0" : "0.0")} cm³";
            string format = value >= 100f ? "N0" : value >= 1f ? "0.0" : "0.000";
            return $"{value.ToString(format)} {unit}³";
        }

        public static string Plain(float value, string unit) =>
            unit == "px" ? value.ToString("0.0") : Mathf.Abs(value) >= 100f ? value.ToString("0") : value.ToString("0.0");
    }
}
