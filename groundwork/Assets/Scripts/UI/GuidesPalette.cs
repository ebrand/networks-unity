// "Design Controls" palette — the shared node + guide controls (PlanGuides) that rail build, rail plan and
// road plan all respect: node display/size/colour, the collinear extension guide, and proximity snapping.
// Self-spawns at runtime (no scene wiring); launcher button, no hotkey (by request). Footer = Topo/Grid/Snap.

using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;
using NetworkDesigner.Roads;

namespace NetworkDesigner.UI
{
    public class GuidesPalette : PaletteBase
    {
        public override string PaletteId => "Guides";
        public override string MenuLabel => "Gd";
        protected override string Title => "Design Controls";
        protected override Color Accent => new Color(0.55f, 0.70f, 0.82f);   // steel blue
        protected override float PanelWidth => 300f;
        protected override bool AnchorRight => true;   // quick-palette overlay — sit clear of the left-anchored tool palette
        protected override string FooterMode => "Design";
        protected override string FooterSub => string.Empty;

        // A settings palette — drop any rail/road/scatter tool underneath it.
        protected override void OnOpened() => Designer.EnterSculptMode();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindFirstObjectByType<GuidesPalette>() != null) return;
            var go = new GameObject("GuidesPalette (auto)");
            go.AddComponent<UIDocument>();
            go.AddComponent<GuidesPalette>();
        }

        protected override void BuildBody(VisualElement body)
        {
            body.Add(SectionLabel("NODES"));
            body.Add(ToggleRow("Show nodes", () => PlanGuides.ShowNodes,
                v => { PlanGuides.ShowNodes = v; Designer.RebuildRoadPlan(); }));
            body.Add(SliderRow("Node size", () => PlanGuides.NodePuckRadius,
                v => { PlanGuides.NodePuckRadius = v; Designer.RebuildRoadPlan(); }, 0.2f, 5f, "0.#"));

            body.Add(Divider());
            body.Add(SectionLabel("GUIDES"));
            body.Add(SliderRow("Guide length", () => PlanGuides.ExtensionGuideLength,
                v => PlanGuides.ExtensionGuideLength = v, 0f, 2000f, "0"));
            body.Add(SliderRow("Colinear snap", () => PlanGuides.ExtensionSnapRadius,
                v => PlanGuides.ExtensionSnapRadius = v, 0f, 30f, "0.#"));
            body.Add(SliderRow("Node snap", () => PlanGuides.EndSnapRadius,
                v => PlanGuides.EndSnapRadius = v, 0f, 30f, "0.#"));
            body.Add(SliderRow("Guide range", () => PlanGuides.GuideRange,
                v => PlanGuides.GuideRange = v, 0f, 2000f, "0"));
            body.Add(SliderRow("Guide snap", () => PlanGuides.GuideSnapRadius,
                v => PlanGuides.GuideSnapRadius = v, 0f, 30f, "0.#"));
            body.Add(ToggleRow("Midpoint guides", () => PlanGuides.MidpointGuides,
                v => PlanGuides.MidpointGuides = v));
            body.Add(SliderRow("Node pick", () => PlanGuides.NodePickRadius,
                v => PlanGuides.NodePickRadius = v, 0f, 10f, "0.#"));

            body.Add(Divider());
            body.Add(SectionLabel("ROAD PLAN"));
            body.Add(ToggleRow("Lane-edge mode", () => LaneEdgeModel.Enabled,
                v => { LaneEdgeModel.Enabled = v;
                       if (!v) LaneEdgeModel.MappingMode = false;
                       LaneEdgeWorld.CancelDraw(); LaneEdgeWorld.CancelMap(); }));
            body.Add(ToggleRow("Map lane flows", () => LaneEdgeModel.MappingMode,
                v => { LaneEdgeModel.MappingMode = v;
                       if (v) LaneEdgeModel.Enabled = true;
                       LaneEdgeWorld.CancelMap(); }));
            Button exitPullBtn = null;
            void SyncExit()
            {
                var m = LaneEdgeWorld.ExitMode;
                exitPullBtn.text = "Exit pull: " + (m == LaneEdgeWorld.ExitPullMode.Keep ? "Keep lanes"
                    : m == LaneEdgeWorld.ExitPullMode.DeleteAll ? "Delete all pulled" : "Delete outer only");
                StyleActive(exitPullBtn, m != LaneEdgeWorld.ExitPullMode.Keep);
            }
            exitPullBtn = MakeButton("Exit pull: …", () =>
            {
                LaneEdgeWorld.ExitMode = (LaneEdgeWorld.ExitPullMode)(((int)LaneEdgeWorld.ExitMode + 1) % 3);
                SyncExit();
            });
            exitPullBtn.style.marginTop = 6;
            exitPullBtn.tooltip = "When pulling lanes off to a ramp: Keep = source keeps its lanes; " +
                                  "Delete all = the pulled lanes leave; Delete outer = only the outermost pulled lane leaves.";
            body.Add(exitPullBtn); SyncExit();
            var limitRadius = ToggleRow("Limit curve radius (speed)",
                () => Designer.RoadPlanLayer.LimitCurveRadius,
                v => Designer.RoadPlanLayer.LimitCurveRadius = v);
            limitRadius.tooltip = "On: curves are held to the design-speed minimum radius (tighter curves refused, preview turns red). " +
                                  "Off: unconstrained — draw tight/small turns by hand.";
            body.Add(limitRadius);

            body.Add(Divider());
            body.Add(SectionLabel("MEASUREMENTS"));
            body.Add(ToggleRow("Distance ladder", () => LaneEdgeModel.ShowRuler,
                v => LaneEdgeModel.ShowRuler = v));
            body.Add(ToggleRow("Segment lengths", () => LaneEdgeModel.ShowSegmentLabels,
                v => LaneEdgeModel.ShowSegmentLabels = v));

            body.Add(Divider());
            body.Add(SectionLabel("SLIP LANE"));
            body.Add(ToggleRow("Build transition pockets", () => LaneEdgeWorld.SlipBuildPockets,
                v => LaneEdgeWorld.SlipBuildPockets = v));
            var clipGores = ToggleRow("Clip gore shoulders", () => LaneEdgeWorld.SlipClipGores,
                v => { LaneEdgeWorld.SlipClipGores = v; Designer.RebuildRoadPlan(); });
            clipGores.tooltip = "Clip slip/through shoulders at the gore. Fragile on skewed or curved intersections (can leave stray lines) — off by default.";
            body.Add(clipGores);
            body.Add(SliderRow("Curve tightness", () => LaneEdgeWorld.SlipCurveTightness,
                v => LaneEdgeWorld.SlipCurveTightness = v, 0.1f, 0.9f, "0.00"));
            body.Add(SliderRow("Lane width (0=auto)", () => LaneEdgeWorld.SlipLaneWidth,
                v => LaneEdgeWorld.SlipLaneWidth = v, 0f, 6f, "0.#"));

            body.Add(Divider());
            body.Add(SectionLabel("GESTURES"));
            foreach (var g in new[]
            {
                "Z   slip lane — click start node, then merge node",
                "X   split a segment at the click",
                "C   connect two nodes with a curve",
                "I   inspect a corridor (dumps to console)",
                ",   flip a corridor's cross-section",
                "Alt  pull lanes one at a time (ramp subset)",
                "drag yellow puck — set junction setback",
            })
            {
                var l = new Label(g);
                l.style.color = Sub; l.style.fontSize = 11; l.style.whiteSpace = WhiteSpace.Normal; l.style.marginBottom = 1;
                body.Add(l);
            }
        }
    }
}
