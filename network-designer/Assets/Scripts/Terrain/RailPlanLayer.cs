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
        [Tooltip("After the bend is placed, snap the end to the same leg length as the " +
                 "start->bend leg (a symmetric curve) when the cursor is within this " +
                 "fraction of that length. 0 = off.")]
        [Range(0f, 0.5f)] public float CurveSymmetrySnap = 0.1f;
        [Tooltip("The bend can't be placed until the first leg gives at least this much " +
                 "turn (deg) above/below the centreline for the design speed — the min-" +
                 "distance target. The guide stays red until then.")]
        public float MinCurveDeflectionDeg = 5f;
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
        [System.NonSerialized] public readonly List<Vector3> CurveTickWorld = new List<Vector3>();
        [System.NonSerialized] public readonly List<int> CurveTickDeg = new List<int>();

        // Rendered draped lines (persistent) + the placement preview.
        GameObject _go; MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; Material _mat;
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _idx = new List<int>();
        readonly List<Color32> _cols = new List<Color32>(); // per-vertex analysis colour
        GameObject _pvGo; MeshFilter _pvMf; MeshRenderer _pvMr; Mesh _pvMesh; Material _pvMat;
        readonly List<Vector3> _pvVerts = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();
        // Speed-coloured equal-leg symmetry ring (per-vertex colour, its own overlay).
        GameObject _symGo; MeshFilter _symMf; MeshRenderer _symMr; Mesh _symMesh; Material _symMat;
        readonly List<Vector3> _symV = new List<Vector3>();
        readonly List<int> _symIdx = new List<int>();
        readonly List<Color32> _symCol = new List<Color32>();

        // Curve-inspection overlay ("design mode"): translucent box per curve + hovered-
        // curve legs/angle. Submesh 0 triangles (fill), 1 lines (outline + legs). Mirrors
        // RailTrackLayer; plan curves carry radius/max-speed/angle only (no grade/decel).
        [System.NonSerialized] public bool ShowCurveInspect;
        public float CurveInspectWidth = 20f;
        public float TypicalTrainLengthM = 100f;
        public Color CurveInspectFill = new Color(0.72f, 0.82f, 0.35f, 0.22f);
        public Color CurveInspectOutline = new Color(1f, 0.95f, 0.20f, 0.85f);
        GameObject _inspGo; MeshFilter _inspMf; MeshRenderer _inspMr; Mesh _inspMesh; Material _inspMat;
        readonly List<Vector3> _inspV = new List<Vector3>();
        readonly List<Color32> _inspCol = new List<Color32>();
        readonly List<int> _inspTri = new List<int>();
        readonly List<int> _inspLine = new List<int>();
        readonly List<Vector2> _boxL = new List<Vector2>();
        readonly List<Vector2> _boxR = new List<Vector2>();
        int _inspSig = -1;
        [System.NonSerialized] public bool InspectHovered, InspectHasCorner, InspectHasGrade, InspectIsCurve;
        [System.NonSerialized] public Vector2 InspectMid, InspectCorner, InspectLegAMid, InspectLegBMid;
        [System.NonSerialized] public float InspectLegA, InspectLegB, InspectAngleDeg;
        [System.NonSerialized] public float InspectRadius, InspectMaxSpeed, InspectGradePct, InspectRated;
        [System.NonSerialized] public float InspectLength, InspectTrainCount;
        [System.NonSerialized] public string InspectDecel;

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

        public void EndChain()
        {
            // Cancelling a just-started chain leaves the tail node with no edges — drop it.
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count && !NodeHasEdge(_chainTail))
                Graph.RemoveNode(_chainTail);
            _chainTail = -1; _cornerPending = false;
        }

        bool NodeHasEdge(int n)
        {
            foreach (LineEdge e in Graph.Edges) if (e.A == n || e.B == n) return true;
            return false;
        }

        // Heading continuing straight out of the chain tail (180° / collinear). When the
        // tail is mid-corridor (several edges), pick the edge whose continuation points
        // toward `toward`, so the cursor side selects which leg to extend. False when the
        // tail has no edge (then the seeded rail heading, if any, is used).
        bool IncomingDirection(Vector2 toward, out Vector2 dir)
        {
            dir = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            Vector2 side = toward - Graph.Nodes[_chainTail];
            float bestDot = float.NegativeInfinity; bool found = false;
            for (int i = 0; i < Graph.Edges.Count; i++)
            {
                LineEdge e = Graph.Edges[i];
                if (e.A != _chainTail && e.B != _chainTail) continue;
                GetBezier(e, out Vector2 p0, out Vector2 q1, out Vector2 q2, out Vector2 p3);
                Vector2 cont = e.B == _chainTail ? LineGraph.BezierTangent(p0, q1, q2, p3, 1f)
                                                 : -LineGraph.BezierTangent(p0, q1, q2, p3, 0f);
                if (cont.sqrMagnitude < 1e-6f) cont = e.B == _chainTail ? p3 - p0 : p0 - p3;
                if (cont.sqrMagnitude < 1e-6f) continue;
                cont = cont.normalized;
                float dot = Vector2.Dot(cont, side);
                if (dot > bestDot) { bestDot = dot; dir = cont; found = true; }
            }
            if (found) return true;
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
            if (r <= 0f || !IncomingDirection(cursor, out Vector2 dir)) return false;
            Vector2 origin = Graph.Nodes[_chainTail];
            float along = Vector2.Dot(cursor - origin, dir);
            if (along <= 0f || along > ExtensionGuideLength) return false;
            Vector2 proj = origin + dir * along;
            if ((cursor - proj).sqrMagnitude > r * r) return false;
            snapped = proj;
            return true;
        }

        // Extending a corridor in STRAIGHT mode: HARD-lock the cursor onto the chosen
        // collinear extension line (ahead of the tail) — a rail corridor can't kink, so a
        // straight continuation stays collinear (turns use the curve tool). Off in curve
        // mode and on a fresh start with no incoming/seeded heading.
        public bool TrySnapExtensionHard(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (CurveModifier || _cornerPending
                || _chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            if (!IncomingDirection(cursor, out Vector2 ext)) return false;
            Vector2 origin = Graph.Nodes[_chainTail];
            float along = Mathf.Max(0.5f, Vector2.Dot(cursor - origin, ext));
            snapped = origin + ext * along;
            return true;
        }

        // A curve is buildable at any deflection within safety (radius ≥ design speed). No
        // deflection floor: straight-ahead (0 deflection) is a straight line — radius ∞ — so
        // it's buildable too, keeping the yellow arc SOLID through the middle. True if the
        // radius limit is off (no constraint then — unconstrained survey).
        bool CurveBuildable(Vector2 start, Vector2 bend, Vector2 end)
        {
            if (!LimitCurveRadius) return true;
            Vector2 inD = bend - start, outD = end - bend;
            if (inD.sqrMagnitude < 1e-6f || outD.sqrMagnitude < 1e-6f) return false;
            CurveControls(start, end, bend, out Vector2 c1, out Vector2 c2);
            return MinCurveRadius(start, c1, c2, end) >= MinRadiusForSpeed;
        }

        // After the bend is armed, HARD-lock the end onto the equal-leg circle (symmetric
        // curve) and clamp the direction into the buildable real-turn arcs. False when no
        // real turn fits (leg too short → whole ring red) so you can back out.
        public bool TrySnapCurveSymmetry(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (!_cornerPending || CurveSymmetrySnap <= 0f
                || _chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            Vector2 start = Graph.Nodes[_chainTail], bend = _corner;
            float legA = Vector2.Distance(start, bend);
            if (legA < 0.5f) return false;
            if (!LimitCurveRadius)   // unconstrained: lock distance only, any direction
            {
                Vector2 t = cursor - bend;
                Vector2 d = t.sqrMagnitude > 1e-6f ? t.normalized : (bend - start).normalized;
                snapped = bend + d * legA; return true;
            }
            Vector2 toCur = cursor - bend;
            Vector2 dir = toCur.sqrMagnitude > 1e-6f ? toCur.normalized : (bend - start).normalized;
            ClampToBuildableArc(start, bend, legA, dir, out Vector2 cdir);
            snapped = bend + cdir * legA;
            return true;
        }

        void ClampToBuildableArc(Vector2 start, Vector2 bend, float legA, Vector2 dir, out Vector2 outDir)
        {
            Vector2 a0 = (bend - start).sqrMagnitude > 1e-6f ? (bend - start).normalized : Vector2.right;
            float a0A = Mathf.Atan2(a0.y, a0.x) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(a0A, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
            float sign = delta >= 0f ? 1f : -1f;
            // Constrain to [0, thMax] on the cursor's side — straight-ahead up to the
            // tightest safe turn. The end can never leave this yellow arc.
            float thMax = MaxBuildableDeflection(start, bend, legA, a0A, sign);
            float mag = SnapDeflectionToTick(Mathf.Clamp(Mathf.Abs(delta), 0f, thMax), thMax);
            float ang = (a0A + mag * sign) * Mathf.Deg2Rad;
            outDir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
        }

        static readonly float[] DeflectionTicksDeg = { 15f, 30f, 45f, 60f, 75f, 90f };

        static float SnapDeflectionToTick(float deg, float maxDeg)
        {
            const float tol = 3f;
            for (int i = 0; i < DeflectionTicksDeg.Length; i++)
            {
                float t = DeflectionTicksDeg[i];
                if (t > maxDeg + 0.5f) break;
                if (Mathf.Abs(deg - t) <= tol) return t;
            }
            return deg;
        }

        bool DeflectionBuildable(Vector2 start, Vector2 bend, float legA, float a0A, float sign, float d)
        {
            float ang = (a0A + sign * d) * Mathf.Deg2Rad;
            return CurveBuildable(start, bend, bend + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * legA);
        }

        // Largest deflection (deg) from a0 on the given side whose curve still meets the
        // radius. Coarse 1° scan to bracket the boundary (radius is monotonic in
        // deflection), then bisect so the clamp reaches the FULL yellow extent.
        float MaxBuildableDeflection(Vector2 start, Vector2 bend, float legA, float a0A, float sign)
        {
            // Scan to nearly 180°: a curve can deflect past 90° (toward a U-turn) while its
            // radius stays ≥ the speed's minimum, so the buildable arc isn't capped at 90°.
            float lo = 0f, hi = 181f;
            for (float d = 0.5f; d <= 179f; d += 1f)
            {
                if (DeflectionBuildable(start, bend, legA, a0A, sign, d)) lo = d;
                else { hi = d; break; }
            }
            for (int i = 0; i < 6 && hi <= 179f; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (DeflectionBuildable(start, bend, legA, a0A, sign, mid)) lo = mid; else hi = mid;
            }
            return lo;
        }

        // While positioning the bend (Shift held, before the end click), constrain it to
        // the min-distance target so the first leg is always long enough for a real turn
        // at the speed. If we're extending a corridor, lock the bend onto its collinear
        // extension line (no kink), no closer than the MDT; otherwise it sticks to the MDT
        // ring and is free to pull out past it for a sharper buildable curve.
        public bool TrySnapBendToTarget(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (!LimitCurveRadius || CurveSymmetrySnap <= 0f || !CurveModifier || _cornerPending
                || _chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            float minLeg = MinFirstLegForSpeed();
            if (minLeg < 1f) return false;
            Vector2 start = Graph.Nodes[_chainTail];
            if (IncomingDirection(cursor, out Vector2 ext))
            {
                float along = Mathf.Max(minLeg, Vector2.Dot(cursor - start, ext));
                snapped = start + ext * along;   // on the extension line, no closer than the MDT
                return true;
            }
            Vector2 toCur = cursor - start; float d = toCur.magnitude;
            if (d < 1e-4f) return false;
            if (d >= minLeg + Mathf.Max(5f, minLeg * 0.04f)) return false; // well past target → free angle
            snapped = start + (toCur / d) * minLeg;                        // snap/constrain to the MDT ring
            return true;
        }

        // True while a curve is being built (bend awaiting placement, or end awaiting it).
        public bool InCurveMode => (CurveModifier || _cornerPending) && CurveSymmetrySnap > 0f && LimitCurveRadius;

        // True while positioning the curve END: the PAC (equal-leg ring) must own the
        // cursor exclusively — no falling through to the straight extension line or a
        // nearby corridor, which would pull the end off the buildable arc.
        public bool PlacingCurveEnd => _cornerPending && CurveSymmetrySnap > 0f && LimitCurveRadius;

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
            HideSymRing();                 // default off; re-enabled by BuildSymRing below
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
                // Hidden once the bend is armed — the curve is what's being placed then.
                if (!_cornerPending && IncomingDirection(cur, out Vector2 ext))
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
                    // Dashed construction legs (start->bend, bend->end), like rail mode.
                    Vector2 la = _corner - start, lb = cur - _corner;
                    EmitOffsetLine(field, start, start + la / 3f, start + la * (2f / 3f), _corner, 0f, true, _pvVerts, _pvIdx);
                    EmitOffsetLine(field, _corner, _corner + lb / 3f, _corner + lb * (2f / 3f), cur, 0f, true, _pvVerts, _pvIdx);
                    // Speed-coloured equal-leg ring around the bend (yellow = OK radius for
                    // the design speed, red = too tight). Where a symmetric end snaps.
                    if (CurveSymmetrySnap > 0f) BuildSymRing(field, start, _corner);
                    // Dimension labels: the two construction legs A->bend and bend->B.
                    CurveDimsValid = true;
                    CurveLegA = Vector2.Distance(start, _corner);
                    CurveLegB = Vector2.Distance(_corner, cur);
                    CurveLegAMid = LegMid(field, start, _corner);
                    CurveLegBMid = LegMid(field, _corner, cur);
                }
                else if (CurveModifier)
                {
                    // Arming the corner: the guide leg, RED until the min leg, + the
                    // min-leg target (drawn into the coloured overlay).
                    BuildMinLegGuide(field, start, cur);
                    // Single-leg dimension while positioning the bend (before the click).
                    CurveDimsValid = true;
                    CurveLegA = Vector2.Distance(start, cur);
                    CurveLegB = 0f;
                    CurveLegAMid = LegMid(field, start, cur);
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

        // The equal-leg symmetry ring around the bend as two SOLID translucent arcs:
        // YELLOW where ending the curve there is a real buildable turn for the design
        // speed (deflection + radius), RED otherwise (near-straight centre and too-tight).
        void BuildSymRing(TerrainField field, Vector2 start, Vector2 bend)
        {
            EnsureSymRing();
            _symV.Clear(); _symIdx.Clear(); _symCol.Clear();
            CurveTickWorld.Clear(); CurveTickDeg.Clear();
            float radius = Vector2.Distance(start, bend);
            if (radius >= 0.5f)
            {
                // Fine segments so the yellow→red boundary lands crisply (matches the clamp).
                int N = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * radius / 3f), 64, 512);
                Color32 ok = new Color32(255, 225, 50, 200), bad = new Color32(255, 55, 45, 200);
                Vector3 prev = RingPt(field, bend, radius, 0f);
                for (int i = 1; i <= N; i++)
                {
                    Vector3 w = RingPt(field, bend, radius, i / (float)N * Mathf.PI * 2f);
                    // Colour each segment by its MIDPOINT so the boundary lands within ~half a segment.
                    float aMid = (i - 0.5f) / N * Mathf.PI * 2f;
                    Vector2 endMid = new Vector2(bend.x + Mathf.Cos(aMid) * radius, bend.y + Mathf.Sin(aMid) * radius);
                    Color32 col = CurveBuildable(start, bend, endMid) ? ok : bad;
                    int s = _symV.Count; _symV.Add(prev); _symV.Add(w);
                    _symCol.Add(col); _symCol.Add(col); _symIdx.Add(s); _symIdx.Add(s + 1);
                    prev = w;
                }
                // Standard-angle tick marks (snap targets) at ±15/30/45/60/75/90° where buildable.
                Vector2 a0 = (bend - start).sqrMagnitude > 1e-6f ? (bend - start).normalized : Vector2.right;
                float a0A = Mathf.Atan2(a0.y, a0.x) * Mathf.Rad2Deg;
                float tl = Mathf.Clamp(radius * 0.02f, 2f, 25f);
                Color32 tickCol = ok;
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    float thMax = MaxBuildableDeflection(start, bend, radius, a0A, sgn);
                    for (int ti = 0; ti < DeflectionTicksDeg.Length && DeflectionTicksDeg[ti] <= thMax + 0.5f; ti++)
                    {
                        float ang = (a0A + sgn * DeflectionTicksDeg[ti]) * Mathf.Deg2Rad;
                        Vector3 wi = RingPt(field, bend, radius - tl, ang), wo = RingPt(field, bend, radius + tl, ang);
                        int ts = _symV.Count; _symV.Add(wi); _symV.Add(wo);
                        _symCol.Add(tickCol); _symCol.Add(tickCol); _symIdx.Add(ts); _symIdx.Add(ts + 1);
                        CurveTickWorld.Add(RingPt(field, bend, radius + tl + Mathf.Max(2f, tl), ang));
                        CurveTickDeg.Add((int)DeflectionTicksDeg[ti]);
                    }
                }
            }
            _symMesh.Clear();
            _symMesh.SetVertices(_symV);
            _symMesh.SetColors(_symCol);
            _symMesh.SetIndices(_symIdx, MeshTopology.Lines, 0);
            _symMesh.RecalculateBounds();
            _symMr.enabled = true;
        }

        Vector3 RingPt(TerrainField field, Vector2 c, float radius, float angle)
        {
            float x = c.x + Mathf.Cos(angle) * radius, z = c.y + Mathf.Sin(angle) * radius;
            return new Vector3(x, (field != null ? field.SampleHeight(x, z) : 0f) + Lift, z);
        }

        // Leg length at which a θmin-deflection curve first meets the min radius for the
        // design speed. 0 when radius isn't limited (no min leg then).
        float MinFirstLegForSpeed()
        {
            if (!LimitCurveRadius) return 0f;
            float th = Mathf.Max(0.1f, MinCurveDeflectionDeg) * Mathf.Deg2Rad;
            Vector2 end = new Vector2(1f + Mathf.Cos(th), Mathf.Sin(th));
            CurveControls(Vector2.zero, end, new Vector2(1f, 0f), out Vector2 c1, out Vector2 c2);
            float k = MinCurveRadius(Vector2.zero, c1, c2, end);
            return k > 1e-3f ? MinRadiusForSpeed / k : 0f;
        }

        // While positioning the bend: draw the guide leg into the coloured overlay — RED
        // until the first leg is long enough for a real turn at this speed — and mark the
        // min-leg target.
        void BuildMinLegGuide(TerrainField field, Vector2 start, Vector2 cur)
        {
            EnsureSymRing();
            _symV.Clear(); _symIdx.Clear(); _symCol.Clear();
            float minLeg = MinFirstLegForSpeed();
            float leg = Vector2.Distance(start, cur);
            Color32 ok = new Color32(255, 225, 50, 220), bad = new Color32(255, 55, 45, 220);
            SymDashedLine(field, start, cur, (minLeg > 1f && leg < minLeg) ? bad : ok);
            if (minLeg > 1f && (cur - start).sqrMagnitude > 1e-4f)
            {
                Vector2 dir = (cur - start).normalized;
                SymCircle(field, start + dir * minLeg, 5f, bad);
            }
            _symMesh.Clear();
            _symMesh.SetVertices(_symV);
            _symMesh.SetColors(_symCol);
            _symMesh.SetIndices(_symIdx, MeshTopology.Lines, 0);
            _symMesh.RecalculateBounds();
            _symMr.enabled = true;
        }

        void SymDashedLine(TerrainField field, Vector2 a, Vector2 b, Color32 col)
        {
            float len = Vector2.Distance(a, b);
            if (len < 1e-4f) return;
            Vector2 dir = (b - a) / len;
            const float dash = 1f, gap = 0.6f, period = dash + gap;
            for (float pos = 0f; pos < len; pos += period)
            {
                Vector2 p0 = a + dir * pos, p1 = a + dir * Mathf.Min(pos + dash, len);
                Vector3 w0 = new Vector3(p0.x, (field != null ? field.SampleHeight(p0.x, p0.y) : 0f) + Lift, p0.y);
                Vector3 w1 = new Vector3(p1.x, (field != null ? field.SampleHeight(p1.x, p1.y) : 0f) + Lift, p1.y);
                int s = _symV.Count; _symV.Add(w0); _symV.Add(w1); _symCol.Add(col); _symCol.Add(col); _symIdx.Add(s); _symIdx.Add(s + 1);
            }
        }

        void SymCircle(TerrainField field, Vector2 c, float r, Color32 col)
        {
            const int n = 18; Vector3 prev = default;
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                Vector3 w = RingPt(field, c, r, a);
                if (i > 0) { int s = _symV.Count; _symV.Add(prev); _symV.Add(w); _symCol.Add(col); _symCol.Add(col); _symIdx.Add(s); _symIdx.Add(s + 1); }
                prev = w;
            }
        }

        void HideSymRing() { if (_symMr != null) _symMr.enabled = false; }

        void EnsureSymRing()
        {
            if (_symMf != null) return;
            _symGo = new GameObject(RootName + "_SymRing") { hideFlags = HideFlags.DontSave };
            _symMf = _symGo.AddComponent<MeshFilter>();
            _symMr = _symGo.AddComponent<MeshRenderer>();
            _symMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _symMr.receiveShadows = false;
            _symMesh = new Mesh { name = "RailPlanSymRing" };
            _symMf.sharedMesh = _symMesh;
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            _symMat = sh != null ? new Material(sh) { name = "RailPlanSymRingMat" } : MakeLineMat("RailPlanSymRingMat");
            _symMr.sharedMaterial = _symMat;
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

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; HideSymRing(); }

        // ---- curve inspection overlay ("design mode") — mirrors RailTrackLayer ----

        void EnsureCurveInspect()
        {
            if (_inspMf != null) return;
            _inspGo = new GameObject(RootName + "_Inspect") { hideFlags = HideFlags.DontSave };
            _inspMf = _inspGo.AddComponent<MeshFilter>();
            _inspMr = _inspGo.AddComponent<MeshRenderer>();
            _inspMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _inspMr.receiveShadows = false;
            _inspMesh = new Mesh { name = "RailPlanInspectMesh" };
            _inspMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _inspMf.sharedMesh = _inspMesh;
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            _inspMat = sh != null ? new Material(sh) { name = "RailPlanInspectMat" } : MakeLineMat("RailPlanInspectMat");
            _inspMr.sharedMaterials = new[] { _inspMat, _inspMat };
        }

        public void RebuildCurveInspect(TerrainField field, Vector3 cursorW, bool show)
        {
            EnsureCurveInspect();
            bool on = show && ShowCurveInspect && field != null && Graph != null && Graph.Edges.Count > 0;
            _inspMr.enabled = on;
            if (!on)
            {
                if (_inspSig != -1) { _inspV.Clear(); _inspCol.Clear(); _inspTri.Clear(); _inspLine.Clear(); _inspMesh.Clear(); _inspSig = -1; InspectHovered = false; }
                return;
            }
            Vector2 cursor = new Vector2(cursorW.x, cursorW.z);
            float halfW = Mathf.Max(1f, CurveInspectWidth * 0.5f);
            int hovered = HoveredCurveEdge(cursor, halfW);
            int sig = Graph.Edges.Count * 92821 ^ (hovered + 1) * 6379 ^ Mathf.RoundToInt(CurveInspectWidth * 16f);
            if (sig == _inspSig) return;
            _inspSig = sig;

            _inspV.Clear(); _inspCol.Clear(); _inspTri.Clear(); _inspLine.Clear();
            InspectHovered = false;
            Color32 fill = CurveInspectFill, outline = CurveInspectOutline;
            const float lift = 0.2f;
            for (int ei = 0; ei < Graph.Edges.Count; ei++)
            {
                LineEdge e = Graph.Edges[ei];
                EdgeControls(e, Graph.Nodes[e.A], Graph.Nodes[e.B], out Vector2 c1, out Vector2 c2);
                EmitCurveBox(field, Graph.Nodes[e.A], c1, c2, Graph.Nodes[e.B], halfW, lift, ei == hovered, false, fill, outline, fill);
            }
            if (hovered >= 0) EmitHoverDetails(field, hovered, lift);
            _inspMesh.Clear();
            _inspMesh.SetVertices(_inspV);
            _inspMesh.SetColors(_inspCol);
            _inspMesh.subMeshCount = 2;
            _inspMesh.SetIndices(_inspTri, MeshTopology.Triangles, 0);
            _inspMesh.SetIndices(_inspLine, MeshTopology.Lines, 1);
            _inspMesh.RecalculateBounds();
        }

        Vector3 InspLift(TerrainField field, Vector2 p, float lift)
        {
            float y = field != null ? field.SampleHeight(p.x, p.y) : 0f;
            return new Vector3(p.x, y + lift, p.y);
        }

        void InspTri(Vector3 a, Vector3 b, Vector3 c, Color32 col)
        {
            int s = _inspV.Count;
            _inspV.Add(a); _inspV.Add(b); _inspV.Add(c);
            _inspCol.Add(col); _inspCol.Add(col); _inspCol.Add(col);
            _inspTri.Add(s); _inspTri.Add(s + 1); _inspTri.Add(s + 2);
        }

        void InspLine(Vector3 a, Vector3 b, Color32 col)
        {
            int s = _inspV.Count;
            _inspV.Add(a); _inspV.Add(b);
            _inspCol.Add(col); _inspCol.Add(col);
            _inspLine.Add(s); _inspLine.Add(s + 1);
        }

        // Dash each segment with a bounded for-loop (no growing phase accumulator).
        void InspDashedPolyline(TerrainField field, List<Vector2> pts, float lift, Color32 col)
        {
            const float dash = 2.5f, gap = 1.8f, period = dash + gap;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                float seg = Vector2.Distance(a, b);
                if (seg < 1e-5f) continue;
                Vector2 dir = (b - a) / seg;
                for (float p = 0f; p < seg; p += period)
                    InspLine(InspLift(field, a + dir * p, lift),
                             InspLift(field, a + dir * Mathf.Min(p + dash, seg), lift), col);
            }
        }

        void InspDashedSeg(TerrainField field, Vector2 a, Vector2 b, float lift, Color32 col)
        {
            _boxL.Clear(); _boxL.Add(a); _boxL.Add(b);
            InspDashedPolyline(field, _boxL, lift, col);
        }

        void EdgeControls(LineEdge e, Vector2 S, Vector2 E, out Vector2 c1, out Vector2 c2)
        {
            if (e.HasCurve) { c1 = e.ControlA; c2 = e.ControlB; }
            else { Vector2 d = E - S; c1 = S + d / 3f; c2 = S + d * (2f / 3f); }
        }

        void EmitCurveBox(TerrainField field, Vector2 S, Vector2 c1, Vector2 c2, Vector2 E,
                          float halfW, float lift, bool filled, bool hatched, Color32 fill, Color32 outline, Color32 hatch)
        {
            const int N = 20;
            _boxL.Clear(); _boxR.Clear();
            for (int i = 0; i <= N; i++)
            {
                float t = i / (float)N;
                Vector2 p = LineGraph.Bezier(S, c1, c2, E, t);
                Vector2 tan = LineGraph.BezierTangent(S, c1, c2, E, t);
                if (tan.sqrMagnitude < 1e-8f) tan = E - S;
                tan = tan.sqrMagnitude > 1e-8f ? tan.normalized : Vector2.right;
                Vector2 nrm = new Vector2(-tan.y, tan.x);
                _boxL.Add(p + nrm * halfW);
                _boxR.Add(p - nrm * halfW);
            }
            if (hatched)
                for (int i = 0; i < N; i++)
                    InspLine(InspLift(field, _boxL[i], lift), InspLift(field, _boxR[i + 1], lift), hatch);
            else if (filled)
                for (int i = 0; i < N; i++)
                {
                    Vector3 l0 = InspLift(field, _boxL[i], lift), r0 = InspLift(field, _boxR[i], lift);
                    Vector3 l1 = InspLift(field, _boxL[i + 1], lift), r1 = InspLift(field, _boxR[i + 1], lift);
                    InspTri(l0, r0, r1, fill); InspTri(l0, r1, l1, fill);
                }
            InspDashedPolyline(field, _boxL, lift, outline);
            InspDashedPolyline(field, _boxR, lift, outline);
            InspLine(InspLift(field, _boxL[0], lift), InspLift(field, _boxR[0], lift), outline);
            InspLine(InspLift(field, _boxL[N], lift), InspLift(field, _boxR[N], lift), outline);
        }

        int HoveredCurveEdge(Vector2 cursor, float halfW)
        {
            int best = -1; float bestD = halfW * halfW;
            for (int ei = 0; ei < Graph.Edges.Count; ei++)
            {
                LineEdge e = Graph.Edges[ei];
                Vector2 S = Graph.Nodes[e.A], E = Graph.Nodes[e.B];
                EdgeControls(e, S, E, out Vector2 c1, out Vector2 c2);
                const int N = 16;
                Vector2 prev = S;
                for (int i = 1; i <= N; i++)
                {
                    Vector2 cur = LineGraph.Bezier(S, c1, c2, E, i / (float)N);
                    float d2 = SqDistToSeg(cursor, prev, cur);
                    if (d2 < bestD) { bestD = d2; best = ei; }
                    prev = cur;
                }
            }
            return best;
        }

        static float SqDistToSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a; float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-9f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            return (p - (a + ab * t)).sqrMagnitude;
        }

        void EmitHoverDetails(TerrainField field, int ei, float lift)
        {
            LineEdge e = Graph.Edges[ei];
            Vector2 S = Graph.Nodes[e.A], E = Graph.Nodes[e.B];
            EdgeControls(e, S, E, out Vector2 c1, out Vector2 c2);
            Color32 legCol = new Color32(255, 235, 90, 230);

            InspectHovered = true;
            InspectIsCurve = e.HasCurve;
            InspectMid = LineGraph.Bezier(S, c1, c2, E, 0.5f);
            InspectRated = SpeedLimitKmh;                  // plan: the one design speed
            InspectHasGrade = false; InspectGradePct = 0f; InspectDecel = null; // plan has no built grade/decel

            if (!e.HasCurve)
            {
                float run = (E - S).magnitude;
                InspectRadius = float.PositiveInfinity;
                InspectMaxSpeed = 0f;
                InspectHasCorner = false;
                InspectLength = run;
                InspectTrainCount = TypicalTrainLengthM > 1f ? run / TypicalTrainLengthM : 0f;
                return;
            }

            InspectRadius = MinCurveRadius(S, c1, c2, E);
            float rMS = float.IsInfinity(InspectRadius) ? 1e6f : InspectRadius;
            InspectMaxSpeed = Mathf.Sqrt(Mathf.Max(0f, rMS) * 9.81f * Mathf.Max(0.01f, MaxLateralG)) * 3.6f;

            InspectHasCorner = LineIntersect(S, c1 - S, E, c2 - E, out Vector2 corner);
            if (InspectHasCorner)
            {
                InspectCorner = corner;
                InspectLegA = Vector2.Distance(S, corner);
                InspectLegB = Vector2.Distance(E, corner);
                InspectLegAMid = (S + corner) * 0.5f;
                InspectLegBMid = (E + corner) * 0.5f;
                InspectAngleDeg = Vector2.Angle(corner - S, E - corner);
                InspDashedSeg(field, S, corner, lift, legCol);
                InspDashedSeg(field, E, corner, lift, legCol);
                Vector2 d1 = (S - corner), d2 = (E - corner);
                if (d1.sqrMagnitude > 1e-6f && d2.sqrMagnitude > 1e-6f)
                {
                    float r = Mathf.Min(InspectLegA, InspectLegB) * 0.18f;
                    float a1 = Mathf.Atan2(d1.y, d1.x), a2 = Mathf.Atan2(d2.y, d2.x);
                    float sweep = Mathf.DeltaAngle(a1 * Mathf.Rad2Deg, a2 * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    const int AN = 12; Vector3 prev = default; bool have = false;
                    for (int i = 0; i <= AN; i++)
                    {
                        float a = a1 + sweep * (i / (float)AN);
                        Vector2 ap = corner + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                        Vector3 w = InspLift(field, ap, lift);
                        if (have) InspLine(prev, w, legCol);
                        prev = w; have = true;
                    }
                }
            }
        }

        static bool LineIntersect(Vector2 p1, Vector2 d1, Vector2 p2, Vector2 d2, out Vector2 x)
        {
            x = Vector2.zero;
            float denom = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(denom) < 1e-6f) return false;
            float t = ((p2.x - p1.x) * d2.y - (p2.y - p1.y) * d2.x) / denom;
            x = p1 + d1 * t;
            return true;
        }

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
