using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Reko.ColorPicker
{    
    public class ObjectSwitch : MonoBehaviour
    {
        [SerializeField]
        private List<GameObject> m_objects = new List<GameObject>();

        public void Switch(int index)
        {
            for(int i = 0; i < m_objects.Count; i++)
            {
                m_objects[i].SetActive(i == index);
            }
        }

        /// <summary>
        /// Visibility is not changed. You have to call Switch() to apply the visibility to the new objects
        /// </summary>
        public void SetObjects(IEnumerable<GameObject> objects)
        {
            m_objects = objects.ToList();
        }

        /// <summary>
        /// Visibility is not changed. You have to call Switch() to apply the visibility to the new objects
        /// </summary>
        public void SetObjects(params GameObject[] objects)
        {
            m_objects = objects.ToList();
        }
    }
}
