using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Reko
{
    [RequireComponent(typeof(CanvasRenderer))]
    [ExecuteInEditMode]
    public class UIRing : Graphic, ICanvasRaycastFilter
    {
        [SerializeField]
        private float m_thickness = 10;

        [SerializeField]
        private bool m_reverseDirection = false;
        
        [SerializeField]
        private bool m_mirror = false;

        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, screenPoint, eventCamera, out Vector2 localPoint))
                return false;

            // convert to center point as reference
            var rect = GetPixelAdjustedRect(); 
            localPoint.x += (rectTransform.pivot.x - 0.5f) * rect.width;
            localPoint.y += (rectTransform.pivot.y - 0.5f) * rect.height;

            var radius = GetOuterRadius();
            var innerRadius = GetInnerRadius();
            var distance = localPoint.magnitude;
            return distance >= innerRadius && distance <= radius;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            var radius = GetOuterRadius();
            var innerRadius = GetInnerRadius();

            vh.Clear();

            var tesselation = CircleUtils.ComputeTesselation(radius);
            var innerPositions = CircleUtils.GetCirclePositions(innerRadius, tesselation).ToList();
            var outerPositions = CircleUtils.GetCirclePositions(radius, tesselation).ToList();
            AddVertices(vh, innerPositions, 0.0f);
            AddVertices(vh, outerPositions, 1.0f);

            var ringVertCount = innerPositions.Count;
            for (int i = 0; i < innerPositions.Count-1; i++)
            {
                var next = (i + 1);

                vh.AddTriangle(i, i + ringVertCount, next);
                vh.AddTriangle(next, i + ringVertCount, next + ringVertCount);
            }
        }

        private float GetInnerRadius()
        {
            return Mathf.Max(0, GetOuterRadius() - m_thickness);
        }

        private float GetOuterRadius()
        {
            var rectangle = rectTransform.rect;
            return 0.5f * Mathf.Min(rectangle.width, rectangle.height);
        }


        private void AddVertices(VertexHelper vh, List<Vector2> positions, float v)
        {
            var center = rectTransform.rect.center;
            UIVertex vert = UIVertex.simpleVert;
            for(int i = 0; i < positions.Count; i++)
            {
                vert.position = center + positions[i];
                vert.color = color;

                var u = ComputeU((float)i / (positions.Count - 1));
                
                vert.uv0 = new Vector2(u, v);
                vh.AddVert(vert);
            }
        }

        private float ComputeU(float fraction)
        {
            var u = fraction;
            if(!m_reverseDirection)
            {
                u = 1.0f - fraction;
            }

            if(m_mirror)
            {
                u = 2 * u;
                if (u > 1)
                    u = 2 - u;
            }
            return u;
        }
    }
}
