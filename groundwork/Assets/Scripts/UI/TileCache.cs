// Persistent on-disk cache for basemap tiles + a US overview prefetch.
//
// Tiles are stored under Application.persistentDataPath/TileCache/<layer>/<z>/<x>/<y>.tile as the raw
// downloaded bytes (PNG/JPEG). Source is USGS "The National Map" topo, which is PUBLIC DOMAIN — so
// caching/offline use is allowed (Esri's and OpenStreetMap's tile terms forbid bulk/offline caching).
//
// PrefetchUS() walks the CONUS bounding box over a zoom range and downloads every tile not already on
// disk, so the country can be browsed offline; deeper zooms cache on demand as the user pans.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace NetworkDesigner.UI
{
    public class TileCache : MonoBehaviour
    {
        // USGS National Map topo (public domain). Tile path order is /{z}/{row}/{col} = /{z}/{Y}/{X}.
        public const string UsgsTopoUrl = "https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/{0}/{1}/{2}";
        public const string Layer = "USGSTopo";

        // CONUS bounding box (degrees) for the overview prefetch.
        public const double UsW = -125.0, UsE = -66.5, UsN = 49.5, UsS = 24.4;

        static string Root => Path.Combine(Application.persistentDataPath, "TileCache");
        static string PathFor(string layer, int z, int x, int y)
            => Path.Combine(Root, layer, z.ToString(), x.ToString(), y + ".tile");

        public static byte[] TryLoad(string layer, int z, int x, int y)
        {
            try { string p = PathFor(layer, z, x, y); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
            catch { return null; }
        }

        // Cheap existence check (no read) — for the prefetch skip so resuming doesn't re-read every cached tile.
        public static bool Exists(string layer, int z, int x, int y)
        { try { return File.Exists(PathFor(layer, z, x, y)); } catch { return false; } }

        public static void Save(string layer, int z, int x, int y, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            try
            {
                string p = PathFor(layer, z, x, y);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, bytes);
            }
            catch { }
        }

        public static long CacheSizeBytes(string layer = Layer)
        {
            try
            {
                var d = new DirectoryInfo(Path.Combine(Root, layer));
                if (!d.Exists) return 0;
                long sum = 0;
                foreach (var f in d.GetFiles("*", SearchOption.AllDirectories)) sum += f.Length;
                return sum;
            }
            catch { return 0; }
        }

        public static int EstimateTiles(int zMin, int zMax)
        {
            long total = 0;
            for (int z = zMin; z <= zMax; z++)
            {
                int x0 = (int)Math.Floor(Lon2TileX(UsW, z)), x1 = (int)Math.Floor(Lon2TileX(UsE, z));
                int y0 = (int)Math.Floor(Lat2TileY(UsN, z)), y1 = (int)Math.Floor(Lat2TileY(UsS, z));
                total += (long)(x1 - x0 + 1) * (y1 - y0 + 1);
            }
            return (int)total;
        }

        // ---- overview prefetch (runs on a self-spawned, persistent runner) ----
        static TileCache _runner; static bool _cancel;
        static TileCache Runner
        {
            get
            {
                if (_runner == null)
                {
                    var go = new GameObject("TileCache") { hideFlags = HideFlags.DontSave };
                    _runner = go.AddComponent<TileCache>();
                }
                return _runner;
            }
        }

        public static void CancelPrefetch() => _cancel = true;

        public static void PrefetchUS(int zMin, int zMax, Action<float, string> onProgress, Action<bool, string> onDone)
            => Runner.StartCoroutine(Runner.PrefetchRun(zMin, zMax, onProgress, onDone));

        IEnumerator PrefetchRun(int zMin, int zMax, Action<float, string> onProgress, Action<bool, string> onDone)
        {
            _cancel = false;
            const int Concurrency = 10;          // tiles downloaded in parallel
            // Build the full (z, X, Y) job list.
            var jobs = new List<(int z, int x, int y)>();
            for (int z = zMin; z <= zMax; z++)
            {
                int worldTiles = 1 << z;
                int x0 = (int)Math.Floor(Lon2TileX(UsW, z)), x1 = (int)Math.Floor(Lon2TileX(UsE, z));
                int y0 = (int)Math.Floor(Lat2TileY(UsN, z)), y1 = (int)Math.Floor(Lat2TileY(UsS, z));
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        jobs.Add((z, ((x % worldTiles) + worldTiles) % worldTiles, Mathf.Clamp(y, 0, worldTiles - 1)));
            }
            int total = Mathf.Max(1, jobs.Count), done = 0, fetched = 0, ji = 0;
            var inflight = new List<(UnityWebRequest req, int z, int x, int y)>();
            while ((ji < jobs.Count || inflight.Count > 0) && !_cancel)
            {
                while (inflight.Count < Concurrency && ji < jobs.Count)
                {
                    var (z, X, Y) = jobs[ji++]; done++;
                    if (Exists(Layer, z, X, Y)) continue;
                    var req = UnityWebRequest.Get(string.Format(UsgsTopoUrl, z, Y, X));
                    req.SendWebRequest();
                    inflight.Add((req, z, X, Y));
                }
                yield return null;
                for (int i = inflight.Count - 1; i >= 0; i--)
                {
                    if (!inflight[i].req.isDone) continue;
                    if (inflight[i].req.result == UnityWebRequest.Result.Success)
                    { Save(Layer, inflight[i].z, inflight[i].x, inflight[i].y, inflight[i].req.downloadHandler.data); fetched++; }
                    inflight[i].req.Dispose();
                    inflight.RemoveAt(i);
                }
                if ((done & 31) == 0) onProgress?.Invoke(done / (float)total, $"{done:n0}/{total:n0}  ({fetched:n0} new)");
            }
            for (int i = 0; i < inflight.Count; i++) inflight[i].req.Dispose();
            onDone?.Invoke(!_cancel,
                _cancel ? $"stopped at {done:n0}/{total:n0} ({fetched:n0} new)"
                        : $"done — {fetched:n0} new tiles · {CacheSizeBytes() / 1_000_000} MB cached");
        }

        static double Lon2TileX(double lon, int z) => (lon + 180.0) / 360.0 * (1 << z);
        static double Lat2TileY(double lat, int z)
        { double r = lat * Math.PI / 180.0; return (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * (1 << z); }
    }
}
