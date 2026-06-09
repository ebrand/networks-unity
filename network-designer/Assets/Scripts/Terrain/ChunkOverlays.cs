// Two flat overlays for the chunk world:
//   • WATER — a single transparent plane at a configurable sea level spanning the whole block.
//   • LOCAL GRID — a fine 10 m / 50 m grid patch that follows where the camera is looking, for
//     close-up building/sculpting (the chunk grid's 100 m lines are too coarse up close).
// Self-contained GameObjects, created lazily on toggle and torn down when the chunk world stops.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class ChunkOverlays
    {
        // --- water ---
        public static bool ShowWater = false;
        public static float WaterLevel = 0f;          // world Y (m); DEM heights are real metres, sea level ≈ 0
        public static Color WaterColor = new Color(0.18f, 0.42f, 0.62f, 0.55f);
        public static float WaterSmoothness = 0.4f;
        static GameObject _water;
        static Material _waterMat;

        // --- local build grid ---
        public static bool ShowLocalGrid = false;
        public static float LocalGridSize = 400f;     // metres across the patch
        static GameObject _grid;
        static Material _gridMat;
        static Texture2D _gridTex;
        static Camera _cam;   // latest active camera (for choosing the drop spot); grid itself is static

        public static void SetWater(bool on)
        {
            ShowWater = on;
            if (on) EnsureWater();
            if (_water != null) _water.SetActive(on);
        }

        public static void SetWaterLevel(float y)
        {
            WaterLevel = Mathf.Round(y);   // snap to whole metres (0 = sea level), for both the slider + React
            if (_water != null) { var p = _water.transform.position; p.y = WaterLevel; _water.transform.position = p; }
        }

        public static void SetWaterColor(Color c)
        {
            WaterColor = c;
            if (_waterMat != null)
            {
                _waterMat.color = c;
                if (_waterMat.HasProperty("_BaseColor")) _waterMat.SetColor("_BaseColor", c);
            }
        }

        public static void SetWaterSmoothness(float s)
        {
            WaterSmoothness = Mathf.Clamp01(s);
            if (_waterMat != null && _waterMat.HasProperty("_Smoothness")) _waterMat.SetFloat("_Smoothness", WaterSmoothness);
        }

        static void EnsureWater()
        {
            if (_water != null) return;
            _water = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _water.name = "ChunkWater";
            var col = _water.GetComponent<Collider>(); if (col != null) Object.Destroy(col);
            _water.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // lie flat, face up (Quad fronts -Z)
            _waterMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(WaterColor, WaterSmoothness, "ChunkWater");
            var mr = _water.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _waterMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // Span the DEM block (centred on origin) or a large fixed area for the flat world.
            float span = DemChunkSource.Active ? Mathf.Max(DemChunkSource.WorldWidthX, DemChunkSource.WorldLengthZ) * 1.1f : 100000f;
            Vector3 c = DemChunkSource.Active ? DemChunkSource.CenterWorld : Vector3.zero;
            _water.transform.position = new Vector3(c.x, WaterLevel, c.z);
            _water.transform.localScale = new Vector3(span, span, 1f);
        }

        public static void SetLocalGrid(bool on)
        {
            ShowLocalGrid = on;
            if (on) { EnsureGridObject(); Drape(); }   // drop where you're looking and bake it to the terrain
            if (_grid != null) _grid.SetActive(on);
        }

        static void EnsureGridObject()
        {
            if (_grid != null) return;
            _grid = new GameObject("ChunkLocalGrid");
            _grid.AddComponent<MeshFilter>().sharedMesh = new Mesh { name = "LocalGrid" };
            if (_gridTex == null) _gridTex = BuildGridTex();
            _gridMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(Color.white, 0f, "ChunkLocalGrid");
            _gridMat.mainTexture = _gridTex;
            if (_gridMat.HasProperty("_BaseMap")) _gridMat.SetTexture("_BaseMap", _gridTex);
            var mr = _grid.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _gridMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Drop the grid where the camera is looking and bake a mesh that DRAPES the terrain. Static:
        // it stays put once placed (re-toggle to move it). Re-draping picks up sculpt edits too.
        static void Drape()
        {
            if (_grid == null) return;
            Camera cam = _cam != null ? _cam : Camera.main;
            float cx = 0f, cz = 0f;
            if (cam != null)
            {
                Vector3 p = cam.transform.position, f = cam.transform.forward;
                Vector3 g = (f.y < -1e-3f) ? p + f * (-p.y / f.y) : new Vector3(p.x, 0f, p.z);
                cx = g.x; cz = g.z;
            }
            _grid.transform.position = new Vector3(cx, 0f, cz);

            const int n = 64;
            float half = LocalGridSize * 0.5f, step = LocalGridSize / (n - 1);
            var verts = new Vector3[n * n];
            var uv = new Vector2[n * n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float dx = -half + x * step, dz = -half + z * step;
                    float h = ChunkWorld.SampleHeight(cx + dx, cz + dz);
                    verts[z * n + x] = new Vector3(dx, h + 0.4f, dz);   // local; GO sits at (cx,0,cz)
                    uv[z * n + x] = new Vector2((float)x / (n - 1), (float)z / (n - 1));
                }
            var tris = new int[(n - 1) * (n - 1) * 6];
            int ti = 0;
            for (int z = 0; z < n - 1; z++)
                for (int x = 0; x < n - 1; x++)
                {
                    int a = z * n + x, b = a + 1, c = a + n, d = c + 1;
                    tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                    tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
                }
            var m = _grid.GetComponent<MeshFilter>().sharedMesh;
            m.Clear();
            m.vertices = verts; m.uv = uv; m.triangles = tris;
            m.RecalculateNormals(); m.RecalculateBounds();
        }

        // Just track the active camera (so a fresh toggle drops the grid at the current view). The
        // grid does NOT follow the camera — it's static once placed.
        public static void Update(Camera cam) { _cam = cam; }

        // Re-apply the current toggle state (used when the chunk world (re)starts after a teardown).
        public static void Reapply()
        {
            SetWater(ShowWater);
            SetLocalGrid(ShowLocalGrid);
        }

        public static void Teardown()
        {
            if (_water != null) Object.Destroy(_water);
            if (_grid != null) Object.Destroy(_grid);
            _water = null; _grid = null;
        }

        // RGBA grid: transparent gaps, cyan minor lines (~10 m), brighter major lines (~50 m).
        static Texture2D BuildGridTex()
        {
            const int N = 512;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true);
            var px = new Color[N * N];
            Color clear = new Color(0f, 0f, 0f, 0f);
            Color minor = new Color(0.40f, 0.90f, 1f, 0.45f);
            Color major = new Color(0.60f, 1f, 1f, 0.85f);
            int minorStep = N / 40;   // ~10 m if patch = 400 m
            int majorStep = N / 8;    // ~50 m
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    Color c = clear;
                    if (x % minorStep < 1 || y % minorStep < 1) c = minor;
                    if (x % majorStep < 2 || y % majorStep < 2) c = major;
                    px[y * N + x] = c;
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(true);
            return tex;
        }
    }
}
