// In-game Road Designer: author cross-section road profiles (lane counts per direction, median / turn
// lane, shoulders), preview them, and save to the in-game profiles file (RoadProfileLibrary) so the road
// planner can pick them. Phase 1: full authoring + a 2D cross-section preview in the viewport area.
// Phase 2 will swap that area for a rotatable / zoomable 3D view. Auto-spawns; opened from the Road palette.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Model;
using NetworkDesigner.Import;
using NetworkDesigner.Roads;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class RoadDesignerModal : PaletteBase
    {
        public override string PaletteId => "RoadDesigner";
        public override bool Toggleable => false;           // opened on demand (Road palette button)
        protected override bool ShouldShow() => IsOpen;
        protected override bool Centered => true;
        protected override bool ShowFooter => false;
        protected override string Title => "Road Designer";
        protected override float PanelWidth => Mathf.Max(720f, Screen.width - 120f);
        protected override Color Accent => new Color(0.95f, 0.55f, 0.15f);

        enum Center { None, TurnLane, NormalMedian, WideMedian }

        // ---- editing state ----
        string _name = "new-road-profile";
        int _aLanes = 2, _bLanes = 2;
        bool _oneWay, _highway;
        Center _center = Center.None;
        float _laneW = 3.5f, _medianW = 3.5f, _shoulderW = 2.0f;

        VisualElement _preview, _listBox;
        bool _heldModal;

        // Hold ModalOpen while visible (suspends world hotkeys / camera under the full-screen window).
        protected override void Update()
        {
            base.Update();
            bool shown = ShouldShow();
            if (shown != _heldModal) { if (shown) PushModal(); else PopModal(); _heldModal = shown; }
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (_heldModal) { PopModal(); _heldModal = false; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindFirstObjectByType<RoadDesignerModal>() != null) return;
            var go = new GameObject("RoadDesignerModal (auto)");
            go.AddComponent<UIDocument>();
            go.AddComponent<RoadDesignerModal>();
        }

        protected override void BuildBody(VisualElement body)
        {
            var row = HBox();
            row.style.flexGrow = 1; row.style.alignItems = Align.FlexStart;
            body.Add(row);

            var left = new VisualElement();
            left.style.width = 280; left.style.marginRight = 16; left.style.flexShrink = 0;
            row.Add(left);

            var right = new VisualElement();
            right.style.flexGrow = 1; right.style.minHeight = 420;
            right.style.backgroundColor = new Color(0.08f, 0.09f, 0.10f);
            Radius(right, 8);
            row.Add(right);
            _preview = new VisualElement();
            _preview.style.flexGrow = 1;
            right.Add(_preview);

            BuildControls(left);
            RefreshList();
            RefreshPreview();
        }

        void BuildControls(VisualElement left)
        {
            left.Add(Cap("New road profile:"));
            var nameField = new TextField { value = _name };
            nameField.style.marginBottom = 6;
            nameField.RegisterValueChangedCallback(e => _name = e.newValue);
            left.Add(nameField);
            var createBtn = MakeButton("Create", () => { _name = nameField.value; SaveCurrent(); RefreshList(); });
            createBtn.style.marginBottom = 10; left.Add(createBtn);

            _listBox = ScrollBox(150);
            _listBox.style.marginBottom = 12;
            SetBorder(_listBox, 1, new Color(0.3f, 0.32f, 0.36f));
            Radius(_listBox, 8);
            left.Add(_listBox);

            left.Add(DropdownRow("A lanes", CountChoices, () => _aLanes.ToString(),
                v => { int.TryParse(v, out _aLanes); RefreshPreview(); }));
            left.Add(DropdownRow("B lanes", CountChoices, () => _bLanes.ToString(),
                v => { int.TryParse(v, out _bLanes); RefreshPreview(); }));

            left.Add(ToggleRow("One-Way", () => _oneWay, v => { _oneWay = v; RefreshPreview(); }));
            left.Add(ToggleRow("Highway", () => _highway, v => { _highway = v; RefreshPreview(); }));
            // Turn Lane / Wide Median / Normal Median are mutually exclusive (one centre treatment).
            left.Add(ToggleRow("Turn Lane", () => _center == Center.TurnLane,
                v => { _center = v ? Center.TurnLane : Center.None; RefreshPreview(); }));
            left.Add(ToggleRow("Wide Median", () => _center == Center.WideMedian,
                v => { _center = v ? Center.WideMedian : Center.None; RefreshPreview(); }));
            left.Add(ToggleRow("Normal Median", () => _center == Center.NormalMedian,
                v => { _center = v ? Center.NormalMedian : Center.None; RefreshPreview(); }));

            left.Add(NumberRow("Lane", "m", () => _laneW, v => { _laneW = v; RefreshPreview(); }, 2f, 6f, "0.#"));
            left.Add(NumberRow("Median", "m", () => _medianW, v => { _medianW = v; RefreshPreview(); }, 0.5f, 20f, "0.#"));
            left.Add(NumberRow("Shoulder", "m", () => _shoulderW, v => { _shoulderW = v; RefreshPreview(); }, 0f, 6f, "0.#"));

            var save = MakeButton("Save", () => { SaveCurrent(); RefreshList(); });
            save.style.marginTop = 10; left.Add(save);
            var close = MakeButton("Close", () => SetOpen(false));
            close.style.marginTop = 6; left.Add(close);
        }

        static List<string> CountChoices() => new List<string> { "0", "1", "2", "3", "4", "5", "6" };

        // A "Label  [dropdown]" row.
        VisualElement DropdownRow(string label, Func<List<string>> choices, Func<string> get, Action<string> set)
        {
            var row = Row(label);
            var dd = new DropdownField { choices = choices(), value = get() };
            dd.style.width = 90;
            dd.RegisterValueChangedCallback(e => set(e.newValue));
            _sync.Add(() => { string c = get(); if (dd.value != c) dd.SetValueWithoutNotify(c); });
            row.Add(dd);
            return row;
        }

        Label Cap(string t)
        {
            var l = new Label(t);
            l.style.color = Sub; l.style.fontSize = 12; l.style.marginBottom = 2;
            return l;
        }

        // ---- profile <-> controls ----

        static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "road-profile";
            name = name.Trim().Replace(' ', '-');
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name;
        }

        SavedConfig BuildConfig()
        {
            var p = new RoadProfile { Id = Sanitize(_name) };
            p.AB = new Side();
            for (int i = 0; i < _aLanes; i++) p.AB.Lanes.Add(new Lane { Id = "a" + i, Width = _laneW });
            int b = _oneWay ? 0 : _bLanes;
            p.BA = new Side();
            for (int i = 0; i < b; i++) p.BA.Lanes.Add(new Lane { Id = "b" + i, Width = _laneW });
            if (_center == Center.TurnLane) p.TurnLane = new TurnLane { Width = _medianW };
            else if (_center == Center.NormalMedian) p.Median = new Median { Width = _medianW };
            else if (_center == Center.WideMedian) p.Median = new Median { Width = _medianW * 2.5f };
            p.ShoulderAB = new Shoulder { Width = _shoulderW };
            p.ShoulderBA = new Shoulder { Width = _shoulderW };
            return new SavedConfig { Id = p.Id, Name = _name, Category = _highway ? "Highway" : "Custom", Road = p };
        }

        void SaveCurrent()
        {
            RoadProfileLibrary.SaveUserConfig(BuildConfig());
        }

        void LoadConfig(SavedConfig c)
        {
            if (c?.Road == null) return;
            var p = c.Road;
            _name = c.Name ?? c.Id;
            _aLanes = p.AB.Lanes.Count;
            _bLanes = p.BA.Lanes.Count;
            _oneWay = p.IsOneWay;
            _laneW = (p.AB.Lanes.Count > 0 ? p.AB.Lanes[0].Width : (p.BA.Lanes.Count > 0 ? p.BA.Lanes[0].Width : 3.5f));
            if (p.TurnLane != null) { _center = Center.TurnLane; _medianW = p.TurnLane.Width; }
            else if (p.Median != null) { _medianW = p.Median.Width; _center = p.Median.Width > _laneW * 1.5f ? Center.WideMedian : Center.NormalMedian; }
            else { _center = Center.None; }
            _shoulderW = p.ShoulderAB != null ? p.ShoulderAB.Width : 2f;
            _highway = (c.Category ?? "") == "Highway";
            Rebuild();   // re-read all controls from the loaded state
        }

        // ---- profile list ----

        void RefreshList()
        {
            if (_listBox == null) return;
            _listBox.Clear();
            var user = RoadProfileLibrary.UserConfigs;
            if (user.Count == 0)
            {
                var none = new Label("  (no saved profiles yet)");
                none.style.color = Sub; none.style.fontSize = 11; none.style.unityFontStyleAndWeight = FontStyle.Italic;
                _listBox.Add(none);
                return;
            }
            foreach (var c in user)
            {
                if (c == null) continue;
                SavedConfig cc = c;
                var b = MakeButton(c.Name ?? c.Id, () => LoadConfig(cc));
                b.style.height = 26; b.style.marginBottom = 2;
                b.style.unityTextAlign = TextAnchor.MiddleLeft;
                _listBox.Add(b);
            }
        }

        // ---- 2D cross-section preview ----

        void RefreshPreview()
        {
            if (_preview == null) return;
            _preview.Clear();
            RoadProfile prof = BuildConfig().Road;

            var strips = new List<(float w, string kind)>();
            void S(float w, string k) { if (w > 0.01f) strips.Add((w, k)); }
            S(prof.ShoulderBA.Width, "shoulder");
            for (int i = prof.BA.Lanes.Count - 1; i >= 0; i--) S(prof.BA.Lanes[i].Width, "lane");
            if (prof.Median != null) S(prof.Median.Width, "median");
            else if (prof.TurnLane != null) S(prof.TurnLane.Width, "turn");
            for (int i = 0; i < prof.AB.Lanes.Count; i++) S(prof.AB.Lanes[i].Width, "lane");
            S(prof.ShoulderAB.Width, "shoulder");

            var wrap = new VisualElement();
            wrap.style.flexGrow = 1; wrap.style.justifyContent = Justify.Center;
            wrap.style.paddingLeft = 24; wrap.style.paddingRight = 24;

            var total = new Label($"{prof.TotalWidth:0.#} m · {prof.AB.Lanes.Count}×{prof.BA.Lanes.Count} lanes"
                                  + (_highway ? " · Highway" : ""));
            total.style.color = Ink; total.style.unityTextAlign = TextAnchor.MiddleCenter; total.style.marginBottom = 10;
            wrap.Add(total);

            var sec = HBox();
            sec.style.height = 90; sec.style.alignItems = Align.Stretch;
            SetBorder(sec, 1, new Color(1f, 1f, 1f, 0.4f));
            foreach (var s in strips)
            {
                var box = new VisualElement();
                box.style.flexGrow = s.w;
                box.style.backgroundColor = StripColor(s.kind);
                if (s.kind == "lane")
                { box.style.borderRightWidth = 1; box.style.borderRightColor = new Color(1f, 1f, 1f, 0.45f); }
                sec.Add(box);
            }
            wrap.Add(sec);

            var note = new Label("2D cross-section — rotatable 3D view coming next");
            note.style.color = Sub; note.style.fontSize = 11; note.style.unityTextAlign = TextAnchor.MiddleCenter; note.style.marginTop = 12;
            wrap.Add(note);
            _preview.Add(wrap);
        }

        static Color StripColor(string kind)
        {
            switch (kind)
            {
                case "lane": return new Color(0.28f, 0.29f, 0.31f);
                case "shoulder": return new Color(0.18f, 0.19f, 0.20f);
                case "median": return new Color(0.32f, 0.42f, 0.26f);   // planted median (green)
                case "turn": return new Color(0.42f, 0.36f, 0.14f);     // turn lane (amber)
                default: return new Color(0.25f, 0.25f, 0.25f);
            }
        }
    }
}
