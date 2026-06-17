using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Reko.ColorPicker
{
    public class ColorPickerBinding : MonoBehaviour
    {
        public enum ColorComponent
        {
            Color,
            Red,
            Green,
            Blue,
            Hue,
            Saturation,
            Value,
            Hex,
            Alpha,
        }

        [SerializeField]
        private ColorComponent m_component;

        private IColorPicker m_colorPicker;
        private ISingleInput<float> m_floatInput;
        private ISingleInput<string> m_stringInput;
        private ISingleInput<Color> m_colorInput;
        private bool m_inputFound = false;


        void Start()
        {
            FindColorPicker();
            if (m_colorPicker == null)
            {
                Debug.LogWarning($"Could not find ColorPicker in parent components of {gameObject.name}");
                return;
            }                

            FindInputField();

            if (!m_inputFound)
            {
                Debug.LogWarning($"Could not find SingleInput in component of {gameObject.name}");
                return;
            }

            m_colorPicker.OnColorChanged.AddListener(OnColorChanged);
            OnColorChanged(m_colorPicker.SelectedColor);
            
        }

        private void OnDestroy()
        {
            if (m_colorPicker == null || !m_inputFound)
                return;

            m_colorPicker.OnColorChanged.RemoveListener(OnColorChanged);

            if (m_colorInput != null)
            {
                m_colorInput.onValueChanged.RemoveListener(OnColorValueChanged);
            }
            if (m_stringInput != null)
            {
                m_stringInput.onValueChanged.RemoveListener(OnStringValueChanged);
            }
            if (m_floatInput != null)
            {
                m_floatInput.onValueChanged.RemoveListener(OnFloatValueChanged);
            }
        }

        private void FindColorPicker()
        {
            m_colorPicker = GetComponentInParent<IColorPicker>();
        }

        private void FindInputField()
        {
            switch(m_component)
            {
                case ColorComponent.Color:
                    m_colorInput = GetComponent<ISingleInput<Color>>();
                    if(m_colorInput != null)
                    {
                        m_colorInput.onValueChanged.AddListener(OnColorValueChanged);
                        m_inputFound = true;
                    }
                    break;
                case ColorComponent.Hex:
                    m_stringInput = GetComponent<ISingleInput<string>>();
                    if(m_stringInput != null)
                    {
                        m_stringInput.onValueChanged.AddListener(OnStringValueChanged);
                        m_inputFound = true;
                    }
                    break;
                default:
                    m_floatInput = GetComponent<ISingleInput<float>>();
                    if (m_floatInput != null)
                    {
                        m_floatInput.onValueChanged.AddListener(OnFloatValueChanged);
                        m_inputFound = true;
                    }
                    break;
            }
        }


        private void OnFloatValueChanged(float value)
        {
            var hsv = m_colorPicker.HSV;
            var color = m_colorPicker.SelectedColor;
            switch(m_component)
            {
                case ColorComponent.Red:
                    color.r = value;
                    m_colorPicker.SelectedColor = color;
                    break;
                case ColorComponent.Green:
                    color.g = value;
                    m_colorPicker.SelectedColor = color;
                    break;
                case ColorComponent.Blue:
                    color.b = value;
                    m_colorPicker.SelectedColor = color;
                    break;
                case ColorComponent.Hue:
                    hsv[0] = value;
                    m_colorPicker.HSV = hsv;
                    break;
                case ColorComponent.Saturation:
                    hsv[1] = value;
                    m_colorPicker.HSV = hsv;
                    break;
                case ColorComponent.Value:
                    hsv[2] = value;
                    m_colorPicker.HSV = hsv;
                    break;
                case ColorComponent.Alpha:
                    color.a = value;
                    m_colorPicker.SelectedColor = color;
                    break;
            }
        }
        private void OnColorValueChanged(Color value)
        {
            if(m_component == ColorComponent.Color)
            {
                m_colorPicker.SelectedColor = value;
            }
        }
        private void OnStringValueChanged(string value)
        {
            if (m_component == ColorComponent.Hex)
            {
                m_colorPicker.SelectedColor = ColorUtils.HexToRGB(value);
            }
        }

        private void OnColorChanged(Color color)
        {
            var hsv = m_colorPicker.HSV;
            switch(m_component)
            {
                case ColorComponent.Red:
                    m_floatInput.SetValueWithoutNotify(color.r);
                    break;
                case ColorComponent.Green:
                    m_floatInput.SetValueWithoutNotify(color.g);
                    break;
                case ColorComponent.Blue:
                    m_floatInput.SetValueWithoutNotify(color.b);
                    break;
                case ColorComponent.Color:
                    m_colorInput.SetValueWithoutNotify(color);
                    break;
                case ColorComponent.Hex:
                    m_stringInput.SetValueWithoutNotify(ColorUtils.RGBToHex(color));
                    break;
                case ColorComponent.Hue:
                    m_floatInput.SetValueWithoutNotify(hsv[0]);
                    break;
                case ColorComponent.Saturation:
                    m_floatInput.SetValueWithoutNotify(hsv[1]);
                    break;
                case ColorComponent.Value:
                    m_floatInput.SetValueWithoutNotify(hsv[2]);
                    break;
                case ColorComponent.Alpha:
                    m_floatInput.SetValueWithoutNotify(color.a);
                    break;
            }
        }

        internal ColorComponent TestComponent
        {
            get { return m_component; }
        }

        internal ISingleInput<float> TestFloatInput
        {
            get { return m_floatInput; }
        }
        internal ISingleInput<string> TestStringInput
        {
            get { return m_stringInput; }
        }
        internal ISingleInput<Color> TestColorInput
        {
            get { return m_colorInput; }
        }
    }
}

