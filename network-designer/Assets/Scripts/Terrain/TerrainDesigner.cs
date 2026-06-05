// Terrain designer — LOW-POLY chunked flat-shaded mesh with sculpt brushes.
//
// `TerrainField` is the working heightfield. It's rendered as a grid of CHUNKS
// (each a flat-shaded mesh with un-shared verts -> per-face normals = visible
// facets) plus a MeshCollider per chunk. Sculpting edits the field, then
// rebuilds only the brush-touched chunks (mesh + collider) — so it stays
// interactive at 5 m over 2 km without rebuilding the whole ~1M-vert mesh.
// Coordinates are corner-anchored (field.Origin = the centered world corner).
// Single lit color for now; height/slope vertex-color bands are a follow-up.
//
// Sculpting runs in Play mode: hold left mouse over the terrain and drag.
// Brush: 1=Raise 2=Lower 3=Smooth 4=Flatten 5=Slope; [ / ] resize the brush.
//
// (Replaced an earlier Unity-Terrain heightmap renderer; we went low-poly,
// which is coarse enough that a custom flat-shaded mesh is the right tool.)

using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using NetworkDesigner.Designer; // SceneAmbiance
using NetworkDesigner.Model;    // PlacedTreeData

namespace NetworkDesigner.Terrain
{
    public class TerrainDesigner : MonoBehaviour
    {
        public enum BrushMode { Raise, Lower, Smooth, Flatten, Slope }

        [Header("Terrain (low-poly chunked mesh)")]
        [Tooltip("Terrain width/length in metres (square).")]
        public float TerrainSizeMeters = 2000f;
        [Tooltip("Metres between grid vertices = facet size. 5 m is low-poly + road-usable.")]
        public float CellSize = 5f;
        [Tooltip("Cells per chunk side. Chunks rebuild independently on sculpt; keep <= ~100.")]
        public int ChunkCells = 50;
        [Tooltip("Flat terrain color — the 'grass' band, used on flat/gentle faces.")]
        public Color TerrainColor = new Color(0.40f, 0.5f, 0.30f);
        [Tooltip("Color blended onto steep faces (the 'rock' band). Live; no rebuild.")]
        public Color RockColor = new Color(0.42f, 0.40f, 0.38f);
        [Tooltip("Slope angle (deg) where rock starts blending in. 0 = flat, 90 = vertical.")]
        public float SlopeStartDeg = 26f;
        [Tooltip("Slope angle (deg) at which a face is fully rock.")]
        public float SlopeFullDeg = 45f;
        [Tooltip("Optional low-poly rock texture, sampled triplanar (needs no UVs). Null = flat rock color.")]
        public Texture2D RockTexture;
        [Tooltip("Rock texture tiling (1/world-units). Lower = larger features.")]
        public float RockTextureScale = 0.12f;

        [Header("World grid overlay")]
        [Tooltip("Paint a world grid on the terrain surface (G toggles). Drapes perfectly — " +
                 "it's shaded on the surface, so it follows relief and never needs rebuilding.")]
        public bool GridEnabled = false;
        [Tooltip("Minor grid line spacing (m). 5 matches the cell size.")]
        public float GridSpacing = 5f;
        [Tooltip("Draw a brighter major line every N minor lines (e.g. 10 = every 50 m).")]
        public float GridMajorEvery = 10f;
        public Color GridColor = new Color(0.14f, 0.15f, 0.17f, 1f);
        [Range(0f, 1f)] public float GridStrength = 0.5f;
        [Tooltip("Grid line width in pixels (constant on screen at any distance).")]
        public float GridLineWidth = 1f;
        [Tooltip("Snap line/rail/fence node placement to grid intersections (Shift+G). " +
                 "Uses the grid spacing whether or not the grid is shown.")]
        public bool SnapToGrid = false;

        [Header("Rail cut carving (Carve Rail Approaches)")]
        [Tooltip("Half-width of the flat cut floor (m). Wider = more dug out.")]
        public float CutFloorHalfWidth = 4f;
        [Tooltip("Extra depth carved below the rail bed (m).")]
        public float CutDepthBelowBed = 0.5f;
        [Tooltip("Cut-wall rise per metre out (lower = wider, gentler cut).")]
        public float CutBatter = 0.6f;
        [Tooltip("Smoothing passes applied to the carved cut after digging (rounds the " +
                 "coarse-grid wall steps). The floor under the track is re-clamped so it " +
                 "can't re-bury the rails. 0 = off.")]
        public int CutSmoothPasses = 2;

        [Header("Water")]
        [Tooltip("Show a flat water surface; terrain below the water level reads as submerged.")]
        public bool ShowWater = true;
        [Tooltip("World height (Y) of the water surface. Raise it to flood low ground.")]
        public float WaterLevel = 5f;
        [Tooltip("Water colour (alpha < 1 = see-through to the bed below).")]
        public Color WaterColor = new Color(0.20f, 0.45f, 0.55f, 0.65f);
        [Range(0f, 1f)] public float WaterSmoothness = 0.7f;

        // Vertex counts, derived from TerrainSizeMeters / CellSize in EnsureField.
        [HideInInspector] public int ColumnsX = 401;
        [HideInInspector] public int RowsZ = 401;

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
        [Tooltip("Exponent on Strength for Raise/Lower: effective rate = Strength^exp. " +
                 "1 = linear (unchanged). >1 makes higher Strength values ramp up " +
                 "dramatically (e.g. exp 2 turns Strength 50 into 2500 m/s).")]
        [Range(1f, 4f)] public float BrushStrengthExponent = 1f;
        [Tooltip("0 = hard edge, 1 = soft (smoothstep) falloff to the rim.")]
        [Range(0f, 1f)] public float BrushFalloff = 0.7f;
        [Tooltip("Flatten convergence rate — how fast Flatten (4) pulls terrain to the " +
                 "target height. Higher = snappier / stronger.")]
        public float FlattenStrength = 10f;
        [Tooltip("Slope tool (5): grade % above which the live readout warns red — like " +
                 "the rail tool's grade limit. Does NOT block the slope; just flags it.")]
        public float SlopeMaxGradePct = 6f;
        [Tooltip("Camera used for the sculpt raycast. Defaults to Camera.main.")]
        public Camera PickCamera;

        [Header("Brush cursor (ring)")]
        public bool ShowBrushCursor = true;
        public Color BrushCursorColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        [Tooltip("Slope tool (5): translucent fill previewing the area that will be " +
                 "graded (follows the plan centreline around curves when snapped).")]
        public Color SlopeFillColor = new Color(0.3f, 0.95f, 0.35f, 0.28f);
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
        public bool ShowContours = false;
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
        [Tooltip("Re-settle trees/rocks/fences onto the surface every sculpt frame " +
                 "(raise/lower/smooth/flatten/slope), instead of only when the stroke " +
                 "ends. On = smoothest, but re-conforms ALL items each frame — turn " +
                 "off on a heavily-populated map if it hitches.")]
        public bool LiveConform = false;

        [Header("Scatter brushes  (T = trees, R = rocks)")]
        [Tooltip("Tree scatter layer. Press T to toggle tree mode: left-drag " +
                 "PAINTS, right-drag ERASES. Sculpt is disabled while on.")]
        public ScatterLayer TreeLayer = new ScatterLayer
        { Name = "Trees", Folder = "Assets/Trees", PaintRate = 25f, Spacing = 4f, ScaleRange = new Vector2(1.2f, 1.95f) };
        [Tooltip("Rock scatter layer. Press R to toggle rock mode (same controls).")]
        public ScatterLayer RockLayer = new ScatterLayer
        { Name = "Rocks", Folder = "Assets/Rocks", PaintRate = 15f, Spacing = 6f, ScaleRange = new Vector2(0.5f, 1.6f) };

        [Header("Linework  (F = fence, P = power line)")]
        [Tooltip("Fence linework layer. Press F to toggle: left-click adds a node " +
                 "and connects from the last (chain); right-click ends the chain. " +
                 "Edges auto-curve through the nodes; the Asset renders in series.")]
        public LineworkLayer FenceLayer = new LineworkLayer { Name = "Fence", Spacing = 3f, YawOffset = -90f };
        [Tooltip("Power-line layer (P). Assign a socketed POLE prefab as Asset and set " +
                 "ParentOverride to a PoleChain that carries a cable generator — the " +
                 "placed poles then auto-connect with cables. Use a wide Spacing (span).")]
        public LineworkLayer PowerLineLayer = new LineworkLayer { Name = "PowerLine", Spacing = 35f };
        [Tooltip("Rail-track layer (L). Procedural rails + ties generated along each " +
                 "edge — drawn like a fence; gauge/tie-spacing under the Rail tunables.")]
        public RailTrackLayer RailLayer = new RailTrackLayer { Name = "Rail" };
        public RailPlanLayer PlanLayer = new RailPlanLayer { Name = "Plan" };

        [Header("Initial relief (stamped once)")]
        [Tooltip("Stamp a smooth gaussian hill when the field is first built, " +
                 "so there's something to sculpt. Does NOT re-apply on rebuild.")]
        public bool TestHill = false;
        public float TestHillHeight = 80f;

        [Header("Heightmap import (context-menu 'Import Heightmap')")]
        [Tooltip("Grayscale heightmap file. Relative paths resolve to the project " +
                 "root in the Editor (persistentDataPath in a build). Any size — it's " +
                 "bilinear-sampled to the grid. Black = 0, white = Max height. " +
                 "REPLACES the current heights. Run in Play mode.")]
        public string HeightmapPath = "terrain1.png";
        [Tooltip("Folder the heightmap picker dropdown lists PNGs from (relative to the " +
                 "project root in the Editor). Drop grayscale PNGs here to choose them.")]
        public string HeightmapFolder = "Assets/Heightmaps";
        [Tooltip("Metres of elevation that pure white (1.0) maps to; black maps to 0.")]
        public float HeightmapMaxHeight = 250f;
        [Tooltip("Box-blur passes applied after import. Softens the 8-bit terracing " +
                 "(staircase steps on slopes) and single-pixel spikes. 0 = raw; " +
                 "2-4 looks natural; high values flatten real detail.")]
        [Range(0, 12)] public int HeightmapSmoothPasses = 3;

        [Header("Autosave (terrain persistence across Play stop/start)")]
        public bool Autosave = true;
        [Tooltip("Where the terrain is saved. Empty → project_root/TerrainAutosave.json " +
                 "in the Editor, persistentDataPath in a Player build.")]
        public string AutosavePath = "";
        [Tooltip("Seconds of no sculpting before the terrain is written to disk.")]
        public float AutosaveDebounceSeconds = 1f;

        TerrainField _field;
        float _dirtySince = -1f; // realtime when last edited; -1 = clean
        System.Threading.Tasks.Task _saveTask; // in-flight async autosave (serialize+write off-thread)
        // Camera pose staged from the autosave; applied in Start once the fly
        // camera exists (it's created after the load).
        bool _havePendingCam;
        Vector3 _pendingCamPos;
        float _pendingCamYaw, _pendingCamPitch;
        // Last sampled camera pose, to detect movement (which marks dirty so the
        // debounced autosave captures the new vantage, not just terrain edits).
        bool _haveLastCam;
        Vector3 _lastCamPos;
        float _lastCamYaw, _lastCamPitch;
        GameObject _chunkRoot;
        Mesh[] _chunkMesh;
        MeshCollider[] _chunkCol;
        Material _mat;
        int _chunksX, _chunksZ;
        ScatterLayer _active;      // scatter layer being painted (null = not scattering)
        ITerrainLineLayer _lineActive; // line layer being drawn (null = not drawing)
        MeshFilter _cursorMf;
        MeshRenderer _cursorMr;
        Mesh _cursorMesh;
        Material _cursorMat;
        readonly List<Vector3> _ring = new List<Vector3>();
        readonly List<Vector3> _cursorVerts = new List<Vector3>();
        readonly List<int> _cursorIdx = new List<int>();
        // Slope tool: translucent filled ribbon previewing the area to be graded
        // (follows the plan centreline around curves when snapped).
        MeshFilter _slopeFillMf;
        MeshRenderer _slopeFillMr;
        Mesh _slopeFillMesh;
        Material _slopeFillMat;
        readonly List<Vector3> _fillVerts = new List<Vector3>();
        readonly List<int> _fillIdx = new List<int>();
        MeshFilter _contourMf;
        MeshRenderer _contourMr;
        Mesh _contourMesh;
        Material _contourMat;
        bool _hasFlattenTarget;
        float _flattenTarget; // height (field space): captured on mouse-down, or picked via right-click
        bool _flattenTargetPicked; // true once right-click sampled a height to flatten TO
        bool _flattenCursorValid;  // hovering terrain in Flatten mode -> show the cursor HUD
        float _flattenCursorElev;  // world Y under the cursor, for the HUD
        bool _sculptedStroke;      // this drag changed terrain -> conform scatter/lines on mouse-up
        // Slope tool (brush 5): two-click ramp. A captured on the first click; the
        // corridor between A and the (optionally guide-snapped) end is graded on B.
        bool _slopeArmed;
        Vector3 _slopeA;          // world point A
        float _slopeElevA;        // field-space height captured at A
        bool _slopeHasGuide;      // A landed near rail -> _slopeGuideDir is the "straight"
        Vector2 _slopeGuideDir;   // network heading (XZ, unit) through A
        Vector3 _slopeEnd;        // this frame's end point (guide-snapped), world
        bool _slopeEndValid;
        float _slopeGradePct;     // live grade % for the readout
        List<Vector2> _slopePath; // this frame's plan-centreline path A->end (null = straight)
        readonly List<RailPlanLayer.EdgeGrade> _planGrades = new List<RailPlanLayer.EdgeGrade>();
        // Rail auto-slope (Alt+click node A, then node B): grade the rail bed between two
        // rail nodes to a constant ramp, if the result stays within the rail's max grade.
        Vector3 _lineCursorWorld;          // placement cursor (for the on-screen speed readout)
        bool _lineCursorValid;
        int _railSlopeNodeA = -1;          // armed A node (-1 = none)
        List<Vector2> _railSlopePath;      // this frame's preview path A -> hovered node
        float _railSlopeGradePct;          // preview grade %
        bool _railSlopeGradeOk;            // within the rail's max grade?
        int _railConnectNodeA = -1;        // armed connect end A (-1 = none)
        string _connectStatus;             // HUD line while connecting
        // Inverted so a 0/false deserialize = snapping ENABLED (preserves behavior
        // for an already-serialized scene); the tunable presents it as a positive
        // "snap to rail" toggle, default on.
        [Tooltip("Slope tool (5): when set, DON'T snap the slope endpoints/guide to " +
                 "rail. (Exposed as the positive 'Snap to rail' toggle, default on.)")]
        public bool SlopeDisableRailSnap;
        [Tooltip("Slope tool (5): if point A lands within this of rail track, the " +
                 "track's heading becomes the 'straight' guide. Bigger = easier to catch.")]
        public float SlopeGuideDetectRadius = 40f;
        [Tooltip("Slope tool (5): the end point snaps onto the rail 'straight' guide " +
                 "when within this perpendicular distance of it.")]
        public float SlopeGuideSnapRadius = 8f;
        [Tooltip("Rail auto-slope (Alt+click two rail nodes): full width (m) of the bed " +
                 "corridor graded between them.")]
        public float RailSlopeWidth = 8f;

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

            StripStaleHostMesh();
            EnsureScatterDefaults();
#if UNITY_EDITOR
            // Self-heal lost prefab references: if a layer's list is empty,
            // repopulate from its Folder so a broken link doesn't silently
            // disable the brush.
            if (TreeLayer.IsEmpty && TreeLayer.LoadFromFolder()) UnityEditor.EditorUtility.SetDirty(this);
            if (RockLayer.IsEmpty && RockLayer.LoadFromFolder()) UnityEditor.EditorUtility.SetDirty(this);
#endif
            EnsureField(forceRebuild: true);

            // Restore the autosaved work. The save is the source of truth: runtime
            // map-size changes (resize controls) don't persist to the scene, so the
            // serialized TerrainSizeMeters/CellSize can be stale and SMALLER than the
            // saved grid. Adopt the saved grid wholesale (re-centered on this object)
            // and reconcile the configured size to it — never discard the heightfield
            // over a size mismatch, or the terrain reloads flat (reading as "missing"
            // under the water plane, with every tree water-culled).
            if (Autosave)
            {
                TerrainField loaded = TryLoadTerrain();
                if (loaded != null)
                {
                    float half = (loaded.ColumnsX - 1) * loaded.CellSize * 0.5f;
                    loaded.Origin = transform.position - new Vector3(half, 0f, half);
                    _field = loaded;
                    CellSize = _field.CellSize;
                    TerrainSizeMeters = (_field.ColumnsX - 1) * _field.CellSize;
                    ColumnsX = _field.ColumnsX;
                    RowsZ = _field.RowsZ;
                }
            }

            LoadPacks(); // standalone pack library wins over any packs from the autosave

            BuildAllChunks();
            TreeLayer.SpawnPending(_field); // scatter from the save (heights now known)
            RockLayer.SpawnPending(_field);
            FenceLayer.Rebuild(_field);     // linework from the save
            PowerLineLayer.Rebuild(_field);
            RailLayer.Rebuild(_field);
            PlanLayer.Rebuild(_field);
            RebuildContours();
            ApplyWater();

            // Stand up scene services, sized to the actual terrain.
            if (AutoLighting) EnsureAmbiance();
            if (AutoCameraControl) EnsureCameraControl();
            if (AutoTuning) EnsureTuning();

            // Restore the saved camera pose, after the fly camera exists (it would
            // otherwise sit at the framed default EnsureCameraControl picked).
            if (_havePendingCam) { ApplyCameraPose(_pendingCamPos, _pendingCamYaw, _pendingCamPitch); _havePendingCam = false; }
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

        // The terrain renders via child "TerrainChunks" objects, so a
        // MeshRenderer/Filter on THIS GameObject is a frozen baked mesh from the
        // old single-mesh era — it masks the live chunks and never updates.
        // Strip it so what you see is always the current heightfield.
        void StripStaleHostMesh()
        {
            MeshRenderer mr = GetComponent<MeshRenderer>();
            if (mr != null) { mr.enabled = false; DestroyComp(mr); }
            MeshFilter mf = GetComponent<MeshFilter>();
            if (mf != null) DestroyComp(mf);
        }

        static void DestroyComp(Component c)
        {
            if (c == null) return;
            if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
        }

        // --- Chunked flat-shaded mesh ---

        void EnsureMaterial()
        {
            if (_mat == null)
            {
                Shader sh = Shader.Find("NetworkDesigner/TerrainSlope");
                // Slope-blend shader if present; otherwise fall back to the flat
                // matte material so the terrain is never invisible/magenta.
                _mat = sh != null ? new Material(sh) { name = "TerrainSlopeMat" }
                                  : PipelineMaterials.CreateLitMatte(TerrainColor, "TerrainMat");
            }
            ApplyTerrainMaterial();
        }

        // Live material tweak from the tuning panel — no mesh rebuild needed
        // (the slope blend is computed in-shader from the per-face normal).
        public void ApplyTerrainMaterial()
        {
            if (_mat == null) return;
            if (_mat.HasProperty("_RockColor")) // slope shader
            {
                _mat.SetColor("_GrassColor", TerrainColor);
                _mat.SetColor("_RockColor", RockColor);
                _mat.SetFloat("_SlopeStart", SlopeStartDeg);
                _mat.SetFloat("_SlopeFull", SlopeFullDeg);
                _mat.SetFloat("_RockTexScale", RockTextureScale);
                _mat.SetFloat("_UseRockTex", RockTexture != null ? 1f : 0f);
                if (RockTexture != null) _mat.SetTexture("_RockTex", RockTexture);
                _mat.SetColor("_GridColor", GridColor);
                _mat.SetFloat("_GridSpacing", Mathf.Max(0.5f, GridSpacing));
                _mat.SetFloat("_GridMajorEvery", Mathf.Max(1f, GridMajorEvery));
                _mat.SetFloat("_GridStrength", GridEnabled ? GridStrength : 0f);
                _mat.SetFloat("_GridLineWidth", Mathf.Max(0.1f, GridLineWidth));
            }
            else _mat.color = TerrainColor; // matte fallback
        }

        // Back-compat name still referenced by the color tunable.
        public void ApplyTerrainColor() => ApplyTerrainMaterial();

        GameObject _waterGo;
        Material _waterMat;
        Mesh _waterMesh;
        readonly List<Vector3> _waterVerts = new List<Vector3>();

        // Flat water plane at WaterLevel covering the terrain footprint. Terrain
        // above the level occludes it; below, the (transparent) water shows. Live.
        public void ApplyWater()
        {
            if (WaterColor.a <= 0.02f) WaterColor = new Color(0.20f, 0.45f, 0.55f, 0.65f); // guard a 0-alpha deserialize
            if (!ShowWater)
            {
                if (_waterGo != null) _waterGo.SetActive(false);
                return;
            }
            EnsureWater();
            _waterGo.SetActive(true);
            float w = _field != null ? _field.WidthX : TerrainSizeMeters;
            float l = _field != null ? _field.LengthZ : TerrainSizeMeters;
            Vector3 o = _field != null ? _field.Origin : Vector3.zero;
            _waterGo.transform.position = new Vector3(o.x + w * 0.5f, WaterLevel, o.z + l * 0.5f);
            // (Re)size the quad to the current terrain — the map may have grown.
            float hw = w * 0.5f + 50f, hl = l * 0.5f + 50f; // overscan a little past the edges
            _waterVerts.Clear();
            _waterVerts.Add(new Vector3(-hw, 0f, -hl)); _waterVerts.Add(new Vector3(-hw, 0f, hl));
            _waterVerts.Add(new Vector3(hw, 0f, hl)); _waterVerts.Add(new Vector3(hw, 0f, -hl));
            _waterMesh.Clear();
            _waterMesh.SetVertices(_waterVerts);
            _waterMesh.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            _waterMesh.SetTriangles(new int[] { 0, 1, 2, 0, 2, 3 }, 0);
            _waterMesh.RecalculateBounds();
            if (_waterMat != null)
            {
                _waterMat.color = WaterColor;
                if (_waterMat.HasProperty("_Smoothness")) _waterMat.SetFloat("_Smoothness", WaterSmoothness);
            }
            // Cull scatter that the (possibly risen) water now covers.
            TreeLayer?.CullBelow(WaterLevel);
            RockLayer?.CullBelow(WaterLevel);
        }

        void EnsureWater()
        {
            if (_waterGo != null) return;
            _waterGo = MakeRuntimeRoot("TerrainWater");
            var mf = _waterGo.AddComponent<MeshFilter>();
            var mr = _waterGo.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _waterMesh = new Mesh { name = "WaterMesh" }; // sized in ApplyWater
            mf.sharedMesh = _waterMesh;
            _waterMat = PipelineMaterials.CreateLitTransparent(WaterColor, WaterSmoothness, "WaterMat");
            mr.sharedMaterial = _waterMat;
        }

        // Round a world point to the nearest grid intersection (same world-aligned
        // lattice the grid shader draws), when snap-to-grid is on. Y is left as-is
        // (the line layers re-derive height from the terrain). Off = unchanged.
        Vector3 ApplyGridSnap(Vector3 p)
        {
            if (!SnapToGrid) return p;
            float s = Mathf.Max(0.5f, GridSpacing);
            return new Vector3(Mathf.Round(p.x / s) * s, p.y, Mathf.Round(p.z / s) * s);
        }

        // The cursor point the active tool will actually use, so the brush ring can
        // be drawn there too. Rail: existing track > alignment guide > grid. Other
        // line layers: grid. Slope tool (armed): the guide-snapped end. Scatter and
        // plain sculpt don't snap (returns the raw hit). Off-terrain: unchanged.
        Vector3 SnapCursor(Vector3 raw, bool overTerrain)
        {
            if (!overTerrain) return raw;
            if (_lineActive != null)
            {
                Vector2 flat = new Vector2(raw.x, raw.z);
                if (_lineActive is RailTrackLayer rl)
                {
                    // While placing the bend, it sticks to the min-distance target; while
                    // placing the end, the equal-leg lock owns the cursor.
                    if (rl.TrySnapBendToTarget(flat, out Vector2 bt)) return new Vector3(bt.x, raw.y, bt.y);
                    if (rl.TrySnapCurveSymmetry(flat, out Vector2 sym)) return new Vector3(sym.x, raw.y, sym.y);
                    // Placing the end: the PAC owns the cursor — don't let a nearby track or
                    // the straight extension line pull it off the buildable (yellow) arc.
                    if (rl.PlacingCurveEnd) return raw;
                    // On-line decel snap-target first (it's a point on the extension line),
                    // then the collinear lock — which wins over grabbing a nearby node, so a
                    // straight can't be completed off-axis (rail can't kink; use connect for
                    // real joins). Node/edge snap only applies when NOT extending.
                    if (rl.TrySnapToDecelTarget(flat, out Vector2 dt)) return new Vector3(dt.x, raw.y, dt.y);
                    if (rl.TrySnapExtensionHard(flat, out Vector2 eh)) return new Vector3(eh.x, raw.y, eh.y);
                    if (rl.TrySnapToTrack(flat, out Vector2 sp)) return new Vector3(sp.x, raw.y, sp.y);
                    if (rl.TrySnapToExtension(flat, out Vector2 ep)) return new Vector3(ep.x, raw.y, ep.y);
                }
                if (_lineActive is RailPlanLayer pl)
                {
                    // Bend sticks to the target > equal-leg lock (placing a curve end) >
                    // resume off the plan's own end > rail end > extension guide > grid.
                    if (pl.TrySnapBendToTarget(flat, out Vector2 pbt)) return new Vector3(pbt.x, raw.y, pbt.y);
                    if (pl.TrySnapCurveSymmetry(flat, out Vector2 psym)) return new Vector3(psym.x, raw.y, psym.y);
                    // Placing the end: the PAC owns the cursor exclusively.
                    if (pl.PlacingCurveEnd) return raw;
                    // Collinear lock wins over grabbing a node while extending (no kink);
                    // node/rail-end snap only applies when NOT extending.
                    if (pl.TrySnapExtensionHard(flat, out Vector2 peh)) return new Vector3(peh.x, raw.y, peh.y);
                    if (pl.TrySnapToOwnNode(flat, out Vector2 pn)) return new Vector3(pn.x, raw.y, pn.y);
                    if (RailLayer != null && RailLayer.TrySnapToTrackPoint(flat, out Vector2 rp))
                        return new Vector3(rp.x, raw.y, rp.y);
                    if (pl.TrySnapToExtension(flat, out Vector2 pe)) return new Vector3(pe.x, raw.y, pe.y);
                }
                // Grid snap makes no sense while shaping an arc — the bend/end are already
                // pinned to the MDT / extension line / PAC. So skip it in curve mode.
                bool curveMode = (_lineActive is RailTrackLayer crl && crl.InCurveMode)
                              || (_lineActive is RailPlanLayer cpl && cpl.InCurveMode);
                return curveMode ? raw : ApplyGridSnap(raw);
            }
            if (Brush == BrushMode.Slope)
            {
                if (_slopeArmed && _slopeEndValid) return _slopeEnd;   // after A: the snapped B
                // Before A: preview where A will snap (plan centreline > rail end/edge),
                // so the ring shows it ahead of the click.
                if (TrySlopeSnap(new Vector2(raw.x, raw.z), out Vector2 ap, out _, out _))
                    return new Vector3(ap.x, raw.y, ap.y);
            }
            return raw;
        }

        // The XZ a slope endpoint snaps to on rail: nearest node (edge END) / edge
        // within TrackSnapRadius, else the nearest edge point within the detect
        // radius. False (point unchanged) when rail snap is off or nothing is near.
        // Used for both the pre-click cursor preview and the actual A snap.
        bool TrySlopeRailSnapPoint(Vector2 flat, out Vector2 snapped)
        {
            snapped = flat;
            if (SlopeDisableRailSnap || RailLayer == null) return false;
            if (RailLayer.TrySnapToTrackPoint(flat, out Vector2 sp)) { snapped = sp; return true; }
            if (RailLayer.TryTrackHeadingNear(flat, SlopeGuideDetectRadius, out _, out Vector2 onr)) { snapped = onr; return true; }
            return false;
        }

        // Snap a slope endpoint onto the network for grading: the PLAN centreline
        // first (ride the planned alignment), else rail. Reports the heading there and
        // whether it landed on the plan (so the caller can size the brush to the corridor).
        bool TrySlopeSnap(Vector2 flat, out Vector2 snapped, out Vector2 dir, out bool onPlan)
        {
            snapped = flat; dir = Vector2.zero; onPlan = false;
            if (SlopeDisableRailSnap) return false;
            if (PlanLayer != null && PlanLayer.TryNearestOnPlan(flat, SlopeGuideDetectRadius, out Vector2 pp, out Vector2 pd))
            { snapped = pp; dir = pd; onPlan = true; return true; }
            if (TrySlopeRailSnapPoint(flat, out Vector2 rp))
            {
                snapped = rp;
                if (RailLayer != null) RailLayer.TryTrackHeadingNear(rp, SlopeGuideDetectRadius, out dir, out _);
                return true;
            }
            return false;
        }

        int ChunkSide => Mathf.Clamp(ChunkCells, 8, 100);

        // Build (or rebuild) all chunk meshes from the field. Recreates the
        // chunk GameObjects only when the grid/chunk count changed.
        void BuildAllChunks()
        {
            EnsureMaterial();
            int cells = _field.ColumnsX - 1; // quads per side
            int cc = ChunkSide;
            int chunksX = Mathf.Max(1, Mathf.CeilToInt(cells / (float)cc));
            int n = chunksX * chunksX;

            bool recreate = _chunkRoot == null || _chunkMesh == null
                            || _chunkMesh.Length != n || _chunksX != chunksX;
            if (recreate)
            {
                DestroyAllChunkRoots(); // tracked root + any orphans from edit-mode/reload
                _chunkRoot = new GameObject("TerrainChunks");
                _chunksX = _chunksZ = chunksX;
                _chunkMesh = new Mesh[n];
                _chunkCol = new MeshCollider[n];
                for (int cz = 0; cz < _chunksZ; cz++)
                    for (int cx = 0; cx < _chunksX; cx++)
                        CreateChunk(cx, cz, cc);
            }
            else
            {
                for (int cz = 0; cz < _chunksZ; cz++)
                    for (int cx = 0; cx < _chunksX; cx++)
                        RebuildChunk(cx, cz, cc);
            }
        }

        void CreateChunk(int cx, int cz, int cc)
        {
            int idx = cz * _chunksX + cx;
            GameObject go = new GameObject($"Chunk_{cx}_{cz}");
            go.transform.SetParent(_chunkRoot.transform, worldPositionStays: false);
            MeshFilter mf = go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            MeshCollider mc = go.AddComponent<MeshCollider>();
            Mesh mesh = new Mesh { name = go.name };
            BuildChunkMesh(cx, cz, cc, mesh);
            mf.sharedMesh = mesh;
            mc.sharedMesh = mesh;
            _chunkMesh[idx] = mesh;
            _chunkCol[idx] = mc;
        }

        void RebuildChunk(int cx, int cz, int cc)
        {
            int idx = cz * _chunksX + cx;
            Mesh mesh = _chunkMesh[idx];
            if (mesh == null) { CreateChunk(cx, cz, cc); return; }
            BuildChunkMesh(cx, cz, cc, mesh);
            _chunkCol[idx].sharedMesh = null;     // force collider re-cook
            _chunkCol[idx].sharedMesh = mesh;
        }

        void BuildChunkMesh(int cx, int cz, int cc, Mesh mesh)
        {
            int x0 = cx * cc, z0 = cz * cc;
            int x1 = Mathf.Min(x0 + cc, _field.ColumnsX - 1);
            int z1 = Mathf.Min(z0 + cc, _field.RowsZ - 1);
            TerrainChunkBuilder.Build(_field, x0, z0, x1, z1, mesh);
        }

        // Rebuild only the chunks overlapping a cell region (after a sculpt).
        void RebuildChunkRegion(int x0, int z0, int w, int h)
        {
            if (_chunkMesh == null) return;
            int cc = ChunkSide;
            int cxa = Mathf.Clamp(x0 / cc, 0, _chunksX - 1);
            int cxb = Mathf.Clamp((x0 + w) / cc, 0, _chunksX - 1);
            int cza = Mathf.Clamp(z0 / cc, 0, _chunksZ - 1);
            int czb = Mathf.Clamp((z0 + h) / cc, 0, _chunksZ - 1);
            for (int cz = cza; cz <= czb; cz++)
                for (int cx = cxa; cx <= cxb; cx++)
                    RebuildChunk(cx, cz, cc);
        }

        static void DestroySafe(GameObject go)
        {
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        // Create a named runtime helper object, first destroying ANY existing ones
        // with that name — including old HideFlags.DontSave leftovers that survived a
        // domain reload (those would otherwise pile up one-per-reload, e.g. the
        // 'ContourLines' leak). The fresh object is NOT DontSave, so it's cleaned up
        // normally on Play-stop. FindObjectsOfTypeAll sees DontSave/hidden objects
        // that FindObjectsByType misses.
        static GameObject MakeRuntimeRoot(string objName)
        {
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                GameObject g = all[i];
                if (g != null && g.scene.IsValid() && g.name == objName) DestroySafe(g);
            }
            return new GameObject(objName);
        }

        // Destroy the tracked chunk root AND any orphaned "TerrainChunks" objects.
        // _chunkRoot isn't serialized, so an edit-mode build or domain reload
        // leaves prior roots with no live reference — they stack up and mask the
        // newest terrain. Sweep them all by name before building a fresh one.
        void DestroyAllChunkRoots()
        {
            _chunkRoot = null;
            // FindObjectsOfTypeAll (not FindObjectsByType) so old HideFlags.DontSave
            // roots that survived a reload are caught and don't pile up.
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].scene.IsValid() && all[i].name == "TerrainChunks") DestroySafe(all[i]);
        }

        // --- Scatter brushes (trees, rocks) via ScatterLayer ---

        // The TreeLayer/RockLayer field initializers only apply to a freshly
        // added component (Unity serialization footgun); a component saved before
        // these fields existed deserializes them blank. Fill sane Name/Folder
        // when unset so the brushes work without a manual component Reset.
        void EnsureScatterDefaults()
        {
            if (TreeLayer == null) TreeLayer = new ScatterLayer();
            if (RockLayer == null) RockLayer = new ScatterLayer();
            if (string.IsNullOrEmpty(TreeLayer.Name) || TreeLayer.Name == "Scatter") TreeLayer.Name = "Trees";
            if (string.IsNullOrEmpty(RockLayer.Name) || RockLayer.Name == "Scatter") RockLayer.Name = "Rocks";
            if (string.IsNullOrEmpty(TreeLayer.Folder)) TreeLayer.Folder = "Assets/Trees";
            if (string.IsNullOrEmpty(RockLayer.Folder)) RockLayer.Folder = "Assets/Rocks";
            // New field on the serialized layer: components saved before it
            // existed deserialize MaxSlopeDeg as 0, which would block ALL
            // placement (nothing is flatter than 0 deg). Treat <=0 as "unset".
            if (TreeLayer.MaxSlopeDeg <= 0f) TreeLayer.MaxSlopeDeg = 35f;
            if (RockLayer.MaxSlopeDeg <= 0f) RockLayer.MaxSlopeDeg = 35f;
            if (FenceLayer == null) FenceLayer = new LineworkLayer();
            if (string.IsNullOrEmpty(FenceLayer.Name) || FenceLayer.Name == "Line") FenceLayer.Name = "Fence";
            if (PowerLineLayer == null) PowerLineLayer = new LineworkLayer();
            if (string.IsNullOrEmpty(PowerLineLayer.Name) || PowerLineLayer.Name == "Line") PowerLineLayer.Name = "PowerLine";
            if (RailLayer == null) RailLayer = new RailTrackLayer();
            if (string.IsNullOrEmpty(RailLayer.Name)) RailLayer.Name = "Rail";
            if (PlanLayer == null) PlanLayer = new RailPlanLayer();
            if (string.IsNullOrEmpty(PlanLayer.Name)) PlanLayer.Name = "Plan";
            if (PlanLayer.CorridorWidth <= 0f) PlanLayer.CorridorWidth = 30f; // stale 0-deserialize
            if (PlanLayer.Tracks < 1) PlanLayer.Tracks = 1;
            if (PlanLayer.TrackGap <= 0f) PlanLayer.TrackGap = 4f;
            if (PlanLayer.SampleStep <= 0f) PlanLayer.SampleStep = 2f;
            if (PlanLayer.CurveLever <= 0f) PlanLayer.CurveLever = 0.55f;
            if (PlanLayer.ExtensionGuideLength <= 0f) PlanLayer.ExtensionGuideLength = 120f;
            if (PlanLayer.ExtensionSnapRadius <= 0f) PlanLayer.ExtensionSnapRadius = 4f;
            if (PlanLayer.EndSnapRadius <= 0f) PlanLayer.EndSnapRadius = 8f;
            // New fields on the serialized layer can deserialize as 0 (Unity
            // footgun); 0 lateral-g would blow the required radius up ~100x.
            if (RailLayer.MaxLateralG <= 0f) RailLayer.MaxLateralG = 0.15f;
            if (RailLayer.SpeedLimitKmh <= 0f) RailLayer.SpeedLimitKmh = 40f;
            if (RailLayer.MaxGradeDeg <= 0f) RailLayer.MaxGradeDeg = 5f; // 0 would block all track
            if (RailLayer.BridgeAboveFill <= 0f) RailLayer.BridgeAboveFill = 6f; // 0 = everything a bridge
            // Fields added recently: a stale 0-deserialize hides the alignment guide
            // (zero length) / collapses the grade-scan step. Restore sane defaults.
            if (RailLayer.ExtensionGuideLength <= 0f) RailLayer.ExtensionGuideLength = 120f;
            if (RailLayer.GradeSampleStep <= 0f) RailLayer.GradeSampleStep = 10f;
            // Same for the new TerrainDesigner fields — a 0 here breaks the slope tool
            // (no rail snap / no warn) and Flatten strength.
            if (SlopeGuideDetectRadius <= 0f) SlopeGuideDetectRadius = 40f;
            if (SlopeGuideSnapRadius <= 0f) SlopeGuideSnapRadius = 8f;
            if (SlopeMaxGradePct <= 0f) SlopeMaxGradePct = 6f;
            if (FlattenStrength <= 0f) FlattenStrength = 10f;
        }

        // Global IMGUI scale so panels/text don't shrink to nothing at high
        // resolution / high-DPI. Applied via GUI.matrix; all panel layout uses
        // the "virtual" screen (Vw/Vh) so right-anchored panels stay flush and
        // MouseOverActivePanel converts the mouse back into the same space.
        public static float UiScale = 1.5f;
        static float Vw => Screen.width / Mathf.Max(0.25f, UiScale);
        static float Vh => Screen.height / Mathf.Max(0.25f, UiScale);

        // Palette IMGUI for the active scatter layer, or a hint for linework.
        void OnGUI()
        {
            bool sculptHud = (Brush == BrushMode.Slope || Brush == BrushMode.Flatten)
                             && _lineActive == null && _active == null;
            if (_lineActive == null && _active == null && !sculptHud) return;
            Matrix4x4 prev = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(UiScale, UiScale, 1f));
            DrawPanels();
            GUI.matrix = prev;
        }

        void DrawPanels()
        {
            DrawPlanGradeLabels();
            DrawCurveDimLabels();
            DrawCurveTickLabels();
            DrawSpeedLabels();
            DrawDesignSpeedReadout();
            DrawCurveInspectLabels();
            if (_lineActive != null)
            {
                bool rail = _lineActive is RailTrackLayer;
                bool plan = _lineActive is RailPlanLayer;
                GUILayout.BeginArea(new Rect(Vw - 308f, 8f, 300f, rail ? 332f : (plan ? 292f : 104f)), GUI.skin.box);
                GUILayout.Label(_lineActive.LayerName + " mode");
                GUILayout.Label(rail || plan
                    ? "Click: straight segment. Hold Shift: click a corner, then the end = curve."
                    : "Left-click: add node (chains)\nRight-click: delete near node / end chain\nBackspace: undo last node");
                if (_lineActive is LineworkLayer lw && lw.Asset == null)
                    GUILayout.Label("Assign an Asset prefab on the\nlayer to see it render.");
                if (_lineActive is RailPlanLayer pl)
                {
                    GUILayout.Label($"Corridor {pl.CorridorWidth:0} m · {pl.Tracks} track"
                        + (pl.Tracks >= 2 ? $" (gap {pl.TrackGap:0} m)" : "") + " · snaps to rail end");
                    if (pl.LimitCurveRadius)
                    {
                        GUILayout.Label($"Design {pl.SpeedLimitKmh:0} km/h → min radius {pl.MinRadiusForSpeed:0} m");
                        if (pl.LastPreviewRadius < float.PositiveInfinity)
                            GUILayout.Label(pl.LastPreviewTooTight
                                ? $"Curve {pl.LastPreviewRadius:0} m — TOO TIGHT (slow down or widen)"
                                : $"Curve radius {pl.LastPreviewRadius:0} m — ok");
                    }
                    if (!pl.ShowAnalysis)
                        GUILayout.Label("Analysis off (plain survey). Toggle\n'plan.analyze' to colour the corridor.");
                    else if (pl.RouteLength < 1f)
                        GUILayout.Label("Draw a route to analyze it.");
                    else
                    {
                        float[] L = pl.ClassLen; float tot = Mathf.Max(1f, pl.RouteLength);
                        GUILayout.Label($"Route {pl.RouteLength:0} m  ·  {100f * L[0] / tot:0}% at-grade");
                        GUILayout.Label($"cut {L[1]:0} m · fill {L[2]:0} m");
                        GUILayout.Label($"bridge {L[3]:0} m · tunnel {L[4]:0} m");
                        if (L[5] > 0.5f) GUILayout.Label($"OVER-GRADE {L[5]:0} m — needs reroute");
                        GUILayout.Label("Key: solid=at-grade, dashed=cut,\ndbl-dash=fill · cyan/purple/red=brdg/tun/over");
                    }
                    int bs = PlanBuildableStatus();
                    GUILayout.Label(bs < 0 ? "Draw a plan, then 'plan.buildRail'."
                        : bs == 0 ? "Buildable ✓ — 'plan.buildRail' lays track."
                        : $"{bs} segment(s) over grade — grade before building.");
                }
                if (_lineActive is RailTrackLayer rt)
                {
                    GUILayout.Label("Click a rail edge: insert node (chop).\nClick a node puck: branch from it.");
                    if (rt.PreviewBrakeValid)
                        GUILayout.Label($"Decel {rt.PreviewBrakeVIn:0}→{rt.PreviewBrakeVNew:0} km/h over {rt.PreviewBrakeDist:0} m\nalong this line (then {rt.PreviewBrakeVNew:0}-radius curves OK)");
                    if (rt.PreviewBrakeReqRadius > 0f)
                        GUILayout.Label($"TOO TIGHT for the braking zone — still fast here,\nneed radius ≥ {rt.PreviewBrakeReqRadius:0} m (not the {rt.PreviewBrakeVNew:0}-km/h min)");
                    else if (!rt.PreviewBrakeValid && rt.PreviewHasIncoming)
                        GUILayout.Label($"Off a {rt.PreviewBrakeVIn:0} km/h line, new is {rt.PreviewBrakeVNew:0} — no decel needed.");
                    if (_railSlopeNodeA >= 0)
                        GUILayout.Label(_railSlopePath != null
                            ? (_railSlopeGradeOk
                                ? $"Auto-slope → {_railSlopeGradePct:0.0}% — OK. Alt+click node B."
                                : $"Auto-slope → {_railSlopeGradePct:0.0}% — OVER {rt.MaxGradeDeg:0.0}°. Pick a closer B.")
                            : "Auto-slope: A set. Alt+click node B (right-click cancels).");
                    else
                        GUILayout.Label("Alt+click node A then node B: auto-slope the bed.");
                    if (_railConnectNodeA >= 0)
                        GUILayout.Label(_connectStatus ?? "Connect: A set. C+click end B (right-click cancels).");
                    else
                        GUILayout.Label("C+click end A then end B: join two lines.");
                    GUILayout.Label($"Speed {rt.SpeedLimitKmh:0} km/h  →  min radius {rt.MinRadiusForSpeed:0} m");
                    if (rt.LastPreviewRadius < float.PositiveInfinity)
                        GUILayout.Label(rt.LastPreviewTooTight
                            ? $"Curve {rt.LastPreviewRadius:0} m — TOO TIGHT (lower speed or widen)"
                            : $"Curve radius {rt.LastPreviewRadius:0} m — ok");
                    GUILayout.Label($"Steepest section {rt.LastPreviewGradeDeg:0.0}° / max {rt.MaxGradeDeg:0.0}°");
                    if (rt.OverrideGrade)
                    {
                        float g = rt.LastPreviewEndpointGradeDeg;
                        float pct = Mathf.Tan(g * Mathf.Deg2Rad) * 100f;
                        GUILayout.Label($"OVERRIDE ON (B) — full edge at true\ngrade A→B {g:0.0}° ({pct:0.0}%); deep fills → bridges.");
                    }
                    else if (rt.LastPreviewTruncated)
                        GUILayout.Label($"Buildable {rt.LastPreviewBuildableLen:0} m of {rt.LastPreviewTotalLen:0} m —\nrest (red) is over grade. B = build anyway.");
                    else
                        GUILayout.Label("Grade OK — full edge buildable. (B = override)");
                }
                GUILayout.EndArea();
                return;
            }
            // DrawPalette returns true only on a pack create/update/delete — persist
            // the standalone packs file then (packs aren't part of the debounced save).
            if (_active != null) { if (_active.DrawPalette()) { _dirtySince = Time.realtimeSinceStartup; SavePacks(); } return; }

            // Slope tool (brush 5) readout — no other panel is up in sculpt mode.
            if (Brush == BrushMode.Slope)
            {
                GUILayout.BeginArea(new Rect(Vw - 308f, 8f, 300f, 132f), GUI.skin.box);
                GUILayout.Label("Slope tool (5)");
                if (!_slopeArmed)
                    GUILayout.Label("Left-click point A (start elevation).\nNear rail? It snaps to the track 'straight'.");
                else if (_slopeEndValid)
                {
                    float g = Mathf.Abs(_slopeGradePct);
                    GUILayout.Label(g > SlopeMaxGradePct
                        ? $"Grade {_slopeGradePct:0.0}% — OVER {SlopeMaxGradePct:0.0}%"
                        : $"Grade {_slopeGradePct:0.0}% / warn {SlopeMaxGradePct:0.0}%");
                    GUILayout.Label(_slopeHasGuide ? "Aligned to rail 'straight'." : "Free direction.");
                    GUILayout.Label("Left-click point B to grade. Right-click cancels.");
                }
                else
                    GUILayout.Label("Move over terrain to set point B.");
                GUILayout.EndArea();
            }

            // Flatten tool (4): a small HUD beside the cursor with the elevation under
            // it, plus the picked target height once you've right-clicked (eyedropper).
            if (Brush == BrushMode.Flatten && _flattenCursorValid)
            {
                float s = Mathf.Max(0.25f, UiScale);
                Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / s;
                string txt = $"Elev {_flattenCursorElev:0.0} m";
                if (_flattenTargetPicked)
                    txt += $"\nTarget {(_field != null ? _field.Origin.y + _flattenTarget : _flattenTarget):0.0} m";
                var content = new GUIContent(txt);
                Vector2 size = GUI.skin.box.CalcSize(content);
                GUI.Box(new Rect(m.x + 18f, m.y + 2f, size.x + 8f, size.y + 4f), content);
            }
        }

        // Float a grade-% label over each plan segment (current terrain), so you can read
        // the natural grade before earthworks and the achieved grade after. Shown while
        // editing the plan or using the slope tool. Red box = over the plan's max grade.
        void DrawPlanGradeLabels()
        {
            if (PlanLayer == null || _field == null || _active != null || !PlanLayer.ShowGradeLabels) return;
            if (!(Brush == BrushMode.Slope || _lineActive is RailPlanLayer)) return;
            PlanLayer.CollectEdgeGrades(_field, _planGrades);
            if (_planGrades.Count == 0) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            Color prevC = GUI.color;
            foreach (var g in _planGrades)
            {
                Vector3 sp = cam.WorldToScreenPoint(g.Mid);
                if (sp.z <= 0f) continue; // behind the camera
                float mx = sp.x / s, my = (Screen.height - sp.y) / s;
                var content = new GUIContent($"{Mathf.Abs(g.GradePct):0.0}%");
                Vector2 size = GUI.skin.box.CalcSize(content);
                GUI.color = g.Over ? new Color(1f, 0.45f, 0.4f, 1f) : Color.white;
                GUI.Box(new Rect(mx - size.x * 0.5f, my - size.y * 0.5f, size.x + 6f, size.y + 2f), content);
            }
            GUI.color = prevC;
        }

        // A boxed text label centred at a world position, projected to the (Ui-scaled)
        // screen. No-op when the point is behind the camera.
        void DrawWorldLabel(Camera cam, float s, Vector3 world, string text)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return;
            float mx = sp.x / s, my = (Screen.height - sp.y) / s;
            var content = new GUIContent(text);
            Vector2 size = GUI.skin.box.CalcSize(content);
            GUI.Box(new Rect(mx - size.x * 0.5f, my - size.y * 0.5f, size.x + 6f, size.y + 2f), content);
        }

        // While drawing a curve (rail or plan) with the bend placed, label the two
        // construction legs A->bend and bend->B in metres.
        void DrawCurveDimLabels()
        {
            float la, lb, deg = 0f; Vector3 ma, mb, corner = default;
            if (_lineActive is RailPlanLayer pl && pl.CurveDimsValid)
            { la = pl.CurveLegA; lb = pl.CurveLegB; ma = pl.CurveLegAMid; mb = pl.CurveLegBMid; deg = pl.CurveDeflectionDeg; corner = pl.CurveCornerWorld; }
            else if (_lineActive is RailTrackLayer rt && rt.CurveDimsValid)
            { la = rt.CurveLegA; lb = rt.CurveLegB; ma = rt.CurveLegAMid; mb = rt.CurveLegBMid; deg = rt.CurveDeflectionDeg; corner = rt.CurveCornerWorld; }
            else return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            // Plain text the colour of the line (no boxed tooltip), floating at the legs.
            Color lineCol = _lineActive is RailPlanLayer
                ? new Color(1f, 0.92f, 0.2f) : new Color(1f, 0.8f, 0.3f);
            if (la > 0f) DrawWorldText(cam, s, ma, $"{la:0} m", lineCol);
            if (lb > 0f) DrawWorldText(cam, s, mb, $"{lb:0} m", lineCol); // 0 = bend not placed yet
            if (lb > 0f) DrawWorldText(cam, s, corner, $"{deg:0}°", lineCol); // deflection at the bend
        }

        // Degree labels at the PAC angle ticks (15/30/.../90°) while placing a curve end.
        void DrawCurveTickLabels()
        {
            List<Vector3> pos = null; List<int> deg = null;
            if (_lineActive is RailTrackLayer rt && rt.PlacingCurveEnd) { pos = rt.CurveTickWorld; deg = rt.CurveTickDeg; }
            else if (_lineActive is RailPlanLayer pl && pl.PlacingCurveEnd) { pos = pl.CurveTickWorld; deg = pl.CurveTickDeg; }
            if (pos == null || pos.Count == 0) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            Color col = new Color(1f, 0.88f, 0.2f);   // PAC yellow
            for (int i = 0; i < pos.Count && i < deg.Count; i++)
                DrawWorldText(cam, s, pos[i], $"{deg[i]}°", col);
        }

        // The design speed of the active rail/plan layer, floating near the cursor so
        // you always see what speed you're planning/building for (not just the palette).
        void DrawDesignSpeedReadout()
        {
            if (!_lineCursorValid) return;
            float kmh; Color col; bool haveTail; Vector2 tail;
            if (_lineActive is RailTrackLayer rt)
            { kmh = rt.SpeedLimitKmh; col = new Color(1f, 0.8f, 0.3f); haveTail = rt.TryGetTailXZ(out tail); }
            else if (_lineActive is RailPlanLayer pl)
            { kmh = pl.SpeedLimitKmh; col = new Color(1f, 0.92f, 0.2f); haveTail = pl.TryGetTailXZ(out tail); }
            else return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            // Sit it just AHEAD of the cursor along the leg (toward the ring), off the
            // distance label which lives back on the leg.
            Vector3 anchor = _lineCursorWorld + new Vector3(0f, 2f, 0f);
            if (haveTail)
            {
                Vector2 cur = new Vector2(_lineCursorWorld.x, _lineCursorWorld.z);
                Vector2 dir = cur - tail;
                if (dir.sqrMagnitude > 1e-4f) { dir.Normalize(); anchor += new Vector3(dir.x, 0f, dir.y) * 10f; }
            }
            DrawWorldText(cam, s, anchor, $"{kmh:0} km/h", col);
        }

        // Curve-inspection readout for the hovered curve: leg distances + deflection angle
        // at the construction geometry, and a metrics block (decel / radius+max-speed /
        // grade / rated) anchored near the curve. Fed by the active layer's Inspect* fields.
        void DrawCurveInspectLabels()
        {
            bool hovered = false, hasCorner = false, hasGrade = false, isCurve = false;
            Vector2 mid = default, corner = default, laMid = default, lbMid = default;
            float legA = 0, legB = 0, ang = 0, radius = 0, maxSpd = 0, gradePct = 0, rated = 0, len = 0, trains = 0;
            string decel = null;
            if (_lineActive is RailTrackLayer rt && rt.ShowCurveInspect && rt.InspectHovered)
            {
                hovered = true; hasCorner = rt.InspectHasCorner; hasGrade = rt.InspectHasGrade; isCurve = rt.InspectIsCurve;
                mid = rt.InspectMid; corner = rt.InspectCorner; laMid = rt.InspectLegAMid; lbMid = rt.InspectLegBMid;
                legA = rt.InspectLegA; legB = rt.InspectLegB; ang = rt.InspectAngleDeg;
                radius = rt.InspectRadius; maxSpd = rt.InspectMaxSpeed; gradePct = rt.InspectGradePct;
                rated = rt.InspectRated; decel = rt.InspectDecel; len = rt.InspectLength; trains = rt.InspectTrainCount;
            }
            else if (_lineActive is RailPlanLayer pl && pl.ShowCurveInspect && pl.InspectHovered)
            {
                hovered = true; hasCorner = pl.InspectHasCorner; hasGrade = pl.InspectHasGrade; isCurve = pl.InspectIsCurve;
                mid = pl.InspectMid; corner = pl.InspectCorner; laMid = pl.InspectLegAMid; lbMid = pl.InspectLegBMid;
                legA = pl.InspectLegA; legB = pl.InspectLegB; ang = pl.InspectAngleDeg;
                radius = pl.InspectRadius; maxSpd = pl.InspectMaxSpeed; gradePct = pl.InspectGradePct;
                rated = pl.InspectRated; decel = pl.InspectDecel; len = pl.InspectLength; trains = pl.InspectTrainCount;
            }
            if (!hovered) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            Color col = new Color(1f, 0.95f, 0.25f);

            if (hasCorner)
            {
                if (legA > 0f) DrawWorldText(cam, s, ToWorldXZ(laMid, 2f), $"{legA:0} m", col);
                if (legB > 0f) DrawWorldText(cam, s, ToWorldXZ(lbMid, 2f), $"{legB:0} m", col);
                DrawWorldText(cam, s, ToWorldXZ(corner, 2f), $"{ang:0}°", col);
            }
            var lines = new List<string>(4);
            if (decel != null) lines.Add(decel);
            if (isCurve) lines.Add($"{radius:0}m radius, max speed: {maxSpd:0} km/h");
            else lines.Add($"{len:0}m queue (~{trains:0.0} trains)");
            if (hasGrade) lines.Add($"{gradePct:0.0}% grade");
            lines.Add($"{rated:0} km/h rated");
            DrawWorldTextBlock(cam, s, ToWorldXZ(mid, 3f), lines, col);
        }

        Vector3 ToWorldXZ(Vector2 xz, float lift)
        {
            float y = _field != null ? _field.SampleHeight(xz.x, xz.y) : 0f;
            return new Vector3(xz.x, y + lift, xz.y);
        }

        // Left-aligned multi-line text stacked downward from a world position.
        void DrawWorldTextBlock(Camera cam, float s, Vector3 world, List<string> lines, Color color)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return;
            float mx = sp.x / s, my = (Screen.height - sp.y) / s;
            float lh = GUI.skin.label.lineHeight; if (lh < 1f) lh = 16f;
            Color prev = GUI.color; GUI.color = color;
            for (int i = 0; i < lines.Count; i++)
            {
                var content = new GUIContent(lines[i]);
                Vector2 size = GUI.skin.label.CalcSize(content);
                GUI.Label(new Rect(mx, my + i * lh, size.x + 2f, size.y), content);
            }
            GUI.color = prev;
        }

        // Plain centred text (no box) at a world position, in the given colour.
        void DrawWorldText(Camera cam, float s, Vector3 world, string text, Color color)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return;
            float mx = sp.x / s, my = (Screen.height - sp.y) / s;
            var content = new GUIContent(text);
            Vector2 size = GUI.skin.label.CalcSize(content);
            Color prev = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(mx - size.x * 0.5f, my - size.y * 0.5f, size.x + 2f, size.y), content);
            GUI.color = prev;
        }

        // Speed-limit labels along each rail line (interrogate existing speeds). Shown
        // while editing rail when rail.showSpeedLabels is on.
        void DrawSpeedLabels()
        {
            if (!(_lineActive is RailTrackLayer rt) || !rt.ShowSpeedLabels || rt.SpeedLabels.Count == 0) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            Color prevC = GUI.color;
            GUI.color = new Color(0.85f, 0.95f, 0.75f, 1f);
            foreach (RailTrackLayer.SpeedLabel sl in rt.SpeedLabels)
                DrawWorldLabel(cam, s, sl.World, $"{sl.Kmh:0} km/h");
            GUI.color = prevC;
        }

        // True when the cursor is over the active layer's palette (so paint/erase
        // and camera zoom are suppressed there). Y is flipped (GUI rect is
        // top-left origin, mouse is bottom-left) and divided by UiScale because
        // PanelRect is stored in the unscaled virtual-screen space.
        bool MouseOverActivePanel()
        {
            if (_active == null || _active.PanelRect.width <= 0f) return false;
            float s = Mathf.Max(0.25f, UiScale);
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / s;
            return _active.PanelRect.Contains(m);
        }

        // Linework rebuild/clear — used by the tuning-panel actions and on edits.
        public void RebuildFence() { FenceLayer.Rebuild(Field); }
        [ContextMenu("Clear Fence")]
        public void ClearFence() { FenceLayer.ClearAll(Field); _dirtySince = Time.realtimeSinceStartup; }
        public void RebuildPowerLine() { PowerLineLayer.Rebuild(Field); }
        [ContextMenu("Clear Power Line")]
        public void ClearPowerLine() { PowerLineLayer.ClearAll(Field); _dirtySince = Time.realtimeSinceStartup; }
        [ContextMenu("Rebuild Rail")]
        public void RebuildRail() { RailLayer.Rebuild(Field); }

        // Pick up Inspector edits (e.g. dragging the bridge prefab into its slot)
        // live in play mode — object-slot changes don't go through the tunables.
        void OnValidate()
        {
            if (Application.isPlaying && _field != null) { RailLayer.Rebuild(_field); ApplyWater(); }
        }
        [ContextMenu("Clear Rail")]
        public void ClearRail() { RailLayer.ClearAll(Field); _dirtySince = Time.realtimeSinceStartup; }

        // Diagnostic: what is actually rendering in the scene (esp. the "phantom"
        // trees that aren't tracked by the scatter layer). Logs PlacedTree count +
        // every non-terrain MeshRenderer with its hierarchy path and hideFlags.
        [ContextMenu("Diagnose scene renderers")]
        public void DiagnoseScatter()
        {
            // Resources.FindObjectsOfTypeAll finds HIDDEN / HideAndDontSave objects too
            // (which FindObjectsByType misses) — that's where the phantoms live.
            var rends = Resources.FindObjectsOfTypeAll<Renderer>();
            var counts = new Dictionary<string, int>();
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                GameObject g = r.gameObject;
                if (!g.scene.IsValid()) continue; // skip project/prefab assets
                string type = r.GetType().Name;
                counts[type] = counts.TryGetValue(type, out int c) ? c + 1 : 1;
                string nm = g.name;
                bool terrainish = nm.StartsWith("Chunk_") || nm.Contains("Contour") || nm.Contains("Rail")
                    || nm.Contains("Plan") || nm == "TerrainWater" || nm.Contains("Pole") || nm.Contains("Ground");
                if (terrainish || shown >= 50) continue;
                string path = nm; Transform t = g.transform.parent;
                while (t != null) { path = t.name + "/" + path; t = t.parent; }
                sb.Append($"  [{type}] {path}  mat={(r.sharedMaterial != null ? r.sharedMaterial.name : "none")}  hideFlags={g.hideFlags}  scene={g.scene.name}\n");
                shown++;
            }
            var summary = new System.Text.StringBuilder();
            foreach (var kv in counts) summary.Append($"{kv.Key}={kv.Value} ");
            // Loaded scenes + their root objects (reveals additive scenes / DontDestroyOnLoad).
            var sc = new System.Text.StringBuilder();
            for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                sc.Append($"  scene '{scene.name}' roots: ");
                foreach (var root in scene.GetRootGameObjects()) sc.Append(root.name).Append(", ");
                sc.Append('\n');
            }
            string terrainShader = _mat != null && _mat.shader != null ? _mat.shader.name : "(none)";
            Debug.Log($"[Diag] Renderer totals: {summary}\nterrainShader={terrainShader}\nloaded scenes:\n{sc}Non-terrain renderers ({shown} shown):\n{sb}");
        }
        public void RebuildPlan() { PlanLayer.Rebuild(Field); }
        public void ClearPlan() { PlanLayer.ClearAll(Field); _dirtySince = Time.realtimeSinceStartup; }

        // Status of the "build rail on the plan centreline" action, surfaced in the
        // plan panel. -1 = empty plan, 0 = buildable, >0 = that many over-grade segments.
        public int PlanBuildableStatus()
        {
            if (_field == null || PlanLayer == null || PlanLayer.Graph == null
                || PlanLayer.Graph.Edges.Count == 0) return -1;
            PlanLayer.AllEdgesBuildable(_field, out int over);
            return over;
        }

        // Promote the finished survey plan to real rail: build track on the plan
        // centreline. Refuses unless the WHOLE plan is buildable (no segment over the
        // plan's max grade) — grade the red sections first. Leaves the plan in place.
        public void PromotePlanToRail()
        {
            int status = PlanBuildableStatus();
            if (status < 0) { Debug.LogWarning("[Plan→Rail] Plan is empty — nothing to build."); return; }
            if (status > 0)
            {
                Debug.LogWarning($"[Plan→Rail] {status} segment(s) exceed the plan's max grade "
                    + $"({PlanLayer.MaxGradeDeg:0.0}°). Grade them (red) before building.");
                return;
            }
            int added = RailLayer.AppendGraph(PlanLayer.Graph, RailLayer.SpeedLimitKmh, _field);
            Debug.Log($"[Plan→Rail] Built {added} rail segment(s) on the plan centreline.");
            _dirtySince = Time.realtimeSinceStartup;
        }

        // Cut open trenches in the terrain along rail sections that run below grade
        // (the tunnel approaches), down to the track bed with sloped batter walls,
        // so the cut leads to the portal instead of the track stabbing into the
        // hill. DESTRUCTIVE: permanently lowers the heightfield (no auto-undo, and
        // it won't fill back if you later move the track).
        [ContextMenu("Carve Rail Approaches")]
        public void CarveRailApproaches()
        {
            if (_field == null) EnsureField(forceRebuild: true);
            if (_field == null) return;
            float clearance = Mathf.Max(2f, RailLayer.TunnelClearance);
            float tunnelBury = clearance + Mathf.Max(0f, RailLayer.TunnelMinCover);
            var cuts = new List<Vector3>();
            RailLayer.CollectOpenCuts(_field, tunnelBury, cuts);
            if (cuts.Count == 0) return;

            float cs = _field.CellSize;
            Vector3 o = _field.Origin;
            // Flat floor at least one cell wide, so the grid vertices straddling
            // the track always get lowered (otherwise a coarse grid leaves the
            // bilinear terrain draped over the rails).
            float cutHalf = Mathf.Max(cs, Mathf.Max(0.5f, CutFloorHalfWidth));
            float batterRise = Mathf.Max(0.1f, CutBatter);
            float depthBelow = Mathf.Max(0f, CutDepthBelowBed);
            float reach = cutHalf + (tunnelBury + depthBelow) / batterRise;
            float[] H = _field.Heights;
            var affected = new HashSet<int>();                 // all cells in the cut region (smooth these)
            var floorClamp = new Dictionary<int, float>();     // flat-floor cells -> their floor (keep low)

            foreach (Vector3 c in cuts)
            {
                float sx = c.x, sz = c.z, floorY = c.y - depthBelow; // dig to bed (minus a bit)
                int x0 = Mathf.Clamp(Mathf.FloorToInt((sx - reach - o.x) / cs), 0, _field.ColumnsX - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((sx + reach - o.x) / cs), 0, _field.ColumnsX - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((sz - reach - o.z) / cs), 0, _field.RowsZ - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((sz + reach - o.z) / cs), 0, _field.RowsZ - 1);
                for (int vz = z0; vz <= z1; vz++)
                    for (int vx = x0; vx <= x1; vx++)
                    {
                        float dx = o.x + vx * cs - sx, dz = o.z + vz * cs - sz;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > reach) continue;
                        float targetRel = (floorY + Mathf.Max(0f, d - cutHalf) * batterRise) - o.y;
                        int idx = _field.Index(vx, vz);
                        if (H[idx] > targetRel) H[idx] = targetRel; // lower only (carve)
                        affected.Add(idx);
                        if (d <= cutHalf) // flat floor cell — remember its level so smoothing can't lift it
                        {
                            float floorRel = floorY - o.y;
                            if (!floorClamp.TryGetValue(idx, out float prev) || floorRel < prev)
                                floorClamp[idx] = floorRel;
                        }
                    }
            }

            SmoothCutRegion(H, affected, floorClamp);

            // Rebuild the mesh + re-conform everything to the carved surface.
            BuildAllChunks();
            TreeLayer.ConformToSurface(_field);
            RockLayer.ConformToSurface(_field);
            FenceLayer.Rebuild(_field);
            PowerLineLayer.Rebuild(_field);
            RailLayer.Rebuild(_field);
            PlanLayer.Rebuild(_field);
            RebuildContours();
            ApplyWater();
            _dirtySince = Time.realtimeSinceStartup;
        }

        // Box-blur the carved cells (CutSmoothPasses) to round the coarse-grid wall
        // steps, writing ONLY affected cells (untouched terrain is read but never
        // modified, so the cut blends into its surroundings at the edge). The flat
        // floor under the track is re-clamped after, so smoothing can't lift the
        // ground back over the rails.
        void SmoothCutRegion(float[] H, HashSet<int> affected, Dictionary<int, float> floorClamp)
        {
            if (CutSmoothPasses <= 0 || affected.Count == 0) return;
            int cols = _field.ColumnsX, rows = _field.RowsZ;
            var cells = new List<int>(affected);
            for (int pass = 0; pass < CutSmoothPasses; pass++)
            {
                var before = new Dictionary<int, float>(cells.Count);
                foreach (int idx in cells) before[idx] = H[idx];
                foreach (int idx in cells)
                {
                    int vx = idx % cols, vz = idx / cols;
                    float sum = before[idx]; int cnt = 1;
                    AccumNeighbor(vx - 1, vz, cols, rows, before, H, ref sum, ref cnt);
                    AccumNeighbor(vx + 1, vz, cols, rows, before, H, ref sum, ref cnt);
                    AccumNeighbor(vx, vz - 1, cols, rows, before, H, ref sum, ref cnt);
                    AccumNeighbor(vx, vz + 1, cols, rows, before, H, ref sum, ref cnt);
                    H[idx] = sum / cnt;
                }
            }
            foreach (KeyValuePair<int, float> kv in floorClamp) // keep the floor low (no re-burying)
                if (H[kv.Key] > kv.Value) H[kv.Key] = kv.Value;
        }

        // Neighbour height for the cut smoother: pre-pass value if it's an affected
        // cell, else the current (untouched) terrain. Out-of-range neighbours skip.
        static void AccumNeighbor(int nx, int nz, int cols, int rows,
            Dictionary<int, float> before, float[] H, ref float sum, ref int cnt)
        {
            if (nx < 0 || nz < 0 || nx >= cols || nz >= rows) return;
            int n = nz * cols + nx;
            sum += before.TryGetValue(n, out float bv) ? bv : H[n];
            cnt++;
        }

        // Mode switching (mutually exclusive; same key again returns to sculpt).
        void SetScatterMode(ScatterLayer s) { _active = _active == s ? null : s; _lineActive = null; _railConnectNodeA = -1; HideLinePreviews(); }
        void SetLineMode(ITerrainLineLayer l) { _lineActive = _lineActive == l ? null : l; _active = null; _railConnectNodeA = -1; HideLinePreviews(); }
        void HideLinePreviews() { FenceLayer.HidePreview(); PowerLineLayer.HidePreview(); RailLayer.HidePreview(); PlanLayer.HidePreview(); RailLayer.HideConnectPreview(); }

        // Live preview while a connect end is armed (C held + rail mode): the join to the
        // endpoint under the cursor, green/red, with a HUD line.
        void UpdateConnectPreview(RaycastHit hit, bool overTerrain)
        {
            _connectStatus = null;
            if (!(_lineActive is RailTrackLayer rc)) return;
            if (_railConnectNodeA >= rc.Graph.Nodes.Count) _railConnectNodeA = -1;   // stale after an edit
            if (_railConnectNodeA < 0 || !overTerrain || !Input.GetKey(KeyCode.C)) { rc.HideConnectPreview(); return; }
            int b = rc.NearestNodeForPick(new Vector2(hit.point.x, hit.point.z));
            if (b < 0 || b == _railConnectNodeA || !rc.IsEndpoint(b)) { rc.HideConnectPreview(); _connectStatus = "Connect: click end B."; return; }
            rc.TryConnectGeometry(_railConnectNodeA, b, out var cr);
            rc.RenderConnectPreview(_field, cr);
            _connectStatus = cr.Valid
                ? $"Connect → R {cr.Radius:0} m, max {cr.MaxSpeed:0} km/h — OK. Click end B."
                : $"Connect — {cr.Reason}.";
        }

#if UNITY_EDITOR
        [ContextMenu("Load Trees From Folder")]
        public void LoadTreesFromFolder()
        {
            if (TreeLayer.LoadFromFolder()) UnityEditor.EditorUtility.SetDirty(this);
        }

        [ContextMenu("Load Rocks From Folder")]
        public void LoadRocksFromFolder()
        {
            if (RockLayer.LoadFromFolder()) UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

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

            // Free-fly camera for roaming (the orbit camera fought zooming over a
            // large terrain). Remove any stray orbit controller from a prior setup.
            OrbitCameraController stray = cam.GetComponent<OrbitCameraController>();
            if (stray != null) { if (Application.isPlaying) Destroy(stray); else DestroyImmediate(stray); }

            FlyCameraController fly = cam.GetComponent<FlyCameraController>();
            bool fresh = fly == null;
            if (fresh) fly = cam.gameObject.AddComponent<FlyCameraController>();
            fly.ScrollSuppressor = () => MouseOverActivePanel() || CmdSpeedScroll();
            fly.GroundHeight = WorldGroundHeight; // terrain-aware altitude clamp
            if (fresh) FrameFly(fly);
        }

        float WorldGroundHeight(Vector3 p) => _field != null ? _field.SampleHeight(p.x, p.z) : 0f;

        // True while Cmd is held in rail/plan mode: the wheel adjusts the design speed
        // (and the camera ignores it, via ScrollSuppressor).
        bool CmdSpeedScroll() => RailLayer != null
            && (_lineActive is RailTrackLayer || _lineActive is RailPlanLayer)
            && (Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand));

        // URP render scale — the biggest lever for fill-rate-bound (high-res /
        // full-screen) rendering. Set at runtime on the pipeline asset (in-memory,
        // no asset-file churn). 1 = native; <1 renders fewer pixels and upscales.
        public float RenderScaleValue
        {
            get
            {
                var a = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                return a != null ? a.renderScale : 1f;
            }
            set
            {
                var a = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                if (a != null) a.renderScale = Mathf.Clamp(value, 0.3f, 2f);
            }
        }

        void FrameFly(FlyCameraController fly)
        {
            if (fly == null) return;
            float span = Mathf.Max((Mathf.Max(2, ColumnsX) - 1) * CellSize,
                                   (Mathf.Max(2, RowsZ) - 1) * CellSize);
            fly.Frame(transform.position, span);
        }

        // Drop the fly camera back to a sensible vantage over the terrain — escape
        // hatch if you've roamed off into space.
        [ContextMenu("Reset Camera")]
        public void ResetCamera()
        {
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            FlyCameraController fly = cam != null ? cam.GetComponent<FlyCameraController>() : null;
            if (fly == null) fly = FindFirstObjectByType<FlyCameraController>();
            FrameFly(fly);
        }

        void Update()
        {
            // Brush-mode hotkeys.
            if (Input.GetKeyDown(KeyCode.Alpha1)) Brush = BrushMode.Raise;
            else if (Input.GetKeyDown(KeyCode.Alpha2)) Brush = BrushMode.Lower;
            else if (Input.GetKeyDown(KeyCode.Alpha3)) Brush = BrushMode.Smooth;
            else if (Input.GetKeyDown(KeyCode.Alpha4)) Brush = BrushMode.Flatten;
            else if (Input.GetKeyDown(KeyCode.Alpha5)) Brush = BrushMode.Slope;
            // T/R/F toggle the active mode (mutually exclusive; press the same
            // key again to return to sculpt).
            if (Input.GetKeyDown(KeyCode.T)) SetScatterMode(TreeLayer);
            if (Input.GetKeyDown(KeyCode.R)) SetScatterMode(RockLayer);
            if (Input.GetKeyDown(KeyCode.F)) SetLineMode(FenceLayer);
            if (Input.GetKeyDown(KeyCode.P)) SetLineMode(PowerLineLayer);
            if (Input.GetKeyDown(KeyCode.L)) SetLineMode(RailLayer);
            if (Input.GetKeyDown(KeyCode.K)) SetLineMode(PlanLayer);
            if (Input.GetKeyDown(KeyCode.I) && RailLayer != null) RailLayer.ShowCurveInspect = !RailLayer.ShowCurveInspect;
            // Cmd + mouse wheel: nudge the shared design speed ±10 km/h per notch while in
            // rail/plan mode — set it without leaving the plan. The camera ignores the wheel
            // while Cmd is held (see ScrollSuppressor in the camera setup).
            if (CmdSpeedScroll())
            {
                int notches = Mathf.RoundToInt(Input.mouseScrollDelta.y);
                if (notches != 0)
                    RailLayer.SpeedLimitKmh = Mathf.Clamp(RailLayer.SpeedLimitKmh + notches * 10f, 10f, 200f);
            }
            if (Input.GetKeyDown(KeyCode.G))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (shift) SnapToGrid = !SnapToGrid;            // Shift+G: snap toggle
                else { GridEnabled = !GridEnabled; ApplyTerrainMaterial(); } // G: grid toggle
            }
            // B (in rail mode): toggle grade override — build across whatever terrain
            // the edge crosses instead of truncating at the grade limit.
            if (Input.GetKeyDown(KeyCode.B) && _lineActive is RailTrackLayer)
                RailLayer.OverrideGrade = !RailLayer.OverrideGrade;
            // Bake thumbnails only while NOT painting — the first render of each
            // prefab compiles its shader variant (a one-time editor stall), and
            // we don't want that landing mid-stroke.
            if (_active != null && !Input.GetMouseButton(0)) { _active.EnsureOneThumb(); _active.EnsureModalThumb(); }

            // Brush resize: ] bigger, [ smaller (held = continuous).
            if (Input.GetKey(KeyCode.RightBracket)) BrushRadius += BrushResizeRate * Time.deltaTime;
            if (Input.GetKey(KeyCode.LeftBracket)) BrushRadius -= BrushResizeRate * Time.deltaTime;
            BrushRadius = Mathf.Clamp(BrushRadius, 0.5f, MaxBrushRadius);

            if (_field == null) return;

            // Sample the camera pose every frame so it can still be saved at teardown
            // (when the live camera may be gone). A move also marks dirty so the
            // debounced autosave captures the new vantage (not just terrain edits);
            // the debounce coalesces continuous flying into one save when you stop.
            if (TryGetCameraPose(out Vector3 cp, out float cy, out float cpi))
            {
                if (Autosave && _haveLastCam && ((cp - _lastCamPos).sqrMagnitude > 1e-4f
                    || Mathf.Abs(Mathf.DeltaAngle(cy, _lastCamYaw)) > 0.05f
                    || Mathf.Abs(cpi - _lastCamPitch) > 0.05f))
                    _dirtySince = Time.realtimeSinceStartup;
                _lastCamPos = cp; _lastCamYaw = cy; _lastCamPitch = cpi; _haveLastCam = true;
            }

            // Debounced autosave: write once sculpting/flying has paused.
            if (Autosave && _dirtySince >= 0f
                && Time.realtimeSinceStartup - _dirtySince >= AutosaveDebounceSeconds)
            {
                SaveTerrain(); // clears _dirtySince only if a write actually starts
            }

            // A picked (right-click) flatten height persists across strokes; only
            // the auto-sample-at-stroke-start mode re-captures per left-click.
            if (Input.GetMouseButtonDown(0) && !_flattenTargetPicked) _hasFlattenTarget = false;
            // A picked target only makes sense in Flatten mode — drop it otherwise.
            if (Brush != BrushMode.Flatten) _flattenTargetPicked = false;
            // Slope arming is only valid while the Slope brush is the active tool.
            if (Brush != BrushMode.Slope || _active != null || _lineActive != null)
            { _slopeArmed = false; _slopeHasGuide = false; }

            // One hover raycast per frame (against the TerrainCollider), shared
            // by the brush cursor and the sculpt itself.
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            bool overTerrain = false;
            RaycastHit hit = default;
            if (cam != null)
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                overTerrain = Physics.Raycast(ray, out hit, 100000f)
                              && hit.collider is MeshCollider;
            }

            // Flatten mode: remember the world elevation under the cursor for the HUD.
            _flattenCursorValid = Brush == BrushMode.Flatten && overTerrain
                                  && _active == null && _lineActive == null;
            if (_flattenCursorValid) _flattenCursorElev = hit.point.y;

            // Slope tool: while armed, resolve this frame's end point (snapped to the
            // network "straight" guide when near it) and the live grade — BEFORE the
            // cursor/corridor overlay draws so it reflects the snap.
            _slopeEndValid = false;
            _slopePath = null;
            if (_slopeArmed && Brush == BrushMode.Slope && overTerrain)
            {
                Vector2 a2 = new Vector2(_slopeA.x, _slopeA.z);
                Vector2 c2 = new Vector2(hit.point.x, hit.point.z);
                // B snaps onto the plan centreline first (ride the planned alignment),
                // then a rail end/edge (same as rail placement), else onto the
                // "straight" guide line through A. (All off when rail snap is disabled —
                // _slopeHasGuide is already false then.)
                bool bOnPlan = false;
                if (!SlopeDisableRailSnap && PlanLayer != null
                    && PlanLayer.TryNearestOnPlan(c2, SlopeGuideDetectRadius, out Vector2 pSnap, out _))
                { c2 = pSnap; bOnPlan = true; }
                else if (!SlopeDisableRailSnap && RailLayer != null && RailLayer.TrySnapToTrackPoint(c2, out Vector2 bSnap))
                    c2 = bSnap;
                else if (_slopeHasGuide)
                {
                    Vector2 proj = a2 + _slopeGuideDir * Vector2.Dot(c2 - a2, _slopeGuideDir);
                    if ((c2 - proj).sqrMagnitude <= SlopeGuideSnapRadius * SlopeGuideSnapRadius) c2 = proj;
                }
                // On the plan: keep the brush sized to the corridor as the cursor moves.
                if (bOnPlan && PlanLayer != null)
                    BrushRadius = Mathf.Clamp(PlanLayer.CorridorWidth * 0.5f, 0.5f, MaxBrushRadius);
                _slopeEnd = new Vector3(c2.x, hit.point.y, c2.y);
                _slopeEndValid = true;
                // The plan-centreline path A->end (when both ends sit on the connected
                // plan). Computed once here and reused by the fill, overlay, and commit.
                if (!SlopeDisableRailSnap && PlanLayer != null
                    && PlanLayer.TryPathBetween(a2, c2, SlopeGuideDetectRadius, out List<Vector2> spath))
                    _slopePath = spath;
                GridFromWorld(_slopeEnd, out float sex, out float sez);
                float run = Vector2.Distance(a2, c2);
                _slopeGradePct = run > 1e-3f ? (HeightAtGrid(sex, sez) - _slopeElevA) / run * 100f : 0f;
            }
            // Before A is placed: if the hover cursor is on the plan, size the brush to
            // the corridor too, so the ring previews the right width ahead of the click.
            else if (Brush == BrushMode.Slope && overTerrain && !SlopeDisableRailSnap && PlanLayer != null
                     && _active == null && _lineActive == null
                     && PlanLayer.TryNearestOnPlan(new Vector2(hit.point.x, hit.point.z),
                                                   SlopeGuideDetectRadius, out _, out _))
                BrushRadius = Mathf.Clamp(PlanLayer.CorridorWidth * 0.5f, 0.5f, MaxBrushRadius);

            // Rail auto-slope: while node A is armed, resolve this frame's preview path
            // to the node under the cursor and its resulting grade (BEFORE the fill draws).
            ResolveRailSlopePreview(overTerrain ? new Vector2(hit.point.x, hit.point.z) : new Vector2(1e9f, 1e9f));

            // Rail/Plan: hold Shift = curve mode (else straight). Set BEFORE SnapCursor and
            // the ring rebuild so the bend/MDT/extension snap and preview reflect THIS
            // frame's modifier — otherwise a Shift pressed after the first click lags a
            // frame, the bend snap doesn't fire, and the bend lands inside the MDT (leaving
            // an empty PAC so the end can't be constrained).
            if (_lineActive != null)
            {
                bool curveModNow = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (_lineActive is RailTrackLayer railModNow) railModNow.CurveModifier = curveModNow;
                else if (_lineActive is RailPlanLayer planModNow)
                {
                    planModNow.CurveModifier = curveModNow;
                    // Seed the extension guide from the rail end the plan started on,
                    // so the first segment can carry straight off the track.
                    planModNow.HasSeedDir = false;
                    if (planModNow.TryGetTailXZ(out Vector2 ptailNow) && RailLayer != null
                        && RailLayer.TryEndHeading(ptailNow, 3f, out Vector2 seedNow))
                    { planModNow.SeedDir = seedNow; planModNow.HasSeedDir = true; }
                }
            }

            // Resolve the snapped cursor for the active tool ONCE, so the brush ring
            // sits exactly where placement will land (track / extension / grid / slope
            // guide). Scatter & plain sculpt don't snap.
            Vector3 cursorVis = SnapCursor(hit.point, overTerrain);
            // The sculpt brush ring is irrelevant in line modes (rail/plan have their own
            // cursor + rings) — hide it so it doesn't clutter curve drawing.
            UpdateBrushCursor(ShowBrushCursor && overTerrain && _lineActive == null, cursorVis);
            UpdateSlopeFill();
            // Node pucks: shown while rail is the active line layer; the node under the
            // cursor (and the armed auto-slope node A) highlight.
            if (RailLayer != null)
            {
                RailLayer.UpdateNodePucks(_field,
                    overTerrain ? new Vector2(hit.point.x, hit.point.z) : new Vector2(1e9f, 1e9f),
                    _lineActive is RailTrackLayer, _railSlopeNodeA >= 0 ? _railSlopeNodeA : _railConnectNodeA);
                RailLayer.RebuildBraking(_field, cursorVis, _lineActive is RailTrackLayer);
                // One design speed for the whole network: the plan mirrors the rail's.
                if (PlanLayer != null)
                { PlanLayer.SpeedLimitKmh = RailLayer.SpeedLimitKmh; PlanLayer.MaxLateralG = RailLayer.MaxLateralG; }
                // Curve-inspection overlay: hover off the raw cursor; plan mirrors the toggle.
                Vector3 inspCur = overTerrain ? hit.point : new Vector3(1e9f, 0f, 1e9f);
                RailLayer.RebuildCurveInspect(_field, inspCur, _lineActive is RailTrackLayer);
                if (PlanLayer != null)
                {
                    PlanLayer.ShowCurveInspect = RailLayer.ShowCurveInspect;
                    PlanLayer.CurveInspectWidth = RailLayer.CurveInspectWidth;
                    PlanLayer.TypicalTrainLengthM = RailLayer.TypicalTrainLengthM;
                    PlanLayer.RebuildCurveInspect(_field, inspCur, _lineActive is RailPlanLayer);
                }
                UpdateConnectPreview(hit, overTerrain);
            }
            // Remember the placement cursor + whether it's over terrain, for the on-screen
            // design-speed readout drawn in OnGUI.
            _lineCursorWorld = cursorVis; _lineCursorValid = overTerrain
                && (_lineActive is RailTrackLayer || _lineActive is RailPlanLayer);

            // Linework mode (fence/…): click adds a node + connects from the last
            // (chain); right-click ends the chain; Backspace undoes the last node.
            if (_lineActive != null)
            {
                // (Curve modifier + plan seed-dir were set above, before SnapCursor.)

                // The snapped placement point (same one the ring shows). Deletes use
                // the raw hit so you can remove a node you're not snapping to.
                Vector3 place = cursorVis;
                _lineActive.UpdatePreview(_field, place, overTerrain);
                bool altMod = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                bool connectMod = Input.GetKey(KeyCode.C);
                if (overTerrain && Input.GetMouseButtonDown(0))
                {
                    if (_lineActive is RailTrackLayer railSlope && altMod)
                    {
                        // Rail auto-slope: Alt+click node A, then node B → grade between.
                        int n = railSlope.NearestNodeForPick(new Vector2(hit.point.x, hit.point.z));
                        if (n >= 0)
                        {
                            if (_railSlopeNodeA < 0) _railSlopeNodeA = n;     // pick A
                            else { ApplyRailSlope(n); _railSlopeNodeA = -1; } // pick B → apply
                        }
                    }
                    else if (_lineActive is RailTrackLayer railConn && connectMod)
                    {
                        // Rail connect: C+click endpoint A, then endpoint B → fillet join.
                        int n = railConn.NearestNodeForPick(new Vector2(hit.point.x, hit.point.z));
                        if (n >= 0 && railConn.IsEndpoint(n))
                        {
                            if (_railConnectNodeA < 0) _railConnectNodeA = n;          // pick A
                            else if (railConn.TryConnectGeometry(_railConnectNodeA, n, out var cr) && cr.Valid)
                            {
                                railConn.CommitConnect(_field, cr); _dirtySince = Time.realtimeSinceStartup;
                                _railConnectNodeA = -1; railConn.HideConnectPreview();
                            } // invalid B → keep A armed so a different B can be picked
                        }
                    }
                    else
                    {
                        _lineActive.AddNode(_field, place);
                        _dirtySince = Time.realtimeSinceStartup;
                    }
                }
                if (Input.GetMouseButtonDown(1))
                {
                    // An armed auto-slope / connect cancels first (right-click backs out).
                    if (_railConnectNodeA >= 0) { _railConnectNodeA = -1; if (_lineActive is RailTrackLayer rcx) rcx.HideConnectPreview(); }
                    else if (_railSlopeNodeA >= 0) _railSlopeNodeA = -1;
                    else DeleteOrEndChain(hit, overTerrain);
                }
                if (Input.GetKeyDown(KeyCode.Backspace))
                {
                    _lineActive.RemoveLastNode(_field);
                    _dirtySince = Time.realtimeSinceStartup;
                }
                return;
            }

            // Scatter mode (trees/rocks): left-drag paints, right-drag erases;
            // no sculpting. Suppressed while the cursor is over the palette.
            if (_active != null)
            {
                bool overPanel = MouseOverActivePanel();
                if (!overPanel && overTerrain && Input.GetMouseButton(0)
                    && _active.Paint(_field, hit.point, Time.deltaTime, BrushRadius,
                                     ShowWater ? WaterLevel : float.NegativeInfinity))
                    _dirtySince = Time.realtimeSinceStartup;
                if (!overPanel && overTerrain && Input.GetMouseButton(1)
                    && _active.Erase(hit.point, BrushRadius))
                    _dirtySince = Time.realtimeSinceStartup;
                return;
            }

            // Slope tool: two-click ramp. Click A (capture start elevation), move
            // (corridor + live grade preview, snapping to the rail "straight"), click
            // B (capture end elevation) -> grade the corridor linearly between them.
            // Right-click cancels the armed point.
            if (Brush == BrushMode.Slope)
            {
                if (overTerrain && Input.GetMouseButtonDown(0))
                {
                    if (!_slopeArmed)
                    {
                        GridFromWorld(hit.point, out float ax, out float az);
                        _slopeA = hit.point;
                        _slopeElevA = HeightAtGrid(ax, az);
                        _slopeArmed = true;
                        // Network-aware: snap A onto rail the SAME way the rail tool
                        // does — nearest node (edge END) first, then nearest edge point,
                        // within TrackSnapRadius. Beyond that, fall back to the nearest
                        // edge point within the (larger) detect radius. Either way A
                        // lands ON the rail, and the "straight" guide is the track's
                        // heading there — so the slope path is centred on the rail.
                        _slopeHasGuide = false;
                        if (!SlopeDisableRailSnap)
                        {
                            Vector2 a2 = new Vector2(hit.point.x, hit.point.z);
                            // Snap A exactly where the pre-click cursor previewed it
                            // (plan centreline first, then rail).
                            if (TrySlopeSnap(a2, out Vector2 snapA, out Vector2 snapDir, out bool onPlan))
                            {
                                _slopeA = new Vector3(snapA.x, hit.point.y, snapA.y);
                                GridFromWorld(_slopeA, out float rax, out float raz);
                                _slopeElevA = HeightAtGrid(rax, raz);
                                a2 = snapA;
                                // The "straight" guide is the heading at the snapped A.
                                if (snapDir.sqrMagnitude > 1e-6f) { _slopeGuideDir = snapDir; _slopeHasGuide = true; }
                                // On the plan: size the brush to the planned corridor so a
                                // single fill/slope covers the whole pathway width.
                                if (onPlan && PlanLayer != null)
                                    BrushRadius = Mathf.Clamp(PlanLayer.CorridorWidth * 0.5f, 0.5f, MaxBrushRadius);
                            }
                        }
                    }
                    else if (_slopeEndValid)
                    {
                        GridFromWorld(_slopeEnd, out float ex, out float ez);
                        float elevB = HeightAtGrid(ex, ez);
                        // If both ends sit on the plan and the graph connects them, grade
                        // the corridor ALONG the planned centreline (following curves);
                        // otherwise fall back to the straight A→B corridor.
                        if (_slopePath != null)
                            ApplySlopeAlongPath(_slopePath, _slopeElevA, elevB, BrushRadius);
                        else
                            ApplySlope(_slopeA, _slopeEnd, _slopeElevA, elevB);
                        _slopeArmed = false; _slopeHasGuide = false;
                        _dirtySince = Time.realtimeSinceStartup;
                        RebuildContours();
                        ConformScatterAndLines();
                    }
                }
                if (Input.GetMouseButtonDown(1)) { _slopeArmed = false; _slopeHasGuide = false; }
                return;
            }

            // Flatten mode: right-click samples a target height (the eyedropper).
            // The pick persists, so a following left-click/drag makes everything
            // under the brush that exact height — instead of the height the stroke
            // happened to start on.
            if (Brush == BrushMode.Flatten && overTerrain && Input.GetMouseButtonDown(1))
            {
                GridFromWorld(hit.point, out float pfx, out float pfz);
                _flattenTarget = HeightAtGrid(pfx, pfz);
                _flattenTargetPicked = true;
                _hasFlattenTarget = true;
            }

            // On stroke end: refresh contours, and re-settle trees/rocks/fences onto
            // the new surface (so they don't float/sink where the brush moved ground).
            if (Input.GetMouseButtonUp(0))
            {
                RebuildContours();
                if (_sculptedStroke) { ConformScatterAndLines(); _sculptedStroke = false; }
            }

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
            RebuildChunkRegion(rx0, rz0, rw, rw);   // rebuild touched chunk meshes + colliders
            _dirtySince = Time.realtimeSinceStartup;
            _sculptedStroke = true;                 // conform scatter/lines on mouse-up
            if (LiveContours) RebuildContours();
            if (LiveConform) ConformScatterAndLines();
        }

        // Re-settle trees/rocks onto the surface and rebuild fence/power lines after
        // the terrain height changed (sculpt/slope). Rail is left alone (it has its
        // own constant-grade / carve model). Cheap enough for stroke-end; per-frame
        // only under LiveConform.
        void ConformScatterAndLines()
        {
            if (_field == null) return;
            TreeLayer.ConformToSurface(_field);
            RockLayer.ConformToSurface(_field);
            FenceLayer.Rebuild(_field);
            PowerLineLayer.Rebuild(_field);
            PlanLayer.Rebuild(_field); // re-drape the survey lines onto the new surface
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

            // Slope tool: append the corridor edges + the network "straight" guide.
            if (_slopeArmed && Brush == BrushMode.Slope && _active == null && _lineActive == null)
                AppendSlopeOverlay();

            _cursorMesh.Clear();
            _cursorMesh.SetVertices(_cursorVerts);
            _cursorMesh.SetIndices(_cursorIdx, MeshTopology.Lines, 0);
            _cursorMesh.RecalculateBounds();
            _cursorMf.sharedMesh = _cursorMesh;
        }

        // The slope tool's overlay (appended into the cursor line mesh): two dashed
        // corridor edges at ±BrushRadius from the A→end axis, a small ring at A, and
        // — when A snapped to the rail network — the dashed "straight" guide line.
        void AppendSlopeOverlay()
        {
            Vector2 a2 = new Vector2(_slopeA.x, _slopeA.z);
            Vector2 e2 = _slopeEndValid ? new Vector2(_slopeEnd.x, _slopeEnd.z) : a2;
            float dash = BrushCursorDashLength, gap = BrushCursorDashGap;
            if (_slopeHasGuide)
            {
                const float gl = 150f; // guide length each way through A
                EmitDrapedDashed(a2 - _slopeGuideDir * gl, a2 + _slopeGuideDir * gl, dash, gap);
            }
            // On a plan path the curved fill ribbon already shows the corridor, and a
            // straight A->end dashed pair would cut across it — so only draw the dashed
            // corridor edges for the straight (off-plan) case.
            Vector2 axis = e2 - a2;
            if (_slopePath == null && axis.sqrMagnitude > 1e-4f)
            {
                Vector2 dir = axis.normalized;
                Vector2 perp = new Vector2(-dir.y, dir.x) * BrushRadius;
                EmitDrapedDashed(a2 + perp, e2 + perp, dash, gap);
                EmitDrapedDashed(a2 - perp, e2 - perp, dash, gap);
            }
            AppendRing(a2, 0.9f); // start marker
        }

        // A draped, dashed line between two XZ points (sampled to the terrain).
        void EmitDrapedDashed(Vector2 a2, Vector2 b2, float dash, float gap)
        {
            float len = Vector2.Distance(a2, b2);
            if (len < 1e-3f) return;
            Vector2 dir = (b2 - a2) / len;
            float period = (dash > 0f ? dash : 1f) + Mathf.Max(0f, gap);
            for (float pos = 0f; pos < len; pos += period)
            {
                float e0 = pos, e1 = Mathf.Min(pos + (dash > 0f ? dash : 1f), len);
                Vector2 p0 = a2 + dir * e0, p1 = a2 + dir * e1;
                int s = _cursorVerts.Count;
                _cursorVerts.Add(new Vector3(p0.x, _field.SampleHeight(p0.x, p0.y) + BrushCursorLift, p0.y));
                _cursorVerts.Add(new Vector3(p1.x, _field.SampleHeight(p1.x, p1.y) + BrushCursorLift, p1.y));
                _cursorIdx.Add(s); _cursorIdx.Add(s + 1);
            }
        }

        // A small draped ring marker (solid) at an XZ centre.
        void AppendRing(Vector2 c, float r)
        {
            const int n = 16;
            Vector3 prev = default;
            for (int i = 0; i <= n; i++)
            {
                float ang = i / (float)n * Mathf.PI * 2f;
                float wx = c.x + Mathf.Cos(ang) * r, wz = c.y + Mathf.Sin(ang) * r;
                Vector3 cur = new Vector3(wx, _field.SampleHeight(wx, wz) + BrushCursorLift, wz);
                if (i > 0) { int s = _cursorVerts.Count; _cursorVerts.Add(prev); _cursorVerts.Add(cur); _cursorIdx.Add(s); _cursorIdx.Add(s + 1); }
                prev = cur;
            }
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
            GameObject go = MakeRuntimeRoot("BrushCursor");
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

        // Slope tool: rebuild the translucent fill that previews the corridor about to
        // be graded. While armed, it spans A -> the live (snapped) end; when both ends
        // sit on the plan and connect, it follows the planned centreline around curves.
        void UpdateSlopeFill()
        {
            EnsureSlopeFill();
            // Two sources feed the same fill ribbon: the brush slope tool, and the rail
            // node-to-node auto-slope preview. Pick whichever is active this frame.
            List<Vector2> path = null;
            float halfW = 0f;
            bool tooSteep = false;
            bool brushSlope = _slopeArmed && _slopeEndValid && Brush == BrushMode.Slope
                              && _active == null && _lineActive == null;
            if (brushSlope)
            {
                Vector2 a2 = new Vector2(_slopeA.x, _slopeA.z);
                Vector2 e2 = new Vector2(_slopeEnd.x, _slopeEnd.z);
                path = _slopePath ?? new List<Vector2> { a2, e2 };
                halfW = BrushRadius;
            }
            else if (_railSlopePath != null && _lineActive is RailTrackLayer)
            {
                path = _railSlopePath;
                halfW = RailSlopeWidth * 0.5f;
                tooSteep = !_railSlopeGradeOk;   // tint red when it would exceed max grade
            }

            bool show = path != null && ShowBrushCursor && _field != null;
            _slopeFillMr.enabled = show;
            if (!show) return;
            if (_slopeFillMat != null)
                _slopeFillMat.color = tooSteep ? new Color(1f, 0.3f, 0.25f, SlopeFillColor.a) : SlopeFillColor;
            BuildSlopeRibbon(path, Mathf.Max(_field.CellSize, halfW));
        }

        // Rail auto-slope: resolve the armed-A -> hovered-node preview path + grade, and
        // clear the arm if rail mode was left or A no longer exists.
        void ResolveRailSlopePreview(Vector2 cursorXz)
        {
            _railSlopePath = null;
            if (!(_lineActive is RailTrackLayer) || RailLayer == null || _field == null)
            { _railSlopeNodeA = -1; return; }
            if (_railSlopeNodeA >= RailLayer.Graph.Nodes.Count) _railSlopeNodeA = -1;
            if (_railSlopeNodeA < 0) return;
            int hb = RailLayer.NearestNodeForPick(cursorXz);
            if (hb < 0 || hb == _railSlopeNodeA) return;
            if (!RailLayer.TryCenterlinePath(_railSlopeNodeA, hb, out List<Vector2> rp)) return;
            _railSlopePath = rp;
            Vector2 pa = RailLayer.Graph.Nodes[_railSlopeNodeA], pb = RailLayer.Graph.Nodes[hb];
            float ea = _field.SampleHeight(pa.x, pa.y), eb = _field.SampleHeight(pb.x, pb.y);
            float len = PathLengthXZ(rp);
            _railSlopeGradePct = len > 1e-3f ? (eb - ea) / len * 100f : 0f;
            float gradeDeg = len > 1e-3f ? Mathf.Atan2(Mathf.Abs(eb - ea), len) * Mathf.Rad2Deg : 0f;
            _railSlopeGradeOk = gradeDeg <= RailLayer.MaxGradeDeg;
        }

        // Grade the rail bed between the armed node A and nodeB to a constant ramp (from
        // A's to B's current terrain height), only if that stays within the rail's max
        // grade. Then re-sit the track on the new bed.
        void ApplyRailSlope(int nodeB)
        {
            if (RailLayer == null || _field == null || _railSlopeNodeA < 0 || nodeB < 0
                || nodeB == _railSlopeNodeA) return;
            if (!RailLayer.TryCenterlinePath(_railSlopeNodeA, nodeB, out List<Vector2> path))
            { Debug.LogWarning("[Rail slope] Those two nodes aren't connected."); return; }
            Vector2 pa = RailLayer.Graph.Nodes[_railSlopeNodeA], pb = RailLayer.Graph.Nodes[nodeB];
            float ea = _field.SampleHeight(pa.x, pa.y), eb = _field.SampleHeight(pb.x, pb.y);
            float len = PathLengthXZ(path);
            float gradeDeg = len > 1e-3f ? Mathf.Atan2(Mathf.Abs(eb - ea), len) * Mathf.Rad2Deg : 0f;
            if (gradeDeg > RailLayer.MaxGradeDeg)
            {
                Debug.LogWarning($"[Rail slope] {gradeDeg:0.0}° exceeds the {RailLayer.MaxGradeDeg:0.0}° "
                    + "max — the endpoints are too far apart in height for this span. Not graded.");
                return;
            }
            ApplySlopeAlongPath(path, ea, eb, Mathf.Max(_field.CellSize, RailSlopeWidth * 0.5f));
            _dirtySince = Time.realtimeSinceStartup;
            RebuildContours();
            ConformScatterAndLines();
            RebuildRail();   // re-sit the track on the freshly graded bed
            Debug.Log($"[Rail slope] Graded {len:0} m at {gradeDeg:0.0}° between nodes {_railSlopeNodeA} and {nodeB}.");
        }

        static float PathLengthXZ(List<Vector2> p)
        {
            float L = 0f;
            for (int i = 1; i < p.Count; i++) L += Vector2.Distance(p[i - 1], p[i]);
            return L;
        }

        // Right-click in line mode: delete the snapped/under-cursor node, else end the
        // chain. (Extracted so the rail auto-slope can intercept right-click to cancel.)
        void DeleteOrEndChain(RaycastHit hit, bool overTerrain)
        {
            bool deleted = false;
            if (overTerrain)
            {
                Vector2 dflat = new Vector2(hit.point.x, hit.point.z);
                if (_lineActive is RailTrackLayer rlDel && rlDel.TrySnapToTrack(dflat, out Vector2 dsnap))
                    deleted = rlDel.DeleteNearNode(_field, new Vector3(dsnap.x, hit.point.y, dsnap.y), 2f);
                else if (_lineActive is RailPlanLayer plDel && plDel.TrySnapToOwnNode(dflat, out Vector2 psnap))
                    deleted = plDel.DeleteNearNode(_field, new Vector3(psnap.x, hit.point.y, psnap.y), 2f);
                if (!deleted) deleted = _lineActive.DeleteNearNode(_field, hit.point, 3f);
            }
            if (deleted) _dirtySince = Time.realtimeSinceStartup;
            else { _lineActive.EndChain(); _dirtySince = Time.realtimeSinceStartup; } // may have dropped an orphan tail
        }

        // Build a draped triangle ribbon of half-width `halfW` centred on `path`.
        void BuildSlopeRibbon(List<Vector2> path, float halfW)
        {
            _fillVerts.Clear(); _fillIdx.Clear();
            int n = path != null ? path.Count : 0;
            if (n >= 2)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector2 tan = i == 0 ? path[1] - path[0]
                                : i == n - 1 ? path[n - 1] - path[n - 2]
                                : path[i + 1] - path[i - 1];
                    if (tan.sqrMagnitude < 1e-8f) tan = Vector2.right;
                    tan.Normalize();
                    Vector2 perp = new Vector2(-tan.y, tan.x) * halfW;
                    Vector2 lft = path[i] + perp, rgt = path[i] - perp;
                    _fillVerts.Add(new Vector3(lft.x, _field.SampleHeight(lft.x, lft.y) + BrushCursorLift, lft.y));
                    _fillVerts.Add(new Vector3(rgt.x, _field.SampleHeight(rgt.x, rgt.y) + BrushCursorLift, rgt.y));
                }
                for (int i = 0; i < n - 1; i++)
                {
                    int b = i * 2; // L_i, R_i, L_i+1, R_i+1  (Cull Off so winding is moot)
                    _fillIdx.Add(b); _fillIdx.Add(b + 2); _fillIdx.Add(b + 1);
                    _fillIdx.Add(b + 1); _fillIdx.Add(b + 2); _fillIdx.Add(b + 3);
                }
            }
            _slopeFillMesh.Clear();
            _slopeFillMesh.SetVertices(_fillVerts);
            _slopeFillMesh.SetTriangles(_fillIdx, 0);
            _slopeFillMesh.RecalculateBounds();
            _slopeFillMf.sharedMesh = _slopeFillMesh;
        }

        void EnsureSlopeFill()
        {
            if (_slopeFillMf != null) return;
            GameObject go = MakeRuntimeRoot("SlopeFill");
            _slopeFillMf = go.AddComponent<MeshFilter>();
            _slopeFillMr = go.AddComponent<MeshRenderer>();
            _slopeFillMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _slopeFillMr.receiveShadows = false;
            _slopeFillMesh = new Mesh { name = "SlopeFillMesh" };
            _slopeFillMf.sharedMesh = _slopeFillMesh;
            // Transparent, always-on-top (same shader as the cursor ring, Cull Off).
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            _slopeFillMat = sh != null
                ? new Material(sh) { name = "SlopeFillMat", color = SlopeFillColor }
                : PipelineMaterials.CreateUnlitColor(SlopeFillColor, "SlopeFillMat");
            _slopeFillMr.sharedMaterial = _slopeFillMat;
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
            GameObject go = MakeRuntimeRoot("ContourLines");
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

            // Effective Raise/Lower rate = Strength^exp. exp is floored at 1 so a
            // stale/zero serialized value can't collapse it to a constant 1 m/s.
            float exp = Mathf.Max(1f, BrushStrengthExponent);
            float effStrength = Mathf.Pow(Mathf.Max(0f, BrushStrength), exp);

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
                            h += effStrength * dt * w;
                            break;
                        case BrushMode.Lower:
                            h -= effStrength * dt * w;
                            break;
                        case BrushMode.Flatten:
                            h = Mathf.Lerp(h, _flattenTarget, Mathf.Clamp01(dt * Mathf.Max(0.1f, FlattenStrength) * w));
                            break;
                        case BrushMode.Smooth:
                            h = Mathf.Lerp(h, NeighborAverage(x, z), Mathf.Clamp01(dt * 4f * w));
                            break;
                    }
                    _field.SetHeight(x, z, h);
                }
            }
        }

        // Slope tool: grade the corridor between A and B (width = brush diameter) to
        // a linear ramp from elevA to elevB. Flat across the corridor (it IS the
        // ramp bed), feathering to the existing terrain only near the side edges
        // (BrushFalloff controls how wide that feather is). The ends meet the terrain
        // cleanly because elevA/elevB were sampled there.
        void ApplySlope(Vector3 aWorld, Vector3 bWorld, float elevA, float elevB)
        {
            if (_field == null) return;
            float cs = _field.CellSize;
            Vector2 a = new Vector2(aWorld.x, aWorld.z);
            Vector2 b = new Vector2(bWorld.x, bWorld.z);
            Vector2 axis = b - a;
            float L = axis.magnitude;
            if (L < 1e-3f) return;
            Vector2 dir = axis / L;
            Vector2 perpDir = new Vector2(-dir.y, dir.x);
            float halfW = Mathf.Max(cs, BrushRadius);
            float feather = Mathf.Clamp01(BrushFalloff);
            float inner = 1f - feather; // fraction of half-width held at full strength

            GridFromWorld(aWorld, out float afx, out float afz);
            GridFromWorld(bWorld, out float bfx, out float bfz);
            int pad = Mathf.CeilToInt(halfW / cs) + 1;
            int minX = Mathf.FloorToInt(Mathf.Min(afx, bfx)) - pad;
            int maxX = Mathf.CeilToInt(Mathf.Max(afx, bfx)) + pad;
            int minZ = Mathf.FloorToInt(Mathf.Min(afz, bfz)) - pad;
            int maxZ = Mathf.CeilToInt(Mathf.Max(afz, bfz)) + pad;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!_field.InRange(x, z)) continue;
                    Vector2 rel = new Vector2(_field.Origin.x + x * cs, _field.Origin.z + z * cs) - a;
                    float along = Vector2.Dot(rel, dir);
                    if (along < 0f || along > L) continue;             // only between A and B
                    float perp = Mathf.Abs(Vector2.Dot(rel, perpDir));
                    if (perp > halfW) continue;                        // outside the corridor
                    float target = Mathf.Lerp(elevA, elevB, along / L);
                    float tEdge = halfW > 0f ? perp / halfW : 0f;
                    float w = tEdge <= inner ? 1f
                        : 1f - Mathf.SmoothStep(0f, 1f, (tEdge - inner) / Mathf.Max(1e-3f, feather));
                    float h = _field.GetHeight(x, z);
                    _field.SetHeight(x, z, Mathf.Lerp(h, target, w));
                }
            }
            RebuildChunkRegion(minX, minZ, (maxX - minX) + 1, (maxZ - minZ) + 1);
        }

        // Slope tool, plan-aware: grade a corridor that FOLLOWS a polyline (the plan
        // centreline through curves) from elevA at the start to elevB at the end. Same
        // feel as ApplySlope but the ramp parameter is arc-length along the path, and
        // each cell is graded by its nearest path segment. halfWidth is the corridor
        // half-width (caller passes the brush radius, already sized to the corridor).
        void ApplySlopeAlongPath(List<Vector2> path, float elevA, float elevB, float halfWidth)
        {
            if (_field == null || path == null || path.Count < 2) return;
            float cs = _field.CellSize;
            float halfW = Mathf.Max(cs, halfWidth);
            float feather = Mathf.Clamp01(BrushFalloff);
            float inner = 1f - feather;

            // Cumulative arc length at each path vertex (for the ramp parameter) and the
            // XZ bounding box of the whole corridor.
            int n = path.Count;
            float[] cum = new float[n];
            float total = 0f;
            float minPx = path[0].x, maxPx = path[0].x, minPz = path[0].y, maxPz = path[0].y;
            for (int i = 1; i < n; i++)
            {
                total += Vector2.Distance(path[i - 1], path[i]);
                cum[i] = total;
                minPx = Mathf.Min(minPx, path[i].x); maxPx = Mathf.Max(maxPx, path[i].x);
                minPz = Mathf.Min(minPz, path[i].y); maxPz = Mathf.Max(maxPz, path[i].y);
            }
            if (total < 1e-3f) return;

            int pad = Mathf.CeilToInt(halfW / cs) + 1;
            int minX = Mathf.FloorToInt((minPx - halfW - _field.Origin.x) / cs) - 1;
            int maxX = Mathf.CeilToInt((maxPx + halfW - _field.Origin.x) / cs) + 1;
            int minZ = Mathf.FloorToInt((minPz - halfW - _field.Origin.z) / cs) - 1;
            int maxZ = Mathf.CeilToInt((maxPz + halfW - _field.Origin.z) / cs) + 1;
            minX -= pad; maxX += pad; minZ -= pad; maxZ += pad;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!_field.InRange(x, z)) continue;
                    Vector2 p = new Vector2(_field.Origin.x + x * cs, _field.Origin.z + z * cs);
                    // Nearest point over all path segments → perpendicular distance + arc pos.
                    float bestPerp = float.MaxValue, bestArc = 0f;
                    for (int i = 0; i < n - 1; i++)
                    {
                        Vector2 s0 = path[i], s1 = path[i + 1];
                        Vector2 seg = s1 - s0;
                        float segLen2 = seg.sqrMagnitude;
                        if (segLen2 < 1e-9f) continue;
                        float t = Mathf.Clamp01(Vector2.Dot(p - s0, seg) / segLen2);
                        Vector2 proj = s0 + seg * t;
                        float d = (p - proj).magnitude;
                        if (d < bestPerp)
                        {
                            bestPerp = d;
                            bestArc = cum[i] + Mathf.Sqrt(segLen2) * t;
                        }
                    }
                    if (bestPerp > halfW) continue;                 // outside the corridor
                    float target = Mathf.Lerp(elevA, elevB, bestArc / total);
                    float tEdge = halfW > 0f ? bestPerp / halfW : 0f;
                    float w = tEdge <= inner ? 1f
                        : 1f - Mathf.SmoothStep(0f, 1f, (tEdge - inner) / Mathf.Max(1e-3f, feather));
                    float h = _field.GetHeight(x, z);
                    _field.SetHeight(x, z, Mathf.Lerp(h, target, w));
                }
            }
            RebuildChunkRegion(minX, minZ, (maxX - minX) + 1, (maxZ - minZ) + 1);
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
            float size = Mathf.Max(1f, TerrainSizeMeters);
            float cs = Mathf.Max(0.1f, CellSize);
            int res = Mathf.Max(2, Mathf.RoundToInt(size / cs) + 1); // vertices per side
            float half = (res - 1) * cs * 0.5f;
            Vector3 origin = transform.position - new Vector3(half, 0f, half); // centered on this object

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

        // Rebuild the terrain at the current TerrainSizeMeters / CellSize / ChunkCells
        // — for experimenting with map size live. WIPES the heightfield to flat
        // (re-import a heightmap after). Big sizes can stall while building.
        [ContextMenu("Rebuild Terrain (resize)")]
        public void RebuildTerrain()
        {
            EnsureField(forceRebuild: true);     // new grid (flat) when the size/cell changed
            _chunkMesh = null; _chunkCol = null; // force full recreate + sweep old chunks
            BuildAllChunks();
            TreeLayer.ConformToSurface(_field);
            RockLayer.ConformToSurface(_field);
            FenceLayer.Rebuild(_field);
            PowerLineLayer.Rebuild(_field);
            RailLayer.Rebuild(_field);
            PlanLayer.Rebuild(_field);
            RebuildContours();
            ApplyWater();
            ResetCamera();
            _dirtySince = Time.realtimeSinceStartup;
        }

        // Full reset: new flat field (+ optional test hill) and rebuild.
        [ContextMenu("Reset Terrain")]
        public void ResetTerrain()
        {
            _field = null;
            EnsureField(forceRebuild: true);
            BuildAllChunks();
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
            BuildAllChunks();
            TreeLayer.ConformToSurface(_field);   // re-settle everything onto the flat ground
            RockLayer.ConformToSurface(_field);
            FenceLayer.Rebuild(_field);
            PowerLineLayer.Rebuild(_field);
            RailLayer.Rebuild(_field);
            PlanLayer.Rebuild(_field);
            RebuildContours();
            ApplyWater();
            _dirtySince = Time.realtimeSinceStartup;
        }

        [ContextMenu("Remove All Trees")]
        public void RemoveAllTrees() { TreeLayer.ClearAll(); _dirtySince = Time.realtimeSinceStartup; }
        [ContextMenu("Remove All Rocks")]
        public void RemoveAllRocks() { RockLayer.ClearAll(); _dirtySince = Time.realtimeSinceStartup; }

        // Bake a grayscale heightmap into the current field (REPLACES all
        // heights), then rebuild + conform trees. Read via File+LoadImage so the
        // source needn't be a Read/Write-Enabled asset (or even under Assets/).
        // Bilinear-sampled, so any image size maps onto the grid.
        [ContextMenu("Import Heightmap")]
        public void ImportHeightmap()
        {
            if (_field == null) EnsureField(forceRebuild: true);
            string path = ResolveHeightmapPath();
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"[TerrainDesigner] Heightmap not found: {path}");
                return;
            }
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            bool decoded;
            try { decoded = tex.LoadImage(System.IO.File.ReadAllBytes(path)); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Heightmap read failed: {ex.Message}");
                DestroyTex(tex);
                return;
            }
            if (!decoded)
            {
                Debug.LogWarning($"[TerrainDesigner] Heightmap decode failed: {path}");
                DestroyTex(tex);
                return;
            }

            int iw = tex.width, ih = tex.height;
            Color[] px = tex.GetPixels();
            DestroyTex(tex); // px is a managed copy; safe to free the texture now
            if (px == null || iw < 1 || ih < 1 || px.Length < iw * ih)
            {
                Debug.LogWarning($"[TerrainDesigner] Heightmap pixel read mismatch: " +
                                 $"{iw}x{ih}, {(px == null ? 0 : px.Length)} px.");
                return;
            }

            // Rebuild the field at the CONFIGURED resolution (TerrainSizeMeters /
            // CellSize) before writing. A stale/corrupt _field is what produced
            // both the earlier IndexOutOfRange and the wrong 64x64 grid — don't
            // trust its dims; re-derive them.
            EnsureField(forceRebuild: true);
            int cx = _field.ColumnsX, rz = _field.RowsZ;
            if (_field.Heights == null || _field.Heights.Length != cx * rz)
            {
                Debug.LogWarning($"[TerrainDesigner] Field still inconsistent " +
                                 $"({cx}x{rz} vs {(_field.Heights == null ? 0 : _field.Heights.Length)} heights); rebuilding.");
                _field = new TerrainField(cx, rz, _field.CellSize, _field.Origin);
                cx = _field.ColumnsX; rz = _field.RowsZ;
            }

            for (int z = 0; z < rz; z++)
            {
                float v = rz > 1 ? (float)z / (rz - 1) : 0f;
                for (int x = 0; x < cx; x++)
                {
                    float u = cx > 1 ? (float)x / (cx - 1) : 0f;
                    _field.SetHeight(x, z, SampleBilinearGray(px, iw, ih, u, v) * HeightmapMaxHeight);
                }
            }
            SmoothHeights(HeightmapSmoothPasses);

            // Force the recreate path (which sweeps ALL chunk roots, including
            // orphans) so the mesh can't lag behind the new heights or stack up.
            _chunkMesh = null; _chunkCol = null;
            BuildAllChunks();
            TreeLayer.ConformToSurface(_field);
            RockLayer.ConformToSurface(_field);
            FenceLayer.Rebuild(_field); // re-place linework on the new surface
            PowerLineLayer.Rebuild(_field);
            RailLayer.Rebuild(_field);
            PlanLayer.Rebuild(_field);
            RebuildContours();
            _dirtySince = Time.realtimeSinceStartup;

            float minH = float.MaxValue, maxH = float.MinValue;
            float[] hh = _field.Heights;
            for (int i = 0; i < hh.Length; i++) { if (hh[i] < minH) minH = hh[i]; if (hh[i] > maxH) maxH = hh[i]; }
            Debug.Log($"[TerrainDesigner] Imported {iw}x{ih} → grid {cx}x{rz}, white={HeightmapMaxHeight} m, " +
                      $"{HeightmapSmoothPasses} smooth pass(es); actual height range {minH:F1}..{maxH:F1} m.");
        }

        // Resolve to a FULLY NORMALIZED absolute path (Mono's File.Exists won't
        // reliably resolve an embedded ".."). Searches: project folder, repo
        // root (one up), then Assets/ — returns the first that exists, else the
        // primary candidate (so the warning shows a clean path).
        string ResolveHeightmapPath()
        {
            if (System.IO.Path.IsPathRooted(HeightmapPath))
                return System.IO.Path.GetFullPath(HeightmapPath);
#if UNITY_EDITOR
            string baseDir = System.IO.Path.Combine(Application.dataPath, "..");
#else
            string baseDir = Application.persistentDataPath;
#endif
            string[] candidates =
            {
                System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, HeightmapPath)),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", HeightmapPath)),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, HeightmapPath)),
            };
            foreach (string c in candidates)
                if (System.IO.File.Exists(c)) return c;
            return candidates[0];
        }

        // Absolute path of the heightmap folder (relative resolves to the project root in
        // the Editor, persistentDataPath in a build).
        string HeightmapFolderFull()
        {
            if (System.IO.Path.IsPathRooted(HeightmapFolder)) return HeightmapFolder;
#if UNITY_EDITOR
            string baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
#else
            string baseDir = Application.persistentDataPath;
#endif
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, HeightmapFolder));
        }

        // PNG file names (no path) in the heightmap folder, for the picker dropdown.
        public List<string> ListHeightmapFiles()
        {
            var list = new List<string>();
            try
            {
                string dir = HeightmapFolderFull();
                if (System.IO.Directory.Exists(dir))
                    foreach (string f in System.IO.Directory.GetFiles(dir, "*.png"))
                        list.Add(System.IO.Path.GetFileName(f));
            }
            catch { /* unreadable folder -> empty list */ }
            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // The selected heightmap file (just the name). Getting it reports the current
        // file if it lives in the folder; setting it only points HeightmapPath at the
        // folder — the actual load waits for the "Load heightmap" button.
        public string HeightmapFile
        {
            get
            {
                string name = System.IO.Path.GetFileName(HeightmapPath ?? "");
                return ListHeightmapFiles().Contains(name) ? name : "";
            }
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                HeightmapPath = System.IO.Path.Combine(HeightmapFolder, value);
            }
        }

        // Bilinear gray sample (uses the red channel; grayscale has r=g=b) at
        // normalized (u,v) over a w×h pixel buffer.
        static float SampleBilinearGray(Color[] px, int w, int h, float u, float v)
        {
            float fx = Mathf.Clamp01(u) * (w - 1);
            float fy = Mathf.Clamp01(v) * (h - 1);
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = fx - x0, ty = fy - y0;
            float g00 = px[y0 * w + x0].r, g10 = px[y0 * w + x1].r;
            float g01 = px[y1 * w + x0].r, g11 = px[y1 * w + x1].r;
            return Mathf.Lerp(Mathf.Lerp(g00, g10, tx), Mathf.Lerp(g01, g11, tx), ty);
        }

        // In-place box blur of the height grid (self + 4 neighbours per pass).
        // Edge cells average only their existing neighbours.
        void SmoothHeights(int passes)
        {
            if (passes <= 0 || _field == null) return;
            int cx = _field.ColumnsX, rz = _field.RowsZ;
            float[] src = _field.Heights;
            float[] tmp = new float[src.Length];
            for (int p = 0; p < passes; p++)
            {
                for (int z = 0; z < rz; z++)
                {
                    for (int x = 0; x < cx; x++)
                    {
                        int i = z * cx + x;
                        float sum = src[i];
                        int n = 1;
                        if (x > 0)      { sum += src[i - 1];  n++; }
                        if (x < cx - 1) { sum += src[i + 1];  n++; }
                        if (z > 0)      { sum += src[i - cx]; n++; }
                        if (z < rz - 1) { sum += src[i + cx]; n++; }
                        tmp[i] = sum / n;
                    }
                }
                System.Array.Copy(tmp, src, src.Length);
            }
        }

        static void DestroyTex(Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying) Destroy(tex); else DestroyImmediate(tex);
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
            // Let any in-flight async write finish so the file isn't half-written.
            try { _saveTask?.Wait(3000); } catch { /* ignore */ }
            // Flush synchronously when Play stops / disabled. Always write (not just
            // when edits are pending) so a camera-only move this session — which
            // doesn't mark the terrain dirty — still persists its pose.
            if (Autosave && _field != null)
            {
                WriteSave(BuildSnapshot(), ResolveAutosavePath());
                _dirtySince = -1f;
            }
            SavePacks(); // keep the standalone pack library in sync on Play-stop
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

        // Tree/rock packs live in their OWN file next to the autosave, so they're a
        // reusable preset library that survives deleting/resetting the terrain.
        string ResolvePacksPath()
            => System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ResolveAutosavePath())),
                "TerrainPacks.json");

        class PacksFile { public List<TreePack> TreePacks; public List<TreePack> RockPacks; }

        // Write the pack presets to the standalone file (on pack edits + on save).
        public void SavePacks()
        {
            try
            {
                var data = new PacksFile { TreePacks = TreeLayer.CollectPacks(), RockPacks = RockLayer.CollectPacks() };
                System.IO.File.WriteAllText(ResolvePacksPath(),
                    JsonConvert.SerializeObject(data, Formatting.Indented, TerrainJsonSettings));
            }
            catch (System.Exception ex) { Debug.LogWarning($"[TerrainDesigner] Packs save failed: {ex.Message}"); }
        }

        // Load the standalone packs file; it's authoritative over any packs that came
        // from the terrain autosave. No-op if the file doesn't exist (back-compat).
        void LoadPacks()
        {
            try
            {
                string path = ResolvePacksPath();
                if (!System.IO.File.Exists(path)) return;
                var data = JsonConvert.DeserializeObject<PacksFile>(
                    System.IO.File.ReadAllText(path), TerrainJsonSettings);
                if (data == null) return;
                TreeLayer.SetPacks(data.TreePacks);
                RockLayer.SetPacks(data.RockPacks);
            }
            catch (System.Exception ex) { Debug.LogWarning($"[TerrainDesigner] Packs load failed: {ex.Message}"); }
        }

        // Debounced autosave entry point. The expensive part (JSON serialize +
        // disk write) runs on a background thread so it doesn't hitch the frame;
        // the snapshot (which touches Unity/field state) is built on the main
        // thread first. Skips if a previous async write is still running — the
        // dirty flag will re-trigger shortly, and overlapping writes are avoided.
        public void SaveTerrain()
        {
            if (_field == null) return;
            if (_saveTask != null && !_saveTask.IsCompleted) return;
            try
            {
                TerrainSave save = BuildSnapshot();
                string path = ResolveAutosavePath();
                _saveTask = System.Threading.Tasks.Task.Run(() => WriteSave(save, path));
                _dirtySince = -1f; // a write is now in flight for the current state
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Save failed: {ex.Message}");
            }
        }

        // Manual Save button: force an immediate SYNCHRONOUS write, ignoring the
        // dirty flag and any in-flight async write, so it always captures the
        // current state right now (used when Autosave is off, for testing).
        [ContextMenu("Save Now")]
        public void SaveNow()
        {
            if (_field == null) return;
            try { _saveTask?.Wait(2000); } catch { /* ignore */ }
            WriteSave(BuildSnapshot(), ResolveAutosavePath());
            SavePacks();
            _dirtySince = -1f;
            Debug.Log($"[TerrainDesigner] Saved → {ResolveAutosavePath()}");
        }

        // Manual Load button: reload everything from the save file, live. Clears
        // the current scatter first so trees/rocks don't duplicate, adopts the
        // saved grid, and rebuilds chunks + layers + water + camera.
        [ContextMenu("Load Now")]
        public void LoadNow()
        {
            TerrainField loaded = TryLoadTerrain();
            if (loaded == null) { Debug.LogWarning("[TerrainDesigner] Nothing to load."); return; }
            TreeLayer.ClearAll();
            RockLayer.ClearAll();
            float half = (loaded.ColumnsX - 1) * loaded.CellSize * 0.5f;
            loaded.Origin = transform.position - new Vector3(half, 0f, half);
            _field = loaded;
            CellSize = _field.CellSize;
            TerrainSizeMeters = (_field.ColumnsX - 1) * _field.CellSize;
            ColumnsX = _field.ColumnsX; RowsZ = _field.RowsZ;
            _chunkMesh = null; _chunkCol = null; // grid may have changed -> recreate chunks
            LoadPacks(); // standalone pack library wins over autosave packs
            BuildAllChunks();
            TreeLayer.SpawnPending(_field);
            RockLayer.SpawnPending(_field);
            FenceLayer.Rebuild(_field);
            PowerLineLayer.Rebuild(_field);
            RailLayer.Rebuild(_field);
            PlanLayer.Rebuild(_field);
            RebuildContours();
            ApplyWater();
            if (_havePendingCam) { ApplyCameraPose(_pendingCamPos, _pendingCamYaw, _pendingCamPitch); _havePendingCam = false; }
            _dirtySince = -1f;
            Debug.Log("[TerrainDesigner] Loaded.");
        }

        // Snapshot the current field + trees + packs into an owned, immutable
        // payload safe to serialize off the main thread. Main-thread only.
        TerrainSave BuildSnapshot()
        {
            // Sparse: store only altered (non-zero) heights; zeros implied.
            float[] heights = _field.Heights;
            var idx = new List<int>();
            var hs = new List<float>();
            for (int i = 0; i < heights.Length; i++)
            {
                if (Mathf.Abs(heights[i]) > 1e-4f) { idx.Add(i); hs.Add(heights[i]); }
            }
            bool haveCam = TryGetCameraPose(out Vector3 camPos, out float camYaw, out float camPitch);
            return new TerrainSave
            {
                ColumnsX = _field.ColumnsX,
                RowsZ = _field.RowsZ,
                CellSize = _field.CellSize,
                Idx = idx.ToArray(),
                H = hs.ToArray(),
                Trees = TreeLayer.CollectData(),
                Packs = TreeLayer.CollectPacks(),
                Rocks = RockLayer.CollectData(),
                RockPacks = RockLayer.CollectPacks(),
                Fences = FenceLayer.CollectData(),
                PowerLines = PowerLineLayer.CollectData(),
                Rails = RailLayer.CollectData(),
                Plan = PlanLayer.CollectData(),
                HasCamera = haveCam,
                CamPos = camPos,
                CamYaw = camYaw,
                CamPitch = camPitch,
            };
        }

        // The current fly-camera pose (world position + its look yaw/pitch), for
        // autosave. False if there's no fly camera to read.
        bool TryGetCameraPose(out Vector3 pos, out float yaw, out float pitch)
        {
            FlyCameraController fly = ResolveFly();
            if (fly != null)
            {
                pos = fly.transform.position;
                yaw = fly.Yaw;
                pitch = fly.Pitch;
                return true;
            }
            // Live camera gone (e.g. OnDisable at Play-stop tears it down before us)
            // — fall back to the pose sampled each frame during play, so the final
            // flush still saves a valid camera instead of clobbering it with zeros.
            if (_haveLastCam)
            {
                pos = _lastCamPos; yaw = _lastCamYaw; pitch = _lastCamPitch;
                return true;
            }
            pos = Vector3.zero; yaw = 0f; pitch = 0f;
            return false;
        }

        // Restore a saved pose onto the fly camera (and its look state, so it
        // doesn't ease back to a framed default).
        void ApplyCameraPose(Vector3 pos, float yaw, float pitch)
        {
            FlyCameraController fly = ResolveFly();
            if (fly == null) return;
            fly.transform.position = pos;
            fly.Yaw = yaw;
            fly.Pitch = pitch;
            fly.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        FlyCameraController ResolveFly()
        {
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            FlyCameraController fly = cam != null ? cam.GetComponent<FlyCameraController>() : null;
            return fly != null ? fly : FindFirstObjectByType<FlyCameraController>();
        }

        const int SaveMagic = 0x54524E33; // "TRN3"

        // BINARY serialize + write — primitives straight to a byte buffer, so a
        // dense (heightmap-imported) terrain doesn't allocate megabytes of JSON
        // strings and trip a stop-the-world GC mid-frame. Thread-safe.
        static void WriteSave(TerrainSave save, string path)
        {
            try
            {
                int n = (save.Idx != null && save.H != null)
                    ? Mathf.Min(save.Idx.Length, save.H.Length) : 0;
                int cap = 64 + n * 8
                          + TreeBytes(save.Trees) + TreeBytes(save.Rocks)
                          + GraphBytes(save.Fences) + GraphBytes(save.PowerLines)
                          + GraphBytes(save.Rails) + GraphBytes(save.Plan) + 256;
                using var ms = new System.IO.MemoryStream(cap);
                using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                {
                    w.Write(SaveMagic);
                    w.Write(7); // version (7 added the rail-planning survey graph)
                    w.Write(save.ColumnsX);
                    w.Write(save.RowsZ);
                    w.Write(save.CellSize);
                    w.Write(n);
                    for (int i = 0; i < n; i++) w.Write(save.Idx[i]);
                    for (int i = 0; i < n; i++) w.Write(save.H[i]);
                    WriteTrees(w, save.Trees);
                    WritePacks(w, save.Packs);
                    WriteTrees(w, save.Rocks);
                    WritePacks(w, save.RockPacks);
                    WriteGraph(w, save.Fences);
                    WriteGraph(w, save.PowerLines);
                    WriteGraph(w, save.Rails);
                    WriteGraph(w, save.Plan);               // v7+
                    w.Write(save.HasCamera);                // v5+
                    w.Write(save.CamPos.x); w.Write(save.CamPos.y); w.Write(save.CamPos.z);
                    w.Write(save.CamYaw); w.Write(save.CamPitch);
                }
                System.IO.File.WriteAllBytes(path, ms.ToArray());
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Save write failed: {ex.Message}");
            }
        }

        static int TreeBytes(List<PlacedTreeData> t) => (t?.Count ?? 0) * 40 + 8;
        static int GraphBytes(LineGraphSave g) =>
            ((g?.Nodes?.Count ?? 0) * 8) + ((g?.Edges?.Count ?? 0) * 32) + 16;

        static void WriteTrees(System.IO.BinaryWriter w, List<PlacedTreeData> list)
        {
            int c = list?.Count ?? 0;
            w.Write(c);
            for (int i = 0; i < c; i++)
            {
                PlacedTreeData d = list[i];
                w.Write(d?.Prefab ?? "");
                w.Write(d != null ? d.Position.x : 0f);
                w.Write(d != null ? d.Position.y : 0f);
                w.Write(d != null ? d.RotationY : 0f);
                w.Write(d != null ? d.Scale : 1f);
            }
        }

        static void WritePacks(System.IO.BinaryWriter w, List<TreePack> packs)
        {
            int c = packs?.Count ?? 0;
            w.Write(c);
            for (int i = 0; i < c; i++)
            {
                TreePack p = packs[i];
                w.Write(p?.Name ?? "");
                int m = p?.Trees?.Count ?? 0;
                w.Write(m);
                for (int j = 0; j < m; j++) w.Write(p.Trees[j] ?? "");
                // v6: captured brush settings.
                w.Write(p != null && p.HasParams);
                w.Write(p?.PaintRate ?? 25f);
                w.Write(p?.Spacing ?? 4f);
                w.Write(p?.MaxSlopeDeg ?? 35f);
                w.Write(p != null ? p.AvoidWater : true);
                w.Write(p?.WaterlineMargin ?? 1f);
            }
        }

        static void WriteGraph(System.IO.BinaryWriter w, LineGraphSave g)
        {
            int nn = g?.Nodes?.Count ?? 0;
            w.Write(nn);
            for (int i = 0; i < nn; i++) { w.Write(g.Nodes[i].x); w.Write(g.Nodes[i].y); }
            int ne = g?.Edges?.Count ?? 0;
            w.Write(ne);
            for (int i = 0; i < ne; i++)
            {
                LineEdge e = g.Edges[i];
                w.Write(e.A); w.Write(e.B);
                w.Write(e.HasCurve);                       // save format v3+
                w.Write(e.ControlA.x); w.Write(e.ControlA.y);
                w.Write(e.ControlB.x); w.Write(e.ControlB.y);
                w.Write(e.SpeedLimit);                     // v4+
            }
        }

        TerrainField TryLoadTerrain()
        {
            try
            {
                string path = ResolveAutosavePath();
                if (!System.IO.File.Exists(path)) return null;
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                TerrainSave save = ReadSaveBinary(bytes);
                if (save == null)
                {
                    // Migrate an old JSON autosave: read it once (slow path is fine
                    // on a one-time load) — it re-saves as binary thereafter.
                    try
                    {
                        save = JsonConvert.DeserializeObject<TerrainSave>(
                            System.Text.Encoding.UTF8.GetString(bytes), TerrainJsonSettings);
                    }
                    catch { save = null; }
                }
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
                // Stage scatter data + packs; layers SpawnPending after chunks build.
                TreeLayer.LoadState(save.Trees, save.Packs);
                RockLayer.LoadState(save.Rocks, save.RockPacks);
                FenceLayer.LoadState(save.Fences); // Rebuilt after chunks
                PowerLineLayer.LoadState(save.PowerLines);
                RailLayer.LoadState(save.Rails);
                PlanLayer.LoadState(save.Plan);
                // Stage the camera pose; applied in Start once the fly camera exists.
                _havePendingCam = save.HasCamera;
                _pendingCamPos = save.CamPos;
                _pendingCamYaw = save.CamYaw;
                _pendingCamPitch = save.CamPitch;
                return f;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Load failed: {ex.Message} — starting fresh.");
                return null;
            }
        }

        // Parse the binary save. Returns null if the bytes aren't our format
        // (e.g. an old JSON autosave) — caller then starts fresh.
        static TerrainSave ReadSaveBinary(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8) return null;
            try
            {
                using var ms = new System.IO.MemoryStream(bytes);
                using var r = new System.IO.BinaryReader(ms, System.Text.Encoding.UTF8);
                if (r.ReadInt32() != SaveMagic) return null;
                int version = r.ReadInt32();
                var s = new TerrainSave
                {
                    ColumnsX = r.ReadInt32(),
                    RowsZ = r.ReadInt32(),
                    CellSize = r.ReadSingle(),
                };
                int n = r.ReadInt32();
                s.Idx = new int[n];
                for (int i = 0; i < n; i++) s.Idx[i] = r.ReadInt32();
                s.H = new float[n];
                for (int i = 0; i < n; i++) s.H[i] = r.ReadSingle();
                s.Trees = ReadTrees(r);
                s.Packs = ReadPacks(r, version);
                s.Rocks = ReadTrees(r);
                s.RockPacks = ReadPacks(r, version);
                s.Fences = ReadGraph(r, version);
                s.PowerLines = ReadGraph(r, version);
                if (version >= 2) s.Rails = ReadGraph(r, version); // older saves have no rails
                if (version >= 7) s.Plan = ReadGraph(r, version);  // rail-planning survey graph
                if (version >= 5) // fly-camera pose added in v5
                {
                    s.HasCamera = r.ReadBoolean();
                    s.CamPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    s.CamYaw = r.ReadSingle();
                    s.CamPitch = r.ReadSingle();
                }
                else s.HasCamera = false;
                return s;
            }
            catch { return null; }
        }

        static List<PlacedTreeData> ReadTrees(System.IO.BinaryReader r)
        {
            int c = r.ReadInt32();
            var list = new List<PlacedTreeData>(c);
            for (int i = 0; i < c; i++)
                list.Add(new PlacedTreeData
                {
                    Prefab = r.ReadString(),
                    Position = new Vector2(r.ReadSingle(), r.ReadSingle()),
                    RotationY = r.ReadSingle(),
                    Scale = r.ReadSingle(),
                });
            return list;
        }

        static List<TreePack> ReadPacks(System.IO.BinaryReader r, int version)
        {
            int c = r.ReadInt32();
            var list = new List<TreePack>(c);
            for (int i = 0; i < c; i++)
            {
                var p = new TreePack { Name = r.ReadString(), Trees = new List<string>() };
                int m = r.ReadInt32();
                for (int j = 0; j < m; j++) p.Trees.Add(r.ReadString());
                if (version >= 6) // per-pack brush settings
                {
                    p.HasParams = r.ReadBoolean();
                    p.PaintRate = r.ReadSingle();
                    p.Spacing = r.ReadSingle();
                    p.MaxSlopeDeg = r.ReadSingle();
                    p.AvoidWater = r.ReadBoolean();
                    p.WaterlineMargin = r.ReadSingle();
                }
                list.Add(p);
            }
            return list;
        }

        static LineGraphSave ReadGraph(System.IO.BinaryReader r, int version)
        {
            var g = new LineGraphSave { Nodes = new List<Vector2>(), Edges = new List<LineEdge>() };
            int nn = r.ReadInt32();
            for (int i = 0; i < nn; i++) g.Nodes.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
            int ne = r.ReadInt32();
            for (int i = 0; i < ne; i++)
            {
                var e = new LineEdge(r.ReadInt32(), r.ReadInt32());
                if (version >= 3) // curve controls added in v3
                {
                    e.HasCurve = r.ReadBoolean();
                    e.ControlA = new Vector2(r.ReadSingle(), r.ReadSingle());
                    e.ControlB = new Vector2(r.ReadSingle(), r.ReadSingle());
                }
                if (version >= 4) e.SpeedLimit = r.ReadSingle(); // section speed
                g.Edges.Add(e);
            }
            return g;
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
                        Converters = new List<JsonConverter>
                            { new Vector3JsonConverter(), new Vector2JsonConverter() },
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

        // Vector2 as { x, y } — for PlacedTreeData.Position (tree XZ).
        class Vector2JsonConverter : JsonConverter<Vector2>
        {
            public override void WriteJson(JsonWriter writer, Vector2 value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("x"); writer.WriteValue(value.x);
                writer.WritePropertyName("y"); writer.WriteValue(value.y);
                writer.WriteEndObject();
            }

            public override Vector2 ReadJson(JsonReader reader, System.Type objectType,
                Vector2 existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                Newtonsoft.Json.Linq.JObject jo = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new Vector2(
                    jo["x"]?.ToObject<float>() ?? 0f,
                    jo["y"]?.ToObject<float>() ?? 0f);
            }
        }
    }
}
