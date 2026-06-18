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
        public enum BrushMode { Raise, Lower, Smooth, Flatten, Slope, Sea, Measure, Forest }

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
        public bool ShowWater = false;
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
        // Sea tool (chunk world): click floods the contiguous area within ±SeaTolerance m of the
        // clicked height and flattens it to (clicked height − SeaDrop). Carves the flat DEM ocean.
        public float SeaTolerance = 3f;
        public float SeaDrop = 5f;
        // Slope-tool side-slope batter, 1 : BatterRatio (vertical : horizontal). 2 = 1:2 (gentle), 1 = 1:1 (steep).
        public float BatterRatio = 2f;
        // Measure tool (7): click A, click B → straight-line distance tooltip; A→cursor rubber-bands.
        Vector3 _measA, _measB, _measCursor;
        bool _measHasA, _measHasB;
        // The active terrain backend (Terrain palette's Low-Poly/DEM toggle). When DEM (and a DEM
        // world is built), EVERY tool — sculpt, rail, scatter, fences, placeables, the brush ring —
        // targets the DEM, and the low-poly terrain is hidden; otherwise everything targets the
        // low-poly field. Set via SetActiveBackend (which also flips terrain visibility).
        public bool DemBackend = true;
        float _demFlattenY;   // DEM Flatten target (world Y under the cursor at stroke start)
        [Tooltip("DEM terrain LOD: Unity heightmap pixel error. LOWER = more detail (denser mesh, " +
                 "heavier); HIGHER = coarser/faster. Applied live to a loaded DEM and on load.")]
        public float DemLodPixelError = 2f;
        // Tunable entry point: set the DEM LOD and apply it live if a world is loaded.
        public float DemLod
        {
            get => DemLodPixelError;
            set
            {
                DemLodPixelError = Mathf.Max(1f, value);
                if (DemTerrainWorld.HasWorld) DemTerrainWorld.SetTerrainLod(DemLodPixelError);
            }
        }
        [Tooltip("Brush radius in metres. Resize live with [ (smaller) and ] (larger).")]
        public float BrushRadius = 10f;
        [Tooltip("Brush resize speed (metres/second, while [ or ] is held).")]
        public float BrushResizeRate = 50f;
        [Tooltip("Upper clamp for the brush radius (metres).")]
        public float MaxBrushRadius = 500f;
        [Tooltip("When ON, sculpt brushes skip the ground under BUILT roads so terrain edits can't disturb a road's bed.")]
        public bool ProtectRoadsFromSculpt = true;
        [Tooltip("Extra metres beyond each road's half-width kept protected from the brush (a margin around the bed).")]
        public float RoadProtectMargin = 1.5f;
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

        [Header("Brush mode icon (shown beside the ring in sculpt modes)")]
        [Tooltip("Icon drawn next to the brush ring per sculpt mode. Raise/Lower fall back " +
                 "to generated up/down arrows; assign Smooth/Flatten/Slope textures.")]
        public Texture2D RaiseIcon, LowerIcon, SmoothIcon, FlattenIcon, SlopeIcon;
        [Tooltip("On-screen size (px) of the brush-mode icon.")]
        public float BrushIconSize = 30f;
        [Range(0f, 1f)]
        [Tooltip("Opacity of the in-game UI Toolkit tool palette background (RailPalette).")]
        public float PaletteBgAlpha = 0.96f;

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
        public RoadPlanLayer RoadPlanLayer = new RoadPlanLayer { Name = "Road Plan" };
        [Tooltip("Retaining-wall layer (9): 3m concrete wall to a level top; mouse-wheel sets the top elevation, back side regraded.")]
        public RetainingWallLayer RetainingWallLayer = new RetainingWallLayer { Name = "RetainingWall" };

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
        [Tooltip("Seconds of no editing before the scene is snapshotted to disk. Higher = the " +
                 "main-thread snapshot (terrain + all placed objects) hitches less often.")]
        public float AutosaveDebounceSeconds = 5f;

        TerrainField _field;
        float _dirtySince = -1f; // realtime when last edited; -1 = clean
        float _roadRebuildAfterLoad = -1f; // countdown (s) after a load to re-sweep built roads once the world settles; <0 = none
        System.Threading.Tasks.Task _saveTask; // in-flight async autosave (serialize+write off-thread)
        // Camera pose staged from the autosave; applied in Start once the fly
        // camera exists (it's created after the load).
        bool _havePendingCam;
        Vector3 _pendingCamPos;
        float _pendingCamYaw, _pendingCamPitch;
        // DEM backend + city staged from the save; consumed in Start to reload the world.
        bool _pendingDemBackend;
        string _pendingDemCity;
        DemTerrainWorld.Edits _pendingDemEdits;   // sparse sculpt/carve diff, applied after the DEM builds
        bool _pendingWaterOn; float _pendingWaterLevel;   // chunk water staged from the save; applied in LoadGame
        System.Collections.Generic.List<WaterBodies.Save> _pendingWaterBodies;   // per-level water bodies staged from the save
        List<ForestGen.ForestSpeciesSave> _pendingForest; // forest staged from the save; imported after surface ready
        // The DEM city to fall back to when switching to DEM with no world built — the one
        // loaded this session if any, else the one remembered from the save. Lets the palette
        // auto-reload the last city even when we restarted in low-poly (so it's not "gone").
        public string LastDemCity => !string.IsNullOrEmpty(DemTerrainWorld.CurrentCity)
            ? DemTerrainWorld.CurrentCity : _pendingDemCity;
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
        bool _brushCursorVisible;          // mirror of the ring's visibility for the OnGUI icon
        Vector3 _brushCursorWorld;         // ring centre in world space, for projecting the icon
        Texture2D _genUpArrow, _genDownArrow;  // procedural fallbacks for Raise/Lower
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
        bool _slopeCornerPending; // curve mode: a bend corner is armed (Shift+click after A)
        Vector3 _slopeCorner;     // the armed bend corner (world)
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
        int _roadConnectNodeA = -1;        // road plan connect: armed end A (-1 = none)
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

        DemTerrainSurface _demSurf;
        ChunkSurface _chunkSurf;
        int _minimapW = 6;   // chunk minimap half-extent (± chunks shown); scroll over the map zooms
        bool _showMinimap = true;   // V toggles the corner chunk minimap / diorama
        // The active surface every ground-reading tool uses: the DEM when it's the selected backend
        // AND built, else the low-poly field. Driven by the Low-Poly/DEM toggle (DemBackend).
        ITerrainSurface Surf => ChunkWorld.Active ? (_chunkSurf ??= new ChunkSurface())
            : (DemBackend && DemTerrainWorld.HasWorld) ? (_demSurf ??= new DemTerrainSurface())
            : (ITerrainSurface)_field;

        // Flip the active terrain backend: route all tools (via Surf/DemBackend) and show only the
        // active terrain. The low-poly world is hidden whenever DEM is the active backend (even
        // before a world is loaded — you'll see empty sky until you pick a city, which is fine).
        // Heightmap sampling works on the DEM even while it's hidden.
        public void SetActiveBackend(bool dem)
        {
            DemBackend = dem;
            if (_chunkRoot != null) _chunkRoot.SetActive(!dem);
            DemTerrainWorld.SetVisible(dem);
            // The low-poly water plane is a standalone GameObject (not under _chunkRoot),
            // so hide it explicitly on DEM; on return to low-poly let ApplyWater honour ShowWater.
            if (dem) { if (_waterGo != null) _waterGo.SetActive(false); }
            else ApplyWater();
        }

        // Blocking DEM build + activate, used by the Start restore (the Terrain palette has
        // its own coroutine variant with a loading overlay). Params mirror the palette's
        // demTile/demFrom/demTo/demLod constants — keep them in sync.
        public void LoadDemWorld(string city)
        {
            if (string.IsNullOrEmpty(city)) return;
            DemTerrainWorld.Build(city, 10000f, -500f, 9000f);
            DemTerrainWorld.SetGreen(true);     // Flat green (the palette's default surface)
            DemTerrainWorld.SetTerrainLod(DemLodPixelError);
            DemWater.Apply();
            SetActiveBackend(true);             // hides low-poly + its water, shows the DEM
        }

        // ── Chunk-streaming test world (flat, no DEM data) ───────────────────────────────────
        // Stands up the streaming chunk world: hides the other terrains, drops the camera over
        // the flat plane, and starts ChunkWorld + a ChunkStreamer that keeps a 5×5 bubble loaded
        // around the camera. Sculpt edits persist per-chunk under <save>/ChunkEdits, so an edit
        // made far away survives the chunk unloading and returns when you fly back.
        // Flat procedural streaming test, from the origin.
        public void StartChunkTest()
            => StartChunkWorld(new Vector3(0f, 4000f, 0f), 35f, "ChunkEdits");

        // Real-DEM streaming test: load a *_tile_<row>_<col>.png set into the chunks, centred on it.
        // folder = a city under Heightmaps/Highres (e.g. "Mount Shasta, California"). Uses the
        // current decode range (DemChunkSource.NormMin/Max — exposed as React tunables).
        public void StartChunkDem(string folder)
        {
            if (ChunkWorld.Active) return;
            if (!DemChunkSource.Configure(folder, DemChunkSource.NormMin, DemChunkSource.NormMax)) return;
            // Default the water 20 m below the world's lowest point so it isn't "left behind" at sea level on
            // high inland terrain. A saved level (loaded games) overrides this afterwards in LoadGame.
            ChunkOverlays.SetWaterLevel(Mathf.Floor(DemChunkSource.NormMin) - 20f);
            Vector3 c = DemChunkSource.CenterWorld; c.y = 4000f;
            // Steeper pitch than the flat test so the view footprint sits near the DEM centre and
            // the (Radius-capped) eager load covers the whole tile grid regardless of yaw.
            StartChunkWorld(c, 55f, "ChunkEditsDem");
            MinimapDiorama.Spawn(ChunkCam());   // 3D relief minimap of the whole block
        }

        void StartChunkWorld(Vector3 camPos, float pitch, string editSubdir)
        {
            if (ChunkWorld.Active) return;
            if (_chunkRoot != null) _chunkRoot.SetActive(false);   // hide low-poly mesh terrain
            DemTerrainWorld.SetVisible(false);                     // hide the DEM
            if (_waterGo != null) _waterGo.SetActive(false);
            string dir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ResolveAutosavePath())), editSubdir);
            ChunkWorld.Begin(dir);

            FlyCameraController fly = ResolveFly();
            if (fly != null)
            {
                // Place at the supplied spot, not the camera's (possibly far-flung) prior XZ — Unity
                // physics raycasts (sculpt/placeables ghost) get imprecise/miss tens of km out.
                fly.transform.position = camPos;
                fly.Pitch = pitch;
                fly.transform.rotation = Quaternion.Euler(pitch, fly.Yaw, 0f);
                // Point the altitude-damping clamp at the CHUNK surface (else it reads the hidden
                // low-poly/DEM and pins speed to the minimum → crawl). And pick a pace suited to
                // the scale; the System-palette Camera Speed still overrides this live.
                fly.GroundHeight = p => ChunkWorld.SampleHeight(p.x, p.z);
                fly.MoveSpeed = 500f; fly.ZoomStep = 10f;   // "Overview off" baseline (matches DemTerrainWorld.FrameCamera); palette toggles it on live
            }
            var streamer = FindFirstObjectByType<ChunkStreamer>();
            if (streamer == null) streamer = new GameObject("ChunkStreamer").AddComponent<ChunkStreamer>();
            streamer.Cam = ChunkCam();
            ChunkWorld.Tick(ChunkCam(), eager: true);   // load the view footprint now
            ChunkOverlays.Reapply();                    // restore water / local-grid toggle state
            Debug.Log($"[ChunkTest] started — {ChunkWorld.LoadedCount} chunks resident.");
        }

        // The camera the chunk streamer / screen-space LOD reads (footprint + on-screen size).
        Camera ChunkCam() => PickCamera != null ? PickCamera : Camera.main;

        public void StopChunkTest()
        {
            if (!ChunkWorld.Active) return;
            ChunkWorld.End();   // saves every dirty chunk
            var streamer = FindFirstObjectByType<ChunkStreamer>();
            if (streamer != null) Destroy(streamer.gameObject);
            SetActiveBackend(DemBackend);   // restore the previous backend's visibility
            // Re-point the camera altitude clamp at the now-active surface.
            FlyCameraController fly = ResolveFly();
            if (fly != null) fly.GroundHeight = WorldGroundHeight;
            if (DemBackend && DemTerrainWorld.HasWorld) DemTerrainWorld.WireCameraToDem();
            MinimapDiorama.Dispose();   // tear down the relief minimap (no-op for the flat test)
            ChunkOverlays.Teardown();   // tear down water + local-grid GOs (toggle state is kept)
            WaterBodies.Teardown();     // tear down per-level water bodies (in-session only this phase)
            BridgeArchTool.Teardown();  // tear down the bridge-arch editing overlay
            ForestGen.Teardown();       // tear down the forest-selection highlight
            DemChunkSource.Clear();     // drop the DEM tile mapping/cache (no-op for the flat test)
            Debug.Log("[ChunkTest] stopped.");
        }

        public bool ChunkTestActive => ChunkWorld.Active;
        public bool ChunkDemActive => DemChunkSource.Active;

        // DEM decode range (m) — MUST match the range the PNGs were exported at (the Highres set is
        // -500..9000). Changing it on a loaded DEM re-derives every chunk (rescales, not refines).
        public float ChunkDemNormMin
        {
            get => DemChunkSource.NormMin;
            set
            {
                if (Mathf.Approximately(value, DemChunkSource.NormMin)) return;
                DemChunkSource.NormMin = value;
                if (ChunkWorld.Active && DemChunkSource.Active) ChunkWorld.RefillAll();
            }
        }
        public float ChunkDemNormMax
        {
            get => DemChunkSource.NormMax;
            set
            {
                if (Mathf.Approximately(value, DemChunkSource.NormMax)) return;
                DemChunkSource.NormMax = value;
                if (ChunkWorld.Active && DemChunkSource.Active) ChunkWorld.RefillAll();
            }
        }
        public bool ChunkShowGrid { get => ChunkWorld.ShowGrid; set => ChunkWorld.SetGrid(value); }
        public bool ChunkLockBubble
        {
            get => ChunkWorld.LockBubble;
            set { ChunkWorld.LockBubble = value; ChunkWorld.RefreshCollidersNow(); }   // locking → cook colliders across the whole bubble so the corners are sculptable
        }
        // Perimeter skirts (hide LOD-seam cracks). Toggling rebuilds resident chunk meshes.
        public bool ChunkSkirts
        {
            get => ChunkWorld.Skirts;
            set { if (value != ChunkWorld.Skirts) { ChunkWorld.Skirts = value; ChunkWorld.RebuildAllMeshes(); } }
        }
        // Only chunks within this Chebyshev radius of the bubble centre get a cooked MeshCollider
        // (collider cooking is the main per-load hitch; far chunks are never clicked/sculpted).
        public float ChunkColliderRadius
        {
            get => ChunkWorld.ColliderRadius;
            set { int r = Mathf.Clamp(Mathf.RoundToInt(value), 1, 64); if (r != ChunkWorld.ColliderRadius) { ChunkWorld.ColliderRadius = r; ChunkWorld.RefreshCollidersNow(); } }
        }
        // Re-center the bubble only after the look-point drifts this many chunks (anti-thrash on look-around).
        public float ChunkRecenterDeadband
        {
            get => ChunkWorld.RecenterDeadband;
            set => ChunkWorld.RecenterDeadband = Mathf.Clamp(Mathf.RoundToInt(value), 1, 8);
        }
        // Water plane at a configurable sea level (m), and a fine local build/sculpt grid that follows the view.
        // Water toggle/level mark the scene dirty so the debounced autosave actually fires on a
        // water-only change (otherwise it's only captured if you happen to sculpt afterwards).
        public bool ChunkShowWater
        {
            get => ChunkOverlays.ShowWater;
            set { if (value != ChunkOverlays.ShowWater) { ChunkOverlays.SetWater(value); _dirtySince = Time.realtimeSinceStartup; } }
        }
        public float ChunkWaterLevel
        {
            get => ChunkOverlays.WaterLevel;
            set { if (Mathf.RoundToInt(value) != Mathf.RoundToInt(ChunkOverlays.WaterLevel)) { ChunkOverlays.SetWaterLevel(value); _dirtySince = Time.realtimeSinceStartup; } }
        }
        public Color ChunkWaterColor { get => ChunkOverlays.WaterColor; set => ChunkOverlays.SetWaterColor(value); }
        public float ChunkWaterSmoothness { get => ChunkOverlays.WaterSmoothness; set => ChunkOverlays.SetWaterSmoothness(value); }

        // ── multi-level water bodies (dam case) ──
        public int WaterBodyCount => WaterBodies.Count;
        [System.NonSerialized] bool _placingWaterBody;
        public bool PlacingWaterBody => _placingWaterBody;
        // Arm placement: the NEXT terrain click drops a water body whose LEVEL = clicked ground + 5 m and floods from
        // there (so the seed is always below the surface → always fills). A palette button can't read the terrain
        // cursor (mouse is over the panel), so this defers to the click handled in Update.
        public void ArmWaterBodyPlacement()
        {
            _placingWaterBody = true;
            Debug.Log($"[WaterBodies] click a spot to place a water body (level = ground + {WaterBodies.SeedRise:0} m); Esc cancels.");
        }
        public void ClearWaterBodies() { WaterBodies.Clear(); _dirtySince = Time.realtimeSinceStartup; }

        // ── bridge arch (under-deck, on an existing bridge's trestles) — see BridgeArchTool ──
        public void EnterBridgeArchMode() { EnterSculptMode(); _active = null; _lineActive = null; BridgeArchTool.Enter(); }
        public bool ChunkLocalGrid { get => ChunkOverlays.ShowLocalGrid; set => ChunkOverlays.SetLocalGrid(value); }
        // Topographic contour lines over the loaded terrain (J hotkey too).
        // Topo contours render via the per-pixel overlay SHADER (ChunkWorld.SetContours).
        public bool ChunkContours { get => ChunkWorld.ShowContours; set => ChunkWorld.SetContours(value); }
        public float ChunkContourInterval { get => ChunkWorld.ContourMinor; set => ChunkWorld.SetContourMinor(value); }
        public float ChunkContourStrength { get => ChunkWorld.ContourStrength; set => ChunkWorld.SetContourStrength(value); }
        // Ridge / valley overlay: highlights convex crests + concave hollows from baked terrain curvature.
        public bool ChunkRidges { get => ChunkWorld.ShowRidges; set => ChunkWorld.SetRidges(value); }
        public float ChunkRidgeScale { get => ChunkWorld.RidgeScaleMeters; set => ChunkWorld.SetRidgeScale(value); }
        public float ChunkRidgeThreshold { get => ChunkWorld.RidgeThreshold; set => ChunkWorld.SetRidgeThreshold(value); }
        public float ChunkRidgeStrength { get => ChunkWorld.RidgeStrength; set => ChunkWorld.SetRidgeStrength(value); }
        // Hydrology analysis overlay (drainage / catchment): blue = where rain pools, cyan = where runoff concentrates.
        // Recomputes on toggle (and via Refresh) over a window around the camera's look-point — re-run after grading.
        public bool HydrologyShow { get => HydrologyOverlay.Show; set => HydrologyOverlay.SetShow(value, CameraGroundXZ()); }
        public void RefreshHydrology() => HydrologyOverlay.Refresh(CameraGroundXZ());
        float _hydroDirtyAt;
        // Debounce terrain-edit dirtiness into ONE drainage recompute once the edit stops (a sculpt drag would
        // otherwise trigger an O(n log n) flood-fill every frame). Recenters on the SAME window (RefreshLast), not
        // the camera, so the overlay stays put while you grade. ~0.35 s of no active drag → recompute.
        void TickHydrology()
        {
            if (!HydrologyOverlay.Dirty) return;
            if (Input.GetMouseButton(0)) { _hydroDirtyAt = Time.realtimeSinceStartup; return; }   // still dragging
            if (Time.realtimeSinceStartup - _hydroDirtyAt < 0.35f) return;
            HydrologyOverlay.RefreshLast();   // recompute once; clears Dirty
            _hydroDirtyAt = Time.realtimeSinceStartup;
        }
        Vector2 CameraGroundXZ()
        {
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return Vector2.zero;
            if (Physics.Raycast(new Ray(cam.transform.position, cam.transform.forward), out var hit, 100000f))
                return new Vector2(hit.point.x, hit.point.z);
            return new Vector2(cam.transform.position.x, cam.transform.position.z);   // looking at sky → use position
        }
        // Force the bubble to the FULL Radius regardless of zoom (streams in over frames). Heavy:
        // (2·Radius+1)² chunks — e.g. Radius 40 = 6,561. Following the camera; overrides Lock while on.
        public bool ChunkFillRadius
        {
            get => ChunkWorld.FillRadius;
            set
            {
                if (value == ChunkWorld.FillRadius) return;
                ChunkWorld.FillRadius = value;
                if (ChunkWorld.Active) ChunkWorld.Tick(ChunkCam(), eager: true);   // start filling / cull back
            }
        }
        // Terrain max height (m). Changing it regenerates the loaded chunks (can hitch on big bubbles).
        public float ChunkAmplitude
        {
            get => ChunkWorld.AmpMeters;
            set { if (!Mathf.Approximately(value, ChunkWorld.AmpMeters)) ChunkWorld.SetAmplitude(Mathf.Clamp(value, 0f, 8000f)); }
        }

        // Live bubble radius (resident = (2r+1)²): tunable so you can sweep 5×5→7×7→9×9… in
        // session and find where synchronous loading falls over. Setting it re-streams now.
        public float ChunkRadius
        {
            get => ChunkWorld.Radius;
            set
            {
                int r = Mathf.Clamp(Mathf.RoundToInt(value), 1, 50);
                if (r == ChunkWorld.Radius) return;
                ChunkWorld.Radius = r;
                if (ChunkWorld.Active)
                {
                    ChunkWorld.Tick(ChunkCam(), eager: true);
                }
            }
        }

        // Invisible preload rings beyond the visible radius (the streaming buffer). Re-streams.
        public float ChunkPreloadDepth
        {
            get => ChunkWorld.PreloadDepth;
            set
            {
                int d = Mathf.Clamp(Mathf.RoundToInt(value), 0, 6);
                if (d == ChunkWorld.PreloadDepth) return;
                ChunkWorld.PreloadDepth = d;
                if (ChunkWorld.Active)
                {
                    ChunkWorld.Tick(ChunkCam(), eager: true);
                }
            }
        }

        // Chunks loaded per frame while streaming (0 = unlimited). Lower = smoother but slower to
        // fill; the preload depth is the buffer that lets a low budget keep up as you move.
        public float ChunkBudget
        {
            get => ChunkWorld.Budget;
            set => ChunkWorld.Budget = Mathf.Clamp(Mathf.RoundToInt(value), 0, 32);
        }

        // Screen-space LOD quality: target screen PIXELS per terrain vertex. Lower = finer/denser
        // (more triangles), higher = coarser/cheaper. Drives the per-chunk resolution from zoom.
        public float ChunkPixelsPerVertex
        {
            get => ChunkWorld.PixelsPerVertex;
            set => ChunkWorld.PixelsPerVertex = Mathf.Clamp(value, 1.5f, 24f);
        }

        // Chunk heightmap resolution (must be 2ⁿ+1). The biggest per-chunk SetHeights-cost lever:
        // 129 ≈ 8 m/sample (fastest), 257 ≈ 4 m, 513 ≈ 2 m (sharpest sculpt). Rebuilds the bubble.
        public float ChunkRes
        {
            get => ChunkWorld.Res;
            set
            {
                int[] valid = { 129, 257, 513, 1025 };   // NEAR (ring-0) res; LOD steps it down outward
                int r = valid[0];
                foreach (int v in valid) if (Mathf.Abs(v - value) < Mathf.Abs(r - value)) r = v;
                if (r == ChunkWorld.Res) return;
                ChunkWorld.SetResolution(r);
                if (ChunkWorld.Active)
                {
                    ChunkWorld.Tick(ChunkCam(), eager: true);
                }
            }
        }

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
            // Load scatter/fence prefabs from Resources at runtime — folders moved to
            // Assets/Resources/{Trees,Rocks,Fences}, so nothing needs assigning on the
            // GameObject. Runs in play AND builds.
            TreeLayer.LoadFromResources("Trees");
            RockLayer.LoadFromResources("Rocks");
            FenceLayer.LoadAssetFromResources("Fences");
#if UNITY_EDITOR
            // Self-heal lost prefab references: if a layer's list is STILL empty (no
            // Resources folder), fall back to the editor Folder load.
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

            // Restore the active backend the save was taken in. If it was a DEM world,
            // rebuild that city from its heightmaps NOW (before draping the saved objects)
            // so rails/scatter conform to the DEM, not the hidden low-poly field. Falls
            // back to low-poly if the city folder is gone (or the save predates v8).
            bool restoringDem = _pendingDemBackend
                && !string.IsNullOrEmpty(_pendingDemCity)
                && DemTerrainWorld.ListWorlds().Contains(_pendingDemCity);
            if (restoringDem)
            {
                LoadDemWorld(_pendingDemCity);
                // Re-apply saved sculpt/carve onto the rebuilt tiles (same city only), BEFORE
                // draping objects so trees/rails conform to the restored terrain shape.
                if (_pendingDemEdits != null && _pendingDemEdits.City == _pendingDemCity)
                    DemTerrainWorld.ApplyEdits(_pendingDemEdits);
            }
            else SetActiveBackend(false); // ensure low-poly is shown + DEM hidden

            TreeLayer.SpawnPending(Surf); // scatter from the save (heights now known)
            RockLayer.SpawnPending(Surf);
            FenceLayer.Rebuild(Surf);     // linework from the save
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);
            PlanLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf);
            RebuildBuiltRoads();   // re-sweep the 3D roads from the loaded per-segment Built flags (persist across restart)
            RetainingWallLayer.Rebuild(Surf);   // wall mesh from the save (terrain grade already in the edits)
            RebuildContours();
            if (!DemBackend) ApplyWater(); // low-poly water only; DEM water handled by LoadDemWorld

            // Stand up scene services, sized to the actual terrain.
            if (AutoLighting) EnsureAmbiance();
            if (AutoCameraControl) EnsureCameraControl();
            if (AutoTuning) EnsureTuning();

            // The DEM wires the camera's terrain-aware clamp during Build, but the fly
            // camera may not have existed yet then — re-point it now that it does.
            if (restoringDem && DemTerrainWorld.HasWorld) DemTerrainWorld.WireCameraToDem();

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

        // March the camera ray against the terrain HEIGHTFIELD (Surf.SampleHeight) to find the ground point WITHOUT a
        // physics collider — so line tools place/connect anywhere the terrain is defined, even where the streaming
        // chunk bubble hasn't cooked a collider. Adaptive step (coarse high above ground, fine near it) + bisection
        // refine. Returns false if the ray never descends through the surface (e.g. looking at the horizon).
        bool RaycastTerrainHeightfield(Ray ray, out Vector3 point)
        {
            point = default;
            if (Surf == null) return false;
            Vector3 o = ray.origin, d = ray.direction;
            float prevAbove = o.y - Surf.SampleHeight(o.x, o.z);
            if (prevAbove < 0f) return false;                       // camera under the surface — nothing to hit downward
            float t = 0f; const float maxT = 50000f; int guard = 0;
            while (t < maxT && guard++ < 4000)
            {
                float step = Mathf.Clamp(prevAbove * 0.5f, 1f, 500f);   // overshoot-limited near the ground
                t += step;
                Vector3 cur = o + d * t;
                float above = cur.y - Surf.SampleHeight(cur.x, cur.z);
                if (above <= 0f)                                    // crossed the surface between (t-step) and t → bisect
                {
                    float lo = t - step, hi = t;
                    for (int i = 0; i < 24; i++)
                    {
                        float mid = (lo + hi) * 0.5f; Vector3 m = o + d * mid;
                        if (m.y - Surf.SampleHeight(m.x, m.z) > 0f) lo = mid; else hi = mid;
                    }
                    Vector3 h = o + d * hi;
                    point = new Vector3(h.x, Surf.SampleHeight(h.x, h.z), h.z);
                    return true;
                }
                prevAbove = above;
            }
            return false;
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
                    rl.ExtensionOffAxis = false;   // TrySnapExtensionHard sets it if reached + off-axis
                    // Hovering another endpoint: snap to it (the auto-fillet join), ahead of
                    // the bend snap.
                    if (rl.TryChainConnectSnap(flat, out Vector2 ccs)) return new Vector3(ccs.x, raw.y, ccs.y);
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
                if (_lineActive is RoadPlanLayer rdp)
                {
                    rdp.StraightOffAxis = false;
                    // Shift-curve: bend → equal-leg lock → PAC owns the cursor. Then node JOIN (any-angle junction).
                    if (rdp.TrySnapBendToTarget(flat, out Vector2 rbt)) return new Vector3(rbt.x, raw.y, rbt.y);
                    if (rdp.TrySnapCurveSymmetry(flat, out Vector2 rsym)) return new Vector3(rsym.x, raw.y, rsym.y);
                    if (rdp.PlacingCurveEnd) return raw;
                    // Snap to the highlighted (hovered) node first, so snapping matches what the cursor is over.
                    if (rdp.TrySnapToHoverNode(out Vector2 rhv)) return new Vector3(rhv.x, raw.y, rhv.y);
                    // Starting a fresh chain near a road END → snap onto it so the new plan resumes off that road.
                    if (rdp.TrySnapToRoadEnd(flat, out Vector2 rend)) return new Vector3(rend.x, raw.y, rend.y);
                    // Hold Alt/Option = FREE placement: skip the colinear hard-lock + extension snaps so you can
                    // lay a slightly-unaligned road at any angle. (Node-join + curve locks above still apply.)
                    bool roadFreeAngle = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                    // Reactive guides FIRST (node colinear/perpendicular, guide crossings, segment CENTERPOINTS): a
                    // deliberate alignment, so it must win over the older road-proximity snaps below AND the guided-turns
                    // lock. Clear the off-axis flag so the (possibly angled) click isn't suppressed.
                    if (!roadFreeAngle && rdp.TrySnapToGuides(flat, out Vector2 rdg)) { rdp.StraightOffAxis = false; return new Vector3(rdg.x, raw.y, rdg.y); }
                    // Extending straight toward a road: snap to where the colinear extension CROSSES it (keeps the
                    // segment straight) rather than the nearest road point — must win over the plain road-segment snap.
                    if (!roadFreeAngle && rdp.TrySnapExtensionToRoad(flat, out Vector2 rxr)) return new Vector3(rxr.x, raw.y, rxr.y);
                    // Meet a nearby existing road at 90°: snap onto the perpendicular foot off the tail.
                    if (!roadFreeAngle && rdp.TrySnapPerpendicularToRoad(flat, out Vector2 rpp)) return new Vector3(rpp.x, raw.y, rpp.y);
                    if (rdp.TrySnapToOwnNode(flat, out Vector2 rdn)) return new Vector3(rdn.x, raw.y, rdn.y);
                    if (!roadFreeAngle)
                    {
                        // Guided straights: hard-lock to colinear / 90° (the off-axis flag suppresses kinked clicks).
                        bool rsnap = rdp.SnapStraightConstrained(flat, out Vector2 rsh, out bool roff);
                        rdp.StraightOffAxis = roff;
                        if (rsnap) return new Vector3(rsh.x, raw.y, rsh.y);
                        if (rdp.TrySnapToExtension(flat, out Vector2 rde)) return new Vector3(rde.x, raw.y, rde.y);
                    }
                }
                // Grid snap makes no sense while shaping an arc that EXTENDS existing track —
                // the bend/end are pinned to the MDT / extension line / PAC. But a brand-new
                // rail curve has no incoming tangent to honour, so snapping its bend to the
                // grid is exactly what you want — keep grid snap on in that case.
                bool curveMode = (_lineActive is RailTrackLayer crl && crl.InCurveMode)
                              || (_lineActive is RailPlanLayer cpl && cpl.InCurveMode)
                              || (_lineActive is RoadPlanLayer crd && crd.InCurveMode);
                bool brandNewRailCurve = _lineActive is RailTrackLayer nrl && !nrl.ChainExtendsExisting;
                return (curveMode && !brandNewRailCurve) ? raw : ApplyGridSnap(raw);
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
            if (RoadPlanLayer == null) RoadPlanLayer = new RoadPlanLayer();
            if (string.IsNullOrEmpty(RoadPlanLayer.Name)) RoadPlanLayer.Name = "Road Plan";
            if (RoadPlanLayer.RoadWidth <= 0f) RoadPlanLayer.RoadWidth = 14f;
            if (RoadPlanLayer.SampleStep <= 0f) RoadPlanLayer.SampleStep = 2f;
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

        // The upper-right IMGUI mode/help box (rail/plan/linework status + the plan cut/fill summary).
        // Hidden by default — the plan's earthwork/mass-haul now surfaces in the Inspect (I) hover, and
        // the box (when flipped on) drops below the minimap so they don't overlap (see DrawPanels).
        public bool ShowModeHud = false;

        // Palette IMGUI for the active scatter layer, or a hint for linework.
        void OnGUI()
        {
            // Loading overlay (drawn before the early-return below, in real screen space).
            if (DemTerrainWorld.Building)
            {
                float bw = 300f, bh = 76f;
                var box = new GUIStyle(GUI.skin.box)
                { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                GUI.Box(new Rect((Screen.width - bw) * 0.5f, (Screen.height - bh) * 0.5f, bw, bh), "Loading DEM…", box);
            }
            // Mode/sub-mode + snap now live in the UI Toolkit palette footer (RailPalette),
            // so the old always-on IMGUI status strip is gone.
            DrawBrushModeIcon();   // per-mode glyph beside the ring (sculpt modes only)
            bool sculptHud = (Brush == BrushMode.Slope || Brush == BrushMode.Flatten)
                             && _lineActive == null && _active == null
                             && NetworkDesigner.UI.PaletteBase.IsOpenId("Terrain");   // only with Terrain palette up
            if (_lineActive == null && _active == null && !sculptHud && !ChunkWorld.Active) return;
            Matrix4x4 prev = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(UiScale, UiScale, 1f));
            DrawPanels();
            GUI.matrix = prev;
        }

        // A small per-mode glyph just outside the brush ring (raw screen pixels, drawn
        // before any UiScale matrix). Sculpt modes only — Raise/Lower use generated
        // arrows; Smooth/Flatten/Slope use assigned textures (none = nothing drawn).
        void DrawBrushModeIcon()
        {
            if (_active != null || _lineActive != null) return;   // sculpt modes only
            if (!_brushCursorVisible) return;
            bool generated = (Brush == BrushMode.Raise || Brush == BrushMode.Lower);
            Texture2D tex = IconForBrush(Brush);
            if (tex == null) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(_brushCursorWorld);
            if (sp.z <= 0f) return;                                // behind the camera
            // Ring radius in screen px, so the icon sits just outside it at any zoom.
            Vector3 edge = cam.WorldToScreenPoint(_brushCursorWorld + cam.transform.right * BrushRadius);
            float screenR = Vector2.Distance(new Vector2(sp.x, sp.y), new Vector2(edge.x, edge.y));
            float size = Mathf.Max(8f, BrushIconSize);
            float x = sp.x + screenR + 6f;
            float y = Screen.height - sp.y - size * 0.5f;          // GUI y is top-down
            Color old = GUI.color;
            GUI.color = generated ? BrushCursorColor : Color.white;
            GUI.DrawTexture(new Rect(x, y, size, size), tex, ScaleMode.ScaleToFit, true);
            GUI.color = old;
        }

        Texture2D IconForBrush(BrushMode m)
        {
            switch (m)
            {
                case BrushMode.Raise:   return RaiseIcon != null ? RaiseIcon : (_genUpArrow ??= MakeArrowTex(true));
                case BrushMode.Lower:   return LowerIcon != null ? LowerIcon : (_genDownArrow ??= MakeArrowTex(false));
                case BrushMode.Smooth:  return SmoothIcon;
                case BrushMode.Flatten: return FlattenIcon;
                case BrushMode.Slope:   return SlopeIcon;
                case BrushMode.Sea:     return SmoothIcon;
                case BrushMode.Measure: return SlopeIcon;
                case BrushMode.Forest:  return SmoothIcon;
                default:                return null;
            }
        }

        // A filled triangle (apex up or down) on transparent, tinted at draw time.
        static Texture2D MakeArrowTex(bool up)
        {
            const int s = 64;
            var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color32[s * s];
            var solid = new Color32(255, 255, 255, 255);
            for (int y = 0; y < s; y++)
            {
                // ty: 0 at the base, 1 at the apex (row 0 is the bottom of a Texture2D).
                float ty = up ? y / (float)(s - 1) : 1f - y / (float)(s - 1);
                float halfW = (1f - ty) * 0.5f * (s - 1);
                int cx = s / 2;
                for (int x = 0; x < s; x++)
                    px[y * s + x] = Mathf.Abs(x - cx) <= halfW ? solid : default;
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        void DrawPanels()
        {
            // Plan grade % labels are now world-space TMP (UpdatePlanGradeLabels), not IMGUI.
            DrawCurveDimLabels();
            DrawCurveTickLabels();
            DrawSpeedLabels();
            DrawDesignSpeedReadout();
            DrawRoadCurveLabels();
            DrawCurveInspectLabels();
            DrawSlopeCurveBadge();
            DrawSlopeGradeReadout();
            DrawChunkMinimap();
            DrawMeasure();
            if (ShowModeHud && _lineActive != null)
            {
                bool rail = _lineActive is RailTrackLayer;
                bool plan = _lineActive is RailPlanLayer;
                // Drop below the corner minimap when it's up (chunk world) so they don't overlap.
                float hudY = (ChunkWorld.Active && _showMinimap) ? 264f : 8f;
                GUILayout.BeginArea(new Rect(Vw - 308f, hudY, 300f, rail ? 332f : (plan ? 292f : 128f)), GUI.skin.box);
                GUILayout.Label(_lineActive.LayerName + " mode");
                GUILayout.Label(rail || plan
                    ? "Click: straight segment. Hold Shift: click a corner, then the end = curve."
                    : "Left-click: add node (chains)\nRight-click: delete near node / end chain\nBackspace: undo last node");
                if (_lineActive is RoadPlanLayer rdHud && rdHud.HasOpenChain)
                {
                    Color prevC = GUI.color; GUI.color = new Color(0.30f, 1f, 0.5f);
                    GUILayout.Label("● Drawing a chain (green node) — right-click to finish");
                    GUI.color = prevC;
                }
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
                        // Earthwork volume + mass-haul balance (net cut vs fill).
                        float netM3 = pl.CutVolumeM3 - pl.FillVolumeM3;
                        float maxM3 = Mathf.Max(1f, Mathf.Max(pl.CutVolumeM3, pl.FillVolumeM3));
                        GUILayout.Label($"Earthwork: cut {pl.CutVolumeM3:N0} m³ · fill {pl.FillVolumeM3:N0} m³");
                        GUILayout.Label(Mathf.Abs(netM3) < 0.05f * maxM3
                            ? "Mass-haul: balanced (no haul)"
                            : netM3 > 0f ? $"Mass-haul: +{netM3:N0} m³ surplus (haul off)"
                                         : $"Mass-haul: {-netM3:N0} m³ deficit (import fill)");
                        GUILayout.Label("Key: solid=at-grade, dashed=cut,\ndbl-dash=fill · cyan/purple/red=brdg/tun/over");
                    }
                    int bs = PlanBuildableStatus();
                    GUILayout.Label(bs < 0 ? "Draw a plan, then 'plan.buildRail'."
                        : bs == 0 ? "Buildable ✓ — 'plan.buildRail' lays track."
                        : $"{bs} segment(s) over grade — grade before building.");
                }
                if (_lineActive is RailTrackLayer rt)
                {
                    GUILayout.Label("Click a rail edge: insert node (split).\nShift+right-click an edge: remove it (keep nodes).\nClick a node puck: branch from it.");
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
                    if (_connectStatus != null)
                        GUILayout.Label(_connectStatus);
                    else
                        GUILayout.Label("C+click two ends, or hover an end in curve mode, to join.");
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
            // Scatter (IMGUI) palette — skipped while the UI Toolkit Scatter/Fence palette
            // is open (it drives the same layer), so they don't both show.
            if (_active != null)
            {
                if (!NetworkDesigner.UI.PaletteBase.IsOpenId("ScatterFence")
                    && _active.DrawPalette()) { _dirtySince = Time.realtimeSinceStartup; SavePacks(); }
                return;
            }

            // (The slope tool's readout now floats at the cursor — grade %/max via
            // DrawSlopeGradeReadout, legs/angle/guides via DrawSlopeCurveBadge — so no
            // upper-right box.)

            // Flatten tool (4): a small HUD beside the cursor with the elevation under
            // it, plus the picked target height once you've right-clicked (eyedropper).
            if (Brush == BrushMode.Flatten && _flattenCursorValid)
            {
                float s = Mathf.Max(0.25f, UiScale);
                Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / s;
                string txt = $"Elev {_flattenCursorElev:0.0} m";
                if (_flattenTargetPicked)
                {
                    // Chunk/DEM backends flatten to _demFlattenY (the real hit-point height); only the
                    // low-poly field uses the grid-sampled _flattenTarget. Show whichever is in play.
                    float shownTarget = (ChunkWorld.Active || (DemBackend && DemTerrainWorld.HasWorld))
                        ? _demFlattenY
                        : (_field != null ? _field.Origin.y + _flattenTarget : _flattenTarget);
                    txt += $"\nTarget {shownTarget:0.0} m";
                }
                var content = new GUIContent(txt);
                Vector2 size = GUI.skin.box.CalcSize(content);
                GUI.Box(new Rect(m.x + 18f, m.y + 2f, size.x + 8f, size.y + 4f), content);
            }
        }

        // Float a grade-% label over each plan segment (current terrain), so you can read
        // the natural grade before earthworks and the achieved grade after. Shown while
        // editing the plan or using the slope tool. Red box = over the plan's max grade.
        // Drive the world-space TMP grade labels (replaces the old IMGUI GUI.Box version). Called from
        // Update every frame; hides the labels when the plan/grade view isn't active.
        void UpdatePlanGradeLabels()
        {
            var wl = WorldGradeLabels.Instance;
            if (wl == null) return;
            bool show = PlanLayer != null && _active == null && PlanLayer.ShowGradeLabels
                        && (Brush == BrushMode.Slope || _lineActive is RailPlanLayer);
            if (!show) { wl.Show(null, null); return; }
            PlanLayer.CollectEdgeGrades(Surf, _planGrades);
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            wl.Show(_planGrades, cam);
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
            else if (_lineActive is RoadPlanLayer rd && rd.PlacingCurveEnd) { pos = rd.CurveTickWorld; deg = rd.CurveTickDeg; }
            if (pos == null || pos.Count == 0) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            Color col = new Color(1f, 0.88f, 0.2f);   // PAC yellow
            for (int i = 0; i < pos.Count && i < deg.Count; i++)
                DrawWorldText(cam, s, pos[i], $"{deg[i]}°", col);
        }

        // Slope curve mode: a "CURVE" badge anchored at the armed bend, so it's obvious
        // the next click grades a curve (and where the bend is). Only while armed.
        // Top-right chunk minimap (chunk-test mode only): a north-up grid centred on the
        // camera's chunk. Loaded chunks gray (blue flash for the freshly-streamed ones), visited-
        // but-unloaded chunks pink, the active chunk outlined with a heading arrow.
        void DrawChunkMinimap()
        {
            if (!ChunkWorld.Active || !_showMinimap) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            Vector3 cp = cam.transform.position;
            Vector2Int active = ChunkWorld.ChunkAt(cp.x, cp.z);

            // DEM world: a pre-baked 3D relief diorama (with the camera-tracking marker) instead of
            // the flat chunk grid. Falls back to the grid for the procedural/infinite flat test.
            var diorama = MinimapDiorama.Current;
            if (diorama != null && diorama.Texture != null)
            {
                float dm = 220f, dleft = Vw - dm - 12f, dtop = 12f;
                Color pc = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.4f);
                GUI.DrawTexture(new Rect(dleft - 5f, dtop - 5f, dm + 10f, dm + 24f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(dleft, dtop, dm, dm), diorama.Texture, ScaleMode.ScaleToFit, false);
                int totX = Mathf.Max(1, Mathf.RoundToInt(DemChunkSource.WorldWidthX / ChunkWorld.ChunkSize));
                int totZ = Mathf.Max(1, Mathf.RoundToInt(DemChunkSource.WorldLengthZ / ChunkWorld.ChunkSize));
                GUI.Label(new Rect(dleft, dtop + dm + 3f, dm, 18f),
                          $"chunk {active.x},{active.y}   loaded {ChunkWorld.LoadedCount} / {totX * totZ}  ({totX}×{totZ})");
                GUI.color = pc;
                return;
            }

            int W = _minimapW;               // ± chunks shown (scroll over the map to zoom)
            float map = 190f, cell = map / (2 * W + 1);
            float left = Vw - map - 12f, top = 12f;
            Color prevCol = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.4f);
            GUI.DrawTexture(new Rect(left - 5f, top - 5f, map + 10f, map + 24f), Texture2D.whiteTexture);

            float now = Time.realtimeSinceStartup;
            for (int dz = -W; dz <= W; dz++)
                for (int dx = -W; dx <= W; dx++)
                {
                    var c = new Vector2Int(active.x + dx, active.y + dz);
                    float cx = left + (dx + W) * cell, cy = top + (W - dz) * cell;   // +Z (north) = up
                    Color col;
                    if (ChunkWorld.IsLoaded(c))
                    {
                        float lt = ChunkWorld.LoadedAt(c);
                        if (lt >= 0f && now - lt < 1.2f) col = new Color(0.30f, 0.55f, 1f, 0.95f);   // fresh — flash
                        else
                        {
                            int lvl = ChunkWorld.LodLevelOf(c);                 // LOD ring: near bright → far dim
                            float b = Mathf.Clamp01(0.85f - Mathf.Max(0, lvl) * 0.18f);
                            col = new Color(b, b, b, 0.85f);
                        }
                    }
                    else if (ChunkWorld.WasVisited(c)) col = new Color(0.86f, 0.55f, 0.55f, 0.7f);   // pink
                    else col = new Color(0.5f, 0.5f, 0.5f, 0.12f);
                    GUI.color = col;
                    GUI.DrawTexture(new Rect(cx + 0.5f, cy + 0.5f, cell - 1f, cell - 1f), Texture2D.whiteTexture);
                }

            // Active chunk outline + heading arrow.
            float ax = left + W * cell, ay = top + W * cell;
            GUI.color = Color.black;
            const float bw = 2f;
            GUI.DrawTexture(new Rect(ax, ay, cell, bw), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(ax, ay + cell - bw, cell, bw), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(ax, ay, bw, cell), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(ax + cell - bw, ay, bw, cell), Texture2D.whiteTexture);
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-4f)
            {
                float headingDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;   // 0 = +Z (up)
                Vector2 ctr = new Vector2(ax + cell * 0.5f, ay + cell * 0.5f);
                Matrix4x4 prevM = GUI.matrix;
                GUIUtility.RotateAroundPivot(headingDeg, ctr);
                GUI.color = new Color(0.1f, 0.1f, 0.1f, 1f);
                GUI.DrawTexture(new Rect(ctr.x - 1.5f, ctr.y - cell * 0.42f, 3f, cell * 0.55f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(ctr.x - 4f, ctr.y - cell * 0.42f, 8f, 5f), Texture2D.whiteTexture);
                GUI.matrix = prevM;
            }

            GUI.color = Color.white;
            GUI.Label(new Rect(left, top + map + 3f, map, 18f), $"chunk {active.x},{active.y}   loaded {ChunkWorld.LoadedCount}");
            GUI.color = prevCol;
        }

        // Measure tool: draws the A→(B or live cursor) line + endpoint dots + a distance tooltip.
        void DrawMeasure()
        {
            if (Brush != BrushMode.Measure || !_measHasA) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            Vector3 a = _measA, b = _measHasB ? _measB : _measCursor;
            Vector3 spa = cam.WorldToScreenPoint(a), spb = cam.WorldToScreenPoint(b);
            if (spa.z <= 0f || spb.z <= 0f) return;   // an endpoint is behind the camera
            Vector2 ga = new Vector2(spa.x / s, (Screen.height - spa.y) / s);
            Vector2 gb = new Vector2(spb.x / s, (Screen.height - spb.y) / s);
            Color line = new Color(1f, 0.85f, 0.2f, 0.5f);   // translucent alpha line

            // Line in RAW screen space (the rotated draw must NOT go through the UiScale matrix, or
            // the pivot gets scaled and the line shifts). Dots + tooltip stay in scaled space below.
            DrawScreenLine(new Vector2(spa.x, Screen.height - spa.y),
                           new Vector2(spb.x, Screen.height - spb.y), line, 3f);
            Color prev = GUI.color;
            GUI.color = line;
            GUI.DrawTexture(new Rect(ga.x - 3f, ga.y - 3f, 6f, 6f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(gb.x - 3f, gb.y - 3f, 6f, 6f), Texture2D.whiteTexture);
            GUI.color = prev;

            float dist = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));   // horizontal ground distance
            float dh = b.y - a.y;
            string txt = dist >= 1000f ? $"{dist / 1000f:0.00} km" : $"{dist:0.0} m";
            if (Mathf.Abs(dh) > 0.5f) txt += $"   Δ {(dh >= 0 ? "+" : "")}{dh:0} m";
            // Boxed tooltip near the moving end (B / cursor).
            var content = new GUIContent(txt);
            Vector2 sz = GUI.skin.label.CalcSize(content);
            float bx = gb.x + 12f, by = gb.y - sz.y - 6f;
            var box = new Rect(bx - 4f, by - 2f, sz.x + 8f, sz.y + 4f);
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(bx, by, sz.x + 2f, sz.y), content);
            GUI.color = prev;
        }

        // A 1-pixel-thick screen-space line between two GUI-space points (under the UiScale matrix).
        static void DrawScreenLine(Vector2 a, Vector2 b, Color col, float width)
        {
            float len = Vector2.Distance(a, b);
            if (len < 0.5f) return;
            float ang = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            Matrix4x4 prevM = GUI.matrix;
            Color prevC = GUI.color;
            GUI.matrix = Matrix4x4.identity;   // raw screen space — no UiScale skew on the pivot
            GUIUtility.RotateAroundPivot(ang, a);
            GUI.color = col;
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
            GUI.matrix = prevM;
            GUI.color = prevC;
        }

        // Slope tool: live grade % + the max, floating just above the B cursor (so you read
        // it where you're working, not only in the corner box). Red when over the max.
        void DrawSlopeGradeReadout()
        {
            if (Brush != BrushMode.Slope || !_slopeArmed || !_slopeEndValid) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            bool over = Mathf.Abs(_slopeGradePct) > SlopeMaxGradePct;
            Color col = over ? new Color(1f, 0.4f, 0.35f) : new Color(0.55f, 1f, 0.6f);
            Vector3 anchor = new Vector3(_slopeEnd.x, SlopeElevAtWorld(_slopeEnd) + 3.5f, _slopeEnd.z);
            DrawWorldText(cam, s, anchor, $"{_slopeGradePct:0.0}%  /  max {SlopeMaxGradePct:0.0}%", col,
                          1.1f, new Vector2(0f, -14f));
        }

        // Slope curve (shift) mode: the bend marker, the two leg lengths, and the deflection
        // angle at the corner — the same guidance the rail curve tool gives. (The dashed
        // extension guides through the corner are drawn into the cursor mesh, AppendSlopeOverlay.)
        void DrawSlopeCurveBadge()
        {
            if (!_slopeCornerPending || Brush != BrushMode.Slope) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            Color col = new Color(0.4f, 1f, 0.5f);
            Vector2 a2 = new Vector2(_slopeA.x, _slopeA.z);
            Vector2 cor = new Vector2(_slopeCorner.x, _slopeCorner.z);
            Vector2 b2 = _slopeEndValid ? new Vector2(_slopeEnd.x, _slopeEnd.z) : cor;
            DrawWorldText(cam, s, ToWorldXZ(cor, 2f), "◆ CURVE", col);
            float legA = Vector2.Distance(a2, cor), legB = Vector2.Distance(cor, b2);
            if (legA > 0.5f) DrawWorldText(cam, s, ToWorldXZ((a2 + cor) * 0.5f, 2f), $"{legA:0} m", col);
            if (_slopeEndValid && legB > 0.5f)
            {
                DrawWorldText(cam, s, ToWorldXZ((cor + b2) * 0.5f, 2f), $"{legB:0} m", col);
                Vector2 inDir = cor - a2, outDir = b2 - cor;
                if (inDir.sqrMagnitude > 1e-4f && outDir.sqrMagnitude > 1e-4f)
                    DrawWorldText(cam, s, ToWorldXZ(cor, 3.5f), $"{Vector2.Angle(inDir, outDir):0}°", col);
            }
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
            else if (_lineActive is RoadPlanLayer rd)
            { kmh = rd.DesignSpeedKmh; col = rd.LastPreviewTooTight ? new Color(1f, 0.3f, 0.25f) : new Color(1f, 0.62f, 0.2f); haveTail = rd.TryGetTailXZ(out tail); }
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
            string txt = $"{kmh:0} km/h";
            // While a road shift-curve is armed, show the pending radius vs. the design-speed minimum.
            if (_lineActive is RoadPlanLayer rdp && rdp.CornerPending && !float.IsPositiveInfinity(rdp.LastPreviewRadius))
                txt += rdp.LastPreviewTooTight
                    ? $"  R {rdp.LastPreviewRadius:0} m (min {rdp.MinRadiusForSpeed:0})"
                    : $"  R {rdp.LastPreviewRadius:0} m";
            DrawWorldText(cam, s, anchor, txt, col, 0.75f, new Vector2(-40f, 0f));
        }

        // Road shift-curve dimensions: both construction-leg lengths (tail→bend, bend→end) at their
        // midpoints, and the deflection angle at the bend. Red when the curve is too tight to build.
        void DrawRoadCurveLabels()
        {
            if (!(_lineActive is RoadPlanLayer rd)) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float s = Mathf.Max(0.25f, UiScale);
            if (rd.PreviewCurveActive)
            {
                Color col = rd.LastPreviewTooTight ? new Color(1f, 0.3f, 0.25f) : new Color(1f, 0.85f, 0.3f);
                if (rd.PreviewLegA > 0.5f)
                    DrawWorldText(cam, s, ToWorldXZ((rd.PreviewTail + rd.PreviewCorner) * 0.5f, 2f), $"{rd.PreviewLegA:0} m", col);
                if (rd.PreviewLegB > 0.5f)
                    DrawWorldText(cam, s, ToWorldXZ((rd.PreviewCorner + rd.PreviewEnd) * 0.5f, 2f), $"{rd.PreviewLegB:0} m", col);
                DrawWorldText(cam, s, ToWorldXZ(rd.PreviewCorner, 2f), $"{rd.PreviewDeflectionDeg:0}°", col);
            }
            else if (rd.PreviewStraightActive && rd.PreviewStraightDist > 0.5f)
            {
                // Live span while extending a straight off the tail (and the first leg while arming a curve).
                DrawWorldText(cam, s, ToWorldXZ((rd.PreviewStraightFrom + rd.PreviewStraightTo) * 0.5f, 2f),
                    $"{rd.PreviewStraightDist:0} m", new Color(1f, 0.85f, 0.3f));
            }
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
            var lines = new List<string>(6);
            if (decel != null) lines.Add(decel);
            if (isCurve) lines.Add($"{radius:0}m radius, max speed: {maxSpd:0} km/h");
            else lines.Add($"{len:0}m queue (~{trains:0.0} trains)");
            if (hasGrade) lines.Add($"{gradePct:0.0}% grade");
            lines.Add($"{rated:0} km/h rated");
            // Inspecting the PLAN also surfaces its whole-plan earthwork + mass-haul (reuse the I mode).
            if (_lineActive is RailPlanLayer plE && plE.ShowAnalysis && plE.RouteLength >= 1f)
            {
                lines.Add($"plan: cut {plE.CutVolumeM3:N0} m³ · fill {plE.FillVolumeM3:N0} m³");
                float net = plE.CutVolumeM3 - plE.FillVolumeM3;
                float maxV = Mathf.Max(1f, Mathf.Max(plE.CutVolumeM3, plE.FillVolumeM3));
                lines.Add(Mathf.Abs(net) < 0.05f * maxV ? "mass-haul: balanced"
                    : net > 0f ? $"mass-haul: +{net:N0} m³ surplus" : $"mass-haul: {-net:N0} m³ deficit");
            }
            DrawWorldTextBlock(cam, s, ToWorldXZ(mid, 3f), lines, col);
        }

        Vector3 ToWorldXZ(Vector2 xz, float lift)
        {
            float y = Surf != null ? Surf.SampleHeight(xz.x, xz.y) : 0f;   // drape on the ACTIVE surface
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
        void DrawWorldText(Camera cam, float s, Vector3 world, string text, Color color,
                           float fontScale = 1f, Vector2 screenOffset = default)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return;
            float mx = sp.x / s + screenOffset.x, my = (Screen.height - sp.y) / s + screenOffset.y;
            var style = GUI.skin.label;
            int prevSize = style.fontSize;
            if (!Mathf.Approximately(fontScale, 1f))
                style.fontSize = Mathf.Max(1, Mathf.RoundToInt((prevSize > 0 ? prevSize : 12) * fontScale));
            var content = new GUIContent(text);
            Vector2 size = style.CalcSize(content);
            Color prev = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(mx - size.x * 0.5f, my - size.y * 0.5f, size.x + 2f, size.y), content);
            GUI.color = prev;
            style.fontSize = prevSize;   // restore the shared style
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
            // The full-screen map trimmer is a modal overlay — treat it like a panel so sculpt/look/scroll
            // are all gated while it's open.
            if (ChunkMapEditor.IsOpen) return true;
            // UI Toolkit palette swallows its own events, but the legacy Input polling
            // used by the camera/tools is blind to it — so gate on its hover flag too.
            if (NetworkDesigner.UI.PaletteBase.PointerOverUI) return true;
            if (_active == null || _active.PanelRect.width <= 0f) return false;
            float s = Mathf.Max(0.25f, UiScale);
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / s;
            return _active.PanelRect.Contains(m);
        }

        // Linework rebuild/clear — used by the tuning-panel actions and on edits.
        public void RebuildFence() { FenceLayer.Rebuild(Surf); }
        [ContextMenu("Clear Fence")]
        public void ClearFence() { FenceLayer.ClearAll(Surf); _dirtySince = Time.realtimeSinceStartup; }
        public void RebuildPowerLine() { PowerLineLayer.Rebuild(Surf); }
        [ContextMenu("Clear Power Line")]
        public void ClearPowerLine() { PowerLineLayer.ClearAll(Surf); _dirtySince = Time.realtimeSinceStartup; }
        [ContextMenu("Rebuild Rail")]
        public void RebuildRail() { RailLayer.Rebuild(Surf); }

        // Pick up Inspector edits (e.g. dragging the bridge prefab into its slot)
        // live in play mode — object-slot changes don't go through the tunables.
        void OnValidate()
        {
            if (Application.isPlaying && _field != null) { RailLayer.Rebuild(Surf); ApplyWater(); }
        }
        [ContextMenu("Clear Rail")]
        public void ClearRail() { RailLayer.ClearAll(Surf); _dirtySince = Time.realtimeSinceStartup; }

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
        public void RebuildPlan() { PlanLayer.Rebuild(Surf); }
        public void ClearPlan() { PlanLayer.ClearAll(Surf); _dirtySince = Time.realtimeSinceStartup; }
        public void RebuildRoadPlan() { RoadPlanLayer.Rebuild(Surf); }
        // Clear the editable plan linework only. The BUILT 3D roads are committed geometry → they stay standing
        // (use "Remove roads" to delete those). We DO drop the built-edge tracking set, since its indices point
        // at the now-deleted graph; a fresh plan + build starts clean, and the old meshes survive until then.
        // Clear the PLAN (nodes/edges) but KEEP the already-built 3D roads: detach the build root so neither this
        // nor a future build destroys it (the meshes become standalone geometry). Use "Remove roads" to delete them.
        public void ClearRoadPlan()
        {
            if (_roadBuildRoot != null) { _roadBuildRoot.name = "RoadBuild (kept)"; _roadBuildRoot = null; }
            RoadPlanLayer.ClearAll(Surf);
            _dirtySince = Time.realtimeSinceStartup;
        }

        // ── Named road-plan library: per-world snapshots under <world>/RoadPlans/<name>.json, so you can
        // save a work-in-progress, revert to it, load another, etc. (JsonUtility round-trips LineGraphSave). ──
        [System.NonSerialized] string _currentRoadPlanName = "";
        public string CurrentRoadPlanName => _currentRoadPlanName;

        string RoadPlansDir() => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ResolveAutosavePath())), "RoadPlans");

        static string SanitizePlanName(string n)
        {
            n = (n ?? "").Trim();
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n;
        }

        public List<string> ListRoadPlans()
        {
            var list = new List<string>();
            try
            {
                string d = RoadPlansDir();
                if (System.IO.Directory.Exists(d))
                    foreach (var f in System.IO.Directory.GetFiles(d, "*.json"))
                        list.Add(System.IO.Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public void SaveRoadPlanAs(string name)
        {
            string nm = SanitizePlanName(name);
            if (nm.Length == 0) { Debug.LogWarning("[RoadPlan] enter a name to save."); return; }
            try
            {
                string d = RoadPlansDir(); System.IO.Directory.CreateDirectory(d);
                System.IO.File.WriteAllText(System.IO.Path.Combine(d, nm + ".json"),
                    JsonUtility.ToJson(RoadPlanLayer.CollectData(), true));
                _currentRoadPlanName = nm;
                Debug.Log($"[RoadPlan] saved '{nm}'.");
            }
            catch (System.Exception ex) { Debug.LogWarning($"[RoadPlan] save failed: {ex.Message}"); }
        }

        public bool LoadRoadPlan(string name)
        {
            string nm = SanitizePlanName(name);
            string path = System.IO.Path.Combine(RoadPlansDir(), nm + ".json");
            if (!System.IO.File.Exists(path)) { Debug.LogWarning($"[RoadPlan] '{nm}' not found."); return false; }
            try
            {
                var save = JsonUtility.FromJson<LineGraphSave>(System.IO.File.ReadAllText(path));
                if (save == null) { Debug.LogWarning($"[RoadPlan] '{nm}' is unreadable."); return false; }
                RoadPlanLayer.LoadState(save); RoadPlanLayer.Rebuild(Surf);
                RebuildBuiltRoads();   // re-sweep the 3D roads for any segments saved as Built (JSON keeps the flag)
                _currentRoadPlanName = nm; _dirtySince = Time.realtimeSinceStartup;
                Debug.Log($"[RoadPlan] loaded '{nm}'.");
                return true;
            }
            catch (System.Exception ex) { Debug.LogWarning($"[RoadPlan] load failed: {ex.Message}"); return false; }
        }

        // Reload the last saved/loaded named plan, discarding edits since.
        public void RevertRoadPlan()
        {
            if (string.IsNullOrEmpty(_currentRoadPlanName)) { Debug.LogWarning("[RoadPlan] nothing to revert to — Save a named plan first."); return; }
            LoadRoadPlan(_currentRoadPlanName);
        }

        public void DeleteRoadPlan(string name)
        {
            string nm = SanitizePlanName(name);
            try { string path = System.IO.Path.Combine(RoadPlansDir(), nm + ".json"); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (System.Exception ex) { Debug.LogWarning($"[RoadPlan] delete failed: {ex.Message}"); }
            if (_currentRoadPlanName == nm) _currentRoadPlanName = "";
        }
        // Grade-aware A* auto-route between the last two plan points.
        public void AutoRoutePlan()
        {
            if (PlanLayer == null) return;
            string msg = PlanLayer.AutoRouteLastSegment(Surf);
            _dirtySince = Time.realtimeSinceStartup;
            Debug.Log($"[Plan] auto-route — {msg}");
        }

        // Status of the "build rail on the plan centreline" action, surfaced in the
        // plan panel. -1 = empty plan, 0 = buildable, >0 = that many over-grade segments.
        public int PlanBuildableStatus()
        {
            if (_field == null || PlanLayer == null || PlanLayer.Graph == null
                || PlanLayer.Graph.Edges.Count == 0) return -1;
            PlanLayer.AllEdgesBuildable(Surf, out int over);
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
                    + $"({PlanLayer.MaxGradePercent:0.0}%). Grade them (red) before building.");
                return;
            }
            int added = RailLayer.AppendGraph(PlanLayer.Graph, RailLayer.SpeedLimitKmh, Surf);
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
            RailLayer.CollectOpenCuts(Surf, tunnelBury, cuts);
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
            TreeLayer.ConformToSurface(Surf);
            RockLayer.ConformToSurface(Surf);
            FenceLayer.Rebuild(Surf);
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);
            PlanLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf);
            RebuildContours();
            ApplyWater();
            _dirtySince = Time.realtimeSinceStartup;
        }

        // DEM cut/fill grading: flatten the terrain under the rail centreline to the rail's
        // routed ground line (both cut AND fill), forming a roadbed at the design grade. Drives
        // the DEM Sculpt Flatten primitive per centreline sample, then re-stitches seams and
        // re-drapes objects/rail onto the new shape. DESTRUCTIVE (no auto-undo); DEM only.
        // Cut/fill the terrain to the laid rail's grade line. Routes to the streaming chunk world
        // (writing the LOD-independent edit overlay) when it's active, else the DEM backend.
        // Plant a procedural forest across the Forest-tool elevation selection, using the tree layer's
        // active pack/spacing/slope rules + fBM density + Worley clearings. Marks the scene dirty.
        public void GrowForest()
        {
            if (!ChunkWorld.Active) { Debug.LogWarning("[Forest] Available in the chunk/DEM world only."); return; }
            if (!ForestGen.HasSelection) { Debug.LogWarning("[Forest] Select a region first — Forest brush (8), click terrain."); return; }
            float water = ChunkOverlays.ShowWater ? ChunkOverlays.WaterLevel : float.NegativeInfinity;
            int n = ForestGen.Grow(TreeLayer, Surf, water);
            if (n > 0) _dirtySince = Time.realtimeSinceStartup;   // persist on autosave
            Debug.Log($"[Forest] planted {n} instances ({ForestGen.TreeCount} total, {ForestGen.CellCount} grid cells) over {ForestGen.SelectedCells} selected cells"
                      + (ForestGen.TreeCount >= ForestGen.MaxTrees ? " — HIT THE CAP (raise MaxTrees / spacing, or Clear trees)." : "."));
        }

        // Clear the whole instanced forest (and persist the empty state).
        public void ClearForestTrees()
        {
            ForestGen.ClearForest(forget: true);   // deliberate clear → drop the anti-clobber guard so empty persists
            _dirtySince = Time.realtimeSinceStartup;
        }

        public void GradeRailCorridor()
        {
            if (ChunkWorld.Active)
            {
                var targets = new List<Vector3>();
                RailLayer.CollectGradeTargets(Surf, RailLayer.GradeSampleStep, targets);
                if (targets.Count == 0) { Debug.LogWarning("[Grade] No rail to grade."); return; }
                float halfW = Mathf.Max(2f, RailLayer.GradeCorridorWidth * 0.5f);
                float innerFrac = 1f - Mathf.Clamp01(BrushFalloff);
                ChunkWorld.GradeCorridor(targets, halfW, innerFrac);
                RailLayer.Rebuild(Surf); PlanLayer.Rebuild(Surf); RoadPlanLayer.Rebuild(Surf);
                ConformScatterAndLines();   // re-settle scatter/fences onto the carved bed
                _dirtySince = Time.realtimeSinceStartup;
                Debug.Log($"[Grade] Graded {targets.Count} corridor samples (chunk world).");
                return;
            }
            GradeRailCorridorDem();
        }

        public void GradeRailCorridorDem()
        {
            if (!DemTerrainWorld.HasWorld) { Debug.LogWarning("[Grade] No DEM world loaded — grading is DEM-only."); return; }
            var targets = new List<Vector3>();
            RailLayer.CollectGradeTargets(Surf, RailLayer.GradeSampleStep, targets);
            if (targets.Count == 0) { Debug.LogWarning("[Grade] No rail to grade."); return; }
            float halfW = Mathf.Max(2f, RailLayer.GradeCorridorWidth * 0.5f);
            float innerFrac = 1f - Mathf.Clamp01(BrushFalloff);   // plateau: flat roadbed, feathered edge
            // One-shot flatten of the whole roadbed to the grade line (cut AND fill).
            foreach (Vector3 p in targets)
                DemTerrainWorld.FlattenStamp(p, halfW, innerFrac, p.y);
            DemTerrainWorld.StitchAllSeams();
            // Re-conform everything to the carved surface + re-drape the rail on its new bed.
            TreeLayer.ConformToSurface(Surf);
            RockLayer.ConformToSurface(Surf);
            FenceLayer.Rebuild(Surf);
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);
            PlanLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf);
            _dirtySince = Time.realtimeSinceStartup;
            Debug.Log($"[Grade] Flattened {targets.Count} corridor samples to the rail grade line.");
        }

        // Cut a smoothed, slightly-sunken roadbed along the road plan: each segment is flattened to its
        // own smoothed terrain-follow elevation (dropped ExcavationDepth below ground) across its profile
        // footprint, feathering out beyond. Routes through the chunk overlay or the DEM backend like rail.
        public void ExcavateRoadCorridor()
        {
            var beds = new List<(List<Vector3> pts, float flatHalf)>();
            // Node grades come from the per-node DESIGN elevation (captured from the SHAPED surface and
            // stored) — so the cut respects terrain you carved/flattened and re-running is idempotent.
            RoadPlanLayer.CollectExcavationBeds(Surf, beds);
            if (beds.Count == 0) { Debug.LogWarning("[Road] No road plan to excavate — draw a corridor first (;)."); return; }
            float feather = Mathf.Max(0.1f, RoadPlanLayer.CutFeather);

            if (ChunkWorld.Active)
            {
                int n = 0;
                float batter = Mathf.Max(0.25f, RoadPlanLayer.CutBatter);
                foreach (var b in beds)
                {
                    // Flat bed (road footprint + margin shoulder) then cut/fill batters daylighting into the
                    // terrain — proper bench, no floating shelf or scalloped disc edges.
                    ChunkWorld.GradeBatter(b.pts, b.flatHalf, batter, Mathf.Max(0.25f, RoadPlanLayer.FillBatter), RoadPlanLayer.FillReach);
                    n += b.pts.Count;
                }
                RoadPlanLayer.Rebuild(Surf); RailLayer.Rebuild(Surf); PlanLayer.Rebuild(Surf);
                ConformScatterAndLines();   // re-settle scatter/fences onto the carved bed
                _dirtySince = Time.realtimeSinceStartup;
                Debug.Log($"[Road] Excavated {beds.Count} road segments ({n} samples, chunk world).");
                return;
            }

            if (!DemTerrainWorld.HasWorld) { Debug.LogWarning("[Road] Excavation needs the chunk or DEM world."); return; }
            int m = 0;
            foreach (var b in beds)
            {
                float r = b.flatHalf + feather; float innerFrac = b.flatHalf / r;
                foreach (Vector3 p in b.pts) { DemTerrainWorld.FlattenStamp(p, r, innerFrac, p.y); m++; }
            }
            DemTerrainWorld.StitchAllSeams();
            TreeLayer.ConformToSurface(Surf); RockLayer.ConformToSurface(Surf);
            FenceLayer.Rebuild(Surf); PowerLineLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf); RailLayer.Rebuild(Surf); PlanLayer.Rebuild(Surf);
            _dirtySince = Time.realtimeSinceStartup;
            Debug.Log($"[Road] Excavated {beds.Count} road segments ({m} samples) into the DEM bed.");
        }

        // Excavate just ONE segment (driven by its in-world Excavate button) and mark that edge Excavated.
        public void ExcavateRoadSegment(int edgeIndex)
        {
            if (RoadPlanLayer.IsEdgeBridge(edgeIndex)) { Debug.Log($"[Road] Segment {edgeIndex} is a bridge — skipping excavation (it spans the gap)."); return; }
            if (!RoadPlanLayer.EdgeExcavationBed(Surf, edgeIndex, out var pts, out float flatHalf)) return;
            float feather = Mathf.Max(0.1f, RoadPlanLayer.CutFeather);
            float r = flatHalf + feather; float innerFrac = flatHalf / r;   // (DEM backend still uses the feathered stamp)
            if (ChunkWorld.Active)
            {
                ChunkWorld.GradeBatter(pts, flatHalf, Mathf.Max(0.25f, RoadPlanLayer.CutBatter), Mathf.Max(0.25f, RoadPlanLayer.FillBatter), RoadPlanLayer.FillReach);   // daylighting bench
                RoadPlanLayer.SetEdgeExcavated(edgeIndex, true);
                RoadPlanLayer.Rebuild(Surf); RailLayer.Rebuild(Surf); PlanLayer.Rebuild(Surf);
                ConformScatterAndLines();
            }
            else if (DemTerrainWorld.HasWorld)
            {
                foreach (Vector3 p in pts) DemTerrainWorld.FlattenStamp(p, r, innerFrac, p.y);
                DemTerrainWorld.StitchAllSeams();
                TreeLayer.ConformToSurface(Surf); RockLayer.ConformToSurface(Surf);
                FenceLayer.Rebuild(Surf); PowerLineLayer.Rebuild(Surf);
                RoadPlanLayer.SetEdgeExcavated(edgeIndex, true);
                RoadPlanLayer.Rebuild(Surf); RailLayer.Rebuild(Surf); PlanLayer.Rebuild(Surf);
            }
            else { Debug.LogWarning("[Road] Excavation needs the chunk or DEM world."); return; }
            _dirtySince = Time.realtimeSinceStartup;
            Debug.Log($"[Road] Excavated segment {edgeIndex}.");
        }

        GameObject _roadBuildRoot;   // runtime 3D road meshes from the last build (regenerated, not saved)
        // "Built" is a per-segment flag on the edge (RoadPlanLayer.IsEdgeBuilt), NOT an index set — so it travels
        // with the edge through the index renumbering that drawing/splitting causes (a built road stays fully built
        // when you add a crossing). It IS serialized (save v18+); building marks the world dirty so the autosave
        // captures it, and the world load re-sweeps from it — so 3D roads persist across quit/restart.

        // Build Plan (phase 1): convert the road plan to a Network, resolve it with the GeometryResolver brain
        // (setbacks / intersections / lane flow), then sweep each road BODY — setback-trimmed, draped at the
        // node-to-node grade line — into the excavated bed. Marks EVERY edge built, then re-sweeps.
        public void BuildRoadPlan()
        {
            LineGraph graph = RoadPlanLayer.Graph;
            if (graph == null || graph.Edges.Count == 0) { Debug.LogWarning("[Road] No road plan to build — draw a corridor first (;)."); return; }
            for (int e = 0; e < graph.Edges.Count; e++) RoadPlanLayer.SetEdgeBuilt(e, true);
            RebuildBuiltRoads();
            _dirtySince = Time.realtimeSinceStartup;   // persist the Built flags via autosave
        }

        // Build a SINGLE plan segment. Flags it built and re-sweeps; the whole network still resolves, so this
        // segment's junctions set back correctly.
        public void BuildRoadSegment(int edgeIndex)
        {
            LineGraph graph = RoadPlanLayer.Graph;
            if (graph == null || edgeIndex < 0 || edgeIndex >= graph.Edges.Count) return;
            RoadPlanLayer.SetEdgeBuilt(edgeIndex, true);
            RebuildBuiltRoads();
            _dirtySince = Time.realtimeSinceStartup;   // persist the Built flag via autosave
            Debug.Log($"[Road] Built segment {edgeIndex}.");
        }

        // (Re)sweep every edge flagged Built into a fresh build root. The whole network resolves for
        // correct setbacks; only the built edges emit geometry (RoadPlanBuilder's onlyRoads filter).
        void RebuildBuiltRoads()
        {
            LineGraph graph = RoadPlanLayer.Graph;
            if (graph != null && RoadPlanLayer.RemoveDegenerateEdges() > 0) RoadPlanLayer.Rebuild(Surf);   // drop 0-length stubs before resolving
            ClearRoadBuild();
            if (graph == null) { RoadPlanLayer.ClearResolvedSetbacks(); return; }
            // Built edges come from the per-segment flag (survives index renumbering on draw/split).
            var only = new System.Collections.Generic.HashSet<string>();
            var bridgeEdges = new System.Collections.Generic.HashSet<int>();
            for (int e = 0; e < graph.Edges.Count; e++)
                if (graph.Edges[e].Built) { only.Add("r" + e); if (graph.Edges[e].Bridge) bridgeEdges.Add(e); }
            if (only.Count == 0) { RoadPlanLayer.ClearResolvedSetbacks(); return; }
            // Per-node DESIGN elevation (vertex "v{i}" ↔ node i) — the same heights Excavate cut to, captured
            // from the shaped surface — so the swept road sits in its cut and respects carved/flattened terrain.
            var nodeElev = new System.Collections.Generic.Dictionary<string, float>(graph.Nodes.Count);
            for (int i = 0; i < graph.Nodes.Count; i++) nodeElev["v" + i] = RoadPlanLayer.DesignElevation(i, Surf);
            NetworkDesigner.Model.Network net = NetworkDesigner.Roads.RoadNetworkBridge.Build(
                graph, NetworkDesigner.Model.DriveSide.Right, RoadPlanLayer.RoadWidth);
            _roadBuildRoot = NetworkDesigner.Roads.RoadPlanBuilder.Build(
                net, vid => nodeElev.TryGetValue(vid, out float y) ? y : 0f, RoadPlanLayer.ExcavationDepth, null, only,
                xz => Surf != null ? Surf.SampleHeight(xz.x, xz.y) : 0f, RoadPlanLayer.FollowTerrain);   // terrain-follow blend

            // Sync the setback HANDLES to the resolver's ACTUAL setbacks (acute/secondary boosts push them far past
            // the flat default), so the orange rings sit where the road really sets back instead of at a fixed 10 m.
            var resolvedVg = NetworkDesigner.Geometry.GeometryResolver.ResolveNetwork(net);
            var sbMap = new System.Collections.Generic.Dictionary<int, float>();
            foreach (var vg in resolvedVg)
            {
                if (vg?.Approaches == null) continue;
                foreach (var ap in vg.Approaches)
                {
                    if (ap == null || string.IsNullOrEmpty(ap.RoadId) || ap.RoadId.Length < 2 || ap.RoadId[0] != 'r') continue;
                    if (!int.TryParse(ap.RoadId.Substring(1), out int e)) continue;
                    bool endA = ap.End == NetworkDesigner.Model.RoadEnd.A;
                    sbMap[e * 2 + (endA ? 0 : 1)] = ap.Setback;
                }
            }
            RoadPlanLayer.SetResolvedSetbacks(sbMap);

            if (bridgeEdges.Count > 0 && _roadBuildRoot != null)
                RoadBridgeBuilder.Build(RoadPlanLayer, bridgeEdges,
                    i => nodeElev.TryGetValue("v" + i, out float y) ? y : 0f, Surf,
                    RoadPlanLayer.BridgeDeckDepth, RoadPlanLayer.BridgePierSpacing, RoadPlanLayer.BridgePierWidth,
                    RoadPlanLayer.BridgeParapets, RoadPlanLayer.BridgeParapetHeight,
                    _roadBuildRoot.transform);
        }

        // Re-sweep the currently-built roads (and their bridges) — e.g. after changing a bridge tunable.
        public void RefreshBuiltRoads() => RebuildBuiltRoads();

        public void ClearRoadBuild()
        {
            if (_roadBuildRoot != null) DestroySafe(_roadBuildRoot);
            _roadBuildRoot = null;
        }

        // "Remove roads" / world switch: drop the built meshes AND forget which segments were built.
        public void ClearBuiltRoads() { RoadPlanLayer.ClearAllBuilt(); ClearRoadBuild(); }

        // Tear down ALL live network geometry (rail / rail-plan / road-plan / fence / power / built roads)
        // WITHOUT marking the save dirty. Used on world switch + return-to-menu so one world's networks don't
        // ride onto the next (or float over the launcher). Not marking dirty is essential: otherwise an autosave
        // would fire after the clear and overwrite the just-exited world's saved networks with the empty state.
        public void ClearAllNetworks()
        {
            RailLayer.ClearAll(Surf);
            PlanLayer.ClearAll(Surf);
            RoadPlanLayer.ClearAll(Surf);
            FenceLayer.ClearAll(Surf);
            PowerLineLayer.ClearAll(Surf);
            RetainingWallLayer.ClearAll(Surf);
            ClearBuiltRoads();
        }

        // Launcher/menu state: clear leftover networks and hide every terrain backend so the startup picker
        // sits over empty space — no low-poly block, no floating networks from the world you just left.
        public void EnterMenuState()
        {
            ClearAllNetworks();
            if (_chunkRoot != null) _chunkRoot.SetActive(false);
            DemTerrainWorld.SetVisible(false);
            if (_waterGo != null) _waterGo.SetActive(false);
        }

        // ---- road plan elevation-edit sub-mode (palette "Edit elevations") ----

        public bool RoadElevationEdit => RoadPlanLayer.ElevationEditMode;
        public void SetRoadElevationEdit(bool on)
        {
            if (on) { EnterRoadPlanMode(); RoadPlanLayer.SetExcavateSelectMode(false); RoadPlanLayer.SetBuildSegmentMode(false); RoadPlanLayer.SetBridgeSelectMode(false); RoadPlanLayer.SetSetbackEditMode(false); RoadPlanLayer.SetClassEditMode(false); }
            RoadPlanLayer.SetElevationEditMode(on);
            RoadPlanLayer.Rebuild(Surf);
        }

        // ---- road plan setback-edit sub-mode (drag an orange ring per junction approach to set its setback) ----
        public bool RoadSetbackEdit => RoadPlanLayer.SetbackEditMode;
        public void SetRoadSetbackEdit(bool on)
        {
            if (on) { EnterRoadPlanMode(); RoadPlanLayer.SetElevationEditMode(false); RoadPlanLayer.SetClassEditMode(false); }
            RoadPlanLayer.SetSetbackEditMode(on);
            RoadPlanLayer.Rebuild(Surf);
        }

        // Drag an orange setback handle along its road's outward axis to set that end's setback; right-click resets to
        // auto. The 3D roads re-sweep on release (cheap during the drag — only the handle/overlay updates live).
        void HandleRoadSetbackInput(RoadPlanLayer rd, RaycastHit hit, bool overTerrain)
        {
            if (rd.IsDraggingSetback)
            {
                if (Input.GetMouseButton(0))
                {
                    if (overTerrain && rd.UpdateSetbackDrag(new Vector2(hit.point.x, hit.point.z)))
                    { rd.Rebuild(Surf); _dirtySince = Time.realtimeSinceStartup; }
                }
                else { rd.EndSetbackDrag(); rd.Rebuild(Surf); RebuildBuiltRoads(); }   // re-sweep the 3D roads on release
                return;
            }
            if (MouseOverActivePanel() || !overTerrain) return;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            Vector2 sp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            if (Input.GetMouseButtonDown(0))
            {
                if (rd.PickSetbackHandle(cam, Surf, sp, 30f, out int e, out bool ea)) rd.BeginSetbackDrag(e, ea);
            }
            else if (Input.GetMouseButtonDown(1))
            {
                if (rd.PickSetbackHandle(cam, Surf, sp, 30f, out int e, out bool ea))
                { rd.ResetSetback(e, ea); rd.Rebuild(Surf); RebuildBuiltRoads(); _dirtySince = Time.realtimeSinceStartup; }
            }
        }

        // ---- road plan class-edit sub-mode (recolour by primary/secondary; click an edge to cycle its class) ----
        public bool RoadClassEdit => RoadPlanLayer.ClassEditMode;
        public void SetRoadClassEdit(bool on)
        {
            if (on)
            {
                EnterRoadPlanMode();
                RoadPlanLayer.SetElevationEditMode(false); RoadPlanLayer.SetSetbackEditMode(false);
                RoadPlanLayer.SetExcavateSelectMode(false); RoadPlanLayer.SetBuildSegmentMode(false); RoadPlanLayer.SetBridgeSelectMode(false);
            }
            RoadPlanLayer.SetClassEditMode(on);
            RoadPlanLayer.Rebuild(Surf);
        }

        // Left-click an edge's corridor → cycle Auto → Primary → Secondary → Auto; the overlay recolours, then the 3D
        // re-sweeps (class will drive intersection setback in phase 2). Right-click resets the clicked edge to Auto.
        void HandleRoadClassInput(RoadPlanLayer rd, RaycastHit hit, bool overTerrain)
        {
            if (MouseOverActivePanel() || !overTerrain) return;
            Vector2 xz = new Vector2(hit.point.x, hit.point.z);
            if (Input.GetMouseButtonDown(0))
            {
                int e = rd.PickEdgeInCorridor(xz);
                if (e >= 0) { rd.CycleEdgeClass(e); rd.Rebuild(Surf); RebuildBuiltRoads(); _dirtySince = Time.realtimeSinceStartup; }
            }
            else if (Input.GetMouseButtonDown(1))
            {
                int e = rd.PickEdgeInCorridor(xz);
                if (e >= 0) { rd.SetEdgeClass(e, RoadClass.Auto); rd.Rebuild(Surf); RebuildBuiltRoads(); _dirtySince = Time.realtimeSinceStartup; }
            }
        }

        // ---- road plan path-excavation sub-mode (palette "Excavate Mode") ----

        public bool RoadExcavateMode => RoadPlanLayer.ExcavateSelectMode;
        public void SetRoadExcavateMode(bool on)
        {
            if (on) { EnterRoadPlanMode(); RoadPlanLayer.SetElevationEditMode(false); RoadPlanLayer.SetBuildSegmentMode(false); RoadPlanLayer.SetBridgeSelectMode(false); }
            RoadPlanLayer.SetExcavateSelectMode(on);
            RoadPlanLayer.Rebuild(Surf);
        }

        // ---- road plan per-segment build sub-mode (palette "Build Mode") ----

        public bool RoadBuildSegmentMode => RoadPlanLayer.BuildSegmentMode;
        public void SetRoadBuildSegmentMode(bool on)
        {
            if (on) { EnterRoadPlanMode(); RoadPlanLayer.SetElevationEditMode(false); RoadPlanLayer.SetExcavateSelectMode(false); RoadPlanLayer.SetBridgeSelectMode(false); }
            RoadPlanLayer.SetBuildSegmentMode(on);
            RoadPlanLayer.Rebuild(Surf);
        }

        // ---- road plan bridge sub-mode (palette "Bridge Mode") ----

        public bool RoadBridgeMode => RoadPlanLayer.BridgeSelectMode;
        public void SetRoadBridgeMode(bool on)
        {
            if (on) { EnterRoadPlanMode(); RoadPlanLayer.SetElevationEditMode(false); RoadPlanLayer.SetExcavateSelectMode(false); RoadPlanLayer.SetBuildSegmentMode(false); }
            RoadPlanLayer.SetBridgeSelectMode(on);
            RoadPlanLayer.Rebuild(Surf);
        }

        // Screen-space node pick under the cursor (robust to camera angle / zoom — fixes the "click a few times"
        // friction of world-radius picking). Shared by the Excavate-path and Build-path sub-modes.
        int PickRoadNode(RoadPlanLayer rd)
        {
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            Vector2 sp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            return rd.PickNodeScreen(cam, Surf, sp, 30f);
        }

        // Build Mode (mirrors Excavate Mode): click a START node (amber ring), then an END node → build every
        // EXCAVATED segment on the path between them. Un-excavated segments on the path are skipped (with a note).
        void HandleRoadBuildInput(RoadPlanLayer rd, RaycastHit hit, bool overTerrain)
        {
            if (MouseOverActivePanel() || !overTerrain) return;
            if (!Input.GetMouseButtonDown(0)) return;
            int n = PickRoadNode(rd);
            if (n < 0) return;
            if (rd.BuildStartNode < 0)
            {
                rd.BuildStartNode = n;                              // arm the start node
                rd.Rebuild(Surf);
                Debug.Log($"[Road] Build start = node {n}. Click an end node.");
                return;
            }
            if (n == rd.BuildStartNode)                             // click the start again → cancel
            { rd.BuildStartNode = -1; rd.Rebuild(Surf); return; }
            var edges = rd.EdgePathBetween(rd.BuildStartNode, n);
            rd.BuildStartNode = -1;
            if (edges == null || edges.Count == 0)
            { Debug.LogWarning("[Road] No connected path between those two nodes."); rd.Rebuild(Surf); return; }
            BuildRoadPath(edges);
        }

        // Build every EXCAVATED segment in a path (skip un-excavated, with a note); one re-sweep at the end.
        public void BuildRoadPath(System.Collections.Generic.List<int> edgeIndices)
        {
            if (edgeIndices == null || edgeIndices.Count == 0) return;
            int added = 0, skipped = 0;
            foreach (int e in edgeIndices)
            {
                if (RoadPlanLayer.IsEdgeExcavated(e) || RoadPlanLayer.IsEdgeBridge(e)) { RoadPlanLayer.SetEdgeBuilt(e, true); added++; }
                else skipped++;
            }
            if (added == 0) { Debug.LogWarning("[Road] None of those segments are excavated or bridges — Excavate the path first (or mark it a Bridge)."); return; }
            RebuildBuiltRoads();
            _dirtySince = Time.realtimeSinceStartup;   // persist the Built flags via autosave
            Debug.Log($"[Road] Built {added} segment(s)" + (skipped > 0 ? $", skipped {skipped} un-built." : "."));
        }

        // Right-click while in an Excavate/Build/Bridge sub-mode: first back out of an armed start node; otherwise
        // delete the node under the cursor. Lets you prune/redraw the plan (even after excavating) without first
        // toggling the sub-mode off — the friction the sub-mode's early-return used to impose on right-click.
        void HandleRoadSubModeRightClick(RoadPlanLayer rd, RaycastHit hit, bool overTerrain)
        {
            if (MouseOverActivePanel()) return;
            if (rd.ExcavateStartNode >= 0 || rd.BuildStartNode >= 0 || rd.BridgeStartNode >= 0)
            { rd.ExcavateStartNode = -1; rd.BuildStartNode = -1; rd.BridgeStartNode = -1; rd.Rebuild(Surf); return; }
            if (overTerrain && rd.DeleteNearNode(Surf, hit.point, 3f)) _dirtySince = Time.realtimeSinceStartup;
        }

        // Bridge Mode (mirrors Excavate Mode): click a START node, then an END node → toggle every segment on the
        // path between them as a BRIDGE span. Marking forces both ends of each span LEVEL (flat deck) and clears any
        // excavated flag; the span is then build-eligible without cutting, and Build raises a deck+piers over the gap.
        void HandleRoadBridgeInput(RoadPlanLayer rd, RaycastHit hit, bool overTerrain)
        {
            if (MouseOverActivePanel() || !overTerrain) return;
            if (!Input.GetMouseButtonDown(0)) return;
            int n = PickRoadNode(rd);
            if (n < 0) return;
            if (rd.BridgeStartNode < 0)
            {
                rd.BridgeStartNode = n;                             // arm the start node
                rd.Rebuild(Surf);
                Debug.Log($"[Road] Bridge start = node {n}. Click an end node.");
                return;
            }
            if (n == rd.BridgeStartNode)                            // click the start again → cancel
            { rd.BridgeStartNode = -1; rd.Rebuild(Surf); return; }
            var edges = rd.EdgePathBetween(rd.BridgeStartNode, n);
            rd.BridgeStartNode = -1;
            if (edges == null || edges.Count == 0)
            { Debug.LogWarning("[Road] No connected path between those two nodes."); rd.Rebuild(Surf); return; }
            MarkRoadBridgePath(edges);
        }

        // Toggle a set of plan edges as bridge spans. Toggle-ON levels each span's two end nodes to the HIGHER of
        // their design elevations (a flat deck that clears both abutments) and clears Excavated; toggle-OFF just
        // un-flags (leaves the leveled elevation — re-edit via Edit elevations if you want the original grade back).
        public void MarkRoadBridgePath(System.Collections.Generic.List<int> edgeIndices)
        {
            if (edgeIndices == null || edgeIndices.Count == 0) return;
            LineGraph g = RoadPlanLayer.Graph;
            if (g == null) return;
            bool allBridge = true;
            foreach (int ei in edgeIndices) if (!RoadPlanLayer.IsEdgeBridge(ei)) { allBridge = false; break; }
            bool makeBridge = !allBridge;   // if the whole path is already bridge, this click un-bridges it
            int n = 0;
            foreach (int ei in edgeIndices)
            {
                if (ei < 0 || ei >= g.Edges.Count) continue;
                if (makeBridge)
                {
                    LineEdge e = g.Edges[ei];
                    float yA = RoadPlanLayer.DesignElevation(e.A, Surf);
                    float yB = RoadPlanLayer.DesignElevation(e.B, Surf);
                    float lvl = Mathf.Max(yA, yB);                 // flat deck at the higher abutment
                    g.SetNodeY(e.A, lvl); g.SetNodeY(e.B, lvl);    // shared nodes propagate to adjoining segments
                    RoadPlanLayer.SetEdgeBridge(ei, true);
                    RoadPlanLayer.SetEdgeExcavated(ei, false);     // a bridge spans — it is not cut into the terrain
                }
                else RoadPlanLayer.SetEdgeBridge(ei, false);
                n++;
            }
            RoadPlanLayer.Rebuild(Surf);
            _dirtySince = Time.realtimeSinceStartup;
            Debug.Log(makeBridge ? $"[Road] Marked {n} segment(s) as bridge (ends leveled)." : $"[Road] Cleared bridge on {n} segment(s).");
        }

        // ---- selection-driven actions (the palette's Excavate! / Build! / Force Bridge buttons) ----

        public int RoadSelectionCount => RoadPlanLayer.SelectedEdgeCount;
        public void ClearRoadSelection() { RoadPlanLayer.ClearEdgeSelection(); RoadPlanLayer.Rebuild(Surf); }

        // Per-road junction SETBACK override for the SELECTED segments (applied to BOTH ends): how far the road pulls
        // back from its junction. <0 = auto (resolver-computed). Re-sweeps so built intersections update live.
        public float SelectedRoadSetback()
        {
            var sel = RoadPlanLayer.SelectedEdgesList();
            if (sel.Count == 0 || RoadPlanLayer.Graph == null) return 0f;
            return Mathf.Max(0f, RoadPlanLayer.Graph.Edges[sel[0]].SetbackA);   // auto (<0) reads as 0
        }
        public void SetSelectedRoadSetback(float meters)
        {
            var sel = RoadPlanLayer.SelectedEdgesList();
            if (sel.Count == 0) return;
            foreach (int e in sel) { RoadPlanLayer.Graph.Edges[e].SetbackA = meters; RoadPlanLayer.Graph.Edges[e].SetbackB = meters; }
            RoadPlanLayer.Rebuild(Surf); RebuildBuiltRoads(); _dirtySince = Time.realtimeSinceStartup;
        }
        public void ClearSelectedRoadSetback()   // back to auto
        {
            var sel = RoadPlanLayer.SelectedEdgesList();
            if (sel.Count == 0) return;
            foreach (int e in sel) { RoadPlanLayer.Graph.Edges[e].SetbackA = -1f; RoadPlanLayer.Graph.Edges[e].SetbackB = -1f; }
            RoadPlanLayer.Rebuild(Surf); RebuildBuiltRoads(); _dirtySince = Time.realtimeSinceStartup;
        }

        // Delete the SELECTED plan segments (and any 3D road built on them).
        public void DeleteSelectedRoadSegments()
        {
            var sel = RoadPlanLayer.SelectedEdgesList();   // ascending
            if (sel.Count == 0) { Debug.LogWarning("[Road] No segments selected to delete — Cmd/Ctrl-click some first."); return; }
            int n = DeleteRoadEdges(sel);
            Debug.Log($"[Road] Deleted {n} segment(s).");
        }

        // Remove a set of plan edges (and the built road on them). Removes high-index-first so the lower indices stay
        // valid, remapping the built-edge set as it goes, then drops now-edgeless nodes and re-sweeps. Returns count.
        int DeleteRoadEdges(System.Collections.Generic.List<int> indices)
        {
            if (indices == null || indices.Count == 0) return 0;
            indices.Sort();
            int removed = 0;
            for (int j = indices.Count - 1; j >= 0; j--)   // high-index-first so lower indices stay valid
                if (RoadPlanLayer.Graph.RemoveEdgeAt(indices[j])) removed++;
            // No built-set remap needed: "built" is a per-edge flag that's removed with the edge; survivors keep theirs.
            RoadPlanLayer.ClearEdgeSelection();
            RoadPlanLayer.DropOrphanNodes();
            RoadPlanLayer.Rebuild(Surf);
            RebuildBuiltRoads();
            _dirtySince = Time.realtimeSinceStartup;
            return removed;
        }

        // Delete a node and its segments (+ built road). The node becomes edgeless and is pruned by DropOrphanNodes;
        // a lone node with no edges is removed directly.
        public void DeleteRoadNode(int node)
        {
            if (node < 0 || RoadPlanLayer.Graph == null || node >= RoadPlanLayer.Graph.Nodes.Count) return;
            // A degree-2 pass-through whose two segments run straight through → drop the node but JOIN the segments
            // into one (instead of deleting both), so a collinear point can be removed without breaking the road.
            if (RoadPlanLayer.Graph.TryJoinColinear(node, 8f))
            {
                RoadPlanLayer.ClearEdgeSelection();
                RoadPlanLayer.Rebuild(Surf);
                RebuildBuiltRoads();
                _dirtySince = Time.realtimeSinceStartup;
                return;
            }
            var edges = RoadPlanLayer.EdgesTouchingNode(node);
            if (edges.Count > 0) { DeleteRoadEdges(edges); }
            else
            {
                RoadPlanLayer.Graph.RemoveNode(node);
                RoadPlanLayer.Rebuild(Surf);
                _dirtySince = Time.realtimeSinceStartup;
            }
            Debug.Log($"[Road] Deleted node {node} and its segments.");
        }

        // Excavate! → cut every SELECTED segment that isn't already excavated and isn't a bridge (bridges span the gap).
        public void ExcavateSelectedRoads()
        {
            var sel = RoadPlanLayer.SelectedEdgesList();
            if (sel.Count == 0) { Debug.LogWarning("[Road] No segments selected — Cmd/Ctrl-click inside segments first."); return; }
            var todo = new System.Collections.Generic.List<int>();
            foreach (int ei in sel) if (!RoadPlanLayer.IsEdgeExcavated(ei) && !RoadPlanLayer.IsEdgeBridge(ei)) todo.Add(ei);
            if (todo.Count == 0) { Debug.Log("[Road] Selected segments are already excavated (or bridges)."); return; }
            ExcavateRoadPath(todo);   // cuts beds, marks them Excavated (→ yellow), rebuilds + conforms
        }

        // Build! → sweep the 3D road on every SELECTED segment that's excavated (yellow) or a bridge; skip the rest.
        public void BuildSelectedRoads()
        {
            var sel = RoadPlanLayer.SelectedEdgesList();
            if (sel.Count == 0) { Debug.LogWarning("[Road] No segments selected — Cmd/Ctrl-click inside segments first."); return; }
            BuildRoadPath(sel);   // filters to excavated || bridge, warns on the rest
        }

        // Force Bridge → flag the SELECTED segments as a bridge span (or un-bridge them if all already are).
        public void ForceBridgeSelectedRoads()
        {
            var sel = RoadPlanLayer.SelectedEdgesList();
            if (sel.Count == 0) { Debug.LogWarning("[Road] No segments selected — Cmd/Ctrl-click inside segments first."); return; }
            MarkRoadBridgePath(sel);
        }

        // Click a START node (armed → green ring), then an END node → excavate every plan segment on the
        // shortest path between them (cut high ground, fill low), mark them Excavated, rebuild once.
        void HandleRoadExcavateInput(RoadPlanLayer rd, RaycastHit hit, bool overTerrain)
        {
            if (MouseOverActivePanel() || !overTerrain) return;
            if (!Input.GetMouseButtonDown(0)) return;
            Vector2 xz = new Vector2(hit.point.x, hit.point.z);
            int n = rd.PickNode(xz);
            if (n < 0) return;
            if (rd.ExcavateStartNode < 0)
            {
                rd.ExcavateStartNode = n;                          // arm the start node
                rd.Rebuild(Surf);
                Debug.Log($"[Road] Excavate start = node {n}. Click an end node.");
                return;
            }
            if (n == rd.ExcavateStartNode)                          // click the start again → cancel the arming
            { rd.ExcavateStartNode = -1; rd.Rebuild(Surf); return; }
            var edges = rd.EdgePathBetween(rd.ExcavateStartNode, n);
            rd.ExcavateStartNode = -1;
            if (edges == null || edges.Count == 0)
            { Debug.LogWarning("[Road] No connected path between those two nodes."); rd.Rebuild(Surf); return; }
            ExcavateRoadPath(edges);
        }

        // Excavate a set of plan edges (cut+fill their beds) in one pass, then a single rebuild + conform.
        public void ExcavateRoadPath(System.Collections.Generic.List<int> edgeIndices)
        {
            if (edgeIndices == null || edgeIndices.Count == 0) return;
            float feather = Mathf.Max(0.1f, RoadPlanLayer.CutFeather);
            int done = 0;
            if (ChunkWorld.Active)
            {
                float batter = Mathf.Max(0.25f, RoadPlanLayer.CutBatter);
                foreach (int ei in edgeIndices)
                {
                    if (RoadPlanLayer.IsEdgeBridge(ei)) continue;   // bridge spans the gap — never cut a bed
                    if (!RoadPlanLayer.EdgeExcavationBed(Surf, ei, out var pts, out float flatHalf)) continue;
                    ChunkWorld.GradeBatter(pts, flatHalf, batter, Mathf.Max(0.25f, RoadPlanLayer.FillBatter), RoadPlanLayer.FillReach);   // daylighting cut/fill bench
                    RoadPlanLayer.SetEdgeExcavated(ei, true); done++;
                }
                RoadPlanLayer.Rebuild(Surf); RailLayer.Rebuild(Surf); PlanLayer.Rebuild(Surf);
                ConformScatterAndLines();
            }
            else if (DemTerrainWorld.HasWorld)
            {
                foreach (int ei in edgeIndices)
                {
                    if (RoadPlanLayer.IsEdgeBridge(ei)) continue;   // bridge spans the gap — never cut a bed
                    if (!RoadPlanLayer.EdgeExcavationBed(Surf, ei, out var pts, out float flatHalf)) continue;
                    float r = flatHalf + feather; float innerFrac = flatHalf / r;
                    foreach (Vector3 p in pts) DemTerrainWorld.FlattenStamp(p, r, innerFrac, p.y);
                    RoadPlanLayer.SetEdgeExcavated(ei, true); done++;
                }
                DemTerrainWorld.StitchAllSeams();
                TreeLayer.ConformToSurface(Surf); RockLayer.ConformToSurface(Surf);
                FenceLayer.Rebuild(Surf); PowerLineLayer.Rebuild(Surf);
                RoadPlanLayer.Rebuild(Surf); RailLayer.Rebuild(Surf); PlanLayer.Rebuild(Surf);
            }
            else { Debug.LogWarning("[Road] Excavation needs the chunk or DEM world."); return; }
            _dirtySince = Time.realtimeSinceStartup;
            Debug.Log($"[Road] Excavated path: {done} segment(s).");
        }

        // Drive the elevation-edit interactions from the per-frame mouse state.
        void HandleRoadElevationInput(RoadPlanLayer rd, RaycastHit hit, bool overTerrain)
        {
            Vector2 mouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            if (rd.IsDraggingElevation)
            {
                if (Input.GetMouseButton(0))
                {
                    if (VerticalAxisY(rd.DragNodeXZ, out float y) && rd.UpdateElevationDrag(y, mouse))
                    { rd.Rebuild(Surf); _dirtySince = Time.realtimeSinceStartup; }
                }
                else { rd.EndElevationDrag(); rd.Rebuild(Surf); }
                return;
            }
            if (MouseOverActivePanel() || !overTerrain) return;
            Vector2 xz = new Vector2(hit.point.x, hit.point.z);
            if (Input.GetMouseButtonDown(0))
            {
                int n = rd.PickNode(xz);
                if (n >= 0) { VerticalAxisY(rd.Graph.Nodes[n], out float y0); rd.BeginElevationDrag(Surf, n, y0, mouse); }
            }
            else if (Input.GetMouseButtonDown(1))
            {
                int n = rd.PickNode(xz);
                if (n >= 0) { rd.LevelSelectedTo(Surf, n); rd.Rebuild(Surf); _dirtySince = Time.realtimeSinceStartup; }
            }
        }

        // World Y on the vertical axis through `nodeXZ` that's closest to the camera ray under the cursor —
        // i.e. "what height is the cursor pointing at, at this node's XZ". Used for 1:1 elevation dragging.
        bool VerticalAxisY(Vector2 nodeXZ, out float y)
        {
            y = 0f;
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return false;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            Vector3 d = ray.direction.normalized;
            Vector3 r = ray.origin - new Vector3(nodeXZ.x, 0f, nodeXZ.y);   // axis base at Y=0 under the node
            float b = d.y;                       // dot(up, d)
            float denom = 1f - b * b;
            if (denom < 1e-5f) return false;     // ray ~parallel to vertical → no stable solution
            float s = (b * Vector3.Dot(d, r) - r.y) / denom;   // param along the vertical axis → its world Y
            y = s;
            return true;
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
        void SetScatterMode(ScatterLayer s) { _active = _active == s ? null : s; _lineActive = null; _railConnectNodeA = -1; _roadConnectNodeA = -1; HideLinePreviews(); }
        void SetLineMode(ITerrainLineLayer l) { _lineActive = _lineActive == l ? null : l; _active = null; _railConnectNodeA = -1; _roadConnectNodeA = -1; HideLinePreviews(); }

        // --- Rail palette hooks (the UI Toolkit RailPalette drives these) ---
        public bool IsRailBuildMode => ReferenceEquals(_lineActive, RailLayer);
        public bool IsRailPlanMode => ReferenceEquals(_lineActive, PlanLayer);
        public bool IsRailMode => IsRailBuildMode || IsRailPlanMode;
        public bool IsRoadPlanMode => ReferenceEquals(_lineActive, RoadPlanLayer);
        // Road plan has two sub-modes (both keep the road layer active): PLAN draws/edits + right-click deletes plan
        // nodes; BUILD manages built roads + right-click un-builds (or deletes an un-built node). ' toggles BUILD.
        [System.NonSerialized] bool _roadBuildMode;
        public bool IsRoadBuildMode => IsRoadPlanMode && _roadBuildMode;
        public void EnterRoadPlanMode() { if (!IsRoadPlanMode) SetLineMode(RoadPlanLayer); _roadBuildMode = false; }
        public void EnterRoadBuildMode() { if (!IsRoadPlanMode) SetLineMode(RoadPlanLayer); _roadBuildMode = true; }
        // Default terrain (sculpt) mode: no line layer and no scatter layer active.
        public bool IsSculptMode => _lineActive == null && _active == null;
        // Exit any line/scatter mode back to the terrain brush (used when a palette that
        // implies sculpt — Terrain/System — is opened, so the cursor follows the palette).
        public void EnterSculptMode()
        { _lineActive = null; _active = null; _railConnectNodeA = -1; _roadConnectNodeA = -1; HideLinePreviews(); }

        // Pick a sculpt brush: drop out of any line/scatter tool first, then set the brush.
        public void SetBrush(BrushMode m) { EnterSculptMode(); Brush = m; }

        // --- Scatter/Fence palette hooks ---
        public bool IsTreeMode  => ReferenceEquals(_active, TreeLayer);
        public bool IsRockMode  => ReferenceEquals(_active, RockLayer);
        public bool IsFenceMode => ReferenceEquals(_lineActive, FenceLayer);
        public void EnterTreeMode()  { if (!IsTreeMode)  SetScatterMode(TreeLayer); }
        public void EnterRockMode()  { if (!IsRockMode)  SetScatterMode(RockLayer); }
        public void EnterFenceMode() { if (!IsFenceMode) SetLineMode(FenceLayer); }
        public bool IsRetainingWallMode => ReferenceEquals(_lineActive, RetainingWallLayer);
        public void EnterRetainingWallMode() { if (!IsRetainingWallMode) SetLineMode(RetainingWallLayer); }
        // T/R/F enter scatter/fence + open the Scatter/Fence palette exclusively; toggling
        // back out (key again) closes it to no palette.
        void SyncScatterPalette()
            => NetworkDesigner.UI.PaletteBase.SetExclusive(
                   IsTreeMode || IsRockMode || IsFenceMode ? "ScatterFence" : null);
        // Grid overlay toggle (the G key + footer "Grid" button). In the chunk/DEM world it drives the
        // chunk 1km/100m grid; on the low-poly backend it toggles the terrain-material grid.
        public void ToggleGrid()
        {
            if (ChunkWorld.Active) ChunkShowGrid = !ChunkShowGrid;
            else { GridEnabled = !GridEnabled; ApplyTerrainMaterial(); }
            SaveViewPrefs();
        }
        // Current grid on/off, for the footer button's active styling (whichever grid is in play).
        public bool GridOn => ChunkWorld.Active ? ChunkShowGrid : GridEnabled;

        // Snap + topo + minimap toggles routed through here so they persist (footer buttons / hotkeys call these).
        public void ToggleSnap() { SnapToGrid = !SnapToGrid; SaveViewPrefs(); }
        public void ToggleTopo() { ChunkContours = !ChunkContours; SaveViewPrefs(); }
        public void ToggleRidges() { ChunkRidges = !ChunkRidges; SaveViewPrefs(); }
        public void ToggleMinimap() { _showMinimap = !_showMinimap; SaveViewPrefs(); }

        // The grid / snap / topo / ridge / minimap view toggles persist across worlds in PlayerPrefs.
        const string ViewSnapKey = "ViewSnap", ViewGridKey = "ViewGrid", ViewTopoKey = "ViewTopo", ViewRidgeKey = "ViewRidge", ViewMiniKey = "ViewMinimap";
        void SaveViewPrefs()
        {
            PlayerPrefs.SetInt(ViewSnapKey, SnapToGrid ? 1 : 0);
            PlayerPrefs.SetInt(ViewGridKey, GridOn ? 1 : 0);
            PlayerPrefs.SetInt(ViewTopoKey, ChunkContours ? 1 : 0);
            PlayerPrefs.SetInt(ViewRidgeKey, ChunkRidges ? 1 : 0);
            PlayerPrefs.SetInt(ViewMiniKey, _showMinimap ? 1 : 0);
            PlayerPrefs.Save();
        }
        public void ApplyViewPrefs()
        {
            SnapToGrid = PlayerPrefs.GetInt(ViewSnapKey, 0) == 1;
            bool grid = PlayerPrefs.GetInt(ViewGridKey, 0) == 1;
            if (ChunkWorld.Active) ChunkShowGrid = grid; else { GridEnabled = grid; ApplyTerrainMaterial(); }
            ChunkContours = PlayerPrefs.GetInt(ViewTopoKey, 0) == 1;
            ChunkRidges = PlayerPrefs.GetInt(ViewRidgeKey, 0) == 1;
            _showMinimap = PlayerPrefs.GetInt(ViewMiniKey, 1) == 1;   // default on
        }

        // Entering rail mode (L/K) opens the Rail palette exclusively; toggling back out of
        // rail (L/K again) closes it to NO palette (a clean toggle), not the Terrain palette.
        void SyncPaletteToMode()
            => NetworkDesigner.UI.PaletteBase.SetExclusive(IsRailMode ? "Rail" : null);
        // Switch to build (plan=false) or plan (plan=true). Radio-style: clicking the
        // mode you're already in is a no-op (use the L/K hotkeys to toggle back out).
        public void SetRailMode(bool plan)
        {
            ITerrainLineLayer target = plan ? PlanLayer : (ITerrainLineLayer)RailLayer;
            if (!ReferenceEquals(_lineActive, target)) SetLineMode(target);
        }

        // Common-footer labels for the palette. "Terrain" is the sculpt mode; sub-mode is
        // the active brush/layer with its hotkey (e.g. "Slope (5)").
        public string PaletteModeLabel
        {
            get
            {
                if (IsRailMode) return "Rail";
                if (_active != null) return "Scatter";
                if (_lineActive != null) return _lineActive.LayerName;
                return "Terrain";
            }
        }
        public string PaletteSubModeLabel
        {
            get
            {
                if (_lineActive is RailTrackLayer) return "Build (L)";
                if (_lineActive is RailPlanLayer) return "Plan (K)";
                if (ReferenceEquals(_active, TreeLayer)) return "Trees (T)";
                if (ReferenceEquals(_active, RockLayer)) return "Rocks (R)";
                if (ReferenceEquals(_lineActive, FenceLayer)) return "Fence (F)";
                if (ReferenceEquals(_lineActive, PowerLineLayer)) return "Power (P)";
                switch (Brush)
                {
                    case BrushMode.Raise: return "Raise (1)";
                    case BrushMode.Lower: return "Lower (2)";
                    case BrushMode.Smooth: return "Smooth (3)";
                    case BrushMode.Flatten: return "Flatten (4)";
                    case BrushMode.Slope: return "Slope (5)";
                    case BrushMode.Sea: return "Sea (6)";
                    case BrushMode.Measure: return "Measure (7)";
                    case BrushMode.Forest: return "Forest (8)";
                    default: return "";
                }
            }
        }
        void HideLinePreviews() { FenceLayer.HidePreview(); PowerLineLayer.HidePreview(); RailLayer.HidePreview(); PlanLayer.HidePreview(); RoadPlanLayer.HidePreview(); RetainingWallLayer.HidePreview(); RailLayer.HideConnectPreview(); RoadPlanLayer.HideConnectPreview(); }

        // Live preview while a connect end is armed (C held + rail mode): the join to the
        // endpoint under the cursor, green/red, with a HUD line.
        void UpdateConnectPreview(RaycastHit hit, bool overTerrain)
        {
            _connectStatus = null;
            if (_lineActive is RoadPlanLayer rdc)
            {
                if (_roadConnectNodeA >= rdc.Graph.Nodes.Count) _roadConnectNodeA = -1;   // stale after an edit
                Vector2 rcur = new Vector2(hit.point.x, hit.point.z);
                if (_roadConnectNodeA >= 0 && overTerrain && Input.GetKey(KeyCode.C))
                {
                    int b = rdc.Graph.NearestNode(rcur, rdc.ConnectHoverRadius);
                    if (b < 0 || b == _roadConnectNodeA || rdc.NodeDegree(b) < 1) { rdc.HideConnectPreview(); _connectStatus = "Connect: click end B."; return; }
                    rdc.TryConnectGeometry(_roadConnectNodeA, b, out var rcr);
                    rdc.RenderConnectPreview(Surf, rcr);
                    _connectStatus = rcr.Valid ? $"Connect → R {rcr.Radius:0} m — OK. Click end B." : $"Connect — {rcr.Reason}.";
                    return;
                }
                rdc.HideConnectPreview();
                return;
            }
            if (!(_lineActive is RailTrackLayer rc)) return;
            if (_railConnectNodeA >= rc.Graph.Nodes.Count) _railConnectNodeA = -1;   // stale after an edit
            Vector2 cursor = new Vector2(hit.point.x, hit.point.z);
            // Explicit C-connect (armed end A):
            if (_railConnectNodeA >= 0 && overTerrain && Input.GetKey(KeyCode.C))
            {
                int b = rc.NearestNodeForPick(cursor);
                if (b < 0 || b == _railConnectNodeA || rc.NodeDegree(b) < 1) { rc.HideConnectPreview(); _connectStatus = "Connect: click end B."; return; }
                rc.TryConnectGeometry(_railConnectNodeA, b, out var cr);
                rc.RenderConnectPreview(Surf, cr);
                _connectStatus = cr.Valid
                    ? $"Connect → R {cr.Radius:0} m, max {cr.MaxSpeed:0} km/h — OK. Click end B."
                    : $"Connect — {cr.Reason}.";
                return;
            }
            // Curve mode hovering another endpoint → auto-fillet join preview (A = chain tail):
            if (overTerrain && rc.TryChainConnectTarget(cursor, out var ccr))
            {
                rc.RenderConnectPreview(Surf, ccr);
                _connectStatus = ccr.Valid ? $"Join → R {ccr.Radius:0} m, max {ccr.MaxSpeed:0} km/h — click to join." : $"Join — {ccr.Reason}.";
                return;
            }
            rc.HideConnectPreview();
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
            fly.enabled = true;   // a saved scene / force-quit can leave the component disabled → middle-mouse look + zoom dead
            fly.ScrollSuppressor = () => MouseOverActivePanel() || CmdSpeedScroll() || AltParallelScroll() || ShiftBrushScroll() || MouseOverMinimap() || WallTopScroll() || BridgeArchTool.AdjustingRise;
            fly.LookSuppressor = () => MouseOverActivePanel();
            fly.InputSuppressor = () => ChunkMapEditor.IsOpen;   // freeze the camera while the map trimmer is open
            fly.GroundHeight = WorldGroundHeight; // terrain-aware altitude clamp
            if (fresh) FrameFly(fly);
        }

        float WorldGroundHeight(Vector3 p) => _field != null ? _field.SampleHeight(p.x, p.z) : 0f;

        // True while Cmd is held in rail/plan/road-plan mode: the wheel adjusts the active
        // tool's design speed (and the camera ignores it, via ScrollSuppressor). Only one of
        // these modes is active at a time, so the same gesture drives whichever is current.
        bool CmdSpeedScroll() =>
            (_lineActive is RailTrackLayer || _lineActive is RailPlanLayer || _lineActive is RoadPlanLayer)
            && (Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand));

        // Cmd + wheel in retaining-wall mode sets the wall-top elevation (camera ignores the wheel while Cmd is
        // held; a plain wheel still zooms the camera).
        bool WallTopScroll() => IsRetainingWallMode && !MouseOverActivePanel()
            && (Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand));

        // Option(Alt) + wheel adjusts the parallel-track count (rail Build mode, parallel on).
        bool AltParallelScroll() => RailLayer != null
            && _lineActive is RailTrackLayer && RailLayer.ParallelEnabled
            && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));

        // Shift + wheel resizes the brush (sculpt/scatter — i.e. not while drawing lines,
        // where Shift is the curve modifier). Camera ignores the wheel then.
        bool ShiftBrushScroll() => _lineActive == null
            && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));

        // True when the cursor is over the chunk minimap (real pixels — Input.mousePosition is
        // bottom-left origin; the map is drawn top-right in UiScale-scaled GUI space). Used to
        // route the scroll wheel to minimap zoom and to keep the camera from moving meanwhile.
        bool MouseOverMinimap()
        {
            if (!ChunkWorld.Active) return false;
            float ui = Mathf.Max(0.25f, UiScale);
            float rx0 = Screen.width - 202f * ui, rx1 = Screen.width - 12f * ui;
            float ry0 = 12f * ui, ry1 = 202f * ui;
            float mx = Input.mousePosition.x, my = Screen.height - Input.mousePosition.y;
            return mx >= rx0 && mx <= rx1 && my >= ry0 && my <= ry1;
        }

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

        // Live URP shadow distance (metres). Writes the pipeline asset directly AND keeps the serialized
        // ShadowDistance field in sync so SceneAmbiance.Apply (setup/rebuild) and the persisted default agree.
        public float ShadowDistanceValue
        {
            get
            {
                var a = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                return a != null ? a.shadowDistance : ShadowDistance;
            }
            set
            {
                ShadowDistance = Mathf.Clamp(value, 20f, 4000f);
                var a = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                if (a != null) a.shadowDistance = ShadowDistance;
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
            UpdatePlanGradeLabels();   // world-space TMP grade labels (runs even under a modal, to hide)
            AutoOverviewByAltitude();  // fast-travel auto-engages above ~2 km altitude
            // Deferred post-load road re-sweep: a load stages the plan + Built flags, but the world (chunks) may
            // still be settling — wait a beat, then re-sweep so the 3D roads appear without a manual Build click.
            if (_roadRebuildAfterLoad >= 0f)
            {
                _roadRebuildAfterLoad -= Time.unscaledDeltaTime;
                if (_roadRebuildAfterLoad < 0f) RebuildBuiltRoads();
            }
            // A modal (e.g. New Map name entry) owns the keyboard — suspend tool input so
            // typing a name doesn't fire hotkeys or sculpt.
            if (NetworkDesigner.UI.PaletteBase.ModalOpen || NetworkDesigner.UI.PaletteBase.TextEditing) return;
            TickHydrology();   // debounced drainage-analysis recompute after terrain edits settle
            // Brush-mode hotkeys. A brush key always lands you in sculpt mode (exits any line/scatter tool —
            // e.g. the retaining wall — and hides its preview), matching the palette buttons.
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetBrush(BrushMode.Raise);
            else if (Input.GetKeyDown(KeyCode.Alpha2)) SetBrush(BrushMode.Lower);
            else if (Input.GetKeyDown(KeyCode.Alpha3)) SetBrush(BrushMode.Smooth);
            else if (Input.GetKeyDown(KeyCode.Alpha4)) SetBrush(BrushMode.Flatten);
            else if (Input.GetKeyDown(KeyCode.Alpha5)) SetBrush(BrushMode.Slope);
            else if (Input.GetKeyDown(KeyCode.Alpha6)) SetBrush(BrushMode.Sea);
            else if (Input.GetKeyDown(KeyCode.Alpha7)) SetBrush(BrushMode.Measure);
            else if (Input.GetKeyDown(KeyCode.Alpha8)) SetBrush(BrushMode.Forest);
            else if (Input.GetKeyDown(KeyCode.Alpha9)) SetLineMode(RetainingWallLayer);   // retaining-wall tool
            // T/R/F toggle the active mode (mutually exclusive; press the same
            // key again to return to sculpt).
            if (Input.GetKeyDown(KeyCode.T)) { SetScatterMode(TreeLayer); SyncScatterPalette(); }
            if (Input.GetKeyDown(KeyCode.R)) { SetScatterMode(RockLayer); SyncScatterPalette(); }
            if (Input.GetKeyDown(KeyCode.F)) { SetLineMode(FenceLayer); SyncScatterPalette(); }
            if (Input.GetKeyDown(KeyCode.P)) SetLineMode(PowerLineLayer);
            if (Input.GetKeyDown(KeyCode.L)) { SetLineMode(RailLayer); SyncPaletteToMode(); }
            if (Input.GetKeyDown(KeyCode.K)) { SetLineMode(PlanLayer); SyncPaletteToMode(); }
            // Launcher palette hotkeys (radio toggle, same as the launcher buttons).
            if (Input.GetKeyDown(KeyCode.N)) NetworkDesigner.UI.PaletteBase.ToggleExclusive("Terrain");
            if (Input.GetKeyDown(KeyCode.Y)) NetworkDesigner.UI.PaletteBase.ToggleExclusive("System");
            if (Input.GetKeyDown(KeyCode.O)) NetworkDesigner.UI.PaletteBase.ToggleExclusive("Placeables");
            if (Input.GetKeyDown(KeyCode.U)) NetworkDesigner.UI.PaletteBase.ToggleExclusive("Environment");
            // ; → road PLAN mode (open the palette if closed / switch from Build). Pressing ; while already in Plan
            // closes the Road palette. ' does the same for BUILD mode.
            if (Input.GetKeyDown(KeyCode.Semicolon))
            {
                if (NetworkDesigner.UI.PaletteBase.IsOpenId("Road") && IsRoadPlanMode && !IsRoadBuildMode)
                { NetworkDesigner.UI.PaletteBase.ToggleExclusive("Road"); EnterSculptMode(); }   // already in Plan → close
                else
                {
                    if (!NetworkDesigner.UI.PaletteBase.IsOpenId("Road")) NetworkDesigner.UI.PaletteBase.ToggleExclusive("Road");
                    EnterRoadPlanMode();
                }
            }
            if (Input.GetKeyDown(KeyCode.Quote))
            {
                if (NetworkDesigner.UI.PaletteBase.IsOpenId("Road") && IsRoadBuildMode)
                { NetworkDesigner.UI.PaletteBase.ToggleExclusive("Road"); EnterSculptMode(); }   // already in Build → close
                else
                {
                    if (!NetworkDesigner.UI.PaletteBase.IsOpenId("Road")) NetworkDesigner.UI.PaletteBase.ToggleExclusive("Road");
                    EnterRoadBuildMode();
                }
            }
            if (Input.GetKeyDown(KeyCode.BackQuote)) NetworkDesigner.UI.PaletteBase.ToggleQuick("Guides");   // ` = Design Controls quick palette (overlays, keeps your place)
            if (Input.GetKeyDown(KeyCode.Tab)) NetworkDesigner.UI.PositionPalette.Toggle();                  // Tab = position HUD (Alt/X/Z/Route)
            if (Input.GetKeyDown(KeyCode.I) && RailLayer != null) RailLayer.ShowCurveInspect = !RailLayer.ShowCurveInspect;
            // M toggles the chunk-streaming bubble lock (freeze the resident set to sculpt in place).
            if (Input.GetKeyDown(KeyCode.M) && ChunkWorld.Active) ChunkLockBubble = !ChunkLockBubble;
            // V toggles the corner minimap / 3D relief diorama.
            if (Input.GetKeyDown(KeyCode.V) && ChunkWorld.Active) ToggleMinimap();
            // J toggles topographic contour lines over the terrain.
            if (Input.GetKeyDown(KeyCode.J) && ChunkWorld.Active) ToggleTopo();
            // Cmd + mouse wheel: nudge the active tool's design speed ±10 km/h per notch while
            // in rail/plan/road-plan mode — set it without leaving the plan. The camera ignores
            // the wheel while Cmd is held (see ScrollSuppressor in the camera setup).
            if (CmdSpeedScroll())
            {
                int notches = Mathf.RoundToInt(Input.mouseScrollDelta.y);
                if (notches != 0)
                {
                    // ±5 km/h per notch, snapped to multiples of 5 (…05, 10, 15…).
                    if (_lineActive is RoadPlanLayer rdSpd)
                        rdSpd.DesignSpeedKmh = Mathf.Clamp(Mathf.Round((rdSpd.DesignSpeedKmh + notches * 5f) / 5f) * 5f, 5f, 200f);
                    else
                        RailLayer.SpeedLimitKmh = Mathf.Clamp(Mathf.Round((RailLayer.SpeedLimitKmh + notches * 5f) / 5f) * 5f, 5f, 200f);
                }
            }
            // Option(Alt) + wheel in rail parallel mode: ±1 parallel track per notch. The
            // camera ignores the wheel while this is active (ScrollSuppressor).
            if (AltParallelScroll())
            {
                int notches = Mathf.RoundToInt(Input.mouseScrollDelta.y);
                if (notches != 0)
                    RailLayer.ParallelCount = Mathf.Clamp(RailLayer.ParallelCount + (notches > 0 ? 1 : -1), 1, 8);
            }
            // Shift + wheel: resize the brush (proportional, ~10% per notch). macOS remaps
            // Shift+wheel to HORIZONTAL scroll, so the delta lands in .x not .y — read
            // whichever axis carries it.
            if (ShiftBrushScroll())
            {
                Vector2 sd = Input.mouseScrollDelta;
                float raw = Mathf.Abs(sd.x) > Mathf.Abs(sd.y) ? sd.x : sd.y;
                int notches = Mathf.RoundToInt(raw);
                if (notches != 0)
                    BrushRadius = Mathf.Clamp(BrushRadius * Mathf.Pow(1.1f, notches), 0.5f, MaxBrushRadius);
            }
            // Wheel over the chunk minimap zooms it (camera is suppressed via ScrollSuppressor):
            // scroll up = fewer chunks (zoom in), down = more landscape (zoom out).
            if (ChunkWorld.Active && MouseOverMinimap())
            {
                int notches = Mathf.RoundToInt(Input.mouseScrollDelta.y);
                if (notches != 0) _minimapW = Mathf.Clamp(_minimapW - notches, 2, 24);
            }
            if (Input.GetKeyDown(KeyCode.G))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (shift) ToggleSnap();                        // Shift+G: snap toggle
                else ToggleGrid();                             // G: grid toggle (chunk grid in the chunk world)
            }
            // B (in rail mode): toggle grade override — build across whatever terrain
            // the edge crosses instead of truncating at the grade limit.
            if (Input.GetKeyDown(KeyCode.B) && _lineActive is RailTrackLayer)
                RailLayer.OverrideGrade = !RailLayer.OverrideGrade;
            // Z (rail mode): toggle parallel drawing. X: flip which side it's laid on.
            if (Input.GetKeyDown(KeyCode.Z) && _lineActive is RailTrackLayer)
                RailLayer.ParallelEnabled = !RailLayer.ParallelEnabled;
            if (Input.GetKeyDown(KeyCode.X) && _lineActive is RailTrackLayer && RailLayer.ParallelEnabled)
                RailLayer.FlipParallelSide();
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

            // Debounced autosave: write once sculpting/flying has paused. Suppressed while a modal (tree
            // pack manager, etc.) is open — nothing's changing in there, and the ~8 MB BuildSnapshot landing
            // ~1×/sec was the periodic frame spike. _dirtySince is kept, so it flushes once on modal close.
            if (Autosave && _dirtySince >= 0f && !NetworkDesigner.UI.PaletteBase.ModalOpen
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
            { _slopeArmed = false; _slopeHasGuide = false; _slopeCornerPending = false; }

            // One hover raycast per frame (against the TerrainCollider), shared
            // by the brush cursor and the sculpt itself.
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            bool overTerrain = false;
            RaycastHit hit = default;
            if (cam != null)
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                // Low-poly mesh terrain, OR the DEM Unity-Terrain (TerrainCollider) when one's built,
                // so the line tools can place/drape onto whichever surface is active.
                overTerrain = Physics.Raycast(ray, out hit, 100000f)
                              && (hit.collider is MeshCollider
                                  || ((DemTerrainWorld.HasWorld || ChunkWorld.Active) && hit.collider is TerrainCollider));
                // Collider-independent fallback for LINE tools (rail/road plan): if the physics ray missed because the
                // chunk collider there isn't cooked (streaming bubble / zoomed out), march the camera ray against the
                // terrain HEIGHTFIELD so you can still place/connect anywhere the terrain is defined — not only where a
                // collider happens to be baked. Sculpt tools are excluded (they genuinely need the cooked mesh).
                if (!overTerrain && _lineActive != null && Surf != null && RaycastTerrainHeightfield(ray, out Vector3 gp))
                {
                    hit.point = gp; hit.normal = Vector3.up; overTerrain = true;
                }
            }
            // Retaining-wall mode: Cmd + wheel sets the wall RISE above the natural grade (±0.5 m/notch). The
            // camera ignores the wheel only while Cmd is held (WallTopScroll → ScrollSuppressor); plain wheel zooms.
            if (WallTopScroll())
            {
                int wn = Mathf.RoundToInt(Input.mouseScrollDelta.y);
                if (wn != 0) RetainingWallLayer.NudgeRise(wn * 0.5f);
            }
            // The raycast passes THROUGH the UI Toolkit palette to the terrain behind it,
            // so treat "cursor over a panel" as not-over-terrain — suppresses the brush
            // cursor, line preview, and the world-space design-speed readout over the UI.
            if (overTerrain && MouseOverActivePanel()) overTerrain = false;
            // Sculpt tools (brush / slope / flatten) are live ONLY while the Terrain palette
            // is open. In sculpt mode with no Terrain palette (none open, or System) there's
            // no active terrain tool. (Rail/scatter set _lineActive/_active, so unaffected.)
            if (overTerrain && _lineActive == null && _active == null
                && !NetworkDesigner.UI.PaletteBase.IsOpenId("Terrain")) overTerrain = false;

            // Armed water-body placement: the next terrain click seeds a body at clicked ground + 5 m and floods from
            // there. Does its OWN ground pick (water placement may have no active tool, which zeroes overTerrain above)
            // and swallows the frame's tool input so the click doesn't also draw/sculpt. Esc cancels.
            if (_placingWaterBody)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { _placingWaterBody = false; }
                else if (Input.GetMouseButtonDown(0) && !MouseOverActivePanel() && cam != null)
                {
                    Ray wr = cam.ScreenPointToRay(Input.mousePosition);
                    bool got = Physics.Raycast(wr, out RaycastHit wh, 100000f)
                               && (wh.collider is MeshCollider || wh.collider is TerrainCollider);
                    Vector3 gp = got ? wh.point : default;
                    if (!got) got = RaycastTerrainHeightfield(wr, out gp);
                    if (got)
                    {
                        float lvl = ChunkWorld.SampleHeight(gp.x, gp.z) + WaterBodies.SeedRise;
                        var b = WaterBodies.Add(new Vector2(gp.x, gp.z), lvl);
                        Debug.Log($"[WaterBodies] body at ({gp.x:0},{gp.z:0}) ground+{WaterBodies.SeedRise:0} = {lvl:0} m → {b.CellCount} cells.");
                        _placingWaterBody = false; _dirtySince = Time.realtimeSinceStartup;
                    }
                }
                return;   // armed → don't let this frame's click reach the draw/sculpt tools
            }

            // Bridge-arch editing mode: hover an existing bridge → its trestles highlight; click a start trestle, then an
            // end trestle; a preview arch is drawn base→base; scroll adjusts the rise; Enter confirms, Esc cancels.
            // Drives the whole interaction through BridgeArchTool; swallows the frame's tool input while active.
            if (BridgeArchTool.Active)
            {
                BridgeArchTool.Tick(RoadPlanLayer, idx => RoadPlanLayer.DesignElevation(idx, Surf), Surf,
                    RoadPlanLayer.BridgeDeckDepth, RoadPlanLayer.BridgePierSpacing,
                    cam, new Vector2(Input.mousePosition.x, Input.mousePosition.y),
                    !MouseOverActivePanel() && Input.GetMouseButtonDown(0),
                    Input.mouseScrollDelta.y, Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter),
                    Input.GetKeyDown(KeyCode.Escape) || (!MouseOverActivePanel() && Input.GetMouseButtonDown(1)));
                if (BridgeArchTool.TakeConfirmed()) { RebuildBuiltRoads(); _dirtySince = Time.realtimeSinceStartup; }   // build the arch + truncate piers
                return;   // we were in arch mode this frame → swallow tool input (whether still active or just exited)
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
                if (_slopeCornerPending)
                {
                    // Curve mode: grade follows a bezier A -> corner -> cursor.
                    Vector2 corner = new Vector2(_slopeCorner.x, _slopeCorner.z);
                    _slopePath = SampleSlopeCurve(a2, corner, c2);
                    _slopeEnd = new Vector3(c2.x, hit.point.y, c2.y);
                    _slopeEndValid = true;
                    float crun = PathLengthXZ(_slopePath);
                    _slopeGradePct = crun > 1e-3f ? (SlopeElevAtWorld(_slopeEnd) - _slopeElevA) / crun * 100f : 0f;
                }
                else
                {
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
                float run = Vector2.Distance(a2, c2);
                _slopeGradePct = run > 1e-3f ? (SlopeElevAtWorld(_slopeEnd) - _slopeElevA) / run * 100f : 0f;
                }
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
                else if (_lineActive is RoadPlanLayer roadModNow) roadModNow.CurveModifier = curveModNow;
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
            // Brush ring: in line modes show it at the RAW mouse (not the snapped track
            // cursor) so you always have a stable "where my mouse actually is" anchor —
            // distinct from the placement cursor that slides along the extension line.
            // Brush outline sits at the SNAPPED placement point (cursorVis), so the
            // ring tracks where a node will actually land instead of the raw cursor —
            // matches the grid/rail snap in both sculpt and line modes.
            // Line/rail modes show a small fixed 5 m cursor (matching the node/grid); sculpt
            // and scatter use the brush-radius ring.
            // Cmd/Ctrl held over a road plan = SELECT intent, not draw — drop the in-world placement ring and the
            // new-node preview so the cursor reverts to the plain OS arrow for picking segments.
            bool roadSelecting = _lineActive is RoadPlanLayer
                && (Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand)
                 || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
            UpdateBrushCursor(ShowBrushCursor && overTerrain && !roadSelecting, cursorVis,
                              _lineActive != null ? 5f : BrushRadius);
            UpdateSlopeFill();
            // Node pucks: shown while rail is the active line layer; the node under the
            // cursor (and the armed auto-slope node A) highlight.
            if (RailLayer != null)
            {
                RailLayer.UpdateNodePucks(Surf,
                    overTerrain ? new Vector2(hit.point.x, hit.point.z) : new Vector2(1e9f, 1e9f),
                    _lineActive is RailTrackLayer, _railSlopeNodeA >= 0 ? _railSlopeNodeA : _railConnectNodeA);
                RailLayer.RebuildBraking(Surf, cursorVis, _lineActive is RailTrackLayer);
                // One design speed for the whole network: the plan mirrors the rail's.
                if (PlanLayer != null)
                { PlanLayer.SpeedLimitKmh = RailLayer.SpeedLimitKmh; PlanLayer.MaxLateralG = RailLayer.MaxLateralG; }
                // Curve-inspection overlay: hover off the raw cursor; plan mirrors the toggle.
                Vector3 inspCur = overTerrain ? hit.point : new Vector3(1e9f, 0f, 1e9f);
                RailLayer.RebuildCurveInspect(Surf, inspCur, _lineActive is RailTrackLayer);
                if (PlanLayer != null)
                {
                    PlanLayer.ShowCurveInspect = RailLayer.ShowCurveInspect;
                    PlanLayer.CurveInspectWidth = RailLayer.CurveInspectWidth;
                    PlanLayer.TypicalTrainLengthM = RailLayer.TypicalTrainLengthM;
                    PlanLayer.RebuildCurveInspect(Surf, inspCur, _lineActive is RailPlanLayer);
                }
                UpdateConnectPreview(hit, overTerrain);
            }
            // Road plan: highlight the node under the cursor (screen-space pick) as the snap / delete target — like
            // the rail pucks. Hidden (−1) when road isn't the active layer, off-terrain, or in elevation-edit mode.
            if (RoadPlanLayer != null)
            {
                Camera hc = PickCamera != null ? PickCamera : Camera.main;
                int hn = (_lineActive is RoadPlanLayer rdH && overTerrain && !rdH.ElevationEditMode && hc != null)
                    ? RoadPlanLayer.PickNodeScreen(hc, Surf, new Vector2(Input.mousePosition.x, Input.mousePosition.y), 30f) : -1;
                RoadPlanLayer.SetHoverNode(Surf, hn);
                RoadPlanLayer.RefreshTailHighlight(Surf, _lineActive is RoadPlanLayer);   // show the open-chain tail (right-click to finish)
            }
            // Remember the placement cursor + whether it's over terrain, for the on-screen
            // design-speed readout drawn in OnGUI.
            _lineCursorWorld = cursorVis; _lineCursorValid = overTerrain
                && (_lineActive is RailTrackLayer || _lineActive is RailPlanLayer || _lineActive is RoadPlanLayer);

            // Linework mode (fence/…): click adds a node + connects from the last
            // (chain); right-click ends the chain; Backspace undoes the last node.
            if (_lineActive != null)
            {
                // (Curve modifier + plan seed-dir were set above, before SnapCursor.)

                // The snapped placement point (same one the ring shows). Deletes use
                // the raw hit so you can remove a node you're not snapping to.
                Vector3 place = cursorVis;
                // Road elevation-edit sub-mode owns the mouse: drag a node puck to set its height, click to
                // (de)select, right-click a node to level all selected to it. Skips the normal draw/delete.
                if (_lineActive is RoadPlanLayer rdElev && rdElev.ElevationEditMode)
                { rdElev.HidePreview(); HandleRoadElevationInput(rdElev, hit, overTerrain); return; }
                // Setback-edit sub-mode owns the mouse: drag an orange ring to set a road's junction setback.
                if (_lineActive is RoadPlanLayer rdSbk && rdSbk.SetbackEditMode)
                { rdSbk.HidePreview(); HandleRoadSetbackInput(rdSbk, hit, overTerrain); return; }
                // Class-edit sub-mode owns the mouse: click an edge to cycle its primary/secondary precedence.
                if (_lineActive is RoadPlanLayer rdCls && rdCls.ClassEditMode)
                { rdCls.HidePreview(); HandleRoadClassInput(rdCls, hit, overTerrain); return; }
                // Excavate / Build / Bridge sub-modes own the LEFT mouse (start node → end node). Right-click still
                // cancels an armed start or deletes a node, so you can edit the plan without leaving the sub-mode.
                if (_lineActive is RoadPlanLayer rdSub && (rdSub.ExcavateSelectMode || rdSub.BuildSegmentMode || rdSub.BridgeSelectMode))
                {
                    rdSub.HidePreview();
                    if (Input.GetMouseButtonDown(1)) HandleRoadSubModeRightClick(rdSub, hit, overTerrain);
                    else if (rdSub.ExcavateSelectMode) HandleRoadExcavateInput(rdSub, hit, overTerrain);
                    else if (rdSub.BuildSegmentMode) HandleRoadBuildInput(rdSub, hit, overTerrain);
                    else HandleRoadBridgeInput(rdSub, hit, overTerrain);
                    return;
                }
                // Setback rings are live in NORMAL plan mode too (no edit button): hovering a ring suppresses the
                // add-node cursor (you'd grab the ring, not place a node); a press over a ring grabs it to drag
                // (right-click resets to auto). Claims the mouse only over a ring, never while Cmd/Ctrl-selecting.
                bool overSetbackHandle = false;
                if (_lineActive is RoadPlanLayer rdSh && !roadSelecting)
                {
                    // An active drag owns the mouse regardless of where the cursor is (so it can't get stuck).
                    if (rdSh.IsDraggingSetback) { rdSh.HidePreview(); HandleRoadSetbackInput(rdSh, hit, overTerrain); return; }
                    if (!rdSh.PlanLinesHidden && !MouseOverActivePanel() && overTerrain)
                    {
                        Camera shc = PickCamera != null ? PickCamera : Camera.main;
                        if (shc != null) overSetbackHandle = rdSh.PickSetbackHandle(shc, Surf,
                            new Vector2(Input.mousePosition.x, Input.mousePosition.y), 30f, out _, out _);
                        if (overSetbackHandle && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
                        { rdSh.HidePreview(); HandleRoadSetbackInput(rdSh, hit, overTerrain); return; }
                    }
                }
                if (roadSelecting || overSetbackHandle) _lineActive.HidePreview();   // hide the add-node cursor while picking / over a ring
                else _lineActive.UpdatePreview(Surf, place, overTerrain);
                bool altMod = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                bool connectMod = Input.GetKey(KeyCode.C);
                bool overPanel = MouseOverActivePanel();   // cursor over the rail palette
                if (!overPanel && overTerrain && Input.GetMouseButtonDown(0))
                {
                    // Cmd/Ctrl-click inside a road segment's corridor → toggle it in the action queue (no node clicking).
                    // (Shift is the curve modifier, so selection uses Cmd/Ctrl.) Plain click still draws.
                    bool selMod = Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand)
                               || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    if (_lineActive is RoadPlanLayer rdSel && selMod)
                    {
                        int picked = rdSel.PickEdgeInCorridor(new Vector2(hit.point.x, hit.point.z));
                        if (picked >= 0) { rdSel.ToggleEdgeSelected(picked); rdSel.Rebuild(Surf); }
                    }
                    else if (_lineActive is RailTrackLayer railSlope && altMod)
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
                        // Rail connect: C+click node A, then node B → join (fillet between two
                        // ends, or a tangent branch when a mid-line through-node is involved).
                        int n = railConn.NearestNodeForPick(new Vector2(hit.point.x, hit.point.z));
                        if (n >= 0 && railConn.NodeDegree(n) >= 1)
                        {
                            if (_railConnectNodeA < 0) _railConnectNodeA = n;          // pick A
                            else if (railConn.TryConnectGeometry(_railConnectNodeA, n, out var cr) && cr.Valid)
                            {
                                railConn.CommitConnect(Surf, cr); _dirtySince = Time.realtimeSinceStartup;
                                _railConnectNodeA = -1; railConn.HideConnectPreview();
                            } // invalid B → keep A armed so a different B can be picked
                        }
                    }
                    else if (_lineActive is RoadPlanLayer roadConn && connectMod)
                    {
                        // Road connect: C+click node A, then node B → tangent-matched fillet (with auto-extension).
                        int n = roadConn.Graph.NearestNode(new Vector2(hit.point.x, hit.point.z), roadConn.ConnectHoverRadius);
                        if (n >= 0 && roadConn.NodeDegree(n) >= 1)
                        {
                            if (_roadConnectNodeA < 0) _roadConnectNodeA = n;              // pick A
                            else if (roadConn.TryConnectGeometry(_roadConnectNodeA, n, out var rcr) && rcr.Valid)
                            {
                                roadConn.CommitConnect(Surf, rcr); _dirtySince = Time.realtimeSinceStartup;
                                _roadConnectNodeA = -1; roadConn.HideConnectPreview();
                            } // invalid B → keep A armed
                        }
                    }
                    else if (_lineActive is RailTrackLayer railCC
                             && railCC.TryChainConnectTarget(new Vector2(hit.point.x, hit.point.z), out var ccr) && ccr.Valid)
                    {
                        // Hovering another endpoint → commit the auto-fillet join.
                        railCC.CommitConnect(Surf, ccr); railCC.EndChain(); railCC.HideConnectPreview();
                        _dirtySince = Time.realtimeSinceStartup;
                    }
                    else if (_lineActive is RailTrackLayer railOA && railOA.ExtensionOffAxis)
                    {
                        // Mouse is off the extension line and not over a node — ignore the
                        // click (no wonky straight; come back on-axis or hover a node).
                    }
                    else if (_lineActive is RoadPlanLayer roadOA && roadOA.StraightOffAxis)
                    {
                        // Guided road: off-axis in OPEN space would be a freehand kink — ignore. BUT a deliberate
                        // connection that lands on an existing node/edge is allowed at any angle (so you can join two
                        // networks). Continue colinear, curve (Shift), or snap a 90° corner for open-space turns.
                        if (cam != null && roadOA.TryOffAxisJoin(cam, Surf, Input.mousePosition,
                                new Vector2(hit.point.x, hit.point.z), out Vector2 joinXZ))
                        {
                            roadOA.AddNode(Surf, new Vector3(joinXZ.x, hit.point.y, joinXZ.y));
                            _dirtySince = Time.realtimeSinceStartup;
                        }
                    }
                    else
                    {
                        _lineActive.AddNode(Surf, place);
                        _dirtySince = Time.realtimeSinceStartup;
                    }
                }
                if (!overPanel && Input.GetMouseButtonDown(1))
                {
                    // An armed auto-slope / connect cancels first (right-click backs out).
                    if (_railConnectNodeA >= 0) { _railConnectNodeA = -1; if (_lineActive is RailTrackLayer rcx) rcx.HideConnectPreview(); }
                    else if (_roadConnectNodeA >= 0) { _roadConnectNodeA = -1; if (_lineActive is RoadPlanLayer rdx) rdx.HideConnectPreview(); }
                    else if (_railSlopeNodeA >= 0) _railSlopeNodeA = -1;
                    // Shift+right-click on a rail edge CHOPS that edge, keeping its nodes (gap for a bridge).
                    else if ((Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) && overTerrain
                             && _lineActive is RailTrackLayer railEd && railEd.DeleteEdgeNear(Surf, hit.point, 4f))
                        _dirtySince = Time.realtimeSinceStartup;
                    // A pending shift-curve bend cancels first (keep the chain) before delete/end-chain.
                    else if (_lineActive is RoadPlanLayer roadCancel && roadCancel.CornerPending) roadCancel.CancelCorner();
                    else DeleteOrEndChain(hit, overTerrain);
                }
                // Delete key with road segments selected → delete them; otherwise Backspace undoes the last node.
                if (_lineActive is RoadPlanLayer rdDel && rdDel.SelectedEdgeCount > 0
                    && (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace)))
                {
                    DeleteSelectedRoadSegments();
                }
                else if (Input.GetKeyDown(KeyCode.Backspace))
                {
                    _lineActive.RemoveLastNode(Surf);
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
                    && _active.Paint(Surf, hit.point, Time.deltaTime, BrushRadius,
                                     ShowWater ? WaterLevel : float.NegativeInfinity))
                    _dirtySince = Time.realtimeSinceStartup;
                if (!overPanel && overTerrain && Input.GetMouseButton(1))
                {
                    bool erased = _active.Erase(hit.point, BrushRadius);
                    // The Tree eraser also removes the instanced forest (one delete for both).
                    if (IsTreeMode && ForestGen.EraseAt(hit.point.x, hit.point.z, BrushRadius) > 0) erased = true;
                    if (erased) _dirtySince = Time.realtimeSinceStartup;
                }
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
                        _slopeA = hit.point;
                        _slopeElevA = SlopeElevAtWorld(_slopeA);
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
                                _slopeElevA = SlopeElevAtWorld(_slopeA);
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
                    else if ((Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                             && !_slopeCornerPending)
                    {
                        // Shift+click after A arms a bend corner -> the next click grades a
                        // curve A -> corner -> B (instead of a straight A->B corridor).
                        _slopeCorner = hit.point;
                        _slopeCornerPending = true;
                    }
                    else if (_slopeEndValid)
                    {
                        float elevB = SlopeElevAtWorld(_slopeEnd);
                        // Grade ALONG the path when there is one — a manual bend (curve mode) or a
                        // connected plan centreline — else the straight A→B corridor.
                        if (ChunkWorld.Active)
                        {
                            ApplySlopeChunk(_slopePath, _slopeA, _slopeEnd, _slopeElevA, elevB, BrushRadius);
                        }
                        else
                        {
                            bool dem = DemBackend && DemTerrainWorld.HasWorld;   // else the legacy low-poly variants
                            if (_slopePath != null)
                            {
                                if (dem) ApplySlopeAlongPathDem(_slopePath, _slopeElevA, elevB, BrushRadius);
                                else ApplySlopeAlongPath(_slopePath, _slopeElevA, elevB, BrushRadius);
                            }
                            else
                            {
                                if (dem) ApplySlopeDem(_slopeA, _slopeEnd, _slopeElevA, elevB);
                                else ApplySlope(_slopeA, _slopeEnd, _slopeElevA, elevB);
                            }
                        }
                        _slopeArmed = false; _slopeHasGuide = false; _slopeCornerPending = false;
                        _dirtySince = Time.realtimeSinceStartup;
                        RebuildContours();
                        ConformScatterAndLines();
                    }
                }
                // Right-click cancels the bend first (back to A), then disarms.
                if (Input.GetMouseButtonDown(1))
                {
                    if (_slopeCornerPending) _slopeCornerPending = false;
                    else { _slopeArmed = false; _slopeHasGuide = false; }
                }
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
                _demFlattenY = hit.point.y;
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

            // Measure tool: click A, click B → straight-line distance. Tracks the cursor live for
            // the A→cursor rubber-band; right-click clears. Click-driven — bypasses drag-sculpt.
            if (Brush == BrushMode.Measure)
            {
                if (overTerrain) _measCursor = hit.point;
                if (overTerrain && !MouseOverActivePanel() && Input.GetMouseButtonDown(0))
                {
                    if (!_measHasA || _measHasB) { _measA = hit.point; _measHasA = true; _measHasB = false; }
                    else { _measB = hit.point; _measHasB = true; }
                }
                if (Input.GetMouseButtonDown(1)) { _measHasA = false; _measHasB = false; }
                return;
            }

            // Sea tool (chunk world only): ONE click floods the contiguous same-altitude area and
            // lowers it. Click-only — handled here so it bypasses the held-button drag-sculpt path.
            if (Brush == BrushMode.Sea)
            {
                if (ChunkWorld.Active && overTerrain && !MouseOverActivePanel() && Input.GetMouseButtonDown(0))
                {
                    ChunkWorld.FloodLower(hit.point, SeaTolerance, SeaDrop);
                    _dirtySince = Time.realtimeSinceStartup;
                }
                return;
            }

            // Forest tool (chunk world only): left-click flood-selects a region by elevation (magic
            // wand); right-drag erases planted trees within the brush radius. "Grow forest" plants the
            // selection; "Clear sel" clears the selection highlight.
            if (Brush == BrushMode.Forest)
            {
                if (ChunkWorld.Active && overTerrain && !MouseOverActivePanel())
                {
                    if (Input.GetMouseButtonDown(0)) ForestGen.SelectByElevation(hit.point);
                    if (Input.GetMouseButton(1))   // right-drag = tree eraser (forest + placed trees)
                    {
                        bool erased = ForestGen.EraseAt(hit.point.x, hit.point.z, BrushRadius) > 0;
                        if (TreeLayer.Erase(hit.point, BrushRadius)) erased = true;
                        if (erased) _dirtySince = Time.realtimeSinceStartup;
                    }
                }
                return;
            }

            if (!overTerrain || !Input.GetMouseButton(0) || MouseOverActivePanel()) return;

            if (!_hasFlattenTarget)
            {
                GridFromWorld(hit.point, out float cfx, out float cfz);
                _flattenTarget = HeightAtGrid(cfx, cfz);
                _demFlattenY = hit.point.y;
                _hasFlattenTarget = true;
            }

            // Chunk-test world wins when active: same brush, streamed flat chunks.
            if (ChunkWorld.Active)
            {
                if (Brush != BrushMode.Slope)
                {
                    ChunkWorld.Sculpt(hit.point, BrushRadius, BrushStrength, Time.deltaTime,
                                      (DemTerrainWorld.SculptMode)(int)Brush, _demFlattenY,
                                      BuildRoadProtectMask(hit.point));   // skip cells under built roads
                    _dirtySince = Time.realtimeSinceStartup;
                    HydrologyOverlay.MarkDirty();   // chunk sculpt bypasses ConformScatterAndLines → mark here (debounced)
                }
                return;
            }

            // The SAME brush sculpts the DEM when it's the active backend (Slope is low-poly
            // only). Raise/Lower/Smooth/Flatten map 1:1 onto DemTerrainWorld.SculptMode.
            if (DemBackend && DemTerrainWorld.HasWorld)
            {
                if (Brush != BrushMode.Slope)
                {
                    DemTerrainWorld.Sculpt(hit.point, BrushRadius, BrushStrength, Time.deltaTime,
                                           (DemTerrainWorld.SculptMode)(int)Brush, _demFlattenY);
                    _dirtySince = Time.realtimeSinceStartup;
                }
                return;   // don't touch the low-poly field / chunk meshes
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
            TreeLayer.ConformToSurface(Surf);
            RockLayer.ConformToSurface(Surf);
            FenceLayer.Rebuild(Surf);
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);  // re-drape the rail too (load-restore onto the chunk surface; follows sculpts)
            PlanLayer.Rebuild(Surf); // re-drape the survey lines onto the new surface
            RoadPlanLayer.Rebuild(Surf); // re-drape the road corridor too — else it stays buried after load
            RetainingWallLayer.Rebuild(Surf); // re-seat the wall base on the new surface
            HydrologyOverlay.MarkDirty();      // terrain changed → re-run drainage analysis (debounced in Update)
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
        void UpdateBrushCursor(bool visible, Vector3 worldCenter, float radius)
        {
            _brushCursorVisible = visible;        // for the OnGUI brush-mode icon
            _brushCursorWorld = worldCenter;
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
                float wx = worldCenter.x + Mathf.Cos(ang) * radius;
                float wz = worldCenter.z + Mathf.Sin(ang) * radius;
                float wy = Surf.SampleHeight(wx, wz) + BrushCursorLift;   // drape on the ACTIVE surface (DEM or low-poly)
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
            // Curve (shift) mode: dashed extension guides through the bend corner along each
            // leg, plus a corner marker — so you can read the tangents/angle while placing B.
            if (_slopeCornerPending)
            {
                Vector2 cor = new Vector2(_slopeCorner.x, _slopeCorner.z);
                const float gl = 80f;
                Vector2 inDir = cor - a2;
                if (inDir.sqrMagnitude > 1e-4f)
                { inDir.Normalize(); EmitDrapedDashed(cor - inDir * gl, cor + inDir * gl, dash, gap); }
                if (_slopeEndValid)
                {
                    Vector2 outDir = e2 - cor;
                    if (outDir.sqrMagnitude > 1e-4f)
                    { outDir.Normalize(); EmitDrapedDashed(cor - outDir * gl, cor + outDir * gl, dash, gap); }
                }
                AppendRing(cor, 0.9f); // bend marker
            }
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
                _cursorVerts.Add(new Vector3(p0.x, Surf.SampleHeight(p0.x, p0.y) + BrushCursorLift, p0.y));
                _cursorVerts.Add(new Vector3(p1.x, Surf.SampleHeight(p1.x, p1.y) + BrushCursorLift, p1.y));
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
                Vector3 cur = new Vector3(wx, Surf.SampleHeight(wx, wz) + BrushCursorLift, wz);
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
            // Road plan: right-click deletes the HOVERED node (+ its segments + any built road) in BOTH modes; or,
            // failing a node, the segment under the cursor — built OR un-built — removing its plan line and any 3D
            // road swept on it. Nothing under the cursor just ends the chain.
            if (_lineActive is RoadPlanLayer rdRoad)
            {
                if (overTerrain && rdRoad.HoverNode >= 0) { DeleteRoadNode(rdRoad.HoverNode); return; }
                if (overTerrain)
                {
                    int e = rdRoad.PickEdgeInCorridor(new Vector2(hit.point.x, hit.point.z));
                    if (e >= 0)
                    {
                        DeleteRoadEdges(new System.Collections.Generic.List<int> { e });   // plan edge + any built road; rebuilds + dirties
                        return;
                    }
                }
                rdRoad.EndChain(); _dirtySince = Time.realtimeSinceStartup;
                return;
            }
            bool deleted = false;
            if (overTerrain)
            {
                Vector2 dflat = new Vector2(hit.point.x, hit.point.z);
                if (_lineActive is RailTrackLayer rlDel && rlDel.TrySnapToTrack(dflat, out Vector2 dsnap))
                    deleted = rlDel.DeleteNearNode(Surf, new Vector3(dsnap.x, hit.point.y, dsnap.y), 2f);
                else if (_lineActive is RailPlanLayer plDel && plDel.TrySnapToOwnNode(dflat, out Vector2 psnap))
                    deleted = plDel.DeleteNearNode(Surf, new Vector3(psnap.x, hit.point.y, psnap.y), 2f);
                if (!deleted) deleted = _lineActive.DeleteNearNode(Surf, hit.point, 3f);
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
                    _fillVerts.Add(new Vector3(lft.x, Surf.SampleHeight(lft.x, lft.y) + BrushCursorLift, lft.y));
                    _fillVerts.Add(new Vector3(rgt.x, Surf.SampleHeight(rgt.x, rgt.y) + BrushCursorLift, rgt.y));
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

            // Protect built roads: snapshot nearby built-road footprints ONCE, then skip any cell that falls under
            // one so the brush can't disturb a road's bed. Un-built plan lines aren't protected.
            _roadMaskSegs.Clear();
            bool maskRoads = ProtectRoadsFromSculpt && RoadPlanLayer != null;
            if (maskRoads)
            {
                RoadPlanLayer.CollectBuiltFootprints(new Vector2(worldHit.x, worldHit.z),
                    BrushRadius + Mathf.Max(0f, RoadProtectMargin), _roadMaskSegs);
                maskRoads = _roadMaskSegs.Count > 0;
            }
            float ox = _field.Origin.x, oz = _field.Origin.z, protMargin = Mathf.Max(0f, RoadProtectMargin);

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

                    // Skip cells sitting on a built road (its bed must not move under the brush).
                    if (maskRoads && CellUnderRoad(ox + x * cs, oz + z * cs, protMargin)) continue;

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

        // Per-stroke snapshot of nearby built-road footprint sub-segments (a→b + half-width), filled once per
        // brush frame so the cell loop doesn't re-walk the road graph for every cell.
        readonly List<(Vector2 a, Vector2 b, float half)> _roadMaskSegs = new List<(Vector2, Vector2, float)>();

        // Snapshot built-road footprints near the brush and return a per-cell "is this under a road?" predicate (or
        // null when protection is off / no built roads are near). Used by BOTH the chunk-world and low-poly sculpt.
        System.Func<float, float, bool> BuildRoadProtectMask(Vector3 worldHit)
        {
            _roadMaskSegs.Clear();
            if (!ProtectRoadsFromSculpt || RoadPlanLayer == null) return null;
            RoadPlanLayer.CollectBuiltFootprints(new Vector2(worldHit.x, worldHit.z),
                BrushRadius + Mathf.Max(0f, RoadProtectMargin), _roadMaskSegs);
            if (_roadMaskSegs.Count == 0) return null;
            float m = Mathf.Max(0f, RoadProtectMargin);
            return (wx, wz) => CellUnderRoad(wx, wz, m);
        }

        // Is world (wx,wz) within (half-width + margin) of any snapshotted built-road segment?
        bool CellUnderRoad(float wx, float wz, float margin)
        {
            Vector2 p = new Vector2(wx, wz);
            for (int i = 0; i < _roadMaskSegs.Count; i++)
            {
                var s = _roadMaskSegs[i];
                Vector2 ab = s.b - s.a; float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-9f ? Mathf.Clamp01(Vector2.Dot(p - s.a, ab) / len2) : 0f;
                float r = s.half + margin;
                if ((p - (s.a + ab * t)).sqrMagnitude <= r * r) return true;
            }
            return false;
        }

        // Slope tool: grade the corridor between A and B (width = brush diameter) to
        // a linear ramp from elevA to elevB. Flat across the corridor (it IS the
        // ramp bed), feathering to the existing terrain only near the side edges
        // (BrushFalloff controls how wide that feather is). The ends meet the terrain
        // cleanly because elevA/elevB were sampled there.
        // Elevation under a world XZ for the slope tool — the DEM surface when it's active,
        // else the low-poly grid. Lets the two-click ramp read real heights on the DEM.
        float SlopeElevAtWorld(Vector3 world)
        {
            // Chunk world OR DEM: read the ACTIVE surface (Surf). Only the legacy low-poly path uses the field grid.
            if (ChunkWorld.Active || (DemBackend && DemTerrainWorld.HasWorld)) return Surf.SampleHeight(world.x, world.z);
            GridFromWorld(world, out float fx, out float fz);
            return HeightAtGrid(fx, fz);
        }

        // DEM slope: flatten a straight corridor A→B to a linear elevation ramp (cut + fill)
        // via the Sculpt Flatten primitive — the DEM analogue of ApplySlope (which edits the
        // low-poly field). halfW = BrushRadius; samples spaced ≤ radius so the discs overlap.
        void ApplySlopeDem(Vector3 aWorld, Vector3 bWorld, float elevA, float elevB)
        {
            if (!DemTerrainWorld.HasWorld) return;
            Vector2 a = new Vector2(aWorld.x, aWorld.z), b = new Vector2(bWorld.x, bWorld.z);
            float L = Vector2.Distance(a, b);
            if (L < 1e-3f) return;
            float halfW = Mathf.Max(2f, BrushRadius);
            float innerFrac = 1f - Mathf.Clamp01(BrushFalloff);   // plateau: full-flat width, then feather
            int n = Mathf.Max(2, Mathf.CeilToInt(L / Mathf.Max(1f, halfW)));
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n;
                Vector2 p = Vector2.Lerp(a, b, u);
                float target = Mathf.Lerp(elevA, elevB, u);
                DemTerrainWorld.FlattenStamp(new Vector3(p.x, target, p.y), halfW, innerFrac, target);
            }
            DemTerrainWorld.StitchAllSeams();
        }

        // DEM slope, path-aware: flatten a corridor that FOLLOWS a polyline to a ramp by
        // arc-length — the DEM analogue of ApplySlopeAlongPath (bend/curve + plan centreline).
        void ApplySlopeAlongPathDem(List<Vector2> path, float elevA, float elevB, float halfWidth)
        {
            if (!DemTerrainWorld.HasWorld || path == null || path.Count < 2) return;
            float halfW = Mathf.Max(2f, halfWidth);
            float total = 0f;
            for (int i = 1; i < path.Count; i++) total += Vector2.Distance(path[i - 1], path[i]);
            if (total < 1e-3f) return;
            float spacing = Mathf.Max(1f, halfW);
            float innerFrac = 1f - Mathf.Clamp01(BrushFalloff);   // plateau: full-flat width, then feather
            float walked = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                Vector2 s = path[i - 1], e = path[i];
                float segLen = Vector2.Distance(s, e);
                if (segLen < 1e-4f) continue;
                int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / spacing));
                for (int k = 0; k <= steps; k++)
                {
                    float t = k / (float)steps;
                    Vector2 p = Vector2.Lerp(s, e, t);
                    float target = Mathf.Lerp(elevA, elevB, Mathf.Clamp01((walked + t * segLen) / total));
                    DemTerrainWorld.FlattenStamp(new Vector3(p.x, target, p.y), halfW, innerFrac, target);
                }
                walked += segLen;
            }
            DemTerrainWorld.StitchAllSeams();
        }

        // Slope tool in the CHUNK world: grade a corridor to a LINEAR ramp from elevA (path/A start) to
        // elevB (end) by arc-length. Builds rail-bed-style targets and reuses ChunkWorld.GradeCorridor,
        // so it cut/fills and writes the LOD-independent edit overlay. Path = plan centreline / bend, else straight.
        void ApplySlopeChunk(List<Vector2> path, Vector3 aWorld, Vector3 bWorld, float elevA, float elevB, float halfWidth)
        {
            float halfW = Mathf.Max(2f, halfWidth);
            float innerFrac = 1f - Mathf.Clamp01(BrushFalloff);
            float spacing = Mathf.Max(1f, halfW * 0.5f);
            var targets = new List<Vector3>();
            if (path != null && path.Count >= 2)
            {
                float total = 0f;
                for (int i = 1; i < path.Count; i++) total += Vector2.Distance(path[i - 1], path[i]);
                if (total < 1e-3f) return;
                float walked = 0f;
                for (int i = 1; i < path.Count; i++)
                {
                    Vector2 s = path[i - 1], e = path[i];
                    float segLen = Vector2.Distance(s, e);
                    if (segLen < 1e-4f) continue;
                    int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / spacing));
                    for (int k = 0; k <= steps; k++)
                    {
                        float t = k / (float)steps;
                        Vector2 p = Vector2.Lerp(s, e, t);
                        float frac = Mathf.Clamp01((walked + t * segLen) / total);
                        targets.Add(new Vector3(p.x, Mathf.Lerp(elevA, elevB, frac), p.y));
                    }
                    walked += segLen;
                }
            }
            else
            {
                Vector2 a = new Vector2(aWorld.x, aWorld.z), b = new Vector2(bWorld.x, bWorld.z);
                float L = Vector2.Distance(a, b);
                if (L < 1e-3f) return;
                int n = Mathf.Max(2, Mathf.CeilToInt(L / spacing));
                for (int i = 0; i <= n; i++)
                {
                    float u = i / (float)n;
                    Vector2 p = Vector2.Lerp(a, b, u);
                    targets.Add(new Vector3(p.x, Mathf.Lerp(elevA, elevB, u), p.y));
                }
            }
            if (targets.Count == 0) return;
            // Flat/ramped BED of width 2·halfW along the path, with daylighting batters at 1:BatterRatio.
            ChunkWorld.GradeBatter(targets, halfW, BatterRatio);
            RailLayer.Rebuild(Surf); PlanLayer.Rebuild(Surf); RoadPlanLayer.Rebuild(Surf);
        }

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

        // Slope curve mode: sample a bezier A -> corner -> B into a polyline the slope
        // corridor follows (same path format the plan-centreline grading uses).
        static List<Vector2> SampleSlopeCurve(Vector2 a, Vector2 corner, Vector2 b)
        {
            const float f = 0.55f;   // how strongly the curve leans toward the corner
            Vector2 c1 = Vector2.Lerp(a, corner, f), c2 = Vector2.Lerp(b, corner, f);
            var pts = new List<Vector2>();
            const int n = 24;
            for (int i = 0; i <= n; i++) pts.Add(LineGraph.Bezier(a, c1, c2, b, i / (float)n));
            return pts;
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
            TreeLayer.ConformToSurface(Surf);
            RockLayer.ConformToSurface(Surf);
            FenceLayer.Rebuild(Surf);
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);
            PlanLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf);
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
            TreeLayer.ConformToSurface(Surf);   // re-settle everything onto the flat ground
            RockLayer.ConformToSurface(Surf);
            FenceLayer.Rebuild(Surf);
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);
            PlanLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf);
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
        // Procedurally generate the terrain heightfield at the configured resolution,
        // then rebuild the mesh and re-settle everything onto the new surface.
        public void GenerateTerrain(TerrainGenSettings settings)
        {
            if (settings == null) return;
            EnsureField(forceRebuild: true);
            int cx = _field.ColumnsX, rz = _field.RowsZ;
            if (_field.Heights == null || _field.Heights.Length != cx * rz)
                _field = new TerrainField(cx, rz, _field.CellSize, _field.Origin);
            TerrainGenerator.Generate(_field.Heights, cx, rz, _field.WidthX, _field.LengthZ, settings);
            if (settings.Rivers)
                RiverGenerator.ComputeAndCarve(_field.Heights, cx, rz, _field.CellSize,
                    settings.RiverDensity, settings.RiverCarve, carve: true);
            RebuildAfterHeightChange();
            Debug.Log($"[TerrainDesigner] Generated {settings.Style} terrain ({cx}x{rz}, seed {settings.Seed}" +
                      $"{(settings.Rivers ? ", rivers" : "")}).");
        }

        // Shared tail after a wholesale height change (heightmap import / generation):
        // recreate the chunk meshes and re-place everything that sits on the surface.
        void RebuildAfterHeightChange()
        {
            _chunkMesh = null; _chunkCol = null;
            BuildAllChunks();
            TreeLayer.ConformToSurface(Surf);
            RockLayer.ConformToSurface(Surf);
            FenceLayer.Rebuild(Surf);
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);
            PlanLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf);
            RebuildContours();
            _dirtySince = Time.realtimeSinceStartup;
        }

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
            TreeLayer.ConformToSurface(Surf);
            RockLayer.ConformToSurface(Surf);
            FenceLayer.Rebuild(Surf); // re-place linework on the new surface
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);
            PlanLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf);
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
            // Persist any unsaved chunk edits FIRST (before Unity teardown can destroy the
            // chunk tiles out from under GetHeights). Best-effort; save-on-unload covers the rest.
            if (ChunkWorld.Active) ChunkWorld.SaveAll();
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
        // GLOBAL pack library — one file shared by ALL worlds (not per-world, or each world drifts its own
        // pack set). Editor: project root; Player: persistentDataPath. Mirrors TuningOverrides' location.
        string ResolvePacksPath()
#if UNITY_EDITOR
            => System.IO.Path.Combine(Application.dataPath, "..", "TerrainPacks.json");
#else
            => System.IO.Path.Combine(Application.persistentDataPath, "TerrainPacks.json");
#endif

        class PacksFile { public List<TreePack> TreePacks; public List<TreePack> RockPacks; }

        // Write the pack presets to the standalone file (on pack edits + on save).
        // A pack create/update/delete from the UI Toolkit pack modal — mirror the
        // IMGUI scatter palette: persist the standalone pack library + mark dirty.
        public void OnScatterPacksChanged()
        {
            _dirtySince = Time.realtimeSinceStartup;
            SavePacks();
        }

        public void SavePacks()
        {
            try
            {
                var data = new PacksFile { TreePacks = TreeLayer.CollectPacks(), RockPacks = RockLayer.CollectPacks() };
                // GUARD: never clobber a saved pack library with an EMPTY set. Packs vanishing was
                // traced to this — if the in-memory packs are empty (e.g. they were never loaded
                // this session, or an editor domain reload reset them) and we save, the standalone
                // file gets overwritten with 0 packs. Skip the write unless we have something, OR
                // the file is already empty (so a legitimate delete-all still persists).
                bool empty = (data.TreePacks == null || data.TreePacks.Count == 0)
                          && (data.RockPacks == null || data.RockPacks.Count == 0);
                string path = ResolvePacksPath();
                if (empty && PacksFileHasContent(path))
                {
                    Debug.LogWarning("[TerrainDesigner] SavePacks skipped — refusing to overwrite saved packs with 0 packs.");
                    return;
                }
                System.IO.File.WriteAllText(path,
                    JsonConvert.SerializeObject(data, Formatting.Indented, TerrainJsonSettings));
            }
            catch (System.Exception ex) { Debug.LogWarning($"[TerrainDesigner] Packs save failed: {ex.Message}"); }
        }

        // True if the standalone packs file exists and holds at least one tree/rock pack.
        bool PacksFileHasContent(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return false;
                var d = JsonConvert.DeserializeObject<PacksFile>(System.IO.File.ReadAllText(path), TerrainJsonSettings);
                return d != null && ((d.TreePacks != null && d.TreePacks.Count > 0)
                                  || (d.RockPacks != null && d.RockPacks.Count > 0));
            }
            catch { return false; }
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
                // Only OVERRIDE with the standalone file when it actually has packs — an empty
                // or null list must not wipe packs already loaded from the autosave (the file is
                // authoritative only when populated).
                if (data.TreePacks != null && data.TreePacks.Count > 0) TreeLayer.SetPacks(data.TreePacks);
                if (data.RockPacks != null && data.RockPacks.Count > 0) RockLayer.SetPacks(data.RockPacks);
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
            ApplyLoadedField(loaded);
            Debug.Log("[TerrainDesigner] Loaded.");
        }

        // Adopt a freshly-loaded field, live: re-origin, swap the field, rebuild chunks +
        // all layers + water + camera, and mark clean. Shared by autosave-load and map-load.
        void ApplyLoadedField(TerrainField loaded)
        {
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
            TreeLayer.SpawnPending(Surf);
            RockLayer.SpawnPending(Surf);
            FenceLayer.Rebuild(Surf);
            PowerLineLayer.Rebuild(Surf);
            RailLayer.Rebuild(Surf);
            PlanLayer.Rebuild(Surf);
            RoadPlanLayer.Rebuild(Surf);
            RebuildContours();
            ApplyWater();
            if (_havePendingCam) { ApplyCameraPose(_pendingCamPos, _pendingCamYaw, _pendingCamPitch); _havePendingCam = false; }
            _dirtySince = -1f;
        }

        // --- Named maps (save/load the whole terrain state by name in Resources/Maps) ---
        public bool IsDirty => _dirtySince >= 0f;

        string MapsFolder()
#if UNITY_EDITOR
            => System.IO.Path.Combine(Application.dataPath, "Resources", "Maps");
#else
            => System.IO.Path.Combine(Application.persistentDataPath, "Maps");
#endif

        public List<string> ListMaps()
        {
            var list = new List<string>();
            try
            {
                string dir = MapsFolder();
                if (System.IO.Directory.Exists(dir))
                    foreach (string f in System.IO.Directory.GetFiles(dir, "*.json"))
                        list.Add(System.IO.Path.GetFileNameWithoutExtension(f));
            }
            catch { /* unreadable -> empty */ }
            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // Save the whole current state to Resources/Maps/<name>.json (marks clean). True on success.
        public bool SaveMap(string name)
        {
            if (_field == null || string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                string dir = MapsFolder();
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, name.Trim() + ".json");
                try { _saveTask?.Wait(2000); } catch { /* ignore */ }
                WriteSave(BuildSnapshot(), path);
                SavePacks();
                _dirtySince = -1f;
                Debug.Log($"[TerrainDesigner] Map saved → {path}");
                return true;
            }
            catch (System.Exception ex) { Debug.LogWarning($"[TerrainDesigner] Map save failed: {ex.Message}"); return false; }
        }

        // Create a fresh FLAT map at the current size — no scatter / rail / plan / fence /
        // power / placeables — display it, and save it under `name`. Returns true if saved.
        public bool NewMap(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            float size = Mathf.Max(1f, TerrainSizeMeters);
            float cs = Mathf.Max(0.1f, CellSize);
            int res = Mathf.Max(2, Mathf.RoundToInt(size / cs) + 1);
            float half = (res - 1) * cs * 0.5f;
            _field = new TerrainField(res, res, cs, transform.position - new Vector3(half, 0f, half));
            ColumnsX = _field.ColumnsX; RowsZ = _field.RowsZ; CellSize = _field.CellSize;

            TreeLayer.ClearAll(); RockLayer.ClearAll();
            FenceLayer.ClearAll(Surf); PowerLineLayer.ClearAll(Surf);
            RailLayer.ClearAll(Surf); PlanLayer.ClearAll(Surf);
            FindFirstObjectByType<NetworkDesigner.Placeables.PlaceablesManager>()?.ClearPlaced();

            ShowWater = false;   // a fresh map starts dry
            _chunkMesh = null; _chunkCol = null;
            BuildAllChunks();
            RebuildContours();
            ApplyWater();
            ResetCamera();       // frame the fresh terrain (same as the React camera.reset)
            _dirtySince = -1f;
            return SaveMap(name);
        }

        // Load a named map live. REFUSES if there are unsaved changes (dirty) — save first.
        public bool LoadMap(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (IsDirty)
            {
                Debug.LogWarning("[TerrainDesigner] Unsaved changes — save the current map before loading another.");
                return false;
            }
            string path = System.IO.Path.Combine(MapsFolder(), name.Trim() + ".json");
            TerrainField loaded = TryLoadTerrainFrom(path);
            if (loaded == null) { Debug.LogWarning($"[TerrainDesigner] Map not found / unreadable: {name}"); return false; }
            ApplyLoadedField(loaded);
            Debug.Log($"[TerrainDesigner] Loaded map: {name}");
            return true;
        }

        // --- Games (DEM-based saved games; the startup modal drives these) ---------------------
        // A game = a folder under Games/<name>/ with a manifest (DEM set + decode range), an object
        // snapshot (autosave.json) and its terrain sculpt edits (ChunkEditsDem/). Pointing
        // AutosavePath at the game's autosave.json routes BOTH the snapshot and the chunk-edit dir
        // into the game folder, so autosave then saves THIS game with no further plumbing.
        public List<string> ListGames() => GameManager.ListGames();
        public List<string> ListDemSets() => DemTerrainWorld.ListWorlds();
        public string LastGame => GameManager.Last;
        public bool HasGame(string name) => GameManager.Exists(name);
        public float DefaultNormMin => DemChunkSource.NormMin;
        public float DefaultNormMax => DemChunkSource.NormMax;

        public void NewGame(string name, string demSet, float min, float max)
        {
            if (!GameManager.Create(name, demSet, min, max)) { Debug.LogWarning("[Game] create failed (name/DEM set?)"); return; }
            LoadGame(name);
        }

        public void LoadGame(string name)
        {
            var info = GameManager.Read(name);
            if (info == null) { Debug.LogWarning($"[Game] not found / unreadable: {name}"); return; }
            // Route autosave (object snapshot + chunk edits) into THIS game's folder.
            AutosavePath = GameManager.AutosaveFile(name);
            // Decode range from the manifest — never the −500/3500 mismatch again.
            DemChunkSource.NormMin = info.NormMin; DemChunkSource.NormMax = info.NormMax;

            if (ChunkWorld.Active) StopChunkTest();
            // Drop the previous world's live networks BEFORE staging this one, so a direct world→world switch
            // can't leave the old rail/road/plans draped on the new terrain. (No dirty flag — see ClearAllNetworks.)
            ClearAllNetworks();
            // Restore the object build (rail / scatter / fences / power) from this game's snapshot if it
            // exists (new games have none yet). The low-poly field rides along but gets hidden by
            // StartChunkDem below, which runs LAST so the chunk world ends up on top.
            TerrainField loaded = TryLoadTerrainFrom(AutosavePath);   // stages the saved camera into _pendingCam*
            // Grab the saved camera pose BEFORE ApplyLoadedField consumes it — we re-apply it after
            // StartChunkDem (which would otherwise frame the DEM centre).
            bool haveCam = loaded != null && _havePendingCam;
            Vector3 camPos = _pendingCamPos; float camYaw = _pendingCamYaw, camPitch = _pendingCamPitch;
            if (loaded != null) ApplyLoadedField(loaded);
            // Load this game's DEM into the chunk world; its chunk edits auto-apply from the game folder.
            StartChunkDem(info.DemSet);
            // Move the camera to where it was saved AND stream the chunks around it FIRST — so the
            // rail/scatter re-drape below samples the loaded surface at THEIR location, not the
            // DEM-centre chunks StartChunkDem parked on (otherwise rail drapes onto y=0 → underground).
            if (haveCam)
            {
                ApplyCameraPose(camPos, camYaw, camPitch);
                ChunkWorld.Tick(ChunkCam(), eager: true);
            }
            // Re-settle the restored objects onto the now-loaded DEM surface at their real positions.
            if (loaded != null) ConformScatterAndLines();
            // Restore the saved water plane (toggle + level) for this game.
            if (loaded != null) { ChunkOverlays.SetWaterLevel(_pendingWaterLevel); ChunkOverlays.SetWater(_pendingWaterOn); }
            // Per-level water bodies: re-flood each from the now-loaded terrain (after the chunks around the camera streamed).
            if (loaded != null) WaterBodies.Restore(_pendingWaterBodies);
            // Rebuild the GPU-instanced forest from the save (prior forest cleared by StopChunkTest above).
            if (loaded != null && _pendingForest != null)
            {
                ForestGen.ImportForest(TreeLayer, _pendingForest);
                Debug.Log($"[Forest] restored {ForestGen.TreeCount} trees from save.");
            }
            LoadPacks();        // global pack library overrides this world's embedded packs — one set for all worlds
            GameManager.SetActive(name);
            ApplyViewPrefs();   // restore grid / snap / topo from the last session (persist across worlds)
            Debug.Log($"[Game] loaded “{name}” (DEM {info.DemSet}, range {info.NormMin:0}..{info.NormMax:0}).");
        }

        public void ContinueLastGame()
        {
            string last = GameManager.Last;
            if (GameManager.Exists(last)) LoadGame(last);
            else Debug.LogWarning("[Game] no last game to continue.");
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
                RoadPlan = RoadPlanLayer.CollectData(),
                RetainingWalls = RetainingWallLayer.CollectData(),
                HasCamera = haveCam,
                CamPos = camPos,
                CamYaw = camYaw,
                CamPitch = camPitch,
                // Only treat us as "on DEM" if a world is actually built; a DemBackend
                // flag with no world would restore to an empty low-poly fallback anyway.
                DemBackend = DemBackend && DemTerrainWorld.HasWorld,
                DemCity = DemTerrainWorld.CurrentCity ?? "",
                // Sparse DEM sculpt/carve diff (only when a DEM world is built).
                DemEdits = DemTerrainWorld.HasWorld ? DemTerrainWorld.ExportEdits() : null,
                // Chunk water plane state.
                WaterOn = ChunkOverlays.ShowWater,
                WaterLevel = ChunkOverlays.WaterLevel,
                WaterBodies = NetworkDesigner.Terrain.WaterBodies.CollectData(),   // v19+: per-level water bodies
                // v11+: GPU-instanced forest (decomposed per-species transforms).
                Forest = ForestGen.ExportForest(),
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

        // Fly-camera move speed (m/s), for the System palette Camera slider.
        public float CameraSpeed
        {
            get { var f = ResolveFly(); return f != null ? f.MoveSpeed : 12f; }
            set { var f = ResolveFly(); if (f != null) f.MoveSpeed = value; }
        }

        // Fly-camera scroll-dolly step (m per notch).
        public float CameraZoomStep
        {
            get { var f = ResolveFly(); return f != null ? f.ZoomStep : 14f; }
            set { var f = ResolveFly(); if (f != null) f.ZoomStep = value; }
        }

        // "Overview" fast-travel mode: bumps speed + zoom step for crossing a whole world (5000 / 100),
        // back to fine values for detail work (500 / 10). State is derived from the current speed, so it
        // always reflects reality without a separate stored flag.
        public bool CameraOverview
        {
            get => CameraSpeed >= 2500f;
            set { CameraSpeed = value ? 5000f : 500f; CameraZoomStep = value ? 100f : 10f; }
        }

        // Auto fast-travel: engage Overview when the camera climbs past ~2 km altitude (matches the Position HUD's
        // "Alt" = absolute world Y), disengage when it drops back below. Acts only on threshold CROSSINGS, with a
        // 1.8–2.0 km hysteresis band so it can't flap at the boundary — and so a manual Overview toggle still sticks
        // between crossings.
        [System.NonSerialized] bool _camAboveOverviewAlt;
        void AutoOverviewByAltitude()
        {
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            float y = cam.transform.position.y;
            bool above = _camAboveOverviewAlt ? y > 1800f : y > 2000f;
            if (above == _camAboveOverviewAlt) return;
            _camAboveOverviewAlt = above;
            CameraOverview = above;
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
                          + GraphBytes(save.Rails) + GraphBytes(save.Plan) + GraphBytes(save.RoadPlan)
                          + ForestBytes(save.Forest) + 256;
                using var ms = new System.IO.MemoryStream(cap);
                using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                {
                    w.Write(SaveMagic);
                    w.Write(21); // version (21 added per-edge bridge arches + per-level water bodies)
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
                    w.Write(save.DemBackend);               // v8+
                    w.Write(save.DemCity ?? "");            // v8+
                    // v9+: sparse DEM sculpt/carve diff
                    var de = save.DemEdits;
                    bool hasEdits = de != null && de.Tiles != null && de.Tiles.Count > 0;
                    w.Write(hasEdits);
                    if (hasEdits)
                    {
                        w.Write(de.City ?? "");
                        w.Write(de.Tiles.Count);
                        foreach (var te in de.Tiles)
                        {
                            int en = Mathf.Min(te.Idx?.Length ?? 0, te.H?.Length ?? 0);
                            w.Write(te.R); w.Write(te.C); w.Write(en);
                            for (int i = 0; i < en; i++) w.Write(te.Idx[i]);
                            for (int i = 0; i < en; i++) w.Write(te.H[i]);
                        }
                    }
                    // v10+: chunk water plane
                    w.Write(save.WaterOn);
                    w.Write(save.WaterLevel);
                    // v11+: GPU-instanced forest
                    WriteForest(w, save.Forest);
                    WriteGraph(w, save.RoadPlan);   // v12+: road-plan corridor
                    WriteGraph(w, save.RetainingWalls);   // v16+: retaining walls
                    // v21+: per-level water bodies (seed + level; footprint re-flooded on load)
                    var wb = save.WaterBodies;
                    int nb = wb?.Count ?? 0;
                    w.Write(nb);
                    for (int i = 0; i < nb; i++) { w.Write(wb[i].Seed.x); w.Write(wb[i].Seed.y); w.Write(wb[i].Level); }
                }
                System.IO.File.WriteAllBytes(path, ms.ToArray());
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Save write failed: {ex.Message}");
            }
        }

        static int TreeBytes(List<PlacedTreeData> t) => (t?.Count ?? 0) * 40 + 8;
        static int ForestBytes(List<ForestGen.ForestSpeciesSave> f)
        {
            int b = 8;
            if (f != null) foreach (var s in f) b += 80 + (s?.X?.Length ?? 0) * 20; // 5 floats/instance
            return b;
        }

        static void WriteForest(System.IO.BinaryWriter w, List<ForestGen.ForestSpeciesSave> list)
        {
            int c = list?.Count ?? 0;
            w.Write(c);
            for (int i = 0; i < c; i++)
            {
                var rec = list[i];
                w.Write(rec?.Prefab ?? "");
                int n = rec?.X?.Length ?? 0;
                w.Write(n);
                for (int k = 0; k < n; k++) w.Write(rec.X[k]);
                for (int k = 0; k < n; k++) w.Write(rec.Y[k]);
                for (int k = 0; k < n; k++) w.Write(rec.Z[k]);
                for (int k = 0; k < n; k++) w.Write(rec.Rot[k]);
                for (int k = 0; k < n; k++) w.Write(rec.Scale[k]);
            }
        }

        static List<ForestGen.ForestSpeciesSave> ReadForest(System.IO.BinaryReader r)
        {
            int c = r.ReadInt32();
            var list = new List<ForestGen.ForestSpeciesSave>(c);
            for (int i = 0; i < c; i++)
            {
                var rec = new ForestGen.ForestSpeciesSave { Prefab = r.ReadString() };
                int n = r.ReadInt32();
                rec.X = new float[n];     for (int k = 0; k < n; k++) rec.X[k] = r.ReadSingle();
                rec.Y = new float[n];     for (int k = 0; k < n; k++) rec.Y[k] = r.ReadSingle();
                rec.Z = new float[n];     for (int k = 0; k < n; k++) rec.Z[k] = r.ReadSingle();
                rec.Rot = new float[n];   for (int k = 0; k < n; k++) rec.Rot[k] = r.ReadSingle();
                rec.Scale = new float[n]; for (int k = 0; k < n; k++) rec.Scale[k] = r.ReadSingle();
                list.Add(rec);
            }
            return list;
        }
        static int GraphBytes(LineGraphSave g) =>
            ((g?.Nodes?.Count ?? 0) * 8) + ((g?.Edges?.Count ?? 0) * 48) + ((g?.NodeY?.Count ?? 0) * 4) + 20;

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
                w.Write(e.Profile ?? "");                  // v13+ (road-plan per-segment profile)
                w.Write(e.Excavated);                      // v15+ (road-plan per-segment excavated flag)
                w.Write(e.Bridge);                         // v17+ (road-plan per-segment bridge flag)
                w.Write(e.Built);                          // v18+ (road-plan per-segment built flag → 3D road persists)
                w.Write(e.SetbackA); w.Write(e.SetbackB);  // v19+ (road-plan per-segment setback overrides; <0 = auto)
                w.Write((int)e.Class); w.Write(e.Serial);  // v20+ (road-plan primary/secondary class + draw-order age)
                int na = e.Arches?.Count ?? 0;             // v21+ (under-deck bridge arches)
                w.Write(na);
                for (int k = 0; k < na; k++) { BridgeArch ar = e.Arches[k]; w.Write(ar.StartArc); w.Write(ar.EndArc); w.Write(ar.Rise); }
            }
            int ny = g?.NodeY?.Count ?? 0;                 // v14+: per-node design elevation
            w.Write(ny);
            for (int i = 0; i < ny; i++) w.Write(g.NodeY[i]);
        }

        TerrainField TryLoadTerrain() => TryLoadTerrainFrom(ResolveAutosavePath());

        TerrainField TryLoadTerrainFrom(string path)
        {
            try
            {
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
                RoadPlanLayer.LoadState(save.RoadPlan);
                _roadRebuildAfterLoad = 0.6f;   // re-sweep the 3D roads once the world has settled (covers all load paths / async chunk streaming)
                RetainingWallLayer.LoadState(save.RetainingWalls);   // mesh rebuilt after chunks; terrain grade already in the saved edits
                // Stage the camera pose; applied in Start once the fly camera exists.
                _havePendingCam = save.HasCamera;
                _pendingCamPos = save.CamPos;
                _pendingCamYaw = save.CamYaw;
                _pendingCamPitch = save.CamPitch;
                _pendingDemBackend = save.DemBackend;
                _pendingDemCity = save.DemCity;
                _pendingDemEdits = save.DemEdits;
                _pendingWaterOn = save.WaterOn;
                _pendingWaterLevel = save.WaterLevel;
                _pendingWaterBodies = save.WaterBodies;
                _pendingForest = save.Forest;
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
                if (version >= 8) // DEM backend + city for mode restore
                {
                    s.DemBackend = r.ReadBoolean();
                    s.DemCity = r.ReadString();
                }
                if (version >= 9) // sparse DEM sculpt/carve diff
                {
                    if (r.ReadBoolean())
                    {
                        var de = new DemTerrainWorld.Edits { City = r.ReadString() };
                        int tc = r.ReadInt32();
                        de.Tiles = new List<DemTerrainWorld.TileEdit>(tc);
                        for (int ti = 0; ti < tc; ti++)
                        {
                            var te = new DemTerrainWorld.TileEdit { R = r.ReadInt32(), C = r.ReadInt32() };
                            int en = r.ReadInt32();
                            te.Idx = new int[en];
                            for (int i = 0; i < en; i++) te.Idx[i] = r.ReadInt32();
                            te.H = new float[en];
                            for (int i = 0; i < en; i++) te.H[i] = r.ReadSingle();
                            de.Tiles.Add(te);
                        }
                        s.DemEdits = de;
                    }
                }
                if (version >= 10) // chunk water plane
                {
                    s.WaterOn = r.ReadBoolean();
                    s.WaterLevel = r.ReadSingle();
                }
                if (version >= 11) // GPU-instanced forest
                    s.Forest = ReadForest(r);
                if (version >= 12) // road-plan corridor
                    s.RoadPlan = ReadGraph(r, version);
                if (version >= 16) // retaining-wall polylines
                    s.RetainingWalls = ReadGraph(r, version);
                if (version >= 21) // per-level water bodies
                {
                    int nb = r.ReadInt32();
                    s.WaterBodies = new List<WaterBodies.Save>(nb);
                    for (int i = 0; i < nb; i++)
                        s.WaterBodies.Add(new WaterBodies.Save { Seed = new Vector2(r.ReadSingle(), r.ReadSingle()), Level = r.ReadSingle() });
                }
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
                if (version >= 13) e.Profile = r.ReadString();   // road-plan per-segment profile
                if (version >= 15) e.Excavated = r.ReadBoolean(); // road-plan per-segment excavated flag
                if (version >= 17) e.Bridge = r.ReadBoolean();    // road-plan per-segment bridge flag
                if (version >= 18) e.Built = r.ReadBoolean();     // road-plan per-segment built flag
                if (version >= 19) { e.SetbackA = r.ReadSingle(); e.SetbackB = r.ReadSingle(); }   // setback overrides
                if (version >= 20) { e.Class = (RoadClass)r.ReadInt32(); e.Serial = r.ReadInt32(); }  // primary/secondary + age
                else e.Serial = i + 1;   // pre-v20: no stored age → seed from edge order so derivation isn't all-tied
                if (version >= 21)       // under-deck bridge arches
                {
                    int na = r.ReadInt32();
                    if (na > 0)
                    {
                        e.Arches = new List<BridgeArch>(na);
                        for (int k = 0; k < na; k++)
                            e.Arches.Add(new BridgeArch { StartArc = r.ReadSingle(), EndArc = r.ReadSingle(), Rise = r.ReadSingle() });
                    }
                }
                g.Edges.Add(e);
            }
            if (version >= 14)   // per-node design elevation
            {
                g.NodeY = new List<float>();
                int ny = r.ReadInt32();
                for (int i = 0; i < ny; i++) g.NodeY.Add(r.ReadSingle());
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
