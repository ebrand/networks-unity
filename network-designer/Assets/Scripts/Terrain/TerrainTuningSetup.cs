// Registers TerrainDesigner's tunables into the static TuningRegistry so the
// React TuningPanel can adjust them live over the TuningServer WebSocket.
// Mirrors NetworkDesigner.Tuning.TuningSetup, including its PERSISTENCE: values
// are loaded from / saved to a JSON file so they survive Play stop/start (the
// terrain brush settings were otherwise reset to the serialized defaults every
// session). Uses its OWN file (TerrainTuningOverrides.json) so it can't clobber
// the road designer's TuningOverrides.json — SaveToFile overwrites with only
// the currently-registered keys, and the two scenes register different sets.
//
// TerrainDesigner can auto-create this at runtime (AutoTuning), matching how
// it ensures the camera/lighting — so an empty terrain scene serves tunables
// with no manual setup.

using UnityEngine;
using NetworkDesigner.Tuning;
using NetworkDesigner.Designer; // OrbitCameraController

namespace NetworkDesigner.Terrain
{
    [RequireComponent(typeof(TuningServer))]
    public class TerrainTuningSetup : MonoBehaviour
    {
        public TerrainDesigner Terrain;

        [Tooltip("Load/save tuning values to disk so they survive Play restarts.")]
        public bool PersistChanges = true;
        [Tooltip("Where to read/write persisted tuning values. Leave empty for " +
                 "the default (project_root/TerrainTuningOverrides.json in Editor; " +
                 "persistentDataPath in a Player).")]
        public string PersistencePath = "";
        [Tooltip("Seconds of no changes before the tuning file is rewritten.")]
        public float PersistDebounceSeconds = 0.5f;

        [Tooltip("Fly camera to expose as tunables. Auto-found on Start.")]
        public FlyCameraController Fly;

        float _dirtySinceRealtime = -1f;

        void OnEnable()
        {
            // Keep Unity ticking when focus is on the React panel (see TuningSetup).
            Application.runInBackground = true;
            if (Terrain == null) Terrain = FindFirstObjectByType<TerrainDesigner>();
            if (Terrain == null) return;
            if (Fly == null) Fly = FindFirstObjectByType<FlyCameraController>();
            TuningRegistry.Clear();
            RegisterAll();

            int applied = 0;
            if (PersistChanges)
            {
                applied = TuningRegistry.LoadFromFile(ResolvePersistencePath());
                // Subscribe AFTER load so the load's per-key sets don't
                // immediately mark the file dirty.
                TuningRegistry.OnValueChanged += OnTuningChanged;
            }
            Debug.Log($"[TerrainTuningSetup] Registered {TuningRegistry.Entries.Count} tunables" +
                      (applied > 0 ? $" (loaded {applied} overrides from disk)." : "."));
        }

        void OnDisable()
        {
            TuningRegistry.OnValueChanged -= OnTuningChanged;
            // Flush pending changes so a quick Play→Stop doesn't lose them.
            if (PersistChanges && _dirtySinceRealtime > 0f)
            {
                TuningRegistry.SaveToFile(ResolvePersistencePath());
                _dirtySinceRealtime = -1f;
            }
        }

        void Update()
        {
            // Debounced save of changed tunables. (Fly-camera POSE isn't persisted
            // on purpose — it re-frames on the terrain each Play, so you can't get
            // stuck in a bad saved viewpoint. Fly settings below do persist.)
            if (!PersistChanges) return;
            if (_dirtySinceRealtime < 0f) return;
            if (Time.realtimeSinceStartup - _dirtySinceRealtime < PersistDebounceSeconds) return;
            TuningRegistry.SaveToFile(ResolvePersistencePath());
            _dirtySinceRealtime = -1f;
        }

        void OnTuningChanged() => _dirtySinceRealtime = Time.realtimeSinceStartup;

        string ResolvePersistencePath()
        {
            if (!string.IsNullOrEmpty(PersistencePath)) return PersistencePath;
#if UNITY_EDITOR
            return System.IO.Path.Combine(Application.dataPath, "..", "TerrainTuningOverrides.json");
#else
            return System.IO.Path.Combine(Application.persistentDataPath, "TerrainTuningOverrides.json");
#endif
        }

        void RegisterAll()
        {
            TerrainDesigner t = Terrain;

            // --- Terrain size (rebuild to apply) ---
            TuningRegistry.RegisterFloat("terrain.sizeMeters", "Terrain", "Map size (m, square)",
                () => t.TerrainSizeMeters, v => t.TerrainSizeMeters = v, 200f, 20000f,
                description: "Map width/length. Applies on 'Rebuild terrain'. Grid = size / cell size; a big map needs a bigger cell size or the vert/chunk count explodes.");
            TuningRegistry.RegisterFloat("terrain.cellSize", "Terrain", "Cell size (m)",
                () => t.CellSize, v => t.CellSize = v, 2f, 50f,
                description: "Metres between grid vertices (facet size). Smaller = finer but far more verts/chunks. Applies on 'Rebuild terrain'. 10 m is a good balance for a 10 km map.");
            TuningRegistry.RegisterFloat("terrain.chunkCells", "Terrain", "Chunk cells/side",
                () => t.ChunkCells, v => t.ChunkCells = Mathf.RoundToInt(v), 16f, 100f, 1f,
                description: "Cells per chunk mesh (8-100). Bigger = fewer chunks/draw-calls + colliders, but heavier per-chunk meshes (keep cells^2 * 6 under 65k). Applies on rebuild.");
            TuningRegistry.RegisterAction("terrain.rebuild", "Terrain", "Rebuild terrain (resize)",
                () => t.RebuildTerrain(),
                description: "Rebuild at the current size/cell size. WIPES the heightfield to flat (re-import a heightmap after). A large map can stall while building — watch the load.");

            // --- Autosave / manual persistence ---
            TuningRegistry.RegisterBool("terrain.autosave", "Autosave", "Autosave enabled",
                () => t.Autosave, v => t.Autosave = v,
                description: "On: terrain/trees/rail/camera are saved automatically (debounced after edits, and on Play-stop) and reloaded on Play. Off: nothing auto-saves or auto-loads — use the Save/Load buttons manually (handy for testing).");
            TuningRegistry.RegisterAction("terrain.save", "Autosave", "Save now",
                () => t.SaveNow(),
                description: "Write the current terrain/trees/rail/camera to the autosave file immediately (synchronous). Works whether or not autosave is enabled.");
            TuningRegistry.RegisterAction("terrain.load", "Autosave", "Load now",
                () => t.LoadNow(),
                description: "Reload everything from the autosave file, live — clears current trees/rocks first so they don't duplicate, then rebuilds terrain, layers, water and camera. Also moves the camera to the saved view.");

            // --- Brush ---
            TuningRegistry.RegisterFloat("terrain.brushRadius", "Brush", "Radius (m)",
                () => t.BrushRadius, v => t.BrushRadius = v, 0.5f, 200f,
                description: "Sculpt brush size. Resize live with [ and ] while sculpting.");
            TuningRegistry.RegisterFloat("terrain.brushStrength", "Brush", "Strength (m/s)",
                () => t.BrushStrength, v => t.BrushStrength = v, 0f, 300f,
                description: "How fast the brush raises/lowers terrain while held (metres per second).");
            TuningRegistry.RegisterFloat("terrain.brushStrengthExp", "Brush", "Strength exponent",
                () => t.BrushStrengthExponent, v => t.BrushStrengthExponent = v, 1f, 4f,
                description: "Amplifies strength non-linearly — higher makes 'stronger' much stronger (exponential response).");
            TuningRegistry.RegisterFloat("terrain.brushFalloff", "Brush", "Falloff",
                () => t.BrushFalloff, v => t.BrushFalloff = v, 0f, 1f,
                description: "Edge softness of the brush. 0 = hard flat disc; 1 = smooth feathered edge.");
            TuningRegistry.RegisterFloat("terrain.flattenStrength", "Brush", "Flatten strength",
                () => t.FlattenStrength, v => t.FlattenStrength = v, 0.5f, 60f,
                description: "How hard Flatten (brush 4) pulls terrain to the target height. Higher = snappier. In Flatten mode, right-click to sample a target height (eyedropper), then left-click/drag to make everything that height.");
            TuningRegistry.RegisterBool("terrain.liveConform", "Brush", "Live re-settle scatter",
                () => t.LiveConform, v => t.LiveConform = v,
                description: "Re-settle trees/rocks/fences onto the surface every sculpt frame, not just on stroke-end. On = smoothest, but re-conforms ALL items each frame — turn off on a heavily-populated map if it hitches. (They always re-settle when the stroke ends regardless.)");
            TuningRegistry.RegisterFloat("terrain.slopeMaxGrade", "Brush", "Slope warn grade (%)",
                () => t.SlopeMaxGradePct, v => t.SlopeMaxGradePct = v, 0.5f, 30f,
                description: "Slope tool (brush 5): the live grade readout turns red above this %. Just a warning — it doesn't block the slope. Slope tool: click A (start elev), move (corridor + grade preview, snaps to nearby rail's 'straight'), click B (end elev) to grade the strip.");
            TuningRegistry.RegisterBool("terrain.slopeSnapRail", "Brush", "Slope: snap to rail",
                () => !t.SlopeDisableRailSnap, v => t.SlopeDisableRailSnap = !v,
                description: "Slope tool (5): snap the slope endpoints + 'straight' guide to nearby rail (ends first, then edges), centring the slope path on the track. Off = free placement, no rail guide.");
            TuningRegistry.RegisterFloat("terrain.slopeRailDetect", "Brush", "Slope rail-detect radius (m)",
                () => t.SlopeGuideDetectRadius, v => t.SlopeGuideDetectRadius = v, 0f, 200f,
                description: "Slope tool (5): if point A lands within this of rail track, the track's heading becomes the 'straight' guide (panel shows 'Aligned to rail'). Bigger = easier to catch the track. 0 = off.");
            TuningRegistry.RegisterFloat("terrain.slopeRailSnap", "Brush", "Slope rail-snap radius (m)",
                () => t.SlopeGuideSnapRadius, v => t.SlopeGuideSnapRadius = v, 0f, 50f,
                description: "Slope tool (5): the end point snaps onto the rail 'straight' guide when within this perpendicular distance. Bigger = grabbier.");
            TuningRegistry.RegisterFloat("terrain.brushResizeRate", "Brush", "Resize rate (m/s)",
                () => t.BrushResizeRate, v => t.BrushResizeRate = v, 1f, 100f,
                description: "How fast the radius changes while you hold [ or ].");

            // --- Heightmap import ---
            TuningRegistry.RegisterString("terrain.heightmapPath", "Heightmap", "File",
                () => t.HeightmapPath, v => t.HeightmapPath = v);
            TuningRegistry.RegisterFloat("terrain.heightmapMax", "Heightmap", "Max height (m)",
                () => t.HeightmapMaxHeight, v => t.HeightmapMaxHeight = v, 1f, 2000f);
            TuningRegistry.RegisterFloat("terrain.heightmapSmooth", "Heightmap", "Smooth passes",
                () => t.HeightmapSmoothPasses, v => t.HeightmapSmoothPasses = Mathf.RoundToInt(v), 0f, 12f, 1f);
            TuningRegistry.RegisterAction("terrain.importHeightmap", "Heightmap", "Load heightmap",
                () => t.ImportHeightmap());
            TuningRegistry.RegisterAction("terrain.flatten", "Heightmap", "Flatten terrain",
                () => t.FlattenTerrain(),
                description: "Reset the whole heightfield to flat (keeps the current map size) and re-settle scatter/lines/rail onto it.");

            // --- Trees ---
            TuningRegistry.RegisterFloat("terrain.treePaintRate", "Trees", "Strength (trees/s)",
                () => t.TreeLayer.PaintRate, v => t.TreeLayer.PaintRate = v, 1f, 200f);
            TuningRegistry.RegisterFloat("terrain.treeSpacing", "Trees", "Spacing (m)",
                () => t.TreeLayer.Spacing, v => t.TreeLayer.Spacing = v, 1f, 30f);
            TuningRegistry.RegisterFloat("terrain.treeMaxSlope", "Trees", "Max slope (deg, 90 = off)",
                () => t.TreeLayer.MaxSlopeDeg, v => t.TreeLayer.MaxSlopeDeg = v, 0f, 90f,
                description: "Don't place trees on terrain steeper than this. 90 = no slope limit.");
            TuningRegistry.RegisterBool("terrain.treeAvoidWater", "Trees", "Avoid water",
                () => t.TreeLayer.AvoidWater, v => t.TreeLayer.AvoidWater = v,
                description: "Don't place trees on terrain below the water surface, and cull trees the water rises over.");
            TuningRegistry.RegisterFloat("terrain.treeWaterMargin", "Trees", "Waterline margin (m)",
                () => t.TreeLayer.WaterlineMargin, v => t.TreeLayer.WaterlineMargin = v, 0f, 20f,
                description: "Metres of shoreline ABOVE the waterline to also keep clear of trees (a beach strip).");
            TuningRegistry.RegisterString("terrain.treeFolder", "Trees", "Folder",
                () => t.TreeLayer.Folder, v => t.TreeLayer.Folder = v);
            TuningRegistry.RegisterAction("terrain.removeTrees", "Trees", "Remove all trees",
                () => t.RemoveAllTrees(),
                description: "Delete every placed tree.");
#if UNITY_EDITOR
            TuningRegistry.RegisterAction("terrain.loadTrees", "Trees", "Load trees from folder",
                () => t.LoadTreesFromFolder());
#endif

            // --- Rocks ---
            TuningRegistry.RegisterFloat("terrain.rockPaintRate", "Rocks", "Strength (rocks/s)",
                () => t.RockLayer.PaintRate, v => t.RockLayer.PaintRate = v, 1f, 200f);
            TuningRegistry.RegisterFloat("terrain.rockSpacing", "Rocks", "Spacing (m)",
                () => t.RockLayer.Spacing, v => t.RockLayer.Spacing = v, 1f, 30f);
            TuningRegistry.RegisterFloat("terrain.rockMaxSlope", "Rocks", "Max slope (deg, 90 = off)",
                () => t.RockLayer.MaxSlopeDeg, v => t.RockLayer.MaxSlopeDeg = v, 0f, 90f,
                description: "Don't place rocks on terrain steeper than this. 90 = no slope limit.");
            TuningRegistry.RegisterBool("terrain.rockAvoidWater", "Rocks", "Avoid water",
                () => t.RockLayer.AvoidWater, v => t.RockLayer.AvoidWater = v,
                description: "Don't place rocks on terrain below the water surface, and cull rocks the water rises over.");
            TuningRegistry.RegisterFloat("terrain.rockWaterMargin", "Rocks", "Waterline margin (m)",
                () => t.RockLayer.WaterlineMargin, v => t.RockLayer.WaterlineMargin = v, 0f, 20f,
                description: "Metres of shoreline ABOVE the waterline to also keep clear of rocks.");
            TuningRegistry.RegisterString("terrain.rockFolder", "Rocks", "Folder",
                () => t.RockLayer.Folder, v => t.RockLayer.Folder = v);
            TuningRegistry.RegisterAction("terrain.removeRocks", "Rocks", "Remove all rocks",
                () => t.RemoveAllRocks(),
                description: "Delete every placed rock.");
#if UNITY_EDITOR
            TuningRegistry.RegisterAction("terrain.loadRocks", "Rocks", "Load rocks from folder",
                () => t.LoadRocksFromFolder());
#endif

            // --- Linework (fence) ---
            TuningRegistry.RegisterFloat("fence.spacing", "Fence", "Spacing (m)",
                () => t.FenceLayer.Spacing, v => { t.FenceLayer.Spacing = v; t.RebuildFence(); }, 0.5f, 30f);
            TuningRegistry.RegisterFloat("fence.verticalOffset", "Fence", "Vertical offset (m)",
                () => t.FenceLayer.VerticalOffset, v => { t.FenceLayer.VerticalOffset = v; t.RebuildFence(); }, -5f, 20f);
            TuningRegistry.RegisterFloat("fence.yawOffset", "Fence", "Yaw offset (deg)",
                () => t.FenceLayer.YawOffset, v => { t.FenceLayer.YawOffset = v; t.RebuildFence(); }, -180f, 180f);
            TuningRegistry.RegisterFloat("fence.scale", "Fence", "Scale",
                () => t.FenceLayer.Scale, v => { t.FenceLayer.Scale = v; t.RebuildFence(); }, 0.1f, 10f);
            TuningRegistry.RegisterBool("fence.conform", "Fence", "Conform to terrain",
                () => t.FenceLayer.Conform, v => { t.FenceLayer.Conform = v; t.RebuildFence(); });
            TuningRegistry.RegisterBool("fence.straight", "Fence", "Straight (hard corners)",
                () => t.FenceLayer.Straight, v => { t.FenceLayer.Straight = v; t.RebuildFence(); });
            TuningRegistry.RegisterAction("fence.clear", "Fence", "Clear fence",
                () => t.ClearFence());

            // --- Linework (power line) ---
            TuningRegistry.RegisterFloat("power.spacing", "Power line", "Pole spacing (m)",
                () => t.PowerLineLayer.Spacing, v => { t.PowerLineLayer.Spacing = v; t.RebuildPowerLine(); }, 2f, 80f);
            TuningRegistry.RegisterFloat("power.verticalOffset", "Power line", "Vertical offset (m)",
                () => t.PowerLineLayer.VerticalOffset, v => { t.PowerLineLayer.VerticalOffset = v; t.RebuildPowerLine(); }, -5f, 20f);
            TuningRegistry.RegisterFloat("power.yawOffset", "Power line", "Yaw offset (deg)",
                () => t.PowerLineLayer.YawOffset, v => { t.PowerLineLayer.YawOffset = v; t.RebuildPowerLine(); }, -180f, 180f);
            TuningRegistry.RegisterFloat("power.scale", "Power line", "Scale",
                () => t.PowerLineLayer.Scale, v => { t.PowerLineLayer.Scale = v; t.RebuildPowerLine(); }, 0.1f, 10f);
            TuningRegistry.RegisterBool("power.conform", "Power line", "Conform to terrain",
                () => t.PowerLineLayer.Conform, v => { t.PowerLineLayer.Conform = v; t.RebuildPowerLine(); });
            TuningRegistry.RegisterBool("power.straight", "Power line", "Straight (hard corners)",
                () => t.PowerLineLayer.Straight, v => { t.PowerLineLayer.Straight = v; t.RebuildPowerLine(); });
            TuningRegistry.RegisterAction("power.clear", "Power line", "Clear power line",
                () => t.ClearPowerLine());

            // --- Rail track ---
            TuningRegistry.RegisterFloat("rail.gauge", "Rail", "Gauge (m)",
                () => t.RailLayer.Gauge, v => { t.RailLayer.Gauge = v; t.RebuildRail(); }, 0.5f, 5f,
                description: "Distance between the two rail centres. Real standard gauge is 1.435 m.");
            TuningRegistry.RegisterFloat("rail.tieSpacing", "Rail", "Tie spacing (m)",
                () => t.RailLayer.TieSpacing, v => { t.RailLayer.TieSpacing = v; t.RebuildRail(); }, 0.2f, 5f,
                description: "Gap between sleepers (ties) along the track. Smaller = more, denser ties.");
            TuningRegistry.RegisterFloat("rail.tieLength", "Rail", "Tie length (m)",
                () => t.RailLayer.TieLength, v => { t.RailLayer.TieLength = v; t.RebuildRail(); }, 0.5f, 6f,
                description: "How far each tie extends across the track (should exceed the gauge). Also sizes the ballast/bore width.");
            TuningRegistry.RegisterFloat("rail.railHeight", "Rail", "Rail height (m)",
                () => t.RailLayer.RailHeight, v => { t.RailLayer.RailHeight = v; t.RebuildRail(); }, 0.02f, 1f,
                description: "Height of the rail standing above the ties.");
            TuningRegistry.RegisterFloat("rail.verticalOffset", "Rail", "Vertical offset (m)",
                () => t.RailLayer.VerticalOffset, v => { t.RailLayer.VerticalOffset = v; t.RebuildRail(); }, -2f, 5f,
                description: "Raises/lowers the whole track relative to the terrain.");
            TuningRegistry.RegisterBool("rail.conform", "Rail", "Conform to terrain",
                () => t.RailLayer.Conform, v => { t.RailLayer.Conform = v; t.RebuildRail(); },
                description: "On: node elevations follow the ground (then each segment runs a constant grade between them). Off: flat track at the vertical offset.");
            TuningRegistry.RegisterFloat("rail.curveLever", "Rail", "Curve lever (arc width)",
                () => t.RailLayer.CurveLever, v => { t.RailLayer.CurveLever = v; t.RebuildRail(); }, 0.1f, 0.95f,
                description: "Curve-tool shape: how far the bezier controls lean toward the guide corner. Low = sharp through the corner, high = a wide arc.");
            TuningRegistry.RegisterFloat("rail.speedLimit", "Rail", "Speed limit (km/h)",
                () => t.RailLayer.SpeedLimitKmh, v => t.RailLayer.SpeedLimitKmh = v, 10f, 200f,
                description: "Design speed for sections laid now. Sets the minimum curve radius (R = v^2/(g*a)); tighter curves are refused. Lower it to lay tighter curves.");
            TuningRegistry.RegisterFloat("rail.maxLatG", "Rail", "Max lateral g (curve tightness)",
                () => t.RailLayer.MaxLateralG, v => t.RailLayer.MaxLateralG = v, 0.05f, 0.5f,
                description: "Comfort/cant limit. Higher = tighter curves allowed at a given speed (smaller min radius). Real rail ~0.1; raise for game-scaled curves.");
            TuningRegistry.RegisterFloat("rail.maxGrade", "Rail", "Max grade (deg)",
                () => t.RailLayer.MaxGradeDeg, v => t.RailLayer.MaxGradeDeg = v, 0.5f, 15f,
                description: "Steepest grade any section may have. The terrain is sampled every 'Grade sample step' m along an edge; the edge is buildable up to the first section over this, then truncated there (the rest shows red). 5 deg ~ 8.7%.");
            TuningRegistry.RegisterFloat("rail.gradeStep", "Rail", "Grade sample step (m)",
                () => t.RailLayer.GradeSampleStep, v => t.RailLayer.GradeSampleStep = v, 1f, 50f,
                description: "Spacing at which the terrain elevation is sampled along an edge to check the per-section grade — the 'every N metres' resolution. Smaller = finer (catches short steep bumps), larger = smoother.");
            TuningRegistry.RegisterBool("rail.overrideGrade", "Rail", "Override grade (bridge anyway)",
                () => t.RailLayer.OverrideGrade, v => t.RailLayer.OverrideGrade = v,
                description: "Ignore the grade limit and DON'T truncate: build the whole edge across whatever terrain it crosses (deep fills become bridges automatically). For 'the terrain's whacked, span it anyway'. Hotkey B in rail mode.");
            TuningRegistry.RegisterFloat("rail.trackSnap", "Rail", "Track snap radius (m)",
                () => t.RailLayer.TrackSnapRadius, v => t.RailLayer.TrackSnapRadius = v, 0f, 25f,
                description: "Snap radius for connecting to EXISTING track: a click within this of a node or rail edge is pulled exactly onto it (overrides grid snap) so it reliably joins the network. 0 = grid snap only.");
            TuningRegistry.RegisterFloat("rail.guideLength", "Rail", "Alignment guide length (m)",
                () => t.RailLayer.ExtensionGuideLength, v => t.RailLayer.ExtensionGuideLength = v, 0f, 600f,
                description: "Length of the dashed straight-ahead alignment guide drawn out of the chain tail. Also bounds how far ahead the extension snap reaches. 0 = no guide / no extension snap.");
            TuningRegistry.RegisterFloat("rail.ballastHeight", "Rail", "Ballast height (m)",
                () => t.RailLayer.BallastHeight, v => { t.RailLayer.BallastHeight = v; t.RebuildRail(); }, 0f, 1.5f,
                description: "Height of the raised gravel bed the ties sit on. 0 = no ballast.");
            TuningRegistry.RegisterFloat("rail.ballastShoulder", "Rail", "Ballast shoulder (m)",
                () => t.RailLayer.BallastShoulder, v => { t.RailLayer.BallastShoulder = v; t.RebuildRail(); }, 0f, 2f,
                description: "Extra ballast width beyond the tie ends on each side (the gravel shoulder).");
            TuningRegistry.RegisterFloat("rail.ballastSlope", "Rail", "Ballast shoulder slope",
                () => t.RailLayer.BallastSlope, v => { t.RailLayer.BallastSlope = v; t.RebuildRail(); }, 0f, 4f,
                description: "How far the ballast base flares out per unit of fill height (the embankment batter).");
            TuningRegistry.RegisterColor("rail.ballastColor", "Rail", "Ballast color",
                () => t.RailLayer.BallastColor, v => { t.RailLayer.BallastColor = v; t.RebuildRail(); },
                description: "Colour of the gravel ballast bed.");
            TuningRegistry.RegisterFloat("rail.embankMaxDrop", "Rail", "Gravel cap drop -> wall (m)",
                () => t.RailLayer.EmbankmentMaxDrop, v => { t.RailLayer.EmbankmentMaxDrop = v; t.RebuildRail(); }, 0.1f, 4f,
                description: "On a fill, the gravel shoulder slopes down at most this much; below it a vertical retaining wall drops to the ground.");
            TuningRegistry.RegisterFloat("rail.bridgeAbove", "Rail", "Bridge above fill (m)",
                () => t.RailLayer.BridgeAboveFill, v => { t.RailLayer.BridgeAboveFill = v; t.RebuildRail(); }, 1f, 40f,
                description: "Fill height above which a section is carried on a BRIDGE (deck + piers) instead of a wall/embankment.");
            TuningRegistry.RegisterFloat("rail.deckDepth", "Rail", "Bridge deck depth (m)",
                () => t.RailLayer.DeckDepth, v => { t.RailLayer.DeckDepth = v; t.RebuildRail(); }, 0.2f, 3f,
                description: "Thickness of the procedural bridge deck girder.");
            TuningRegistry.RegisterFloat("rail.pierSpacing", "Rail", "Bridge pier spacing (m)",
                () => t.RailLayer.PierSpacing, v => { t.RailLayer.PierSpacing = v; t.RebuildRail(); }, 4f, 60f,
                description: "Distance between bridge support piers.");
            TuningRegistry.RegisterFloat("rail.pierWidth", "Rail", "Bridge pier width (m)",
                () => t.RailLayer.PierWidth, v => { t.RailLayer.PierWidth = v; t.RebuildRail(); }, 0.3f, 4f,
                description: "Square cross-section size of each bridge pier.");
            TuningRegistry.RegisterColor("rail.structColor", "Rail", "Structure color (wall/bridge)",
                () => t.RailLayer.StructureColor, v => { t.RailLayer.StructureColor = v; t.RebuildRail(); },
                description: "Concrete colour for retaining walls, the bridge deck/piers, and tunnel liners/portals.");
            TuningRegistry.RegisterFloat("rail.bridgeSpan", "Rail", "Bridge prefab span (m)",
                () => t.RailLayer.BridgeSpan, v => { t.RailLayer.BridgeSpan = v; t.RebuildRail(); }, 2f, 60f,
                description: "If a Bridge Prefab is assigned, it's placed every this-many metres along the span. Set to the prefab's real length so pieces butt together.");
            TuningRegistry.RegisterFloat("rail.bridgeYaw", "Rail", "Bridge prefab yaw (deg)",
                () => t.RailLayer.BridgeYawOffset, v => { t.RailLayer.BridgeYawOffset = v; t.RebuildRail(); }, -180f, 180f,
                description: "Rotation added to each bridge prefab instance, to align it to the track if it doesn't face +Z.");
            TuningRegistry.RegisterFloat("rail.bridgeVOffset", "Rail", "Bridge prefab vertical offset (m)",
                () => t.RailLayer.BridgeVerticalOffset, v => { t.RailLayer.BridgeVerticalOffset = v; t.RebuildRail(); }, -10f, 10f,
                description: "Raises/lowers the bridge prefab to line its deck up under the rails.");
            TuningRegistry.RegisterFloat("rail.bridgeScale", "Rail", "Bridge prefab scale",
                () => t.RailLayer.BridgeScale, v => { t.RailLayer.BridgeScale = v; t.RebuildRail(); }, 0.1f, 10f,
                description: "Uniform scale applied to each bridge prefab instance.");
            TuningRegistry.RegisterBool("rail.proceduralPiers", "Rail", "Procedural piers under prefab",
                () => t.RailLayer.ProceduralPiers, v => { t.RailLayer.ProceduralPiers = v; t.RebuildRail(); },
                description: "Keep generating terrain-grounded piers under a bridge prefab (so the supports reach the real ground). Turn off if the prefab has its own piers.");
            TuningRegistry.RegisterBool("rail.highlightDisconnected", "Rail", "Highlight disconnected track",
                () => t.RailLayer.HighlightDisconnected, v => { t.RailLayer.HighlightDisconnected = v; t.RebuildRail(); },
                description: "Draw any track NOT connected to the main network in red, so a stranded stretch is obvious.");
            TuningRegistry.RegisterFloat("rail.tunnelClearance", "Rail", "Tunnel bore height (m)",
                () => t.RailLayer.TunnelClearance, v => { t.RailLayer.TunnelClearance = v; t.RebuildRail(); }, 3f, 12f,
                description: "Height of the tunnel bore above the track bed. Also sets how deep the track must be to count as a tunnel.");
            TuningRegistry.RegisterFloat("rail.tunnelMargin", "Rail", "Tunnel bore margin (m)",
                () => t.RailLayer.TunnelMargin, v => { t.RailLayer.TunnelMargin = v; t.RebuildRail(); }, 0f, 3f,
                description: "Extra tunnel bore width beyond the tie ends on each side.");
            TuningRegistry.RegisterFloat("rail.tunnelCover", "Rail", "Tunnel min cover (m)",
                () => t.RailLayer.TunnelMinCover, v => { t.RailLayer.TunnelMinCover = v; t.RebuildRail(); }, 0f, 10f,
                description: "Rock required above the bore crown before a tunnel forms. Track buried deeper than (bore height + this) gets a liner + portals.");
            TuningRegistry.RegisterFloat("rail.cutWidth", "Rail", "Cut floor half-width (m)",
                () => t.CutFloorHalfWidth, v => t.CutFloorHalfWidth = v, 1f, 15f,
                description: "Carve action: half-width of the flat cut floor. Wider = more terrain dug out. Applied when you press Carve.");
            TuningRegistry.RegisterFloat("rail.cutDepth", "Rail", "Cut depth below bed (m)",
                () => t.CutDepthBelowBed, v => t.CutDepthBelowBed = v, 0f, 5f,
                description: "Carve action: how far below the rail bed the cut floor is dug, so the track sits proud in the trench.");
            TuningRegistry.RegisterFloat("rail.cutBatter", "Rail", "Cut wall steepness",
                () => t.CutBatter, v => t.CutBatter = v, 0.3f, 2f,
                description: "Carve action: cut-wall rise per metre out. Lower = wider, gentler cut; higher = steeper, narrower (keeps more rock over a tunnel).");
            TuningRegistry.RegisterFloat("rail.cutSmooth", "Rail", "Cut smoothing passes",
                () => t.CutSmoothPasses, v => t.CutSmoothPasses = Mathf.RoundToInt(v), 0f, 6f, 1f,
                description: "Carve action: smoothing passes that round the coarse cut walls afterward. The floor under the track is protected, so it won't re-bury the rails. 0 = off.");
            TuningRegistry.RegisterAction("rail.carve", "Rail", "Carve approaches (destructive)",
                () => t.CarveRailApproaches(),
                description: "DESTRUCTIVE: permanently lowers the terrain into open cuts along below-grade track + notches tunnel mouths open. No undo; re-press to dig further (additive).");
            TuningRegistry.RegisterColor("rail.railColor", "Rail", "Rail color",
                () => t.RailLayer.RailColor, v => { t.RailLayer.RailColor = v; t.RebuildRail(); },
                description: "Colour of the metal rails.");
            TuningRegistry.RegisterColor("rail.tieColor", "Rail", "Tie color",
                () => t.RailLayer.TieColor, v => { t.RailLayer.TieColor = v; t.RebuildRail(); },
                description: "Colour of the wooden sleepers (ties).");
            TuningRegistry.RegisterAction("rail.clear", "Rail", "Clear rail",
                () => t.ClearRail(),
                description: "Delete the entire rail network (all nodes and edges).");

            // --- Brush cursor ---
            TuningRegistry.RegisterBool("terrain.showCursor", "Brush cursor", "Show ring",
                () => t.ShowBrushCursor, v => t.ShowBrushCursor = v);
            TuningRegistry.RegisterColor("terrain.cursorColor", "Brush cursor", "Color",
                () => t.BrushCursorColor, v => t.BrushCursorColor = v);
            TuningRegistry.RegisterBool("terrain.cursorDashed", "Brush cursor", "Dashed",
                () => t.BrushCursorDashed, v => t.BrushCursorDashed = v);
            TuningRegistry.RegisterFloat("terrain.cursorDashLength", "Brush cursor", "Dash length (m)",
                () => t.BrushCursorDashLength, v => t.BrushCursorDashLength = v, 0.25f, 20f);
            TuningRegistry.RegisterFloat("terrain.cursorDashGap", "Brush cursor", "Dash gap (m)",
                () => t.BrushCursorDashGap, v => t.BrushCursorDashGap = v, 0.25f, 20f);

            // --- Contours ---
            TuningRegistry.RegisterBool("terrain.showContours", "Contours", "Show",
                () => t.ShowContours, v => { t.ShowContours = v; t.RebuildContours(); });
            TuningRegistry.RegisterBool("terrain.liveContours", "Contours", "Live update",
                () => t.LiveContours, v => t.LiveContours = v);
            TuningRegistry.RegisterFloat("terrain.contourInterval", "Contours", "Interval (m)",
                () => t.ContourInterval, v => { t.ContourInterval = v; t.RebuildContours(); }, 0.25f, 20f);
            TuningRegistry.RegisterColor("terrain.contourColor", "Contours", "Color",
                () => t.ContourColor, v => { t.ContourColor = v; t.RebuildContours(); });
            TuningRegistry.RegisterFloat("terrain.contourLift", "Contours", "Lift (m)",
                () => t.ContourLift, v => { t.ContourLift = v; t.RebuildContours(); }, 0f, 1f);
            TuningRegistry.RegisterBool("terrain.contourDashed", "Contours", "Dashed",
                () => t.ContourDashed, v => { t.ContourDashed = v; t.RebuildContours(); });
            TuningRegistry.RegisterFloat("terrain.contourDashLength", "Contours", "Dash length (m)",
                () => t.ContourDashLength, v => { t.ContourDashLength = v; t.RebuildContours(); }, 0.25f, 20f);
            TuningRegistry.RegisterFloat("terrain.contourDashGap", "Contours", "Dash gap (m)",
                () => t.ContourDashGap, v => { t.ContourDashGap = v; t.RebuildContours(); }, 0.25f, 20f);

            // --- Appearance ---
            TuningRegistry.RegisterColor("terrain.color", "Appearance", "Grass color (flat)",
                () => t.TerrainColor, v => { t.TerrainColor = v; t.ApplyTerrainMaterial(); },
                description: "Colour used on flat/gentle terrain (the 'grass' band).");
            TuningRegistry.RegisterColor("terrain.rockColor", "Appearance", "Rock color (steep)",
                () => t.RockColor, v => { t.RockColor = v; t.ApplyTerrainMaterial(); },
                description: "Colour blended onto steep faces (the 'rock' band).");
            TuningRegistry.RegisterFloat("terrain.slopeStart", "Appearance", "Rock slope start (deg)",
                () => t.SlopeStartDeg, v => { t.SlopeStartDeg = v; t.ApplyTerrainMaterial(); }, 0f, 90f,
                description: "Slope angle where rock starts blending in (0 = flat, 90 = vertical cliff).");
            TuningRegistry.RegisterFloat("terrain.slopeFull", "Appearance", "Rock slope full (deg)",
                () => t.SlopeFullDeg, v => { t.SlopeFullDeg = v; t.ApplyTerrainMaterial(); }, 0f, 90f,
                description: "Slope angle at which a face is fully rock. Tune with the tree/rock 'Max slope' to match.");
            TuningRegistry.RegisterFloat("terrain.rockTexScale", "Appearance", "Rock texture scale",
                () => t.RockTextureScale, v => { t.RockTextureScale = v; t.ApplyTerrainMaterial(); }, 0.01f, 1f,
                description: "Tiling of the optional triplanar rock texture (lower = larger features). Only visible if a Rock Texture is assigned.");

            // --- Water ---
            TuningRegistry.RegisterBool("water.on", "Water", "Show water",
                () => t.ShowWater, v => { t.ShowWater = v; t.ApplyWater(); },
                description: "Show a flat water surface across the map; terrain below the level reads as submerged.");
            TuningRegistry.RegisterFloat("water.level", "Water", "Level (m)",
                () => t.WaterLevel, v => { t.WaterLevel = v; t.ApplyWater(); }, -50f, 300f,
                description: "World height of the water surface. Raise it to flood low ground / fill a gorge.");
            TuningRegistry.RegisterColor("water.color", "Water", "Color",
                () => t.WaterColor, v => { t.WaterColor = v; t.ApplyWater(); },
                description: "Water colour. Its alpha controls transparency (lower = see the bed through it).");
            TuningRegistry.RegisterFloat("water.alpha", "Water", "Alpha (transparency)",
                () => t.WaterColor.a,
                v => { Color c = t.WaterColor; c.a = v; t.WaterColor = c; t.ApplyWater(); }, 0.05f, 1f,
                description: "Water transparency — lower = clearer (see the bed through it), 1 = opaque. (Same as the colour's alpha channel; min 0.05 so it isn't reset by the 0-alpha guard.)");
            TuningRegistry.RegisterFloat("water.smoothness", "Water", "Smoothness",
                () => t.WaterSmoothness, v => { t.WaterSmoothness = v; t.ApplyWater(); }, 0f, 1f,
                description: "Surface gloss — higher gives more of a reflective sheen.");

            // --- World grid ---
            TuningRegistry.RegisterBool("terrain.gridOn", "Grid", "Show grid (G)",
                () => t.GridEnabled, v => { t.GridEnabled = v; t.ApplyTerrainMaterial(); },
                description: "Paint a world grid on the terrain surface (it drapes over relief). Hotkey: G.");
            TuningRegistry.RegisterFloat("terrain.gridSpacing", "Grid", "Spacing (m)",
                () => t.GridSpacing, v => { t.GridSpacing = v; t.ApplyTerrainMaterial(); }, 1f, 50f,
                description: "Distance between minor grid lines. 5 matches the terrain cell size.");
            TuningRegistry.RegisterFloat("terrain.gridMajor", "Grid", "Major every N",
                () => t.GridMajorEvery, v => { t.GridMajorEvery = v; t.ApplyTerrainMaterial(); }, 1f, 20f,
                description: "Draw a brighter major line every N minor lines (e.g. 10 = a bold line every 50 m).");
            TuningRegistry.RegisterFloat("terrain.gridStrength", "Grid", "Strength",
                () => t.GridStrength, v => { t.GridStrength = v; t.ApplyTerrainMaterial(); }, 0f, 1f,
                description: "Grid line opacity (0 = invisible).");
            TuningRegistry.RegisterFloat("terrain.gridWidth", "Grid", "Line width (px)",
                () => t.GridLineWidth, v => { t.GridLineWidth = v; t.ApplyTerrainMaterial(); }, 0.5f, 4f,
                description: "Grid line thickness in screen pixels (constant at any distance).");
            TuningRegistry.RegisterColor("terrain.gridColor", "Grid", "Color",
                () => t.GridColor, v => { t.GridColor = v; t.ApplyTerrainMaterial(); },
                description: "Grid line colour.");
            TuningRegistry.RegisterBool("terrain.gridSnap", "Grid", "Snap nodes to grid (Shift+G)",
                () => t.SnapToGrid, v => t.SnapToGrid = v,
                description: "Snap rail/fence/power node placement to grid intersections. Works whether or not the grid is shown. Hotkey: Shift+G.");

            // --- Interface ---
            TuningRegistry.RegisterFloat("terrain.uiScale", "Interface", "UI scale (in-game panels)",
                () => TerrainDesigner.UiScale, v => TerrainDesigner.UiScale = v, 0.75f, 3f,
                description: "Scale of the in-game IMGUI panels (palettes, mode hints), so they don't shrink at high resolution.");

            // --- Camera (free-fly) ---
            if (Fly != null)
            {
                TuningRegistry.RegisterFloat("fly.moveSpeed", "Camera", "Move speed (m/s)",
                    () => Fly.MoveSpeed, v => Fly.MoveSpeed = v, 5f, 1000f);
                TuningRegistry.RegisterFloat("fly.zoomStep", "Camera", "Zoom step (m/notch)",
                    () => Fly.ZoomStep, v => Fly.ZoomStep = v, 5f, 500f);
                TuningRegistry.RegisterFloat("fly.fastMultiplier", "Camera", "Shift fast multiplier",
                    () => Fly.FastMultiplier, v => Fly.FastMultiplier = v, 1f, 20f);
                TuningRegistry.RegisterFloat("fly.lookSensitivity", "Camera", "Look sensitivity",
                    () => Fly.LookSensitivity, v => Fly.LookSensitivity = v, 0.2f, 10f);
                TuningRegistry.RegisterFloat("fly.smoothing", "Camera", "Look smoothing (higher = snappier)",
                    () => Fly.Smoothing, v => Fly.Smoothing = v, 0f, 30f);
                TuningRegistry.RegisterFloat("fly.zoomSmoothing", "Camera", "Zoom smoothing (higher = snappier)",
                    () => Fly.ZoomSmoothing, v => Fly.ZoomSmoothing = v, 0f, 30f);
                TuningRegistry.RegisterFloat("fly.fov", "Camera", "Field of view (deg)",
                    () => Fly.FieldOfView, v => Fly.FieldOfView = v, 30f, 90f);
                TuningRegistry.RegisterFloat("fly.moveDamping", "Camera", "Move damping (lower = more drift)",
                    () => Fly.MoveDamping, v => Fly.MoveDamping = v, 0.5f, 30f);
                TuningRegistry.RegisterFloat("fly.speedAltRef", "Camera", "Full-speed altitude (m)",
                    () => Fly.SpeedAltitudeReference, v => Fly.SpeedAltitudeReference = v, 10f, 1000f);
                TuningRegistry.RegisterFloat("fly.minSpeedFactor", "Camera", "Min speed factor (low altitude)",
                    () => Fly.MinSpeedFactor, v => Fly.MinSpeedFactor = v, 0.05f, 1f);
                TuningRegistry.RegisterFloat("fly.maxAltitude", "Camera", "Max altitude (m)",
                    () => Fly.MaxAltitude, v => Fly.MaxAltitude = v, 50f, 5000f);
                TuningRegistry.RegisterFloat("fly.minClearance", "Camera", "Min height above ground (m)",
                    () => Fly.MinClearance, v => Fly.MinClearance = v, 0.5f, 100f);
                TuningRegistry.RegisterFloat("fly.farClipPlane", "Camera", "Far clip plane (m)",
                    () => Fly.FarClipPlane, v => Fly.FarClipPlane = v, 100f, 50000f);
                TuningRegistry.RegisterAction("camera.reset", "Camera", "Reset camera (frame terrain)",
                    () => Terrain.ResetCamera());
            }

            // Render scale — fill-rate lever for full-screen / high-DPI lag.
            TuningRegistry.RegisterFloat("quality.renderScale", "Camera", "Render scale (lower = faster)",
                () => t.RenderScaleValue, v => t.RenderScaleValue = v, 0.4f, 1.5f);
        }
    }
}
