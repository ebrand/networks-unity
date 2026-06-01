// Registers TerrainDesigner's tunables into the static TuningRegistry so the
// React TuningPanel can adjust them live over the TuningServer WebSocket.
// Mirrors NetworkDesigner.Tuning.TuningSetup. RequireComponent pulls in a
// TuningServer (which self-starts on OnEnable), so adding this one component
// stands up the whole tuning endpoint for the terrain scene.
//
// TerrainDesigner can auto-create this at runtime (AutoTuning), matching how
// it ensures the camera/lighting — so an empty terrain scene serves tunables
// with no manual setup.

using UnityEngine;
using NetworkDesigner.Designer; // GroundGrid
using NetworkDesigner.Tuning;

namespace NetworkDesigner.Terrain
{
    [RequireComponent(typeof(TuningServer))]
    public class TerrainTuningSetup : MonoBehaviour
    {
        public TerrainDesigner Terrain;
        public GroundGrid Grid;

        void OnEnable()
        {
            // Keep Unity ticking when focus is on the React panel (see TuningSetup).
            Application.runInBackground = true;
            if (Terrain == null) Terrain = FindFirstObjectByType<TerrainDesigner>();
            if (Grid == null) Grid = FindFirstObjectByType<GroundGrid>();
            if (Terrain == null) return;
            TuningRegistry.Clear();
            RegisterAll();
        }

        void RegisterAll()
        {
            TerrainDesigner t = Terrain;

            // --- Brush ---
            TuningRegistry.RegisterFloat("terrain.brushRadius", "Brush", "Radius (m)",
                () => t.BrushRadius, v => t.BrushRadius = v, 0.5f, 200f);
            TuningRegistry.RegisterFloat("terrain.brushStrength", "Brush", "Strength (m/s)",
                () => t.BrushStrength, v => t.BrushStrength = v, 0f, 100f);
            TuningRegistry.RegisterFloat("terrain.brushFalloff", "Brush", "Falloff",
                () => t.BrushFalloff, v => t.BrushFalloff = v, 0f, 1f);
            TuningRegistry.RegisterFloat("terrain.brushResizeRate", "Brush", "Resize rate (m/s)",
                () => t.BrushResizeRate, v => t.BrushResizeRate = v, 1f, 100f);

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
            // TerrainColor maps through Material.color (BaseColor) on rebuild.
            TuningRegistry.RegisterColor("terrain.color", "Appearance", "Terrain color",
                () => t.TerrainColor, v => { t.TerrainColor = v; t.RebuildMesh(); });

            // --- Ground grid --- (mirrors NetworkDesigner.Tuning.TuningSetup)
            if (Grid != null)
            {
                TuningRegistry.RegisterBool("grid.enabled", "Ground grid", "Visible",
                    () => Grid.Enabled, v => { Grid.Enabled = v; Grid.Rebuild(); });
                TuningRegistry.RegisterBool("grid.showMinor", "Ground grid", "Show minor lines",
                    () => Grid.ShowMinor, v => { Grid.ShowMinor = v; Grid.Rebuild(); });
                TuningRegistry.RegisterBool("grid.showMajor", "Ground grid", "Show major lines",
                    () => Grid.ShowMajor, v => { Grid.ShowMajor = v; Grid.Rebuild(); });
                TuningRegistry.RegisterFloat("grid.spacing", "Ground grid", "Spacing (m)",
                    () => Grid.Spacing, v => { Grid.Spacing = v; Grid.Rebuild(); }, 1f, 200f);
                TuningRegistry.RegisterFloat("grid.extent", "Ground grid", "Half-extent (m, grid spans 2x)",
                    () => Grid.Extent, v => { Grid.Extent = v; Grid.Rebuild(); }, 50f, 2000f);
                TuningRegistry.RegisterFloat("grid.majorEvery", "Ground grid", "Major line every Nth",
                    () => Grid.MajorEvery,
                    v => { Grid.MajorEvery = Mathf.Max(1, Mathf.RoundToInt(v)); Grid.Rebuild(); },
                    1f, 100f, 1f);
                TuningRegistry.RegisterColor("grid.minorColor", "Ground grid", "Minor line color",
                    () => Grid.MinorColor, v => { Grid.MinorColor = v; Grid.Rebuild(); });
                TuningRegistry.RegisterColor("grid.majorColor", "Ground grid", "Major line color",
                    () => Grid.MajorColor, v => { Grid.MajorColor = v; Grid.Rebuild(); });
            }
        }
    }
}
