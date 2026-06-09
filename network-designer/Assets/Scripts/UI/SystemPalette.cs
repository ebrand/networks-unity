// System palette (UI Toolkit): the genuinely system-level operations — named map save/load
// and the camera. Everything that authors the terrain (low-poly brush/grid/water/heightmap/
// generator AND the DEM world/sculpt/grass/lighting) now lives in the Terrain palette, behind
// its Low-Poly / DEM toggle. Opened (exclusively) from the launcher; shared plumbing is in
// PaletteBase.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class SystemPalette : PaletteBase
    {
        public override string PaletteId => "System";
        public override string MenuLabel => "Y";
        protected override string Title => "System";
        protected override Color Accent => new Color(0.52f, 0.76f, 0.46f);
        protected override float PanelWidth => 300f;

        protected override string FooterMode => "System";
        protected override string FooterSub => string.Empty;

        // Opening System exits any rail/scatter mode.
        protected override void OnOpened() => Designer.EnterSculptMode();

        protected override void BuildBody(VisualElement body)
        {
            // ---- CAMERA ---- (speed + chunk-world view toggles; map save/load moved to the launcher's
            // Games flow, and DEM worlds are started from there, so this palette is now view-only.)
            body.Add(SectionLabel("CAMERA"));
            body.Add(SliderRow("Speed", () => Designer.CameraSpeed,
                v => Designer.CameraSpeed = v, 5f, 50000f, "0"));
            body.Add(ToggleRow("Grid (1km/100m)", () => Designer.ChunkShowGrid, v => Designer.ChunkShowGrid = v));
            body.Add(ToggleRow("Lock bubble (hold Space)", () => Designer.ChunkLockBubble, v => Designer.ChunkLockBubble = v));
            body.Add(ToggleRow("Local build grid", () => Designer.ChunkLocalGrid, v => Designer.ChunkLocalGrid = v));
            // Full-screen top-down map to trim empty/ocean chunks out of the streamed set (DEM worlds).
            var trimBtn = MakeButton("Trim map (empty chunks)…", () =>
            {
                if (Designer.ChunkDemActive) ChunkMapEditor.Toggle();
            });
            trimBtn.style.marginTop = 6;
            body.Add(trimBtn);

            // ---- AUTOSAVE ---- (snapshots the scene a debounce after each edit; the snapshot
            // touches the terrain + every placed tree on the main thread, so on big scatter
            // worlds it hitches — turn it off or raise the debounce to avoid the periodic lag.)
            body.Add(Divider());
            body.Add(SectionLabel("AUTOSAVE"));
            body.Add(ToggleRow("Autosave", () => Designer.Autosave, v => Designer.Autosave = v));
            body.Add(NumberRow("Debounce", "s", () => Designer.AutosaveDebounceSeconds,
                v => Designer.AutosaveDebounceSeconds = Mathf.Max(0.5f, v), 0.5f, 60f, "0.#"));

            // ---- Back to the launcher (saves the game + tears down the world first) ----
            body.Add(Divider());
            var menuBtn = MakeButton("Save & exit to menu", () =>
            {
                Designer.SaveNow();           // write the game snapshot
                Designer.StopChunkTest();     // tear down the chunk/DEM world (also saves dirty chunks)
                GameManager.SetActive(null);  // clear active game → the startup picker reappears
            });
            menuBtn.style.marginTop = 4;
            body.Add(menuBtn);
        }
    }
}
