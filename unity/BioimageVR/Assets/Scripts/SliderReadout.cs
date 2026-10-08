using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BioimageVR
{
    // zahl rechts ueber einem regler, folgt dem wert live
    public class SliderReadout : MonoBehaviour
    {
        private Slider slider;
        private TMP_Text text;
        private string format = "0.00";

        public void Init(Slider target, TMP_Text label, string numberFormat = "0.00")
        {
            if (slider != null) slider.onValueChanged.RemoveListener(Show);
            slider = target;
            text = label;
            format = numberFormat;
            slider.onValueChanged.AddListener(Show);
            Show(slider.value);
        }

        // SetValueWithoutNotify loest kein event aus, deshalb auch laufend abgleichen
        private void LateUpdate()
        {
            if (slider != null) Show(slider.value);
        }

        private void Show(float value)
        {
            if (text == null) return;
            string s = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            if (text.text != s) text.text = s;
        }
    }
}
