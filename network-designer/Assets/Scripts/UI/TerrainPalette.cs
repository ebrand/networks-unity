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
        protected override void OnOpened()
        {
            Designer.EnterSculptMode();
            // DEM-only palette: if a DEM world is built but we somehow aren't on it, make
            // it the active backend (low-poly is kept in code but no longer offered here).
            if (DemTerrainWorld.HasWorld && !Designer.DemBackend)
            {
                Designer.SetActiveBackend(true);
                DemTerrainWorld.FrameCamera();
            }
        }

        protected override void BuildBody(VisualElement body)
        {
            // DEM is the only terrain the palette offers now. The low-poly backend and its
            // controls (BuildLowPoly) are kept in code — just not exposed here. The shared
            // sculpt brush acts on the DEM; the DEM world controls follow.
            BuildBrush(body);
            BuildDem(body);
        }

        // ───────────────────────── Shared sculpt brush (both backends) ───────────────────
        void BuildBrush(VisualElement body)
        {
            body.Add(SectionLabel("BRUSH"));
            AddBrushButton(body, "Raise (1)",   TerrainDesigner.BrushMode.Raise);
            AddBrushButton(body, "Lower (2)",   TerrainDesigner.BrushMode.Lower);
            AddBrushButton(body, "Smooth (3)",  TerrainDesigner.BrushMode.Smooth);
            AddBrushButton(body, "Flatten (4)", TerrainDesigner.BrushMode.Flatten);
            AddBrushButton(body, "Slope (5)",   TerrainDesigner.BrushMode.Slope);   // two-click ramp
            AddBrushButton(body, "Sea (6)",     TerrainDesigner.BrushMode.Sea);     // click-to-flood lower (chunk world)
            AddBrushButton(body, "Measure (7)", TerrainDesigner.BrushMode.Measure); // click A → click B, distance tooltip

            body.Add(SliderRow("Radius", () => Designer.BrushRadius, v => Designer.BrushRadius = v,
                0.5f, Mathf.Max(1f, Designer.MaxBrushRadius), "0"));
            body.Add(SliderRow("Strength", () => Designer.BrushStrength, v => Designer.BrushStrength = v, 0f, 100f, "0"));
            // Edge feather for Slope (5) + Grade Corridor: 0 = flatten the WHOLE swath flat,
            // 1 = only the centreline, smoothly feathered to the edge. (Freehand brushes keep
            // their own built-in falloff.)
            body.Add(SliderRow("Falloff", () => Designer.BrushFalloff, v => Designer.BrushFalloff = v, 0f, 1f, "0.00"));
            body.Add(Divider());
        }

        // ───────────────────────── Low-poly chunked-mesh terrain ─────────────────────────
        void BuildLowPoly(VisualElement body)
        {
            // ---- HEIGHTMAP ----
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

            // ---- GRID ---- (changes need ApplyTerrainMaterial to hit the shader)
            body.Add(Divider());
            body.Add(SectionLabel("GRID"));
            body.Add(SliderRow("Strength", () => Designer.GridStrength,
                v => { Designer.GridStrength = v; Designer.ApplyTerrainMaterial(); }, 0f, 1f, "0.00"));
            body.Add(SliderRow("Line", () => Designer.GridLineWidth,
                v => { Designer.GridLineWidth = v; Designer.ApplyTerrainMaterial(); }, 0.5f, 4f, "0.0"));

            // ---- WATER ---- (changes need ApplyWater)
            body.Add(Divider());
            body.Add(SectionLabel("WATER"));
            // In the streaming chunk world these drive the CHUNK water plane; otherwise the low-poly water.
            body.Add(ToggleRow("Show Water",
                () => Designer.ChunkTestActive ? Designer.ChunkShowWater : Designer.ShowWater,
                v => { if (Designer.ChunkTestActive) Designer.ChunkShowWater = v; else { Designer.ShowWater = v; Designer.ApplyWater(); } }));
            body.Add(SliderRow("Level",
                () => Designer.ChunkTestActive ? Designer.ChunkWaterLevel : Designer.WaterLevel,
                v => { if (Designer.ChunkTestActive) Designer.ChunkWaterLevel = v; else { Designer.WaterLevel = v; Designer.ApplyWater(); } }, -50f, 1000f, "0"));
        }

        // ───────────────────────── DEM real-world Unity Terrain ──────────────────────────
        void BuildDem(VisualElement body)
        {
            body.Add(SectionLabel("DEM WORLD (Unity Terrain)"));
            // Pick a city → it loads/builds that DEM. (Save/manage comes later.) -500..9000m
            // covers all land; tile size is auto-derived from the filename lat/lon.
            const float demTile = 10000f, demFrom = -500f, demTo = 9000f;  // LOD lives in Designer.DemLodPixelError (tunable)
            // Surface is fixed to Flat green (Albedo / Slope-texture modes retired from the UI).
            void ApplyDemSurface() => DemTerrainWorld.SetGreen(true);

            var cityRow = HBox(); cityRow.style.marginBottom = 6;
            var cityLbl = new Label("City"); cityLbl.style.color = Ink; cityLbl.style.minWidth = 40; cityLbl.style.flexShrink = 0;
            var demCity = new DropdownField { choices = DemTerrainWorld.ListWorlds() };   // unselected → pick to load
            demCity.style.flexGrow = 1; demCity.style.flexShrink = 1; demCity.style.minWidth = 0;
            // Reflect the loaded/remembered city without re-triggering a load.
            if (!string.IsNullOrEmpty(Designer.LastDemCity)) demCity.SetValueWithoutNotify(Designer.LastDemCity);
            // Build blocks the main thread for a few seconds; run it from a coroutine so the
            // "Loading DEM…" overlay paints a frame BEFORE we block (otherwise no feedback).
            demCity.RegisterValueChangedCallback(_ =>
                StartCoroutine(LoadDem(demCity.value, demTile, demFrom, demTo, Designer.DemLodPixelError, ApplyDemSurface)));
            cityRow.Add(cityLbl); cityRow.Add(demCity);
            body.Add(cityRow);

            // ---- WATER ---- (a flat plane at a chosen elevation — floods coasts/valleys)
            body.Add(Divider());
            body.Add(SectionLabel("WATER"));
            // In the streaming chunk world these drive the CHUNK water plane; otherwise the DEM water.
            body.Add(ToggleRow("Show Water",
                () => Designer.ChunkTestActive ? Designer.ChunkShowWater : DemWater.Show,
                v => { if (Designer.ChunkTestActive) Designer.ChunkShowWater = v; else { DemWater.Show = v; DemWater.Apply(); } }));
            body.Add(SliderRow("Level",
                () => Designer.ChunkTestActive ? Designer.ChunkWaterLevel : DemWater.Level,
                v => { if (Designer.ChunkTestActive) Designer.ChunkWaterLevel = v; else { DemWater.Level = v; DemWater.Apply(); } }, -50f, 3000f, "0"));

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

        // Load a DEM world with a "Loading…" overlay: flag it, yield so the overlay paints,
        // THEN run the blocking Build (decode + 100 tiles), then settle surface/water/camera.
        System.Collections.IEnumerator LoadDem(string city, float tile, float from, float to, float lod, System.Action applySurface)
        {
            DemTerrainWorld.Building = true;
            yield return null;           // let the overlay render before we block the main thread
            yield return null;
            try
            {
                DemTerrainWorld.Build(city, tile, from, to);
                applySurface();
                DemTerrainWorld.SetTerrainLod(lod);
                DemWater.Apply();
                Designer.SetActiveBackend(true);
                DemTerrainWorld.FrameCamera();
            }
            finally { DemTerrainWorld.Building = false; }   // never leave the overlay stuck
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
