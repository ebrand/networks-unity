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
            NetworkDesigner.Terrain.WorldManager.EnsureWorldGames();

            // ---- SAVED WORLDS ----
            body.Add(SectionLabel("SAVED WORLDS"));
            var worlds = NetworkDesigner.Terrain.WorldManager.ListWorlds();
            if (worlds.Count == 0)
            {
                var none = new Label("  (none yet — create one below)");
                none.style.color = Sub; none.style.fontSize = 11;
                none.style.unityFontStyleAndWeight = FontStyle.Italic; none.style.marginBottom = 6;
                body.Add(none);
            }
            else
            {
                var wd = new DropdownField { choices = worlds, value = worlds[0] };
                wd.style.marginBottom = 6; body.Add(wd);

                var thumb = new VisualElement();
                thumb.style.height = 180; thumb.style.marginBottom = 8;
                thumb.style.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
                thumb.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
                Radius(thumb, 8);
                body.Add(thumb);
                void ShowThumb(string w) => thumb.style.backgroundImage = new StyleBackground(NetworkDesigner.Terrain.WorldThumbnail.Load(w));
                ShowThumb(wd.value);
                wd.RegisterValueChangedCallback(e => ShowThumb(e.newValue));

                var loadBtn = MakeButton("Load World", () => { if (!string.IsNullOrWhiteSpace(wd.value)) Designer.LoadGame(wd.value); });
                loadBtn.style.height = 40; body.Add(loadBtn);

                var addBtn = MakeButton("Add map areas…", () => { if (!string.IsNullOrWhiteSpace(wd.value)) OpenDownloadModal(wd.value); });
                addBtn.style.height = 28; addBtn.style.marginTop = 6; body.Add(addBtn);
            }

            // ---- NEW WORLD ----
            body.Add(Divider());
            body.Add(SectionLabel("NEW WORLD"));

            var nameLbl = new Label("Name"); nameLbl.style.color = Ink; nameLbl.style.marginBottom = 2; body.Add(nameLbl);
            var nameField = new TextField { value = "" }; nameField.style.marginBottom = 8; body.Add(nameField);

            var status = new Label(); status.style.color = new Color(0.95f, 0.6f, 0.5f); status.style.fontSize = 11; status.style.marginBottom = 4;

            var createBtn = MakeButton("Create", () =>
            {
                string n = nameField.value?.Trim();
                if (string.IsNullOrEmpty(n)) { status.text = "Enter a world name."; return; }
                if (!NetworkDesigner.Terrain.WorldManager.Create(n)) { status.text = "A world by that name already exists."; return; }
                OpenDownloadModal(n);   // position + size + download into the new world
            });
            createBtn.style.height = 40; body.Add(createBtn); body.Add(status);

            body.Add(Divider());
            var quitBtn = MakeButton("Quit", () =>
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            });
            quitBtn.style.height = 28; body.Add(quitBtn);
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
