// Procedural terrain/heightmap generator. Fills a heightfield (row-major metres)
// from layered Perlin noise so you don't need to hunt for real-world heightmaps.
//
// Styles:
//   RollingHills - smooth fBm, flattened valleys (good for roads/rail)
//   Mountains    - ridged multifractal, sharp spines and high relief
//   Islands      - fBm with a radial falloff so edges sink below sea level
//   Plateaus     - terraced fBm with ridged canyons carved in (high-desert mesas)
//
// Domain warping perturbs the sample coordinates so features don't look grid-aligned.
// Feature scale is in WORLD METRES, so a low-res preview matches the full-res apply.
// Hydraulic erosion is layered on separately (see Erode, wired in Stage 2).

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public enum TerrainStyle { RollingHills, Mountains, Islands, Plateaus }

    [System.Serializable]
    public class TerrainGenSettings
    {
        public TerrainStyle Style = TerrainStyle.RollingHills;
        public int Seed = 1337;
        public float FeatureScale = 900f;   // metres per main feature (larger = broader landforms)
        public int Octaves = 6;
        public float Persistence = 0.5f;    // roughness: amplitude falloff per octave
        public float Lacunarity = 2f;       // frequency growth per octave
        public float MaxHeight = 300f;      // metres at the highest point
        public float WarpStrength = 0.45f;  // domain warp amount (0 = none)
        public float SeaLevel = 0f;         // metres; islands dip below this at the edges
        public int ErosionDroplets = 0;     // hydraulic erosion passes (later; 0 = off)

        // Procedural rivers (flow-accumulation channels carved into the surface).
        public bool Rivers = false;
        public float RiverDensity = 0.5f;   // 0..1, more = denser stream network
        public float RiverCarve = 8f;       // metres the main channels are cut down

        public TerrainGenSettings Clone() => (TerrainGenSettings)MemberwiseClone();
    }

    public static class TerrainGenerator
    {
        // Fill heights[] (row-major cx*rz, metres) over the given world span.
        public static void Generate(float[] heights, int cx, int rz,
                                    float worldSpanX, float worldSpanZ, TerrainGenSettings s)
        {
            if (heights == null || cx < 2 || rz < 2 || heights.Length < cx * rz || s == null) return;

            // Seed -> stable sample offsets (Perlin repeats near the origin, so push out).
            var rng = new System.Random(s.Seed);
            float ox = (float)rng.NextDouble() * 8000f - 4000f;
            float oy = (float)rng.NextDouble() * 8000f - 4000f;
            float wox = (float)rng.NextDouble() * 8000f - 4000f;
            float woy = (float)rng.NextDouble() * 8000f - 4000f;

            float scale = Mathf.Max(1f, s.FeatureScale);
            int oct = Mathf.Clamp(s.Octaves, 1, 10);
            float pers = Mathf.Clamp(s.Persistence, 0.1f, 0.95f);
            float lac = Mathf.Clamp(s.Lacunarity, 1.5f, 3f);

            for (int z = 0; z < rz; z++)
            {
                float wz = (float)z / (rz - 1) * worldSpanZ;
                float v = (float)z / (rz - 1);
                for (int x = 0; x < cx; x++)
                {
                    float wx = (float)x / (cx - 1) * worldSpanX;
                    float u = (float)x / (cx - 1);
                    float nx = wx / scale, ny = wz / scale;

                    // Domain warp for organic, non-grid-aligned shapes.
                    if (s.WarpStrength > 0f)
                    {
                        float w1 = Fbm(nx + wox, ny + woy, 4, 0.5f, 2f);
                        float w2 = Fbm(nx + wox + 5.2f, ny + woy + 1.3f, 4, 0.5f, 2f);
                        nx += (w1 - 0.5f) * 2f * s.WarpStrength;
                        ny += (w2 - 0.5f) * 2f * s.WarpStrength;
                    }

                    float h;
                    switch (s.Style)
                    {
                        case TerrainStyle.Mountains:
                            h = Ridged(nx + ox, ny + oy, oct, pers, lac);
                            h = Mathf.Pow(h, 1.3f);
                            break;

                        case TerrainStyle.Plateaus:
                        {
                            float b = Fbm(nx + ox, ny + oy, oct, pers, lac);
                            const float steps = 5f;
                            float terr = Mathf.Round(b * steps) / steps;
                            h = Mathf.Lerp(b, terr, 0.7f);                       // mesa steps
                            float canyon = Ridged(nx * 1.5f + ox + 3f, ny * 1.5f + oy + 3f, 4, 0.5f, 2f);
                            h -= (1f - canyon) * 0.25f;                          // carve valleys
                            h = Mathf.Clamp01(h);
                            break;
                        }

                        case TerrainStyle.Islands:
                        {
                            float b = Fbm(nx + ox, ny + oy, oct, pers, lac);
                            float dx = (u - 0.5f) * 2f, dy = (v - 0.5f) * 2f;
                            float d = Mathf.Sqrt(dx * dx + dy * dy);            // 0 centre .. ~1.41 corner
                            float fall = Mathf.Clamp01(1f - Mathf.SmoothStep(0.5f, 1f, d));
                            h = b * fall - (1f - fall) * 0.35f;                  // edges sink below 0
                            break;
                        }

                        default: // RollingHills
                            h = Fbm(nx + ox, ny + oy, oct, pers, lac);
                            h = Mathf.Pow(Mathf.Clamp01(h), 1.6f);              // flatter valleys
                            break;
                    }

                    heights[z * cx + x] = h * s.MaxHeight + s.SeaLevel;
                }
            }
        }

        // fBm in ~0..1 (Unity Perlin returns ~0..1).
        static float Fbm(float x, float y, int octaves, float persistence, float lacunarity)
        {
            float amp = 1f, freq = 1f, sum = 0f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Mathf.PerlinNoise(x * freq, y * freq);
                norm += amp; amp *= persistence; freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        // Ridged multifractal in ~0..1 (sharp ridges at noise peaks).
        static float Ridged(float x, float y, int octaves, float persistence, float lacunarity)
        {
            float amp = 1f, freq = 1f, sum = 0f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                float n = Mathf.PerlinNoise(x * freq, y * freq);
                float r = 1f - Mathf.Abs(2f * n - 1f);
                r *= r;
                sum += amp * r; norm += amp; amp *= persistence; freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        // Hill-shaded colour preview (top-down lambert shading so relief reads at a glance).
        // Caller owns the returned texture and must Destroy it.
        public static Texture2D RenderPreview(TerrainGenSettings s, int size, float worldSpanX, float worldSpanZ)
        {
            size = Mathf.Clamp(size, 16, 512);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            RenderPreviewInto(tex, s, worldSpanX, worldSpanZ);
            return tex;
        }

        // Fill an existing texture (reused across live-preview updates to avoid churn).
        public static void RenderPreviewInto(Texture2D tex, TerrainGenSettings s, float worldSpanX, float worldSpanZ)
        {
            if (tex == null) return;
            int size = tex.width;
            var h = new float[size * size];
            Generate(h, size, size, worldSpanX, worldSpanZ, s);

            float cell = Mathf.Max(0.01f, worldSpanX / (size - 1));
            float[] riverW = s.Rivers
                ? RiverGenerator.ComputeAndCarve(h, size, size, cell, s.RiverDensity, s.RiverCarve, true)
                : null;
            var river = new Color(0.13f, 0.42f, 0.7f);

            var px = new Color32[size * size];
            Vector3 lightDir = new Vector3(-0.5f, 1f, -0.35f).normalized;
            float sea = s.SeaLevel;
            var lo = new Color(0.26f, 0.5f, 0.22f);   // lowland green
            var hi = new Color(0.86f, 0.85f, 0.8f);   // peak rock/snow
            var deep = new Color(0.04f, 0.12f, 0.26f);
            var shallow = new Color(0.1f, 0.32f, 0.52f);

            for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                {
                    int i = z * size + x;
                    int xl = Mathf.Max(0, x - 1), xr = Mathf.Min(size - 1, x + 1);
                    int zd = Mathf.Max(0, z - 1), zu = Mathf.Min(size - 1, z + 1);
                    float dhdx = (h[z * size + xr] - h[z * size + xl]) / (2f * cell);
                    float dhdz = (h[zu * size + x] - h[zd * size + x]) / (2f * cell);
                    Vector3 normal = new Vector3(-dhdx, 1f, -dhdz).normalized;
                    float lambert = Mathf.Clamp01(Vector3.Dot(normal, lightDir)) * 0.8f + 0.2f;

                    Color c;
                    if (h[i] < sea)
                        c = Color.Lerp(shallow, deep, Mathf.Clamp01(Mathf.InverseLerp(sea, sea - 120f, h[i])));
                    else
                        c = Color.Lerp(lo, hi, Mathf.Clamp01(Mathf.InverseLerp(sea, sea + s.MaxHeight, h[i])));
                    c *= lambert;
                    if (riverW != null && riverW[i] > 0f)
                        c = Color.Lerp(c, river, 0.6f + 0.35f * Mathf.Clamp01(riverW[i] / (cell * 4f)));
                    px[i] = c;
                }
            tex.SetPixels32(px);
            tex.Apply();
        }
    }
}
