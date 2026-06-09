// Feeds REAL DEM tiles into ChunkWorld's height function at TRUE 1:1 scale. A "..._tile_<row>_<col>.png"
// set is laid out as a continuous height mosaic indexed by WORLD position — the per-tile ground size is
// MEASURED from the filenames' lat/lon span (so it works for any tile size, not just 1 km). Orientation
// matches DemTerrainWorld: col = +X (east), row 0 = +Z (north). World extent = [0..Cols·TileMetersX] ×
// [0..Rows·TileMetersZ]. World Y = NormMin + gray(0..1)·(NormMax-NormMin).
//
// IMPORTANT: NormMin/NormMax must match the range the PNGs were ENCODED with (the generator's Norm
// From/To). A mismatch rescales (corrupts) the heights; it does NOT recover resolution.
//
// Tiles are decoded lazily and held in a small LRU cache (a tile serves many 1 km chunks), so resident
// memory tracks the VIEW, not the whole world — drop as many block-folders on disk as you like.

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class DemChunkSource
    {
        public static bool Active { get; private set; }
        public static float NormMin = -500f;   // MUST match the PNG encode range
        public static float NormMax = 9000f;
        public static int Rows { get; private set; }
        public static int Cols { get; private set; }
        public static float TileMetersX { get; private set; }   // measured per-tile ground size
        public static float TileMetersZ { get; private set; }
        public static float OriginX { get; private set; }       // SW corner in world XZ (mosaic centred on origin)
        public static float OriginZ { get; private set; }
        public static string Folder { get; private set; }
        public static int TileCacheCap = 64;   // max decoded tiles kept (LRU); ~4 MB each at 1025²
        public static int DecodeCount { get; private set; }   // total PNG decodes (lets the minimap bake pace itself)

        static string[,] _path;          // [row, col] -> file
        static readonly Dictionary<int, float[]> _gray = new Dictionary<int, float[]>();   // row*Cols+col -> gray
        static readonly Dictionary<int, Vector2Int> _dim = new Dictionary<int, Vector2Int>();
        static readonly Dictionary<int, long> _use = new Dictionary<int, long>();
        static long _useTick;

        // Scan a *_tile_<row>_<col>.png folder, measure the grid + per-tile ground size from the
        // filenames' lat/lon. fallbackTileMeters is used only if the coords don't parse.
        public static bool Configure(string folder, float normMin, float normMax, float fallbackTileMeters = 1000f)
        {
            Clear();
            string dir = ResolveDir(folder);
            if (dir == null) { Debug.LogError($"[DemChunkSource] folder not found: {folder}"); return false; }
            string[] files = Directory.GetFiles(dir, "*.png");

            int maxRow = 0, maxCol = 0;
            double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
            bool haveGeo = false;
            var found = new List<(int row, int col, string path)>();
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (!DemTerrainWorld.TryParseRowCol(name, out int r, out int c)) continue;
                found.Add((r, c, f));
                if (r > maxRow) maxRow = r;
                if (c > maxCol) maxCol = c;
                if (DemTerrainWorld.TryParseLatLon(name, out double lat, out double lon))
                {
                    haveGeo = true;
                    if (lat < minLat) minLat = lat; if (lat > maxLat) maxLat = lat;
                    if (lon < minLon) minLon = lon; if (lon > maxLon) maxLon = lon;
                }
            }
            if (found.Count == 0) { Debug.LogError($"[DemChunkSource] no *_tile_<row>_<col>.png in {dir}"); return false; }

            Rows = maxRow + 1; Cols = maxCol + 1;
            _path = new string[Rows, Cols];
            foreach (var (row, col, path) in found) _path[row, col] = path;

            // Per-tile ground size from the lat/lon span (tile PITCH = span / intervals). Matches
            // DemTerrainWorld's derivation. Fall back to a flat tile size if geo is unavailable.
            if (haveGeo && Rows > 1 && Cols > 1 && maxLat > minLat && maxLon > minLon)
            {
                double centerLat = (maxLat + minLat) * 0.5;
                TileMetersX = (float)((maxLon - minLon) / (Cols - 1) * 111320.0 * System.Math.Cos(centerLat * System.Math.PI / 180.0));
                TileMetersZ = (float)((maxLat - minLat) / (Rows - 1) * 111320.0);
            }
            else { TileMetersX = TileMetersZ = Mathf.Max(1f, fallbackTileMeters); }

            // Centre the mosaic on the origin so the most-used middle is the float-precise spot
            // (raycasts/sculpt degrade tens of km out); the far corners sit at ~half the world span.
            OriginX = -Cols * TileMetersX * 0.5f;
            OriginZ = -Rows * TileMetersZ * 0.5f;

            NormMin = normMin; NormMax = normMax; Folder = folder; Active = true;
            Debug.Log($"[DemChunkSource] {found.Count} tiles ({Rows}×{Cols}) → world " +
                      $"{Cols * TileMetersX / 1000f:0.#}×{Rows * TileMetersZ / 1000f:0.#} km, " +
                      $"tile {TileMetersX:0}×{TileMetersZ:0} m, range {NormMin:0}..{NormMax:0} m.");
            return true;
        }

        public static void Clear()
        {
            _path = null; _gray.Clear(); _dim.Clear(); _use.Clear();
            Rows = Cols = 0; TileMetersX = TileMetersZ = 0f; OriginX = OriginZ = 0f; Folder = null; Active = false; _useTick = 0; DecodeCount = 0;
        }

        public static float WorldWidthX => Cols * TileMetersX;
        public static float WorldLengthZ => Rows * TileMetersZ;
        public static Vector3 CenterWorld => new Vector3(OriginX + WorldWidthX * 0.5f, 0f, OriginZ + WorldLengthZ * 0.5f);

        // Does this 1 km chunk's footprint overlap the mosaic? (Chunks fully outside stay procedural.)
        public static bool CoversChunk(Vector2Int c)
        {
            if (!Active || TileMetersX <= 0f || TileMetersZ <= 0f) return false;
            float x0 = c.x * ChunkWorld.ChunkSize, z0 = c.y * ChunkWorld.ChunkSize;
            float x1 = x0 + ChunkWorld.ChunkSize, z1 = z0 + ChunkWorld.ChunkSize;
            return x1 > OriginX && z1 > OriginZ && x0 < OriginX + WorldWidthX && z0 < OriginZ + WorldLengthZ;
        }

        // World Y at an absolute world (wx,wz). Picks the covering tile + bilinear pixel. Outside the
        // mosaic it clamps to the edge tile (a border smear, not a cliff).
        public static float SampleWorldYAt(float wx, float wz)
        {
            if (!Active || TileMetersX <= 0f || TileMetersZ <= 0f) return NormMin;
            float fx = (wx - OriginX) / TileMetersX;
            int col = Mathf.Clamp(Mathf.FloorToInt(fx), 0, Cols - 1);
            float lu = Mathf.Clamp01(fx - col);
            float fz = (wz - OriginZ) / TileMetersZ;
            int bottom = Mathf.Clamp(Mathf.FloorToInt(fz), 0, Rows - 1);   // 0 = southernmost tile
            int row = (Rows - 1) - bottom;                                 // row 0 = north (+Z)
            float lv = Mathf.Clamp01(fz - bottom);

            float[] g = TileFor(row, col, out int w, out int h);
            if (g == null || w < 1 || h < 1) return NormMin;
            float sx = lu * (w - 1), sz = lv * (h - 1);   // gray is bottom-up: row 0 = south, col 0 = west
            int x0 = Mathf.Clamp((int)sx, 0, w - 1), z0 = Mathf.Clamp((int)sz, 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), z1 = Mathf.Min(z0 + 1, h - 1);
            float fxp = sx - x0, fzp = sz - z0;
            float a = g[z0 * w + x0], b = g[z0 * w + x1];
            float c = g[z1 * w + x0], d = g[z1 * w + x1];
            float norm = Mathf.Lerp(Mathf.Lerp(a, b, fxp), Mathf.Lerp(c, d, fxp), fzp);
            return NormMin + norm * (NormMax - NormMin);
        }

        static float[] TileFor(int row, int col, out int w, out int h)
        {
            w = h = 0;
            if (_path == null || row < 0 || row >= Rows || col < 0 || col >= Cols || _path[row, col] == null) return null;
            int key = row * Cols + col;
            if (_gray.TryGetValue(key, out var g)) { var d = _dim[key]; w = d.x; h = d.y; _use[key] = ++_useTick; return g; }
            g = DemTerrainWorld.DecodeTileGray(_path[row, col], out int gw, out int gh);
            if (g == null) return null;
            DecodeCount++;
            _gray[key] = g; _dim[key] = new Vector2Int(gw, gh); _use[key] = ++_useTick;
            w = gw; h = gh;
            if (_gray.Count > Mathf.Max(4, TileCacheCap)) EvictLru();
            return g;
        }

        static void EvictLru()
        {
            while (_gray.Count > Mathf.Max(4, TileCacheCap))
            {
                long oldest = long.MaxValue; int victim = -1;
                foreach (var kv in _use) if (kv.Value < oldest) { oldest = kv.Value; victim = kv.Key; }
                if (victim < 0) break;
                _gray.Remove(victim); _dim.Remove(victim); _use.Remove(victim);
            }
        }

        static string ResolveDir(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            if (Directory.Exists(folder)) return folder;
            string underBase = Path.Combine(Application.dataPath, "Heightmaps/Highres", folder);
            if (Directory.Exists(underBase)) return underBase;
            string abs = Path.Combine(Application.dataPath, folder.StartsWith("Assets/") ? folder.Substring(7) : folder);
            return Directory.Exists(abs) ? abs : null;
        }
    }
}
