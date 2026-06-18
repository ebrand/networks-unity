// A lightweight node/edge graph for LINEWORK (fences, power lines, pipelines):
// nodes are world XZ points, edges connect two nodes. Each edge is a CUBIC
// BEZIER whose control points are auto-derived (Catmull-Rom) from the node's
// neighbours, so a drawn chain curves smoothly through its points (s-curves
// emerge from placement). Manual per-node handle editing can layer on later by
// overriding the derived controls.
//
// Plain serializable data (XZ only; Y comes from the terrain at render time).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    // Intersection precedence for a road edge. Auto = derive from draw age (older corridor wins). Primary/Secondary
    // are manual overrides: at a junction, primary roads run through with the base setback; secondary roads yield —
    // their setback grows as the approach angle gets acute so they clear the primary's body.
    public enum RoadClass { Auto = 0, Primary = 1, Secondary = 2 }

    [Serializable]
    public class LineEdge
    {
        public int A;
        public int B;
        // Explicit cubic-bezier controls (world XZ), set by the curve tool. When
        // HasCurve is false the edge is straight, or auto-smoothed via EdgeControls.
        public bool HasCurve;
        public Vector2 ControlA;
        public Vector2 ControlB;
        public float SpeedLimit;   // km/h the section was laid at (0 = unset)
        public string Profile;     // road plan: the road-config.json profile id/name for this segment (null = none)
        public bool Excavated;     // road plan: this segment's bed has been cut → renders distinctly + becomes Build-able
        public bool Bridge;        // road plan: this segment SPANS (bridge/trestle) — ends forced level, not excavated, built on piers
        public bool Built;         // road plan: this segment has a 3D road swept on it (runtime; travels with the edge through splits)
        public float SetbackA = -1f;  // road plan: manual setback override (m) at END A's junction; <0 = auto (resolver-computed)
        public float SetbackB = -1f;  // road plan: manual setback override (m) at END B's junction; <0 = auto
        public RoadClass Class = RoadClass.Auto;  // road plan: intersection precedence (manual primary/secondary override)
        public int Serial;             // road plan: monotonic draw-order age (lower = drawn earlier = primary by default)
        public List<BridgeArch> Arches; // road plan: under-deck open-spandrel arches on a bridge span (null = none)
        // Rail layer: non-null ⇒ this edge was GENERATED from a road's rail corridor (key = owning road edge + track),
        // so it's regenerated on every road (re)build and must NOT be persisted (filtered out of CollectData). Null ⇒
        // a hand-drawn edge. Runtime-only — never written to a save. See RailTrackLayer.RegenerateRoadCorridors.
        [System.NonSerialized] public string CorridorKey;
        public LineEdge() { }
        public LineEdge(int a, int b) { A = a; B = b; }
    }

    // An under-deck arch carrying a stretch of a bridge: springs from the trestle at StartArc to the one at EndArc
    // (arc-distance along the edge), peaking Rise above the straight base line between their feet. The bridge builder
    // truncates the intermediate trestles to sit on it. (Increment 1 just records it; Increment 2 builds it.)
    [System.Serializable]
    public class BridgeArch
    {
        public float StartArc;   // arc-distance (m) along the edge of the start trestle
        public float EndArc;     // arc-distance (m) of the end trestle
        public float Rise;       // peak height (m) above the straight line joining the two trestle feet
    }

    [Serializable]
    public class LineGraph
    {
        public List<Vector2> Nodes = new List<Vector2>();
        public List<LineEdge> Edges = new List<LineEdge>();
        // Per-node DESIGN elevation (world Y), parallel to Nodes; NaN = unset → fall back to the terrain
        // height. The road plan uses this so a node's road grade is the surface the user SHAPED (and can be
        // edited via drag handles), not the raw DEM. Other line layers (fence/power/rail) just leave it NaN.
        public List<float> NodeY = new List<float>();

        public int AddNode(Vector2 p) { Nodes.Add(p); NodeY.Add(float.NaN); return Nodes.Count - 1; }

        public float GetNodeY(int i) => i >= 0 && i < NodeY.Count ? NodeY[i] : float.NaN;
        public void SetNodeY(int i, float y) { if (i < 0) return; while (NodeY.Count <= i) NodeY.Add(float.NaN); NodeY[i] = y; }

        // Returns true if a new edge was actually added; false if rejected (out-of-range / self-loop) or a duplicate
        // already existed. Callers must check this rather than infer success from Edges.Count, and the new edge (when
        // true) is the last in Edges.
        public bool AddEdge(int a, int b)
        {
            if (a == b || a < 0 || b < 0 || a >= Nodes.Count || b >= Nodes.Count) return false;
            foreach (LineEdge e in Edges)
                if ((e.A == a && e.B == b) || (e.A == b && e.B == a)) return false; // no dup
            Edges.Add(new LineEdge(a, b) { Serial = NextSerial() });
            return true;
        }

        // Next draw-order serial: one past the oldest existing edge. O(n) but AddEdge is rare and n is small;
        // computing from the live edges (rather than a stored counter) means it just works after load/clear.
        public int NextSerial()
        {
            int max = 0;
            for (int i = 0; i < Edges.Count; i++) if (Edges[i] != null && Edges[i].Serial > max) max = Edges[i].Serial;
            return max + 1;
        }

        public void Clear() { Nodes.Clear(); Edges.Clear(); NodeY.Clear(); }

        // If `node` is a degree-2 pass-through whose two edges run (near) straight through it (within tolDeg of
        // collinear), replace them with ONE straight edge joining their far endpoints — preserving profile/speed/
        // class/built/excavated and the OUTER setbacks — and remove the node. Returns true if it merged.
        public bool TryJoinColinear(int node, float tolDeg)
        {
            if (node < 0 || node >= Nodes.Count) return false;
            int e1 = -1, e2 = -1;
            for (int i = 0; i < Edges.Count; i++)
            {
                var e = Edges[i];
                if (e.A == node || e.B == node) { if (e1 < 0) e1 = i; else if (e2 < 0) e2 = i; else return false; } // >2 → not a pass-through
            }
            if (e1 < 0 || e2 < 0) return false;
            LineEdge a = Edges[e1], b = Edges[e2];
            int p = a.A == node ? a.B : a.A;
            int q = b.A == node ? b.B : b.A;
            if (p == q) return false;
            Vector2 dp = Nodes[p] - Nodes[node], dq = Nodes[q] - Nodes[node];
            if (dp.sqrMagnitude < 1e-6f || dq.sqrMagnitude < 1e-6f) return false;
            if (Vector2.Angle(dp.normalized, -dq.normalized) > Mathf.Max(0f, tolDeg)) return false;  // not collinear
            // already a direct p–q edge? bail rather than clobber it.
            for (int i = 0; i < Edges.Count; i++) { var e = Edges[i]; if (i != e1 && i != e2 && ((e.A == p && e.B == q) || (e.A == q && e.B == p))) return false; }

            LineEdge keep = a.Built ? a : b;
            float sbP = a.A == node ? a.SetbackB : a.SetbackA;   // a's setback at the FAR end p
            float sbQ = b.A == node ? b.SetbackB : b.SetbackA;   // b's setback at the FAR end q
            string prof = keep.Profile; float spd = keep.SpeedLimit;
            bool built = a.Built || b.Built, exc = a.Excavated || b.Excavated, brg = a.Bridge && b.Bridge;
            RoadClass cls = keep.Class; int ser = Mathf.Min(a.Serial, b.Serial);

            RemoveNode(node);                                    // drops node + its two edges, reindexes
            int pp = p > node ? p - 1 : p, qq = q > node ? q - 1 : q;
            AddEdge(pp, qq);
            for (int i = 0; i < Edges.Count; i++)
            {
                var ne = Edges[i];
                if ((ne.A == pp && ne.B == qq) || (ne.A == qq && ne.B == pp))
                {
                    ne.Profile = prof; ne.SpeedLimit = spd; ne.Built = built; ne.Excavated = exc; ne.Bridge = brg;
                    ne.Class = cls; ne.Serial = ser; ne.HasCurve = false;
                    if (ne.A == pp) { ne.SetbackA = sbP; ne.SetbackB = sbQ; } else { ne.SetbackA = sbQ; ne.SetbackB = sbP; }
                    break;
                }
            }
            return true;
        }

        // Remove a single edge by index, KEEPING both endpoint nodes — for chopping a gap mid-line
        // (split twice, then delete the middle edge to leave the two nodes for a bridge, etc.).
        public bool RemoveEdgeAt(int edgeIndex)
        {
            if (edgeIndex < 0 || edgeIndex >= Edges.Count) return false;
            Edges.RemoveAt(edgeIndex);
            return true;
        }

        // Remove a node and every edge touching it, fixing up the indices of
        // edges that referenced higher-numbered nodes.
        public void RemoveNode(int idx)
        {
            if (idx < 0 || idx >= Nodes.Count) return;
            Edges.RemoveAll(e => e.A == idx || e.B == idx);
            Nodes.RemoveAt(idx);
            if (idx < NodeY.Count) NodeY.RemoveAt(idx);
            foreach (LineEdge e in Edges)
            {
                if (e.A > idx) e.A--;
                if (e.B > idx) e.B--;
            }
        }

        // Nearest node to a world XZ within maxDist; -1 if none.
        public int NearestNode(Vector2 p, float maxDist)
        {
            int best = -1;
            float bestSq = maxDist * maxDist;
            for (int i = 0; i < Nodes.Count; i++)
            {
                float d = (Nodes[i] - p).sqrMagnitude;
                if (d <= bestSq) { bestSq = d; best = i; }
            }
            return best;
        }

        public bool IsEmpty => Edges.Count == 0 && Nodes.Count == 0;

        // Closest point lying ON any edge (its bezier) within maxDist, for branching
        // off mid-track. Returns the edge, the bezier param t, and the world point.
        public bool NearestPointOnEdge(Vector2 p, float maxDist, out int edgeIndex, out float t, out Vector2 point)
        {
            edgeIndex = -1; t = 0f; point = Vector2.zero;
            float bestSq = maxDist * maxDist;
            const int N = 24;
            for (int ei = 0; ei < Edges.Count; ei++)
            {
                LineEdge e = Edges[ei];
                Vector2 p0 = Nodes[e.A], p3 = Nodes[e.B], q1, q2;
                if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
                else { Vector2 d = p3 - p0; q1 = p0 + d / 3f; q2 = p0 + d * (2f / 3f); }
                Vector2 prev = p0;
                for (int i = 1; i <= N; i++)
                {
                    Vector2 cur = Bezier(p0, q1, q2, p3, i / (float)N);
                    Vector2 seg = cur - prev;
                    float len2 = seg.sqrMagnitude;
                    float u = len2 > 1e-9f ? Mathf.Clamp01(Vector2.Dot(p - prev, seg) / len2) : 0f;
                    Vector2 cp = prev + seg * u;
                    float dsq = (cp - p).sqrMagnitude;
                    if (dsq < bestSq)
                    {
                        bestSq = dsq; edgeIndex = ei; point = cp;
                        t = (i - 1 + u) / N;
                    }
                    prev = cur;
                }
            }
            return edgeIndex >= 0;
        }

        // Split an edge at bezier param t, inserting a node there and replacing the
        // edge with its two (de Casteljau) halves. Returns the new node's index (or
        // the existing endpoint if t is at an end — no degenerate sliver edge).
        public int SplitEdge(int edgeIndex, float t)
        {
            if (edgeIndex < 0 || edgeIndex >= Edges.Count) return -1;
            LineEdge e = Edges[edgeIndex];
            if (t <= 0.02f) return e.A;
            if (t >= 0.98f) return e.B;
            int origA = e.A, origB = e.B;
            float spd = e.SpeedLimit;
            string prof = e.Profile;
            bool curved = e.HasCurve;
            bool exc = e.Excavated, brg = e.Bridge, blt = e.Built;   // per-segment state travels onto both halves
            float sbA = e.SetbackA, sbB = e.SetbackB;                 // setback overrides stay on their original-endpoint half
            RoadClass cls = e.Class; int ser = e.Serial;             // both halves keep the parent's class + draw age
            Vector2 p0 = Nodes[origA], p3 = Nodes[origB], q1, q2;
            if (curved) { q1 = e.ControlA; q2 = e.ControlB; }
            else { Vector2 d = p3 - p0; q1 = p0 + d / 3f; q2 = p0 + d * (2f / 3f); }
            // de Casteljau subdivision at t.
            Vector2 a = Vector2.Lerp(p0, q1, t);
            Vector2 b = Vector2.Lerp(q1, q2, t);
            Vector2 c = Vector2.Lerp(q2, p3, t);
            Vector2 ab = Vector2.Lerp(a, b, t);
            Vector2 bc = Vector2.Lerp(b, c, t);
            Vector2 m = Vector2.Lerp(ab, bc, t);
            int mi = AddNode(m);
            // Sit the new junction node on the split edge's DESIGN grade line (lerp of its endpoints' elevations)
            // instead of leaving it NaN → captured from raw terrain. Keeps a road perpendicular off an existing
            // road, and the junction pad, on the same grade as the road being split (no step/gap at the junction).
            float syA = GetNodeY(origA), syB = GetNodeY(origB);
            if (!float.IsNaN(syA) && !float.IsNaN(syB)) SetNodeY(mi, Mathf.Lerp(syA, syB, t));
            Edges.RemoveAt(edgeIndex);
            var e1 = new LineEdge(origA, mi) { SpeedLimit = spd, Profile = prof, Excavated = exc, Bridge = brg, Built = blt, SetbackA = sbA, SetbackB = -1f, Class = cls, Serial = ser };
            var e2 = new LineEdge(mi, origB) { SpeedLimit = spd, Profile = prof, Excavated = exc, Bridge = brg, Built = blt, SetbackA = -1f, SetbackB = sbB, Class = cls, Serial = ser };
            if (curved)
            {
                e1.HasCurve = true; e1.ControlA = a; e1.ControlB = ab;
                e2.HasCurve = true; e2.ControlA = bc; e2.ControlB = c;
            }
            Edges.Add(e1); Edges.Add(e2);
            return mi;
        }

        // First neighbour of `node` that isn't `exclude` (for Catmull-Rom
        // tangents). -1 if none. Junctions just pick the first — good enough.
        int OtherNeighbor(int node, int exclude)
        {
            for (int i = 0; i < Edges.Count; i++)
            {
                LineEdge e = Edges[i];
                if (e.A == node && e.B != exclude) return e.B;
                if (e.B == node && e.A != exclude) return e.A;
            }
            return -1;
        }

        // Cubic-bezier control points for an edge, Catmull-Rom-smoothed using
        // the neighbouring nodes. Endpoints mirror so the curve doesn't kink.
        public void EdgeControls(LineEdge e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3)
        {
            p0 = Nodes[e.A];
            p3 = Nodes[e.B];
            if (e.HasCurve) { p1 = e.ControlA; p2 = e.ControlB; return; } // explicit (curve tool)
            int prevA = OtherNeighbor(e.A, e.B);
            int nextB = OtherNeighbor(e.B, e.A);
            Vector2 prev = prevA >= 0 ? Nodes[prevA] : p0 - (p3 - p0);
            Vector2 next = nextB >= 0 ? Nodes[nextB] : p3 + (p3 - p0);
            p1 = p0 + (p3 - prev) / 6f;
            p2 = p3 - (next - p0) / 6f;
        }

        // Unit direction pointing AWAY from node `v` along edge `e` (uses the edge's resolved bezier tangent).
        public Vector2 DirAtNode(LineEdge e, int v)
        {
            EdgeControls(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
            Vector2 d = e.A == v ? BezierTangent(p0, p1, p2, p3, 0f) : -BezierTangent(p0, p1, p2, p3, 1f);
            if (d.sqrMagnitude < 1e-8f) d = e.A == v ? (Nodes[e.B] - Nodes[e.A]) : (Nodes[e.A] - Nodes[e.B]);
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.right;
        }

        // Approaches within this of straight-through (180°) are treated as the SAME corridor, not a crossing.
        public const float ClassCollinearTolDeg = 25f;

        // Resolved intersection precedence of an edge: its manual class, or — when Auto — derived from draw age. An
        // Auto edge is Secondary if a STRICTLY OLDER edge crosses it non-collinearly at either endpoint (it's the
        // newcomer cutting across an existing road); otherwise Primary. Shared by the class overlay and the resolver.
        public RoadClass EffectiveClass(int ei)
        {
            if (ei < 0 || ei >= Edges.Count) return RoadClass.Primary;
            LineEdge e = Edges[ei];
            if (e == null) return RoadClass.Primary;
            if (e.Class != RoadClass.Auto) return e.Class;
            if (CrossedByOlder(ei, e.A) || CrossedByOlder(ei, e.B)) return RoadClass.Secondary;
            return RoadClass.Primary;
        }
        bool CrossedByOlder(int ei, int node)
        {
            LineEdge e = Edges[ei];
            Vector2 myDir = DirAtNode(e, node);
            for (int j = 0; j < Edges.Count; j++)
            {
                if (j == ei) continue;
                LineEdge o = Edges[j];
                if (o == null || (o.A != node && o.B != node)) continue;
                if (o.Serial >= e.Serial) continue;                          // only strictly-older roads outrank us
                float ang = Vector2.Angle(myDir, -DirAtNode(o, node));        // 0 = collinear (same corridor through node)
                if (ang > ClassCollinearTolDeg) return true;                 // older road we CROSS (not continue) → we yield
            }
            return false;
        }

        public static Vector2 Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        public static Vector2 BezierTangent(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float u = 1f - t;
            return 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
        }
    }

    // Sparse on-disk form: just the nodes + edges (XZ).
    [Serializable]
    public class LineGraphSave
    {
        public List<Vector2> Nodes;
        public List<LineEdge> Edges;
        public List<float> NodeY;   // per-node design elevation (NaN = unset); parallel to Nodes
    }
}
