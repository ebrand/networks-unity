// In-game road tool palette (UI Toolkit). Plan drawing today; the cross-section designer ("…" → Road
// Designer) + the excavate / build-on-plan sweep pipeline as they land (Build / Excavate are scaffolded
// until that pipeline exists). Self-spawns at runtime; all shared plumbing lives in PaletteBase.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;
using NetworkDesigner.Roads;
using NetworkDesigner.Import;

namespace NetworkDesigner.UI
{
    public class RoadPalette : PaletteBase
    {
        public override string PaletteId => "Road";
        public override string MenuLabel => ";";
        protected override string Title => "Road";
        protected override Color Accent => new Color(0.95f, 0.55f, 0.15f);   // road-plan amber
        protected override float PanelWidth => 300f;
        protected override string FooterMode => "Road";
        protected override string FooterSub => string.Empty;

        protected override void OnOpened() => Designer.EnterRoadPlanMode();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindFirstObjectByType<RoadPalette>() != null) return;
            var go = new GameObject("RoadPalette (auto)");
            go.AddComponent<UIDocument>();
            go.AddComponent<RoadPalette>();
        }

        const string Custom = "Custom (width)";

        protected override void BuildBody(VisualElement body)
        {
            // ---- MODES ----
            body.Add(SectionLabel("MODES"));
            var modes = HBox(); modes.style.marginBottom = 12;
            var planBtn = MakeButton("Plan (;)", () =>
            {
                if (Designer.IsRoadPlanMode) Designer.EnterSculptMode(); else Designer.EnterRoadPlanMode();
            });
            planBtn.style.flexGrow = 1; planBtn.style.marginRight = 6;
            var buildBtn = MakeButton("Build", () => { });   // 3D sweep pipeline — not built yet
            buildBtn.style.flexGrow = 1; buildBtn.SetEnabled(false);
            buildBtn.tooltip = "Excavate + 3D road sweep — coming next";
            modes.Add(planBtn); modes.Add(buildBtn);
            body.Add(modes);
            _sync.Add(() => StyleActive(planBtn, Designer.IsRoadPlanMode));

            // ---- profile type + designer ----
            body.Add(Cap("Road profile type:"));
            var ptRow = HBox(); ptRow.style.marginBottom = 8;
            var names = new List<string> { Custom };
            foreach (var c in RoadProfileLibrary.Configs)
                if (c != null && !string.IsNullOrEmpty(c.Name)) names.Add(c.Name);
            string cur = string.IsNullOrEmpty(Designer.RoadPlanLayer.ActiveProfileId) ? Custom : Designer.RoadPlanLayer.ActiveProfileId;
            if (!names.Contains(cur)) cur = Custom;
            var profDd = new DropdownField { choices = names, value = cur };
            profDd.style.flexGrow = 1;
            profDd.RegisterValueChangedCallback(e => { Designer.RoadPlanLayer.ActiveProfileId = e.newValue == Custom ? "" : e.newValue; Rebuild(); });
            var dots = MakeButton("…", () => PaletteBase.ToggleQuick("RoadDesigner"));
            dots.style.width = 42; dots.style.flexGrow = 0; dots.style.marginLeft = 6;
            dots.tooltip = "Open the Road Designer";
            ptRow.Add(profDd); ptRow.Add(dots);
            body.Add(ptRow);

            // ---- visual profile picker (thumbnails) ----
            body.Add(BuildThumbGrid());

            // Custom-width fallback (used when the Custom profile is active).
            body.Add(NumberRow("Custom width", "m",
                () => Designer.RoadPlanLayer.RoadWidth,
                v => { Designer.RoadPlanLayer.RoadWidth = v; Designer.RebuildRoadPlan(); }, 3f, 60f, "0"));

            // ---- actions ----
            var acts = HBox(); acts.style.marginTop = 8; acts.style.marginBottom = 10;
            var exc = MakeButton("Excavate", () => Designer.ExcavateRoadCorridor()); exc.style.flexGrow = 1; exc.style.marginRight = 6;
            exc.tooltip = "Smooth + cut the roadbed into the terrain along the plan";
            var bop = MakeButton("Build Plan", () => Designer.BuildRoadPlan()); bop.style.flexGrow = 1; bop.style.marginRight = 6;
            bop.tooltip = "Sweep the resolved 3D roads (setback-trimmed) into the excavated bed";
            var clr = MakeButton("Clear Plan", () => Designer.ClearRoadPlan()); clr.style.flexGrow = 1;
            acts.Add(exc); acts.Add(bop); acts.Add(clr);
            body.Add(acts);

            // ---- excavation tuning ----
            body.Add(NumberRow("Excavate depth", "m",
                () => Designer.RoadPlanLayer.ExcavationDepth,
                v => Designer.RoadPlanLayer.ExcavationDepth = v, 0f, 5f, "0.0"));

            // ---- toggles ----
            body.Add(ToggleRow("Show crosswalks", () => Designer.RoadPlanLayer.ShowCrosswalks,
                v => { Designer.RoadPlanLayer.ShowCrosswalks = v; Designer.RebuildRoadPlan(); }));
            body.Add(ToggleRow("Show stop lines", () => Designer.RoadPlanLayer.ShowStopBars,
                v => { Designer.RoadPlanLayer.ShowStopBars = v; Designer.RebuildRoadPlan(); }));
            body.Add(ToggleRow("Guided turns (lock)", () => Designer.RoadPlanLayer.GuidedTurns,
                v => Designer.RoadPlanLayer.GuidedTurns = v));
        }

        // A scrollable grid of profile thumbnails (mini cross-sections); click to make it the active profile.
        VisualElement BuildThumbGrid()
        {
            var sv = ScrollBox(190);
            sv.style.marginBottom = 8;
            SetBorder(sv, 1, new Color(0.3f, 0.32f, 0.36f));
            Radius(sv, 8);
            sv.style.paddingLeft = 6; sv.style.paddingTop = 4;

            string active = Designer.RoadPlanLayer.ActiveProfileId;
            // Group profiles by category (preserving first-seen category order).
            var order = new List<string>();
            var byCat = new System.Collections.Generic.Dictionary<string, List<SavedConfig>>();
            foreach (var c in RoadProfileLibrary.Configs)
            {
                if (c?.Road == null) continue;
                string cat = string.IsNullOrWhiteSpace(c.Category) ? "Uncategorized" : c.Category.Trim();
                if (!byCat.TryGetValue(cat, out var list)) { list = new List<SavedConfig>(); byCat[cat] = list; order.Add(cat); }
                list.Add(c);
            }
            foreach (string cat in order)
            {
                sv.Add(SectionLabel(cat.ToUpperInvariant()));
                var grid = new VisualElement();
                grid.style.flexDirection = FlexDirection.Row; grid.style.flexWrap = Wrap.Wrap; grid.style.marginBottom = 4;
                foreach (var c in byCat[cat])
                    grid.Add(BuildThumb(c, active == c.Id || active == c.Name));
                sv.Add(grid);
            }
            return sv;
        }

        VisualElement BuildThumb(SavedConfig c, bool selected)
        {
            var cell = new VisualElement();
            cell.style.width = 78; cell.style.height = 66;
            cell.style.marginRight = 6; cell.style.marginBottom = 6;
            cell.style.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            Radius(cell, 6);
            SetBorder(cell, selected ? 2 : 1, selected ? Accent : new Color(0.30f, 0.32f, 0.36f));
            cell.style.paddingTop = 6; cell.style.paddingLeft = 5; cell.style.paddingRight = 5;
            cell.style.overflow = Overflow.Hidden;

            var strip = HBox();
            strip.style.height = 26; strip.style.alignItems = Align.Stretch;
            bool sidewalk = c.Road.Sidewalks && !c.Road.Elevated;
            foreach (var (w, k) in NetworkDesigner.Roads.RoadLayout.Of(c.Road))
            {
                var box = new VisualElement();
                box.style.flexGrow = w; box.style.backgroundColor = NetworkDesigner.Roads.RoadLayout.KindColor(k, sidewalk);
                strip.Add(box);
            }
            cell.Add(strip);

            var lbl = new Label(c.Name ?? c.Id);
            lbl.style.color = Sub; lbl.style.fontSize = 9; lbl.style.marginTop = 4;
            lbl.style.unityTextAlign = TextAnchor.MiddleCenter; lbl.style.whiteSpace = WhiteSpace.NoWrap;
            cell.Add(lbl);

            SavedConfig cc = c;
            cell.RegisterCallback<ClickEvent>(_ => { Designer.RoadPlanLayer.ActiveProfileId = cc.Id; Designer.RebuildRoadPlan(); Rebuild(); });
            return cell;
        }

        // Cross-section strips (width, kind) for a mini preview — shoulders, lanes, median / turn lane.
        static List<(float w, string kind)> Strips(NetworkDesigner.Model.RoadProfile p)
        {
            var s = new List<(float, string)>();
            void Add(float w, string k) { if (w > 0.01f) s.Add((w, k)); }
            Add(p.ShoulderBA != null ? p.ShoulderBA.Width : 0f, "shoulder");
            for (int i = p.BA.Lanes.Count - 1; i >= 0; i--) Add(p.BA.Lanes[i].Width, "lane");
            if (p.Median != null) Add(p.Median.Width, "median");
            else if (p.TurnLane != null) Add(p.TurnLane.Width, "turn");
            for (int i = 0; i < p.AB.Lanes.Count; i++) Add(p.AB.Lanes[i].Width, "lane");
            Add(p.ShoulderAB != null ? p.ShoulderAB.Width : 0f, "shoulder");
            return s;
        }

        static Color StripColor(string kind)
        {
            switch (kind)
            {
                case "lane": return new Color(0.30f, 0.31f, 0.33f);
                case "shoulder": return new Color(0.19f, 0.20f, 0.21f);
                case "median": return new Color(0.32f, 0.42f, 0.26f);
                case "turn": return new Color(0.42f, 0.36f, 0.14f);
                default: return new Color(0.25f, 0.25f, 0.25f);
            }
        }

        Label Cap(string t)
        {
            var l = new Label(t);
            l.style.color = Sub; l.style.fontSize = 12; l.style.marginBottom = 2;
            return l;
        }
    }
}
