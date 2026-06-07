// Builds a grid of Unity Terrain tiles from a folder of DEM heightmap PNGs
// (the "..._16bit_tile_<row>_<col>.png" exports). Render-only first step of the
// dual-backend terrain work: prove the tiles load, build as Unity Terrain, stitch
// seamlessly, and sit at the right world scale — before wiring ITerrainSurface
// draping or the Low-Poly toggle.
//
// Height reconstruction: a 16-bit DEM pixel 0..1 maps linearly to [normFrom,normTo]
// metres. Unity TerrainData stores heights 0..1 over size.y, so size.y = the metre
// RANGE and the terrain object sits at world Y = normFrom — then the stored 0..1 IS
// the pixel fraction, no rescaling. Tiles share 1-px edges, so placing tile (row,col)
// at (col*tileMeters, normFrom, row*tileMeters) lines the seams up.
//
// NOTE: heights are read via Texture2D.LoadImage + GetPixels — if Unity decodes the
// 16-bit PNG to 8-bit the relief will be terraced (~4 m steps over a 1020 m range);
// a dedicated 16-bit PNG decoder is the immediate follow-up if so. Orientation: row
// maps to +Z, so +Z is "south" here (consistent, just flip later if north-up wanted).

using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.IO;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class DemTerrainWorld
    {
        public const string RootName = "DemTerrainWorld";
        public const string BaseRel = "Heightmaps/Highres";   // Assets/Heightmaps/Highres/<City>/
        const int Res = 1025;   // Unity heightmap resolution per tile (2^10 + 1)

        // Live state for the surface mode (albedo / flat-green / slope-textured), kept so we
        // can swap without rebuilding.
        static UnityEngine.Terrain[,] _grid;
        static TerrainLayer[,] _albedo;   // per-tile draped imagery (null if none)
        static TerrainLayer _green;       // shared flat-green layer
        static TerrainLayer _ground, _rock, _patch, _rock2;   // runtime copies: grass, cliff, earth-patch, rock-variant
        static string _flatVar, _steepVar, _patchVar, _rock2Var; // which TerrainLayer assets are loaded
        static bool _macroActive;                     // last SetTextured used the 4-layer anti-tiling blend
        static GameObject _detailHolder;     // hidden parent for runtime grass detail-mesh prototypes

        // City folders under Assets/Heightmaps/Highres that contain DEM tiles.
        public static List<string> ListWorlds()
        {
            var list = new List<string>();
            string baseDir = Path.Combine(Application.dataPath, BaseRel);
            if (!Directory.Exists(baseDir)) return list;
            foreach (string d in Directory.GetDirectories(baseDir))
                if (Directory.GetFiles(d, "*16bit_tile*.png").Length > 0)
                    list.Add(Path.GetFileName(d));
            list.Sort();
            return list;
        }

        // Norm range is now a fixed -500..9000m for every city (export all DEMs with that
        // range), so there's no per-city norm file to read anymore.

        public static void Clear()
        {
            // Free the cropped albedo textures + the green texture we created.
            if (_albedo != null)
                foreach (var l in _albedo)
                    if (l != null && l.diffuseTexture != null) Object.DestroyImmediate(l.diffuseTexture);
            if (_green != null && _green.diffuseTexture != null) Object.DestroyImmediate(_green.diffuseTexture);
            _grid = null; _albedo = null; _green = null;
            CleanupDetailHolder();

            var existing = GameObject.Find(RootName);
            if (existing != null)
            {
                if (Application.isPlaying) Object.Destroy(existing);
                else Object.DestroyImmediate(existing);
            }
        }

        // Swap every tile between its draped albedo and a shared flat-green layer, live
        // (no rebuild). Tiles without albedo always show green.
        public static void SetGreen(bool green)
        {
            if (_grid == null) return;
            EnsureGreen();
            int rows = _grid.GetLength(0), cols = _grid.GetLength(1);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var t = _grid[r, c];
                    if (t == null || t.terrainData == null) continue;
                    TerrainLayer layer = (!green && _albedo != null && _albedo[r, c] != null) ? _albedo[r, c] : _green;
                    t.terrainData.terrainLayers = new[] { layer };
                }
        }

        static void EnsureGreen()
        {
            if (_green != null && _green.diffuseTexture != null) return;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, new Color(0.32f, 0.5f, 0.24f, 0f));   // alpha 0 = matte (terrain reads diffuse alpha as smoothness)
            tex.Apply();
            _green = new TerrainLayer { diffuseTexture = tex, tileSize = new Vector2(64f, 64f), smoothness = 0f, metallic = 0f };
        }

        // Slope-based texturing: two tiling layers (grass on flatter ground, rock on steeper),
        // blended per-tile by a control map computed from terrain steepness. slopeLow..slopeHigh
        // (degrees) is the rock transition band. Heavier than the toggle (computes splatmaps) —
        // a few seconds over 100 tiles.
        // Ground-pack variant folders (groundN) under Assets/Textures/ground_vol1.
        // Every TerrainLayer asset in the project (e.g. the Rocky Hills pack's Grass/Cliff/
        // Rock layers), by name — for the Flat/Steep slope-texture pickers.
        public static List<string> ListTerrainLayers()
        {
            var list = new List<string>();
#if UNITY_EDITOR
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:TerrainLayer"))
                list.Add(Path.GetFileNameWithoutExtension(UnityEditor.AssetDatabase.GUIDToAssetPath(guid)));
#endif
            list.Sort();
            return list;
        }

        // Slope texturing with anti-tiling. When patch+steepB are given (the default), uses a
        // 4-layer blend — grass/earth on flats, cliff/rock-variant on steeps — each pair mixed
        // by low-frequency world-space noise so NEITHER the meadow nor the cliffs read as a
        // repeating texture. Four layers = URP terrain's single-pass limit, so no extra cost.
        public static void SetTextured(string flatVariant, string steepVariant,
                                       string patchVariant = null, string steepBVariant = null,
                                       float slopeLow = 22f, float slopeHigh = 38f,
                                       float tileSizeMeters = 30f, int alphaRes = 256)
        {
            if (_grid == null) return;
            bool macro = !string.IsNullOrEmpty(patchVariant) && !string.IsNullOrEmpty(steepBVariant);
            _macroActive = macro;
            EnsureTextureLayers(flatVariant, steepVariant, macro ? patchVariant : null,
                                macro ? steepBVariant : null, tileSizeMeters);
            // macro layers: 0 grass, 1 earth, 2 cliff, 3 rock-variant. plain: 0 grass, 1 rock.
            var layers = macro ? new[] { _ground, _patch, _rock, _rock2 } : new[] { _ground, _rock };
            int nLayers = layers.Length;

            int rows = _grid.GetLength(0), cols = _grid.GetLength(1);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var t = _grid[r, c];
                    if (t == null || t.terrainData == null) continue;
                    var td = t.terrainData;
                    td.terrainLayers = layers;
                    if (td.alphamapResolution != alphaRes) td.alphamapResolution = alphaRes;
                    int res = td.alphamapResolution;
                    Vector3 origin = t.transform.position;   // world origin → noise continuous across tiles
                    float sx = td.size.x, sz = td.size.z;
                    var a = new float[res, res, nLayers];
                    for (int j = 0; j < res; j++)
                    {
                        float v = res > 1 ? (float)j / (res - 1) : 0f;
                        for (int i = 0; i < res; i++)
                        {
                            float u = res > 1 ? (float)i / (res - 1) : 0f;
                            float rock = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(slopeLow, slopeHigh, td.GetSteepness(u, v)));
                            float groundTotal = 1f - rock;
                            if (macro)
                            {
                                float wx = origin.x + u * sx, wz = origin.z + v * sz;
                                // flats: grass↔earth; steeps: cliff↔rock-variant. Different
                                // freqs/offsets so the two blends don't share a pattern. Only
                                // compute the blend that actually has weight here (most cells
                                // are purely one or the other) — unset channels stay 0.
                                if (groundTotal > 0.001f)
                                {
                                    float nFlat = Mathf.PerlinNoise(wx * 0.0055f, wz * 0.0055f) * 0.65f
                                                + Mathf.PerlinNoise(wx * 0.017f + 100f, wz * 0.017f + 100f) * 0.35f;
                                    float patchMix = Mathf.SmoothStep(0.42f, 0.62f, nFlat);
                                    a[j, i, 0] = groundTotal * (1f - patchMix);   // grass
                                    a[j, i, 1] = groundTotal * patchMix;          // earth/dry patch
                                }
                                if (rock > 0.001f)
                                {
                                    float nSteep = Mathf.PerlinNoise(wx * 0.0090f + 530f, wz * 0.0090f + 530f) * 0.6f
                                                 + Mathf.PerlinNoise(wx * 0.026f + 900f, wz * 0.026f + 900f) * 0.4f;
                                    float rockMix = Mathf.SmoothStep(0.40f, 0.60f, nSteep);
                                    a[j, i, 2] = rock * (1f - rockMix);           // cliff
                                    a[j, i, 3] = rock * rockMix;                  // rock variant
                                }
                            }
                            else { a[j, i, 0] = groundTotal; a[j, i, 1] = rock; }
                        }
                    }
                    td.SetAlphamaps(0, 0, a);
                }
            Debug.Log($"[DemTerrainWorld] slope textures applied (rock {slopeLow:0}-{slopeHigh:0}°"
                      + (macro ? $", 4-layer macro blend)." : ")."));
        }

        // Geometry LOD across all tiles: heightmapPixelError (LOWER = mesh stays detailed
        // farther out, but more triangles → costs FPS) + how far textures stay sharp.
        public static void SetTerrainLod(float pixelError, float basemapDistance = 8000f)
        {
            if (_grid == null) return;
            pixelError = Mathf.Clamp(pixelError, 1f, 200f);
            foreach (var t in _grid)
                if (t != null)
                {
                    t.heightmapPixelError = pixelError;
                    t.basemapDistance = Mathf.Max(0f, basemapDistance);
                }
        }

        // Live-change the texture repeat (metres per tile) without recomputing the slope
        // blend — just updates the shared layers and pokes each tile to refresh.
        public static void SetTextureTiling(float meters)
        {
            ApplyTileSizes(meters);   // keeps the per-layer offset frequencies
            if (_grid == null || _ground == null) return;
            var layers = (_macroActive && _patch != null && _rock2 != null)
                ? new[] { _ground, _patch, _rock, _rock2 } : new[] { _ground, _rock };
            foreach (var t in _grid)
                if (t != null && t.terrainData != null) t.terrainData.terrainLayers = layers;
        }

        // ── Path A: grass via Unity Terrain DETAIL meshes ───────────────────────────────
        // Scatter the pack's grass as terrain details (GPU-instanced, auto-LOD/culled by
        // Unity) so it scales to the whole world for free, with density driven by the same
        // slope map SetTextured uses (lush on flat ground, thinning out into rock on steeps).
        // The open question this answers: does the TTFE (Amplify) material render as a detail
        // mesh? If the grass comes out FLAT/untextured, detail instancing isn't honoring the
        // material and we pivot to a streamed prefab scatterer instead.
        public static void SetGrassDetail(float slopeLow = 22f, float slopeHigh = 38f,
                                          int detailRes = 1024, float viewDistance = 350f,
                                          int maxPerCell = 40, int maxGrass = 3,
                                          float sizeScale = 1.3f, float accents = 6f)
        {
            if (_grid == null) { Debug.LogWarning("[DemTerrainWorld] build a DEM world first."); return; }
            FoliageProbe.EnsureController();   // wind globals for the TTFE grass shader (no sway without it)

            EnsureDetailHolder();
            var grass = LoadPrototypes(GrassFolder, Mathf.Max(1, maxGrass), GrassScore);
            if (grass.Length == 0) { Debug.LogWarning("[DemTerrainWorld] no grass prefabs found for detail layer."); return; }
            // Sparse accents — a couple of bush + flower types interspersed to break up the grass.
            bool useAccents = accents > 0.001f;
            var bush = useAccents ? LoadPrototypes(BushFolder, 2, AccentScore) : System.Array.Empty<GameObject>();
            var flower = useAccents ? LoadPrototypes(FlowerFolder, 2, AccentScore) : System.Array.Empty<GameObject>();
            int nG = grass.Length, nB = bush.Length, nF = flower.Length;

            var all = new System.Collections.Generic.List<GameObject>(nG + nB + nF);
            all.AddRange(grass); all.AddRange(bush); all.AddRange(flower);

            var dp = new DetailPrototype[all.Count];
            for (int p = 0; p < all.Count; p++)
            {
                // per-kind scale: grass uses the Grass Size slider; bushes chunkier; flowers small.
                float lo, hiW, hiH;
                if (p < nG) { lo = 1.0f * sizeScale; hiW = 1.7f * sizeScale; hiH = 1.5f * sizeScale; }
                else if (p < nG + nB) { lo = 1.0f; hiW = 1.8f; hiH = 1.7f; }   // bushes
                else { lo = 0.7f; hiW = 1.2f; hiH = 1.1f; }                    // flowers
                dp[p] = new DetailPrototype
                {
                    prototype = all[p],
                    usePrototypeMesh = true,
                    useInstancing = true,                       // render the prototype's own material
                    renderMode = DetailRenderMode.VertexLit,    // required when instancing meshes
                    minWidth = lo, maxWidth = hiW,
                    minHeight = lo, maxHeight = hiH,
                    noiseSpread = 0.3f,
                    healthyColor = Color.white, dryColor = Color.white,
                };
            }

            int bushPer1000 = Mathf.RoundToInt(accents * 8f);      // ~sparse shrubs
            int flowerPer1000 = Mathf.RoundToInt(accents * 14f);   // a few more flower clusters

            int rows = _grid.GetLength(0), cols = _grid.GetLength(1);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var t = _grid[r, c];
                    if (t == null || t.terrainData == null) continue;
                    var td = t.terrainData;
                    td.SetDetailResolution(detailRes, 32);      // also clears existing layers
                    td.detailPrototypes = dp;

                    int res = td.detailResolution;
                    var maps = new int[dp.Length][,];
                    for (int p = 0; p < dp.Length; p++) maps[p] = new int[res, res];

                    for (int j = 0; j < res; j++)
                    {
                        float v = res > 1 ? (float)j / (res - 1) : 0f;
                        for (int i = 0; i < res; i++)
                        {
                            float u = res > 1 ? (float)i / (res - 1) : 0f;
                            float ground = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(slopeLow, slopeHigh, td.GetSteepness(u, v)));
                            if (ground <= 0.001f) continue;       // bare on the steeps

                            int count = Mathf.RoundToInt(maxPerCell * ground);
                            if (count > 0)
                            {
                                int gl = (((i * 73856093) ^ (j * 19349663)) & 0x7fffffff) % nG;   // spread grass variety
                                maps[gl][j, i] = count;
                            }
                            // Sparse accents, only on flatter ground (not the slope fringe).
                            if (useAccents && ground > 0.5f)
                            {
                                int hb = (((i * 92837111) ^ (j * 689287499)) & 0x7fffffff) % 1000;
                                if (nB > 0 && hb < bushPer1000) maps[nG + (hb % nB)][j, i] = 1;
                                int hf = (((i * 283923481) ^ (j * 198491317)) & 0x7fffffff) % 1000;
                                if (nF > 0 && hf < flowerPer1000) maps[nG + nB + (hf % nF)][j, i] = 1 + (hf % 3);
                            }
                        }
                    }
                    for (int p = 0; p < dp.Length; p++) td.SetDetailLayer(0, 0, p, maps[p]);

                    t.detailObjectDistance = Mathf.Clamp(viewDistance, 0f, 1000f);
                    t.detailObjectDensity = 1f;
                }
            Debug.Log($"[DemTerrainWorld] foliage detail applied: {nG} grass + {nB} bush + {nF} flower prototype(s), res {detailRes}, view {viewDistance:0}m.");
        }

        public static void ClearGrassDetail()
        {
            if (_grid != null)
                foreach (var t in _grid)
                    if (t != null && t.terrainData != null) t.terrainData.detailPrototypes = new DetailPrototype[0];
            CleanupDetailHolder();
        }

        // Cheap live tweak: how far grass details draw (no density remap). On these huge tiles
        // grass only ever renders within this radius of the camera, so it caps both how far you
        // see grass AND the instance count on screen.
        public static void SetGrassViewDistance(float meters)
        {
            if (_grid == null) return;
            meters = Mathf.Clamp(meters, 0f, 1000f);
            foreach (var t in _grid) if (t != null) t.detailObjectDistance = meters;
        }

        // Teleport the camera straight down to just above the DEM surface at its current XZ, so
        // you can actually inspect ground-level detail without hand-flying down from kilometres up.
        public static void DropCameraToSurface()
        {
            var cam = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
            if (cam == null) { Debug.LogWarning("[DemTerrainWorld] no camera."); return; }
            WireCameraToDem();
            Vector3 p = cam.transform.position;
            if (Physics.Raycast(new Vector3(p.x, 12000f, p.z), Vector3.down, out RaycastHit hit, 30000f))
                cam.transform.position = new Vector3(p.x, hit.point.y + 5f, p.z);
            else
                Debug.LogWarning("[DemTerrainWorld] no DEM surface under the camera's XZ.");
        }

        public enum SculptMode { Raise, Lower, Smooth, Flatten }

        // Brush-edit the DEM Unity Terrain heights at a world point, across tile seams. Every
        // overlapping tile processes the SAME world positions, so a shared seam pixel gets the
        // same delta/target on both sides → raise/lower/flatten stay seamless (smooth can drift
        // a hair at borders, it only sees same-tile neighbours). strength is metres/sec for
        // raise/lower (and the approach speed for flatten/smooth); targetY is the world height
        // for Flatten. Edits are IN-MEMORY (the runtime TerrainData) — rebuild/Clear resets,
        // and the source DEM PNGs are never touched. NOTE: the slope textures + grass density
        // were computed from the OLD shape, so re-apply them after big edits.
        public static void Sculpt(Vector3 world, float radius, float strength, float dt, SculptMode mode, float targetY)
        {
            if (_grid == null || radius <= 0f) return;
            float r2 = radius * radius;
            int rows = _grid.GetLength(0), cols = _grid.GetLength(1);
            var edited = new System.Collections.Generic.List<(int, int)>();
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var t = _grid[r, c];
                if (t == null || t.terrainData == null) continue;
                var td = t.terrainData;
                Vector3 o = t.transform.position;
                float sx = td.size.x, sz = td.size.z, sy = td.size.y;
                if (sx <= 0f || sz <= 0f || sy <= 0f) continue;
                if (world.x + radius < o.x || world.x - radius > o.x + sx) continue;   // AABB reject
                if (world.z + radius < o.z || world.z - radius > o.z + sz) continue;

                int res = td.heightmapResolution;
                float ppX = (res - 1) / sx, ppZ = (res - 1) / sz;   // heightmap pixels per metre
                int x0 = Mathf.Clamp(Mathf.FloorToInt((world.x - radius - o.x) * ppX), 0, res - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((world.x + radius - o.x) * ppX), 0, res - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((world.z - radius - o.z) * ppZ), 0, res - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((world.z + radius - o.z) * ppZ), 0, res - 1);
                int w = x1 - x0 + 1, h = z1 - z0 + 1;
                if (w <= 0 || h <= 0) continue;

                float[,] hm = td.GetHeights(x0, z0, w, h);   // [z, x], normalized 0..1
                // Smooth samples a WIDE box from the ORIGINAL heights (a copy) so it averages
                // real terrain features, not pixel neighbours / already-smoothed values.
                float[,] src = mode == SculptMode.Smooth ? (float[,])hm.Clone() : null;
                int kernel = Mathf.Clamp(Mathf.RoundToInt(radius * ppX * 0.25f), 2, 12);  // box radius in pixels
                float stepNorm = (strength * dt * 3f) / sy;   // raise/lower gain so it has real punch
                float targetNorm = Mathf.Clamp01((targetY - o.y) / sy);
                float blendF = Mathf.Clamp01(strength * dt * 0.12f);   // flatten approach
                float blendS = Mathf.Clamp01(strength * dt * 0.7f);    // smooth approach (stronger + wide kernel)
                bool changed = false;
                for (int zz = 0; zz < h; zz++)
                    for (int xx = 0; xx < w; xx++)
                    {
                        float wx = o.x + (x0 + xx) / ppX, wz = o.z + (z0 + zz) / ppZ;
                        float dx = wx - world.x, dz = wz - world.z, d2 = dx * dx + dz * dz;
                        if (d2 > r2) continue;
                        float fall = 1f - Mathf.Sqrt(d2) / radius;
                        fall = fall * fall * (3f - 2f * fall);   // smoothstep falloff
                        float val = hm[zz, xx];
                        switch (mode)
                        {
                            case SculptMode.Raise: val += stepNorm * fall; break;
                            case SculptMode.Lower: val -= stepNorm * fall; break;
                            case SculptMode.Flatten: val = Mathf.Lerp(val, targetNorm, blendF * fall); break;
                            case SculptMode.Smooth: val = Mathf.Lerp(val, BoxAvg(src, xx, zz, w, h, kernel), blendS * fall); break;
                        }
                        val = Mathf.Clamp01(val);
                        if (val != hm[zz, xx]) { hm[zz, xx] = val; changed = true; }
                    }
                if (changed) { td.SetHeights(x0, z0, hm); edited.Add((r, c)); }
            }
            // Reconcile shared tile edges so brushes (esp. Smooth, which averages within a tile)
            // don't crack open seams between grid blocks. All DEM tiles share the same height
            // normalization, so averaging the shared edge values is exact.
            foreach (var (r, c) in edited)
            {
                StitchEast(r, c); StitchEast(r, c - 1);     // shared columns (±X)
                StitchSouth(r, c); StitchSouth(r - 1, c);   // shared rows (±Z)
            }
        }

        // Average the shared COLUMN between [r,c] (its +X edge) and [r,c+1] (its -X edge).
        static void StitchEast(int r, int c)
        {
            int rows = _grid.GetLength(0), cols = _grid.GetLength(1);
            if (r < 0 || r >= rows || c < 0 || c + 1 >= cols) return;
            var L = _grid[r, c]; var R = _grid[r, c + 1];
            if (L == null || R == null || L.terrainData == null || R.terrainData == null) return;
            int res = L.terrainData.heightmapResolution;
            if (R.terrainData.heightmapResolution != res) return;
            var lc = L.terrainData.GetHeights(res - 1, 0, 1, res);   // [z,1]
            var rc = R.terrainData.GetHeights(0, 0, 1, res);
            for (int z = 0; z < res; z++) { float a = 0.5f * (lc[z, 0] + rc[z, 0]); lc[z, 0] = a; rc[z, 0] = a; }
            L.terrainData.SetHeights(res - 1, 0, lc);
            R.terrainData.SetHeights(0, 0, rc);
        }

        // Average the shared ROW between [r,c] (its -Z edge, z=0) and [r+1,c] (its +Z edge, z=res-1).
        static void StitchSouth(int r, int c)
        {
            int rows = _grid.GetLength(0), cols = _grid.GetLength(1);
            if (r < 0 || r + 1 >= rows || c < 0 || c >= cols) return;
            var N = _grid[r, c]; var S = _grid[r + 1, c];
            if (N == null || S == null || N.terrainData == null || S.terrainData == null) return;
            int res = N.terrainData.heightmapResolution;
            if (S.terrainData.heightmapResolution != res) return;
            var nr = N.terrainData.GetHeights(0, 0, res, 1);          // [1,res] row z=0
            var sr = S.terrainData.GetHeights(0, res - 1, res, 1);    // row z=res-1
            for (int x = 0; x < res; x++) { float a = 0.5f * (nr[0, x] + sr[0, x]); nr[0, x] = a; sr[0, x] = a; }
            N.terrainData.SetHeights(0, 0, nr);
            S.terrainData.SetHeights(0, res - 1, sr);
        }

        // Average of a (2k+1)² box around (x,z), clamped to the sub-array.
        static float BoxAvg(float[,] s, int x, int z, int w, int h, int k)
        {
            int xa = Mathf.Max(0, x - k), xb = Mathf.Min(w - 1, x + k);
            int za = Mathf.Max(0, z - k), zb = Mathf.Min(h - 1, z + k);
            float sum = 0f; int n = 0;
            for (int zz = za; zz <= zb; zz++)
                for (int xx = xa; xx <= xb; xx++) { sum += s[zz, xx]; n++; }
            return n > 0 ? sum / n : s[z, x];
        }

        // Point the fly camera's terrain-aware clamp + speed-damping at the DEM surface (it's
        // wired to the low-poly terrain by default, which reads Y≈0 here → no slowdown near the
        // real surface). Raycast-based so it works regardless of tile heights.
        static void WireCameraToDem()
        {
            var fly = Object.FindFirstObjectByType<NetworkDesigner.Designer.FlyCameraController>();
            if (fly != null) fly.GroundHeight = DemGroundHeight;
        }

        static float DemGroundHeight(Vector3 p)
        {
            return Physics.Raycast(new Vector3(p.x, 12000f, p.z), Vector3.down, out RaycastHit hit, 30000f)
                ? hit.point.y : 0f;
        }

        // Build up to `max` single-mesh detail prototypes from the pack's PV_Grass LOD prefabs.
        // Detail prototypes need a plain MeshFilter+MeshRenderer GameObject (the grass prefabs
        // are LODGroups), so we lift LOD0's mesh + material into a hidden holder and enable GPU
        // instancing on MATERIAL COPIES (never mutate the pack assets). Grass-named prefabs first.
        const string VegRoot = "Assets/Toby Fredson/Rocky Hills Environment - Whitebark Pine/RHEWP_Demo/Prefabs/Prefabs_Vegetation";
        const string GrassFolder = VegRoot + "/PV_Grass";
        const string BushFolder = VegRoot + "/PV_Bushes";
        const string FlowerFolder = VegRoot + "/PV_Flowers";

        // Prefer the FULL GREEN grass: "Clump"/"Grass", reject "Dry" (pale tan dots) and sparse weeds.
        static int GrassScore(string p)
        {
            int s = 0;
            if (p.IndexOf("Clump", System.StringComparison.OrdinalIgnoreCase) >= 0) s += 4;
            if (p.IndexOf("Grass", System.StringComparison.OrdinalIgnoreCase) >= 0) s += 2;
            if (p.IndexOf("Dry", System.StringComparison.OrdinalIgnoreCase) >= 0) s -= 5;
            if (p.IndexOf("Shot", System.StringComparison.OrdinalIgnoreCase) >= 0) s -= 2;
            if (p.IndexOf("Stalk", System.StringComparison.OrdinalIgnoreCase) >= 0) s -= 2;
            if (p.IndexOf("Plants", System.StringComparison.OrdinalIgnoreCase) >= 0) s -= 3;
            return s;
        }

        // For bushes/flowers: just avoid the dry/dead variants so accents stay green/colourful.
        static int AccentScore(string p)
        {
            int s = 0;
            if (p.IndexOf("Dry", System.StringComparison.OrdinalIgnoreCase) >= 0) s -= 5;
            if (p.IndexOf("Dead", System.StringComparison.OrdinalIgnoreCase) >= 0) s -= 5;
            return s;
        }

        static void EnsureDetailHolder()
        {
            CleanupDetailHolder();
            _detailHolder = new GameObject("DemDetailProtos") { hideFlags = HideFlags.HideAndDontSave };
            _detailHolder.SetActive(false);
        }

        // Load up to `max` detail-mesh prototypes from a vegetation folder, best-scored first.
        // Detail prototypes need a plain MeshFilter+MeshRenderer GameObject (the prefabs are
        // LODGroups), so BuildSingleMeshProto lifts LOD0 into the hidden holder.
        static GameObject[] LoadPrototypes(string folder, int max, System.Func<string, int> score)
        {
            var list = new System.Collections.Generic.List<GameObject>();
#if UNITY_EDITOR
            var paths = new System.Collections.Generic.List<string>();
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
                paths.Add(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
            if (score != null) paths.Sort((x, y) => score(y) - score(x));
            foreach (string path in paths)
            {
                if (list.Count >= max) break;
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var proto = prefab != null ? BuildSingleMeshProto(prefab) : null;
                if (proto != null) list.Add(proto);
            }
#endif
            return list.ToArray();
        }

        // Lift LOD0's mesh + (instancing-enabled copies of) its materials into a plain GameObject.
        static GameObject BuildSingleMeshProto(GameObject prefab)
        {
            MeshFilter mf = null; MeshRenderer mr = null;
            var lod = prefab.GetComponentInChildren<LODGroup>();
            if (lod != null)
            {
                var lods = lod.GetLODs();
                if (lods.Length > 0)
                    foreach (var rend in lods[0].renderers)
                        if (rend is MeshRenderer m) { mr = m; mf = m.GetComponent<MeshFilter>(); break; }
            }
            if (mf == null) { mf = prefab.GetComponentInChildren<MeshFilter>(); mr = mf != null ? mf.GetComponent<MeshRenderer>() : null; }
            if (mf == null || mf.sharedMesh == null || mr == null) return null;

            var go = new GameObject("proto_" + prefab.name);
            go.transform.SetParent(_detailHolder.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var src = mr.sharedMaterials;
            var mats = new Material[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                mats[i] = src[i] != null ? Object.Instantiate(src[i]) : null;
                if (mats[i] != null) mats[i].enableInstancing = true;   // detail instancing needs it
            }
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            return go;
        }

        static void CleanupDetailHolder()
        {
            if (_detailHolder == null) return;
            if (Application.isPlaying) Object.Destroy(_detailHolder);
            else Object.DestroyImmediate(_detailHolder);
            _detailHolder = null;
        }

        static void EnsureTextureLayers(string flat, string steep, string patch, string steepB, float tileSize)
        {
            if (_ground == null || _flatVar != flat) { _ground = CloneLayer(flat); _flatVar = flat; }
            if (_rock == null || _steepVar != steep) { _rock = CloneLayer(steep); _steepVar = steep; }
            if (patch != null && (_patch == null || _patchVar != patch)) { _patch = CloneLayer(patch); _patchVar = patch; }
            if (steepB != null && (_rock2 == null || _rock2Var != steepB)) { _rock2 = CloneLayer(steepB); _rock2Var = steepB; }
            ApplyTileSizes(tileSize);
        }

        // Explicit, DIFFERENT tile sizes per layer so no two repeats line up — and rock tiles
        // larger (its features are bigger; small tiles made the cliffs read as obvious repeats).
        static void ApplyTileSizes(float m)
        {
            m = Mathf.Max(0.5f, m);
            if (_ground != null) _ground.tileSize = new Vector2(m, m);
            if (_patch != null) _patch.tileSize = new Vector2(m * 1.6f, m * 1.6f);
            if (_rock != null) _rock.tileSize = new Vector2(m * 2.2f, m * 2.2f);
            if (_rock2 != null) _rock2.tileSize = new Vector2(m * 3.3f, m * 3.3f);
        }

        // Runtime COPY of a named TerrainLayer asset (so tile-size tweaks don't mutate the
        // shared asset). Keeps the pack's authored diffuse/normal/mask/smoothness.
        static TerrainLayer CloneLayer(string layerName)
        {
            TerrainLayer asset = LoadTerrainLayer(layerName);
            if (asset != null) return Object.Instantiate(asset);
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);   // fallback solid (no asset found)
            t.SetPixel(0, 0, new Color(0.32f, 0.5f, 0.24f, 0f)); t.Apply();
            return new TerrainLayer { diffuseTexture = t, tileSize = new Vector2(25f, 25f), smoothness = 0f, metallic = 0f };
        }

        static TerrainLayer LoadTerrainLayer(string layerName)
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(layerName)) return null;
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:TerrainLayer"))
            {
                string p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) == layerName)
                    return UnityEditor.AssetDatabase.LoadAssetAtPath<TerrainLayer>(p);
            }
#endif
            return null;
        }

        // folder: absolute or Assets-relative path to the *_tile_<row>_<col>.png set.
        // tileMeters: ground size of one tile (10000 for 10km). norm*: metre range the
        // 16-bit values map to. Returns the root GameObject (null on failure).
        public static GameObject Build(string folder, float tileMeters, float normFrom, float normTo)
        {
            string dir = ResolveDir(folder);
            if (dir == null) { Debug.LogError($"[DemTerrainWorld] folder not found: {folder}"); return null; }

            string[] files = Directory.GetFiles(dir, "*.png");
            if (files.Length == 0) { Debug.LogError($"[DemTerrainWorld] no PNGs in {dir}"); return null; }

            // Parse (row,col) + lat/lon from each filename; find grid extent + geo span.
            var tiles = new List<(int row, int col, string path)>();
            int maxRow = 0, maxCol = 0;
            double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
            bool haveGeo = false;
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (!TryParseRowCol(name, out int r, out int c)) continue;
                tiles.Add((r, c, f));
                if (r > maxRow) maxRow = r;
                if (c > maxCol) maxCol = c;
                if (TryParseLatLon(name, out double lat, out double lon))
                {
                    haveGeo = true;
                    minLat = System.Math.Min(minLat, lat); maxLat = System.Math.Max(maxLat, lat);
                    minLon = System.Math.Min(minLon, lon); maxLon = System.Math.Max(maxLon, lon);
                }
            }
            if (tiles.Count == 0) { Debug.LogError("[DemTerrainWorld] no files matched *_tile_<row>_<col>.png"); return null; }

            Clear();
            int rows = maxRow + 1, cols = maxCol + 1;

            // Derive the REAL per-tile ground size from the filename lat/lon span, so the
            // world renders at 1:1 scale for any export area (not a hard-coded 10km). Falls
            // back to the passed tileMeters if the coords didn't parse.
            float tileX = tileMeters, tileZ = tileMeters;
            if (haveGeo && rows > 1 && cols > 1 && maxLat > minLat && maxLon > minLon)
            {
                double centerLat = (maxLat + minLat) * 0.5;
                tileX = (float)((maxLon - minLon) / (cols - 1) * 111320.0 * System.Math.Cos(centerLat * System.Math.PI / 180.0));
                tileZ = (float)((maxLat - minLat) / (rows - 1) * 111320.0);
            }

            float range = Mathf.Max(1f, normTo - normFrom);
            var root = new GameObject(RootName);
            var grid = new UnityEngine.Terrain[rows, cols];
            _grid = grid; _albedo = new TerrainLayer[rows, cols];   // for the live albedo/green toggle

            foreach (var (row, col, path) in tiles)
            {
                float[,] heights = LoadHeights(path);
                if (heights == null) continue;

                var td = new TerrainData { heightmapResolution = Res };
                td.size = new Vector3(tileX, range, tileZ);
                td.SetHeights(0, 0, heights);

                GameObject go = UnityEngine.Terrain.CreateTerrainGameObject(td);
                go.name = $"Terrain_{row:00}_{col:00}";
                go.transform.SetParent(root.transform, false);
                // row 0 = north → highest Z (north = +Z); col → +X (east).
                go.transform.position = new Vector3(col * tileX, normFrom, (rows - 1 - row) * tileZ);
                var terrain = go.GetComponent<UnityEngine.Terrain>();
                terrain.basemapDistance = 8000f;   // keep splat/textures sharp out to ~8km (texture LOD)
                grid[row, col] = terrain;
            }

            // Stitch LOD/heights across seams (left=-X, top=+Z, right=+X, bottom=-Z).
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var t = grid[r, c];
                    if (t == null) continue;
                    // left=-X(col-1), top=+Z=north(row-1), right=+X(col+1), bottom=-Z=south(row+1)
                    t.SetNeighbors(
                        c > 0 ? grid[r, c - 1] : null,
                        r > 0 ? grid[r - 1, c] : null,
                        c < cols - 1 ? grid[r, c + 1] : null,
                        r < rows - 1 ? grid[r + 1, c] : null);
                }

            ApplyAlbedo(dir, grid, rows, cols, tileX, tileZ);
            WireCameraToDem();   // make the camera's clamp/speed-damping follow the DEM surface

            Debug.Log($"[DemTerrainWorld] built {tiles.Count} tiles ({rows}x{cols}), " +
                      $"tile {tileX:0}x{tileZ:0}m → world {tileX * cols / 1000f:0.#}x{tileZ * rows / 1000f:0.#}km, " +
                      $"height {normFrom:0.#}..{normTo:0.#}m.");
            return root;
        }

        // Load a heightmap PNG into a Res*Res normalized float[,] (heights[z,x] = pixel
        // fraction). Resamples (bilinear) if the source isn't Res*Res, so edge tiles fit.
        static float[,] LoadHeights(string path)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (System.Exception e) { Debug.LogWarning($"[DemTerrainWorld] read failed {path}: {e.Message}"); return null; }

            // True 16-bit decode (full precision); fall back to 8-bit LoadImage if the PNG
            // isn't 16-bit grayscale. gray[] is bottom-up, normalized 0..1 (GetPixels layout).
            if (!TryDecodeGray16(bytes, out float[] gray, out int w, out int h))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes)) { Object.DestroyImmediate(tex); Debug.LogWarning($"[DemTerrainWorld] decode failed {path}"); return null; }
                w = tex.width; h = tex.height;
                Color[] px = tex.GetPixels();
                Object.DestroyImmediate(tex);
                gray = new float[w * h];
                for (int i = 0; i < gray.Length; i++) gray[i] = px[i].r;
            }

            var heights = new float[Res, Res];
            for (int z = 0; z < Res; z++)
            {
                float v = (Res > 1) ? (float)z / (Res - 1) : 0f;   // 0..1 down the tile
                float sy = v * (h - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, h - 1);
                int y1 = Mathf.Min(y0 + 1, h - 1);
                float fy = sy - y0;
                for (int x = 0; x < Res; x++)
                {
                    float u = (Res > 1) ? (float)x / (Res - 1) : 0f;
                    float sx = u * (w - 1);
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, w - 1);
                    int x1 = Mathf.Min(x0 + 1, w - 1);
                    float fx = sx - x0;
                    float a = gray[y0 * w + x0], b = gray[y0 * w + x1];
                    float c = gray[y1 * w + x0], d = gray[y1 * w + x1];
                    heights[z, x] = Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
                }
            }
            return heights;
        }

        // Decode a non-interlaced 16-bit grayscale PNG to normalized 0..1 floats, BOTTOM-UP
        // (matching Texture2D.GetPixels) so it's a drop-in for the 8-bit path. Returns false
        // for any other PNG type (caller falls back to LoadImage).
        static bool TryDecodeGray16(byte[] b, out float[] gray, out int width, out int height)
        {
            gray = null; width = height = 0;
            if (b == null || b.Length < 8 || b[0] != 137 || b[1] != 80 || b[2] != 78 || b[3] != 71) return false;

            int w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0, pos = 8;
            var idat = new System.IO.MemoryStream();
            while (pos + 8 <= b.Length)
            {
                int len = (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
                string type = System.Text.Encoding.ASCII.GetString(b, pos + 4, 4);
                int d = pos + 8;
                if (len < 0 || d + len + 4 > b.Length) break;
                if (type == "IHDR")
                {
                    w = (b[d] << 24) | (b[d + 1] << 16) | (b[d + 2] << 8) | b[d + 3];
                    h = (b[d + 4] << 24) | (b[d + 5] << 16) | (b[d + 6] << 8) | b[d + 7];
                    bitDepth = b[d + 8]; colorType = b[d + 9]; interlace = b[d + 12];
                }
                else if (type == "IDAT") idat.Write(b, d, len);
                else if (type == "IEND") break;
                pos = d + len + 4;   // skip data + CRC
            }
            if (w <= 0 || h <= 0 || bitDepth != 16 || colorType != 0 || interlace != 0) return false;

            byte[] comp = idat.ToArray();
            if (comp.Length < 3) return false;
            byte[] raw;
            try
            {
                using var ms = new System.IO.MemoryStream(comp, 2, comp.Length - 2);   // skip 2-byte zlib header
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                using var outMs = new System.IO.MemoryStream(w * h * 2 + h);
                ds.CopyTo(outMs);
                raw = outMs.ToArray();
            }
            catch { return false; }

            const int bpp = 2;                 // 16-bit grayscale
            int stride = w * bpp;
            if (raw.Length < h * (stride + 1)) return false;

            var recon = new byte[h * stride];
            for (int y = 0; y < h; y++)
            {
                int filt = raw[y * (stride + 1)];
                int inRow = y * (stride + 1) + 1, recRow = y * stride, prevRow = (y - 1) * stride;
                for (int i = 0; i < stride; i++)
                {
                    int rawv = raw[inRow + i];
                    int a = i >= bpp ? recon[recRow + i - bpp] : 0;
                    int up = y > 0 ? recon[prevRow + i] : 0;
                    int c = (y > 0 && i >= bpp) ? recon[prevRow + i - bpp] : 0;
                    int val;
                    switch (filt)
                    {
                        case 0: val = rawv; break;
                        case 1: val = rawv + a; break;
                        case 2: val = rawv + up; break;
                        case 3: val = rawv + ((a + up) >> 1); break;
                        case 4: val = rawv + Paeth(a, up, c); break;
                        default: return false;
                    }
                    recon[recRow + i] = (byte)(val & 0xFF);
                }
            }

            gray = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int srcRow = y * stride, dstRow = (h - 1 - y) * w;   // flip to bottom-up
                for (int x = 0; x < w; x++)
                    gray[dstRow + x] = ((recon[srcRow + x * 2] << 8) | recon[srcRow + x * 2 + 1]) / 65535f;
            }
            width = w; height = h;
            return true;
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = System.Math.Abs(p - a), pb = System.Math.Abs(p - b), pc = System.Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        // Trailing "..._tile_<row>_<col>" -> row, col.
        static bool TryParseRowCol(string name, out int row, out int col)
        {
            row = col = 0;
            string[] t = name.Split('_');
            int ti = System.Array.LastIndexOf(t, "tile");
            if (ti < 0 || ti + 2 >= t.Length) return false;
            return int.TryParse(t[ti + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out row)
                && int.TryParse(t[ti + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out col);
        }

        // Leading "LAT_LATFRAC_LON_LONFRAC_..." (e.g. 47_929_-122_646_...) -> degrees.
        static bool TryParseLatLon(string name, out double lat, out double lon)
        {
            lat = lon = 0;
            string[] t = name.Split('_');
            if (t.Length < 4) return false;
            return double.TryParse($"{t[0]}.{t[1]}", NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
                && double.TryParse($"{t[2]}.{t[3]}", NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
        }

        // Accept a bare city name (under Heightmaps/Highres), an Assets-relative path,
        // or an absolute folder.
        static string ResolveDir(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            if (Directory.Exists(folder)) return folder;
            string underBase = Path.Combine(Application.dataPath, BaseRel, folder);
            if (Directory.Exists(underBase)) return underBase;
            string abs = Path.Combine(Application.dataPath, folder.StartsWith("Assets/") ? folder.Substring(7) : folder);
            return Directory.Exists(abs) ? abs : null;
        }

        // Drape the single full-mosaic albedo (one *albedo*.png) across the tiles by
        // cropping each tile's window into a full-coverage TerrainLayer. Uses the SAME
        // row/col→world orientation as the heights, so the colour aligns with the relief.
        static void ApplyAlbedo(string dir, UnityEngine.Terrain[,] grid, int rows, int cols, float tileX, float tileZ)
        {
            string[] al = Directory.GetFiles(dir, "*albedo*.png");
            if (al.Length == 0) return;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(al[0]); }
            catch (System.Exception e) { Debug.LogWarning($"[DemTerrainWorld] albedo read failed: {e.Message}"); return; }

            var big = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!big.LoadImage(bytes)) { Object.DestroyImmediate(big); Debug.LogWarning("[DemTerrainWorld] albedo decode failed"); return; }
            int aw = big.width, ah = big.height;
            int tw = aw / cols, th = ah / rows;     // ~1024 per tile
            if (tw < 2 || th < 2) { Object.DestroyImmediate(big); return; }

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var t = grid[r, c];
                    if (t == null) continue;
                    // image row 0 = north (top); GetPixels is bottom-up, so the tile's
                    // band sits at y = ah - (r+1)*th. Matches the heights' z=0=south.
                    int sx = Mathf.Clamp(c * tw, 0, aw - tw);
                    int sy = Mathf.Clamp(ah - (r + 1) * th, 0, ah - th);
                    Color[] block = big.GetPixels(sx, sy, tw, th);
                    // Terrain reads diffuse ALPHA as smoothness when there's no mask map;
                    // PNGs load opaque (a=1 = glass), so zero it for a matte surface.
                    for (int i = 0; i < block.Length; i++) block[i].a = 0f;
                    var tex = new Texture2D(tw, th, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
                    tex.SetPixels(block);
                    tex.Apply();
                    var layer = new TerrainLayer
                    {
                        diffuseTexture = tex,
                        tileSize = new Vector2(tileX, tileZ),  // cover the tile exactly once
                        tileOffset = Vector2.zero,
                        smoothness = 0f,
                        metallic = 0f
                    };
                    if (_albedo != null) _albedo[r, c] = layer;   // cache for the toggle
                    t.terrainData.terrainLayers = new[] { layer };
                }
            Object.DestroyImmediate(big);
            Debug.Log($"[DemTerrainWorld] albedo draped from {Path.GetFileName(al[0])} ({aw}x{ah}).");
        }
    }
}
