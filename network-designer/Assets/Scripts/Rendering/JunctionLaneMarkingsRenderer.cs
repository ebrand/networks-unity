// Per-vertex overlay that carries the WHITE interior lane-divider dashes
// across a promoted collinear junction. The dividers are part of each road
// body and stop at the setback; this draws short dashed lines bridging each
// matched divider across the gap.
//
// NetworkRenderer does the lane-correspondence matching (by world side +
// order from the centerline, so an outer-lane drop simply leaves its outer
// divider unmatched and undrawn) and hands this renderer the resulting
// (From → To) world segment pairs. This component just paints dashed lines.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Rendering
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class JunctionLaneMarkingsRenderer : MonoBehaviour
    {
        [Header("Segments (world XZ; From[i]→To[i])")]
        public List<Vector2> SegFrom = new List<Vector2>();
        public List<Vector2> SegTo = new List<Vector2>();

        [Header("Style")]
        public float LineWidth = 0.15f;
        public float DashLength = 3f;
        public float DashGap = 9f;
        public float MarkingHeight = 0.01f;
        public Material LaneMarkingMaterial;
        public Color LaneMarkingColor = new Color(0.95f, 0.95f, 0.95f);

        Mesh _mesh;

        void Start() { Rebuild(); }

        public void Rebuild()
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();

            var verts = new List<Vector3>();
            var tris = new List<int>();
            int count = Mathf.Min(SegFrom.Count, SegTo.Count);
            float half = LineWidth * 0.5f;
            for (int i = 0; i < count; i++)
                AddDashedLine(verts, tris, SegFrom[i], SegTo[i], half);

            if (verts.Count == 0)
            {
                if (_mesh != null) _mesh.Clear();
                mf.sharedMesh = _mesh;
                return;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "JunctionLaneMarkings" };
                mf.sharedMesh = _mesh;
            }
            else
            {
                _mesh.Clear();
            }
            _mesh.SetVertices(verts);
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            Material mat = LaneMarkingMaterial != null
                ? LaneMarkingMaterial
                : CreateLitMatte(LaneMarkingColor, "JunctionLaneMarkingMat");
            mr.sharedMaterials = new[] { mat };
        }

        void AddDashedLine(List<Vector3> verts, List<int> tris,
            Vector2 from, Vector2 to, float half)
        {
            Vector2 axis = to - from;
            float len = axis.magnitude;
            if (len < 1e-4f || DashLength <= 0f) return;
            Vector2 dir = axis / len;
            Vector2 perp = new Vector2(dir.y, -dir.x);
            Vector3 dir3 = new Vector3(dir.x, 0f, dir.y);
            Vector3 perp3 = new Vector3(perp.x, 0f, perp.y);
            Vector3 origin = new Vector3(from.x, MarkingHeight, from.y);

            float period = DashLength + Mathf.Max(0f, DashGap);
            float pos = 0f;
            int safety = 0;
            while (pos < len && safety < 100000)
            {
                float ds = pos;
                float de = Mathf.Min(pos + DashLength, len);
                if (de > ds)
                {
                    int b = verts.Count;
                    verts.Add(origin + dir3 * ds - perp3 * half);
                    verts.Add(origin + dir3 * de - perp3 * half);
                    verts.Add(origin + dir3 * de + perp3 * half);
                    verts.Add(origin + dir3 * ds + perp3 * half);
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                }
                pos += period;
                safety++;
            }
        }

        // Lit matte material — MUST match RoadRenderer.CreateFlatMaterial so
        // these bridge dashes receive lighting/shadows identically to the road
        // body's own markings. (An Unlit material here renders full-bright and
        // looks brighter than the rest of the line through the junction.)
        static Material CreateLitMatte(Color c, string name)
        {
            Material m = new Material(Shader.Find("Standard")) { name = name, color = c };
            m.SetFloat("_Glossiness", 0f);
            m.SetFloat("_Metallic", 0f);
            return m;
        }
    }
}
