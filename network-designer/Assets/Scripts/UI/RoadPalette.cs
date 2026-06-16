// In-game road tool palette (UI Toolkit). MODES (Plan/Build) · DESIGN PROFILE (category-filtered thumbnail
// picker + Excavate/Build interactive sub-modes) · PLANS (named save/load library) · an ADVANCED foldout
// holding width/depth/margin, whole-plan excavate/build, clear/remove, and the marking toggles.
// Self-spawns at runtime; all shared plumbing lives in PaletteBase.

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

        string _profileCategory;   // currently-shown DESIGN PROFILE category (drives the thumbnail grid)

        protected override void BuildBody(VisualElement body)
        {
            // ---- MODES ----
            body.Add(SectionLabel("MODES"));
            var modes = HBox(); modes.style.marginBottom = 12;
            var planBtn = MakeButton("Plan (;)", () =>
            {
                if (Designer.IsRoadPlanMode && !Designer.IsRoadBuildMode) Designer.EnterSculptMode(); else Designer.EnterRoadPlanMode();
            });
            planBtn.style.flexGrow = 1; planBtn.style.marginRight = 6;
            planBtn.tooltip = "Plan mode: draw/edit the road plan. Right-click a node deletes it + its segments (+ built road).";
            var buildBtn = MakeButton("Build (')", () =>
            {
                if (Designer.IsRoadBuildMode) Designer.EnterSculptMode(); else Designer.EnterRoadBuildMode();
            });
            buildBtn.style.flexGrow = 1;
            buildBtn.tooltip = "Build mode. Right-click a node deletes it + its segments (+ built road), same as Plan mode.";
            modes.Add(planBtn); modes.Add(buildBtn);
            body.Add(modes);
            _sync.Add(() => { StyleActive(planBtn, Designer.IsRoadPlanMode && !Designer.IsRoadBuildMode); StyleActive(buildBtn, Designer.IsRoadBuildMode); });

            // ---- DESIGN PROFILE ----
            body.Add(Divider());
            body.Add(SectionLabel("DESIGN PROFILE"));
            body.Add(Cap("Road profile category:"));

            var cats = ProfileCategories();
            if (string.IsNullOrEmpty(_profileCategory) || !cats.Contains(_profileCategory))
                _profileCategory = ActiveProfileCategory(cats);

            var catRow = HBox(); catRow.style.marginBottom = 8;
            var catDd = new DropdownField { choices = cats, value = _profileCategory };
            catDd.style.flexGrow = 1;
            catDd.RegisterValueChangedCallback(e => { _profileCategory = e.newValue; Rebuild(); });
            var dots = MakeButton("…", () => PaletteBase.ToggleQuick("RoadDesigner"));
            dots.style.width = 42; dots.style.flexGrow = 0; dots.style.marginLeft = 6;
            dots.tooltip = "Open the Road Designer";
            catRow.Add(catDd); catRow.Add(dots);
            body.Add(catRow);

            // visual profile picker (thumbnails), filtered to the selected category
            body.Add(BuildThumbGrid(_profileCategory));

            // ---- ACTION QUEUE: Cmd/Ctrl-click inside segments to select, then act on the selection ----
            var selHint = Cap("Cmd/Ctrl-click inside a segment to select it (red=planned · yellow=excavated · blue=bridge)");
            selHint.style.fontSize = 10; selHint.style.marginTop = 4;
            body.Add(selHint);
            var selCount = Cap("0 selected");
            selCount.style.fontSize = 10; selCount.style.marginBottom = 4;
            body.Add(selCount);

            var actRow = HBox(); actRow.style.marginBottom = 4;
            var excBtn = MakeButton("Excavate", () => Designer.ExcavateSelectedRoads());
            excBtn.style.flexGrow = 1; excBtn.style.marginRight = 6;
            excBtn.tooltip = "Cut + fill the bed of every SELECTED planned (red) segment → they turn yellow, ready to build. Bridges are skipped.";
            var bldBtn = MakeButton("Build", () => Designer.BuildSelectedRoads());
            bldBtn.style.flexGrow = 1;
            bldBtn.tooltip = "Sweep the 3D road on every SELECTED segment that's excavated (yellow) or a bridge, each with its own profile.";
            actRow.Add(excBtn); actRow.Add(bldBtn);
            body.Add(actRow);

            var actRow2 = HBox(); actRow2.style.marginBottom = 10;
            var brBtn = MakeButton("Force Bridge", () => Designer.ForceBridgeSelectedRoads());
            brBtn.style.flexGrow = 1; brBtn.style.marginRight = 6;
            brBtn.tooltip = "Flag the SELECTED segments as a BRIDGE (blue): ends leveled, NOT excavated, built on a deck + piers. " +
                            "If they're all already bridges, this un-bridges them.";
            var clrBtn = MakeButton("Clear Plan", () => Designer.ClearRoadPlan());
            clrBtn.style.flexGrow = 1;
            clrBtn.tooltip = "Delete the ENTIRE road plan (all nodes + segments) and any 3D road built from it.";
            actRow2.Add(brBtn); actRow2.Add(clrBtn);
            body.Add(actRow2);

            _sync.Add(() =>
            {
                int c = Designer.RoadSelectionCount;
                selCount.text = c + " selected";
                excBtn.SetEnabled(c > 0); bldBtn.SetEnabled(c > 0); brBtn.SetEnabled(c > 0);   // Clear Plan always enabled
            });

            // Show/hide the plan-line markings (nodes stay visible). Highlighted = lines shown.
            var planLinesRow = ToggleRow("Plan lines",
                () => !Designer.RoadPlanLayer.PlanLinesHidden,
                v => Designer.RoadPlanLayer.SetPlanLinesVisible(v));
            planLinesRow.tooltip = "Show or hide the plan overlay — both the line markings and the node pucks.";
            body.Add(planLinesRow);

            // Bridge parapets toggle (top-level so it's reachable without opening ADVANCED; height lives in ADVANCED).
            var parapetTop = MakeButton("Bridge parapets", () => { Designer.RoadPlanLayer.BridgeParapets = !Designer.RoadPlanLayer.BridgeParapets; Designer.RefreshBuiltRoads(); });
            parapetTop.style.marginBottom = 10;
            parapetTop.tooltip = "Build side barrier walls along the deck edges of built bridge segments (height in ADVANCED).";
            body.Add(parapetTop);
            _sync.Add(() => StyleActive(parapetTop, Designer.RoadPlanLayer.BridgeParapets));

            // ---- PLANS (named per-world library) ----
            body.Add(Divider());
            body.Add(SectionLabel("PLANS"));
            body.Add(Cap("Current Plan:"));
            string sel = Designer.CurrentRoadPlanName ?? string.Empty;
            TextField nameField = null;   // assigned below; planDd's callback only fires after that
            var planRow = HBox(); planRow.style.marginBottom = 6;
            var planDd = new DropdownField { choices = Designer.ListRoadPlans(), value = sel };
            planDd.style.flexGrow = 1; planDd.style.marginRight = 6;
            planDd.RegisterValueChangedCallback(e => { sel = e.newValue; nameField?.SetValueWithoutNotify(e.newValue); });
            var revertBtn = MakeButton("Revert", () => Designer.RevertRoadPlan());
            revertBtn.style.width = 80;
            revertBtn.tooltip = "Reload the last saved/loaded plan, discarding edits since.";
            planRow.Add(planDd); planRow.Add(revertBtn);
            body.Add(planRow);

            // Editable name: picking a plan above fills it; edit it to Save a new copy.
            nameField = new TextField(); nameField.style.marginBottom = 6;
            nameField.SetValueWithoutNotify(sel);
            body.Add(nameField);

            var planActs = HBox();
            var saveBtn = MakeButton("Save", () =>
            {
                string nm = string.IsNullOrWhiteSpace(nameField.value) ? sel : nameField.value;
                Designer.SaveRoadPlanAs(nm); sel = SanitizeShown(nm);
            });
            saveBtn.style.flexGrow = 1; saveBtn.style.marginRight = 6;
            saveBtn.tooltip = "Save the current plan under the name above (edit the name to save a new copy).";
            var delBtn = MakeButton("Delete", () => { if (!string.IsNullOrEmpty(sel)) Designer.DeleteRoadPlan(sel); });
            delBtn.style.flexGrow = 1; delBtn.style.marginRight = 6;
            var loadBtn = MakeButton("Load", () => { if (!string.IsNullOrEmpty(sel)) Designer.LoadRoadPlan(sel); });
            loadBtn.style.flexGrow = 1;
            planActs.Add(saveBtn); planActs.Add(delBtn); planActs.Add(loadBtn);
            body.Add(planActs);
            var planNote = Cap("Save names a snapshot · Revert reloads it · Load swaps to another");
            planNote.style.fontSize = 10; planNote.style.marginTop = 2;
            body.Add(planNote);

            // ---- ADVANCED (collapsed by default): everything the simplified palette tucks away ----
            var adv = Section(body, "ADVANCED");
            adv.Add(NumberRow("Custom width", "m",
                () => Designer.RoadPlanLayer.RoadWidth,
                v => { Designer.RoadPlanLayer.RoadWidth = v; Designer.RebuildRoadPlan(); }, 3f, 60f, "0"));
            adv.Add(NumberRow("Excavate depth", "m",
                () => Designer.RoadPlanLayer.ExcavationDepth,
                v => Designer.RoadPlanLayer.ExcavationDepth = v, 0f, 5f, "0.0"));
            adv.Add(NumberRow("Excavate margin", "m",
                () => Designer.RoadPlanLayer.ExcavationMargin,
                v => { Designer.RoadPlanLayer.ExcavationMargin = v; Designer.RebuildRoadPlan(); }, 0f, 20f, "0.0"));
            adv.Add(NumberRow("Cut/fill slope 1:", "",
                () => Designer.RoadPlanLayer.CutBatter,
                v => Designer.RoadPlanLayer.CutBatter = v, 0.5f, 6f, "0.0"));
            var autoBr = MakeButton("Auto-bridge on draw", () => Designer.RoadPlanLayer.AutoBridge = !Designer.RoadPlanLayer.AutoBridge);
            autoBr.style.marginTop = 6;
            autoBr.tooltip = "While drawing, detect a terrain dip under a straight segment and auto-split it into approach / bridge (blue) / approach.";
            adv.Add(autoBr);
            _sync.Add(() => StyleActive(autoBr, Designer.RoadPlanLayer.AutoBridge));
            adv.Add(NumberRow("Bridge trigger depth", "m",
                () => Designer.RoadPlanLayer.BridgeTriggerDepth,
                v => Designer.RoadPlanLayer.BridgeTriggerDepth = v, 1f, 40f, "0.0"));
            adv.Add(NumberRow("Bridge approach pad", "m",
                () => Designer.RoadPlanLayer.BridgeApproachPad,
                v => Designer.RoadPlanLayer.BridgeApproachPad = v, 0f, 40f, "0"));
            adv.Add(NumberRow("Bridge deck depth", "m",
                () => Designer.RoadPlanLayer.BridgeDeckDepth,
                v => { Designer.RoadPlanLayer.BridgeDeckDepth = v; Designer.RefreshBuiltRoads(); }, 0.2f, 4f, "0.0"));
            adv.Add(NumberRow("Bridge pier spacing", "m",
                () => Designer.RoadPlanLayer.BridgePierSpacing,
                v => { Designer.RoadPlanLayer.BridgePierSpacing = v; Designer.RefreshBuiltRoads(); }, 4f, 60f, "0"));
            adv.Add(NumberRow("Bridge pier width", "m",
                () => Designer.RoadPlanLayer.BridgePierWidth,
                v => { Designer.RoadPlanLayer.BridgePierWidth = v; Designer.RefreshBuiltRoads(); }, 0.3f, 4f, "0.0"));
            adv.Add(NumberRow("Parapet height", "m",
                () => Designer.RoadPlanLayer.BridgeParapetHeight,
                v => { Designer.RoadPlanLayer.BridgeParapetHeight = v; Designer.RefreshBuiltRoads(); }, 0.2f, 2.5f, "0.0"));

            var elevBtn = MakeButton("Edit elevations", () => Designer.SetRoadElevationEdit(!Designer.RoadElevationEdit));
            elevBtn.style.marginTop = 6;
            elevBtn.tooltip = "Drag a node up/down to set its height; click a node to select; right-click a node to level all selected nodes to it";
            adv.Add(elevBtn);
            _sync.Add(() => StyleActive(elevBtn, Designer.RoadElevationEdit));
            var elevNote = Cap("Drag = set height · click = select · right-click = level selected to that node");
            elevNote.style.fontSize = 10; elevNote.style.marginBottom = 6;
            adv.Add(elevNote);

            var advActs = HBox(); advActs.style.marginBottom = 6;
            var excavateAll = MakeButton("Excavate", () => Designer.ExcavateRoadCorridor());
            excavateAll.style.flexGrow = 1; excavateAll.style.marginRight = 6;
            excavateAll.tooltip = "Smooth + cut the WHOLE roadbed into the terrain along the plan";
            var buildAll = MakeButton("Build Plan", () => Designer.BuildRoadPlan());
            buildAll.style.flexGrow = 1;
            buildAll.tooltip = "Sweep the whole resolved 3D road network into the excavated bed";
            advActs.Add(excavateAll); advActs.Add(buildAll);
            adv.Add(advActs);

            var advActs2 = HBox(); advActs2.style.marginBottom = 8;
            var rm = MakeButton("Remove roads", () => Designer.ClearBuiltRoads());   // Clear Plan now lives in the action area
            rm.style.flexGrow = 1;
            rm.tooltip = "Delete the built 3D road meshes (keeps the plan) — for testing";
            advActs2.Add(rm);
            adv.Add(advActs2);

            adv.Add(ToggleRow("Show crosswalks", () => Designer.RoadPlanLayer.ShowCrosswalks,
                v => { Designer.RoadPlanLayer.ShowCrosswalks = v; Designer.RebuildRoadPlan(); }));
            adv.Add(ToggleRow("Show stop lines", () => Designer.RoadPlanLayer.ShowStopBars,
                v => { Designer.RoadPlanLayer.ShowStopBars = v; Designer.RebuildRoadPlan(); }));
            adv.Add(ToggleRow("Guided turns (lock)", () => Designer.RoadPlanLayer.GuidedTurns,
                v => Designer.RoadPlanLayer.GuidedTurns = v));

            // ---- INTERSECTIONS ----
            adv.Add(SectionLabel("INTERSECTIONS"));
            // Global tightness: base setback floor as a fraction of road width (geometric minimum always enforced).
            adv.Add(NumberRow("Junction tightness", "× W",
                () => NetworkDesigner.Geometry.GeometryResolver.JunctionSetbackFloor,
                v => { NetworkDesigner.Geometry.GeometryResolver.JunctionSetbackFloor = v; Designer.RefreshBuiltRoads(); }, 0f, 1.5f, "0.0"));
            // Per-road override for the SELECTED segments (both ends); "Auto" reverts to the resolver-computed value.
            adv.Add(NumberRow("Sel. road setback", "m",
                () => Designer.SelectedRoadSetback(),
                v => Designer.SetSelectedRoadSetback(v), 0f, 40f, "0.0"));
            var autoSb = MakeButton("Auto setback (selected)", () => Designer.ClearSelectedRoadSetback());
            autoSb.tooltip = "Revert the SELECTED segments' junction setback to auto (resolver-computed).";
            adv.Add(autoSb);
            var sbNote = Cap("Select a road (Cmd/Ctrl-click), then set its setback; applies to both ends.");
            sbNote.style.fontSize = 10;
            adv.Add(sbNote);
        }

        static string SanitizeShown(string n) => (n ?? "").Trim();

        // Distinct profile categories in first-seen order (for the DESIGN PROFILE dropdown).
        static List<string> ProfileCategories()
        {
            var order = new List<string>();
            foreach (var c in RoadProfileLibrary.Configs)
            {
                if (c?.Road == null) continue;
                string cat = string.IsNullOrWhiteSpace(c.Category) ? "Uncategorized" : c.Category.Trim();
                if (!order.Contains(cat)) order.Add(cat);
            }
            if (order.Count == 0) order.Add("Uncategorized");
            return order;
        }

        // Category of the currently-active profile (so the dropdown lands on it), else the first category.
        string ActiveProfileCategory(List<string> cats)
        {
            string active = Designer.RoadPlanLayer.ActiveProfileId;
            if (!string.IsNullOrEmpty(active))
                foreach (var c in RoadProfileLibrary.Configs)
                    if (c?.Road != null && (c.Id == active || c.Name == active))
                        return string.IsNullOrWhiteSpace(c.Category) ? "Uncategorized" : c.Category.Trim();
            return cats.Count > 0 ? cats[0] : "Uncategorized";
        }

        // A scrollable grid of profile thumbnails (mini cross-sections) for ONE category; click to activate.
        VisualElement BuildThumbGrid(string category)
        {
            var sv = ScrollBox(190);
            sv.style.marginBottom = 8;
            SetBorder(sv, 1, new Color(0.3f, 0.32f, 0.36f));
            Radius(sv, 8);
            sv.style.paddingLeft = 6; sv.style.paddingTop = 4;

            string active = Designer.RoadPlanLayer.ActiveProfileId;
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row; grid.style.flexWrap = Wrap.Wrap; grid.style.marginBottom = 4;
            int n = 0;
            foreach (var c in RoadProfileLibrary.Configs)
            {
                if (c?.Road == null) continue;
                string cat = string.IsNullOrWhiteSpace(c.Category) ? "Uncategorized" : c.Category.Trim();
                if (cat != category) continue;
                grid.Add(BuildThumb(c, active == c.Id || active == c.Name));
                n++;
            }
            if (n == 0) sv.Add(Cap("No profiles in this category."));
            else sv.Add(grid);
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

        Label Cap(string t)
        {
            var l = new Label(t);
            l.style.color = Sub; l.style.fontSize = 12; l.style.marginBottom = 2;
            return l;
        }
    }
}
