// A STREAMING chunked terrain world — MESH-based, with SCREEN-SPACE LOD + VIEW-FOOTPRINT streaming
// (tuned for top-down / iso god-mode, not first-person). The streaming backbone is unchanged: a
// budgeted load/cull of chunks around what the camera is looking at, with per-chunk persistence.
// Two things are camera-driven instead of distance-ring-driven:
//   • LOD: each chunk's mesh resolution = f(its on-screen size). Zoom out → all chunks coarsen
//     together (whole map cheap); zoom in → the visible patch goes to the near res (up to 1 m).
//     So rendered triangles are bounded by the SCREEN, not the map size or zoom.
//   • Streaming: the resident set follows the camera's ground FOOTPRINT (+margin), capped at
//     Radius. Zoom out → bigger footprint, more (coarse) chunks; zoom in → fewer (fine) chunks.
//
// Heights are a per-chunk world-Y float[] at that chunk's LOD res (regenerated procedurally on LOD
// change — deterministic, seamless via global-node hashing). Sculpt edits persist per chunk,
// res-tagged. SEAMS: no skirts yet → small cracks at LOD boundaries (next polish step).

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class ChunkWorld
    {
        public const float ChunkSize = 1000f;
        public static int Res = 1025;            // MAX / near res; snaps 129/257/513/1025
        public static float PixelsPerVertex = 6f; // screen-space error: bigger = coarser/cheaper
        public static int Radius = 10;            // MAX footprint cap (chunks), safety on zoom-out
        public static bool FillRadius = false;    // force the bubble to the FULL Radius (ignore the view footprint)
        public static int PreloadDepth = 1;       // margin chunks beyond the view footprint
        public static int Budget = 4;             // chunk builds/rebuilds per frame
        const string RootName = "ChunkWorld";
        public static float AmpMeters = 0f;       // max terrain height (multi-octave); 0 = flat
        static readonly int[] ResLevels = { 65, 129, 257, 513, 1025 };

        public static bool Active { get; private set; }
        public static bool LockBubble = true;     // frozen by default: pan/zoom freely, no cull/load
        public static bool StreamHeld = false;    // hold-to-stream key (set each frame by the streamer)
        static bool Streaming => !LockBubble || StreamHeld;

        class Chunk
        {
            public GameObject Go;
            public MeshFilter Mf;
            public MeshRenderer Mr;
            public MeshCollider Mc;
            public Mesh Mesh;
            public float[] H;
            public int LodRes;
        }

        public static bool ShowGrid = true;      // 1 km major / 100 m minor grid painted on the ground
        static GameObject _root;
        static Material _mat;
        static Texture2D _gridTex;
        static string _editDir;
        static Camera _cam;
        static readonly Dictionary<Vector2Int, Chunk> _chunks = new Dictionary<Vector2Int, Chunk>();
        static readonly Dictionary<Vector2Int, HashSet<int>> _dirty = new Dictionary<Vector2Int, HashSet<int>>();
        static readonly HashSet<Vector2Int> _everLoaded = new HashSet<Vector2Int>();
        static readonly Dictionary<Vector2Int, float> _loadTime = new Dictionary<Vector2Int, float>();
        static readonly List<Vector2Int> _pending = new List<Vector2Int>();
        static Vector2Int _center = new Vector2Int(int.MinValue, int.MinValue);
        static Vector2Int _lastLodCenter = new Vector2Int(int.MinValue, int.MinValue);
        static int _lastFoot = -1;
        static float _lastDist = -1f;
        static readonly Dictionary<int, Vector3[]> _vertsByRes = new Dictionary<int, Vector3[]>();
        static readonly Dictionary<int, int[]> _trisByRes = new Dictionary<int, int[]>();
        static readonly Dictionary<int, Vector2[]> _uvByRes = new Dictionary<int, Vector2[]>();

        public static bool HasWorld => Active && _chunks.Count > 0;
        public static int LoadedCount => _chunks.Count;

        // World-space XZ bounds of the resident set (the "bubble") — for the minimap overlay.
        public static bool TryLoadedBounds(out Vector3 center, out float sizeX, out float sizeZ)
        {
            center = default; sizeX = sizeZ = 0f;
            if (_chunks.Count == 0) return false;
            int minx = int.MaxValue, maxx = int.MinValue, minz = int.MaxValue, maxz = int.MinValue;
            foreach (var c in _chunks.Keys)
            {
                if (c.x < minx) minx = c.x; if (c.x > maxx) maxx = c.x;
                if (c.y < minz) minz = c.y; if (c.y > maxz) maxz = c.y;
            }
            float x0 = minx * ChunkSize, x1 = (maxx + 1) * ChunkSize;
            float z0 = minz * ChunkSize, z1 = (maxz + 1) * ChunkSize;
            center = new Vector3((x0 + x1) * 0.5f, 0f, (z0 + z1) * 0.5f);
            sizeX = x1 - x0; sizeZ = z1 - z0;
            return true;
        }
        public static Vector2Int ChunkAt(float x, float z) => ChunkOf(x, z);
        public static bool IsLoaded(Vector2Int c) => _chunks.ContainsKey(c);
        public static bool IsVisibleChunk(Vector2Int c) => _chunks.ContainsKey(c);
        public static int LodLevelOf(Vector2Int c) => _chunks.TryGetValue(c, out var ch) ? LevelOfRes(ch.LodRes) : -1;
        public static bool WasVisited(Vector2Int c) => _everLoaded.Contains(c);
        public static float LoadedAt(Vector2Int c) => _loadTime.TryGetValue(c, out float t) ? t : -1f;

        public static void Begin(string editDir)
        {
            if (Active) return;
            _editDir = editDir;
            try { if (!string.IsNullOrEmpty(_editDir)) Directory.CreateDirectory(_editDir); } catch { }
            _root = new GameObject(RootName);
            if (_mat == null) _mat = NetworkDesigner.PipelineMaterials.CreateLitMatte(Color.white, "ChunkGround");
            ApplyGridMaterial();
            _center = new Vector2Int(int.MinValue, int.MinValue);
            _lastLodCenter = new Vector2Int(int.MinValue, int.MinValue);
            _lastFoot = -1; _lastDist = -1f; StreamHeld = false; _pending.Clear();
            Active = true;
        }

        public static void End()
        {
            if (!Active) return;
            foreach (var kv in _chunks) SaveChunkEdits(kv.Key);
            if (_root != null) { if (Application.isPlaying) Object.Destroy(_root); else Object.DestroyImmediate(_root); }
            _root = null; _chunks.Clear(); _dirty.Clear();
            _everLoaded.Clear(); _loadTime.Clear(); _pending.Clear();
            Active = false;
        }

        public static void SetVisible(bool on) { if (_root != null && _root.activeSelf != on) _root.SetActive(on); }
        public static void SaveAll() { foreach (var kv in _chunks) SaveChunkEdits(kv.Key); }

        public static void SetGrid(bool on) { ShowGrid = on; ApplyGridMaterial(); }

        // Set the terrain max height and regenerate every loaded chunk at the new amplitude
        // (keeps each chunk's LOD res). Heavy on big/high-res bubbles — expect a hitch per change.
        public static void SetAmplitude(float a)
        {
            AmpMeters = Mathf.Max(0f, a);
            RefillAll();
        }

        // Re-derive every loaded chunk's heights from the current source (procedural amplitude or
        // DEM norm range) and rebuild its mesh. Heavy on big/high-res bubbles — hitch per call.
        public static void RefillAll()
        {
            if (!Active) return;
            foreach (var kv in _chunks)
            {
                var ch = kv.Value;
                FillHeights(kv.Key, ch.H, ch.LodRes);
                ApplyEditsToArray(kv.Key, ch.H, ch.LodRes);
                BuildMesh(ch);
                if (ch.Mc != null) { ch.Mc.sharedMesh = null; ch.Mc.sharedMesh = ch.Mesh; }
            }
        }

        // The shared chunk material is white × the grid texture when ShowGrid, else flat green.
        static void ApplyGridMaterial()
        {
            if (_mat == null) return;
            if (ShowGrid)
            {
                if (_gridTex == null) _gridTex = BuildGridTexture();
                _mat.mainTexture = _gridTex;
                if (_mat.HasProperty("_BaseMap")) _mat.SetTexture("_BaseMap", _gridTex);
                if (_mat.HasProperty("_BaseColor")) _mat.SetColor("_BaseColor", Color.white);
                _mat.color = Color.white;
            }
            else
            {
                Color g = new Color(0.34f, 0.5f, 0.26f);
                _mat.mainTexture = null;
                if (_mat.HasProperty("_BaseMap")) _mat.SetTexture("_BaseMap", null);
                if (_mat.HasProperty("_BaseColor")) _mat.SetColor("_BaseColor", g);
                _mat.color = g;
            }
        }

        // One 512² tile = one 1 km chunk: green base, MINOR lines every 100 m, MAJOR lines on the
        // chunk borders (1 km). UV = local XZ / ChunkSize (0..1), so it aligns across chunk seams.
        static Texture2D BuildGridTexture()
        {
            const int N = 512;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true);
            Color baseC = new Color(0.34f, 0.5f, 0.26f);
            Color minorC = new Color(0.26f, 0.38f, 0.20f);
            Color majorC = new Color(0.12f, 0.16f, 0.10f);
            int minorStep = N / 10;   // 100 m
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    Color c = baseC;
                    bool minor = (x % minorStep) < 2 || (y % minorStep) < 2;
                    bool major = x < 3 || y < 3 || x >= N - 3 || y >= N - 3;
                    px[y * N + x] = major ? majorC : (minor ? minorC : baseC);
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = 4;
            tex.Apply(true);
            return tex;
        }

        public static void SetResolution(int r)
        {
            r = Mathf.Clamp(r, 65, 1025);
            if (r == Res) return;
            if (Active)
            {
                var keys = new List<Vector2Int>(_chunks.Keys);
                for (int i = 0; i < keys.Count; i++) UnloadChunk(keys[i]);
                _pending.Clear();
                _center = new Vector2Int(int.MinValue, int.MinValue);
                _lastFoot = -1; _lastDist = -1f;
            }
            Res = r;
        }

        static Vector2Int ChunkOf(float x, float z)
            => new Vector2Int(Mathf.FloorToInt(x / ChunkSize), Mathf.FloorToInt(z / ChunkSize));

        static int Cheb(Vector2Int c, Vector2Int center)
            => Mathf.Max(Mathf.Abs(c.x - center.x), Mathf.Abs(c.y - center.y));

        // ── Screen-space LOD: a chunk's mesh res from how big it is on screen ─────────────────
        static int DesiredRes(Vector2Int c)
        {
            if (_cam == null) return SnapRes(Res);
            Vector3 cc = new Vector3((c.x + 0.5f) * ChunkSize, 0f, (c.y + 0.5f) * ChunkSize);
            float d = Vector3.Distance(_cam.transform.position, cc);
            float mpp = 2f * d * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, _cam.pixelHeight);
            float spacing = mpp * Mathf.Max(1f, PixelsPerVertex);   // target metres between verts
            int want = Mathf.RoundToInt(ChunkSize / Mathf.Max(0.01f, spacing)) + 1;
            return SnapRes(want);
        }

        static int SnapRes(int want)
        {
            int res = ResLevels[0];
            foreach (int lv in ResLevels) if (lv <= want && lv <= Res) res = lv;
            return res;
        }

        static int LevelOfRes(int res)
        {
            int lvl = 0;
            for (int i = ResLevels.Length - 1; i >= 0; i--) { if (ResLevels[i] == res) return ResLevels.Length - 1 - i; }
            return lvl;
        }

        // ── Streaming (view footprint, budgeted) ─────────────────────────────────────────────
        public static void Tick(Camera cam, bool eager = false)
        {
            if (!Active || _root == null || cam == null) return;
            _cam = cam;
            // Footprint: where the camera looks at the ground, and how wide the view is there.
            Vector3 p = cam.transform.position, f = cam.transform.forward;
            Vector3 ground = (f.y < -1e-3f) ? p + f * (-p.y / f.y) : new Vector3(p.x, 0f, p.z);
            var center = ChunkOf(ground.x, ground.z);
            float dist = Vector3.Distance(p, ground);
            float halfExtent = dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * Mathf.Max(1f, cam.aspect) * 1.5f;
            // FillRadius: load the FULL (2·Radius+1)² regardless of zoom. Otherwise the bubble is the
            // on-screen footprint (+preload), capped by Radius — so Radius alone never forces a big load.
            int foot = FillRadius ? Mathf.Max(1, Radius)
                     : Mathf.Clamp(Mathf.CeilToInt(halfExtent / ChunkSize) + Mathf.Max(0, PreloadDepth), 1, Mathf.Max(1, Radius));

            bool zoomChanged = _lastDist < 0f || Mathf.Abs(dist - _lastDist) / Mathf.Max(1f, _lastDist) > 0.1f;
            bool footChanged = center != _center || foot != _lastFoot;
            // STREAMING (or eager): re-centre the resident set on the view → cull + load + re-LOD.
            // FROZEN (locked): keep the resident set put; only re-LOD the existing chunks as you
            // pan/zoom (refine where you look). New chunks never load, nothing culls.
            if (eager || FillRadius || (Streaming && footChanged))
            {
                _center = center; _lastFoot = foot; _lastDist = dist; _lastLodCenter = center;
                Recompute(center, foot);
            }
            else if (zoomChanged || center != _lastLodCenter)
            {
                _lastDist = dist; _lastLodCenter = center;
                ReLodResident(center);
            }

            // FillRadius streams a huge bubble in over many frames (never an int.MaxValue eager freeze).
            int budget = FillRadius ? Mathf.Max(16, Budget)
                       : (eager || Budget <= 0) ? int.MaxValue : Budget;
            int done = 0;
            while (done < budget && _pending.Count > 0)
            {
                var c = _pending[_pending.Count - 1];
                _pending.RemoveAt(_pending.Count - 1);
                if (!_chunks.ContainsKey(c)) LoadChunk(c);
                else { int want = DesiredRes(c); if (_chunks[c].LodRes != want) RebuildLod(c, want); }
                done++;
            }
        }

        // Re-LOD the EXISTING resident set only (no cull/load) — for the frozen bubble as you
        // pan/zoom within it, and for zoom-only changes while streaming.
        static void ReLodResident(Vector2Int center)
        {
            _pending.Clear();
            foreach (var kv in _chunks)
                if (kv.Value.LodRes != DesiredRes(kv.Key)) _pending.Add(kv.Key);
            _pending.Sort((a, b) => Cheb(b, center).CompareTo(Cheb(a, center)));
        }

        static void Recompute(Vector2Int center, int foot)
        {
            List<Vector2Int> cull = null;
            foreach (var c in _chunks.Keys)
                if (Mathf.Abs(c.x - center.x) > foot || Mathf.Abs(c.y - center.y) > foot)
                    (cull ??= new List<Vector2Int>()).Add(c);
            if (cull != null) for (int i = 0; i < cull.Count; i++) UnloadChunk(cull[i]);

            _pending.Clear();
            for (int dz = -foot; dz <= foot; dz++)
                for (int dx = -foot; dx <= foot; dx++)
                {
                    var c = new Vector2Int(center.x + dx, center.y + dz);
                    if (!_chunks.ContainsKey(c)) _pending.Add(c);
                    else if (_chunks[c].LodRes != DesiredRes(c)) _pending.Add(c);
                }
            _pending.Sort((a, b) => Cheb(b, center).CompareTo(Cheb(a, center)));
        }

        // ── Load / unload / LOD rebuild ──────────────────────────────────────────────────────
        static void LoadChunk(Vector2Int c)
        {
            int res = DesiredRes(c);
            var ch = new Chunk { H = new float[res * res], LodRes = res };
            FillHeights(c, ch.H, res);
            ApplyEditsToArray(c, ch.H, res);

            var go = new GameObject($"Chunk_{c.x}_{c.y}");
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(c.x * ChunkSize, 0f, c.y * ChunkSize);
            ch.Go = go;
            ch.Mesh = new Mesh { name = $"ChunkMesh_{c.x}_{c.y}" };
            BuildMesh(ch);
            ch.Mf = go.AddComponent<MeshFilter>(); ch.Mf.sharedMesh = ch.Mesh;
            ch.Mr = go.AddComponent<MeshRenderer>(); ch.Mr.sharedMaterial = _mat;
            ch.Mc = go.AddComponent<MeshCollider>(); ch.Mc.sharedMesh = ch.Mesh;

            _chunks[c] = ch;
            _everLoaded.Add(c);
            _loadTime[c] = Time.realtimeSinceStartup;
        }

        static void RebuildLod(Vector2Int c, int newRes)
        {
            if (!_chunks.TryGetValue(c, out var ch)) return;
            SaveChunkEdits(c);
            _dirty.Remove(c);
            ch.LodRes = newRes;
            ch.H = new float[newRes * newRes];
            FillHeights(c, ch.H, newRes);
            ApplyEditsToArray(c, ch.H, newRes);
            BuildMesh(ch);
            if (ch.Mc != null) { ch.Mc.sharedMesh = null; ch.Mc.sharedMesh = ch.Mesh; }
        }

        static void UnloadChunk(Vector2Int c)
        {
            if (!_chunks.TryGetValue(c, out var ch)) return;
            SaveChunkEdits(c);
            if (ch.Go != null) { if (Application.isPlaying) Object.Destroy(ch.Go); else Object.DestroyImmediate(ch.Go); }
            if (ch.Mesh != null) { if (Application.isPlaying) Object.Destroy(ch.Mesh); else Object.DestroyImmediate(ch.Mesh); }
            _chunks.Remove(c);
            _dirty.Remove(c);
            // (DEM tiles are shared across chunks → freed by DemChunkSource's own LRU, not per-chunk.)
        }

        // ── Mesh build (per-chunk res) ───────────────────────────────────────────────────────
        static (Vector3[] v, int[] t, Vector2[] uv) Buffers(int res)
        {
            if (!_vertsByRes.TryGetValue(res, out var v)) { v = new Vector3[res * res]; _vertsByRes[res] = v; }
            if (!_trisByRes.TryGetValue(res, out var t))
            {
                t = new int[(res - 1) * (res - 1) * 6];
                int ti = 0;
                for (int z = 0; z < res - 1; z++)
                    for (int x = 0; x < res - 1; x++)
                    {
                        int a = z * res + x, b = a + 1, cc = a + res, d = cc + 1;
                        t[ti++] = a; t[ti++] = cc; t[ti++] = b;
                        t[ti++] = b; t[ti++] = cc; t[ti++] = d;
                    }
                _trisByRes[res] = t;
            }
            if (!_uvByRes.TryGetValue(res, out var uv))
            {
                uv = new Vector2[res * res];
                float inv = 1f / (res - 1);   // local XZ → 0..1 across the chunk (grid tile per 1 km)
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                        uv[z * res + x] = new Vector2(x * inv, z * inv);
                _uvByRes[res] = uv;
            }
            return (v, t, uv);
        }

        static void BuildMesh(Chunk ch)
        {
            int res = ch.LodRes; float sp = ChunkSize / (res - 1);
            var (verts, tris, uvs) = Buffers(res);
            var H = ch.H;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    verts[i] = new Vector3(x * sp, H[i], z * sp);
                }
            var m = ch.Mesh;
            m.Clear();
            if (res * res > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = verts;
            m.uv = uvs;
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
        }

        // ── Procedural heights ───────────────────────────────────────────────────────────────
        // Dramatic multi-octave terrain (0..AmpMeters) sampled at each vertex's WORLD position, so
        // it's seamless across chunks AND LOD resolutions (any res sampling the same point agrees).
        // The fine ~16 m octave is what makes LOD visible: high res resolves it, low res smooths it.
        static void FillHeights(Vector2Int c, float[] h, int res)
        {
            if (DemChunkSource.CoversChunk(c)) { FillFromDem(c, h, res); return; }
            float sp = ChunkSize / (res - 1);
            float ox = c.x * ChunkSize, oz = c.y * ChunkSize;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    h[z * res + x] = HeightAt(ox + x * sp, oz + z * sp);
        }

        // Real DEM heights: sample the continuous DEM mosaic at each vertex's WORLD position, so any
        // LOD res reads the same data (high res resolves detail, low res downsamples) and a DEM tile
        // that spans several 1 km chunks renders at true scale across all of them.
        static void FillFromDem(Vector2Int c, float[] h, int res)
        {
            float sp = ChunkSize / (res - 1);
            float ox = c.x * ChunkSize, oz = c.y * ChunkSize;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    h[z * res + x] = DemChunkSource.SampleWorldYAt(ox + x * sp, oz + z * sp);
        }

        static float HeightAt(float wx, float wz)
        {
            float n = Octave(wx, wz, 2000f, 0) * 0.60f    // mountains (~2 km)
                    + Octave(wx, wz, 400f, 1) * 0.22f     // hills
                    + Octave(wx, wz, 80f, 2) * 0.12f      // ridges
                    + Octave(wx, wz, 16f, 3) * 0.06f;     // surface roughness — the LOD-visible detail
            return n * AmpMeters;
        }

        // Value noise: smoothstepped bilinear of the hashed lattice at `wl` metres.
        static float Octave(float wx, float wz, float wl, int seed)
        {
            float gx = wx / wl + seed * 131.7f, gz = wz / wl + seed * 71.3f;
            int x0 = Mathf.FloorToInt(gx), z0 = Mathf.FloorToInt(gz);
            float tx = gx - x0, tz = gz - z0;
            tx = tx * tx * (3f - 2f * tx); tz = tz * tz * (3f - 2f * tz);
            float a = Mathf.Lerp(Hash01(x0, z0), Hash01(x0 + 1, z0), tx);
            float b = Mathf.Lerp(Hash01(x0, z0 + 1), Hash01(x0 + 1, z0 + 1), tx);
            return Mathf.Lerp(a, b, tz);
        }

        static float Hash01(int x, int y)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return (h & 0xFFFFFFu) / (float)0x1000000;
        }

        // ── Surface queries ──────────────────────────────────────────────────────────────────
        public static float SampleHeight(float x, float z)
        {
            var c = ChunkOf(x, z);
            if (!_chunks.TryGetValue(c, out var ch) || ch.H == null) return 0f;
            int res = ch.LodRes; float sp = ChunkSize / (res - 1);
            float lx = (x - c.x * ChunkSize) / sp, lz = (z - c.y * ChunkSize) / sp;
            int ix = Mathf.Clamp((int)lx, 0, res - 2), iz = Mathf.Clamp((int)lz, 0, res - 2);
            float tx = Mathf.Clamp01(lx - ix), tz = Mathf.Clamp01(lz - iz);
            float h00 = ch.H[iz * res + ix], h10 = ch.H[iz * res + ix + 1];
            float h01 = ch.H[(iz + 1) * res + ix], h11 = ch.H[(iz + 1) * res + ix + 1];
            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
        }

        public static float SampleSlopeDegrees(float x, float z)
        {
            const float e = 2f;
            float gx = (SampleHeight(x + e, z) - SampleHeight(x - e, z)) / (2f * e);
            float gz = (SampleHeight(x, z + e) - SampleHeight(x, z - e)) / (2f * e);
            return Mathf.Atan(Mathf.Sqrt(gx * gx + gz * gz)) * Mathf.Rad2Deg;
        }

        // ── Sculpt (per-chunk res) ───────────────────────────────────────────────────────────
        public static void Sculpt(Vector3 world, float radius, float strength, float dt,
                                  DemTerrainWorld.SculptMode mode, float targetY)
        {
            if (!Active || radius <= 0f) return;
            float r2 = radius * radius;
            foreach (var kv in _chunks)
            {
                var ch = kv.Value; var c = kv.Key;
                int res = ch.LodRes; float sp = ChunkSize / (res - 1);
                float ox = c.x * ChunkSize, oz = c.y * ChunkSize;
                if (world.x + radius < ox || world.x - radius > ox + ChunkSize) continue;
                if (world.z + radius < oz || world.z - radius > oz + ChunkSize) continue;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((world.x - radius - ox) / sp), 0, res - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((world.x + radius - ox) / sp), 0, res - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((world.z - radius - oz) / sp), 0, res - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((world.z + radius - oz) / sp), 0, res - 1);
                if (x1 < x0 || z1 < z0) continue;
                float[] src = mode == DemTerrainWorld.SculptMode.Smooth ? (float[])ch.H.Clone() : null;
                int kernel = Mathf.Clamp(Mathf.RoundToInt(radius / sp * 0.25f), 2, 12);
                float blendF = Mathf.Clamp01(strength * dt * 0.12f);
                float blendS = Mathf.Clamp01(strength * dt * 0.7f);
                float stepM = strength * dt * 3f;
                bool changed = false;
                HashSet<int> ds = null;
                for (int zz = z0; zz <= z1; zz++)
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        float wx = ox + xx * sp, wz = oz + zz * sp;
                        float dx = wx - world.x, dz = wz - world.z, d2 = dx * dx + dz * dz;
                        if (d2 > r2) continue;
                        float fall = 1f - Mathf.Sqrt(d2) / radius;
                        fall = fall * fall * (3f - 2f * fall);
                        int i = zz * res + xx;
                        float val = ch.H[i];
                        switch (mode)
                        {
                            case DemTerrainWorld.SculptMode.Raise: val += stepM * fall; break;
                            case DemTerrainWorld.SculptMode.Lower: val -= stepM * fall; break;
                            case DemTerrainWorld.SculptMode.Flatten: val = Mathf.Lerp(val, targetY, blendF * fall); break;
                            case DemTerrainWorld.SculptMode.Smooth: val = Mathf.Lerp(val, BoxAvg(src, xx, zz, res, kernel), blendS * fall); break;
                        }
                        if (val != ch.H[i])
                        {
                            ch.H[i] = val; changed = true;
                            if (ds == null) ds = DirtySetFor(c);
                            ds.Add(i);
                        }
                    }
                if (changed) { BuildMesh(ch); if (ch.Mc != null) { ch.Mc.sharedMesh = null; ch.Mc.sharedMesh = ch.Mesh; } }
            }
        }

        // ── Sea tool: click-to-flood lower ──────────────────────────────────────────────────
        // From `hit`, flood-fill the contiguous region whose height is within ±tolerance of the
        // clicked height (staying INSIDE loaded chunks), then flatten it to (seed − drop). Carves
        // the flat DEM ocean below the water plane so it stops z-fighting. Works on any flat area.
        public static void FloodLower(Vector3 hit, float tolerance, float drop, float cell = 30f, int maxCells = 400000)
        {
            if (!Active || _chunks.Count == 0) return;
            float seedH = SampleHeight(hit.x, hit.z);
            var start = new Vector2Int(Mathf.FloorToInt(hit.x / cell), Mathf.FloorToInt(hit.z / cell));
            if (!CellIsSea(start, cell, seedH, tolerance)) return;   // click wasn't on a same-altitude area

            var sel = new HashSet<Vector2Int> { start };
            var q = new Queue<Vector2Int>();
            q.Enqueue(start);
            int mincx = start.x, maxcx = start.x, mincz = start.y, maxcz = start.y;
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
            while (q.Count > 0 && sel.Count < maxCells)
            {
                var cc = q.Dequeue();
                for (int k = 0; k < 4; k++)
                {
                    var nc = cc + dirs[k];
                    if (sel.Contains(nc) || !CellIsSea(nc, cell, seedH, tolerance)) continue;
                    sel.Add(nc); q.Enqueue(nc);
                    if (nc.x < mincx) mincx = nc.x; if (nc.x > maxcx) maxcx = nc.x;
                    if (nc.y < mincz) mincz = nc.y; if (nc.y > maxcz) maxcz = nc.y;
                }
            }
            if (sel.Count >= maxCells) Debug.LogWarning($"[ChunkWorld] FloodLower hit the {maxCells}-cell cap — selection truncated.");

            float target = seedH - Mathf.Max(0f, drop);
            float selX0 = mincx * cell, selX1 = (maxcx + 1) * cell, selZ0 = mincz * cell, selZ1 = (maxcz + 1) * cell;
            foreach (var kv in _chunks)
            {
                var ch = kv.Value; var ck = kv.Key;
                float ox = ck.x * ChunkSize, oz = ck.y * ChunkSize;
                if (ox + ChunkSize < selX0 || ox > selX1 || oz + ChunkSize < selZ0 || oz > selZ1) continue;   // chunk outside bbox
                int res = ch.LodRes; float sp = ChunkSize / (res - 1);
                bool changed = false; HashSet<int> ds = null;
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                    {
                        float wx = ox + x * sp, wz = oz + z * sp;
                        var cc = new Vector2Int(Mathf.FloorToInt(wx / cell), Mathf.FloorToInt(wz / cell));
                        if (!sel.Contains(cc)) continue;
                        int i = z * res + x;
                        if (Mathf.Abs(ch.H[i] - seedH) > tolerance) continue;   // don't carve land inside a sea cell
                        if (ch.H[i] != target)
                        {
                            ch.H[i] = target; changed = true;
                            if (ds == null) ds = DirtySetFor(ck);
                            ds.Add(i);
                        }
                    }
                if (changed) { BuildMesh(ch); if (ch.Mc != null) { ch.Mc.sharedMesh = null; ch.Mc.sharedMesh = ch.Mesh; } }
            }
        }

        // A flood cell counts as "sea" if its centre is inside a loaded chunk and within tolerance
        // of the seed height. Unloaded cells return false → the flood stops at the bubble edge.
        static bool CellIsSea(Vector2Int c, float cell, float seedH, float tol)
        {
            float wx = (c.x + 0.5f) * cell, wz = (c.y + 0.5f) * cell;
            if (!_chunks.ContainsKey(ChunkOf(wx, wz))) return false;
            return Mathf.Abs(SampleHeight(wx, wz) - seedH) <= tol;
        }

        static float BoxAvg(float[] s, int x, int z, int res, int k)
        {
            float sum = 0f; int n = 0;
            for (int dz = -k; dz <= k; dz++)
                for (int dx = -k; dx <= k; dx++)
                {
                    int xx = x + dx, zz = z + dz;
                    if (xx < 0 || xx >= res || zz < 0 || zz >= res) continue;
                    sum += s[zz * res + xx]; n++;
                }
            return n > 0 ? sum / n : s[z * res + x];
        }

        static HashSet<int> DirtySetFor(Vector2Int c)
        {
            if (!_dirty.TryGetValue(c, out var set)) _dirty[c] = set = new HashSet<int>();
            return set;
        }

        // ── Per-chunk persistence (sparse world-Y vertex diff, res-tagged) ───────────────────
        static string ChunkFile(Vector2Int c)
            => string.IsNullOrEmpty(_editDir) ? null : Path.Combine(_editDir, $"chunk_{c.x}_{c.y}.bin");

        static void SaveChunkEdits(Vector2Int c)
        {
            if (!_dirty.TryGetValue(c, out var set) || set.Count == 0) return;
            if (!_chunks.TryGetValue(c, out var ch) || ch.H == null) return;
            string path = ChunkFile(c);
            if (path == null) return;
            try
            {
                using var ms = new MemoryStream(set.Count * 8 + 16);
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(0x43484b32);
                    w.Write(ch.LodRes);
                    w.Write(set.Count);
                    foreach (int i in set) { w.Write(i); w.Write(ch.H[i]); }
                }
                File.WriteAllBytes(path, ms.ToArray());
            }
            catch (System.Exception ex) { Debug.LogWarning($"[ChunkWorld] save {c} failed: {ex.Message}"); }
        }

        static void ApplyEditsToArray(Vector2Int c, float[] h, int res)
        {
            string path = ChunkFile(c);
            if (path == null || !File.Exists(path)) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(bytes);
                using var r = new BinaryReader(ms);
                if (r.ReadInt32() != 0x43484b32) return;
                int fres = r.ReadInt32();
                if (fres != res) return;
                int n = r.ReadInt32();
                var set = DirtySetFor(c);
                for (int k = 0; k < n; k++)
                {
                    int i = r.ReadInt32(); float v = r.ReadSingle();
                    if (i >= 0 && i < h.Length) { h[i] = v; set.Add(i); }
                }
            }
            catch (System.Exception ex) { Debug.LogWarning($"[ChunkWorld] load {c} failed: {ex.Message}"); }
        }
    }

    public class ChunkSurface : ITerrainSurface
    {
        public float SampleHeight(float x, float z) => ChunkWorld.SampleHeight(x, z);
        public float SampleSlopeDegrees(float x, float z) => ChunkWorld.SampleSlopeDegrees(x, z);
        public Vector3 Origin => Vector3.zero;
        public float WidthX => ChunkWorld.ChunkSize * 64f;
        public float LengthZ => ChunkWorld.ChunkSize * 64f;
    }
}
