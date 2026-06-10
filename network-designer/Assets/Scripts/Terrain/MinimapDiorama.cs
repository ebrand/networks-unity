// A pre-baked 3D RELIEF DIORAMA of the whole DEM mosaic for the corner minimap. On DEM load we
// sample the mosaic into a small low-poly relief mesh, park it far from the play area on a dedicated
// layer, and point a private camera at it that renders to a RenderTexture (drawn in the corner by
// TerrainDesigner.DrawChunkMinimap). A filled yellow patch marks the ACTUAL loaded-chunk bubble on
// the relief, and a small hollow yellow outline marks where the play camera is — so when the bubble
// is locked and you fly away, you can see both where your terrain is and where you are.
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
        GameObject _meshGo, _marker, _camMarker;
        Mesh _mesh;
        Material _mat, _markerMat, _camMarkerMat;
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
            BuildCamMarker();
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
            // A translucent yellow patch DRAPED over the relief at the ACTUAL loaded-chunk footprint
            // (rebuilt each frame in LateUpdate to conform to the surface). Same winding as the relief
            // → faces up, no single-sided-quad gotcha.
            _marker = new GameObject("BubbleMarker");
            _marker.transform.SetParent(transform, false);
            _marker.transform.position = FarOffset;
            _marker.layer = Layer;
            _marker.AddComponent<MeshFilter>().sharedMesh = new Mesh { name = "BubbleMarker" };
            _markerMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(1f, 0.88f, 0.1f, 0.55f), 0f, "MinimapBubble");
            var mr = _marker.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _markerMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        void BuildCamMarker()
        {
            // A small hollow yellow OUTLINE marking where the play camera is (so you can tell it apart
            // from the loaded-chunk patch when the bubble is locked and you've flown elsewhere).
            _camMarker = new GameObject("CamMarker");
            _camMarker.transform.SetParent(transform, false);
            _camMarker.transform.position = FarOffset;
            _camMarker.layer = Layer;
            _camMarker.AddComponent<MeshFilter>().sharedMesh = new Mesh { name = "CamMarker" };
            _camMarkerMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(1f, 0.95f, 0.2f, 0.95f), 0f, "MinimapCam");
            _camMarkerMat.SetFloat("_Cull", 0f);   // double-sided so the thin ring shows regardless of winding
            var mr = _camMarker.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _camMarkerMat;
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

            OrientCamera(_track);   // initial framing (then re-oriented each LateUpdate to the camera heading)
        }

        // Orbit the diorama camera around the relief by the play camera's heading, so the play camera's
        // forward always points "into/up" the minimap. Keeps the minimap's orientation locked to where
        // you're actually facing instead of fixed north-up. Runs even mid-bake (only needs the spans).
        void OrientCamera(Camera cam)
        {
            if (_cam == null) return;
            float span = Mathf.Max(_spanX, _spanZ);
            Vector3 center = FarOffset + new Vector3(_spanX * 0.5f, span * 0.10f, _spanZ * 0.5f);
            float headingDeg = 0f;
            if (cam != null)
            {
                Vector3 f = cam.transform.forward; f.y = 0f;
                if (f.sqrMagnitude > 1e-4f) headingDeg = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;   // 0 = +Z
            }
            // Base eye sits south & above (heading 0 → north-up, matching the old fixed framing); rotating
            // the offset by the heading swings the viewpoint so forward ends up away-from-viewer (up).
            Vector3 off = Quaternion.Euler(0f, headingDeg, 0f) * new Vector3(0f, span * 1.05f, -span * 0.95f);
            Vector3 eye = center + off;
            _cam.transform.position = eye;
            _cam.transform.rotation = Quaternion.LookRotation((center - eye).normalized, Vector3.up);
        }

        void LateUpdate()
        {
            Camera cam = _track != null ? _track : Camera.main;
            OrientCamera(cam);   // keep the diorama heading synced to the play camera, even while baking

            if (_marker == null || _camMarker == null || !DemChunkSource.Active || _relief == null) return;

            // Filled patch = the ACTUAL loaded chunks (true centre + extent from the resident set), so it
            // stays put on the relief when the bubble is locked and you fly away.
            if (!ChunkWorld.TryLoadedBounds(out Vector3 bc, out float sx, out float sz))
            { if (_marker.activeSelf) _marker.SetActive(false); }
            else
            {
                if (!_marker.activeSelf) _marker.SetActive(true);
                float liftB = Mathf.Max(_spanX, _spanZ) * 0.01f;
                DrapePatch(_marker.GetComponent<MeshFilter>().sharedMesh,
                    WorldToDioramaX(bc.x), WorldToDioramaZ(bc.z), sx * _scale * 0.5f, sz * _scale * 0.5f, liftB);
            }

            // Hollow outline = where the play camera is looking (small fixed square, lifted above the patch).
            if (cam == null) { if (_camMarker.activeSelf) _camMarker.SetActive(false); return; }
            if (!_camMarker.activeSelf) _camMarker.SetActive(true);
            Vector3 cp = cam.transform.position, f = cam.transform.forward;
            Vector3 g = (f.y < -1e-3f) ? cp + f * (-cp.y / f.y) : new Vector3(cp.x, 0f, cp.z);
            float camHalf = Mathf.Max(_spanX, _spanZ) * 0.025f;
            float liftC = Mathf.Max(_spanX, _spanZ) * 0.02f;
            DrapeOutline(_camMarker.GetComponent<MeshFilter>().sharedMesh,
                WorldToDioramaX(g.x), WorldToDioramaZ(g.z), camHalf, camHalf, camHalf * 0.34f, liftC);
        }

        float WorldToDioramaX(float wx) => (wx - DemChunkSource.OriginX) / Mathf.Max(1f, DemChunkSource.WorldWidthX) * _spanX;
        float WorldToDioramaZ(float wz) => (wz - DemChunkSource.OriginZ) / Mathf.Max(1f, DemChunkSource.WorldLengthZ) * _spanZ;

        Vector3 Pt(float dx, float dz, float lift) =>
            new Vector3(dx, ReliefY(dx / Mathf.Max(1e-3f, _spanX), dz / Mathf.Max(1e-3f, _spanZ)) + lift, dz);

        // A filled grid patch draped over the relief, centred at (cxD,czD) with the given half-extents.
        void DrapePatch(Mesh m, float cxD, float czD, float halfX, float halfZ, float lift)
        {
            const int n = 18;
            var verts = new Vector3[n * n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float dx = cxD - halfX + (x / (float)(n - 1)) * (2f * halfX);
                    float dz = czD - halfZ + (z / (float)(n - 1)) * (2f * halfZ);
                    verts[z * n + x] = Pt(dx, dz, lift);
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
            m.Clear(); m.vertices = verts; m.triangles = tris;
            m.RecalculateNormals(); m.RecalculateBounds();
        }

        // A hollow rectangular OUTLINE (border ring of width t) draped over the relief. Double-sided
        // material, so winding doesn't matter.
        void DrapeOutline(Mesh m, float cxD, float czD, float halfX, float halfZ, float t, float lift)
        {
            float[] ox = { cxD - halfX, cxD + halfX, cxD + halfX, cxD - halfX };
            float[] oz = { czD - halfZ, czD - halfZ, czD + halfZ, czD + halfZ };
            float[] ix = { cxD - halfX + t, cxD + halfX - t, cxD + halfX - t, cxD - halfX + t };
            float[] iz = { czD - halfZ + t, czD - halfZ + t, czD + halfZ - t, czD + halfZ - t };
            var verts = new Vector3[8];
            for (int i = 0; i < 4; i++) verts[i] = Pt(ox[i], oz[i], lift);
            for (int i = 0; i < 4; i++) verts[4 + i] = Pt(ix[i], iz[i], lift);
            // Wound to face UP (so the lit material isn't dark); double-sided material covers either way.
            var tris = new int[] { 0,5,1, 0,4,5, 1,6,2, 1,5,6, 2,7,3, 2,6,7, 3,4,0, 3,7,4 };
            m.Clear(); m.vertices = verts; m.triangles = tris;
            m.RecalculateNormals(); m.RecalculateBounds();
        }
    }
}
