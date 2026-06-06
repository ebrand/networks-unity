using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;


namespace Reko.ColorPicker
{

    public interface IColorPaletteItem
    {
        Color Color { get; set; }
        bool IsSelected { get; set; }
        
        IColorPalette Palette { get; set; } 
    }

    public class ColorPaletteItem : MonoBehaviour, IColorPaletteItem, IPointerClickHandler
    {
        [SerializeField]
        private Graphic m_color;

        [SerializeField]
        private Graphic m_selection;
        
        public Color Color
        {
            get { return m_color != null ? m_color.color : Color.white; }
            set { if (m_color != null) m_color.color = value; }
        }
        
        public bool IsSelected
        {
            get { return m_selection != null ? m_selection.enabled : false; }
            set { if (m_selection != null) m_selection.enabled = value; }
        }
        
        
        public int Index { get; set; }

        public IColorPalette Palette { get; set; }

        public void OnPointerClick(PointerEventData eventData)
        {
            IsSelected = true;
            Palette.SelectIndex(Index);
        }
    }
}