// Per-vertex mesh for dead-end caps — a semicircular extension of the
// road past a 1-approach (dead-end) vertex. The half-disc is asphalt
// (the drivable surface continuation); the surrounding half-ring is
// shoulder. Spawned automatically by NetworkRenderer on any vertex
// with exactly 1 incident road.
//
// Geometry:
//   center = setback midpoint at this approach (which == vertex pos
//            for the typical setback=0 dead-end).
//   outward = approach.OuterEdgeDir (away from vertex into road body).
//   perp = cross-direction along the setback line (OuterRight - OuterLeft).
//   The cap bulges in the -outward direction (away from the road
//   body), so its "diameter line" runs along the setback line.
//
// Symmetric — uses average shoulder width when CW/CCW shoulders differ.
// For most road profiles the shoulders match and this is exact.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class DeadEndCapRenderer : MonoBehaviour
    {
        [Header("Source")]
        public VertexApproach Approach;

        [Header("Materials (fall back to colors when null)")]
        public Material AsphaltMaterial;
        public Material ShoulderMaterial;
        public Color AsphaltColor = new Color(0.18f, 0.18f, 0.19f);
        public Color ShoulderColor = new Color(0.55f, 0.55f, 0.55f);

        [Header("Mesh")]
        [Range(8, 64)] public int Segments = 24;
        [Tooltip("World meters per UV unit. Match RoadRenderer's UvTileSize so textures tile continuously across the road↔cap boundary.")]
        public float UvTileSize = 2f;

        Mesh _mesh;

        void Start()
        {
            if (Approach != null) Rebuild();
        }

        public void Rebuild()
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();

            if (Approach == null || Approach.OuterEdgeDir.sqrMagnitude < 1e-6f)
            {
                if (_mesh != null) _mesh.Clear();
                mf.sharedMesh = _mesh;
                return;
            }

            Vector2 center = (Approach.OuterLeft + Approach.OuterRight) * 0.5f;
            Vector2 outward = Approach.OuterEdgeDir.normalized;
            Vector2 cross = Approach.OuterRight - Approach.OuterLeft;
            float totalDiameter = cross.magnitude;
            if (totalDiameter < 1e-4f)
            {
                if (_mesh != null) _mesh.Clear();
                mf.sharedMesh = _mesh;
                return;
            }
            Vector2 perp = cross / totalDiameter;
            float halfTotal = totalDiameter * 0.5f;

            int seg = Mathf.Max(4, Segments);

            // Single full-width half-disc of asphalt — fills the entire
            // road footprint (shoulder-to-shoulder) past the dead-end
            // vertex with one continuous asphalt surface. Matches the
            // cul-de-sac fill pattern: pure pavement, no shoulder ring.
            //
            // Verts:
            //   [0]              = center (fan center, at setback midpoint)
            //   [1 .. seg+1]     = arc (radius = halfTotal) θ ∈ [0, π]
            // At θ=0 → +perp side (≈ OuterRight)
            // At θ=π → -perp side (≈ OuterLeft)
            // At θ=π/2 → apex past the vertex (-outward * halfTotal)
            int arcCount = seg + 1;
            int totalVerts = 1 + arcCount;
            Vector3[] verts = new Vector3[totalVerts];
            Vector2[] uvs = new Vector2[totalVerts];
            int[] tris = new int[seg * 3];

            float invUv = 1f / Mathf.Max(0.001f, UvTileSize);
            verts[0] = new Vector3(center.x, 0f, center.y);
            uvs[0] = new Vector2(center.x * invUv, center.y * invUv);

            for (int i = 0; i <= seg; i++)
            {
                float theta = (i / (float)seg) * Mathf.PI;
                float cs = Mathf.Cos(theta);
                float sn = Mathf.Sin(theta);
                Vector2 dir = cs * perp + sn * (-outward);
                Vector2 p = center + dir * halfTotal;
                int idx = 1 + i;
                verts[idx] = new Vector3(p.x, 0f, p.y);
                uvs[idx] = new Vector2(p.x * invUv, p.y * invUv);
            }

            // Fan triangulation. The arc sweeps east→south→west as θ
            // increases (CW in top-down view), so to get a +Y normal
            // we wind (center, current, next) — the cross product
            // (arc[i] - center) × (arc[i+1] - center) lands on +Y for
            // a CW arc. Got this wrong earlier and the cap was being
            // back-face-culled by Standard shader → invisible.
            for (int i = 0; i < seg; i++)
            {
                int baseT = i * 3;
                tris[baseT + 0] = 0;
                tris[baseT + 1] = 1 + i;
                tris[baseT + 2] = 1 + (i + 1);
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "DeadEndCap" };
                mf.sharedMesh = _mesh;
            }
            else
            {
                _mesh.Clear();
            }
            _mesh.vertices = verts;
            _mesh.uv = uvs;
            _mesh.subMeshCount = 1;
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            // Always reassign so the cap stays in sync with the
            // NetworkRenderer's AsphaltMaterial — the road body and
            // cap need to share the same material instance to look
            // continuous (especially with textured asphalt). The
            // length-only check was caching a stale fallback when
            // the cap was first spawned before AsphaltMaterial was
            // assigned in the Inspector.
            Material asphalt = AsphaltMaterial != null
                ? AsphaltMaterial
                : CreateLitMatte(AsphaltColor, "DeadEndAsphaltMat");
            mr.sharedMaterials = new[] { asphalt };
        }

        static Material CreateLitMatte(Color c, string name)
        {
            Material m = new Material(Shader.Find("Standard")) { name = name, color = c };
            m.SetFloat("_Glossiness", 0f);
            m.SetFloat("_Metallic", 0f);
            return m;
        }
    }
}
