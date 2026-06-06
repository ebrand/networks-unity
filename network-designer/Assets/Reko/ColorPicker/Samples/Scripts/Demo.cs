using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Reko.ColorPicker
{

    public class Demo : MonoBehaviour
    {
        private List<GameObject> m_items;
        private int m_index = 0;

        private void Start()
        {
            m_items = new List<GameObject>();
            foreach(var child in transform)
            {
                m_items.Add(((Transform)child).gameObject);
            }
            UpdateVisibility();
        }

        public void Next()
        {
            m_index++;
            if (m_index >= m_items.Count)
                m_index = 0;

            UpdateVisibility();
        }

        public void Previous()
        {
            m_index--;
            if (m_index < 0)
                m_index = m_items.Count - 1;

            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            for(int i = 0; i < m_items.Count; i++)
            {
                m_items[i].SetActive(i == m_index);
            }
        }


    }

}