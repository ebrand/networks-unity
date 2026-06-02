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

        float _dirtySinceRealtime = -1f;

        void OnEnable()
        {
            // Keep Unity ticking when focus is on the React panel (see TuningSetup).
            Application.runInBackground = true;
            if (Terrain == null) Terrain = FindFirstObjectByType<TerrainDesigner>();
            if (Terrain == null) return;
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
                () => t.TreePaintRate, v => t.TreePaintRate = v, 1f, 200f);
            TuningRegistry.RegisterString("terrain.treeFolder", "Trees", "Tree folder",
                () => t.TreeFolder, v => t.TreeFolder = v);
#if UNITY_EDITOR
            TuningRegistry.RegisterAction("terrain.loadTrees", "Trees", "Load trees from folder",
                () => t.LoadTreesFromFolder());
#endif

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
        }
    }
}
