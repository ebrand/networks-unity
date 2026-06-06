// Shared base for the in-game UI Toolkit palettes (RailPalette, TerrainPalette, ...).
// Owns the panel lifecycle, the PointerOverUI gate (legacy Input is blind to UIT, so
// camera/tools consult this), the common footer (mode/sub-mode + Grid + Snap), live
// opacity from TerrainDesigner.PaletteBgAlpha, and the widget helpers. A subclass fills
// BuildBody() and says when it's visible via ShouldShow(); palettes are mode-exclusive
// so only one shows at a time and the footers never stack.
//
// Built entirely in C# with inline styles (no UXML/USS); the only asset needed is one
// themed PanelSettings (see ResolvePanelSettings).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public abstract class PaletteBase : MonoBehaviour
    {
        // True while the cursor is over ANY visible palette (one shows at a time).
        public static bool PointerOverUI { get; private set; }

        public TerrainDesigner Designer;
        [Tooltip("Optional themed PanelSettings. If empty, the palette finds one " +
                 "(Resources/RailPanelSettings, then any themed PanelSettings asset).")]
        public PanelSettings PanelSettings;

        protected static readonly Color Amber    = new Color(1f, 0.8f, 0.3f);
        protected static readonly Color PanelBg  = new Color(0.10f, 0.11f, 0.13f, 0.96f);
        protected static readonly Color Ink      = new Color(0.90f, 0.92f, 0.95f);
        protected static readonly Color Sub      = new Color(0.60f, 0.64f, 0.70f);
        protected static readonly Color BtnBg    = new Color(0.17f, 0.19f, 0.22f);
        protected static readonly Color DarkInk  = new Color(0.06f, 0.07f, 0.08f);
        protected static readonly Color TrackOff = new Color(0.22f, 0.24f, 0.27f);
        protected static readonly Color TrackOn  = new Color(0.32f, 0.56f, 0.42f);
        protected static readonly Color Knob     = new Color(0.95f, 0.96f, 0.98f);
        protected static readonly Color RuleCol  = new Color(1f, 1f, 1f, 0.12f);

        protected readonly List<Action> _sync = new List<Action>();

        UIDocument _doc;
        VisualElement _panel, _body;
        Label _footMode, _footSub;
        Button _snapBtn, _gridBtn;
        bool _built;

        // --- subclass hooks ---
        public abstract string PaletteId { get; }        // unique key (launcher button + open state)
        public virtual string MenuLabel => PaletteId;    // launcher button text
        public virtual bool Toggleable => true;          // false = no launcher button (the launcher)
        protected virtual bool DefaultOpen => false;     // open on first run
        protected abstract void BuildBody(VisualElement body);
        protected virtual bool ShouldShow() => IsOpen;   // visibility = its toggle state
        protected virtual string Title => null;          // big header; null/empty = none
        protected virtual Color Accent => Amber;         // top rule + active toggle color
        protected virtual float PanelWidth => 300f;
        protected virtual bool AnchorRight => false;     // right edge instead of left
        protected virtual bool AnchorBottom => false;    // bottom edge instead of top
        protected virtual bool ShowFooter => true;       // mode/Grid/Snap footer
        // Footer mode/sub labels. Default = the live editing mode (right for Terrain/Rail);
        // palettes with no editing mode (System/Spike) override to name themselves.
        protected virtual string FooterMode => Designer.PaletteModeLabel;
        protected virtual string FooterSub => Designer.PaletteSubModeLabel;

        // --- open/close registry (the launcher drives these) ---
        static readonly List<PaletteBase> _all = new List<PaletteBase>();
        static readonly HashSet<string> _open = new HashSet<string>();
        public static IReadOnlyList<PaletteBase> All => _all;
        public bool IsOpen => _open.Contains(PaletteId);
        public static bool IsOpenId(string id) => _open.Contains(id);
        public void SetOpen(bool v) { if (v) _open.Add(PaletteId); else _open.Remove(PaletteId); }
        // Radio toggle by id: close it if open, else open it exclusively AND run its
        // OnOpened hook (so the editing mode follows — e.g. Terrain→sculpt, Rail→Build).
        public static void ToggleExclusive(string id)
        {
            if (IsOpenId(id)) { SetExclusive(null); return; }
            PaletteBase p = _all.Find(x => x.PaletteId == id);
            if (p != null) p.OpenExclusive(); else SetExclusive(id);
        }
        // Open this palette exclusively and run its OnOpened hook.
        public void OpenExclusive() { SetExclusive(PaletteId); OnOpened(); }
        protected virtual void OnOpened() { }
        // Radio behaviour: open exactly `id` (closing every other palette), or null = none.
        public static void SetExclusive(string id)
        {
            _open.Clear();
            if (!string.IsNullOrEmpty(id)) _open.Add(id);
        }

        protected virtual void Start()
        {
            _doc = GetComponent<UIDocument>();
            if (Designer == null) Designer = FindFirstObjectByType<TerrainDesigner>();
            if (Designer == null) { Debug.LogError($"[{GetType().Name}] No TerrainDesigner in scene."); return; }
            PanelSettings ps = ResolvePanelSettings();
            if (ps == null)
            {
                Debug.LogError($"[{GetType().Name}] No themed PanelSettings found. Create one via " +
                    "Assets ▸ Create ▸ UI Toolkit ▸ Panel Settings Asset; the palette will pick it up.");
                return;
            }
            _doc.panelSettings = ps;
            BuildPanel(_doc.rootVisualElement);
            _built = true;
        }

        protected virtual void Awake()
        {
            if (!_all.Contains(this)) _all.Add(this);
            if (DefaultOpen) _open.Add(PaletteId);
        }

        protected virtual void OnDestroy() { _all.Remove(this); }

        protected virtual void OnDisable() { PointerOverUI = false; }

        protected virtual void Update()
        {
            if (!_built || Designer == null || _panel == null) return;
            bool show = ShouldShow();
            _panel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;
            for (int i = 0; i < _sync.Count; i++) _sync[i]();
            _panel.style.backgroundColor = new Color(PanelBg.r, PanelBg.g, PanelBg.b,
                Mathf.Clamp01(Designer.PaletteBgAlpha));
            if (_footMode != null)
            {
                _footMode.text = FooterMode;
                _footSub.text = FooterSub;
                StyleActive(_snapBtn, Designer.SnapToGrid);
                StyleActive(_gridBtn, Designer.GridEnabled);
            }
        }

        PanelSettings ResolvePanelSettings()
        {
            if (PanelSettings != null) return PanelSettings;
            var r = Resources.Load<PanelSettings>("PanelSettings")
                 ?? Resources.Load<PanelSettings>("RailPanelSettings");
            if (r != null) return r;
            var found = Resources.FindObjectsOfTypeAll<PanelSettings>();
            for (int i = 0; i < (found?.Length ?? 0); i++)
                if (found[i] != null && found[i].themeStyleSheet != null) return found[i];
            return null;
        }

        void BuildPanel(VisualElement root)
        {
            root.Clear();
            _sync.Clear();

            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.width = PanelWidth;
            if (AnchorBottom) _panel.style.bottom = 16; else _panel.style.top = 16;
            if (AnchorRight) _panel.style.right = 16; else _panel.style.left = 16;
            _panel.style.maxHeight = Mathf.Max(300f, Screen.height - 32f);   // tall palettes scroll
            Pad(_panel, 14, 14, 12, 16);
            _panel.style.backgroundColor = PanelBg;
            Radius(_panel, 12);
            _panel.style.borderTopWidth = 3; _panel.style.borderTopColor = Accent;
            _panel.RegisterCallback<PointerEnterEvent>(_ => PointerOverUI = true);
            _panel.RegisterCallback<PointerLeaveEvent>(_ => PointerOverUI = false);
            root.Add(_panel);

            if (!string.IsNullOrEmpty(Title))
            {
                var t = new Label(Title);
                t.style.color = Ink; t.style.fontSize = 17;
                t.style.unityFontStyleAndWeight = FontStyle.Bold;
                t.style.unityTextAlign = TextAnchor.MiddleCenter;
                t.style.marginBottom = 10;
                _panel.Add(t);
            }

            // Body scrolls if it's taller than the panel; the footer stays pinned below.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            _panel.Add(scroll);
            _body = scroll.contentContainer;
            BuildBody(_body);

            if (ShowFooter) _panel.Add(BuildFooter());
        }

        // The shared footer: a rule, the current mode (bold) + sub-mode, and Grid + Snap
        // toggle buttons. Same block on every palette; replaces the old IMGUI status strip.
        VisualElement BuildFooter()
        {
            var foot = new VisualElement();
            foot.Add(Divider());

            var row = HBox();
            row.style.justifyContent = Justify.SpaceBetween;

            var labels = new VisualElement();
            _footMode = new Label("Terrain");
            _footMode.style.color = Ink; _footMode.style.fontSize = 14;
            _footMode.style.unityFontStyleAndWeight = FontStyle.Bold;
            _footSub = new Label(string.Empty);
            _footSub.style.color = Sub; _footSub.style.fontSize = 13;
            _footSub.style.marginTop = 2;
            labels.Add(_footMode); labels.Add(_footSub);

            var btns = HBox();
            _gridBtn = FooterBtn("Grid", () => Designer.ToggleGrid());
            _snapBtn = FooterBtn("Snap", () => Designer.SnapToGrid = !Designer.SnapToGrid);
            _gridBtn.style.marginRight = 6;
            btns.Add(_gridBtn); btns.Add(_snapBtn);

            row.Add(labels); row.Add(btns);
            foot.Add(row);
            return foot;
        }

        Button FooterBtn(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.width = 66; b.style.height = 40;
            b.style.marginLeft = 0; b.style.marginRight = 0;
            b.style.borderTopWidth = b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = b.style.borderRightWidth = 0;
            Radius(b, 8);
            return b;
        }

        // ---- widgets (protected so subclasses build their bodies) ----

        protected VisualElement NumberRow(string label, string suffix, Func<float> get, Action<float> set,
                                          float min, float max, string fmt)
        {
            var row = Row(label);
            var tf = new TextField { isDelayed = true };
            tf.style.width = 92;
            tf.SetValueWithoutNotify(Fmt(get(), suffix, fmt));

            bool editing = false;
            tf.RegisterCallback<FocusInEvent>(_ => { editing = true; tf.SelectAll(); });
            tf.RegisterCallback<FocusOutEvent>(_ => { editing = false; tf.SetValueWithoutNotify(Fmt(get(), suffix, fmt)); });
            tf.RegisterValueChangedCallback(evt =>
            {
                // NOTE: string.Replace("", "") throws — guard the empty-suffix case.
                string s = (string.IsNullOrEmpty(suffix) ? evt.newValue
                                                         : evt.newValue.Replace(suffix, "")).Trim();
                if (float.TryParse(s, out float v))
                {
                    v = Mathf.Clamp(v, min, max);
                    set(v);
                    tf.SetValueWithoutNotify(Fmt(v, suffix, fmt));
                }
                else tf.SetValueWithoutNotify(Fmt(get(), suffix, fmt));
            });
            _sync.Add(() => { if (!editing) tf.SetValueWithoutNotify(Fmt(get(), suffix, fmt)); });

            row.Add(tf);
            return row;
        }

        protected VisualElement SliderRow(string label, Func<float> get, Action<float> set,
                                          float min, float max, string fmt = "0.#")
        {
            var row = HBox();
            row.style.marginBottom = 8;
            var l = new Label(label);
            l.style.color = Ink; l.style.fontSize = 13; l.style.minWidth = 76;
            row.Add(l);
            var s = new Slider(min, max) { value = Mathf.Clamp(get(), min, max) };
            s.style.flexGrow = 1; s.style.marginLeft = 4; s.style.marginRight = 6;
            var val = new Label(get().ToString(fmt));
            val.style.color = Sub; val.style.minWidth = 36; val.style.unityTextAlign = TextAnchor.MiddleRight;
            s.RegisterValueChangedCallback(e => { set(e.newValue); val.text = e.newValue.ToString(fmt); });
            _sync.Add(() => { float g = get(); s.SetValueWithoutNotify(Mathf.Clamp(g, min, max)); val.text = g.ToString(fmt); });
            row.Add(s); row.Add(val);
            return row;
        }

        protected DropdownField DropdownRow(Func<List<string>> choices, Func<string> get, Action<string> set)
        {
            var dd = new DropdownField();
            dd.choices = choices() ?? new List<string>();
            dd.SetValueWithoutNotify(get());
            dd.style.marginBottom = 8;
            dd.RegisterValueChangedCallback(e => set(e.newValue));
            _sync.Add(() => { string c = get(); if (dd.value != c) dd.SetValueWithoutNotify(c); });
            return dd;
        }

        protected VisualElement ToggleRow(string label, Func<bool> get, Action<bool> set)
        {
            var row = Row(label);

            var track = new VisualElement();
            track.style.width = 46; track.style.height = 24;
            track.style.flexDirection = FlexDirection.Row;
            track.style.alignItems = Align.Center;
            track.style.paddingLeft = 3; track.style.paddingRight = 3;
            Radius(track, 12);

            var knob = new VisualElement();
            knob.style.width = 18; knob.style.height = 18;
            knob.style.backgroundColor = Knob;
            Radius(knob, 9);
            track.Add(knob);

            void Refresh(bool on)
            {
                track.style.backgroundColor = on ? TrackOn : TrackOff;
                track.style.justifyContent = on ? Justify.FlexEnd : Justify.FlexStart;
            }
            track.RegisterCallback<ClickEvent>(_ => { bool nv = !get(); set(nv); Refresh(nv); });
            Refresh(get());
            _sync.Add(() => Refresh(get()));

            row.Add(track);
            return row;
        }

        protected Button MakeButton(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.flexGrow = 1;
            b.style.height = 32;
            b.style.marginLeft = 0; b.style.marginRight = 0;
            b.style.backgroundColor = BtnBg;
            b.style.color = Ink;
            b.style.unityFontStyleAndWeight = FontStyle.Normal;
            Radius(b, 6);
            b.style.borderTopWidth = b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = b.style.borderRightWidth = 0;
            return b;
        }

        // Active = accent fill + dark bold text. Used for mode buttons and footer toggles.
        protected void StyleActive(Button b, bool active)
        {
            b.style.backgroundColor = active ? Accent : BtnBg;
            b.style.color = active ? DarkInk : Ink;
            b.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
        }

        // ---- layout helpers ----

        protected VisualElement Row(string label)
        {
            var row = HBox();
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 8;
            var l = new Label(label);
            l.style.color = Ink; l.style.fontSize = 13; l.style.flexGrow = 1;
            row.Add(l);
            return row;
        }

        protected static VisualElement HBox()
        {
            var e = new VisualElement();
            e.style.flexDirection = FlexDirection.Row;
            e.style.alignItems = Align.Center;
            return e;
        }

        protected Label SectionLabel(string t)
        {
            var l = new Label(t);
            l.style.color = Sub; l.style.fontSize = 11;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginBottom = 8;
            return l;
        }

        protected VisualElement Divider()
        {
            var d = new VisualElement();
            d.style.height = 1; d.style.backgroundColor = RuleCol;
            d.style.marginTop = 10; d.style.marginBottom = 10;
            return d;
        }

        protected static string Fmt(float v, string suffix, string fmt)
            => string.IsNullOrEmpty(suffix) ? v.ToString(fmt) : v.ToString(fmt) + " " + suffix;

        protected static void Radius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        protected static void Pad(VisualElement e, float l, float r, float t, float b)
        {
            e.style.paddingLeft = l; e.style.paddingRight = r;
            e.style.paddingTop = t; e.style.paddingBottom = b;
        }
    }
}
