// Procedural rail track drawn on a node/edge graph (same click-to-chain UX as
// the linework layers). Each STRAIGHT edge emits two parallel rails + evenly
// spaced ties as low-poly flat-shaded boxes, conformed to the terrain at the
// edge endpoints (flat between for now — curves & ballast are later slices).
// Implements ITerrainLineLayer so TerrainDesigner draws/saves it like a fence.
//
// Mesh split into two children (Rails, Ties) so each gets its own material.
// Boxes use un-shared per-face verts -> RecalculateNormals gives hard facets,
// matching the terrain's low-poly look.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class RailTrackLayer : ITerrainLineLayer
    {
        [Tooltip("Display name + GameObject root name + hotkey label.")]
        public string Name = "Rail";
        [Tooltip("Distance between the two rail centres (track gauge), metres. " +
                 "Standard gauge is 1.435; scale to taste for the low-poly world.")]
        public float Gauge = 1.5f;
        [Tooltip("Rail cross-section width (metres).")]
        public float RailWidth = 0.1f;
        [Tooltip("Rail height standing above the ties (metres).")]
        public float RailHeight = 0.14f;
        [Tooltip("Metres between ties (sleepers) along the track.")]
        public float TieSpacing = 0.7f;
        [Tooltip("Tie length across the track — should exceed the gauge (metres).")]
        public float TieLength = 2.0f;
        [Tooltip("Tie thickness along the direction of travel (metres).")]
        public float TieThickness = 0.24f;
        [Tooltip("Tie height (metres).")]
        public float TieHeight = 0.12f;
        [Tooltip("Metres the track is raised above the terrain surface.")]
        public float VerticalOffset = 0.02f;
        [Tooltip("Conform the track to the terrain surface (sampled along the curve).")]
        public bool Conform = true;
        [Tooltip("Curve mode (held-Shift): how far the bezier controls sit from each " +
                 "node toward the guide corner (0 = sharp through corner, 1 = wide arc).")]
        [Range(0.1f, 0.95f)] public float CurveLever = 0.55f;
        [Tooltip("After the bend is placed, snap the end to the same leg length as the " +
                 "start->bend leg (a symmetric curve) when within this fraction of it. 0 = off.")]
        [Range(0f, 0.5f)] public float CurveSymmetrySnap = 0.1f;
        [Tooltip("The bend can't be placed until the first leg is long enough to give at " +
                 "least this much turn (deg) above/below the centreline for the design " +
                 "speed — the min-distance target. The guide stays red until then.")]
        public float MinCurveDeflectionDeg = 5f;
        [Tooltip("Design speed (km/h) for sections laid now. Sets the minimum curve " +
                 "radius — tighter curves are refused (preview turns red). Lower it " +
                 "to lay tighter curves.")]
        public float SpeedLimitKmh = 40f;
        [Tooltip("Max comfortable lateral acceleration in g (with cant). Higher = " +
                 "tighter curves allowed for a given speed. Real rail ~0.1; raise it " +
                 "for game-scaled tighter curves.")]
        [Range(0.05f, 0.5f)] public float MaxLateralG = 0.15f;

        // Minimum curve radius (m) for the current design speed: R = v^2 / (g*a),
        // v in m/s, a the lateral-accel limit. Quadratic in speed, as for real rail.
        public float MinRadiusForSpeed
        {
            get { float v = SpeedLimitKmh / 3.6f; return v * v / (9.81f * Mathf.Max(0.01f, MaxLateralG)); }
        }
        // Last previewed curve's tightest radius + whether it violated the minimum
        // (read by the on-screen hint; updated each frame while a corner is armed).
        [System.NonSerialized] public float LastPreviewRadius = float.PositiveInfinity;
        [System.NonSerialized] public bool LastPreviewTooTight;
        // While a curve corner is armed: the two leg lengths (A->bend, bend->B) in metres
        // and draped world anchors for the on-screen dimension labels.
        [System.NonSerialized] public bool CurveDimsValid;
        [System.NonSerialized] public float CurveLegA, CurveLegB;
        [System.NonSerialized] public Vector3 CurveLegAMid, CurveLegBMid;
        [Tooltip("Max grade (degrees) any section may have. The terrain is sampled " +
                 "every GradeSampleStep metres along an edge; the edge is buildable " +
                 "up to the first section that exceeds this, and is truncated there " +
                 "on click (the rest shows red). 5 deg ~ 8.7%.")]
        public float MaxGradeDeg = 5f;
        [Tooltip("Spacing (m) at which the terrain elevation is sampled along an edge " +
                 "to check the per-section grade — the 'every N metres' resolution.")]
        public float GradeSampleStep = 10f;
        [Tooltip("Override: ignore the grade limit and DON'T truncate — build the " +
                 "whole edge across whatever terrain it crosses (deep fills become " +
                 "bridges automatically). For 'the terrain's whacked, span it anyway'. " +
                 "Toggle with B in rail mode.")]
        public bool OverrideGrade = false;
        [System.NonSerialized] public float LastPreviewGradeDeg;        // steepest sampled section
        [System.NonSerialized] public float LastPreviewEndpointGradeDeg; // true grade A->B (the constant build grade)
        [System.NonSerialized] public bool LastPreviewTooSteep;
        // Last preview's buildable arc length vs total, and whether it would be
        // truncated — drives the on-screen hint and the red un-buildable tail.
        [System.NonSerialized] public float LastPreviewBuildableLen;
        [System.NonSerialized] public float LastPreviewTotalLen;
        [System.NonSerialized] public bool LastPreviewTruncated;
        public Color RailColor = new Color(0.28f, 0.28f, 0.30f);
        public Color TieColor = new Color(0.32f, 0.22f, 0.14f);
        [Tooltip("Height of the ballast bed the ties sit on (m). 0 = no ballast.")]
        public float BallastHeight = 0.25f;
        [Tooltip("Ballast top width beyond the tie ends, each side (m).")]
        public float BallastShoulder = 0.35f;
        [Tooltip("Ballast shoulder slope (horizontal run per unit height).")]
        public float BallastSlope = 1.4f;
        public Color BallastColor = new Color(0.40f, 0.37f, 0.34f);
        [Tooltip("Gravel shoulder slopes down at most this much; below it a vertical " +
                 "retaining wall drops to the ground (m).")]
        public float EmbankmentMaxDrop = 0.5f;
        [Tooltip("Fill height above which the section is carried on a BRIDGE (deck + " +
                 "piers) instead of a wall/embankment (m).")]
        public float BridgeAboveFill = 6f;
        [Tooltip("Bridge deck thickness (m).")]
        public float DeckDepth = 0.8f;
        [Tooltip("Spacing between bridge piers (m).")]
        public float PierSpacing = 12f;
        [Tooltip("Bridge pier width (m, square).")]
        public float PierWidth = 1.0f;
        [Tooltip("Concrete colour for retaining walls + bridge structure.")]
        public Color StructureColor = new Color(0.62f, 0.62f, 0.60f);
        [Tooltip("Optional bridge prefab placed in series over bridge spans instead " +
                 "of the procedural deck. Null = procedural deck.")]
        public GameObject BridgePrefab;
        [Tooltip("Metres between placed bridge-prefab instances (its span length).")]
        public float BridgeSpan = 10f;
        [Tooltip("Yaw added to each bridge instance (deg) if it doesn't face +Z.")]
        public float BridgeYawOffset = 0f;
        [Tooltip("Metres the bridge prefab is raised/lowered to line up under the rails.")]
        public float BridgeVerticalOffset = 0f;
        [Tooltip("Uniform scale for each bridge instance.")]
        public float BridgeScale = 1f;
        [Tooltip("Keep generating procedural piers (grounded to terrain) under the " +
                 "prefab deck. Turn off if the prefab carries its own piers.")]
        public bool ProceduralPiers = true;
        [Tooltip("Track buried deeper than (TunnelClearance + this rock cover) is bored " +
                 "as a TUNNEL — a concrete liner + portal frames (m).")]
        public float TunnelMinCover = 1.5f;
        [Tooltip("Tunnel bore height above the track bed (m).")]
        public float TunnelClearance = 6f;
        [Tooltip("Tunnel bore half-width beyond the tie ends (m).")]
        public float TunnelMargin = 0.6f;
        [Tooltip("Highlight (red) any track not connected to the main network, so a " +
                 "stranded stretch is obvious.")]
        public bool HighlightDisconnected = true;

        [Tooltip("Show a translucent puck at each rail node while editing rail, so nodes " +
                 "are visible and the one under the cursor highlights.")]
        public bool ShowNodePucks = true;
        [Tooltip("Radius (m) of the node pucks.")]
        public float NodePuckSize = 1.6f;
        [Tooltip("Height (m) of the 3D node pucks (the short cylinder's thickness).")]
        public float NodePuckHeight = 0.6f;
        [Tooltip("Node puck colour (alpha < 1 = translucent).")]
        public Color NodePuckColor = new Color(0.3f, 0.7f, 1f, 0.55f);
        [Tooltip("Colour of the puck under the cursor (the node you'd pick/insert by).")]
        public Color NodePuckHoverColor = new Color(1f, 0.85f, 0.3f, 0.85f);

        [Tooltip("Mark braking distances at speed drops in the network: where decel must " +
                 "begin on the faster line, and where the train is fully slowed on the " +
                 "slower line (curves at the slower speed can start). Shown while editing rail.")]
        public bool ShowBrakingMarkers = true;
        [Tooltip("Service deceleration (m/s^2) used for the braking distance " +
                 "d = (vFast^2 - vSlow^2) / (2a). Comfortable rail ~0.2-0.4.")]
        public float BrakingDecel = 0.3f;
        [Tooltip("Marker colour for where the train is fully slowed on the new line " +
                 "(slower-speed curves can begin past here).")]
        public Color BrakingOkColor = new Color(0.3f, 0.85f, 0.95f, 0.95f);

        [Tooltip("Outer (dashed) ring radius (m) of a decel snap target.")]
        public float DecelRingOuterRadius = 10f;
        [Tooltip("Inner (solid) snap-point radius (m) of a decel target.")]
        public float DecelRingInnerRadius = 1f;
        [Tooltip("Cursor snap radius (m) to a decel target.")]
        public float DecelSnapRadius = 15f;
        [Tooltip("Colour of the decel target rings.")]
        public Color DecelRingColor = new Color(1f, 0.2f, 0.2f, 1f);

        [Tooltip("Label each rail line with its speed limit every so often (to interrogate " +
                 "what speed an existing line is). Shown while editing rail.")]
        public bool ShowSpeedLabels = true;
        [Tooltip("Spacing (m) between speed-limit labels along a line.")]
        public float SpeedLabelSpacing = 500f;

        // A speed-limit label anchored along a line (km/h), for interrogating speeds.
        public struct SpeedLabel { public Vector3 World; public float Kmh; }
        [System.NonSerialized] public readonly List<SpeedLabel> SpeedLabels = new List<SpeedLabel>();
        // Live readout while drawing off an existing line (for the rail HUD).
        [System.NonSerialized] public bool PreviewHasIncoming; // tail sits on an existing edge
        [System.NonSerialized] public bool PreviewBrakeValid;  // ...and the new line is slower
        [System.NonSerialized] public float PreviewBrakeDist, PreviewBrakeVIn, PreviewBrakeVNew;
        [System.NonSerialized] public float PreviewBrakeReqRadius; // required radius at the worst braking-zone violation (0 = none)
        [System.NonSerialized] public float PreviewBrakeArcCovered; // arc already covered from the junction to the chain tail
        // Snap-target rings along the line ahead of the junction: half-decel + full decel.
        [System.NonSerialized] public bool PreviewDecelTargetsValid;
        [System.NonSerialized] public Vector2 PreviewHalfXZ, PreviewFullXZ;
        [System.NonSerialized] public Vector3 PreviewHalfWorld, PreviewFullWorld;
        [System.NonSerialized] public float PreviewHalfDist, PreviewFullDist;

        // ---- runtime (not serialized) ----
        LineGraph _graph = new LineGraph();
        GameObject _root, _railObj, _tieObj, _ballastObj, _structObj;
        Mesh _railMesh, _tieMesh, _ballastMesh, _structMesh;
        Material _railMat, _tieMat, _ballastMat, _structMat;
        int _chainTail = -1;          // current anchor node (start of next segment)
        // Straight by default; while CurveModifier (Shift) is held, a click drops a
        // guide corner and the next click ends a curve through it.
        [System.NonSerialized] public bool CurveModifier;
        bool _cornerPending;          // a guide corner has been placed, awaiting the end click
        Vector2 _corner;              // the pending guide corner
        // PAC angle-tick label anchors + their degree values (filled while placing the end).
        [System.NonSerialized] public readonly List<Vector3> CurveTickWorld = new List<Vector3>();
        [System.NonSerialized] public readonly List<int> CurveTickDeg = new List<int>();
        readonly List<Vector3> _rv = new List<Vector3>();
        readonly List<int> _rt = new List<int>();
        readonly List<Vector3> _tv = new List<Vector3>();
        readonly List<int> _tt = new List<int>();
        readonly List<Vector3> _bv = new List<Vector3>();
        readonly List<int> _bt = new List<int>();
        readonly List<Vector3> _sv = new List<Vector3>(); // walls + bridge (concrete)
        readonly List<int> _st = new List<int>();
        readonly List<GameObject> _bridgeInstances = new List<GameObject>();

        // Navigable graph (adjacency + A* routing) the train system rides on,
        // rebuilt from the track each Rebuild. Runtime-only.
        [System.NonSerialized] public RailNetwork Network;
        // Disconnected-track highlight overlay.
        GameObject _netGo;
        MeshFilter _netMf;
        MeshRenderer _netMr;
        Mesh _netMesh;
        Material _netMat;
        readonly List<Vector3> _nv = new List<Vector3>();
        readonly List<int> _ni = new List<int>();

        // Braking-distance marker overlay (per-vertex colour so both marker kinds share
        // one mesh). Built in Rebuild; the labels are drawn by the editor in OnGUI.
        GameObject _brkGo;
        MeshFilter _brkMf;
        MeshRenderer _brkMr;
        Mesh _brkMesh;
        Material _brkMat;
        readonly List<Vector3> _brkV = new List<Vector3>();
        readonly List<int> _brkIdx = new List<int>();
        readonly List<Color32> _brkCol = new List<Color32>();

        // Curve-inspection overlay ("design mode"): a translucent box hugging every built
        // curve, plus — on the hovered curve — its construction legs/angle. Submesh 0 =
        // triangles (fill), submesh 1 = lines (dashed outline + hover legs). One vertex-
        // colour material (alpha-blended). Metrics text is drawn by the editor in OnGUI.
        [System.NonSerialized] public bool ShowCurveInspect;
        [Tooltip("Width (m) of the dashed inspection box drawn around each segment in inspect mode.")]
        public float CurveInspectWidth = 20f;
        [Tooltip("Typical train length (m) used to report how many trains fit on a straight (queue space).")]
        public float TypicalTrainLengthM = 100f;
        public Color CurveInspectFill = new Color(0.72f, 0.82f, 0.35f, 0.22f);
        public Color CurveInspectOutline = new Color(1f, 0.95f, 0.20f, 0.85f);
        public Color CurveInspectDecelFill = new Color(1f, 0.55f, 0.12f, 0.85f); // amber hatch on decel zones
        readonly Dictionary<int, float> _nodeMaxSpeed = new Dictionary<int, float>(); // node -> fastest incident edge
        GameObject _inspGo;
        MeshFilter _inspMf;
        MeshRenderer _inspMr;
        Mesh _inspMesh;
        Material _inspMat;
        readonly List<Vector3> _inspV = new List<Vector3>();
        readonly List<Color32> _inspCol = new List<Color32>();
        readonly List<int> _inspTri = new List<int>();    // submesh 0: box fill
        readonly List<int> _inspLine = new List<int>();   // submesh 1: outline + hover legs
        readonly List<Vector2> _boxL = new List<Vector2>();
        readonly List<Vector2> _boxR = new List<Vector2>();
        int _inspSig = -1;   // last-built signature; rebuild only when it changes (not every frame)
        // Hovered-curve readout, consumed by the editor's OnGUI (null/false when none).
        [System.NonSerialized] public bool InspectHovered, InspectIsCurve;
        [System.NonSerialized] public Vector2 InspectMid, InspectCorner, InspectLegAMid, InspectLegBMid;
        [System.NonSerialized] public float InspectLegA, InspectLegB, InspectAngleDeg;
        [System.NonSerialized] public float InspectRadius, InspectMaxSpeed, InspectGradePct, InspectRated;
        [System.NonSerialized] public float InspectLength, InspectTrainCount;   // straight: queue space
        [System.NonSerialized] public bool InspectHasGrade, InspectHasCorner;
        [System.NonSerialized] public string InspectDecel;   // "100 → 60 km/h decel zone (189m)" or null

        // Translucent 3D vertex pucks (short lit cylinders) shown at each node while
        // editing; the one under the cursor highlights. Two submeshes (base / hover)
        // so base + hover share one mesh with two lit materials.
        GameObject _puckGo;
        MeshFilter _puckMf;
        MeshRenderer _puckMr;
        Mesh _puckMesh;
        Material _puckMat, _puckMatHover;
        readonly List<Vector3> _puckV = new List<Vector3>();
        readonly List<Vector3> _puckN = new List<Vector3>();
        readonly List<int> _puckT0 = new List<int>();   // submesh 0: base pucks
        readonly List<int> _puckT1 = new List<int>();   // submesh 1: hovered puck

        // Placement preview (ghost puck + dashed pending centreline + rail edges).
        GameObject _pvGo;
        MeshFilter _pvMf;
        MeshRenderer _pvMr;
        Mesh _pvMesh;
        Material _pvMat;       // buildable part (amber; red when curve too tight)
        Material _pvMatRed;    // un-buildable / over-grade tail (always red)
        readonly List<Vector3> _pvVerts = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();      // submesh 0 (buildable)
        readonly List<int> _pvIdxRed = new List<int>();   // submesh 1 (over-grade tail)

        public LineGraph Graph => _graph ??= new LineGraph();
        string ITerrainLineLayer.LayerName => Name;
        string RootName => "TerrainRail_" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- editing ----

        // One click. Straight by default: chains a straight segment from the anchor.
        // Hold Shift (CurveModifier): the click drops a guide corner; the following
        // click ends a bezier curve through it. The anchor carries over either way,
        // so straight and curved segments mix freely in one chain.
        public void AddNode(TerrainField field, Vector3 hit)
        {
            Vector2 p = new Vector2(hit.x, hit.z);

            // First click of a chain: resume from an existing node (arm a chain off it),
            // or — clicking directly ON an existing edge — insert a node there and STOP
            // (chop the segment; the new puck can then be clicked to branch). Else drop
            // a fresh anchor to start a new line.
            if (_chainTail < 0)
            {
                int near = Graph.NearestNode(p, NodePickRadius);
                if (near >= 0) { _chainTail = near; _cornerPending = false; return; }
                if (Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _))
                {
                    Graph.SplitEdge(ei, tt);   // chop the edge — insert a node, don't arm a chain
                    _cornerPending = false;
                    Rebuild(field);            // split changed the geometry
                    return;
                }
                _chainTail = Graph.AddNode(p);
                _cornerPending = false;
                return;
            }

            // A corner is armed: this click is the endpoint -> commit the curve,
            // unless it would be tighter than the speed's minimum radius (refused;
            // the corner stays armed so you can pick a wider endpoint). The curve
            // is truncated at the first section the terrain takes over-grade
            // (unless override is on), via a de Casteljau split.
            if (_cornerPending)
            {
                Vector2 start = Graph.Nodes[_chainTail];
                // No deflection floor: a near-straight pick just builds a gentle (∞-radius)
                // bezier — the end was constrained to the buildable arc, so trust it.
                CurveControls(start, p, _corner, out Vector2 c1, out Vector2 c2);
                if (MinCurveRadius(start, c1, c2, p) < MinRadiusForSpeed) return; // too tight
                // In a braking zone (drawn off a faster line), the curve must also be
                // gentle enough for the still-high speed there — refuse if it isn't.
                if (PreviewBrakeValid
                    && WorstBrakingRadiusViolation(start, c1, c2, p, PreviewBrakeVIn, SpeedLimitKmh, PreviewBrakeArcCovered) > 0f) return;
                Vector2 endPt = p, cc1 = c1, cc2 = c2;
                if (!OverrideGrade && ScanBuildable(field, start, c1, c2, p,
                        out float bl, out float bt, out _, out _) && bt < 0.999f)
                {
                    if (bl < 1e-2f) return;  // first section already over-grade — refuse
                    SubdivideFirst(start, c1, c2, p, bt, out cc1, out cc2, out endPt);
                }
                int end = NearestOrNew(endPt);
                Graph.AddEdge(_chainTail, end);
                LineEdge ce = FindEdge(_chainTail, end);
                if (ce != null) { ce.HasCurve = true; ce.ControlA = cc1; ce.ControlB = cc2; ce.SpeedLimit = SpeedLimitKmh; }
                _chainTail = end;
                _cornerPending = false;
                Rebuild(field);
                return;
            }

            // Anchor present, no corner armed:
            if (CurveModifier)
            {
                _corner = p;            // Shift held -> this click arms the guide corner
                _cornerPending = true;
                return;
            }
            // plain click -> straight segment, truncated at the first section the
            // terrain takes over-grade (unless override is on).
            Vector2 s = Graph.Nodes[_chainTail];
            Vector2 dchord = p - s;
            Vector2 sc1 = s + dchord / 3f, sc2 = s + dchord * (2f / 3f);
            Vector2 endS = p;
            if (!OverrideGrade && ScanBuildable(field, s, sc1, sc2, p,
                    out float sbl, out float sbt, out _, out _) && sbt < 0.999f)
            {
                if (sbl < 1e-2f) return;  // first section already over-grade — refuse
                endS = LineGraph.Bezier(s, sc1, sc2, p, sbt); // == lerp(s,p,sbt) for a straight
            }
            int idx = NearestOrNew(endS);           // join to an existing node if clicked on one
            Graph.AddEdge(_chainTail, idx);
            TagEdge(_chainTail, idx);
            _chainTail = idx;
            Rebuild(field);
        }

        const float NodePickRadius = 5f; // click within this of a node to pick it up / join

        [Tooltip("Snap radius (m) for connecting to EXISTING track. A click within " +
                 "this of a node or rail edge is pulled exactly onto it, so it reliably " +
                 "joins the network — this overrides grid snap. 0 = off (grid snap only).")]
        public float TrackSnapRadius = 8f;

        // Pull a candidate XZ onto the nearest existing node or rail edge within
        // TrackSnapRadius so clicks reliably connect to the network. Excludes the
        // active chain anchor (a segment can't connect to its own start). Beats grid
        // snap. Returns false (point unchanged) if nothing is in range.
        public bool TrySnapToTrack(Vector2 p, out Vector2 snapped)
        {
            snapped = p;
            float r = Mathf.Max(0f, TrackSnapRadius);
            if (r <= 0f || Graph == null) return false;
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

        // Like TrySnapToTrack (node first, then edge, within TrackSnapRadius) but with
        // NO chain-anchor exclusion — for callers that aren't building a chain (e.g.
        // the terrain slope tool snapping its endpoints onto rail ends/edges).
        public bool TrySnapToTrackPoint(Vector2 p, out Vector2 snapped)
        {
            snapped = p;
            float r = Mathf.Max(0f, TrackSnapRadius);
            if (r <= 0f || Graph == null) return false;
            int n = Graph.NearestNode(p, r);
            if (n >= 0) { snapped = Graph.Nodes[n]; return true; }
            if (Graph.NearestPointOnEdge(p, r, out _, out _, out Vector2 pt)) { snapped = pt; return true; }
            return false;
        }

        // The node to end a segment on: an existing nearby node, a new junction
        // split into an existing track (so a line can merge mid-span), or a new node.
        int NearestOrNew(Vector2 p)
        {
            int n = Graph.NearestNode(p, NodePickRadius);
            if (n >= 0 && n != _chainTail) return n;
            if (n < 0 && Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _))
                return Graph.SplitEdge(ei, tt);   // merge into existing track mid-span
            return Graph.AddNode(p);
        }

        // Bezier controls for a curve from a to b bending toward corner.
        void CurveControls(Vector2 a, Vector2 b, Vector2 corner, out Vector2 c1, out Vector2 c2)
        {
            float f = Mathf.Clamp(CurveLever, 0.1f, 0.95f);
            c1 = Vector2.Lerp(a, corner, f);
            c2 = Vector2.Lerp(b, corner, f);
        }

        // Connect a..b and tag it with bezier controls leaning toward the corner.
        void AddCurvedEdge(int a, int b, Vector2 corner)
        {
            Graph.AddEdge(a, b);
            LineEdge e = FindEdge(a, b);
            if (e == null) return;
            CurveControls(Graph.Nodes[e.A], Graph.Nodes[e.B], corner, out Vector2 c1, out Vector2 c2);
            e.HasCurve = true;
            e.ControlA = c1; e.ControlB = c2;
            e.SpeedLimit = SpeedLimitKmh;
        }

        void TagEdge(int a, int b) { var e = FindEdge(a, b); if (e != null) e.SpeedLimit = SpeedLimitKmh; }

        LineEdge FindEdge(int a, int b)
        {
            foreach (LineEdge e in Graph.Edges)
                if ((e.A == a && e.B == b) || (e.A == b && e.B == a)) return e;
            return null;
        }

        // Nearest node to an XZ point within the node-pick radius (the same radius the
        // pucks highlight at), or -1. For the editor's node-anchored actions.
        public int NearestNodeForPick(Vector2 p) => Graph != null ? Graph.NearestNode(p, NodePickRadius) : -1;

        // Sample the rail centreline (XZ polyline) between two nodes, following edges via
        // BFS (shortest hop count). False if the nodes aren't connected. Used by the
        // editor's node-to-node auto-slope.
        public bool TryCenterlinePath(int a, int b, out List<Vector2> path)
        {
            path = null;
            if (Graph == null || a < 0 || b < 0 || a == b
                || a >= Graph.Nodes.Count || b >= Graph.Nodes.Count) return false;
            List<int> nodes = BfsNodePath(a, b);
            if (nodes == null || nodes.Count < 2) return false;
            path = new List<Vector2>();
            for (int k = 0; k < nodes.Count - 1; k++)
            {
                LineEdge e = FindEdge(nodes[k], nodes[k + 1]);
                if (e == null) { path = null; return false; }
                Vector2 q0 = Graph.Nodes[e.A], q3 = Graph.Nodes[e.B], q1, q2;
                if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
                else { Vector2 d = q3 - q0; q1 = q0 + d / 3f; q2 = q0 + d * (2f / 3f); }
                bool fwd = e.A == nodes[k];
                int steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(q0, q3) / 2f), 1, 4000);
                for (int s = (k == 0 ? 0 : 1); s <= steps; s++)
                {
                    float t = s / (float)steps;
                    path.Add(LineGraph.Bezier(q0, q1, q2, q3, fwd ? t : 1f - t));
                }
            }
            return path.Count >= 2;
        }

        // Breadth-first node path (fewest hops) between two nodes over the edge graph.
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

        // Speed-aware tightness check for a curve drawn off a faster line. Walks the
        // curve from the junction; at each sample the braking-profile speed
        // v(s) = sqrt(max(vNew^2, vIn^2 - 2*a*s)) sets the required min radius
        // v(s)^2/(g*latG). Returns the largest required radius among violating samples
        // (the worst point), or 0 if the curve is gentle enough the whole way.
        float WorstBrakingRadiusViolation(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float vInKmh, float vNewKmh, float arcOffset)
        {
            const int N = 32;
            float a = Mathf.Max(0.05f, BrakingDecel);
            float vIn = vInKmh / 3.6f, vNew2 = (vNewKmh / 3.6f) * (vNewKmh / 3.6f);
            float gLat = 9.81f * Mathf.Max(0.01f, MaxLateralG);
            float worstReq = 0f;
            Vector2 a0 = LineGraph.Bezier(p0, p1, p2, p3, 0f);
            Vector2 b0 = LineGraph.Bezier(p0, p1, p2, p3, 1f / N);
            float arc = Vector2.Distance(a0, b0);
            for (int i = 2; i <= N; i++)
            {
                Vector2 c0 = LineGraph.Bezier(p0, p1, p2, p3, i / (float)N);
                float ab = Vector2.Distance(a0, b0), bc = Vector2.Distance(b0, c0), ca = Vector2.Distance(c0, a0);
                float area2 = Mathf.Abs((b0.x - a0.x) * (c0.y - a0.y) - (b0.y - a0.y) * (c0.x - a0.x));
                float r = area2 > 1e-6f ? ab * bc * ca / (2f * area2) : float.PositiveInfinity;
                // arcOffset = how far down the braking zone the segment already starts.
                float v2 = Mathf.Max(vNew2, vIn * vIn - 2f * a * (arcOffset + arc));
                float req = v2 / gLat;                                  // required radius here
                if (r < req && req > worstReq) worstReq = req;
                arc += bc; a0 = b0; b0 = c0;
            }
            return worstReq;
        }

        // Tightest radius (m) along a cubic bezier, via 3-point circumradius over
        // samples. Returns +inf for a straight line.
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

        public void EndChain()
        {
            // Cancelling a just-started track leaves the tail node with no edges — drop
            // it so an orphan node (and its puck) isn't left behind.
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count && !NodeHasEdge(_chainTail))
                Graph.RemoveNode(_chainTail);
            _chainTail = -1; _cornerPending = false;
        }

        bool NodeHasEdge(int n)
        {
            foreach (LineEdge e in Graph.Edges) if (e.A == n || e.B == n) return true;
            return false;
        }

        // XZ of the current chain tail (start of the next segment), for label placement.
        public bool TryGetTailXZ(out Vector2 pos)
        {
            pos = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            pos = Graph.Nodes[_chainTail];
            return true;
        }

        public void ClearAll(TerrainField field)
        {
            Graph.Clear();
            _chainTail = -1;
            _cornerPending = false;
            Rebuild(field);
        }

        public void RemoveLastNode(TerrainField field)
        {
            // Backspace first cancels an armed (un-committed) guide corner.
            if (_cornerPending) { _cornerPending = false; return; }
            int last = Graph.Nodes.Count - 1;
            if (last < 0) return;
            Graph.Edges.RemoveAll(e => e.A == last || e.B == last);
            Graph.Nodes.RemoveAt(last);
            if (_chainTail >= Graph.Nodes.Count) _chainTail = -1;
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

        // ---- rendering ----

        void EnsureObjects()
        {
            if (_root == null)
            {
                GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == RootName) DestroySafe(all[i]);
                _root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            }
            if (_railMat == null) _railMat = NetworkDesigner.PipelineMaterials.CreateLit(RailColor, 0.6f, "RailMat");
            if (_tieMat == null) _tieMat = NetworkDesigner.PipelineMaterials.CreateLitMatte(TieColor, "TieMat");
            if (_ballastMat == null) _ballastMat = NetworkDesigner.PipelineMaterials.CreateLitMatte(BallastColor, "BallastMat");
            if (_structMat == null) _structMat = NetworkDesigner.PipelineMaterials.CreateLitMatte(StructureColor, "StructMat");
            // Ballast/structure first so ties/rails draw over them.
            _ballastObj = EnsureChild(_ballastObj, "Ballast", ref _ballastMesh, _ballastMat);
            _structObj = EnsureChild(_structObj, "Structure", ref _structMesh, _structMat);
            _railObj = EnsureChild(_railObj, "Rails", ref _railMesh, _railMat);
            _tieObj = EnsureChild(_tieObj, "Ties", ref _tieMesh, _tieMat);
        }

        GameObject EnsureChild(GameObject go, string childName, ref Mesh mesh, Material mat)
        {
            if (go != null) return go;
            go = new GameObject(childName) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            mesh = new Mesh { name = childName + "Mesh" };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        public void Rebuild(TerrainField field)
        {
            EnsureObjects();
            _rv.Clear(); _rt.Clear(); _tv.Clear(); _tt.Clear();
            _bv.Clear(); _bt.Clear(); _sv.Clear(); _st.Clear();
            for (int i = 0; i < _bridgeInstances.Count; i++) DestroySafe(_bridgeInstances[i]);
            _bridgeInstances.Clear();
            foreach (LineEdge e in Graph.Edges)
                BuildEdge(field, e);
            Apply(_railMesh, _rv, _rt);
            Apply(_tieMesh, _tv, _tt);
            Apply(_ballastMesh, _bv, _bt);
            Apply(_structMesh, _sv, _st);
            if (_railMat != null) _railMat.color = RailColor; // live colour tweaks
            if (_tieMat != null) _tieMat.color = TieColor;
            if (_ballastMat != null) _ballastMat.color = BallastColor;
            if (_structMat != null) _structMat.color = StructureColor;

            // Refresh the navigable graph + the disconnected-track highlight.
            (Network ??= new RailNetwork()).Build(Graph);
            BuildNetworkOverlay(field);
            BuildSpeedLabels(field);
        }

        // Place a speed-limit label every SpeedLabelSpacing metres along each edge (at
        // least one per edge), for interrogating an existing line's speeds. Display is
        // gated by the editor; the list is always populated so toggling is instant.
        void BuildSpeedLabels(TerrainField field)
        {
            SpeedLabels.Clear();
            if (field == null || Graph == null || Graph.Edges.Count == 0) return;
            float spacing = Mathf.Max(50f, SpeedLabelSpacing);
            foreach (LineEdge e in Graph.Edges)
            {
                OrientedBezier(e, e.A, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3);
                float L = EdgeArcLength(q0, q1, q2, q3);
                if (L < 1e-2f) continue;
                int n = Mathf.Max(1, Mathf.RoundToInt(L / spacing));
                float spd = EdgeSpeed(e);
                for (int i = 0; i < n; i++)
                {
                    float arc = (i + 0.5f) / n * L;
                    if (!WalkBezier(q0, q1, q2, q3, arc, out Vector2 p, out _)) continue;
                    float y = field.SampleHeight(p.x, p.y) + 1.6f;
                    SpeedLabels.Add(new SpeedLabel { World = new Vector3(p.x, y, p.y), Kmh = spd });
                }
            }
        }

        static float EdgeArcLength(Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3)
        {
            const int N = 24;
            float L = 0f; Vector2 prev = q0;
            for (int i = 1; i <= N; i++)
            {
                Vector2 cp = LineGraph.Bezier(q0, q1, q2, q3, i / (float)N);
                L += Vector2.Distance(prev, cp); prev = cp;
            }
            return L;
        }

        // Design speed of an edge (km/h): its laid SpeedLimit, or the layer default.
        float EdgeSpeed(LineEdge e) => e.SpeedLimit > 0f ? e.SpeedLimit : SpeedLimitKmh;

        // Braking distance (m) to go from vFast to vSlow (km/h) at BrakingDecel.
        float BrakeDist(float vFastKmh, float vSlowKmh)
        {
            float a = Mathf.Max(0.05f, BrakingDecel);
            float vf = vFastKmh / 3.6f, vs = vSlowKmh / 3.6f;
            return Mathf.Max(0f, (vf * vf - vs * vs) / (2f * a));
        }

        // Bezier control points of edge e oriented so param 0 = fromNode, 1 = the far end.
        void OrientedBezier(LineEdge e, int fromNode, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3)
        {
            Vector2 a = Graph.Nodes[e.A], b = Graph.Nodes[e.B], c1, c2;
            if (e.HasCurve) { c1 = e.ControlA; c2 = e.ControlB; }
            else { Vector2 d = b - a; c1 = a + d / 3f; c2 = a + d * (2f / 3f); }
            if (e.A == fromNode) { q0 = a; q1 = c1; q2 = c2; q3 = b; }
            else { q0 = b; q1 = c2; q2 = c1; q3 = a; }
        }

        // Per-frame rebuild of the braking overlay: the LIVE decel snap-targets off the
        // segment currently being drawn (cursor). Driven each frame by the editor while
        // rail is the active line layer.
        public void RebuildBraking(TerrainField field, Vector3 cursor, bool show)
        {
            EnsureBrakingOverlay();
            _brkMr.enabled = show;   // visible while editing rail (carries both overlays)
            _brkV.Clear(); _brkIdx.Clear(); _brkCol.Clear();
            PreviewBrakeValid = false; PreviewHasIncoming = false; PreviewDecelTargetsValid = false;
            if (show && field != null && Graph != null && Graph.Edges.Count > 0)
            {
                // Live decel snap-targets (the committed "≤X km/h" markers were removed).
                if (ShowBrakingMarkers) AppendPreviewBraking(field, cursor);
                // The speed-coloured equal-leg symmetry ring while placing a curve end.
                AppendSymmetryRing(field);
            }
            _brkMesh.Clear();
            _brkMesh.SetVertices(_brkV);
            _brkMesh.SetColors(_brkCol);
            _brkMesh.SetIndices(_brkIdx, MeshTopology.Lines, 0);
            _brkMesh.RecalculateBounds();
        }

        // Live braking preview for the segment being drawn. Walks back along the new
        // (slower) line to the speed-drop junction to get the arc already covered, then
        // projects two snap-target rings by ARC LENGTH along the current segment: the
        // full decel point (vNew reached) and the half-decel point. Both are cursor snap
        // targets; neither is enforced.
        void AppendPreviewBraking(TerrainField field, Vector3 cursorW)
        {
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return;
            float vNew = SpeedLimitKmh;
            if (!FindBrakingContext(_chainTail, out float arcCovered, out float vIn))
            {
                // Not in a braking zone — but report the incoming speed for the HUD note.
                float vMax = 0f; bool any = false;
                for (int ei = 0; ei < Graph.Edges.Count; ei++)
                {
                    LineEdge e = Graph.Edges[ei];
                    if (e.A != _chainTail && e.B != _chainTail) continue;
                    any = true; vMax = Mathf.Max(vMax, EdgeSpeed(e));
                }
                if (any) { PreviewHasIncoming = true; PreviewBrakeVIn = vMax; PreviewBrakeVNew = vNew; }
                return;
            }
            PreviewHasIncoming = true; PreviewBrakeVIn = vIn; PreviewBrakeVNew = vNew;
            float d = BrakeDist(vIn, vNew);
            float remainingFull = d - arcCovered;
            if (d < 1f || remainingFull <= 1f) return;   // no drop / already fully slowed
            PreviewBrakeValid = true; PreviewBrakeDist = d; PreviewBrakeArcCovered = arcCovered;

            // Current segment's prospective path (curved when a corner is armed).
            Vector2 start = Graph.Nodes[_chainTail];
            Vector2 cur = new Vector2(cursorW.x, cursorW.z);
            Vector2 q0 = start, q1, q2, q3 = cur;
            if (_cornerPending) CurveControls(start, cur, _corner, out q1, out q2);
            else { Vector2 dd = cur - start; q1 = start + dd / 3f; q2 = start + dd * (2f / 3f); }

            PreviewFullDist = d;
            PreviewFullXZ = PointAlongOrProject(q0, q1, q2, q3, remainingFull);
            PreviewHalfDist = d * 0.5f;
            float remainingHalf = d * 0.5f - arcCovered;
            PreviewHalfXZ = remainingHalf > 1f ? PointAlongOrProject(q0, q1, q2, q3, remainingHalf) : start;

            PreviewHalfWorld = new Vector3(PreviewHalfXZ.x, (field != null ? field.SampleHeight(PreviewHalfXZ.x, PreviewHalfXZ.y) : 0f) + 1.8f, PreviewHalfXZ.y);
            PreviewFullWorld = new Vector3(PreviewFullXZ.x, (field != null ? field.SampleHeight(PreviewFullXZ.x, PreviewFullXZ.y) : 0f) + 1.8f, PreviewFullXZ.y);
            PreviewDecelTargetsValid = true;
            EmitBrakeRing(field, PreviewHalfXZ);
            EmitBrakeRing(field, PreviewFullXZ);
        }

        // Walk back from `tail` along the new (slower) line to the speed-drop junction —
        // a node touching an edge faster than the line speed. Returns the arc covered from
        // the junction to `tail` and the junction's faster speed. False if none found.
        bool FindBrakingContext(int tail, out float arc, out float vIn)
        {
            arc = 0f; vIn = 0f;
            float vLine = SpeedLimitKmh;
            int cur = tail, fromEdge = -1;
            for (int guard = 0; guard < 400; guard++)
            {
                int back = -1; float vf = 0f;
                for (int ei = 0; ei < Graph.Edges.Count; ei++)
                {
                    if (ei == fromEdge) continue;
                    LineEdge e = Graph.Edges[ei];
                    if (e.A != cur && e.B != cur) continue;
                    float v = EdgeSpeed(e);
                    if (v > vf) vf = v;
                    if (back < 0) back = ei;
                }
                if (vf > vLine + 1f) { vIn = vf; return true; }   // junction reached
                if (back < 0) return false;                       // dead end, no junction
                OrientedBezier(Graph.Edges[back], cur, out Vector2 b0, out Vector2 b1, out Vector2 b2, out Vector2 b3);
                arc += EdgeArcLength(b0, b1, b2, b3);
                cur = Graph.Edges[back].A == cur ? Graph.Edges[back].B : Graph.Edges[back].A;
                fromEdge = back;
            }
            return false;
        }

        // Point at arc-length `dist` along a bezier, or — if the curve is shorter than
        // dist — projected past its end along the end tangent.
        Vector2 PointAlongOrProject(Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3, float dist)
        {
            if (WalkBezier(q0, q1, q2, q3, dist, out Vector2 pt, out _)) return pt;
            float lc = EdgeArcLength(q0, q1, q2, q3);
            Vector2 endTan = LineGraph.BezierTangent(q0, q1, q2, q3, 1f);
            if (endTan.sqrMagnitude < 1e-6f) endTan = q3 - q0;
            endTan = endTan.sqrMagnitude > 1e-6f ? endTan.normalized : Vector2.right;
            return q3 + endTan * Mathf.Max(0f, dist - lc);
        }

        // The equal-leg symmetry ring around the bend, drawn into the braking overlay
        // (per-vertex colour) as two SOLID translucent arcs: YELLOW where ending the curve
        // there stays within the min radius for the speed, RED where it would be too tight.
        void AppendSymmetryRing(TerrainField field)
        {
            CurveTickWorld.Clear(); CurveTickDeg.Clear();
            if (!_cornerPending || CurveSymmetrySnap <= 0f
                || _chainTail < 0 || _chainTail >= Graph.Nodes.Count) return;
            Vector2 start = Graph.Nodes[_chainTail], bend = _corner;
            float radius = Vector2.Distance(start, bend);
            if (radius < 0.5f) return;
            // Fine segments so the yellow→red boundary lands crisply (~0.7° even on a big
            // ring) and matches where the clamp actually stops the end.
            int N = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * radius / 3f), 64, 512);
            Color32 ok = new Color32(255, 225, 50, 200), bad = new Color32(255, 55, 45, 200);
            Vector3 prev = RingPt(field, bend, radius, 0f);
            for (int i = 1; i <= N; i++)
            {
                Vector3 w = RingPt(field, bend, radius, i / (float)N * Mathf.PI * 2f);
                // Colour each segment by its MIDPOINT so the boundary lands within ~half a
                // segment instead of being pushed a whole segment early.
                float aMid = (i - 0.5f) / N * Mathf.PI * 2f;
                Vector2 endMid = new Vector2(bend.x + Mathf.Cos(aMid) * radius, bend.y + Mathf.Sin(aMid) * radius);
                int s = _brkV.Count; _brkV.Add(prev); _brkV.Add(w);
                Color32 col = CurveBuildable(start, bend, endMid) ? ok : bad;
                _brkCol.Add(col); _brkCol.Add(col); _brkIdx.Add(s); _brkIdx.Add(s + 1);
                prev = w;
            }
            // Standard-angle tick marks (the snap targets) at ±15/30/45/60/75/90° off
            // straight-ahead, only where buildable (within thMax) on that side.
            Vector2 a0 = (bend - start).sqrMagnitude > 1e-6f ? (bend - start).normalized : Vector2.right;
            float a0A = Mathf.Atan2(a0.y, a0.x) * Mathf.Rad2Deg;
            float tl = Mathf.Clamp(radius * 0.02f, 2f, 25f);   // half-length, straddling the ring
            Color32 tickCol = ok;                              // same yellow as the PAC arc
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                float thMax = MaxBuildableDeflection(start, bend, radius, a0A, sgn);
                for (int ti = 0; ti < DeflectionTicksDeg.Length && DeflectionTicksDeg[ti] <= thMax + 0.5f; ti++)
                {
                    float ang = (a0A + sgn * DeflectionTicksDeg[ti]) * Mathf.Deg2Rad;
                    Vector3 wi = RingPt(field, bend, radius - tl, ang), wo = RingPt(field, bend, radius + tl, ang);
                    int ts = _brkV.Count; _brkV.Add(wi); _brkV.Add(wo);
                    _brkCol.Add(tickCol); _brkCol.Add(tickCol); _brkIdx.Add(ts); _brkIdx.Add(ts + 1);
                    CurveTickWorld.Add(RingPt(field, bend, radius + tl + Mathf.Max(2f, tl), ang));
                    CurveTickDeg.Add((int)DeflectionTicksDeg[ti]);
                }
            }
        }

        static readonly float[] DeflectionTicksDeg = { 15f, 30f, 45f, 60f, 75f, 90f };

        // Snap a deflection (deg) to the nearest standard tick within tolerance, but only to
        // ticks that are actually buildable (≤ thMax).
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

        Vector3 RingPt(TerrainField field, Vector2 c, float radius, float angle)
        {
            float x = c.x + Mathf.Cos(angle) * radius, z = c.y + Mathf.Sin(angle) * radius;
            return new Vector3(x, (field != null ? field.SampleHeight(x, z) : 0f) + 0.2f, z);
        }

        // A decel snap target: a dashed outer ring + a small solid inner snap point.
        void EmitBrakeRing(TerrainField field, Vector2 c)
        {
            Color32 col = DecelRingColor;
            EmitCircle(field, c, Mathf.Max(0.5f, DecelRingOuterRadius), col, true);   // dashed outer
            EmitCircle(field, c, Mathf.Max(0.1f, DecelRingInnerRadius), col, false);  // solid inner
        }

        void EmitCircle(TerrainField field, Vector2 c, float r, Color32 col, bool dashed)
        {
            const int N = 32;
            Vector3 prev = default; bool havePrev = false;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                float x = c.x + Mathf.Cos(a) * r, z = c.y + Mathf.Sin(a) * r;
                Vector3 w = new Vector3(x, (field != null ? field.SampleHeight(x, z) : 0f) + 0.25f, z);
                if (havePrev && (!dashed || (i % 2 == 0)))
                {
                    int s = _brkV.Count; _brkV.Add(prev); _brkV.Add(w);
                    _brkCol.Add(col); _brkCol.Add(col); _brkIdx.Add(s); _brkIdx.Add(s + 1);
                }
                prev = w; havePrev = true;
            }
        }

        // Snap a cursor onto the nearest decel ring (full or half) within range.
        public bool TrySnapToDecelTarget(Vector2 p, out Vector2 snapped)
        {
            snapped = p;
            if (!PreviewDecelTargetsValid) return false;
            float r = Mathf.Max(0.5f, DecelSnapRadius); float r2 = r * r;
            float dFull = (PreviewFullXZ - p).sqrMagnitude, dHalf = (PreviewHalfXZ - p).sqrMagnitude;
            if (dFull <= r2 && dFull <= dHalf) { snapped = PreviewFullXZ; return true; }
            if (dHalf <= r2) { snapped = PreviewHalfXZ; return true; }
            return false;
        }

        // Walk `dist` metres along a single bezier from q0. False if the curve is shorter
        // than dist (so callers can skip an out-of-range mark).
        bool WalkBezier(Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3, float dist, out Vector2 point, out Vector2 heading)
        {
            point = q3; heading = q3 - q0;
            const int N = 32;
            Vector2 prev = q0; float remaining = dist;
            for (int i = 1; i <= N; i++)
            {
                Vector2 cp = LineGraph.Bezier(q0, q1, q2, q3, i / (float)N);
                float seg = Vector2.Distance(prev, cp);
                if (seg >= remaining)
                {
                    point = Vector2.Lerp(prev, cp, seg > 1e-4f ? remaining / seg : 0f);
                    heading = cp - prev;
                    return true;
                }
                remaining -= seg; prev = cp;
            }
            return false; // curve too short for `dist`
        }


        void EnsureBrakingOverlay()
        {
            if (_brkMf != null) return;
            _brkGo = new GameObject(RootName + "_Braking") { hideFlags = HideFlags.DontSave };
            _brkGo.transform.SetParent(_root != null ? _root.transform : null, worldPositionStays: false);
            _brkMf = _brkGo.AddComponent<MeshFilter>();
            _brkMr = _brkGo.AddComponent<MeshRenderer>();
            _brkMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _brkMr.receiveShadows = false;
            _brkMesh = new Mesh { name = "RailBrakingMesh" };
            _brkMf.sharedMesh = _brkMesh;
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            _brkMat = sh != null ? new Material(sh) { name = "RailBrakingMat" }
                                 : NetworkDesigner.PipelineMaterials.CreateUnlitColor(BrakingOkColor, "RailBrakingMat");
            _brkMr.sharedMaterial = _brkMat;
        }

        // ---- curve inspection overlay ("design mode") ----

        void EnsureCurveInspect()
        {
            if (_inspMf != null) return;
            _inspGo = new GameObject(RootName + "_Inspect") { hideFlags = HideFlags.DontSave };
            _inspGo.transform.SetParent(_root != null ? _root.transform : null, worldPositionStays: false);
            _inspMf = _inspGo.AddComponent<MeshFilter>();
            _inspMr = _inspGo.AddComponent<MeshRenderer>();
            _inspMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _inspMr.receiveShadows = false;
            _inspMesh = new Mesh { name = "RailInspectMesh" };
            _inspMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32; // whole network can exceed 65k verts
            _inspMf.sharedMesh = _inspMesh;
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            _inspMat = sh != null ? new Material(sh) { name = "RailInspectMat" }
                                  : NetworkDesigner.PipelineMaterials.CreateUnlitColor(CurveInspectOutline, "RailInspectMat");
            _inspMr.sharedMaterials = new[] { _inspMat, _inspMat }; // submesh 0 fill, 1 lines
        }

        // Per-frame rebuild (driven by the editor like RebuildBraking): a translucent box
        // around every built curve; the curve under the cursor adds its construction legs
        // and angle and publishes its metrics to the Inspect* fields for OnGUI.
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
            // Rebuild only on a real change (graph size / hovered curve / width) — NOT every
            // frame, which would re-mesh the whole network each tick.
            int sig = Graph.Edges.Count * 92821 ^ (hovered + 1) * 6379 ^ Mathf.RoundToInt(CurveInspectWidth * 16f);
            if (sig == _inspSig) return;
            _inspSig = sig;

            _inspV.Clear(); _inspCol.Clear(); _inspTri.Clear(); _inspLine.Clear();
            InspectHovered = false;
            BuildNodeMaxSpeed();
            Color32 fill = CurveInspectFill, outline = CurveInspectOutline, hatch = CurveInspectDecelFill;
            const float lift = 0.2f;
            for (int ei = 0; ei < Graph.Edges.Count; ei++)
            {
                LineEdge e = Graph.Edges[ei];
                EdgeControls(e, Graph.Nodes[e.A], Graph.Nodes[e.B], out Vector2 c1, out Vector2 c2);
                // Decel zones are always hatched (so they read at a glance); everything else
                // fills solid only when hovered.
                EmitCurveBox(field, Graph.Nodes[e.A], c1, c2, Graph.Nodes[e.B], halfW, lift,
                             ei == hovered, IsDecelEdge(e), fill, outline, hatch);
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

        // Dash a (possibly curved) polyline. Each segment restarts the dash pattern with a
        // bounded for-loop (advancing by `period`) — no growing phase accumulator, which on
        // a large network loses float precision and spins forever.
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

        // Curve or straight controls for an edge (straight = the thirds of the chord).
        void EdgeControls(LineEdge e, Vector2 S, Vector2 E, out Vector2 c1, out Vector2 c2)
        {
            if (e.HasCurve) { c1 = e.ControlA; c2 = e.ControlB; }
            else { Vector2 d = E - S; c1 = S + d / 3f; c2 = S + d * (2f / 3f); }
        }

        // Fastest incident-edge speed at every node, in one pass (so decel detection is O(E)).
        void BuildNodeMaxSpeed()
        {
            _nodeMaxSpeed.Clear();
            for (int i = 0; i < Graph.Edges.Count; i++)
            {
                LineEdge e = Graph.Edges[i]; float v = EdgeSpeed(e);
                if (!_nodeMaxSpeed.TryGetValue(e.A, out float a) || v > a) _nodeMaxSpeed[e.A] = v;
                if (!_nodeMaxSpeed.TryGetValue(e.B, out float b) || v > b) _nodeMaxSpeed[e.B] = v;
            }
        }

        // A faster edge ties into this one at either end → it's a braking (decel) zone.
        bool IsDecelEdge(LineEdge e)
        {
            float a = _nodeMaxSpeed.TryGetValue(e.A, out float va) ? va : 0f;
            float b = _nodeMaxSpeed.TryGetValue(e.B, out float vb) ? vb : 0f;
            return Mathf.Max(a, b) > EdgeSpeed(e) + 1f;
        }

        // A box of width 2*halfW hugging the segment, with a dashed outline (sides + caps).
        // Decel zones fill with an amber hatch; other segments fill solid only when hovered.
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
                for (int i = 0; i < N; i++)   // diagonal across each band cell = "/////" hatch
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
            InspLine(InspLift(field, _boxL[0], lift), InspLift(field, _boxR[0], lift), outline);       // start cap
            InspLine(InspLift(field, _boxL[N], lift), InspLift(field, _boxR[N], lift), outline);       // end cap
        }

        // The curve whose centreline passes within halfW of the cursor (nearest wins), or -1.
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

        // Metrics + (for curves) construction legs/angle for the hovered segment → Inspect* fields.
        void EmitHoverDetails(TerrainField field, int ei, float lift)
        {
            LineEdge e = Graph.Edges[ei];
            Vector2 S = Graph.Nodes[e.A], E = Graph.Nodes[e.B];
            EdgeControls(e, S, E, out Vector2 c1, out Vector2 c2);
            Color32 legCol = new Color32(255, 235, 90, 230);

            InspectHovered = true;
            InspectIsCurve = e.HasCurve;
            InspectMid = LineGraph.Bezier(S, c1, c2, E, 0.5f);
            InspectRated = EdgeSpeed(e);
            float run = (E - S).magnitude;
            InspectGradePct = run > 1e-3f ? Mathf.Abs(NodeBedY(field, E) - NodeBedY(field, S)) / run * 100f : 0f;
            InspectHasGrade = true;

            // Decel zone if a faster neighbour ties into this (slower) segment.
            float vFast = Mathf.Max(_nodeMaxSpeed.TryGetValue(e.A, out float va) ? va : 0f,
                                    _nodeMaxSpeed.TryGetValue(e.B, out float vb) ? vb : 0f);
            InspectDecel = vFast > InspectRated + 1f
                ? $"{vFast:0} → {InspectRated:0} km/h decel zone ({BrakeDist(vFast, InspectRated):0}m)"
                : null;

            if (!e.HasCurve)
            {
                // Straight: queue space = its length (in typical-train lengths).
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

            // Construction legs: the control-leg lines (S,c1) and (E,c2) meet at the bend.
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
                // Small angle arc at the corner between the two legs.
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
            if (Mathf.Abs(denom) < 1e-6f) return false;            // parallel (near-straight)
            float t = ((p2.x - p1.x) * d2.y - (p2.y - p1.y) * d2.x) / denom;
            x = p1 + d1 * t;
            return true;
        }

        // Append every node/edge of `src` into this track (preserving curves), at the
        // given speed limit, then rebuild. Used to promote a finished survey plan to
        // real rail on its centreline. Plan nodes that sit on an existing rail node
        // (within WeldRadius) reuse it, so a plan that started on a rail end joins the
        // network instead of forming a disconnected stub. Returns the edges added.
        public int AppendGraph(LineGraph src, float speedLimit, TerrainField field)
        {
            if (src == null || src.Edges.Count == 0) return 0;
            const float weldR = 1.5f;
            float weldSq = weldR * weldR;
            float spd = speedLimit > 0f ? speedLimit : SpeedLimitKmh;
            // Map each src node to a graph node index, reusing a coincident existing one.
            int[] map = new int[src.Nodes.Count];
            for (int i = 0; i < src.Nodes.Count; i++)
            {
                Vector2 p = src.Nodes[i];
                int reuse = -1;
                for (int j = 0; j < Graph.Nodes.Count; j++)
                    if ((Graph.Nodes[j] - p).sqrMagnitude <= weldSq) { reuse = j; break; }
                map[i] = reuse >= 0 ? reuse : Graph.AddNode(p);
            }
            int added = 0;
            foreach (LineEdge e in src.Edges)
            {
                int a = map[e.A], b = map[e.B];
                if (a == b) continue;
                bool dup = false;
                foreach (LineEdge x in Graph.Edges)
                    if ((x.A == a && x.B == b) || (x.A == b && x.B == a)) { dup = true; break; }
                if (dup) continue;
                Graph.Edges.Add(new LineEdge(a, b)
                {
                    HasCurve = e.HasCurve,
                    ControlA = e.ControlA,
                    ControlB = e.ControlB,
                    SpeedLimit = spd,
                });
                added++;
            }
            _chainTail = -1; // don't chain the next interactive click off the appended end
            Rebuild(field);
            return added;
        }

        // Red overlay along any edge whose component isn't the largest (the "main"
        // network) — so a stranded stretch that isn't truly connected stands out.
        void BuildNetworkOverlay(TerrainField field)
        {
            EnsureNetOverlay();
            _nv.Clear(); _ni.Clear();
            if (HighlightDisconnected && Graph.Edges.Count > 0)
            {
                int[] label = Network.ComponentLabels(out int count);
                if (count > 1)
                {
                    int main = Network.LargestComponent(label, count);
                    foreach (LineEdge e in Graph.Edges)
                    {
                        if (e.A < 0 || e.A >= label.Length || label[e.A] == main) continue;
                        Vector2 p0 = Graph.Nodes[e.A], p3 = Graph.Nodes[e.B], q1, q2;
                        if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
                        else { Vector2 d = p3 - p0; q1 = p0 + d / 3f; q2 = p0 + d * (2f / 3f); }
                        const int N = 16;
                        Vector3 prev = default;
                        for (int i = 0; i <= N; i++)
                        {
                            Vector2 xz = LineGraph.Bezier(p0, q1, q2, p3, i / (float)N);
                            Vector3 c = new Vector3(xz.x, (field != null ? field.SampleHeight(xz.x, xz.y) : 0f) + 0.5f, xz.y);
                            if (i > 0) { _ni.Add(_nv.Count); _ni.Add(_nv.Count + 1); _nv.Add(prev); _nv.Add(c); }
                            prev = c;
                        }
                    }
                }
            }
            _netMr.enabled = _nv.Count > 0;
            _netMesh.Clear();
            _netMesh.SetVertices(_nv);
            _netMesh.SetIndices(_ni, MeshTopology.Lines, 0);
            _netMesh.RecalculateBounds();
        }

        void EnsureNetOverlay()
        {
            if (_netMf != null) return;
            _netGo = new GameObject(RootName + "_Network") { hideFlags = HideFlags.DontSave };
            _netGo.transform.SetParent(_root != null ? _root.transform : null, worldPositionStays: false);
            _netMf = _netGo.AddComponent<MeshFilter>();
            _netMr = _netGo.AddComponent<MeshRenderer>();
            _netMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _netMr.receiveShadows = false;
            _netMesh = new Mesh { name = "RailNetMesh" };
            _netMf.sharedMesh = _netMesh;
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            Color col = new Color(1f, 0.2f, 0.2f, 1f);
            _netMat = sh != null ? new Material(sh) { name = "RailNetMat", color = col }
                                 : NetworkDesigner.PipelineMaterials.CreateUnlitColor(col, "RailNetMat");
            _netMr.sharedMaterial = _netMat;
        }

        void EnsurePuckOverlay()
        {
            if (_puckMf != null) return;
            _puckGo = new GameObject(RootName + "_Pucks") { hideFlags = HideFlags.DontSave };
            _puckGo.transform.SetParent(_root != null ? _root.transform : null, worldPositionStays: false);
            _puckMf = _puckGo.AddComponent<MeshFilter>();
            _puckMr = _puckGo.AddComponent<MeshRenderer>();
            _puckMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _puckMr.receiveShadows = false;
            _puckMesh = new Mesh { name = "RailPuckMesh" };
            _puckMf.sharedMesh = _puckMesh;
            // Lit, translucent (so the 3D form shades like the road pucks). Two materials
            // for the two submeshes (base / hover).
            _puckMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(NodePuckColor, 0.2f, "RailPuckMat");
            _puckMatHover = NetworkDesigner.PipelineMaterials.CreateLitTransparent(NodePuckHoverColor, 0.2f, "RailPuckHoverMat");
            _puckMr.sharedMaterials = new[] { _puckMat, _puckMatHover };
        }

        // Rebuild the node pucks: a short 3D lit cylinder per node sitting on the
        // terrain, the node nearest `hover` (within NodePickRadius) in the hover colour.
        // Driven each frame by the editor while rail is the active line layer.
        public void UpdateNodePucks(TerrainField field, Vector2 hover, bool show, int armedNode = -1)
        {
            EnsurePuckOverlay();
            bool on = show && ShowNodePucks;
            _puckMr.enabled = on;
            if (!on) return;
            _puckV.Clear(); _puckN.Clear(); _puckT0.Clear(); _puckT1.Clear();
            int hoverIdx = Graph != null ? Graph.NearestNode(hover, NodePickRadius) : -1;
            float r = Mathf.Max(0.2f, NodePuckSize), h = Mathf.Max(0.05f, NodePuckHeight);
            for (int i = 0; i < Graph.Nodes.Count; i++)
            {
                bool hot = i == hoverIdx || i == armedNode;   // armed A stays highlighted
                EmitPuckCylinder(field, Graph.Nodes[i], hot ? r * 1.3f : r, hot ? h * 1.3f : h,
                                 hot ? _puckT1 : _puckT0);
            }
            if (_puckMat != null) _puckMat.color = NodePuckColor;            // live colour tweaks
            if (_puckMatHover != null) _puckMatHover.color = NodePuckHoverColor;
            _puckMesh.Clear();
            _puckMesh.indexFormat = _puckV.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            _puckMesh.SetVertices(_puckV);
            _puckMesh.SetNormals(_puckN);
            _puckMesh.subMeshCount = 2;
            _puckMesh.SetTriangles(_puckT0, 0);
            _puckMesh.SetTriangles(_puckT1, 1);
            _puckMesh.RecalculateBounds();
        }

        // A short, draped 3D cylinder (top cap + side wall) at an XZ centre, with manual
        // outward normals (flat-shaded, low-poly). Rigid like the road pucks — both rings
        // sit at the centre's ground height, top raised by `height`. Tris go into `tris`.
        void EmitPuckCylinder(TerrainField field, Vector2 c, float radius, float height, List<int> tris)
        {
            const int N = 16; const float lift = 0.05f;
            float baseY = (field != null ? field.SampleHeight(c.x, c.y) : 0f) + lift;
            float topY = baseY + height;

            // Top cap (normal up): fan centre + rim.
            int capC = _puckV.Count;
            _puckV.Add(new Vector3(c.x, topY, c.y)); _puckN.Add(Vector3.up);
            int capRim = _puckV.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                _puckV.Add(new Vector3(c.x + Mathf.Cos(a) * radius, topY, c.y + Mathf.Sin(a) * radius));
                _puckN.Add(Vector3.up);
            }
            // Wound so the front face (and the +Y normal) point UP, visible from above.
            for (int i = 0; i < N; i++) { tris.Add(capC); tris.Add(capRim + i + 1); tris.Add(capRim + i); }

            // Side wall (radial normals): a top ring and a bottom ring.
            int wTop = _puckV.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                float nx = Mathf.Cos(a), nz = Mathf.Sin(a);
                _puckV.Add(new Vector3(c.x + nx * radius, topY, c.y + nz * radius));
                _puckN.Add(new Vector3(nx, 0f, nz));
            }
            int wBot = _puckV.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                float nx = Mathf.Cos(a), nz = Mathf.Sin(a);
                _puckV.Add(new Vector3(c.x + nx * radius, baseY, c.y + nz * radius));
                _puckN.Add(new Vector3(nx, 0f, nz));
            }
            for (int i = 0; i < N; i++)
            {
                int ti = wTop + i, tj = wTop + i + 1, bi = wBot + i, bj = wBot + i + 1;
                tris.Add(bi); tris.Add(ti); tris.Add(tj);   // outward-wound
                tris.Add(bi); tris.Add(tj); tris.Add(bj);
            }
        }

        static void Apply(Mesh m, List<Vector3> v, List<int> t)
        {
            if (m == null) return;
            m.Clear();
            m.indexFormat = v.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
        }

        // Ground height under a world XZ (respects Conform; 0 when off).
        float GroundY(TerrainField field, Vector2 xz)
            => (Conform && field != null) ? field.SampleHeight(xz.x, xz.y) : 0f;

        // Bed-top elevation at a node = ground there + offset + ballast height.
        float NodeBedY(TerrainField field, Vector2 xz)
            => GroundY(field, xz) + VerticalOffset + Mathf.Max(0f, BallastHeight);

        // Average grade (degrees) of a section a->b: rise over straight horizontal
        // run. This is the "average slope from start to end" used by the limit.
        public float GradeDegrees(TerrainField field, Vector2 a, Vector2 b)
        {
            float run = Vector2.Distance(a, b);
            if (run < 1e-3f) return 90f;
            return Mathf.Atan2(Mathf.Abs(NodeBedY(field, b) - NodeBedY(field, a)), run) * Mathf.Rad2Deg;
        }

        // Walk an edge (its bezier) sampling the TERRAIN every GradeSampleStep
        // metres and checking each section's grade — like a real alignment that
        // conforms to the ground. Returns how far you can build before a section
        // exceeds MaxGradeDeg: the buildable arc length, the curve parameter there
        // (for truncating the bezier), the total arc length, and the steepest
        // section grade seen. Result == true means a section was over the limit
        // (so buildable < total — truncate there, then mitigate / bridge across).
        public bool ScanBuildable(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3,
            out float buildableLen, out float buildableT, out float totalLen, out float worstGradeDeg)
        {
            float step = Mathf.Max(1f, GradeSampleStep);
            // Fine resolution so each sub-step is well under one grade section,
            // even on a curve (control-polygon length over-estimates the arc).
            float chord = Vector2.Distance(q0, q3);
            float poly = Vector2.Distance(q0, q1) + Vector2.Distance(q1, q2) + Vector2.Distance(q2, q3);
            float approx = Mathf.Max(chord, (chord + poly) * 0.5f);
            int fine = Mathf.Clamp(Mathf.CeilToInt(approx / Mathf.Min(step * 0.25f, 3f)), 32, 4000);

            Vector2 prev = q0;
            float arc = 0f, segRun = 0f, segStartArc = 0f, segStartT = 0f;
            float segStartY = GroundY(field, q0);
            buildableLen = 0f; buildableT = 0f; worstGradeDeg = 0f;
            bool blocked = false;
            for (int i = 1; i <= fine; i++)
            {
                float t = i / (float)fine;
                Vector2 cur = LineGraph.Bezier(q0, q1, q2, q3, t);
                float d = Vector2.Distance(prev, cur);
                arc += d; segRun += d;
                prev = cur;
                if (segRun >= step || i == fine)
                {
                    float curY = GroundY(field, cur);
                    float gradeDeg = Mathf.Atan2(Mathf.Abs(curY - segStartY), Mathf.Max(1e-3f, segRun)) * Mathf.Rad2Deg;
                    if (gradeDeg > worstGradeDeg) worstGradeDeg = gradeDeg;
                    if (!blocked)
                    {
                        if (gradeDeg > MaxGradeDeg) { blocked = true; buildableLen = segStartArc; buildableT = segStartT; }
                        else { buildableLen = arc; buildableT = t; }
                    }
                    segStartArc = arc; segStartT = t; segStartY = curY; segRun = 0f;
                }
            }
            totalLen = arc;
            if (!blocked) { buildableLen = totalLen; buildableT = 1f; }
            return blocked;
        }

        // de Casteljau: the first portion [0,t] of a cubic bezier, as a cubic of
        // its own — new end point + the two control points (matching SplitEdge).
        static void SubdivideFirst(Vector2 p0, Vector2 q1, Vector2 q2, Vector2 p3, float t,
            out Vector2 c1, out Vector2 c2, out Vector2 end)
        {
            Vector2 a = Vector2.Lerp(p0, q1, t);
            Vector2 b = Vector2.Lerp(q1, q2, t);
            Vector2 c = Vector2.Lerp(q2, p3, t);
            Vector2 ab = Vector2.Lerp(a, b, t);
            Vector2 bc = Vector2.Lerp(b, c, t);
            end = Vector2.Lerp(ab, bc, t);
            c1 = a; c2 = ab;
        }

        // Sample the track and collect points the terrain carve should trench down
        // to grade — (x, bedTopY, z). Includes BOTH the open-cut approaches (below
        // grade but not tunnel-deep) AND a short notch into each tunnel mouth, so
        // both portals open up even when one side drops off steeply (no approach).
        public void CollectOpenCuts(TerrainField field, float tunnelBury, List<Vector3> outXZBed)
        {
            const float mouthNotch = 9f; // how far into the bore the mouth is dug open
            foreach (LineEdge e in Graph.Edges)
            {
                Vector2 q0 = Graph.Nodes[e.A], q3 = Graph.Nodes[e.B], q1, q2;
                if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
                else { Vector2 d = q3 - q0; q1 = q0 + d / 3f; q2 = q0 + d * (2f / 3f); }
                float yA = NodeBedY(field, q0), yB = NodeBedY(field, q3);
                float chord = Vector2.Distance(q0, q3);
                int n = Mathf.Max(2, Mathf.CeilToInt(chord / 3f));
                float step = chord / n;
                int notch = Mathf.Max(1, Mathf.CeilToInt(mouthNotch / Mathf.Max(0.5f, step)));

                var xzA = new Vector2[n + 1];
                var bedA = new float[n + 1];
                var tun = new bool[n + 1];
                var openCut = new bool[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    float u = i / (float)n;
                    xzA[i] = LineGraph.Bezier(q0, q1, q2, q3, u);
                    bedA[i] = Mathf.Lerp(yA, yB, u);
                    float bury = GroundY(field, xzA[i]) - bedA[i];
                    tun[i] = bury >= tunnelBury;
                    openCut[i] = bury > 0.3f && bury < tunnelBury;
                }
                for (int i = 0; i <= n; i++)
                {
                    bool emit = openCut[i];
                    if (!emit && tun[i]) // a tunnel sample within `notch` of a mouth?
                        for (int k = -notch; k <= notch && !emit; k++)
                        {
                            int j = i + k;
                            if (j >= 0 && j <= n && !tun[j]) emit = true;
                        }
                    if (emit) outXZBed.Add(new Vector3(xzA[i].x, bedA[i], xzA[i].y));
                }
            }
        }

        const int SubSteps = 32;            // bezier samples for the arc-length table
        const float RailSegment = 1.5f;     // target rail box length along the curve
        static readonly Vector2[] _pts = new Vector2[SubSteps + 1];
        static readonly float[] _cum = new float[SubSteps + 1];

        // Build one edge as a CONSTANT-GRADE alignment: the bed top runs straight
        // in elevation from the start node to the end node (not draping over every
        // bump), and the ballast fills the gap down to the actual ground — a fill
        // embankment with a gravel cap over a vertical retaining wall, or a deck +
        // piers (bridge) for deep fills. In cuts the track sits below the terrain.
        void BuildEdge(TerrainField field, LineEdge e)
        {
            Vector2 q0 = Graph.Nodes[e.A], q3 = Graph.Nodes[e.B];
            Vector2 q1, q2;
            if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
            else { Vector2 d = q3 - q0; q1 = q0 + d / 3f; q2 = q0 + d * (2f / 3f); }

            _pts[0] = q0; _cum[0] = 0f;
            for (int i = 1; i <= SubSteps; i++)
            {
                _pts[i] = LineGraph.Bezier(q0, q1, q2, q3, i / (float)SubSteps);
                _cum[i] = _cum[i - 1] + Vector2.Distance(_pts[i - 1], _pts[i]);
            }
            float len = _cum[SubSteps];
            if (len < 1e-3f) return;

            float bed = Mathf.Max(0f, BallastHeight);
            float yA = NodeBedY(field, q0), yB = NodeBedY(field, q3); // bed top at the nodes
            float grade = (yB - yA) / len;
            float halfG = Gauge * 0.5f;
            float railUp = TieHeight + RailHeight * 0.5f; // above the bed top (pos)
            float topHW = TieLength * 0.5f + Mathf.Max(0f, BallastShoulder);
            float slope = Mathf.Max(0f, BallastSlope);

            int segs = Mathf.Max(1, Mathf.CeilToInt(len / RailSegment));
            float embMax = Mathf.Max(0.01f, EmbankmentMaxDrop);
            float deckDepth = Mathf.Max(0.1f, DeckDepth);
            int pierStep = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(1f, PierSpacing) / (len / segs)));
            bool prefabDeck = BridgePrefab != null;
            int spanStep = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(1f, BridgeSpan) / (len / segs)));
            float tHalf = TieLength * 0.5f + Mathf.Max(0f, TunnelMargin);  // tunnel bore half-width
            float clearance = Mathf.Max(2f, TunnelClearance);
            float tunnelBury = clearance + Mathf.Max(0f, TunnelMinCover);  // bed must be this far under ground

            Vector3 prevL = default, prevR = default;
            Vector3 pTL = default, pTR = default;       // bed-top edges
            Vector3 pToeL = default, pToeR = default;   // gravel-shoulder toes
            Vector3 pWbL = default, pWbR = default;     // wall bases (ground)
            float pToeY = 0f; Vector3 pCtr = default;   // prev centreline (deck)
            bool pBridge = false;
            // Tunnel bore cross-section (prev): floor L/R, ceiling L/R.
            bool pTunnel = false;
            Vector3 pFL = default, pFR = default, pCL = default, pCR = default;
            for (int i = 0; i <= segs; i++)
            {
                GradedSample(field, q0, q1, q2, q3, len * i / segs, len, yA, yB, grade,
                             out Vector3 pos, out Vector3 fwd, out Vector3 right, out Vector3 up, out float groundYc);
                Vector3 cL = pos + right * halfG + up * railUp;
                Vector3 cR = pos - right * halfG + up * railUp;

                // Per-sample fill -> how the bed reaches the ground.
                float fill = Mathf.Max(0f, pos.y - groundYc);
                bool bridge = fill > BridgeAboveFill;
                float gravelDrop = Mathf.Min(fill, embMax);       // sloped gravel cap
                float toeHWc = topHW + gravelDrop * slope;
                float toeY = pos.y - gravelDrop;
                Vector2 cxz = new Vector2(pos.x, pos.z);
                Vector2 rxz = new Vector2(right.x, right.z);
                rxz = rxz.sqrMagnitude > 1e-6f ? rxz.normalized : Vector2.right;
                Vector2 toeXZL = cxz - rxz * toeHWc, toeXZR = cxz + rxz * toeHWc;
                Vector3 TL = pos - right * topHW, TR = pos + right * topHW;
                Vector3 toeL = new Vector3(toeXZL.x, toeY, toeXZL.y);
                Vector3 toeR = new Vector3(toeXZR.x, toeY, toeXZR.y);
                Vector3 wbL = new Vector3(toeXZL.x, Mathf.Min(GroundY(field, toeXZL), toeY), toeXZL.y);
                Vector3 wbR = new Vector3(toeXZR.x, Mathf.Min(GroundY(field, toeXZR), toeY), toeXZR.y);

                // Tunnel: bed buried deeper than the bore + cover -> a bored liner.
                bool tunnel = (groundYc - pos.y) > tunnelBury;
                const float floorSink = 0.1f;
                Vector3 FL = pos - right * tHalf - up * floorSink;
                Vector3 FR = pos + right * tHalf - up * floorSink;
                Vector3 CL = pos - right * tHalf + up * clearance;
                Vector3 CR = pos + right * tHalf + up * clearance;

                if (i > 0)
                {
                    AddRailSeg(prevL, cL);
                    AddRailSeg(prevR, cR);
                    bool bridgeSeg = bridge || pBridge;

                    if (tunnel && pTunnel)
                    {
                        // Inward-facing bore liner (concrete): floor, ceiling, 2 walls.
                        Quad(_sv, _st, pFL, FL, FR, pFR);   // floor (+up)
                        Quad(_sv, _st, pCL, pCR, CR, CL);   // ceiling (-up)
                        Quad(_sv, _st, pFL, pCL, CL, FL);   // left wall (inward)
                        Quad(_sv, _st, pFR, FR, CR, pCR);   // right wall (inward)
                    }
                    if (tunnel != pTunnel) // a mouth: frame the opening with a portal
                    {
                        if (tunnel) AddTunnelPortal(FL, FR, CL, CR);
                        else AddTunnelPortal(pFL, pFR, pCL, pCR);
                    }
                    if (bed > 1e-4f)
                    {
                        // On a prefab bridge span the prefab IS the deck, so skip the
                        // gravel bed (it would hide the prefab).
                        if (!(bridgeSeg && prefabDeck))
                        {
                            Quad(_bv, _bt, pTL, TL, TR, pTR);     // bed top (gravel)
                            Quad(_bv, _bt, pTR, TR, toeR, pToeR); // right gravel cap
                            Quad(_bv, _bt, pTL, pToeL, toeL, TL); // left gravel cap
                        }
                        if (bridgeSeg && !prefabDeck)
                        {
                            // Procedural deck beam (only when no prefab is assigned).
                            AddBeam(_sv, _st, new Vector3(pCtr.x, pToeY, pCtr.z),
                                    new Vector3(pos.x, toeY, pos.z), 2f * toeHWc, deckDepth);
                        }
                        else if (!bridgeSeg)
                        {
                            // Vertical retaining walls (concrete) toe -> ground.
                            if (toeY - wbR.y > 1e-3f || pToeY - pWbR.y > 1e-3f)
                                Quad(_sv, _st, pToeR, toeR, wbR, pWbR);
                            if (toeY - wbL.y > 1e-3f || pToeY - pWbL.y > 1e-3f)
                                Quad(_sv, _st, pToeL, pWbL, wbL, toeL);
                        }
                    }
                }

                // Bridge prefab placed in series along the span (replaces the deck).
                if (bridge && prefabDeck && i % spanStep == 0)
                    SpawnBridge(new Vector3(pos.x, toeY + BridgeVerticalOffset, pos.z), fwd);

                // Bridge piers: a vertical column from the deck soffit to ground
                // (kept under a prefab deck unless the prefab carries its own).
                if (bridge && bed > 1e-4f && i % pierStep == 0 && (!prefabDeck || ProceduralPiers))
                {
                    float top = toeY - deckDepth;
                    if (top - groundYc > 0.2f)
                    {
                        float pw = Mathf.Max(0.1f, PierWidth);
                        AddBox(_sv, _st, new Vector3(pos.x, (top + groundYc) * 0.5f, pos.z),
                               Vector3.forward, Vector3.right, Vector3.up, pw, pw, top - groundYc);
                    }
                }

                prevL = cL; prevR = cR;
                pTL = TL; pTR = TR; pToeL = toeL; pToeR = toeR; pWbL = wbL; pWbR = wbR;
                pToeY = toeY; pCtr = pos; pBridge = bridge;
                pTunnel = tunnel; pFL = FL; pFR = FR; pCL = CL; pCR = CR;
            }

            // Ties at fixed spacing, resting on the bed top.
            float s = Mathf.Max(0.1f, TieSpacing);
            for (float d = 0f; d <= len + 1e-3f; d += s)
            {
                GradedSample(field, q0, q1, q2, q3, Mathf.Min(d, len), len, yA, yB, grade,
                             out Vector3 pos, out Vector3 fwd, out Vector3 right, out Vector3 up, out _);
                Vector3 center = pos + up * (TieHeight * 0.5f);
                AddBox(_tv, _tt, center, fwd, right, up, TieThickness, TieLength, TieHeight);
            }
        }

        // A horizontal beam (box) between two centreline points, its TOP at a/b's
        // level and extending DOWN by `height`. Used for the bridge deck.
        void AddBeam(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, float width, float height)
        {
            Vector3 along = b - a;
            float l = along.magnitude;
            if (l < 1e-4f) return;
            Vector3 fwd = along / l;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;
            AddBox(v, t, (a + b) * 0.5f - up * (height * 0.5f), fwd, right, up, l, width, height);
        }

        // Concrete portal frame around a tunnel mouth: two jambs + a lintel.
        void AddTunnelPortal(Vector3 fl, Vector3 fr, Vector3 cl, Vector3 cr)
        {
            const float thick = 0.7f;
            AddStrut(fl, cl, thick); // left jamb
            AddStrut(fr, cr, thick); // right jamb
            AddStrut(cl, cr, thick); // lintel
        }

        // A square-section concrete strut (box) between two points.
        void AddStrut(Vector3 a, Vector3 b, float thick)
        {
            Vector3 along = b - a;
            float l = along.magnitude;
            if (l < 1e-4f) return;
            Vector3 f = along / l;
            Vector3 r = Vector3.Cross(Vector3.up, f);
            r = r.sqrMagnitude < 1e-6f ? Vector3.right : r.normalized;
            Vector3 u = Vector3.Cross(f, r).normalized;
            AddBox(_sv, _st, (a + b) * 0.5f, f, r, u, l, thick, thick);
        }

        // Instantiate the bridge prefab at a deck point, yawed to the track.
        void SpawnBridge(Vector3 pos, Vector3 fwd)
        {
            if (BridgePrefab == null || _root == null) return;
            Vector3 f = fwd; f.y = 0f;
            float yaw = (f.sqrMagnitude > 1e-6f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : 0f) + BridgeYawOffset;
            GameObject go = UnityEngine.Object.Instantiate(BridgePrefab, pos,
                Quaternion.Euler(0f, yaw, 0f), _root.transform);
            if (BridgeScale > 0f && !Mathf.Approximately(BridgeScale, 1f)) go.transform.localScale *= BridgeScale;
            Collider[] cols = go.GetComponentsInChildren<Collider>();
            for (int c = 0; c < cols.Length; c++) DestroySafe(cols[c]);
            _bridgeInstances.Add(go);
        }

        // Sample the constant-grade alignment at arc length `arc`: pos = bed top
        // (graded Y), basis tilts with the grade, groundY = actual terrain there.
        void GradedSample(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3,
                          float arc, float len, float yA, float yB, float grade,
                          out Vector3 pos, out Vector3 fwd, out Vector3 right, out Vector3 up, out float groundY)
        {
            float u = ArcToU(arc);
            Vector2 xz = LineGraph.Bezier(q0, q1, q2, q3, u);
            pos = new Vector3(xz.x, Mathf.Lerp(yA, yB, len > 1e-4f ? arc / len : 0f), xz.y);
            groundY = GroundY(field, xz);
            Vector2 t = LineGraph.BezierTangent(q0, q1, q2, q3, u);
            Vector3 horiz = t.sqrMagnitude > 1e-6f ? new Vector3(t.x, 0f, t.y).normalized : Vector3.forward;
            fwd = (horiz + Vector3.up * grade).normalized;
            right = Vector3.Cross(Vector3.up, horiz);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            up = Vector3.Cross(fwd, right).normalized;
        }

        // A rail box between two consecutive rail-centre points on the curve.
        void AddRailSeg(Vector3 a, Vector3 b)
        {
            Vector3 along = b - a;
            float l = along.magnitude;
            if (l < 1e-4f) return;
            Vector3 fwd = along / l;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;
            AddBox(_rv, _rt, (a + b) * 0.5f, fwd, right, up, l, RailWidth, RailHeight);
        }

        // Arc length -> bezier parameter u, via the cumulative table.
        float ArcToU(float d)
        {
            if (d <= 0f) return 0f;
            float len = _cum[SubSteps];
            if (d >= len) return 1f;
            int seg = 1;
            while (seg < SubSteps && _cum[seg] < d) seg++;
            float segLen = _cum[seg] - _cum[seg - 1];
            float f = segLen > 1e-5f ? (d - _cum[seg - 1]) / segLen : 0f;
            return (seg - 1 + f) / SubSteps;
        }

        // Oriented box with un-shared per-face verts (flat low-poly normals).
        // lenF/lenR/lenU are full sizes along fwd/right/up. Each face is wound so
        // RecalculateNormals (normal ~ cross(b-a, c-a)) points outward.
        static void AddBox(List<Vector3> v, List<int> t, Vector3 c,
                           Vector3 fwd, Vector3 right, Vector3 up,
                           float lenF, float lenR, float lenU)
        {
            Vector3 F = fwd * (lenF * 0.5f);
            Vector3 R = right * (lenR * 0.5f);
            Vector3 U = up * (lenU * 0.5f);
            Quad(v, t, c + F - R - U, c + F + R - U, c + F + R + U, c + F - R + U); // +fwd
            Quad(v, t, c - F - U - R, c - F + U - R, c - F + U + R, c - F - U + R); // -fwd
            Quad(v, t, c + R - U - F, c + R + U - F, c + R + U + F, c + R - U + F); // +right
            Quad(v, t, c - R - F - U, c - R + F - U, c - R + F + U, c - R - F + U); // -right
            Quad(v, t, c + U - F - R, c + U + F - R, c + U + F + R, c + U - F + R); // +up
            Quad(v, t, c - U - R - F, c - U + R - F, c - U + R + F, c - U - R + F); // -up
        }

        static void Quad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        // ---- placement preview ----

        [Tooltip("Length (m) of the straight-ahead alignment guide — the dashed " +
                 "collinear extension drawn out of the chain tail. Also bounds how " +
                 "far ahead the extension snap reaches.")]
        public float ExtensionGuideLength = 120f;

        // Heading that continues straight out of the chain tail (180° / collinear
        // continuation). When the tail sits mid-track (several edges), pick the edge whose
        // continuation points toward `toward` — so clicking a node and moving the cursor to
        // one side extends the track on THAT side (cursor right of the node → the left
        // track, which continues rightward, is extended). False when the tail has no edge.
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
                Vector2 p0 = Graph.Nodes[e.A], p3 = Graph.Nodes[e.B], q1, q2;
                if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
                else { Vector2 d = p3 - p0; q1 = p0 + d / 3f; q2 = p0 + d * (2f / 3f); }
                // Direction leaving the tail, continuing the way the track arrived.
                Vector2 cont = e.B == _chainTail ? LineGraph.BezierTangent(p0, q1, q2, p3, 1f)
                                                 : -LineGraph.BezierTangent(p0, q1, q2, p3, 0f);
                if (cont.sqrMagnitude < 1e-6f) cont = e.B == _chainTail ? p3 - p0 : p0 - p3;
                if (cont.sqrMagnitude < 1e-6f) continue;
                cont = cont.normalized;
                float dot = Vector2.Dot(cont, side);          // align continuation with cursor side
                if (dot > bestDot) { bestDot = dot; dir = cont; found = true; }
            }
            return found;
        }

        [Tooltip("Snap radius (m) to the straight-ahead alignment guide (the dashed " +
                 "collinear extension of the incoming track). A cursor within this of " +
                 "that line locks onto it so the next segment continues dead straight. " +
                 "0 = off.")]
        public float ExtensionSnapRadius = 4f;

        // Snap a cursor XZ onto the straight-ahead alignment guide — the collinear
        // extension of the incoming edge — when within ExtensionSnapRadius of that
        // ray, so the next segment continues dead straight. Only ahead of the tail
        // and out to the guide's drawn length. False if there's no incoming heading
        // or the cursor is too far off the line.
        public bool TrySnapToExtension(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            float r = Mathf.Max(0f, ExtensionSnapRadius);
            if (r <= 0f || !IncomingDirection(cursor, out Vector2 dir)) return false;
            Vector2 origin = Graph.Nodes[_chainTail];
            float along = Vector2.Dot(cursor - origin, dir);
            if (along <= 0f || along > ExtensionGuideLength) return false; // behind tail / past guide
            Vector2 proj = origin + dir * along; // foot of the perpendicular on the ray
            if ((cursor - proj).sqrMagnitude > r * r) return false;
            snapped = proj;
            return true;
        }

        // Extending an existing track in STRAIGHT mode: HARD-lock the cursor onto the chosen
        // collinear extension line (ahead of the tail). Rail can't kink, so a straight
        // continuation must stay collinear — turns are made with the curve tool (Shift).
        // Off in curve mode (the bend/end own the cursor then) and on a fresh chain with no
        // incoming edge (the first segment picks its own heading).
        public bool TrySnapExtensionHard(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (CurveModifier || _cornerPending
                || _chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            if (!IncomingDirection(cursor, out Vector2 ext)) return false;
            Vector2 origin = Graph.Nodes[_chainTail];
            float along = Mathf.Max(0.5f, Vector2.Dot(cursor - origin, ext)); // stay ahead of the tail
            snapped = origin + ext * along;
            return true;
        }

        // After the bend is armed, HARD-lock the end onto the equal-leg circle (bend->end
        // == start->bend) so curves stay symmetric, and clamp the direction into the
        // buildable (yellow) arc so you can't make a curve that won't work for the speed.
        public bool TrySnapCurveSymmetry(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (!_cornerPending || CurveSymmetrySnap <= 0f
                || _chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            Vector2 start = Graph.Nodes[_chainTail], bend = _corner;
            float legA = Vector2.Distance(start, bend);
            if (legA < 0.5f) return false;
            Vector2 toCur = cursor - bend;
            Vector2 dir = toCur.sqrMagnitude > 1e-6f ? toCur.normalized : (bend - start).normalized;
            if (!ClampToBuildableArc(start, bend, legA, dir, out Vector2 cdir)) return false; // no real turn fits → free cursor (back out)
            snapped = bend + cdir * legA;   // lock to ring distance + a buildable turn
            return true;
        }

        // Clamp a direction onto the buildable (real-turn) part of the equal-leg ring,
        // which is two arcs at [θmin, θmax] deflection on each side of the straight-ahead
        // continuation. Clamps the cursor's signed deflection into that band on its side.
        // False when no real turn fits (leg too short → whole ring red).
        bool ClampToBuildableArc(Vector2 start, Vector2 bend, float legA, Vector2 dir, out Vector2 outDir)
        {
            outDir = dir;
            Vector2 a0 = (bend - start).sqrMagnitude > 1e-6f ? (bend - start).normalized : Vector2.right;
            float a0A = Mathf.Atan2(a0.y, a0.x) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(a0A, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg); // signed deflection
            float sign = delta >= 0f ? 1f : -1f;
            // Constrain to [0, thMax] on the cursor's side — straight-ahead (0) up to the
            // tightest safe turn. The end can never leave this yellow arc.
            float thMax = MaxBuildableDeflection(start, bend, legA, a0A, sign);
            float mag = SnapDeflectionToTick(Mathf.Clamp(Mathf.Abs(delta), 0f, thMax), thMax);
            float ang = (a0A + mag * sign) * Mathf.Deg2Rad;
            outDir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            return true;
        }

        bool DeflectionBuildable(Vector2 start, Vector2 bend, float legA, float a0A, float sign, float d)
        {
            float ang = (a0A + sign * d) * Mathf.Deg2Rad;
            return CurveBuildable(start, bend, bend + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * legA);
        }

        // Largest deflection (deg) from a0 on the given side whose curve still meets the
        // radius. Coarse 1° scan to bracket the buildable→too-tight boundary (radius is
        // monotonic in deflection), then bisect so the clamp reaches the FULL yellow extent
        // instead of stopping up to a whole step short.
        float MaxBuildableDeflection(Vector2 start, Vector2 bend, float legA, float a0A, float sign)
        {
            // Scan to nearly 180° — a curve can deflect past 90° (even toward a U-turn) as
            // long as its radius stays ≥ the speed's minimum, so the buildable arc must not
            // be capped at 90° (that left the clamp short of the yellow ring on long legs).
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
        // at the speed. If we're extending a track, lock the bend onto that track's
        // collinear extension line (no kink), no closer than the MDT; otherwise it sticks
        // to the MDT ring and is free to pull out past it for a sharper buildable curve.
        public bool TrySnapBendToTarget(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (CurveSymmetrySnap <= 0f || !CurveModifier || _cornerPending
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
        public bool InCurveMode => (CurveModifier || _cornerPending) && CurveSymmetrySnap > 0f;

        // True while positioning the curve END: the PAC (equal-leg ring) must own the
        // cursor exclusively — no falling through to the straight extension line or a
        // nearby track, which would pull the end off the buildable arc.
        public bool PlacingCurveEnd => _cornerPending && CurveSymmetrySnap > 0f;

        // Outward continuation heading at the rail NODE nearest p (within maxDist):
        // the direction the track was travelling as it reached that end, so a plan
        // can carry straight on off the rail. False if no node / it has no edge.
        public bool TryEndHeading(Vector2 p, float maxDist, out Vector2 dir)
        {
            dir = Vector2.zero;
            if (Graph == null) return false;
            int n = Graph.NearestNode(p, maxDist);
            if (n < 0) return false;
            for (int i = 0; i < Graph.Edges.Count; i++)
            {
                LineEdge e = Graph.Edges[i];
                if (e.A != n && e.B != n) continue;
                Vector2 p0 = Graph.Nodes[e.A], p3 = Graph.Nodes[e.B], q1, q2;
                if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
                else { Vector2 d = p3 - p0; q1 = p0 + d / 3f; q2 = p0 + d * (2f / 3f); }
                dir = e.B == n ? LineGraph.BezierTangent(p0, q1, q2, p3, 1f)
                               : -LineGraph.BezierTangent(p0, q1, q2, p3, 0f);
                if (dir.sqrMagnitude < 1e-6f) dir = e.B == n ? p3 - p0 : p0 - p3;
                if (dir.sqrMagnitude < 1e-6f) return false;
                dir = dir.normalized;
                return true;
            }
            return false;
        }

        // Heading (unit XZ) of the nearest rail edge within maxDist of p, plus the
        // point on that edge. For the terrain slope tool's network-aware "straight"
        // guide. False if no edge is in range.
        public bool TryTrackHeadingNear(Vector2 p, float maxDist, out Vector2 dir, out Vector2 at)
        {
            dir = Vector2.zero; at = p;
            if (Graph == null || !Graph.NearestPointOnEdge(p, maxDist, out int ei, out float t, out Vector2 pt))
                return false;
            LineEdge e = Graph.Edges[ei];
            Vector2 p0 = Graph.Nodes[e.A], p3 = Graph.Nodes[e.B], q1, q2;
            if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
            else { Vector2 d = p3 - p0; q1 = p0 + d / 3f; q2 = p0 + d * (2f / 3f); }
            Vector2 tan = LineGraph.BezierTangent(p0, q1, q2, p3, t);
            if (tan.sqrMagnitude < 1e-6f) tan = p3 - p0;
            if (tan.sqrMagnitude < 1e-6f) return false;
            dir = tan.normalized; at = pt;
            return true;
        }

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; }

        public void UpdatePreview(TerrainField field, Vector3 cursor, bool show)
        {
            EnsurePreview();
            _pvMr.enabled = show;
            if (!show) return;
            _pvVerts.Clear();
            _pvIdx.Clear();
            _pvIdxRed.Clear();
            const float lift = 0.15f;

            DrawPuck(field, cursor, lift);
            Vector2 cur = new Vector2(cursor.x, cursor.z);
            bool haveStart = _chainTail >= 0 && _chainTail < Graph.Nodes.Count;
            Vector2 start = haveStart ? Graph.Nodes[_chainTail] : Vector2.zero;

            LastPreviewRadius = float.PositiveInfinity;
            LastPreviewTooTight = false;
            LastPreviewGradeDeg = 0f;
            LastPreviewEndpointGradeDeg = 0f;
            LastPreviewTooSteep = false;
            LastPreviewTruncated = false;
            LastPreviewBuildableLen = 0f;
            LastPreviewTotalLen = 0f;
            CurveDimsValid = false;
            PreviewBrakeReqRadius = 0f;

            if (haveStart)
            {
                // Bed elevations at the endpoints (the constant grade the rails
                // would be drawn at). The terrain profile in between drives the
                // buildable length / red tail (PreviewScan).
                float yA = NodeBedY(field, start), yB = NodeBedY(field, cur);
                // True grade A->B = the constant grade the section is actually
                // built at (what override mode commits to across the whole edge).
                LastPreviewEndpointGradeDeg = GradeDegrees(field, start, cur);

                // Alignment guide: a dashed extension continuing the incoming
                // segment's heading, so the next stretch can be laid collinear (180°).
                // Hidden once the bend is armed — the curve, not the straight guide,
                // is what's being placed then.
                if (!_cornerPending && IncomingDirection(cur, out Vector2 ext))
                    EmitDashed(field, start, start + ext * ExtensionGuideLength, Vector2.zero, lift);

                if (_cornerPending)
                {
                    EmitDashed(field, start, _corner, Vector2.zero, lift); // construction legs
                    EmitDashed(field, _corner, cur, Vector2.zero, lift);
                    // (The equal-leg symmetry ring is drawn speed-coloured in the braking
                    // overlay — see AppendSymmetryRing.)
                    CurveControls(start, cur, _corner, out Vector2 c1, out Vector2 c2);
                    LastPreviewRadius = MinCurveRadius(start, c1, c2, cur);
                    LastPreviewTooTight = LastPreviewRadius < MinRadiusForSpeed;
                    // Off a faster line: also require the speed-appropriate radius through
                    // the braking zone (gentle while still fast, tighter once slowed).
                    if (PreviewBrakeValid)
                    {
                        PreviewBrakeReqRadius = WorstBrakingRadiusViolation(start, c1, c2, cur, PreviewBrakeVIn, SpeedLimitKmh, PreviewBrakeArcCovered);
                        if (PreviewBrakeReqRadius > 0f) LastPreviewTooTight = true;
                    }
                    float bLen = PreviewScan(field, start, c1, c2, cur);
                    DrawGradedRails(start, c1, c2, cur, yA, yB, lift, bLen);
                    // Dimension labels: the two construction legs A->bend and bend->B.
                    CurveDimsValid = true;
                    CurveLegA = Vector2.Distance(start, _corner);
                    CurveLegB = Vector2.Distance(_corner, cur);
                    CurveLegAMid = LegMid(field, start, _corner);
                    CurveLegBMid = LegMid(field, _corner, cur);
                }
                else if (CurveModifier)
                {
                    // Below the min leg, no symmetric turn meets the speed's min radius —
                    // draw the guide RED and mark the min-leg target along the direction.
                    float minLeg = MinFirstLegForSpeed();
                    float leg = Vector2.Distance(start, cur);
                    bool tooShort = leg < minLeg;
                    EmitDashed(field, start, cur, Vector2.zero, lift, tooShort); // arming the corner
                    if (minLeg > 1f && (cur - start).sqrMagnitude > 1e-4f)
                    {
                        Vector2 dir = (cur - start).normalized;
                        DrawTargetMarker(field, start + dir * minLeg, lift);
                    }
                    // Single-leg dimension while positioning the bend (before the click).
                    CurveDimsValid = true;
                    CurveLegA = leg;
                    CurveLegB = 0f;
                    CurveLegAMid = LegMid(field, start, cur);
                }
                else
                {
                    Vector2 dd = cur - start; // straight: chord controls
                    Vector2 c1 = start + dd / 3f, c2 = start + dd * (2f / 3f);
                    float bLen = PreviewScan(field, start, c1, c2, cur);
                    DrawGradedRails(start, c1, c2, cur, yA, yB, lift, bLen);
                }
            }

            // Buildable submesh: red only when the curve is too tight (a whole-edge
            // problem); otherwise amber. The over-grade tail (submesh 1) is always red.
            if (_pvMat != null)
                _pvMat.color = LastPreviewTooTight
                    ? new Color(1f, 0.25f, 0.2f, 1f) : new Color(1f, 0.8f, 0.3f, 1f);

            _pvMesh.Clear();
            _pvMesh.SetVertices(_pvVerts);
            _pvMesh.subMeshCount = 2;
            _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0);
            _pvMesh.SetIndices(_pvIdxRed, MeshTopology.Lines, 1);
            _pvMesh.RecalculateBounds();
        }

        // Draped world midpoint of an A->B leg (label anchor), lifted clear of the line.
        Vector3 LegMid(TerrainField field, Vector2 a, Vector2 b)
        {
            Vector2 m = (a + b) * 0.5f;
            float y = (field != null ? field.SampleHeight(m.x, m.y) : 0f) + 1.5f;
            return new Vector3(m.x, y, m.y);
        }

        // Cursor ring.
        void DrawPuck(TerrainField field, Vector3 cursor, float lift)
        {
            const int N = 24; const float Rr = 0.9f;
            Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float ang = i / (float)N * Mathf.PI * 2f;
                float x = cursor.x + Mathf.Cos(ang) * Rr;
                float z = cursor.z + Mathf.Sin(ang) * Rr;
                float y = (field != null ? field.SampleHeight(x, z) : cursor.y) + lift;
                Vector3 c = new Vector3(x, y, z);
                if (i > 0) AddSeg(prev, c);
                prev = c;
            }
        }

        // Preview of the centreline + both rails at a CONSTANT grade (yA -> yB),
        // matching how the section will actually be built. Any part beyond
        // buildableLen (arc distance) is drawn red — the over-grade tail that
        // gets truncated on click (pass +inf to colour the whole edge buildable).
        void DrawGradedRails(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float yA, float yB, float lift, float buildableLen)
        {
            const int N = 24;
            float halfG = Gauge * 0.5f;
            Vector3 pc = default, pl = default, pr = default;
            Vector2 prevXz = p0;
            float arc = 0f;
            for (int i = 0; i <= N; i++)
            {
                float u = i / (float)N;
                Vector2 xz = LineGraph.Bezier(p0, p1, p2, p3, u);
                if (i > 0) arc += Vector2.Distance(prevXz, xz);
                prevXz = xz;
                Vector2 t = LineGraph.BezierTangent(p0, p1, p2, p3, u);
                Vector2 perp = t.sqrMagnitude > 1e-6f
                    ? new Vector2(-t.y, t.x).normalized * halfG : Vector2.zero;
                float y = Mathf.Lerp(yA, yB, u) + lift;
                Vector3 c = new Vector3(xz.x, y, xz.y);
                Vector3 l = new Vector3(xz.x + perp.x, y, xz.y + perp.y);
                Vector3 r = new Vector3(xz.x - perp.x, y, xz.y - perp.y);
                if (i > 0)
                {
                    bool red = arc > buildableLen;   // segment ends past the buildable point
                    AddSeg(pc, c, red); AddSeg(pl, l, red); AddSeg(pr, r, red);
                }
                pc = c; pl = l; pr = r;
            }
        }

        // Scan the pending edge's terrain profile, update the grade/buildable
        // preview hint fields, and return the buildable arc length to colour the
        // rails with (+inf = whole edge buildable, e.g. when override is on).
        float PreviewScan(TerrainField field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            bool blocked = ScanBuildable(field, p0, p1, p2, p3,
                out float bl, out float bt, out float tot, out float worst);
            LastPreviewGradeDeg = worst;
            LastPreviewBuildableLen = bl;
            LastPreviewTotalLen = tot;
            if (OverrideGrade)
            {
                LastPreviewTruncated = false;
                LastPreviewTooSteep = false;
                return float.PositiveInfinity;   // span it all; fills become bridges
            }
            LastPreviewTruncated = blocked;
            LastPreviewTooSteep = blocked;
            return blocked ? bl : float.PositiveInfinity;
        }

        void EmitDashed(TerrainField field, Vector2 a, Vector2 b, Vector2 offset, float lift, bool red = false)
        {
            Vector2 a2 = a + offset, b2 = b + offset;
            Vector3 start = new Vector3(a2.x, 0f, a2.y);
            Vector3 d = new Vector3(b2.x - a2.x, 0f, b2.y - a2.y);
            float len = d.magnitude;
            if (len < 1e-4f) return;
            Vector3 dir = d / len;
            const float dash = 1.0f, gap = 0.6f, period = dash + gap;
            for (float pos = 0f; pos < len; pos += period)
            {
                float e0 = pos, e1 = Mathf.Min(pos + dash, len);
                Vector3 p0 = start + dir * e0, p1 = start + dir * e1;
                if (field != null)
                {
                    p0.y = field.SampleHeight(p0.x, p0.z) + lift;
                    p1.y = field.SampleHeight(p1.x, p1.z) + lift;
                }
                AddSeg(p0, p1, red);
            }
        }

        // A curve start->bend->end is buildable if its radius meets the design speed (and
        // braking radius, if any). No deflection floor: straight-ahead (0 deflection) is a
        // straight line — radius ∞ — so it's buildable too, keeping the yellow arc SOLID
        // through the middle instead of leaving a red sliver at the centre.
        bool CurveBuildable(Vector2 start, Vector2 bend, Vector2 end)
        {
            Vector2 inD = bend - start, outD = end - bend;
            if (inD.sqrMagnitude < 1e-6f || outD.sqrMagnitude < 1e-6f) return false;
            CurveControls(start, end, bend, out Vector2 c1, out Vector2 c2);
            if (MinCurveRadius(start, c1, c2, end) < MinRadiusForSpeed) return false;            // too tight
            if (PreviewBrakeValid && WorstBrakingRadiusViolation(start, c1, c2, end, PreviewBrakeVIn, SpeedLimitKmh, PreviewBrakeArcCovered) > 0f) return false;
            return true;
        }

        // Leg length at which a θmin-deflection symmetric curve first meets the min radius
        // for the speed — below it, no turn off this bend is a buildable real curve.
        float MinFirstLegForSpeed()
        {
            float th = Mathf.Max(0.1f, MinCurveDeflectionDeg) * Mathf.Deg2Rad;
            Vector2 end = new Vector2(1f + Mathf.Cos(th), Mathf.Sin(th)); // unit symmetric, deflect by θmin
            CurveControls(Vector2.zero, end, new Vector2(1f, 0f), out Vector2 c1, out Vector2 c2);
            float k = MinCurveRadius(Vector2.zero, c1, c2, end); // radius per unit leg
            return k > 1e-3f ? MinRadiusForSpeed / k : 0f;
        }

        // A small red draped ring (preview red submesh) marking the min-leg target.
        void DrawTargetMarker(TerrainField field, Vector2 c, float lift)
        {
            const int n = 18; const float r = 5f;
            Vector3 prev = default;
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                float x = c.x + Mathf.Cos(a) * r, z = c.y + Mathf.Sin(a) * r;
                Vector3 w = new Vector3(x, (field != null ? field.SampleHeight(x, z) : 0f) + lift, z);
                if (i > 0) AddSeg(prev, w, true);
                prev = w;
            }
        }

        void AddSeg(Vector3 a, Vector3 b) => AddSeg(a, b, false);

        // red = route into submesh 1 (the over-grade / un-buildable tail).
        void AddSeg(Vector3 a, Vector3 b, bool red)
        {
            int s = _pvVerts.Count;
            _pvVerts.Add(a); _pvVerts.Add(b);
            List<int> idx = red ? _pvIdxRed : _pvIdx;
            idx.Add(s); idx.Add(s + 1);
        }

        void EnsurePreview()
        {
            if (_pvMf != null) return;
            _pvGo = new GameObject(RootName + "_Preview") { hideFlags = HideFlags.DontSave };
            _pvMf = _pvGo.AddComponent<MeshFilter>();
            _pvMr = _pvGo.AddComponent<MeshRenderer>();
            _pvMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _pvMr.receiveShadows = false;
            _pvMesh = new Mesh { name = "RailPreviewMesh" };
            _pvMf.sharedMesh = _pvMesh;
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            Color col = new Color(1f, 0.8f, 0.3f, 1f);
            Color red = new Color(1f, 0.25f, 0.2f, 1f);
            _pvMat = sh != null
                ? new Material(sh) { name = "RailPreviewMat", color = col }
                : NetworkDesigner.PipelineMaterials.CreateUnlitColor(col, "RailPreviewMat");
            _pvMatRed = sh != null
                ? new Material(sh) { name = "RailPreviewMatRed", color = red }
                : NetworkDesigner.PipelineMaterials.CreateUnlitColor(red, "RailPreviewMatRed");
            // Submesh 0 = buildable (amber), submesh 1 = over-grade tail (red).
            _pvMr.sharedMaterials = new[] { _pvMat, _pvMatRed };
        }

        // ---- save / load (the graph; geometry regenerates on load) ----

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
            _chainTail = -1;
            if (save != null)
            {
                if (save.Nodes != null) _graph.Nodes.AddRange(save.Nodes);
                if (save.Edges != null) _graph.Edges.AddRange(save.Edges);
            }
        }
    }
}
