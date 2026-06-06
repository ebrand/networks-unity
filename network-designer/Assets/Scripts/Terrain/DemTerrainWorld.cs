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
using System.IO;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class DemTerrainWorld
    {
        public const string RootName = "DemTerrainWorld";
        public const string BaseRel = "Heightmaps/Highres";   // Assets/Heightmaps/Highres/<City>/
        const int Res = 1025;   // Unity heightmap resolution per tile (2^10 + 1)

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

        // --- per-city Norm range memory ---
        // The DEM export doesn't record the metres range anywhere, so it must be entered
        // once per city; we remember it here (tab-delimited so city names with commas/
        // spaces are safe) and auto-fill it next time that city is picked.
        static string NormsPath => Path.Combine(Application.dataPath, BaseRel, "dem_norms.txt");

        public static bool TryGetNorm(string city, out float from, out float to)
        {
            from = 0f; to = 0f;
            try
            {
                if (string.IsNullOrEmpty(city) || !File.Exists(NormsPath)) return false;
                foreach (string line in File.ReadAllLines(NormsPath))
                {
                    string[] p = line.Split('\t');
                    if (p.Length >= 3 && p[0] == city
                        && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out from)
                        && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out to))
                        return true;
                }
            }
            catch { }
            return false;
        }

        public static void SaveNorm(string city, float from, float to)
        {
            if (string.IsNullOrEmpty(city)) return;
            try
            {
                var lines = File.Exists(NormsPath) ? new List<string>(File.ReadAllLines(NormsPath)) : new List<string>();
                lines.RemoveAll(l => { var p = l.Split('\t'); return p.Length > 0 && p[0] == city; });
                lines.Add($"{city}\t{from.ToString(CultureInfo.InvariantCulture)}\t{to.ToString(CultureInfo.InvariantCulture)}");
                File.WriteAllLines(NormsPath, lines);
            }
            catch (System.Exception e) { Debug.LogWarning($"[DemTerrainWorld] norm save failed: {e.Message}"); }
        }

        public static void Clear()
        {
            var existing = GameObject.Find(RootName);
            if (existing != null)
            {
                if (Application.isPlaying) Object.Destroy(existing);
                else Object.DestroyImmediate(existing);
            }
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

            // Parse (row,col) from each filename; find grid extent.
            var tiles = new List<(int row, int col, string path)>();
            int maxRow = 0, maxCol = 0;
            foreach (string f in files)
            {
                if (!TryParseRowCol(Path.GetFileNameWithoutExtension(f), out int r, out int c)) continue;
                tiles.Add((r, c, f));
                if (r > maxRow) maxRow = r;
                if (c > maxCol) maxCol = c;
            }
            if (tiles.Count == 0) { Debug.LogError("[DemTerrainWorld] no files matched *_tile_<row>_<col>.png"); return null; }

            Clear();
            int rows = maxRow + 1, cols = maxCol + 1;
            float range = Mathf.Max(1f, normTo - normFrom);
            var root = new GameObject(RootName);
            var grid = new UnityEngine.Terrain[rows, cols];

            foreach (var (row, col, path) in tiles)
            {
                float[,] heights = LoadHeights(path);
                if (heights == null) continue;

                var td = new TerrainData { heightmapResolution = Res };
                td.size = new Vector3(tileMeters, range, tileMeters);
                td.SetHeights(0, 0, heights);

                GameObject go = UnityEngine.Terrain.CreateTerrainGameObject(td);
                go.name = $"Terrain_{row:00}_{col:00}";
                go.transform.SetParent(root.transform, false);
                // row 0 = north → highest Z (north = +Z); col → +X (east).
                go.transform.position = new Vector3(col * tileMeters, normFrom, (rows - 1 - row) * tileMeters);
                grid[row, col] = go.GetComponent<UnityEngine.Terrain>();
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

            ApplyAlbedo(dir, grid, rows, cols, tileMeters);

            Debug.Log($"[DemTerrainWorld] built {tiles.Count} tiles ({rows}x{cols}), " +
                      $"tile {tileMeters}m, height {normFrom:0.#}..{normTo:0.#}m.");
            return root;
        }

        // Load a heightmap PNG into a Res*Res normalized float[,] (heights[z,x] = pixel
        // fraction). Resamples (bilinear) if the source isn't Res*Res, so edge tiles fit.
        static float[,] LoadHeights(string path)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (System.Exception e) { Debug.LogWarning($"[DemTerrainWorld] read failed {path}: {e.Message}"); return null; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes)) { Object.DestroyImmediate(tex); Debug.LogWarning($"[DemTerrainWorld] decode failed {path}"); return null; }
            int w = tex.width, h = tex.height;
            Color[] px = tex.GetPixels();
            Object.DestroyImmediate(tex);

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
                    float a = px[y0 * w + x0].r, b = px[y0 * w + x1].r;
                    float c = px[y1 * w + x0].r, d = px[y1 * w + x1].r;
                    heights[z, x] = Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
                }
            }
            return heights;
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
        static void ApplyAlbedo(string dir, UnityEngine.Terrain[,] grid, int rows, int cols, float tileMeters)
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
                    var tex = new Texture2D(tw, th, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
                    tex.SetPixels(block);
                    tex.Apply();
                    var layer = new TerrainLayer
                    {
                        diffuseTexture = tex,
                        tileSize = new Vector2(tileMeters, tileMeters),  // cover the tile exactly once
                        tileOffset = Vector2.zero
                    };
                    t.terrainData.terrainLayers = new[] { layer };
                }
            Object.DestroyImmediate(big);
            Debug.Log($"[DemTerrainWorld] albedo draped from {Path.GetFileName(al[0])} ({aw}x{ah}).");
        }
    }
}
