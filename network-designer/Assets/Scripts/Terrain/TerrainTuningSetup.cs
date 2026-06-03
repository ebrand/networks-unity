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

            // --- Brush ---
            TuningRegistry.RegisterFloat("terrain.brushRadius", "Brush", "Radius (m)",
                () => t.BrushRadius, v => t.BrushRadius = v, 0.5f, 200f);
            TuningRegistry.RegisterFloat("terrain.brushStrength", "Brush", "Strength (m/s)",
                () => t.BrushStrength, v => t.BrushStrength = v, 0f, 300f);
            TuningRegistry.RegisterFloat("terrain.brushStrengthExp", "Brush", "Strength exponent",
                () => t.BrushStrengthExponent, v => t.BrushStrengthExponent = v, 1f, 4f);
            TuningRegistry.RegisterFloat("terrain.brushFalloff", "Brush", "Falloff",
                () => t.BrushFalloff, v => t.BrushFalloff = v, 0f, 1f);
            TuningRegistry.RegisterFloat("terrain.brushResizeRate", "Brush", "Resize rate (m/s)",
                () => t.BrushResizeRate, v => t.BrushResizeRate = v, 1f, 100f);

            // --- Heightmap import ---
            TuningRegistry.RegisterString("terrain.heightmapPath", "Heightmap", "File",
                () => t.HeightmapPath, v => t.HeightmapPath = v);
            TuningRegistry.RegisterFloat("terrain.heightmapMax", "Heightmap", "Max height (m)",
                () => t.HeightmapMaxHeight, v => t.HeightmapMaxHeight = v, 1f, 2000f);
            TuningRegistry.RegisterFloat("terrain.heightmapSmooth", "Heightmap", "Smooth passes",
                () => t.HeightmapSmoothPasses, v => t.HeightmapSmoothPasses = Mathf.RoundToInt(v), 0f, 12f, 1f);
            TuningRegistry.RegisterAction("terrain.importHeightmap", "Heightmap", "Load heightmap",
                () => t.ImportHeightmap());

            // --- Trees ---
            TuningRegistry.RegisterFloat("terrain.treePaintRate", "Trees", "Strength (trees/s)",
                () => t.TreeLayer.PaintRate, v => t.TreeLayer.PaintRate = v, 1f, 200f);
            TuningRegistry.RegisterFloat("terrain.treeSpacing", "Trees", "Spacing (m)",
                () => t.TreeLayer.Spacing, v => t.TreeLayer.Spacing = v, 1f, 30f);
            TuningRegistry.RegisterFloat("terrain.treeMaxSlope", "Trees", "Max slope (deg, 90 = off)",
                () => t.TreeLayer.MaxSlopeDeg, v => t.TreeLayer.MaxSlopeDeg = v, 0f, 90f);
            TuningRegistry.RegisterString("terrain.treeFolder", "Trees", "Folder",
                () => t.TreeLayer.Folder, v => t.TreeLayer.Folder = v);
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
                () => t.RockLayer.MaxSlopeDeg, v => t.RockLayer.MaxSlopeDeg = v, 0f, 90f);
            TuningRegistry.RegisterString("terrain.rockFolder", "Rocks", "Folder",
                () => t.RockLayer.Folder, v => t.RockLayer.Folder = v);
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
                () => t.RailLayer.Gauge, v => { t.RailLayer.Gauge = v; t.RebuildRail(); }, 0.5f, 5f);
            TuningRegistry.RegisterFloat("rail.tieSpacing", "Rail", "Tie spacing (m)",
                () => t.RailLayer.TieSpacing, v => { t.RailLayer.TieSpacing = v; t.RebuildRail(); }, 0.2f, 5f);
            TuningRegistry.RegisterFloat("rail.tieLength", "Rail", "Tie length (m)",
                () => t.RailLayer.TieLength, v => { t.RailLayer.TieLength = v; t.RebuildRail(); }, 0.5f, 6f);
            TuningRegistry.RegisterFloat("rail.railHeight", "Rail", "Rail height (m)",
                () => t.RailLayer.RailHeight, v => { t.RailLayer.RailHeight = v; t.RebuildRail(); }, 0.02f, 1f);
            TuningRegistry.RegisterFloat("rail.verticalOffset", "Rail", "Vertical offset (m)",
                () => t.RailLayer.VerticalOffset, v => { t.RailLayer.VerticalOffset = v; t.RebuildRail(); }, -2f, 5f);
            TuningRegistry.RegisterBool("rail.conform", "Rail", "Conform to terrain",
                () => t.RailLayer.Conform, v => { t.RailLayer.Conform = v; t.RebuildRail(); });
            TuningRegistry.RegisterBool("rail.straight", "Rail", "Straight tool (vs curve tool)",
                () => t.RailLayer.Straight, v => { t.RailLayer.Straight = v; t.RebuildRail(); });
            TuningRegistry.RegisterFloat("rail.curveLever", "Rail", "Curve lever (arc width)",
                () => t.RailLayer.CurveLever, v => { t.RailLayer.CurveLever = v; t.RebuildRail(); }, 0.1f, 0.95f);
            TuningRegistry.RegisterColor("rail.railColor", "Rail", "Rail color",
                () => t.RailLayer.RailColor, v => { t.RailLayer.RailColor = v; t.RebuildRail(); });
            TuningRegistry.RegisterColor("rail.tieColor", "Rail", "Tie color",
                () => t.RailLayer.TieColor, v => { t.RailLayer.TieColor = v; t.RebuildRail(); });
            TuningRegistry.RegisterAction("rail.clear", "Rail", "Clear rail",
                () => t.ClearRail());

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
                () => t.TerrainColor, v => { t.TerrainColor = v; t.ApplyTerrainMaterial(); });
            TuningRegistry.RegisterColor("terrain.rockColor", "Appearance", "Rock color (steep)",
                () => t.RockColor, v => { t.RockColor = v; t.ApplyTerrainMaterial(); });
            TuningRegistry.RegisterFloat("terrain.slopeStart", "Appearance", "Rock slope start (deg)",
                () => t.SlopeStartDeg, v => { t.SlopeStartDeg = v; t.ApplyTerrainMaterial(); }, 0f, 90f);
            TuningRegistry.RegisterFloat("terrain.slopeFull", "Appearance", "Rock slope full (deg)",
                () => t.SlopeFullDeg, v => { t.SlopeFullDeg = v; t.ApplyTerrainMaterial(); }, 0f, 90f);
            TuningRegistry.RegisterFloat("terrain.rockTexScale", "Appearance", "Rock texture scale",
                () => t.RockTextureScale, v => { t.RockTextureScale = v; t.ApplyTerrainMaterial(); }, 0.01f, 1f);

            // --- World grid ---
            TuningRegistry.RegisterBool("terrain.gridOn", "Grid", "Show grid (G)",
                () => t.GridEnabled, v => { t.GridEnabled = v; t.ApplyTerrainMaterial(); });
            TuningRegistry.RegisterFloat("terrain.gridSpacing", "Grid", "Spacing (m)",
                () => t.GridSpacing, v => { t.GridSpacing = v; t.ApplyTerrainMaterial(); }, 1f, 50f);
            TuningRegistry.RegisterFloat("terrain.gridMajor", "Grid", "Major every N",
                () => t.GridMajorEvery, v => { t.GridMajorEvery = v; t.ApplyTerrainMaterial(); }, 1f, 20f);
            TuningRegistry.RegisterFloat("terrain.gridStrength", "Grid", "Strength",
                () => t.GridStrength, v => { t.GridStrength = v; t.ApplyTerrainMaterial(); }, 0f, 1f);
            TuningRegistry.RegisterFloat("terrain.gridWidth", "Grid", "Line width (px)",
                () => t.GridLineWidth, v => { t.GridLineWidth = v; t.ApplyTerrainMaterial(); }, 0.5f, 4f);
            TuningRegistry.RegisterColor("terrain.gridColor", "Grid", "Color",
                () => t.GridColor, v => { t.GridColor = v; t.ApplyTerrainMaterial(); });

            // --- Interface ---
            TuningRegistry.RegisterFloat("terrain.uiScale", "Interface", "UI scale (in-game panels)",
                () => TerrainDesigner.UiScale, v => TerrainDesigner.UiScale = v, 0.75f, 3f);

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
