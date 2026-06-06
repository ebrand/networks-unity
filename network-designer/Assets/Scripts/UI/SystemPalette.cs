// System palette (UI Toolkit): terrain generation + heightmap import + autosave — the
// heavier, less-frequent "system" operations split out of the Terrain Operations palette so that
// one isn't absurdly tall. Opened (exclusively) from the launcher; renders top-left like
// the other content palettes, with the shared footer. Shared plumbing is in PaletteBase.

using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class SystemPalette : PaletteBase
    {
        public override string PaletteId => "System";
        public override string MenuLabel => "System (Y)";
        protected override string Title => "System";
        protected override Color Accent => new Color(0.52f, 0.76f, 0.46f);   // match Terrain palette
        protected override float PanelWidth => 300f;

        protected override string FooterMode => "System";
        protected override string FooterSub => string.Empty;

        // Opening System exits any rail/scatter mode (these are terrain-level operations).
        protected override void OnOpened() => Designer.EnterSculptMode();

        protected override void BuildBody(VisualElement body)
        {
            // ---- TERRAIN ----
            body.Add(SectionLabel("TERRAIN"));
            body.Add(NumberRow("Map Side", "m", () => Designer.TerrainSizeMeters,
                v => Designer.TerrainSizeMeters = v, 100f, 20000f, "0"));
            body.Add(NumberRow("Cell Size", "m", () => Designer.CellSize,
                v => Designer.CellSize = v, 1f, 50f, "0.#"));
            body.Add(NumberRow("Cells/Chnk", "", () => Designer.ChunkCells,
                v => Designer.ChunkCells = Mathf.Max(1, Mathf.RoundToInt(v)), 1f, 200f, "0"));
            var gen = MakeButton("Generate Terrain (destructive!)", () => Designer.RebuildTerrain());
            gen.style.marginTop = 4;
            body.Add(gen);

            // ---- HEIGHTMAP ----
            body.Add(Divider());
            body.Add(SectionLabel("HEIGHTMAP"));
            body.Add(DropdownRow(() => Designer.ListHeightmapFiles(),
                () => Designer.HeightmapFile, v => Designer.HeightmapFile = v));
            body.Add(NumberRow("Max Hgt", "m", () => Designer.HeightmapMaxHeight,
                v => Designer.HeightmapMaxHeight = v, 1f, 2000f, "0"));
            body.Add(NumberRow("Smooth", "", () => Designer.HeightmapSmoothPasses,
                v => Designer.HeightmapSmoothPasses = Mathf.Max(0, Mathf.RoundToInt(v)), 0f, 12f, "0"));
            var hmLoad = MakeButton("Load", () => Designer.ImportHeightmap());
            hmLoad.style.marginTop = 4;
            body.Add(hmLoad);

            // ---- AUTOSAVE ----
            body.Add(Divider());
            body.Add(SectionLabel("AUTOSAVE"));
            body.Add(ToggleRow("Autosave", () => Designer.Autosave, v => Designer.Autosave = v));
            var save = MakeButton("Save", () => Designer.SaveNow());
            save.style.marginTop = 4;
            body.Add(save);
            var load = MakeButton("Load", () => Designer.LoadNow());
            load.style.marginTop = 6;
            body.Add(load);
        }
    }
}
