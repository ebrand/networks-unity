// Scatter/Fence palette (UI Toolkit) — consolidates the Tree / Rock scatter layers and
// the Fence linework layer into one palette. Phase 1: mode buttons (Trees/Rocks/Fences),
// the shared scatter brush Strength/Density sliders, and a pack dropdown per scatter
// layer. Phase 2 will add the prefab thumbnail grid and the "..." pack-management modal
// (with a live 3D preview); the ellipsis buttons are stubbed for now. While this palette
// is open the old IMGUI scatter palette is suppressed (TerrainDesigner). Plumbing: PaletteBase.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class ScatterFencePalette : PaletteBase
    {
        public override string PaletteId => "ScatterFence";
        public override string MenuLabel => "T/R/F";
        protected override string Title => "Scatter/Fence";
        protected override Color Accent => new Color(0.55f, 0.8f, 0.45f);   // foliage green
        protected override float PanelWidth => 360f;

        protected override string FooterMode =>
            Designer.IsTreeMode ? "Trees" : Designer.IsRockMode ? "Rocks"
            : Designer.IsFenceMode ? "Fences" : "Scatter";
        protected override string FooterSub => "Place";

        // Opening defaults to Trees scatter.
        protected override void OnOpened() => Designer.EnterTreeMode();

        // Density slider maps inversely to Spacing (denser = smaller spacing).
        const float MinSp = 0.5f, MaxSp = 15f;
        static float DensityOf(float spacing) => Mathf.InverseLerp(MaxSp, MinSp, spacing);
        static float SpacingOf(float density) => Mathf.Lerp(MaxSp, MinSp, density);

        // The scatter layer the shared Strength/Density sliders drive (Rocks if in rock
        // mode, else Trees).
        ScatterLayer ActiveScatter() => Designer.IsRockMode ? Designer.RockLayer : Designer.TreeLayer;

        protected override void BuildBody(VisualElement body)
        {
            body.Add(SectionLabel("SCATTER BRUSH"));
            body.Add(SliderRow("Strength",
                () => ActiveScatter().PaintRate, v => ActiveScatter().PaintRate = v, 1f, 100f, "0"));
            body.Add(SliderRow("Density",
                () => DensityOf(ActiveScatter().Spacing), v => ActiveScatter().Spacing = SpacingOf(v), 0f, 1f, "0.00"));

            body.Add(ScatterRow("Trees (T)", Designer.TreeLayer, () => Designer.IsTreeMode, Designer.EnterTreeMode));
            body.Add(ScatterRow("Rocks (R)", Designer.RockLayer, () => Designer.IsRockMode, Designer.EnterRockMode));

            var fBtn = MakeButton("Fences (F)", () => Designer.EnterFenceMode());
            fBtn.style.flexGrow = 0; fBtn.style.width = 100; fBtn.style.marginTop = 4;
            body.Add(fBtn);
            _sync.Add(() => StyleActive(fBtn, Designer.IsFenceMode));

            // TODO Phase 2: prefab thumbnail grid of the active layer's pack here.
        }

        // [ mode button ] [ pack dropdown ] [ ... ]
        VisualElement ScatterRow(string label, ScatterLayer layer, System.Func<bool> active, System.Action enter)
        {
            var row = HBox(); row.style.marginBottom = 6;

            var btn = MakeButton(label, () => enter());
            btn.style.flexGrow = 0; btn.style.flexShrink = 0; btn.style.width = 84; btn.style.marginRight = 6;
            _sync.Add(() => StyleActive(btn, active()));

            var dd = new DropdownField();
            dd.style.flexGrow = 1; dd.style.flexShrink = 1; dd.style.minWidth = 0;
            dd.RegisterValueChangedCallback(e => layer.SelectPackByName(e.newValue));
            // Keep choices + value live: packs may load AFTER this row is built, or be
            // created/deleted in the management modal. Only reassign when the set changed
            // (so an open dropdown isn't reset every frame).
            _sync.Add(() =>
            {
                var names = layer.PackNames();
                var cur = dd.choices;
                bool changed = cur == null || cur.Count != names.Count;
                if (!changed) for (int i = 0; i < names.Count; i++) if (cur[i] != names[i]) { changed = true; break; }
                if (changed) dd.choices = names;
                string a = layer.ActivePackName;
                if (dd.value != a) dd.SetValueWithoutNotify(a);
            });

            var dots = MakeButton("...", () => OpenPackModal(layer));
            dots.style.flexGrow = 0; dots.style.flexShrink = 0; dots.style.width = 30; dots.style.marginLeft = 6;

            row.Add(btn); row.Add(dd); row.Add(dots);
            return row;
        }

        // --- Pack-management modal (Stage 1: pack list + name/save + delete + sliders;
        // the prefab grid and live 3D preview are placeholders, filled in later stages).
        void OpenPackModal(ScatterLayer layer)
        {
            // Fixed layout widths (panel px) — explicit so the grid wraps within bounds
            // instead of bleeding past the modal's right edge.
            const float modalW = 900f, listW = 180f, gap = 10f;
            const float gridW = modalW - 32f - listW - gap;   // 32 = modal L/R padding
            const float previewW = modalW - 32f, previewH = 360f;  // full inner width, fixed height
            float h = Mathf.Min(960f, Screen.height - 40f);
            // No backdrop-click dismissal: closing routes through the Close button so we
            // can warn about unsaved changes first.
            var modal = BeginModal(layer.Name + " Pack Management", modalW, h, out System.Action close,
                                   closeOnBackdropClick: false);
            if (modal == null) return;

            // SliderRow registers per-frame _sync closures; trim them when the modal
            // closes (any path) so they don't pile up on the palette's permanent list.
            int syncMark = _sync.Count;
            modal.RegisterCallback<DetachFromPanelEvent>(_ =>
            { if (_sync.Count > syncMark) _sync.RemoveRange(syncMark, _sync.Count - syncMark); });

            string selected = layer.ActivePackName;

            // Widgets referenced by the rebuild closure — declare first.
            var list = ScrollBox(230f);
            list.style.width = listW; list.style.flexShrink = 0;
            list.style.backgroundColor = DarkInk; Radius(list, 8);
            var nameField = new TextField { value = selected };
            var rowBtns = new List<(string name, Button btn)>();   // for live "*" dirty marking

            void Rebuild()
            {
                list.Clear();
                rowBtns.Clear();
                var names = layer.PackNames();
                if (names.Count == 0)
                {
                    var empty = new Label("  (no packs yet)");
                    empty.style.color = Sub; empty.style.paddingTop = 8;
                    list.Add(empty);
                }
                for (int i = 0; i < names.Count; i++)
                {
                    string n = names[i];
                    var b = MakeButton(n, () =>
                    {
                        selected = n;
                        layer.SelectPackByName(n);        // restores membership + brush params
                        nameField.SetValueWithoutNotify(n);
                        Rebuild();
                    });
                    StyleActive(b, n == selected);
                    b.style.marginBottom = 4;
                    list.Add(b);
                    rowBtns.Add((n, b));
                }
            }
            Rebuild();

            // Live "*" suffix on the selected pack while its membership/params differ
            // from the saved state; clears automatically once saved (no longer dirty).
            _sync.Add(() =>
            {
                for (int i = 0; i < rowBtns.Count; i++)
                {
                    string n = rowBtns[i].name; Button b = rowBtns[i].btn;
                    bool dirty = (n == selected) && layer.PackIsDirty(n);
                    string want = dirty ? n + "   *" : n;
                    if (b.text != want) b.text = want;
                }
            });

            // --- top: pack list (left) + prefab thumbnail grid (right) ---
            var top = HBox(); top.style.alignItems = Align.FlexStart;
            top.Add(list);

            // Prefab grid: one tile per prefab; highlight = in the current selection
            // (the membership a pack save captures); click toggles it + selects it for
            // the preview. Thumbnails bake progressively (one/frame) so the open is snappy.
            GameObject selectedPrefab = null;
            var grid = ScrollBox(230f);
            grid.style.width = gridW; grid.style.flexShrink = 0;
            grid.style.backgroundColor = DarkInk; Radius(grid, 8);
            grid.horizontalScrollerVisibility = ScrollerVisibility.Hidden;   // wrap, don't scroll sideways
            grid.contentContainer.style.flexDirection = FlexDirection.Row;
            grid.contentContainer.style.flexWrap = Wrap.Wrap;
            grid.contentContainer.style.paddingLeft = 6; grid.contentContainer.style.paddingTop = 6;

            int count = layer.PrefabCount;
            var tiles = new VisualElement[count];
            var imgEls = new Image[count];
            var imgSet = new bool[count];
            var lastOn = new bool[count];
            for (int gi = 0; gi < count; gi++)
            {
                int idx = gi;
                var tile = new VisualElement();
                tile.style.width = 64; tile.style.height = 64;
                tile.style.marginRight = 6; tile.style.marginBottom = 6;
                tile.style.backgroundColor = new Color(1f, 1f, 1f, 0.05f);
                Radius(tile, 6);
                tile.style.borderTopWidth = 2; tile.style.borderBottomWidth = 2;
                tile.style.borderLeftWidth = 2; tile.style.borderRightWidth = 2;
                var img = new Image { scaleMode = ScaleMode.ScaleToFit };
                img.style.flexGrow = 1;
                tile.Add(img);
                tile.RegisterCallback<ClickEvent>(_ =>
                {
                    layer.SetEnabled(idx, !layer.IsEnabled(idx));   // toggle membership
                    selectedPrefab = layer.PrefabAt(idx);           // select for preview (Stage 3)
                });
                tiles[idx] = tile; imgEls[idx] = img;
                lastOn[idx] = !layer.IsEnabled(idx);                // force first highlight apply
                grid.Add(tile);
            }

            // Right column: an All / None membership header above the grid.
            var gridCol = new VisualElement();
            gridCol.style.marginLeft = gap; gridCol.style.flexShrink = 0;
            var gridHeader = HBox(); gridHeader.style.justifyContent = Justify.FlexEnd; gridHeader.style.marginBottom = 4;
            var allBtn = MakeButton("All", () => layer.SetAllEnabled(true));
            var noneBtn = MakeButton("None", () => layer.SetAllEnabled(false));
            allBtn.style.flexGrow = 0; allBtn.style.width = 64; allBtn.style.marginRight = 6;
            noneBtn.style.flexGrow = 0; noneBtn.style.width = 64;
            gridHeader.Add(allBtn); gridHeader.Add(noneBtn);
            gridCol.Add(gridHeader); gridCol.Add(grid);
            top.Add(gridCol);

            // Progressive thumbnail bake + membership-highlight refresh.
            Color offCol = new Color(1f, 1f, 1f, 0.10f);
            _sync.Add(() =>
            {
                layer.EnsureOneThumb();                             // bake one missing thumb/frame
                for (int i = 0; i < count; i++)
                {
                    if (!imgSet[i])
                    {
                        Texture tx = layer.GetThumb(layer.PrefabAt(i));
                        if (tx != null) { imgEls[i].image = tx; imgSet[i] = true; }
                    }
                    bool on = layer.IsEnabled(i);
                    if (on != lastOn[i])
                    {
                        Color bc = on ? Accent : offCol;
                        tiles[i].style.borderTopColor = bc; tiles[i].style.borderBottomColor = bc;
                        tiles[i].style.borderLeftColor = bc; tiles[i].style.borderRightColor = bc;
                        tiles[i].style.opacity = on ? 1f : 0.45f;
                        lastOn[i] = on;
                    }
                }
            });

            modal.Add(top);

            modal.Add(Divider());

            // --- name + Update/Save ---
            var nameRow = HBox(); nameRow.style.marginBottom = 8;
            var nameLbl = new Label("Name"); nameLbl.style.color = Ink; nameLbl.style.minWidth = 60;
            nameField.style.flexGrow = 1; nameField.style.marginRight = 6;
            var saveBtn = MakeButton("Update / Save", () =>
            {
                string nm = (nameField.value ?? "").Trim();
                if (nm.Length == 0) return;
                layer.CreatePack(nm);                 // create or replace by name
                selected = nm;
                Designer.OnScatterPacksChanged();
                Rebuild();
            });
            saveBtn.style.flexGrow = 0; saveBtn.style.width = 130;
            StyleActive(saveBtn, true);
            nameRow.Add(nameLbl); nameRow.Add(nameField); nameRow.Add(saveBtn);
            modal.Add(nameRow);

            // --- brush params (per-pack; captured on save) ---
            modal.Add(SliderRow("Strength", () => layer.PaintRate, v => layer.PaintRate = v, 1f, 100f, "0"));
            modal.Add(SliderRow("Density",
                () => DensityOf(layer.Spacing), v => layer.Spacing = SpacingOf(v), 0f, 1f, "0.00"));

            // --- delete ---
            var delRow = HBox(); delRow.style.justifyContent = Justify.FlexEnd; delRow.style.marginBottom = 8;
            var delBtn = MakeButton("Delete", () =>
            {
                if (string.IsNullOrEmpty(selected)) return;
                layer.DeletePackByName(selected);
                selected = ""; nameField.SetValueWithoutNotify("");
                Designer.OnScatterPacksChanged();
                Rebuild();
            });
            delBtn.style.flexGrow = 0; delBtn.style.width = 100;
            delRow.Add(delBtn);
            modal.Add(delRow);

            // --- rotatable 3D preview — OFF by default (rendering a full NM tree every frame is costly).
            // A button builds the viewer on first click and shows/hides it; the render loop no-ops while hidden.
            NetworkDesigner.Terrain.RuntimePrefabViewer viewer = null;
            Image previewImg = null;
            bool dragging = false; Vector2 dragLast = default;
            GameObject lastShown = null; float spinAccum = 0f;

            var preview = new VisualElement();
            preview.style.height = previewH; preview.style.flexShrink = 0;
            preview.style.backgroundColor = DarkInk; Radius(preview, 8);
            preview.style.overflow = Overflow.Hidden;
            preview.style.display = DisplayStyle.None;   // hidden until expanded
            preview.style.marginTop = 4;

            Button prevToggle = null;
            prevToggle = MakeButton("Show 3D preview", () =>
            {
                bool show = preview.style.display == DisplayStyle.None;
                if (show && viewer == null)   // lazy-build on first expand
                {
                    viewer = new NetworkDesigner.Terrain.RuntimePrefabViewer(
                        (int)previewW, (int)previewH, new Color(0.06f, 0.07f, 0.08f, 1f));
                    modal.RegisterCallback<DetachFromPanelEvent>(_ => viewer.Dispose());
                    previewImg = new Image { image = viewer.Texture, scaleMode = ScaleMode.ScaleToFit };
                    previewImg.style.flexGrow = 1;
                    preview.Add(previewImg);
                    previewImg.RegisterCallback<PointerDownEvent>(e =>
                    { dragging = true; dragLast = new Vector2(e.position.x, e.position.y); previewImg.CapturePointer(e.pointerId); });
                    previewImg.RegisterCallback<PointerMoveEvent>(e =>
                    {
                        if (!dragging) return;
                        Vector2 p = new Vector2(e.position.x, e.position.y);
                        Vector2 d = p - dragLast; dragLast = p;
                        viewer.Yaw += d.x * 0.5f;
                        viewer.Pitch = Mathf.Clamp(viewer.Pitch - d.y * 0.5f, -85f, 85f);
                    });
                    previewImg.RegisterCallback<PointerUpEvent>(e =>
                    { dragging = false; previewImg.ReleasePointer(e.pointerId); });
                    lastShown = null;   // force a render on first show
                }
                preview.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                prevToggle.text = show ? "Hide 3D preview" : "Show 3D preview";
            });
            prevToggle.style.marginTop = 6;
            modal.Add(prevToggle);
            modal.Add(preview);

            // Render only when expanded — on a new prefab, while dragging, and a throttled ~12 fps auto-spin.
            _sync.Add(() =>
            {
                if (viewer == null || preview.style.display == DisplayStyle.None) return;   // collapsed → no render at all
                GameObject show = selectedPrefab;
                if (show == null)
                {
                    for (int i = 0; i < count; i++) if (layer.IsEnabled(i)) { show = layer.PrefabAt(i); break; }
                    if (show == null) show = layer.PrefabAt(0);
                }
                bool needRender = false;
                if (show != lastShown) { viewer.SetPrefab(show); lastShown = show; needRender = true; }
                if (dragging) needRender = true;                       // smooth while orbiting
                else
                {
                    spinAccum += Time.deltaTime;
                    if (spinAccum >= 0.08f) { viewer.Yaw += 18f * spinAccum; spinAccum = 0f; needRender = true; }  // ~12 fps idle spin
                }
                if (needRender) { viewer.Render(); previewImg.MarkDirtyRepaint(); }
            });

            // --- close (warn first if the selected pack has unsaved edits) ---
            var closeRow = HBox(); closeRow.style.justifyContent = Justify.FlexEnd; closeRow.style.marginTop = 10;
            var closeBtn = MakeButton("Close", () =>
            {
                if (!string.IsNullOrEmpty(selected) && layer.PackIsDirty(selected))
                    ShowConfirm("Unsaved changes",
                        $"\"{selected}\" has unsaved changes. Discard them?", "Discard", close);
                else
                    close();
            });
            closeBtn.style.flexGrow = 0; closeBtn.style.width = 100;
            closeRow.Add(closeBtn);
            modal.Add(closeRow);
        }
    }
}
