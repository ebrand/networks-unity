using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Reko.ColorPicker
{
    /// <summary>
    /// Show or hide objects when the color palette contains a selected color.
    /// </summary>
    public class PaletteVisibility : MonoBehaviour
    {
        [SerializeField] 
        private ColorPicker m_picker;

        [SerializeField]
        private ColorPalette m_palette;
        
        [SerializeField]
        private bool m_inverse;
        
        [SerializeField]
        private UnityEvent<bool> m_onVisibilityChanged;


        private bool m_isVisible = true;
        public bool IsVisible
        {
            get { return m_isVisible; }
            private set
            {
                if (m_isVisible != value)
                {
                    m_isVisible = value;
                    OnVisibilityChanged?.Invoke(value);
                }
            }
        }

        public UnityEvent<bool> OnVisibilityChanged => m_onVisibilityChanged;
        
        private void Start()
        {
            if (m_picker == null || m_palette == null)
            {
                Debug.LogWarning("No picker or palette defined. PaletteVisibility is deactivated");
                enabled = false;
                return;
            }
            m_picker.OnColorChanged.AddListener(OnColorChanged);
            OnColorChanged(m_picker.SelectedColor);
        }


        private void OnDestroy()
        {
            if (m_picker != null)
            {
                m_picker.OnColorChanged.RemoveListener(OnColorChanged);
            }
        }


        private void OnColorChanged(Color color)
        {
            var newValue = m_palette.ContainsColor(color);
            if (m_inverse)
                newValue = !newValue;
            IsVisible = newValue;
        }
    }
 
}
