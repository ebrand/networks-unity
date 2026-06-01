// Terrain designer — drives a Unity Terrain (heightmap) with sculpt brushes.
//
// The Terrain (assigned to `Surface`, or auto-found) does the rendering, LOD
// and collision — so it scales to 1 m over 2 km (~4M cells) where a single
// mesh + MeshCollider could not. `TerrainField` stays the working heightfield
// (source of truth for contours / cursor / save); sculpting edits it and then
// pushes only the brush-affected region into the heightmap (SetHeightsDelayLOD).
// Coordinates are corner-anchored (the Terrain's world position = field Origin).
//
// Sculpting runs in Play mode: hold the left mouse button over the terrain and
// drag. Brush mode: 1=Raise, 2=Lower, 3=Smooth, 4=Flatten.
//
// LIMITS (this slice): heights clamp to [0, terrain height] — no digging below
// the floor yet; contours are skipped on the full-res heightmap (region-based
// contours are a later pass). The "test hill" stamps once on a fresh field.

using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using NetworkDesigner.Designer; // SceneAmbiance

namespace NetworkDesigner.Terrain
{
    public class TerrainDesigner : MonoBehaviour
    {
        public enum BrushMode { Raise, Lower, Smooth, Flatten }

        [Header("Unity Terrain surface")]
        [Tooltip("The Unity Terrain to drive. Create one in the editor (Terrain " +
                 "Settings: heightmap resolution e.g. 2049, size e.g. 2000x2000x600, " +
                 "positioned at -W/2,0,-L/2 to center on origin) and assign it here.")]
        public UnityEngine.Terrain Surface;

        // Heightfield dimensions are derived from Surface in EnsureField:
        // ColumnsX = RowsZ = heightmap resolution, CellSize = size / (res-1).
        [HideInInspector] public int ColumnsX = 2049;
        [HideInInspector] public int RowsZ = 2049;
        [HideInInspector] public float CellSize = 2000f / 2048f;

        [Header("Ground layers (grass + slope rock)")]
        [Tooltip("On Start, if the Terrain has no layers, build a grass layer " +
                 "(plus a rock layer blended by slope when rock textures are " +
                 "assigned). Editor-assigned layers are kept.")]
        public bool AutoGrass = true;
        [Tooltip("Grass albedo (e.g. grass/grassy-meadow1_albedo).")]
        public Texture2D GrassAlbedo;
        [Tooltip("Grass normal map (import as Normal map).")]
        public Texture2D GrassNormal;
        [Tooltip("Grass tile size in metres.")]
        public float GrassTileSize = 30f;

        [Tooltip("Optional rock/dirt albedo — blended in on steeper slopes.")]
        public Texture2D RockAlbedo;
        [Tooltip("Rock normal map (import as Normal map).")]
        public Texture2D RockNormal;
        [Tooltip("Rock tile size in metres.")]
        public float RockTileSize = 20f;
        [Tooltip("Slope (degrees) at/below which it's all grass.")]
        [Range(0f, 90f)] public float SlopeGrassMaxAngle = 25f;
        [Tooltip("Slope (degrees) at/above which it's all rock.")]
        [Range(0f, 90f)] public float SlopeRockMinAngle = 45f;

        [Header("Sculpt brush")]
        public BrushMode Brush = BrushMode.Raise;
        [Tooltip("Brush radius in metres. Resize live with [ (smaller) and ] (larger).")]
        public float BrushRadius = 10f;
        [Tooltip("Brush resize speed (metres/second, while [ or ] is held).")]
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

        void Start()
        {
            if (PickCamera == null) PickCamera = Camera.main;
            if (PickCamera == null) PickCamera = FindFirstObjectByType<Camera>();

            if (Surface == null) Surface = FindFirstObjectByType<UnityEngine.Terrain>();
            if (Surface == null)
            {
                Debug.LogError("[TerrainDesigner] No 'Surface' Terrain assigned or found. " +
                    "Create a Unity Terrain (resolution e.g. 2049, size 2000x2000x600, " +
                    "centered at -1000,0,-1000) and assign it to Surface.");
                enabled = false;
                return;
            }

            // Field dimensions come from the Terrain's heightmap.
            EnsureField(forceRebuild: true);

            // Adopt a saved heightfield only if it matches the terrain resolution.
            if (Autosave)
            {
                TerrainField loaded = TryLoadTerrain();
                if (loaded != null && loaded.ColumnsX == _field.ColumnsX
                                   && loaded.RowsZ == _field.RowsZ)
                {
                    loaded.Origin = _field.Origin;
                    _field = loaded;
                }
            }

            SyncTerrainFull();
            RebuildContours();
            if (AutoGrass) EnsureGroundLayers();

            // Stand up scene services, sized to the actual terrain.
            if (AutoLighting) EnsureAmbiance();
            if (AutoCameraControl) EnsureCameraControl();
            if (AutoTuning) EnsureTuning();
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

        // Build the ground Terrain Layers (grass, + rock when assigned) and the
        // slope-blended splatmap — only if the Terrain has no layers yet, so an
        // editor-painted setup is respected.
        void EnsureGroundLayers()
        {
            if (Surface == null || GrassAlbedo == null) return;
            TerrainData td = Surface.terrainData;
            if (td.terrainLayers == null || td.terrainLayers.Length == 0)
            {
                var layers = new List<TerrainLayer>
                {
                    new TerrainLayer
                    {
                        name = "GrassLayer",
                        diffuseTexture = GrassAlbedo,
                        normalMapTexture = GrassNormal,
                        tileSize = new Vector2(GrassTileSize, GrassTileSize),
                    },
                };
                if (RockAlbedo != null)
                {
                    layers.Add(new TerrainLayer
                    {
                        name = "RockLayer",
                        diffuseTexture = RockAlbedo,
                        normalMapTexture = RockNormal,
                        tileSize = new Vector2(RockTileSize, RockTileSize),
                    });
                }
                td.terrainLayers = layers.ToArray();
            }
            RebuildSplatFull();
        }

        // Recompute the whole splatmap: grass on flat ground, rock on steep
        // slopes (smooth blend between the two angle thresholds). Cheap-ish —
        // alphamap res is well below the heightmap res. Used on start/reset.
        public void RebuildSplatFull()
        {
            if (Surface == null) return;
            TerrainData td = Surface.terrainData;
            int layers = td.terrainLayers != null ? td.terrainLayers.Length : 0;
            if (layers == 0) return;
            int ar = td.alphamapResolution;
            float[,,] maps = new float[ar, ar, layers];
            for (int z = 0; z < ar; z++)
                for (int x = 0; x < ar; x++)
                    WriteSplat(td, maps, z, x, x, z, ar, layers);
            td.SetAlphamaps(0, 0, maps);
        }

        // Reblend the splatmap only where a sculpt changed the slope. Maps a
        // heightmap-cell region to alphamap cells (alphamap is lower-res).
        void PushSplatRegion(int hx0, int hz0, int hw, int hh)
        {
            if (Surface == null || _field == null) return;
            TerrainData td = Surface.terrainData;
            int layers = td.terrainLayers != null ? td.terrainLayers.Length : 0;
            if (layers < 2) return; // single grass layer — nothing to reblend
            int ar = td.alphamapResolution;
            float s = ar / (float)(_field.ColumnsX - 1); // alphamap cells per heightmap cell
            int ax0 = Mathf.Clamp(Mathf.FloorToInt(hx0 * s) - 1, 0, ar - 1);
            int az0 = Mathf.Clamp(Mathf.FloorToInt(hz0 * s) - 1, 0, ar - 1);
            int ax1 = Mathf.Clamp(Mathf.CeilToInt((hx0 + hw) * s) + 1, 0, ar - 1);
            int az1 = Mathf.Clamp(Mathf.CeilToInt((hz0 + hh) * s) + 1, 0, ar - 1);
            int aw = ax1 - ax0 + 1, ah = az1 - az0 + 1;
            float[,,] maps = new float[ah, aw, layers];
            for (int z = 0; z < ah; z++)
                for (int x = 0; x < aw; x++)
                    WriteSplat(td, maps, z, x, ax0 + x, az0 + z, ar, layers);
            td.SetAlphamaps(ax0, az0, maps);
        }

        // Slope-based blend for global alphamap cell (gx,gz); writes into the
        // (possibly offset) local map cell [lz,lx]. Layer 0 = grass, 1 = rock.
        void WriteSplat(TerrainData td, float[,,] maps, int lz, int lx, int gx, int gz, int ar, int layers)
        {
            if (layers < 2) { maps[lz, lx, 0] = 1f; return; }
            float slope = td.GetSteepness((gx + 0.5f) / ar, (gz + 0.5f) / ar); // degrees
            float rock = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(SlopeGrassMaxAngle, SlopeRockMinAngle, slope));
            maps[lz, lx, 0] = 1f - rock; // grass
            maps[lz, lx, 1] = rock;      // rock
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

            // Brush resize: ] bigger, [ smaller (held = continuous).
            if (Input.GetKey(KeyCode.RightBracket)) BrushRadius += BrushResizeRate * Time.deltaTime;
            if (Input.GetKey(KeyCode.LeftBracket)) BrushRadius -= BrushResizeRate * Time.deltaTime;
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

            // One hover raycast per frame (against the TerrainCollider), shared
            // by the brush cursor and the sculpt itself.
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            bool overTerrain = false;
            RaycastHit hit = default;
            if (cam != null)
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                overTerrain = Physics.Raycast(ray, out hit, 100000f)
                              && hit.collider is TerrainCollider;
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

            // Sculpt the field, then push ONLY the brush-affected heightmap
            // region to the Terrain (cheap — never the whole 4M-cell map).
            ApplyBrush(hit.point, Time.deltaTime);
            GridFromWorld(hit.point, out float bfx, out float bfz);
            int rad = Mathf.CeilToInt(BrushRadius / Mathf.Max(0.01f, _field.CellSize)) + 1;
            int rx0 = Mathf.RoundToInt(bfx) - rad, rz0 = Mathf.RoundToInt(bfz) - rad;
            int rw = rad * 2 + 1;
            PushRegionToTerrain(rx0, rz0, rw, rw);  // heights (incl. SyncHeightmap)
            PushSplatRegion(rx0, rz0, rw, rw);      // reblend grass/rock by new slope
            _dirtySince = Time.realtimeSinceStartup;
            if (LiveContours) RebuildContours();
        }

        // World hit -> fractional grid coords, relative to the terrain corner
        // (Origin). Unity Terrain is axis-aligned and corner-anchored.
        void GridFromWorld(Vector3 worldHit, out float fx, out float fz)
        {
            float cs = _field.CellSize;
            fx = (worldHit.x - _field.Origin.x) / cs;
            fz = (worldHit.z - _field.Origin.z) / cs;
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

            // Conforming ring points in world space; the cursor mesh object
            // lives at world identity so these render as-is.
            _ring.Clear();
            for (int i = 0; i < n; i++)
            {
                float ang = (i / (float)n) * Mathf.PI * 2f;
                float wx = worldCenter.x + Mathf.Cos(ang) * BrushRadius;
                float wz = worldCenter.z + Mathf.Sin(ang) * BrushRadius;
                float wy = _field.SampleHeight(wx, wz) + BrushCursorLift;
                _ring.Add(new Vector3(wx, wy, wz));
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
            // Root object at world identity — the ring verts are world-space.
            GameObject go = new GameObject("BrushCursor");
            _cursorMf = go.AddComponent<MeshFilter>();
            _cursorMr = go.AddComponent<MeshRenderer>();
            _cursorMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cursorMr.receiveShadows = false;
            _cursorMesh = new Mesh { name = "BrushCursorMesh" };
            _cursorMf.sharedMesh = _cursorMesh;
            // Always-on-top so the ring isn't occluded by terrain relief.
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            _cursorMat = sh != null
                ? new Material(sh) { name = "BrushCursorMat", color = BrushCursorColor }
                : PipelineMaterials.CreateUnlitColor(BrushCursorColor, "BrushCursorMat");
            _cursorMr.sharedMaterial = _cursorMat;
        }

        // Rebuild the topographic contour lines from the current field.
        [ContextMenu("Rebuild Contours")]
        public void RebuildContours()
        {
            EnsureContours();
            // Contours over the full 2 km / 1 m heightmap (~4M cells) are far
            // too heavy to rebuild whole; that's a later region-based pass.
            // Skip above a cell budget for now.
            long cells = _field != null ? (long)_field.ColumnsX * _field.RowsZ : 0;
            if (_field == null || !ShowContours || ContourInterval <= 0f || cells > 300000)
            {
                if (_contourMr != null) _contourMr.enabled = false;
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

        // (Re)create the field to match the Terrain's heightmap. Origin is the
        // terrain's world corner; CellSize = terrain size / (resolution - 1).
        void EnsureField(bool forceRebuild)
        {
            int res = Surface != null ? Surface.terrainData.heightmapResolution : Mathf.Max(2, ColumnsX);
            float sizeX = Surface != null ? Surface.terrainData.size.x : (res - 1) * Mathf.Max(0.01f, CellSize);
            float cs = sizeX / Mathf.Max(1, res - 1);
            Vector3 origin = Surface != null
                ? Surface.transform.position
                : transform.position - new Vector3((res - 1) * cs * 0.5f, 0f, (res - 1) * cs * 0.5f);

            bool fresh = _field == null || _field.ColumnsX != res || _field.RowsZ != res;
            if (fresh)
            {
                _field = new TerrainField(res, res, cs, origin);
                if (TestHill) StampTestHill();
            }
            else if (forceRebuild)
            {
                _field.CellSize = cs;
                _field.Origin = origin;
            }

            // Keep the (hidden) public dims in sync for camera framing etc.
            ColumnsX = _field.ColumnsX;
            RowsZ = _field.RowsZ;
            CellSize = _field.CellSize;
        }

        // Full reset: new flat field (+ optional test hill) and rebuild.
        [ContextMenu("Reset Terrain")]
        public void ResetTerrain()
        {
            _field = null;
            EnsureField(forceRebuild: true);
            SyncTerrainFull();
            RebuildSplatFull(); // re-blend grass/rock for the new slopes
            RebuildContours();
            _dirtySince = Time.realtimeSinceStartup; // persist the reset
        }

        // Zero all heights in place (keeps the current grid size). Reliable
        // flat slate regardless of the test-hill / size settings.
        [ContextMenu("Flatten Terrain")]
        public void FlattenTerrain()
        {
            if (_field == null) EnsureField(forceRebuild: true);
            System.Array.Clear(_field.Heights, 0, _field.Heights.Length);
            SyncTerrainFull();
            RebuildSplatFull(); // flat now -> all grass
            RebuildContours();
            _dirtySince = Time.realtimeSinceStartup;
        }

        // Push the ENTIRE heightfield into the Terrain heightmap. Use sparingly
        // (Start / reset / flatten) — it's a ~res^2 upload. Sculpt uses the
        // region push instead.
        public void SyncTerrainFull()
        {
            if (Surface == null || _field == null) return;
            int res = _field.ColumnsX;
            float maxH = Mathf.Max(0.01f, Surface.terrainData.size.y);
            float[,] hm = new float[res, res]; // Unity heightmap is [z, x]
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    hm[z, x] = Mathf.Clamp01(_field.GetHeight(x, z) / maxH);
            Surface.terrainData.SetHeights(0, 0, hm);
        }

        // Push only a rectangular cell region [x0,z0]..(+w,+h) to the heightmap.
        // Cheap (brush-sized), so it runs every sculpt frame.
        void PushRegionToTerrain(int x0, int z0, int w, int h)
        {
            if (Surface == null || _field == null) return;
            int res = _field.ColumnsX;
            x0 = Mathf.Clamp(x0, 0, res - 1);
            z0 = Mathf.Clamp(z0, 0, res - 1);
            w = Mathf.Clamp(w, 1, res - x0);
            h = Mathf.Clamp(h, 1, res - z0);
            float maxH = Mathf.Max(0.01f, Surface.terrainData.size.y);
            float[,] region = new float[h, w]; // [z, x]
            for (int zr = 0; zr < h; zr++)
                for (int xr = 0; xr < w; xr++)
                    region[zr, xr] = Mathf.Clamp01(_field.GetHeight(x0 + xr, z0 + zr) / maxH);
            Surface.terrainData.SetHeightsDelayLOD(x0, z0, region);
            Surface.terrainData.SyncHeightmap(); // refresh mesh LOD + collider
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
