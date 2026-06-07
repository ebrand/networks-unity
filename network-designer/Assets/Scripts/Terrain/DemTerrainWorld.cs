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
        static TerrainLayer _ground, _rock;  // runtime copies of the flat + steep TerrainLayers
        static string _flatVar, _steepVar;   // which TerrainLayer assets are loaded

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

        public static void SetTextured(string flatVariant, string steepVariant,
                                       float slopeLow = 22f, float slopeHigh = 38f,
                                       float tileSizeMeters = 25f, int alphaRes = 256)
        {
            if (_grid == null) return;
            EnsureTextureLayers(flatVariant, steepVariant, tileSizeMeters);
            // Keep each layer's authored tileSize (the pack tunes it) — the Tex Size slider
            // overrides it live via SetTextureTiling if you want to change it.
            var layers = new[] { _ground, _rock };
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
                    var a = new float[res, res, 2];
                    for (int j = 0; j < res; j++)
                    {
                        float v = res > 1 ? (float)j / (res - 1) : 0f;
                        for (int i = 0; i < res; i++)
                        {
                            float u = res > 1 ? (float)i / (res - 1) : 0f;
                            float rock = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(slopeLow, slopeHigh, td.GetSteepness(u, v)));
                            a[j, i, 0] = 1f - rock;   // grass
                            a[j, i, 1] = rock;        // rock
                        }
                    }
                    td.SetAlphamaps(0, 0, a);
                }
            Debug.Log($"[DemTerrainWorld] slope textures applied (rock {slopeLow:0}-{slopeHigh:0}°).");
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
            meters = Mathf.Max(0.5f, meters);
            if (_ground != null) _ground.tileSize = new Vector2(meters, meters);
            if (_rock != null) _rock.tileSize = new Vector2(meters, meters);
            if (_grid == null || _ground == null) return;
            var layers = new[] { _ground, _rock };
            foreach (var t in _grid)
                if (t != null && t.terrainData != null) t.terrainData.terrainLayers = layers;
        }

        static void EnsureTextureLayers(string flat, string steep, float tileSize)
        {
            if (_ground == null || _flatVar != flat) { _ground = CloneLayer(flat); _flatVar = flat; }
            if (_rock == null || _steepVar != steep) { _rock = CloneLayer(steep); _steepVar = steep; }
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
