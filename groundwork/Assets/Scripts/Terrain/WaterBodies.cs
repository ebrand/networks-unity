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

        public class Body
        {
            public Vector2 Seed;       // world XZ the fill grew from
            public float Level;        // world Y of this body's surface
            public GameObject Go;
            public Mesh Mesh;
            public int CellCount;      // last fill size (for the UI / leak diagnostics)
        }

        static readonly List<Body> _bodies = new List<Body>();
        static Material _mat;
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
            if (_bodies[index].Go != null) Object.Destroy(_bodies[index].Go);
            _bodies.RemoveAt(index);
        }

        public static void Clear()
        {
            foreach (Body b in _bodies) if (b.Go != null) Object.Destroy(b.Go);
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

        static void BuildSurface(Body b, List<Vector2Int> cells)
        {
            EnsureRoot();
            if (b.Go == null)
            {
                b.Go = new GameObject("WaterBody") { hideFlags = HideFlags.DontSave };
                b.Go.transform.SetParent(_root.transform, false);
                var mf = b.Go.AddComponent<MeshFilter>();
                b.Mesh = new Mesh { name = "WaterBodyMesh" };
                mf.sharedMesh = b.Mesh;
                var mr = b.Go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = Mat();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            b.Mesh.Clear();
            if (cells.Count == 0) return;
            float y = b.Level;
            // SHARED corner vertices (one per grid point, not 4 per cell) so a fine cell size stays cheap.
            var idx = new Dictionary<long, int>(cells.Count * 2);
            var verts = new List<Vector3>(cells.Count);
            var tris = new List<int>(cells.Count * 6);
            int Corner(int gx, int gz)
            {
                long k = ((long)gx << 32) ^ (uint)gz;
                if (!idx.TryGetValue(k, out int v)) { v = verts.Count; verts.Add(new Vector3(gx * CellSize, y, gz * CellSize)); idx[k] = v; }
                return v;
            }
            foreach (Vector2Int c in cells)
            {
                int a = Corner(c.x, c.y), bb = Corner(c.x, c.y + 1), cc = Corner(c.x + 1, c.y + 1), d = Corner(c.x + 1, c.y);
                tris.Add(a); tris.Add(bb); tris.Add(cc); tris.Add(a); tris.Add(cc); tris.Add(d);
            }
            if (verts.Count > 65000) b.Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            b.Mesh.SetVertices(verts);
            b.Mesh.SetTriangles(tris, 0);
            var up = new Vector3[verts.Count];
            for (int i = 0; i < up.Length; i++) up[i] = Vector3.up;
            b.Mesh.SetNormals(up);
            b.Mesh.RecalculateBounds();
        }

        static Material Mat()
        {
            // Share the chunk water's colour/smoothness so bodies match the global plane's look.
            if (_mat == null)
                _mat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(ChunkOverlays.WaterColor, ChunkOverlays.WaterSmoothness, "WaterBody");
            return _mat;
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
            _root = null; _mat = null;
        }
    }
}
