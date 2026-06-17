using UnityEngine;

namespace Reko
{
    [CreateAssetMenu(fileName = "Data", menuName = "Reko/Color Scheme", order = 1)]
    public class ColorScheme : ScriptableObject
    {
        public Color Background;
        public Color OnBackground;
        public Color PanelBackground;
        public Color OnPanelBackground;
        public Color Primary;
        public Color OnPrimary; 
        public Color Secondary;
        public Color OnSecondary;
        public Color PrimaryVariant;
        public Color SecondaryVariant;
    }
    
    public enum ColorSchemeType
    {
        Background = 8,
        OnBackground = 9,
        PanelBackground = 0,
        OnPanelBackground = 1,
        Primary = 2,
        OnPrimary = 3,
        Secondary = 4,
        OnSecondary = 5,
        PrimaryVariant = 6,
        SecondaryVariant = 7,
    }
    
    public static class ColorSchemeExtensions
    {
        public static Color GetColor(this ColorScheme scheme, ColorSchemeType type)
        {
            switch (type)
            {
                case ColorSchemeType.Background: return scheme.Background;
                case ColorSchemeType.OnBackground: return scheme.OnBackground;
                case ColorSchemeType.PanelBackground: return scheme.PanelBackground;
                case ColorSchemeType.OnPanelBackground: return scheme.OnPanelBackground;
                case ColorSchemeType.Primary: return scheme.Primary;
                case ColorSchemeType.OnPrimary: return scheme.OnPrimary;
                case ColorSchemeType.Secondary: return scheme.Secondary;
                case ColorSchemeType.OnSecondary: return scheme.OnSecondary;
                case ColorSchemeType.PrimaryVariant: return scheme.PrimaryVariant;
                case ColorSchemeType.SecondaryVariant: return scheme.SecondaryVariant;
                default: 
                    Debug.LogError($"Missing Scheme Color {type}");
                    return scheme.Background;
            }
        }
    }


}
