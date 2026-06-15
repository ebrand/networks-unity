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
            PollEnvironmentForChanges();   // catch in-game Environment-palette edits (they bypass the registry)
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

            // --- DEM world ---
            TuningRegistry.RegisterFloat("terrain.demLod", "DEM", "LOD (pixel error)",
                () => t.DemLod, v => t.DemLod = v, 1f, 20f, 0.5f,
                description: "DEM terrain detail: Unity heightmap pixel error. LOWER = denser mesh / more detail (heavier); HIGHER = coarser / faster. Applies live to a loaded DEM, and on load.");

            // --- Chunk-streaming test world ---
            TuningRegistry.RegisterFloat("terrain.chunkRadius", "Chunk", "Bubble radius (chunks)",
                () => t.ChunkRadius, v => t.ChunkRadius = v, 1f, 50f, 1f,
                description: "VISIBLE chunk radius (rendered) = (2r+1)²: r2 = 5×5/25, r3 = 7×7/49, r4 = 9×9/81. " +
                             "Re-streams on change.");
            TuningRegistry.RegisterFloat("terrain.chunkPreload", "Chunk", "Preload depth (rings)",
                () => t.ChunkPreloadDepth, v => t.ChunkPreloadDepth = v, 0f, 6f, 1f,
                description: "Extra rings loaded-but-INVISIBLE beyond the visible radius — the streaming " +
                             "buffer. As you move, these flip visible instantly while a new outer ring loads " +
                             "in the background. Deeper = more headroom for fast movement, but more resident memory.");
            TuningRegistry.RegisterFloat("terrain.chunkBudget", "Chunk", "Load budget (chunks/frame)",
                () => t.ChunkBudget, v => t.ChunkBudget = v, 1f, 32f, 1f,
                description: "How many chunks may load per frame while streaming. Lower = smoother (no hitch) " +
                             "but fills slower; the preload depth must be deep enough that a low budget keeps " +
                             "up with your speed, or you'll see pop-in at the visible edge.");
            TuningRegistry.RegisterFloat("terrain.chunkAmplitude", "Chunk", "Amplitude (m)",
                () => t.ChunkAmplitude, v => t.ChunkAmplitude = v, 0f, 8000f, 50f,
                description: "Max terrain height for the procedural multi-octave landscape. Changing it " +
                             "regenerates every loaded chunk (can hitch on big/high-res bubbles). ~20 = gentle, " +
                             "5000 = dramatic mountains.");
            TuningRegistry.RegisterFloat("terrain.chunkDetail", "Chunk", "Detail (px/vertex)",
                () => t.ChunkPixelsPerVertex, v => t.ChunkPixelsPerVertex = v, 1.5f, 24f, 0.5f,
                description: "Screen-space LOD quality: target screen pixels per terrain vertex. LOWER = " +
                             "finer/denser mesh (more triangles), HIGHER = coarser/cheaper. Each chunk's " +
                             "resolution is derived from this + how big it is on screen, so zooming drives LOD.");
            TuningRegistry.RegisterFloat("terrain.chunkRes", "Chunk", "Near res (2ⁿ+1)",
                () => t.ChunkRes, v => t.ChunkRes = v, 129f, 1025f, 1f,
                description: "Per-chunk heightmap resolution; snaps to 129 / 257 / 513 / 1025. The BIGGEST " +
                             "per-chunk load-cost lever (SetHeights scales with Res²): 129 ≈ 8 m/sample (fastest), " +
                             "513 ≈ 2 m (sharpest sculpt). Rebuilds the bubble; edits saved at another Res are dropped.");
            TuningRegistry.RegisterBool("terrain.chunkGrid", "Chunk", "Grid (1 km / 100 m)",
                () => t.ChunkShowGrid, v => t.ChunkShowGrid = v,
                description: "Paint a 1 km major / 100 m minor grid on the chunk ground for scale.");
            TuningRegistry.RegisterBool("terrain.chunkLock", "Chunk", "Lock bubble (hold Space)",
                () => t.ChunkLockBubble, v => t.ChunkLockBubble = v,
                description: "Freeze the resident set: pan/zoom freely, no cull/load. Hold Space to reposition.");
            TuningRegistry.RegisterBool("terrain.chunkSkirts", "Chunk", "Skirts (hide LOD cracks)",
                () => t.ChunkSkirts, v => t.ChunkSkirts = v,
                description: "Perimeter skirt walls that fill the thin gaps where chunks at different LODs meet. " +
                             "Off rebuilds the resident meshes flat — toggle it to A/B whether cracks return.");
            TuningRegistry.RegisterFloat("terrain.chunkColliderRadius", "Chunk", "Collider radius cap (chunks)",
                () => t.ChunkColliderRadius, v => t.ChunkColliderRadius = v, 1f, 64f, 1f,
                description: "CAP on how far from the bubble centre chunks get a cooked MeshCollider. Default (32) " +
                             "covers the whole bubble so everything you see is sculptable; lower it only to save " +
                             "physics memory on huge stress bubbles (collider cooking is async now, so it's cheap).");
            TuningRegistry.RegisterFloat("terrain.chunkRecenter", "Chunk", "Re-center deadband (chunks)",
                () => t.ChunkRecenterDeadband, v => t.ChunkRecenterDeadband = v, 1f, 8f, 1f,
                description: "Re-center the bubble only after the look-point drifts this many chunks. Higher = less " +
                             "load/cull churn when panning around at altitude, at the cost of a laggier leading edge.");
            // (Water controls live in the "Water" group — they drive the chunk water when active.)
            TuningRegistry.RegisterBool("terrain.chunkLocalGrid", "Chunk", "Local build grid",
                () => t.ChunkLocalGrid, v => t.ChunkLocalGrid = v,
                description: "Drops a fine ~10 m / 50 m grid patch where you're looking, draped on the terrain, for " +
                             "close-up building/sculpting. STATIC once placed — re-toggle to move it to a new spot.");
            TuningRegistry.RegisterFloat("terrain.chunkTopoMinor", "Chunk", "Topo minor (m)",
                () => t.ChunkContourInterval, v => t.ChunkContourInterval = v, 1f, 100f, 1f,
                description: "Spacing of the chunk-world topo contour lines (the Topo footer button toggles them on/off).");
            TuningRegistry.RegisterFloat("terrain.chunkTopoStrength", "Chunk", "Topo strength",
                () => t.ChunkContourStrength, v => t.ChunkContourStrength = v, 0.05f, 1f, 0.05f,
                description: "Opacity/contrast of the chunk-world topo contour overlay.");
            TuningRegistry.RegisterFloat("terrain.chunkRidgeScale", "Chunk", "Ridge scale (m)",
                () => t.ChunkRidgeScale, v => t.ChunkRidgeScale = v, 2f, 200f, 1f,
                description: "Curvature stencil arm for the ridge/valley overlay (⛰ footer toggle). Bigger = broader " +
                             "ridges, smoother, ignores fine DEM noise. Changing this re-bakes every loaded chunk (a brief hitch).");
            TuningRegistry.RegisterFloat("terrain.chunkRidgeThreshold", "Chunk", "Ridge sensitivity (1/m)",
                () => t.ChunkRidgeThreshold, v => t.ChunkRidgeThreshold = v, 0.001f, 0.06f, 0.001f,
                description: "Curvature a crest/hollow must exceed to light up. LOWER = more sensitive (fainter ridges show).");
            TuningRegistry.RegisterFloat("terrain.chunkRidgeStrength", "Chunk", "Ridge strength",
                () => t.ChunkRidgeStrength, v => t.ChunkRidgeStrength = v, 0.05f, 1f, 0.05f,
                description: "Opacity of the ridge/valley overlay.");

            // --- Hydrology (drainage / catchment) analysis overlay (💧 footer toggle) ---
            TuningRegistry.RegisterFloat("hydro.window", "Hydrology", "Window size (m)",
                () => HydrologyOverlay.WorldSize, v => { HydrologyOverlay.WorldSize = v; HydrologyOverlay.RefreshLast(); }, 300f, 8000f, 100f,
                description: "Side of the square analysis window (centred on the camera's look-point). MUST enclose the whole catchment — its edges are treated as outlets, so too small a window reads false ponding at the borders.");
            TuningRegistry.RegisterFloat("hydro.res", "Hydrology", "Resolution (cells/side)",
                () => HydrologyOverlay.Res, v => { HydrologyOverlay.Res = Mathf.RoundToInt(v); HydrologyOverlay.RefreshLast(); }, 64f, 1024f, 32f,
                description: "Analysis grid resolution. Higher = finer drainage detail but a longer recompute.");
            TuningRegistry.RegisterFloat("hydro.pondMin", "Hydrology", "Min ponding (m)",
                () => HydrologyOverlay.PondMin, v => { HydrologyOverlay.PondMin = v; HydrologyOverlay.RefreshLast(); }, 0.05f, 5f, 0.05f,
                description: "Minimum depression depth (m) to paint blue. Lower = shows shallow ponding too.");
            TuningRegistry.RegisterFloat("hydro.rain", "Hydrology", "Rain intensity",
                () => HydrologyOverlay.RainIntensity, v => { HydrologyOverlay.RainIntensity = v; HydrologyOverlay.RefreshLast(); }, 0f, 1f, 0.02f,
                description: "How wet: UP = heavier rain, so lower/finer river beds fill with flow (cyan); DOWN = only major channels. Internally lowers the upstream-area threshold on a log scale (driest ~0.02 → wettest ~5e-5 of the window).");
            TuningRegistry.RegisterFloat("hydro.strength", "Hydrology", "Overlay strength",
                () => HydrologyOverlay.Strength, v => { HydrologyOverlay.Strength = v; HydrologyOverlay.RefreshLast(); }, 0.1f, 1f, 0.05f,
                description: "Opacity of the hydrology overlay.");
            TuningRegistry.RegisterBool("hydro.live", "Hydrology", "Live update on edit",
                () => HydrologyOverlay.LiveUpdate, v => HydrologyOverlay.LiveUpdate = v,
                description: "Re-run the drainage analysis automatically after each terrain edit (sculpt/road/terrace) settles, over the SAME window. Off = only via 'Recompute here'. Each recompute is a brief hitch (the flood-fill is O(n log n)).");
            TuningRegistry.RegisterAction("hydro.refresh", "Hydrology", "Recompute here",
                () => t.RefreshHydrology(),
                description: "Re-run the analysis around the current look-point — use this to MOVE the window to where you're now looking.");
            TuningRegistry.RegisterFloat("terrain.seaTolerance", "Chunk", "Sea tool: tolerance (m)",
                () => t.SeaTolerance, v => t.SeaTolerance = v, 0.5f, 30f, 0.5f,
                description: "Sea brush (6): how close in altitude counts as the same flooded area. One click floods " +
                             "the contiguous region within ±this of the clicked height (inside loaded chunks only).");
            TuningRegistry.RegisterFloat("terrain.seaDrop", "Chunk", "Sea tool: drop (m)",
                () => t.SeaDrop, v => t.SeaDrop = v, 0f, 200f, 1f,
                description: "Sea brush (6): how far below the clicked height to flatten the flooded area — sink the " +
                             "flat DEM ocean below the water plane so it stops z-fighting.");
            TuningRegistry.RegisterFloat("terrain.batterRatio", "Chunk", "Slope tool: batter 1:N",
                () => t.BatterRatio, v => t.BatterRatio = v, 0.5f, 8f, 0.5f,
                description: "Slope tool (5) side-slope steepness, 1 vertical : N horizontal. The batter daylights " +
                             "into the terrain (cut/fill), so the smoothing extent grows with depth. 2 = 1:2 gentle, 1 = 1:1 steep.");
            TuningRegistry.RegisterFloat("terrain.chunkDemNormMin", "Chunk", "DEM range min (m)",
                () => t.ChunkDemNormMin, v => t.ChunkDemNormMin = v, -1000f, 4000f, 50f,
                description: "Elevation that 16-bit value 0 decodes to. MUST match the range the DEM PNGs were " +
                             "EXPORTED at (the Highres set is -500). Changing it on a loaded DEM rescales the heights " +
                             "(does NOT recover resolution — that needs a re-export). Takes effect on the next DEM load.");
            TuningRegistry.RegisterFloat("terrain.chunkDemNormMax", "Chunk", "DEM range max (m)",
                () => t.ChunkDemNormMax, v => t.ChunkDemNormMax = v, 500f, 9000f, 50f,
                description: "Elevation that 16-bit value 65535 decodes to. MUST match the PNG export range (Highres = 9000). " +
                             "Tighter span = finer vertical steps ONLY if the tiles were re-exported at that span.");

            // --- Heightmap import ---
            TuningRegistry.RegisterSelect("terrain.heightmapPick", "Heightmap", "Pick from Heightmaps/",
                () => t.HeightmapFile, v => t.HeightmapFile = v, () => t.ListHeightmapFiles(),
                description: "Choose a PNG from Assets/Heightmaps. This only selects it — click 'Load heightmap' to actually load it (which REPLACES all heights). Drop new PNGs in that folder and reconnect to refresh the list.");
            TuningRegistry.RegisterString("terrain.heightmapPath", "Heightmap", "File (or path)",
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
            TuningRegistry.RegisterFloat("rail.curveSymmetrySnap", "Rail", "Symmetric-curve snap",
                () => t.RailLayer.CurveSymmetrySnap, v => t.RailLayer.CurveSymmetrySnap = v, 0f, 0.5f,
                description: "After the bend, snap the end to the same leg length as start->bend (symmetric curve) when within this fraction of it. 0 = off.");
            TuningRegistry.RegisterFloat("rail.connectHover", "Rail", "Join hover radius (m)",
                () => t.RailLayer.ConnectHoverRadius, v => t.RailLayer.ConnectHoverRadius = v, 5f, 120f,
                description: "Hover within this of another endpoint while extending to engage the auto-fillet join (and stop the placement cursor sliding toward it).");
            TuningRegistry.RegisterFloat("rail.extOffAxis", "Rail", "Off-axis suppress (m)",
                () => t.RailLayer.ExtensionOffAxisDist, v => t.RailLayer.ExtensionOffAxisDist = v, 2f, 60f,
                description: "While extending a straight, if the mouse is more than this off the collinear line the placement cursor stops sliding (only the brush ring follows).");
            TuningRegistry.RegisterFloat("rail.minDeflection", "Rail", "Min curve deflection (deg)",
                () => t.RailLayer.MinCurveDeflectionDeg, v => t.RailLayer.MinCurveDeflectionDeg = v, 0.5f, 30f,
                description: "Min-distance target: the bend can't be placed until the first leg gives at least this much turn above/below the centreline for the speed. Guide stays red until then; any safe deflection builds past it.");
            TuningRegistry.RegisterFloat("rail.inspectWidth", "Rail", "Curve inspect box width (m)",
                () => t.RailLayer.CurveInspectWidth, v => t.RailLayer.CurveInspectWidth = v, 4f, 60f,
                description: "Width of the dashed inspection box drawn around each segment in the inspect overlay.");
            TuningRegistry.RegisterFloat("rail.trainLength", "Rail", "Typical train length (m)",
                () => t.RailLayer.TypicalTrainLengthM, v => t.RailLayer.TypicalTrainLengthM = v, 10f, 600f,
                description: "Used by the inspect overlay to report queue space on a straight: how many of these trains fit on the hovered segment.");
            TuningRegistry.RegisterFloat("rail.maxLatG", "Rail", "Max lateral g (curve tightness)",
                () => t.RailLayer.MaxLateralG, v => t.RailLayer.MaxLateralG = v, 0.05f, 0.5f,
                description: "Comfort/cant limit (shared by build + plan). Higher = tighter curves allowed at a given speed (smaller min radius). Real rail ~0.1; raise for game-scaled curves.");
            TuningRegistry.RegisterFloat("rail.gradeStep", "Rail", "Grade sample step (m)",
                () => t.RailLayer.GradeSampleStep, v => t.RailLayer.GradeSampleStep = v, 1f, 50f,
                description: "Spacing at which the terrain elevation is sampled along an edge to check the per-section grade — the 'every N metres' resolution. Smaller = finer (catches short steep bumps), larger = smoother.");

            // --- Plan grade labels (world-space, painted on the ground) ---
            TuningRegistry.RegisterFloat("plan.labelSize", "Grade labels", "Label size (world m)",
                () => WorldGradeLabels.Size, v => WorldGradeLabels.Size = v, 5f, 120f, 1f,
                description: "World text height of the on-ground grade % labels. Bigger = readable from higher up / farther away.");
            TuningRegistry.RegisterFloat("plan.labelLift", "Grade labels", "Label lift (m)",
                () => WorldGradeLabels.Lift, v => WorldGradeLabels.Lift = v, 0f, 20f, 0.5f,
                description: "Metres the labels float above the route midpoint (clears z-fighting with the terrain + contour overlay).");
            TuningRegistry.RegisterBool("plan.labelFlat", "Grade labels", "Lay flat on the ground",
                () => WorldGradeLabels.LieFlat, v => WorldGradeLabels.LieFlat = v,
                description: "On: labels painted flat on the terrain along the track. Off: upright billboard text that always faces the camera.");
            // --- Auto-route (A* plan router) ---
            TuningRegistry.RegisterFloat("plan.routeClimb", "Auto-route", "Climb avoidance",
                () => RailAutoRoute.ClimbWeight, v => RailAutoRoute.ClimbWeight = v, 5f, 80f, 1f,
                description: "Metres of horizontal detour worth avoiding 1 m of climb. Higher = hunts harder for the draw / hugs contours; lower = straighter, more climb.");
            TuningRegistry.RegisterBool("plan.routeEnforceGrade", "Auto-route", "Route ≤ ruling grade",
                () => RailAutoRoute.EnforceGrade, v => RailAutoRoute.EnforceGrade = v,
                description: "On: escalate climb-avoidance until every segment's ground grade ≤ Max grade %, else report best effort. Off: single-pass least-ascent corridor.");
            TuningRegistry.RegisterBool("plan.routeSpeedCurves", "Auto-route", "Speed-limit curves",
                () => RailAutoRoute.SpeedLimitCurves, v => RailAutoRoute.SpeedLimitCurves = v,
                description: "On: round route corners tighter than the design speed's min radius after routing (approximate; can pull tight bends in a little and nudge the grade).");
            TuningRegistry.RegisterFloat("plan.routeStraightness", "Auto-route", "Route straightness (m)",
                () => RailAutoRoute.SimplifyMeters, v => RailAutoRoute.SimplifyMeters = v, 10f, 300f, 5f,
                description: "Douglas-Peucker tolerance: how much micro-wiggle to drop before smoothing. Higher = longer straights with deliberate bends (more cut/fill, more realistic); lower = hugs the terrain.");
            TuningRegistry.RegisterFloat("rail.trackSnap", "Rail", "Track snap radius (m)",
                () => t.RailLayer.TrackSnapRadius, v => t.RailLayer.TrackSnapRadius = v, 0f, 25f,
                description: "Snap radius for connecting to EXISTING track: a click within this of a node or rail edge is pulled exactly onto it (overrides grid snap) so it reliably joins the network. 0 = grid snap only.");
            TuningRegistry.RegisterFloat("rail.guideLength", "Rail", "Alignment guide length (m)",
                () => t.RailLayer.ExtensionGuideLength, v => t.RailLayer.ExtensionGuideLength = v, 0f, 600f,
                description: "Length of the dashed straight-ahead alignment guide drawn out of the chain tail. Also bounds how far ahead the extension snap reaches. 0 = no guide / no extension snap.");
            TuningRegistry.RegisterFloat("rail.puckSize", "Rail", "Node puck size (m)",
                () => t.RailLayer.NodePuckSize, v => t.RailLayer.NodePuckSize = v, 0.3f, 6f,
                description: "Radius of the translucent node pucks.");
            TuningRegistry.RegisterFloat("rail.puckHeight", "Rail", "Node puck height (m)",
                () => t.RailLayer.NodePuckHeight, v => t.RailLayer.NodePuckHeight = v, 0.05f, 3f,
                description: "Height (thickness) of the 3D node pucks.");
            TuningRegistry.RegisterFloat("rail.slopeWidth", "Rail", "Auto-slope corridor width (m)",
                () => t.RailSlopeWidth, v => t.RailSlopeWidth = v, 2f, 40f,
                description: "Full width of the bed corridor graded by the node-to-node auto-slope (Alt+click node A then node B).");
            TuningRegistry.RegisterColor("rail.puckColor", "Rail", "Node puck color",
                () => t.RailLayer.NodePuckColor, v => t.RailLayer.NodePuckColor = v,
                description: "Colour (alpha < 1 = translucent) of the rail node pucks.");
            TuningRegistry.RegisterColor("rail.puckHoverColor", "Rail", "Node puck hover color",
                () => t.RailLayer.NodePuckHoverColor, v => t.RailLayer.NodePuckHoverColor = v,
                description: "Colour of the puck under the cursor (the node you'd pick / insert by).");
            TuningRegistry.RegisterFloat("rail.brakingDecel", "Rail", "Braking decel (m/s^2)",
                () => t.RailLayer.BrakingDecel, v => t.RailLayer.BrakingDecel = v, 0.1f, 1.3f,
                description: "Service deceleration used for braking distance d = (vFast^2 - vSlow^2)/(2a). Comfortable rail ~0.2-0.4; emergency up to ~1.3. Bigger = shorter distance.");
            TuningRegistry.RegisterColor("rail.brakingOkColor", "Rail", "Decel-complete color",
                () => t.RailLayer.BrakingOkColor, v => t.RailLayer.BrakingOkColor = v,
                description: "Marker colour for where the train is fully slowed on the new line (slower-speed curves can begin past there).");
            TuningRegistry.RegisterFloat("rail.decelRingOuter", "Rail", "Decel ring outer radius (m)",
                () => t.RailLayer.DecelRingOuterRadius, v => t.RailLayer.DecelRingOuterRadius = v, 1f, 50f,
                description: "Radius of the dashed outer ring on a decel snap target.");
            TuningRegistry.RegisterFloat("rail.decelRingInner", "Rail", "Decel ring inner radius (m)",
                () => t.RailLayer.DecelRingInnerRadius, v => t.RailLayer.DecelRingInnerRadius = v, 0.2f, 10f,
                description: "Radius of the small solid inner snap-point on a decel target.");
            TuningRegistry.RegisterFloat("rail.decelSnap", "Rail", "Decel snap radius (m)",
                () => t.RailLayer.DecelSnapRadius, v => t.RailLayer.DecelSnapRadius = v, 1f, 60f,
                description: "How close the cursor must get to a decel target (half or full) to snap to it.");
            TuningRegistry.RegisterColor("rail.decelRingColor", "Rail", "Decel ring color",
                () => t.RailLayer.DecelRingColor, v => t.RailLayer.DecelRingColor = v,
                description: "Colour of the decel target rings.");
            TuningRegistry.RegisterBool("rail.showSpeedLabels", "Rail", "Show speed labels",
                () => t.RailLayer.ShowSpeedLabels, v => t.RailLayer.ShowSpeedLabels = v,
                description: "Label each rail line with its speed limit at intervals, to interrogate what speed an existing line carries. Shown while editing rail.");
            TuningRegistry.RegisterFloat("rail.speedLabelSpacing", "Rail", "Speed label spacing (m)",
                () => t.RailLayer.SpeedLabelSpacing, v => { t.RailLayer.SpeedLabelSpacing = v; t.RebuildRail(); }, 50f, 3000f,
                description: "Spacing between speed-limit labels along a line. Bigger = fewer labels.");
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
            TuningRegistry.RegisterColor("rail.railColor", "Rail", "Rail color",
                () => t.RailLayer.RailColor, v => { t.RailLayer.RailColor = v; t.RebuildRail(); },
                description: "Colour of the metal rails.");
            TuningRegistry.RegisterColor("rail.tieColor", "Rail", "Tie color",
                () => t.RailLayer.TieColor, v => { t.RailLayer.TieColor = v; t.RebuildRail(); },
                description: "Colour of the wooden sleepers (ties).");
            TuningRegistry.RegisterAction("rail.clear", "Rail", "Clear rail",
                () => t.ClearRail(),
                description: "Delete the entire rail network (all nodes and edges).");
            TuningRegistry.RegisterAction("terrain.diagnose", "Autosave", "Diagnose scene renderers",
                () => t.DiagnoseScatter(),
                description: "Log every non-terrain mesh renderer (name/path/hideFlags/scene) — for tracking down 'phantom' objects that aren't deletable.");

            // --- Rail planning (survey) ---
            TuningRegistry.RegisterFloat("plan.tracks", "Rail plan", "Tracks (1 or 2)",
                () => t.PlanLayer.Tracks, v => { t.PlanLayer.Tracks = Mathf.Clamp(Mathf.RoundToInt(v), 1, 2); t.RebuildPlan(); }, 1f, 2f,
                description: "Plan a single centreline (1) or a double-track pair (2) drawn at +/- half the track gap.");
            TuningRegistry.RegisterFloat("plan.trackGap", "Rail plan", "Track gap (m)",
                () => t.PlanLayer.TrackGap, v => { t.PlanLayer.TrackGap = v; t.RebuildPlan(); }, 1f, 30f,
                description: "Distance between the two tracks (centre to centre) for a double-track corridor.");
            TuningRegistry.RegisterBool("plan.limitRadius", "Rail plan", "Limit curve radius (speed)",
                () => t.PlanLayer.LimitCurveRadius, v => t.PlanLayer.LimitCurveRadius = v,
                description: "Restrict planned curves to the minimum radius for the design speed + lateral g (tighter curves are refused; preview turns red). Off = unconstrained survey lines. Uses the shared design speed (rail.speedLimit / rail.maxLatG).");
            TuningRegistry.RegisterFloat("plan.curveLever", "Rail plan", "Curve lever (arc width)",
                () => t.PlanLayer.CurveLever, v => t.PlanLayer.CurveLever = v, 0.1f, 0.95f,
                description: "Curve-tool shape for the plan: how far the bezier controls lean toward the guide corner.");
            TuningRegistry.RegisterFloat("plan.curveSymmetrySnap", "Rail plan", "Symmetric-curve snap",
                () => t.PlanLayer.CurveSymmetrySnap, v => t.PlanLayer.CurveSymmetrySnap = v, 0f, 0.5f,
                description: "After the bend, snap the end to the same leg length as start->bend (symmetric curve) when within this fraction of it. 0 = off.");
            TuningRegistry.RegisterFloat("plan.minDeflection", "Rail plan", "Min curve deflection (deg)",
                () => t.PlanLayer.MinCurveDeflectionDeg, v => t.PlanLayer.MinCurveDeflectionDeg = v, 0.5f, 30f,
                description: "Min-distance target: the bend can't be placed until the first leg gives at least this much turn above/below the centreline for the speed. Guide stays red until then; any safe deflection builds past it.");
            TuningRegistry.RegisterFloat("plan.guideLength", "Rail plan", "Guide length (m)",
                () => t.PlanLayer.ExtensionGuideLength, v => t.PlanLayer.ExtensionGuideLength = v, 0f, 1000f,
                description: "Length of the dashed straight-ahead alignment guide (off the rail end or the previous segment). Also bounds how far the snap reaches.");
            TuningRegistry.RegisterFloat("plan.guideSnap", "Rail plan", "Guide snap radius (m)",
                () => t.PlanLayer.ExtensionSnapRadius, v => t.PlanLayer.ExtensionSnapRadius = v, 0f, 30f,
                description: "Perpendicular distance within which the cursor snaps onto the alignment guide.");
            TuningRegistry.RegisterFloat("plan.endSnap", "Rail plan", "End/resume snap radius (m)",
                () => t.PlanLayer.EndSnapRadius, v => t.PlanLayer.EndSnapRadius = v, 0f, 30f,
                description: "Snap radius for resuming/joining the plan's own nodes (the end of the corridor you already drew).");
            TuningRegistry.RegisterFloat("plan.sampleStep", "Rail plan", "Drape step (m)",
                () => t.PlanLayer.SampleStep, v => { t.PlanLayer.SampleStep = v; t.RebuildPlan(); }, 0.5f, 20f,
                description: "Sampling step for draping the plan lines onto the terrain. Smaller = smoother, more segments.");
            TuningRegistry.RegisterColor("plan.color", "Rail plan", "Plan color",
                () => t.PlanLayer.PlanColor, v => { t.PlanLayer.PlanColor = v; t.RebuildPlan(); },
                description: "Colour (with alpha) of the draped survey lines.");
            TuningRegistry.RegisterBool("plan.analyze", "Rail plan", "Analyze (mark corridor)",
                () => t.PlanLayer.ShowAnalysis, v => { t.PlanLayer.ShowAnalysis = v; t.RebuildPlan(); },
                description: "Mark the corridor by what each section needs vs a grade-limited bed. Colour-blind-safe: at-grade=solid plan line, cut=dashed, fill=double-dashed; bridge=cyan, tunnel=purple, over-grade=red. Off = plain survey.");
            TuningRegistry.RegisterBool("plan.gradeLabels", "Rail plan", "Grade labels",
                () => t.PlanLayer.ShowGradeLabels, v => t.PlanLayer.ShowGradeLabels = v,
                description: "Float a grade-% label over each plan segment (current terrain) while editing the plan or slope-grading — reads the natural grade before earthworks, the achieved grade after. Red = over max grade.");
            TuningRegistry.RegisterFloat("plan.maxGrade", "Rail plan", "Max grade (%)",
                () => t.PlanLayer.MaxGradePercent, v => { t.PlanLayer.MaxGradePercent = v; t.RebuildPlan(); }, 0.5f, 10f, 0.1f,
                description: "Ruling grade: an edge whose endpoint-to-endpoint grade exceeds this %% is flagged OVER-GRADE (red) — too steep to connect at grade; develop the line / switchback. 2.2% is steep mountain mainline.");
            TuningRegistry.RegisterFloat("plan.atGradeBand", "Rail plan", "At-grade band (m)",
                () => t.PlanLayer.AtGradeBand, v => { t.PlanLayer.AtGradeBand = v; t.RebuildPlan(); }, 0f, 5f,
                description: "Cut/fill within this much of the graded bed reads as buildable at-grade (green).");
            TuningRegistry.RegisterFloat("plan.bridgeDepth", "Rail plan", "Bridge fill depth (m)",
                () => t.PlanLayer.BridgeFillDepth, v => { t.PlanLayer.BridgeFillDepth = v; t.RebuildPlan(); }, 1f, 40f,
                description: "Fill deeper than this is flagged as needing a bridge (cyan) rather than an embankment.");
            TuningRegistry.RegisterFloat("plan.tunnelDepth", "Rail plan", "Tunnel cut depth (m)",
                () => t.PlanLayer.TunnelCutDepth, v => { t.PlanLayer.TunnelCutDepth = v; t.RebuildPlan(); }, 1f, 40f,
                description: "Cut deeper than this is flagged as needing a tunnel (purple) rather than an open cut.");

            // --- Brush cursor ---
            TuningRegistry.RegisterBool("terrain.showCursor", "Brush cursor", "Show ring",
                () => t.ShowBrushCursor, v => t.ShowBrushCursor = v);
            TuningRegistry.RegisterColor("terrain.cursorColor", "Brush cursor", "Color",
                () => t.BrushCursorColor, v => t.BrushCursorColor = v);
            TuningRegistry.RegisterColor("terrain.slopeFillColor", "Brush cursor", "Slope fill color",
                () => t.SlopeFillColor, v => t.SlopeFillColor = v);
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

            // --- Water --- (drives the CHUNK water plane when the chunk world is active, else low-poly)
            TuningRegistry.RegisterBool("water.on", "Water", "Show water",
                () => t.ChunkTestActive ? t.ChunkShowWater : t.ShowWater,
                v => { if (t.ChunkTestActive) t.ChunkShowWater = v; else { t.ShowWater = v; t.ApplyWater(); } },
                description: "Show a flat water surface across the map; terrain below the level reads as submerged.");
            TuningRegistry.RegisterFloat("water.level", "Water", "Level (m)",
                () => t.ChunkTestActive ? t.ChunkWaterLevel : t.WaterLevel,
                v => { if (t.ChunkTestActive) t.ChunkWaterLevel = v; else { t.WaterLevel = v; t.ApplyWater(); } }, -50f, 1000f, 1f,
                description: "World height of the water surface (chunk water snaps to whole metres; 0 ≈ sea level). " +
                             "Raise it to flood low ground / fill a gorge.");
            // Water LOOK (colour/alpha/smoothness) isn't stored per-game, so it relies on this global tunable.
            // The persisted value loads during Start, BEFORE the chunk world is up (ChunkTestActive=false) — so
            // write BOTH backends, else the loaded colour lands on the unused low-poly water and the chunk water
            // (ChunkOverlays statics) keeps its default. ApplyWater only when the low-poly water is the live one.
            TuningRegistry.RegisterColor("water.color", "Water", "Color",
                () => t.ChunkTestActive ? t.ChunkWaterColor : t.WaterColor,
                v => { t.WaterColor = v; t.ChunkWaterColor = v; if (!t.ChunkTestActive) t.ApplyWater(); },
                description: "Water colour. Its alpha controls transparency (lower = see the bed through it).");
            TuningRegistry.RegisterFloat("water.alpha", "Water", "Alpha (transparency)",
                () => (t.ChunkTestActive ? t.ChunkWaterColor : t.WaterColor).a,
                v => { Color c = t.WaterColor; c.a = v; t.WaterColor = c; Color cc = t.ChunkWaterColor; cc.a = v; t.ChunkWaterColor = cc; if (!t.ChunkTestActive) t.ApplyWater(); }, 0.05f, 1f,
                description: "Water transparency — lower = clearer (see the bed through it), 1 = opaque. (Same as the colour's alpha channel; min 0.05 so it isn't reset by the 0-alpha guard.)");
            TuningRegistry.RegisterFloat("water.smoothness", "Water", "Smoothness",
                () => t.ChunkTestActive ? t.ChunkWaterSmoothness : t.WaterSmoothness,
                v => { t.WaterSmoothness = v; t.ChunkWaterSmoothness = v; if (!t.ChunkTestActive) t.ApplyWater(); }, 0f, 1f,
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
            TuningRegistry.RegisterFloat("terrain.paletteAlpha", "Interface", "Palette background alpha",
                () => t.PaletteBgAlpha, v => t.PaletteBgAlpha = v, 0f, 1f,
                description: "Opacity of the in-game UI Toolkit tool palette background (0 = transparent, 1 = solid).");

            // --- Camera (free-fly) ---
            if (Fly != null)
            {
                TuningRegistry.RegisterFloat("fly.moveSpeed", "Camera", "Move speed (m/s)",
                    () => Fly.MoveSpeed, v => Fly.MoveSpeed = v, 5f, 50000f);
                TuningRegistry.RegisterFloat("fly.zoomStep", "Camera", "Zoom step (m/notch)",
                    () => Fly.ZoomStep, v => Fly.ZoomStep = v, 5f, 20000f);
                TuningRegistry.RegisterFloat("fly.fastMultiplier", "Camera", "Shift fast multiplier",
                    () => Fly.FastMultiplier, v => Fly.FastMultiplier = v, 1f, 100f);
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
                    () => Fly.SpeedAltitudeReference, v => Fly.SpeedAltitudeReference = v, 10f, 200000f);
                TuningRegistry.RegisterFloat("fly.minSpeedFactor", "Camera", "Min speed factor (low altitude)",
                    () => Fly.MinSpeedFactor, v => Fly.MinSpeedFactor = v, 0.05f, 1f);
                TuningRegistry.RegisterFloat("fly.maxAltitude", "Camera", "Max altitude (m)",
                    () => Fly.MaxAltitude, v => Fly.MaxAltitude = v, 50f, 1000000f);
                TuningRegistry.RegisterFloat("fly.minClearance", "Camera", "Min height above ground (m)",
                    () => Fly.MinClearance, v => Fly.MinClearance = v, 0.5f, 100f);
                TuningRegistry.RegisterFloat("fly.farClipPlane", "Camera", "Far clip plane (m)",
                    () => Fly.FarClipPlane, v => Fly.FarClipPlane = v, 100f, 1000000f);
                TuningRegistry.RegisterAction("camera.reset", "Camera", "Reset camera (frame terrain)",
                    () => Terrain.ResetCamera());
            }

            // Render scale — fill-rate lever for full-screen / high-DPI lag.
            TuningRegistry.RegisterFloat("quality.renderScale", "Camera", "Render scale (lower = faster)",
                () => t.RenderScaleValue, v => t.RenderScaleValue = v, 0.4f, 1.5f);

            // --- Environment: time of day (DayCycle) ---
            // These mirror the in-game Environment palette (hotkey U). Registering them here is what makes
            // the palette PERSIST (it writes the statics directly; the poll in Update saves them) and brings
            // them into the React panel for parity. Setters re-Apply so a loaded value drives the scene.
            TuningRegistry.RegisterBool("env.dayCycle", "Time of day", "Day cycle",
                () => DayCycle.Enabled, v => DayCycle.SetEnabled(v));
            TuningRegistry.RegisterBool("env.manageSky", "Time of day", "Sky + ambient",
                () => DayCycle.ManageSky, v => { DayCycle.ManageSky = v; DayCycle.Apply(); });
            TuningRegistry.RegisterFloat("env.timeOfDay", "Time of day", "Time (h)",
                () => DayCycle.TimeOfDay, v => { DayCycle.TimeOfDay = v; DayCycle.Apply(); }, 0f, 24f);
            TuningRegistry.RegisterFloat("env.sunHeight", "Time of day", "Sun height (deg)",
                () => DayCycle.PeakElevation, v => { DayCycle.PeakElevation = v; DayCycle.Apply(); }, 5f, 89f);
            TuningRegistry.RegisterFloat("env.sunDir", "Time of day", "Sun direction (deg)",
                () => DayCycle.NorthYaw, v => { DayCycle.NorthYaw = v; DayCycle.Apply(); }, 0f, 360f);
            TuningRegistry.RegisterFloat("env.brightness", "Time of day", "Brightness",
                () => DayCycle.Intensity, v => { DayCycle.Intensity = v; DayCycle.Apply(); }, 0f, 3f);
            TuningRegistry.RegisterFloat("env.ambient", "Time of day", "Ambient",
                () => DayCycle.AmbientScale, v => { DayCycle.AmbientScale = v; DayCycle.Apply(); }, 0f, 1.5f,
                description: "Multiplier on sky ambient — pull down to tame the bright-sky wash-out that flattens trees.");
            TuningRegistry.RegisterBool("env.autoAdvance", "Time of day", "Auto-advance",
                () => DayCycle.AutoAdvance, v => DayCycle.AutoAdvance = v);
            TuningRegistry.RegisterFloat("env.dayLength", "Time of day", "Day length (min)",
                () => DayCycle.DayLengthMinutes, v => DayCycle.DayLengthMinutes = v, 0.5f, 30f);
            TuningRegistry.RegisterFloat("env.shadowDistance", "Time of day", "Shadow distance (m)",
                () => t.ShadowDistanceValue, v => t.ShadowDistanceValue = v, 50f, 3000f, 10f,
                description: "How far from the camera URP renders real-time shadows. Larger = shadows on distant terrain/trees, but costlier.");

            // --- Environment: atmosphere + colour grade (DemLighting) ---
            TuningRegistry.RegisterBool("env.atmosphere", "Atmosphere", "Atmosphere enable",
                () => DemLighting.Enabled, v => DemLighting.SetEnabled(v));
            TuningRegistry.RegisterBool("env.aces", "Atmosphere", "ACES tonemap (vs Neutral)",
                () => DemLighting.UseACES, v => { DemLighting.UseACES = v; DemLighting.Apply(); },
                description: "Filmic ACES tonemapping (punchy, CS2-like) vs flat Neutral. Pair with exposure ~0.");
            TuningRegistry.RegisterFloat("env.fog", "Atmosphere", "Haze (fog)",
                () => DemLighting.FogDensity, v => { DemLighting.FogDensity = v; DemLighting.Apply(); }, 0f, 0.0006f);
            TuningRegistry.RegisterFloat("env.exposure", "Atmosphere", "Exposure",
                () => DemLighting.Exposure, v => { DemLighting.Exposure = v; DemLighting.Apply(); }, -2f, 2f);
            TuningRegistry.RegisterFloat("env.contrast", "Atmosphere", "Contrast",
                () => DemLighting.Contrast, v => { DemLighting.Contrast = v; DemLighting.Apply(); }, -50f, 50f);
            TuningRegistry.RegisterFloat("env.saturation", "Atmosphere", "Saturation",
                () => DemLighting.Saturation, v => { DemLighting.Saturation = v; DemLighting.Apply(); }, -50f, 50f);
            TuningRegistry.RegisterFloat("env.warmth", "Atmosphere", "Warmth",
                () => DemLighting.Warmth, v => { DemLighting.Warmth = v; DemLighting.Apply(); }, 0f, 1f);
            TuningRegistry.RegisterFloat("env.bloom", "Atmosphere", "Bloom",
                () => DemLighting.BloomIntensity, v => { DemLighting.BloomIntensity = v; DemLighting.Apply(); }, 0f, 1.5f);
            TuningRegistry.RegisterFloat("env.vignette", "Atmosphere", "Vignette",
                () => DemLighting.VignetteAmount, v => { DemLighting.VignetteAmount = v; DemLighting.Apply(); }, 0f, 0.6f);

            // --- Environment: ground texture (TerrainGround shader, via ChunkWorld) ---
            TuningRegistry.RegisterBool("ground.textures", "Ground texture", "Textures",
                () => ChunkWorld.GroundTextures, v => ChunkWorld.GroundTextures = v);
            TuningRegistry.RegisterColor("ground.grassFlat", "Ground texture", "Flat grass colour",
                () => ChunkWorld.GroundGrassColor, v => ChunkWorld.GroundGrassColor = v,
                description: "Untextured terrain grass/flat colour (only visible with Textures off).");
            TuningRegistry.RegisterColor("ground.slopeFlat", "Ground texture", "Flat slope colour",
                () => ChunkWorld.GroundSlopeColor, v => ChunkWorld.GroundSlopeColor = v,
                description: "Untextured terrain mid-slope colour (Textures off).");
            TuningRegistry.RegisterColor("ground.rockFlat", "Ground texture", "Flat rock colour",
                () => ChunkWorld.GroundRockColor, v => ChunkWorld.GroundRockColor = v,
                description: "Untextured terrain steep/rock colour (Textures off).");
            TuningRegistry.RegisterBool("ground.stochastic", "Ground texture", "Anti-repeat (stochastic)",
                () => ChunkWorld.GroundStochastic, v => ChunkWorld.GroundStochastic = v);
            TuningRegistry.RegisterFloat("ground.brightness", "Ground texture", "Ground brightness",
                () => ChunkWorld.GroundBrightness, v => ChunkWorld.GroundBrightness = v, 0f, 1.5f);
            TuningRegistry.RegisterFloat("ground.macroAmount", "Ground texture", "Macro variation",
                () => ChunkWorld.GroundMacroAmount, v => ChunkWorld.GroundMacroAmount = v, 0f, 0.8f);
            TuningRegistry.RegisterFloat("ground.macroSize", "Ground texture", "Macro size (m)",
                () => ChunkWorld.GroundMacroSize, v => ChunkWorld.GroundMacroSize = v, 50f, 800f);
            TuningRegistry.RegisterFloat("ground.texSize", "Ground texture", "Texture size (m)",
                () => ChunkWorld.GroundTexSize, v => ChunkWorld.GroundTexSize = v, 2f, 80f);
            TuningRegistry.RegisterFloat("ground.detailStrength", "Ground texture", "Detail strength",
                () => ChunkWorld.GroundDetailStrength, v => ChunkWorld.GroundDetailStrength = v, 0f, 1f);
            TuningRegistry.RegisterFloat("ground.detailSize", "Ground texture", "Detail size (m)",
                () => ChunkWorld.GroundDetailSize, v => ChunkWorld.GroundDetailSize = v, 0.3f, 8f);
            TuningRegistry.RegisterFloat("ground.noiseSize", "Ground texture", "Patch size (m)",
                () => ChunkWorld.GroundNoiseSize, v => ChunkWorld.GroundNoiseSize = v, 20f, 600f);
            TuningRegistry.RegisterFloat("ground.dirtPatches", "Ground texture", "Dirt patches",
                () => ChunkWorld.GroundDirtPatches, v => ChunkWorld.GroundDirtPatches = v, 0f, 1f);
            TuningRegistry.RegisterFloat("ground.dirtSlope", "Ground texture", "Dirt slope (deg)",
                () => ChunkWorld.GroundDirtSlope, v => ChunkWorld.GroundDirtSlope = v, 0f, 60f);
            TuningRegistry.RegisterFloat("ground.gravelSlope", "Ground texture", "Gravel slope (deg)",
                () => ChunkWorld.GroundGravelSlope, v => ChunkWorld.GroundGravelSlope = v, 0f, 80f);
            TuningRegistry.RegisterFloat("ground.sandHeight", "Ground texture", "Beach sand (m above water)",
                () => ChunkWorld.GroundSandHeight, v => ChunkWorld.GroundSandHeight = v, 0f, 15f);
            TuningRegistry.RegisterFloat("ground.slopeJitter", "Ground texture", "Seam jitter (deg)",
                () => ChunkWorld.GroundSlopeJitter, v => ChunkWorld.GroundSlopeJitter = v, 0f, 40f);
            TuningRegistry.RegisterFloat("ground.normalStrength", "Ground texture", "Normal strength",
                () => ChunkWorld.GroundNormalStrength, v => ChunkWorld.GroundNormalStrength = v, 0f, 2f);

            // --- Foliage shading (instanced trees) ---
            TuningRegistry.RegisterFloat("foliage.nearLodTint", "Foliage", "Near LOD tint",
                () => ForestGen.NearLodTint, v => ForestGen.NearLodTint = v, 0.3f, 1f,
                description: "Brightness of the closest full-detail trees (1 = untinted). Lower if near trees read too bright vs the shaded scene.");
            TuningRegistry.RegisterFloat("foliage.farLodDarken", "Foliage", "Far LOD darken",
                () => ForestGen.FarLodDarken, v => ForestGen.FarLodDarken = v, 0.3f, 1f,
                description: "Brightness of the farthest cross-card LOD (1 = untinted). Far impostors catch full light and pop bright on approach; lower this to match the near mesh.");
            TuningRegistry.RegisterFloat("foliage.nightTint", "Foliage", "Night canopy tint",
                () => ForestGen.NightFoliageTint, v => ForestGen.NightFoliageTint = v, 0f, 0.6f,
                description: "Canopy brightness at full night (0 = black). Counters the foliage shader glowing against the dark sky when the day cycle is on.");

            // --- Road plan excavation ---
            TuningRegistry.RegisterFloat("road.excavateDepth", "Road plan", "Excavate depth (m)",
                () => t.RoadPlanLayer.ExcavationDepth, v => t.RoadPlanLayer.ExcavationDepth = v, 0f, 5f,
                description: "How far below the node-to-node grade line the roadbed is cut.");
            TuningRegistry.RegisterFloat("road.excavateMargin", "Road plan", "Excavate margin (m/side)",
                () => t.RoadPlanLayer.ExcavationMargin, v => { t.RoadPlanLayer.ExcavationMargin = v; t.RebuildRoadPlan(); }, 0f, 20f,
                description: "Extra flat corridor excavated beyond the road footprint on EACH side (shoulder space for deeper cuts / larger fills). Shown as the cyan skirt in plan view.");
            TuningRegistry.RegisterFloat("road.cutBatter", "Road plan", "Cut/fill slope 1:N",
                () => t.RoadPlanLayer.CutBatter, v => t.RoadPlanLayer.CutBatter = v, 0.5f, 6f,
                description: "Side-slope ratio (1 vertical : N horizontal) the excavation ramps beyond the flat bed until it DAYLIGHTS into the existing terrain — prevents the road sitting on a floating shelf / cliff. Bigger = gentler, wider earthwork. Applies to the chunk world; the DEM backend still uses a feathered stamp.");
            TuningRegistry.RegisterAction("road.removeBuilt", "Road plan", "Remove built roads",
                () => t.ClearBuiltRoads(),
                description: "Delete the built 3D road meshes (keeps the plan) — for testing.");

            // --- Retaining wall (tool 9) ---
            TuningRegistry.RegisterFloat("wall.backFillCap", "Retaining wall", "Back fill cap (m)",
                () => t.RetainingWallLayer.BackFillCap, v => t.RetainingWallLayer.BackFillCap = v, 5f, 400f,
                description: "How far back the flat fill bench can reach before giving up. The bench normally stops where the hillside rises to the wall top; lower this to rein in walls with low/flat ground behind (which would otherwise fill a large plateau).");
            TuningRegistry.RegisterFloat("wall.thickness", "Retaining wall", "Thickness (m)",
                () => t.RetainingWallLayer.WallThickness, v => t.RetainingWallLayer.WallThickness = v, 0.5f, 10f,
                description: "Wall thickness. Real retaining walls are ~3m.");
            TuningRegistry.RegisterBool("wall.flipSide", "Retaining wall", "Flip front/back side",
                () => t.RetainingWallLayer.FlipSide, v => t.RetainingWallLayer.FlipSide = v,
                description: "Swap which side of the drawn line is the exposed FRONT face vs the filled BACK. Use if the wall builds facing the wrong way for your draw direction.");
        }

        // Persisted-settings changes made through the in-game Environment palette write the statics directly
        // (they don't flow through TrySet → OnValueChanged), so poll them here and mark the file dirty when they
        // move. Cheap: a hash of the values, compared frame-to-frame. Primed on the first frame (post-load) so
        // loading doesn't immediately re-save.
        bool _envSigInit;
        int _lastEnvSig;
        void PollEnvironmentForChanges()
        {
            int sig = EnvSignature();
            if (_envSigInit && sig != _lastEnvSig) _dirtySinceRealtime = Time.realtimeSinceStartup;
            _lastEnvSig = sig;
            _envSigInit = true;
        }

        static int EnvSignature()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + DayCycle.Enabled.GetHashCode();
                h = h * 31 + DayCycle.ManageSky.GetHashCode();
                // Skip the running clock while auto-advancing, or it would re-save every debounce tick forever.
                // A manual Time drag (auto-advance off) still registers and persists.
                if (!DayCycle.AutoAdvance) h = h * 31 + DayCycle.TimeOfDay.GetHashCode();
                h = h * 31 + DayCycle.PeakElevation.GetHashCode();
                h = h * 31 + DayCycle.NorthYaw.GetHashCode();
                h = h * 31 + DayCycle.Intensity.GetHashCode();
                h = h * 31 + DayCycle.AmbientScale.GetHashCode();
                h = h * 31 + DayCycle.AutoAdvance.GetHashCode();
                h = h * 31 + DayCycle.DayLengthMinutes.GetHashCode();
                h = h * 31 + DemLighting.Enabled.GetHashCode();
                h = h * 31 + DemLighting.UseACES.GetHashCode();
                h = h * 31 + DemLighting.FogDensity.GetHashCode();
                h = h * 31 + DemLighting.Exposure.GetHashCode();
                h = h * 31 + DemLighting.Contrast.GetHashCode();
                h = h * 31 + DemLighting.Saturation.GetHashCode();
                h = h * 31 + DemLighting.Warmth.GetHashCode();
                h = h * 31 + DemLighting.BloomIntensity.GetHashCode();
                h = h * 31 + DemLighting.VignetteAmount.GetHashCode();
                h = h * 31 + ChunkWorld.GroundTextures.GetHashCode();
                h = h * 31 + ChunkWorld.GroundGrassColor.GetHashCode();
                h = h * 31 + ChunkWorld.GroundSlopeColor.GetHashCode();
                h = h * 31 + ChunkWorld.GroundRockColor.GetHashCode();
                h = h * 31 + ChunkWorld.GroundStochastic.GetHashCode();
                h = h * 31 + ChunkWorld.GroundBrightness.GetHashCode();
                h = h * 31 + ChunkWorld.GroundMacroAmount.GetHashCode();
                h = h * 31 + ChunkWorld.GroundMacroSize.GetHashCode();
                h = h * 31 + ChunkWorld.GroundTexSize.GetHashCode();
                h = h * 31 + ChunkWorld.GroundDetailStrength.GetHashCode();
                h = h * 31 + ChunkWorld.GroundDetailSize.GetHashCode();
                h = h * 31 + ChunkWorld.GroundNoiseSize.GetHashCode();
                h = h * 31 + ChunkWorld.GroundDirtPatches.GetHashCode();
                h = h * 31 + ChunkWorld.GroundDirtSlope.GetHashCode();
                h = h * 31 + ChunkWorld.GroundGravelSlope.GetHashCode();
                h = h * 31 + ChunkWorld.GroundSandHeight.GetHashCode();
                h = h * 31 + ChunkWorld.GroundSlopeJitter.GetHashCode();
                h = h * 31 + ChunkWorld.GroundNormalStrength.GetHashCode();
                h = h * 31 + ForestGen.NearLodTint.GetHashCode();
                h = h * 31 + ForestGen.FarLodDarken.GetHashCode();
                h = h * 31 + ForestGen.NightFoliageTint.GetHashCode();
                var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                          as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                h = h * 31 + (urp != null ? urp.shadowDistance : -1f).GetHashCode();
                return h;
            }
        }
    }
}
