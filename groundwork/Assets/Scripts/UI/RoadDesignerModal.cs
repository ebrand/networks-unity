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
        // Modal size — exposed as tunables (corridor.width / corridor.height) so it can be sized live in the React
        // panel. Width sets the panel; "height" sets the 3D preview's height, which drives the modal's overall
        // height (the panel then sizes to content). Changing a tunable rebuilds this modal.
        public static float PanelW = 1120f;
        public static float PanelH = 560f;
        protected override float PanelWidth => Mathf.Clamp(PanelW, 600f, Mathf.Max(600f, Screen.width - 40f));
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

        VisualElement _preview, _listBox, _view3d, _baBox, _centerBox, _abBox;
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
            // Columns stretch to the tallest (the 3D preview at PanelH); the form/save columns are alignSelf=
            // FlexStart so they stay natural height (no empty stretch box), while the profiles list + preview +
            // stack boxes flex-fill that height.
            var topRow = HBox();
            topRow.style.alignItems = Align.Stretch;
            body.Add(topRow);

            // Far-left: saved profiles, full height.
            var profCol = new VisualElement();
            profCol.style.width = 200; profCol.style.marginRight = 16; profCol.style.flexShrink = 0;
            profCol.Add(Cap("Profiles:"));
            _listBox = ScrollBox(2000); _listBox.style.flexGrow = 1;
            SetBorder(_listBox, 1, new Color(0.3f, 0.32f, 0.36f)); Radius(_listBox, 8);
            profCol.Add(_listBox);
            topRow.Add(profCol);

            // Right of profiles: upper (form + 3D preview), lower (save controls + the three direction stacks).
            var rightArea = new VisualElement();
            rightArea.style.flexGrow = 1;
            topRow.Add(rightArea);

            var upper = HBox();
            upper.style.alignItems = Align.Stretch;
            rightArea.Add(upper);

            var formCol = new VisualElement();
            formCol.style.width = 290; formCol.style.marginRight = 16; formCol.style.flexShrink = 0;
            formCol.style.alignSelf = Align.FlexStart;   // natural height (top), don't stretch into empty space
            BuildSegmentForm(formCol);
            formCol.Add(Divider());
            BuildSaveControls(formCol);   // save controls sit directly under the segment form
            upper.Add(formCol);

            var previewCol = new VisualElement();
            previewCol.style.flexGrow = 1;
            _view3d = new VisualElement();
            _view3d.style.flexGrow = 1; _view3d.style.minHeight = PanelH;   // tunable — drives the modal height
            _view3d.style.backgroundColor = new Color(0.08f, 0.09f, 0.10f);
            _view3d.style.unityBackgroundScaleMode = ScaleMode.ScaleAndCrop;
            Radius(_view3d, 8);
            previewCol.Add(_view3d);
            RegisterViewportInput(_view3d);
            _preview = new VisualElement();   // thin caption row under the 3D view
            _preview.style.height = 18; _preview.style.marginTop = 4;
            previewCol.Add(_preview);
            upper.Add(previewCol);

            var lower = HBox();
            lower.style.height = 150; lower.style.marginTop = 8; lower.style.alignItems = Align.Stretch;
            rightArea.Add(lower);

            var spacer = new VisualElement();   // aligns the stack boxes under the 3D preview (matches the form column width)
            spacer.style.width = 290; spacer.style.marginRight = 16; spacer.style.flexShrink = 0;
            lower.Add(spacer);

            var stacks = HBox();
            stacks.style.flexGrow = 1; stacks.style.alignItems = Align.Stretch;
            _baBox = AddStackColumn(stacks, "B → A");
            _centerBox = AddStackColumn(stacks, "Center");
            _abBox = AddStackColumn(stacks, "A → B");
            lower.Add(stacks);

            RefreshList();
            RefreshStackList();
            RefreshPreview();
        }

        // The segment-definition form (top of the second column): direction target + type/width/attrs + Add.
        void BuildSegmentForm(VisualElement col)
        {
            var dirRow = HBox();
            var baBtn = MakeButton("B → A", () => { _target = 0; RefreshStackList(); });
            var ctBtn = MakeButton("Center", () => { _target = 1; RefreshStackList(); });
            var abBtn = MakeButton("A → B", () => { _target = 2; RefreshStackList(); });
            baBtn.style.marginRight = 6; ctBtn.style.marginRight = 6;
            dirRow.Add(baBtn); dirRow.Add(ctBtn); dirRow.Add(abBtn);
            dirRow.style.marginBottom = 10;
            col.Add(dirRow);
            _sync.Add(() => { StyleActive(baBtn, _target == 0); StyleActive(ctBtn, _target == 1); StyleActive(abBtn, _target == 2); });

            col.Add(DropdownRow("Type", TypeChoices, () => TypeLabel(_addType),
                v => { _addType = ParseType(v); _addWidth = CorridorStack.DefaultWidth(_addType); }));
            col.Add(NumberRow("Width", "m", () => _addWidth, v => _addWidth = v, 0.5f, 60f, "0.#"));
            col.Add(ToggleRow("HOV", () => _addHOV, v => _addHOV = v));
            col.Add(ToggleRow("Fence", () => _addFence, v => _addFence = v));
            col.Add(ToggleRow("Parapet", () => _addParapet, v => _addParapet = v));
            col.Add(NumberRow("Height", "m", () => _addParapetH, v => _addParapetH = v, 0.2f, 5f, "0.#"));
            col.Add(ToggleRow("Guardrail", () => _addGuardrail, v => _addGuardrail = v));

            var add = MakeButton("Add →", AddSegment);
            add.style.marginTop = 8;
            col.Add(add);
        }

        // Profile name/category + New/Save/Delete/Close (bottom of the second column).
        void BuildSaveControls(VisualElement col)
        {
            col.Add(Cap("Profile name:"));
            var nameField = new TextField { value = _name };
            nameField.RegisterValueChangedCallback(e => _name = e.newValue);
            col.Add(nameField);
            col.Add(Cap("Category:"));
            var catField = new TextField { value = _category };
            catField.style.marginBottom = 6;
            catField.RegisterValueChangedCallback(e => _category = e.newValue);
            col.Add(catField);

            var saveRow = HBox();
            var newBtn = MakeButton("New", NewProfile);
            var saveBtn = MakeButton("Save", () => { SaveCurrent(); RefreshList(); });
            var delBtn = MakeButton("Delete", () => { RoadProfileLibrary.DeleteUserConfig(Sanitize(_name)); RebuildId("Road"); RefreshList(); });
            newBtn.style.marginRight = 6; saveBtn.style.marginRight = 6;
            saveRow.Add(newBtn); saveRow.Add(saveBtn); saveRow.Add(delBtn);
            saveRow.style.marginTop = 4;
            col.Add(saveRow);

            var close = MakeButton("Close", () => SetOpen(false));
            close.style.marginTop = 6; col.Add(close);
        }

        // A bottom stack column: centered header + a removable segment list; returns the list box.
        VisualElement AddStackColumn(VisualElement parent, string title)
        {
            var col = new VisualElement();
            col.style.flexGrow = 1; col.style.marginRight = 8;
            var h = new Label(title);
            h.style.color = Sub; h.style.fontSize = 11; h.style.unityTextAlign = TextAnchor.MiddleCenter; h.style.marginBottom = 2;
            col.Add(h);
            var box = ScrollBox(2000); box.style.flexGrow = 1;
            SetBorder(box, 1, new Color(0.3f, 0.32f, 0.36f)); Radius(box, 6);
            col.Add(box);
            parent.Add(col);
            return box;
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
            FillBox(_baBox, _stack.BA);
            FillBox(_centerBox, _stack.Center);
            FillBox(_abBox, _stack.AB);
        }

        // Fill one direction's box with its segments (centreline→outer), each removable.
        void FillBox(VisualElement box, List<CorridorSegment> side)
        {
            if (box == null) return;
            box.Clear();
            if (side.Count == 0)
            {
                var none = new Label("  (empty)");
                none.style.color = Sub; none.style.fontSize = 11; none.style.unityFontStyleAndWeight = FontStyle.Italic;
                box.Add(none);
                return;
            }
            for (int i = 0; i < side.Count; i++)
            {
                CorridorSegment s = side[i]; List<CorridorSegment> list = side;
                var r = HBox();
                r.style.marginBottom = 1; r.style.alignItems = Align.Center;
                var lbl = new Label(" " + SegLabel(s));
                lbl.style.color = Ink; lbl.style.fontSize = 12; lbl.style.flexGrow = 1; lbl.style.overflow = Overflow.Hidden;
                var x = MakeButton("✕", () => { list.Remove(s); RefreshStackList(); RefreshPreview(); });
                // Fixed small width, right-justified (MakeButton defaults to flexGrow:1, which would stretch it).
                x.style.flexGrow = 0; x.style.flexShrink = 0;
                x.style.width = 26; x.style.minWidth = 26; x.style.maxWidth = 26; x.style.height = 18;
                r.Add(lbl); r.Add(x);
                box.Add(r);
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

        // ---- preview: 3D sweep (the stack contents are shown in the bottom B→A / Center / A→B boxes) ----

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
            var total = new Label($"{xs.Width:0.#} m wide · {_stack.AB.Count + _stack.BA.Count + _stack.Center.Count} segments"
                                  + (string.IsNullOrWhiteSpace(_category) ? "" : " · " + _category)
                                  + "   · cyan = A↔B midline   (drag to orbit · wheel to zoom)");
            total.style.color = Sub; total.style.fontSize = 11;
            _preview.Add(total);
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
