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
        protected override bool AnchorBottom => true;    // bottom-left
        protected override bool ShowFooter => false;     // footer lives on the open palette
        protected override float PanelWidth => 210f;

        protected override void BuildBody(VisualElement body)
        {
            body.Add(SectionLabel("PALETTES"));
            // One toggle button per other (toggleable) palette. All palettes have
            // registered in Awake by the time this Start-time body build runs.
            foreach (var p in All)
            {
                if (!p.Toggleable) continue;
                var pal = p;   // capture
                // Radio: open this one (closing the others), or close it if it's already open.
                var b = MakeButton(pal.MenuLabel, () => SetExclusive(pal.IsOpen ? null : pal.PaletteId));
                b.style.marginBottom = 6;
                body.Add(b);
                _sync.Add(() => StyleActive(b, pal.IsOpen));
            }
        }
    }
}
