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
        // True while the cursor is over ANY palette UI: a panel, an in-panel scroll
        // list, OR an open dropdown popup. Enter/leave tracks the panel body; the
        // per-frame hit-test (PointerOverPanelTree) additionally catches dropdown
        // popups, which UI Toolkit renders OUTSIDE the panel as a sibling tree —
        // so scrolling an open dropdown no longer leaks to the camera zoom.
        public static bool PointerOverUI => ModalOpen || _hoverPanel || PointerOverPanelTree();
        static bool _hoverPanel;                                   // enter/leave over a panel body
        static readonly List<VisualElement> _panels = new List<VisualElement>();  // live panel bodies (for the hit-test)
        // True while a modal is up — the designer/camera suspend keyboard input so typing
        // doesn't trigger hotkeys or move the camera. Ref-counted so a confirm modal
        // stacked over another modal doesn't clear it early when the top one closes.
        public static bool ModalOpen => _modalDepth > 0;
        static int _modalDepth;
        // Public ref-count hooks so a full-screen modal palette (e.g. the startup game-picker) can hold
        // ModalOpen while it's visible — suppressing hotkeys/sculpt/camera underneath it.
        public static void PushModal() => _modalDepth++;
        public static void PopModal() => _modalDepth = Mathf.Max(0, _modalDepth - 1);

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
        Button _snapBtn, _gridBtn, _topoBtn;
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
        protected virtual bool Centered => false;        // dead-centre on screen (ignores Anchor*)
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

        protected virtual void OnDestroy() { _all.Remove(this); if (_panel != null) _panels.Remove(_panel); }

        protected virtual void OnDisable() { _hoverPanel = false; }

        // True while a UI Toolkit dropdown popup is OPEN. Panels themselves are handled by
        // the reliable _hoverPanel enter/leave; this only covers the dropdown popup, which
        // the enter/leave callbacks can't see (it's a sibling tree). Detection is
        // COORDINATE-FREE — we just check whether the open menu exists in the panel tree —
        // because a cursor->panel pick with a wrong Y-flip would mis-flag the cursor as
        // over UI and wrongly suppress painting/sculpting/look across the world. While a
        // dropdown is open the user is interacting with it, so treating it as "over UI"
        // regardless of cursor position is correct anyway.
        static bool PointerOverPanelTree()
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                IPanel p = _panels[i] != null ? _panels[i].panel : null;
                if (p == null) continue;
                VisualElement root = p.visualTree;
                if (root == null) return false;
                // The dropdown menu is added near the top of the panel tree.
                for (int c = 0; c < root.childCount; c++)
                    if (root[c].ClassListContains("unity-base-dropdown")) return true;
                return false;   // shared panel — one check covers all palettes
            }
            return false;
        }

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
                StyleActive(_gridBtn, Designer.GridOn);
                StyleActive(_topoBtn, Designer.ChunkContours);
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

        // Rebuild the panel body in place (e.g. after a download adds a new map to the list).
        protected void Rebuild() { if (_built && _doc != null) BuildPanel(_doc.rootVisualElement); }

        void BuildPanel(VisualElement root)
        {
            root.Clear();
            _sync.Clear();
            if (_panel != null) _panels.Remove(_panel);   // drop the stale entry before reassigning

            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.width = PanelWidth;
            if (Centered)
            {
                // Dead-centre on screen (translate by half its own size — resolution-independent).
                _panel.style.left = Length.Percent(50);
                _panel.style.top = Length.Percent(50);
                _panel.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50), 0);
            }
            else
            {
                if (AnchorBottom) _panel.style.bottom = 16; else _panel.style.top = 16;
                if (AnchorRight) _panel.style.right = 16; else _panel.style.left = 16;
            }
            _panel.style.maxHeight = Mathf.Max(300f, Screen.height - 32f);   // tall palettes scroll
            Pad(_panel, 14, 14, 12, 16);
            _panel.style.backgroundColor = PanelBg;
            Radius(_panel, 12);
            _panel.style.borderTopWidth = 3; _panel.style.borderTopColor = Accent;
            _panel.RegisterCallback<PointerEnterEvent>(_ => _hoverPanel = true);
            _panel.RegisterCallback<PointerLeaveEvent>(_ => _hoverPanel = false);
            if (!_panels.Contains(_panel)) _panels.Add(_panel);   // for the dropdown-popup hit-test
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
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;  // never a horizontal bar
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
            _topoBtn = FooterBtn("Topo", () => Designer.ChunkContours = !Designer.ChunkContours);
            _gridBtn = FooterBtn("Grid", () => Designer.ToggleGrid());
            _snapBtn = FooterBtn("Snap", () => Designer.SnapToGrid = !Designer.SnapToGrid);
            _topoBtn.style.marginRight = 6; _gridBtn.style.marginRight = 6;
            btns.Add(_topoBtn); btns.Add(_gridBtn); btns.Add(_snapBtn);

            row.Add(labels); row.Add(btns);
            foot.Add(row);
            return foot;
        }

        Button FooterBtn(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.width = 56; b.style.height = 40;
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
                                          float min, float max, string fmt = "0.#", float step = 0f)
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
            // step > 0 snaps the handle itself to discrete increments (so it moves N at a time).
            s.RegisterValueChangedCallback(e =>
            {
                float v = step > 0f ? Mathf.Round(e.newValue / step) * step : e.newValue;
                if (step > 0f && !Mathf.Approximately(v, e.newValue)) s.SetValueWithoutNotify(v);
                set(v); val.text = v.ToString(fmt);
            });
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
            _sync.Add(() =>
            {
                // Refresh the option LIST too — packs (etc.) are often created after the palette is built.
                var cur = choices() ?? new List<string>();
                bool changed = dd.choices == null || dd.choices.Count != cur.Count;
                if (!changed) for (int i = 0; i < cur.Count; i++) if (dd.choices[i] != cur[i]) { changed = true; break; }
                if (changed) dd.choices = new List<string>(cur);
                string c = get(); if (dd.value != c) dd.SetValueWithoutNotify(c);
            });
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

        // A simple modal: dim full-screen backdrop + centered panel with a text field and
        // Cancel / <ok> buttons. onOk receives the entered text. Click the backdrop or
        // Cancel to dismiss. Added to this palette's UIDocument root (overlays everything).
        // Build a centered modal over the whole screen and return the content
        // container the caller fills. `close` dismisses it. ModalOpen is held while it's
        // up (suspends world keyboard/camera). width/maxHeight in panel px; maxHeight
        // <= 0 = unconstrained. closeOnBackdropClick=false forces dismissal through an
        // explicit control (so a heavy modal can guard against accidental close).
        protected VisualElement BeginModal(string title, float width, float maxHeight, out Action close,
                                           bool closeOnBackdropClick = true)
        {
            close = () => { };
            var root = _doc != null ? _doc.rootVisualElement : null;
            if (root == null) return null;

            var backdrop = new VisualElement();
            backdrop.style.position = Position.Absolute;
            backdrop.style.left = 0; backdrop.style.right = 0; backdrop.style.top = 0; backdrop.style.bottom = 0;
            backdrop.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            backdrop.style.alignItems = Align.Center;
            backdrop.style.justifyContent = Justify.Center;
            // Cover the screen so world input doesn't leak while the modal is up.
            // ModalOpen (ref-counted) forces PointerOverUI true on its own.
            _modalDepth++;
            void Close()
            {
                if (backdrop.parent == null) return;      // already closed (don't double-decrement)
                root.Remove(backdrop);
                _modalDepth = Mathf.Max(0, _modalDepth - 1);
            }
            close = Close;
            if (closeOnBackdropClick)
                backdrop.RegisterCallback<ClickEvent>(e => { if (e.target == backdrop) Close(); });
            root.Add(backdrop);

            var modal = new VisualElement();
            modal.style.width = width;
            if (maxHeight > 0f) modal.style.maxHeight = maxHeight;
            Pad(modal, 16, 16, 14, 16);
            modal.style.backgroundColor = new Color(PanelBg.r, PanelBg.g, PanelBg.b, 0.99f);
            Radius(modal, 12);
            modal.style.borderTopWidth = 3; modal.style.borderTopColor = Accent;
            backdrop.Add(modal);

            if (!string.IsNullOrEmpty(title))
            {
                var t = new Label(title);
                t.style.color = Ink; t.style.fontSize = 15;
                t.style.unityFontStyleAndWeight = FontStyle.Bold;
                t.style.unityTextAlign = TextAnchor.MiddleCenter;
                t.style.marginBottom = 10;
                modal.Add(t);
            }
            return modal;
        }

        protected void ShowTextModal(string title, string okLabel, Action<string> onOk)
        {
            var modal = BeginModal(title, 320f, 0f, out Action close);
            if (modal == null) return;

            var tf = new TextField();
            tf.style.marginBottom = 12;
            modal.Add(tf);

            var row = HBox();
            row.style.justifyContent = Justify.FlexEnd;
            var cancel = MakeButton("Cancel", close);
            var ok = MakeButton(okLabel, () => { string v = tf.value; close(); onOk?.Invoke(v); });
            cancel.style.flexGrow = 0; cancel.style.width = 96; cancel.style.marginRight = 6;
            ok.style.flexGrow = 0; ok.style.width = 96;
            StyleActive(ok, true);
            row.Add(cancel); row.Add(ok);
            modal.Add(row);

            tf.Focus();
        }

        // A yes/no confirmation modal. Cancel just dismisses (returning to whatever was
        // beneath); the confirm button dismisses AND runs onConfirm. Safe to stack over
        // another modal — ModalOpen is ref-counted.
        protected void ShowConfirm(string title, string message, string confirmLabel, Action onConfirm)
        {
            var modal = BeginModal(title, 400f, 0f, out Action close);
            if (modal == null) return;

            var msg = new Label(message);
            msg.style.color = Ink; msg.style.fontSize = 13;
            msg.style.whiteSpace = WhiteSpace.Normal; msg.style.marginBottom = 16;
            modal.Add(msg);

            var row = HBox();
            row.style.justifyContent = Justify.FlexEnd;
            var cancel = MakeButton("Cancel", close);
            var ok = MakeButton(confirmLabel, () => { close(); onConfirm?.Invoke(); });
            cancel.style.flexGrow = 0; cancel.style.width = 100; cancel.style.marginRight = 6;
            ok.style.flexGrow = 0; ok.style.width = 110;
            StyleActive(ok, true);
            row.Add(cancel); row.Add(ok);
            modal.Add(row);
        }

        // A fixed-max-height vertical scroll box for long lists, so the rest of the body
        // (controls above/below) stays put while the list scrolls inside it.
        protected static ScrollView ScrollBox(float maxHeight)
        {
            var sv = new ScrollView(ScrollViewMode.Vertical);
            sv.style.maxHeight = maxHeight;
            sv.style.flexShrink = 0;
            return sv;
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

        protected static void SetBorder(VisualElement e, float w, Color c)
        {
            e.style.borderTopWidth = w; e.style.borderBottomWidth = w;
            e.style.borderLeftWidth = w; e.style.borderRightWidth = w;
            e.style.borderTopColor = c; e.style.borderBottomColor = c;
            e.style.borderLeftColor = c; e.style.borderRightColor = c;
        }

        // Default map centre when a world isn't anchored yet (Astoria, OR).
        protected const double DefaultLat = 46.18, DefaultLon = -123.83;

        // Full-screen picker for adding map areas to a world: drag the box to position it (snaps to the
        // world's 1 km lattice), drag the map to pan, "Download this area" appends the block. Existing
        // areas are outlined; overlaps are blocked. Shared by the launcher + the in-world System palette;
        // Rebuild() refreshes whichever palette opened it (so its world thumbnail updates).
        protected void OpenDownloadModal(string world, Action<bool> onClosed = null)
        {
            var wi = NetworkDesigner.Terrain.WorldManager.Read(world);
            if (wi == null) return;
            int defaultKm = wi.MapSizeKm;
            double clat = DefaultLat, clon = DefaultLon;
            if (wi.Anchored)
            {
                double cmx = wi.OriginMercX + defaultKm * wi.TileMercM / 2.0, cmy = wi.OriginMercY - defaultKm * wi.TileMercM / 2.0;
                clon = NetworkDesigner.Terrain.WorldManager.MercX2Lon(cmx); clat = NetworkDesigner.Terrain.WorldManager.MercY2Lat(cmy);
            }

            float modalW = Mathf.Max(640f, Screen.width - 80f);
            float modalH = Mathf.Max(460f, Screen.height - 80f);
            var modal = BeginModal($"Add map areas — “{world}”", modalW, modalH, out System.Action close, closeOnBackdropClick: false);
            if (modal == null) return;

            float leftW = 230f;
            int mapW = Mathf.Clamp(Mathf.RoundToInt(modalW - leftW - 64f), 256, 4096);
            int mapH = Mathf.Clamp(Mathf.RoundToInt(modalH - 120f), 256, 3000);

            var rowBox = new VisualElement();
            rowBox.style.flexDirection = FlexDirection.Row; rowBox.style.flexGrow = 1;
            modal.Add(rowBox);

            var left = new VisualElement();
            left.style.width = leftW; left.style.marginRight = 16;
            rowBox.Add(left);

            var picker = new DemMapPicker(this, clat, clon, mapW, mapH, defaultKm, defaultKm);
            rowBox.Add(picker.Root);

            Label Cap(string t) { var l = new Label(t); l.style.color = Sub; l.style.fontSize = 11; l.style.marginBottom = 2; l.style.marginTop = 8; return l; }

            var info = new Label(); info.style.color = Sub; info.style.fontSize = 12;
            info.style.whiteSpace = WhiteSpace.Normal; info.style.marginTop = 4; info.style.marginBottom = 10; left.Add(info);

            var srcDd = new DropdownField(new System.Collections.Generic.List<string> {
                "USGS 3DEP — 1 m (US)", "AWS Terrarium — global ~30 m" }, 0);
            srcDd.style.marginBottom = 10; left.Add(srcDd);

            bool busy = false, anyDownload = false;

            var dlBtn = MakeButton("Download this area", () => { });
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

            // onClosed(didDownload): lets the caller react (launcher just refreshes; the in-world palette
            // reloads the world so new tiles appear live). Default = refresh this palette.
            var closeBtn = MakeButton("Close", () => { close(); if (onClosed != null) onClosed(anyDownload); else Rebuild(); });
            closeBtn.style.height = 28; closeBtn.style.flexGrow = 0; closeBtn.style.marginTop = 10; left.Add(closeBtn);

            void UpdateInfo()
            {
                NetworkDesigner.Terrain.Dem3DEP.Estimate(picker.AreaKmW, picker.AreaKmH, out double sizeMB, out double secs);
                info.text = $"World “{world}” · drag a corner to size (1 km steps) @ 1 m/px\n"
                          + $"{picker.AreaKmW:0} × {picker.AreaKmH:0} km · Center {picker.CenterLat:0.0000}, {picker.CenterLon:0.0000}\n"
                          + $"this area ≈ {sizeMB:0} MB · ~{secs:0} s\n"
                          + (picker.Overlapping
                              ? "⚠ overlaps an existing area — move it to a free spot"
                              : $"snaps to the world grid · {NetworkDesigner.Terrain.WorldManager.ListMapSets(world).Count} area(s) so far");
                dlBtn.SetEnabled(!busy && !picker.Overlapping);
            }

            // Show this world's already-downloaded areas + turn on lattice snapping. Re-read after each
            // download so the new block appears and the next placement snaps around it. Un-anchored
            // (brand-new) world → no context yet, so the first area places freely.
            void RefreshAreas()
            {
                var wi2 = NetworkDesigner.Terrain.WorldManager.Read(world);
                if (wi2 == null || !wi2.Anchored) { picker.SetWorld(0, 0, 0, null); return; }
                var areas = new System.Collections.Generic.List<(double w, double s, double e, double n)>();
                double tm = wi2.TileMercM;
                foreach (var ms in NetworkDesigner.Terrain.WorldManager.ListMapSets(world))
                {
                    var mi = NetworkDesigner.Terrain.WorldManager.ReadMapSet(world, ms);
                    if (mi == null) continue;
                    int mw = mi.W > 0 ? mi.W : mi.N, mh = mi.H > 0 ? mi.H : mi.N;
                    if (mw <= 0 || mh <= 0) continue;
                    double w0 = wi2.OriginMercX + mi.GC * tm, e0 = w0 + mw * tm;
                    double n0 = wi2.OriginMercY - mi.GR * tm, s0 = n0 - mh * tm;
                    areas.Add((w0, s0, e0, n0));
                }
                picker.SetWorld(wi2.OriginMercX, wi2.OriginMercY, tm, areas);
            }

            void DoDownload()
            {
                if (busy) return;
                busy = true; dlBtn.SetEnabled(false);
                fill.style.width = Length.Percent(0); status.text = "Starting…";
                var src = srcDd.index == 1 ? NetworkDesigner.Terrain.Dem3DEP.Source.AwsTerrarium
                                           : NetworkDesigner.Terrain.Dem3DEP.Source.Usgs3DEP;
                NetworkDesigner.Terrain.Dem3DEP.StartInWorld(world, picker.CenterLat, picker.CenterLon, picker.AreaKmW, picker.AreaKmH, src,
                    (p, msg) => { fill.style.width = Length.Percent(Mathf.Clamp01(p) * 100f); status.text = msg; },
                    (ok, msg) =>
                    {
                        busy = false; status.text = msg;
                        if (ok) { NetworkDesigner.Terrain.WorldThumbnail.Generate(world); RefreshAreas(); anyDownload = true; }
                        UpdateInfo();
                    });
            }

            dlBtn.clicked += DoDownload;
            picker.OnChanged = UpdateInfo;
            RefreshAreas();
            UpdateInfo();
        }
    }
}
