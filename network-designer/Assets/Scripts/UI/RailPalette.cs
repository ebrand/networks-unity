// In-game rail tool palette (UI Toolkit). Consolidates the operational rail/plan
// controls that used to live as hotkeys + live-tuning entries into one screen-space
// panel, styled to tie to the in-world rail-amber accent. Built entirely in C# with
// inline styles (no UXML/USS) so there is no extra asset to author beyond the one
// themed PanelSettings the bootstrap already needs.
//
// USAGE: drop this on any GameObject; it adds its own UIDocument and finds the
// TerrainDesigner. The panel only shows while a rail mode (Build/L or Plan/K) is
// active, and reflects external changes (Cmd+wheel speed, B/I hotkeys) every frame.
//
// Controls map to real fields/actions — NOT reimplementations:
//   Build/Plan  -> TerrainDesigner.SetRailMode
//   Dgn Speed   -> RailLayer.SpeedLimitKmh        Max Grade -> RailLayer.MaxGradeDeg (deg)
//   Force Grade -> RailLayer.OverrideGrade        Show Nodes-> RailLayer.ShowNodePucks
//   Show Decel  -> RailLayer.ShowBrakingMarkers   Inspect   -> RailLayer.ShowCurveInspect
//   Corridor W  -> PlanLayer.CorridorWidth (+RebuildPlan)
//   Carve Appr. -> CarveRailApproaches()  Build on Plan -> PromotePlanToRail()  Clear Plan -> ClearPlan()
//   Parallel / Spacing -> RailLayer.ParallelEnabled / ParallelSpacing (geometry still TODO)

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class RailPalette : MonoBehaviour
    {
        // True while the cursor is over the (visible) palette. The legacy Input polling
        // in the camera/tools is blind to UI Toolkit, so they consult this to avoid
        // click/scroll/pan bleeding through to the world. Static: there is one palette.
        public static bool PointerOverUI { get; private set; }

        public TerrainDesigner Designer;
        [Tooltip("Optional: assign a themed PanelSettings. If empty, the palette finds " +
                 "one (Resources/RailPanelSettings, then any themed PanelSettings asset).")]
        public PanelSettings PanelSettings;

        static readonly Color Amber    = new Color(1f, 0.8f, 0.3f);
        static readonly Color PanelBg  = new Color(0.10f, 0.11f, 0.13f, 0.96f);
        static readonly Color Ink      = new Color(0.90f, 0.92f, 0.95f);
        static readonly Color Sub      = new Color(0.60f, 0.64f, 0.70f);
        static readonly Color BtnBg    = new Color(0.17f, 0.19f, 0.22f);
        static readonly Color DarkInk  = new Color(0.06f, 0.07f, 0.08f);
        static readonly Color TrackOff = new Color(0.22f, 0.24f, 0.27f);
        static readonly Color TrackOn  = new Color(0.32f, 0.56f, 0.42f);
        static readonly Color Knob     = new Color(0.95f, 0.96f, 0.98f);
        static readonly Color RuleCol  = new Color(1f, 1f, 1f, 0.12f);

        UIDocument _doc;
        VisualElement _panel;       // always visible (footer is always on)
        VisualElement _railBody;    // rail/plan controls — shown only in rail mode
        Label _footMode, _footSub;  // common footer: mode + sub-mode
        Button _snapBtn;            // common footer: snap-to-grid toggle
        readonly List<Action> _sync = new List<Action>();   // rail-body control sync
        bool _built;

        void Start()
        {
            _doc = GetComponent<UIDocument>();
            if (Designer == null) Designer = FindFirstObjectByType<TerrainDesigner>();
            if (Designer == null) { Debug.LogError("[RailPalette] No TerrainDesigner in scene."); return; }

            PanelSettings ps = ResolvePanelSettings();
            if (ps == null)
            {
                Debug.LogError("[RailPalette] No themed PanelSettings found. Create one via " +
                    "Assets ▸ Create ▸ UI Toolkit ▸ Panel Settings Asset (it auto-creates the " +
                    "default theme); the palette will then pick it up.");
                return;
            }
            _doc.panelSettings = ps;
            Build(_doc.rootVisualElement);
            _built = true;
        }

        void OnDisable() { PointerOverUI = false; }

        PanelSettings ResolvePanelSettings()
        {
            if (PanelSettings != null) return PanelSettings;
            var r = Resources.Load<PanelSettings>("RailPanelSettings");
            if (r != null) return r;
            var found = Resources.FindObjectsOfTypeAll<PanelSettings>();
            for (int i = 0; i < (found?.Length ?? 0); i++)
                if (found[i] != null && found[i].themeStyleSheet != null) return found[i];
            return null;
        }

        void Update()
        {
            if (!_built || Designer == null || _panel == null) return;

            // Common footer is always on; the rail/plan body only shows in rail mode.
            bool rail = Designer.IsRailMode;
            _railBody.style.display = rail ? DisplayStyle.Flex : DisplayStyle.None;
            if (rail) for (int i = 0; i < _sync.Count; i++) _sync[i]();

            // Live palette opacity (tunable from the React panel via TerrainDesigner).
            _panel.style.backgroundColor = new Color(PanelBg.r, PanelBg.g, PanelBg.b,
                Mathf.Clamp01(Designer.PaletteBgAlpha));

            // Footer sync (every frame, every mode).
            _footMode.text = Designer.PaletteModeLabel;
            _footSub.text = Designer.PaletteSubModeLabel;
            StyleSnap(_snapBtn, Designer.SnapToGrid);
        }

        // ---- build ----

        void Build(VisualElement root)
        {
            root.Clear();
            _sync.Clear();

            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.left = 16; _panel.style.top = 16; _panel.style.width = 300;
            Pad(_panel, 14, 14, 12, 16);
            _panel.style.backgroundColor = PanelBg;
            Radius(_panel, 12);
            _panel.style.borderTopWidth = 3; _panel.style.borderTopColor = Amber;
            // PointerEnter/Leave fire for the panel and ALL descendants as one unit, so
            // this is a clean "cursor is somewhere on the palette" flag.
            _panel.RegisterCallback<PointerEnterEvent>(_ => PointerOverUI = true);
            _panel.RegisterCallback<PointerLeaveEvent>(_ => PointerOverUI = false);
            root.Add(_panel);

            // Rail/plan body — shown only in rail mode (Update toggles its display).
            _railBody = new VisualElement();
            _panel.Add(_railBody);

            _railBody.Add(SectionLabel("MODES"));

            // Build / Plan segmented buttons.
            var modeRow = HBox();
            var buildBtn = MakeButton("Build (L)", () => Designer.SetRailMode(false));
            var planBtn  = MakeButton("Plan (K)",  () => Designer.SetRailMode(true));
            buildBtn.style.marginRight = 6;
            modeRow.Add(buildBtn); modeRow.Add(planBtn);
            modeRow.style.marginBottom = 12;
            _railBody.Add(modeRow);
            _sync.Add(() =>
            {
                StyleMode(buildBtn, Designer.IsRailBuildMode);
                StyleMode(planBtn,  Designer.IsRailPlanMode);
            });

            _railBody.Add(NumberRow("Dgn Speed", "km/h",
                () => Designer.RailLayer.SpeedLimitKmh, v => Designer.RailLayer.SpeedLimitKmh = v, 10f, 200f, "0"));
            _railBody.Add(NumberRow("Max Grade", "deg",
                () => Designer.RailLayer.MaxGradeDeg, v => Designer.RailLayer.MaxGradeDeg = v, 0.5f, 15f, "0.#"));

            _railBody.Add(ToggleRow("Force Grade",
                () => Designer.RailLayer.OverrideGrade, v => Designer.RailLayer.OverrideGrade = v));
            _railBody.Add(ToggleRow("Show Nodes",
                () => Designer.RailLayer.ShowNodePucks, v => Designer.RailLayer.ShowNodePucks = v));
            _railBody.Add(ToggleRow("Show Decel",
                () => Designer.RailLayer.ShowBrakingMarkers, v => Designer.RailLayer.ShowBrakingMarkers = v));
            _railBody.Add(ToggleRow("Inspect",
                () => Designer.RailLayer.ShowCurveInspect, v => Designer.RailLayer.ShowCurveInspect = v));

            _railBody.Add(NumberRow("Corridor Width", "m",
                () => Designer.PlanLayer.CorridorWidth,
                v => { Designer.PlanLayer.CorridorWidth = v; Designer.RebuildPlan(); }, 2f, 200f, "0"));

            // Plan/track actions.
            var actRow = HBox();
            var carve = MakeButton("Carve Appr.", () => Designer.CarveRailApproaches());
            var bop   = MakeButton("Build on Plan", () => Designer.PromotePlanToRail());
            carve.style.marginRight = 6;
            actRow.Add(carve); actRow.Add(bop);
            actRow.style.marginTop = 4;
            _railBody.Add(actRow);
            var clear = MakeButton("Clear Plan", () => Designer.ClearPlan());
            clear.style.marginTop = 6;
            _railBody.Add(clear);

            _railBody.Add(Divider());

            _railBody.Add(ToggleRow("Parallel (Z)",
                () => Designer.RailLayer.ParallelEnabled, v => Designer.RailLayer.ParallelEnabled = v));
            _railBody.Add(NumberRow("Spacing", "m",
                () => Designer.RailLayer.ParallelSpacing, v => Designer.RailLayer.ParallelSpacing = v, 5f, 100f, "0.#"));
            _railBody.Add(NumberRow("Parallels", "",
                () => Designer.RailLayer.ParallelCount,
                v => Designer.RailLayer.ParallelCount = Mathf.Max(1, Mathf.RoundToInt(v)), 1f, 8f, "0"));

            // Common footer — ALWAYS visible (mode + sub-mode + Snap), on every palette.
            _panel.Add(BuildFooter());
        }

        // The shared footer: a rule, the current mode (bold) + sub-mode, and a Snap
        // (snap-to-grid) toggle button. Same block regardless of mode, so it reads as
        // the common base of every palette and replaces the old IMGUI status strip.
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

            _snapBtn = new Button(() => Designer.SnapToGrid = !Designer.SnapToGrid) { text = "Snap" };
            _snapBtn.style.width = 76; _snapBtn.style.height = 44;
            _snapBtn.style.marginLeft = 0; _snapBtn.style.marginRight = 0;
            _snapBtn.style.borderTopWidth = _snapBtn.style.borderBottomWidth = 0;
            _snapBtn.style.borderLeftWidth = _snapBtn.style.borderRightWidth = 0;
            Radius(_snapBtn, 8);
            StyleSnap(_snapBtn, false);

            row.Add(labels); row.Add(_snapBtn);
            foot.Add(row);
            return foot;
        }

        void StyleSnap(Button b, bool on)
        {
            b.style.backgroundColor = on ? Amber : BtnBg;
            b.style.color = on ? DarkInk : Ink;
            b.style.unityFontStyleAndWeight = on ? FontStyle.Bold : FontStyle.Normal;
        }

        // ---- rows / widgets ----

        VisualElement NumberRow(string label, string suffix, Func<float> get, Action<float> set,
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

        VisualElement ToggleRow(string label, Func<bool> get, Action<bool> set)
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

        Button MakeButton(string text, Action onClick)
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

        void StyleMode(Button b, bool active)
        {
            b.style.backgroundColor = active ? Amber : BtnBg;
            b.style.color = active ? DarkInk : Ink;
            b.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
        }

        // ---- layout helpers ----

        VisualElement Row(string label)
        {
            var row = HBox();
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 8;
            var l = new Label(label);
            l.style.color = Ink; l.style.fontSize = 13; l.style.flexGrow = 1;
            row.Add(l);
            return row;
        }

        static VisualElement HBox()
        {
            var e = new VisualElement();
            e.style.flexDirection = FlexDirection.Row;
            e.style.alignItems = Align.Center;
            return e;
        }

        Label SectionLabel(string t)
        {
            var l = new Label(t);
            l.style.color = Sub; l.style.fontSize = 11;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginBottom = 8;
            return l;
        }

        VisualElement Divider()
        {
            var d = new VisualElement();
            d.style.height = 1; d.style.backgroundColor = RuleCol;
            d.style.marginTop = 10; d.style.marginBottom = 10;
            return d;
        }

        static string Fmt(float v, string suffix, string fmt)
            => string.IsNullOrEmpty(suffix) ? v.ToString(fmt) : v.ToString(fmt) + " " + suffix;

        static void Radius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        static void Pad(VisualElement e, float l, float r, float t, float b)
        {
            e.style.paddingLeft = l; e.style.paddingRight = r;
            e.style.paddingTop = t; e.style.paddingBottom = b;
        }
    }
}
