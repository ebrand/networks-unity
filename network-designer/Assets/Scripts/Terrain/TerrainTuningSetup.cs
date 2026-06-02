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

        [Tooltip("Orbit camera to expose as tunables. Auto-found on Start.")]
        public OrbitCameraController Orbit;

        float _dirtySinceRealtime = -1f;
        // Previous-frame orbit snapshot — so mouse-driven camera moves (which
        // don't flow through TrySet) still mark the tuning file dirty.
        Vector3 _prevOrbitTarget;
        float _prevOrbitYaw, _prevOrbitPitch, _prevOrbitDistance;
        bool _prevOrbitInit;

        void OnEnable()
        {
            // Keep Unity ticking when focus is on the React panel (see TuningSetup).
            Application.runInBackground = true;
            if (Terrain == null) Terrain = FindFirstObjectByType<TerrainDesigner>();
            if (Terrain == null) return;
            if (Orbit == null) Orbit = FindFirstObjectByType<OrbitCameraController>();
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
            // Mouse-driven camera moves don't go through TrySet, so poll for
            // drift and mark dirty (mirrors the road TuningSetup).
            PollOrbitCameraForChanges();

            if (!PersistChanges) return;
            if (_dirtySinceRealtime < 0f) return;
            if (Time.realtimeSinceStartup - _dirtySinceRealtime < PersistDebounceSeconds) return;
            TuningRegistry.SaveToFile(ResolvePersistencePath());
            _dirtySinceRealtime = -1f;
        }

        void PollOrbitCameraForChanges()
        {
            if (Orbit == null) return;
            if (!_prevOrbitInit)
            {
                _prevOrbitTarget = Orbit.Target;
                _prevOrbitYaw = Orbit.Yaw;
                _prevOrbitPitch = Orbit.Pitch;
                _prevOrbitDistance = Orbit.DistanceTarget;
                _prevOrbitInit = true;
                return;
            }
            const float EPS = 0.02f;
            if ((Orbit.Target - _prevOrbitTarget).sqrMagnitude < EPS * EPS
                && Mathf.Abs(Orbit.Yaw - _prevOrbitYaw) < EPS
                && Mathf.Abs(Orbit.Pitch - _prevOrbitPitch) < EPS
                && Mathf.Abs(Orbit.DistanceTarget - _prevOrbitDistance) < EPS) return;
            _prevOrbitTarget = Orbit.Target;
            _prevOrbitYaw = Orbit.Yaw;
            _prevOrbitPitch = Orbit.Pitch;
            _prevOrbitDistance = Orbit.DistanceTarget;
            if (_dirtySinceRealtime < 0f) _dirtySinceRealtime = Time.realtimeSinceStartup;
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
            TuningRegistry.RegisterColor("terrain.color", "Appearance", "Terrain color",
                () => t.TerrainColor, v => { t.TerrainColor = v; t.ApplyTerrainColor(); });

            // --- Camera (orbit) --- mirrors the road designer's Camera group.
            if (Orbit != null)
            {
                // Bind to DistanceTarget (steady-state zoom), not the animating
                // Distance, so smoothing isn't read back as a change.
                TuningRegistry.RegisterFloat("orbit.distance", "Camera", "Distance",
                    () => Orbit.DistanceTarget, v => Orbit.DistanceTarget = Mathf.Min(v, Orbit.MaxDistance), 1f, 5000f);
                TuningRegistry.RegisterFloat("orbit.maxDistance", "Camera", "Max distance",
                    () => Orbit.MaxDistance, v => Orbit.MaxDistance = v, 100f, 10000f);
                TuningRegistry.RegisterFloat("orbit.minDistance", "Camera", "Min distance",
                    () => Orbit.MinDistance, v => Orbit.MinDistance = Mathf.Max(0.05f, v), 0.05f, 500f);
                TuningRegistry.RegisterFloat("orbit.farClipPlane", "Camera", "Far clip plane (m)",
                    () => Orbit.FarClipPlane, v => Orbit.FarClipPlane = v, 100f, 50000f);
                TuningRegistry.RegisterFloat("orbit.pitch", "Camera", "Pitch (deg)",
                    () => Orbit.Pitch, v => Orbit.Pitch = v, -89f, 89f);
                TuningRegistry.RegisterFloat("orbit.yaw", "Camera", "Yaw (deg)",
                    () => Orbit.Yaw, v => Orbit.Yaw = v, -360f, 360f);
                TuningRegistry.RegisterVector3("orbit.target", "Camera", "Pivot target (world XYZ)",
                    () => Orbit.Target, v => Orbit.Target = v, -10000f, 10000f);
                TuningRegistry.RegisterFloat("orbit.keyboardPanSensitivity", "Camera", "WASD pan speed (× distance/s)",
                    () => Orbit.KeyboardPanSensitivity, v => Orbit.KeyboardPanSensitivity = v, 0f, 10f);
                TuningRegistry.RegisterFloat("orbit.keyboardPanShiftMultiplier", "Camera", "WASD shift-boost multiplier",
                    () => Orbit.KeyboardPanShiftMultiplier, v => Orbit.KeyboardPanShiftMultiplier = v, 1f, 10f);
                TuningRegistry.RegisterFloat("orbit.minPanReference", "Camera", "Min pan reference distance (m)",
                    () => Orbit.MinPanReferenceDistance, v => Orbit.MinPanReferenceDistance = v, 0f, 500f);
                TuningRegistry.RegisterFloat("orbit.zoomSensitivity", "Camera", "Zoom per wheel notch (× distance)",
                    () => Orbit.ZoomSensitivity, v => Orbit.ZoomSensitivity = v, 0f, 30f);
                TuningRegistry.RegisterFloat("orbit.zoomSmoothing", "Camera", "Zoom smoothing (1/s)",
                    () => Orbit.ZoomSmoothing, v => Orbit.ZoomSmoothing = v, 0f, 40f);
                TuningRegistry.RegisterFloat("orbit.minZoomReference", "Camera", "Min zoom reference distance (m)",
                    () => Orbit.MinZoomReferenceDistance, v => Orbit.MinZoomReferenceDistance = v, 0f, 500f);
            }
        }
    }
}
