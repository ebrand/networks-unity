// "Design Controls" palette — the shared node + guide controls (PlanGuides) that rail build, rail plan and
// road plan all respect: node display/size/colour, the collinear extension guide, and proximity snapping.
// Self-spawns at runtime (no scene wiring); launcher button, no hotkey (by request). Footer = Topo/Grid/Snap.

using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

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
            body.Add(SliderRow("Node height", () => PlanGuides.NodePuckHeight,
                v => { PlanGuides.NodePuckHeight = v; Designer.RebuildRoadPlan(); }, 0.05f, 3f, "0.#"));
            body.Add(ColorHexRow("Rail node", () => PlanGuides.RailNodeColor,
                c => PlanGuides.RailNodeColor = c));                                  // rail puck recolours live
            body.Add(ColorHexRow("Road node", () => PlanGuides.RoadNodeColor,
                c => { PlanGuides.RoadNodeColor = c; Designer.RebuildRoadPlan(); }));

            body.Add(Divider());
            body.Add(SectionLabel("GUIDES"));
            body.Add(SliderRow("Guide length", () => PlanGuides.ExtensionGuideLength,
                v => PlanGuides.ExtensionGuideLength = v, 0f, 2000f, "0"));
            body.Add(SliderRow("Colinear snap", () => PlanGuides.ExtensionSnapRadius,
                v => PlanGuides.ExtensionSnapRadius = v, 0f, 30f, "0.#"));
            body.Add(ToggleRow("Proximity", () => PlanGuides.ProximitySnapOn,
                v => PlanGuides.ProximitySnapOn = v));
            body.Add(SliderRow("Node snap", () => PlanGuides.EndSnapRadius,
                v => PlanGuides.EndSnapRadius = v, 0f, 30f, "0.#"));
            body.Add(SliderRow("Guide range", () => PlanGuides.GuideRange,
                v => PlanGuides.GuideRange = v, 0f, 2000f, "0"));
            body.Add(SliderRow("Guide snap", () => PlanGuides.GuideSnapRadius,
                v => PlanGuides.GuideSnapRadius = v, 0f, 30f, "0.#"));
            body.Add(SliderRow("Node pick", () => PlanGuides.NodePickRadius,
                v => PlanGuides.NodePickRadius = v, 0f, 10f, "0.#"));

            body.Add(Divider());
            body.Add(SectionLabel("CURVES"));
            body.Add(SliderRow("Curve lever", () => PlanGuides.CurveLever,
                v => PlanGuides.CurveLever = v, 0f, 1f, "0.00"));
            body.Add(SliderRow("Curve symmetry", () => PlanGuides.CurveSymmetrySnap,
                v => PlanGuides.CurveSymmetrySnap = v, 0f, 1f, "0.00"));

            body.Add(Divider());
            body.Add(SectionLabel("SPEED CONSTRAINTS"));
            // Design speeds gate the tightest curve each tool will draw (min radius from speed).
            body.Add(NumberRow("Rail design speed", "km/h",
                () => Designer.RailLayer.SpeedLimitKmh,
                v => Designer.RailLayer.SpeedLimitKmh = v, 5f, 200f, "0"));   // rail plan mirrors this live
            body.Add(NumberRow("Road design speed", "km/h",
                () => Designer.RoadPlanLayer.DesignSpeedKmh,
                v => Designer.RoadPlanLayer.DesignSpeedKmh = v, 5f, 200f, "0"));
        }

        // A "Label  #RRGGBBAA" row: a delayed text field that parses an HTML colour hex.
        VisualElement ColorHexRow(string label, System.Func<Color> get, System.Action<Color> set)
        {
            var row = HBox();
            var lbl = new Label(label);
            lbl.style.color = Ink; lbl.style.flexGrow = 1; lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
            var tf = new TextField { value = "#" + ColorUtility.ToHtmlStringRGBA(get()), isDelayed = true };
            tf.style.width = 110;
            tf.RegisterValueChangedCallback(e =>
            {
                string s = e.newValue.StartsWith("#") ? e.newValue : "#" + e.newValue;
                if (ColorUtility.TryParseHtmlString(s, out Color c)) set(c);
                tf.SetValueWithoutNotify("#" + ColorUtility.ToHtmlStringRGBA(get()));   // normalise display
            });
            row.Add(lbl); row.Add(tf);
            return row;
        }
    }
}
