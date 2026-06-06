using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Reko.ColorPicker
{
    public class Slider2DCoordinate : MonoBehaviour, ISingleInput<float>
    {
        public enum Coordinate { X, Y }

        [SerializeField]
        private Slider2D m_slider;

        [SerializeField]
        private Coordinate m_coordinate;

        [SerializeField]
        private UnityEvent<float> m_onValueChanged = new UnityEvent<float>();

        public float Value
        {
            get
            {
                return m_coordinate == Coordinate.X ? m_slider.XValue : m_slider.YValue;
            }
            set
            {
                if (m_coordinate == Coordinate.X)
                    m_slider.XValue = value;
                else
                    m_slider.YValue = value;
            }
        }

        public void SetValueWithoutNotify(float value)
        {
            if (m_coordinate == Coordinate.X)
                m_slider.SetXValueWithoutNotify(value);
            else
                m_slider.SetYValueWithoutNotify(value);
        }

        public UnityEvent<float> onValueChanged => m_onValueChanged;



        private void Awake()
        {
            if (m_slider == null)
            {
                m_slider = GetComponent<Slider2D>();
            }

            m_slider.onValueChanged.AddListener(OnSliderValueChanged);
        }

        private void OnDestroy()
        {
            m_slider.onValueChanged.RemoveListener(OnSliderValueChanged);
        }


        private void OnSliderValueChanged(Vector2 value)
        {
            m_onValueChanged?.Invoke(m_coordinate == Coordinate.X ? value.x : value.y);
        }
    }
}
