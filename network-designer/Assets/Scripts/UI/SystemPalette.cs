// System palette (UI Toolkit): named map save/load + heightmap import + autosave — the
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
            // ---- MAP (named save/load from Resources/Maps) ----
            body.Add(SectionLabel("MAP"));
            var mapDrop = new DropdownField { choices = Designer.ListMaps() };
            mapDrop.style.marginBottom = 6;
            body.Add(mapDrop);
            // Transient status line under the buttons (auto-clears after a few seconds).
            var status = new Label();
            status.style.fontSize = 11;
            status.style.unityFontStyleAndWeight = FontStyle.Italic;
            status.style.marginBottom = 6;
            float[] until = { 0f };
            void Status(string msg, bool ok)
            {
                status.text = msg;
                status.style.color = ok ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.95f, 0.6f, 0.5f);
                until[0] = Time.realtimeSinceStartup + 3f;
            }
            _sync.Add(() => { if (!string.IsNullOrEmpty(status.text) && Time.realtimeSinceStartup > until[0]) status.text = ""; });

            var mapRow = HBox();
            var newBtn = MakeButton("New", () => ShowTextModal("New Map", "Create", n =>
            {
                if (string.IsNullOrWhiteSpace(n)) { Status("Enter a map name", false); return; }
                if (Designer.NewMap(n))
                {
                    mapDrop.choices = Designer.ListMaps();
                    mapDrop.SetValueWithoutNotify(n);
                    Status($"Created “{n}” ✓", true);
                }
                else Status("Create failed (see Console)", false);
            }));
            var loadBtn = MakeButton("Load", () =>
            {
                if (string.IsNullOrWhiteSpace(mapDrop.value)) { Status("Pick a map to load", false); return; }
                if (Designer.LoadMap(mapDrop.value)) Status($"Loaded “{mapDrop.value}”", true);
                else Status(Designer.IsDirty ? "Unsaved changes — save first" : "Load failed", false);
            });
            var saveBtn = MakeButton("Save", () =>
            {
                if (string.IsNullOrWhiteSpace(mapDrop.value)) { Status("Pick or create a map first", false); return; }
                if (Designer.SaveMap(mapDrop.value))
                {
                    Status($"Saved “{mapDrop.value}” ✓", true);
                    mapDrop.choices = Designer.ListMaps();
                }
                else Status("Save failed (see Console)", false);
            });
            newBtn.style.marginRight = 6; loadBtn.style.marginRight = 6;
            mapRow.Add(newBtn); mapRow.Add(loadBtn); mapRow.Add(saveBtn);
            mapRow.style.marginBottom = 6;
            body.Add(mapRow);
            body.Add(status);

            // ---- CAMERA ----
            body.Add(Divider());
            body.Add(SectionLabel("CAMERA"));
            body.Add(SliderRow("Speed", () => Designer.CameraSpeed,
                v => Designer.CameraSpeed = v, 5f, 1000f, "0"));

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
