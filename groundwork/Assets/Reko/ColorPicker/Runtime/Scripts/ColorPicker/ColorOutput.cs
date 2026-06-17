using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Reko.ColorPicker
{
    [RequireComponent(typeof(Graphic))]
    public class ColorOutput : MonoBehaviour
    {
        public enum Conversion
        {
            None,
            FullSaturation,
            FullValue,
            FullSaturationValue,
            FullAlpha,
            FullSaturationValueAlpha,
            BlackWhiteContrast,
        }

        [SerializeField]
        private Conversion m_conversion;

        private IColorPicker m_colorPicker;
        private Graphic m_output;

        void Start()
        {
            m_output = GetComponent<Graphic>();
            m_colorPicker = GetComponentInParent<IColorPicker>();

            if (m_colorPicker != null)
            {
                m_colorPicker.OnColorChanged.AddListener(OnColorChanged);
                OnColorChanged(m_colorPicker.SelectedColor);
            }
        }

        private void OnDestroy()
        {
            if(m_colorPicker != null)
            {
                m_colorPicker.OnColorChanged.RemoveListener(OnColorChanged);
            }
        }

        private void OnColorChanged(Color rgba)
        {
            var convertedHSV = ConvertHSV(m_colorPicker.HSV);
            var alpha = rgba.a;
            if (m_conversion is Conversion.FullSaturationValueAlpha or Conversion.FullAlpha or Conversion.BlackWhiteContrast)
            {
                alpha = 1.0f;
            }
            m_output.color = ColorUtils.HSVToRGB(convertedHSV, alpha);

        }

        private Vector3 ConvertHSV(Vector3 hsv)
        {
            switch(m_conversion)
            {
                case Conversion.FullSaturation:
                    return new Vector3(hsv[0], 1, hsv[2]);
                case Conversion.FullValue:
                    return new Vector3(hsv[0], hsv[1], 1);
                case Conversion.FullSaturationValueAlpha:
                case Conversion.FullSaturationValue:
                    return new Vector3(hsv[0], 1, 1);
                case Conversion.BlackWhiteContrast:
                    if (hsv[2] > 0.9f && hsv[1] < 0.1f)
                        return new Vector3(0, 0, 0);
                    return new Vector3(0, 0, 1);
                case Conversion.None:
                default:
                    return hsv;
            }
        }


        internal Conversion TestConversion
        {
            get { return m_conversion; }
        }
    }
}
