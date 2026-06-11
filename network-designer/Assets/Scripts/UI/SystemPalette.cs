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

        // Opening System exits any rail/scatter mode. Rebuild the body too: it's first built at Start (before
        // any world is active), so without this the WORLD section + thumbnail would never appear once one is.
        protected override void OnOpened() { Designer.EnterSculptMode(); Rebuild(); }

        protected override void BuildBody(VisualElement body)
        {
            // ---- WORLD ---- (the active world: a thumbnail of its downloaded extent + grow it with more
            // map areas. Fine-grained chunk/topo/grid tunables now live in the React tuning app.)
            string world = GameManager.ActiveGame;
            if (!string.IsNullOrEmpty(world) && WorldManager.Exists(world))
            {
                body.Add(SectionLabel("WORLD"));

                var thumb = new VisualElement();
                thumb.style.height = 150; thumb.style.marginBottom = 8;
                thumb.style.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
                thumb.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
                Radius(thumb, 8);
                var tex = WorldThumbnail.Load(world);
                if (tex != null) thumb.style.backgroundImage = new StyleBackground(tex);
                body.Add(thumb);

                // Opens the shared download picker for THIS world (snaps to its lattice). On close, if any
                // area was downloaded, save + reload the world so the new tiles stream in live (no menu trip).
                var addBtn = MakeButton("Add geographic area…", () => OpenDownloadModal(world, didDownload =>
                {
                    if (didDownload) { Designer.SaveNow(); Designer.LoadGame(world); }
                    Rebuild();
                }));
                addBtn.style.marginBottom = 4; body.Add(addBtn);

                // Full-screen top-down map to trim empty/ocean chunks out of the streamed set (DEM worlds).
                if (Designer.ChunkDemActive)
                    body.Add(MakeButton("Trim map (empty chunks)…", () => ChunkMapEditor.Toggle()));

                body.Add(Divider());
            }

            // ---- CAMERA ---- (Overview = fast-travel preset: bumps fly speed + zoom step for crossing a
            // whole world; off returns to fine values. Manual tunables are in the React tuning app.)
            body.Add(SectionLabel("CAMERA"));
            body.Add(ToggleRow("Overview (fast travel)", () => Designer.CameraOverview, v => Designer.CameraOverview = v));

            // ---- AUTOSAVE ---- (snapshots the scene a debounce after each edit; the snapshot touches the
            // terrain + every placed tree on the main thread, so on big scatter worlds it hitches — turn it
            // off or raise the debounce to avoid the periodic lag.)
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
