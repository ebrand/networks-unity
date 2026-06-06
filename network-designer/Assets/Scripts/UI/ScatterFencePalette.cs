// Scatter/Fence palette (UI Toolkit) — consolidates the Tree / Rock scatter layers and
// the Fence linework layer into one palette. Phase 1: mode buttons (Trees/Rocks/Fences),
// the shared scatter brush Strength/Density sliders, and a pack dropdown per scatter
// layer. Phase 2 will add the prefab thumbnail grid and the "..." pack-management modal
// (with a live 3D preview); the ellipsis buttons are stubbed for now. While this palette
// is open the old IMGUI scatter palette is suppressed (TerrainDesigner). Plumbing: PaletteBase.

using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class ScatterFencePalette : PaletteBase
    {
        public override string PaletteId => "ScatterFence";
        public override string MenuLabel => "Scatter/Fence";
        protected override string Title => "Scatter/Fence Operations";
        protected override Color Accent => new Color(0.55f, 0.8f, 0.45f);   // foliage green
        protected override float PanelWidth => 320f;

        protected override string FooterMode =>
            Designer.IsTreeMode ? "Trees" : Designer.IsRockMode ? "Rocks"
            : Designer.IsFenceMode ? "Fences" : "Scatter";
        protected override string FooterSub => "Place";

        // Opening defaults to Trees scatter.
        protected override void OnOpened() => Designer.EnterTreeMode();

        // Density slider maps inversely to Spacing (denser = smaller spacing).
        const float MinSp = 0.5f, MaxSp = 15f;
        static float DensityOf(float spacing) => Mathf.InverseLerp(MaxSp, MinSp, spacing);
        static float SpacingOf(float density) => Mathf.Lerp(MaxSp, MinSp, density);

        // The scatter layer the shared Strength/Density sliders drive (Rocks if in rock
        // mode, else Trees).
        ScatterLayer ActiveScatter() => Designer.IsRockMode ? Designer.RockLayer : Designer.TreeLayer;

        protected override void BuildBody(VisualElement body)
        {
            body.Add(SectionLabel("SCATTER BRUSH"));
            body.Add(SliderRow("Strength",
                () => ActiveScatter().PaintRate, v => ActiveScatter().PaintRate = v, 1f, 100f, "0"));
            body.Add(SliderRow("Density",
                () => DensityOf(ActiveScatter().Spacing), v => ActiveScatter().Spacing = SpacingOf(v), 0f, 1f, "0.00"));

            body.Add(ScatterRow("Trees (T)", Designer.TreeLayer, () => Designer.IsTreeMode, Designer.EnterTreeMode));
            body.Add(ScatterRow("Rocks (R)", Designer.RockLayer, () => Designer.IsRockMode, Designer.EnterRockMode));

            var fBtn = MakeButton("Fences (F)", () => Designer.EnterFenceMode());
            fBtn.style.flexGrow = 0; fBtn.style.width = 100; fBtn.style.marginTop = 4;
            body.Add(fBtn);
            _sync.Add(() => StyleActive(fBtn, Designer.IsFenceMode));

            // TODO Phase 2: prefab thumbnail grid of the active layer's pack here.
        }

        // [ mode button ] [ pack dropdown ] [ ... ]
        VisualElement ScatterRow(string label, ScatterLayer layer, System.Func<bool> active, System.Action enter)
        {
            var row = HBox(); row.style.marginBottom = 6;

            var btn = MakeButton(label, () => enter());
            btn.style.flexGrow = 0; btn.style.width = 100; btn.style.marginRight = 6;
            _sync.Add(() => StyleActive(btn, active()));

            var dd = new DropdownField { choices = layer.PackNames() };
            dd.SetValueWithoutNotify(layer.ActivePackName);
            dd.style.flexGrow = 1;
            dd.RegisterValueChangedCallback(e => layer.SelectPackByName(e.newValue));
            _sync.Add(() => { if (dd.value != layer.ActivePackName) dd.SetValueWithoutNotify(layer.ActivePackName); });

            var dots = MakeButton("...", () =>
                Debug.Log($"[ScatterFence] {layer.Name} pack management modal — coming in Phase 2."));
            dots.style.flexGrow = 0; dots.style.width = 36; dots.style.marginLeft = 6;

            row.Add(btn); row.Add(dd); row.Add(dots);
            return row;
        }
    }
}
