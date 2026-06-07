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
                v => Designer.CameraSpeed = v, 5f, 50000f, "0"));

            // ---- AUTOSAVE ---- (snapshots the scene a debounce after each edit; the snapshot
            // touches the terrain + every placed tree on the main thread, so on big scatter
            // worlds it hitches — turn it off or raise the debounce to avoid the periodic lag.)
            body.Add(Divider());
            body.Add(SectionLabel("AUTOSAVE"));
            body.Add(ToggleRow("Autosave", () => Designer.Autosave, v => Designer.Autosave = v));
            body.Add(NumberRow("Debounce", "s", () => Designer.AutosaveDebounceSeconds,
                v => Designer.AutosaveDebounceSeconds = Mathf.Max(0.5f, v), 0.5f, 60f, "0.#"));
        }
    }
}
