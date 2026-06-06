// Spawns/clears test trains on the rail network. Add to a GameObject and assign the
// Locomotive + Wagon prefabs (Assets/Vehicles/Low_Poly_Trains/Prefabs/Trains). The Rail
// palette's Spawn/Clear buttons drive this; SpawnTestTrain auto-picks the longest route
// through the current track and runs a loco + WagonCount wagons on it (ping-pong).

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Terrain;

namespace NetworkDesigner.Trains
{
    public class TrainManager : MonoBehaviour
    {
        public TerrainDesigner Designer;
        [Tooltip("Locomotive prefab (e.g. Low_Poly_Trains/Prefabs/Trains/Locomotive_01).")]
        public GameObject Locomotive;
        [Tooltip("Wagon prefab (e.g. Low_Poly_Trains/Prefabs/Trains/Wagon_01).")]
        public GameObject Wagon;
        [Min(0)] public int WagonCount = 3;
        [Tooltip("Arc-length spacing (m) between car origins — set to roughly a car length.")]
        public float CarSpacing = 18f;
        public float SpeedKmh = 60f;
        [Tooltip("Extra lift (m) so the cars sit on the rail tops, not in the ballast.")]
        public float RideHeight = 0f;

        readonly List<TrainAgent> _trains = new List<TrainAgent>();

        // Push live tunables (spacing / ride height) to every running train each frame, so
        // adjusting them from the palette updates the train in place — no re-spawn needed.
        void Update()
        {
            for (int i = 0; i < _trains.Count; i++)
            {
                TrainAgent t = _trains[i];
                if (t == null) continue;
                t.CarSpacing = CarSpacing;
                t.RideHeight = RideHeight;
            }
        }

        public void SpawnTestTrain()
        {
            if (Designer == null) Designer = FindFirstObjectByType<TerrainDesigner>();
            if (Designer == null || Designer.RailLayer == null || Designer.Field == null)
            { Debug.LogWarning("[TrainManager] No TerrainDesigner / rail layer / field."); return; }

            if (!Designer.RailLayer.TryLongestRouteWorld(Designer.Field, out List<Vector3> path))
            { Debug.LogWarning("[TrainManager] No rail route found — draw some connected track first."); return; }

            var go = new GameObject("Train");
            var agent = go.AddComponent<TrainAgent>();
            agent.RideHeight = RideHeight;
            if (!agent.Init(path, Locomotive, Wagon, WagonCount, CarSpacing, SpeedKmh / 3.6f))
            { Debug.LogWarning("[TrainManager] Train init failed (degenerate path)."); Destroy(go); return; }
            _trains.Add(agent);
            Debug.Log($"[TrainManager] Spawned train on a {path.Count}-pt route.");
        }

        public void ClearTrains()
        {
            for (int i = 0; i < _trains.Count; i++) if (_trains[i] != null) Destroy(_trains[i].gameObject);
            _trains.Clear();
        }
    }
}
