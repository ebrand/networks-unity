// USGS 3DEP 1 m elevation downloader (Phase 2).
//
// The 3DEPElevation ImageServer exportImage endpoint 500s above ~2048 px per request, so we fetch the
// area as a grid of 1 km tiles (1000 px = 1 m/px), in parallel with per-tile retries (the gov server
// throws transient 502s under load). Tiles are requested in WEB MERCATOR (EPSG:3857) so a square-ground
// tile is a square bbox — a lat/lon bbox gets snapped to a square-degree aspect and the tiles misalign
// (seams + edge smear). Each tile is a float32 GeoTIFF; we decode it, SPILL the floats to a temp file
// (RAM stays bounded → areas up to 32 km), and track the global min/max. A second pass reads the spills,
// encodes 16-bit PNG tiles (NW-corner lat/lon + GLOBAL row/col in the filename), and cleans up. US-only.
//
// Two entry points share RunGrid: Start() lays a standalone game, StartInWorld() lays a map set into a
// world's shared mercator lattice (so adjacent downloads tile seamlessly — see WorldManager).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace NetworkDesigner.Terrain
{
    public class Dem3DEP : MonoBehaviour
    {
        const string ExportUrl = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage";
        const int TilePx = 1000;           // 1 km tile at 1 m/px (exportImage 500s above ~2048 px per request)
        const int MaxTilesPerSide = 32;    // ≤32 km at 1 m; tile floats spill to temp disk so RAM stays bounded
        const int FetchConcurrency = 4;    // tiles in parallel (gentle on the 3DEP server)
        const int MaxRetries = 8;          // transient 502/504/timeout retries per tile (with backoff)

        static Dem3DEP _runner;
        static Dem3DEP Runner
        {
            get
            {
                if (_runner == null)
                {
                    var go = new GameObject("Dem3DEP") { hideFlags = HideFlags.DontSave };
                    _runner = go.AddComponent<Dem3DEP>();
                }
                return _runner;
            }
        }

        public static void Estimate(double wKm, double hKm, out double sizeMB, out double seconds)
        {
            int W = Mathf.Clamp((int)Math.Round(wKm), 1, MaxTilesPerSide), H = Mathf.Clamp((int)Math.Round(hKm), 1, MaxTilesPerSide);
            sizeMB = (double)W * H * TilePx * TilePx * 2.0 / 1_000_000.0;   // 16-bit output on disk
            seconds = Math.Max(5.0, W * H * 0.8);                          // ~0.8 s/tile effective (parallel)
        }

        // ── standalone game ──
        public static void Start(string name, double centerLat, double centerLon, double areaKm,
                                 Action<float, string> onProgress, Action<bool, string> onDone)
        {
            int N = Mathf.Clamp((int)Math.Round(areaKm), 2, MaxTilesPerSide);
            double tileMerc = 1000.0 / Math.Cos(centerLat * Math.PI / 180.0);
            double mx = WorldManager.Lon2MercX(centerLon), my = WorldManager.Lat2MercY(centerLat);
            var gs = new GridSpec
            {
                outDir = Path.Combine(Application.dataPath, "Heightmaps/Highres", name),
                nW = N, nH = N, tileMerc = tileMerc, nwX = mx - N * tileMerc / 2.0, nwY = my + N * tileMerc / 2.0, grBase = 0, gcBase = 0
            };
            Runner.StartCoroutine(Runner.RunGrid(gs,
                (mn, mxr) => GameManager.Create(name, name, mn, mxr) ? null : "manifest create failed — name in use?",
                onProgress, onDone));
        }

        // ── a (any W×H sized) map set into a world's 1 km lattice, auto-named by its NW tile ──
        public static void StartInWorld(string world, double centerLat, double centerLon, double areaKmW, double areaKmH,
                                        Action<float, string> onProgress, Action<bool, string> onDone)
        {
            var wi = WorldManager.Read(world);
            if (wi == null) { onDone?.Invoke(false, "world not found"); return; }
            int W = Mathf.Clamp((int)Math.Round(areaKmW), 1, MaxTilesPerSide), H = Mathf.Clamp((int)Math.Round(areaKmH), 1, MaxTilesPerSide);
            WorldManager.PlaceArea(wi, centerLat, centerLon, W, H, out int grNW, out int gcNW, out double tileMerc, out double nwX, out double nwY);
            if (WorldManager.Overlaps(world, grNW, gcNW, W, H)) { onDone?.Invoke(false, "this area overlaps one already in the world"); return; }
            string mapSet = $"r{grNW}_c{gcNW}";
            if (WorldManager.ReadMapSet(world, mapSet) != null) { onDone?.Invoke(false, "this area is already in the world"); return; }
            var gs = new GridSpec { outDir = WorldManager.MapSetDir(world, mapSet), nW = W, nH = H, tileMerc = tileMerc, nwX = nwX, nwY = nwY, grBase = grNW, gcBase = gcNW };
            Runner.StartCoroutine(Runner.RunGrid(gs,
                (mn, mxr) => { WorldManager.SaveMapSet(world, new MapSetInfo { Name = mapSet, NormMin = mn, NormMax = mxr, GR = grNW, GC = gcNW, W = W, H = H }); return null; },
                onProgress, onDone));
        }

        struct GridSpec { public string outDir; public int nW, nH; public double tileMerc, nwX, nwY; public int grBase, gcBase; }
        struct TileReq { public UnityWebRequest req; public int r, c; }

        IEnumerator RunGrid(GridSpec gs, Func<float, float, string> persist,
                            Action<float, string> onProgress, Action<bool, string> onDone)
        {
            var ci = CultureInfo.InvariantCulture;
            int nW = gs.nW, nH = gs.nH, total = nW * nH;
            double tileMerc = gs.tileMerc;

            string tmpDir = Path.Combine(Application.temporaryCachePath, "dem3dep_tmp");
            try { TryDelete(tmpDir); Directory.CreateDirectory(tmpDir); }
            catch (Exception ex) { onDone?.Invoke(false, "temp dir failed: " + ex.Message); yield break; }
            string TilePath(int r, int c) => Path.Combine(tmpDir, r + "_" + c + ".f32");

            // ── Pass 1: parallel fetch (with retries) + decode → spill floats; track min/max. RAM bounded. ──
            var pending = new Queue<int>();
            for (int i = 0; i < total; i++) pending.Enqueue(i);
            var attempts = new int[total];
            var retryAt = new float[total];   // realtimeSinceStartup before a backed-off tile may retry
            var inflight = new List<TileReq>();
            float lo = float.MaxValue, hi = float.MinValue;
            int doneCount = 0; bool failed = false; string failMsg = null;

            while ((pending.Count > 0 || inflight.Count > 0) && !failed)
            {
                float now = Time.realtimeSinceStartup;
                for (int scan = pending.Count; scan > 0 && inflight.Count < FetchConcurrency; scan--)
                {
                    int idx = pending.Dequeue();
                    if (retryAt[idx] > now) { pending.Enqueue(idx); continue; }   // still backing off
                    int r = idx / nW, c = idx % nW;
                    double xmin = gs.nwX + c * tileMerc, xmax = xmin + tileMerc, ymax = gs.nwY - r * tileMerc, ymin = ymax - tileMerc;
                    string bbox = string.Format(ci, "{0},{1},{2},{3}", xmin, ymin, xmax, ymax);
                    string url = ExportUrl + "?bbox=" + bbox + "&bboxSR=3857&imageSR=3857&size=" + TilePx + "," + TilePx
                               + "&format=tiff&pixelType=F32&interpolation=RSP_BilinearInterpolation&f=image";
                    var rq = UnityWebRequest.Get(url); rq.SendWebRequest();
                    inflight.Add(new TileReq { req = rq, r = r, c = c });
                }
                yield return null;
                for (int i = inflight.Count - 1; i >= 0 && !failed; i--)
                {
                    var tr = inflight[i];
                    if (!tr.req.isDone) continue;
                    long code = tr.req.responseCode;
                    var res = tr.req.result;
                    byte[] data = res == UnityWebRequest.Result.Success && tr.req.downloadHandler != null ? tr.req.downloadHandler.data : null;
                    tr.req.Dispose(); inflight.RemoveAt(i);
                    int idx = tr.r * nW + tr.c;

                    bool isTiff = data != null && data.Length >= 8 && ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D));
                    float[] e = null; int gw = 0, gh = 0;
                    if (res == UnityWebRequest.Result.Success && isTiff)
                    {
                        try { e = DecodeGeoTiffF32(data, out gw, out gh); }
                        catch (Exception ex) { Debug.LogError($"[Dem3DEP] tile {tr.r},{tr.c} decode exception: {ex}"); e = null; }
                        if (e != null && (gw != TilePx || gh != TilePx)) e = null;
                    }
                    if (e == null)
                    {
                        if (++attempts[idx] <= MaxRetries)
                        {
                            retryAt[idx] = Time.realtimeSinceStartup + Mathf.Min(1.5f * attempts[idx], 12f);   // backoff so the server can recover
                            Debug.LogWarning($"[Dem3DEP] tile {tr.r},{tr.c} HTTP {code} ({res}) — retry {attempts[idx]}/{MaxRetries} after backoff");
                            pending.Enqueue(idx); continue;
                        }
                        if (data != null && !isTiff) Debug.LogWarning($"[Dem3DEP] tile {tr.r},{tr.c} body:\n{System.Text.Encoding.UTF8.GetString(data, 0, Mathf.Min(300, data.Length))}");
                        failed = true; failMsg = $"tile {tr.r},{tr.c} failed after {MaxRetries} retries (HTTP {code})"; Debug.LogError("[Dem3DEP] " + failMsg); break;
                    }
                    for (int k = 0; k < e.Length; k++) { float v = e[k]; if (float.IsNaN(v) || v < -1000f || v > 10000f) continue; if (v < lo) lo = v; if (v > hi) hi = v; }
                    try { var b = new byte[e.Length * 4]; Buffer.BlockCopy(e, 0, b, 0, b.Length); File.WriteAllBytes(TilePath(tr.r, tr.c), b); }
                    catch (Exception ex) { failed = true; failMsg = "spill write failed: " + ex.Message; break; }
                    doneCount++;
                    onProgress?.Invoke(0.05f + 0.55f * doneCount / total, $"fetching {doneCount}/{total} tiles");
                }
            }
            if (failed) { for (int i = 0; i < inflight.Count; i++) inflight[i].req.Dispose(); TryDelete(tmpDir); onDone?.Invoke(false, failMsg ?? "fetch failed"); yield break; }
            if (lo > hi) { TryDelete(tmpDir); onDone?.Invoke(false, "no valid elevation (ocean / outside 3DEP coverage?)"); yield break; }

            float normMin = Mathf.Floor(lo / 10f) * 10f - 10f, normMax = Mathf.Ceil(hi / 10f) * 10f + 10f, range = Mathf.Max(1f, normMax - normMin);

            // ── Pass 2: read spilled floats → encode 16-bit tiles → write mosaic → delete temp. ──
            try { Directory.CreateDirectory(gs.outDir); }
            catch (Exception ex) { TryDelete(tmpDir); onDone?.Invoke(false, "mkdir failed: " + ex.Message); yield break; }
            var gray = new ushort[TilePx * TilePx];
            for (int r = 0; r < nH; r++)
                for (int c = 0; c < nW; c++)
                {
                    float[] e;
                    try { var b = File.ReadAllBytes(TilePath(r, c)); e = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, e, 0, b.Length); }
                    catch (Exception ex) { TryDelete(tmpDir); onDone?.Invoke(false, "spill read failed: " + ex.Message); yield break; }
                    for (int k = 0; k < gray.Length; k++)
                    {
                        float v = k < e.Length ? e[k] : normMin;
                        if (float.IsNaN(v) || v < -1000f || v > 10000f) v = normMin;
                        gray[k] = (ushort)Mathf.RoundToInt(Mathf.Clamp01((v - normMin) / range) * 65535f);
                    }
                    double tlat = WorldManager.MercY2Lat(gs.nwY - r * tileMerc), tlon = WorldManager.MercX2Lon(gs.nwX + c * tileMerc);  // NW corner
                    int gr = gs.grBase + r, gc = gs.gcBase + c;     // world-global row/col
                    string fn = $"{Fmt(tlat)}_{Fmt(tlon)}_0_{TilePx}_{TilePx}_16bit_tile_{gr}_{gc}.png";
                    try { File.WriteAllBytes(Path.Combine(gs.outDir, fn), DemPng16.Encode(gray, TilePx, TilePx)); File.Delete(TilePath(r, c)); }
                    catch (Exception ex) { TryDelete(tmpDir); onDone?.Invoke(false, "write failed: " + ex.Message); yield break; }
                    onProgress?.Invoke(0.6f + 0.38f * ((r * nW + c + 1) / (float)total), $"writing tile {r},{c}");
                    yield return null;
                }
            TryDelete(tmpDir);

            string err = persist(normMin, normMax);
            if (err != null) { onDone?.Invoke(false, err); yield break; }
            onDone?.Invoke(true, $"{nW}×{nH} km @ 1 m/px · range {normMin:0}..{normMax:0} m");
        }

        static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
        static string Fmt(double v) => v.ToString("F6", CultureInfo.InvariantCulture).Replace('.', '_');

        // Minimal GeoTIFF reader for the 3DEP exportImage response: little/big-endian, UNCOMPRESSED,
        // single-sample 32-bit FLOAT, tiled OR stripped. Returns row-major top-down float[w*h], or null.
        // (Validated against the live service: 128×128 tiles, compression 1, sampleFormat 3.)
        static float[] DecodeGeoTiffF32(byte[] d, out int W, out int H)
        {
            W = H = 0;
            if (d == null || d.Length < 16) return null;
            bool le = d[0] == 0x49 && d[1] == 0x49;     // 'II'
            bool be = d[0] == 0x4D && d[1] == 0x4D;     // 'MM'
            if (!le && !be) return null;

            ushort U16(int o) => le ? (ushort)(d[o] | (d[o + 1] << 8)) : (ushort)((d[o] << 8) | d[o + 1]);
            uint U32(int o) => le ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24))
                                  : (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
            if (U16(2) != 42) return null;
            int ifd = (int)U32(4);
            if (ifd <= 0 || ifd + 2 > d.Length) return null;
            int n = U16(ifd);

            uint[] ReadVals(int e)
            {
                int typ = U16(e + 2); uint cnt = U32(e + 4);
                int tsz = typ == 3 ? 2 : typ == 4 ? 4 : typ == 1 ? 1 : 4;
                long tot = (long)tsz * cnt;
                int b = tot <= 4 ? e + 8 : (int)U32(e + 8);
                var arr = new uint[cnt];
                for (int i = 0; i < cnt; i++) { int o = b + i * tsz; if (o + tsz > d.Length) break; arr[i] = typ == 3 ? U16(o) : typ == 4 ? U32(o) : (uint)d[o]; }
                return arr;
            }
            int One(int e) { var v = ReadVals(e); return v.Length > 0 ? (int)v[0] : 0; }

            int width = 0, height = 0, bits = 0, comp = 1, spp = 1, sf = 1, tw = 0, tl = 0, rps = 0;
            uint[] tileOff = null, stripOff = null;
            for (int i = 0; i < n; i++)
            {
                int e = ifd + 2 + i * 12;
                if (e + 12 > d.Length) break;
                switch (U16(e))
                {
                    case 256: width = One(e); break;
                    case 257: height = One(e); break;
                    case 258: bits = One(e); break;
                    case 259: comp = One(e); break;
                    case 277: spp = One(e); break;
                    case 278: rps = One(e); break;
                    case 273: stripOff = ReadVals(e); break;
                    case 322: tw = One(e); break;
                    case 323: tl = One(e); break;
                    case 324: tileOff = ReadVals(e); break;
                    case 339: sf = One(e); break;
                }
            }
            if (width <= 0 || height <= 0 || bits != 32 || sf != 3 || comp != 1 || spp != 1) return null;

            float F32(int o)
            {
                if (o < 0 || o + 4 > d.Length) return float.NaN;
                if (le == BitConverter.IsLittleEndian) return BitConverter.ToSingle(d, o);
                var b = new byte[4]; b[0] = d[o + 3]; b[1] = d[o + 2]; b[2] = d[o + 1]; b[3] = d[o];
                return BitConverter.ToSingle(b, 0);
            }

            var outp = new float[(long)width * height];
            if (tw > 0 && tl > 0 && tileOff != null)
            {
                int across = (width + tw - 1) / tw;
                for (int t = 0; t < tileOff.Length; t++)
                {
                    int tx = t % across, ty = t / across, baseOff = (int)tileOff[t];
                    for (int iy = 0; iy < tl; iy++)
                    {
                        int oy = ty * tl + iy; if (oy >= height) break;
                        for (int ix = 0; ix < tw; ix++)
                        {
                            int ox = tx * tw + ix; if (ox >= width) continue;
                            outp[oy * width + ox] = F32(baseOff + (iy * tw + ix) * 4);
                        }
                    }
                }
            }
            else if (stripOff != null)
            {
                int rowsPer = rps > 0 ? rps : height;
                for (int s = 0; s < stripOff.Length; s++)
                {
                    int baseOff = (int)stripOff[s], y0 = s * rowsPer;
                    for (int ry = 0; ry < rowsPer; ry++)
                    {
                        int oy = y0 + ry; if (oy >= height) break;
                        for (int ox = 0; ox < width; ox++)
                            outp[oy * width + ox] = F32(baseOff + (ry * width + ox) * 4);
                    }
                }
            }
            else return null;

            W = width; H = height;
            return outp;
        }
    }
}
