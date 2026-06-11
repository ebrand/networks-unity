// A road "plan" corridor: a node/edge bezier graph drawn on the terrain (mirrors the rail PlanLayer and
// the generic LineworkLayer through ITerrainLineLayer, so TerrainDesigner drives the click-to-chain
// drawing for free). It carries a road WIDTH so the plan shows the road's footprint — the centreline plus
// the two outer edges at ±halfWidth — draped on the terrain as a line overlay. The plan is the alignment
// you later excavate a bed for and sweep the parametric RoadSweep road along (see [[road-designer-3d]]).
//
// Phase 1: draw + visualise the corridor. Curve smoothing, snapping, profile binding and the excavate/lay
// pipeline come next, reusing the rail plan + GeometryResolver machinery.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class RoadPlanLayer : ITerrainLineLayer
    {
        public string Name = "Road Plan";
        string ITerrainLineLayer.LayerName => Name;

        [Tooltip("Profile (road-config.json id/name) applied to NEW segments as you draw them; empty = use RoadWidth.")]
        public string ActiveProfileId = "";
        [Tooltip("Fallback width (m) for segments with no profile — corridor footprint either side of the centreline.")]
        public float RoadWidth = 14f;

        // Footprint width for a single segment: its profile's total cross-section, else the fallback width.
        public float EdgeWidth(LineEdge e) => NetworkDesigner.Roads.RoadProfileLibrary.TotalWidth(e?.Profile, RoadWidth);
        // The active profile's width (for the placement preview).
        public float ActiveWidth() => NetworkDesigner.Roads.RoadProfileLibrary.TotalWidth(ActiveProfileId, RoadWidth);
        [Tooltip("Straight edges with hard corners (default). Off = auto-smoothed bezier through the nodes.")]
        public bool Straight = true;
        [Tooltip("Guided drawing: a continuing straight locks to colinear; turns must be a speed-based curve, or a 90° corner where the speed allows. Off = freehand straights at any angle.")]
        public bool GuidedTurns = true;
        [Tooltip("At/below this design speed (km/h), a straight may snap a hard 90° corner; above it, turns must be curves.")]
        public float HardCornerMaxSpeedKmh = 50f;
        public bool AllowHardCorner => DesignSpeedKmh <= HardCornerMaxSpeedKmh;
        [System.NonSerialized] public bool StraightOffAxis;   // cursor isn't on an allowed heading → suppress the (kinked) click
        [Tooltip("Metres between draped samples along the curve.")]
        public float SampleStep = 2f;
        [Tooltip("Metres above the terrain (avoids z-fighting with the ground).")]
        public float Lift = 0.2f;
        [Tooltip("Metres between the cross-ties drawn across the corridor.")]
        public float TieSpacing = 8f;
        public Color PlanColor = new Color(1f, 0.55f, 0.12f, 0.95f);   // amber-orange (rail plan is yellow)
        // Guide/snap controls — shared by all plan tools via PlanGuides (tune in the Guides palette).
        public float NodePickRadius { get => PlanGuides.NodePickRadius; set => PlanGuides.NodePickRadius = value; }
        public float ExtensionSnapRadius { get => PlanGuides.ExtensionSnapRadius; set => PlanGuides.ExtensionSnapRadius = value; }
        public float EndSnapRadius { get => PlanGuides.EndSnapRadius; set => PlanGuides.EndSnapRadius = value; }
        public float ExtensionGuideLength { get => PlanGuides.ExtensionGuideLength; set => PlanGuides.ExtensionGuideLength = value; }
        public float NodePuckRadius { get => PlanGuides.NodePuckRadius; set => PlanGuides.NodePuckRadius = value; }
        public float CurveLever { get => PlanGuides.CurveLever; set => PlanGuides.CurveLever = value; }

        // ── shift-curves: hold Shift, click to drop a bend corner, click again for the end ──
        [Tooltip("Refuse curves tighter than the design speed allows (AASHTO-style min radius). No decel zones — one design speed for the corridor.")]
        public bool LimitCurveRadius = true;
        [Tooltip("Design speed (km/h) the corridor's curves are built for.")]
        public float DesignSpeedKmh = 40f;
        [Range(0f, 0.12f)]
        [Tooltip("Max superelevation e_max (road banking). Urban low-speed ~0.04, suburban/rural ~0.06, mountain up to 0.12.")]
        public float MaxSuperelevation = 0.06f;
        // AASHTO minimum horizontal radius (m): R = V²/(127·(e_max + f)), V in km/h, f = speed-dependent side friction.
        public float MinRadiusForSpeed
        {
            get { float v = DesignSpeedKmh; return v * v / (127f * Mathf.Max(0.02f, MaxSuperelevation + SideFriction(v))); }
        }
        // AASHTO max side-friction factor f, interpolated by design speed (km/h) — it drops as speed rises.
        static float SideFriction(float kmh)
        {
            float[] V = { 30f, 40f, 50f, 60f, 70f, 80f, 90f, 100f, 110f, 120f };
            float[] F = { 0.28f, 0.23f, 0.19f, 0.17f, 0.15f, 0.14f, 0.13f, 0.12f, 0.11f, 0.09f };
            if (kmh <= V[0]) return F[0];
            for (int i = 1; i < V.Length; i++)
                if (kmh <= V[i]) return Mathf.Lerp(F[i - 1], F[i], (kmh - V[i - 1]) / (V[i] - V[i - 1]));
            return F[F.Length - 1];
        }
        [Tooltip("Smallest deflection (deg) that still counts as a real turn — below this, treat the leg as straight.")]
        public float MinCurveDeflectionDeg = 5f;
        public float CurveSymmetrySnap { get => PlanGuides.CurveSymmetrySnap; set => PlanGuides.CurveSymmetrySnap = value; }

        [System.NonSerialized] public bool CurveModifier;                       // Shift held this frame
        [System.NonSerialized] public float LastPreviewRadius = float.PositiveInfinity;
        [System.NonSerialized] public bool LastPreviewTooTight;                 // pending curve tighter than MinRadiusForSpeed
        // While a shift-curve bend is being placed: geometry for the on-screen leg/angle labels.
        [System.NonSerialized] public bool PreviewCurveActive;
        [System.NonSerialized] public Vector2 PreviewTail, PreviewCorner, PreviewEnd;
        [System.NonSerialized] public float PreviewLegA, PreviewLegB, PreviewDeflectionDeg;
        public bool CornerPending => _cornerPending;                           // a bend is armed, awaiting the end click
        public void CancelCorner() { _cornerPending = false; }                 // right-click backs out of the armed bend
        Vector2 _corner; bool _cornerPending;

        // ---- runtime (not serialized) ----
        LineGraph _graph = new LineGraph();
        public LineGraph Graph => _graph ??= new LineGraph();
        int _chainTail = -1;

        GameObject _root; MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; Material _mat;
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<int> _idx = new List<int>();
        readonly List<Color32> _col = new List<Color32>();   // per-vertex lane-marking colours
        // Node pucks ride a SEPARATE 3D mesh (lit-transparent) so they carry their own colour + height + toggle.
        GameObject _nodeGo; MeshFilter _nodeMf; MeshRenderer _nodeMr; Mesh _nodeMesh; Material _nodeMat;
        readonly List<Vector3> _nv = new List<Vector3>();
        readonly List<Vector3> _nn = new List<Vector3>();
        readonly List<int> _nidx = new List<int>();

        GameObject _pvGo; MeshFilter _pvMf; MeshRenderer _pvMr; Mesh _pvMesh; Material _pvBadMat;
        readonly List<Vector3> _pv = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();       // submesh 0: normal (plan colour)
        readonly List<int> _pvBadIdx = new List<int>();    // submesh 1: too-tight curve (red)

        // Equal-leg "PAC" ring (vertex-coloured yellow=buildable / red=too-tight) + its 15° tick labels.
        GameObject _symGo; MeshFilter _symMf; MeshRenderer _symMr; Mesh _symMesh; Material _symMat;
        readonly List<Vector3> _symV = new List<Vector3>();
        readonly List<int> _symIdx = new List<int>();
        readonly List<Color32> _symCol = new List<Color32>();
        [System.NonSerialized] public readonly List<Vector3> CurveTickWorld = new List<Vector3>();
        [System.NonSerialized] public readonly List<int> CurveTickDeg = new List<int>();

        const int SubSteps = 48;
        static readonly Vector2[] _pts = new Vector2[SubSteps + 1];

        string RootName => "RoadPlan_" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- editing (chain drawing) ----

        public void AddNode(ITerrainSurface field, Vector3 hit)
        {
            Vector2 p = new Vector2(hit.x, hit.z);
            if (_chainTail < 0)   // start a chain: grab an existing node/edge so corridors branch + join
            {
                int near = Graph.NearestNode(p, NodePickRadius);
                if (near >= 0) _chainTail = near;
                else if (Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _)) { _chainTail = Graph.SplitEdge(ei, tt); Rebuild(field); }
                else _chainTail = Graph.AddNode(p);
                _cornerPending = false;
                return;
            }
            if (_cornerPending)   // END click of a shift-curve: build the curve tail→end through the armed corner
            {
                if (LimitCurveRadius)
                {
                    CurveControls(Graph.Nodes[_chainTail], p, _corner, out Vector2 cc1, out Vector2 cc2);
                    if (MinCurveRadius(Graph.Nodes[_chainTail], cc1, cc2, p) < MinRadiusForSpeed) return;   // too tight: keep the corner armed for a wider end
                }
                int endc = NearestOrNew(p);
                AddCurvedEdge(_chainTail, endc, _corner);
                _chainTail = endc; _cornerPending = false;
                Rebuild(field);
                return;
            }
            if (CurveModifier) { _corner = p; _cornerPending = true; return; }   // arm the bend; the next click is the end
            int end = NearestOrNew(p);   // join an existing node → a real intersection
            int before = Graph.Edges.Count;
            Graph.AddEdge(_chainTail, end);
            if (Graph.Edges.Count > before) Graph.Edges[Graph.Edges.Count - 1].Profile = ActiveProfileId;   // tag the new segment
            _chainTail = end;
            Rebuild(field);
        }

        int NearestOrNew(Vector2 p)
        {
            int near = Graph.NearestNode(p, NodePickRadius);
            if (near >= 0 && near != _chainTail) return near;
            // Crossing an existing road mid-span → split it into a shared intersection node (so an
            // oblique through-crossing becomes a real junction, not two overlapping roads).
            if (near < 0 && Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _))
                return Graph.SplitEdge(ei, tt);
            return Graph.AddNode(p);
        }

        // Cubic controls that pull the curve toward the bend corner (CurveLever ≈ 0.55 ≈ circular arc).
        void CurveControls(Vector2 a, Vector2 b, Vector2 corner, out Vector2 c1, out Vector2 c2)
        {
            float f = Mathf.Clamp(CurveLever, 0.1f, 0.95f);
            c1 = Vector2.Lerp(a, corner, f);
            c2 = Vector2.Lerp(b, corner, f);
        }

        void AddCurvedEdge(int a, int b, Vector2 corner)
        {
            int before = Graph.Edges.Count;
            Graph.AddEdge(a, b);
            if (Graph.Edges.Count <= before) return;   // edge already existed (dedup) — leave it straight
            LineEdge e = Graph.Edges[Graph.Edges.Count - 1];
            CurveControls(Graph.Nodes[a], Graph.Nodes[b], corner, out Vector2 c1, out Vector2 c2);
            e.HasCurve = true; e.ControlA = c1; e.ControlB = c2; e.Profile = ActiveProfileId;
        }

        // Tightest radius (m) along a cubic bezier, via 3-point circumradius over samples (+inf for a line).
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

        // ---- curve-constraint guides (mirrors the rail plan: equal-leg ring, buildable arc, 15° ticks) ----

        // Would a symmetric curve through (start, bend, end) meet the design-speed minimum radius?
        bool CurveBuildable(Vector2 start, Vector2 bend, Vector2 end)
        {
            if (!LimitCurveRadius) return true;
            Vector2 inD = bend - start, outD = end - bend;
            if (inD.sqrMagnitude < 1e-6f || outD.sqrMagnitude < 1e-6f) return false;
            CurveControls(start, end, bend, out Vector2 c1, out Vector2 c2);
            return MinCurveRadius(start, c1, c2, end) >= MinRadiusForSpeed;
        }

        // After the bend is armed, lock the end onto the equal-leg circle and clamp its direction
        // into the buildable arc (and onto 15° ticks). False when the leg is too short for any turn.
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
                Vector2 t0 = cursor - bend;
                Vector2 d0 = t0.sqrMagnitude > 1e-6f ? t0.normalized : (bend - start).normalized;
                snapped = bend + d0 * legA; return true;
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

        // Largest deflection (deg) on the given side whose curve still meets the min radius.
        float MaxBuildableDeflection(Vector2 start, Vector2 bend, float legA, float a0A, float sign)
        {
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

        // While positioning the bend (Shift held, before the end click), keep the first leg at least
        // long enough for a real turn at the speed: lock onto the collinear extension (no kink) if
        // extending, else onto the min-distance-target ring.
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
                snapped = start + ext * along;
                return true;
            }
            Vector2 toCur = cursor - start; float d = toCur.magnitude;
            if (d < 1e-4f) return false;
            if (d >= minLeg + Mathf.Max(5f, minLeg * 0.04f)) return false;   // well past target → free angle
            snapped = start + (toCur / d) * minLeg;
            return true;
        }

        // Leg length at which a MinCurveDeflectionDeg curve first meets the min radius (0 if unlimited).
        float MinFirstLegForSpeed()
        {
            if (!LimitCurveRadius) return 0f;
            float th = Mathf.Max(0.1f, MinCurveDeflectionDeg) * Mathf.Deg2Rad;
            Vector2 end = new Vector2(1f + Mathf.Cos(th), Mathf.Sin(th));
            CurveControls(Vector2.zero, end, new Vector2(1f, 0f), out Vector2 c1, out Vector2 c2);
            float k = MinCurveRadius(Vector2.zero, c1, c2, end);
            return k > 1e-3f ? MinRadiusForSpeed / k : 0f;
        }

        // True while a curve is being built (bend awaiting placement, or end awaiting it).
        public bool InCurveMode => (CurveModifier || _cornerPending) && CurveSymmetrySnap > 0f && LimitCurveRadius;
        // True while positioning the END: the equal-leg ring owns the cursor exclusively.
        public bool PlacingCurveEnd => _cornerPending && CurveSymmetrySnap > 0f && LimitCurveRadius;

        public void EndChain()
        {
            // Cancelling a just-started chain leaves a lone node with no edges — drop it.
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count && !NodeHasEdge(_chainTail)) Graph.RemoveNode(_chainTail);
            _chainTail = -1; _cornerPending = false;
        }

        bool NodeHasEdge(int n) { foreach (LineEdge e in Graph.Edges) if (e.A == n || e.B == n) return true; return false; }

        public void ClearAll(ITerrainSurface field)
        {
            Graph.Clear();
            _chainTail = -1;
            Rebuild(field);
        }

        public void RemoveLastNode(ITerrainSurface field)
        {
            int last = Graph.Nodes.Count - 1;
            if (last < 0) return;
            Graph.Edges.RemoveAll(e => e.A == last || e.B == last);
            Graph.Nodes.RemoveAt(last);
            if (_chainTail >= Graph.Nodes.Count) _chainTail = -1;
            Rebuild(field);
        }

        public bool DeleteNearNode(ITerrainSurface field, Vector3 hit, float radius)
        {
            int n = Graph.NearestNode(new Vector2(hit.x, hit.z), radius);
            if (n < 0) return false;
            Graph.RemoveNode(n);
            if (_chainTail == n) _chainTail = -1;
            else if (_chainTail > n) _chainTail--;
            _cornerPending = false;
            PruneOrphanNodes();   // drop the far end(s) of the deleted segment(s) if now edgeless
            Rebuild(field);
            return true;
        }

        // Remove any node left with no edges (e.g. the far end of a just-deleted segment),
        // keeping the active chain tail (a fresh, not-yet-connected start node).
        void PruneOrphanNodes()
        {
            for (int i = Graph.Nodes.Count - 1; i >= 0; i--)
            {
                if (i == _chainTail || NodeHasEdge(i)) continue;
                Graph.RemoveNode(i);
                if (_chainTail > i) _chainTail--;
            }
        }

        // ---- snapping (extension guide + node join), mirroring the rail plan tool ----

        // Heading continuing straight out of the chain tail (collinear with the incoming segment);
        // the cursor side picks which leg to extend when the tail has several edges.
        bool IncomingDirection(Vector2 toward, out Vector2 dir)
        {
            dir = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            Vector2 side = toward - Graph.Nodes[_chainTail];
            float bestDot = float.NegativeInfinity; bool found = false;
            foreach (LineEdge e in Graph.Edges)
            {
                if (e.A != _chainTail && e.B != _chainTail) continue;
                EdgeBezier(e, out Vector2 p0, out Vector2 q1, out Vector2 q2, out Vector2 p3);
                Vector2 cont = e.B == _chainTail ? LineGraph.BezierTangent(p0, q1, q2, p3, 1f) : -LineGraph.BezierTangent(p0, q1, q2, p3, 0f);
                if (cont.sqrMagnitude < 1e-6f) cont = e.B == _chainTail ? p3 - p0 : p0 - p3;
                if (cont.sqrMagnitude < 1e-6f) continue;
                cont = cont.normalized;
                float dot = Vector2.Dot(cont, side);
                if (dot > bestDot) { bestDot = dot; dir = cont; found = true; }
            }
            return found;
        }

        public bool TryGetTailXZ(out Vector2 pos)
        {
            pos = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            pos = Graph.Nodes[_chainTail]; return true;
        }

        // SOFT-snap the cursor onto the straight-ahead extension of the previous segment (within
        // ExtensionSnapRadius, ahead of the tail). Roads turn freely, so this assists, it doesn't lock.
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
            snapped = proj; return true;
        }

        // Snap onto the plan's own nearest node/edge within EndSnapRadius (excluding the active anchor) —
        // so segments join existing nodes into intersections, and you can resume from any end.
        public bool TrySnapToOwnNode(Vector2 p, out Vector2 snapped)
        {
            snapped = p;
            if (!PlanGuides.ProximitySnapOn) return false;
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

        // Hard-lock a continuing straight to an allowed heading off the chain tail: colinear (continue the
        // tangent) always, plus a 90° corner when the design speed permits one. Snaps the cursor to the
        // nearest allowed heading within tolerance; otherwise reports offAxis so the caller suppresses the
        // (kinked) click. Returns false with offAxis=false when there's no constraint (first tangent /
        // guided off / drawing a curve) so the caller can fall back to the soft assist.
        public bool SnapStraightConstrained(Vector2 cursor, out Vector2 snapped, out bool offAxis)
        {
            snapped = cursor; offAxis = false;
            if (!GuidedTurns || CurveModifier || _cornerPending) return false;     // curve path / freehand
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            if (!IncomingDirection(cursor, out Vector2 inDir)) return false;       // first tangent: free angle
            Vector2 tail = Graph.Nodes[_chainTail];
            Vector2 toCur = cursor - tail;
            float dist = toCur.magnitude;
            if (dist < 1e-3f) return false;
            Vector2 curDir = toCur / dist;

            // Pick the closest allowed heading: colinear, plus the two 90° corners if the speed allows.
            float bestDot = Vector2.Dot(curDir, inDir); Vector2 best = inDir;
            if (AllowHardCorner)
            {
                Vector2 hL = new Vector2(-inDir.y, inDir.x), hR = new Vector2(inDir.y, -inDir.x);
                float dL = Vector2.Dot(curDir, hL), dR = Vector2.Dot(curDir, hR);
                if (dL > bestDot) { bestDot = dL; best = hL; }
                if (dR > bestDot) { bestDot = dR; best = hR; }
            }
            const float tolDeg = 22f;   // within this of an allowed heading → snap; else it's a kink
            float devDeg = Mathf.Acos(Mathf.Clamp(bestDot, -1f, 1f)) * Mathf.Rad2Deg;
            if (devDeg > tolDeg) { offAxis = true; return false; }
            float along = Vector2.Dot(toCur, best);
            if (along <= 0.1f) { offAxis = true; return false; }                   // behind the tail → suppress
            snapped = tail + best * along;
            return true;
        }

        // ---- rendering: a draped corridor ribbon (centreline + both edges + cross-ties) + node pucks ----

        public void Rebuild(ITerrainSurface field)
        {
            EnsureRoot();
            _v.Clear(); _idx.Clear(); _col.Clear(); _nv.Clear(); _nn.Clear(); _nidx.Clear();

            foreach (LineEdge e in Graph.Edges)
            {
                EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
                BuildCorridorEdge(field, p0, p1, p2, p3, e);   // lane schematic from the segment's profile
            }
            foreach (Vector2 n in Graph.Nodes) DrawPuck(field, n);   // into the node mesh (own colour + toggle)

            _mesh.Clear(); _mesh.SetVertices(_v); _mesh.SetColors(_col); _mesh.SetIndices(_idx, MeshTopology.Lines, 0); _mesh.RecalculateBounds();
            _nodeMesh.Clear(); _nodeMesh.SetVertices(_nv); _nodeMesh.SetNormals(_nn); _nodeMesh.SetTriangles(_nidx, 0); _nodeMesh.RecalculateBounds();
            _nodeMr.enabled = PlanGuides.ShowNodes;
            if (_nodeMat != null) _nodeMat.color = PlanGuides.RoadNodeColor;   // live colour
        }

        void EdgeBezier(LineEdge e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3)
        {
            if (e.HasCurve)   // an explicit shift-curve always renders as its own bezier (even with Straight on)
            {
                p0 = Graph.Nodes[e.A]; p3 = Graph.Nodes[e.B]; p1 = e.ControlA; p2 = e.ControlB;
            }
            else if (Straight || HasCurvedNeighbor(e))   // hard-corner mode, OR keep a tangent straight beside a curve
            {
                p0 = Graph.Nodes[e.A]; p3 = Graph.Nodes[e.B];
                Vector2 d = p3 - p0; p1 = p0 + d / 3f; p2 = p0 + d * (2f / 3f);
            }
            else Graph.EdgeControls(e, out p0, out p1, out p2, out p3);
        }

        // A plain segment sharing a node with an explicit shift-curve stays straight — the curve
        // shouldn't bow its neighbour via the Catmull-Rom auto-smoothing (what you drew as a tangent
        // stays a tangent). Pure straight chains (no curves) still auto-smooth.
        bool HasCurvedNeighbor(LineEdge e)
        {
            foreach (LineEdge o in Graph.Edges)
                if (!ReferenceEquals(o, e) && o.HasCurve
                    && (o.A == e.A || o.A == e.B || o.B == e.A || o.B == e.B)) return true;
            return false;
        }

        // ---- lane schematic: draw the segment's cross-section as real lane markings ----

        static readonly Color32 ColEdge   = new Color32(235, 235, 235, 255);   // pavement / lane edge line (white, solid)
        static readonly Color32 ColLane   = new Color32(205, 205, 205, 235);   // same-direction lane divider (dashed)
        static readonly Color32 ColCenter = new Color32(245, 205, 45, 255);    // opposing centreline (yellow, double)
        static readonly Color32 ColTurn   = new Color32(245, 205, 45, 255);    // turn-lane boundary (yellow)
        static readonly Color32 ColMedian = new Color32(200, 165, 110, 220);   // median hatch (warm tan — reads as raised, not drivable)
        Color32 ColFoot => new Color32((byte)(PlanColor.r * 255f), (byte)(PlanColor.g * 255f), (byte)(PlanColor.b * 255f), 170); // shoulder/footprint (plan amber, dashed)

        // strip kinds across the cross-section (BA side → centre → AB side)
        const int KOut = -1, KShBA = 0, KLnBA = 1, KMed = 2, KTrn = 3, KLnAB = 4, KShAB = 5;

        // Lay the segment's lanes/median/turn-lane/shoulders out as draped markings, draped along the bezier.
        void BuildCorridorEdge(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, LineEdge e)
        {
            float len = 0f;
            _pts[0] = p0;
            for (int i = 1; i <= SubSteps; i++) { _pts[i] = LineGraph.Bezier(p0, p1, p2, p3, i / (float)SubSteps); len += Vector2.Distance(_pts[i - 1], _pts[i]); }
            if (len < 1e-3f) return;
            int n = Mathf.Clamp(Mathf.CeilToInt(len / 1.5f), 2, 2048);   // fine enough for dashed markings

            NetworkDesigner.Model.RoadProfile prof = NetworkDesigner.Roads.RoadProfileLibrary.Resolve(e?.Profile);
            if (prof == null || prof.TotalWidth < 0.5f)
            {
                float half = Mathf.Max(0.1f, EdgeWidth(e) * 0.5f);
                EmitOffsetLine(field, p0, p1, p2, p3, n, half, ColEdge, 0f, 0f);   // generic: two solid edges + dashed centre
                EmitOffsetLine(field, p0, p1, p2, p3, n, -half, ColEdge, 0f, 0f);
                EmitOffsetLine(field, p0, p1, p2, p3, n, 0f, ColCenter, 2f, 2f);
                return;
            }

            float W = prof.TotalWidth;
            // Build the strip order across the section.
            var w = new List<float>(8); var k = new List<int>(8);
            void S(float width, int kind) { if (width > 0.01f) { w.Add(width); k.Add(kind); } }
            S(prof.ShoulderBA.Width, KShBA);
            for (int i = prof.BA.Lanes.Count - 1; i >= 0; i--) S(prof.BA.Lanes[i].Width, KLnBA);
            if (prof.Median != null) S(prof.Median.Width, KMed);
            else if (prof.TurnLane != null) S(prof.TurnLane.Width, KTrn);
            for (int i = 0; i < prof.AB.Lanes.Count; i++) S(prof.AB.Lanes[i].Width, KLnAB);
            S(prof.ShoulderAB.Width, KShAB);

            float u = -W * 0.5f;
            EmitBoundary(field, p0, p1, p2, p3, n, u, KOut, k.Count > 0 ? k[0] : KOut);
            for (int i = 0; i < w.Count; i++)
            {
                if (k[i] == KMed) EmitMedianHatch(field, p0, p1, p2, p3, n, len, u, u + w[i]);
                u += w[i];
                EmitBoundary(field, p0, p1, p2, p3, n, u, k[i], (i + 1 < k.Count) ? k[i + 1] : KOut);
            }
        }

        // Pick the marking style for the line between two strip kinds, then emit it.
        void EmitBoundary(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int n, float u, int left, int right)
        {
            bool isSh(int kk) => kk == KShBA || kk == KShAB;
            bool isLn(int kk) => kk == KLnBA || kk == KLnAB;
            if (left == KOut || right == KOut)
            {
                int s = left == KOut ? right : left;
                if (isSh(s)) EmitOffsetLine(field, p0, p1, p2, p3, n, u, ColFoot, 3f, 2.2f);   // footprint/shoulder edge (dashed amber)
                else EmitOffsetLine(field, p0, p1, p2, p3, n, u, ColEdge, 0f, 0f);             // pavement edge (solid)
                return;
            }
            if (isSh(left) || isSh(right)) { EmitOffsetLine(field, p0, p1, p2, p3, n, u, ColEdge, 0f, 0f); return; }  // shoulder|lane edge
            if (left == KMed || right == KMed) { EmitOffsetLine(field, p0, p1, p2, p3, n, u, ColEdge, 0f, 0f); return; } // median edge (solid white)
            if (left == KTrn || right == KTrn) { EmitOffsetLine(field, p0, p1, p2, p3, n, u, ColTurn, 0f, 0f); return; } // turn-lane edge (yellow)
            if (isLn(left) && isLn(right))
            {
                if (left == right) EmitOffsetLine(field, p0, p1, p2, p3, n, u, ColLane, 1.5f, 2.2f);   // same dir → dashed lane divider
                else { EmitOffsetLine(field, p0, p1, p2, p3, n, u - 0.25f, ColCenter, 0f, 0f);          // opposing → double-yellow centreline
                       EmitOffsetLine(field, p0, p1, p2, p3, n, u + 0.25f, ColCenter, 0f, 0f); }
                return;
            }
            EmitOffsetLine(field, p0, p1, p2, p3, n, u, ColEdge, 0f, 0f);
        }

        // A line at constant lateral offset `u`, draped, dashed when gap>0 (solid when gap<=0).
        void EmitOffsetLine(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int n, float u, Color32 col, float dash, float gap)
        {
            float period = dash + gap, walked = 0f;
            Vector3 prev = default; bool have = false;
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                Vector2 pos = LineGraph.Bezier(p0, p1, p2, p3, t);
                Vector2 tan = LineGraph.BezierTangent(p0, p1, p2, p3, t);
                Vector2 perp = tan.sqrMagnitude > 1e-8f ? new Vector2(-tan.y, tan.x).normalized : Vector2.right;
                Vector3 cwld = Drape(field, pos + perp * u);
                if (have)
                {
                    bool on = gap <= 0f || (walked % period) < dash;
                    if (on) AddSeg(prev, cwld, col);
                    walked += Vector3.Distance(prev, cwld);
                }
                prev = cwld; have = true;
            }
        }

        // Diagonal hatch fill across a median band [uA,uB] (the boundary lines are drawn separately).
        void EmitMedianHatch(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int n, float len, float uA, float uB)
        {
            float band = Mathf.Abs(uB - uA);
            int m = Mathf.Clamp(Mathf.CeilToInt(len / 3f), 1, 1024);
            float dt = Mathf.Max(1e-4f, band / Mathf.Max(1f, len));   // ~45° lead in t for the diagonal
            for (int j = 0; j < m; j++)
            {
                float t0 = (float)j / m;
                float t1 = Mathf.Min(1f, t0 + dt);
                AddSeg(OffsetPt(field, p0, p1, p2, p3, t0, uA), OffsetPt(field, p0, p1, p2, p3, t1, uB), ColMedian);
            }
        }

        Vector3 OffsetPt(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t, float u)
        {
            Vector2 pos = LineGraph.Bezier(p0, p1, p2, p3, t);
            Vector2 tan = LineGraph.BezierTangent(p0, p1, p2, p3, t);
            Vector2 perp = tan.sqrMagnitude > 1e-8f ? new Vector2(-tan.y, tan.x).normalized : Vector2.right;
            return Drape(field, pos + perp * u);
        }

        Vector3 Drape(ITerrainSurface field, Vector2 xz)
            => new Vector3(xz.x, (field != null ? field.SampleHeight(xz.x, xz.y) : 0f) + Lift, xz.y);

        void AddSeg(Vector3 a, Vector3 b, Color32 col)
        { int s = _v.Count; _v.Add(a); _v.Add(b); _col.Add(col); _col.Add(col); _idx.Add(s); _idx.Add(s + 1); }
        // A short draped 3D cylinder puck (top cap + side wall, manual outward normals) at a node — the
        // visible handle you grab to move / curve / delete. Lit-transparent so the alpha shows. Mirrors rail.
        void DrawPuck(ITerrainSurface field, Vector2 c)
        {
            const int N = 16;
            float radius = Mathf.Max(0.2f, NodePuckRadius);
            float baseY = (field != null ? field.SampleHeight(c.x, c.y) : 0f) + Lift;   // coplanar with the corridor ribbon
            float topY = baseY + Mathf.Max(0.02f, PlanGuides.NodePuckHeight);

            int capC = _nv.Count;
            _nv.Add(new Vector3(c.x, topY, c.y)); _nn.Add(Vector3.up);
            int capRim = _nv.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                _nv.Add(new Vector3(c.x + Mathf.Cos(a) * radius, topY, c.y + Mathf.Sin(a) * radius)); _nn.Add(Vector3.up);
            }
            for (int i = 0; i < N; i++) { _nidx.Add(capC); _nidx.Add(capRim + i + 1); _nidx.Add(capRim + i); }   // cap up

            int wTop = _nv.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f; float nx = Mathf.Cos(a), nz = Mathf.Sin(a);
                _nv.Add(new Vector3(c.x + nx * radius, topY, c.y + nz * radius)); _nn.Add(new Vector3(nx, 0f, nz));
            }
            int wBot = _nv.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f; float nx = Mathf.Cos(a), nz = Mathf.Sin(a);
                _nv.Add(new Vector3(c.x + nx * radius, baseY, c.y + nz * radius)); _nn.Add(new Vector3(nx, 0f, nz));
            }
            for (int i = 0; i < N; i++)
            {
                int ti = wTop + i, tj = wTop + i + 1, bi = wBot + i, bj = wBot + i + 1;
                _nidx.Add(bi); _nidx.Add(ti); _nidx.Add(tj);   // outward
                _nidx.Add(bi); _nidx.Add(tj); _nidx.Add(bj);
            }
        }

        void EnsureRoot()
        {
            if (_mf != null) { return; }
            GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].name == RootName) DestroySafe(all[i]);
            _root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            _mf = _root.AddComponent<MeshFilter>();
            _mr = _root.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _mr.receiveShadows = false;
            _mesh = new Mesh { name = "RoadPlanMesh" };
            _mf.sharedMesh = _mesh;
            Shader vc = Shader.Find("NetworkDesigner/VertexColorOverlay");   // per-vertex lane-marking colours
            _mat = vc != null ? new Material(vc) { name = "RoadPlanMat" } : MakeMat(PlanColor, "RoadPlanMat");
            _mr.sharedMaterial = _mat;

            _nodeGo = new GameObject(RootName + "_Nodes") { hideFlags = HideFlags.DontSave };
            _nodeGo.transform.SetParent(_root.transform, false);
            _nodeMf = _nodeGo.AddComponent<MeshFilter>();
            _nodeMr = _nodeGo.AddComponent<MeshRenderer>();
            _nodeMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _nodeMr.receiveShadows = false;
            _nodeMesh = new Mesh { name = "RoadPlanNodesMesh" };
            _nodeMf.sharedMesh = _nodeMesh;
            _nodeMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(PlanGuides.RoadNodeColor, 0.2f, "RoadPlanNodeMat");
            _nodeMr.sharedMaterial = _nodeMat;
        }

        Material MakeMat(Color c, string name)
        {
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            return sh != null ? new Material(sh) { name = name, color = c }
                              : NetworkDesigner.PipelineMaterials.CreateUnlitColor(c, name);
        }

        // ---- placement preview (ghost puck + dashed pending edge) ----

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; HideSymRing(); }

        // ---- curve-constraint visuals (equal-leg ring + 15° ticks + min-leg target) ----

        // The equal-leg symmetry ring around the bend: YELLOW where ending there is a buildable turn for
        // the design speed, RED otherwise (near-straight centre + too-tight), with 15° snap-tick marks.
        void BuildSymRing(ITerrainSurface field, Vector2 start, Vector2 bend)
        {
            EnsureSymRing();
            _symV.Clear(); _symIdx.Clear(); _symCol.Clear();
            CurveTickWorld.Clear(); CurveTickDeg.Clear();
            float radius = Vector2.Distance(start, bend);
            if (radius >= 0.5f)
            {
                int N = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * radius / 3f), 64, 512);
                Color32 ok = new Color32(255, 225, 50, 200), bad = new Color32(255, 55, 45, 200);
                Vector3 prev = RingPt(field, bend, radius, 0f);
                for (int i = 1; i <= N; i++)
                {
                    Vector3 w = RingPt(field, bend, radius, i / (float)N * Mathf.PI * 2f);
                    float aMid = (i - 0.5f) / N * Mathf.PI * 2f;
                    Vector2 endMid = new Vector2(bend.x + Mathf.Cos(aMid) * radius, bend.y + Mathf.Sin(aMid) * radius);
                    Color32 col = CurveBuildable(start, bend, endMid) ? ok : bad;
                    int s = _symV.Count; _symV.Add(prev); _symV.Add(w);
                    _symCol.Add(col); _symCol.Add(col); _symIdx.Add(s); _symIdx.Add(s + 1);
                    prev = w;
                }
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
            _symMesh.Clear(); _symMesh.SetVertices(_symV); _symMesh.SetColors(_symCol);
            _symMesh.SetIndices(_symIdx, MeshTopology.Lines, 0); _symMesh.RecalculateBounds();
            _symMr.enabled = true;
        }

        Vector3 RingPt(ITerrainSurface field, Vector2 c, float radius, float angle)
        {
            float x = c.x + Mathf.Cos(angle) * radius, z = c.y + Mathf.Sin(angle) * radius;
            return new Vector3(x, (field != null ? field.SampleHeight(x, z) : 0f) + Lift, z);
        }

        // While positioning the bend: a dashed guide leg (RED until the first leg is long enough for a
        // real turn at this speed) + a circle marking the min-distance target.
        void BuildMinLegGuide(ITerrainSurface field, Vector2 start, Vector2 cur)
        {
            EnsureSymRing();
            _symV.Clear(); _symIdx.Clear(); _symCol.Clear();
            CurveTickWorld.Clear(); CurveTickDeg.Clear();
            float minLeg = MinFirstLegForSpeed();
            float leg = Vector2.Distance(start, cur);
            Color32 ok = new Color32(255, 225, 50, 220), bad = new Color32(255, 55, 45, 220);
            SymDashedLine(field, start, cur, (minLeg > 1f && leg < minLeg) ? bad : ok);
            if (minLeg > 1f && (cur - start).sqrMagnitude > 1e-4f)
            {
                Vector2 dir = (cur - start).normalized;
                SymCircle(field, start + dir * minLeg, 5f, bad);
            }
            _symMesh.Clear(); _symMesh.SetVertices(_symV); _symMesh.SetColors(_symCol);
            _symMesh.SetIndices(_symIdx, MeshTopology.Lines, 0); _symMesh.RecalculateBounds();
            _symMr.enabled = true;
        }

        void SymDashedLine(ITerrainSurface field, Vector2 a, Vector2 b, Color32 col)
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

        void SymCircle(ITerrainSurface field, Vector2 c, float r, Color32 col)
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
            _symMesh = new Mesh { name = "RoadPlanSymRing" };
            _symMf.sharedMesh = _symMesh;
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            _symMat = sh != null ? new Material(sh) { name = "RoadPlanSymRingMat" } : MakeMat(PlanColor, "RoadPlanSymRingMat");
            _symMr.sharedMaterial = _symMat;
        }

        public void UpdatePreview(ITerrainSurface field, Vector3 cursor, bool show)
        {
            EnsurePreview();
            _pvMr.enabled = show;
            LastPreviewRadius = float.PositiveInfinity; LastPreviewTooTight = false;
            PreviewCurveActive = false;
            HideSymRing();   // default off each frame; BuildSymRing/BuildMinLegGuide re-enable it
            if (!show) return;
            _pv.Clear(); _pvIdx.Clear(); _pvBadIdx.Clear();

            const int N = 24; const float R = 1.0f;
            Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float ang = i / (float)N * Mathf.PI * 2f;
                Vector3 cur = Drape(field, new Vector2(cursor.x + Mathf.Cos(ang) * R, cursor.z + Mathf.Sin(ang) * R));
                if (i > 0) AddPv(prev, cur);
                prev = cur;
            }

            // Shift-curve in progress: the bend is armed, so draw the curve tail→corner→cursor (red when too
            // tight for the design speed), plus the two construction legs and a marker at the corner.
            if (_cornerPending && _chainTail >= 0 && _chainTail < Graph.Nodes.Count)
            {
                Vector2 tnode = Graph.Nodes[_chainTail];
                Vector2 endp = new Vector2(cursor.x, cursor.z);
                CurveControls(tnode, endp, _corner, out Vector2 c1, out Vector2 c2);
                LastPreviewRadius = MinCurveRadius(tnode, c1, c2, endp);
                LastPreviewTooTight = LimitCurveRadius && LastPreviewRadius < MinRadiusForSpeed;
                List<int> idx = LastPreviewTooTight ? _pvBadIdx : _pvIdx;

                // Expose geometry for the on-screen leg-length + deflection-angle labels.
                Vector2 inLeg = _corner - tnode, outLeg = endp - _corner;
                PreviewCurveActive = true; PreviewTail = tnode; PreviewCorner = _corner; PreviewEnd = endp;
                PreviewLegA = inLeg.magnitude; PreviewLegB = outLeg.magnitude;
                PreviewDeflectionDeg = (inLeg.sqrMagnitude > 1e-6f && outLeg.sqrMagnitude > 1e-6f)
                    ? Vector2.Angle(inLeg, outLeg) : 0f;

                int cn = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(tnode, endp) / 3f), 8, 256);
                Vector3 bPrev = Drape(field, tnode);
                for (int i = 1; i <= cn; i++)
                {
                    Vector3 cur = Drape(field, LineGraph.Bezier(tnode, c1, c2, endp, (float)i / cn));
                    AddPvTo(idx, bPrev, cur);
                    bPrev = cur;
                }
                DashLeg(field, tnode, _corner);     // construction legs (dashed, plan colour)
                DashLeg(field, _corner, endp);
                MarkCorner(field, _corner);
                BuildSymRing(field, tnode, _corner);   // equal-leg ring: yellow buildable / red too-tight + 15° ticks
                _pvMesh.Clear(); _pvMesh.subMeshCount = 2; _pvMesh.SetVertices(_pv);
                _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0); _pvMesh.SetIndices(_pvBadIdx, MeshTopology.Lines, 1);
                _pvMesh.RecalculateBounds();
                return;
            }

            // Arming a bend (Shift held, corner not yet dropped): show the min-leg target guide.
            if (CurveModifier && _chainTail >= 0 && _chainTail < Graph.Nodes.Count)
                BuildMinLegGuide(field, Graph.Nodes[_chainTail], new Vector2(cursor.x, cursor.z));

            // Straight placement: a dashed line tail→cursor + the collinear extension guide.
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count)
            {
                Vector2 tnode = Graph.Nodes[_chainTail];
                DashLeg(field, tnode, new Vector2(cursor.x, cursor.z));
            }
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count
                && IncomingDirection(new Vector2(cursor.x, cursor.z), out Vector2 gdir))
            {
                Vector2 o = Graph.Nodes[_chainTail];
                DashGuide(field, o, gdir, ExtensionGuideLength);                 // colinear continuation
                if (GuidedTurns && AllowHardCorner)                              // 90° corner guides (slow enough)
                {
                    float gl = Mathf.Min(ExtensionGuideLength, 60f);
                    DashGuide(field, o, new Vector2(-gdir.y, gdir.x), gl);
                    DashGuide(field, o, new Vector2(gdir.y, -gdir.x), gl);
                }
            }
            _pvMesh.Clear(); _pvMesh.subMeshCount = 2; _pvMesh.SetVertices(_pv);
            _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0); _pvMesh.SetIndices(_pvBadIdx, MeshTopology.Lines, 1);
            _pvMesh.RecalculateBounds();
        }

        // A dashed ray from origin `o` along unit `dir` for `len` metres (a heading guide).
        void DashGuide(ITerrainSurface field, Vector2 o, Vector2 dir, float len)
        {
            const int gn = 30;
            Vector3 gp = default;
            for (int i = 0; i <= gn; i++)
            {
                Vector3 cur = Drape(field, o + dir * ((float)i / gn * len));
                if (i > 0 && (i % 2 == 0)) AddPv(gp, cur);   // dashed
                gp = cur;
            }
        }

        // A dashed straight leg a→b into the normal (plan-colour) preview submesh.
        void DashLeg(ITerrainSurface field, Vector2 a, Vector2 b)
        {
            float len = Vector2.Distance(a, b);
            if (len < 1e-3f) return;
            int n = Mathf.Clamp(Mathf.CeilToInt(len / 3f), 1, 200);
            Vector3 prev = default;
            for (int i = 0; i <= n; i++)
            {
                Vector3 cur = Drape(field, Vector2.Lerp(a, b, (float)i / n));
                if (i > 0 && (i % 2 == 0)) AddPv(prev, cur);
                prev = cur;
            }
        }

        // A small draped ring marking the armed bend corner.
        void MarkCorner(ITerrainSurface field, Vector2 c)
        {
            const int N = 16; const float R = 1.2f;
            Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                Vector3 cur = Drape(field, new Vector2(c.x + Mathf.Cos(a) * R, c.y + Mathf.Sin(a) * R));
                if (i > 0) AddPv(prev, cur);
                prev = cur;
            }
        }

        void AddPv(Vector3 a, Vector3 b) { int s = _pv.Count; _pv.Add(a); _pv.Add(b); _pvIdx.Add(s); _pvIdx.Add(s + 1); }
        void AddPvTo(List<int> idx, Vector3 a, Vector3 b) { int s = _pv.Count; _pv.Add(a); _pv.Add(b); idx.Add(s); idx.Add(s + 1); }

        void EnsurePreview()
        {
            if (_pvMf != null) return;
            _pvGo = new GameObject(RootName + "_Preview") { hideFlags = HideFlags.DontSave };
            _pvMf = _pvGo.AddComponent<MeshFilter>();
            _pvMr = _pvGo.AddComponent<MeshRenderer>();
            _pvMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _pvMr.receiveShadows = false;
            _pvMesh = new Mesh { name = "RoadPlanPreviewMesh" };
            _pvMf.sharedMesh = _pvMesh;
            _pvBadMat = MakeMat(new Color(1f, 0.25f, 0.2f, 0.95f), "RoadPlanPreviewBadMat");   // too-tight curve
            _pvMr.sharedMaterials = new[] { MakeMat(PlanColor, "RoadPlanPreviewMat"), _pvBadMat };
        }

        // ---- save / load (the node/edge graph; geometry regenerated on load) ----

        public LineGraphSave CollectData() => new LineGraphSave { Nodes = new List<Vector2>(Graph.Nodes), Edges = new List<LineEdge>(Graph.Edges) };

        public void LoadState(LineGraphSave save)
        {
            _graph = new LineGraph();
            _chainTail = -1;
            if (save != null)
            {
                if (save.Nodes != null) _graph.Nodes.AddRange(save.Nodes);
                if (save.Edges != null) _graph.Edges.AddRange(save.Edges);
            }
        }
    }
}
