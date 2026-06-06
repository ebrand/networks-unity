using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Reko.ColorPicker
{
    public interface IColorPicker
    {
        Color SelectedColor { get; set; }
        Vector3 HSV { get; set; }
        UnityEvent<Color> OnColorChanged { get; }
    }

    public class ColorPicker : MonoBehaviour, IColorPicker
    {
        [SerializeField]
        private Color m_color = Color.white;
        private Vector3 m_hsv;

        [SerializeField]
        private UnityEvent<Color> m_onColorChanged = new UnityEvent<Color>();

        public Color SelectedColor
        {
            get { return m_color; }
            set { SetColor(value); }
        }
        public UnityEvent<Color> OnColorChanged { get => m_onColorChanged; }

        public Vector3 HSV
        {
            get { return m_hsv; }
            set { SetHSV(value); }
        }

        public string Hex
        {
            get { return ColorUtils.RGBToHex(m_color); }
        }

        protected virtual void Start()
        {
            SetColor(m_color);
        }

        public void SetHSV(Vector3 hsv)
        {
            m_hsv = hsv;
            m_color = ColorUtils.HSVToRGB(m_hsv, m_color.a);
            ColorChanged();
        }

        public void SetHSV(float H, float S, float V)
        {
            SetHSV(new Vector3(H, S, V));
        }

        public void SetColor(Color color)
        {
            m_hsv = ColorUtils.RGBToHSV(color);
            m_color = color;
            ColorChanged();
        }

        public void SetHex(string hex)
        {
            SetColor(ColorUtils.HexToRGB(hex));
        }

        protected virtual void ColorChanged()
        {
            m_onColorChanged?.Invoke(m_color);
        }
    }

}