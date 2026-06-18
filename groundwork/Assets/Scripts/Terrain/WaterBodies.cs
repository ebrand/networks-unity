// Multiple independent WATER BODIES, each at its OWN level — for the dam case the single global water plane
// (ChunkOverlays) can't represent: a reservoir held high on one side, tailwater low on the other.
//
// Each body is a SEED point + a LEVEL. A bounded flood-fill over the terrain heightfield grows the body from the
// seed across every connected cell whose ground is below the level, stopping where terrain rises above it — so a
// dam (raised terrain across a gorge) naturally separates two fills at two levels. The flooded cells are rendered
// as a flat translucent surface at the level; terrain higher than the level pokes through for a free shoreline.
//
// Phase 1: in-session only (not yet persisted) and a blocky per-cell surface (smooth shoreline = later). Renders
// independently of the global ChunkOverlays plane — turn that off when using bodies for a multi-level scene.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class WaterBodies
    {
        public const float CellSize = 8f;      // flood-fill / surface cell (m). Finer = smoother shoreline (shared-corner mesh keeps it cheap).
        public const int MaxCells = 300000;    // safety budget per body (~ 19 km² at 8 m) so a leak can't run away.
        public static float SeedRise = 5f;     // m the surface sits ABOVE the clicked ground (= water depth at the click). Exposed in the Terrain palette.

        public class Body
        {
            public Vector2 Seed;       // world XZ the fill grew from
            public float Level;        // world Y of this body's surface
            public GameObject Go;
            public Mesh Mesh;          // a single flat quad over the basin bounding box
            public Material Mat;       // transparent, mask-textured (per body)
            public Texture2D Mask;     // basin footprint (alpha = inside); clips the quad to the basin
            public int CellCount;      // last fill size (for the UI / leak diagnostics)
        }

        const int MaxMaskDim = 2048;   // cap the mask texture side; coarser past this (terrain occlusion hides it anyway)

        static readonly List<Body> _bodies = new List<Body>();
        static GameObject _root;

        public static IReadOnlyList<Body> All => _bodies;
        public static int Count => _bodies.Count;

        // Add a body seeded at `seedWorldXZ`, filling up to `level`. Returns it (CellCount==0 if the seed is already
        // above the level — i.e. not in a basin). Re-fills + meshes immediately.
        public static Body Add(Vector2 seedWorldXZ, float level)
        {
            var b = new Body { Seed = seedWorldXZ, Level = level };
            _bodies.Add(b);
            Refill(b);
            return b;
        }

        public static void SetLevel(int index, float level)
        {
            if (index < 0 || index >= _bodies.Count) return;
            _bodies[index].Level = level;
            Refill(_bodies[index]);
        }

        public static void Remove(int index)
        {
            if (index < 0 || index >= _bodies.Count) return;
            Destroy(_bodies[index]);
            _bodies.RemoveAt(index);
        }

        static void Destroy(Body b)
        {
            if (b.Go != null) Object.Destroy(b.Go);
            if (b.Mat != null) Object.Destroy(b.Mat);
            if (b.Mask != null) Object.Destroy(b.Mask);
            b.Go = null; b.Mat = null; b.Mask = null;
        }

        public static void Clear()
        {
            foreach (Body b in _bodies) Destroy(b);
            _bodies.Clear();
        }

        // Re-flood + re-mesh EVERY body — call after terrain edits (the basins changed) or a colour/look change.
        public static void RebuildAll()
        {
            foreach (Body b in _bodies) Refill(b);
        }

        // ── flood-fill + surface build ───────────────────────────────────────────────────────────────────
        static void Refill(Body b)
        {
            var cells = Flood(b.Seed, b.Level);
            b.CellCount = cells.Count;
            BuildSurface(b, cells);
        }

        // 4-neighbour flood from the seed cell across cells whose ground is below `level`, bounded by MaxCells.
        static List<Vector2Int> Flood(Vector2 seed, float level)
        {
            var outCells = new List<Vector2Int>();
            int sx = Mathf.FloorToInt(seed.x / CellSize), sz = Mathf.FloorToInt(seed.y / CellSize);
            // Seed must itself be underwater (in a basin) — else nothing to fill.
            if (Ground(sx, sz) >= level) return outCells;

            var seen = new HashSet<long>();
            var q = new Queue<Vector2Int>();
            void Push(int x, int z) { if (!InBounds(x, z)) return; long k = ((long)x << 32) ^ (uint)z; if (seen.Add(k)) q.Enqueue(new Vector2Int(x, z)); }
            Push(sx, sz);
            while (q.Count > 0 && outCells.Count < MaxCells)
            {
                Vector2Int c = q.Dequeue();
                if (Ground(c.x, c.y) >= level) continue;   // ground above the surface here → shoreline / dam, don't flood
                outCells.Add(c);
                Push(c.x + 1, c.y); Push(c.x - 1, c.y); Push(c.x, c.y + 1); Push(c.x, c.y - 1);
            }
            if (outCells.Count >= MaxCells)
                Debug.LogWarning($"[WaterBodies] fill hit the {MaxCells}-cell budget (level {level:0} m) — likely leaking past a low saddle; " +
                                 "lower the level or raise the barrier (dam).");
            return outCells;
        }

        static float Ground(int cx, int cz) => ChunkWorld.SampleHeight((cx + 0.5f) * CellSize, (cz + 0.5f) * CellSize);

        // Treat the downloaded mosaic boundary as a WALL: beyond it SampleHeight returns the flat no-data floor
        // (NormMin), which is below any level, so without this the fill runs off the edge and wraps around to other
        // basins. Inside the mosaic (or with no DEM source) everything is fillable.
        static bool InBounds(int cx, int cz)
        {
            if (!DemChunkSource.Active) return true;
            float wx = (cx + 0.5f) * CellSize, wz = (cz + 0.5f) * CellSize;
            return wx >= DemChunkSource.OriginX && wx <= DemChunkSource.OriginX + DemChunkSource.Cols * DemChunkSource.TileMetersX
                && wz >= DemChunkSource.OriginZ && wz <= DemChunkSource.OriginZ + DemChunkSource.Rows * DemChunkSource.TileMetersZ;
        }

        // MASK-QUAD surface: a single flat quad over the basin's bounding box, clipped to the basin by a footprint
        // MASK texture (alpha = inside). The TERRAIN occludes the quad at the waterline, so the shoreline is at
        // terrain-mesh resolution (identical to the global plane) for ~the cost of one quad — no fitted perimeter mesh.
        static void BuildSurface(Body b, List<Vector2Int> cells)
        {
            EnsureRoot();
            if (b.Go == null)
            {
                b.Go = new GameObject("WaterBody") { hideFlags = HideFlags.DontSave };
                b.Go.transform.SetParent(_root.transform, false);
                var mf = b.Go.AddComponent<MeshFilter>();
                b.Mesh = new Mesh { name = "WaterBodyQuad" };
                mf.sharedMesh = b.Mesh;
                var mr = b.Go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            b.Mesh.Clear();
            var rend = b.Go.GetComponent<MeshRenderer>();
            if (cells.Count == 0) { rend.enabled = false; return; }
            rend.enabled = true;

            // Cell-index bounding box → world bounding box (cell (cx,cz) spans [cx,cx+1]·CellSize).
            int cxMin = int.MaxValue, cxMax = int.MinValue, czMin = int.MaxValue, czMax = int.MinValue;
            foreach (Vector2Int c in cells)
            { if (c.x < cxMin) cxMin = c.x; if (c.x > cxMax) cxMax = c.x; if (c.y < czMin) czMin = c.y; if (c.y > czMax) czMax = c.y; }
            int wCells = cxMax - cxMin + 1, hCells = czMax - czMin + 1;
            float minX = cxMin * CellSize, maxX = (cxMax + 1) * CellSize;
            float minZ = czMin * CellSize, maxZ = (czMax + 1) * CellSize;
            float y = b.Level;

            // Footprint mask: 1 texel per cell, downsampled if the basin is huge (the terrain hides the mask's own
            // resolution — the mask only has to separate THIS basin from neighbours, and basin divides are high ground).
            int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(wCells, hCells) / (float)MaxMaskDim));
            int tw = Mathf.Max(1, Mathf.CeilToInt(wCells / (float)step)), th = Mathf.Max(1, Mathf.CeilToInt(hCells / (float)step));
            if (b.Mask == null || b.Mask.width != tw || b.Mask.height != th)
            {
                if (b.Mask != null) Object.Destroy(b.Mask);
                b.Mask = new Texture2D(tw, th, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            }
            var px = new Color32[tw * th];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);   // white, fully outside
            foreach (Vector2Int c in cells)
            {
                int tx = (c.x - cxMin) / step, tz = (c.y - czMin) / step;
                px[tz * tw + tx] = new Color32(255, 255, 255, 255);                       // inside the basin
            }
            b.Mask.SetPixels32(px); b.Mask.Apply(false);

            // Per-body transparent material tinted by the chunk water colour; the mask's alpha confines it to the basin
            // (final alpha = WaterColor.a · mask.a → 0 outside, translucent inside).
            if (b.Mat == null)
                b.Mat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(ChunkOverlays.WaterColor, ChunkOverlays.WaterSmoothness, "WaterBodyMask");
            b.Mat.mainTexture = b.Mask;
            if (b.Mat.HasProperty("_BaseMap")) b.Mat.SetTexture("_BaseMap", b.Mask);
            rend.sharedMaterial = b.Mat;

            // One quad over the world bounding box, UV 0..1 (so cell (cxMin,czMin)→UV(0,0)).
            b.Mesh.SetVertices(new List<Vector3> {
                new Vector3(minX, y, minZ), new Vector3(minX, y, maxZ), new Vector3(maxX, y, maxZ), new Vector3(maxX, y, minZ) });
            b.Mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            b.Mesh.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            b.Mesh.SetTriangles(new List<int> { 0, 1, 2, 0, 2, 3 }, 0);
            b.Mesh.RecalculateBounds();
        }

        static void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("WaterBodies") { hideFlags = HideFlags.DontSave };
        }

        public static void Teardown()
        {
            Clear();
            if (_root != null) Object.Destroy(_root);
            _root = null;
        }
    }
}
