// Per-vertex mesh that carries a road's CENTER MEDIAN across a promoted
// collinear 2-approach junction. The median is part of each road body and
// stops at the setback line, so when two median-bearing roads meet at such
// a junction (or a turn-lane road meets a median road, where the dedicated
// turn-lane median has already flared to ~the neighbor's median width) the
// center strip is left broken across the gap. This renderer bridges it.
//
// Geometry: a short median extrusion lofted between the two approaches'
// Centerline points (the axis points on each setback line), centered on the
// axis, with the half-width interpolated linearly from Width0 at Center0 to
// Width1 at Center1 — so unequal medians taper. Top + 4 walls + 2 end caps,
// same winding convention as RoadRenderer's BuildStraightMedianExtrusion so
// it reads continuously with the road-body medians it joins.
//
// Centered-on-axis assumption: correct in AxisSplit centerline mode (and for
// symmetric two-way roads in either mode). On an asymmetric two-way road in
// GeometricCenter mode the body median is offset from the axis by the
// centering shift, which this bridge does not reproduce — acceptable for now.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Rendering
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class JunctionMedianBridgeRenderer : MonoBehaviour
    {
        [Header("Source (world XZ)")]
        public Vector2 Center0;
        public Vector2 Center1;
        public float Width0;
        public float Width1;

        [Header("Style")]
        public float Height = 0.15f;
        public Material MedianMaterial;
        public Color MedianColor = new Color(0.45f, 0.45f, 0.45f);
        public float UvTileSize = 2f;

        Mesh _mesh;

        void Start()
        {
            Rebuild();
        }

        public void Rebuild()
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();

            Vector2 axis = Center1 - Center0;
            float len = axis.magnitude;
            if (len < 1e-4f || (Width0 <= 0f && Width1 <= 0f))
            {
                if (_mesh != null) _mesh.Clear();
                mf.sharedMesh = _mesh;
                return;
            }

            Vector2 dir = axis / len;
            Vector2 right = new Vector2(dir.y, -dir.x); // PerpRight
            float half0 = Mathf.Max(0f, Width0) * 0.5f;
            float half1 = Mathf.Max(0f, Width1) * 0.5f;
            float h = Mathf.Max(Height, 0.01f);

            // Bottom corners (local y = 0; the GameObject sits at MeshLift).
            // a* = Center0 end, b* = Center1 end. Lo = -right side, Hi = +right.
            Vector3 aLoB = To3(Center0 - right * half0);
            Vector3 aHiB = To3(Center0 + right * half0);
            Vector3 bLoB = To3(Center1 - right * half1);
            Vector3 bHiB = To3(Center1 + right * half1);
            Vector3 up = Vector3.up * h;
            Vector3 aLoT = aLoB + up, aHiT = aHiB + up;
            Vector3 bLoT = bLoB + up, bHiT = bHiB + up;

            var verts = new List<Vector3>();
            var tris = new List<int>();
            // Same face order/winding as RoadRenderer.BuildStraightMedianExtrusion
            // (forward = Center0→Center1, right = PerpRight).
            EmitQuad(verts, tris, aLoT, bLoT, bHiT, aHiT); // top (+Y)
            EmitQuad(verts, tris, aLoB, aLoT, bLoT, bLoB); // -right wall
            EmitQuad(verts, tris, bHiB, bHiT, aHiT, aHiB); // +right wall
            EmitQuad(verts, tris, aHiB, aHiT, aLoT, aLoB); // Center0 end cap
            EmitQuad(verts, tris, bLoB, bLoT, bHiT, bHiB); // Center1 end cap

            float invUv = 1f / Mathf.Max(0.001f, UvTileSize);
            var uvs = new Vector2[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                uvs[i] = new Vector2(verts[i].x * invUv, verts[i].z * invUv);

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "JunctionMedianBridge" };
                mf.sharedMesh = _mesh;
            }
            else
            {
                _mesh.Clear();
            }
            _mesh.SetVertices(verts);
            _mesh.uv = uvs;
            _mesh.subMeshCount = 1;
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            Material median = MedianMaterial != null
                ? MedianMaterial
                : CreateLitMatte(MedianColor, "JunctionMedianBridgeMat");
            mr.sharedMaterials = new[] { median };
        }

        static Vector3 To3(Vector2 p) => new Vector3(p.x, 0f, p.y);

        static void EmitQuad(List<Vector3> verts, List<int> tris,
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            int b = verts.Count;
            verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
            tris.Add(b);     tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b);     tris.Add(b + 2); tris.Add(b + 3);
        }

        static Material CreateLitMatte(Color c, string name)
        {
            return PipelineMaterials.CreateLitMatte(c, name);
        }
    }
}
