using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace Reko.ColorPicker
{
    public interface IColorPalette : ISingleInput<Color> 
    {
        void SelectIndex(int index);
        
        bool HasSelection { get; }
        bool ContainsColor(Color color);
        
        IEnumerable<Color> Colors { get; }
    }
    
    public class ColorPalette : MonoBehaviour, IColorPalette
    {
        [SerializeField]
        [HideInInspector]
        private List<Color> m_colors = new() { Color.red, Color.green, Color.blue };

        [SerializeField]
        private ColorPaletteItem m_itemPrefab;

        [SerializeField]
        private Transform m_container;

        [SerializeField]
        private UnityEvent<Color> m_onValueChanged = new UnityEvent<Color>();

        [SerializeField]
        private List<ColorPaletteItem> m_items;

        [SerializeField]
        private ColorSet m_colorSet;
        
        private int m_selectedIndex = -1;




        public Color Value
        {
            get
            {
                if (m_selectedIndex < 0)
                    return Color.white;
                return m_colors[m_selectedIndex];
            }
            set { SetValueWithoutNotify(value); }
        }

        public IEnumerable<Color> Colors
        {
            get { return m_colors; }
        }

        public void SetValueWithoutNotify(Color value)
        { 
            SelectIndex(FindIndex(value), false);
        }

        public UnityEvent<Color> onValueChanged => m_onValueChanged;

        
        public bool HasSelection
        {
            get { return m_selectedIndex >= 0; }
        }
        
        public bool ContainsColor(Color color)
        {
            return m_colors.Contains(color);
        }


        private void Awake()
        {
            if (m_container == null)
                m_container = transform;
            ClearPaletteItems();
            CreatePaletteItems();
        }
        
        public void SelectIndex(int index)
        {
            SelectIndex(index, true);
        }
        
        private void SelectIndex(int index, bool notify)
        {
            if (m_selectedIndex == index)
                return;
            
            m_selectedIndex = index;
            for (int i = 0; i < m_items.Count; i++)
            {
                m_items[i].IsSelected = i == index;
            }

            if (notify)
            {
                m_onValueChanged?.Invoke(Value);
            }
        }

        public void UpdatePaletteItems()
        {
            if (m_container == null)
                return;
            
            ClearPaletteItems();
            CreatePaletteItems();
        }

        private void ClearPaletteItems()
        {
            for (int i = m_container.childCount - 1; i >= 0; i--)
                DestroyImmediate(m_container.GetChild(i).gameObject);
            m_items = null;
        }

        private void CreatePaletteItems()
        {
            m_items = new List<ColorPaletteItem>(m_colors.Count);
            if (m_itemPrefab == null)
                return;

            for (int i = 0; i < m_colors.Count; i++)
            {
                var item = Instantiate(m_itemPrefab, m_container);
                item.Index = i;
                item.Color = m_colors[i];
                item.IsSelected = m_selectedIndex == i;
                item.Palette = this;
                m_items.Add(item);
            }
        }


        private int FindIndex(Color value)
        {
            for (int i = 0; i < m_colors.Count; i++)
            {
                if (m_colors[i] == value)
                {
                    return i;
                }
            }
            return -1;
        }
        
        internal void TestSetColors(IEnumerable<Color> colors)
        {
            m_colors = colors.ToList();
            UpdatePaletteItems();
        }

        internal ColorPaletteItem TestItemPrefab
        {
            get { return m_itemPrefab; }
            set
            {
                m_itemPrefab = value;
                UpdatePaletteItems();
            }
        }
    }
}
