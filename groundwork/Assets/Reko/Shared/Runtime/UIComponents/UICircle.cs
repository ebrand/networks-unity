using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Reko
{
    [RequireComponent(typeof(CanvasRenderer))]
    [ExecuteInEditMode]
    public class UICircle : Graphic
    {
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            var rectangle = rectTransform.rect;
            var center = rectangle.center;
            var radius = 0.5f * Mathf.Min(rectangle.width, rectangle.height);

            vh.Clear();

            UIVertex vert = UIVertex.simpleVert; 
            vert.position = center;
            vert.color = color;
            vert.uv0 = GetRelativePos(center);
            vh.AddVert(vert);

            var positions = CircleUtils.GetCirclePositions(radius).ToList();

            for(int i = 0; i < positions.Count; i++)
            {
                vert.position = positions[i];
                vert.color = color;
                vert.uv0 = GetRelativePos(positions[i]);
                vh.AddVert(vert);
            }

            for(int i = 0; i < positions.Count-1; i++)
            {
                var t1 = i + 1;
                var t2 = i + 2;
                vh.AddTriangle(0, t1, t2);
            }
        }

        private Vector2 GetLocalPos(Vector2 pos)
        {
            return pos + (rectTransform.pivot - new Vector2(0.5f, 0.5f)) * rectTransform.rect.size;
        }

        private Vector2 GetRelativePos(Vector2 pos)
        {
            var localPos = GetLocalPos(pos);
            return localPos / (0.5f * rectTransform.rect.size);
        }
    }
}
