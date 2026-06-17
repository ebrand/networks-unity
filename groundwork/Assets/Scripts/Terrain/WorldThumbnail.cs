// Builds a slippy-map thumbnail for a world: a USGS-topo overview covering the world's downloaded
// extent (+ a buffer), with each downloaded block outlined in blue. Saved as <world>/thumbnail.png and
// regenerated after each download, so the start palette shows where the world's map data lives.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using NetworkDesigner.UI;   // TileCache

namespace NetworkDesigner.Terrain
{
    public class WorldThumbnail : MonoBehaviour
    {
        const int TileSize = 256;
        const int MaxTilesAcross = 5;
        const double WorldMerc = 40075016.685578488;   // 2·π·6378137 (Web-Mercator world width)
        // Global basemap (Esri World Imagery) — USGS topo is US-only, so non-US (Terrarium) worlds would
        // render a blank thumbnail. Fetched per generation, not cached (Esri terms).
        const string EsriImageryUrl = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{0}/{1}/{2}";

        static string FilePath(string world) => Path.Combine(WorldManager.WorldDir(world), "thumbnail.png");

        public static Texture2D Load(string world)
        {
            try { var p = FilePath(world); if (!File.Exists(p)) return null; var t = new Texture2D(2, 2); return t.LoadImage(File.ReadAllBytes(p)) ? t : null; }
            catch { return null; }
        }

        static WorldThumbnail _runner;
        static WorldThumbnail Runner
        {
            get { if (_runner == null) { var go = new GameObject("WorldThumbnail") { hideFlags = HideFlags.DontSave }; _runner = go.AddComponent<WorldThumbnail>(); } return _runner; }
        }

        public static void Generate(string world, Action onDone = null) => Runner.StartCoroutine(Runner.Run(world, onDone));

        IEnumerator Run(string world, Action onDone)
        {
            var wi = WorldManager.Read(world);
            var names = WorldManager.ListMapSets(world);
            if (wi == null || !wi.Anchored || names.Count == 0) { onDone?.Invoke(); yield break; }

            // Downloaded extent (mercator) + each map set's rectangle to outline (areas can differ in size).
            double tm = wi.TileMercM;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            var rects = new List<(double x0, double x1, double yS, double yN)>();
            foreach (var s in names)
            {
                var mi = WorldManager.ReadMapSet(world, s);
                if (mi == null) continue;
                int mw = mi.W > 0 ? mi.W : mi.N, mh = mi.H > 0 ? mi.H : mi.N;
                if (mw <= 0 || mh <= 0) continue;
                double x0 = wi.OriginMercX + mi.GC * tm, x1 = x0 + mw * tm;
                double yN = wi.OriginMercY - mi.GR * tm, yS = yN - mh * tm;
                rects.Add((x0, x1, yS, yN));
                if (x0 < minX) minX = x0; if (x1 > maxX) maxX = x1; if (yS < minY) minY = yS; if (yN > maxY) maxY = yN;
            }
            if (rects.Count == 0) { onDone?.Invoke(); yield break; }
            double buf = tm;   // ~1 km buffer
            minX -= buf; maxX += buf; minY -= buf; maxY += buf;

            // Pick an overview zoom so the extent spans ~MaxTilesAcross tiles.
            double span = Math.Max(maxX - minX, maxY - minY);
            int z = Mathf.Clamp((int)Math.Floor(Math.Log(WorldMerc * MaxTilesAcross / Math.Max(1.0, span), 2)), 3, 16);
            double n = 1 << z;
            int tx0 = (int)Math.Floor((minX + WorldMerc / 2) / WorldMerc * n);
            int tx1 = (int)Math.Floor((maxX + WorldMerc / 2) / WorldMerc * n);
            int ty0 = (int)Math.Floor((WorldMerc / 2 - maxY) / WorldMerc * n);   // north (maxY) → smaller tile y
            int ty1 = (int)Math.Floor((WorldMerc / 2 - minY) / WorldMerc * n);
            int across = Mathf.Clamp(tx1 - tx0 + 1, 1, 8), down = Mathf.Clamp(ty1 - ty0 + 1, 1, 8);
            tx1 = tx0 + across - 1; ty1 = ty0 + down - 1;

            int w = across * TileSize, h = down * TileSize;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            int worldTiles = 1 << z;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    int X = ((tx % worldTiles) + worldTiles) % worldTiles, Y = Mathf.Clamp(ty, 0, worldTiles - 1);
                    Color32[] px = null;
                    byte[] bytes = null;
                    using (var req = UnityWebRequest.Get(string.Format(EsriImageryUrl, z, Y, X)))
                    {
                        yield return req.SendWebRequest();
                        if (req.result == UnityWebRequest.Result.Success) bytes = req.downloadHandler.data;
                    }
                    if (bytes != null) { var t = new Texture2D(2, 2); if (t.LoadImage(bytes) && t.width == TileSize && t.height == TileSize) px = t.GetPixels32(); UnityEngine.Object.Destroy(t); }
                    if (px != null) tex.SetPixels32((tx - tx0) * TileSize, (down - 1 - (ty - ty0)) * TileSize, TileSize, TileSize, px);   // north tile at top
                }

            var blue = new Color(0.16f, 0.35f, 0.95f);
            foreach (var (rx0, rx1, ryS, ryN) in rects)
            {
                int x0 = (int)Math.Round(((rx0 + WorldMerc / 2) / WorldMerc * n - tx0) * TileSize);
                int x1 = (int)Math.Round(((rx1 + WorldMerc / 2) / WorldMerc * n - tx0) * TileSize);
                int yTop = h - (int)Math.Round(((WorldMerc / 2 - ryN) / WorldMerc * n - ty0) * TileSize);   // texture y is bottom-up
                int yBot = h - (int)Math.Round(((WorldMerc / 2 - ryS) / WorldMerc * n - ty0) * TileSize);
                DrawRect(tex, x0, yBot, x1, yTop, blue, w, h);
            }
            tex.Apply();
            try { File.WriteAllBytes(FilePath(world), tex.EncodeToPNG()); } catch { }
            UnityEngine.Object.Destroy(tex);
            onDone?.Invoke();
        }

        static void DrawRect(Texture2D tex, int x0, int y0, int x1, int y1, Color c, int w, int h)
        {
            if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
            if (y1 < y0) { int t = y0; y0 = y1; y1 = t; }
            for (int b = 0; b < 2; b++)   // 2 px border
            {
                for (int x = x0; x <= x1; x++) { SetPx(tex, x, y0 + b, c, w, h); SetPx(tex, x, y1 - b, c, w, h); }
                for (int y = y0; y <= y1; y++) { SetPx(tex, x0 + b, y, c, w, h); SetPx(tex, x1 - b, y, c, w, h); }
            }
        }

        static void SetPx(Texture2D tex, int x, int y, Color c, int w, int h) { if (x >= 0 && x < w && y >= 0 && y < h) tex.SetPixel(x, y, c); }
    }
}
