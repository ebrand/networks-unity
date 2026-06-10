// USGS 3DEP 1 m elevation downloader — PHASE 2, NOT YET IMPLEMENTED.
//
// Planned: fetch the selected bbox from the USGS 3DEPElevation ImageServer
// (https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage,
// format=tiff, pixelType=F32) at 1 m/px where lidar coverage exists, decode the float32 GeoTIFF,
// and re-tile into the DemChunkSource 16-bit PNG mosaic (1 km tiles, NW-corner lat/lon in the
// filename, ≥2×2 so the loader can measure tile pitch) + GameManager manifest. US-only.
//
// Until then Start() reports "not implemented" so the UI never silently downloads wrong data.

using System;

namespace NetworkDesigner.Terrain
{
    public static class Dem3DEP
    {
        // 16-bit output (2 bytes/px) at 1 m/px → an areaKm-square map is (areaKm·1000)² pixels.
        public static void Estimate(double areaKm, out double sizeMB, out double seconds, out long pxSide)
        {
            pxSide = (long)Math.Round(areaKm * 1000.0);
            sizeMB = pxSide * pxSide * 2.0 / 1_000_000.0;
            seconds = Math.Max(3.0, areaKm * areaKm * 1.5);   // ~1.5 s per 1 km tile (rough placeholder)
        }

        public static void Start(string name, double centerLat, double centerLon, double areaKm,
                                 Action<float, string> onProgress, Action<bool, string> onDone)
        {
            onDone?.Invoke(false, "1 m USGS 3DEP backend not wired yet — UI preview only.");
        }
    }
}
