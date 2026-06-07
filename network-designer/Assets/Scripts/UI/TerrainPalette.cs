// In-game terrain palette (UI Toolkit). The single home for terrain authoring on BOTH
// backends, switched by a Low-Poly / DEM toggle at the top:
//   • Low-Poly  — the chunked-mesh sculpt terrain: brush, grid, water, heightmap, generator.
//   • DEM       — the real-world Unity-Terrain world: build, surface/textures, sculpt, grass,
//                 lighting, markers, route, drop-to-surface.
// Only the truly system-level bits (map save/load, camera) stay in SystemPalette. Shared
// plumbing is in PaletteBase; this applies the same side-effects the live-tuning setters do.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class TerrainPalette : PaletteBase
    {
        public override string PaletteId => "Terrain";
        public override string MenuLabel => "N";
        protected override string Title => "Terrain";
        protected override Color Accent => new Color(0.52f, 0.76f, 0.46f);   // terrain green
        protected override float PanelWidth => 320f;

        // Opening Terrain returns the cursor to the sculpt brush (exits rail/scatter).
        protected override void OnOpened() => Designer.EnterSculptMode();

        int _backend = 1;   // 0 = Low-Poly, 1 = DEM (the current focus)

        protected override void BuildBody(VisualElement body)
        {
            // ---- BACKEND TOGGLE ----
            var lowBox = new VisualElement();
            var demBox = new VisualElement();
            void SetBackend(int b)
            {
                _backend = b;
                Designer.SculptDem = b == 1;   // the shared brush sculpts the DEM in DEM mode
                lowBox.style.display = b == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                demBox.style.display = b == 1 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            var toggleRow = HBox();
            var lowBtn = MakeButton("Low-Poly", () => SetBackend(0));
            var demBtn = MakeButton("DEM", () => SetBackend(1));
            lowBtn.style.marginRight = 6;
            toggleRow.Add(lowBtn); toggleRow.Add(demBtn);
            toggleRow.style.marginBottom = 10;
            body.Add(toggleRow);
            _sync.Add(() => { StyleActive(lowBtn, _backend == 0); StyleActive(demBtn, _backend == 1); });

            // Shared sculpt brush — acts on whichever backend the toggle selects.
            BuildBrush(body);

            body.Add(lowBox);
            body.Add(demBox);
            BuildLowPoly(lowBox);
            BuildDem(demBox);
            SetBackend(_backend);
        }

        // ───────────────────────── Shared sculpt brush (both backends) ───────────────────
        void BuildBrush(VisualElement body)
        {
            body.Add(SectionLabel("BRUSH"));
            AddBrushButton(body, "Raise (1)",   TerrainDesigner.BrushMode.Raise);
            AddBrushButton(body, "Lower (2)",   TerrainDesigner.BrushMode.Lower);
            AddBrushButton(body, "Smooth (3)",  TerrainDesigner.BrushMode.Smooth);
            AddBrushButton(body, "Flatten (4)", TerrainDesigner.BrushMode.Flatten);
            AddBrushButton(body, "Slope (5)",   TerrainDesigner.BrushMode.Slope);   // low-poly only

            body.Add(SliderRow("Radius", () => Designer.BrushRadius, v => Designer.BrushRadius = v,
                0.5f, Mathf.Max(1f, Designer.MaxBrushRadius), "0"));
            body.Add(SliderRow("Strength", () => Designer.BrushStrength, v => Designer.BrushStrength = v, 0f, 100f, "0"));
            body.Add(SliderRow("Falloff", () => Designer.BrushFalloff, v => Designer.BrushFalloff = v, 0f, 1f, "0.00"));
            body.Add(Divider());
        }

        // ───────────────────────── Low-poly chunked-mesh terrain ─────────────────────────
        void BuildLowPoly(VisualElement body)
        {
            // ---- GRID ---- (changes need ApplyTerrainMaterial to hit the shader)
            body.Add(Divider());
            body.Add(SectionLabel("GRID"));
            body.Add(SliderRow("Strength", () => Designer.GridStrength,
                v => { Designer.GridStrength = v; Designer.ApplyTerrainMaterial(); }, 0f, 1f, "0.00"));
            body.Add(SliderRow("Line", () => Designer.GridLineWidth,
                v => { Designer.GridLineWidth = v; Designer.ApplyTerrainMaterial(); }, 0.5f, 4f, "0.0"));
            body.Add(NumberRow("Spacing", "m", () => Designer.GridSpacing,
                v => { Designer.GridSpacing = v; Designer.ApplyTerrainMaterial(); }, 1f, 100f, "0.#"));
            body.Add(NumberRow("Major", "", () => Designer.GridMajorEvery,
                v => { Designer.GridMajorEvery = v; Designer.ApplyTerrainMaterial(); }, 1f, 100f, "0"));

            // ---- WATER ---- (changes need ApplyWater)
            body.Add(Divider());
            body.Add(SectionLabel("WATER"));
            body.Add(ToggleRow("Show Water",
                () => Designer.ShowWater, v => { Designer.ShowWater = v; Designer.ApplyWater(); }));
            body.Add(SliderRow("Level", () => Designer.WaterLevel,
                v => { Designer.WaterLevel = v; Designer.ApplyWater(); }, -50f, 300f, "0.0"));
            body.Add(SliderRow("Alpha", () => Designer.WaterColor.a,
                v => { Color c = Designer.WaterColor; c.a = v; Designer.WaterColor = c; Designer.ApplyWater(); }, 0.05f, 1f, "0.00"));
            body.Add(SliderRow("Smooth", () => Designer.WaterSmoothness,
                v => { Designer.WaterSmoothness = v; Designer.ApplyWater(); }, 0f, 1f, "0.00"));

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
            body.Add(MakeButton("Terrain Generator…", () => OpenGeneratorModal()));
        }

        // ───────────────────────── DEM real-world Unity Terrain ──────────────────────────
        void BuildDem(VisualElement body)
        {
            body.Add(SectionLabel("DEM WORLD (Unity Terrain)"));
            // Pick a city → it loads/builds that DEM. (Save/manage comes later.) -500..9000m
            // covers all land; tile size is auto-derived from the filename lat/lon.
            const float demTile = 10000f, demFrom = -500f, demTo = 9000f, demLod = 5f;
            int demMode = 1;   // 0 = Albedo, 1 = Flat green (default), 2 = Slope textures
            void ApplyDemSurface()
            {
                if (demMode == 2) DemTerrainWorld.SetTextured("Grass_Layer", "Cliff_Layer", "RockyGround_Layer", "Rock_Layer", 22f, 38f, 30f);
                else DemTerrainWorld.SetGreen(demMode == 1);
            }

            var cityRow = HBox(); cityRow.style.marginBottom = 6;
            var cityLbl = new Label("City"); cityLbl.style.color = Ink; cityLbl.style.minWidth = 40; cityLbl.style.flexShrink = 0;
            var demCity = new DropdownField { choices = DemTerrainWorld.ListWorlds() };   // unselected → pick to load
            demCity.style.flexGrow = 1; demCity.style.flexShrink = 1; demCity.style.minWidth = 0;
            demCity.RegisterValueChangedCallback(_ =>
            {
                DemTerrainWorld.Build(demCity.value, demTile, demFrom, demTo);
                ApplyDemSurface();
                DemTerrainWorld.SetTerrainLod(demLod);
            });
            cityRow.Add(cityLbl); cityRow.Add(demCity);
            body.Add(cityRow);

            var surfRow = HBox(); surfRow.style.marginBottom = 6;
            var surfLbl = new Label("Surface"); surfLbl.style.color = Ink; surfLbl.style.minWidth = 56; surfLbl.style.flexShrink = 0;
            var surfDd = new DropdownField { choices = new List<string> { "Albedo", "Flat green", "Slope textures" } };
            surfDd.index = 1;   // Flat green
            surfDd.style.flexGrow = 1; surfDd.style.flexShrink = 1; surfDd.style.minWidth = 0;
            surfDd.RegisterValueChangedCallback(_ => { demMode = surfDd.index; ApplyDemSurface(); });
            surfRow.Add(surfLbl); surfRow.Add(surfDd);
            body.Add(surfRow);

            // ---- LIGHTING ----
            body.Add(Divider());
            body.Add(SectionLabel("LIGHTING"));
            var profiles = DemLighting.ListProfiles();
            int dbi = profiles.IndexOf(DemLighting.Custom); if (dbi < 0) dbi = profiles.Count > 0 ? 0 : -1;
            var ppDd = GroundDropdown(profiles, dbi);
            float sunInt = 1.1f;
            body.Add(GroundRow("Look", ppDd, () => { }));
            body.Add(SliderRow("Sun", () => sunInt, v => sunInt = v, 0.3f, 2.5f, "0.0"));
            var lightBtn = MakeButton("Apply Lighting",
                () => { if (ppDd.value != null) DemLighting.Apply(ppDd.value, sunInt); });
            lightBtn.style.marginTop = 6;
            body.Add(lightBtn);
            var lightClr = MakeButton("Clear Lighting", () => DemLighting.Clear());
            lightClr.style.marginTop = 6;
            body.Add(lightClr);
        }

        void AddBrushButton(VisualElement body, string label, TerrainDesigner.BrushMode mode)
        {
            // Picking a brush always lands you in sculpt mode (exits any rail/scatter).
            var b = MakeButton(label, () => { Designer.EnterSculptMode(); Designer.Brush = mode; });
            b.style.marginBottom = 6;
            body.Add(b);
            _sync.Add(() => StyleActive(b, Designer.Brush == mode));
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
        // preview; Generate writes the result to the (low-poly) terrain. ---
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
