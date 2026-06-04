// Rail PLANNING layer — an unconstrained survey alignment drawn on the terrain
// before any earthworks. You sketch straight/curved segments (same click-to-
// chain UX as the rail tool, Shift = curve) with NO grade or radius limits, so
// you can trace exactly where you intend to build and read the lay of the land.
//
// It renders only as draped yellow lines (no 3D track): the planned centreline
// (or two track lines, for a double-track corridor) plus dashed corridor-edge
// lines offset to each side. Phase 1 is survey-only; the analyzer (cut / fill /
// bridge / tunnel classification) lands in Phase 2.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class RailPlanLayer : ITerrainLineLayer
    {
        [Tooltip("Display name + GameObject root name + hotkey label.")]
        public string Name = "Plan";
        [Tooltip("Total graded corridor width (m) — the dashed edge lines sit this " +
                 "far apart, centred on the alignment. The footprint you plan to clear/grade.")]
        public float CorridorWidth = 30f;
        [Tooltip("Tracks in the corridor: 1 = single centreline, 2 = a double-track " +
                 "pair drawn at +/- half the track gap.")]
        [Range(1, 2)] public int Tracks = 1;
        [Tooltip("Distance between the two tracks (m) when planning a double-track " +
                 "corridor (track centre to track centre).")]
        public float TrackGap = 4f;
        [Tooltip("Curve mode (held-Shift): how far the bezier controls sit from each " +
                 "node toward the guide corner (0 = sharp, 1 = wide arc).")]
        [Range(0.1f, 0.95f)] public float CurveLever = 0.55f;
        [Tooltip("Restrict planned curves to the minimum radius for the design speed + " +
                 "lateral g (curves tighter than that are refused; preview turns red). " +
                 "Off = unconstrained survey lines.")]
        public bool LimitCurveRadius = true;
        [Tooltip("Design speed (km/h) the plan's curves are checked against. With Max " +
                 "lateral g this sets the minimum curve radius (R = v^2 / (g*a)).")]
        public float SpeedLimitKmh = 40f;
        [Tooltip("Max comfortable lateral acceleration in g. Higher = tighter curves " +
                 "allowed for a given speed. Real rail ~0.1.")]
        [Range(0.05f, 0.5f)] public float MaxLateralG = 0.15f;

        // Minimum curve radius (m) for the design speed: R = v^2 / (g*a), v in m/s.
        public float MinRadiusForSpeed
        {
            get { float v = SpeedLimitKmh / 3.6f; return v * v / (9.81f * Mathf.Max(0.01f, MaxLateralG)); }
        }
        // Last previewed curve's tightest radius + whether it violated the minimum
        // (read by the on-screen hint while a curve corner is armed).
        [System.NonSerialized] public float LastPreviewRadius = float.PositiveInfinity;
        [System.NonSerialized] public bool LastPreviewTooTight;
        // While a curve corner is armed: the two leg lengths (A->bend, bend->B) in metres
        // and draped world anchors for the on-screen dimension labels.
        [System.NonSerialized] public bool CurveDimsValid;
        [System.NonSerialized] public float CurveLegA, CurveLegB;
        [System.NonSerialized] public Vector3 CurveLegAMid, CurveLegBMid;
        [Tooltip("Sampling step (m) for draping the lines onto the terrain surface. " +
                 "Smaller = smoother but more segments.")]
        public float SampleStep = 2f;
        [Tooltip("Metres the plan lines float above the terrain so they don't z-fight.")]
        public float Lift = 0.2f;
        [Tooltip("Length (m) of the straight-ahead alignment guide — the dashed " +
                 "collinear extension out of the chain tail. Also bounds the snap reach.")]
        public float ExtensionGuideLength = 120f;
        [Tooltip("Snap radius (m) to the straight-ahead alignment guide.")]
        public float ExtensionSnapRadius = 4f;
        [Tooltip("Snap radius (m) for resuming/joining the plan's OWN nodes (the end " +
                 "of the corridor you already drew).")]
        public float EndSnapRadius = 8f;
        public Color PlanColor = new Color(1f, 0.92f, 0.2f, 0.85f);

        [Header("Analysis (Phase 2)")]
        [Tooltip("Mark the corridor by what each section needs to build a grade-" +
                 "limited rail. Colour-blind-safe encoding: at-grade = solid plan line, " +
                 "cut = dashed, fill = double-dashed; bridge = cyan, tunnel = purple, " +
                 "over-grade = red. Off = plain survey lines.")]
        public bool ShowAnalysis = true;
        [Tooltip("Max grade (deg) for the analysis. An edge whose endpoint-to-endpoint " +
                 "grade exceeds this is flagged OVER-GRADE (red) — can't be connected " +
                 "at grade; needs a reroute/switchback. 5 deg ~ 8.7%.")]
        public float MaxGradeDeg = 5f;
        [Tooltip("Cut/fill within this band (m) of the graded bed reads as buildable " +
                 "at-grade (solid plan line).")]
        public float AtGradeBand = 0.6f;
        [Tooltip("Fill deeper than this (m) is flagged as needing a BRIDGE (cyan) " +
                 "rather than an embankment.")]
        public float BridgeFillDepth = 6f;
        [Tooltip("Cut deeper than this (m) is flagged as needing a TUNNEL (purple) " +
                 "rather than an open cut.")]
        public float TunnelCutDepth = 8f;
        [Tooltip("Float a grade %% label over each plan segment (sampled from the CURRENT " +
                 "terrain — reads the natural grade before earthworks, the achieved grade " +
                 "after). Red = over the max grade.")]
        public bool ShowGradeLabels = true;

        // Per-edge grade readout (current terrain), for the floating labels.
        public struct EdgeGrade { public Vector3 Mid; public float GradePct; public bool Over; }

        // Section classes (also index the per-class length tallies below).
        const int CLS_ATGRADE = 0, CLS_CUT = 1, CLS_FILL = 2, CLS_BRIDGE = 3, CLS_TUNNEL = 4, CLS_OVER = 5;
        static readonly Color[] ClassColors =
        {
            new Color(0.45f, 0.9f, 0.35f, 0.95f),  // at-grade  (green)
            new Color(1f, 0.6f, 0.15f, 0.95f),     // cut       (orange)
            new Color(0.82f, 0.72f, 0.45f, 0.95f), // fill      (tan)
            new Color(0.3f, 0.8f, 0.95f, 0.95f),   // bridge    (cyan)
            new Color(0.72f, 0.45f, 0.95f, 0.95f), // tunnel    (purple)
            new Color(1f, 0.25f, 0.2f, 0.97f),     // over-grade(red)
        };
        // Analysis summary (metres per class + total route length), read by the palette.
        [System.NonSerialized] public float RouteLength;
        [System.NonSerialized] public readonly float[] ClassLen = new float[6];

        [System.NonSerialized] public bool CurveModifier; // Shift held this frame
        // Extension-guide heading seeded from the rail this plan starts on, used
        // when the chain tail has no plan edge of its own yet (set by the host).
        [System.NonSerialized] public bool HasSeedDir;
        [System.NonSerialized] public Vector2 SeedDir;

        LineGraph _graph;
        public LineGraph Graph => _graph ??= new LineGraph();
        int _chainTail = -1;
        Vector2 _corner;
        bool _cornerPending;

        // Rendered draped lines (persistent) + the placement preview.
        GameObject _go; MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; Material _mat;
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _idx = new List<int>();
        readonly List<Color32> _cols = new List<Color32>(); // per-vertex analysis colour
        GameObject _pvGo; MeshFilter _pvMf; MeshRenderer _pvMr; Mesh _pvMesh; Material _pvMat;
        readonly List<Vector3> _pvVerts = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();

        public string LayerName => Name;
        string RootName => "TerrainRailPlan_" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- editing (unconstrained: it's a plan, so no grade/radius refusal) ----

        public void AddNode(TerrainField field, Vector3 hit)
        {
            Vector2 p = new Vector2(hit.x, hit.z);
            if (_chainTail < 0)
            {
                int near = Graph.NearestNode(p, NodePickRadius);
                if (near >= 0) _chainTail = near;
                else if (Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _))
                { _chainTail = Graph.SplitEdge(ei, tt); Rebuild(field); }
                else _chainTail = Graph.AddNode(p);
                _cornerPending = false;
                return;
            }
            if (_cornerPending)
            {
                // Refuse a curve tighter than the design speed allows (keeps the corner
                // armed so a wider endpoint can be picked) — same as the rail tool.
                if (LimitCurveRadius)
                {
                    CurveControls(Graph.Nodes[_chainTail], p, _corner, out Vector2 cc1, out Vector2 cc2);
                    if (MinCurveRadius(Graph.Nodes[_chainTail], cc1, cc2, p) < MinRadiusForSpeed) return;
                }
                int end = NearestOrNew(p);
                AddCurvedEdge(_chainTail, end, _corner);
                _chainTail = end; _cornerPending = false;
                Rebuild(field);
                return;
            }
            if (CurveModifier) { _corner = p; _cornerPending = true; return; }
            int idx = NearestOrNew(p);
            Graph.AddEdge(_chainTail, idx);
            _chainTail = idx;
            Rebuild(field);
        }

        const float NodePickRadius = 5f;

        int NearestOrNew(Vector2 p)
        {
            int n = Graph.NearestNode(p, NodePickRadius);
            if (n >= 0 && n != _chainTail) return n;
            if (n < 0 && Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _))
                return Graph.SplitEdge(ei, tt);
            return Graph.AddNode(p);
        }

        void CurveControls(Vector2 a, Vector2 b, Vector2 corner, out Vector2 c1, out Vector2 c2)
        {
            float f = Mathf.Clamp(CurveLever, 0.1f, 0.95f);
            c1 = Vector2.Lerp(a, corner, f);
            c2 = Vector2.Lerp(b, corner, f);
        }

        void AddCurvedEdge(int a, int b, Vector2 corner)
        {
            Graph.AddEdge(a, b);
            LineEdge e = FindEdge(a, b);
            if (e == null) return;
            CurveControls(Graph.Nodes[e.A], Graph.Nodes[e.B], corner, out Vector2 c1, out Vector2 c2);
            e.HasCurve = true; e.ControlA = c1; e.ControlB = c2;
        }

        // Tightest radius (m) along a cubic bezier, via 3-point circumradius over
        // samples. +inf for a straight line. (Same as the rail tool's check.)
        static float MinCurveRadius(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            const int N = 24;
            float minR = float.PositiveInfinity;
            Vector2 a = LineGraph.Bezier(p0, p1, p2, p3, 0f);
            Vector2 b = LineGraph.Bezier(p0, p1, p2, p3, 1f / N);
            for (int i = 2; i <= N; i++)
            {
                Vector2 c = LineGraph.Bezier(p0, p1, p2, p3, i / (float)N);
                float ab = Vector2.Distance(a, b), bc = Vector2.Distance(b, c), ca = Vector2.Distance(c, a);
                float area2 = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
                if (area2 > 1e-6f) minR = Mathf.Min(minR, ab * bc * ca / (2f * area2));
                a = b; b = c;
            }
            return minR;
        }

        LineEdge FindEdge(int a, int b)
        {
            foreach (LineEdge e in Graph.Edges)
                if ((e.A == a && e.B == b) || (e.A == b && e.B == a)) return e;
            return null;
        }

        public void EndChain() { _chainTail = -1; _cornerPending = false; }

        // Heading continuing straight out of the chain tail's incoming edge (180° /
        // collinear). False when the tail has no edge.
        bool IncomingDirection(out Vector2 dir)
        {
            dir = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            for (int i = Graph.Edges.Count - 1; i >= 0; i--)
            {
                LineEdge e = Graph.Edges[i];
                if (e.A != _chainTail && e.B != _chainTail) continue;
                GetBezier(e, out Vector2 p0, out Vector2 q1, out Vector2 q2, out Vector2 p3);
                dir = e.B == _chainTail ? LineGraph.BezierTangent(p0, q1, q2, p3, 1f)
                                        : -LineGraph.BezierTangent(p0, q1, q2, p3, 0f);
                if (dir.sqrMagnitude < 1e-6f) dir = e.B == _chainTail ? p3 - p0 : p0 - p3;
                if (dir.sqrMagnitude < 1e-6f) return false;
                dir = dir.normalized;
                return true;
            }
            // No own edge yet (first segment off the rail): use the seeded heading.
            if (HasSeedDir && SeedDir.sqrMagnitude > 1e-6f) { dir = SeedDir.normalized; return true; }
            return false;
        }

        // The chain tail's XZ (the node the next segment grows from), for the host to
        // seed the extension guide from the rail it sits on. False if not drawing.
        public bool TryGetTailXZ(out Vector2 pos)
        {
            pos = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            pos = Graph.Nodes[_chainTail];
            return true;
        }

        // Snap a cursor onto the straight-ahead alignment guide (collinear extension
        // of the incoming edge) when within ExtensionSnapRadius and ahead of the tail.
        public bool TrySnapToExtension(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            float r = Mathf.Max(0f, ExtensionSnapRadius);
            if (r <= 0f || !IncomingDirection(out Vector2 dir)) return false;
            Vector2 origin = Graph.Nodes[_chainTail];
            float along = Vector2.Dot(cursor - origin, dir);
            if (along <= 0f || along > ExtensionGuideLength) return false;
            Vector2 proj = origin + dir * along;
            if ((cursor - proj).sqrMagnitude > r * r) return false;
            snapped = proj;
            return true;
        }

        // Snap to the plan's OWN nearest node (corridor end) / edge within EndSnapRadius,
        // excluding the active chain anchor — so you can stop drawing and resume.
        public bool TrySnapToOwnNode(Vector2 p, out Vector2 snapped)
        {
            snapped = p;
            float r = Mathf.Max(0f, EndSnapRadius);
            if (r <= 0f) return false;
            int best = -1; float bestSq = r * r;
            for (int i = 0; i < Graph.Nodes.Count; i++)
            {
                if (i == _chainTail) continue;
                float d = (Graph.Nodes[i] - p).sqrMagnitude;
                if (d <= bestSq) { bestSq = d; best = i; }
            }
            if (best >= 0) { snapped = Graph.Nodes[best]; return true; }
            if (Graph.NearestPointOnEdge(p, r, out _, out _, out Vector2 pt)) { snapped = pt; return true; }
            return false;
        }

        // Snap onto the nearest plan edge within maxDist + its heading there — for the
        // terrain slope tool to ride the planned alignment. False if none in range.
        // Per-edge grade (endpoint-to-endpoint, sampled from the CURRENT terrain) with a
        // midpoint world position to anchor a floating label. Over = exceeds MaxGradeDeg.
        public void CollectEdgeGrades(TerrainField field, List<EdgeGrade> outList)
        {
            outList.Clear();
            if (Graph == null || field == null) return;
            float maxPct = Mathf.Tan(Mathf.Max(0.1f, MaxGradeDeg) * Mathf.Deg2Rad) * 100f;
            foreach (LineEdge e in Graph.Edges)
            {
                GetBezier(e, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3);
                float L = ApproxArcLength(q0, q1, q2, q3);
                if (L < 1e-3f) continue;
                float eA = field.SampleHeight(q0.x, q0.y), eB = field.SampleHeight(q3.x, q3.y);
                float pct = (eB - eA) / L * 100f;
                Vector2 mid = LineGraph.Bezier(q0, q1, q2, q3, 0.5f);
                float my = field.SampleHeight(mid.x, mid.y) + Lift + 1.5f;
                outList.Add(new EdgeGrade
                {
                    Mid = new Vector3(mid.x, my, mid.y),
                    GradePct = pct,
                    Over = Mathf.Abs(pct) > maxPct,
                });
            }
        }

        // True when EVERY plan edge's endpoint-to-endpoint grade is within MaxGradeDeg on
        // the current terrain — i.e. a grade-limited rail can sit on the whole centreline.
        // overEdges = count of segments that exceed it. False (and 0) on an empty plan.
        public bool AllEdgesBuildable(TerrainField field, out int overEdges)
        {
            overEdges = 0;
            if (Graph == null || field == null || Graph.Edges.Count == 0) return false;
            float maxPct = Mathf.Tan(Mathf.Max(0.1f, MaxGradeDeg) * Mathf.Deg2Rad) * 100f;
            foreach (LineEdge e in Graph.Edges)
            {
                GetBezier(e, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3);
                float L = ApproxArcLength(q0, q1, q2, q3);
                if (L < 1e-3f) continue;
                float eA = field.SampleHeight(q0.x, q0.y), eB = field.SampleHeight(q3.x, q3.y);
                if (Mathf.Abs(eB - eA) / L * 100f > maxPct) overEdges++;
            }
            return overEdges == 0;
        }

        public bool TryNearestOnPlan(Vector2 p, float maxDist, out Vector2 onPlan, out Vector2 dir)
        {
            onPlan = p; dir = Vector2.zero;
            if (Graph == null || !Graph.NearestPointOnEdge(p, maxDist, out int ei, out float t, out Vector2 pt))
                return false;
            GetBezier(Graph.Edges[ei], out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3);
            Vector2 tan = LineGraph.BezierTangent(q0, q1, q2, q3, t);
            if (tan.sqrMagnitude < 1e-6f) tan = q3 - q0;
            if (tan.sqrMagnitude < 1e-6f) return false;
            onPlan = pt; dir = tan.normalized;
            return true;
        }

        // The plan centreline (sampled XZ) between the exact points a and b project to
        // on the plan, walking the planned curves between them. Handles A and B mid-edge
        // on different edges: the first/last edges are clipped to the click parameters
        // and the span between is filled with whole edges. A->B order preserved. False
        // if either end isn't near the plan, or the two edges aren't connected.
        public bool TryPathBetween(Vector2 a, Vector2 b, float maxDist, out List<Vector2> path)
        {
            path = null;
            if (Graph == null) return false;
            if (!Graph.NearestPointOnEdge(a, maxDist, out int eia, out float ta, out _)) return false;
            if (!Graph.NearestPointOnEdge(b, maxDist, out int eib, out float tb, out _)) return false;

            // Same edge: sample directly between the two params.
            if (eia == eib)
            {
                GetBezier(Graph.Edges[eia], out Vector2 s0, out Vector2 s1, out Vector2 s2, out Vector2 s3);
                path = SampleEdgeRange(s0, s1, s2, s3, ta, tb);
                return path != null && path.Count >= 2;
            }

            LineEdge ea = Graph.Edges[eia], eb = Graph.Edges[eib];
            int[] exitNode = { ea.A, ea.B };   float[] exitParam = { 0f, 1f }; // A=param0, B=param1
            int[] entryNode = { eb.A, eb.B };  float[] entryParam = { 0f, 1f };

            // Of the 4 ways to leave edge A and reach edge B, keep the fewest node hops.
            List<int> bestNodes = null; int bestX = -1, bestN = -1, bestHops = int.MaxValue;
            for (int xi = 0; xi < 2; xi++)
                for (int ni = 0; ni < 2; ni++)
                {
                    List<int> np = BfsNodePath(exitNode[xi], entryNode[ni]);
                    if (np != null && np.Count < bestHops) { bestHops = np.Count; bestNodes = np; bestX = xi; bestN = ni; }
                }
            if (bestNodes == null) return false;

            var pts = new List<Vector2>();
            // Partial of edge A: from the click param to the chosen exit node.
            GetBezier(ea, out Vector2 a0, out Vector2 a1, out Vector2 a2, out Vector2 a3);
            AppendRange(pts, SampleEdgeRange(a0, a1, a2, a3, ta, exitParam[bestX]));
            // Whole intermediate edges from exit node to entry node.
            for (int k = 0; k < bestNodes.Count - 1; k++)
            {
                LineEdge e = FindEdge(bestNodes[k], bestNodes[k + 1]);
                if (e == null) return false;
                GetBezier(e, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3);
                bool fwd = e.A == bestNodes[k];
                AppendRange(pts, SampleEdgeRange(q0, q1, q2, q3, fwd ? 0f : 1f, fwd ? 1f : 0f));
            }
            // Partial of edge B: from the chosen entry node to the click param.
            GetBezier(eb, out Vector2 b0, out Vector2 b1, out Vector2 b2, out Vector2 b3);
            AppendRange(pts, SampleEdgeRange(b0, b1, b2, b3, entryParam[bestN], tb));

            path = Dedup(pts);
            return path.Count >= 2;
        }

        static void AppendRange(List<Vector2> dst, List<Vector2> src)
        {
            if (src != null) dst.AddRange(src);
        }

        // Drop consecutive near-duplicate points (shared junctions appear twice).
        static List<Vector2> Dedup(List<Vector2> pts)
        {
            var outp = new List<Vector2>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
                if (i == 0 || (pts[i] - outp[outp.Count - 1]).sqrMagnitude > 1e-6f) outp.Add(pts[i]);
            return outp;
        }

        // Sample one bezier edge between parameters t0..t1 (A->B order preserved), XZ.
        List<Vector2> SampleEdgeRange(Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3, float t0, float t1)
        {
            if (Mathf.Abs(t1 - t0) < 1e-4f) return null;
            float span = Vector2.Distance(q0, q3) * Mathf.Abs(t1 - t0);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(span, 1f) / Mathf.Max(0.5f, SampleStep)), 1, 4000);
            var pts = new List<Vector2>(steps + 1);
            for (int s = 0; s <= steps; s++)
                pts.Add(LineGraph.Bezier(q0, q1, q2, q3, Mathf.Lerp(t0, t1, s / (float)steps)));
            return pts;
        }

        // Breadth-first node path (shortest hop count) between two plan nodes.
        List<int> BfsNodePath(int start, int goal)
        {
            var prev = new Dictionary<int, int> { { start, -1 } };
            var q = new Queue<int>();
            q.Enqueue(start);
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (cur == goal) break;
                foreach (LineEdge e in Graph.Edges)
                {
                    int nxt = e.A == cur ? e.B : (e.B == cur ? e.A : -1);
                    if (nxt < 0 || prev.ContainsKey(nxt)) continue;
                    prev[nxt] = cur; q.Enqueue(nxt);
                }
            }
            if (!prev.ContainsKey(goal)) return null;
            var rev = new List<int>();
            for (int n = goal; n != -1; n = prev[n]) rev.Add(n);
            rev.Reverse();
            return rev;
        }

        public void RemoveLastNode(TerrainField field)
        {
            if (_cornerPending) { _cornerPending = false; return; }
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return;
            Graph.RemoveNode(_chainTail);
            _chainTail = -1;
            Rebuild(field);
        }

        public bool DeleteNearNode(TerrainField field, Vector3 hit, float radius)
        {
            int n = Graph.NearestNode(new Vector2(hit.x, hit.z), radius);
            if (n < 0) return false;
            Graph.RemoveNode(n);
            if (_chainTail == n) _chainTail = -1; else if (_chainTail > n) _chainTail--;
            _cornerPending = false;
            Rebuild(field);
            return true;
        }

        // ---- draped rendering ----

        void GetBezier(LineEdge e, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3)
        {
            q0 = Graph.Nodes[e.A]; q3 = Graph.Nodes[e.B];
            if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
            else { Vector2 d = q3 - q0; q1 = q0 + d / 3f; q2 = q0 + d * (2f / 3f); }
        }

        public void Rebuild(TerrainField field)
        {
            EnsureRender();
            _verts.Clear(); _idx.Clear(); _cols.Clear();
            RouteLength = 0f;
            for (int i = 0; i < ClassLen.Length; i++) ClassLen[i] = 0f;
            if (field != null)
                foreach (LineEdge e in Graph.Edges)
                    EmitEdge(field, e);
            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_cols);
            _mesh.SetIndices(_idx, MeshTopology.Lines, 0);
            _mesh.RecalculateBounds();
        }

        // One planned edge: the track centreline(s) + the two dashed corridor edges,
        // draped onto the terrain. With analysis on, the track line(s) are coloured by
        // section (cut/fill/bridge/tunnel/over-grade); else everything is the plan colour.
        void EmitEdge(TerrainField field, LineEdge e)
        {
            GetBezier(e, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3);
            float hw = Mathf.Max(0.5f, CorridorWidth * 0.5f);
            if (ShowAnalysis)
            {
                float L = ApproxArcLength(q0, q1, q2, q3);
                float eA = field.SampleHeight(q0.x, q0.y), eB = field.SampleHeight(q3.x, q3.y);
                float maxPct = Mathf.Tan(Mathf.Max(0.1f, MaxGradeDeg) * Mathf.Deg2Rad) * 100f;
                bool overGrade = L > 1e-3f && Mathf.Abs(eB - eA) / L * 100f > maxPct;
                if (Tracks >= 2)
                {
                    EmitAnalyzedTrack(field, q0, q1, q2, q3, eA, eB, overGrade, TrackGap * 0.5f, true);
                    EmitAnalyzedTrack(field, q0, q1, q2, q3, eA, eB, overGrade, -TrackGap * 0.5f, false);
                }
                else EmitAnalyzedTrack(field, q0, q1, q2, q3, eA, eB, overGrade, 0f, true);
            }
            else
            {
                Color32 c = PlanColor;
                if (Tracks >= 2)
                {
                    EmitOffsetLine(field, q0, q1, q2, q3, TrackGap * 0.5f, false, c);
                    EmitOffsetLine(field, q0, q1, q2, q3, -TrackGap * 0.5f, false, c);
                }
                else EmitOffsetLine(field, q0, q1, q2, q3, 0f, false, c);
            }
            // Corridor edges always stay the plain plan colour (dashed).
            EmitOffsetLine(field, q0, q1, q2, q3, hw, true, PlanColor);
            EmitOffsetLine(field, q0, q1, q2, q3, -hw, true, PlanColor);
        }

        // Approx arc length of a cubic bezier (chord averaged with the control polygon).
        static float ApproxArcLength(Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3)
        {
            float chord = Vector2.Distance(q0, q3);
            float poly = Vector2.Distance(q0, q1) + Vector2.Distance(q1, q2) + Vector2.Distance(q2, q3);
            return Mathf.Max(chord, (chord + poly) * 0.5f);
        }

        // Colour-blind-friendly encoding: the three grade-relative classes share the
        // plan colour and are told apart by DASH PATTERN (at-grade solid, cut dashed,
        // fill double-dashed); bridge/tunnel/over-grade keep their distinct colours
        // (cyan/purple/red), drawn solid.
        Color32 ClassLineColor(int cls)
            => (cls == CLS_ATGRADE || cls == CLS_CUT || cls == CLS_FILL) ? PlanColor : ClassColors[cls];

        // Whether sample-segment #seg of a class is drawn — the gaps of its dash
        // pattern are skipped. (Length tallies still count skipped segments.)
        static bool ClassDrawsSeg(int cls, int seg)
        {
            if (cls == CLS_CUT) return (seg & 1) == 0;     // dashed:  ▪ ▪ ▪
            if (cls == CLS_FILL) return (seg & 3) < 2;     // double-dashed:  ▪▪  ▪▪
            return true;                                   // solid (at-grade + coloured classes)
        }

        // terrain-minus-bed delta (+ = terrain above the bed = cut; - = fill) -> class.
        int Classify(bool overGrade, float delta)
        {
            if (overGrade) return CLS_OVER;
            if (Mathf.Abs(delta) <= Mathf.Max(0f, AtGradeBand)) return CLS_ATGRADE;
            if (delta > 0f) return delta > TunnelCutDepth ? CLS_TUNNEL : CLS_CUT;
            return -delta > BridgeFillDepth ? CLS_BRIDGE : CLS_FILL;
        }

        // A track line offset `lateral` from the centreline, coloured per section by
        // how the terrain deviates from the straight grade-limited bed (eA -> eB).
        // accumulate = tally section lengths into the summary (once per edge).
        void EmitAnalyzedTrack(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3,
                               float eA, float eB, bool overGrade, float lateral, bool accumulate)
        {
            float chord = Vector2.Distance(q0, q3);
            int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(chord, 1f) / Mathf.Max(0.5f, SampleStep)), 2, 4000);
            Vector3 prevW = default; Vector2 prevXz = default; bool havePrev = false;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                Vector2 c = LineGraph.Bezier(q0, q1, q2, q3, t);
                Vector2 pt = c;
                if (Mathf.Abs(lateral) > 1e-4f)
                {
                    Vector2 tan = LineGraph.BezierTangent(q0, q1, q2, q3, t);
                    if (tan.sqrMagnitude > 1e-6f) pt = c + new Vector2(-tan.y, tan.x).normalized * lateral;
                }
                float ground = field.SampleHeight(pt.x, pt.y);
                float bed = Mathf.Lerp(eA, eB, t);
                int cls = Classify(overGrade, ground - bed);
                Vector3 w = new Vector3(pt.x, ground + Lift, pt.y);
                if (havePrev)
                {
                    if (accumulate)
                    {
                        float segLen = Vector2.Distance(prevXz, pt);
                        ClassLen[cls] += segLen;
                        RouteLength += segLen;
                    }
                    if (ClassDrawsSeg(cls, i - 1))         // skip this dash's gap segments
                    {
                        Color32 col = ClassLineColor(cls);
                        int s = _verts.Count;
                        _verts.Add(prevW); _verts.Add(w);
                        _idx.Add(s); _idx.Add(s + 1);
                        _cols.Add(col); _cols.Add(col);
                    }
                }
                prevW = w; prevXz = pt; havePrev = true;
            }
        }

        // Drape a polyline offset `lateral` metres to the side of the bezier onto the
        // terrain. dashed = emit every other segment (corridor edges). One uniform colour.
        void EmitOffsetLine(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3,
                            float lateral, bool dashed, Color32 col)
            => EmitOffsetLine(field, q0, q1, q2, q3, lateral, dashed, _verts, _idx, _cols, col);

        void EmitOffsetLine(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3,
                            float lateral, bool dashed, List<Vector3> verts, List<int> idx,
                            List<Color32> cols = null, Color32 col = default)
        {
            float chord = Vector2.Distance(q0, q3);
            int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(chord, 1f) / Mathf.Max(0.5f, SampleStep)), 2, 4000);
            Vector3 prev = default; bool havePrev = false;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                Vector2 c = LineGraph.Bezier(q0, q1, q2, q3, t);
                Vector2 pt = c;
                if (Mathf.Abs(lateral) > 1e-4f)
                {
                    Vector2 tan = LineGraph.BezierTangent(q0, q1, q2, q3, t);
                    if (tan.sqrMagnitude > 1e-6f)
                        pt = c + new Vector2(-tan.y, tan.x).normalized * lateral;
                }
                float y = field.SampleHeight(pt.x, pt.y) + Lift;
                Vector3 w = new Vector3(pt.x, y, pt.y);
                if (havePrev && (!dashed || (i % 2 == 1)))
                {
                    int s = verts.Count;
                    verts.Add(prev); verts.Add(w);
                    idx.Add(s); idx.Add(s + 1);
                    if (cols != null) { cols.Add(col); cols.Add(col); }
                }
                prev = w; havePrev = true;
            }
        }

        void EnsureRender()
        {
            if (_mf != null) return;
            _go = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            _mf = _go.AddComponent<MeshFilter>();
            _mr = _go.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mesh = new Mesh { name = "RailPlanMesh" };
            _mf.sharedMesh = _mesh;
            // Vertex-colour overlay so each section can be tinted by its analysis class.
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            _mat = sh != null ? new Material(sh) { name = "RailPlanMat" } : MakeLineMat("RailPlanMat");
            _mr.sharedMaterial = _mat;
        }

        Material MakeLineMat(string name)
        {
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            return sh != null
                ? new Material(sh) { name = name, color = PlanColor }
                : NetworkDesigner.PipelineMaterials.CreateUnlitColor(PlanColor, name);
        }

        // ---- placement preview ----

        public void UpdatePreview(TerrainField field, Vector3 cursor, bool show)
        {
            EnsurePreview();
            _pvMr.enabled = show;
            if (!show) return;
            _pvVerts.Clear(); _pvIdx.Clear();

            // Cursor ring.
            DrawRing(field, new Vector2(cursor.x, cursor.z), 0.9f);

            LastPreviewRadius = float.PositiveInfinity;
            LastPreviewTooTight = false;
            CurveDimsValid = false;
            bool haveStart = _chainTail >= 0 && _chainTail < Graph.Nodes.Count;
            if (haveStart)
            {
                Vector2 start = Graph.Nodes[_chainTail];
                Vector2 cur = new Vector2(cursor.x, cursor.z);
                // Straight-ahead alignment guide (collinear continuation), dashed.
                if (IncomingDirection(out Vector2 ext))
                {
                    Vector2 gend = start + ext * ExtensionGuideLength;
                    Vector2 gd = gend - start;
                    EmitOffsetLine(field, start, start + gd / 3f, start + gd * (2f / 3f), gend, 0f, true, _pvVerts, _pvIdx);
                }
                if (_cornerPending)
                {
                    CurveControls(start, cur, _corner, out Vector2 c1, out Vector2 c2);
                    LastPreviewRadius = MinCurveRadius(start, c1, c2, cur);
                    LastPreviewTooTight = LimitCurveRadius && LastPreviewRadius < MinRadiusForSpeed;
                    EmitPendingEdge(field, start, c1, c2, cur);
                    // Dimension labels: the two construction legs A->bend and bend->B.
                    CurveDimsValid = true;
                    CurveLegA = Vector2.Distance(start, _corner);
                    CurveLegB = Vector2.Distance(_corner, cur);
                    CurveLegAMid = LegMid(field, start, _corner);
                    CurveLegBMid = LegMid(field, _corner, cur);
                }
                else if (CurveModifier)
                {
                    // Arming the corner: just a guide leg to the cursor.
                    EmitOffsetLine(field, start, Vector2.Lerp(start, cur, 1f / 3f),
                                   Vector2.Lerp(start, cur, 2f / 3f), cur, 0f, true, _pvVerts, _pvIdx);
                }
                else
                {
                    Vector2 d = cur - start;
                    EmitPendingEdge(field, start, start + d / 3f, start + d * (2f / 3f), cur);
                }
            }

            _pvMesh.Clear();
            _pvMesh.SetVertices(_pvVerts);
            _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0);
            _pvMesh.RecalculateBounds();
            // Red while the armed curve is tighter than the design speed allows.
            if (_pvMat != null)
                _pvMat.color = LastPreviewTooTight ? new Color(1f, 0.25f, 0.2f, 0.95f) : PlanColor;
        }

        void EmitPendingEdge(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3)
        {
            if (Tracks >= 2)
            {
                EmitOffsetLine(field, q0, q1, q2, q3, TrackGap * 0.5f, false, _pvVerts, _pvIdx);
                EmitOffsetLine(field, q0, q1, q2, q3, -TrackGap * 0.5f, false, _pvVerts, _pvIdx);
            }
            else EmitOffsetLine(field, q0, q1, q2, q3, 0f, false, _pvVerts, _pvIdx);
            float hw = Mathf.Max(0.5f, CorridorWidth * 0.5f);
            EmitOffsetLine(field, q0, q1, q2, q3, hw, true, _pvVerts, _pvIdx);
            EmitOffsetLine(field, q0, q1, q2, q3, -hw, true, _pvVerts, _pvIdx);
        }

        // Draped world midpoint of an A->B leg (label anchor), lifted clear of the line.
        Vector3 LegMid(TerrainField field, Vector2 a, Vector2 b)
        {
            Vector2 m = (a + b) * 0.5f;
            float y = (field != null ? field.SampleHeight(m.x, m.y) : 0f) + Lift + 1.5f;
            return new Vector3(m.x, y, m.y);
        }

        void DrawRing(TerrainField field, Vector2 c, float r)
        {
            const int n = 20;
            Vector3 prev = default;
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                float wx = c.x + Mathf.Cos(a) * r, wz = c.y + Mathf.Sin(a) * r;
                float y = (field != null ? field.SampleHeight(wx, wz) : 0f) + Lift;
                Vector3 w = new Vector3(wx, y, wz);
                if (i > 0) { int s = _pvVerts.Count; _pvVerts.Add(prev); _pvVerts.Add(w); _pvIdx.Add(s); _pvIdx.Add(s + 1); }
                prev = w;
            }
        }

        void EnsurePreview()
        {
            if (_pvMf != null) return;
            _pvGo = new GameObject(RootName + "_Preview") { hideFlags = HideFlags.DontSave };
            _pvMf = _pvGo.AddComponent<MeshFilter>();
            _pvMr = _pvGo.AddComponent<MeshRenderer>();
            _pvMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _pvMr.receiveShadows = false;
            _pvMesh = new Mesh { name = "RailPlanPreviewMesh" };
            _pvMf.sharedMesh = _pvMesh;
            _pvMat = MakeLineMat("RailPlanPreviewMat");
            _pvMr.sharedMaterial = _pvMat;
        }

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; }

        public void ClearAll(TerrainField field)
        {
            _graph = new LineGraph();
            _chainTail = -1; _cornerPending = false;
            Rebuild(field);
        }

        // ---- save / load (the plan graph; lines regenerate on load) ----

        public LineGraphSave CollectData()
        {
            return new LineGraphSave
            {
                Nodes = new List<Vector2>(Graph.Nodes),
                Edges = new List<LineEdge>(Graph.Edges),
            };
        }

        public void LoadState(LineGraphSave save)
        {
            _graph = new LineGraph();
            _chainTail = -1; _cornerPending = false;
            if (save != null)
            {
                if (save.Nodes != null) _graph.Nodes.AddRange(save.Nodes);
                if (save.Edges != null) _graph.Edges.AddRange(save.Edges);
            }
        }
    }
}
