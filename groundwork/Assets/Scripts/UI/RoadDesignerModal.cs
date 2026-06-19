// In-game Transportation Corridor Designer: author cross-section profiles as an ORDERED STACK of typed
// corridor segments (Traffic / Bike / Median / Rail / Shoulder / Sidewalk / Turn), each with a width and
// per-type attributes (HOV / Fence / Parapet+height / Guardrail). Pick a direction (A→B or B→A), define a
// segment, and "Add →" it to that stack. The stack is the source of truth; a derived RoadProfile is written
// alongside it so the car-agent sim + geometry keep working (see Model/CorridorStack.ToRoadProfile).
// Saved to RoadProfileLibrary (road-profiles-ingame.json). Auto-spawns; opened from the Road palette ("…").

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
        public override bool Toggleable => false;           // opened on demand (Road palette "…" button)
        protected override bool ShouldShow() => IsOpen;
        protected override bool Centered => true;
        protected override bool ShowFooter => false;
        protected override string Title => "Transportation Corridor Designer";
        protected override float PanelWidth => Mathf.Clamp(Screen.width - 720f, 760f, 1280f);
        protected override Color Accent => new Color(0.95f, 0.55f, 0.15f);

        // ---- editing state ----
        string _name = "new-road-profile";
        string _category = "";
        readonly CorridorStack _stack = new CorridorStack();

        // The segment currently being defined (the left-panel form) + which stack "Add →" targets.
        int _target = 2;                                // 0 = B→A, 1 = Center (straddles axis), 2 = A→B
        CorridorType _addType = CorridorType.Traffic;
        float _addWidth = 4f;
        bool _addHOV, _addFence, _addParapet, _addGuardrail;
        float _addParapetH = 1f;

        VisualElement _preview, _listBox, _stackBox, _view3d;
        bool _heldModal, _dragging;
        Vector2 _lastPtr;
        RoadPreview3D _rig;

        protected override void Update()
        {
            base.Update();
            bool shown = ShouldShow();
            if (shown != _heldModal) { if (shown) PushModal(); else PopModal(); _heldModal = shown; }
            if (_rig != null) _rig.SetActive(shown);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (_heldModal) { PopModal(); _heldModal = false; }
            if (_rig != null) _rig.SetActive(false);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_rig != null) Destroy(_rig.gameObject);
        }

        void EnsureRig()
        {
            if (_rig != null) return;
            var go = new GameObject("CorridorDesignerPreview3D") { hideFlags = HideFlags.DontSave };
            _rig = go.AddComponent<RoadPreview3D>();
        }

        void RegisterViewportInput(VisualElement v)
        {
            v.RegisterCallback<PointerDownEvent>(e => { v.CapturePointer(e.pointerId); _dragging = true; _lastPtr = (Vector2)e.position; });
            v.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (_dragging && _rig != null) { Vector2 d = (Vector2)e.position - _lastPtr; _rig.Orbit(d.x, d.y); _lastPtr = (Vector2)e.position; }
            });
            v.RegisterCallback<PointerUpEvent>(e => { v.ReleasePointer(e.pointerId); _dragging = false; });
            v.RegisterCallback<WheelEvent>(e => { if (_rig != null) _rig.Zoom(e.delta.y); });
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
            left.style.width = 300; left.style.marginRight = 16; left.style.flexShrink = 0;
            row.Add(left);

            var right = new VisualElement();
            right.style.flexGrow = 1; right.style.minHeight = 440;
            row.Add(right);

            _view3d = new VisualElement();
            _view3d.style.flexGrow = 1; _view3d.style.minHeight = 360;
            _view3d.style.backgroundColor = new Color(0.08f, 0.09f, 0.10f);
            _view3d.style.unityBackgroundScaleMode = ScaleMode.ScaleAndCrop;
            Radius(_view3d, 8);
            right.Add(_view3d);
            RegisterViewportInput(_view3d);

            _preview = new VisualElement();
            _preview.style.height = 110; _preview.style.marginTop = 8;
            right.Add(_preview);

            BuildControls(left);
            RefreshList();
            RefreshStackList();
            RefreshPreview();
        }

        void BuildControls(VisualElement left)
        {
            // Direction selector — which stack "Add →" appends to.
            var dirRow = HBox();
            var baBtn = MakeButton("B → A", () => { _target = 0; RefreshStackList(); });
            var ctBtn = MakeButton("Center", () => { _target = 1; RefreshStackList(); });
            var abBtn = MakeButton("A → B", () => { _target = 2; RefreshStackList(); });
            baBtn.style.marginRight = 6; ctBtn.style.marginRight = 6;
            dirRow.Add(baBtn); dirRow.Add(ctBtn); dirRow.Add(abBtn);
            dirRow.style.marginBottom = 10;
            left.Add(dirRow);
            _sync.Add(() => { StyleActive(baBtn, _target == 0); StyleActive(ctBtn, _target == 1); StyleActive(abBtn, _target == 2); });

            // Segment definition form.
            left.Add(DropdownRow("Type", TypeChoices, () => TypeLabel(_addType),
                v => { _addType = ParseType(v); _addWidth = CorridorStack.DefaultWidth(_addType); }));
            left.Add(NumberRow("Width", "m", () => _addWidth, v => _addWidth = v, 0.5f, 60f, "0.#"));
            left.Add(ToggleRow("HOV", () => _addHOV, v => _addHOV = v));
            left.Add(ToggleRow("Fence", () => _addFence, v => _addFence = v));
            left.Add(ToggleRow("Parapet", () => _addParapet, v => _addParapet = v));
            left.Add(NumberRow("Height", "m", () => _addParapetH, v => _addParapetH = v, 0.2f, 5f, "0.#"));
            left.Add(ToggleRow("Guardrail", () => _addGuardrail, v => _addGuardrail = v));

            var add = MakeButton("Add →", AddSegment);
            add.style.marginTop = 8; add.style.marginBottom = 8;
            left.Add(add);

            _stackBox = ScrollBox(150);
            SetBorder(_stackBox, 1, new Color(0.3f, 0.32f, 0.36f));
            Radius(_stackBox, 8);
            _stackBox.style.marginBottom = 12;
            left.Add(_stackBox);

            left.Add(Divider());

            // Saved-profile library.
            left.Add(Cap("Profiles:"));
            _listBox = ScrollBox(120);
            SetBorder(_listBox, 1, new Color(0.3f, 0.32f, 0.36f));
            Radius(_listBox, 8);
            _listBox.style.marginBottom = 8;
            left.Add(_listBox);

            left.Add(Cap("Profile name:"));
            var nameField = new TextField { value = _name };
            nameField.RegisterValueChangedCallback(e => _name = e.newValue);
            left.Add(nameField);
            left.Add(Cap("Category:"));
            var catField = new TextField { value = _category };
            catField.style.marginBottom = 6;
            catField.RegisterValueChangedCallback(e => _category = e.newValue);
            left.Add(catField);

            var saveRow = HBox();
            var newBtn = MakeButton("New", NewProfile);
            var saveBtn = MakeButton("Save", () => { SaveCurrent(); RefreshList(); });
            var delBtn = MakeButton("Delete", () => { RoadProfileLibrary.DeleteUserConfig(Sanitize(_name)); RebuildId("Road"); RefreshList(); });
            newBtn.style.marginRight = 6; saveBtn.style.marginRight = 6;
            saveRow.Add(newBtn); saveRow.Add(saveBtn); saveRow.Add(delBtn);
            saveRow.style.marginTop = 4;
            left.Add(saveRow);

            var close = MakeButton("Close", () => SetOpen(false));
            close.style.marginTop = 8; left.Add(close);
        }

        // ---- segment add / stack list ----

        void AddSegment()
        {
            var seg = new CorridorSegment(_addType, Mathf.Max(0.1f, _addWidth))
            {
                HOV = _addHOV && _addType == CorridorType.Traffic,
                Fence = _addFence,
                Parapet = _addParapet,
                ParapetHeight = _addParapetH,
                Guardrail = _addGuardrail,
            };
            List<CorridorSegment> dst = _target == 0 ? _stack.BA : _target == 1 ? _stack.Center : _stack.AB;
            dst.Add(seg);
            RefreshStackList();
            RefreshPreview();
        }

        void RefreshStackList()
        {
            if (_stackBox == null) return;
            _stackBox.Clear();
            AddStackGroup("A → B", _stack.AB);
            AddStackGroup("Center", _stack.Center);
            AddStackGroup("B → A", _stack.BA);
            if (_stack.AB.Count == 0 && _stack.BA.Count == 0 && _stack.Center.Count == 0)
            {
                var none = new Label("  (empty — define a segment and Add →)");
                none.style.color = Sub; none.style.fontSize = 11; none.style.unityFontStyleAndWeight = FontStyle.Italic;
                _stackBox.Add(none);
            }
        }

        void AddStackGroup(string title, List<CorridorSegment> side)
        {
            if (side.Count == 0) return;
            var head = new Label(title);
            head.style.color = Sub; head.style.fontSize = 11; head.style.unityFontStyleAndWeight = FontStyle.Bold; head.style.marginTop = 2;
            _stackBox.Add(head);
            for (int i = 0; i < side.Count; i++)
            {
                CorridorSegment s = side[i]; List<CorridorSegment> list = side;
                var r = HBox();
                r.style.justifyContent = Justify.SpaceBetween; r.style.marginBottom = 1;
                var lbl = new Label("  " + SegLabel(s));
                lbl.style.color = Ink; lbl.style.fontSize = 12;
                var x = MakeButton("✕", () => { list.Remove(s); RefreshStackList(); RefreshPreview(); });
                x.style.width = 24; x.style.height = 20;
                r.Add(lbl); r.Add(x);
                _stackBox.Add(r);
            }
        }

        static string SegLabel(CorridorSegment s)
        {
            string t = TypeLabel(s.Type) + " " + s.Width.ToString("0.#") + "m";
            if (s.HOV) t += " HOV";
            if (s.Parapet) t += " ¶" + s.ParapetHeight.ToString("0.#");
            if (s.Fence) t += " fence";
            if (s.Guardrail) t += " rail";
            return t;
        }

        // ---- type dropdown ----

        static List<string> TypeChoices() => new List<string>
        { "Traffic Lane", "Turn Lane", "Bike Lane", "Shoulder", "Sidewalk", "Median", "Rail" };

        static string TypeLabel(CorridorType t) => t switch
        {
            CorridorType.Traffic => "Traffic Lane",
            CorridorType.Turn => "Turn Lane",
            CorridorType.Bike => "Bike Lane",
            CorridorType.Shoulder => "Shoulder",
            CorridorType.Sidewalk => "Sidewalk",
            CorridorType.Median => "Median",
            CorridorType.Rail => "Rail",
            _ => "Traffic Lane",
        };

        static CorridorType ParseType(string v) => v switch
        {
            "Traffic Lane" => CorridorType.Traffic,
            "Turn Lane" => CorridorType.Turn,
            "Bike Lane" => CorridorType.Bike,
            "Shoulder" => CorridorType.Shoulder,
            "Sidewalk" => CorridorType.Sidewalk,
            "Median" => CorridorType.Median,
            "Rail" => CorridorType.Rail,
            _ => CorridorType.Traffic,
        };

        VisualElement DropdownRow(string label, Func<List<string>> choices, Func<string> get, Action<string> set)
        {
            var row = Row(label);
            var dd = new DropdownField { choices = choices(), value = get() };
            dd.style.width = 130;
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

        // ---- profile <-> stack ----

        static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "road-profile";
            name = name.Trim().Replace(' ', '-');
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name;
        }

        SavedConfig BuildConfig()
        {
            string id = Sanitize(_name);
            // Dual-write: the stack is the source of truth; Road is the derived projection consumers read.
            RoadProfile road = _stack.ToRoadProfile(id);
            string cat = string.IsNullOrWhiteSpace(_category) ? "Uncategorized" : _category.Trim();
            // Store an INDEPENDENT copy — sharing the live _stack would let later edits (and LoadConfig's
            // clear-then-copy, where st aliases _stack) corrupt the saved profile.
            return new SavedConfig { Id = id, Name = _name, Category = cat, Road = road, Corridor = CloneStack(_stack) };
        }

        static CorridorStack CloneStack(CorridorStack s)
        {
            var c = new CorridorStack();
            if (s == null) return c;
            if (s.AB != null) foreach (CorridorSegment seg in s.AB) c.AB.Add(CloneSeg(seg));
            if (s.BA != null) foreach (CorridorSegment seg in s.BA) c.BA.Add(CloneSeg(seg));
            if (s.Center != null) foreach (CorridorSegment seg in s.Center) c.Center.Add(CloneSeg(seg));
            return c;
        }

        static CorridorSegment CloneSeg(CorridorSegment s) => new CorridorSegment(s.Type, s.Width)
        { HOV = s.HOV, Fence = s.Fence, Parapet = s.Parapet, ParapetHeight = s.ParapetHeight, Guardrail = s.Guardrail };

        void SaveCurrent()
        {
            RoadProfileLibrary.SaveUserConfig(BuildConfig());
            RebuildId("Road");   // refresh the Road palette dropdown + thumbnails live
        }

        // Start a fresh, empty profile (clears the stacks + resets the name).
        void NewProfile()
        {
            _stack.AB.Clear(); _stack.BA.Clear(); _stack.Center.Clear();
            _name = "new-road-profile"; _category = "";
            _target = 2; _addType = CorridorType.Traffic; _addWidth = CorridorStack.DefaultWidth(CorridorType.Traffic);
            RefreshStackList(); RefreshPreview(); Rebuild();
        }

        void LoadConfig(SavedConfig c)
        {
            if (c == null) return;
            _name = c.Name ?? c.Id;
            _category = c.Category ?? "";
            CorridorStack st = CloneStack(c.Corridor ?? CorridorStack.FromRoadProfile(c.Road));   // independent copy
            _stack.AB.Clear(); _stack.AB.AddRange(st.AB);
            _stack.BA.Clear(); _stack.BA.AddRange(st.BA);
            _stack.Center.Clear(); _stack.Center.AddRange(st.Center);
            RefreshStackList();
            RefreshPreview();
            Rebuild();   // re-read name/category fields
        }

        void RefreshList()
        {
            if (_listBox == null) return;
            _listBox.Clear();
            var user = new List<SavedConfig>(RoadProfileLibrary.UserConfigs);
            user.Sort((a, b) => string.Compare(a?.Name ?? a?.Id, b?.Name ?? b?.Id, System.StringComparison.OrdinalIgnoreCase));
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
                b.style.height = 24; b.style.marginBottom = 2;
                b.style.unityTextAlign = TextAnchor.MiddleLeft;
                _listBox.Add(b);
            }
        }

        // ---- preview: 3D sweep + 2D cross-section strip ----

        void RefreshPreview()
        {
            var bands = new List<RoadCrossSectionBuilder.StackBand>();
            RoadCrossSection xs = RoadCrossSectionBuilder.FromStack(_stack, bands);
            var marks = RoadCrossSectionBuilder.StackMarkings(bands, xs.Width);

            EnsureRig();
            _rig.SetCrossSection(xs, xs.Width, marks, bands);
            if (_view3d != null && _rig.Texture != null)
                _view3d.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(_rig.Texture));

            if (_preview == null) return;
            _preview.Clear();

            var total = new Label($"{xs.Width:0.#} m wide · {_stack.AB.Count}+{_stack.BA.Count} segments"
                                  + (string.IsNullOrWhiteSpace(_category) ? "" : " · " + _category)
                                  + "   · cyan = A↔B midline   (drag to orbit · wheel to zoom)");
            total.style.color = Sub; total.style.fontSize = 11; total.style.marginBottom = 4;
            _preview.Add(total);

            var sec = HBox();
            sec.style.height = 56; sec.style.alignItems = Align.Stretch;
            SetBorder(sec, 1, new Color(1f, 1f, 1f, 0.4f));
            // One flex band per non-zero-width cross-section segment (skip the zero-width walls).
            for (int i = 0; i < xs.Segs.Count; i++)
            {
                float w = xs.Pts[i + 1].x - xs.Pts[i].x;
                if (w <= 0.01f) continue;
                var box = new VisualElement();
                box.style.flexGrow = w;
                box.style.backgroundColor = SurfaceColor(xs.Segs[i]);
                box.style.borderRightWidth = 1; box.style.borderRightColor = new Color(1f, 1f, 1f, 0.25f);
                sec.Add(box);
            }
            // The A→B / B→A midline marker (cyan), absolutely positioned over the strip at the split fraction.
            if (xs.SplitU >= 0f && xs.Width > 0.5f)
            {
                var mark = new VisualElement();
                mark.style.position = Position.Absolute;
                mark.style.top = 0; mark.style.bottom = 0; mark.style.width = 2;
                mark.style.left = Length.Percent(Mathf.Clamp01(xs.SplitU / xs.Width) * 100f);
                mark.style.backgroundColor = new Color(0.31f, 0.86f, 1f, 1f);
                sec.Add(mark);
            }
            _preview.Add(sec);
        }

        static Color SurfaceColor(RoadSurface s) => s switch
        {
            RoadSurface.Asphalt => new Color(0.18f, 0.18f, 0.20f),
            RoadSurface.Shoulder => new Color(0.22f, 0.22f, 0.23f),
            RoadSurface.Concrete => new Color(0.62f, 0.62f, 0.60f),
            RoadSurface.Grass => new Color(0.30f, 0.45f, 0.22f),
            RoadSurface.Curb => new Color(0.72f, 0.72f, 0.72f),
            RoadSurface.Sidewalk => new Color(0.66f, 0.66f, 0.67f),
            RoadSurface.Guardrail => new Color(0.56f, 0.57f, 0.60f),
            RoadSurface.Rail => new Color(0.30f, 0.28f, 0.26f),
            RoadSurface.Fence => new Color(0.50f, 0.42f, 0.32f),
            RoadSurface.Parapet => new Color(0.60f, 0.60f, 0.58f),
            RoadSurface.Bike => new Color(0.20f, 0.30f, 0.22f),
            _ => new Color(0.25f, 0.25f, 0.25f),
        };
    }
}
