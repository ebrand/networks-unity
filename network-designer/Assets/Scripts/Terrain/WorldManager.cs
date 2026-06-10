// A "world" is a container of DEM map sets that grow a single navigable area. It lives at
// Heightmaps/Highres/<World>/ with a world.json (the shared Web-Mercator lattice + fixed map-set size)
// and one subfolder per map set (one download), each with a mapset.json (its own elevation range +
// block position). Every map set snaps to the world's lattice so downloads tile seamlessly; tile
// filenames carry WORLD-GLOBAL row/col so the loader can mosaic all map sets into one grid.
//
// This file owns the world/map-set on-disk model + the mercator lattice math. The first map set
// "anchors" the lattice (sets its origin + per-tile mercator size from that download's latitude).

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class WorldInfo
    {
        public string Name;
        public int MapSizeKm = 8;       // every map set is this many km square
        public bool Anchored;           // true once the first map set fixes the lattice
        public double RefLat;           // latitude the mercator tile size was computed at
        public double TileMercM;        // 1 km tile size in mercator metres (1000 / cos(refLat))
        public double OriginMercX;      // lattice origin = first map set's NW corner (mercator)
        public double OriginMercY;
    }

    [Serializable]
    public class MapSetInfo
    {
        public string Name;
        public float NormMin, NormMax;  // this map set's own encode range
        public int GR, GC, W, H;        // NW global tile row/col + width/height in 1 km tiles (any size)
        public int N;                   // legacy: square size, read when W/H are absent (older map sets)
    }

    public static class WorldManager
    {
        const double MercR = 6378137.0;

        static string Root => Path.Combine(Application.dataPath, "Heightmaps/Highres");
        public static string WorldDir(string world) => Path.Combine(Root, world);
        static string WorldFile(string world) => Path.Combine(WorldDir(world), "world.json");
        public static string MapSetDir(string world, string mapSet) => Path.Combine(WorldDir(world), mapSet);

        public static bool Exists(string world) => !string.IsNullOrWhiteSpace(world) && File.Exists(WorldFile(world));

        public static List<string> ListWorlds()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(Root))
                    foreach (var d in Directory.GetDirectories(Root))
                        if (File.Exists(Path.Combine(d, "world.json"))) list.Add(Path.GetFileName(d));
            }
            catch { }
            list.Sort();
            return list;
        }

        public static List<string> ListMapSets(string world)
        {
            var list = new List<string>();
            try
            {
                var d = WorldDir(world);
                if (Directory.Exists(d))
                    foreach (var sd in Directory.GetDirectories(d))
                        if (File.Exists(Path.Combine(sd, "mapset.json"))) list.Add(Path.GetFileName(sd));
            }
            catch { }
            list.Sort();
            return list;
        }

        public static WorldInfo Read(string world)
        {
            try { var p = WorldFile(world); return File.Exists(p) ? JsonUtility.FromJson<WorldInfo>(File.ReadAllText(p)) : null; }
            catch { return null; }
        }

        public static void Save(WorldInfo wi)
        {
            try { Directory.CreateDirectory(WorldDir(wi.Name)); File.WriteAllText(WorldFile(wi.Name), JsonUtility.ToJson(wi, true)); }
            catch { }
        }

        public static bool Create(string world)
        {
            if (string.IsNullOrWhiteSpace(world) || Exists(world)) return false;
            try
            {
                var wi = new WorldInfo { Name = world, MapSizeKm = 8, Anchored = false };   // MapSizeKm = default picker area
                Save(wi);
                // Also a game manifest so the world is loadable + shows in the launcher. The placeholder
                // range is overridden by DemChunkSource.Configure, which uses each map set's own range.
                GameManager.Create(world, world, -500f, 9000f);
                return Exists(world);
            }
            catch { return false; }
        }

        // Give every world a game manifest so it shows in the launcher + loads (backfills worlds made
        // before manifests were written). The placeholder range is overridden by DemChunkSource.Configure.
        public static void EnsureWorldGames()
        {
            foreach (var w in ListWorlds())
                if (!GameManager.Exists(w)) GameManager.Create(w, w, -500f, 9000f);
        }

        public static MapSetInfo ReadMapSet(string world, string mapSet)
        {
            try { var p = Path.Combine(MapSetDir(world, mapSet), "mapset.json"); return File.Exists(p) ? JsonUtility.FromJson<MapSetInfo>(File.ReadAllText(p)) : null; }
            catch { return null; }
        }

        public static void SaveMapSet(string world, MapSetInfo mi)
        {
            try { Directory.CreateDirectory(MapSetDir(world, mi.Name)); File.WriteAllText(Path.Combine(MapSetDir(world, mi.Name), "mapset.json"), JsonUtility.ToJson(mi, true)); }
            catch { }
        }

        // Snap a download (w×h 1 km tiles, centred at lat/lon) to the world's 1 km lattice — areas can be
        // any size. Anchors the lattice on the first map set. Returns the NW global tile row/col + the NW
        // corner (mercator) the map set's tiles lay out from.
        public static void PlaceArea(WorldInfo wi, double lat, double lon, int w, int h,
                                     out int grNW, out int gcNW, out double tileMerc, out double nwMercX, out double nwMercY)
        {
            double mx = Lon2MercX(lon), my = Lat2MercY(lat);
            if (!wi.Anchored)
            {
                tileMerc = 1000.0 / Math.Cos(lat * Math.PI / 180.0);
                nwMercX = mx - w * tileMerc / 2.0;
                nwMercY = my + h * tileMerc / 2.0;
                wi.Anchored = true; wi.RefLat = lat; wi.TileMercM = tileMerc;
                wi.OriginMercX = nwMercX; wi.OriginMercY = nwMercY;
                Save(wi);
                grNW = 0; gcNW = 0;
                return;
            }
            tileMerc = wi.TileMercM;
            double nwx = mx - w * tileMerc / 2.0, nwy = my + h * tileMerc / 2.0;
            gcNW = (int)Math.Round((nwx - wi.OriginMercX) / tileMerc);
            grNW = (int)Math.Round((wi.OriginMercY - nwy) / tileMerc);
            nwMercX = wi.OriginMercX + gcNW * tileMerc;
            nwMercY = wi.OriginMercY - grNW * tileMerc;
        }

        // True if a w×h tile area at (grNW,gcNW) would overlap any existing map set's tiles.
        public static bool Overlaps(string world, int grNW, int gcNW, int w, int h)
        {
            foreach (var s in ListMapSets(world))
            {
                var mi = ReadMapSet(world, s);
                if (mi == null) continue;
                int mw = mi.W > 0 ? mi.W : mi.N, mh = mi.H > 0 ? mi.H : mi.N;
                if (mw <= 0 || mh <= 0) continue;
                if (grNW < mi.GR + mh && grNW + h > mi.GR && gcNW < mi.GC + mw && gcNW + w > mi.GC) return true;
            }
            return false;
        }

        public static double Lon2MercX(double lon) => MercR * lon * Math.PI / 180.0;
        public static double Lat2MercY(double lat) { double r = lat * Math.PI / 180.0; return MercR * Math.Log(Math.Tan(Math.PI / 4.0 + r / 2.0)); }
        public static double MercX2Lon(double x) => x / MercR * 180.0 / Math.PI;
        public static double MercY2Lat(double y) => (2.0 * Math.Atan(Math.Exp(y / MercR)) - Math.PI / 2.0) * 180.0 / Math.PI;
    }
}
