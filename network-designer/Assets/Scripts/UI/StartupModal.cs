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
