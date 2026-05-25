// Runtime entry point for the in-game network designer.
//
// Step 3 — vertex/edge creation. Build-mode input:
//   - Left-click empty ground → place a new vertex at the cursor.
//   - Left-click an existing vertex → begin an "edge-in-progress",
//     anchored at that vertex. A preview line follows the mouse.
//   - With an edge in progress, left-click on:
//       * empty ground       → place a new vertex AND create a road
//                              from the anchor to it.
//       * a different vertex → create a road between the two.
//       * the anchor vertex  → cancel the in-progress edge.
//   - Escape (any time) → cancel the in-progress edge.
//
// The state machine is intentionally small so adding multi-click curve
// operations later is straightforward: a future state can collect a
// list of waypoints between the anchor and the final click, and emit a
// curved/poly-segment road instead of a single straight one. For now
// the only "shape" we emit is a straight road between two vertices.
//
// Profile: every new road is a hard-coded 1×1 no-median for now. We'll
// hook up the preset library (from road-config.json) in a later step.
//
// What's NOT here yet: right-click delete, edit-mode handles, lane-
// connectivity editing, preset selection UI.

using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using NetworkDesigner.Geometry;
using NetworkDesigner.Import;
using NetworkDesigner.Model;
using NetworkDesigner.Rendering;

namespace NetworkDesigner.Designer
{
    [RequireComponent(typeof(NetworkRenderer))]
    [DisallowMultipleComponent]
    public class NetworkDesigner : MonoBehaviour
    {
        // NOTE: enum order matters — Unity serializes the int value, so
        // keeping Create=0/Edit=1 preserves existing scene asset values.
        public enum DesignerMode { Create, Edit }
        public enum BuildTool { Straight, Fillet, SCurve, Roundabout, CulDeSac }
        enum BuildState { Idle, EdgeFromVertex }
        // Shared state machine for the 3-click curve tools (Fillet, SCurve).
        // Tools differ only in how the cubic controls are derived at click 3.
        enum FilletState { Idle, AwaitingClick2, AwaitingClick3 }

        [Header("Mode")]
        public DesignerMode CurrentMode = DesignerMode.Create;
        public BuildTool CurrentTool = BuildTool.Straight;
        [Tooltip("Key that toggles Create ↔ Edit mode at runtime.")]
        public KeyCode ModeToggleKey = KeyCode.Tab;
        [Tooltip("Key that cycles the Create-mode tool (Straight → Fillet → SCurve → Roundabout).")]
        public KeyCode ToolToggleKey = KeyCode.C;
        [Tooltip("Direct-select hotkey for Straight tool (Create mode).")]
        public KeyCode ToolHotkeyStraight = KeyCode.Alpha1;
        [Tooltip("Direct-select hotkey for Fillet tool (Create mode).")]
        public KeyCode ToolHotkeyFillet = KeyCode.Alpha2;
        [Tooltip("Direct-select hotkey for S-Curve tool (Create mode).")]
        public KeyCode ToolHotkeySCurve = KeyCode.Alpha3;
        [Tooltip("Direct-select hotkey for Roundabout tool (Create mode).")]
        public KeyCode ToolHotkeyRoundabout = KeyCode.Alpha4;
        [Tooltip("Direct-select hotkey for Cul-de-sac tool (Create mode).")]
        public KeyCode ToolHotkeyCulDeSac = KeyCode.Alpha5;
        [Tooltip("Toggles visibility of vertex markers (the small puck at each vertex).")]
        public KeyCode VertexViewToggleHotkey = KeyCode.V;

        [Header("Tool palette UI")]
        [Tooltip("Show the on-screen tool palette (IMGUI overlay).")]
        public bool ShowToolPalette = true;
        [Tooltip("Pixel position of the palette's top-left corner (Game-view).")]
        public Vector2 ToolPaletteOrigin = new Vector2(10, 10);
        [Tooltip("Palette width in pixels. Height auto-sizes (clipped at 600px).")]
        public float ToolPaletteWidth = 220f;

        [Header("Road configs (palette source)")]
        [Tooltip("Path to road-config.json (exported from the React Road Designer). Relative to the Unity project root, or absolute.")]
        public string RoadConfigPath = "road-config.json";

        [Header("Fillet curve")]
        [Tooltip("Fraction along (click1→click2) and (click3→click2) where the cubic control points sit. 0.55 ≈ classic circle approximation; smaller = tighter corner.")]
        [Range(0.05f, 0.95f)] public float FilletLeverFraction = 0.55f;

        [Header("Measurement tooltip")]
        [Tooltip("Show a small tooltip near the cursor with the distance in meters during Create-mode placement.")]
        public bool ShowMeasurementTooltip = true;

        [Header("Network")]
        public DriveSide DriveSide = DriveSide.Right;

        [Header("Roundabout (Edit-mode 'R' on selected vertex)")]
        [Tooltip("Distance from the central vertex to each ring vertex (m).")]
        public float RoundaboutRadius = 12f;
        [Tooltip("Lane count of the one-way ring road. Recommended: 2.")]
        [Range(1, 4)] public int RoundaboutLanes = 2;
        [Tooltip("Key that converts the currently-selected vertex (with ≥2 incident roads) into a roundabout.")]
        public KeyCode RoundaboutHotkey = KeyCode.R;

        [Header("Cul-de-sac (Create-mode tool — click a dead-end vertex)")]
        [Tooltip("Bulb radius = approach-road half-width × this multiplier.")]
        public float CulDeSacWidthMultiplier = 2f;
        [Tooltip("Lane count for the cul-de-sac bulb ring road.")]
        [Range(1, 3)] public int CulDeSacLanes = 1;

        [Header("Autosave (network persistence across Play stop/start)")]
        [Tooltip("When on, the vertex/road network is written to disk on a debounced timer after mutations, and loaded back on enable.")]
        public bool Autosave = true;
        [Tooltip("Where the network is saved. Empty → default (project_root/NetworkAutosave.json in Editor, persistentDataPath in Player).")]
        public string AutosavePath = "";
        [Tooltip("Debounce window (s) before pending mutations are flushed to disk.")]
        public float AutosaveDebounceSeconds = 1f;

        [Header("Picking")]
        [Tooltip("Camera used for mouse-to-world picking. Defaults to Camera.main when null.")]
        public Camera PickCamera;
        [Tooltip("World Y of the ground plane that mouse rays hit.")]
        public float GroundY = 0f;

        [Header("Snap")]
        [Tooltip("When on, ground picks snap to the GroundGrid. Minor lines = " +
                 "default snap; major lines have a stronger pull radius. " +
                 "Applies to vertex placement, preview line, and edit-mode drag.")]
        public bool SnapToGrid = true;
        [Tooltip("Master enable for angle snapping. When off, AngleSnapDegrees is ignored regardless of value.")]
        public bool AngleSnapEnabled = true;
        [Tooltip("Distance, in minor-grid units, within which an axis snaps to " +
                 "the nearest major line instead of the nearest minor line. " +
                 "0 = no major bias; 1 = same strength as minor (every point " +
                 "halfway to a major snaps to that major); larger = stronger.")]
        public float MajorSnapRange = 1.5f;
        [Tooltip("GroundGrid to snap against. Auto-found at OnEnable if null. " +
                 "Snap is disabled when this is null or the grid is disabled.")]
        public GroundGrid Grid;

        [Header("Edit-mode setback handles")]
        [Tooltip("Outer diameter of the hollow-ring handle shown at each road end (meters).")]
        public float SetbackHandleDiameter = 2.0f;
        [Tooltip("Stroke width of the ring (meters). Difference between outer and inner radii.")]
        public float SetbackRingThickness = 0.35f;
        [Tooltip("World Y of the setback handle center.")]
        public float SetbackHandleHeight = 0.4f;
        [Tooltip("Minimum offset distance from the road body's setback midpoint to where the handle sits (meters). The handle sits OUTSIDE the road body so it doesn't compete with lane markers for clicks.")]
        public float SetbackHandleOffsetMin = 3f;
        [Tooltip("Offset distance also scales with road width (meters) by this multiplier — wider roads → handle sits further away.")]
        public float SetbackHandleOffsetWidthMultiplier = 0.4f;
        [Tooltip("Dash length of the connecting stem between the road's setback line and the offset handle.")]
        public float SetbackStemDashLength = 0.4f;
        [Tooltip("Gap length between stem dashes.")]
        public float SetbackStemGapLength = 0.3f;
        [Tooltip("Width of the stem line (meters).")]
        public float SetbackStemWidth = 0.1f;
        public Color SetbackHandleColor = new Color(1f, 0.55f, 0.15f);
        public Color SetbackHandleActiveColor = new Color(1f, 0.85f, 0.2f);

        [Header("Edit-mode lane-flow markers (click to author overrides)")]
        [Tooltip("Outer diameter of each clickable lane-endpoint ring (meters). Slightly larger than SetbackHandleDiameter since lane markers are functional click targets that benefit from being prominent.")]
        public float LaneMarkerDiameter = 2.5f;
        [Tooltip("Stroke width of the ring (meters).")]
        public float LaneMarkerRingThickness = 0.5f;
        [Tooltip("Outer diameter of the smaller lane-corner markers (Origin/Primary/Secondary/Tertiary). Smaller than midpoint markers so they don't dominate.")]
        public float LaneCornerMarkerDiameter = 1.4f;
        [Tooltip("Stroke width of corner-marker rings.")]
        public float LaneCornerMarkerRingThickness = 0.28f;
        [Tooltip("World Y of the marker center. Sits above the setback handles so lane markers don't get visually occluded.")]
        public float LaneMarkerHeight = 1.4f;
        [Tooltip("Default alpha for unselected/unhovered markers and arrows. 0.25 = ambient dimmed visibility.")]
        [Range(0f, 1f)] public float LaneOverlayDimAlpha = 0.25f;
        [Tooltip("Alpha applied when a lane marker is hovered or armed (and to its flow arrows).")]
        [Range(0f, 1f)] public float LaneOverlayFullAlpha = 1.0f;
        [Tooltip("Color for OUTBOUND markers (shown only when an inbound lane is armed — all the same color since they're click targets).")]
        public Color LaneOutboundMarkerColor = new Color(0.85f, 0.85f, 0.85f);
        [Tooltip("Color for an armed inbound marker. Inbound color comes from a deterministic hash of the lane reference so different lanes are visually distinct.")]
        public Color LaneMarkerArmedColor = new Color(1f, 1f, 0.3f);

        [Header("Vertex markers")]
        [Tooltip("Diameter of the puck shown at each vertex (m).")]
        public float MarkerDiameter = 2f;
        [Tooltip("Height of the puck (m). Kept short so the marker reads as a flat disc on the ground.")]
        public float MarkerThickness = 0.15f;
        [Tooltip("World Y of the puck's center. Sits just above the road lines so the disc reads as resting on them.")]
        public float MarkerHeight = 0.085f;
        public Color MarkerColor = new Color(0.2f, 0.6f, 1f);
        public Color MarkerHoverColor = new Color(0.55f, 0.85f, 1f);
        public Color MarkerActiveColor = new Color(1f, 0.85f, 0.2f);
        [Tooltip("When off, vertex markers are hidden AND their colliders are disabled — clicks pass through to ground (so you can place new vertices behind where markers were). Toggle back on to interact with existing vertices.")]
        public bool ShowVertexMarkers = true;

        [Header("Edge preview")]
        public float PreviewLineWidth = 0.5f;
        public Color PreviewLineColor = new Color(0.5f, 0.85f, 1f, 0.8f);

        [Header("Ghost puck (Create-mode placement preview)")]
        [Tooltip("Opacity of the translucent puck shown at the snapped cursor in Create mode. 0 hides it completely.")]
        [Range(0f, 1f)] public float GhostPuckAlpha = 0.35f;

        [Header("Range circle (Create-mode visual ruler)")]
        [Tooltip("Show a dashed circle around the cursor in Create mode. Purely visual — doesn't drive any logic.")]
        public bool RangeCircleEnabled = true;
        [Tooltip("Radius (m) of the ghost range circle.")]
        public float RangeCircleRadius = 10f;
        [Tooltip("Line width (m) of the circle stroke.")]
        public float RangeCircleWidth = 0.06f;
        [Tooltip("Length (m) of one dash + gap cycle around the circle.")]
        public float RangeCircleDashPeriod = 0.6f;
        public Color RangeCircleColor = new Color(0.95f, 0.85f, 0.2f, 1f);
        [Tooltip("Opacity (alpha, 0–1) of the range circle. Overrides the color picker's alpha so you can tune it from a single slider.")]
        [Range(0f, 1f)] public float RangeCircleOpacity = 0.55f;

        [Header("Snap guides")]
        [Tooltip("Maximum distance (m) from the cursor to a guide line for the guide to win the snap. When in range, the guide overrides the grid snap.")]
        public float GuideSnapDistance = 0.5f;
        [Tooltip("Width (m) of the visualized active guide line.")]
        public float GuideLineWidth = 0.05f;
        [Tooltip("Length (m) of one dash + gap cycle along the guide line.")]
        public float GuideDashPeriod = 0.6f;
        public Color GuideLineColor = new Color(1f, 0.4f, 0.8f, 0.5f);

        [Header("Snap guides — Angle (from edge anchor)")]
        [Tooltip("When drawing an edge, snap the next endpoint to multiples of this many degrees off of each existing edge meeting at the anchor. Set 0 to disable.")]
        public float AngleSnapDegrees = 15f;
        [Tooltip("Pull radius (m) for angle snap specifically. Typically larger than the generic guide radius so angle alignments feel sticky while drawing.")]
        public float AngleSnapDistance = 2f;

        [Header("Snap guides — Proximity (vertices near the cursor)")]
        [Tooltip("Radius (m) within which an existing vertex's incident edges extend an extension guide for the cursor to snap to. 0 disables this provider.")]
        public float ExtensionGuideRange = 25f;
        [Tooltip("Radius (m) within which an existing vertex's incident edges extend a perpendicular guide for the cursor to snap to. 0 disables this provider.")]
        public float PerpendicularGuideRange = 100f;

        [Header("Topology snap")]
        [Tooltip("If a new edge passes within this distance (m) of an existing vertex, " +
                 "the edge is split at that vertex (reusing it) instead of crossing through. " +
                 "0 = disabled.")]
        public float VertexOnEdgeTolerance = 0.25f;
        [Tooltip("If a placement click lands within this distance (m) of an existing edge, " +
                 "the edge is split at the projected point and the new vertex becomes the " +
                 "split point. 0 = disabled (clicks always go to the cursor position).")]
        public float EdgeClickTolerance = 0.5f;
        [Tooltip("When deleting a vertex with exactly 2 incident edges, merge them into a " +
                 "single edge if the angle between them is within this many degrees of " +
                 "straight (180°). 0 = disabled (always destroy vertex + edges).")]
        public float CollinearMergeAngleDeg = 8f;

        [Header("Default profile (used for new roads)")]
        public int DefaultAbLanes = 1;
        public int DefaultBaLanes = 1;
        public float DefaultLaneWidth = 4f;
        public float DefaultShoulderWidth = 1f;

        NetworkRenderer _renderer;
        Network _network;
        public Network Network => _network;

        // Autosave debounce. Set by MarkDirty; consumed by Update to time
        // the actual disk write so a slider drag or vertex drag doesn't
        // hammer the file system every frame.
        float _autosaveDirtySinceRealtime = -1f;

        // Create-mode state (Straight tool)
        BuildState _buildState = BuildState.Idle;
        Vertex _edgeAnchor;

        // Create-mode state (Fillet tool)
        FilletState _filletState = FilletState.Idle;
        Vertex _filletStart;
        Vector2 _filletCorner;

        // Edit-mode state
        Vertex _draggedVertex;

        // Edit-mode hover highlight: a single GameObject (lazy-spawned)
        // whose mesh + transform are set to mirror the currently hovered
        // road body or intersection asphalt. Disabled when nothing is
        // hovered. Yellow translucent unlit material via the
        // NetworkDesigner/EditorHighlight shader.
        GameObject _hoverHighlightGo;
        Material _hoverHighlightMat;
        NetworkRoad _hoverRoad;
        Vertex _hoverIntersectionVertex;
        [Header("Edit-mode hover highlight")]
        public Color HoverHighlightColor = new Color(1f, 0.9f, 0f, 0.25f);
        [Tooltip("Y offset applied to the hover-highlight mesh so it sits just above the road/intersection asphalt without z-fighting.")]
        public float HoverHighlightLift = 0.02f;

        // Edit-mode vertex selection + setback handles
        Vertex _selectedVertex;
        SetbackHandle _draggedHandle;
        // Spawned handle GameObjects keyed by "<roadId>:<end>" so we can
        // update positions in place when a Rebuild fires.
        readonly Dictionary<string, GameObject> _setbackHandles = new Dictionary<string, GameObject>();

        // Lateral-offset drag handles — one per (road, end) at the
        // selected vertex. Drag perpendicular to the road's outward
        // direction → writes NetworkRoad.LateralOffsetA/B.
        readonly Dictionary<string, GameObject> _lateralOffsetHandles = new Dictionary<string, GameObject>();
        LateralOffsetHandle _draggedLateralHandle;
        [Header("Lateral-offset handle (perpendicular endpoint shift)")]
        public float LateralOffsetHandleDiameter = 1.4f;
        public float LateralOffsetHandleRingThickness = 0.25f;
        public float LateralOffsetHandleHeight = 1.5f;
        public Color LateralOffsetHandleColor = new Color(0.45f, 0.85f, 1f, 0.95f);
        public Color LateralOffsetHandleActiveColor = new Color(0.2f, 0.7f, 1f, 1f);
        [Tooltip("How far INTO the road body (along its outward direction at this end) the lateral-offset handle is drawn, so neighboring approaches' handles don't pile up at the vertex. Drag math is unchanged — only the displayed position shifts.")]
        public float LateralOffsetHandleInwardOffset = 4f;
        // Shared unlit/vertex-color/transparent material used for every
        // edit-mode overlay primitive (setback rings + stems, lane
        // marker rings, lane-flow arrows). One material = good batching.
        Material _editorOverlayMaterial;

        // Edit-mode lane-flow override editor. Spawns one clickable
        // marker per lane endpoint when a vertex is selected. _armedLane
        // holds the currently-armed "from" lane while the user is
        // mid-edit (waiting for the second click).
        readonly Dictionary<string, GameObject> _laneEndpointMarkers = new Dictionary<string, GameObject>();
        LaneEndpointMarker _armedLaneEndpoint;
        // Hovered lane marker (Edit mode, vertex selected). Drives the
        // per-frame alpha lift on the marker + its flow arrows.
        LaneEndpointMarker _hoverLaneEndpoint;
        // Modal "intersection lane marking" mode — toggled by holding
        // Shift in Edit mode. While on, the midpoint A/B markers are
        // hidden and the smaller Origin/Primary/Secondary/Tertiary
        // corner markers are shown for marking authoring. Released
        // shift reverts to the regular connectivity-edit overlay.
        bool _markingShiftMode;

        // Hover (works in both modes; shown as a third marker state).
        Vertex _hoverVertex;

        // Visuals
        readonly Dictionary<string, GameObject> _vertexMarkers = new Dictionary<string, GameObject>();
        GameObject _previewLineGo;
        LineRenderer _previewLine;
        Material _markerMaterial;
        Material _markerHoverMaterial;
        Material _markerActiveMaterial;

        GameObject _ghostPuckGo;
        Material _ghostPuckMaterial;

        // Roundabout-tool ghost: three concentric circles at the cursor
        // (or hovered vertex) showing the road's outer edge, centerline,
        // and inner edge — so the user sees the actual footprint, not
        // just the centerline.
        GameObject _roundaboutGhostGo;
        LineRenderer _roundaboutGhostCenter;
        LineRenderer _roundaboutGhostOuter;
        LineRenderer _roundaboutGhostInner;
        Material _roundaboutGhostMaterial;

        // Create-mode dashed range circle. Visual ruler — doesn't drive logic.
        GameObject _rangeCircleGo;
        LineRenderer _rangeCircleLine;
        Material _rangeCircleMaterial;

        // Dashed construction guideline for the fillet tool. During
        // AwaitingClick2 it's a single segment (click-1 → cursor); during
        // AwaitingClick3 it's a 3-point polyline (click-1 → corner → cursor),
        // independent of the solid curve preview that lives on _previewLine.
        GameObject _filletGuideLineGo;
        LineRenderer _filletGuideLine;
        Material _filletGuideLineMaterial;

        // Snap-guide state. Populated each frame by CollectGuides; consumed
        // by ApplyGuidesOrGrid inside PickGround. _activeGuide tracks the
        // single guide currently being snapped to so we can visualize it.
        struct GuideRay
        {
            public Vector2 Origin;
            public Vector2 Direction; // unit
            // Per-candidate pull radius (m). Lets each provider tune its
            // own "stickiness" — angle snap is typically larger than the
            // proximity providers' radius.
            public float Distance;
            // If true, the snap check uses the grid-snapped cursor instead
            // of the raw mouse cursor. Proximity providers turn this on so
            // the snap result is stable across sub-grid mouse movements
            // (it stays "tied to the puck"). Angle snap leaves it off so
            // the mouse smoothly pulls onto the fan ray.
            public bool TestAgainstSnappedCursor;
        }
        readonly List<GuideRay> _guideCandidates = new List<GuideRay>(64);
        // All guides tied for closest distance to the cursor. Snap point
        // is taken from the first; the rest are drawn for clarity.
        readonly List<GuideRay> _activeGuides = new List<GuideRay>(4);
        // Pool of LineRenderer GameObjects, one per drawn active guide.
        // Grows as needed; unused entries are hidden (enabled=false).
        readonly List<GameObject> _guideLineGos = new List<GameObject>(4);
        readonly List<LineRenderer> _guideLines = new List<LineRenderer>(4);
        Material _guideLineMaterial;
        Texture2D _guideDashTexture;

        void OnEnable()
        {
            _renderer = GetComponent<NetworkRenderer>();
            if (PickCamera == null) PickCamera = Camera.main;
            if (Grid == null) Grid = FindFirstObjectByType<GroundGrid>();

            if (PickCamera == null)
            {
                Debug.LogError("[NetworkDesigner] No PickCamera assigned and Camera.main returned null. " +
                               "Make sure your scene has a Camera tagged 'MainCamera', or assign PickCamera in the Inspector.");
            }
            else
            {
                // Suppress the orbit-camera's scroll-zoom while the cursor
                // is over the palette so the palette's scroll view (and
                // potentially in-palette scroll handlers) get the wheel.
                OrbitCameraController orbit = PickCamera.GetComponent<OrbitCameraController>();
                if (orbit != null) orbit.ScrollSuppressor = MouseOverPalette;
            }

            // Try to load the autosaved network. Falls through to an empty
            // default if no file exists or parsing fails.
            int loadedVertexCount = 0;
            if (Autosave && _network == null)
            {
                _network = TryLoadNetwork(out loadedVertexCount);
            }

            if (_network == null)
            {
                _network = new Network
                {
                    DriveSide = DriveSide,
                    Vertices = new List<Vertex>(),
                    Roads = new List<NetworkRoad>(),
                };
            }

            // Spawn vertex markers for any loaded vertices.
            foreach (Vertex v in _network.Vertices)
            {
                SpawnVertexMarker(v);
            }

            _renderer.Network = _network;
            _renderer.Rebuild();

            ReloadConfigs();

            Debug.Log($"[NetworkDesigner] Initialized. {loadedVertexCount} vertices loaded from autosave. " +
                      $"Build-mode ready. Left-click ground to place a vertex.");
        }

        // -----------------------------------------------------------------
        // Road config palette
        // -----------------------------------------------------------------

        // Configs loaded from road-config.json. Re-read on demand via the
        // palette's Reload button so the user can re-export from the React
        // app without restarting Play mode.
        List<SavedConfig> _configs = new List<SavedConfig>();
        string _activeCategory; // null until first ReloadConfigs picks one
        string _activeConfigId; // tracks which button gets highlighted

        // Lazy cache of palette icons (config Name → Texture2D). A null
        // value means "looked it up, no icon exists" — distinguishes from
        // "not yet looked up." Cleared on ReloadConfigs so renamed configs
        // re-resolve their icons.
        Dictionary<string, Texture2D> _configIconCache = new Dictionary<string, Texture2D>();

        public void ReloadConfigs()
        {
            string path = ResolveRoadConfigPath();
            if (!System.IO.File.Exists(path))
            {
                _configs = new List<SavedConfig>();
                _configIconCache.Clear();
                Debug.LogWarning($"[NetworkDesigner] road-config.json not found at '{path}'. Palette will be empty.");
                return;
            }
            try
            {
                ExportedConfigFile file = ConfigImporter.LoadFromFile(path);
                _configs = file.Configs ?? new List<SavedConfig>();
                _configIconCache.Clear();
                // Default the active category to the first one in the list
                // so the palette opens to something useful instead of blank.
                // Pick a default tab if we don't have one, or if the
                // previously-selected category no longer has any entries
                // (e.g., the user renamed it and re-exported).
                if (string.IsNullOrEmpty(_activeCategory) ||
                    ConfigsInCategory(_activeCategory).Count == 0)
                {
                    _activeCategory = FirstCategoryWithEntries();
                }
                Debug.Log($"[NetworkDesigner] Loaded {_configs.Count} road config(s) from '{path}'.");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[NetworkDesigner] Failed to load road configs from '{path}': {ex.Message}");
                _configs = new List<SavedConfig>();
                _configIconCache.Clear();
            }
        }

        string ResolveRoadConfigPath()
        {
            if (string.IsNullOrEmpty(RoadConfigPath)) return "";
            if (System.IO.Path.IsPathRooted(RoadConfigPath)) return RoadConfigPath;
            return System.IO.Path.Combine(Application.dataPath, "..", RoadConfigPath);
        }

        // Normalized category key — empty/whitespace collapses to a
        // shared "Uncategorized" bucket.
        static string CategoryKey(SavedConfig c)
        {
            string s = c?.Category;
            if (string.IsNullOrWhiteSpace(s)) return "Uncategorized";
            return s.Trim();
        }

        List<string> AllCategoriesSorted()
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (SavedConfig c in _configs) seen.Add(CategoryKey(c));
            List<string> list = new List<string>(seen);
            // "Uncategorized" floats to the end so named categories come first.
            list.Sort((a, b) =>
            {
                if (a == "Uncategorized" && b != "Uncategorized") return 1;
                if (b == "Uncategorized" && a != "Uncategorized") return -1;
                return string.Compare(a, b, System.StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        List<SavedConfig> ConfigsInCategory(string category)
        {
            List<SavedConfig> list = new List<SavedConfig>();
            foreach (SavedConfig c in _configs)
            {
                if (CategoryKey(c) == category) list.Add(c);
            }
            list.Sort((a, b) => string.Compare(
                (a.Name ?? ""), (b.Name ?? ""), System.StringComparison.OrdinalIgnoreCase));
            return list;
        }

        string FirstCategoryWithEntries()
        {
            List<string> all = AllCategoriesSorted();
            return all.Count > 0 ? all[0] : null;
        }

        // Resolve a palette icon for a config. Looks under
        // Resources/icons/buttons/roads/{Name}.png and caches both hits
        // and misses (null) so missing icons don't repeatedly Resources.Load
        // each OnGUI frame.
        Texture2D GetConfigIcon(string configName)
        {
            if (string.IsNullOrEmpty(configName)) return null;
            if (_configIconCache.TryGetValue(configName, out Texture2D cached)) return cached;
            Texture2D tex = Resources.Load<Texture2D>("icons/buttons/roads/" + configName);
            _configIconCache[configName] = tex;
            return tex;
        }

        void OnConfigButtonClicked(SavedConfig c)
        {
            if (c == null || c.Road == null) return;
            SetActiveProfile(c.Road);
            _activeConfigId = c.Id;
            Debug.Log($"[NetworkDesigner] Active profile → '{c.Name}' " +
                      $"(category: {CategoryKey(c)}, id: {c.Id}).");
        }

        void OnDisable()
        {
            // Flush any pending changes before the play session ends.
            if (Autosave && _autosaveDirtySinceRealtime > 0f)
            {
                SaveNetwork();
                _autosaveDirtySinceRealtime = -1f;
            }

            // Drop the scroll-suppressor delegate so the camera doesn't
            // call back into a disabled/destroyed designer.
            if (PickCamera != null)
            {
                OrbitCameraController orbit = PickCamera.GetComponent<OrbitCameraController>();
                if (orbit != null && orbit.ScrollSuppressor == (System.Func<bool>)MouseOverPalette)
                    orbit.ScrollSuppressor = null;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(ModeToggleKey)) ToggleMode();
            if (Input.GetKeyDown(ToolToggleKey)) ToggleTool();
            if (Input.GetKeyDown(RoundaboutHotkey)) TryRoundaboutHotkey();
            if (Input.GetKeyDown(VertexViewToggleHotkey)) SetVertexMarkersVisible(!ShowVertexMarkers);

            // Direct tool select (Create mode only). Edit mode ignores
            // these so number keys stay free for future Edit-mode actions.
            if (CurrentMode == DesignerMode.Create)
            {
                if (Input.GetKeyDown(ToolHotkeyStraight))   SelectTool(BuildTool.Straight);
                if (Input.GetKeyDown(ToolHotkeyFillet))     SelectTool(BuildTool.Fillet);
                if (Input.GetKeyDown(ToolHotkeySCurve))     SelectTool(BuildTool.SCurve);
                if (Input.GetKeyDown(ToolHotkeyRoundabout)) SelectTool(BuildTool.Roundabout);
                if (Input.GetKeyDown(ToolHotkeyCulDeSac))   SelectTool(BuildTool.CulDeSac);
            }

            // Hover detection runs in both modes so the user always gets
            // visual feedback for what they're about to click.
            UpdateHover();
            UpdateLaneHover();
            UpdateRoadIntersectionHover();
            UpdateMarkingShiftMode();

            if (CurrentMode == DesignerMode.Create) HandleCreateInput();
            else HandleEditInput();

            // Runs unconditionally so the ghost hides correctly the
            // frame the user toggles out of Create mode.
            UpdateGhostPuck();

            // Mirror the ghost: the visualizer reads _activeGuide which is
            // set as a side effect of any PickGround call this frame
            // (preview line, ghost puck, or hover).
            UpdateGuideLineVisual();

            // Dashed construction guideline that follows the ghost during
            // fillet creation (click-1 → cursor, or click-1 → corner → cursor).
            UpdateFilletGuideLine();

            // Roundabout-tool ghost circle.
            UpdateRoundaboutGhost();

            // Create-mode dashed range circle.
            UpdateRangeCircle();

            // Debounced autosave flush.
            if (Autosave && _autosaveDirtySinceRealtime > 0f
                && Time.realtimeSinceStartup - _autosaveDirtySinceRealtime >= AutosaveDebounceSeconds)
            {
                SaveNetwork();
                _autosaveDirtySinceRealtime = -1f;
            }
        }

        // Track which vertex is under the cursor and refresh its material
        // (and the previously-hovered vertex's material) when that changes.
        // We don't refresh every frame — only on transitions — because the
        // active/anchor states need to win over hover and we don't want to
        // clobber them.
        void UpdateHover()
        {
            // While editing a specific vertex, suppress hover-recoloring
            // of OTHER vertex markers so the user isn't visually pulled
            // off the vertex they're working on.
            Vertex newHover = (CurrentMode == DesignerMode.Edit && _selectedVertex != null)
                ? null
                : PickVertex();
            if (newHover == _hoverVertex) return;
            Vertex old = _hoverVertex;
            _hoverVertex = newHover;
            if (old != null) RefreshMarker(old);
            if (newHover != null) RefreshMarker(newHover);
        }

        // Per-frame hover detection for lane endpoint markers. Only
        // active in Edit mode with a vertex selected. On hover change,
        // recolors the affected markers and pushes the new state to
        // the LaneFlowRenderer so its arrows dim/pop in step.
        void UpdateLaneHover()
        {
            if (CurrentMode != DesignerMode.Edit || _selectedVertex == null)
            {
                if (_hoverLaneEndpoint != null)
                {
                    LaneEndpointMarker prev = _hoverLaneEndpoint;
                    _hoverLaneEndpoint = null;
                    RefreshLaneMarkerColor(prev, GetLaneMarkerAmbientColor(prev));
                    PushLaneFlowState();
                }
                return;
            }
            LaneEndpointMarker newHover = PickLaneEndpointMarker();
            if (newHover == _hoverLaneEndpoint) return;

            LaneEndpointMarker oldHover = _hoverLaneEndpoint;
            _hoverLaneEndpoint = newHover;
            // Restore ambient color on the old hovered marker (unless
            // it's the armed one — armed stays at full alpha).
            if (oldHover != null && oldHover != _armedLaneEndpoint)
            {
                RefreshLaneMarkerColor(oldHover, GetLaneMarkerAmbientColor(oldHover));
            }
            // Pop the new hovered marker to full alpha.
            if (newHover != null)
            {
                bool isCorner = newHover.Node != LaneNode.A && newHover.Node != LaneNode.B;
                Color c;
                if (newHover == _armedLaneEndpoint) c = LaneMarkerArmedColor;
                else if (newHover.IsInbound && isCorner) c = GetLaneCornerLineColor(newHover.Lane, newHover.Node);
                else if (newHover.IsInbound) c = EditorGeometry.HashToColor(LaneFlowKey(newHover.Lane));
                else c = LaneOutboundMarkerColor;
                c.a = LaneOverlayFullAlpha;
                RefreshLaneMarkerColor(newHover, c);
            }
            PushLaneFlowState();
        }

        // Edit-mode only: when the cursor is over a road body or
        // intersection asphalt, mirror that mesh into a yellow
        // translucent overlay GameObject (`_hoverHighlightGo`) so the
        // user sees what their next click would target. Only one
        // highlight is shown at a time (vertex marker hover > road >
        // intersection in pick priority). Vertex-marker hover already
        // has its own marker recolor — we don't double-highlight.
        void UpdateRoadIntersectionHover()
        {
            if (CurrentMode != DesignerMode.Edit || PickCamera == null)
            {
                ClearHoverHighlight();
                return;
            }

            // While editing a specific vertex, suppress the road/
            // intersection hover overlay — the user is focused on the
            // selected vertex's per-vertex overlays (setback handles,
            // lane endpoint markers) and stray asphalt highlights from
            // moving the cursor around the scene are pure noise.
            if (_selectedVertex != null)
            {
                ClearHoverHighlight();
                return;
            }

            // If a vertex marker is being hovered, the marker hover
            // recolor already conveys the selection — skip the asphalt
            // overlay so we don't stack two indicators.
            if (_hoverVertex != null)
            {
                ClearHoverHighlight();
                return;
            }

            // RaycastAll so we can pick the road body / intersection
            // asphalt even when an overlay collider (lane marker, sign,
            // setback handle) sits along the same ray. Prefer
            // road/intersection hits with the smallest distance.
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
            NetworkRoad hitRoad = null;
            Vertex hitVertex = null;
            GameObject hitGo = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit h = hits[i];
                RoadMarker rm = h.collider.GetComponentInParent<RoadMarker>();
                if (rm != null && rm.Road != null)
                {
                    if (h.distance < bestDist)
                    {
                        bestDist = h.distance;
                        hitRoad = rm.Road;
                        hitVertex = null;
                        hitGo = h.collider.gameObject;
                    }
                    continue;
                }
                IntersectionMarker im = h.collider.GetComponentInParent<IntersectionMarker>();
                if (im != null && im.Vertex != null)
                {
                    if (h.distance < bestDist)
                    {
                        bestDist = h.distance;
                        hitRoad = null;
                        hitVertex = im.Vertex;
                        hitGo = h.collider.gameObject;
                    }
                }
            }

            if (hitRoad == null && hitVertex == null)
            {
                ClearHoverHighlight();
                return;
            }

            // Same target as last frame → keep the highlight as-is
            // (don't rebind the mesh every frame; that thrashes the
            // collider/renderer state for no visual benefit).
            if (hitRoad != null && hitRoad == _hoverRoad) return;
            if (hitVertex != null && hitVertex == _hoverIntersectionVertex) return;

            _hoverRoad = hitRoad;
            _hoverIntersectionVertex = hitVertex;
            ShowHoverHighlight(hitGo);
        }

        void ClearHoverHighlight()
        {
            _hoverRoad = null;
            _hoverIntersectionVertex = null;
            if (_hoverHighlightGo != null) _hoverHighlightGo.SetActive(false);
        }

        void ShowHoverHighlight(GameObject sourceGo)
        {
            if (sourceGo == null) { ClearHoverHighlight(); return; }
            MeshFilter srcMf = sourceGo.GetComponent<MeshFilter>();
            if (srcMf == null || srcMf.sharedMesh == null) { ClearHoverHighlight(); return; }

            EnsureHoverHighlightGo();
            _hoverHighlightGo.SetActive(true);

            MeshFilter mf = _hoverHighlightGo.GetComponent<MeshFilter>();
            mf.sharedMesh = srcMf.sharedMesh;

            // Match source transform; lift slightly so the overlay
            // draws cleanly above the underlying asphalt and any other
            // overlays at the same Y (e.g. lane markings render-queue).
            Transform st = sourceGo.transform;
            Transform ht = _hoverHighlightGo.transform;
            ht.position = st.position + new Vector3(0f, HoverHighlightLift, 0f);
            ht.rotation = st.rotation;
            ht.localScale = st.lossyScale;

            // Materials array: one entry per submesh, but ONLY index 0
            // (the asphalt submesh in both RoadRenderer + IntersectionRenderer)
            // gets a non-null material. Higher submeshes (lane markings,
            // shoulder strips, arrows, etc.) are left unrendered so the
            // highlight only paints the asphalt body shape.
            int subCount = srcMf.sharedMesh.subMeshCount;
            Material[] mats = new Material[subCount];
            mats[0] = GetHoverHighlightMaterial();
            // (mats[1..subCount-1] stay null → submeshes skipped)
            MeshRenderer mr = _hoverHighlightGo.GetComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
        }

        void EnsureHoverHighlightGo()
        {
            if (_hoverHighlightGo != null) return;
            _hoverHighlightGo = new GameObject("EditHoverHighlight");
            _hoverHighlightGo.transform.SetParent(transform, worldPositionStays: false);
            _hoverHighlightGo.AddComponent<MeshFilter>();
            MeshRenderer mr = _hoverHighlightGo.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _hoverHighlightGo.SetActive(false);
        }

        Material GetHoverHighlightMaterial()
        {
            if (_hoverHighlightMat == null)
            {
                Shader sh = Shader.Find("NetworkDesigner/EditorHighlight");
                if (sh == null)
                {
                    Debug.LogWarning("[NetworkDesigner] EditorHighlight shader missing — hover highlight will use Sprites/Default fallback.");
                    sh = Shader.Find("Sprites/Default");
                }
                _hoverHighlightMat = new Material(sh) { name = "EditHoverHighlightMat" };
            }
            _hoverHighlightMat.color = HoverHighlightColor;
            return _hoverHighlightMat;
        }

        // Recompute a vertex marker's material from its current state.
        // Priority: anchor / dragged (active) > hover > default.
        void RefreshMarker(Vertex v)
        {
            if (v == null) return;
            if (!_vertexMarkers.TryGetValue(v.Id, out GameObject go) || go == null) return;
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return;
            bool isActive = (v == _edgeAnchor) || (v == _draggedVertex);
            bool isHover = (v == _hoverVertex);
            Material mat = isActive
                ? GetMarkerActiveMaterial()
                : (isHover ? GetMarkerHoverMaterial() : GetMarkerMaterial());
            mr.sharedMaterial = mat;
        }

        void ToggleMode()
        {
            SetMode(CurrentMode == DesignerMode.Create ? DesignerMode.Edit : DesignerMode.Create);
        }

        // Idempotent mode setter. Cancels any in-progress operation when
        // actually switching so we don't leave dangling state (preview
        // line, dragged vertex, in-progress fillet, selected vertex + handles).
        void SetMode(DesignerMode m)
        {
            if (m == CurrentMode) return;
            CancelEdgeOperation();
            CancelFillet();
            EndDrag();
            EndHandleDrag();
            SetSelectedVertex(null);
            ClearHoverHighlight();
            CurrentMode = m;
            // Default vertex view per mode: pucks ON in Create (so the
            // user can see what they're connecting), OFF in Edit (so
            // they don't occlude the hover highlight + per-vertex
            // edit overlays). User can still toggle with V at any time.
            SetVertexMarkersVisible(m == DesignerMode.Create);
            Debug.Log($"[NetworkDesigner] Mode → {CurrentMode}");
        }

        void ToggleTool()
        {
            // Cycle: Straight → Fillet → SCurve → Roundabout → CulDeSac → Straight.
            BuildTool next;
            switch (CurrentTool)
            {
                case BuildTool.Straight:   next = BuildTool.Fillet; break;
                case BuildTool.Fillet:     next = BuildTool.SCurve; break;
                case BuildTool.SCurve:     next = BuildTool.Roundabout; break;
                case BuildTool.Roundabout: next = BuildTool.CulDeSac; break;
                default:                   next = BuildTool.Straight; break;
            }
            SelectTool(next);
        }

        // Idempotent tool selector. Cancels any in-progress click chain
        // for the current tool so the next click starts the new tool fresh.
        void SelectTool(BuildTool t)
        {
            if (t == CurrentTool) return;
            CancelEdgeOperation();
            CancelFillet();
            CurrentTool = t;
            Debug.Log($"[NetworkDesigner] Tool → {CurrentTool}");
        }

        // -----------------------------------------------------------------
        // Create-mode input
        // -----------------------------------------------------------------

        void HandleCreateInput()
        {
            UpdatePreviewLine();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelEdgeOperation();
                CancelFillet();
            }

            // Roundabout-tool-only: +/- adjust radius. Step = grid spacing
            // when snap-to-grid is on, otherwise 1m. Listen for both the
            // main-row keys and the keypad equivalents.
            if (CurrentTool == BuildTool.Roundabout)
            {
                if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus)
                    || Input.GetKeyDown(KeyCode.KeypadPlus))
                {
                    AdjustRoundaboutRadius(+1);
                }
                if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                {
                    AdjustRoundaboutRadius(-1);
                }
            }

            if (Input.GetMouseButtonDown(0) && !MouseOverPalette())
            {
                if (CurrentTool == BuildTool.Straight)
                {
                    Vertex hitVertex = PickVertex();
                    if (hitVertex != null) OnVertexClicked(hitVertex);
                    else
                    {
                        Vector2? ground = PickGround();
                        if (ground.HasValue) OnGroundClicked(ground.Value);
                    }
                }
                else if (CurrentTool == BuildTool.Roundabout)
                {
                    HandleRoundaboutClick();
                }
                else if (CurrentTool == BuildTool.CulDeSac)
                {
                    HandleCulDeSacClick();
                }
                else // Fillet or SCurve — same 3-click state machine
                {
                    HandleFilletClick();
                }
            }

            if (Input.GetMouseButtonDown(1) && !MouseOverPalette())
            {
                HandleRightClickDelete();
            }
        }

        void AdjustRoundaboutRadius(int direction)
        {
            float step = 1f;
            if (SnapToGrid && Grid != null && Grid.Enabled && Grid.Spacing > 0f)
            {
                step = Grid.Spacing;
            }
            RoundaboutRadius = Mathf.Max(2f, RoundaboutRadius + direction * step);
        }

        void HandleRoundaboutClick()
        {
            Vertex hit = PickVertex();
            if (hit != null)
            {
                ConvertVertexToRoundabout(hit);
                return;
            }
            Vector2? ground = PickGround();
            if (ground.HasValue)
            {
                CreateRoundaboutAt(ground.Value);
            }
        }

        // Cul-de-sac tool entry: click an existing dead-end vertex (a
        // vertex with exactly one incident road) to convert it into a
        // teardrop turnaround. Clicks on non-dead-end vertices or on
        // empty ground log a warning and no-op.
        void HandleCulDeSacClick()
        {
            Vertex hit = PickVertex();
            if (hit == null)
            {
                Debug.Log("[NetworkDesigner] Cul-de-sac: click a dead-end vertex (a vertex with exactly one incident road).");
                return;
            }
            List<NetworkRoad> incident = new List<NetworkRoad>();
            foreach (NetworkRoad r in _network.Roads)
            {
                if (r.EndA == hit.Id || r.EndB == hit.Id) incident.Add(r);
            }
            if (incident.Count != 1)
            {
                Debug.LogWarning($"[NetworkDesigner] Cul-de-sac refused at vertex '{hit.Id}': needs exactly 1 incident road, found {incident.Count}.");
                return;
            }
            if (incident[0].Curve != null)
            {
                Debug.LogWarning($"[NetworkDesigner] Cul-de-sac refused at vertex '{hit.Id}': approach road '{incident[0].Id}' is curved. " +
                                 "Only straight dead-end roads are supported (curved approach + offset bulb produces warped geometry). " +
                                 "Tip: this usually means the dead-end is already part of a roundabout ring — pick a different vertex.");
                return;
            }
            BuildCulDeSac(hit, incident[0]);
        }

        // Build a teardrop cul-de-sac at the dead-end vertex. The dead-end
        // vertex is PRESERVED — it becomes the bulb's entry ring vertex.
        // The bulb center sits perpendicular to the road direction (at
        // radius distance to the chosen side), so the ring is tangent to
        // the road direction at the dead-end → the road smoothly enters
        // the bulb without bending. A CulDeSacBulb entry is also added to
        // the network so the renderer fills the bulb interior with
        // asphalt (no median/island).
        //
        // Does NOT call BuildRoundabout — that one deletes the
        // centerToDelete vertex and reconnects the road to a new ring
        // vertex elsewhere, which moves the user's clicked vertex.
        void BuildCulDeSac(Vertex deadEnd, NetworkRoad road)
        {
            string otherId = road.EndA == deadEnd.Id ? road.EndB : road.EndA;
            RoadEnd end = road.EndA == deadEnd.Id ? RoadEnd.A : RoadEnd.B;
            Vertex other = FindVertexById(otherId);
            if (other == null)
            {
                Debug.LogWarning($"[NetworkDesigner] Cul-de-sac refused: road '{road.Id}' has no other endpoint.");
                return;
            }

            Vector2 outward = GeometryResolver.OutwardDirection(road, end, deadEnd, other);
            if (outward.sqrMagnitude < 1e-6f)
            {
                Debug.LogWarning($"[NetworkDesigner] Cul-de-sac refused: road '{road.Id}' has degenerate direction at vertex.");
                return;
            }
            // extend = direction past the dead-end, away from the road body.
            Vector2 extend = -outward;

            float halfWidth = road.Profile.TotalWidth * 0.5f;
            float bulbRadius = Mathf.Max(0.5f, halfWidth * CulDeSacWidthMultiplier);

            // Centered lollipop: bulb sits directly past the dead-end
            // along the road's extension. The dead-end stays on the ring
            // at its closest point to the road body. Ring tangent at the
            // dead-end is perpendicular to the road, so traffic going
            // AB approaches the bulb head-on and the ring carries it
            // left or right depending on DriveSide.
            Vector2 bulbCenter = deadEnd.Position + extend * bulbRadius;

            // Walk CCW around the bulb starting from the dead-end's
            // angle. Subdivide every ≤90° (same convention as
            // BuildRoundabout) so each cubic-bezier arc stays a clean
            // circle approximation.
            Vector2 deadEndRadial = (deadEnd.Position - bulbCenter).normalized;
            float startBearing = Mathf.Atan2(deadEndRadial.y, deadEndRadial.x);
            const float MAX_SEG_ARC = Mathf.PI / 2f;
            int subs = Mathf.CeilToInt(2f * Mathf.PI / MAX_SEG_ARC);
            float segArc = 2f * Mathf.PI / subs;

            // ringVerts[0] = the existing dead-end. The other (subs-1)
            // vertices are spawned around the bulb perimeter.
            Vertex[] ringVerts = new Vertex[subs];
            ringVerts[0] = deadEnd;
            for (int i = 1; i < subs; i++)
            {
                float bearing = startBearing + i * segArc;
                Vector2 pos = bulbCenter + new Vector2(Mathf.Cos(bearing), Mathf.Sin(bearing)) * bulbRadius;
                Vertex v = new Vertex
                {
                    Id = $"v-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    Position = pos,
                    ConnectivityOverrides = new List<LaneConnection>(),
                };
                _network.Vertices.Add(v);
                SpawnVertexMarker(v);
                ringVerts[i] = v;
            }

            RoadProfile ringProfile = BuildRingProfile(CulDeSacLanes);

            bool ccw = DriveSide == DriveSide.Right;
            for (int i = 0; i < subs; i++)
            {
                Vertex a = ringVerts[i];
                Vertex b = ringVerts[(i + 1) % subs];

                float arcAngle = ComputeArcCCW(a.Position - bulbCenter, b.Position - bulbCenter);
                float k = (4f / 3f) * Mathf.Tan(arcAngle * 0.25f) * bulbRadius;
                Vector2 aTangent = TangentOnCircle(a.Position, bulbCenter, ccw: true);
                Vector2 bTangent = TangentOnCircle(b.Position, bulbCenter, ccw: true);

                Vector2 c1 = a.Position + aTangent * k;
                Vector2 c2 = b.Position - bTangent * k;

                Vertex fromV, toV;
                Vector2 controlA, controlB;
                if (ccw)
                {
                    fromV = a; toV = b; controlA = c1; controlB = c2;
                }
                else
                {
                    fromV = b; toV = a; controlA = c2; controlB = c1;
                }

                NetworkRoad ringRoad = new NetworkRoad
                {
                    Id = $"r-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    EndA = fromV.Id,
                    EndB = toV.Id,
                    Classification = RoadClassification.Secondary,
                    Profile = CloneProfile(ringProfile),
                    Curve = new RoadCurve { ControlA = controlA, ControlB = controlB },
                };
                _network.Roads.Add(ringRoad);
            }

            // Record the filled interior so NetworkRenderer can spawn a
            // disc mesh that turns the ring into a solid bulb. Disc
            // radius extends past the ring centerline by the ring's
            // half-width, so the disc edge meets (and slightly overlaps)
            // the ring road body's outer edge — no visible shoulder/
            // grass gap between the disc and the lane asphalt.
            float ringHalfWidth = ringProfile.TotalWidth * 0.5f;
            _network.CulDeSacs.Add(new CulDeSacBulb
            {
                Id = $"cds-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Center = bulbCenter,
                Radius = bulbRadius + ringHalfWidth,
                EntryVertexId = deadEnd.Id,
            });

            // Tighten ring-arc setbacks: the resolver's auto setback for
            // curved roads includes CurvedSetbackMultiplier (1.5×) which
            // is too generous around a small bulb — the ring road body
            // gets shortened and leaves a visible gap into the bulb fill.
            // For each ring vertex, resolve the natural setback, then
            // write 0.5× as a per-end override on each CURVED incident
            // road (skips the straight approach so its setback is
            // untouched).
            foreach (Vertex rv in ringVerts)
            {
                VertexGeometry rvg = GeometryResolver.ResolveVertex(_network, rv);
                if (rvg == null) continue;
                foreach (VertexApproach app in rvg.Approaches)
                {
                    NetworkRoad rr = null;
                    foreach (NetworkRoad cand in _network.Roads)
                    {
                        if (cand.Id == app.RoadId) { rr = cand; break; }
                    }
                    if (rr == null || rr.Curve == null) continue;
                    float halved = app.Setback * 0.5f;
                    if (app.End == RoadEnd.A) rr.SetbackA = halved;
                    else rr.SetbackB = halved;
                }
            }

            MarkNetworkDirty();
            Rebuild();
            Debug.Log($"[NetworkDesigner] Cul-de-sac built at '{deadEnd.Id}': bulb r={bulbRadius:F1}m, " +
                      $"lanes={CulDeSacLanes}, dead-end preserved as entry ring vertex.");
        }

        // -----------------------------------------------------------------
        // Fillet tool (3-click cubic-bezier curve)
        // -----------------------------------------------------------------
        //
        // Click 1: start endpoint. Same picker as Straight tool — snaps
        //          to an existing vertex, splits an existing edge, or
        //          creates a fresh vertex at the snapped ground.
        // Click 2: the "corner" reference. No vertex created. Drives both
        //          control points; if click 2 lands on the tangent
        //          intersection of click 1's and click 3's incoming
        //          edges, the resulting curve is tangent-matched.
        // Click 3: end endpoint. Same picker as click 1. Emits one
        //          NetworkRoad with Curve.ControlA along (click1→click2)
        //          and Curve.ControlB along (click3→click2).

        void HandleFilletClick()
        {
            switch (_filletState)
            {
                case FilletState.Idle:
                    FilletClick1();
                    break;
                case FilletState.AwaitingClick2:
                    FilletClick2();
                    break;
                case FilletState.AwaitingClick3:
                    FilletClick3();
                    break;
            }
        }

        void FilletClick1()
        {
            Vertex hit = PickVertex();
            if (hit != null)
            {
                _filletStart = hit;
            }
            else
            {
                Vector2? ground = PickGround();
                if (!ground.HasValue) return;
                _filletStart = CreateOrSplitAtPos(ground.Value);
            }
            RefreshMarker(_filletStart);
            _filletState = FilletState.AwaitingClick2;
        }

        void FilletClick2()
        {
            // Click 2 may land on a vertex (use its position) or on the
            // ground (use the snapped ground position). It does NOT create
            // a vertex — it's just the corner reference for the bezier.
            Vector2 corner;
            Vertex hit = PickVertex();
            if (hit != null) corner = hit.Position;
            else
            {
                Vector2? ground = PickGround();
                if (!ground.HasValue) return;
                corner = ground.Value;
            }
            _filletCorner = corner;
            _filletState = FilletState.AwaitingClick3;
        }

        void FilletClick3()
        {
            Vertex hit = PickVertex();
            Vertex end;
            if (hit != null)
            {
                end = hit;
            }
            else
            {
                Vector2? ground = PickGround();
                if (!ground.HasValue) return;
                end = CreateOrSplitAtPos(ground.Value);
            }

            if (end == _filletStart)
            {
                CancelFillet();
                return;
            }

            if (CurrentTool == BuildTool.SCurve)
                EmitSCurveRoad(_filletStart, end, _filletCorner);
            else
                EmitFilletRoad(_filletStart, end, _filletCorner);
            CancelFillet();
            Rebuild();
        }

        // Build a cubic-bezier road from start through corner to end.
        // Control points sit at FilletLeverFraction along each tangent leg.
        void EmitFilletRoad(Vertex start, Vertex end, Vector2 corner)
        {
            float f = Mathf.Clamp(FilletLeverFraction, 0.05f, 0.95f);
            Vector2 controlA = Vector2.Lerp(start.Position, corner, f);
            Vector2 controlB = Vector2.Lerp(end.Position, corner, f);

            NetworkRoad road = new NetworkRoad
            {
                Id = $"r-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                EndA = start.Id,
                EndB = end.Id,
                Classification = RoadClassification.Secondary,
                Profile = BuildDefaultProfile(),
                Curve = new RoadCurve { ControlA = controlA, ControlB = controlB },
            };
            _network.Roads.Add(road);
            MarkNetworkDirty();
            Debug.Log($"[NetworkDesigner] Fillet road {road.Id}: " +
                      $"A({start.Id}) → B({end.Id}) via corner ({corner.x:F2}, {corner.y:F2}); " +
                      $"controlA=({controlA.x:F2},{controlA.y:F2}) " +
                      $"controlB=({controlB.x:F2},{controlB.y:F2})");
        }

        // S-curve construction: symmetric cubic with c1 = click 2 and
        // c2 = mirror of click 2 about the chord midpoint. The tangents at
        // both endpoints point in the same direction (click2 - start),
        // which is what makes the curve an S (not a corner). If click 2 is
        // placed along an existing edge's extension, the curve is tangent-
        // matched at the start; symmetric placement gives a clean
        // lane-shift S.
        void EmitSCurveRoad(Vertex start, Vertex end, Vector2 control)
        {
            Vector2 controlA = control;
            Vector2 controlB = start.Position + end.Position - control;

            NetworkRoad road = new NetworkRoad
            {
                Id = $"r-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                EndA = start.Id,
                EndB = end.Id,
                Classification = RoadClassification.Secondary,
                Profile = BuildDefaultProfile(),
                Curve = new RoadCurve { ControlA = controlA, ControlB = controlB },
            };
            _network.Roads.Add(road);
            MarkNetworkDirty();
            Debug.Log($"[NetworkDesigner] S-curve road {road.Id}: " +
                      $"A({start.Id}) → B({end.Id}) via control ({control.x:F2}, {control.y:F2}); " +
                      $"controlA=({controlA.x:F2},{controlA.y:F2}) " +
                      $"controlB=({controlB.x:F2},{controlB.y:F2})");
        }

        void CancelFillet()
        {
            if (_filletStart != null) RefreshMarker(_filletStart);
            _filletState = FilletState.Idle;
            _filletStart = null;
            _filletCorner = Vector2.zero;
        }

        // Helper used in several places to mean "in one of the 3-click
        // curve tools" (Fillet or SCurve, both of which share state).
        static bool IsCurveTool(BuildTool t) => t == BuildTool.Fillet || t == BuildTool.SCurve;

        // -----------------------------------------------------------------
        // Edit-mode input
        // -----------------------------------------------------------------

        void HandleEditInput()
        {
            // Esc: unarm a click-to-edit-in-progress first, otherwise deselect.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_armedLaneEndpoint != null) UnarmLaneEndpoint();
                else SetSelectedVertex(null);
            }

            // Begin drag on left-button down.
            // Priority: Alt+road → reverse direction. Otherwise:
            // sign > lane endpoint > marking > setback handle > vertex > ground.
            if (Input.GetMouseButtonDown(0) && !MouseOverPalette())
            {
                bool altHeld = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                if (altHeld)
                {
                    NetworkRoad hitR = PickRoad();
                    if (hitR != null)
                    {
                        ReverseRoadDirection(hitR);
                        return;
                    }
                }
                SignClickTarget hitSign = PickSignClickTarget();
                if (hitSign != null)
                {
                    HandleSignClick(hitSign);
                    return;
                }
                // While armed, prefer OUTBOUND hits — same affordance
                // logic as the right-click delete path (see picker
                // docstring). Without this, clicking near the boundary
                // between two markers (e.g. the other corner of the
                // armed lane vs an outbound on a neighboring lane) is
                // a coin flip and the user has to retry.
                LaneEndpointMarker hitLane = PickLaneEndpointMarker(preferOutbound: _armedLaneEndpoint != null);
                if (hitLane != null)
                {
                    HandleLaneEndpointClick(hitLane);
                }
                else if (_selectedVertex != null
                         && PickMarkingClickTarget() is MarkingClickTarget hitMark
                         && hitMark != null)
                {
                    // Only allow marking-style cycling while a vertex
                    // is in edit mode — otherwise a click anywhere near
                    // a painted marking on a non-edited intersection
                    // mutates state the user wasn't focused on.
                    CycleMarkingStyle(hitMark);
                }
                else
                {
                    LateralOffsetHandle hitLat = PickLateralOffsetHandle();
                    if (hitLat != null)
                    {
                        _draggedLateralHandle = hitLat;
                        // No active-color refresh for now — could mirror
                        // setback handle's RefreshHandleMaterial pattern
                        // if the visual distinction becomes important.
                    }
                    else
                    {
                    SetbackHandle hitHandle = PickSetbackHandle();
                    if (hitHandle != null)
                    {
                        _draggedHandle = hitHandle;
                        if (_setbackHandles.TryGetValue(HandleKey(hitHandle.Road.Id, hitHandle.End), out GameObject hgo))
                        {
                            RefreshHandleMaterial(hgo, active: true);
                        }
                    }
                    else
                    {
                        Vertex hit = PickVertex();
                        if (hit != null)
                        {
                            // Puck click is drag-only — does NOT enter
                            // vertex edit mode. Entering edit mode is
                            // reserved for clicking the intersection
                            // asphalt (see below). This keeps the two
                            // gestures distinct: "I want to move this
                            // vertex" vs "I want to edit this vertex".
                            _draggedVertex = hit;
                            RefreshMarker(hit);
                        }
                        else
                        {
                            // No puck hit. If the click landed on an
                            // intersection asphalt, select that vertex
                            // (no drag — pucks are likely hidden in
                            // Edit mode anyway). Same target the user
                            // saw highlighted via the hover overlay.
                            Vertex hitInt = PickIntersectionVertex();
                            if (hitInt != null)
                            {
                                SetSelectedVertex(hitInt);
                            }
                            else
                            {
                                // Click on empty ground: if a lane is armed,
                                // unarm (so the user can dismiss the click-
                                // to-edit gesture without losing the vertex
                                // selection). Otherwise no-op — Esc is the
                                // explicit deselect path. This prevents an
                                // accidental ground click from blowing away
                                // the entire edit context.
                                if (_armedLaneEndpoint != null) UnarmLaneEndpoint();
                            }
                        }
                    }
                    }
                }
            }

            // Drag a handle: project cursor onto the road's outward bearing
            // and write the projected distance as the setback override.
            if (_draggedLateralHandle != null && Input.GetMouseButton(0))
            {
                UpdateLateralOffsetDrag();
            }
            else if (_draggedHandle != null && Input.GetMouseButton(0))
            {
                UpdateHandleDrag();
            }
            // Drag a vertex: existing move behavior.
            else if (_draggedVertex != null && Input.GetMouseButton(0))
            {
                Vector2? ground = PickGround();
                if (ground.HasValue)
                {
                    _draggedVertex.Position = ground.Value;
                    if (_vertexMarkers.TryGetValue(_draggedVertex.Id, out GameObject marker) && marker != null)
                    {
                        marker.transform.position = new Vector3(ground.Value.x, MarkerHeight, ground.Value.y);
                    }
                    MarkNetworkDirty();
                    Rebuild();
                }
            }

            if (Input.GetMouseButtonUp(0))
            {
                EndHandleDrag();
                EndLateralOffsetDrag();
                EndDrag();
            }

            // Right-click priority. NOTE: deleting a flow arrow or
            // lane marking requires arming an inbound marker first,
            // then right-clicking the matching outbound marker. There
            // is no "right-click directly on the line" delete shortcut
            // — that gesture is reserved so a stray right-click can't
            // wipe paint by accident.
            //   1. Armed + clicked OUTBOUND marker → remove matching
            //      connection (regular) or marking (shift). Stays armed
            //      for repeated removals.
            //   2. Armed + anything else → unarm (no delete).
            //   3. Not armed + setback handle → clear override.
            //   4. Not armed + anything else → vertex/road delete.
            if (Input.GetMouseButtonDown(1) && !MouseOverPalette())
            {
                if (_armedLaneEndpoint != null)
                {
                    LaneEndpointMarker hitOut = PickLaneEndpointMarker(preferOutbound: true);
                    if (hitOut != null && !hitOut.IsInbound
                        && hitOut.VertexId == _selectedVertex?.Id)
                    {
                        HandleLaneEndpointRightClick(hitOut);
                    }
                    else
                    {
                        UnarmLaneEndpoint();
                    }
                }
                else
                {
                    SetbackHandle h = PickSetbackHandle();
                    if (h != null)
                    {
                        ClearSetbackOverride(h.Road, h.End);
                    }
                    else
                    {
                        HandleRightClickDelete();
                    }
                }
            }
        }

        // Right-click on an OUTBOUND lane marker while armed: removes
        // the corresponding connection/marking from the armed inbound.
        // In regular mode, removes a ConnectivityOverride. In shift
        // mode, removes a LaneMarking with matching nodes. Stays
        // armed so the user can remove multiple in sequence.
        void HandleLaneEndpointRightClick(LaneEndpointMarker m)
        {
            if (_selectedVertex == null || _armedLaneEndpoint == null) return;
            if (m.IsInbound) return;
            if (_markingShiftMode)
            {
                RemoveLaneMarking(_armedLaneEndpoint.Lane, _armedLaneEndpoint.Node, m.Lane, m.Node);
            }
            else
            {
                RemoveConnectionOverride(_armedLaneEndpoint.Lane, m.Lane);
            }
            MarkNetworkDirty();
            Rebuild();
        }

        // Remove the (fromLane → toLane) connection from the selected
        // vertex's EFFECTIVE connectivity. Works whether the connection
        // came from a user-authored override or from the resolver's
        // default rules.
        //
        // Strategy: read the effective list (after defaults+overrides
        // merge), drop the target, and write the surviving connections
        // from this from-lane back as explicit overrides — the
        // resolver's per-from-lane REPLACE semantics then suppresses
        // the default contribution. If the surviving list is empty
        // (target was the only connection from this from-lane), write
        // a SENTINEL override (To.RoadId == "") that exists solely to
        // trigger the REPLACE check without emitting a real connection.
        void RemoveConnectionOverride(LaneRef fromLane, LaneRef toLane)
        {
            if (_selectedVertex == null) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, _selectedVertex);
            if (vg == null || vg.Connectivity == null) return;

            List<LaneConnection> keep = new List<LaneConnection>();
            bool foundTarget = false;
            foreach (LaneConnection c in vg.Connectivity)
            {
                if (c == null || c.From == null || c.To == null) continue;
                if (!LaneRefMatches(c.From, fromLane)) continue;
                if (LaneRefMatches(c.To, toLane)) { foundTarget = true; continue; }
                keep.Add(c);
            }
            if (!foundTarget) return; // nothing to remove

            if (_selectedVertex.ConnectivityOverrides == null)
                _selectedVertex.ConnectivityOverrides = new List<LaneConnection>();
            // Wipe any existing overrides for this from-lane; we're about
            // to write the authoritative list (or a sentinel).
            _selectedVertex.ConnectivityOverrides.RemoveAll(
                c => c != null && LaneRefMatches(c.From, fromLane));

            foreach (LaneConnection c in keep)
            {
                _selectedVertex.ConnectivityOverrides.Add(new LaneConnection
                {
                    From = new LaneRef { RoadId = c.From.RoadId, Direction = c.From.Direction, Index = c.From.Index },
                    To = new LaneRef { RoadId = c.To.RoadId, Direction = c.To.Direction, Index = c.To.Index },
                });
            }
            if (keep.Count == 0)
            {
                _selectedVertex.ConnectivityOverrides.Add(new LaneConnection
                {
                    From = new LaneRef { RoadId = fromLane.RoadId, Direction = fromLane.Direction, Index = fromLane.Index },
                    To = new LaneRef { RoadId = "", Direction = Direction.AB, Index = -1 },
                });
            }
        }

        // Remove the FIRST LaneMarking matching (fromLane+fromNode →
        // toLane+toNode) on the selected vertex. No-op if none.
        void RemoveLaneMarking(LaneRef fromLane, LaneNode fromNode, LaneRef toLane, LaneNode toNode)
        {
            if (_selectedVertex == null || _selectedVertex.LaneMarkings == null) return;
            List<LaneMarking> list = _selectedVertex.LaneMarkings;
            for (int i = 0; i < list.Count; i++)
            {
                LaneMarking lm = list[i];
                if (LaneRefMatches(lm.From, fromLane) && lm.FromNode == fromNode
                    && LaneRefMatches(lm.To, toLane) && lm.ToNode == toNode)
                {
                    list.RemoveAt(i);
                    return;
                }
            }
        }

        // Left-click on a sign — cycle Control: Stop → Yield → None → Stop.
        // Works in Edit mode without needing a vertex selected (the sign
        // belongs to a specific road end, no selection required).
        void HandleSignClick(SignClickTarget t)
        {
            NetworkRoad r = FindRoadById(t.RoadId);
            if (r == null) return;
            StopYieldControl cur = t.End == RoadEnd.A ? r.ControlA : r.ControlB;
            StopYieldControl next = NextControl(cur);
            if (t.End == RoadEnd.A) r.ControlA = next;
            else r.ControlB = next;
            MarkNetworkDirty();
            Rebuild();
        }

        // Left-click on a lane endpoint marker. Three cases:
        //   - Inbound marker (no current arm OR different arm): arm it.
        //   - Inbound marker that's already armed: unarm (toggle).
        //   - Outbound marker with no arm: no-op, log a hint.
        //   - Outbound marker while armed: toggle the (armedFrom →
        //     thisOut) connection in the selected vertex's
        //     ConnectivityOverrides, then rebuild so the resolver
        //     re-merges defaults vs overrides.
        void HandleLaneEndpointClick(LaneEndpointMarker m)
        {
            if (_selectedVertex == null) return;
            if (m.VertexId != _selectedVertex.Id) return;

            if (m.IsInbound)
            {
                if (_armedLaneEndpoint == m) UnarmLaneEndpoint();
                else ArmLaneEndpoint(m);
                return;
            }

            // Outbound clicked.
            if (_armedLaneEndpoint == null)
            {
                Debug.Log("[NetworkDesigner] Lane edit: click an INBOUND endpoint first, then click an OUTBOUND endpoint. (Hold Shift to enter intersection-marking mode and connect lane corner nodes instead.)");
                return;
            }

            if (_markingShiftMode)
            {
                CreateLaneMarking(_armedLaneEndpoint.Lane, _armedLaneEndpoint.Node, m.Lane, m.Node);
            }
            else
            {
                AddConnectionOverride(_armedLaneEndpoint.Lane, m.Lane);
            }
            // Keep the from-lane armed so the user can author multiple
            // markings / connections without re-clicking.
            MarkNetworkDirty();
            Rebuild();
        }

        // Create a new painted lane marking from (from.fromNode → to.toNode)
        // on the selected vertex. Bezier control points are auto-placed
        // along the inbound/outbound flow tangents (same heuristic as
        // the flow arrows), so the curve enters/exits each lane
        // smoothly. Color defaults to Auto (hash-derived from the From
        // lane, matching the lane's marker color), style to Dashed.
        void CreateLaneMarking(LaneRef from, LaneNode fromNode, LaneRef to, LaneNode toNode)
        {
            if (_selectedVertex == null) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, _selectedVertex);
            if (vg == null) return;

            Vector2? fromPos = GeometryResolver.ResolveLaneNode(vg, from, fromNode);
            Vector2? toPos = GeometryResolver.ResolveLaneNode(vg, to, toNode);
            if (!fromPos.HasValue || !toPos.HasValue) return;

            Vector2 inboundFlow = -FindApproachOuterEdgeDir(vg, from.RoadId);
            Vector2 outboundFlow = FindApproachOuterEdgeDir(vg, to.RoadId);
            float chord = Vector2.Distance(fromPos.Value, toPos.Value);
            float ctrlLen = chord * 0.45f;
            Vector2 primary = fromPos.Value + inboundFlow * ctrlLen;
            Vector2 secondary = toPos.Value - outboundFlow * ctrlLen;

            if (_selectedVertex.LaneMarkings == null)
                _selectedVertex.LaneMarkings = new List<LaneMarking>();
            _selectedVertex.LaneMarkings.Add(new LaneMarking
            {
                Id = $"lm-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                From = new LaneRef { RoadId = from.RoadId, Direction = from.Direction, Index = from.Index },
                FromNode = fromNode,
                To = new LaneRef { RoadId = to.RoadId, Direction = to.Direction, Index = to.Index },
                ToNode = toNode,
                Primary = primary,
                Secondary = secondary,
                Color = LaneMarkingColor.Auto,
                Style = LaneMarkingStyle.Dashed,
            });

            // Diagnostic: log the inputs that drive the Auto color
            // decision in LaneMarkingsRenderer so we can tell why a
            // marking came out yellow vs white.
            bool innerSide = fromNode == LaneNode.Origin || fromNode == LaneNode.Tertiary;
            bool innermostLane = from.Index == 0;
            string expectedColor = (innerSide && innermostLane) ? "YELLOW" : "WHITE";
            Debug.Log($"[NetworkDesigner] CreateLaneMarking: From=(road={from.RoadId}, dir={from.Direction}, idx={from.Index}, node={fromNode})  " +
                      $"To=(road={to.RoadId}, dir={to.Direction}, idx={to.Index}, node={toNode})  " +
                      $"→ innerSide={innerSide}, innermostLane={innermostLane}, expected={expectedColor}");
        }

        void CycleMarkingStyle(MarkingClickTarget t)
        {
            Vertex v = FindVertexById(t.VertexId);
            if (v == null || v.LaneMarkings == null) return;
            LaneMarking m = v.LaneMarkings.Find(x => x.Id == t.MarkingId);
            if (m == null) return;
            // Cycle: White-Solid → White-Dashed → Yellow-Solid → Yellow-Dashed → …
            if (m.Color == LaneMarkingColor.White && m.Style == LaneMarkingStyle.Solid)
            {
                m.Style = LaneMarkingStyle.Dashed;
            }
            else if (m.Color == LaneMarkingColor.White && m.Style == LaneMarkingStyle.Dashed)
            {
                m.Color = LaneMarkingColor.Yellow;
                m.Style = LaneMarkingStyle.Solid;
            }
            else if (m.Color == LaneMarkingColor.Yellow && m.Style == LaneMarkingStyle.Solid)
            {
                m.Style = LaneMarkingStyle.Dashed;
            }
            else
            {
                m.Color = LaneMarkingColor.White;
                m.Style = LaneMarkingStyle.Solid;
            }
            MarkNetworkDirty();
            Rebuild();
        }

        void DeleteMarking(MarkingClickTarget t)
        {
            Vertex v = FindVertexById(t.VertexId);
            if (v == null || v.LaneMarkings == null) return;
            int removed = v.LaneMarkings.RemoveAll(x => x.Id == t.MarkingId);
            if (removed > 0)
            {
                MarkNetworkDirty();
                Rebuild();
            }
        }

        static Vector2 FindApproachOuterEdgeDir(VertexGeometry vg, string roadId)
        {
            foreach (VertexApproach a in vg.Approaches)
            {
                if (a.RoadId == roadId && a.OuterEdgeDir.sqrMagnitude > 1e-6f)
                    return a.OuterEdgeDir.normalized;
            }
            return Vector2.right;
        }

        void ArmLaneEndpoint(LaneEndpointMarker m)
        {
            if (_selectedVertex == null) return;
            _armedLaneEndpoint = m;
            // Respawn lane markers in armed state: hide other inbounds,
            // show reachable outbound click targets.
            LaneRef armed = m.Lane;
            LaneNode armedNode = m.Node;
            DestroyLaneEndpointMarkers();
            _armedLaneEndpoint = null; // cleared by destroy; reassign
            SpawnLaneEndpointMarkersFiltered(_selectedVertex, armed);
            if (_laneEndpointMarkers.TryGetValue(LaneEndpointKey(armed, armedNode), out GameObject ago) && ago != null)
            {
                _armedLaneEndpoint = ago.GetComponent<LaneEndpointMarker>();
                if (_armedLaneEndpoint != null)
                {
                    Color c = LaneMarkerArmedColor; c.a = LaneOverlayFullAlpha;
                    RefreshLaneMarkerColor(_armedLaneEndpoint, c);
                }
            }
            PushLaneFlowState();
        }

        void UnarmLaneEndpoint()
        {
            if (_armedLaneEndpoint == null && _laneEndpointMarkers.Count > 0) return;
            _armedLaneEndpoint = null;
            DestroyLaneEndpointMarkers();
            if (_selectedVertex != null) SpawnLaneEndpointMarkers(_selectedVertex);
            PushLaneFlowState();
        }

        // Add a (from → to) connection to the selected vertex's
        // EFFECTIVE connectivity. Symmetric to RemoveConnectionOverride:
        // synthesizes a full override list for the from-lane so the
        // resolver's per-from-lane REPLACE semantics doesn't silently
        // drop other default-generated connections from the same
        // from-lane. Idempotent — if the connection already exists in
        // the effective list, this is a no-op.
        void AddConnectionOverride(LaneRef fromLane, LaneRef toLane)
        {
            if (_selectedVertex == null) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, _selectedVertex);
            if (vg == null || vg.Connectivity == null) return;

            List<LaneConnection> keep = new List<LaneConnection>();
            bool alreadyExists = false;
            foreach (LaneConnection c in vg.Connectivity)
            {
                if (c == null || c.From == null || c.To == null) continue;
                if (!LaneRefMatches(c.From, fromLane)) continue;
                if (LaneRefMatches(c.To, toLane)) alreadyExists = true;
                keep.Add(c);
            }
            if (alreadyExists) return; // nothing to add

            keep.Add(new LaneConnection
            {
                From = new LaneRef { RoadId = fromLane.RoadId, Direction = fromLane.Direction, Index = fromLane.Index },
                To = new LaneRef { RoadId = toLane.RoadId, Direction = toLane.Direction, Index = toLane.Index },
            });

            if (_selectedVertex.ConnectivityOverrides == null)
                _selectedVertex.ConnectivityOverrides = new List<LaneConnection>();
            // Wipe any existing overrides for this from-lane; we're
            // about to write the authoritative list.
            _selectedVertex.ConnectivityOverrides.RemoveAll(
                c => c != null && LaneRefMatches(c.From, fromLane));
            foreach (LaneConnection c in keep)
            {
                _selectedVertex.ConnectivityOverrides.Add(new LaneConnection
                {
                    From = new LaneRef { RoadId = c.From.RoadId, Direction = c.From.Direction, Index = c.From.Index },
                    To = new LaneRef { RoadId = c.To.RoadId, Direction = c.To.Direction, Index = c.To.Index },
                });
            }
        }

        static bool LaneRefMatches(LaneRef a, LaneRef b)
        {
            if (a == null || b == null) return false;
            return a.RoadId == b.RoadId && a.Direction == b.Direction && a.Index == b.Index;
        }

        void EndDrag()
        {
            if (_draggedVertex == null) return;
            Vertex was = _draggedVertex;
            _draggedVertex = null;
            RefreshMarker(was);
        }

        void EndHandleDrag()
        {
            if (_draggedHandle == null) return;
            SetbackHandle was = _draggedHandle;
            _draggedHandle = null;
            if (was != null && _setbackHandles.TryGetValue(HandleKey(was.Road.Id, was.End), out GameObject hgo))
            {
                RefreshHandleMaterial(hgo, active: false);
            }
        }

        // -----------------------------------------------------------------
        // Mode HUD
        // -----------------------------------------------------------------

        void OnGUI()
        {
            if (ShowToolPalette) DrawToolPalette();

            // Measurement tooltip: small floating label near the cursor
            // showing the relevant distance(s) in meters during Create-mode
            // placement.
            if (ShowMeasurementTooltip && CurrentMode == DesignerMode.Create)
            {
                string measurement = ComputeMeasurementTooltip();
                if (!string.IsNullOrEmpty(measurement))
                {
                    Vector3 m = Input.mousePosition;
                    float x = m.x + 16f;
                    float y = Screen.height - m.y - 30f;
                    Rect r = new Rect(x, y, 260f, 22f);
                    GUI.Label(r, measurement);
                }
            }
        }

        // Cached IMGUI styles built lazily on first OnGUI (Unity's GUI.skin
        // isn't safe to touch outside of OnGUI, and styles can't be made
        // in Awake).
        GUIStyle _paletteHeader;
        GUIStyle _paletteButton;
        GUIStyle _paletteButtonActive;
        GUIStyle _paletteSubtle;
        GUIStyle _paletteBox;
        GUIStyle _paletteConfigButton;        // icon-above-label, used for road-profile palette buttons
        GUIStyle _paletteConfigButtonActive;
        Texture2D _paletteActiveTex;

        void EnsurePaletteStyles()
        {
            if (_paletteHeader != null) return;
            _paletteHeader = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                normal = { textColor = new Color(0.85f, 0.85f, 0.9f) },
                padding = new RectOffset(4, 4, 2, 2),
            };
            _paletteButton = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 4, 4),
                fontSize = 12,
            };
            _paletteActiveTex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _paletteActiveTex.SetPixel(0, 0, new Color(0.25f, 0.55f, 0.85f, 1f));
            _paletteActiveTex.Apply();
            _paletteButtonActive = new GUIStyle(_paletteButton)
            {
                normal = { background = _paletteActiveTex, textColor = Color.white },
                hover = { background = _paletteActiveTex, textColor = Color.white },
                active = { background = _paletteActiveTex, textColor = Color.white },
                fontStyle = FontStyle.Bold,
            };
            _paletteSubtle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                normal = { textColor = new Color(0.65f, 0.65f, 0.7f) },
                padding = new RectOffset(8, 4, 0, 4),
                wordWrap = true,
            };
            _paletteBox = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(6, 6, 6, 6),
            };
            _paletteConfigButton = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.LowerCenter,
                imagePosition = ImagePosition.ImageAbove,
                padding = new RectOffset(2, 2, 2, 2),
                fontSize = 10,
            };
            _paletteConfigButtonActive = new GUIStyle(_paletteConfigButton)
            {
                normal = { background = _paletteActiveTex, textColor = Color.white },
                hover = { background = _paletteActiveTex, textColor = Color.white },
                active = { background = _paletteActiveTex, textColor = Color.white },
                fontStyle = FontStyle.Bold,
            };
        }

        Vector2 _paletteScroll;

        // Returns true when the cursor is inside the palette's screen rect
        // and the palette is enabled. Used to suppress world-space clicks
        // from punching through to ground picking. IMGUI's own click
        // handling already consumes the event for the buttons themselves,
        // but `Input.GetMouseButtonDown` is independent of IMGUI's event
        // system, so we have to gate manually.
        bool MouseOverPalette()
        {
            if (!ShowToolPalette) return false;
            float maxH = Mathf.Min(Screen.height - ToolPaletteOrigin.y - 10f, 660f);
            Vector2 m = Input.mousePosition;
            // Flip Y: Input.mousePosition has origin at bottom-left; the
            // palette rect uses GUI coords with origin at top-left.
            float guiY = Screen.height - m.y;
            return m.x >= ToolPaletteOrigin.x
                && m.x <  ToolPaletteOrigin.x + ToolPaletteWidth
                && guiY >= ToolPaletteOrigin.y
                && guiY <  ToolPaletteOrigin.y + maxH;
        }

        void DrawToolPalette()
        {
            EnsurePaletteStyles();

            // Cap the visible height so the palette doesn't run off the
            // screen when there are many road configs. The inner area
            // scrolls past the cap.
            float maxH = Mathf.Min(Screen.height - ToolPaletteOrigin.y - 10f, 660f);
            Rect area = new Rect(ToolPaletteOrigin.x, ToolPaletteOrigin.y, ToolPaletteWidth, maxH);
            GUILayout.BeginArea(area, _paletteBox);
            _paletteScroll = GUILayout.BeginScrollView(_paletteScroll, GUIStyle.none, GUI.skin.verticalScrollbar);

            GUILayout.Label("MODE", _paletteHeader);
            GUILayout.BeginHorizontal();
            DrawModeButton("Create", DesignerMode.Create);
            DrawModeButton("Edit", DesignerMode.Edit);
            GUILayout.EndHorizontal();
            GUILayout.Label($"toggle: {ModeToggleKey}", _paletteSubtle);

            GUILayout.Space(4);

            GUILayout.Label("TOOLS", _paletteHeader);
            DrawToolButton("Straight",   BuildTool.Straight,   ToolHotkeyStraight);
            DrawToolButton("Fillet",     BuildTool.Fillet,     ToolHotkeyFillet);
            DrawToolButton("S-Curve",    BuildTool.SCurve,     ToolHotkeySCurve);
            DrawToolButton("Roundabout", BuildTool.Roundabout, ToolHotkeyRoundabout);
            DrawToolButton("Cul-de-sac", BuildTool.CulDeSac,   ToolHotkeyCulDeSac);
            GUILayout.Label($"cycle: {ToolToggleKey}", _paletteSubtle);

            // Live tool-state readout — same info the old HUD showed.
            string detail = ComputeToolStateDetail();
            if (!string.IsNullOrEmpty(detail))
            {
                GUILayout.Space(4);
                GUILayout.Label(detail, _paletteSubtle);
            }

            GUILayout.Space(8);
            DrawViewSection();

            GUILayout.Space(8);
            DrawSelectedVertexSection();

            GUILayout.Space(8);
            DrawConfigsSection();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // Toggle row for view + snap state. Each button is a stateful
        // pill: highlighted when the underlying bool is true. Click flips
        // the bool. Vertex toggle also calls SetVertexMarkersVisible so
        // existing markers update without a Rebuild.
        void DrawViewSection()
        {
            GUILayout.Label("VIEW / SNAP", _paletteHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Grid", SnapToGrid ? _paletteButtonActive : _paletteButton))
                SnapToGrid = !SnapToGrid;
            if (GUILayout.Button("Angle", AngleSnapEnabled ? _paletteButtonActive : _paletteButton))
                AngleSnapEnabled = !AngleSnapEnabled;
            if (GUILayout.Button("Verts", ShowVertexMarkers ? _paletteButtonActive : _paletteButton))
                SetVertexMarkersVisible(!ShowVertexMarkers);
            GUILayout.EndHorizontal();
            GUILayout.Label($"verts: {VertexViewToggleHotkey}", _paletteSubtle);
        }

        // Read-only inspector for the selected vertex (Edit mode only).
        // Shows id, position, and per-incident-road details: profile
        // width, chord length, computed setback + override (if any),
        // and the outward bearing in degrees. Re-resolves the vertex
        // geometry each frame — cheap (single vertex) and always
        // current with edits to the network.
        void DrawSelectedVertexSection()
        {
            if (CurrentMode != DesignerMode.Edit) return;
            if (_selectedVertex == null) return;
            if (_network == null) return;

            GUILayout.Label("VERTEX", _paletteHeader);
            GUILayout.Label($"id: {_selectedVertex.Id}", _paletteSubtle);
            GUILayout.Label($"pos: ({_selectedVertex.Position.x:F2}, {_selectedVertex.Position.y:F2})", _paletteSubtle);

            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, _selectedVertex);

            int roadCount = 0;
            foreach (NetworkRoad r in _network.Roads)
            {
                bool isA = r.EndA == _selectedVertex.Id;
                bool isB = r.EndB == _selectedVertex.Id;
                if (!isA && !isB) continue;
                roadCount++;

                RoadEnd end = isA ? RoadEnd.A : RoadEnd.B;
                string otherId = isA ? r.EndB : r.EndA;
                Vertex other = FindVertexById(otherId);
                if (other == null) continue;

                float chordLen = Vector2.Distance(_selectedVertex.Position, other.Position);

                VertexApproach app = null;
                if (vg != null)
                {
                    foreach (VertexApproach a in vg.Approaches)
                    {
                        if (a.RoadId == r.Id && a.End == end) { app = a; break; }
                    }
                }

                float? overrideSetback = isA ? r.SetbackA : r.SetbackB;
                float resolvedSetback = app != null ? app.Setback : 0f;
                float bearingDeg = app != null ? app.Bearing * Mathf.Rad2Deg : 0f;
                // Normalize to [-180, 180] for readability.
                while (bearingDeg > 180f) bearingDeg -= 360f;
                while (bearingDeg <= -180f) bearingDeg += 360f;

                GUILayout.Space(3);
                GUILayout.Label($"road {r.Id}", _paletteSubtle);
                GUILayout.Label($"  end {end}  w={r.Profile.TotalWidth:F1}m  chord={chordLen:F1}m", _paletteSubtle);
                string setbackStr = overrideSetback.HasValue
                    ? $"  setback {resolvedSetback:F2}m (override {overrideSetback.Value:F2}m)"
                    : $"  setback {resolvedSetback:F2}m (auto)";
                GUILayout.Label(setbackStr, _paletteSubtle);
                GUILayout.Label($"  bearing {bearingDeg:F1}°", _paletteSubtle);

                // Control (stop/yield/none): read-only label here. To
                // change it, click the sign in the scene view (cycles
                // Stop → Yield → None → Stop).
                bool hasInbound = ApproachHasInboundLanes(app, end);
                if (hasInbound)
                {
                    StopYieldControl currentCtrl = isA ? r.ControlA : r.ControlB;
                    GUILayout.Label($"  control: {currentCtrl}", _paletteSubtle);
                }
            }

            if (roadCount == 0)
            {
                GUILayout.Label("(no incident roads)", _paletteSubtle);
            }
        }

        static bool ApproachHasInboundLanes(VertexApproach app, RoadEnd end)
        {
            if (app == null) return false;
            Direction inboundDir = end == RoadEnd.A ? Direction.BA : Direction.AB;
            List<Vector2> inboundLanes = inboundDir == Direction.AB ? app.LaneEndsAB : app.LaneEndsBA;
            return inboundLanes != null && inboundLanes.Count > 0;
        }

        static StopYieldControl NextControl(StopYieldControl c)
        {
            switch (c)
            {
                case StopYieldControl.None:  return StopYieldControl.Stop;
                case StopYieldControl.Stop:  return StopYieldControl.Yield;
                default:                     return StopYieldControl.None;
            }
        }

        void DrawConfigsSection()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("ROAD CONFIGS", _paletteHeader);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("↻", GUILayout.Width(22), GUILayout.Height(18)))
            {
                ReloadConfigs();
            }
            GUILayout.EndHorizontal();

            if (_configs.Count == 0)
            {
                GUILayout.Label("No configs loaded. Export from the React Road Designer, then press ↻.", _paletteSubtle);
                return;
            }

            // Category tabs. Render as a wrapping row of small buttons —
            // GUILayout doesn't wrap automatically, so we manually break
            // when the next button would overflow.
            List<string> cats = AllCategoriesSorted();
            float maxW = ToolPaletteWidth - 24f; // account for box padding + scrollbar
            float used = 0f;
            GUILayout.BeginHorizontal();
            foreach (string cat in cats)
            {
                GUIContent content = new GUIContent(cat);
                Vector2 size = _paletteButton.CalcSize(content);
                size.x = Mathf.Min(size.x + 4f, maxW); // a hair of padding, never exceed row width
                if (used + size.x > maxW && used > 0f)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    used = 0f;
                }
                GUIStyle s = (cat == _activeCategory) ? _paletteButtonActive : _paletteButton;
                if (GUILayout.Button(content, s, GUILayout.Width(size.x), GUILayout.Height(20)))
                {
                    _activeCategory = cat;
                }
                used += size.x + 2f; // GUILayout's default spacing
            }
            GUILayout.EndHorizontal();

            // Buttons for configs in the active category. 2-per-row grid
            // with icon above label; falls back to label-only for any
            // config whose Name doesn't match a PNG under
            // Resources/icons/buttons/roads/.
            if (!string.IsNullOrEmpty(_activeCategory))
            {
                GUILayout.Space(2);
                List<SavedConfig> list = ConfigsInCategory(_activeCategory);
                const int columns = 2;
                const float btnHeight = 110f;
                float btnWidth = (maxW - (columns - 1) * 2f) / columns;
                for (int i = 0; i < list.Count; i += columns)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = 0; j < columns; j++)
                    {
                        int idx = i + j;
                        if (idx >= list.Count) { GUILayout.FlexibleSpace(); continue; }
                        DrawConfigButton(list[idx], btnWidth, btnHeight);
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        void DrawConfigButton(SavedConfig c, float width, float height)
        {
            string label = string.IsNullOrEmpty(c.Name) ? "(unnamed)" : c.Name;
            Texture2D icon = GetConfigIcon(c.Name);
            bool active = c.Id == _activeConfigId;

            GUIContent content = icon != null
                ? new GUIContent(label, icon)
                : new GUIContent(label);
            GUIStyle s = icon != null
                ? (active ? _paletteConfigButtonActive : _paletteConfigButton)
                : (active ? _paletteButtonActive : _paletteButton);

            if (GUILayout.Button(content, s, GUILayout.Width(width), GUILayout.Height(height)))
                OnConfigButtonClicked(c);
        }

        void DrawModeButton(string label, DesignerMode mode)
        {
            GUIStyle s = (CurrentMode == mode) ? _paletteButtonActive : _paletteButton;
            if (GUILayout.Button(label, s)) SetMode(mode);
        }

        void DrawToolButton(string label, BuildTool tool, KeyCode hotkey)
        {
            // Dim tool buttons in Edit mode so it's obvious they don't
            // do anything until the user switches back to Create.
            bool enabled = CurrentMode == DesignerMode.Create;
            Color prev = GUI.color;
            if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.55f);

            GUIStyle s = (enabled && CurrentTool == tool) ? _paletteButtonActive : _paletteButton;
            string text = $"{label}    ({hotkey.ToString().Replace("Alpha", "")})";
            if (GUILayout.Button(text, s) && enabled) SelectTool(tool);

            GUI.color = prev;
        }

        // Single-line context detail for the active tool. Mirrors what the
        // old HUD displayed; gets piped under the tool buttons in the palette.
        string ComputeToolStateDetail()
        {
            if (CurrentMode != DesignerMode.Create) return null;
            if (IsCurveTool(CurrentTool))
            {
                string middleLabel = CurrentTool == BuildTool.SCurve ? "control" : "corner";
                switch (_filletState)
                {
                    case FilletState.Idle: return "click 1 of 3";
                    case FilletState.AwaitingClick2: return $"click 2 of 3 ({middleLabel})";
                    case FilletState.AwaitingClick3: return "click 3 of 3 (end)";
                }
            }
            else if (CurrentTool == BuildTool.Roundabout)
            {
                return $"r = {RoundaboutRadius:F1} m\n{RoundaboutLanes} lanes\n+ / − to resize";
            }
            else if (CurrentTool == BuildTool.CulDeSac)
            {
                return $"click a dead-end vertex\nbulb = {CulDeSacWidthMultiplier:F1}× road width";
            }
            return null;
        }

        // Distance(s) to display in the Create-mode measurement tooltip.
        // Returns null when nothing meaningful applies (Idle states).
        string ComputeMeasurementTooltip()
        {
            Vector2? ground = PickGround();
            if (!ground.HasValue) return null;
            Vector2 c = ground.Value;

            if (CurrentTool == BuildTool.Straight)
            {
                if (_buildState == BuildState.EdgeFromVertex && _edgeAnchor != null)
                {
                    float d = Vector2.Distance(_edgeAnchor.Position, c);
                    return $"{d:F2} m";
                }
                return null;
            }

            // Curve tools (Fillet, SCurve)
            if (_filletStart == null) return null;
            switch (_filletState)
            {
                case FilletState.AwaitingClick2:
                {
                    float d = Vector2.Distance(_filletStart.Position, c);
                    return $"{d:F2} m";
                }
                case FilletState.AwaitingClick3:
                {
                    if (CurrentTool == BuildTool.SCurve)
                    {
                        // For an S-curve the chord (start → end) is the
                        // useful measurement; the control's offset matters
                        // less to the user than the overall span.
                        float chord = Vector2.Distance(_filletStart.Position, c);
                        return $"Chord: {chord:F2} m";
                    }
                    float d1 = Vector2.Distance(_filletStart.Position, _filletCorner);
                    float d2 = Vector2.Distance(_filletCorner, c);
                    return $"Leg 1: {d1:F2} m   Leg 2: {d2:F2} m";
                }
                default:
                    return null;
            }
        }

        void HandleRightClickDelete()
        {
            if (PickCamera == null) return;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 10000f)) return;

            // Vertex marker wins over road if both are stacked (markers
            // sit slightly above the road surface, so they'd typically
            // hit first anyway).
            VertexMarker vm = hit.collider.GetComponentInParent<VertexMarker>();
            if (vm != null && vm.Vertex != null)
            {
                DeleteVertex(vm.Vertex);
                return;
            }

            RoadMarker rm = hit.collider.GetComponentInParent<RoadMarker>();
            if (rm != null && rm.Road != null)
            {
                DeleteRoad(rm.Road);
                return;
            }
        }

        // Pick the intersection asphalt under the cursor, skipping any
        // non-intersection colliders along the same ray. Returns the
        // Vertex backing the closest intersection mesh hit, or null.
        Vertex PickIntersectionVertex()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
            float bestDist = float.MaxValue;
            Vertex best = null;
            for (int i = 0; i < hits.Length; i++)
            {
                IntersectionMarker im = hits[i].collider.GetComponentInParent<IntersectionMarker>();
                if (im == null || im.Vertex == null) continue;
                if (hits[i].distance < bestDist)
                {
                    bestDist = hits[i].distance;
                    best = im.Vertex;
                }
            }
            return best;
        }

        // Pick the road body under the cursor, skipping any non-road
        // colliders along the same ray (vertex marker, lane endpoint,
        // setback handle, etc.). Returns the closest road hit, or null.
        NetworkRoad PickRoad()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
            float bestDist = float.MaxValue;
            NetworkRoad best = null;
            for (int i = 0; i < hits.Length; i++)
            {
                RoadMarker rm = hits[i].collider.GetComponentInParent<RoadMarker>();
                if (rm == null || rm.Road == null) continue;
                if (hits[i].distance < bestDist)
                {
                    bestDist = hits[i].distance;
                    best = rm.Road;
                }
            }
            return best;
        }

        // Reverse a road's traffic direction by swapping its AB / BA
        // lane lists and shoulders. Geometry (EndA/EndB, Setbacks,
        // Curve controls, StopYieldControl) stays attached to the same
        // physical ends — only which DIRECTION the lanes carry traffic
        // flips. Side effect: any ConnectivityOverrides / LaneMarkings
        // on either endpoint that reference this road become invalid
        // (their stored Direction labels no longer match the lanes)
        // and are dropped. Clearing is safer than remapping because
        // AB and BA may have different lane counts (and the FROM/TO
        // semantics of overrides depend on which direction is in/out
        // at the vertex). Users re-author intersection editing as
        // needed after a reverse.
        void ReverseRoadDirection(NetworkRoad road)
        {
            if (road == null || road.Profile == null) return;

            // Swap lane sides + shoulders in the profile.
            var savedAB = road.Profile.AB;
            road.Profile.AB = road.Profile.BA;
            road.Profile.BA = savedAB;
            var savedShoulderAB = road.Profile.ShoulderAB;
            road.Profile.ShoulderAB = road.Profile.ShoulderBA;
            road.Profile.ShoulderBA = savedShoulderAB;

            // Drop now-orphaned per-vertex lane connectivity that
            // pointed at the old direction labels.
            int droppedOverrides = 0;
            int droppedMarkings = 0;
            foreach (Vertex v in _network.Vertices)
            {
                if (v.Id != road.EndA && v.Id != road.EndB) continue;
                if (v.ConnectivityOverrides != null)
                {
                    int before = v.ConnectivityOverrides.Count;
                    v.ConnectivityOverrides.RemoveAll(c =>
                        (c.From != null && c.From.RoadId == road.Id) ||
                        (c.To   != null && c.To.RoadId   == road.Id));
                    droppedOverrides += before - v.ConnectivityOverrides.Count;
                }
                if (v.LaneMarkings != null)
                {
                    int before = v.LaneMarkings.Count;
                    v.LaneMarkings.RemoveAll(m =>
                        (m.From != null && m.From.RoadId == road.Id) ||
                        (m.To   != null && m.To.RoadId   == road.Id));
                    droppedMarkings += before - v.LaneMarkings.Count;
                }
            }

            Debug.Log($"[NetworkDesigner] Reversed road '{road.Id}' direction. " +
                      $"Dropped {droppedOverrides} connectivity overrides + " +
                      $"{droppedMarkings} lane markings on its endpoints.");

            MarkNetworkDirty();
            Rebuild();
            // Force re-evaluation of hover next frame — the cached
            // road reference is still valid, but the mesh changed.
            _hoverRoad = null;
        }

        // -----------------------------------------------------------------
        // Setback handle editing (Edit mode)
        // -----------------------------------------------------------------

        SetbackHandle PickSetbackHandle()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 10000f))
            {
                return hit.collider.GetComponentInParent<SetbackHandle>();
            }
            return null;
        }

        MarkingClickTarget PickMarkingClickTarget()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
            float bestDist = float.MaxValue;
            MarkingClickTarget best = null;
            for (int i = 0; i < hits.Length; i++)
            {
                MarkingClickTarget t = hits[i].collider.GetComponentInParent<MarkingClickTarget>();
                if (t == null) continue;
                if (hits[i].distance < bestDist)
                {
                    bestDist = hits[i].distance;
                    best = t;
                }
            }
            return best;
        }

        SignClickTarget PickSignClickTarget()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
            float bestDist = float.MaxValue;
            SignClickTarget best = null;
            for (int i = 0; i < hits.Length; i++)
            {
                SignClickTarget t = hits[i].collider.GetComponentInParent<SignClickTarget>();
                if (t == null) continue;
                if (hits[i].distance < bestDist)
                {
                    bestDist = hits[i].distance;
                    best = t;
                }
            }
            return best;
        }

        LaneEndpointMarker PickLaneEndpointMarker()
        {
            return PickLaneEndpointMarker(preferOutbound: false);
        }

        // When `preferOutbound` is true, any hit outbound marker beats
        // any hit inbound marker (even if the inbound is closer along
        // the ray). Used in the armed-state context where the user's
        // next click is unambiguously aimed at an outbound — without
        // this preference, the other corner of the same armed lane
        // (which is also inbound, often closer to the cursor than the
        // intended outbound on a neighboring lane) wins the picker
        // and silently unarms instead of delete/create.
        LaneEndpointMarker PickLaneEndpointMarker(bool preferOutbound)
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            // RaycastAll so the picker can find a lane marker even when
            // a non-lane collider (setback handle, vertex marker, etc.)
            // is in front along the same ray. Picks the closest hit
            // that actually has a LaneEndpointMarker component.
            RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
            float bestOutDist = float.MaxValue;
            float bestInDist = float.MaxValue;
            LaneEndpointMarker bestOut = null;
            LaneEndpointMarker bestIn = null;
            for (int i = 0; i < hits.Length; i++)
            {
                LaneEndpointMarker m = hits[i].collider.GetComponentInParent<LaneEndpointMarker>();
                if (m == null) continue;
                if (m.IsInbound)
                {
                    if (hits[i].distance < bestInDist)
                    {
                        bestInDist = hits[i].distance;
                        bestIn = m;
                    }
                }
                else
                {
                    if (hits[i].distance < bestOutDist)
                    {
                        bestOutDist = hits[i].distance;
                        bestOut = m;
                    }
                }
            }
            if (preferOutbound && bestOut != null) return bestOut;
            // Fall back to the closest overall.
            if (bestOut == null) return bestIn;
            if (bestIn == null) return bestOut;
            return bestOutDist <= bestInDist ? bestOut : bestIn;
        }

        void SetSelectedVertex(Vertex v)
        {
            if (_selectedVertex == v) return;
            Vertex previouslySelected = _selectedVertex;
            _selectedVertex = v;
            DestroySetbackHandles();
            DestroyLateralOffsetHandles();
            DestroyLaneEndpointMarkers();
            // Restore the previously-selected vertex's marker now that
            // it's no longer the edit focus — but only if the global
            // vertex-view toggle is currently ON. Otherwise leave it
            // hidden so it doesn't pop out as the lone visible puck
            // while every other vertex stays hidden.
            if (previouslySelected != null && ShowVertexMarkers)
                SetVertexMarkerVisible(previouslySelected.Id, true);
            if (v != null)
            {
                SpawnSetbackHandles(v);
                SpawnLateralOffsetHandles(v);
                SpawnLaneEndpointMarkers(v);
                // Hide the selected vertex's marker puck so it doesn't
                // occlude the per-vertex edit overlays (setback handles
                // sit on top of it; lane endpoint rings sit at the lane
                // endings which can be very close to the puck on tight
                // intersections). Restored on Esc / re-select.
                SetVertexMarkerVisible(v.Id, false);
            }
            PushLaneFlowState();
        }

        void SetVertexMarkerVisible(string vertexId, bool visible)
        {
            if (_vertexMarkers.TryGetValue(vertexId, out GameObject go) && go != null)
                go.SetActive(visible);
        }

        void SpawnSetbackHandles(Vertex v)
        {
            if (_network == null) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, v);
            if (vg == null || vg.Approaches == null) return;

            foreach (VertexApproach a in vg.Approaches)
            {
                NetworkRoad road = FindRoadById(a.RoadId);
                if (road == null) continue;
                SpawnOrUpdateSetbackHandle(road, a, vg);
            }
        }

        // Re-position existing handles in place — used after a Rebuild
        // (handle dragging or vertex moving). Idempotent: also spawns
        // any newly-needed handle and removes stale ones.
        void RefreshSetbackHandlePositions()
        {
            if (_selectedVertex == null || _setbackHandles.Count == 0) return;
            if (_network == null) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, _selectedVertex);
            if (vg == null || vg.Approaches == null) return;

            HashSet<string> stillValid = new HashSet<string>();
            foreach (VertexApproach a in vg.Approaches)
            {
                string key = HandleKey(a.RoadId, a.End);
                stillValid.Add(key);
                NetworkRoad road = FindRoadById(a.RoadId);
                if (road == null) continue;
                SpawnOrUpdateSetbackHandle(road, a, vg);
            }
            // Remove any handle that no longer corresponds to a live approach.
            List<string> stale = new List<string>();
            foreach (string key in _setbackHandles.Keys)
            {
                if (!stillValid.Contains(key)) stale.Add(key);
            }
            foreach (string key in stale)
            {
                if (_setbackHandles.TryGetValue(key, out GameObject go) && go != null)
                {
                    if (Application.isPlaying) Destroy(go);
                    else DestroyImmediate(go);
                }
                _setbackHandles.Remove(key);
            }
        }

        void DestroySetbackHandles()
        {
            foreach (KeyValuePair<string, GameObject> kvp in _setbackHandles)
            {
                if (kvp.Value == null) continue;
                if (Application.isPlaying) Destroy(kvp.Value);
                else DestroyImmediate(kvp.Value);
            }
            _setbackHandles.Clear();
            _draggedHandle = null;
        }

        // ---- Lateral-offset handles ----

        void SpawnLateralOffsetHandles(Vertex v)
        {
            if (_network == null || v == null) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, v);
            if (vg == null || vg.Approaches == null) return;
            foreach (VertexApproach a in vg.Approaches)
            {
                NetworkRoad road = FindRoadById(a.RoadId);
                if (road == null) continue;
                SpawnOrUpdateLateralOffsetHandle(road, a, v);
            }
        }

        void RefreshLateralOffsetHandlePositions()
        {
            if (_selectedVertex == null || _lateralOffsetHandles.Count == 0) return;
            if (_network == null) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, _selectedVertex);
            if (vg == null || vg.Approaches == null) return;
            HashSet<string> stillValid = new HashSet<string>();
            foreach (VertexApproach a in vg.Approaches)
            {
                string key = HandleKey(a.RoadId, a.End);
                stillValid.Add(key);
                NetworkRoad road = FindRoadById(a.RoadId);
                if (road == null) continue;
                SpawnOrUpdateLateralOffsetHandle(road, a, _selectedVertex);
            }
            List<string> stale = new List<string>();
            foreach (string key in _lateralOffsetHandles.Keys)
                if (!stillValid.Contains(key)) stale.Add(key);
            foreach (string key in stale)
            {
                GameObject go = _lateralOffsetHandles[key];
                if (go != null)
                {
                    if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
                }
                _lateralOffsetHandles.Remove(key);
            }
        }

        void SpawnOrUpdateLateralOffsetHandle(NetworkRoad road, VertexApproach a, Vertex thisVertex)
        {
            string key = HandleKey(road.Id, a.End);

            // Effective centerline endpoint (after current LateralOffset).
            // Displayed position is pushed INTO the road body by
            // LateralOffsetHandleInwardOffset so neighboring approaches'
            // handles don't pile up on each other at the vertex. The
            // drag math (in UpdateLateralOffsetDrag) projects the cursor
            // onto the perpendicular axis rooted at the un-shifted
            // VertexXZ — independent of the displayed depth — so the
            // computed offset value isn't affected by this inward shift.
            string otherId = a.End == RoadEnd.A ? road.EndB : road.EndA;
            Vertex other = FindVertexById(otherId);
            Vector2 centerlineEnd = GeometryResolver.EffectiveEndpoint(road, a.End, thisVertex, other);

            Vector2 outward = a.OuterEdgeDir.sqrMagnitude > 1e-6f
                ? a.OuterEdgeDir.normalized
                : Vector2.right;
            Vector2 perpRight = new Vector2(outward.y, -outward.x);

            Vector2 handlePos = centerlineEnd + outward * LateralOffsetHandleInwardOffset;

            if (!_lateralOffsetHandles.TryGetValue(key, out GameObject go) || go == null)
            {
                go = new GameObject($"LateralOffsetHandle_{road.Id}_{a.End}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.AddComponent<MeshFilter>();
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = GetEditorOverlayMaterial();
                go.AddComponent<SphereCollider>();
                LateralOffsetHandle nlh = go.AddComponent<LateralOffsetHandle>();
                nlh.Road = road;
                nlh.End = a.End;
                _lateralOffsetHandles[key] = go;
            }

            LateralOffsetHandle lh = go.GetComponent<LateralOffsetHandle>();
            lh.VertexXZ = thisVertex.Position;
            lh.PerpRightXZ = perpRight;

            go.transform.position = new Vector3(handlePos.x, LateralOffsetHandleHeight, handlePos.y);

            float outerR = Mathf.Max(0.1f, LateralOffsetHandleDiameter * 0.5f);
            float innerR = Mathf.Max(0.05f, outerR - Mathf.Max(0.05f, LateralOffsetHandleRingThickness));
            MeshFilter mf = go.GetComponent<MeshFilter>();
            Mesh ringMesh = mf.sharedMesh;
            if (ringMesh == null)
            {
                ringMesh = new Mesh { name = $"LateralOffsetRing_{key}" };
                mf.sharedMesh = ringMesh;
            }
            ringMesh.Clear();
            _scratchVerts.Clear();
            _scratchTris.Clear();
            _scratchColors.Clear();
            EditorGeometry.AppendRing(_scratchVerts, _scratchTris, _scratchColors,
                Vector2.zero, outerR, innerR, 28, 0f, LateralOffsetHandleColor);
            ringMesh.SetVertices(_scratchVerts);
            ringMesh.SetTriangles(_scratchTris, 0);
            ringMesh.SetColors(_scratchColors);
            ringMesh.RecalculateBounds();

            SphereCollider sc = go.GetComponent<SphereCollider>();
            sc.center = Vector3.zero;
            sc.radius = outerR;
        }

        void DestroyLateralOffsetHandles()
        {
            foreach (KeyValuePair<string, GameObject> kvp in _lateralOffsetHandles)
            {
                if (kvp.Value == null) continue;
                if (Application.isPlaying) Destroy(kvp.Value);
                else DestroyImmediate(kvp.Value);
            }
            _lateralOffsetHandles.Clear();
            _draggedLateralHandle = null;
        }

        LateralOffsetHandle PickLateralOffsetHandle()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
            float bestDist = float.MaxValue;
            LateralOffsetHandle best = null;
            for (int i = 0; i < hits.Length; i++)
            {
                LateralOffsetHandle h = hits[i].collider.GetComponentInParent<LateralOffsetHandle>();
                if (h == null) continue;
                if (hits[i].distance < bestDist)
                {
                    bestDist = hits[i].distance;
                    best = h;
                }
            }
            return best;
        }

        void UpdateLateralOffsetDrag()
        {
            if (_draggedLateralHandle == null || _draggedLateralHandle.Road == null) return;
            Vector2? ground = PickGround();
            if (!ground.HasValue) return;
            // Project the cursor (relative to the unshifted vertex) onto
            // the perpendicular axis to get the signed offset. Positive
            // = perp-right of the road's A→B direction at this end.
            Vector2 rel = ground.Value - _draggedLateralHandle.VertexXZ;
            float signed = Vector2.Dot(rel, _draggedLateralHandle.PerpRightXZ);
            if (_draggedLateralHandle.End == RoadEnd.A)
                _draggedLateralHandle.Road.LateralOffsetA = signed;
            else
                _draggedLateralHandle.Road.LateralOffsetB = signed;
            MarkNetworkDirty();
            Rebuild();
        }

        void EndLateralOffsetDrag()
        {
            _draggedLateralHandle = null;
        }

        // ---- Lane-endpoint markers (click-to-edit overrides) ----

        // Edit-mode only: spawn one clickable sphere per lane endpoint
        // at the selected vertex. Inbound vs outbound is derived from
        // RoadEnd + lane direction (at EndA: BA=inbound, AB=outbound;
        // at EndB: opposite). Spawned positions come straight from the
        // resolver's LaneEndsAB/LaneEndsBA so they sit on the same
        // setback line the lane-flow overlay uses.
        // Per-frame: detect Shift state in Edit mode. On transition,
        // respawn lane markers so the right subset (midpoints in
        // regular mode, corners in marking mode) is visible. Also
        // pushes a suppression flag onto the selected vertex's
        // ApproachMarkingsRenderer so stop lines / sharks teeth / lane
        // arrows hide while the user is authoring markings.
        void UpdateMarkingShiftMode()
        {
            if (CurrentMode != DesignerMode.Edit || _selectedVertex == null)
            {
                if (_markingShiftMode)
                {
                    _markingShiftMode = false;
                    PushPaintedControlsSuppression(null, false);
                }
                return;
            }
            bool now = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (now == _markingShiftMode) return;
            _markingShiftMode = now;
            // Releasing shift cancels any armed corner; arming through
            // shift mode → flow mode would orphan a corner-Node armed
            // reference that doesn't exist in regular mode.
            _armedLaneEndpoint = null;
            _hoverLaneEndpoint = null;
            DestroyLaneEndpointMarkers();
            SpawnLaneEndpointMarkers(_selectedVertex);
            PushPaintedControlsSuppression(_selectedVertex.Id, _markingShiftMode);
            PushLaneFlowState();
        }

        // Tell the per-vertex ApproachMarkingsRenderer whether to
        // suppress its stop-line / sharks-teeth / lane-arrow paint.
        // When vertexId is null, clears the flag everywhere.
        void PushPaintedControlsSuppression(string vertexId, bool suppress)
        {
            if (_renderer == null) return;
            if (vertexId != null)
            {
                if (_renderer.TryGetApproachMarkingsRenderer(vertexId, out ApproachMarkingsRenderer amr))
                {
                    amr.SuppressForMarkingMode = suppress;
                    amr.Rebuild();
                }
                return;
            }
            // Clear-all path — used when leaving the selection.
            foreach (Vertex v in _network.Vertices)
            {
                if (_renderer.TryGetApproachMarkingsRenderer(v.Id, out ApproachMarkingsRenderer amr))
                {
                    if (amr.SuppressForMarkingMode)
                    {
                        amr.SuppressForMarkingMode = false;
                        amr.Rebuild();
                    }
                }
            }
        }

        // Default spawn — used when no inbound is armed. Shows only
        // INBOUND markers at the selected vertex. In regular Edit mode,
        // emits the centerline midpoint markers (A or B) used for
        // connectivity-override editing. In shift-marking mode, emits
        // the smaller Origin/Primary/Secondary/Tertiary corner markers
        // used for intersection lane-marking creation.
        void SpawnLaneEndpointMarkers(Vertex v)
        {
            if (_network == null) return;
            if (CurrentMode != DesignerMode.Edit) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, v);
            if (vg == null || vg.Approaches == null) return;

            foreach (VertexApproach a in vg.Approaches)
            {
                NetworkRoad road = FindRoadById(a.RoadId);
                if (road == null) continue;

                Direction inboundDir = a.End == RoadEnd.A ? Direction.BA : Direction.AB;
                List<Vector2> inboundLanes = inboundDir == Direction.AB ? a.LaneEndsAB : a.LaneEndsBA;
                if (inboundLanes == null) continue;

                bool aEnd = a.End == RoadEnd.A;
                LaneNode midNode = aEnd ? LaneNode.A : LaneNode.B;
                LaneNode innerCorner = aEnd ? LaneNode.Origin : LaneNode.Tertiary;
                LaneNode outerCorner = aEnd ? LaneNode.Primary : LaneNode.Secondary;

                // Position-based dedupe scratch — see TrySpawnDedupedCorner.
                // Lives per (road, direction) since adjacent roads' corners
                // can legitimately land near each other and shouldn't merge.
                _spawnedCornerPositions.Clear();
                for (int i = 0; i < inboundLanes.Count; i++)
                {
                    LaneRef lr = new LaneRef { RoadId = road.Id, Direction = inboundDir, Index = i };
                    if (_markingShiftMode)
                    {
                        TrySpawnDedupedCorner(v, road, lr, a, inboundDir, i, innerCorner, isInbound: true);
                        TrySpawnDedupedCorner(v, road, lr, a, inboundDir, i, outerCorner, isInbound: true);
                    }
                    else
                    {
                        // Regular mode: emit only the midpoint marker.
                        SpawnLaneRingMarker(v, road, lr, midNode, inboundLanes[i], isInbound: true, isCorner: false);
                    }
                }
            }
        }

        // Armed-state spawn — hides all OTHER inbound markers, keeps the
        // armed inbound visible, AND spawns OUTBOUND markers on every
        // OTHER incident road (so the user can click them to author
        // connections from the armed inbound).
        void SpawnLaneEndpointMarkersFiltered(Vertex v, LaneRef armedLane)
        {
            if (_network == null || armedLane == null) return;
            if (CurrentMode != DesignerMode.Edit) return;
            VertexGeometry vg = GeometryResolver.ResolveVertex(_network, v);
            if (vg == null || vg.Approaches == null) return;

            foreach (VertexApproach a in vg.Approaches)
            {
                NetworkRoad road = FindRoadById(a.RoadId);
                if (road == null) continue;

                bool isArmedRoad = road.Id == armedLane.RoadId;
                Direction inboundDir = a.End == RoadEnd.A ? Direction.BA : Direction.AB;
                Direction outboundDir = a.End == RoadEnd.A ? Direction.AB : Direction.BA;

                bool aEnd = a.End == RoadEnd.A;
                LaneNode midNode = aEnd ? LaneNode.A : LaneNode.B;
                LaneNode innerCorner = aEnd ? LaneNode.Origin : LaneNode.Tertiary;
                LaneNode outerCorner = aEnd ? LaneNode.Primary : LaneNode.Secondary;

                if (isArmedRoad)
                {
                    // Keep ONLY the armed lane's relevant nodes visible.
                    if (armedLane.Direction == inboundDir)
                    {
                        List<Vector2> inboundLanes = inboundDir == Direction.AB ? a.LaneEndsAB : a.LaneEndsBA;
                        if (armedLane.Index >= 0 && armedLane.Index < inboundLanes.Count)
                        {
                            LaneRef lr = new LaneRef { RoadId = road.Id, Direction = armedLane.Direction, Index = armedLane.Index };
                            if (_markingShiftMode)
                            {
                                // Marking mode: keep both corner markers
                                // for the armed lane (user might re-arm).
                                Vector2? innerPos = GeometryResolver.ResolveLaneNode(a, armedLane.Direction, armedLane.Index, innerCorner);
                                if (innerPos.HasValue) SpawnLaneRingMarker(v, road, lr, innerCorner, innerPos.Value, isInbound: true, isCorner: true);
                                Vector2? outerPos = GeometryResolver.ResolveLaneNode(a, armedLane.Direction, armedLane.Index, outerCorner);
                                if (outerPos.HasValue) SpawnLaneRingMarker(v, road, lr, outerCorner, outerPos.Value, isInbound: true, isCorner: true);
                            }
                            else
                            {
                                SpawnLaneRingMarker(v, road, lr, midNode, inboundLanes[armedLane.Index], isInbound: true, isCorner: false);
                            }
                        }
                    }
                }
                else
                {
                    // Other roads: show their OUTBOUND nodes as click targets.
                    List<Vector2> outboundLanes = outboundDir == Direction.AB ? a.LaneEndsAB : a.LaneEndsBA;
                    if (outboundLanes == null) continue;
                    _spawnedCornerPositions.Clear();
                    for (int i = 0; i < outboundLanes.Count; i++)
                    {
                        LaneRef lr = new LaneRef { RoadId = road.Id, Direction = outboundDir, Index = i };
                        if (_markingShiftMode)
                        {
                            TrySpawnDedupedCorner(v, road, lr, a, outboundDir, i, innerCorner, isInbound: false);
                            TrySpawnDedupedCorner(v, road, lr, a, outboundDir, i, outerCorner, isInbound: false);
                        }
                        else
                        {
                            SpawnLaneRingMarker(v, road, lr, midNode, outboundLanes[i], isInbound: false, isCorner: false);
                        }
                    }
                }
            }
        }

        // Scratch list of corner positions already spawned for the
        // current (road, direction) pass — used by TrySpawnDedupedCorner
        // to skip a duplicate corner that lands within an epsilon of an
        // earlier one. Re-cleared at the start of every per-approach
        // spawn loop in SpawnLaneEndpointMarkers / Filtered.
        readonly List<Vector2> _spawnedCornerPositions = new List<Vector2>();
        const float CornerDedupeEpsilonSq = 0.04f; // 0.2m radius

        // Resolve the corner position, then spawn a marker UNLESS one
        // is already present within CornerDedupeEpsilonSq at the same
        // XZ. This handles both:
        //   (a) Standard two-way roads: lane N's outer corner == lane
        //       N+1's inner corner (always shared at lane boundaries).
        //   (b) One-way roads with no median: lanes on opposite sides
        //       of the asphalt midpoint have their "inner" sides BOTH
        //       facing the midpoint, so two lane-0/lane-1 INNER
        //       corners land at the same midpoint position — while
        //       the OUTER corners on each side are unique.
        // Earlier "always spawn inner, only outer on last lane" rule
        // got (a) right but broke (b) — left the asphalt edges with
        // no markers. Position-dedup handles both cases uniformly.
        // First spawn wins; subsequent lanes lose their dup'd corner.
        void TrySpawnDedupedCorner(Vertex v, NetworkRoad road, LaneRef lr,
            VertexApproach a, Direction dir, int laneIndex, LaneNode node, bool isInbound)
        {
            Vector2? p = GeometryResolver.ResolveLaneNode(a, dir, laneIndex, node);
            if (!p.HasValue) return;
            Vector2 pos = p.Value;
            for (int i = 0; i < _spawnedCornerPositions.Count; i++)
            {
                if ((_spawnedCornerPositions[i] - pos).sqrMagnitude < CornerDedupeEpsilonSq) return;
            }
            _spawnedCornerPositions.Add(pos);
            SpawnLaneRingMarker(v, road, lr, node, pos, isInbound: isInbound, isCorner: true);
        }

        void SpawnLaneRingMarker(Vertex v, NetworkRoad road, LaneRef lr, LaneNode node,
            Vector2 pos, bool isInbound, bool isCorner)
        {
            string markerKey = LaneEndpointKey(lr, node);

            GameObject go = new GameObject($"LaneEndpoint_{road.Id}_{lr.Direction}_{lr.Index}_{node}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position = new Vector3(pos.x, LaneMarkerHeight, pos.y);
            go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetEditorOverlayMaterial();
            float diameter = isCorner ? LaneCornerMarkerDiameter : LaneMarkerDiameter;
            float thickness = isCorner ? LaneCornerMarkerRingThickness : LaneMarkerRingThickness;
            SphereCollider sc = go.AddComponent<SphereCollider>();
            sc.radius = diameter * 0.5f;

            LaneEndpointMarker m = go.AddComponent<LaneEndpointMarker>();
            m.VertexId = v.Id;
            m.Lane = lr;
            m.Node = node;
            m.IsInbound = isInbound;
            m.WorldXZ = pos;

            // Base color depends on marker kind:
            //   Corner markers (used by lane markings) follow real
            //     paint conventions: yellow if the lane is innermost
            //     (adjacent to the road centerline), white otherwise
            //     (adjacent to a same-direction lane divider).
            //   Midpoint markers (used by lane flow / connectivity) use
            //     a deterministic per-lane hash hue so different
            //     inbound lanes are visually distinct.
            //   Outbound markers always use the neutral target color.
            Color baseColor;
            if (!isInbound) baseColor = LaneOutboundMarkerColor;
            else if (isCorner) baseColor = GetLaneCornerLineColor(lr, node);
            else baseColor = EditorGeometry.HashToColor(LaneFlowKey(lr));
            baseColor.a = LaneOverlayDimAlpha;

            float outerR = Mathf.Max(0.1f, diameter * 0.5f);
            float innerR = Mathf.Max(0.05f, outerR - Mathf.Max(0.05f, thickness));
            Mesh ringMesh = new Mesh { name = $"LaneRing_{markerKey}" };
            _scratchVerts.Clear(); _scratchTris.Clear(); _scratchColors.Clear();
            EditorGeometry.AppendRing(_scratchVerts, _scratchTris, _scratchColors,
                Vector2.zero, outerR, innerR, 32, 0f, baseColor);
            ringMesh.SetVertices(_scratchVerts);
            ringMesh.SetTriangles(_scratchTris, 0);
            ringMesh.SetColors(_scratchColors);
            ringMesh.RecalculateBounds();
            go.GetComponent<MeshFilter>().sharedMesh = ringMesh;

            _laneEndpointMarkers[markerKey] = go;
        }

        // Marker pool key — different markers (midpoint vs each corner)
        // on the same lane need distinct keys. Includes the LaneNode.
        static string LaneEndpointKey(LaneRef lr, LaneNode node)
        {
            return $"{lr.RoadId}|{(int)lr.Direction}|{lr.Index}|{(int)node}";
        }

        // Lane-level key for the LaneFlowRenderer (filters arrows by
        // lane, not by specific node — flow arrows always go A↔B).
        static string LaneFlowKey(LaneRef lr)
        {
            return $"{lr.RoadId}|{(int)lr.Direction}|{lr.Index}";
        }

        void DestroyLaneEndpointMarkers()
        {
            foreach (KeyValuePair<string, GameObject> kvp in _laneEndpointMarkers)
            {
                if (kvp.Value == null) continue;
                if (Application.isPlaying) Destroy(kvp.Value);
                else DestroyImmediate(kvp.Value);
            }
            _laneEndpointMarkers.Clear();
            _armedLaneEndpoint = null;
            _hoverLaneEndpoint = null;
        }

        // Recolor a lane marker's ring mesh. Used by hover/arm/unarm
        // transitions to pop or dim a specific marker without rebuilding
        // its mesh.
        void RefreshLaneMarkerColor(LaneEndpointMarker m, Color color)
        {
            if (m == null) return;
            MeshFilter mf = m.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            RecolorMesh(mf.sharedMesh, color);
        }

        // Compute the "ambient" (un-hovered, un-armed) color a marker
        // should show right now. Inbound = hashed color; outbound =
        // neutral; armed marker = armed color. Alpha is the dim
        // baseline; hover/arm callers override alpha as needed.
        // Color of the real-road paint stripe THIS CORNER sits on:
        //   - Inner-side corners (Origin / Tertiary) of the innermost
        //     lane (Index == 0) lie on the road centerline → YELLOW.
        //   - Every other corner lies on a same-direction lane divider
        //     or the outer fog line → WHITE.
        // The outer-side corners (Primary / Secondary) of the innermost
        // lane are NOT yellow — they border the next lane out, which
        // is a white divider. Lane index alone isn't enough; node
        // identity matters too.
        static Color GetLaneCornerLineColor(LaneRef lr, LaneNode node)
        {
            bool innerSide = node == LaneNode.Origin || node == LaneNode.Tertiary;
            bool innermostLane = lr.Index == 0;
            return innerSide && innermostLane
                ? new Color(1f, 0.85f, 0.2f, 1f)
                : new Color(0.95f, 0.95f, 0.95f, 1f);
        }

        Color GetLaneMarkerAmbientColor(LaneEndpointMarker m)
        {
            string key = LaneFlowKey(m.Lane);
            bool isCorner = m.Node != LaneNode.A && m.Node != LaneNode.B;
            Color c;
            if (m == _armedLaneEndpoint) c = LaneMarkerArmedColor;
            else if (m.IsInbound && isCorner) c = GetLaneCornerLineColor(m.Lane, m.Node);
            else if (m.IsInbound) c = EditorGeometry.HashToColor(key);
            else c = LaneOutboundMarkerColor;
            c.a = LaneOverlayDimAlpha;
            return c;
        }

        void UpdateHandleDrag()
        {
            if (_draggedHandle == null) return;
            NetworkRoad r = _draggedHandle.Road;
            if (r == null) return;
            Vector2? ground = PickGroundRaw();
            if (!ground.HasValue) return;

            string thisId = _draggedHandle.End == RoadEnd.A ? r.EndA : r.EndB;
            string otherId = _draggedHandle.End == RoadEnd.A ? r.EndB : r.EndA;
            Vertex thisV = FindVertexById(thisId);
            Vertex otherV = FindVertexById(otherId);
            if (thisV == null || otherV == null) return;

            Vector2 dir = GeometryResolver.OutwardDirection(r, _draggedHandle.End, thisV, otherV);
            if (dir.sqrMagnitude < 1e-6f) return;

            float t = Vector2.Dot(ground.Value - thisV.Position, dir);
            t = Mathf.Max(0f, t);

            if (_draggedHandle.End == RoadEnd.A) r.SetbackA = t;
            else r.SetbackB = t;

            MarkNetworkDirty();
            Rebuild();
        }

        void ClearSetbackOverride(NetworkRoad r, RoadEnd end)
        {
            if (r == null) return;
            if (end == RoadEnd.A) r.SetbackA = null;
            else r.SetbackB = null;
            MarkNetworkDirty();
            Debug.Log($"[NetworkDesigner] Cleared setback override on road {r.Id} end {end}.");
            Rebuild();
        }

        // Spawn or update the ring+stem visual for a single setback
        // handle at the given approach. Idempotent — safe to call from
        // both initial spawn and per-frame refresh during drag.
        void SpawnOrUpdateSetbackHandle(NetworkRoad road, VertexApproach a, VertexGeometry vg)
        {
            string key = HandleKey(road.Id, a.End);

            Vector2 anchor = (a.OuterLeft + a.OuterRight) * 0.5f;
            // OuterEdgeDir runs AWAY from the vertex INTO the road body.
            // Cached on the SetbackHandle for the drag handler (which
            // projects cursor along outward to compute setback distance).
            Vector2 outward = a.OuterEdgeDir.sqrMagnitude > 1e-6f
                ? a.OuterEdgeDir.normalized
                : Vector2.right;

            // Handle sits PERPENDICULAR to the road direction — off in
            // the grass to one side. To always pick the side that's
            // "outside the intersection", look at where the OTHER
            // approaches at this vertex sit relative to this approach:
            // sum each neighbor's outward bearing projected onto the
            // perpendicular axis, and place the handle on the OPPOSITE
            // side from that sum (so it's on the side with the most
            // empty grass, away from neighboring asphalt). With no
            // neighbors (isolated dead-end), defaults to perpRight.
            Vector2 perpRight = new Vector2(outward.y, -outward.x);
            float neighborProjection = 0f;
            if (vg != null && vg.Approaches != null)
            {
                for (int i = 0; i < vg.Approaches.Count; i++)
                {
                    VertexApproach other = vg.Approaches[i];
                    if (other == a) continue;
                    Vector2 oDir = other.OuterEdgeDir;
                    if (oDir.sqrMagnitude < 1e-6f) continue;
                    oDir = oDir.normalized;
                    neighborProjection += Vector2.Dot(oDir, perpRight);
                }
            }
            // > 0: most neighbors are on the perpRight side → put handle on perpLeft.
            // < 0: most on the perpLeft side → put handle on perpRight.
            // == 0 (no neighbors / perfectly balanced): fall back to perpRight.
            Vector2 outsideDir = neighborProjection > 0f ? -perpRight : perpRight;
            float halfWidth = road.Profile.TotalWidth * 0.5f;
            float offset = halfWidth + Mathf.Max(
                SetbackHandleOffsetMin,
                road.Profile.TotalWidth * SetbackHandleOffsetWidthMultiplier);
            Vector2 handlePos = anchor + outsideDir * offset;

            if (!_setbackHandles.TryGetValue(key, out GameObject go) || go == null)
            {
                go = new GameObject($"SetbackHandle_{road.Id}_{a.End}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.AddComponent<MeshFilter>();
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = GetEditorOverlayMaterial();
                go.AddComponent<SphereCollider>();
                SetbackHandle nsh = go.AddComponent<SetbackHandle>();
                nsh.Road = road;
                nsh.End = a.End;
                _setbackHandles[key] = go;
            }

            SetbackHandle sh = go.GetComponent<SetbackHandle>();
            sh.AnchorXZ = anchor;
            sh.OutwardXZ = outward;

            go.transform.position = new Vector3(handlePos.x, SetbackHandleHeight, handlePos.y);

            // Ring mesh — built in LOCAL coords (centered at origin) so
            // the GameObject's transform handles world placement.
            float outerR = Mathf.Max(0.1f, SetbackHandleDiameter * 0.5f);
            float innerR = Mathf.Max(0.05f, outerR - Mathf.Max(0.05f, SetbackRingThickness));
            MeshFilter mf = go.GetComponent<MeshFilter>();
            Mesh ringMesh = mf.sharedMesh;
            if (ringMesh == null)
            {
                ringMesh = new Mesh { name = $"SetbackRing_{key}" };
                mf.sharedMesh = ringMesh;
            }
            _scratchVerts.Clear(); _scratchTris.Clear(); _scratchColors.Clear();
            EditorGeometry.AppendRing(_scratchVerts, _scratchTris, _scratchColors,
                Vector2.zero, outerR, innerR, 32, 0f, SetbackHandleColor);
            ringMesh.Clear();
            ringMesh.SetVertices(_scratchVerts);
            ringMesh.SetTriangles(_scratchTris, 0);
            ringMesh.SetColors(_scratchColors);
            ringMesh.RecalculateBounds();

            SphereCollider sc = go.GetComponent<SphereCollider>();
            if (sc != null) sc.radius = outerR;

            // Stem child — dashed line in LOCAL coords from the anchor's
            // position (relative to handle center) to local origin.
            Transform stemTf = go.transform.Find("Stem");
            GameObject stem;
            MeshFilter stemMf;
            if (stemTf == null)
            {
                stem = new GameObject("Stem");
                stem.transform.SetParent(go.transform, worldPositionStays: false);
                stem.transform.localPosition = Vector3.zero;
                stemMf = stem.AddComponent<MeshFilter>();
                MeshRenderer stemMr = stem.AddComponent<MeshRenderer>();
                stemMr.sharedMaterial = GetEditorOverlayMaterial();
            }
            else
            {
                stem = stemTf.gameObject;
                stemMf = stem.GetComponent<MeshFilter>();
            }
            Vector2 anchorLocal = anchor - handlePos;
            Mesh stemMesh = stemMf.sharedMesh;
            if (stemMesh == null)
            {
                stemMesh = new Mesh { name = $"SetbackStem_{key}" };
                stemMf.sharedMesh = stemMesh;
            }
            _scratchVerts.Clear(); _scratchTris.Clear(); _scratchColors.Clear();
            EditorGeometry.AppendDashedLine(_scratchVerts, _scratchTris, _scratchColors,
                anchorLocal, Vector2.zero,
                SetbackStemWidth, SetbackStemDashLength, SetbackStemGapLength,
                0f, SetbackHandleColor);
            stemMesh.Clear();
            stemMesh.SetVertices(_scratchVerts);
            stemMesh.SetTriangles(_scratchTris, 0);
            stemMesh.SetColors(_scratchColors);
            stemMesh.RecalculateBounds();
        }

        /// <summary>
        /// Destroy + respawn setback handles and lane endpoint markers
        /// for the currently-selected vertex. Used when a tunable
        /// affecting handle/marker dimensions changes — cheaper than a
        /// full network rebuild.
        /// </summary>
        public void RefreshEditOverlays()
        {
            if (_selectedVertex == null) return;
            DestroySetbackHandles();
            DestroyLateralOffsetHandles();
            DestroyLaneEndpointMarkers();
            SpawnSetbackHandles(_selectedVertex);
            SpawnLateralOffsetHandles(_selectedVertex);
            SpawnLaneEndpointMarkers(_selectedVertex);
        }

        // Mutate the ring + stem vertex colors in place — no material
        // swap. Used by drag start/end to flash active color.
        void RefreshHandleMaterial(GameObject go, bool active)
        {
            if (go == null) return;
            Color c = active ? SetbackHandleActiveColor : SetbackHandleColor;
            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) RecolorMesh(mf.sharedMesh, c);
            Transform stemTf = go.transform.Find("Stem");
            if (stemTf != null)
            {
                MeshFilter stemMf = stemTf.GetComponent<MeshFilter>();
                if (stemMf != null && stemMf.sharedMesh != null) RecolorMesh(stemMf.sharedMesh, c);
            }
        }

        static void RecolorMesh(Mesh mesh, Color c)
        {
            Color32[] cols = mesh.colors32;
            if (cols == null || cols.Length == 0) return;
            Color32 c32 = (Color32)c;
            for (int i = 0; i < cols.Length; i++) cols[i] = c32;
            mesh.colors32 = cols;
        }

        Material GetEditorOverlayMaterial()
        {
            if (_editorOverlayMaterial == null)
            {
                Shader sh = Shader.Find("NetworkDesigner/EditorOverlay");
                if (sh == null)
                {
                    Debug.LogWarning("[NetworkDesigner] EditorOverlay shader missing — falling back to Sprites/Default. Edit-mode overlays may look off.");
                    sh = Shader.Find("Sprites/Default");
                }
                _editorOverlayMaterial = new Material(sh) { name = "EditorOverlayMat" };
            }
            return _editorOverlayMaterial;
        }

        // Scratch buffers reused across mesh builds in this designer to
        // avoid per-build allocations. Single-threaded GUI code.
        readonly List<Vector3> _scratchVerts = new List<Vector3>();
        readonly List<int> _scratchTris = new List<int>();
        readonly List<Color32> _scratchColors = new List<Color32>();

        static string HandleKey(string roadId, RoadEnd end) => $"{roadId}:{end}";

        NetworkRoad FindRoadById(string id)
        {
            foreach (NetworkRoad r in _network.Roads)
                if (r.Id == id) return r;
            return null;
        }

        // Raw cursor → ground plane intersection without snap / guides.
        // The handle drag should follow the literal cursor; snap pipelines
        // are meant for vertex placement, not setback fine-tuning.
        Vector2? PickGroundRaw()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            if (Mathf.Abs(ray.direction.y) < 1e-6f) return null;
            float t = (GroundY - ray.origin.y) / ray.direction.y;
            if (t < 0f) return null;
            Vector3 hit = ray.origin + ray.direction * t;
            return new Vector2(hit.x, hit.z);
        }

        void DeleteVertex(Vertex v)
        {
            // Cancel an in-progress edge if it was anchored on this vertex,
            // and abort an in-progress drag if it was on this vertex. Also
            // clear hover so we don't hold a stale reference to a destroyed
            // marker GameObject.
            if (_edgeAnchor == v) CancelEdgeOperation();
            if (_filletStart == v) CancelFillet();
            if (_draggedVertex == v) _draggedVertex = null;
            if (_hoverVertex == v) _hoverVertex = null;
            if (_selectedVertex == v) SetSelectedVertex(null);

            // If this is a 2-way collinear junction, prefer fusing the two
            // incident edges into one rather than destroying everything.
            // (Merge keeps the other endpoints connected — no orphans.)
            if (TryCollinearMerge(v))
            {
                _network.Vertices.RemoveAll(x => x.Id == v.Id);
                DestroyVertexMarker(v.Id);
                MarkNetworkDirty();
                Rebuild();
                return;
            }

            // Drop any cul-de-sac bulb whose entry vertex is this one
            // (the user explicitly deleted the entry → no point in
            // keeping the filled disc rendering).
            if (_network.CulDeSacs != null)
            {
                _network.CulDeSacs.RemoveAll(b => b.EntryVertexId == v.Id);
            }

            // Fallback: drop any roads that touch this vertex AND any
            // vertices that become orphaned (no remaining incident roads)
            // as a result. One-step cleanup — doesn't cascade further.
            HashSet<string> neighborIds = new HashSet<string>();
            foreach (NetworkRoad r in _network.Roads)
            {
                if (r.EndA == v.Id && r.EndB != v.Id) neighborIds.Add(r.EndB);
                else if (r.EndB == v.Id && r.EndA != v.Id) neighborIds.Add(r.EndA);
            }
            _network.Roads.RemoveAll(r => r.EndA == v.Id || r.EndB == v.Id);

            foreach (string neighborId in neighborIds)
            {
                bool stillConnected = false;
                foreach (NetworkRoad r in _network.Roads)
                {
                    if (r.EndA == neighborId || r.EndB == neighborId)
                    {
                        stillConnected = true;
                        break;
                    }
                }
                if (stillConnected) continue;

                // Orphaned — remove the vertex and its marker. Also clear
                // any in-progress references so we don't dangle.
                Vertex orphan = FindVertexById(neighborId);
                if (orphan == null) continue;
                if (_edgeAnchor == orphan) CancelEdgeOperation();
                if (_filletStart == orphan) CancelFillet();
                if (_draggedVertex == orphan) _draggedVertex = null;
                if (_hoverVertex == orphan) _hoverVertex = null;
                _network.Vertices.Remove(orphan);
                DestroyVertexMarker(orphan.Id);
            }

            _network.Vertices.RemoveAll(x => x.Id == v.Id);
            DestroyVertexMarker(v.Id);

            MarkNetworkDirty();
            Rebuild();
        }

        void DestroyVertexMarker(string vertexId)
        {
            if (!_vertexMarkers.TryGetValue(vertexId, out GameObject marker)) return;
            if (marker != null)
            {
                if (Application.isPlaying) Destroy(marker);
                else DestroyImmediate(marker);
            }
            _vertexMarkers.Remove(vertexId);
        }

        // Returns true if `v` was a "pass-through" (exactly 2 incident
        // roads collinear within CollinearMergeAngleDeg). When true, the
        // two incident roads are replaced with a single merged road and
        // the caller should NOT also delete the incident roads.
        bool TryCollinearMerge(Vertex v)
        {
            if (CollinearMergeAngleDeg <= 0f) return false;

            // Collect the roads incident to v.
            List<NetworkRoad> incident = new List<NetworkRoad>();
            foreach (NetworkRoad r in _network.Roads)
            {
                if (r.EndA == v.Id || r.EndB == v.Id) incident.Add(r);
            }
            if (incident.Count != 2) return false;

            NetworkRoad r1 = incident[0];
            NetworkRoad r2 = incident[1];
            // Phase A scope: only fuse two straight roads. Fusing a curve
            // (or two curves) would need the merged road to carry combined
            // control points; that's Phase B.
            if (r1.Curve != null || r2.Curve != null) return false;
            string other1Id = r1.EndA == v.Id ? r1.EndB : r1.EndA;
            string other2Id = r2.EndA == v.Id ? r2.EndB : r2.EndA;
            Vertex other1 = FindVertexById(other1Id);
            Vertex other2 = FindVertexById(other2Id);
            if (other1 == null || other2 == null) return false;
            if (other1 == other2) return false; // self-loop; can't sensibly merge

            // Vectors pointing out of v toward each neighbor. Anti-parallel
            // → dot product near -1.
            Vector2 d1 = (other1.Position - v.Position).normalized;
            Vector2 d2 = (other2.Position - v.Position).normalized;
            float dot = Vector2.Dot(d1, d2);
            float threshold = -Mathf.Cos(CollinearMergeAngleDeg * Mathf.Deg2Rad);
            if (dot > threshold) return false;

            // Replace r1 + r2 with a single merged road. Profile is taken
            // from r1 — in the common case (auto-split chain) both share
            // the same profile so the choice doesn't matter.
            _network.Roads.Remove(r1);
            _network.Roads.Remove(r2);
            NetworkRoad merged = new NetworkRoad
            {
                Id = $"r-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                EndA = other1.Id,
                EndB = other2.Id,
                Classification = r1.Classification,
                Profile = r1.Profile,
            };
            _network.Roads.Add(merged);
            Debug.Log($"[NetworkDesigner] Collinear merge at vertex {v.Id}: " +
                      $"{r1.Id} + {r2.Id} → {merged.Id} " +
                      $"({other1.Id} ↔ {other2.Id})");
            return true;
        }

        // -----------------------------------------------------------------
        // Roundabout conversion
        // -----------------------------------------------------------------
        //
        // Replaces `center` with a ring of N vertices arranged at radius
        // RoundaboutRadius around its position, connected by N curved
        // arc-roads (cubic-Bezier circle approximation). Each existing
        // incoming road is reconnected from `center` to the ring vertex
        // at its bearing. Ring roads carry a forced one-way profile,
        // direction = CCW for RHD, CW for LHD.
        //
        // Skipped (with a warning) if the vertex has < 2 incident roads.
        // Wrapper around ConvertVertexToRoundabout that explains in the
        // Console why nothing happened if the preconditions aren't met.
        void TryRoundaboutHotkey()
        {
            if (CurrentMode != DesignerMode.Edit)
            {
                Debug.Log($"[NetworkDesigner] Roundabout hotkey ({RoundaboutHotkey}) ignored: " +
                          $"current mode is {CurrentMode}, need Edit. Press Tab to switch.");
                return;
            }
            if (_selectedVertex == null)
            {
                Debug.Log($"[NetworkDesigner] Roundabout hotkey ({RoundaboutHotkey}) ignored: " +
                          $"no vertex selected. Click a vertex in Edit mode first.");
                return;
            }
            ConvertVertexToRoundabout(_selectedVertex);
        }

        // Public entry point: convert an existing vertex into a roundabout.
        // Uses the vertex's incident roads as approaches; 0/1/N all work.
        // Refuses with a Console warning if the vertex isn't a valid site.
        void ConvertVertexToRoundabout(Vertex center)
        {
            if (center == null || _network == null) return;
            List<NetworkRoad> incident = new List<NetworkRoad>();
            foreach (NetworkRoad r in _network.Roads)
            {
                if (r.EndA == center.Id || r.EndB == center.Id) incident.Add(r);
            }
            string reason;
            if (!IsValidRoundaboutSite(incident, out reason))
            {
                Debug.LogWarning($"[NetworkDesigner] Roundabout refused at vertex '{center.Id}': {reason}");
                return;
            }
            BuildRoundabout(center.Position, incident, center);
        }

        // Returns true iff this set of incident roads can be safely
        // rewired into a roundabout ring. Currently blocks:
        //   - Any incident road with a baked curve (Curve != null).
        //     Reconnecting moves the road's center-end without touching
        //     ControlA/ControlB, producing a distorted Bezier that has
        //     crashed mesh generation (notably when placing a roundabout
        //     on a ring vertex of an existing roundabout — every ring
        //     arc is curved).
        bool IsValidRoundaboutSite(List<NetworkRoad> incidentRoads, out string reason)
        {
            for (int i = 0; i < incidentRoads.Count; i++)
            {
                if (incidentRoads[i].Curve != null)
                {
                    reason = $"incident road '{incidentRoads[i].Id}' is curved. " +
                             "Roundabouts can only be placed on vertices joined by straight roads. " +
                             "(Tip: this typically happens when clicking a vertex that's already part of " +
                             "a roundabout ring — delete that roundabout first, or pick a different vertex.)";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        // Public entry point: place a standalone roundabout at a world
        // position. No existing vertex required; the ring just sits there
        // unconnected. (User can attach roads to ring vertices later.)
        void CreateRoundaboutAt(Vector2 worldPos)
        {
            if (_network == null) return;
            BuildRoundabout(worldPos, new List<NetworkRoad>(), null);
        }

        void BuildRoundabout(Vector2 centerPos, List<NetworkRoad> incidentRoads, Vertex centerToDelete)
        {
            // 1) Collect bearings (FROM center toward each road's other endpoint).
            // Standalone roundabouts (centerToDelete == null) have no incidents.
            List<RoundaboutApproach> approaches = new List<RoundaboutApproach>();
            if (centerToDelete != null)
            foreach (NetworkRoad r in incidentRoads)
            {
                string otherId = r.EndA == centerToDelete.Id ? r.EndB : r.EndA;
                Vertex other = FindVertexById(otherId);
                if (other == null) continue;
                Vector2 dir = other.Position - centerPos;
                if (dir.sqrMagnitude < 1e-6f) continue;
                dir.Normalize();
                approaches.Add(new RoundaboutApproach
                {
                    Road = r,
                    Bearing = Mathf.Atan2(dir.y, dir.x),
                });
            }
            approaches.Sort((a, b) => a.Bearing.CompareTo(b.Bearing));

            float radius = Mathf.Max(0.5f, RoundaboutRadius);
            const float MAX_SEG_ARC = Mathf.PI / 2f; // 90°

            // 2) Compute the sequence of ring-vertex bearings in CCW order,
            //    inserting subdivision bearings between approaches whenever
            //    an arc exceeds 90° (so each cubic Bezier stays a good
            //    circle approximation).
            //
            //    Three cases:
            //      - 0 approaches → 4 cardinal points (standalone ring)
            //      - 1 approach   → the approach + 3 subdivisions, full 360° loop
            //      - 2+ approaches → each approach + subdivisions per arc
            List<float> ringBearings = new List<float>();
            // For each approach, remember the index in ringBearings where
            // its ring vertex lands. Used to reconnect incoming roads.
            int[] approachVertIdx = new int[approaches.Count];

            if (approaches.Count == 0)
            {
                for (int i = 0; i < 4; i++) ringBearings.Add(i * (Mathf.PI / 2f));
            }
            else
            {
                int N = approaches.Count;
                for (int i = 0; i < N; i++)
                {
                    int next = (i + 1) % N;
                    float fromBearing = approaches[i].Bearing;
                    float toBearing = approaches[next].Bearing;
                    if (next == 0) toBearing += 2f * Mathf.PI; // wrap

                    approachVertIdx[i] = ringBearings.Count;
                    ringBearings.Add(fromBearing);

                    float arc = toBearing - fromBearing;
                    int subs = Mathf.Max(1, Mathf.CeilToInt(arc / MAX_SEG_ARC));
                    float segArc = arc / subs;
                    for (int j = 1; j < subs; j++)
                    {
                        ringBearings.Add(fromBearing + j * segArc);
                    }
                }
            }

            // 3) Spawn ring vertices at radius along each bearing.
            int ringCount = ringBearings.Count;
            Vertex[] ringVerts = new Vertex[ringCount];
            for (int i = 0; i < ringCount; i++)
            {
                Vector2 dir = new Vector2(Mathf.Cos(ringBearings[i]), Mathf.Sin(ringBearings[i]));
                Vector2 pos = centerPos + dir * radius;
                Vertex v = new Vertex
                {
                    Id = $"v-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    Position = pos,
                    ConnectivityOverrides = new List<LaneConnection>(),
                };
                _network.Vertices.Add(v);
                SpawnVertexMarker(v);
                ringVerts[i] = v;
            }

            // 4) Reconnect each incident road's center-end to its approach
            //    vertex. Skip if there's no center vertex (standalone) since
            //    there's nothing to reconnect.
            if (centerToDelete != null)
            {
                for (int i = 0; i < approaches.Count; i++)
                {
                    NetworkRoad r = approaches[i].Road;
                    if (r.EndA == centerToDelete.Id) r.EndA = ringVerts[approachVertIdx[i]].Id;
                    else r.EndB = ringVerts[approachVertIdx[i]].Id;
                    r.SetbackA = null;
                    r.SetbackB = null;
                }
            }

            // 5) Build ring road profile.
            RoadProfile ringProfile = BuildRingProfile(RoundaboutLanes);

            // 6) Connect consecutive ring vertices (CCW around the circle)
            //    with cubic-Bezier arcs. Each segment is ≤ 90° thanks to
            //    subdivision, so the Bezier circle approximation stays
            //    accurate.
            bool ccw = DriveSide == DriveSide.Right;
            for (int i = 0; i < ringCount; i++)
            {
                Vertex a = ringVerts[i];
                Vertex b = ringVerts[(i + 1) % ringCount];

                float arcAngle = ComputeArcCCW(a.Position - centerPos, b.Position - centerPos);
                float k = (4f / 3f) * Mathf.Tan(arcAngle * 0.25f) * radius;
                Vector2 aTangent = TangentOnCircle(a.Position, centerPos, ccw: true);
                Vector2 bTangent = TangentOnCircle(b.Position, centerPos, ccw: true);

                Vector2 c1 = a.Position + aTangent * k;
                Vector2 c2 = b.Position - bTangent * k;

                Vertex fromV, toV;
                Vector2 controlA, controlB;
                if (ccw)
                {
                    fromV = a; toV = b; controlA = c1; controlB = c2;
                }
                else
                {
                    fromV = b; toV = a; controlA = c2; controlB = c1;
                }

                NetworkRoad ringRoad = new NetworkRoad
                {
                    Id = $"r-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    EndA = fromV.Id,
                    EndB = toV.Id,
                    Classification = RoadClassification.Secondary,
                    Profile = CloneProfile(ringProfile),
                    Curve = new RoadCurve { ControlA = controlA, ControlB = controlB },
                };
                _network.Roads.Add(ringRoad);
            }

            // 7) Drop the central vertex (if any) and any state referring to it.
            if (centerToDelete != null)
            {
                if (_selectedVertex == centerToDelete) SetSelectedVertex(null);
                if (_edgeAnchor == centerToDelete) CancelEdgeOperation();
                if (_filletStart == centerToDelete) CancelFillet();
                if (_draggedVertex == centerToDelete) _draggedVertex = null;
                if (_hoverVertex == centerToDelete) _hoverVertex = null;
                _network.Vertices.RemoveAll(x => x.Id == centerToDelete.Id);
                DestroyVertexMarker(centerToDelete.Id);
            }

            MarkNetworkDirty();
            Rebuild();
            Debug.Log($"[NetworkDesigner] Roundabout @ ({centerPos.x:F1}, {centerPos.y:F1}): " +
                      $"{ringCount} ring vertices, {ringCount} arc roads ({RoundaboutLanes}-lane " +
                      $"{(ccw ? "CCW" : "CW")}), {approaches.Count} approach(es) reconnected.");
        }

        // CCW central angle (radians) from vector A to vector B around origin.
        // Always returns a value in (0, 2π].
        static float ComputeArcCCW(Vector2 a, Vector2 b)
        {
            float angA = Mathf.Atan2(a.y, a.x);
            float angB = Mathf.Atan2(b.y, b.x);
            float arc = angB - angA;
            while (arc <= 0f) arc += 2f * Mathf.PI;
            return arc;
        }

        struct RoundaboutApproach
        {
            public NetworkRoad Road;
            public float Bearing; // FROM center toward the road's other endpoint
        }

        // Tangent direction at a point on the circle, in the direction of
        // travel. For CCW travel, rotate (point - center) by +90°.
        static Vector2 TangentOnCircle(Vector2 onCircle, Vector2 center, bool ccw)
        {
            Vector2 radial = (onCircle - center).normalized;
            return ccw
                ? new Vector2(-radial.y, radial.x)   // +90° rotation
                : new Vector2(radial.y, -radial.x);  // -90° rotation
        }

        // One-way ring profile: N lanes on AB, none on BA. Shoulder + lane
        // widths come from the designer's defaults.
        RoadProfile BuildRingProfile(int laneCount)
        {
            laneCount = Mathf.Max(1, laneCount);
            RoadProfile p = new RoadProfile
            {
                Id = $"p-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                AB = new Side(),
                BA = new Side(),
                ShoulderAB = new Shoulder { Width = DefaultShoulderWidth },
                ShoulderBA = new Shoulder { Width = DefaultShoulderWidth },
                Median = null,
            };
            for (int i = 0; i < laneCount; i++)
            {
                p.AB.Lanes.Add(new Lane { Id = $"{p.Id}-ab-{i}", Width = DefaultLaneWidth });
            }
            return p;
        }

        void DeleteRoad(NetworkRoad road)
        {
            _network.Roads.RemoveAll(r => r.Id == road.Id);
            MarkNetworkDirty();
            Rebuild();
        }

        void OnVertexClicked(Vertex v)
        {
            if (_buildState == BuildState.Idle)
            {
                SetEdgeAnchor(v);
            }
            else // EdgeFromVertex
            {
                if (v == _edgeAnchor)
                {
                    CancelEdgeOperation();
                    return;
                }
                CreateRoad(_edgeAnchor, v);
                // Chain: keep building from this vertex until Escape.
                SetEdgeAnchor(v);
            }
        }

        void OnGroundClicked(Vector2 worldXZ)
        {
            // Translate the raw click into "the vertex this click should
            // attach to" — either creating a fresh one or splitting an
            // existing edge if the click lands on it.
            Vertex v = CreateOrSplitAtPos(worldXZ);

            if (_buildState == BuildState.Idle)
            {
                // First click both places the anchor and starts an edge.
                SetEdgeAnchor(v);
                Rebuild();
            }
            else // EdgeFromVertex
            {
                CreateRoad(_edgeAnchor, v);
                // Chain: the just-placed vertex becomes the new anchor
                // so the user can keep extending the path.
                SetEdgeAnchor(v);
            }
        }

        // Set or move the current edge anchor. Refreshes both the previous
        // and new marker visuals so the active highlight follows along.
        void SetEdgeAnchor(Vertex v)
        {
            Vertex was = _edgeAnchor;
            _buildState = BuildState.EdgeFromVertex;
            _edgeAnchor = v;
            if (was != null && was != v) RefreshMarker(was);
            if (v != null) RefreshMarker(v);
        }

        void CancelEdgeOperation()
        {
            Vertex was = _edgeAnchor;
            _buildState = BuildState.Idle;
            _edgeAnchor = null;
            if (was != null) RefreshMarker(was);
            if (_previewLine != null) _previewLine.enabled = false;
        }

        // -----------------------------------------------------------------
        // Network mutation
        // -----------------------------------------------------------------

        Vertex CreateAndPlaceVertex(Vector2 pos)
        {
            Vertex v = new Vertex
            {
                Id = $"v-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Position = pos,
                ConnectivityOverrides = new List<LaneConnection>(),
            };
            _network.Vertices.Add(v);
            SpawnVertexMarker(v);
            MarkNetworkDirty();
            return v;
        }

        // Vertex factory used by the click handlers. If `pos` falls within
        // EdgeClickTolerance of an existing edge's interior, that edge is
        // split at the projected point and the resulting split-vertex is
        // returned. Otherwise a fresh vertex is created at `pos`.
        //
        // The "interior" check excludes the first/last 5% of each edge so
        // a click near an existing endpoint doesn't trigger a split — the
        // user is probably aiming at the endpoint, and the vertex picker
        // will catch that path separately.
        Vertex CreateOrSplitAtPos(Vector2 pos)
        {
            if (EdgeClickTolerance > 0f)
            {
                NetworkRoad closestRoad = null;
                Vector2 closestProj = Vector2.zero;
                float closestDist = float.MaxValue;
                foreach (NetworkRoad r in _network.Roads)
                {
                    // Phase A scope: only straight roads participate in
                    // click-to-split. Curve-vs-click projection is Phase B.
                    if (r.Curve != null) continue;
                    Vertex va = FindVertexById(r.EndA);
                    Vertex vb = FindVertexById(r.EndB);
                    if (va == null || vb == null) continue;
                    float t = ProjectOntoSegment(va.Position, vb.Position, pos, out Vector2 proj);
                    if (t <= 0.05f || t >= 0.95f) continue;
                    float d = Vector2.Distance(pos, proj);
                    if (d < closestDist && d <= EdgeClickTolerance)
                    {
                        closestDist = d;
                        closestRoad = r;
                        closestProj = proj;
                    }
                }

                if (closestRoad != null)
                {
                    // Don't create a near-duplicate if the projection
                    // happens to land on/near an existing vertex.
                    float reuseTol = Mathf.Max(VertexOnEdgeTolerance, 0.05f);
                    foreach (Vertex existing in _network.Vertices)
                    {
                        if (Vector2.Distance(existing.Position, closestProj) <= reuseTol)
                        {
                            return existing;
                        }
                    }

                    Vertex split = CreateAndPlaceVertex(closestProj);
                    SplitRoad(closestRoad, split);
                    Debug.Log($"[NetworkDesigner] Click on edge {closestRoad.Id} " +
                              $"→ inserted vertex {split.Id} at " +
                              $"({closestProj.x:F2}, {closestProj.y:F2})");
                    return split;
                }
            }
            return CreateAndPlaceVertex(pos);
        }

        // Project p onto the segment a→b. Returns the un-clamped parameter
        // t (>0 < 1 means "inside the segment"), and outputs the clamped
        // closest point on the segment.
        static float ProjectOntoSegment(Vector2 a, Vector2 b, Vector2 p, out Vector2 proj)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-9f) { proj = a; return 0f; }
            float t = Vector2.Dot(p - a, ab) / len2;
            float tClamped = Mathf.Clamp01(t);
            proj = a + ab * tClamped;
            return t;
        }

        void CreateRoad(Vertex a, Vertex b)
        {
            if (a == b) return;

            List<Crossing> crossings = new List<Crossing>();

            // 1a) Crossings with existing edges (segment vs segment).
            //     Snapshot because we'll mutate _network.Roads when we split.
            List<NetworkRoad> existing = new List<NetworkRoad>(_network.Roads);
            foreach (NetworkRoad other in existing)
            {
                // Phase A scope: skip curved roads in segment-segment
                // crossing detection. Curve-vs-segment intersection is
                // Phase B; using the chord here would split at the wrong
                // point.
                if (other.Curve != null) continue;
                Vertex oa = FindVertexById(other.EndA);
                Vertex ob = FindVertexById(other.EndB);
                if (oa == null || ob == null) continue;
                // Skip edges that already share an endpoint with the new
                // edge — they meet at a vertex, not a true crossing.
                if (oa == a || oa == b || ob == a || ob == b) continue;
                if (TrySegmentIntersection(a.Position, b.Position,
                                            oa.Position, ob.Position,
                                            out float tNew, out float tOld, out Vector2 hit))
                {
                    crossings.Add(new Crossing
                    {
                        TNew = tNew,
                        TOld = tOld,
                        Point = hit,
                        OldRoad = other,
                        ExistingVertex = null,
                    });
                }
            }

            // 1b) Existing vertices that lie on the new segment's interior
            //     within VertexOnEdgeTolerance. Reuse them instead of
            //     punching new vertices through them.
            if (VertexOnEdgeTolerance > 0f)
            {
                List<Vertex> verticesSnapshot = new List<Vertex>(_network.Vertices);
                foreach (Vertex v in verticesSnapshot)
                {
                    if (v == a || v == b) continue;
                    float t = ProjectOntoSegment(a.Position, b.Position, v.Position, out Vector2 proj);
                    if (t <= 0.01f || t >= 0.99f) continue;
                    float d = Vector2.Distance(v.Position, proj);
                    if (d > VertexOnEdgeTolerance) continue;
                    crossings.Add(new Crossing
                    {
                        TNew = t,
                        TOld = 0f,
                        Point = v.Position,
                        OldRoad = null,
                        ExistingVertex = v,
                    });
                }
            }

            if (crossings.Count == 0)
            {
                EmitRoadSegment(a, b);
                Rebuild();
                return;
            }

            // 2) Sort by t along the new edge so we walk front-to-back.
            crossings.Sort((x, y) => x.TNew.CompareTo(y.TNew));

            // 3) Build the chain. At each crossing either reuse an
            //    existing vertex or create a new one and split the old road.
            List<Vertex> chain = new List<Vertex>();
            chain.Add(a);
            foreach (Crossing c in crossings)
            {
                Vertex split;
                if (c.ExistingVertex != null)
                {
                    split = c.ExistingVertex;
                }
                else
                {
                    split = CreateAndPlaceVertex(c.Point);
                    SplitRoad(c.OldRoad, split);
                }
                // Defend against duplicate consecutive entries (e.g.
                // a crossing point that coincided with an existing
                // vertex via two different detection paths).
                if (chain[chain.Count - 1] != split) chain.Add(split);
            }
            if (chain[chain.Count - 1] != b) chain.Add(b);

            // 4) Emit each consecutive pair in the chain as a road.
            for (int i = 0; i < chain.Count - 1; i++)
            {
                Vertex p = chain[i];
                Vertex q = chain[i + 1];
                if (p == q) continue;
                if (Vector2.Distance(p.Position, q.Position) < 1e-3f) continue;
                EmitRoadSegment(p, q);
            }
            Rebuild();
        }

        struct Crossing
        {
            public float TNew;
            public float TOld;
            public Vector2 Point;
            public NetworkRoad OldRoad;
            public Vertex ExistingVertex;
        }

        Vertex FindVertexById(string id)
        {
            foreach (Vertex v in _network.Vertices)
                if (v.Id == id) return v;
            return null;
        }

        void EmitRoadSegment(Vertex a, Vertex b)
        {
            // Skip if a road between these two vertices already exists
            // (either direction). Otherwise the chain-builder for a new
            // edge passing through an existing vertex re-emits the road
            // that's already there, causing duplicate meshes to z-fight.
            foreach (NetworkRoad existing in _network.Roads)
            {
                if ((existing.EndA == a.Id && existing.EndB == b.Id) ||
                    (existing.EndA == b.Id && existing.EndB == a.Id))
                {
                    Debug.Log($"[NetworkDesigner] Skipping duplicate road A({a.Id}) → B({b.Id}); " +
                              $"existing road {existing.Id} already connects them.");
                    return;
                }
            }

            NetworkRoad road = new NetworkRoad
            {
                Id = $"r-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                EndA = a.Id,
                EndB = b.Id,
                Classification = RoadClassification.Secondary,
                Profile = BuildDefaultProfile(),
            };
            _network.Roads.Add(road);
            MarkNetworkDirty();
            Debug.Log($"[NetworkDesigner] Road {road.Id}: " +
                      $"A({a.Id}) @ ({a.Position.x:F2}, {a.Position.y:F2}) → " +
                      $"B({b.Id}) @ ({b.Position.x:F2}, {b.Position.y:F2}), " +
                      $"length={Vector2.Distance(a.Position, b.Position):F2}m");
        }

        // Replace `old` (which goes A→B) with two edges A→split, split→B,
        // copying the original profile to both new pieces. The lane-
        // connectivity overrides on the split vertex are left empty so the
        // resolver's default through-connection kicks in for now.
        void SplitRoad(NetworkRoad old, Vertex split)
        {
            // Don't bother if the split happens to coincide with one of the
            // existing endpoints (numerical edge case).
            Vertex oa = FindVertexById(old.EndA);
            Vertex ob = FindVertexById(old.EndB);
            if (oa == null || ob == null) return;
            if (Vector2.Distance(split.Position, oa.Position) < 1e-3f) return;
            if (Vector2.Distance(split.Position, ob.Position) < 1e-3f) return;

            _network.Roads.RemoveAll(r => r.Id == old.Id);

            NetworkRoad left = new NetworkRoad
            {
                Id = $"r-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                EndA = old.EndA,
                EndB = split.Id,
                Classification = old.Classification,
                Profile = old.Profile,
            };
            NetworkRoad right = new NetworkRoad
            {
                Id = $"r-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                EndA = split.Id,
                EndB = old.EndB,
                Classification = old.Classification,
                Profile = old.Profile,
            };
            _network.Roads.Add(left);
            _network.Roads.Add(right);
            MarkNetworkDirty();
            Debug.Log($"[NetworkDesigner] Split road {old.Id} at vertex {split.Id} " +
                      $"→ {left.Id} + {right.Id}");
        }

        // Segment-segment intersection in the XZ (Vector2) plane.
        // Returns true and the (tA, tB, point) of the hit if the two
        // segments cross strictly in their interiors. Endpoint touches
        // are intentionally excluded — those are vertex meetings, not
        // crossings, and the caller already filters shared-endpoint pairs.
        static bool TrySegmentIntersection(Vector2 a0, Vector2 a1,
                                            Vector2 b0, Vector2 b1,
                                            out float tA, out float tB, out Vector2 point)
        {
            tA = 0f; tB = 0f; point = Vector2.zero;
            Vector2 r = a1 - a0;
            Vector2 s = b1 - b0;
            float denom = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(denom) < 1e-9f) return false; // parallel/colinear
            Vector2 diff = b0 - a0;
            tA = (diff.x * s.y - diff.y * s.x) / denom;
            tB = (diff.x * r.y - diff.y * r.x) / denom;
            const float eps = 1e-4f;
            if (tA <= eps || tA >= 1f - eps) return false;
            if (tB <= eps || tB >= 1f - eps) return false;
            point = a0 + r * tA;
            return true;
        }

        // Active road profile selected from the tuning panel. When null,
        // BuildDefaultProfile falls back to the hard-coded 1×1 default.
        RoadProfile _activeProfile;

        public RoadProfile GetActiveProfile() => _activeProfile;

        // Apply a profile picked from the React tuning panel. Stored as
        // the default for FUTURE roads only — existing roads keep the
        // profile they were created with. Use a per-road editor (TBD) if
        // you want to change an existing road's profile.
        public void SetActiveProfile(RoadProfile p)
        {
            _activeProfile = p;
        }

        // Deep-clone via JSON round-trip so each road gets its own
        // independent profile instance (no shared Lane references).
        static RoadProfile CloneProfile(RoadProfile p)
        {
            if (p == null) return null;
            string json = JsonConvert.SerializeObject(p);
            return JsonConvert.DeserializeObject<RoadProfile>(json);
        }

        RoadProfile BuildDefaultProfile()
        {
            if (_activeProfile != null) return CloneProfile(_activeProfile);

            RoadProfile p = new RoadProfile
            {
                Id = $"p-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                AB = new Side(),
                BA = new Side(),
                ShoulderAB = new Shoulder { Width = DefaultShoulderWidth },
                ShoulderBA = new Shoulder { Width = DefaultShoulderWidth },
                Median = null,
            };
            for (int i = 0; i < DefaultAbLanes; i++)
                p.AB.Lanes.Add(new Lane { Id = $"{p.Id}-ab-{i}", Width = DefaultLaneWidth });
            for (int i = 0; i < DefaultBaLanes; i++)
                p.BA.Lanes.Add(new Lane { Id = $"{p.Id}-ba-{i}", Width = DefaultLaneWidth });
            return p;
        }

        /// <summary>Re-render the network from current state.</summary>
        public void Rebuild()
        {
            if (_renderer == null) _renderer = GetComponent<NetworkRenderer>();
            _renderer.Network = _network;
            _renderer.Rebuild();
            // Keep edit-mode setback handles synced to the post-resolve
            // geometry of the selected vertex.
            RefreshSetbackHandlePositions();
            RefreshLateralOffsetHandlePositions();
            // Lane-endpoint markers depend on lane count + profile, so a
            // full respawn is simpler than position-only updates here.
            // If we were mid-armed, respawn in filtered (armed) state
            // and keep the armed reference pointing at the new GameObject.
            if (_selectedVertex != null && CurrentMode == DesignerMode.Edit)
            {
                LaneRef priorArmed = _armedLaneEndpoint?.Lane;
                LaneNode priorArmedNode = _armedLaneEndpoint != null ? _armedLaneEndpoint.Node : LaneNode.A;
                DestroyLaneEndpointMarkers();
                if (priorArmed != null)
                {
                    SpawnLaneEndpointMarkersFiltered(_selectedVertex, priorArmed);
                    if (_laneEndpointMarkers.TryGetValue(LaneEndpointKey(priorArmed, priorArmedNode), out GameObject ago) && ago != null)
                    {
                        _armedLaneEndpoint = ago.GetComponent<LaneEndpointMarker>();
                        if (_armedLaneEndpoint != null)
                        {
                            Color c = LaneMarkerArmedColor; c.a = LaneOverlayFullAlpha;
                            RefreshLaneMarkerColor(_armedLaneEndpoint, c);
                        }
                    }
                }
                else
                {
                    SpawnLaneEndpointMarkers(_selectedVertex);
                }
            }
            else
            {
                DestroyLaneEndpointMarkers();
            }
            PushLaneFlowState();
        }

        // Push current edit state (selection, armed lane, hover) into
        // the LaneFlowRenderer for the selected vertex so it can filter
        // arrows by armed lane and dim/pop by hover.
        void PushLaneFlowState()
        {
            if (_renderer == null) return;
            // We only care about the selected vertex's overlay; other
            // vertices' LFRs render an empty mesh because HasSelection
            // stays false on them.
            if (_selectedVertex == null)
            {
                // Clear edit state on EVERY vertex's LFR — easiest way
                // is to fall through to NetworkRenderer's per-rebuild
                // path, but PushLaneFlowState is also called when
                // selection ends. Iterate every pool entry and reset.
                foreach (Vertex v in _network.Vertices)
                {
                    if (_renderer.TryGetLaneFlowRenderer(v.Id, out LaneFlowRenderer lfr))
                    {
                        if (lfr.HasSelection)
                        {
                            lfr.HasSelection = false;
                            lfr.ArmedFromLaneKey = null;
                            lfr.HoveredLaneKey = null;
                            lfr.Rebuild();
                        }
                    }
                }
                return;
            }

            if (!_renderer.TryGetLaneFlowRenderer(_selectedVertex.Id, out LaneFlowRenderer selfLfr)) return;
            string newArmed = _armedLaneEndpoint != null ? LaneFlowKey(_armedLaneEndpoint.Lane) : null;
            string newHover = _hoverLaneEndpoint != null ? LaneFlowKey(_hoverLaneEndpoint.Lane) : null;
            // Hide all flow arrows while in shift-marking mode — they
            // visually compete with the painted markings the user is
            // authoring. They return as soon as Shift is released.
            bool wantSelection = !_markingShiftMode;
            bool filterChanged = selfLfr.HasSelection != wantSelection || selfLfr.ArmedFromLaneKey != newArmed;
            selfLfr.HasSelection = wantSelection;
            selfLfr.ArmedFromLaneKey = newArmed;
            selfLfr.HoveredLaneKey = newHover;
            if (filterChanged)
            {
                // Arm state changed → arrow set changes → full Rebuild.
                selfLfr.Rebuild();
            }
            else
            {
                // Only hover changed → in-place alpha update.
                selfLfr.RefreshAlphas();
            }
        }

        // -----------------------------------------------------------------
        // Autosave
        // -----------------------------------------------------------------

        // Call from every site that mutates _network.Vertices, _network.Roads,
        // a vertex position, or a road profile/setback override. Doesn't
        // touch disk — just resets the debounce so Update flushes after a
        // brief idle period.
        void MarkNetworkDirty()
        {
            _autosaveDirtySinceRealtime = Time.realtimeSinceStartup;
        }

        string ResolveAutosavePath()
        {
            if (!string.IsNullOrEmpty(AutosavePath)) return AutosavePath;
#if UNITY_EDITOR
            return System.IO.Path.Combine(Application.dataPath, "..", "NetworkAutosave.json");
#else
            return System.IO.Path.Combine(Application.persistentDataPath, "NetworkAutosave.json");
#endif
        }

        void SaveNetwork()
        {
            try
            {
                string path = ResolveAutosavePath();
                string json = JsonConvert.SerializeObject(_network, AutosaveSettings);
                System.IO.File.WriteAllText(path, json);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[NetworkDesigner] Autosave failed: {ex.Message}");
            }
        }

        Network TryLoadNetwork(out int loadedVertexCount)
        {
            loadedVertexCount = 0;
            try
            {
                string path = ResolveAutosavePath();
                if (!System.IO.File.Exists(path)) return null;
                string json = System.IO.File.ReadAllText(path);
                Network n = JsonConvert.DeserializeObject<Network>(json, AutosaveSettings);
                if (n == null) return null;
                if (n.Vertices == null) n.Vertices = new List<Vertex>();
                if (n.Roads == null) n.Roads = new List<NetworkRoad>();
                loadedVertexCount = n.Vertices.Count;
                return n;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[NetworkDesigner] Autosave load failed: {ex.Message} — starting empty.");
                return null;
            }
        }

        // Shared serializer settings. The custom Vector2 converter stops
        // Newtonsoft from chasing Vector2's derived properties (normalized,
        // magnitude, sqrMagnitude) which would otherwise either trigger a
        // self-referencing loop on `normalized` or just produce noisy JSON.
        static JsonSerializerSettings _autosaveSettings;
        static JsonSerializerSettings AutosaveSettings
        {
            get
            {
                if (_autosaveSettings == null)
                {
                    _autosaveSettings = new JsonSerializerSettings
                    {
                        Formatting = Formatting.Indented,
                        Converters = new List<JsonConverter> { new Vector2JsonConverter() },
                        NullValueHandling = NullValueHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                    };
                }
                return _autosaveSettings;
            }
        }

        // Reads / writes Vector2 as { "x": ..., "y": ... }.
        class Vector2JsonConverter : JsonConverter<Vector2>
        {
            public override void WriteJson(JsonWriter writer, Vector2 value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("x");
                writer.WriteValue(value.x);
                writer.WritePropertyName("y");
                writer.WriteValue(value.y);
                writer.WriteEndObject();
            }

            public override Vector2 ReadJson(JsonReader reader, System.Type objectType,
                Vector2 existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                Newtonsoft.Json.Linq.JObject jo = Newtonsoft.Json.Linq.JObject.Load(reader);
                float x = jo["x"]?.ToObject<float>() ?? 0f;
                float y = jo["y"]?.ToObject<float>() ?? 0f;
                return new Vector2(x, y);
            }
        }

        // -----------------------------------------------------------------
        // Picking
        // -----------------------------------------------------------------

        public Vector2? PickGround()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            if (Mathf.Abs(ray.direction.y) < 1e-6f) return null;
            float t = (GroundY - ray.origin.y) / ray.direction.y;
            if (t < 0f) return null;
            Vector3 hit = ray.origin + ray.direction * t;
            return ApplyGuidesOrGrid(new Vector2(hit.x, hit.z));
        }

        // Snap pipeline: collect guide rays for the current state, find the
        // closest one within tolerance, and return its projection. If no
        // guide is in range, fall back to grid snap.
        //
        // The guide wins over the grid intentionally — guides are about
        // aligning with existing geometry, which is usually more important
        // than landing on a grid line.
        Vector2 ApplyGuidesOrGrid(Vector2 cursor)
        {
            CollectGuides(cursor);
            _activeGuides.Clear();

            // Two-pass selection so we can both pick a single snap target
            // AND visualize every guide tied with it (within TIE_EPS).
            // Each candidate carries its own pull radius. The "test point"
            // is either the raw cursor (for angle snap; gives smooth mouse
            // pull) or the grid-snapped cursor (for proximity guides;
            // keeps the snap tied to puck position).
            Vector2 cursorSnapped = ApplySnap(cursor);
            const float TIE_EPS = 1e-3f; // 1mm — tighter than float noise

            // Pass 1: find the smallest in-range distance.
            float bestDist = float.PositiveInfinity;
            Vector2 bestProj = cursor;
            int bestIdx = -1;
            for (int i = 0; i < _guideCandidates.Count; i++)
            {
                GuideRay g = _guideCandidates[i];
                float pull = g.Distance > 0f ? g.Distance : GuideSnapDistance;
                Vector2 testPoint = g.TestAgainstSnappedCursor ? cursorSnapped : cursor;
                if (!TryProjectOntoRay(testPoint, g.Origin, g.Direction, out Vector2 proj)) continue;
                float d = Vector2.Distance(testPoint, proj);
                if (d > pull) continue;
                if (d < bestDist)
                {
                    bestDist = d;
                    bestProj = proj;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) return ApplySnap(cursor);

            // Pass 2: collect every guide whose in-range distance ties the
            // best within epsilon. The first one in this list is the snap
            // target; the rest are drawn alongside for clarity.
            for (int i = 0; i < _guideCandidates.Count; i++)
            {
                GuideRay g = _guideCandidates[i];
                float pull = g.Distance > 0f ? g.Distance : GuideSnapDistance;
                Vector2 testPoint = g.TestAgainstSnappedCursor ? cursorSnapped : cursor;
                if (!TryProjectOntoRay(testPoint, g.Origin, g.Direction, out Vector2 proj)) continue;
                float d = Vector2.Distance(testPoint, proj);
                if (d > pull) continue;
                if (d <= bestDist + TIE_EPS) _activeGuides.Add(g);
            }

            // If the snap-target guide happens to be collinear with a grid
            // axis (cardinal or 45°, etc.), grid-snap the projection and
            // use that — but only if the grid-snapped point still lies on
            // the guide line. For non-aligned guides, the grid-snapped
            // point falls off the line so we stick with the pure guide
            // projection.
            GuideRay primary = _guideCandidates[bestIdx];
            Vector2 gridSnapped = ApplySnap(bestProj);
            float distGsToLine = PerpendicularDistanceFromLine(
                gridSnapped, primary.Origin, primary.Direction);
            if (distGsToLine < 1e-3f) return gridSnapped;
            return bestProj;
        }

        static float PerpendicularDistanceFromLine(Vector2 p, Vector2 origin, Vector2 dir)
        {
            Vector2 toP = p - origin;
            float along = Vector2.Dot(toP, dir);
            Vector2 perp = toP - dir * along;
            return perp.magnitude;
        }

        void CollectGuides(Vector2 cursor)
        {
            _guideCandidates.Clear();
            CollectAngleGuides();
            CollectExtensionGuides(cursor);
            CollectPerpendicularGuides(cursor);
        }

        // Extension guide: for every existing vertex within
        // ExtensionGuideRange of the grid-snapped cursor, emit a half-ray
        // continuing each incident edge BEYOND that vertex (away from the
        // edge's other endpoint). All in-range vertices emit; the framework
        // picks the closest winning projection — so "closest wins" emerges
        // from the snap competition rather than from filtering candidates
        // up front. That way the full configured range is usable even when
        // multiple vertices are in the scene.
        void CollectExtensionGuides(Vector2 cursor)
        {
            if (CurrentMode != DesignerMode.Create) return;
            if (ExtensionGuideRange <= 0f) return;
            if (_network == null) return;

            Vector2 proxRef = ApplySnap(cursor);
            foreach (Vertex v in _network.Vertices)
            {
                if (Vector2.Distance(v.Position, proxRef) > ExtensionGuideRange) continue;

                foreach (NetworkRoad r in _network.Roads)
                {
                    RoadEnd end;
                    Vertex other;
                    if (r.EndA == v.Id) { end = RoadEnd.A; other = FindVertexById(r.EndB); }
                    else if (r.EndB == v.Id) { end = RoadEnd.B; other = FindVertexById(r.EndA); }
                    else continue;
                    if (other == null) continue;

                    // Curve-aware: use bezier tangent for curved roads so
                    // the extension lines up with the curve's heading at v.
                    Vector2 outDir = GeometryResolver.OutwardDirection(r, end, v, other);
                    if (outDir.sqrMagnitude < 1e-6f) continue;

                    _guideCandidates.Add(new GuideRay
                    {
                        Origin = v.Position,
                        Direction = outDir,
                        Distance = GuideSnapDistance,
                        TestAgainstSnappedCursor = true,
                    });
                }
            }
        }

        // Perpendicular guide: same idea as extension, but the ray runs at
        // 90° to each incident edge. Two rays per edge (one for each
        // perpendicular direction) so the cursor can be on either side.
        void CollectPerpendicularGuides(Vector2 cursor)
        {
            if (CurrentMode != DesignerMode.Create) return;
            if (PerpendicularGuideRange <= 0f) return;
            if (_network == null) return;

            Vector2 proxRef = ApplySnap(cursor);
            foreach (Vertex v in _network.Vertices)
            {
                if (Vector2.Distance(v.Position, proxRef) > PerpendicularGuideRange) continue;

                foreach (NetworkRoad r in _network.Roads)
                {
                    RoadEnd end;
                    Vertex other;
                    if (r.EndA == v.Id) { end = RoadEnd.A; other = FindVertexById(r.EndB); }
                    else if (r.EndB == v.Id) { end = RoadEnd.B; other = FindVertexById(r.EndA); }
                    else continue;
                    if (other == null) continue;

                    Vector2 dir = GeometryResolver.OutwardDirection(r, end, v, other);
                    if (dir.sqrMagnitude < 1e-6f) continue;
                    Vector2 perp = new Vector2(-dir.y, dir.x);

                    _guideCandidates.Add(new GuideRay
                    {
                        Origin = v.Position,
                        Direction = perp,
                        Distance = GuideSnapDistance,
                        TestAgainstSnappedCursor = true,
                    });
                    _guideCandidates.Add(new GuideRay
                    {
                        Origin = v.Position,
                        Direction = -perp,
                        Distance = GuideSnapDistance,
                        TestAgainstSnappedCursor = true,
                    });
                }
            }
        }

        // Angle snapping fan: for each existing edge meeting the current
        // anchor, emit a ray every AngleSnapDegrees around the anchor for
        // the full 360°. Reference direction is the existing edge's
        // outgoing direction at the anchor (i.e. (anchor - other).norm).
        // 0° = exact extension; subsequent rays rotate counter-clockwise.
        void CollectAngleGuides()
        {
            if (CurrentMode != DesignerMode.Create) return;
            if (!AngleSnapEnabled || AngleSnapDegrees <= 0f) return;
            if (_network == null) return;

            // Anchor for the angle fan: either the active straight-tool
            // anchor, or the fillet-tool's click-1 vertex (so clicks 2 and
            // 3 get the same angular guidance from existing edges).
            Vertex anchor = null;
            if (_buildState == BuildState.EdgeFromVertex && _edgeAnchor != null)
                anchor = _edgeAnchor;
            else if (IsCurveTool(CurrentTool) && _filletStart != null)
                anchor = _filletStart;
            if (anchor == null) return;

            Vector2 anchorPos = anchor.Position;
            float step = AngleSnapDegrees;
            int n = Mathf.Max(1, Mathf.RoundToInt(360f / step));

            foreach (NetworkRoad r in _network.Roads)
            {
                RoadEnd end;
                Vertex other;
                if (r.EndA == anchor.Id) { end = RoadEnd.A; other = FindVertexById(r.EndB); }
                else if (r.EndB == anchor.Id) { end = RoadEnd.B; other = FindVertexById(r.EndA); }
                else continue;
                if (other == null) continue;

                Vector2 forward = GeometryResolver.OutwardDirection(r, end, anchor, other);
                if (forward.sqrMagnitude < 1e-6f) continue;

                for (int i = 0; i < n; i++)
                {
                    float rad = (i * step) * Mathf.Deg2Rad;
                    float cs = Mathf.Cos(rad);
                    float sn = Mathf.Sin(rad);
                    Vector2 d = new Vector2(
                        forward.x * cs - forward.y * sn,
                        forward.x * sn + forward.y * cs);
                    _guideCandidates.Add(new GuideRay
                    {
                        Origin = anchorPos,
                        Direction = d,
                        Distance = AngleSnapDistance,
                    });
                }
            }
        }

        // Project cursor onto a half-ray starting at `origin` going in
        // `dir`. Returns false if the cursor is behind the origin (so the
        // caller skips this candidate — important for proximity guides
        // where falling back to "snap to vertex" would feel wrong).
        // Angle-snap rays cover 360° so for any cursor location there's
        // always at least one ray with the cursor in front of it.
        static bool TryProjectOntoRay(Vector2 cursor, Vector2 origin, Vector2 dir, out Vector2 proj)
        {
            float t = Vector2.Dot(cursor - origin, dir);
            if (t < 0f)
            {
                proj = origin;
                return false;
            }
            proj = origin + dir * t;
            return true;
        }

        // Visualize the active snap guide as a thin dashed world-space line.
        // Hidden when no guide is currently being snapped to. Called from
        // Update.
        //
        // Dashing is done by tiling a tiny generated texture (opaque/clear
        // alternating columns) along the line. The material uses
        // Sprites/Default so vertex color (GuideLineColor) is multiplied
        // onto the alpha texture for tinting + the per-color alpha.
        void UpdateGuideLineVisual()
        {
            // Snap guides only make sense during placement (Create mode).
            // Switching to Edit mode stops calling ApplyGuidesOrGrid, so
            // _activeGuides would hold whatever the last Create-mode
            // frame computed — leaving the dashed rays "stuck on" after
            // a Tab into Edit. Clear them here so the per-frame draw
            // loop disables every LineRenderer in Edit mode.
            if (CurrentMode != DesignerMode.Create) _activeGuides.Clear();

            int activeCount = _activeGuides.Count;

            // Make sure the pool has at least `activeCount` LineRenderers.
            while (_guideLineGos.Count < activeCount) CreateGuideLineGo();

            // Y placement: sit just above the ground grid so the dashed
            // guide visually overlaps grid lines at oblique camera angles
            // (instead of floating up at MarkerHeight). Grid lines live at
            // `Grid.transform.position.y + Grid.YOffset` in world space
            // — so we need to track the parent transform, not just YOffset,
            // or guides will float when GroundGrid is attached to a non-
            // origin GameObject (e.g. the Ground plane).
            float gridY = 0f;
            if (Grid != null) gridY = Grid.transform.position.y + Grid.YOffset;
            float y = gridY + 0.001f;
            const float HALF_LEN = 1000f;

            // Set tile count so one dash+gap cycle = GuideDashPeriod meters
            // along the line, independent of camera distance or line length.
            // Shared material — set once before the loop.
            float period = Mathf.Max(0.05f, GuideDashPeriod);
            float tile = (HALF_LEN * 2f) / period;
            if (_guideLineMaterial != null)
            {
                _guideLineMaterial.mainTextureScale = new Vector2(tile, 1f);
            }

            for (int i = 0; i < _guideLineGos.Count; i++)
            {
                LineRenderer lr = _guideLines[i];
                if (lr == null) continue;
                if (i >= activeCount)
                {
                    lr.enabled = false;
                    continue;
                }

                GuideRay g = _activeGuides[i];
                // Practically-infinite line through the origin in both
                // directions of the ray. 1km each side handles any sane scene.
                Vector3 p0 = new Vector3(g.Origin.x - g.Direction.x * HALF_LEN, y,
                                         g.Origin.y - g.Direction.y * HALF_LEN);
                Vector3 p1 = new Vector3(g.Origin.x + g.Direction.x * HALF_LEN, y,
                                         g.Origin.y + g.Direction.y * HALF_LEN);

                lr.enabled = true;
                lr.startWidth = GuideLineWidth;
                lr.endWidth = GuideLineWidth;
                lr.startColor = GuideLineColor;
                lr.endColor = GuideLineColor;
                lr.SetPosition(0, p0);
                lr.SetPosition(1, p1);
            }
        }

        void CreateGuideLineGo()
        {
            int idx = _guideLineGos.Count;
            GameObject go = new GameObject($"SnapGuideLine_{idx}");
            go.transform.SetParent(transform, worldPositionStays: false);
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.textureMode = LineTextureMode.Tile;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = GetGuideLineMaterial();
            _guideLineGos.Add(go);
            _guideLines.Add(lr);
        }

        Material GetGuideLineMaterial()
        {
            if (_guideLineMaterial != null) return _guideLineMaterial;
            // Sprites/Default is built-in and supports vertex-color * texture
            // (alpha) tinting, which is exactly what we want here.
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            _guideLineMaterial = new Material(sh) { name = "SnapGuideMat" };
            _guideLineMaterial.mainTexture = GetGuideDashTexture();
            return _guideLineMaterial;
        }

        // Procedural 1-row dash texture: a run of opaque pixels followed by
        // a run of transparent pixels, then repeats. Point-filtered + repeat
        // wrap so the LineRenderer's Tile mode produces crisp dashes.
        Texture2D GetGuideDashTexture()
        {
            if (_guideDashTexture != null) return _guideDashTexture;
            const int W = 16;
            const int ON = 8; // 50% duty cycle (4-on, 4-off would dash short)
            _guideDashTexture = new Texture2D(W, 1, TextureFormat.RGBA32, mipChain: false)
            {
                name = "GuideDashTex",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };
            Color32[] pix = new Color32[W];
            Color32 on = new Color32(255, 255, 255, 255);
            Color32 off = new Color32(255, 255, 255, 0);
            for (int i = 0; i < W; i++) pix[i] = i < ON ? on : off;
            _guideDashTexture.SetPixels32(pix);
            _guideDashTexture.Apply(updateMipmaps: false);
            return _guideDashTexture;
        }

        // Dashed construction guideline for the fillet tool. Shares the
        // dash texture + material style with the snap guides so the
        // construction language reads as "guide", not "preview".
        void UpdateFilletGuideLine()
        {
            bool active = CurrentMode == DesignerMode.Create
                          && IsCurveTool(CurrentTool)
                          && _filletStart != null
                          && (_filletState == FilletState.AwaitingClick2
                              || _filletState == FilletState.AwaitingClick3);
            if (!active)
            {
                if (_filletGuideLine != null) _filletGuideLine.enabled = false;
                return;
            }

            Vector2? ground = PickGround();
            if (!ground.HasValue)
            {
                if (_filletGuideLine != null) _filletGuideLine.enabled = false;
                return;
            }

            if (_filletGuideLineGo == null) CreateFilletGuideLineGo();

            // Sit at the grid level so the line reads as flat on the
            // ground, like the snap guides.
            float y = (Grid != null ? Grid.YOffset : 0f) + 0.001f;
            Vector3 click1 = new Vector3(_filletStart.Position.x, y, _filletStart.Position.y);
            Vector3 cursor = new Vector3(ground.Value.x, y, ground.Value.y);

            float totalLen;
            if (_filletState == FilletState.AwaitingClick2)
            {
                _filletGuideLine.positionCount = 2;
                _filletGuideLine.SetPosition(0, click1);
                _filletGuideLine.SetPosition(1, cursor);
                totalLen = Vector3.Distance(click1, cursor);
            }
            else // AwaitingClick3
            {
                Vector3 corner = new Vector3(_filletCorner.x, y, _filletCorner.y);
                _filletGuideLine.positionCount = 3;
                _filletGuideLine.SetPosition(0, click1);
                _filletGuideLine.SetPosition(1, corner);
                _filletGuideLine.SetPosition(2, cursor);
                totalLen = Vector3.Distance(click1, corner) + Vector3.Distance(corner, cursor);
            }

            _filletGuideLine.enabled = true;
            _filletGuideLine.startWidth = GuideLineWidth;
            _filletGuideLine.endWidth = GuideLineWidth;
            _filletGuideLine.startColor = GuideLineColor;
            _filletGuideLine.endColor = GuideLineColor;

            float period = Mathf.Max(0.05f, GuideDashPeriod);
            if (_filletGuideLineMaterial != null)
            {
                _filletGuideLineMaterial.mainTextureScale = new Vector2(totalLen / period, 1f);
            }
        }

        void CreateFilletGuideLineGo()
        {
            _filletGuideLineGo = new GameObject("FilletGuideLine");
            _filletGuideLineGo.transform.SetParent(transform, worldPositionStays: false);
            _filletGuideLine = _filletGuideLineGo.AddComponent<LineRenderer>();
            _filletGuideLine.useWorldSpace = true;
            _filletGuideLine.textureMode = LineTextureMode.Tile;
            _filletGuideLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _filletGuideLine.receiveShadows = false;

            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            _filletGuideLineMaterial = new Material(sh) { name = "FilletGuideLineMat" };
            _filletGuideLineMaterial.mainTexture = GetGuideDashTexture();
            _filletGuideLine.sharedMaterial = _filletGuideLineMaterial;
        }

        // Snap a ground-plane position to the grid if enabled.
        //
        // Two-tier behavior, per axis independently:
        //   - Nearest major line wins when the cursor is within
        //     MajorSnapRange × minorSpacing of it.
        //   - Otherwise the axis snaps to the nearest minor line.
        //
        // Treating each axis independently means a cursor near a major
        // line in X but far from any major line in Z gets pulled in X and
        // snapped to a minor line in Z — which feels natural for the
        // "stronger pull near major" UX.
        Vector2 ApplySnap(Vector2 p)
        {
            if (!SnapToGrid) return p;
            if (Grid == null || !Grid.Enabled) return p;
            float s = Grid.Spacing;
            if (s <= 0f) return p;

            int every = Mathf.Max(1, Grid.MajorEvery);
            float S = s * every;
            float majorThreshold = Mathf.Max(0f, MajorSnapRange) * s;

            return new Vector2(SnapAxis(p.x, s, S, majorThreshold),
                               SnapAxis(p.y, s, S, majorThreshold));
        }

        static float SnapAxis(float v, float minor, float major, float majorThreshold)
        {
            float majorTarget = Mathf.Round(v / major) * major;
            if (Mathf.Abs(v - majorTarget) <= majorThreshold) return majorTarget;
            return Mathf.Round(v / minor) * minor;
        }

        Vertex PickVertex()
        {
            if (PickCamera == null) return null;
            Ray ray = PickCamera.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 10000f))
            {
                VertexMarker m = hit.collider.GetComponentInParent<VertexMarker>();
                if (m != null) return m.Vertex;
            }
            return null;
        }

        // -----------------------------------------------------------------
        // Visuals
        // -----------------------------------------------------------------

        void SpawnVertexMarker(Vertex v)
        {
            // Cylinder primitive is 2 units tall and 1 unit wide, so to land
            // at MarkerThickness meters tall we use Y scale = thickness/2,
            // and X/Z scale = MarkerDiameter for the puck's diameter.
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = $"VertexMarker_{v.Id}";
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position = new Vector3(v.Position.x, MarkerHeight, v.Position.y);
            go.transform.localScale = new Vector3(MarkerDiameter, MarkerThickness * 0.5f, MarkerDiameter);

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = GetMarkerMaterial();

            VertexMarker marker = go.AddComponent<VertexMarker>();
            marker.Vertex = v;

            if (!ShowVertexMarkers) go.SetActive(false);

            _vertexMarkers[v.Id] = go;
        }

        // Toggles vertex markers on/off (the V hotkey + palette button
        // both call this). Iterates every spawned marker rather than just
        // mutating ShowVertexMarkers so the change is immediate without
        // forcing a Rebuild that would respawn vertex puck GameObjects.
        public void SetVertexMarkersVisible(bool visible)
        {
            ShowVertexMarkers = visible;
            foreach (KeyValuePair<string, GameObject> kv in _vertexMarkers)
            {
                if (kv.Value != null) kv.Value.SetActive(visible);
            }
            // Re-honor the "hide selected vertex while editing" rule
            // (otherwise toggling vertex view back ON would un-hide
            // the currently-selected vertex's puck and let it occlude
            // the per-vertex edit overlays).
            if (visible && _selectedVertex != null)
                SetVertexMarkerVisible(_selectedVertex.Id, false);
        }

        Material GetMarkerMaterial()
        {
            if (_markerMaterial == null) _markerMaterial = CreateLitMarker(MarkerColor, "VertexMarkerMat");
            return _markerMaterial;
        }

        Material GetMarkerHoverMaterial()
        {
            if (_markerHoverMaterial == null) _markerHoverMaterial = CreateLitMarker(MarkerHoverColor, "VertexMarkerHoverMat");
            return _markerHoverMaterial;
        }

        Material GetMarkerActiveMaterial()
        {
            if (_markerActiveMaterial == null) _markerActiveMaterial = CreateLitMarker(MarkerActiveColor, "VertexMarkerActiveMat");
            return _markerActiveMaterial;
        }

        // Called by the tuning system when a marker color changes at runtime.
        // We update the existing cached Material instances in place so all
        // markers using them re-render with the new color without us having
        // to touch each MeshRenderer.
        public void InvalidateMarkerMaterials()
        {
            if (_markerMaterial != null) _markerMaterial.color = MarkerColor;
            if (_markerHoverMaterial != null) _markerHoverMaterial.color = MarkerHoverColor;
            if (_markerActiveMaterial != null) _markerActiveMaterial.color = MarkerActiveColor;
        }

        // Called by the tuning system when puck dimensions change. The
        // existing primitives were instantiated with a fixed localScale, so
        // we throw them all out and respawn with the current MarkerDiameter
        // / MarkerThickness / MarkerHeight values.
        public void RebuildMarkers()
        {
            foreach (KeyValuePair<string, GameObject> kvp in _vertexMarkers)
            {
                GameObject go = kvp.Value;
                if (go == null) continue;
                if (Application.isPlaying) Destroy(go);
                else DestroyImmediate(go);
            }
            _vertexMarkers.Clear();
            if (_network == null) return;
            foreach (Vertex v in _network.Vertices)
            {
                SpawnVertexMarker(v);
                RefreshMarker(v);
            }
            // Re-apply the hide-while-selected state after the rebuild,
            // since freshly-spawned markers default to active.
            if (_selectedVertex != null)
                SetVertexMarkerVisible(_selectedVertex.Id, false);
        }

        // Standard shader so the marker puck shades + casts shadows.
        // Slight glossiness picks up a subtle highlight against the dim
        // ambient — reads as a tangible disc rather than a flat sprite.
        static Material CreateLitMarker(Color c, string name)
        {
            Material m = new Material(Shader.Find("Standard")) { name = name, color = c };
            m.SetFloat("_Glossiness", 0.25f);
            m.SetFloat("_Metallic", 0f);
            return m;
        }

        // Translucent placement preview shown in Create mode at the snapped
        // cursor position. Hides when:
        //   - not in Create mode
        //   - cursor isn't on the ground plane
        //   - cursor is over an existing vertex (clicking would pick it,
        //     not place a new one — showing a ghost there would lie)
        //   - GhostPuckAlpha == 0
        void UpdateGhostPuck()
        {
            bool wantShow = CurrentMode == DesignerMode.Create && GhostPuckAlpha > 0f;
            Vector2? ground = wantShow ? PickGround() : null;
            bool overVertex = wantShow && PickVertex() != null;
            bool shouldShow = wantShow && ground.HasValue && !overVertex;

            if (!shouldShow)
            {
                if (_ghostPuckGo != null) _ghostPuckGo.SetActive(false);
                return;
            }

            if (_ghostPuckGo == null) CreateGhostPuck();

            _ghostPuckGo.SetActive(true);
            _ghostPuckGo.transform.position =
                new Vector3(ground.Value.x, MarkerHeight, ground.Value.y);
            _ghostPuckGo.transform.localScale =
                new Vector3(MarkerDiameter, MarkerThickness * 0.5f, MarkerDiameter);

            // Keep the ghost material's tint synced to MarkerColor + current
            // alpha so changing either via tuning takes effect immediately.
            if (_ghostPuckMaterial != null)
            {
                Color c = MarkerColor;
                c.a = GhostPuckAlpha;
                _ghostPuckMaterial.color = c;
            }
        }

        void CreateGhostPuck()
        {
            _ghostPuckGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _ghostPuckGo.name = "VertexGhostPuck";
            _ghostPuckGo.transform.SetParent(transform, worldPositionStays: false);
            // No collider — must not interfere with raycast picking. The
            // primitive shipped a MeshCollider that we throw away.
            Collider col = _ghostPuckGo.GetComponent<Collider>();
            if (col != null)
            {
                if (Application.isPlaying) Destroy(col);
                else DestroyImmediate(col);
            }
            MeshRenderer mr = _ghostPuckGo.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sharedMaterial = GetGhostPuckMaterial();
        }

        // Standard shader configured for alpha blending — same look as the
        // marker pucks but translucent. Keeps the visual language consistent
        // (a ghost is just a translucent version of the real thing).
        Material GetGhostPuckMaterial()
        {
            if (_ghostPuckMaterial != null) return _ghostPuckMaterial;
            Material m = new Material(Shader.Find("Standard")) { name = "VertexGhostMat" };
            Color c = MarkerColor;
            c.a = GhostPuckAlpha;
            m.color = c;
            m.SetFloat("_Mode", 3f); // Transparent
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = 3000;
            m.SetFloat("_Glossiness", 0.1f);
            m.SetFloat("_Metallic", 0f);
            _ghostPuckMaterial = m;
            return _ghostPuckMaterial;
        }

        // Ghost for the Roundabout tool. Three concentric circles centered
        // on the hovered vertex (if any) or the snapped cursor — outer
        // edge of asphalt, centerline (where RoundaboutRadius lands),
        // inner edge. Hidden when the tool isn't active or the cursor is
        // off the ground.
        void UpdateRoundaboutGhost()
        {
            bool wantShow = CurrentMode == DesignerMode.Create
                            && CurrentTool == BuildTool.Roundabout;

            Vector3? center = null;
            bool invalidSite = false;
            if (wantShow)
            {
                Vertex over = PickVertex();
                if (over != null)
                {
                    center = new Vector3(over.Position.x, MarkerHeight, over.Position.y);
                    // Same predicate as the click path so the ghost color
                    // matches what would actually happen on click.
                    List<NetworkRoad> incident = new List<NetworkRoad>();
                    if (_network != null)
                    {
                        foreach (NetworkRoad rd in _network.Roads)
                        {
                            if (rd.EndA == over.Id || rd.EndB == over.Id) incident.Add(rd);
                        }
                    }
                    string _reason;
                    invalidSite = !IsValidRoundaboutSite(incident, out _reason);
                }
                else
                {
                    Vector2? ground = PickGround();
                    if (ground.HasValue)
                    {
                        center = new Vector3(ground.Value.x, MarkerHeight, ground.Value.y);
                    }
                }
            }

            if (!center.HasValue)
            {
                if (_roundaboutGhostCenter != null) _roundaboutGhostCenter.enabled = false;
                if (_roundaboutGhostOuter != null) _roundaboutGhostOuter.enabled = false;
                if (_roundaboutGhostInner != null) _roundaboutGhostInner.enabled = false;
                return;
            }

            if (_roundaboutGhostGo == null) CreateRoundaboutGhost();

            // Compute road width from the same profile-building logic as
            // BuildRoundabout so the ghost matches what gets built.
            float r = Mathf.Max(0.1f, RoundaboutRadius);
            float roadWidth = 2f * DefaultShoulderWidth + RoundaboutLanes * DefaultLaneWidth;
            float halfW = roadWidth * 0.5f;
            float outerR = r + halfW;
            float innerR = r - halfW;

            UpdateGhostRing(_roundaboutGhostCenter, center.Value, r);
            UpdateGhostRing(_roundaboutGhostOuter, center.Value, outerR);
            if (innerR > 0.05f)
            {
                UpdateGhostRing(_roundaboutGhostInner, center.Value, innerR);
            }
            else
            {
                _roundaboutGhostInner.enabled = false;
            }

            if (_roundaboutGhostMaterial != null)
            {
                Color c = invalidSite ? new Color(1f, 0.25f, 0.25f) : MarkerColor;
                c.a = Mathf.Max(0.3f, GhostPuckAlpha);
                _roundaboutGhostMaterial.color = c;
            }
        }

        static void UpdateGhostRing(LineRenderer lr, Vector3 center, float radius)
        {
            const int SAMPLES = 48;
            lr.enabled = true;
            lr.loop = true;
            lr.positionCount = SAMPLES;
            for (int i = 0; i < SAMPLES; i++)
            {
                float a = (i / (float)SAMPLES) * Mathf.PI * 2f;
                Vector3 p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
                lr.SetPosition(i, p);
            }
        }

        void CreateRoundaboutGhost()
        {
            _roundaboutGhostGo = new GameObject("RoundaboutGhost");
            _roundaboutGhostGo.transform.SetParent(transform, worldPositionStays: false);
            _roundaboutGhostMaterial = new Material(Shader.Find("Unlit/Color")) { name = "RoundaboutGhostMat" };

            _roundaboutGhostCenter = MakeRoundaboutGhostLine("Center", 0.2f);
            _roundaboutGhostOuter = MakeRoundaboutGhostLine("Outer", 0.4f);
            _roundaboutGhostInner = MakeRoundaboutGhostLine("Inner", 0.4f);
        }

        LineRenderer MakeRoundaboutGhostLine(string name, float width)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(_roundaboutGhostGo.transform, worldPositionStays: false);
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = _roundaboutGhostMaterial;
            return lr;
        }

        // Dashed range circle around the Create-mode cursor. Visual ruler
        // only — doesn't gate any snap logic. Shares the snap-guide's dash
        // texture but uses its own material instance so the texture-tile
        // count can be set independently from the snap guides'.
        void UpdateRangeCircle()
        {
            bool wantShow = RangeCircleEnabled
                            && CurrentMode == DesignerMode.Create
                            && RangeCircleRadius > 0.1f;

            Vector2? ground = wantShow ? PickGround() : null;
            if (!ground.HasValue)
            {
                if (_rangeCircleLine != null) _rangeCircleLine.enabled = false;
                return;
            }

            if (_rangeCircleGo == null) CreateRangeCircle();

            // Y placement same as the snap guides — sit just above grid.
            float gridY = 0f;
            if (Grid != null) gridY = Grid.transform.position.y + Grid.YOffset;
            float y = gridY + 0.001f;

            const int SAMPLES = 96;
            _rangeCircleLine.enabled = true;
            _rangeCircleLine.loop = true;
            _rangeCircleLine.positionCount = SAMPLES;
            _rangeCircleLine.startWidth = RangeCircleWidth;
            _rangeCircleLine.endWidth = RangeCircleWidth;
            // Composite the picker's RGB with the dedicated alpha slider
            // so users can tune transparency without opening the color popup.
            Color tinted = RangeCircleColor;
            tinted.a = RangeCircleOpacity;
            _rangeCircleLine.startColor = tinted;
            _rangeCircleLine.endColor = tinted;

            Vector3 c = new Vector3(ground.Value.x, y, ground.Value.y);
            for (int i = 0; i < SAMPLES; i++)
            {
                float a = (i / (float)SAMPLES) * Mathf.PI * 2f;
                _rangeCircleLine.SetPosition(i,
                    c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * RangeCircleRadius);
            }

            // Tile the dash texture so one dash+gap cycle = RangeCircleDashPeriod
            // meters along the circumference, independent of the radius.
            float period = Mathf.Max(0.05f, RangeCircleDashPeriod);
            float circumference = 2f * Mathf.PI * RangeCircleRadius;
            float tile = circumference / period;
            if (_rangeCircleMaterial != null)
            {
                _rangeCircleMaterial.mainTextureScale = new Vector2(tile, 1f);
            }
        }

        void CreateRangeCircle()
        {
            _rangeCircleGo = new GameObject("RangeCircle");
            _rangeCircleGo.transform.SetParent(transform, worldPositionStays: false);
            _rangeCircleLine = _rangeCircleGo.AddComponent<LineRenderer>();
            _rangeCircleLine.useWorldSpace = true;
            _rangeCircleLine.textureMode = LineTextureMode.Tile;
            _rangeCircleLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _rangeCircleLine.receiveShadows = false;

            // Reuse the snap-guide dash texture but on its own material
            // instance so RangeCircleDashPeriod doesn't fight with the
            // snap guides' period.
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            _rangeCircleMaterial = new Material(sh) { name = "RangeCircleMat" };
            _rangeCircleMaterial.mainTexture = GetGuideDashTexture();
            _rangeCircleLine.sharedMaterial = _rangeCircleMaterial;
        }

        void UpdatePreviewLine()
        {
            // Two preview cases (the AwaitingClick2 case moved to the
            // dashed fillet guideline so we don't double-draw):
            //   1) Straight tool, EdgeFromVertex → straight line anchor→cursor
            //   2) Fillet tool, AwaitingClick3   → cubic preview through corner
            //      to cursor (cursor is the prospective end)
            bool active =
                (_buildState == BuildState.EdgeFromVertex && _edgeAnchor != null)
                || (IsCurveTool(CurrentTool) && _filletState == FilletState.AwaitingClick3 && _filletStart != null);

            if (!active)
            {
                if (_previewLine != null) _previewLine.enabled = false;
                return;
            }

            Vector2? ground = PickGround();
            if (!ground.HasValue)
            {
                if (_previewLine != null) _previewLine.enabled = false;
                return;
            }

            if (_previewLineGo == null)
            {
                _previewLineGo = new GameObject("EdgePreview");
                _previewLineGo.transform.SetParent(transform, worldPositionStays: false);
                _previewLine = _previewLineGo.AddComponent<LineRenderer>();
                _previewLine.useWorldSpace = true;
                _previewLine.startWidth = PreviewLineWidth;
                _previewLine.endWidth = PreviewLineWidth;
                _previewLine.material = new Material(Shader.Find("Unlit/Color")) { color = PreviewLineColor, name = "EdgePreviewMat" };
            }

            _previewLine.enabled = true;

            // Case 3: cubic preview (Fillet or SCurve).
            if (IsCurveTool(CurrentTool) && _filletState == FilletState.AwaitingClick3)
            {
                Vector2 cA, cB;
                if (CurrentTool == BuildTool.Fillet)
                {
                    float f = Mathf.Clamp(FilletLeverFraction, 0.05f, 0.95f);
                    cA = Vector2.Lerp(_filletStart.Position, _filletCorner, f);
                    cB = Vector2.Lerp(ground.Value, _filletCorner, f);
                }
                else // SCurve
                {
                    cA = _filletCorner;
                    cB = _filletStart.Position + ground.Value - _filletCorner;
                }
                const int N = 24;
                _previewLine.positionCount = N;
                for (int i = 0; i < N; i++)
                {
                    float t = i / (float)(N - 1);
                    Vector2 p = GeometryResolver.SampleCubic(_filletStart.Position, cA, cB, ground.Value, t);
                    _previewLine.SetPosition(i, new Vector3(p.x, MarkerHeight, p.y));
                }
                return;
            }

            // Case 1: straight preview (straight tool only).
            Vector2 startPos = _edgeAnchor.Position;
            _previewLine.positionCount = 2;
            _previewLine.SetPosition(0, new Vector3(startPos.x, MarkerHeight, startPos.y));
            _previewLine.SetPosition(1, new Vector3(ground.Value.x, MarkerHeight, ground.Value.y));
        }
    }
}
