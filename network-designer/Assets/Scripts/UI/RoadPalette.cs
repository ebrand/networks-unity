// In-game road tool palette (UI Toolkit). The home for the road plan today, and the cross-section
// designer + excavate/lay pipeline as they land. Self-spawns at runtime — no scene wiring (same pattern
// as EnvironmentPalette). Mirrors RailPalette; all shared plumbing lives in PaletteBase.

using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

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

        // Opening the Road palette drops straight into road-plan drawing.
        protected override void OnOpened() => Designer.EnterRoadPlanMode();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindFirstObjectByType<RoadPalette>() != null) return;
            var go = new GameObject("RoadPalette (auto)");
            go.AddComponent<UIDocument>();
            go.AddComponent<RoadPalette>();
        }

        protected override void BuildBody(VisualElement body)
        {
            body.Add(SectionLabel("PLAN"));

            // Toggle road-plan drawing (also the ';' hotkey).
            var planBtn = MakeButton("Road Plan (;)", () =>
            {
                if (Designer.IsRoadPlanMode) Designer.EnterSculptMode();
                else Designer.EnterRoadPlanMode();
            });
            planBtn.style.marginBottom = 10;
            body.Add(planBtn);
            _sync.Add(() => StyleActive(planBtn, Designer.IsRoadPlanMode));

            // Profile: a road-config.json preset whose total width sets the corridor footprint.
            // "Custom" falls back to the width slider below.
            const string custom = "Custom (width)";
            var names = new System.Collections.Generic.List<string> { custom };
            foreach (var c in NetworkDesigner.Roads.RoadProfileLibrary.Configs)
                if (c != null && !string.IsNullOrEmpty(c.Name)) names.Add(c.Name);
            string cur = string.IsNullOrEmpty(Designer.RoadPlanLayer.ProfileId) ? custom : Designer.RoadPlanLayer.ProfileId;
            if (!names.Contains(cur)) cur = custom;
            var profDd = new DropdownField { choices = names, value = cur, label = "Profile" };
            profDd.RegisterValueChangedCallback(e =>
            {
                Designer.RoadPlanLayer.ProfileId = e.newValue == custom ? "" : e.newValue;
                Designer.RebuildRoadPlan();
            });
            profDd.style.marginBottom = 6; body.Add(profDd);

            var reload = MakeButton("Reload profiles", () => { NetworkDesigner.Roads.RoadProfileLibrary.Reload(); Rebuild(); });
            reload.style.marginBottom = 8; body.Add(reload);

            // Custom-width fallback (used when no profile is selected; a profile overrides this).
            body.Add(NumberRow("Width (custom)", "m",
                () => Designer.RoadPlanLayer.RoadWidth,
                v => { Designer.RoadPlanLayer.RoadWidth = v; Designer.RebuildRoadPlan(); }, 3f, 60f, "0"));
            body.Add(ToggleRow("Straight (hard corners)",
                () => Designer.RoadPlanLayer.Straight,
                v => { Designer.RoadPlanLayer.Straight = v; Designer.RebuildRoadPlan(); }));

            var clear = MakeButton("Clear Plan", () => Designer.ClearRoadPlan());
            clear.style.marginTop = 10;
            body.Add(clear);

            body.Add(Divider());
            var note = new Label("Click to chain corridor nodes. Profile binding, excavation + 3D road sweep coming next.");
            note.style.color = Sub; note.style.fontSize = 11; note.style.whiteSpace = WhiteSpace.Normal;
            body.Add(note);
        }
    }
}
