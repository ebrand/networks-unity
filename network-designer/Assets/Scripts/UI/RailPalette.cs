// In-game rail tool palette (UI Toolkit). Shows in rail Build/Plan mode; consolidates
// the operational rail/plan controls (mode, speed, grade, toggles, plan actions,
// parallel). All shared plumbing — panel lifecycle, common footer, widgets, the
// PointerOverUI gate — lives in PaletteBase; this just fills the body.

using UnityEngine;
using UnityEngine.UIElements;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class RailPalette : PaletteBase
    {
        public override string PaletteId => "Rail";
        public override string MenuLabel => "Rail (L/K)";
        protected override Color Accent => Amber;

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

            body.Add(NumberRow("Dgn Speed", "km/h",
                () => Designer.RailLayer.SpeedLimitKmh, v => Designer.RailLayer.SpeedLimitKmh = v, 10f, 200f, "0"));
            body.Add(NumberRow("Max Grade", "deg",
                () => Designer.RailLayer.MaxGradeDeg, v => Designer.RailLayer.MaxGradeDeg = v, 0.5f, 15f, "0.#"));

            body.Add(ToggleRow("Force Grade",
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

            // Plan/track actions.
            var actRow = HBox();
            var carve = MakeButton("Carve Appr.", () => Designer.CarveRailApproaches());
            var bop   = MakeButton("Build on Plan", () => Designer.PromotePlanToRail());
            carve.style.marginRight = 6;
            actRow.Add(carve); actRow.Add(bop);
            actRow.style.marginTop = 4;
            body.Add(actRow);
            var clear = MakeButton("Clear Plan", () => Designer.ClearPlan());
            clear.style.marginTop = 6;
            body.Add(clear);

            body.Add(Divider());

            body.Add(ToggleRow("Parallel (Z)",
                () => Designer.RailLayer.ParallelEnabled, v => Designer.RailLayer.ParallelEnabled = v));
            body.Add(NumberRow("Spacing", "m",
                () => Designer.RailLayer.ParallelSpacing, v => Designer.RailLayer.ParallelSpacing = v, 5f, 100f, "0.#"));
            body.Add(NumberRow("Parallels", "",
                () => Designer.RailLayer.ParallelCount,
                v => Designer.RailLayer.ParallelCount = Mathf.Max(1, Mathf.RoundToInt(v)), 1f, 8f, "0"));
        }
    }
}
