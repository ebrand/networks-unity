using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Roads
{
    // Lane-edge road model (D1–D4). The graph's NAVIGABLE bands ARE edges; a Corridor groups parallel lane-edges and
    // carries the NON-navigable bands (median/shoulder) as metadata. Lives behind LaneEdgeModel.Enabled until the new
    // model reaches parity with the corridor-edge model — the old model stays working in the meantime.
    public static class LaneEdgeModel
    {
        public static bool Enabled = false;   // master flag: when true, road plan uses the lane-edge model
    }

    // Navigable band kinds → each is a graph edge an agent can traverse. (Median/Shoulder are bands, not edges; Rail
    // stays its own system.) Direction routing: cars→Traffic/Turn, pedestrians→Sidewalk, bikes→Bike.
    public enum LaneKind { Traffic, Turn, Sidewalk, Bike }

    // One navigable lane = one graph edge between two nodes. Offset is the signed lateral distance of the lane's CENTRE
    // from the corridor centreline (− = BA/left side, + = AB/right side), so lanes order naturally by Offset.
    public class LaneEdge
    {
        public int A, B;                       // node indices into LaneEdgeNetwork.Nodes
        public int CorridorId = -1;            // which corridor (road) this lane belongs to; -1 = loose
        public LaneKind Kind = LaneKind.Traffic;
        public int Direction;                  // 0 = BA travel (A←B), 2 = AB travel (A→B) — matches the stack zone convention
        public float Width = 3.5f;
        public float Offset;                   // signed lateral offset of the lane centre from the corridor centreline (m)
        public int Serial;                     // stable id for persistence / connectivity references
    }

    // A road = an ordered set of parallel lane-edges plus the non-navigable bands around/within them. The corridor's
    // reference path is derived from its lanes (they share endpoints until a lane diverges — Phase 4).
    public class Corridor
    {
        public int Id;
        public readonly List<int> Lanes = new List<int>();   // lane-edge indices, ordered outer-BA → centre → outer-AB
        public float MedianWidth;                             // centre band width between BA and AB lanes (0 = none)
        public float ShoulderBA, ShoulderAB;                  // outer non-navigable shoulder widths
        public string Profile;                                // source profile id (markings/style reuse during the spike)
    }

    // The lane-edge graph: a shared node list + lane-edges + corridors. Parallels LineGraph but at lane granularity.
    public class LaneEdgeNetwork
    {
        public readonly List<Vector2> Nodes = new List<Vector2>();
        public readonly List<LaneEdge> Edges = new List<LaneEdge>();
        public readonly List<Corridor> Corridors = new List<Corridor>();
        int _serial;

        public int AddNode(Vector2 p) { Nodes.Add(p); return Nodes.Count - 1; }

        public int AddLane(LaneEdge e) { e.Serial = ++_serial; Edges.Add(e); return Edges.Count - 1; }

        public Corridor AddCorridor()
        {
            var c = new Corridor { Id = Corridors.Count };
            Corridors.Add(c);
            return c;
        }

        // Lanes of a corridor, sorted by lateral offset (outer BA → outer AB) — the cross-section order.
        public void SortCorridorLanes(Corridor c)
            => c.Lanes.Sort((i, j) => Edges[i].Offset.CompareTo(Edges[j].Offset));
    }
}
