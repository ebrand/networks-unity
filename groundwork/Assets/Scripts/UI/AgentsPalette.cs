// In-game car-agent palette (UI Toolkit): spawn / clear / pause a traffic sim on the BUILT road network.
// Self-spawns at runtime and appears as a launcher button. The sim itself is hosted by TerrainDesigner
// (SpawnAgents / ClearAgents / ToggleAgentsPaused over the built-only sub-network). Plumbing: PaletteBase.

using UnityEngine;
using UnityEngine.UIElements;

namespace NetworkDesigner.UI
{
    public class AgentsPalette : PaletteBase
    {
        public override string PaletteId => "Agents";
        public override string MenuLabel => "🚗";
        protected override string Title => "Agents";
        protected override Color Accent => new Color(0.30f, 0.75f, 0.95f);   // traffic blue
        protected override float PanelWidth => 260f;
        protected override string FooterMode => "Agents";
        protected override string FooterSub => string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
            if (FindFirstObjectByType<AgentsPalette>() != null) return;
            var go = new GameObject("AgentsPalette (auto)");
            go.AddComponent<UIDocument>();
            go.AddComponent<AgentsPalette>();
        }

        float _spawnCount = 30f;

        protected override void BuildBody(VisualElement body)
        {
            body.Add(SectionLabel("CAR AGENTS"));
            var hint = Cap("Cars drive the BUILT road network, routing between road dead-ends. Build road segments first.");
            hint.style.whiteSpace = WhiteSpace.Normal; hint.style.marginBottom = 8;
            body.Add(hint);

            body.Add(NumberRow("Spawn count", "",
                () => _spawnCount,
                v => _spawnCount = Mathf.Round(Mathf.Clamp(v, 1f, 500f)), 1f, 500f, "0"));

            var row = HBox(); row.style.marginTop = 8; row.style.marginBottom = 8;
            var spawnBtn = MakeButton("Spawn", () => Designer.SpawnAgents(Mathf.RoundToInt(_spawnCount)));
            spawnBtn.style.flexGrow = 1; spawnBtn.style.marginRight = 6;
            spawnBtn.tooltip = "Spawn cars on the built roads (queued in over a few frames).";
            var clrBtn = MakeButton("Clear", () => Designer.ClearAgents());
            clrBtn.style.flexGrow = 1;
            clrBtn.tooltip = "Despawn all cars.";
            row.Add(spawnBtn); row.Add(clrBtn);
            body.Add(row);

            var pauseBtn = MakeButton("Pause", () => Designer.ToggleAgentsPaused());
            pauseBtn.style.marginBottom = 10;
            pauseBtn.tooltip = "Freeze / resume the running cars.";
            body.Add(pauseBtn);
            _sync.Add(() => StyleActive(pauseBtn, Designer.AgentsPaused));

            var status = Cap(string.Empty);
            body.Add(status);
            _sync.Add(() =>
            {
                bool has = Designer.HasBuiltRoadsForAgents;
                status.text = has
                    ? $"{Designer.AgentCount} car(s) · {(Designer.AgentsPaused ? "paused" : "running")}"
                    : "No built roads — Build some first.";
                spawnBtn.SetEnabled(has);
            });
        }

        Label Cap(string t)
        {
            var l = new Label(t);
            l.style.color = Sub; l.style.fontSize = 12; l.style.marginBottom = 2;
            return l;
        }
    }
}
