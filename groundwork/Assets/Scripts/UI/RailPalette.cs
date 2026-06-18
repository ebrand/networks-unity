// In-game rail tool palette (UI Toolkit). Shows in rail Build/Plan mode; consolidates
// the operational rail/plan controls (mode, speed, grade, toggles, plan actions,
// parallel). All shared plumbing — panel lifecycle, common footer, widgets, the
// PointerOverUI gate — lives in PaletteBase; this just fills the body.

using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class RailPalette : PaletteBase
    {
        public override string PaletteId => "Rail";
        public override string MenuLabel => "L/K";
        protected override string Title => "Rail";
        protected override Color Accent => Amber;

        // Opening Rail from the launcher defaults into Build (L) mode.
        protected override void OnOpened() => Designer.SetRailMode(false);

        protected override void BuildBody(VisualElement body)
        {
            body.Add(SectionLabel("MODES"));

            // Build / Plan segmented buttons.
            var modeRow = HBox();
            var buildBtn = MakeButton("Build (L)", () => Designer.SetRailMode(false));
            var planBtn  = MakeButton("Plan (K)",  () => Designer.SetRailMode(true));
            buildBtn.style.marginRight = 6;
            modeRow.Add(buildBtn); modeRow.Add(planBtn);
            modeRow.style.marginBottom = 12;
            body.Add(modeRow);
            _sync.Add(() =>
            {
                StyleActive(buildBtn, Designer.IsRailBuildMode);
                StyleActive(planBtn,  Designer.IsRailPlanMode);
            });

            body.Add(ToggleRow("Force Grade (B)",
                () => Designer.RailLayer.OverrideGrade, v => Designer.RailLayer.OverrideGrade = v));
            body.Add(ToggleRow("Show Nodes",
                () => Designer.RailLayer.ShowNodePucks, v => Designer.RailLayer.ShowNodePucks = v));
            body.Add(ToggleRow("Show Decel",
                () => Designer.RailLayer.ShowBrakingMarkers, v => Designer.RailLayer.ShowBrakingMarkers = v));
            body.Add(ToggleRow("Inspect",
                () => Designer.RailLayer.ShowCurveInspect, v => Designer.RailLayer.ShowCurveInspect = v));

            body.Add(NumberRow("Corridor Width", "m",
                () => Designer.PlanLayer.CorridorWidth,
                v => { Designer.PlanLayer.CorridorWidth = v; Designer.RebuildPlan(); }, 2f, 200f, "0"));

            // Grade-aware A* auto-route between the last two plan points (place A, place B, then this).
            var autoBtn = MakeButton("Auto-route A→B", () => Designer.AutoRoutePlan());
            autoBtn.style.marginTop = 4;
            body.Add(autoBtn);
            // How hard the auto-route works to avoid climbing (hug the draw) vs. go straight.
            body.Add(SliderRow("Route climb avoid", () => RailAutoRoute.ClimbWeight,
                v => RailAutoRoute.ClimbWeight = v, 5f, 80f, "0"));

            // Plan/track actions.
            var bop = MakeButton("Build on Plan", () => Designer.PromotePlanToRail());
            bop.style.marginTop = 4;
            body.Add(bop);
            // Cut/fill grading: carve+fill a roadbed under the rail to its routed grade (chunk world or DEM).
            var grade = MakeButton("Grade Corridor (cut/fill)", () => Designer.GradeRailCorridor());
            grade.style.marginTop = 4;
            body.Add(grade);
            var clearRow = HBox();
            var clearPlan = MakeButton("Clear Plan", () => Designer.ClearPlan());
            var clearRail = MakeButton("Clear Rail", () => Designer.ClearRail());
            clearPlan.style.marginRight = 6;
            clearRow.Add(clearPlan); clearRow.Add(clearRail);
            clearRow.style.marginTop = 6;
            body.Add(clearRow);

            body.Add(Divider());

            body.Add(ToggleRow("Parallel (Z)",
                () => Designer.RailLayer.ParallelEnabled, v => Designer.RailLayer.ParallelEnabled = v));
            body.Add(NumberRow("Spacing", "m",
                () => Designer.RailLayer.ParallelSpacing, v => Designer.RailLayer.ParallelSpacing = v, 5f, 100f, "0.#"));
            body.Add(NumberRow("Parallels (opt)", "",
                () => Designer.RailLayer.ParallelCount,
                v => Designer.RailLayer.ParallelCount = Mathf.Max(1, Mathf.RoundToInt(v)), 1f, 8f, "0"));
        }
    }
}
