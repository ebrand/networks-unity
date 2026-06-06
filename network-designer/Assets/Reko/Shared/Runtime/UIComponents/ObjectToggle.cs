using System;
using UnityEngine;

namespace Reko
{
    /// <summary>
    /// Switch between two gameobjects.
    /// </summary>
    public class ObjectToggle : MonoBehaviour
    {
        [SerializeField]
        private GameObject m_on;
    
        [SerializeField]
        private GameObject m_off;

        [SerializeField]
        private bool m_isOn;
        public bool IsOn
        {
            get => m_isOn;
            set
            {
                if (m_isOn != value)
                {
                    m_isOn = value;
                    UpdateVisibility();
                }
            }
        }
        public bool IsOff
        {
            get => !m_isOn;
            set
            {
                IsOn = !value;
            }
        }
        
        private void Start()
        {
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            m_on.SetActive(m_isOn);
            m_off.SetActive(!m_isOn);
        }
    }
  
}
