// In-app DEM downloader: fetches AWS Terrarium elevation tiles for an area and writes them as the
// DemChunkSource tile mosaic (`lat_lon_zoom_W_H_16bit_tile_ROW_COL.png`) + a game.json manifest, then
// the caller loads the world. PIXEL-EXACT: output tiles are aligned to whole Terrarium tiles (no
// resampling), so every output pixel is a real elevation sample at an exact slippy-tile lat/lon —
// the area extent is computed precisely (not an approximate resampled rectangle).
//
// Each 1025² output tile = 4×4 Terrarium tiles (1024 px) + 1 shared edge px, so the loader's per-tile
// metres (derived from the tile lat/lon span) are exact and tiles abut seamlessly. North-up throughout.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace NetworkDesigner.Terrain
{
    public class DemDownloader : MonoBehaviour
    {
        // Clamp elevations below this (m) UP to it. Terrarium includes ocean bathymetry (deep negatives);
        // for a land sim that's noise — clamping to sea level flattens water, removes coastal pits, and
        // keeps the 16-bit encode range tight on the land. Set lower to keep some bathymetry.
        public static float SeaFloor = 0f;

        const string TileUrl = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
        const int OutTile = 1025;            // output tile size (px)
        const int TerrariumPx = 256;         // Terrarium tile size
        const int TerrPerOut = 4;            // Terrarium tiles per output tile (4*256 = 1024 unique px)

        static DemDownloader _runner;
        static DemDownloader Runner
        {
            get
            {
                if (_runner == null)
                {
                    var go = new GameObject("DemDownloader") { hideFlags = HideFlags.DontSave };
                    _runner = go.AddComponent<DemDownloader>();
                }
                return _runner;
            }
        }

        // Exact ground extent + tile count for a request, WITHOUT downloading — for an accurate UI readout.
        public static void Describe(double centerLat, double centerLon, int zoom, int tilesPerSide,
                                    out double widthKm, out double heightKm, out double metresPerPx,
                                    out double north, out double south, out double west, out double east)
        {
            int span = tilesPerSide * TerrPerOut;                  // Terrarium tiles across the mosaic
            int bx = (int)Math.Floor(Lon2TileX(centerLon, zoom)) - span / 2;
            int by = (int)Math.Floor(Lat2TileY(centerLat, zoom)) - span / 2;
            west = Tile2Lon(bx, zoom); east = Tile2Lon(bx + span, zoom);
            north = Tile2Lat(by, zoom); south = Tile2Lat(by + span, zoom);
            double midLat = (north + south) * 0.5;
            widthKm = (east - west) * 111.320 * Math.Cos(midLat * Math.PI / 180.0);
            heightKm = (north - south) * 111.320;
            metresPerPx = widthKm * 1000.0 / (tilesPerSide * (OutTile - 1));
        }

        public static void Start(string name, double centerLat, double centerLon, int zoom, int tilesPerSide,
                                 Action<float, string> onProgress, Action<bool, string> onDone)
            => Runner.StartCoroutine(Runner.Run(name, centerLat, centerLon, zoom, tilesPerSide, onProgress, onDone));

        IEnumerator Run(string name, double centerLat, double centerLon, int zoom, int tilesPerSide,
                        Action<float, string> onProgress, Action<bool, string> onDone)
        {
            tilesPerSide = Mathf.Clamp(tilesPerSide, 1, 8);
            zoom = Mathf.Clamp(zoom, 1, 15);   // 15 = Terrarium max (~2.5 m/px at high latitude, ~5 m at equator)
            int worldTiles = 1 << zoom;
            int span = tilesPerSide * TerrPerOut;
            int bx = (int)Math.Floor(Lon2TileX(centerLon, zoom)) - span / 2;
            int by = (int)Math.Floor(Lat2TileY(centerLat, zoom)) - span / 2;
            int fetchN = span + 1;            // +1 row/col for the shared edge

            // ── Fetch all Terrarium tiles → north-up elevation arrays (keyed by local tx,ty). ──
            var tiles = new Dictionary<long, float[]>();
            int total = fetchN * fetchN, done = 0;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int ty = 0; ty < fetchN; ty++)
                for (int tx = 0; tx < fetchN; tx++)
                {
                    int X = Mod(bx + tx, worldTiles);
                    int Y = Mathf.Clamp(by + ty, 0, worldTiles - 1);
                    string url = string.Format(TileUrl, zoom, X, Y);
                    using (var req = UnityWebRequestTexture.GetTexture(url, nonReadable: false))
                    {
                        yield return req.SendWebRequest();
                        if (req.result != UnityWebRequest.Result.Success)
                        { onDone?.Invoke(false, $"tile {X}/{Y} failed: {req.error}"); yield break; }
                        var tex = DownloadHandlerTexture.GetContent(req);
                        float[] e = DecodeTerrarium(tex);
                        Destroy(tex);
                        tiles[Key(tx, ty)] = e;
                        for (int i = 0; i < e.Length; i++) { if (e[i] < lo) lo = e[i]; if (e[i] > hi) hi = e[i]; }
                    }
                    if ((++done & 3) == 0) { onProgress?.Invoke(0.85f * done / total, $"fetching {done}/{total} tiles"); yield return null; }
                }
            if (lo > hi) { onDone?.Invoke(false, "no elevation data"); yield break; }

            // Fixed encode range (rounded outward), written to the manifest so decode is EXACT.
            float normMin = Mathf.Floor(lo / 10f) * 10f - 10f;
            float normMax = Mathf.Ceil(hi / 10f) * 10f + 10f;
            float range = Mathf.Max(1f, normMax - normMin);

            // ── Assemble + write each output tile (1025², 16-bit gray). ──
            string dir = Path.Combine(Application.dataPath, "Heightmaps/Highres", name);
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { onDone?.Invoke(false, $"mkdir failed: {ex.Message}"); yield break; }

            var gray = new ushort[OutTile * OutTile];
            int outTotal = tilesPerSide * tilesPerSide, outDone = 0;
            for (int R = 0; R < tilesPerSide; R++)
                for (int C = 0; C < tilesPerSide; C++)
                {
                    for (int py = 0; py < OutTile; py++)
                        for (int px = 0; px < OutTile; px++)
                        {
                            int gx = C * (OutTile - 1) + px;     // global mosaic px (north-up)
                            int gy = R * (OutTile - 1) + py;
                            float[] arr = tiles.TryGetValue(Key(gx / TerrariumPx, gy / TerrariumPx), out var a) ? a : null;
                            float e = arr != null ? arr[(gy % TerrariumPx) * TerrariumPx + (gx % TerrariumPx)] : normMin;
                            gray[py * OutTile + px] = (ushort)Mathf.RoundToInt(Mathf.Clamp01((e - normMin) / range) * 65535f);
                        }
                    // Filename lat/lon = this tile's NW corner (exact slippy position).
                    double tlon = Tile2Lon(bx + (C * (OutTile - 1)) / (double)TerrariumPx, zoom);
                    double tlat = Tile2Lat(by + (R * (OutTile - 1)) / (double)TerrariumPx, zoom);
                    string fn = $"{FmtCoord(tlat)}_{FmtCoord(tlon)}_{zoom}_{OutTile}_{OutTile}_16bit_tile_{R:00}_{C:00}.png";
                    try { File.WriteAllBytes(Path.Combine(dir, fn), DemPng16.Encode(gray, OutTile, OutTile)); }
                    catch (Exception ex) { onDone?.Invoke(false, $"write failed: {ex.Message}"); yield break; }
                    onProgress?.Invoke(0.85f + 0.15f * (++outDone / (float)outTotal), $"writing tile {R},{C}");
                    yield return null;
                }

            // Manifest → the world shows up in the launcher and loads with the exact decode range.
            if (!GameManager.Create(name, name, normMin, normMax))
            { onDone?.Invoke(false, "manifest (game.json) create failed — name in use?"); yield break; }

            onDone?.Invoke(true, $"{tilesPerSide}×{tilesPerSide} tiles, range {normMin:0}..{normMax:0} m");
        }

        // Terrarium RGB → metres, flipped to NORTH-UP (GetPixels32 is bottom-up).
        static float[] DecodeTerrarium(Texture2D tex)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            var e = new float[w * h];
            for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                {
                    Color32 c = px[(h - 1 - ty) * w + tx];     // texture row 0 = south → flip so ty 0 = north
                    float m = c.r * 256f + c.g + c.b / 256f - 32768f;
                    e[ty * w + tx] = Mathf.Max(SeaFloor, m);   // drop ocean bathymetry → flat sea level
                }
            return e;
        }

        static long Key(int tx, int ty) => ((long)tx << 32) ^ (uint)ty;
        static int Mod(int a, int m) => ((a % m) + m) % m;
        static string FmtCoord(double v) => v.ToString("F6", CultureInfo.InvariantCulture).Replace('.', '_');

        static double Lon2TileX(double lon, int z) => (lon + 180.0) / 360.0 * (1 << z);
        static double Lat2TileY(double lat, int z)
        {
            double r = lat * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * (1 << z);
        }
        static double Tile2Lon(double x, int z) => x / (1 << z) * 360.0 - 180.0;
        static double Tile2Lat(double y, int z)
        {
            double n = Math.PI - 2.0 * Math.PI * y / (1 << z);
            return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
        }
    }
}
