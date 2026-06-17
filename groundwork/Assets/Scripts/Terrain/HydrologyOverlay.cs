// Hydrology analysis overlay for drainage / catchment planning. Runs RiverGenerator's pipeline over a WINDOW of
// the CURRENT (edited) terrain and draws a translucent, vertex-coloured mesh draped on the ground:
//   - BLUE  = ponding depth (where rain would pool in a depression → flood / catchment basins)
//   - CYAN  = flow accumulation (where runoff concentrates → streams / swale / culvert lines)
// Static: recompute on toggle / Refresh (so re-running after you grade shows how the drainage changed). Chunk
// world only (uses the streamed/source heights). The window must enclose the catchment or its edges read as outlets.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class HydrologyOverlay
    {
        public static bool Show;
        public static float WorldSize = 2000f;   // square analysis window side (m) — must enclose the catchment
        public static int Res = 384;             // grid resolution per side
        public static float PondMin = 0.25f;     // min ponding depth (m) to draw
        public static float RainIntensity = 0.5f; // 0 = dry (only major channels), 1 = wettest (fine beds fill in)
        // Upstream-area fraction a cell needs to read as a channel — DERIVED from rain on a log scale so the slider
        // feels even: driest 0.02 (~major rivers only) → wettest 5e-5 (fine tributaries). Lower = more/finer channels.
        public static float AccumFrac => 0.02f * Mathf.Pow(0.0025f, Mathf.Clamp01(RainIntensity));
        public static float Strength = 0.7f;
        public static bool LiveUpdate = true;    // recompute after terrain edits (debounced) so drainage tracks grading

        // Set true by terrain edits (sculpt/road/terrace) while shown; the designer debounces this into a single
        // RefreshLast() once the edit settles, so a drag doesn't trigger a flood-fill every frame.
        public static bool Dirty;
        public static void MarkDirty() { if (Show && LiveUpdate) Dirty = true; }

        static GameObject _go; static MeshFilter _mf; static MeshRenderer _mr; static Mesh _mesh; static Material _mat;
        static Vector2 _center; static bool _hasCenter;
        static readonly List<Vector3> _v = new List<Vector3>();
        static readonly List<Color32> _c = new List<Color32>();
        static readonly List<int> _t = new List<int>();

        public static void SetShow(bool on, Vector2 center)
        {
            EnsureGo();
            if (_mr == null) { Show = false; return; }
            Show = on;
            if (on) Build(center);
            _mr.enabled = on;
        }

        // Recompute around a new centre (e.g. after grading, or moving to a new area) while shown.
        public static void Refresh(Vector2 center) { if (Show) { Build(center); _mr.enabled = true; } }

        // Recompute over the LAST window (no re-centring) — called after a terrain edit so the analysis tracks
        // your grading without the overlay jumping to wherever the camera now points. No-op if never shown.
        public static void RefreshLast() { Dirty = false; if (Show && _hasCenter) { Build(_center); _mr.enabled = true; } }

        static float H(float x, float z) => ChunkWorld.Active ? ChunkWorld.SampleHeightOrSource(x, z) : 0f;

        static void Build(Vector2 center)
        {
            EnsureGo();
            _center = center; _hasCenter = true;
            if (_mat != null) _mat.SetFloat("_Strength", Mathf.Clamp01(Strength));
            int res = Mathf.Clamp(Res, 16, 1024);
            float size = Mathf.Max(50f, WorldSize);
            float cell = size / (res - 1);
            float ox = center.x - size * 0.5f, oz = center.y - size * 0.5f;

            var h = new float[res * res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    h[z * res + x] = H(ox + x * cell, oz + z * cell);

            RiverGenerator.Analyze(h, res, res, cell, out var pond, out var accum);
            float accumThresh = Mathf.Max(8f, AccumFrac * res * res);

            _v.Clear(); _c.Clear(); _t.Clear();
            const float lift = 1.0f;
            for (int z = 0; z < res - 1; z++)
                for (int x = 0; x < res - 1; x++)
                {
                    int i = z * res + x;
                    float pd = pond[i], a = accum[i];
                    Color32 col;
                    if (pd > PondMin)
                    {
                        float tt = Mathf.Clamp01(pd / 6f);
                        col = new Color32(40, (byte)(120 - 50 * tt), 230, (byte)(110 + 130 * tt));   // deeper → more opaque blue
                    }
                    else if (a > accumThresh)
                    {
                        float tt = Mathf.Clamp01(Mathf.Log10(a / accumThresh) / 2f);                 // 1×..100× threshold
                        col = new Color32(70, 210, 235, (byte)(70 + 150 * tt));                      // bigger channel → brighter cyan
                    }
                    else continue;

                    int i01 = z * res + (x + 1), i10 = (z + 1) * res + x, i11 = (z + 1) * res + (x + 1);
                    float x0 = ox + x * cell, z0 = oz + z * cell, x1 = x0 + cell, z1 = z0 + cell;
                    int s = _v.Count;
                    _v.Add(new Vector3(x0, h[i] + lift, z0));
                    _v.Add(new Vector3(x1, h[i01] + lift, z0));
                    _v.Add(new Vector3(x1, h[i11] + lift, z1));
                    _v.Add(new Vector3(x0, h[i10] + lift, z1));
                    _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
                    _t.Add(s); _t.Add(s + 1); _t.Add(s + 2); _t.Add(s); _t.Add(s + 2); _t.Add(s + 3);
                }

            _mesh.Clear();
            if (_v.Count > 65000) _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.SetVertices(_v); _mesh.SetColors(_c); _mesh.SetTriangles(_t, 0);
            _mesh.RecalculateBounds();
        }

        static void EnsureGo()
        {
            if (_go != null) return;
            _go = new GameObject("HydrologyOverlay") { hideFlags = HideFlags.DontSave };
            _mf = _go.AddComponent<MeshFilter>();
            _mr = _go.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mesh = new Mesh { name = "HydrologyMesh" };
            _mf.sharedMesh = _mesh;
            var sh = Shader.Find("NetworkDesigner/HydrologyOverlay");
            if (sh == null) { Debug.LogWarning("[Hydrology] shader 'NetworkDesigner/HydrologyOverlay' not found."); return; }
            _mat = new Material(sh) { name = "HydrologyOverlay" };
            _mat.SetFloat("_Strength", Mathf.Clamp01(Strength));
            _mr.sharedMaterial = _mat;
            _mr.enabled = false;
        }
    }
}
