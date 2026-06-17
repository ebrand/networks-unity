using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;


namespace Reko
{
    [RequireComponent(typeof(CanvasRenderer))]
    [ExecuteInEditMode]
    public class UIRoundedRect : Graphic
    {
        [SerializeField, HideInInspector]
        private bool m_uniformValues = true;
        [SerializeField, HideInInspector]
        private float m_radius = 30;
        [SerializeField, HideInInspector]
        private Vector4 m_radii = new Vector4(30, 30, 30, 30);
        [SerializeField, HideInInspector]
        private bool m_border;
        [SerializeField, HideInInspector]
        private float m_borderWidth;
        [SerializeField, HideInInspector]
        private Shader m_shader;


        internal static string ShaderPath => "Reko/RoundedCorners";


        protected override void Awake()
        {
            base.Awake();
            m_Material = null;
        }
        protected override void OnDestroy()
        {
            base.OnDestroy();
            if(m_Material != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(m_Material);
                }
                m_Material = null;
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            var rectangle = rectTransform.rect;

            vh.Clear();

            AddVertex(vh, rectangle.xMin, rectangle.yMin);
            AddVertex(vh, rectangle.xMin, rectangle.yMax);
            AddVertex(vh, rectangle.xMax, rectangle.yMax);
            AddVertex(vh, rectangle.xMax, rectangle.yMin);

            vh.AddTriangle(0, 2, 1);
            vh.AddTriangle(2, 3, 0);

            SetShaderProperties();
        }


        public override Material material
        {
            get
            {
                if (m_Material == null)
                {
                    m_Material = CreateMaterial();
                    SetShaderProperties();
                }
                return m_Material;
            }

            set
            {
                // not possible
            }
        }

        private void AddVertex(VertexHelper vh, float x, float y)
        {
            var localPos = new Vector2(x, y) + (rectTransform.pivot - new Vector2(0.5f, 0.5f)) * rectTransform.rect.size;
            vh.AddVert(new Vector3(x, y), color, localPos);
        }

        private Material CreateMaterial()
        {
            if(m_shader == null)
            {
                m_shader = Shader.Find(ShaderPath);
            }
            return new Material(m_shader);
        }

        private void SetShaderProperties()
        {
            if(m_Material != null)
            {
                var size = rectTransform.rect.size;
                m_Material.SetVector("_Size", size);

                var radii = m_radii;
                if(m_uniformValues)
                {
                    radii = new Vector4(m_radius, m_radius, m_radius, m_radius);
                }
                m_Material.SetVector("_Radius", radii);

                m_Material.SetFloat("_Border", m_border ? m_borderWidth : 0);
            }
        }
    }
}
