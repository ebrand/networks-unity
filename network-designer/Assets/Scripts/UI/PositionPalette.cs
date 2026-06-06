// Small always-on HUD (top-right) showing the camera's world position — altitude (Y)
// and X/Z — useful for orienting on the big DEM worlds. Not toggleable and always
// visible (independent of the launcher's exclusive palette state). Auto-spawns so no
// scene setup is needed.

using UnityEngine;
using UnityEngine.UIElements;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class PositionPalette : PaletteBase
    {
        public override string PaletteId => "Position";
        public override bool Toggleable => false;          // no launcher button
        protected override bool ShouldShow() => true;      // always visible
        protected override bool ShowFooter => false;
        protected override bool AnchorRight => true;       // top-right
        protected override string Title => null;
        protected override float PanelWidth => 150f;
        protected override Color Accent => new Color(0.55f, 0.7f, 0.95f);

        Camera _cam;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindFirstObjectByType<PositionPalette>() != null) return;
            var go = new GameObject("PositionPalette (auto)");
            go.AddComponent<UIDocument>();
            go.AddComponent<PositionPalette>();
        }

        protected override void BuildBody(VisualElement body)
        {
            var altRow = ValueRow("Alt", out Label altV);
            var xRow = ValueRow("X", out Label xV);
            var zRow = ValueRow("Z", out Label zV);
            body.Add(altRow); body.Add(xRow); body.Add(zRow);

            _sync.Add(() =>
            {
                if (_cam == null) _cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
                if (_cam == null) return;
                Vector3 p = _cam.transform.position;
                altV.text = p.y.ToString("#,0") + " m";
                xV.text = p.x.ToString("#,0") + " m";
                zV.text = p.z.ToString("#,0") + " m";
            });
        }

        VisualElement ValueRow(string label, out Label value)
        {
            var row = HBox();
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 2;
            var l = new Label(label);
            l.style.color = Sub; l.style.fontSize = 12;
            value = new Label("—");
            value.style.color = Ink; value.style.fontSize = 13;
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(l); row.Add(value);
            return row;
        }
    }
}
