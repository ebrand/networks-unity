using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Roads
{
    // Lane-edge road model (D1–D4). The graph's NAVIGABLE bands ARE edges; a Corridor groups parallel lane-edges and
    // carries the NON-navigable bands (median/shoulder) as metadata. Lives behind LaneEdgeModel.Enabled until the new
    // model reaches parity with the corridor-edge model — the old model stays working in the meantime.
    public static class LaneEdgeModel
    {
        public static bool Enabled = true;        // master flag: when true, road plan uses the lane-edge model (now the default)
        public static bool MappingMode = false;   // when true, road-plan clicks author lane-flow mappings instead of drawing
        public static bool ShowRuler = true;       // plan-mode distance ladder (5 m tick ruler down each junction approach)
        public static bool ShowSegmentLabels = true;  // plan-mode per-segment length "m" labels
    }

    // A through/turn movement at a node: traffic on the incoming lane-edge continues onto the outgoing lane-edge.
    // This is the #149 intra-node mapping — the intersection routing + the agent graph through the node.
    [System.Serializable]
    public class LaneFlow { public int Node; public int FromEdge; public int ToEdge; public bool Auto; }   // Auto = default (regenerated); else manual

    // Navigable band kinds → each is a graph edge an agent can traverse. (Median/Shoulder are bands, not edges; Rail
    // stays its own system.) Direction routing: cars→Traffic/Turn, pedestrians→Sidewalk, bikes→Bike.
    public enum LaneKind { Traffic, Turn, Sidewalk, Bike }

    // One navigable lane = one graph edge between two nodes. Offset is the signed lateral distance of the lane's CENTRE
    // from the corridor centreline (− = BA/left side, + = AB/right side), so lanes order naturally by Offset.
    [System.Serializable]
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

    // A lane-drop taper: where a lane was dropped (an exit pull reduced the lane count), the dropped lane's slot is paved
    // as a wedge that runs from FULL lane width at one corridor end and narrows to zero over `Length` (MUTCD-ish, speed-
    // based). The inner edge (toward the surviving lanes) stays straight; the outer edge converges in.
    [System.Serializable]
    public struct LaneDropTaper
    {
        public bool AtA;       // the zero-width TIP is at the corridor's A end (else the B end) — the mismatch node
        public float Offset;   // signed lateral offset of the tapering lane's centre, in the corridor frame (m)
        public float Width;    // tapering lane width = the wedge's full width away from the tip (m)
        public float Length;   // taper run length from the tip into the corridor (m)
        public int LaneEdge;   // the lane-edge index this taper REPLACES (rebuild-scoped; excluded from the uniform body)
    }

    // A road = an ordered set of parallel lane-edges plus the non-navigable bands around/within them. The corridor's
    // reference path is derived from its lanes (they share endpoints until a lane diverges — Phase 4).
    [System.Serializable]
    public class Corridor
    {
        public int Id;
        public List<int> Lanes = new List<int>();            // lane-edge indices, ordered outer-BA → centre → outer-AB (non-readonly so JsonUtility restores it)
        public bool Curved;                                  // reference path is a cubic bezier A→B (else a straight chord)
        public Vector2 ControlA, ControlB;                    // cubic control points in the corridor's A→B param (only when Curved)
        public bool AlignLanes;                              // lane-subset extension: lay the body at each lane's actual Offset
                                                             // (fill gaps with asphalt + shift CenterU) so it sits on the source lanes
        public float CenterShift;                            // lateral body shift at the A end (canonicalised pull-off / connector):
                                                             // render the body on a centreline shifted from the lane[0] path, so the
                                                             // lanes' graph nodes stay shared (flow) while stored offsets are canonical.
        public float CenterShiftB;                           // lateral body shift at the B end; the body interpolates A→B. Equals
                                                             // CenterShift for a uniform shift (pull-off); differs for a CONNECTOR that
                                                             // sweeps from the source's pavement (A) to the target's (B). 0 = no shift.
        public Vector2 ShiftDir;                             // explicit world-space unit normal for a UNIFORM shift (else PathFrame derives
                                                             // it from the A-tangent). Captured when a curve is split/extended so both
                                                             // pieces translate along the SAME direction — a re-derived seam tangent would
                                                             // splay them apart. Zero = derive as before (straight / freshly-pulled).
        public float MedianWidth;                             // centre band width between BA and AB lanes (0 = none)
        public float ShoulderBA, ShoulderAB;                  // outer non-navigable shoulder widths
        public string Profile;                                // source profile id (markings/style reuse during the spike)
        public bool Planned = true;                           // drawn, not yet excavated/built (plan→excavate→build, mirrors LineEdge)
        public bool Excavated;                                // terrain bed cut under the corridor footprint
        public bool Built;                                    // 3D body swept + registered for agents
        public float BedDepth;                                // excavation depth (built body becomes a slab of this thickness)
        public List<LaneDropTaper> Tapers = new List<LaneDropTaper>();   // paved lane-drop wedges (Phase-2 taper geometry)
        public float SetbackA = -1f, SetbackB = -1f;          // per-end junction stop-line setback OVERRIDE (m); <0 = auto (computed from crossing geometry). Dragged via the junction grab handles.
        public bool IsSlip;                                   // a channelised slip lane (corner-bypass connector) — excluded from junction-footprint detection.
    }

    // The lane-edge graph: a shared node list + lane-edges + corridors. Parallels LineGraph but at lane granularity.
    public class LaneEdgeNetwork
    {
        public readonly List<Vector2> Nodes = new List<Vector2>();
        public readonly List<float> NodeY = new List<float>();         // captured design grade per node (NaN = not captured)
        public readonly List<LaneEdge> Edges = new List<LaneEdge>();
        public readonly List<Corridor> Corridors = new List<Corridor>();
        public readonly List<LaneFlow> Flows = new List<LaneFlow>();   // intra-node lane-flow mappings (#149)
        int _serial;

        public int AddNode(Vector2 p) { Nodes.Add(p); NodeY.Add(float.NaN); return Nodes.Count - 1; }
        public float GetNodeY(int i) => i >= 0 && i < NodeY.Count ? NodeY[i] : float.NaN;
        public void SetNodeY(int i, float y) { if (i >= 0 && i < NodeY.Count) NodeY[i] = y; }

        public void Clear() { Nodes.Clear(); NodeY.Clear(); Edges.Clear(); Corridors.Clear(); Flows.Clear(); _serial = 0; }

        // Replace the whole network from a deserialized save (persistence).
        public void LoadFrom(List<Vector2> nodes, List<float> nodeY, List<LaneEdge> edges, List<Corridor> corridors, List<LaneFlow> flows)
        {
            Clear();
            if (nodes != null) Nodes.AddRange(nodes);
            if (nodeY != null) NodeY.AddRange(nodeY);
            while (NodeY.Count < Nodes.Count) NodeY.Add(float.NaN);
            if (edges != null) Edges.AddRange(edges);
            if (corridors != null) Corridors.AddRange(corridors);
            if (flows != null) Flows.AddRange(flows);
            foreach (LaneEdge e in Edges) if (e.Serial > _serial) _serial = e.Serial;
        }

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
