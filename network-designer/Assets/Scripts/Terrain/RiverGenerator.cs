// Procedural rivers for the terrain generator. Standard hydrology pipeline:
//   1. Priority-flood depression fill  - so water can always reach the map edge
//      instead of pooling in noise pits (Barnes et al. 2014).
//   2. D8 flow directions              - each cell drains to its steepest lower neighbour.
//   3. Flow accumulation               - upstream drainage area per cell.
//   4. Threshold by accumulation       - cells over the threshold are river; width grows
//      with drainage area (small streams merge into big rivers downhill).
//   5. Carve                           - lower river cells (and shoulders) to form beds.
//
// Returns a per-cell river WIDTH (metres, 0 = dry) for preview tinting and, later, for
// building the flowing-water mesh. Pure heightfield math — no Unity objects.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class RiverGenerator
    {
        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        // Compute river cells and (optionally) carve channels into `heights` in place.
        // density 0..1 (more = denser/smaller streams). Returns per-cell width in metres.
        public static float[] ComputeAndCarve(float[] heights, int cx, int rz, float cellSize,
                                              float density, float carveDepth, bool carve)
        {
            int n = cx * rz;
            var width = new float[n];
            if (heights == null || heights.Length < n || cx < 3 || rz < 3) return width;
            cellSize = Mathf.Max(0.01f, cellSize);

            float[] filled = PriorityFlood(heights, cx, rz);
            int[] down = FlowDirections(filled, cx, rz, cellSize);
            float[] accum = Accumulate(filled, down, n);

            // density -> fraction of the whole map's drainage needed to count as a river.
            float frac = Mathf.Lerp(0.06f, 0.004f, Mathf.Clamp01(density));
            float threshold = Mathf.Max(4f, frac * n);

            for (int i = 0; i < n; i++)
            {
                if (accum[i] < threshold) continue;
                float w = cellSize * (1f + Mathf.Sqrt(accum[i] / threshold));
                width[i] = Mathf.Min(w, cellSize * 8f);
            }

            if (carve && carveDepth > 0f)
            {
                for (int z = 0; z < rz; z++)
                    for (int x = 0; x < cx; x++)
                    {
                        int i = z * cx + x;
                        if (width[i] <= 0f) continue;
                        float d = carveDepth * Mathf.Clamp01(width[i] / (cellSize * 4f) + 0.4f);
                        heights[i] -= d;
                        for (int k = 0; k < 4; k++)   // 4-neighbour shoulders, shallower
                        {
                            int nx = x + DX[k], nz = z + DZ[k];
                            if (nx < 0 || nz < 0 || nx >= cx || nz >= rz) continue;
                            heights[nz * cx + nx] -= d * 0.4f;
                        }
                    }
            }
            return width;
        }

        // Hydrology ANALYSIS for drainage / catchment planning (no terrain change). Reuses the same pipeline:
        //   ponding[i] = filled - terrain  → metres of standing water that would pool in a depression (> 0 = a basin
        //                that holds water given rain). flood / catchment basins.
        //   accum[i]   = upstream drainage area (cells) → where runoff concentrates (streams / swale / culvert lines).
        // Grid EDGES are treated as outlets, so the window must enclose the catchment (up to surrounding ridges).
        public static void Analyze(float[] heights, int cx, int rz, float cellSize, out float[] ponding, out float[] accum)
        {
            int n = cx * rz;
            ponding = new float[n]; accum = new float[n];
            if (heights == null || heights.Length < n || cx < 3 || rz < 3) return;
            cellSize = Mathf.Max(0.01f, cellSize);
            float[] filled = PriorityFlood(heights, cx, rz);
            int[] down = FlowDirections(filled, cx, rz, cellSize);
            accum = Accumulate(filled, down, n);
            for (int i = 0; i < n; i++) ponding[i] = Mathf.Max(0f, filled[i] - heights[i]);
        }

        // Fill depressions so every cell has a downhill path to the boundary. Boundary
        // cells are the outlets; interior cells get raised to the lowest spill level.
        static float[] PriorityFlood(float[] h, int cx, int rz)
        {
            int n = cx * rz;
            var filled = new float[n];
            var done = new bool[n];
            var heap = new List<int>(n);   // binary min-heap of cell indices, keyed by filled[]

            void Push(int idx)
            {
                heap.Add(idx);
                int i = heap.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (filled[heap[p]] <= filled[heap[i]]) break;
                    (heap[p], heap[i]) = (heap[i], heap[p]);
                    i = p;
                }
            }
            int Pop()
            {
                int top = heap[0];
                int last = heap.Count - 1;
                heap[0] = heap[last];
                heap.RemoveAt(last);
                int i = 0, c = heap.Count;
                while (true)
                {
                    int l = 2 * i + 1, r = 2 * i + 2, s = i;
                    if (l < c && filled[heap[l]] < filled[heap[s]]) s = l;
                    if (r < c && filled[heap[r]] < filled[heap[s]]) s = r;
                    if (s == i) break;
                    (heap[s], heap[i]) = (heap[i], heap[s]);
                    i = s;
                }
                return top;
            }

            for (int z = 0; z < rz; z++)
                for (int x = 0; x < cx; x++)
                    if (x == 0 || z == 0 || x == cx - 1 || z == rz - 1)
                    {
                        int idx = z * cx + x;
                        filled[idx] = h[idx]; done[idx] = true; Push(idx);
                    }

            while (heap.Count > 0)
            {
                int c = Pop();
                int cz = c / cx, cxi = c % cx;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cxi + DX[k], nz = cz + DZ[k];
                    if (nx < 0 || nz < 0 || nx >= cx || nz >= rz) continue;
                    int ni = nz * cx + nx;
                    if (done[ni]) continue;
                    // +epsilon variant: flats get a faint gradient toward the outlet so D8
                    // can route across filled basins instead of dead-ending. `filled` is
                    // only used for routing, never for the final terrain, so this is free.
                    filled[ni] = h[ni] > filled[c] ? h[ni] : filled[c] + 1e-3f;
                    done[ni] = true;
                    Push(ni);
                }
            }
            return filled;
        }

        // Steepest-descent neighbour per cell on the filled surface; -1 = drains off-map.
        static int[] FlowDirections(float[] filled, int cx, int rz, float cellSize)
        {
            int n = cx * rz;
            var down = new int[n];
            float diag = cellSize * 1.41421356f;
            for (int z = 0; z < rz; z++)
                for (int x = 0; x < cx; x++)
                {
                    int i = z * cx + x;
                    if (x == 0 || z == 0 || x == cx - 1 || z == rz - 1) { down[i] = -1; continue; }
                    int best = -1; float bestSlope = 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        int ni = (z + DZ[k]) * cx + (x + DX[k]);
                        float dist = k < 4 ? cellSize : diag;
                        float slope = (filled[i] - filled[ni]) / dist;
                        if (slope > bestSlope) { bestSlope = slope; best = ni; }
                    }
                    down[i] = best;
                }
            return down;
        }

        // Upstream drainage area per cell: process high -> low so each cell's accumulation
        // is final before it's pushed downstream.
        static float[] Accumulate(float[] filled, int[] down, int n)
        {
            var accum = new float[n];
            for (int i = 0; i < n; i++) accum[i] = 1f;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            System.Array.Sort(order, (a, b) => filled[b].CompareTo(filled[a]));
            for (int o = 0; o < n; o++)
            {
                int c = order[o];
                int d = down[c];
                if (d >= 0) accum[d] += accum[c];
            }
            return accum;
        }
    }
}
