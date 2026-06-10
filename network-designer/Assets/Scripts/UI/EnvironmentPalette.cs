// Environment palette (hotkey U): ATMOSPHERE (fog / aerial perspective + mood sky) and COLOUR GRADE
// (post-FX: exposure / contrast / saturation / warmth / bloom / vignette). The sun stays owned by
// SceneAmbiance. All controls are LIVE via DemLighting. Self-spawns at runtime — no scene wiring.

using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    public class EnvironmentPalette : PaletteBase
    {
        public override string PaletteId => "Environment";
        public override string MenuLabel => "U";
        protected override string Title => "Environment";
        protected override Color Accent => new Color(0.45f, 0.62f, 0.85f);   // sky blue
        protected override float PanelWidth => 300f;
        protected override string FooterMode => "Environment";
        protected override string FooterSub => string.Empty;

        // Opening Environment is a view/settings mode — drop any rail/scatter tool underneath it.
        protected override void OnOpened() => Designer.EnterSculptMode();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindFirstObjectByType<EnvironmentPalette>() != null) return;
            var go = new GameObject("EnvironmentPalette (auto)");
            go.AddComponent<UIDocument>();
            go.AddComponent<EnvironmentPalette>();
        }

        protected override void BuildBody(VisualElement body)
        {
            // Master toggle: builds/tears down the fog + mood sky + post-FX volume. The sliders below
            // take effect live while it's on.
            body.Add(SectionLabel("ATMOSPHERE"));
            body.Add(ToggleRow("Enable", () => DemLighting.Enabled, v => DemLighting.SetEnabled(v)));
            body.Add(SliderRow("Haze (fog)", () => DemLighting.FogDensity,
                v => { DemLighting.FogDensity = v; DemLighting.Apply(); }, 0f, 0.0006f, "0.00000"));

            body.Add(Divider());
            body.Add(SectionLabel("COLOUR GRADE"));
            body.Add(SliderRow("Exposure", () => DemLighting.Exposure,
                v => { DemLighting.Exposure = v; DemLighting.Apply(); }, -2f, 2f, "0.00"));
            body.Add(SliderRow("Contrast", () => DemLighting.Contrast,
                v => { DemLighting.Contrast = v; DemLighting.Apply(); }, -50f, 50f, "0"));
            body.Add(SliderRow("Saturation", () => DemLighting.Saturation,
                v => { DemLighting.Saturation = v; DemLighting.Apply(); }, -50f, 50f, "0"));
            body.Add(SliderRow("Warmth", () => DemLighting.Warmth,
                v => { DemLighting.Warmth = v; DemLighting.Apply(); }, 0f, 1f, "0.00"));
            body.Add(SliderRow("Bloom", () => DemLighting.BloomIntensity,
                v => { DemLighting.BloomIntensity = v; DemLighting.Apply(); }, 0f, 1.5f, "0.00"));
            body.Add(SliderRow("Vignette", () => DemLighting.VignetteAmount,
                v => { DemLighting.VignetteAmount = v; DemLighting.Apply(); }, 0f, 0.6f, "0.00"));
        }
    }
}
