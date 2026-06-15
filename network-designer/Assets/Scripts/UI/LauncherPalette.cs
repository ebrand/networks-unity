// The default launcher palette (UI Toolkit): a small, always-visible dock at bottom-left
// that never goes away. Holds a radio button per other palette — clicking one opens it
// exclusively (closing the others) and re-clicking the open one closes it. No footer of
// its own (the footer rides on whichever content palette is open). Plumbing: PaletteBase.

using UnityEngine;
using UnityEngine.UIElements;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class LauncherPalette : PaletteBase
    {
        public override string PaletteId => "Launcher";
        public override bool Toggleable => false;        // no button for itself
        protected override bool ShouldShow() => true;    // always visible
        protected override bool AnchorBottom => true;    // bottom-right (clear of the
        protected override bool AnchorRight => true;     // top-left content palettes)
        protected override bool ShowFooter => false;     // footer lives on the open palette
        protected override float PanelWidth => 76f;   // slim: buttons are just hotkey labels

        protected override void BuildBody(VisualElement body)
        {
            // One thin toggle button per other (toggleable) palette, labelled with its hotkey.
            foreach (var p in All)
            {
                if (!p.Toggleable) continue;
                var pal = p;   // capture
                // Radio: open this one (closing the others, running its OnOpened hook),
                // or close it if it's already open. Same path as the N/Y/L/K hotkeys.
                var b = MakeButton(pal.MenuLabel, () => ToggleExclusive(pal.PaletteId));
                b.style.height = 22;            // thin
                b.style.fontSize = 12;
                b.style.marginBottom = 4;
                body.Add(b);
                _sync.Add(() => StyleActive(b, pal.IsOpen));
            }

            // Chunk-world toggles (shown only in a chunk world, highlighted when on).
            // Bubble-lock (🫧): 🔒 locked / 🔓 streaming. Hotkey M.
            var lockBtn = MakeButton("🫧🔓", () => Designer.ChunkLockBubble = !Designer.ChunkLockBubble);
            lockBtn.style.height = 22;
            lockBtn.style.fontSize = 15;
            lockBtn.style.marginTop = 6;
            body.Add(lockBtn);
            _sync.Add(() =>
            {
                bool active = Designer.ChunkTestActive;
                lockBtn.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
                if (active)
                {
                    lockBtn.text = Designer.ChunkLockBubble ? "🫧🔒" : "🫧🔓";   // closed = locked, open = streaming
                    StyleActive(lockBtn, Designer.ChunkLockBubble);
                }
            });

            // Grid (1 km/100 m) and Topo contours (J) toggles below the lock.
            ChunkToggle("▦", () => Designer.ChunkShowGrid, v => Designer.ChunkShowGrid = v);
            ChunkToggle("〰", () => Designer.ChunkContours, v => Designer.ChunkContours = v);
            ChunkToggle("⛰", () => Designer.ChunkRidges, _ => Designer.ToggleRidges());   // ridge/valley curvature overlay (persisted)
            ChunkToggle("💧", () => Designer.HydrologyShow, v => Designer.HydrologyShow = v);   // drainage/catchment analysis

            void ChunkToggle(string icon, System.Func<bool> get, System.Action<bool> set)
            {
                var b = MakeButton(icon, () => set(!get()));
                b.style.height = 22;
                b.style.fontSize = 15;
                b.style.marginTop = 4;
                body.Add(b);
                _sync.Add(() =>
                {
                    bool active = Designer.ChunkTestActive;
                    b.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
                    if (active) StyleActive(b, get());
                });
            }
        }
    }
}
