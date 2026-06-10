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
                Designer.NewGame(n, _selectedRegion, Designer.DefaultNormMin, Designer.DefaultNormMax);
            });
            createBtn.style.height = 40;
            body.Add(createBtn);
            body.Add(status);

            BuildDownloadSection(body);
        }

        // Download a NEW region straight from real-world coordinates (AWS Terrarium), write the DEM tiles
        // + manifest, then play it — no website round-trip. Live readout shows the EXACT ground extent.
        void BuildDownloadSection(VisualElement body)
        {
            body.Add(Divider());
            var hdr = new Label("DOWNLOAD A REGION (real terrain)");
            hdr.style.color = Ink; hdr.style.marginTop = 6; hdr.style.marginBottom = 4;
            body.Add(hdr);

            TextField Field(string label, string val)
            {
                var l = new Label(label); l.style.color = Sub; l.style.fontSize = 11; body.Add(l);
                var f = new TextField { value = val }; f.style.marginBottom = 6; body.Add(f);
                return f;
            }
            var dName = Field("Name", "");
            var dLat = Field("Center latitude", "46.18");
            var dLon = Field("Center longitude", "-123.83");
            var dZoom = Field("Zoom (1-15; higher = finer)", "13");
            var dTiles = Field("Tiles per side (1-8)", "3");

            var readout = new Label(); readout.style.color = Sub; readout.style.fontSize = 11;
            readout.style.whiteSpace = WhiteSpace.Normal; readout.style.marginBottom = 4; body.Add(readout);
            var dStatus = new Label(); dStatus.style.color = new Color(0.95f, 0.6f, 0.5f); dStatus.style.fontSize = 11;

            bool Parse(out double lat, out double lon, out int z, out int n)
            {
                lat = lon = 0; z = n = 0;
                var ci = System.Globalization.CultureInfo.InvariantCulture; var fl = System.Globalization.NumberStyles.Float;
                bool ok = double.TryParse(dLat.value, fl, ci, out lat) && double.TryParse(dLon.value, fl, ci, out lon)
                       && int.TryParse(dZoom.value, out z) && int.TryParse(dTiles.value, out n);
                z = Mathf.Clamp(z, 1, 15); n = Mathf.Clamp(n, 1, 8);
                return ok;
            }
            void Refresh()
            {
                if (!Parse(out double lat, out double lon, out int z, out int n)) { readout.text = "Enter valid numbers."; return; }
                NetworkDesigner.Terrain.DemDownloader.Describe(lat, lon, z, n,
                    out double wKm, out double hKm, out double mpp, out double N, out double S, out double W, out double E);
                readout.text = $"{wKm:0.0} × {hKm:0.0} km · {mpp:0.0} m/px · {n * 1024 + 1}px/side · {(n * 4 + 1) * (n * 4 + 1)} tiles to fetch\n"
                             + $"N {N:0.0000}  S {S:0.0000}  W {W:0.0000}  E {E:0.0000}";
            }
            dLat.RegisterValueChangedCallback(_ => Refresh());
            dLon.RegisterValueChangedCallback(_ => Refresh());
            dZoom.RegisterValueChangedCallback(_ => Refresh());
            dTiles.RegisterValueChangedCallback(_ => Refresh());
            Refresh();

            var dlBtn = MakeButton("Download & Play", () =>
            {
                string n0 = dName.value?.Trim();
                if (string.IsNullOrEmpty(n0)) { dStatus.text = "Enter a name."; return; }
                if (Designer.HasGame(n0)) { dStatus.text = "A game by that name exists."; return; }
                if (!Parse(out double lat, out double lon, out int z, out int n)) { dStatus.text = "Bad numbers."; return; }
                dStatus.text = "starting…";
                NetworkDesigner.Terrain.DemDownloader.Start(n0, lat, lon, z, n,
                    (p, msg) => dStatus.text = $"{(int)(p * 100)}% — {msg}",
                    (ok, msg) => { dStatus.text = ok ? $"done — {msg}" : $"failed — {msg}"; if (ok) Designer.LoadGame(n0); });
            });
            dlBtn.style.height = 36; dlBtn.style.marginTop = 4;
            body.Add(dlBtn);
            body.Add(dStatus);
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
