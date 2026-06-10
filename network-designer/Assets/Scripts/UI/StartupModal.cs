// Startup picker (centred on screen): Continue the last game, load a saved game, or create a new
// game on a DEM region — regions shown as preview tiles (loads <region>/preview.png if present).
// Visible only until a game is loaded. Auto-spawns at runtime — no scene setup needed.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class StartupModal : PaletteBase
    {
        public override string PaletteId => "Startup";
        public override bool Toggleable => false;
        protected override bool ShouldShow() => !GameManager.HasActiveGame;
        protected override bool Centered => true;
        protected override bool ShowFooter => false;     // no mode/snap/grid footer on the picker
        protected override string Title => "Networks";
        protected override float PanelWidth => 480f;
        protected override Color Accent => new Color(0.52f, 0.76f, 0.46f);

        string _selectedRegion;
        readonly List<VisualElement> _tiles = new List<VisualElement>();
        static readonly Dictionary<string, Texture2D> _previewCache = new Dictionary<string, Texture2D>();
        bool _heldModal;   // whether we're currently holding the global modal ref-count

        // While the picker is visible, hold ModalOpen so typing a game name doesn't fire tool/brush
        // hotkeys and the world underneath stays inert.
        protected override void Update()
        {
            base.Update();
            bool shown = ShouldShow();
            if (shown != _heldModal)
            {
                if (shown) PushModal(); else PopModal();
                _heldModal = shown;
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (_heldModal) { PopModal(); _heldModal = false; }   // don't leak the ref-count
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindFirstObjectByType<StartupModal>() != null) return;
            var go = new GameObject("StartupModal (auto)");
            go.AddComponent<UIDocument>();
            go.AddComponent<StartupModal>();
        }

        protected override void BuildBody(VisualElement body)
        {
            // ---- CONTINUE ----
            string last = Designer.LastGame;
            if (Designer.HasGame(last))
            {
                var cont = MakeButton($"Continue \"{last}\"", () => Designer.LoadGame(last));
                cont.style.marginBottom = 12;
                cont.style.height = 40;
                body.Add(cont);
            }

            // ---- SAVED GAMES ----
            body.Add(Divider());
            body.Add(SectionLabel("SAVED GAMES"));
            var games = Designer.ListGames();
            if (games.Count == 0)
            {
                var none = new Label("  (none yet — create one below)");
                none.style.color = Sub; none.style.fontSize = 11;
                none.style.unityFontStyleAndWeight = FontStyle.Italic;
                none.style.marginBottom = 6;
                body.Add(none);
            }
            else
            {
                var gd = new DropdownField { choices = games, value = games[0] };
                gd.style.marginBottom = 6;
                body.Add(gd);
                var loadBtn = MakeButton("Load", () => { if (!string.IsNullOrWhiteSpace(gd.value)) Designer.LoadGame(gd.value); });
                loadBtn.style.height = 36;
                body.Add(loadBtn);
            }

            // ---- NEW GAME ----
            body.Add(Divider());
            body.Add(SectionLabel("NEW GAME"));

            var nameLbl = new Label("Name");
            nameLbl.style.color = Ink; nameLbl.style.marginBottom = 2;
            body.Add(nameLbl);
            var nameField = new TextField { value = "" };
            nameField.style.marginBottom = 12;
            body.Add(nameField);

            var regionLbl = new Label("Region");
            regionLbl.style.color = Ink; regionLbl.style.marginBottom = 4;
            body.Add(regionLbl);

            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.marginBottom = 10;
            var regions = Designer.ListDemSets();
            foreach (var r in regions) grid.Add(BuildRegionTile(r));
            body.Add(grid);

            var status = new Label();
            status.style.color = new Color(0.95f, 0.6f, 0.5f); status.style.fontSize = 11;
            status.style.marginBottom = 4;

            var createBtn = MakeButton("Create & Play", () =>
            {
                string n = nameField.value?.Trim();
                if (string.IsNullOrEmpty(n)) { status.text = "Enter a name."; return; }
                if (string.IsNullOrEmpty(_selectedRegion)) { status.text = "Pick a region."; return; }
                if (Designer.HasGame(n)) { status.text = "A game by that name already exists."; return; }
                // Use the region's own data-derived range (from its manifest, written at download) so we
                // don't fall back to the hardcoded defaults and need manual NormMin/Max tweaking.
                float rmn = Designer.DefaultNormMin, rmx = Designer.DefaultNormMax;
                var ri = GameManager.Read(_selectedRegion);
                if (ri != null && ri.NormMax > ri.NormMin) { rmn = ri.NormMin; rmx = ri.NormMax; }
                Designer.NewGame(n, _selectedRegion, rmn, rmx);
            });
            createBtn.style.height = 40;
            body.Add(createBtn);
            body.Add(status);

            BuildDownloadSection(body);
        }

        // Entry point: a button that opens the full-screen "Download terrain" modal.
        void BuildDownloadSection(VisualElement body)
        {
            body.Add(Divider());
            body.Add(SectionLabel("DOWNLOAD A REGION (real terrain)"));
            var btn = MakeButton("Download terrain…", OpenDownloadModal);
            btn.style.height = 38;
            body.Add(btn);

            var cacheBtn = MakeButton("Cache US basemap (offline)…", OpenPrefetchModal);
            cacheBtn.style.height = 28; cacheBtn.style.marginTop = 6;
            body.Add(cacheBtn);
        }

        // Pre-download the USGS topo basemap for the US (overview zooms) to the persistent disk cache,
        // so the picker map works offline. Deeper zoom still caches on demand as you pan.
        void OpenPrefetchModal()
        {
            var modal = BeginModal("Cache US basemap (offline)", 440f, 0f, out System.Action close);
            if (modal == null) return;
            const int zMin = 5, zMax = 10;
            int tiles = TileCache.EstimateTiles(zMin, zMax);
            double estMB = tiles * 0.025;                 // ~25 KB/tile
            long haveMB = TileCache.CacheSizeBytes() / 1_000_000;

            var info = new Label($"USGS The National Map topo (public domain), zoom {zMin}–{zMax} over the US:\n"
                               + $"~{tiles:n0} tiles ≈ {estMB:0} MB · a long one-time download.\n"
                               + $"Browses the US offline; deeper zoom caches as you pan.\n"
                               + $"Already cached: {haveMB} MB.");
            info.style.color = Ink; info.style.fontSize = 12; info.style.whiteSpace = WhiteSpace.Normal; info.style.marginBottom = 12;
            modal.Add(info);

            var bar = new VisualElement();
            bar.style.height = 14; bar.style.marginBottom = 6;
            bar.style.backgroundColor = TrackOff; Radius(bar, 4);
            var fill = new VisualElement();
            fill.style.height = 14; fill.style.width = Length.Percent(0); fill.style.backgroundColor = Accent; Radius(fill, 4);
            bar.Add(fill); modal.Add(bar);

            var status = new Label(); status.style.color = Sub; status.style.fontSize = 11;
            status.style.whiteSpace = WhiteSpace.Normal; status.style.marginBottom = 12; modal.Add(status);

            bool running = false;
            var row = HBox(); row.style.justifyContent = Justify.FlexEnd;
            var stopBtn = MakeButton("Stop", () => { TileCache.CancelPrefetch(); status.text = "Stopping…"; });
            var closeBtn = MakeButton("Close", () => close());     // leaves any download running in the background
            Button startBtn = null;
            startBtn = MakeButton("Start", () =>
            {
                if (running) return;
                running = true; startBtn.SetEnabled(false); status.text = "Starting…";
                TileCache.PrefetchUS(zMin, zMax,
                    (p, msg) => { fill.style.width = Length.Percent(Mathf.Clamp01(p) * 100f); status.text = msg; },
                    (ok, msg) => { running = false; startBtn.SetEnabled(true); status.text = msg; });
            });
            foreach (var b in new[] { stopBtn, closeBtn, startBtn }) { b.style.flexGrow = 0; b.style.width = 92; b.style.marginLeft = 6; }
            StyleActive(startBtn, true);
            row.Add(stopBtn); row.Add(closeBtn); row.Add(startBtn);
            modal.Add(row);
        }

        const double AstoriaLat = 46.18, AstoriaLon = -123.83;
        const double DefaultSizeKm = 3.0;

        // Full-screen modal modelled on the Leaflet/Esri picker: a large interactive map (drag to
        // reposition, drag a corner handle to resize the area in 1 km steps, wheel/± to zoom) + a left
        // panel with the map name, size (km, synced to the box), area/download-size/est-time info, a
        // Download button and a progress bar. 1 m via USGS 3DEP (Dem3DEP). Success: close + rebuild.
        void OpenDownloadModal()
        {
            float modalW = Mathf.Max(640f, Screen.width - 80f);
            float modalH = Mathf.Max(460f, Screen.height - 80f);
            var modal = BeginModal("Download terrain", modalW, modalH, out System.Action close, closeOnBackdropClick: false);
            if (modal == null) return;

            float leftW = 230f;
            int mapW = Mathf.Clamp(Mathf.RoundToInt(modalW - leftW - 64f), 256, 4096);
            int mapH = Mathf.Clamp(Mathf.RoundToInt(modalH - 120f), 256, 3000);

            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var fl = System.Globalization.NumberStyles.Float;

            var rowBox = new VisualElement();
            rowBox.style.flexDirection = FlexDirection.Row; rowBox.style.flexGrow = 1;
            modal.Add(rowBox);

            var left = new VisualElement();
            left.style.width = leftW; left.style.marginRight = 16;
            rowBox.Add(left);

            var picker = new DemMapPicker(this, AstoriaLat, AstoriaLon, mapW, mapH, DefaultSizeKm);
            rowBox.Add(picker.Root);

            Label Cap(string t) { var l = new Label(t); l.style.color = Sub; l.style.fontSize = 11; l.style.marginBottom = 2; l.style.marginTop = 8; return l; }

            left.Add(Cap("Map name:"));
            var nameField = new TextField { value = "" };
            nameField.style.marginBottom = 4; left.Add(nameField);

            left.Add(Cap("Map size (km):"));
            var sizeField = new TextField { value = DefaultSizeKm.ToString("0", ci), isDelayed = true };
            sizeField.style.marginBottom = 4; left.Add(sizeField);

            var info = new Label(); info.style.color = Sub; info.style.fontSize = 12;
            info.style.whiteSpace = WhiteSpace.Normal; info.style.marginTop = 10; info.style.marginBottom = 14; left.Add(info);

            bool busy = false;

            var dlBtn = MakeButton("Download", () => { });
            dlBtn.style.height = 36; dlBtn.style.flexGrow = 0; left.Add(dlBtn);

            left.Add(Cap("Progress:"));
            var bar = new VisualElement();
            bar.style.height = 14; bar.style.marginBottom = 6;
            bar.style.backgroundColor = TrackOff; Radius(bar, 4);
            SetBorder(bar, 1, new Color(0.3f, 0.32f, 0.36f));
            var fill = new VisualElement();
            fill.style.height = 12; fill.style.width = Length.Percent(0); fill.style.backgroundColor = Accent; Radius(fill, 3);
            bar.Add(fill); left.Add(bar);

            var status = new Label(); status.style.color = Sub; status.style.fontSize = 11;
            status.style.whiteSpace = WhiteSpace.Normal; left.Add(status);

            var cancel = MakeButton("Cancel", () => close());
            cancel.style.height = 28; cancel.style.flexGrow = 0; cancel.style.marginTop = 10; left.Add(cancel);

            void UpdateInfo()
            {
                double areaKm = picker.AreaKm;
                NetworkDesigner.Terrain.Dem3DEP.Estimate(areaKm, out double sizeMB, out double secs, out long pxSide);
                info.text = $"Center {picker.CenterLat:0.0000}, {picker.CenterLon:0.0000}\n"
                          + $"{areaKm:0} × {areaKm:0} km  ({areaKm * areaKm:0} km²)\n"
                          + $"{pxSide} × {pxSide} px @ 1 m/px\n"
                          + $"download ≈ {sizeMB:0} MB · ~{secs:0} s";
                sizeField.SetValueWithoutNotify(areaKm.ToString("0", ci));
            }

            void DoDownload()
            {
                if (busy) return;
                string nm = nameField.value?.Trim();
                if (string.IsNullOrEmpty(nm)) { status.text = "Enter a map name."; return; }
                if (Designer.HasGame(nm)) { status.text = "A map by that name already exists."; return; }
                busy = true; dlBtn.SetEnabled(false); cancel.SetEnabled(false);
                fill.style.width = Length.Percent(0); status.text = "Starting…";
                NetworkDesigner.Terrain.Dem3DEP.Start(nm, picker.CenterLat, picker.CenterLon, picker.AreaKm,
                    (p, msg) => { fill.style.width = Length.Percent(Mathf.Clamp01(p) * 100f); status.text = msg; },
                    (ok, msg) =>
                    {
                        if (ok) { close(); Rebuild(); }
                        else { busy = false; dlBtn.SetEnabled(true); cancel.SetEnabled(true); status.text = msg; }
                    });
            }

            dlBtn.clicked += DoDownload;
            picker.OnChanged = UpdateInfo;
            sizeField.RegisterValueChangedCallback(_ =>
            { if (double.TryParse(sizeField.value, fl, ci, out double km)) picker.SetAreaKm(km); });
            UpdateInfo();
        }

        VisualElement BuildRegionTile(string region)
        {
            var tile = new VisualElement();
            tile.style.width = 102; tile.style.height = 102;
            tile.style.marginRight = 8; tile.style.marginBottom = 8;
            tile.style.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
            Radius(tile, 8);
            SetBorder(tile, 2, new Color(0.32f, 0.35f, 0.40f));
            tile.style.justifyContent = Justify.FlexEnd;    // label hugs the bottom
            tile.style.overflow = Overflow.Hidden;

            var tex = LoadPreview(region);
            if (tex != null) tile.style.backgroundImage = new StyleBackground(tex);

            var lbl = new Label(region);
            lbl.style.color = Color.white; lbl.style.fontSize = 10;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.whiteSpace = WhiteSpace.Normal;
            lbl.style.unityTextAlign = TextAnchor.LowerCenter;
            lbl.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            lbl.style.paddingTop = 2; lbl.style.paddingBottom = 2;
            tile.Add(lbl);

            tile.RegisterCallback<ClickEvent>(_ => Select(region));
            _tiles.Add(tile);
            tile.userData = region;
            return tile;
        }

        void Select(string region)
        {
            _selectedRegion = region;
            foreach (var t in _tiles)
                SetBorder(t, (string)t.userData == region ? 3 : 2,
                          (string)t.userData == region ? Accent : new Color(0.32f, 0.35f, 0.40f));
        }

        static void SetBorder(VisualElement e, float w, Color c)
        {
            e.style.borderTopWidth = w; e.style.borderBottomWidth = w;
            e.style.borderLeftWidth = w; e.style.borderRightWidth = w;
            e.style.borderTopColor = c; e.style.borderBottomColor = c;
            e.style.borderLeftColor = c; e.style.borderRightColor = c;
        }

        // Region thumbnail: <region folder>/preview.png (drop a screenshot there). Cached; null if absent.
        static Texture2D LoadPreview(string region)
        {
            if (_previewCache.TryGetValue(region, out var cached)) return cached;
            Texture2D tex = null;
            try
            {
                string path = Path.Combine(Application.dataPath, "Heightmaps", "Highres", region, "preview.png");
                if (File.Exists(path))
                {
                    tex = new Texture2D(2, 2);
                    if (!tex.LoadImage(File.ReadAllBytes(path))) { Object.Destroy(tex); tex = null; }
                }
            }
            catch { tex = null; }
            _previewCache[region] = tex;
            return tex;
        }
    }
}
