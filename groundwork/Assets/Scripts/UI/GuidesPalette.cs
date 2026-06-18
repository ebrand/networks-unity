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
        }
    }
}
