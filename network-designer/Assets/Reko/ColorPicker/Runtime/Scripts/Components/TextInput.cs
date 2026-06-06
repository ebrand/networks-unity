using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Reko.ColorPicker
{ 
    public class TextInput : MonoBehaviour, ISingleInput<float>
    {
        [SerializeField]
        private TMPro.TMP_InputField m_textInput;

        [SerializeField]
        private int m_minValue;
        [SerializeField]
        private int m_maxValue;

        [SerializeField]
        private UnityEvent<float> m_onValueChanged = new UnityEvent<float>();

        public float Value 
        {
            get => ConvertToFloat(m_textInput.text);
            set => m_textInput.text = ConvertToString(value);
        }

        public void SetValueWithoutNotify(float value)
        {
            m_textInput.SetTextWithoutNotify(ConvertToString(value));
        }

        public UnityEvent<float> onValueChanged => m_onValueChanged;

        private void Start()
        {
            m_textInput.onValueChanged.AddListener(OnTextValueChanged);
        }
        private void OnDestroy()
        {
            m_textInput.onValueChanged.RemoveListener(OnTextValueChanged);
        }

        private void OnTextValueChanged(string text)
        {
            m_onValueChanged?.Invoke(ConvertToFloat(text));
        }


        private string ConvertToString(float value)
        {
            var result = Mathf.RoundToInt(Mathf.Lerp(m_minValue, m_maxValue, value));
            return result.ToString();
        }

        private float ConvertToFloat(string text)
        {
            if(int.TryParse(text, out int value))
            {
                value = Math.Clamp(value, m_minValue, m_maxValue);
                return (float)(value - m_minValue) / (m_maxValue - m_minValue);
            }
            return 0;
        }

        internal int TestMinValue { get => m_minValue; set => m_minValue = value; }
        internal int TestMaxValue { get => m_maxValue; set => m_maxValue = value; }
    }

}
