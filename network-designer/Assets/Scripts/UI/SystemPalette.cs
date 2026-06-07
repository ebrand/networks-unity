// System palette (UI Toolkit): named map save/load + heightmap import + autosave — the
// heavier, less-frequent "system" operations split out of the Terrain Operations palette so that
// one isn't absurdly tall. Opened (exclusively) from the launcher; renders top-left like
// the other content palettes, with the shared footer. Shared plumbing is in PaletteBase.

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
                v => Designer.CameraSpeed = v, 5f, 50000f, "0"));

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

            // ---- GENERATE (procedural terrain) ----
            body.Add(Divider());
            body.Add(SectionLabel("GENERATE"));
            var genBtn = MakeButton("Terrain Generator…", () => OpenGeneratorModal());
            body.Add(genBtn);

            // ---- DEM WORLD (Unity Terrain, render-only first step) ----
            body.Add(Divider());
            body.Add(SectionLabel("DEM WORLD (Unity Terrain)"));
            // City picker: a fixed-width "City" label + a dropdown that fills the rest and
            // shrinks to the panel width (no inline field-label, which overflows).
            var cities = DemTerrainWorld.ListWorlds();
            var cityRow = HBox(); cityRow.style.marginBottom = 6;
            var cityLbl = new Label("City"); cityLbl.style.color = Ink; cityLbl.style.minWidth = 40; cityLbl.style.flexShrink = 0;
            var demCity = new DropdownField { choices = cities };
            if (cities.Count > 0) demCity.SetValueWithoutNotify(cities[0]);
            demCity.style.flexGrow = 1; demCity.style.flexShrink = 1; demCity.style.minWidth = 0;
            cityRow.Add(cityLbl); cityRow.Add(demCity);
            body.Add(cityRow);
            // Fixed elevation range for ALL cities (export every DEM with -500..9000m;
            // covers all land on Earth at ~0.14m precision). Tile size is auto-derived from
            // the filename lat/lon, so demTile is only a fallback. Norm boxes removed.
            const float demTile = 10000f, demFrom = -500f, demTo = 9000f;
            // Slope-texture pickers: which TerrainLayer for flat ground vs steep rock/cliff.
            var layerNames = DemTerrainWorld.ListTerrainLayers();
            int fi = layerNames.IndexOf("Grass_Layer"); if (fi < 0) fi = layerNames.Count > 0 ? 0 : -1;
            int si = layerNames.IndexOf("Cliff_Layer");
            if (si < 0) si = layerNames.IndexOf("Rock_Layer");
            if (si < 0) si = layerNames.Count > 1 ? 1 : 0;
            var flatDd = GroundDropdown(layerNames, fi);
            var steepDd = GroundDropdown(layerNames, si);
            // Surface mode: 0 = satellite albedo, 1 = flat green, 2 = slope textures.
            int demMode = 0;
            float demTexSize = 25f;   // texture repeat in metres (slope mode)
            float demLod = 5f;        // heightmap pixel error (lower = LOD detail farther out)
            void ApplyDemSurface()
            {
                if (demMode == 2) DemTerrainWorld.SetTextured(flatDd.value, steepDd.value, 22f, 38f, demTexSize);
                else DemTerrainWorld.SetGreen(demMode == 1);
            }
            var surfRow = HBox(); surfRow.style.marginBottom = 6;
            var surfLbl = new Label("Surface"); surfLbl.style.color = Ink; surfLbl.style.minWidth = 56; surfLbl.style.flexShrink = 0;
            var surfDd = new DropdownField { choices = new List<string> { "Albedo", "Flat green", "Slope textures" } };
            surfDd.index = 0;
            surfDd.style.flexGrow = 1; surfDd.style.flexShrink = 1; surfDd.style.minWidth = 0;
            surfDd.RegisterValueChangedCallback(_ => { demMode = surfDd.index; ApplyDemSurface(); });
            surfRow.Add(surfLbl); surfRow.Add(surfDd);
            body.Add(surfRow);
            // Flat/Steep variant rows (only matter in Slope mode); changing re-applies live.
            body.Add(GroundRow("Flat", flatDd, () => { if (demMode == 2) ApplyDemSurface(); }));
            body.Add(GroundRow("Steep", steepDd, () => { if (demMode == 2) ApplyDemSurface(); }));
            // Texture repeat (m/tile) — live, no slope recompute.
            body.Add(SliderRow("Tex Size", () => demTexSize,
                v => { demTexSize = v; if (demMode == 2) DemTerrainWorld.SetTextureTiling(v); }, 2f, 200f, "0"));
            // Geometry LOD: lower = terrain mesh stays detailed farther (costs FPS). Live.
            body.Add(SliderRow("LOD Err", () => demLod,
                v => { demLod = v; DemTerrainWorld.SetTerrainLod(v); }, 1f, 30f, "0.#"));
            var demBuild = MakeButton("Build DEM World", () =>
            {
                DemTerrainWorld.Build(demCity.value, demTile, demFrom, demTo);
                ApplyDemSurface();   // honor the chosen surface mode on (re)build
                DemTerrainWorld.SetTerrainLod(demLod);   // honor the LOD setting on (re)build
            });
            demBuild.style.marginTop = 4;
            body.Add(demBuild);
            var demClear = MakeButton("Clear DEM World", () => DemTerrainWorld.Clear());
            demClear.style.marginTop = 6;
            body.Add(demClear);
            // Scale-check: drop known-size markers (1.8m person → 1km pole) on the surface
            // below the camera. Descend to ground level to compare against the landscape.
            var markers = MakeButton("Drop Scale Markers", () => ScaleMarkers.Drop());
            markers.style.marginTop = 10;
            body.Add(markers);
            var markersClr = MakeButton("Clear Markers", () => ScaleMarkers.Clear());
            markersClr.style.marginTop = 6;
            body.Add(markersClr);
            // Route tool: left-click drops points on the terrain, right-click removes the
            // last; the draped length shows in the top-right HUD.
            body.Add(ToggleRow("Route Tool (L/R click)", () => TerrainRouteTool.Active,
                v => TerrainRouteTool.Active = v));
            var routeClr = MakeButton("Clear Route", () => TerrainRouteTool.Clear());
            routeClr.style.marginTop = 6;
            body.Add(routeClr);
        }

        // A ground-variant dropdown (full-width, shrinkable) preset to an index.
        DropdownField GroundDropdown(List<string> choices, int index)
        {
            var dd = new DropdownField { choices = choices };
            if (index >= 0 && index < choices.Count) dd.SetValueWithoutNotify(choices[index]);
            dd.style.flexGrow = 1; dd.style.flexShrink = 1; dd.style.minWidth = 0;
            return dd;
        }

        // A labelled row wrapping a ground dropdown; onChange fires when the value changes.
        VisualElement GroundRow(string label, DropdownField dd, System.Action onChange)
        {
            var row = HBox(); row.style.marginBottom = 4;
            var l = new Label(label); l.style.color = Sub; l.style.minWidth = 56; l.style.flexShrink = 0;
            dd.RegisterValueChangedCallback(_ => onChange());
            row.Add(l); row.Add(dd);
            return row;
        }

        // --- Terrain Generator modal: style + seed + params with a live hill-shaded
        // preview; Generate writes the result to the real terrain. ---
        static readonly string[] StyleNames = { "Rolling Hills", "Mountains", "Islands", "Plateaus & Canyons" };
        static TerrainStyle StyleFromName(string n)
        {
            int i = System.Array.IndexOf(StyleNames, n);
            return (TerrainStyle)Mathf.Clamp(i, 0, StyleNames.Length - 1);
        }

        void OpenGeneratorModal()
        {
            var s = new TerrainGenSettings();
            float hgt = Mathf.Min(900f, Screen.height - 40f);
            var modal = BeginModal("Terrain Generator", 560f, hgt, out System.Action close);
            if (modal == null) return;
            int syncMark = _sync.Count;

            float span = Mathf.Max(50f, Designer.TerrainSizeMeters);
            var previewTex = new Texture2D(160, 160, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };

            var preview = new VisualElement();
            preview.style.height = 240; preview.style.flexShrink = 0; preview.style.marginBottom = 10;
            preview.style.backgroundColor = DarkInk; Radius(preview, 8);
            preview.style.overflow = Overflow.Hidden;
            var previewImg = new Image { scaleMode = ScaleMode.ScaleToFit, image = previewTex };
            previewImg.style.flexGrow = 1;
            preview.Add(previewImg);
            modal.Add(preview);

            void UpdatePreview()
            {
                TerrainGenerator.RenderPreviewInto(previewTex, s, span, span);
                previewImg.MarkDirtyRepaint();
            }

            modal.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (_sync.Count > syncMark) _sync.RemoveRange(syncMark, _sync.Count - syncMark);
                UnityEngine.Object.Destroy(previewTex);
            });

            // Style
            modal.Add(SectionLabel("STYLE"));
            var styleDd = new DropdownField { choices = new List<string>(StyleNames) };
            styleDd.SetValueWithoutNotify(StyleNames[(int)s.Style]);
            styleDd.RegisterValueChangedCallback(e => { s.Style = StyleFromName(e.newValue); UpdatePreview(); });
            styleDd.style.marginBottom = 8;
            modal.Add(styleDd);

            // Seed + Randomize
            var seedRow = HBox();
            var seedNum = NumberRow("Seed", "", () => s.Seed,
                v => { s.Seed = Mathf.RoundToInt(v); UpdatePreview(); }, 0f, 999999f, "0");
            seedNum.style.flexGrow = 1;
            var randBtn = MakeButton("Randomize", () => { s.Seed = UnityEngine.Random.Range(0, 1000000); UpdatePreview(); });
            randBtn.style.flexGrow = 0; randBtn.style.width = 110; randBtn.style.marginLeft = 6;
            seedRow.Add(seedNum); seedRow.Add(randBtn);
            modal.Add(seedRow);

            // Params — each setter re-renders the preview.
            modal.Add(SliderRow("Feature Sz", () => s.FeatureScale, v => { s.FeatureScale = v; UpdatePreview(); }, 100f, 3000f, "0"));
            modal.Add(SliderRow("Max Height", () => s.MaxHeight, v => { s.MaxHeight = v; UpdatePreview(); }, 10f, 1000f, "0"));
            modal.Add(SliderRow("Roughness", () => s.Persistence, v => { s.Persistence = v; UpdatePreview(); }, 0.2f, 0.75f, "0.00"));
            modal.Add(SliderRow("Detail", () => s.Octaves, v => { s.Octaves = Mathf.RoundToInt(v); UpdatePreview(); }, 1f, 9f, "0"));
            modal.Add(SliderRow("Sea Level", () => s.SeaLevel, v => { s.SeaLevel = v; UpdatePreview(); }, -100f, 200f, "0"));

            // Rivers
            modal.Add(ToggleRow("Rivers", () => s.Rivers, v => { s.Rivers = v; UpdatePreview(); }));
            modal.Add(SliderRow("River Dens", () => s.RiverDensity, v => { s.RiverDensity = v; UpdatePreview(); }, 0f, 1f, "0.00"));
            modal.Add(SliderRow("Carve", () => s.RiverCarve, v => { s.RiverCarve = v; UpdatePreview(); }, 0f, 40f, "0"));

            // Generate / Close
            var btnRow = HBox(); btnRow.style.justifyContent = Justify.FlexEnd; btnRow.style.marginTop = 12;
            var gen = MakeButton("Generate", () => Designer.GenerateTerrain(s.Clone()));
            gen.style.flexGrow = 0; gen.style.width = 130; gen.style.marginRight = 6;
            StyleActive(gen, true);
            var closeBtn = MakeButton("Close", () => close());
            closeBtn.style.flexGrow = 0; closeBtn.style.width = 100;
            btnRow.Add(gen); btnRow.Add(closeBtn);
            modal.Add(btnRow);

            UpdatePreview();
        }
    }
}
