// Least-ASCENT CORRIDOR finder for rail plans. Grids the terrain between two points and runs A* where
// only UPHILL costs (heavily) — so the route seeks the lowest crossing (through the DRAW / saddle / pass),
// hugs contours, and avoids cresting hills, instead of a straight line over every rise. Returns a
// simplified world-XZ polyline (endpoints first/last) the plan layer injects as a node chain.
//
// NOTE: this picks a good CORRIDOR; it does NOT guarantee the alignment grade (rail cuts/fills to hold
// grade — that's a 3D elevation-state search, the proper follow-up). The plan's grade analysis + the
// cut/fill grader still apply to the routed node-line. ClimbWeight trades detour-length vs climbing.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class RailAutoRoute
    {
        // How many metres of horizontal detour are worth avoiding 1 m of climb. Higher = the route
        // works harder to find the draw/pass and stay on contour; lower = straighter, more climb.
        public static float ClimbWeight = 25f;

        // a,b = world XZ. maxGradePct = ruling grade (e.g. 2.2). Returns null + a reason on failure.
        public static List<Vector2> Route(ITerrainSurface surf, Vector2 a, Vector2 b, float maxGradePct,
                                          out string status, float desiredCell = 30f, int maxCells = 90000)
        {
            status = "";
            if (surf == null) { status = "no terrain surface"; return null; }
            float dAB = Vector2.Distance(a, b);
            if (dAB < desiredCell) { status = "endpoints too close to route"; return null; }

            // Region = AABB of a,b padded so the route has room to detour/switchback.
            float margin = Mathf.Clamp(dAB * 0.6f, 300f, 4000f);
            float minx = Mathf.Min(a.x, b.x) - margin, maxx = Mathf.Max(a.x, b.x) + margin;
            float minz = Mathf.Min(a.y, b.y) - margin, maxz = Mathf.Max(a.y, b.y) + margin;
            float w = maxx - minx, h = maxz - minz;

            // Grow the cell if the grid would exceed the node cap.
            float cell = Mathf.Max(desiredCell, Mathf.Sqrt(w * h / Mathf.Max(1, maxCells)));
            int cols = Mathf.Max(2, Mathf.CeilToInt(w / cell) + 1);
            int rows = Mathf.Max(2, Mathf.CeilToInt(h / cell) + 1);
            int N = cols * rows;

            // Sample the terrain once per grid node. Prefer the DEM source — it's map-wide, where
            // ChunkWorld.SampleHeight only covers LOADED chunks (0 elsewhere → would route off cliffs at
            // the bubble edge). Falls back to the active surface off the DEM.
            bool dem = DemChunkSource.Active;
            var hgt = new float[N];
            for (int z = 0; z < rows; z++)
                for (int x = 0; x < cols; x++)
                {
                    float wx = minx + x * cell, wz = minz + z * cell;
                    hgt[z * cols + x] = dem ? DemChunkSource.SampleWorldYAt(wx, wz) : surf.SampleHeight(wx, wz);
                }

            int start = CellOf(a, minx, minz, cell, cols, rows);
            int goal = CellOf(b, minx, minz, cell, cols, rows);
            if (start == goal) { status = "endpoints in the same cell"; return null; }

            int gcx = goal % cols, gcz = goal / cols;

            var g = new float[N];
            var from = new int[N];
            var closed = new bool[N];
            for (int i = 0; i < N; i++) { g[i] = float.PositiveInfinity; from[i] = -1; }
            g[start] = 0f;

            // Min-heap keyed by f = g + heuristic; lazy decrease-key via (oldKey) removal.
            var open = new SortedSet<(float f, int i)>();
            float H(int c) { int cx = c % cols, cz = c / cols; return Mathf.Sqrt((cx - gcx) * (cx - gcx) + (cz - gcz) * (cz - gcz)) * cell; }
            open.Add((H(start), start));

            bool found = false;
            while (open.Count > 0)
            {
                var top = open.Min; open.Remove(top);
                int u = top.i;
                if (closed[u]) continue;
                if (u == goal) { found = true; break; }
                closed[u] = true;
                int ux = u % cols, uz = u / cols;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int vx = ux + dx, vz = uz + dz;
                        if (vx < 0 || vx >= cols || vz < 0 || vz >= rows) continue;
                        int v = vz * cols + vx;
                        if (closed[v]) continue;
                        float dist = cell * ((dx != 0 && dz != 0) ? 1.41421356f : 1f);
                        // Least-ASCENT: only UPHILL costs (heavily), so the route seeks the lowest
                        // crossing — through the draw / saddle — and hugs contours instead of cresting hills.
                        float climb = hgt[v] - hgt[u];
                        if (climb < 0f) climb = 0f;
                        float cost = dist + ClimbWeight * climb;
                        float ng = g[u] + cost;
                        if (ng < g[v])
                        {
                            if (!float.IsPositiveInfinity(g[v])) open.Remove((g[v] + H(v), v));
                            g[v] = ng; from[v] = u;
                            open.Add((ng + H(v), v));
                        }
                    }
            }

            if (!found) { status = "no route within the ruling grade — raise Max grade or move the endpoint"; return null; }

            var path = new List<Vector2>();
            for (int cur = goal; cur != -1; cur = from[cur])
            {
                int cx = cur % cols, cz = cur / cols;
                path.Add(new Vector2(minx + cx * cell, minz + cz * cell));
                if (cur == start) break;
            }
            path.Reverse();

            var simp = Simplify(path, cell * 0.4f);   // keep draw-following bends, drop only grid noise
            status = $"routed {simp.Count} pts on a {cols}×{rows} grid (cell {cell:0} m)";
            return simp;
        }

        static int CellOf(Vector2 p, float minx, float minz, float cell, int cols, int rows)
        {
            int cx = Mathf.Clamp(Mathf.RoundToInt((p.x - minx) / cell), 0, cols - 1);
            int cz = Mathf.Clamp(Mathf.RoundToInt((p.y - minz) / cell), 0, rows - 1);
            return cz * cols + cx;
        }

        // Douglas-Peucker: drop near-collinear points so the chain isn't one node per grid cell.
        static List<Vector2> Simplify(List<Vector2> pts, float tol)
        {
            if (pts.Count < 3) return pts;
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            DP(pts, 0, pts.Count - 1, tol, keep);
            var outp = new List<Vector2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]);
            return outp;
        }

        static void DP(List<Vector2> pts, int i0, int i1, float tol, bool[] keep)
        {
            if (i1 <= i0 + 1) return;
            float maxD = -1f; int idx = -1;
            Vector2 A = pts[i0], B = pts[i1]; Vector2 ab = B - A; float abLen = ab.magnitude;
            for (int i = i0 + 1; i < i1; i++)
            {
                float d = abLen < 1e-4f ? (pts[i] - A).magnitude
                                        : Mathf.Abs(ab.x * (A.y - pts[i].y) - (A.x - pts[i].x) * ab.y) / abLen;
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (maxD > tol) { keep[idx] = true; DP(pts, i0, idx, tol, keep); DP(pts, idx, i1, tol, keep); }
        }
    }
}
