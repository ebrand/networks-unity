// In-game terrain operations palette (UI Toolkit). Shows in the default sculpt mode.
// Sections: brush, grid, water. Terrain generation, heightmap, and autosave live in the
// separate SystemPalette; the Env Colors section (hex-field swatches) is
// increment 2. Shared plumbing lives in PaletteBase; this applies the same side-effects
// the live-tuning setters do (ApplyTerrainMaterial for grid, ApplyWater for water).

using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class TerrainPalette : PaletteBase
    {
        public override string PaletteId => "Terrain";
        public override string MenuLabel => "Terrain (N)";
        protected override bool DefaultOpen => true;
        protected override string Title => "Terrain Operations";
        protected override Color Accent => new Color(0.52f, 0.76f, 0.46f);   // terrain green
        protected override float PanelWidth => 320f;

        // Opening Terrain returns the cursor to the sculpt brush (exits rail/scatter).
        protected override void OnOpened() => Designer.EnterSculptMode();

        protected override void BuildBody(VisualElement body)
        {
            // ---- BRUSH ----
            body.Add(SectionLabel("BRUSH"));
            AddBrushButton(body, "Raise (1)",   TerrainDesigner.BrushMode.Raise);
            AddBrushButton(body, "Lower (2)",   TerrainDesigner.BrushMode.Lower);
            AddBrushButton(body, "Smooth (3)",  TerrainDesigner.BrushMode.Smooth);
            AddBrushButton(body, "Flatten (4)", TerrainDesigner.BrushMode.Flatten);
            AddBrushButton(body, "Slope (5)",   TerrainDesigner.BrushMode.Slope);

            body.Add(SliderRow("Radius", () => Designer.BrushRadius, v => Designer.BrushRadius = v,
                0.5f, Mathf.Max(1f, Designer.MaxBrushRadius), "0"));
            body.Add(SliderRow("Strength", () => Designer.BrushStrength, v => Designer.BrushStrength = v, 0f, 100f, "0"));
            body.Add(SliderRow("Falloff", () => Designer.BrushFalloff, v => Designer.BrushFalloff = v, 0f, 1f, "0.00"));

            // ---- GRID ---- (changes need ApplyTerrainMaterial to hit the shader)
            body.Add(Divider());
            body.Add(SectionLabel("GRID"));
            body.Add(SliderRow("Strength", () => Designer.GridStrength,
                v => { Designer.GridStrength = v; Designer.ApplyTerrainMaterial(); }, 0f, 1f, "0.00"));
            body.Add(SliderRow("Line", () => Designer.GridLineWidth,
                v => { Designer.GridLineWidth = v; Designer.ApplyTerrainMaterial(); }, 0.5f, 4f, "0.0"));
            body.Add(NumberRow("Spacing", "m", () => Designer.GridSpacing,
                v => { Designer.GridSpacing = v; Designer.ApplyTerrainMaterial(); }, 1f, 100f, "0.#"));
            body.Add(NumberRow("Major", "", () => Designer.GridMajorEvery,
                v => { Designer.GridMajorEvery = v; Designer.ApplyTerrainMaterial(); }, 1f, 100f, "0"));

            // ---- WATER ---- (changes need ApplyWater)
            body.Add(Divider());
            body.Add(SectionLabel("WATER"));
            body.Add(ToggleRow("Show Water",
                () => Designer.ShowWater, v => { Designer.ShowWater = v; Designer.ApplyWater(); }));
            body.Add(SliderRow("Alpha", () => Designer.WaterColor.a,
                v => { Color c = Designer.WaterColor; c.a = v; Designer.WaterColor = c; Designer.ApplyWater(); }, 0.05f, 1f, "0.00"));
            body.Add(SliderRow("Smooth", () => Designer.WaterSmoothness,
                v => { Designer.WaterSmoothness = v; Designer.ApplyWater(); }, 0f, 1f, "0.00"));

        }

        void AddBrushButton(VisualElement body, string label, TerrainDesigner.BrushMode mode)
        {
            // Picking a brush always lands you in sculpt mode (exits any rail/scatter).
            var b = MakeButton(label, () => { Designer.EnterSculptMode(); Designer.Brush = mode; });
            b.style.marginBottom = 6;
            body.Add(b);
            _sync.Add(() => StyleActive(b, Designer.Brush == mode));
        }
    }
}
