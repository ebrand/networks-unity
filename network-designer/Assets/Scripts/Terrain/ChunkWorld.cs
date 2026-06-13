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
// Heights are a per-chunk world-Y float[] at that chunk's LOD res (regenerated from the source — DEM
// or procedural — on LOD change). Sculpts are stored as a LOD-INDEPENDENT absolute-override (Abs + Op,
// render H = lerp(DEM, Abs, Op)) bilinear-resampled onto whatever res a chunk rebuilds at, so edits
// don't pop in/out as the LOD flips. SEAMS: perimeter skirts (Skirts) hide LOD-boundary cracks; Smooth
// averages the global surface across seams so a stroke can't tear shared edges apart.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Jobs;

namespace NetworkDesigner.Terrain
{
    // Bakes a mesh's collision cooking on a WORKER thread (Physics.BakeMesh is thread-safe). After it
    // completes, assigning the (now pre-cooked) mesh to a MeshCollider on the main thread is nearly
    // free — so streaming a chunk no longer hitches the frame on synchronous collision cooking.
    struct BakeMeshJob : IJob
    {
        public int MeshId;
        public void Execute() => Physics.BakeMesh(MeshId, false);   // false = concave (terrain)
    }

    public static class ChunkWorld
    {
        public const float ChunkSize = 1000f;
        public static int Res = 513;             // MAX / near res; snaps 129/257/513/1025 (513 ≈ 2 m/vertex on a 1 km chunk — smooth streaming; raise for finer sculpt detail)
        public static float PixelsPerVertex = 6f; // screen-space error: bigger = coarser/cheaper
        public static int Radius = 10;            // MAX footprint cap (chunks), safety on zoom-out
        public static bool FillRadius = false;    // force the bubble to the FULL Radius (ignore the view footprint)
        public static int PreloadDepth = 1;       // margin chunks beyond the view footprint
        public static int Budget = 2;             // chunk builds/rebuilds per frame (lower = smoother streaming, slower fill-in)
        public static int RecenterDeadband = 2;   // re-center the bubble only after the look-point moves this many chunks (anti-thrash on look-around)
        public static int ColliderRadius = 32;    // CAP on collider coverage (Chebyshev from centre); default covers the whole bubble, so everything you see is sculptable. Lower it only to save physics memory on huge stress bubbles.
        public static bool Skirts = true;         // perimeter skirt walls hide LOD-seam cracks; off → drop 0 (degenerate, no walls)
        // ── Topo contour overlay (per-pixel shader; a 2nd renderer over each chunk mesh) ──
        public static bool ShowContours = false;
        public static float ContourMinor = 1f;     // minor isoline spacing (m)
        public static float ContourMajorEvery = 10f; // every Nth minor is a thicker major (→ 10 m)
        public static float ContourStrength = 0.55f; // line opacity 0..1
        static Material _contourMat;
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
            public Vector2Int Coord;   // this chunk's grid coordinate (for collider-distance gating)
            public GameObject ContourGo; // child renderer drawing the contour-overlay shader over this mesh
            public JobHandle BakeHandle; // in-flight async collision-bake (valid only while BakePending)
            public bool BakePending;   // a BakeMeshJob is baking this chunk's collider on a worker thread
            public float[] H;          // render heights at LodRes (= source DEM/proc + Edit)
            public int LodRes;
            public float[] Abs;        // edited ABSOLUTE surface at EditRes (the height you sculpted)
            public float[] Op;         // edit OPACITY 0..1 at EditRes — how much Abs overrides the DEM (feathered)
            public int EditRes;        // resolution Abs/Op are stored at (never view-degraded)
            public bool EditDirty;     // unsaved sculpt changes
        }

        public static bool ShowGrid = true;      // 1 km major / 100 m minor grid painted on the ground
        static GameObject _root;
        static Material _mat;
        static Texture2D _gridTex;
        static string _editDir;
        static Camera _cam;
        static readonly Dictionary<Vector2Int, Chunk> _chunks = new Dictionary<Vector2Int, Chunk>();
        static readonly HashSet<Vector2Int> _excluded = new HashSet<Vector2Int>();   // trimmed-away chunks (map editor); never streamed
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
        static readonly Dictionary<int, int[]> _perimByRes = new Dictionary<int, int[]>();
        public static float SkirtFactor = 3f;    // skirt drop = vertexSpacing × this (min 10 m); hides LOD-seam cracks

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

        // ── Chunk exclusions (map-trim editor) ───────────────────────────────────────────────
        public static int ExcludedCount => _excluded.Count;
        public static bool IsExcluded(Vector2Int c) => _excluded.Contains(c);

        // Replace the whole exclusion set, drop any now-excluded resident chunks, persist, and re-stream
        // the trimmed map. Called when the map editor commits its selection.
        public static void ApplyExclusions(IEnumerable<Vector2Int> coords)
        {
            _excluded.Clear();
            if (coords != null) foreach (var c in coords) _excluded.Add(c);
            List<Vector2Int> drop = null;
            foreach (var c in _chunks.Keys) if (_excluded.Contains(c)) (drop ??= new List<Vector2Int>()).Add(c);
            if (drop != null) for (int i = 0; i < drop.Count; i++) UnloadChunk(drop[i]);
            SaveExclusions();
            if (Active && _cam != null) Tick(_cam, eager: true);   // re-stream the trimmed set now
        }

        static string ExclusionsFile => string.IsNullOrEmpty(_editDir) ? null : Path.Combine(_editDir, "_excluded.bin");

        static void SaveExclusions()
        {
            string path = ExclusionsFile; if (path == null) return;
            try
            {
                using var ms = new MemoryStream(_excluded.Count * 8 + 8);
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(0x43584c31);   // "CXL1"
                    w.Write(_excluded.Count);
                    foreach (var c in _excluded) { w.Write(c.x); w.Write(c.y); }
                }
                File.WriteAllBytes(path, ms.ToArray());
            }
            catch (System.Exception ex) { Debug.LogWarning($"[ChunkWorld] save exclusions failed: {ex.Message}"); }
        }

        static void LoadExclusions()
        {
            _excluded.Clear();
            string path = ExclusionsFile; if (path == null || !File.Exists(path)) return;
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var r = new BinaryReader(ms);
                if (r.ReadInt32() != 0x43584c31) return;
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) { int x = r.ReadInt32(), y = r.ReadInt32(); _excluded.Add(new Vector2Int(x, y)); }
            }
            catch (System.Exception ex) { Debug.LogWarning($"[ChunkWorld] load exclusions failed: {ex.Message}"); }
        }

        public static void Begin(string editDir)
        {
            if (Active) return;
            _editDir = editDir;
            try { if (!string.IsNullOrEmpty(_editDir)) Directory.CreateDirectory(_editDir); } catch { }
            LoadExclusions();   // restore this map's trimmed-chunk set
            _root = new GameObject(RootName);
            if (_mat == null) _mat = GroundMaterial();
            ApplyGridMaterial();
            _center = new Vector2Int(int.MinValue, int.MinValue);
            _lastLodCenter = new Vector2Int(int.MinValue, int.MinValue);
            _lastFoot = -1; _lastDist = -1f; StreamHeld = false; _pending.Clear();
            Active = true;
        }

        public static void End()
        {
            if (!Active) return;
            CompleteAllBakes();   // no worker thread may be reading a mesh we're about to destroy
            foreach (var kv in _chunks) SaveChunkEdits(kv.Key);
            if (_root != null) { if (Application.isPlaying) Object.Destroy(_root); else Object.DestroyImmediate(_root); }
            _root = null; _chunks.Clear();
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
                OverlayEdit(ch);   // re-apply the absolute-override sculpt onto the fresh DEM heights
                BuildMesh(ch);
                CookCollider(ch, true);
            }
        }

        // Overlay the chunk's ABSOLUTE edited surface onto its (freshly source-filled) render heights:
        //   H = lerp(DEM, Abs, Op)
        // resampling THROWAWAY copies of Abs/Op from EditRes to the render res. ch.Abs/ch.Op themselves
        // are never mutated here, so repeated LOD/view changes can't degrade the edit, and because the
        // edited core has Op≈1 the render shows YOUR surface — never the re-derived DEM detail at any zoom.
        static void OverlayEdit(Chunk ch)
        {
            if (ch.Op == null || ch.Abs == null) return;
            float[] a = ch.EditRes == ch.LodRes ? ch.Abs : ResampleGrid(ch.Abs, ch.EditRes, ch.LodRes);
            float[] o = ch.EditRes == ch.LodRes ? ch.Op : ResampleGrid(ch.Op, ch.EditRes, ch.LodRes);
            int n = Mathf.Min(ch.H.Length, Mathf.Min(a.Length, o.Length));
            for (int i = 0; i < n; i++)
            {
                float op = o[i];
                if (op <= 0f) continue;
                ch.H[i] = Mathf.Lerp(ch.H[i], a[i], op > 1f ? 1f : op);
            }
        }

        // Make ch.Abs/ch.Op live at `res` for an active sculpt at that res (allocating or resampling once).
        // Resampling only happens when you actually sculpt at a new res — never on a plain view change.
        // First allocation SEEDS Abs from the chunk's current heights (= DEM where unedited) so the
        // absolute surface is coherent everywhere — Smooth's box-average can read unedited neighbours.
        // Op starts all-zero (no override → render = DEM). Callers only ever pass res == ch.LodRes,
        // so ch.H.Length == res*res and the seed Copy is exact.
        static void EnsureEditRes(Chunk ch, int res)
        {
            if (ch.Op == null)
            {
                ch.Abs = new float[res * res];
                ch.Op = new float[res * res];
                if (ch.H != null && ch.H.Length == res * res)
                    System.Array.Copy(ch.H, ch.Abs, res * res);
                ch.EditRes = res;
            }
            else if (ch.EditRes != res)
            {
                ch.Abs = ResampleGrid(ch.Abs, ch.EditRes, res);
                ch.Op = ResampleGrid(ch.Op, ch.EditRes, res);
                ch.EditRes = res;
            }
        }

        // The shared chunk material is white × the grid texture when ShowGrid, else flat green.
        // The realistic ground material: triplanar grass / grass-dirt / dirt-gravel blended by slope + noise
        // (NetworkDesigner/TerrainGround). Optional textures load from Resources/TerrainGround/{grass,
        // grass_dirt,gravel}; missing ones just leave the layer as its tint. Falls back to a flat green
        // lit material if the shader isn't in the project.
        static Material GroundMaterial()
        {
            Shader sh = Shader.Find("NetworkDesigner/TerrainGround");
            if (sh == null) return NetworkDesigner.PipelineMaterials.CreateLitMatte(new Color(0.34f, 0.5f, 0.26f), "ChunkGround");
            var m = new Material(sh) { name = "ChunkGround" };
            var grass = Resources.Load<Texture2D>("TerrainGround/grass");
            var mid = Resources.Load<Texture2D>("TerrainGround/grass_dirt");
            var gravel = Resources.Load<Texture2D>("TerrainGround/gravel");
            if (grass != null) m.SetTexture("_GrassTex", grass);
            if (mid != null) m.SetTexture("_MidTex", mid);
            if (gravel != null) m.SetTexture("_GravelTex", gravel);
            // Optional normal maps — same names with a _normal suffix (import them as Normal map type).
            var grassN = Resources.Load<Texture2D>("TerrainGround/grass_normal");
            var midN = Resources.Load<Texture2D>("TerrainGround/grass_dirt_normal");
            var gravelN = Resources.Load<Texture2D>("TerrainGround/gravel_normal");
            if (grassN != null) m.SetTexture("_GrassNormal", grassN);
            if (midN != null) m.SetTexture("_MidNormal", midN);
            if (gravelN != null) m.SetTexture("_GravelNormal", gravelN);
            // Optional high-frequency detail-overlay texture (breaks up the base tile up close).
            var detail = Resources.Load<Texture2D>("TerrainGround/detail");
            if (detail != null) m.SetTexture("_DetailTex", detail);
            // Sand (4th, height-based) layer + the water surface it bands above.
            var sand = Resources.Load<Texture2D>("TerrainGround/sand");
            var sandN = Resources.Load<Texture2D>("TerrainGround/sand_normal");
            if (sand != null) m.SetTexture("_SandTex", sand);
            if (sandN != null) m.SetTexture("_SandNormal", sandN);
            if (m.HasProperty("_SeaLevel")) m.SetFloat("_SeaLevel", ChunkOverlays.WaterLevel);
            // Re-apply any ground-blend values set BEFORE this material existed (persisted settings loaded at
            // startup, or values held across a material rebuild) so they aren't lost — see _groundProps.
            foreach (var kv in _groundProps)
                if (m.HasProperty(kv.Key)) m.SetFloat(kv.Key, kv.Value);
            foreach (var kv in _groundColors)
                if (m.HasProperty(kv.Key)) m.SetColor(kv.Key, kv.Value);
            return m;
        }

        // ── Live ground-blend tunables (TerrainGround shader) ────────────────────────────────────
        // Drive the shared chunk material's blend properties live — every chunk updates instantly, no
        // rebuild. Values are cached in _groundProps so a Set that arrives BEFORE the material is built
        // (persisted settings loaded at startup) — or a later material rebuild — still applies them
        // (GroundMaterial replays the cache). The cache is the source of truth for the getter, so the
        // palette/registry reads the desired value even when no material exists yet.
        static readonly Dictionary<string, float> _groundProps = new Dictionary<string, float>();
        static float GroundGet(string p, float dflt)
            => _groundProps.TryGetValue(p, out float v) ? v
             : (_mat != null && _mat.HasProperty(p)) ? _mat.GetFloat(p) : dflt;
        static void GroundSet(string p, float v)
        {
            _groundProps[p] = v;
            if (_mat != null && _mat.HasProperty(p)) _mat.SetFloat(p, v);
        }

        // Same cache/replay story for Color properties (e.g. the untextured flat colours).
        static readonly Dictionary<string, Color> _groundColors = new Dictionary<string, Color>();
        static Color GroundGetColor(string p, Color dflt)
            => _groundColors.TryGetValue(p, out Color v) ? v
             : (_mat != null && _mat.HasProperty(p)) ? _mat.GetColor(p) : dflt;
        static void GroundSetColor(string p, Color v)
        {
            _groundColors[p] = v;
            if (_mat != null && _mat.HasProperty(p)) _mat.SetColor(p, v);
        }

        // Untextured "flat" terrain colours (TerrainGround _GrassFlat/_MidFlat/_GravelFlat) — only visible when
        // Textures is OFF. Independent of the textured tints so they can be set without shifting the textured look.
        public static Color GroundGrassColor  { get => GroundGetColor("_GrassFlat",  new Color(0.32f, 0.42f, 0.20f)); set => GroundSetColor("_GrassFlat",  value); }
        public static Color GroundSlopeColor  { get => GroundGetColor("_MidFlat",    new Color(0.40f, 0.34f, 0.20f)); set => GroundSetColor("_MidFlat",    value); }
        public static Color GroundRockColor   { get => GroundGetColor("_GravelFlat", new Color(0.44f, 0.41f, 0.36f)); set => GroundSetColor("_GravelFlat", value); }

        // Texture tile size in METRES (world units per texture repeat) — larger = bigger features, less obvious
        // tiling. Drives all three layers together. (Shader stores 1/size as _*Scale.)
        public static float GroundTexSize
        {
            get { float s = GroundGet("_GrassScale", 0.15f); return s > 1e-4f ? 1f / s : 6.7f; }
            set { float s = value > 0.01f ? 1f / value : 0.15f; GroundSet("_GrassScale", s); GroundSet("_MidScale", s); GroundSet("_GravelScale", s); }
        }
        // Blend-noise feature size in METRES — how big the dirt/grass patches are.
        public static float GroundNoiseSize
        {
            get { float s = GroundGet("_NoiseScale", 0.01f); return s > 1e-6f ? 1f / s : 100f; }
            set { GroundSet("_NoiseScale", value > 1f ? 1f / value : 0.01f); }
        }
        public static float GroundSlopeJitter { get => GroundGet("_SlopeNoise", 14f); set => GroundSet("_SlopeNoise", value); }
        // Slope (deg) where grass starts turning to dirt / dirt to gravel; the transition band is fixed-width.
        public static float GroundDirtSlope
        {
            get => GroundGet("_MidSlopeStart", 8f);
            set { GroundSet("_MidSlopeStart", value); GroundSet("_MidSlopeFull", value + 16f); }
        }
        public static float GroundGravelSlope
        {
            get => GroundGet("_GravelSlopeStart", 28f);
            set { GroundSet("_GravelSlopeStart", value); GroundSet("_GravelSlopeFull", value + 18f); }
        }
        public static float GroundDirtPatches { get => GroundGet("_DirtPatchAmount", 0.6f); set => GroundSet("_DirtPatchAmount", Mathf.Clamp01(value)); }
        public static float GroundNormalStrength { get => GroundGet("_NormalStrength", 1f); set => GroundSet("_NormalStrength", value); }
        // Master texture toggle — off = the slope/noise TINT blend only (no texture/normal/stochastic samples).
        public static bool GroundTextures { get => GroundGet("_UseTextures", 1f) > 0.5f; set => GroundSet("_UseTextures", value ? 1f : 0f); }
        public static bool GroundStochastic { get => GroundGet("_Stochastic", 1f) > 0.5f; set => GroundSet("_Stochastic", value ? 1f : 0f); }
        public static float GroundMacroAmount { get => GroundGet("_MacroAmount", 0.28f); set => GroundSet("_MacroAmount", Mathf.Clamp(value, 0f, 0.8f)); }
        public static float GroundMacroSize   // metres per macro-variation cycle
        {
            get { float s = GroundGet("_MacroScale", 0.004f); return s > 1e-6f ? 1f / s : 250f; }
            set { GroundSet("_MacroScale", value > 1f ? 1f / value : 0.004f); }
        }
        public static float GroundDetailStrength { get => GroundGet("_DetailStrength", 0.5f); set => GroundSet("_DetailStrength", Mathf.Clamp01(value)); }
        public static float GroundSeaLevel { get => GroundGet("_SeaLevel", 0f); set => GroundSet("_SeaLevel", value); }
        public static float GroundSandHeight { get => GroundGet("_SandHeight", 3f); set => GroundSet("_SandHeight", Mathf.Max(0f, value)); }
        public static float GroundDetailSize   // metres per detail-overlay repeat
        {
            get { float s = GroundGet("_DetailScale", 0.6f); return s > 1e-4f ? 1f / s : 1.7f; }
            set { GroundSet("_DetailScale", value > 0.01f ? 1f / value : 0.6f); }
        }
        public static float GroundBrightness { get => GroundGet("_GroundBrightness", 0.8f); set => GroundSet("_GroundBrightness", Mathf.Clamp(value, 0f, 2f)); }

        static void ApplyGridMaterial()
        {
            if (_mat == null) return;
            // TerrainGround/TerrainSlope shaders draw the grid as a world-space overlay (_GridStrength).
            if (_mat.HasProperty("_GridStrength")) { _mat.SetFloat("_GridStrength", ShowGrid ? 0.85f : 0f); return; }
            // Legacy fallback material (no slope shader): swap a baked grid texture / flat green base colour.
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
            // DEADBAND: ignore look-point drift under RecenterDeadband chunks so panning the camera
            // around at altitude doesn't thrash the bubble (cull+load+collider-cook) every chunk. The
            // PreloadDepth margin covers the lag so no gap shows at the leading edge. A foot (zoom) change
            // always recomputes immediately.
            // (_center starts at int.MinValue — the uninitialized sentinel; Cheb on it overflows Mathf.Abs,
            // so treat that as "changed" before doing any distance arithmetic.)
            bool footChanged = _center.x == int.MinValue
                            || Cheb(center, _center) >= Mathf.Max(1, RecenterDeadband)
                            || foot != _lastFoot;
            // STREAMING (or eager): re-centre the resident set on the view → cull + load + re-LOD.
            // FROZEN (locked): keep the resident set put; only re-LOD the existing chunks as you
            // pan/zoom (refine where you look). New chunks never load, nothing culls.
            if (eager || FillRadius || (Streaming && footChanged))
            {
                _center = center; _lastFoot = foot; _lastDist = dist; _lastLodCenter = center;
                Recompute(center, foot);
                RefreshColliders();   // cook colliders that entered the near radius, drop those that left
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

            ProcessBakes();   // assign any async collision bakes that finished this frame (off-thread → no hitch)
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

        // A chunk may stream if it's not user-trimmed and (in a DEM world) actually inside the mosaic —
        // so the flat procedural apron beyond the DEM data never loads.
        static bool Streamable(Vector2Int c)
            => !_excluded.Contains(c) && (!DemChunkSource.Active || DemChunkSource.CoversChunk(c));

        static void Recompute(Vector2Int center, int foot)
        {
            List<Vector2Int> cull = null;
            foreach (var c in _chunks.Keys)
                if (Mathf.Abs(c.x - center.x) > foot || Mathf.Abs(c.y - center.y) > foot || !Streamable(c))
                    (cull ??= new List<Vector2Int>()).Add(c);
            if (cull != null) for (int i = 0; i < cull.Count; i++) UnloadChunk(cull[i]);

            _pending.Clear();
            for (int dz = -foot; dz <= foot; dz++)
                for (int dx = -foot; dx <= foot; dx++)
                {
                    var c = new Vector2Int(center.x + dx, center.y + dz);
                    if (!Streamable(c)) continue;                         // trimmed / outside DEM coverage: never load
                    if (!_chunks.ContainsKey(c)) _pending.Add(c);
                    else if (_chunks[c].LodRes != DesiredRes(c)) _pending.Add(c);
                }
            _pending.Sort((a, b) => Cheb(b, center).CompareTo(Cheb(a, center)));
        }

        // ── Collider gating ──────────────────────────────────────────────────────────────────
        // MeshCollider cooking is the biggest per-load hitch, and you only ever click/sculpt/raycast
        // near where you're looking. So only chunks within ColliderRadius of the centre carry a cooked
        // collision mesh; the rest keep the component but a null sharedMesh (no cook, no physics cost).
        // LOCKED bubble (frozen) → the WHOLE bubble is collidable, so you can sculpt anywhere incl. the
        // corners. STREAMING (flying) → cover out to ColliderRadius of the centre (capped by the loaded
        // foot). Default cap (32) covers a normal bubble in both states; async baking keeps it cheap.
        static bool NeedsCollider(Vector2Int c)
        {
            if (!Streaming) return true;
            int cap = Mathf.Max(0, ColliderRadius);
            if (_lastFoot > 0) cap = Mathf.Min(cap, _lastFoot);
            return Cheb(c, _center) <= cap;
        }

        // Kick an async collision bake for this chunk on a worker thread (must finish before the mesh is
        // mutated or destroyed — see BuildMesh/UnloadChunk). ProcessBakes assigns the result on completion.
        static void ScheduleBake(Chunk ch)
        {
            if (ch.Mc == null || ch.Mesh == null) return;
            if (ch.BakePending) ch.BakeHandle.Complete();   // never overlap two bakes of the same mesh
            ch.BakeHandle = new BakeMeshJob { MeshId = ch.Mesh.GetInstanceID() }.Schedule();
            ch.BakePending = true;
        }

        // Decide this chunk's collider. Near → (re)bake async; far → drop it. geometryChanged is true
        // after a BuildMesh (the collision is stale and must re-bake); false on a plain re-centre refresh
        // (only newly-near chunks without a collider need a bake — idempotent, no redundant cooking).
        static void CookCollider(Chunk ch, bool geometryChanged)
        {
            if (ch.Mc == null) return;
            if (!NeedsCollider(ch.Coord))
            {
                if (ch.BakePending) { ch.BakeHandle.Complete(); ch.BakePending = false; }
                if (ch.Mc.sharedMesh != null) ch.Mc.sharedMesh = null;
                return;
            }
            if (geometryChanged || (ch.Mc.sharedMesh == null && !ch.BakePending)) ScheduleBake(ch);
        }

        // Assign a freshly-baked mesh to the collider (cheap — uses the cached bake). Null→set forces the
        // collider to re-read even when it already referenced the same mesh object. MUST be called while
        // the mesh still matches the bake (before any new BuildMesh mutates it), or it re-cooks on the spot.
        static void AssignBakedCollider(Chunk ch)
        {
            if (ch.Mc != null && NeedsCollider(ch.Coord)) { ch.Mc.sharedMesh = null; ch.Mc.sharedMesh = ch.Mesh; }
        }

        // Per-frame: any finished bake → assign the pre-cooked mesh on the main thread (cheap, no hitch).
        static void ProcessBakes()
        {
            foreach (var kv in _chunks)
            {
                var ch = kv.Value;
                if (!ch.BakePending || !ch.BakeHandle.IsCompleted) continue;
                ch.BakeHandle.Complete();
                ch.BakePending = false;
                AssignBakedCollider(ch);
            }
        }

        // Drain all in-flight bakes (shutdown / before bulk teardown) so no worker thread touches a mesh
        // we're about to destroy.
        static void CompleteAllBakes()
        {
            foreach (var kv in _chunks)
                if (kv.Value.BakePending) { kv.Value.BakeHandle.Complete(); kv.Value.BakePending = false; }
        }

        // After a re-centre, refresh collider membership: bake chunks that just entered the near radius,
        // drop those that left. Only the boundary ring changes, so this is cheap.
        static void RefreshColliders()
        {
            foreach (var kv in _chunks) CookCollider(kv.Value, false);
        }

        // Re-cook all colliders to the current ColliderRadius (after the tunable changes).
        public static void RefreshCollidersNow() { if (Active) RefreshColliders(); }

        // Rebuild every resident chunk's mesh (cheap vs RefillAll — no DEM re-derive), e.g. when the
        // Skirts toggle flips. Re-cooks near colliders too since the mesh changed.
        public static void RebuildAllMeshes()
        {
            if (!Active) return;
            foreach (var kv in _chunks) { BuildMesh(kv.Value); CookCollider(kv.Value, true); }
        }

        // ── Topo contour overlay ─────────────────────────────────────────────────────────────
        // Per-pixel shader drawn as a 2nd renderer over each chunk's mesh (shared — auto-updates on
        // rebuild). Purely visual; nothing baked. Returns null if the shader isn't in the project.
        static Material ContourMat()
        {
            if (_contourMat == null)
            {
                var sh = Shader.Find("NetworkDesigner/ContourOverlay");
                if (sh == null) { Debug.LogWarning("[ChunkWorld] ContourOverlay shader not found."); return null; }
                _contourMat = new Material(sh) { name = "ContourOverlay" };
                ApplyContourParams();
            }
            return _contourMat;
        }

        static void ApplyContourParams()
        {
            if (_contourMat == null) return;
            _contourMat.SetFloat("_MinorInterval", Mathf.Max(0.05f, ContourMinor));
            _contourMat.SetFloat("_MajorEvery", Mathf.Max(1f, ContourMajorEvery));
            _contourMat.SetFloat("_Strength", Mathf.Clamp01(ContourStrength));
        }

        static void EnsureContourChild(Chunk ch)
        {
            if (ch.ContourGo != null || ch.Go == null) return;
            var mat = ContourMat(); if (mat == null) return;
            var go = new GameObject("Contour");
            go.transform.SetParent(ch.Go.transform, false);   // local 0 → same world transform as the chunk
            go.AddComponent<MeshFilter>().sharedMesh = ch.Mesh;   // SHARED → follows mesh rebuilds for free
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            ch.ContourGo = go;
        }

        public static void SetContours(bool on)
        {
            ShowContours = on;
            if (on && ContourMat() == null) { ShowContours = false; return; }   // shader missing → no-op
            foreach (var kv in _chunks)
            {
                if (on) EnsureContourChild(kv.Value);
                if (kv.Value.ContourGo != null) kv.Value.ContourGo.SetActive(on);
            }
        }

        public static void SetContourMinor(float m) { ContourMinor = Mathf.Clamp(m, 0.1f, 1000f); ApplyContourParams(); }
        public static void SetContourStrength(float s) { ContourStrength = Mathf.Clamp01(s); ApplyContourParams(); }

        // ── Load / unload / LOD rebuild ──────────────────────────────────────────────────────
        static void LoadChunk(Vector2Int c)
        {
            int res = DesiredRes(c);
            var ch = new Chunk { H = new float[res * res], LodRes = res, Coord = c };
            FillHeights(c, ch.H, res);
            LoadEdit(c, ch);        // loads the sculpt delta at its native res
            OverlayEdit(ch);        // render it at this chunk's res (resampled copy)

            var go = new GameObject($"Chunk_{c.x}_{c.y}");
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(c.x * ChunkSize, 0f, c.y * ChunkSize);
            ch.Go = go;
            ch.Mesh = new Mesh { name = $"ChunkMesh_{c.x}_{c.y}" };
            BuildMesh(ch);
            ch.Mf = go.AddComponent<MeshFilter>(); ch.Mf.sharedMesh = ch.Mesh;
            ch.Mr = go.AddComponent<MeshRenderer>(); ch.Mr.sharedMaterial = _mat;
            ch.Mc = go.AddComponent<MeshCollider>(); CookCollider(ch, true);   // only cooks if near the centre
            if (ShowContours) EnsureContourChild(ch);   // 2nd renderer for the topo overlay

            _chunks[c] = ch;
            _everLoaded.Add(c);
            _loadTime[c] = Time.realtimeSinceStartup;
        }

        static void RebuildLod(Vector2Int c, int newRes)
        {
            if (!_chunks.TryGetValue(c, out var ch)) return;
            SaveChunkEdits(c);
            ch.LodRes = newRes;
            ch.H = new float[newRes * newRes];
            FillHeights(c, ch.H, newRes);
            OverlayEdit(ch);   // re-render the edit at the new res from the FULL-FIDELITY ch.Abs/ch.Op
            BuildMesh(ch);
            CookCollider(ch, true);
        }

        static void UnloadChunk(Vector2Int c)
        {
            if (!_chunks.TryGetValue(c, out var ch)) return;
            if (ch.BakePending) { ch.BakeHandle.Complete(); ch.BakePending = false; }   // don't destroy a mesh mid-bake
            SaveChunkEdits(c);
            if (ch.Go != null) { if (Application.isPlaying) Object.Destroy(ch.Go); else Object.DestroyImmediate(ch.Go); }
            if (ch.Mesh != null) { if (Application.isPlaying) Object.Destroy(ch.Mesh); else Object.DestroyImmediate(ch.Mesh); }
            _chunks.Remove(c);
            // (DEM tiles are shared across chunks → freed by DemChunkSource's own LRU, not per-chunk.)
        }

        // ── Mesh build (per-chunk res) ───────────────────────────────────────────────────────
        // Each chunk mesh is a res×res grid PLUS a SKIRT: walls hung straight down around the border.
        // When a neighbour renders at a different LOD its border line won't match this chunk's, leaving
        // a thin gap you'd see sky through — the skirt wall fills that sliver. The skirt gets its OWN
        // top ring (duplicates of the perimeter verts) so the grid's vertex normals stay clean and the
        // shared-vertex tilt doesn't shade-seam every chunk edge. Layout:
        //   [0 .. res²-1]            grid
        //   [res² .. res²+P-1]       skirt TOP ring  (= perimeter positions, full height)
        //   [res²+P .. res²+2P-1]    skirt BOTTOM ring (top − drop)
        static int PerimCount(int res) => 4 * (res - 1);

        // Perimeter grid indices walked CCW-from-above (interior on the left → outward normal to the
        // right of the walk), so the skirt walls front-face outward to match the grid's winding.
        static int[] Perim(int res)
        {
            if (_perimByRes.TryGetValue(res, out var p)) return p;
            p = new int[PerimCount(res)];
            int k = 0;
            for (int x = 0; x < res; x++) p[k++] = x;                          // bottom z=0, +x
            for (int z = 1; z < res; z++) p[k++] = z * res + (res - 1);        // right x=res-1, +z
            for (int x = res - 2; x >= 0; x--) p[k++] = (res - 1) * res + x;   // top z=res-1, -x
            for (int z = res - 2; z >= 1; z--) p[k++] = z * res;               // left x=0, -z
            _perimByRes[res] = p;
            return p;
        }

        static (Vector3[] v, int[] t, Vector2[] uv) Buffers(int res)
        {
            int P = PerimCount(res);
            int vCount = res * res + 2 * P;
            var perim = Perim(res);
            if (!_vertsByRes.TryGetValue(res, out var v) || v.Length != vCount) { v = new Vector3[vCount]; _vertsByRes[res] = v; }
            if (!_trisByRes.TryGetValue(res, out var t))
            {
                t = new int[(res - 1) * (res - 1) * 6 + P * 6];   // grid quads + skirt wall quads
                int ti = 0;
                for (int z = 0; z < res - 1; z++)
                    for (int x = 0; x < res - 1; x++)
                    {
                        int a = z * res + x, b = a + 1, cc = a + res, d = cc + 1;
                        t[ti++] = a; t[ti++] = cc; t[ti++] = b;
                        t[ti++] = b; t[ti++] = cc; t[ti++] = d;
                    }
                int topBase = res * res, botBase = res * res + P;
                for (int e = 0; e < P; e++)
                {
                    int e1 = (e + 1) % P;
                    int t0 = topBase + e, t1 = topBase + e1;   // skirt top ring
                    int b0 = botBase + e, b1 = botBase + e1;   // skirt bottom ring
                    // (t0,t1,b1),(t0,b1,b0) → outward-facing wall (see Perim() winding note).
                    t[ti++] = t0; t[ti++] = t1; t[ti++] = b1;
                    t[ti++] = t0; t[ti++] = b1; t[ti++] = b0;
                }
                _trisByRes[res] = t;
            }
            if (!_uvByRes.TryGetValue(res, out var uv) || uv.Length != vCount)
            {
                uv = new Vector2[vCount];
                float inv = 1f / (res - 1);   // local XZ → 0..1 across the chunk (grid tile per 1 km)
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                        uv[z * res + x] = new Vector2(x * inv, z * inv);
                for (int e = 0; e < P; e++)
                {
                    Vector2 puv = uv[perim[e]];                 // both skirt rings inherit the edge vert's UV
                    uv[res * res + e] = puv;
                    uv[res * res + P + e] = puv;
                }
                _uvByRes[res] = uv;
            }
            return (v, t, uv);
        }

        static void BuildMesh(Chunk ch)
        {
            // A bake job may be reading this mesh on a worker thread — finish it before we mutate it, and
            // assign its result first so the collider tracks 1 frame behind (not frozen) across a stroke.
            if (ch.BakePending) { ch.BakeHandle.Complete(); ch.BakePending = false; AssignBakedCollider(ch); }
            int res = ch.LodRes; float sp = ChunkSize / (res - 1);
            var (verts, tris, uvs) = Buffers(res);
            var perim = Perim(res);
            var H = ch.H;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    verts[i] = new Vector3(x * sp, H[i], z * sp);
                }
            // Skirt top ring = the perimeter verts; bottom ring = top − drop. Drop is deeper at coarse
            // LODs (bigger cell → bigger possible seam mismatch); floored at 10 m so high-res chunks
            // still cover small cracks. Skirts off → drop 0: the walls collapse to zero-area (invisible)
            // without touching the cached buffers, so it's a clean runtime toggle.
            float drop = Skirts ? Mathf.Max(10f, sp * SkirtFactor) : 0f;
            int topBase = res * res, botBase = res * res + perim.Length;
            for (int e = 0; e < perim.Length; e++)
            {
                Vector3 top = verts[perim[e]];
                verts[topBase + e] = top;
                verts[botBase + e] = new Vector3(top.x, top.y - drop, top.z);
            }
            var m = ch.Mesh;
            m.Clear();
            if (verts.Length > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = verts;
            m.uv = uvs;
            m.triangles = tris;
            // Analytic normals from the height GRADIENT (not mesh-local RecalculateNormals): edge vertices sample
            // the neighbour's height across the chunk border, so both sides of a seam get matching normals → no
            // shading step. Interior reads ch.H directly; only the perimeter pays a cross-chunk SampleHeight.
            var normals = NormalsBuf(res);
            ComputeChunkNormals(ch, res, sp, perim, normals);
            m.normals = normals;
            m.RecalculateBounds();
        }

        // Reused per-res scratch for the analytic normals (copied into the mesh on assign, so reuse is safe).
        static readonly Dictionary<int, Vector3[]> _normalsByRes = new Dictionary<int, Vector3[]>();
        static Vector3[] NormalsBuf(int res)
        {
            int vCount = res * res + 2 * PerimCount(res);
            if (!_normalsByRes.TryGetValue(res, out var nrm) || nrm.Length != vCount) { nrm = new Vector3[vCount]; _normalsByRes[res] = nrm; }
            return nrm;
        }

        // Per-vertex normal = normalize(-dH/dx, 1, -dH/dz). Interior uses ch.H; edge verts read the neighbour's
        // height across the border so seam normals match. Skirt rings inherit their perimeter vertex's normal.
        static void ComputeChunkNormals(Chunk ch, int res, float sp, int[] perim, Vector3[] normals)
        {
            var H = ch.H;
            float ox = ch.Coord.x * ChunkSize, oz = ch.Coord.y * ChunkSize;
            float inv2sp = 1f / (2f * sp);
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    float hl = x > 0        ? H[i - 1]   : EdgeHeight(ox + (x - 1) * sp, oz + z * sp);
                    float hr = x < res - 1  ? H[i + 1]   : EdgeHeight(ox + (x + 1) * sp, oz + z * sp);
                    float hd = z > 0        ? H[i - res] : EdgeHeight(ox + x * sp, oz + (z - 1) * sp);
                    float hu = z < res - 1  ? H[i + res] : EdgeHeight(ox + x * sp, oz + (z + 1) * sp);
                    float nx = -(hr - hl) * inv2sp, nz = -(hu - hd) * inv2sp;
                    float invLen = 1f / Mathf.Sqrt(nx * nx + 1f + nz * nz);
                    normals[i] = new Vector3(nx * invLen, invLen, nz * invLen);
                }
            int topBase = res * res, botBase = res * res + perim.Length;
            for (int e = 0; e < perim.Length; e++)
            {
                Vector3 pn = normals[perim[e]];
                normals[topBase + e] = pn;
                normals[botBase + e] = pn;
            }
        }

        // Height just outside a chunk edge for the normal gradient: the neighbour's EDITED surface if it's
        // loaded, else the continuous SOURCE (avoids SampleHeight's 0 for an unloaded neighbour mid-stream).
        static float EdgeHeight(float wx, float wz)
        {
            var c = ChunkOf(wx, wz);
            return (_chunks.TryGetValue(c, out var ch) && ch.H != null) ? SampleHeight(wx, wz) : SourceAt(c, wx, wz);
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

        // Original (un-edited) source ground height at a world XZ — the DEM/procedural surface BEFORE any
        // sculpt overlay. Use this (not SampleHeight) when an edit must be idempotent w.r.t. its own prior
        // cuts (e.g. road excavation grading off the node ground, so re-running doesn't dig progressively).
        public static float SampleSourceHeight(float x, float z) => SourceAt(ChunkOf(x, z), x, z);

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
                EnsureEditRes(ch, res);   // seed Abs from the live DEM heights before we read/smooth it
                int kernel = Mathf.Clamp(Mathf.RoundToInt(radius / sp * 0.25f), 2, 12);
                float blendF = Mathf.Clamp01(strength * dt * 0.12f);
                float blendS = Mathf.Clamp01(strength * dt * 0.7f);
                float stepM = strength * dt * 3f;
                bool changed = false;
                for (int zz = z0; zz <= z1; zz++)
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        float wx = ox + xx * sp, wz = oz + zz * sp;
                        float dx = wx - world.x, dz = wz - world.z, d2 = dx * dx + dz * dz;
                        if (d2 > r2) continue;
                        float fall = 1f - Mathf.Sqrt(d2) / radius;
                        fall = fall * fall * (3f - 2f * fall);
                        int i = zz * res + xx;
                        // Brushes edit the stored ABSOLUTE surface (Abs); Op tracks override coverage.
                        float a = ch.Abs[i];
                        switch (mode)
                        {
                            case DemTerrainWorld.SculptMode.Raise: a += stepM * fall; break;
                            case DemTerrainWorld.SculptMode.Lower: a -= stepM * fall; break;
                            case DemTerrainWorld.SculptMode.Flatten: a = Mathf.Lerp(a, targetY, blendF * fall); break;
                            // Average the GLOBAL surface in world space so the box reaches across chunk
                            // seams — chunk-local averaging splits shared edges and tears a gap there.
                            case DemTerrainWorld.SculptMode.Smooth: a = Mathf.Lerp(a, WorldBoxAvg(wx, wz, sp, kernel), blendS * fall); break;
                        }
                        // Coverage plateaus to 1 over the inner ~half of the brush (so the edited core
                        // fully overrides DEM — no re-derived detail leak) and feathers the rim.
                        float newOp = Mathf.Max(ch.Op[i], Mathf.Clamp01(fall * 2f));
                        float newH = Mathf.Lerp(SourceAt(c, wx, wz), a, newOp);
                        if (a != ch.Abs[i] || newOp != ch.Op[i] || newH != ch.H[i])
                        {
                            ch.Abs[i] = a;
                            ch.Op[i] = newOp;
                            ch.H[i] = newH;
                            changed = true;
                        }
                    }
                if (changed) { ch.EditDirty = true; BuildMesh(ch); CookCollider(ch, true); }
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
                bool changed = false;
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                    {
                        float wx = ox + x * sp, wz = oz + z * sp;
                        var cc = new Vector2Int(Mathf.FloorToInt(wx / cell), Mathf.FloorToInt(wz / cell));
                        if (!sel.Contains(cc)) continue;
                        int i = z * res + x;
                        if (Mathf.Abs(ch.H[i] - seedH) > tolerance) continue;   // don't carve land inside a sea cell
                        if (ch.H[i] != target || ch.Op == null || ch.EditRes != res || ch.Op[i] < 1f)
                        {
                            EnsureEditRes(ch, res);
                            ch.Abs[i] = target;
                            ch.Op[i] = 1f;          // full override — the carved sea bed replaces the DEM
                            ch.H[i] = target;
                            changed = true;
                        }
                    }
                if (changed) { ch.EditDirty = true; BuildMesh(ch); CookCollider(ch, true); }
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

        // ── Rail cut/fill: grade the terrain to a corridor of bed targets ───────────────────────
        // Flatten the terrain to each (X, bedY, Z) target within ±halfWidth, with a flat plateau
        // (innerFrac of the width) feathering out to the existing surface — i.e. cut where the ground
        // is high, fill where it's low. Writes the LOD-independent edit overlay and rebuilds each
        // touched chunk's mesh once. Re-drape the rail afterwards so it sits on its new bed.
        public static void GradeCorridor(List<Vector3> targets, float halfWidth, float innerFrac)
        {
            if (!Active || targets == null || targets.Count == 0) return;
            float r = Mathf.Max(0.5f, halfWidth), r2 = r * r;
            float inner = Mathf.Clamp01(innerFrac) * r;
            float band = Mathf.Max(1e-3f, r - inner);
            var touched = new List<Vector2Int>();
            foreach (var kv in _chunks)
            {
                var ch = kv.Value; var c = kv.Key;
                int res = ch.LodRes; float sp = ChunkSize / (res - 1);
                float ox = c.x * ChunkSize, oz = c.y * ChunkSize;
                bool changed = false;
                for (int ti = 0; ti < targets.Count; ti++)
                {
                    Vector3 t = targets[ti];
                    if (t.x + r < ox || t.x - r > ox + ChunkSize || t.z + r < oz || t.z - r > oz + ChunkSize) continue;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt((t.x - r - ox) / sp), 0, res - 1);
                    int x1 = Mathf.Clamp(Mathf.CeilToInt((t.x + r - ox) / sp), 0, res - 1);
                    int z0 = Mathf.Clamp(Mathf.FloorToInt((t.z - r - oz) / sp), 0, res - 1);
                    int z1 = Mathf.Clamp(Mathf.CeilToInt((t.z + r - oz) / sp), 0, res - 1);
                    for (int zz = z0; zz <= z1; zz++)
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            float wx = ox + xx * sp, wz = oz + zz * sp;
                            float dx = wx - t.x, dz = wz - t.z, d2 = dx * dx + dz * dz;
                            if (d2 > r2) continue;
                            float dist = Mathf.Sqrt(d2);
                            float w = dist <= inner ? 1f : 1f - (dist - inner) / band;
                            w = Mathf.Clamp01(w); w = w * w * (3f - 2f * w);   // smoothstep feather
                            int i = zz * res + xx;
                            EnsureEditRes(ch, res);   // need the absolute surface to flatten toward the bed
                            float val = Mathf.Lerp(ch.Abs[i], t.y, w);   // pull the stored surface toward the bed
                            float newOp = Mathf.Max(ch.Op[i], w);        // feathered rim blends back to DEM
                            float newH = Mathf.Lerp(SourceAt(c, wx, wz), val, newOp);
                            if (val != ch.Abs[i] || newOp != ch.Op[i] || newH != ch.H[i])
                            {
                                ch.Abs[i] = val;
                                ch.Op[i] = newOp;
                                ch.H[i] = newH;
                                changed = true;
                            }
                        }
                }
                if (changed) { ch.EditDirty = true; touched.Add(c); }
            }
            for (int i = 0; i < touched.Count; i++)
            {
                var ch = _chunks[touched[i]];
                BuildMesh(ch);
                CookCollider(ch, true);
            }
        }

        // ── Engineering corridor: flat bed + daylighting batters ────────────────────────────────
        // A flat BED (±bedHalfWidth) follows the centreline targets (X, bedY, Z); beyond it the ground
        // ramps at a BATTER of 1:batterN (1 vertical : N horizontal) until it DAYLIGHTS into the
        // existing terrain — cut where the land's high, fill (embankment) where it's low. The batter
        // extent is dynamic (deeper cut/fill → wider batter). Writes the LOD-independent edit overlay.
        // O(verts × targets) — meant for short slope-tool spans, not the whole-network grade.
        public static void GradeBatter(List<Vector3> targets, float bedHalfWidth, float batterN)
        {
            if (!Active || targets == null || targets.Count == 0) return;
            float bed = Mathf.Max(0f, bedHalfWidth);
            float n = Mathf.Max(0.25f, batterN);
            const float maxRun = 120f;                       // cap the daylight search beyond the bed (m)
            float reach = bed + maxRun, reach2 = reach * reach;
            float minX = 1e9f, maxX = -1e9f, minZ = 1e9f, maxZ = -1e9f;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.x < minX) minX = t.x; if (t.x > maxX) maxX = t.x;
                if (t.z < minZ) minZ = t.z; if (t.z > maxZ) maxZ = t.z;
            }
            minX -= reach; maxX += reach; minZ -= reach; maxZ += reach;
            var touched = new List<Vector2Int>();
            foreach (var kv in _chunks)
            {
                var ch = kv.Value; var c = kv.Key;
                float ox = c.x * ChunkSize, oz = c.y * ChunkSize;
                if (ox + ChunkSize < minX || ox > maxX || oz + ChunkSize < minZ || oz > maxZ) continue;
                int res = ch.LodRes; float sp = ChunkSize / (res - 1);
                int vx0 = Mathf.Clamp(Mathf.FloorToInt((minX - ox) / sp), 0, res - 1);
                int vx1 = Mathf.Clamp(Mathf.CeilToInt((maxX - ox) / sp), 0, res - 1);
                int vz0 = Mathf.Clamp(Mathf.FloorToInt((minZ - oz) / sp), 0, res - 1);
                int vz1 = Mathf.Clamp(Mathf.CeilToInt((maxZ - oz) / sp), 0, res - 1);
                bool changed = false;
                for (int zz = vz0; zz <= vz1; zz++)
                    for (int xx = vx0; xx <= vx1; xx++)
                    {
                        float wx = ox + xx * sp, wz = oz + zz * sp;
                        float bestD2 = reach2, bedElev = 0f; bool found = false;
                        for (int ti = 0; ti < targets.Count; ti++)   // nearest centreline sample
                        {
                            var t = targets[ti]; float dx = wx - t.x, dz = wz - t.z, d2 = dx * dx + dz * dz;
                            if (d2 < bestD2) { bestD2 = d2; bedElev = t.y; found = true; }
                        }
                        if (!found) continue;
                        int i = zz * res + xx;
                        float terrain = ch.H[i];
                        float d = Mathf.Sqrt(bestD2);
                        float graded;
                        if (d <= bed) graded = bedElev;
                        else
                        {
                            float rise = (d - bed) / n;   // 1:n batter
                            graded = terrain >= bedElev ? Mathf.Min(terrain, bedElev + rise)   // cut, daylight up
                                                        : Mathf.Max(terrain, bedElev - rise);  // fill, daylight down
                        }
                        if (graded != ch.H[i])
                        {
                            EnsureEditRes(ch, res);
                            ch.Abs[i] = graded;
                            ch.Op[i] = 1f;          // engineered cut/fill fully overrides the DEM
                            ch.H[i] = graded;
                            changed = true;
                        }
                    }
                if (changed) { ch.EditDirty = true; touched.Add(c); }
            }
            for (int i = 0; i < touched.Count; i++)
            {
                var ch = _chunks[touched[i]];
                BuildMesh(ch);
                CookCollider(ch, true);
            }
        }

        // World-space box average of the CURRENT surface (k cells each way at `step` spacing), sampling
        // across chunk boundaries via SampleHeight so smoothing stays continuous at seams. Samples that
        // land on an unloaded chunk are skipped (else they'd drag the average toward 0 at the bubble edge).
        static float WorldBoxAvg(float wx, float wz, float step, int k)
        {
            float sum = 0f; int n = 0;
            for (int dz = -k; dz <= k; dz++)
                for (int dx = -k; dx <= k; dx++)
                {
                    float sx = wx + dx * step, sz = wz + dz * step;
                    if (!_chunks.ContainsKey(ChunkOf(sx, sz))) continue;
                    sum += SampleHeight(sx, sz); n++;
                }
            return n > 0 ? sum / n : SampleHeight(wx, wz);
        }

        // DEM (or procedural) base height at a world position — the pre-sculpt surface.
        static float SourceAt(Vector2Int c, float wx, float wz)
            => DemChunkSource.CoversChunk(c) ? DemChunkSource.SampleWorldYAt(wx, wz) : HeightAt(wx, wz);

        // Bilinear-resample a dense grid (Abs heights or Op opacity) from srcRes to dstRes (both 2ⁿ+1).
        // This is what makes sculpts LOD-independent: the edit carries onto whatever res a chunk rebuilds at.
        static float[] ResampleGrid(float[] src, int srcRes, int dstRes)
        {
            if (src == null) return null;
            if (srcRes == dstRes) return src;
            var dst = new float[dstRes * dstRes];
            float scale = (srcRes - 1) / (float)(dstRes - 1);
            for (int z = 0; z < dstRes; z++)
            {
                float sz = z * scale; int z0 = Mathf.Clamp((int)sz, 0, srcRes - 1), z1 = Mathf.Min(z0 + 1, srcRes - 1); float tz = sz - z0;
                for (int x = 0; x < dstRes; x++)
                {
                    float sx = x * scale; int x0 = Mathf.Clamp((int)sx, 0, srcRes - 1), x1 = Mathf.Min(x0 + 1, srcRes - 1); float tx = sx - x0;
                    float a = src[z0 * srcRes + x0], b = src[z0 * srcRes + x1];
                    float cc = src[z1 * srcRes + x0], d = src[z1 * srcRes + x1];
                    dst[z * dstRes + x] = Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(cc, d, tx), tz);
                }
            }
            return dst;
        }

        // ── Per-chunk persistence (sparse absolute-override edit, res-tagged but RESAMPLED on load) ──
        static string ChunkFile(Vector2Int c)
            => string.IsNullOrEmpty(_editDir) ? null : Path.Combine(_editDir, $"chunk_{c.x}_{c.y}.bin");

        // Write the chunk's absolute-override edit (sparse, only cells with Op>0) at its native res.
        // "CHK4" = (Abs, Op) format. Each record: int index, float Abs, float Op.
        static void SaveChunkEdits(Vector2Int c)
        {
            if (!_chunks.TryGetValue(c, out var ch) || ch.Op == null || ch.Abs == null || !ch.EditDirty) return;
            string path = ChunkFile(c);
            if (path == null) return;
            float[] op = ch.Op, abs = ch.Abs;
            int count = 0; for (int i = 0; i < op.Length; i++) if (op[i] > 0f) count++;
            try
            {
                using var ms = new MemoryStream(count * 12 + 16);
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(0x43484b34);   // CHK4 — sparse (Abs, Op)
                    w.Write(ch.EditRes);   // the resolution Abs/Op are stored at (NOT the render res)
                    w.Write(count);
                    for (int i = 0; i < op.Length; i++)
                        if (op[i] > 0f) { w.Write(i); w.Write(abs[i]); w.Write(op[i]); }
                }
                File.WriteAllBytes(path, ms.ToArray());
                ch.EditDirty = false;
            }
            catch (System.Exception ex) { Debug.LogWarning($"[ChunkWorld] save {c} failed: {ex.Message}"); }
        }

        // Load the edit at its NATIVE (saved) res into ch.Abs/ch.Op/ch.EditRes — OverlayEdit renders it.
        // Reads CHK4 (Abs,Op) directly; migrates CHK3 (sparse delta → Abs = source+delta, Op = 1) and the
        // legacy CHK2 (sparse absolute → Abs = saved, Op = 1). Migrated cells get full opacity (hard edit).
        static void LoadEdit(Vector2Int c, Chunk ch)
        {
            string path = ChunkFile(c);
            if (path == null || !File.Exists(path)) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(bytes);
                using var r = new BinaryReader(ms);
                int magic = r.ReadInt32();
                bool isAbsOp = magic == 0x43484b34;   // CHK4
                bool isDelta = magic == 0x43484b33;   // CHK3 (legacy delta)
                bool isLegacyAbs = magic == 0x43484b32; // CHK2 (legacy absolute)
                if (!isAbsOp && !isDelta && !isLegacyAbs) return;
                int savedRes = r.ReadInt32();
                int n = r.ReadInt32();
                if (savedRes < 2) return;
                var abs = new float[savedRes * savedRes];
                var op = new float[savedRes * savedRes];   // 0 everywhere → unedited cells render pure DEM
                float spOld = ChunkSize / (savedRes - 1);
                float ox = c.x * ChunkSize, oz = c.y * ChunkSize;
                // SEED the whole absolute surface with the DEM so unedited cells hold the real height
                // (not 0). Brushes read ch.Abs[i] as their base and resampling interpolates it — leaving
                // 0-holes would crater the surface toward zero. Only the sparse edited cells override.
                // ch.H is the freshly DEM-filled render heights (LoadChunk fills before LoadEdit), so when
                // the saved res matches we copy it directly; otherwise sample the DEM at the saved res.
                if (ch.H != null && ch.H.Length == savedRes * savedRes)
                    System.Array.Copy(ch.H, abs, savedRes * savedRes);
                else
                    for (int z = 0; z < savedRes; z++)
                        for (int x = 0; x < savedRes; x++)
                            abs[z * savedRes + x] = SourceAt(c, ox + x * spOld, oz + z * spOld);
                for (int k = 0; k < n; k++)
                {
                    int i = r.ReadInt32();
                    if (isAbsOp)
                    {
                        float a = r.ReadSingle(), o = r.ReadSingle();
                        if (i < 0 || i >= abs.Length) continue;
                        abs[i] = a; op[i] = Mathf.Clamp01(o);
                    }
                    else
                    {
                        float v = r.ReadSingle();
                        if (i < 0 || i >= abs.Length) continue;
                        abs[i] = isDelta ? abs[i] + v : v;   // CHK3 delta (abs[i] already = DEM) vs CHK2 absolute
                        op[i] = 1f;
                    }
                }
                ch.Abs = abs; ch.Op = op; ch.EditRes = savedRes;   // native res; OverlayEdit resamples copies
                ch.EditDirty = false;
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
