// Mesh builder for a single vertex's lane + curb-corner geometry.
//
// Two sub-meshes:
//
//   Sub 0 (asphalt LANE area)
//     A closed polygon with smooth bezier-curved corners on the
//     interior side. Per approach: shrunkStart + shrunkEnd (setback
//     endpoints pulled 1 m·shoulder inward along the setback line).
//     Per corner: interior bezier samples of an INNER quadratic bezier
//     that mirrors the outer bezier with control point at the inner
//     miter (the perpendicular-offset intersection of the corner edges).
//     Fan-triangulated from the vertex center.
//
//   Sub 1 (shoulder CURB STRIP at corners)
//     Per corner: a 1 m·shoulder uniform-width band between the OUTER
//     bezier (curb / outer boundary of the intersection) and the INNER
//     bezier (lane-area boundary at the corner). Tessellated into N
//     quad pairs and triangulated as 2N triangles per corner.
//
// The strips share their inner-side vertices with the lane outline, so
// the boundary between lane area and curb is exact — no gaps, no
// overlap. The bezier curves on both sides are sampled with the same
// parameter t, giving a near-constant 1 m perpendicular width along
// the whole curb. (The offset of a quadratic isn't exactly a quadratic,
// so the width wobbles slightly — about 6% off at t=0.5 for the default
// 90° perpendicular case. Visually imperceptible.)

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Geometry;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class IntersectionRenderer : MonoBehaviour
    {
        [Header("Source")]
        public VertexGeometry Geometry;
        public Vector2 VertexCenter;

        [Header("Tessellation")]
        [Range(2, 64)] public int BezierSamplesPerSegment = 16;

        [Header("UVs")]
        [Tooltip("World meters per UV unit. Must match RoadRenderer.UvTileSize for textures to tile continuously across road→intersection boundaries.")]
        public float UvTileSize = 2f;

        [Header("Materials")]
        public Material AsphaltMaterial;
        public Material ShoulderMaterial;

        [Header("Default colors when materials are null")]
        public Color AsphaltColor = new Color(0.18f, 0.18f, 0.19f);
        public Color ShoulderColor = new Color(0.55f, 0.55f, 0.55f);

        void Start()
        {
            if (Geometry != null) Rebuild();
        }

        public void Rebuild()
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();

            if (Geometry == null || Geometry.Approaches == null
                || Geometry.Approaches.Count < 2
                || Geometry.Outline == null || Geometry.Outline.Count == 0)
            {
                mf.sharedMesh = null;
                return;
            }

            int n = Geometry.Approaches.Count;
            int N = Mathf.Max(2, BezierSamplesPerSegment);

            // -------- Compute per-approach geometry --------
            Vector2[] shrunkStart = new Vector2[n];
            Vector2[] shrunkEnd = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                VertexApproach a = Geometry.Approaches[i];
                Vector2 dir = (a.OuterRight - a.OuterLeft).normalized;
                shrunkStart[i] = a.OuterLeft + dir * a.ShoulderWidthCCW;
                shrunkEnd[i] = a.OuterRight - dir * a.ShoulderWidthCW;
            }

            // Effective outer control + inner miter at each corner.
            //
            // For QuadraticBezier transitions, the outer control is the
            // resolver's OE intersection; the inner miter is the
            // intersection of the two adjacent inner-offset lines.
            //
            // For Line transitions (joint case — two collinear approaches),
            // the resolver leaves Control at its default (Vector2.zero).
            // Treating that as a quadratic control would warp the outer
            // curve toward the world origin, which is exactly the
            // "smudge at the top of the T" artifact. We use the midpoint
            // of (From, To) as the effective control so SampleQuadratic
            // degenerates to a straight line, and we set innerMiter to
            // the midpoint of the perpendicular-offset inner line so the
            // inner sampling also stays straight at the correct depth.
            Vector2[] outerControl = new Vector2[n];
            Vector2[] innerMiter = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                int transIdx = 2 * i + 1;
                if (transIdx >= Geometry.Outline.Count) { mf.sharedMesh = null; return; }
                OutlineSegment trans = Geometry.Outline[transIdx];

                VertexApproach a = Geometry.Approaches[i];
                VertexApproach b = Geometry.Approaches[(i + 1) % n];

                if (trans.Kind == SegmentKind.QuadraticBezier)
                {
                    outerControl[i] = trans.Control;

                    Vector2 e1 = (trans.Control - a.OuterRight).normalized;
                    Vector2 e2 = (b.OuterLeft - trans.Control).normalized;
                    Vector2 p1 = a.OuterRight + PerpRight(e1) * a.ShoulderWidthCW;
                    Vector2 p2 = b.OuterLeft + PerpRight(e2) * b.ShoulderWidthCCW;
                    Vector2? mi = LineIntersect(p1, e1, p2, e2);
                    innerMiter[i] = mi ?? trans.Control;
                }
                else
                {
                    // Joint segment. Midpoint as the effective quadratic
                    // control collapses the outer curve to a straight line.
                    Vector2 mid = (trans.From + trans.To) * 0.5f;
                    outerControl[i] = mid;

                    // For the inner side: take the perpendicular-offset
                    // inner line and use ITS midpoint. The two
                    // perpendicular-offsets are parallel (joint = collinear),
                    // so LineIntersect would return null; use the midpoint
                    // of the inner endpoints instead.
                    Vector2 joinDir = (trans.To - trans.From).normalized;
                    Vector2 innerLeft  = a.OuterRight + PerpRight(joinDir) * a.ShoulderWidthCW;
                    Vector2 innerRight = b.OuterLeft  + PerpRight(joinDir) * b.ShoulderWidthCCW;
                    innerMiter[i] = (innerLeft + innerRight) * 0.5f;
                }
            }

            // -------- Vertex layout --------
            //
            //   [0]                                            = vertex center
            //   [1 .. n·(N+1)]                                 = LANE OUTLINE (CW).
            //     For each approach i, slot of size (N+1) at base = 1 + i·(N+1):
            //       +0  shrunkStart[i]
            //       +1  shrunkEnd[i]
            //       +2..+N  inner bezier i samples at t = 1/N, …, (N-1)/N
            //     Inner bezier i is (shrunkEnd[i], innerMiter[i], shrunkStart[(i+1)%n]).
            //     Its endpoints (t=0, t=1) ARE shrunkEnd[i] and shrunkStart[(i+1)%n],
            //     which sit in this same array (no duplication needed).
            //   [outerBase ..]                                 = corner OUTER bezier samples.
            //     Per corner i, slot of size (N+1) at outerBase + i·(N+1):
            //       outer bezier i sampled at t = 0, 1/N, …, 1.
            int laneSlot = N + 1;
            int laneVerts = n * laneSlot;
            int outerSlot = N + 1;
            int outerBase = 1 + laneVerts;
            int totalVerts = outerBase + n * outerSlot;

            Vector3[] verts = new Vector3[totalVerts];
            verts[0] = ToVec3(VertexCenter);

            for (int i = 0; i < n; i++)
            {
                int b = 1 + i * laneSlot;
                verts[b + 0] = ToVec3(shrunkStart[i]);
                verts[b + 1] = ToVec3(shrunkEnd[i]);

                Vector2 ibFrom = shrunkEnd[i];
                Vector2 ibCtrl = innerMiter[i];
                Vector2 ibTo = shrunkStart[(i + 1) % n];
                for (int k = 1; k <= N - 1; k++)
                {
                    float t = (float)k / N;
                    verts[b + 1 + k] = ToVec3(
                        GeometryResolver.SampleQuadratic(ibFrom, ibCtrl, ibTo, t));
                }
            }

            for (int i = 0; i < n; i++)
            {
                OutlineSegment trans = Geometry.Outline[2 * i + 1];
                int ob = outerBase + i * outerSlot;
                for (int k = 0; k <= N; k++)
                {
                    float t = (float)k / N;
                    verts[ob + k] = ToVec3(
                        GeometryResolver.SampleQuadratic(trans.From, outerControl[i], trans.To, t));
                }
            }

            // -------- Lane fan (sub 0) --------
            // n·(N+1) outline vertices in CW order → that many fan triangles.
            int[] laneTris = new int[laneVerts * 3];
            for (int v = 0; v < laneVerts; v++)
            {
                int next = (v + 1) % laneVerts;
                laneTris[v * 3 + 0] = 0;
                laneTris[v * 3 + 1] = 1 + v;
                laneTris[v * 3 + 2] = 1 + next;
            }

            // -------- Corner strips (sub 1) --------
            // For each corner i and each k in [0, N-1], a quad between
            // (outer[k], outer[k+1], inner[k+1], inner[k]) → 2 triangles.
            // Winding (CW from +Y, front-facing): (outer[k], outer[k+1],
            // inner[k+1]) and (outer[k], inner[k+1], inner[k]).
            int[] stripTris = new int[n * N * 6];
            int w = 0;
            for (int i = 0; i < n; i++)
            {
                int ob = outerBase + i * outerSlot;
                for (int k = 0; k < N; k++)
                {
                    int outerK = ob + k;
                    int outerKp1 = ob + k + 1;
                    int innerK = InnerSampleIdx(i, k, n, N);
                    int innerKp1 = InnerSampleIdx(i, k + 1, n, N);

                    stripTris[w++] = outerK;
                    stripTris[w++] = outerKp1;
                    stripTris[w++] = innerKp1;

                    stripTris[w++] = outerK;
                    stripTris[w++] = innerKp1;
                    stripTris[w++] = innerK;
                }
            }

            // World-aligned UVs (matches RoadRenderer) so textures tile
            // continuously across the road/intersection boundary.
            float uvScale = 1f / Mathf.Max(0.001f, UvTileSize);
            Vector2[] uvs = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                uvs[i] = new Vector2(verts[i].x * uvScale, verts[i].z * uvScale);
            }

            // Reuse the existing Mesh — avoid the allocation every Rebuild.
            Mesh mesh = mf.sharedMesh;
            if (mesh == null)
            {
                mesh = new Mesh { name = $"Intersection_{Geometry.VertexId}" };
                mf.sharedMesh = mesh;
            }
            else
            {
                mesh.Clear();
            }
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(laneTris, 0);
            mesh.SetTriangles(stripTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            if (mr.sharedMaterials == null || mr.sharedMaterials.Length != 2)
            {
                mr.sharedMaterials = new[]
                {
                    AsphaltMaterial  != null ? AsphaltMaterial  : CreateLitMatte(AsphaltColor,  "AsphaltMat"),
                    ShoulderMaterial != null ? ShoulderMaterial : CreateLitMatte(ShoulderColor, "ShoulderMat"),
                };
            }
        }

        // Standard shader with matte settings — gives the surface real
        // lighting / shadow behavior. Glossiness 0 + metallic 0 reads as
        // asphalt-ish under any lighting.
        static Material CreateLitMatte(Color c, string name)
        {
            Material m = new Material(Shader.Find("Standard")) { name = name, color = c };
            m.SetFloat("_Glossiness", 0f);
            m.SetFloat("_Metallic", 0f);
            return m;
        }

        // Maps (corner i, bezier sample k) back to its vertex index in the
        // lane outline. Sample k=0 is shrunkEnd[i]; sample k=N is
        // shrunkStart[(i+1) % n]; samples 1..N-1 are the interior samples
        // stored in approach i's slot.
        static int InnerSampleIdx(int i, int k, int n, int N)
        {
            int laneSlot = N + 1;
            if (k == 0) return 1 + i * laneSlot + 1;                 // shrunkEnd[i]
            if (k == N) return 1 + ((i + 1) % n) * laneSlot + 0;     // shrunkStart[(i+1)%n]
            return 1 + i * laneSlot + 1 + k;                          // interior k
        }

        // -----------------------------------------------------------------
        // Math helpers
        // -----------------------------------------------------------------

        static Vector2 PerpRight(Vector2 v) => new Vector2(v.y, -v.x);

        static Vector2? LineIntersect(Vector2 p1, Vector2 d1, Vector2 p2, Vector2 d2)
        {
            float det = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(det) < 1e-6f) return null;
            Vector2 diff = p2 - p1;
            float t = (diff.x * d2.y - diff.y * d2.x) / det;
            return p1 + t * d1;
        }

        static Vector3 ToVec3(Vector2 v) => new Vector3(v.x, 0f, v.y);
    }
}
