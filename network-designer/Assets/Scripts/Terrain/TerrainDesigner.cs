// Terrain designer — grid-heightfield terrain with sculpt brushes (URP).
//
// Setup: put this on an empty GameObject (ideally at world origin, like the
// road designer's GroundGrid). RequireComponent adds the MeshFilter /
// MeshRenderer; a MeshCollider is added for the sculpt raycast. The mesh is
// centered on the GameObject, so its transform positions the terrain.
//
// Sculpting runs in Play mode: hold the left mouse button over the terrain
// and drag. Brush mode: 1=Raise, 2=Lower, 3=Smooth, 4=Flatten (or set in the
// Inspector). Save/load is a later slice.
//
// The "test hill" is stamped ONCE when the field is first created, so it's
// just starting relief you can sculpt on top of — it is not re-applied on
// rebuilds.

using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using NetworkDesigner.Designer; // SceneAmbiance

namespace NetworkDesigner.Terrain
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TerrainDesigner : MonoBehaviour
    {
        public enum BrushMode { Raise, Lower, Smooth, Flatten }

        [Header("Grid")]
        [Tooltip("Vertex count along X / Z. Total verts = ColumnsX * RowsZ. " +
                 "201 x 10 m = 2 km. Higher counts hitch on per-frame rebuilds.")]
        public int ColumnsX = 201;
        public int RowsZ = 201;
        [Tooltip("Metres between adjacent grid vertices. 201 verts x 10 m = 2 km.")]
        public float CellSize = 10f;

        [Header("Appearance")]
        public Color TerrainColor = new Color(0.42f, 0.5f, 0.30f); // grassy
        [Range(0f, 1f)] public float Smoothness = 0f;

        [Header("Sculpt brush")]
        public BrushMode Brush = BrushMode.Raise;
        [Tooltip("Brush radius in metres. Resize live with numpad +/-.")]
        public float BrushRadius = 10f;
        [Tooltip("Numpad +/- resize speed (metres/second, while held).")]
        public float BrushResizeRate = 50f;
        [Tooltip("Upper clamp for the brush radius (metres).")]
        public float MaxBrushRadius = 500f;
        [Tooltip("Height change rate (metres/second) at the brush centre.")]
        public float BrushStrength = 20f;
        [Tooltip("0 = hard edge, 1 = soft (smoothstep) falloff to the rim.")]
        [Range(0f, 1f)] public float BrushFalloff = 0.7f;
        [Tooltip("Camera used for the sculpt raycast. Defaults to Camera.main.")]
        public Camera PickCamera;

        [Header("Brush cursor (ring)")]
        public bool ShowBrushCursor = true;
        public Color BrushCursorColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        [Range(8, 128)] public int BrushCursorSegments = 48;
        [Tooltip("Metres the ring floats above the surface so it doesn't z-fight.")]
        public float BrushCursorLift = 0.15f;
        [Tooltip("Draw the cursor ring dashed instead of solid.")]
        public bool BrushCursorDashed = false;
        [Tooltip("Dash length (m) when Brush Cursor Dashed is on.")]
        public float BrushCursorDashLength = 1.5f;
        [Tooltip("Gap length (m) between dashes when Brush Cursor Dashed is on.")]
        public float BrushCursorDashGap = 1.5f;

        [Header("Topographic lines")]
        public bool ShowContours = true;
        [Tooltip("Elevation between contour lines, in metres.")]
        public float ContourInterval = 1f;
        public Color ContourColor = new Color(0.22f, 0.15f, 0.08f, 1f); // dark brown
        [Tooltip("Metres the lines float above the surface to avoid z-fighting.")]
        public float ContourLift = 0.05f;
        [Tooltip("Draw the contour lines dashed instead of solid.")]
        public bool ContourDashed = false;
        [Tooltip("Dash length (m) when Contour Dashed is on.")]
        public float ContourDashLength = 2f;
        [Tooltip("Gap length (m) between dashes when Contour Dashed is on.")]
        public float ContourDashGap = 2f;
        [Tooltip("Rebuild contours every sculpt frame so they track the terrain " +
                 "in real time. Turn off (rebuild only on stroke-end) if it " +
                 "hitches on very large grids.")]
        public bool LiveContours = true;

        [Header("Initial relief (stamped once)")]
        [Tooltip("Stamp a smooth gaussian hill when the field is first built, " +
                 "so there's something to sculpt. Does NOT re-apply on rebuild.")]
        public bool TestHill = false;
        public float TestHillHeight = 80f;

        [Header("Autosave (terrain persistence across Play stop/start)")]
        public bool Autosave = true;
        [Tooltip("Where the terrain is saved. Empty → project_root/TerrainAutosave.json " +
                 "in the Editor, persistentDataPath in a Player build.")]
        public string AutosavePath = "";
        [Tooltip("Seconds of no sculpting before the terrain is written to disk.")]
        public float AutosaveDebounceSeconds = 1f;

        TerrainField _field;
        float _dirtySince = -1f; // realtime when last edited; -1 = clean
        Mesh _mesh;
        Material _mat;
        MeshCollider _collider;
        MeshFilter _cursorMf;
        MeshRenderer _cursorMr;
        Mesh _cursorMesh;
        Material _cursorMat;
        readonly List<Vector3> _ring = new List<Vector3>();
        readonly List<Vector3> _cursorVerts = new List<Vector3>();
        readonly List<int> _cursorIdx = new List<int>();
        MeshFilter _contourMf;
        MeshRenderer _contourMr;
        Mesh _contourMesh;
        Material _contourMat;
        bool _hasFlattenTarget;
        float _flattenTarget; // height offset (field space) captured on mouse-down

        public TerrainField Field => _field;

        [Header("Scene lighting")]
        [Tooltip("On Start, if the scene has no SceneAmbiance, create one that " +
                 "lights itself: a directional sun, soft shadows, ambient fill, " +
                 "and a large URP shadow distance. Turn off to manage lighting " +
                 "yourself in the Lighting window.")]
        public bool AutoLighting = true;
        [Tooltip("URP shadow distance (metres) the auto-lighting requests — " +
                 "should comfortably exceed the terrain footprint.")]
        public float ShadowDistance = 300f;

        [Header("Camera")]
        [Tooltip("On Start, add an OrbitCameraController to the pick camera if it " +
                 "has none, framed on the terrain. Middle-drag = orbit, " +
                 "shift+middle = pan, scroll = zoom, WASD = pan. Sculpt is " +
                 "left-drag, so they don't conflict. Off = manage the camera yourself.")]
        public bool AutoCameraControl = true;

        [Header("Live tuning")]
        [Tooltip("On Start, stand up a TuningServer + TerrainTuningSetup if the " +
                 "scene has none, so the React tuning panel can adjust the " +
                 "terrain live (ws://localhost:8787). Off = no tuning server.")]
        public bool AutoTuning = true;

        [Header("Ground grid")]
        [Tooltip("On Start, create a GroundGrid (same as the network designer) " +
                 "on its own GameObject at origin, sized to the terrain.")]
        public bool AutoGrid = true;
        [Tooltip("Grid line spacing (m) for the auto-created ground grid.")]
        public float GridSpacing = 50f;

        void Start()
        {
            if (PickCamera == null) PickCamera = Camera.main;
            if (PickCamera == null) PickCamera = FindFirstObjectByType<Camera>();

            // Establish the field first (load or create) so dimensions are known
            // before the scene services size themselves to the terrain.
            if (Autosave) _field = TryLoadTerrain();
            if (_field == null)
            {
                EnsureField(forceRebuild: true); // fresh field (+ test hill)
            }
            else
            {
                // Adopt loaded dimensions; refresh Origin to the current
                // GameObject placement so sculpt mapping stays correct even if
                // the object moved between sessions.
                ColumnsX = _field.ColumnsX;
                RowsZ = _field.RowsZ;
                CellSize = _field.CellSize;
                float halfW = (ColumnsX - 1) * CellSize * 0.5f;
                float halfL = (RowsZ - 1) * CellSize * 0.5f;
                _field.Origin = transform.position - new Vector3(halfW, 0f, halfL);
            }
            RebuildMesh();
            RebuildContours();

            // Stand up scene services, sized to the actual terrain.
            if (AutoLighting) EnsureAmbiance();
            if (AutoCameraControl) EnsureCameraControl();
            if (AutoGrid) EnsureGrid();
            if (AutoTuning) EnsureTuning(); // after the grid so its tunables register
        }

        // Create a GroundGrid (reusing the network designer's component) on its
        // OWN GameObject at origin — never parented to the terrain (see the
        // GroundGrid placement rule) — sized to span the terrain footprint.
        void EnsureGrid()
        {
            if (FindFirstObjectByType<GroundGrid>() == null)
            {
                GroundGrid grid = new GameObject("GroundGrid").AddComponent<GroundGrid>();
                grid.transform.position = Vector3.zero;
                grid.Spacing = Mathf.Max(0.1f, GridSpacing);
                grid.MajorEvery = 10;
            }
            ResizeGridToTerrain();
        }

        // Match the ground grid's half-extent to the terrain footprint.
        void ResizeGridToTerrain()
        {
            GroundGrid grid = FindFirstObjectByType<GroundGrid>();
            if (grid == null) return;
            float span = _field != null ? Mathf.Max(_field.WidthX, _field.LengthZ) : 2000f;
            grid.Extent = Mathf.Max(span, 100f) * 0.5f; // half-extent
            grid.Rebuild();
        }

        // Stand up the live-tuning endpoint (TuningServer + registration) if
        // the scene has none, so the React panel can tune the terrain.
        void EnsureTuning()
        {
            if (FindFirstObjectByType<TerrainTuningSetup>() != null) return;
            TerrainTuningSetup setup = new GameObject("TerrainTuning")
                .AddComponent<TerrainTuningSetup>(); // RequireComponent adds TuningServer
            setup.Terrain = this;
        }

        // If the (empty) scene has no SceneAmbiance, create one configured to
        // light itself — sun + soft shadows + ambient fill + URP shadow range.
        void EnsureAmbiance()
        {
            if (FindFirstObjectByType<SceneAmbiance>() != null) return;
            SceneAmbiance amb = new GameObject("SceneAmbiance").AddComponent<SceneAmbiance>();
            amb.CreateSunIfMissing = true;
            amb.ManageAmbient = true;
            amb.ShadowDistance = ShadowDistance;
            amb.Apply();
        }

        // If the pick camera has no orbit controller, add one framed on the
        // terrain. Left alone if one already exists (respect manual setup).
        void EnsureCameraControl()
        {
            // Prefer the assigned camera, then the tagged main, then ANY camera
            // (an untagged camera is the common scene-setup footgun), and as a
            // last resort create one so even a bare scene is usable.
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) cam = FindFirstObjectByType<Camera>();
            if (cam == null)
            {
                GameObject camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = camGo.AddComponent<Camera>();
            }
            PickCamera = cam; // sculpt raycast uses the same camera

            if (cam.GetComponent<OrbitCameraController>() == null)
            {
                OrbitCameraController orbit = cam.gameObject.AddComponent<OrbitCameraController>();
                orbit.Target = transform.position; // terrain centre
                float span = Mathf.Max((Mathf.Max(2, ColumnsX) - 1) * CellSize,
                                       (Mathf.Max(2, RowsZ) - 1) * CellSize);
                orbit.DistanceTarget = span * 1.2f; // frame the whole footprint
                orbit.Distance = orbit.DistanceTarget;
                orbit.Pitch = 45f;
            }
        }

        void Update()
        {
            // Brush-mode hotkeys.
            if (Input.GetKeyDown(KeyCode.Alpha1)) Brush = BrushMode.Raise;
            else if (Input.GetKeyDown(KeyCode.Alpha2)) Brush = BrushMode.Lower;
            else if (Input.GetKeyDown(KeyCode.Alpha3)) Brush = BrushMode.Smooth;
            else if (Input.GetKeyDown(KeyCode.Alpha4)) Brush = BrushMode.Flatten;

            // Brush resize: numpad + / - (held = continuous).
            if (Input.GetKey(KeyCode.KeypadPlus)) BrushRadius += BrushResizeRate * Time.deltaTime;
            if (Input.GetKey(KeyCode.KeypadMinus)) BrushRadius -= BrushResizeRate * Time.deltaTime;
            BrushRadius = Mathf.Clamp(BrushRadius, 0.5f, MaxBrushRadius);

            if (_field == null) return;

            // Debounced autosave: write once sculpting has paused.
            if (Autosave && _dirtySince >= 0f
                && Time.realtimeSinceStartup - _dirtySince >= AutosaveDebounceSeconds)
            {
                SaveTerrain();
                _dirtySince = -1f;
            }

            if (Input.GetMouseButtonDown(0)) _hasFlattenTarget = false;

            // One hover raycast per frame, shared by the brush cursor and the
            // sculpt itself.
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            bool overTerrain = false;
            RaycastHit hit = default;
            if (cam != null && _collider != null)
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                overTerrain = _collider.Raycast(ray, out hit, 100000f);
            }

            UpdateBrushCursor(ShowBrushCursor && overTerrain, hit.point);

            // Refresh contours when a stroke ends (cheap path); live rebuild
            // during the drag is opt-in via LiveContours.
            if (Input.GetMouseButtonUp(0)) RebuildContours();

            if (!overTerrain || !Input.GetMouseButton(0)) return;

            if (!_hasFlattenTarget)
            {
                GridFromWorld(hit.point, out float cfx, out float cfz);
                _flattenTarget = HeightAtGrid(cfx, cfz);
                _hasFlattenTarget = true;
            }

            ApplyBrush(hit.point, Time.deltaTime);
            RebuildMesh();
            _dirtySince = Time.realtimeSinceStartup;
            if (LiveContours) RebuildContours();
        }

        // World hit -> fractional grid coords, through the GameObject transform
        // so it's correct under any position/rotation/scale. The mesh is built
        // centered-local, so local (0,0) is the grid centre.
        void GridFromWorld(Vector3 worldHit, out float fx, out float fz)
        {
            float cs = _field.CellSize;
            float halfW = (_field.ColumnsX - 1) * cs * 0.5f;
            float halfL = (_field.RowsZ - 1) * cs * 0.5f;
            Vector3 local = transform.InverseTransformPoint(worldHit);
            fx = (local.x + halfW) / cs;
            fz = (local.z + halfL) / cs;
        }

        // Bilinear height (field offset space) at fractional grid coords.
        float HeightAtGrid(float fx, float fz)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, _field.ColumnsX - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, _field.RowsZ - 1);
            int x1 = Mathf.Min(x0 + 1, _field.ColumnsX - 1);
            int z1 = Mathf.Min(z0 + 1, _field.RowsZ - 1);
            float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
            float h0 = Mathf.Lerp(_field.GetHeight(x0, z0), _field.GetHeight(x1, z0), tx);
            float h1 = Mathf.Lerp(_field.GetHeight(x0, z1), _field.GetHeight(x1, z1), tx);
            return Mathf.Lerp(h0, h1, tz);
        }

        // A ring at the hovered point showing the brush footprint, conforming
        // to the terrain surface (each point sampled via HeightAtGrid) and
        // transform-correct (built in local space, then TransformPoint'd).
        // Rendered as a line mesh (like the contours) so it can be dashed.
        void UpdateBrushCursor(bool visible, Vector3 worldCenter)
        {
            EnsureCursor();
            _cursorMr.enabled = visible;
            if (!visible) return;
            if (_cursorMat != null) _cursorMat.color = BrushCursorColor;

            int n = Mathf.Max(8, BrushCursorSegments);
            float cs = _field.CellSize;
            float halfW = (_field.ColumnsX - 1) * cs * 0.5f;
            float halfL = (_field.RowsZ - 1) * cs * 0.5f;
            Vector3 localCenter = transform.InverseTransformPoint(worldCenter);

            // Conforming ring points (world space).
            _ring.Clear();
            for (int i = 0; i < n; i++)
            {
                float ang = (i / (float)n) * Mathf.PI * 2f;
                float lx = localCenter.x + Mathf.Cos(ang) * BrushRadius;
                float lz = localCenter.z + Mathf.Sin(ang) * BrushRadius;
                float ly = HeightAtGrid((lx + halfW) / cs, (lz + halfL) / cs) + BrushCursorLift;
                _ring.Add(transform.TransformPoint(new Vector3(lx, ly, lz)));
            }

            // Build the closed-loop line mesh, optionally dashed.
            _cursorVerts.Clear();
            _cursorIdx.Clear();
            float dash = BrushCursorDashed ? BrushCursorDashLength : 0f;
            for (int i = 0; i < n; i++)
                EmitCursorSegment(_ring[i], _ring[(i + 1) % n], dash, BrushCursorDashGap);

            _cursorMesh.Clear();
            _cursorMesh.SetVertices(_cursorVerts);
            _cursorMesh.SetIndices(_cursorIdx, MeshTopology.Lines, 0);
            _cursorMesh.RecalculateBounds();
            _cursorMf.sharedMesh = _cursorMesh;
        }

        // Like TerrainContourBuilder.EmitSegment: one line a->b, or dash/gap
        // pieces. Phase restarts per ring edge — even spacing on a regular ring.
        void EmitCursorSegment(Vector3 a, Vector3 b, float dash, float gap)
        {
            if (dash <= 0f)
            {
                int s = _cursorVerts.Count;
                _cursorVerts.Add(a); _cursorVerts.Add(b);
                _cursorIdx.Add(s); _cursorIdx.Add(s + 1);
                return;
            }
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-5f) return;
            Vector3 dir = d / len;
            float period = dash + Mathf.Max(0f, gap);
            for (float pos = 0f; pos < len; pos += period)
            {
                float e0 = pos, e1 = Mathf.Min(pos + dash, len);
                int s = _cursorVerts.Count;
                _cursorVerts.Add(a + dir * e0); _cursorVerts.Add(a + dir * e1);
                _cursorIdx.Add(s); _cursorIdx.Add(s + 1);
            }
        }

        void EnsureCursor()
        {
            if (_cursorMf != null) return;
            GameObject go = new GameObject("BrushCursor");
            go.transform.SetParent(transform, worldPositionStays: false);
            _cursorMf = go.AddComponent<MeshFilter>();
            _cursorMr = go.AddComponent<MeshRenderer>();
            _cursorMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cursorMr.receiveShadows = false;
            _cursorMesh = new Mesh { name = "BrushCursorMesh" };
            _cursorMf.sharedMesh = _cursorMesh;
            _cursorMat = PipelineMaterials.CreateUnlitColor(BrushCursorColor, "BrushCursorMat");
            _cursorMr.sharedMaterial = _cursorMat;
        }

        // Rebuild the topographic contour lines from the current field.
        [ContextMenu("Rebuild Contours")]
        public void RebuildContours()
        {
            EnsureContours();
            if (!ShowContours || ContourInterval <= 0f)
            {
                _contourMr.enabled = false;
                return;
            }
            _contourMr.enabled = true;
            if (_contourMat != null) _contourMat.color = ContourColor;
            TerrainContourBuilder.Build(_field, ContourInterval, ContourLift,
                ContourDashed ? ContourDashLength : 0f, ContourDashGap, _contourMesh);
            _contourMf.sharedMesh = _contourMesh;
        }

        void EnsureContours()
        {
            if (_contourMf != null) return;
            GameObject go = new GameObject("ContourLines");
            go.transform.SetParent(transform, worldPositionStays: false);
            _contourMf = go.AddComponent<MeshFilter>();
            _contourMr = go.AddComponent<MeshRenderer>();
            _contourMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _contourMr.receiveShadows = false;
            _contourMesh = new Mesh { name = "TerrainContours" };
            _contourMf.sharedMesh = _contourMesh;
            _contourMat = PipelineMaterials.CreateUnlitColor(ContourColor, "ContourMat");
            _contourMr.sharedMaterial = _contourMat;
        }

        // Modify the heightfield under the brush, in field (height-offset) space.
        void ApplyBrush(Vector3 worldHit, float dt)
        {
            float cs = _field.CellSize;
            // Map the world hit to grid space through the GameObject transform
            // (handles any position/rotation/scale), NOT world-Origin algebra.
            GridFromWorld(worldHit, out float fx, out float fz);
            int cx0 = Mathf.RoundToInt(fx);
            int cz0 = Mathf.RoundToInt(fz);
            int rad = Mathf.Max(1, Mathf.CeilToInt(BrushRadius / cs));

            for (int dz = -rad; dz <= rad; dz++)
            {
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int x = cx0 + dx, z = cz0 + dz;
                    if (!_field.InRange(x, z)) continue;

                    // Metres from the brush centre (use the float centre).
                    float mx = (x - fx) * cs, mz = (z - fz) * cs;
                    float dist = Mathf.Sqrt(mx * mx + mz * mz);
                    if (dist > BrushRadius) continue;

                    float tEdge = BrushRadius > 0f ? dist / BrushRadius : 0f;
                    // BrushFalloff blends between a flat (hard) profile and a
                    // smoothstep that eases to zero at the rim.
                    float soft = 1f - Mathf.SmoothStep(0f, 1f, tEdge);
                    float w = Mathf.Lerp(1f - tEdge, soft, BrushFalloff);

                    float h = _field.GetHeight(x, z);
                    switch (Brush)
                    {
                        case BrushMode.Raise:
                            h += BrushStrength * dt * w;
                            break;
                        case BrushMode.Lower:
                            h -= BrushStrength * dt * w;
                            break;
                        case BrushMode.Flatten:
                            h = Mathf.Lerp(h, _flattenTarget, Mathf.Clamp01(dt * 4f * w));
                            break;
                        case BrushMode.Smooth:
                            h = Mathf.Lerp(h, NeighborAverage(x, z), Mathf.Clamp01(dt * 4f * w));
                            break;
                    }
                    _field.SetHeight(x, z, h);
                }
            }
        }

        // Average of the in-range 4-neighbours (falls back to self at edges).
        float NeighborAverage(int x, int z)
        {
            float sum = 0f; int n = 0;
            if (_field.InRange(x - 1, z)) { sum += _field.GetHeight(x - 1, z); n++; }
            if (_field.InRange(x + 1, z)) { sum += _field.GetHeight(x + 1, z); n++; }
            if (_field.InRange(x, z - 1)) { sum += _field.GetHeight(x, z - 1); n++; }
            if (_field.InRange(x, z + 1)) { sum += _field.GetHeight(x, z + 1); n++; }
            return n > 0 ? sum / n : _field.GetHeight(x, z);
        }

        // (Re)create the field. Stamps the test hill only on a fresh field.
        void EnsureField(bool forceRebuild)
        {
            int cx = Mathf.Max(2, ColumnsX);
            int rz = Mathf.Max(2, RowsZ);
            float cs = Mathf.Max(0.01f, CellSize);
            float halfW = (cx - 1) * cs * 0.5f;
            float halfL = (rz - 1) * cs * 0.5f;
            Vector3 origin = transform.position - new Vector3(halfW, 0f, halfL);

            bool fresh = _field == null || _field.ColumnsX != cx || _field.RowsZ != rz;
            if (fresh || forceRebuild)
            {
                if (fresh)
                {
                    _field = new TerrainField(cx, rz, cs, origin);
                    if (TestHill) StampTestHill();
                }
                else
                {
                    _field.CellSize = cs;
                    _field.Origin = origin;
                }
            }
        }

        // Full reset: new flat field (+ optional test hill) and rebuild.
        [ContextMenu("Reset Terrain")]
        public void ResetTerrain()
        {
            _field = null;
            EnsureField(forceRebuild: true);
            RebuildMesh();
            RebuildContours();
            ResizeGridToTerrain(); // keep the grid matched to the new size
            _dirtySince = Time.realtimeSinceStartup; // persist the reset
        }

        // Zero all heights in place (keeps the current grid size). Reliable
        // flat slate regardless of the test-hill / size settings.
        [ContextMenu("Flatten Terrain")]
        public void FlattenTerrain()
        {
            if (_field == null) EnsureField(forceRebuild: true);
            System.Array.Clear(_field.Heights, 0, _field.Heights.Length);
            RebuildMesh();
            RebuildContours();
            ResizeGridToTerrain();
            _dirtySince = Time.realtimeSinceStartup;
        }

        // Rebuild the render mesh + collider from the current field. Cheap
        // enough to call every drag frame at MVP grid sizes.
        public void RebuildMesh()
        {
            if (_field == null) EnsureField(forceRebuild: true);

            if (_mesh == null) _mesh = new Mesh { name = "TerrainMesh" };
            TerrainMeshBuilder.Build(_field, _mesh);
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            if (_mat == null)
                _mat = PipelineMaterials.CreateLit(TerrainColor, Smoothness, "TerrainMat");
            else
                _mat.color = TerrainColor;
            GetComponent<MeshRenderer>().sharedMaterial = _mat;

            if (_collider == null)
            {
                _collider = GetComponent<MeshCollider>();
                if (_collider == null) _collider = gameObject.AddComponent<MeshCollider>();
            }
            _collider.sharedMesh = null;
            _collider.sharedMesh = _mesh;
        }

        void StampTestHill()
        {
            int cx = _field.ColumnsX, rz = _field.RowsZ;
            float cxh = (cx - 1) * 0.5f, rzh = (rz - 1) * 0.5f;
            float sigma = Mathf.Max(1f, Mathf.Min(cx, rz) * 0.18f);
            float twoSigSq = 2f * sigma * sigma;
            for (int z = 0; z < rz; z++)
            {
                for (int x = 0; x < cx; x++)
                {
                    float dx = x - cxh, dz = z - rzh;
                    float g = Mathf.Exp(-(dx * dx + dz * dz) / twoSigSq);
                    _field.SetHeight(x, z, g * TestHillHeight);
                }
            }
        }

        // -----------------------------------------------------------------
        // Save / load (JSON, mirrors the road designer's autosave)
        // -----------------------------------------------------------------

        void OnDisable()
        {
            // Flush any pending edits when Play stops / the object is disabled.
            if (Autosave && _dirtySince >= 0f)
            {
                SaveTerrain();
                _dirtySince = -1f;
            }
        }

        string ResolveAutosavePath()
        {
            if (!string.IsNullOrEmpty(AutosavePath)) return AutosavePath;
#if UNITY_EDITOR
            return System.IO.Path.Combine(Application.dataPath, "..", "TerrainAutosave.json");
#else
            return System.IO.Path.Combine(Application.persistentDataPath, "TerrainAutosave.json");
#endif
        }

        public void SaveTerrain()
        {
            if (_field == null) return;
            try
            {
                // Sparse: store only altered (non-zero) heights; zeros implied.
                float[] heights = _field.Heights;
                var idx = new List<int>();
                var hs = new List<float>();
                for (int i = 0; i < heights.Length; i++)
                {
                    if (Mathf.Abs(heights[i]) > 1e-4f) { idx.Add(i); hs.Add(heights[i]); }
                }
                TerrainSave save = new TerrainSave
                {
                    ColumnsX = _field.ColumnsX,
                    RowsZ = _field.RowsZ,
                    CellSize = _field.CellSize,
                    Idx = idx.ToArray(),
                    H = hs.ToArray(),
                };
                string json = JsonConvert.SerializeObject(save, TerrainJsonSettings);
                System.IO.File.WriteAllText(ResolveAutosavePath(), json);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Save failed: {ex.Message}");
            }
        }

        TerrainField TryLoadTerrain()
        {
            try
            {
                string path = ResolveAutosavePath();
                if (!System.IO.File.Exists(path)) return null;
                TerrainSave save = JsonConvert.DeserializeObject<TerrainSave>(
                    System.IO.File.ReadAllText(path), TerrainJsonSettings);
                if (save == null || save.ColumnsX < 2 || save.RowsZ < 2) return null;

                float cs = save.CellSize > 0f ? save.CellSize : 1f;
                TerrainField f = new TerrainField(save.ColumnsX, save.RowsZ, cs, Vector3.zero);
                if (save.Idx != null && save.H != null)
                {
                    int n = Mathf.Min(save.Idx.Length, save.H.Length);
                    for (int k = 0; k < n; k++)
                    {
                        int i = save.Idx[k];
                        if (i >= 0 && i < f.Heights.Length) f.Heights[i] = save.H[k];
                    }
                }
                return f;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Load failed: {ex.Message} — starting fresh.");
                return null;
            }
        }

        static JsonSerializerSettings _terrainJsonSettings;
        static JsonSerializerSettings TerrainJsonSettings
        {
            get
            {
                if (_terrainJsonSettings == null)
                    _terrainJsonSettings = new JsonSerializerSettings
                    {
                        Formatting = Formatting.Indented,
                        Converters = new List<JsonConverter> { new Vector3JsonConverter() },
                        NullValueHandling = NullValueHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                    };
                return _terrainJsonSettings;
            }
        }

        // Vector3 as { x, y, z } — keeps Newtonsoft from chasing the derived
        // properties (normalized/magnitude) on UnityEngine.Vector3.
        class Vector3JsonConverter : JsonConverter<Vector3>
        {
            public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("x"); writer.WriteValue(value.x);
                writer.WritePropertyName("y"); writer.WriteValue(value.y);
                writer.WritePropertyName("z"); writer.WriteValue(value.z);
                writer.WriteEndObject();
            }

            public override Vector3 ReadJson(JsonReader reader, System.Type objectType,
                Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                Newtonsoft.Json.Linq.JObject jo = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new Vector3(
                    jo["x"]?.ToObject<float>() ?? 0f,
                    jo["y"]?.ToObject<float>() ?? 0f,
                    jo["z"]?.ToObject<float>() ?? 0f);
            }
        }
    }
}
