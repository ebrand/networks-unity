// Builds topographic (iso-elevation) contour lines from a TerrainField using
// marching squares: for every multiple of `interval` metres of elevation,
// trace where that level crosses each grid cell and emit line segments.
//
// Output is a MeshTopology.Lines mesh in the SAME centered-local space as
// TerrainMeshBuilder (so it parents under the terrain GameObject and inherits
// its transform). Each contour vertex sits at Y = level (+ a small lift), i.e.
// exactly on the surface, since that's where the height equals the level.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkDesigner.Terrain
{
    public static class TerrainContourBuilder
    {
        // Marching-squares case -> edge indices, paired (each consecutive pair
        // is one segment). Corner bits: c00=1, c10=2, c11=4, c01=8. Edges:
        // 0=bottom(c00-c10) 1=right(c10-c11) 2=top(c11-c01) 3=left(c01-c00).
        static readonly int[][] CaseEdges =
        {
            new int[0],        // 0
            new[] { 0, 3 },    // 1
            new[] { 0, 1 },    // 2
            new[] { 1, 3 },    // 3
            new[] { 1, 2 },    // 4
            new[] { 0, 3, 1, 2 }, // 5 (saddle)
            new[] { 0, 2 },    // 6
            new[] { 2, 3 },    // 7
            new[] { 2, 3 },    // 8
            new[] { 0, 2 },    // 9
            new[] { 0, 1, 2, 3 }, // 10 (saddle)
            new[] { 1, 2 },    // 11
            new[] { 1, 3 },    // 12
            new[] { 0, 1 },    // 13
            new[] { 0, 3 },    // 14
            new int[0],        // 15
        };

        // Reused across calls so per-frame (live) rebuilds don't allocate/GC.
        static readonly List<Vector3> verts = new List<Vector3>();
        static readonly List<int> idx = new List<int>();

        // dashLength <= 0 => solid lines; otherwise each segment is broken into
        // dashLength-on / dashGap-off pieces.
        public static void Build(TerrainField field, float interval, float lift,
            float dashLength, float dashGap, Mesh mesh)
        {
            mesh.Clear();
            if (field == null || interval <= 0f) return;

            int cx = field.ColumnsX, rz = field.RowsZ;
            float cs = field.CellSize;
            float halfW = (cx - 1) * cs * 0.5f;
            float halfL = (rz - 1) * cs * 0.5f;

            verts.Clear();
            idx.Clear();

            for (int z = 0; z < rz - 1; z++)
            {
                for (int x = 0; x < cx - 1; x++)
                {
                    float h00 = field.GetHeight(x, z);
                    float h10 = field.GetHeight(x + 1, z);
                    float h11 = field.GetHeight(x + 1, z + 1);
                    float h01 = field.GetHeight(x, z + 1);

                    float cmin = Mathf.Min(Mathf.Min(h00, h10), Mathf.Min(h11, h01));
                    float cmax = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h11, h01));
                    int lo = Mathf.CeilToInt(cmin / interval);
                    int hi = Mathf.FloorToInt(cmax / interval);

                    for (int li = lo; li <= hi; li++)
                    {
                        float L = li * interval;
                        int ci = (h00 > L ? 1 : 0) | (h10 > L ? 2 : 0)
                               | (h11 > L ? 4 : 0) | (h01 > L ? 8 : 0);
                        int[] edges = CaseEdges[ci];
                        for (int e = 0; e + 1 < edges.Length; e += 2)
                        {
                            Vector3 a = EdgePoint(edges[e], L, x, z, cs, halfW, halfL, lift, h00, h10, h11, h01);
                            Vector3 b = EdgePoint(edges[e + 1], L, x, z, cs, halfW, halfL, lift, h00, h10, h11, h01);
                            EmitSegment(a, b, dashLength, dashGap);
                        }
                    }
                }
            }

            if (verts.Count == 0) return;
            mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetIndices(idx, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
        }

        // Emit a->b as one line segment when dashLength<=0, else as dash/gap
        // pieces. Phase restarts per segment (contours aren't traced into
        // continuous polylines), which reads fine at terrain scale.
        static void EmitSegment(Vector3 a, Vector3 b, float dashLength, float dashGap)
        {
            if (dashLength <= 0f)
            {
                int s = verts.Count;
                verts.Add(a); verts.Add(b);
                idx.Add(s); idx.Add(s + 1);
                return;
            }
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-5f) return;
            Vector3 dir = d / len;
            float period = dashLength + Mathf.Max(0f, dashGap);
            for (float pos = 0f; pos < len; pos += period)
            {
                float e0 = pos;
                float e1 = Mathf.Min(pos + dashLength, len);
                int s = verts.Count;
                verts.Add(a + dir * e0);
                verts.Add(a + dir * e1);
                idx.Add(s); idx.Add(s + 1);
            }
        }

        // Interpolated crossing point of level L on a cell edge, in centered
        // local space. Edge endpoints are grid coords; Y is the level itself.
        static Vector3 EdgePoint(int edge, float L, int x, int z,
            float cs, float halfW, float halfL, float lift,
            float h00, float h10, float h11, float h01)
        {
            float gxA, gzA, hA, gxB, gzB, hB;
            switch (edge)
            {
                case 0: gxA = x;     gzA = z;     hA = h00; gxB = x + 1; gzB = z;     hB = h10; break; // bottom
                case 1: gxA = x + 1; gzA = z;     hA = h10; gxB = x + 1; gzB = z + 1; hB = h11; break; // right
                case 2: gxA = x + 1; gzA = z + 1; hA = h11; gxB = x;     gzB = z + 1; hB = h01; break; // top
                default: gxA = x;    gzA = z + 1; hA = h01; gxB = x;     gzB = z;     hB = h00; break; // left (3)
            }
            float denom = hB - hA;
            float t = Mathf.Abs(denom) < 1e-6f ? 0.5f : Mathf.Clamp01((L - hA) / denom);
            float gx = Mathf.Lerp(gxA, gxB, t);
            float gz = Mathf.Lerp(gzA, gzB, t);
            return new Vector3(gx * cs - halfW, L + lift, gz * cs - halfL);
        }
    }
}
