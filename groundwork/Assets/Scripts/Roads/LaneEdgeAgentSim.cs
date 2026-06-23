using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace NetworkDesigner.Roads
{
    // Phase 8.4: lane-flow-native agent sim. A car follows a lane-edge from its source cluster to its destination, then
    // at the destination picks a LaneFlow (#149) onto the next lane-edge. The AUTHORED flows directly determine routing
    // — turns, ramps, lane-drops all fall out of which incoming→outgoing mappings exist. Self-ticking MonoBehaviour.
    public class LaneEdgeAgentSim : MonoBehaviour
    {
        public static System.Func<Vector2, float> GroundHeight;   // terrain drape fallback (set by the host)
        static LaneEdgeAgentSim _inst;

        class Car { public int Lane; public float T; public float Speed; public Transform Vis; }
        readonly List<Car> _cars = new List<Car>();
        static Material _carMat;
        readonly System.Random _rng = new System.Random(20260622);

        static Material CarMat() => _carMat != null ? _carMat
            : (_carMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(0.85f, 0.2f, 0.2f, 1f), 0.3f, "LaneEdgeCar"));

        public static LaneEdgeAgentSim Instance()
        {
            if (_inst == null) _inst = new GameObject("LaneEdgeAgentSim").AddComponent<LaneEdgeAgentSim>();
            return _inst;
        }

        // Traffic lane-edges on BUILT corridors — the drivable set.
        static List<int> BuiltLaneEdges()
        {
            var list = new List<int>();
            LaneEdgeNetwork net = LaneEdgeWorld.Net;
            for (int i = 0; i < net.Edges.Count; i++)
            {
                LaneEdge e = net.Edges[i];
                if (e.Kind != LaneKind.Traffic) continue;
                if (e.CorridorId >= 0 && e.CorridorId < net.Corridors.Count && net.Corridors[e.CorridorId].Built) list.Add(i);
            }
            return list;
        }

        public void Spawn(int count)
        {
            var built = BuiltLaneEdges();
            if (built.Count == 0) { Debug.LogWarning("[LaneEdgeAgents] No built corridors — Build a corridor first."); return; }
            for (int i = 0; i < count; i++)
            {
                var car = new Car { Lane = built[_rng.Next(built.Count)], T = (float)_rng.NextDouble(), Speed = 8f + (float)_rng.NextDouble() * 6f };
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var col = go.GetComponent<Collider>(); if (col != null) Destroy(col);
                go.GetComponent<MeshRenderer>().sharedMaterial = CarMat();
                go.transform.SetParent(transform, false);
                go.transform.localScale = new Vector3(2f, 1.5f, 4f);
                car.Vis = go.transform;
                _cars.Add(car);
            }
            Debug.Log($"[LaneEdgeAgents] spawned {count}; total {_cars.Count}");
        }

        public void Clear()
        {
            foreach (Car c in _cars) if (c.Vis != null) Destroy(c.Vis.gameObject);
            _cars.Clear();
        }

        void Update()
        {
            if (_cars.Count == 0) return;
            LaneEdgeNetwork net = LaneEdgeWorld.Net;
            float dt = Time.deltaTime;
            for (int i = 0; i < _cars.Count; i++) Advance(_cars[i], net, dt);
        }

        void Advance(Car c, LaneEdgeNetwork net, float dt)
        {
            if (!ResolveLane(net, c.Lane, out int src, out int dst, out Vector2 sp, out Vector2 dp, out float len)) { Respawn(c, net); return; }
            c.T += (c.Speed * dt) / len;
            int guard = 0;
            while (c.T >= 1f && guard++ < 8)
            {
                int next = PickNext(net, c.Lane, dst);
                if (next < 0) { Respawn(c, net); return; }       // dead end (no flow) → respawn elsewhere
                float over = c.T - 1f;
                c.Lane = next;
                if (!ResolveLane(net, c.Lane, out src, out dst, out sp, out dp, out len)) { Respawn(c, net); return; }
                c.T = over;
            }
            c.T = Mathf.Clamp01(c.T);
            LaneEdge e = net.Edges[c.Lane];
            Vector2 cdir = net.Nodes[e.B] - net.Nodes[e.A]; if (cdir.sqrMagnitude < 1e-6f) { Respawn(c, net); return; }
            cdir.Normalize();
            Vector2 fr = new Vector2(cdir.y, -cdir.x);
            Vector2 p = Vector2.Lerp(sp, dp, c.T) + fr * e.Offset;
            // Drive on the ROAD surface (captured design grade) once excavated; else the terrain.
            float gA = net.GetNodeY(src), gB = net.GetNodeY(dst);
            float y = (!float.IsNaN(gA) && !float.IsNaN(gB)) ? Mathf.Lerp(gA, gB, c.T) : (GroundHeight != null ? GroundHeight(p) : 0f);
            c.Vis.position = new Vector3(p.x, y + 0.5f, p.y);
            Vector2 travel = (dp - sp); if (travel.sqrMagnitude > 1e-6f) c.Vis.rotation = Quaternion.LookRotation(new Vector3(travel.x, 0f, travel.y), Vector3.up);
        }

        // A lane-edge traversed in its travel direction: source→dest cluster (Direction 2 = A→B, 0 = B→A).
        static bool ResolveLane(LaneEdgeNetwork net, int lane, out int src, out int dst, out Vector2 sp, out Vector2 dp, out float len)
        {
            src = dst = -1; sp = dp = Vector2.zero; len = 0f;
            if (lane < 0 || lane >= net.Edges.Count) return false;
            LaneEdge e = net.Edges[lane];
            src = e.Direction == 2 ? e.A : e.B;
            dst = e.Direction == 2 ? e.B : e.A;
            if (src < 0 || dst < 0 || src >= net.Nodes.Count || dst >= net.Nodes.Count) return false;
            sp = net.Nodes[src]; dp = net.Nodes[dst]; len = (dp - sp).magnitude;
            return len >= 0.5f;
        }

        // Reservoir-pick one outgoing lane-edge among the LaneFlows leaving `fromLane` at `atNode`.
        int PickNext(LaneEdgeNetwork net, int fromLane, int atNode)
        {
            int count = 0, chosen = -1;
            foreach (LaneFlow f in net.Flows)
                if (f.Node == atNode && f.FromEdge == fromLane) { count++; if (_rng.Next(count) == 0) chosen = f.ToEdge; }
            return chosen;
        }

        void Respawn(Car c, LaneEdgeNetwork net)
        {
            var built = BuiltLaneEdges();
            if (built.Count == 0) { c.Lane = -1; if (c.Vis != null) c.Vis.gameObject.SetActive(false); return; }
            c.Lane = built[_rng.Next(built.Count)]; c.T = 0f;
            if (c.Vis != null) c.Vis.gameObject.SetActive(true);
        }

#if UNITY_EDITOR
        [MenuItem("Tools/Lane-Edge Spike/Spawn 10 Lane-Edge Cars")]
        static void SpawnCars() => Instance().Spawn(10);

        [MenuItem("Tools/Lane-Edge Spike/Clear Lane-Edge Cars")]
        static void ClearCars() { if (_inst != null) _inst.Clear(); }
#endif
    }
}
