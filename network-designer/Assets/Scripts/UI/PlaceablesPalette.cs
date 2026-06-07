// Placeables palette (UI Toolkit): lists the prefabs in Assets/Resources/Placeables,
// click one to select it, then click on the terrain to place it. A Physics toggle drops
// objects with a Rigidbody (settle on the ground) vs places them static. Placement is
// handled by PlaceablesManager (only while this palette is open). Shared plumbing is in
// PaletteBase.

using UnityEngine;
using UnityEngine.UIElements;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class PlaceablesPalette : PaletteBase
    {
        public override string PaletteId => "Placeables";
        public override string MenuLabel => "O";   // hotkey O = Objects/placeables
        protected override string Title => "Placeables";
        protected override Color Accent => new Color(0.95f, 0.66f, 0.32f);   // orange
        protected override float PanelWidth => 280f;
        protected override string FooterMode => "Placeables";
        protected override string FooterSub => string.Empty;

        // Not a brushing mode — exit rail/scatter so only placement is live behind it.
        protected override void OnOpened() => Designer.EnterSculptMode();

        protected override void BuildBody(VisualElement body)
        {
            var mgr = FindOrCreateManager();

            body.Add(ToggleRow("Physics (drop)",
                () => FindOrCreateManager().DropPhysics, v => FindOrCreateManager().DropPhysics = v));

            body.Add(Divider());
            body.Add(SectionLabel("PICK one → ghost follows cursor · RMB-drag rotates · LMB places."
                + " Deselect (re-click) → drag placed objects to move."));

            var prefabs = mgr.Prefabs;
            if (prefabs == null || prefabs.Length == 0)
            {
                var hint = new Label("No placeables found.\nDrop prefabs into Assets/Resources/Placeables, then re-enter Play.");
                hint.style.color = Sub; hint.style.fontSize = 12; hint.style.whiteSpace = WhiteSpace.Normal;
                body.Add(hint);
            }
            else
            {
                var list = ScrollBox(240f);   // scroll the list; Physics/Clear stay put
                foreach (var p in prefabs)
                {
                    var prefab = p;
                    // Toggle: select for ghost-place, or click the active one to deselect
                    // (no selection = grab/move mode).
                    var b = MakeButton(prefab.name, () =>
                    {
                        var m = FindOrCreateManager();
                        m.Selected = m.Selected == prefab ? null : prefab;
                    });
                    b.style.marginBottom = 4;
                    list.Add(b);
                    _sync.Add(() => StyleActive(b, FindManager() != null && FindManager().Selected == prefab));
                }
                body.Add(list);
            }

            body.Add(Divider());
            body.Add(MakeButton("Clear Placed", () => FindManager()?.ClearPlaced()));
        }

        NetworkDesigner.Placeables.PlaceablesManager _mgr;
        NetworkDesigner.Placeables.PlaceablesManager FindManager()
            => _mgr != null ? _mgr : (_mgr = FindFirstObjectByType<NetworkDesigner.Placeables.PlaceablesManager>());

        NetworkDesigner.Placeables.PlaceablesManager FindOrCreateManager()
        {
            var m = FindManager();
            if (m == null)
                m = _mgr = new GameObject("PlaceablesManager")
                    .AddComponent<NetworkDesigner.Placeables.PlaceablesManager>();
            return m;
        }
    }
}
