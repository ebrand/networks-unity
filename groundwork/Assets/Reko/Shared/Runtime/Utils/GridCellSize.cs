using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Reko
{
    [RequireComponent(typeof(GridLayoutGroup))]
    public class GridCellSize : UIBehaviour
    {
        [SerializeField] private bool m_x;
        
        [SerializeField] private bool m_y;
        
        private GridLayoutGroup m_layout;
        private RectTransform m_rectTransform;

        private int m_childCount = 0;
        private int m_rowCount = 0;
        private int m_colCount = 0;
        private Vector2 m_cellSize;

        protected override void Awake()
        {
            m_layout = GetComponent<GridLayoutGroup>();
            m_rectTransform = (RectTransform)transform;

            if (m_layout.constraint == GridLayoutGroup.Constraint.Flexible)
            {
                Debug.LogWarning("Grid Cell Size does not work for flexible layout");
                this.enabled = false;
            }
        }


        private void Update()
        {
            if (m_childCount != m_layout.transform.childCount)
            {
                m_childCount = m_layout.transform.childCount;
                m_cellSize = m_layout.cellSize;
                UpdateCounts();
                AdjustCellSize();
            }
        }


        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            if (m_layout != null)
            {
                AdjustCellSize();
            }
        }

        private void AdjustCellSize()
        {
            if(m_y) m_cellSize.y = GetBestHeight();
            if(m_x) m_cellSize.x = GetBestWidth();
            m_layout.cellSize = m_cellSize;
        }

        private float GetBestHeight()
        {
            if (m_rowCount == 0)
                return m_cellSize.y;
            var totalSpacing = (m_rowCount - 1) * m_layout.spacing.y;
            var bestHeight = (m_rectTransform.rect.height - totalSpacing) / m_rowCount;
            return Mathf.Max(0, bestHeight);
        }
        
        private float GetBestWidth()
        {
            if (m_colCount == 0)
                return m_cellSize.x;
            
            var totalSpacing = (m_colCount - 1) * m_layout.spacing.x;
            var bestWidth = (m_rectTransform.rect.width - totalSpacing) / m_colCount;
            return Mathf.Max(0, bestWidth);
        }

        private void UpdateCounts()
        {
            if (m_layout.constraint == GridLayoutGroup.Constraint.FixedColumnCount)
            {
                m_colCount = m_layout.constraintCount;
                m_rowCount = Mathf.CeilToInt(m_childCount / (float)m_layout.constraintCount);
            }
            else if (m_layout.constraint == GridLayoutGroup.Constraint.FixedRowCount)
            {
                m_colCount = Mathf.CeilToInt(m_childCount / (float)m_layout.constraintCount);
                m_rowCount = m_layout.constraintCount;
            }
        }

    }  
}

