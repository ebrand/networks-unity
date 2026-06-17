using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Reko.ColorPicker
{
    public class LinearSlider : MonoBehaviour, ISingleInput<float>
    {
        [SerializeField]
        private Slider m_slider;

        public float Value
        {
            get => m_slider.value; 
            set => m_slider.value = value;
        }

        public void SetValueWithoutNotify(float value)
        {
            m_slider.SetValueWithoutNotify(value);
        }

        public UnityEvent<float> onValueChanged => m_slider.onValueChanged;

        private void Awake()
        {
            if(m_slider == null)
            {
                m_slider = GetComponent<Slider>();
            }
        }
    }
}