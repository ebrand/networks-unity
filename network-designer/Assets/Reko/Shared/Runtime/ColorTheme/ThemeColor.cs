using System.ComponentModel;
using UnityEngine;
using UnityEngine.UI;

namespace Reko
{
    /// <summary>
    /// Applies a color from a theme to a UI graphic.
    /// </summary>
    public class ThemeColor : MonoBehaviour
    {
        [SerializeField]
        private ColorSchemeType m_type;

        private ColorTheme m_theme;
        private Graphic m_graphic;

        private void OnEnable()
        {
            m_graphic = GetComponent<Graphic>();
            if (m_graphic == null)
                return;
            
            
            m_theme = ColorTheme.GetInstance(gameObject);
            if (m_theme != null)
            {
                m_theme.PropertyChanged += OnThemePropertyChanged;
                ApplyColor();
            }
        }

        private void OnDisable()
        {
            if (m_theme != null)
            {
                m_theme.PropertyChanged -= OnThemePropertyChanged;
                m_theme = null;
            }
        }

        private void OnThemePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ApplyColor();
        }

        private void ApplyColor()
        {
            if (m_theme.TryGetColor(m_type, out Color color))
                m_graphic.color = color;
        }


        internal ColorSchemeType ColorSchemeType
        {
            get => m_type;
            set => m_type = value;
        }
    } 
}

