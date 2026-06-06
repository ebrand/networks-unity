// Scratch / experimental palette: a place to wire up new UI + features without touching
// the production palettes (Terrain/System/Rail). Currently hosts the test-train controls.
// Auto-gets a launcher button (registry-driven); shared plumbing is in PaletteBase.

using UnityEngine;
using UnityEngine.UIElements;

namespace NetworkDesigner.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class SpikePalette : PaletteBase
    {
        public override string PaletteId => "Spike";
        protected override string Title => "Spike";
        protected override Color Accent => new Color(0.72f, 0.5f, 0.95f);   // purple = scratch
        protected override float PanelWidth => 300f;

        // Not a brushing mode — exit any rail/scatter so no tool stays live behind it.
        protected override void OnOpened() => Designer.EnterSculptMode();

        protected override void BuildBody(VisualElement body)
        {
            body.Add(SectionLabel("TRAINS"));
            var trainRow = HBox();
            var spawn = MakeButton("Spawn Train", () => FindOrCreateManager().SpawnTestTrain());
            var clear = MakeButton("Clear", () => FindManager()?.ClearTrains());
            spawn.style.marginRight = 6;
            trainRow.Add(spawn); trainRow.Add(clear);
            body.Add(trainRow);
            // Live on the running train (TrainManager pushes these each frame).
            body.Add(NumberRow("Ride Height", "m",
                () => { var m = FindManager(); return m != null ? m.RideHeight : 0f; },
                v => FindOrCreateManager().RideHeight = v, -2f, 5f, "0.00"));
            body.Add(NumberRow("Car Spacing", "m",
                () => { var m = FindManager(); return m != null ? m.CarSpacing : 18f; },
                v => FindOrCreateManager().CarSpacing = v, 4f, 80f, "0.#"));
        }

        NetworkDesigner.Trains.TrainManager _tm;
        NetworkDesigner.Trains.TrainManager FindManager()
            => _tm != null ? _tm : (_tm = FindFirstObjectByType<NetworkDesigner.Trains.TrainManager>());

        // Find the scene's TrainManager or make one (so Spawn / the tunables always work);
        // with no prefabs assigned it spawns placeholder cubes until you set Locomotive/Wagon.
        NetworkDesigner.Trains.TrainManager FindOrCreateManager()
        {
            var m = FindManager();
            if (m == null)
            {
                m = _tm = new GameObject("TrainManager").AddComponent<NetworkDesigner.Trains.TrainManager>();
                Debug.LogWarning("[SpikePalette] Created a TrainManager — assign Locomotive_01 / " +
                    "Wagon_01 prefabs on it for real trains (placeholder cubes until then).");
            }
            return m;
        }
    }
}
