// A pre-baked 3D RELIEF DIORAMA of the whole DEM mosaic for the corner minimap. On DEM load we
// sample the mosaic into a small low-poly relief mesh, park it far from the play area on a dedicated
// layer, and point a private camera at it that renders to a RenderTexture (drawn in the corner by
// TerrainDesigner.DrawChunkMinimap). A translucent marker box tracks the play camera's position on
// the relief so you can see where you are in the ~71 km block.
//
// Lit by the scene's directional sun (position-independent), so no extra light is added. Vertical
// exaggeration makes the relief read at this scale. Built/destroyed by Start/StopChunkDem.

using System.Collections;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public class MinimapDiorama : MonoBehaviour
    {
        public static MinimapDiorama Current { get; private set; }
        public RenderTexture Texture => _rt;

        const int Layer = 31;                                   // dedicated render layer (main cam never sees it — diorama is 1000 km away)
        const int GridRes = 160;                                // relief samples per side
        const int RtSize = 360;                                 // render-texture resolution
        static readonly Vector3 FarOffset = new Vector3(1_000_000f, 0f, 0f);
        const float DioramaSpan = 100f;                         // largest world axis -> this many diorama units
        const float VertExag = 4.5f;                            // vertical exaggeration so slopes read
        const int BakeDecodesPerFrame = 3;                      // pace the bake so it doesn't freeze the app

        Camera _cam;
        RenderTexture _rt;
        GameObject _meshGo, _marker;
        Mesh _mesh;
        Material _mat, _markerMat;
        Camera _track;
        float _scale, _spanX, _spanZ, _minY;
        float[] _relief;   // baked relief height grid (GridRes²), so the marker can drape the same surface
        int _reliefN;

        public static void Spawn(Camera track)
        {
            Dispose();
            if (!DemChunkSource.Active) return;
            var go = new GameObject("MinimapDiorama");
            Current = go.AddComponent<MinimapDiorama>();
            Current._track = track;
            Current.Build();
        }

        public static void Dispose()
        {
            if (Current == null) return;
            if (Application.isPlaying) Destroy(Current.gameObject); else DestroyImmediate(Current.gameObject);
            Current = null;
        }

        void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); _rt = null; }
            if (Current == this) Current = null;
        }

        void Build()
        {
            float ww = DemChunkSource.WorldWidthX, wl = DemChunkSource.WorldLengthZ;
            if (ww <= 0f || wl <= 0f) { Dispose(); return; }
            _scale = DioramaSpan / Mathf.Max(ww, wl);
            _spanX = ww * _scale; _spanZ = wl * _scale;

            BuildReliefShell();
            BuildMarker();
            BuildCamera();
            StartCoroutine(BakeRelief(ww, wl));   // sample the mosaic progressively (decode-paced, no freeze)
        }

        // Empty relief mesh + material up front; BakeRelief fills it in over the next few seconds.
        void BuildReliefShell()
        {
            _meshGo = new GameObject("Relief");
            _meshGo.transform.SetParent(transform, false);
            _meshGo.transform.position = FarOffset;
            _meshGo.layer = Layer;
            _mesh = new Mesh { name = "MinimapRelief" };
            if (GridRes * GridRes > 65000) _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _meshGo.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = _meshGo.AddComponent<MeshRenderer>();
            _mat = NetworkDesigner.PipelineMaterials.CreateLitMatte(new Color(0.34f, 0.5f, 0.26f), "MinimapRelief");
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Sample the whole mosaic into the relief grid, yielding every few PNG decodes so the ~441-tile
        // first bake spreads across frames instead of locking up. Builds the mesh once at the end.
        IEnumerator BakeRelief(float ww, float wl)
        {
            int n = GridRes;
            var raw = new float[n * n];
            float ox = DemChunkSource.OriginX, oz = DemChunkSource.OriginZ;
            float minY = float.MaxValue, maxY = float.MinValue;
            int gate = DemChunkSource.DecodeCount + BakeDecodesPerFrame;
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    if (!DemChunkSource.Active) yield break;   // stopped mid-bake
                    float u = (float)x / (n - 1), v = (float)z / (n - 1);
                    float y = DemChunkSource.SampleWorldYAt(ox + u * ww, oz + v * wl);
                    raw[z * n + x] = y; if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
                if (DemChunkSource.DecodeCount >= gate) { gate = DemChunkSource.DecodeCount + BakeDecodesPerFrame; yield return null; }
            }
            _minY = minY;
            _relief = raw; _reliefN = n;   // keep for the marker drape

            var verts = new Vector3[n * n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float u = (float)x / (n - 1), v = (float)z / (n - 1);
                    verts[z * n + x] = new Vector3(u * _spanX, (raw[z * n + x] - minY) * _scale * VertExag, v * _spanZ);
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
            _mesh.Clear();
            _mesh.vertices = verts; _mesh.triangles = tris;
            _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
        }

        void BuildMarker()
        {
            // A translucent yellow patch DRAPED over the relief (a mesh, rebuilt each frame in
            // LateUpdate to follow the camera and conform to the surface). Same winding as the relief
            // → faces up, no single-sided-quad gotcha.
            _marker = new GameObject("BubbleMarker");
            _marker.transform.SetParent(transform, false);
            _marker.transform.position = FarOffset;
            _marker.layer = Layer;
            _marker.AddComponent<MeshFilter>().sharedMesh = new Mesh { name = "BubbleMarker" };
            _markerMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(1f, 0.9f, 0.15f, 0.4f), 0f, "MinimapBubble");
            var mr = _marker.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _markerMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Diorama Y of the baked relief at world-fraction (u,v) — so the marker drapes the same surface.
        float ReliefY(float u, float v)
        {
            int n = _reliefN;
            if (_relief == null || n < 2) return 0f;
            float fx = Mathf.Clamp01(u) * (n - 1), fz = Mathf.Clamp01(v) * (n - 1);
            int x0 = Mathf.Clamp((int)fx, 0, n - 1), z0 = Mathf.Clamp((int)fz, 0, n - 1);
            int x1 = Mathf.Min(x0 + 1, n - 1), z1 = Mathf.Min(z0 + 1, n - 1);
            float tx = fx - x0, tz = fz - z0;
            float h0 = Mathf.Lerp(_relief[z0 * n + x0], _relief[z0 * n + x1], tx);
            float h1 = Mathf.Lerp(_relief[z1 * n + x0], _relief[z1 * n + x1], tx);
            return (Mathf.Lerp(h0, h1, tz) - _minY) * _scale * VertExag;
        }

        void BuildCamera()
        {
            _rt = new RenderTexture(RtSize, RtSize, 24) { name = "MinimapRT" };
            _rt.Create();
            var camGo = new GameObject("MinimapCam");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.cullingMask = 1 << Layer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            _cam.fieldOfView = 42f;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = DioramaSpan * 8f;
            _cam.targetTexture = _rt;

            // Frame the whole diorama from above and to the south (–Z), tilted ~50° down.
            float span = Mathf.Max(_spanX, _spanZ);
            Vector3 center = FarOffset + new Vector3(_spanX * 0.5f, span * 0.10f, _spanZ * 0.5f);
            Vector3 eye = center + new Vector3(0f, span * 1.05f, -span * 0.95f);
            _cam.transform.position = eye;
            _cam.transform.rotation = Quaternion.LookRotation((center - eye).normalized, Vector3.up);
        }

        void LateUpdate()
        {
            if (_marker == null || !DemChunkSource.Active || _relief == null) return;
            Camera cam = _track != null ? _track : Camera.main;
            if (cam == null) { if (_marker.activeSelf) _marker.SetActive(false); return; }
            // Size from the resident bubble (how much is loaded)…
            if (!ChunkWorld.TryLoadedBounds(out _, out float sx, out float sz))
            { if (_marker.activeSelf) _marker.SetActive(false); return; }
            if (!_marker.activeSelf) _marker.SetActive(true);

            // …CENTRE on where the camera is looking (smooth tracking), and DRAPE a grid over the
            // relief across the bubble footprint so it follows the surface instead of clipping.
            Vector3 cp = cam.transform.position, f = cam.transform.forward;
            Vector3 g = (f.y < -1e-3f) ? cp + f * (-cp.y / f.y) : new Vector3(cp.x, 0f, cp.z);
            float ucx = (g.x - DemChunkSource.OriginX) / Mathf.Max(1f, DemChunkSource.WorldWidthX);
            float vcz = (g.z - DemChunkSource.OriginZ) / Mathf.Max(1f, DemChunkSource.WorldLengthZ);
            float cxD = ucx * _spanX, czD = vcz * _spanZ;                 // diorama-space centre
            float halfX = sx * _scale * 0.5f, halfZ = sz * _scale * 0.5f;
            float lift = Mathf.Max(_spanX, _spanZ) * 0.01f;              // float just above the relief

            const int n = 18;
            var verts = new Vector3[n * n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float dx = cxD - halfX + (x / (float)(n - 1)) * (2f * halfX);
                    float dz = czD - halfZ + (z / (float)(n - 1)) * (2f * halfZ);
                    float y = ReliefY(dx / Mathf.Max(1e-3f, _spanX), dz / Mathf.Max(1e-3f, _spanZ)) + lift;
                    verts[z * n + x] = new Vector3(dx, y, dz);
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
            var m = _marker.GetComponent<MeshFilter>().sharedMesh;
            m.Clear();
            m.vertices = verts; m.triangles = tris;
            m.RecalculateNormals(); m.RecalculateBounds();
        }
    }
}
