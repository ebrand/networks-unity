using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Reko
{
    public class Dialog : MonoBehaviour
    {
        [SerializeField] 
        private GameObject m_target;
        
        public void Toggle()
        {
            m_target.SetActive(!m_target.activeSelf);
        }

        public void Open()
        {
            m_target.SetActive(true);
        }

        public void Close()
        {
            m_target.SetActive(false);
        }

    }
   
}
