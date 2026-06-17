using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Reko.ColorPicker
{
    public class HexInput : MonoBehaviour, ISingleInput<string>
    {
        [SerializeField]
        private TMPro.TMP_InputField m_inputField;

        [SerializeField]
        private bool m_adjustTextColor = true;

        [SerializeField]
        private UnityEvent<string> m_onValueChanged;

        public string Value
        {
            get => m_inputField.text;
            set => m_inputField.text = value;
        }

        public void SetValueWithoutNotify(string value)
        {
            if(!m_inputField.isFocused)
            {
                m_inputField.SetTextWithoutNotify(value);
            }

            if (m_adjustTextColor)
            {
                UpdateTextColor();
            }
        }

        public UnityEvent<string> onValueChanged => m_onValueChanged;

        void Start()
        {
            m_inputField.onValueChanged.AddListener(OnValueChanged);
            m_inputField.onEndEdit.AddListener(OnValueChanged);
        }

        void OnDestroy()
        {
            m_inputField.onValueChanged.RemoveListener(OnValueChanged);
            m_inputField.onEndEdit.RemoveListener(OnValueChanged);
        }


        private void OnValueChanged(string text)
        {
            if (m_adjustTextColor)
            {
                UpdateTextColor();
            }
            m_onValueChanged?.Invoke(text);
        }

        private void UpdateTextColor()
        {
            var hsv = ColorUtils.RGBToHSV(ColorUtils.HexToRGB(m_inputField.text));
            if (hsv[2] < 0.5f)
                m_inputField.textComponent.color = Color.white;
            else
                m_inputField.textComponent.color = Color.black;
        }
    }
}
