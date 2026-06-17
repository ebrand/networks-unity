using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Reko
{
    [DefaultExecutionOrder(-100)]
    public class ColorTheme : MonoBehaviour, INotifyPropertyChanged
    {
        [SerializeField]
        private ColorScheme m_darkScheme;
        [SerializeField]
        private ColorScheme m_lightScheme;

        [SerializeField]
        private bool m_useDarkMode = false;
        public bool UseDarkMode
        {
            get { return Scheme == m_darkScheme; }
            set { Scheme = value ? m_darkScheme : m_lightScheme; }
        }

        private ColorScheme m_currentScheme;
        public ColorScheme Scheme
        {
            get => m_currentScheme;
            set
            {
                if (m_currentScheme != value)
                {
                    m_currentScheme = value;
                    RaisePropertyChanged();
                }
            }
        }
        
        private void Awake()
        {
            m_currentScheme = m_useDarkMode ? m_darkScheme : m_lightScheme;
        }
        
        private void OnValidate()
        {
            if (Application.isPlaying && Application.isEditor)
            {
                UseDarkMode = m_useDarkMode;
            }
        }

        public static ColorTheme GetInstance(GameObject request)
        {
            return request.GetComponentInParent<ColorTheme>();
        }
        
        public bool TryGetColor(ColorSchemeType type, out Color color)
        {
            if (m_currentScheme == null)
            {
                color = Color.black;
                return false;
            }
            
            color = m_currentScheme.GetColor(type);
            return true;
        }
        
        public Color GetColor(ColorSchemeType type)
        {
            return m_currentScheme.GetColor(type);
        }


        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static ColorTheme CreateInstance()
        {
            var go = new GameObject("Color Theme");
            var instance = go.AddComponent<ColorTheme>();
            return instance;
        }
        
        internal ColorScheme TestDarkScheme
        {
            get => m_darkScheme;
            set => m_darkScheme = value;
        }
        
        internal ColorScheme TestLightScheme
        {
            get => m_lightScheme;
            set => m_lightScheme = value;
        }
    }
}
