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
        // After drawing a segment, end the chain instead of continuing it — each lane-snapped segment is standalone, so
        // the next click re-snaps fresh (and the snap preview, gated on no-open-chain, returns immediately). To continue
        // a road, snap to the just-drawn segment's end lane nodes. (Multi-segment same-profile chains become the edge case.)
        public bool AutoEndChain = true;
        [Tooltip("Fallback width (m) for segments with no profile — corridor footprint either side of the centreline.")]
        public float RoadWidth = 14f;

        // 0 = built roads ride a straight A→B grade between node elevations (cut/fill the terrain to suit). 1 = the
        // road's mid-span hugs a smoothed terrain profile (endpoints stay pinned to the node grades). Blends between.
        [Tooltip("0 = straight grade between nodes (cut/fill). 1 = follow the terrain. Endpoints stay pinned to node grades.")]
        [Range(0f, 1f)] public float FollowTerrain = 0f;

        // Footprint width for a single segment: its profile's total cross-section, else the fallback width.
        public float EdgeWidth(LineEdge e) => ProfileCorridorWidth(e?.Profile);
        // The active profile's width (for the placement preview).
        public float ActiveWidth() => ProfileCorridorWidth(ActiveProfileId);

        // Full corridor footprint (lanes + shoulders + curbs + parapets/guardrails) for a profile, else the fallback width.
        float ProfileCorridorWidth(string id)
        {
            var prof = NetworkDesigner.Roads.RoadProfileLibrary.Resolve(id);
            return prof != null && prof.TotalWidth > 0.1f ? NetworkDesigner.Roads.RoadLayout.Width(prof) : RoadWidth;
        }
        [Tooltip("Guided drawing: a continuing straight locks to colinear; turns must be a speed-based curve, or a 90° corner where the speed allows. Off = freehand straights at any angle.")]
        public bool GuidedTurns = true;
        [Tooltip("At/below this design speed (km/h), a straight may snap a hard 90° corner; above it, turns must be curves.")]
        public float HardCornerMaxSpeedKmh = 50f;
        public bool AllowHardCorner => DesignSpeedKmh <= HardCornerMaxSpeedKmh;
        [System.NonSerialized] public bool StraightOffAxis;   // cursor isn't on an allowed heading → suppress the (kinked) click
        // Stop bars + crosswalks across each intersection approach. NonSerialized so the OFF default ALWAYS applies on
        // load — a stale `true` saved in the scene can't force them back on — while the palette still toggles them live.
        [System.NonSerialized] public bool ShowStopBars = false;
        [System.NonSerialized] public bool ShowCrosswalks = false;
        [Tooltip("Metres between draped samples along the curve.")]
        public float SampleStep = 2f;
        [Tooltip("Metres above the terrain (avoids z-fighting with the ground).")]
        public float Lift = 0.2f;
        [Tooltip("Metres between the cross-ties drawn across the corridor.")]
        public float TieSpacing = 8f;
        // ── excavation: cut a smoothed, slightly-sunken roadbed along the plan ──
        [Tooltip("Excavate the roadbed this far (m) below the node-to-node grade line — a consistent flat-bottomed corridor cut.")]
        public float ExcavationDepth = 0.75f;
        [Tooltip("Extra flat corridor (m) excavated BEYOND the road's footprint on EACH side — shoulder space for deeper cuts / larger fills.")]
        public float ExcavationMargin = 5f;
        [Tooltip("Metres between graded-bed samples along the corridor when excavating.")]
        public float GradeSampleStep = 2f;
        [Tooltip("Width (m) of the feathered batter BEYOND the corridor edge. 0 = vertical walls at the corridor edge (the cut is exactly the corridor width).")]
        public float CutFeather = 0f;
        [Tooltip("CUT side-slope ratio 1:N (1 vertical : N horizontal). The excavation ramps the ground at this slope beyond the flat bed until it DAYLIGHTS into the existing terrain — no floating shelf / cliff. Bigger = gentler, wider earthwork.")]
        public float CutBatter = 2f;
        [Tooltip("FILL (embankment) side-slope ratio 1:N — separate from the cut side so downhill embankments can be held steeper. Bigger = gentler / wider fill.")]
        public float FillBatter = 2f;
        [Tooltip("How far (m) a FILL embankment may spread out from the corridor before it stops; past this the natural ground is left as a drop-off instead of building out endlessly down a slope / cliff.")]
        public float FillReach = 120f;
        public Color PlanColor = new Color(1f, 0.55f, 0.12f, 0.95f);   // amber-orange (rail plan is yellow)
        // Guide/snap controls — shared by all plan tools via PlanGuides (tune in the Guides palette).
        public float NodePickRadius { get => PlanGuides.NodePickRadius; set => PlanGuides.NodePickRadius = value; }
        public float ExtensionSnapRadius { get => PlanGuides.ExtensionSnapRadius; set => PlanGuides.ExtensionSnapRadius = value; }
        public float EndSnapRadius { get => PlanGuides.EndSnapRadius; set => PlanGuides.EndSnapRadius = value; }
        public float ExtensionGuideLength { get => PlanGuides.ExtensionGuideLength; set => PlanGuides.ExtensionGuideLength = value; }
        public float GuideRange { get => PlanGuides.GuideRange; set => PlanGuides.GuideRange = value; }
        public float GuideSnapRadius { get => PlanGuides.GuideSnapRadius; set => PlanGuides.GuideSnapRadius = value; }
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
        // While dragging a STRAIGHT extension off the chain tail (also the first leg while arming a curve): the live
        // tail→cursor span, for the on-screen distance label.
        [System.NonSerialized] public bool PreviewStraightActive;
        [System.NonSerialized] public Vector2 PreviewStraightFrom, PreviewStraightTo;
        [System.NonSerialized] public float PreviewStraightDist;
        public bool CornerPending => _cornerPending;                           // a bend is armed, awaiting the end click
        public void CancelCorner() { _cornerPending = false; }                 // right-click backs out of the armed bend
        Vector2 _corner; bool _cornerPending;

        // ---- runtime (not serialized) ----
        LineGraph _graph = new LineGraph();
        public LineGraph Graph => _graph ??= new LineGraph();
        int _chainTail = -1;
        const float MinSegLenSq = 0.25f;   // (0.5 m)² — reject degenerate near-zero-length segments (coincident endpoints
                                           // make a 0-length road → negative-length body, corrupt junction, giant collider tris)
        // True only between starting a FRESH chain (a brand-new start node with no edge yet) and drawing its first
        // edge. PruneOrphanNodes exempts the chain tail ONLY while this holds — so a fresh start isn't pruned if you
        // delete elsewhere mid-draw, but a tail left edgeless by deleting its segment IS pruned (no orphan).
        bool _freshStartTail;

        GameObject _root; MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; Material _mat;
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<int> _idx = new List<int>();
        readonly List<Color32> _col = new List<Color32>();   // per-vertex lane-marking colours
        // Node pucks ride a SEPARATE 3D mesh (lit-transparent) so they carry their own colour + height + toggle.
        GameObject _nodeGo; MeshFilter _nodeMf; MeshRenderer _nodeMr; Mesh _nodeMesh; Material _nodeMat;
        readonly List<Vector3> _nv = new List<Vector3>();
        readonly List<Vector3> _nn = new List<Vector3>();
        readonly List<int> _nidx = new List<int>();
        // PROTOTYPE: lane-node pucks — one small blue puck per TRAFFIC lane at each segment end (read-only for now).
        // A separate mesh so they carry their own colour + size, parallel to the node-puck mesh.
        GameObject _laneGo; MeshFilter _laneMf; MeshRenderer _laneMr; Mesh _laneMesh; Material _laneMat;
        readonly List<Vector3> _lnv = new List<Vector3>();
        readonly List<Vector3> _lnn = new List<Vector3>();
        readonly List<int> _lnidx = new List<int>();
        static readonly Color _RoadLaneNodeColor = new Color(0.25f, 0.55f, 1f, 0.5f);   // blue, semi-transparent (subordinate to the corridor node pucks)
        // Per-lane handle record: which edge/end/lane it is + its world XZ and elevation (for screen-pick + hover).
        public struct LaneNode { public int Edge; public int End; public int Lane; public Vector2 Pos; public float Y; }
        readonly List<LaneNode> _laneNodes = new List<LaneNode>();
        public System.Collections.Generic.IReadOnlyList<LaneNode> LaneNodes => _laneNodes;
        [System.NonSerialized] LaneAttach _pendingAttach;     // attach to stamp on the first edge of a lane-snapped chain
        [System.NonSerialized] int _pendingAttachNode = -1;   // the lane-snapped start node carrying _pendingAttach
        // Segment-node pucks are PERMANENTLY hidden in plain (lane) mode — the lane nodes are the only handles. They
        // reappear only in special edit modes (elevation/excavate/build/setback/class/bridge) and, later, when a
        // specific intersection is opened in "Intersection mode". _segmentPuckNodes (the "treated" junctions) is kept
        // for intersection-click detection. A node with no lane nodes (non-corridor) still shows so it's grabbable.
        readonly HashSet<int> _segmentPuckNodes = new HashSet<int>();     // junctions (corners/transitions/3+ way) — used to detect intersections
        readonly HashSet<int> _nodesWithLaneNodes = new HashSet<int>();   // nodes that have derived lane nodes
        bool ShowAllSegmentPucks => ElevationEditMode || ExcavateSelectMode || BuildSegmentMode || BridgeSelectMode || SetbackEditMode || ClassEditMode;
        // True when node i's segment puck is hidden because its lanes are the handles (any lane-bearing node, plain mode).
        public bool SegmentNodeHidden(int i) => !ShowAllSegmentPucks && _nodesWithLaneNodes.Contains(i);
        // Lane-node hover overlay (golden, scaled — mirrors the corridor node hover).
        GameObject _laneHoverGo; MeshFilter _laneHoverMf; MeshRenderer _laneHoverMr; Mesh _laneHoverMesh; Material _laneHoverMat;
        readonly List<Vector3> _lhv = new List<Vector3>();
        readonly List<Vector3> _lhn = new List<Vector3>();
        readonly List<int> _lhidx = new List<int>();
        [System.NonSerialized] int _hoverLane = -1;
        public int HoverLane => _hoverLane;
        public LaneNode? HoveredLaneNode => (_hoverLane >= 0 && _hoverLane < _laneNodes.Count) ? _laneNodes[_hoverLane] : (LaneNode?)null;
        // PROXIMITY REVEAL: lane pucks render only for the corridor node nearest the cursor (set per frame). The
        // _laneNodes RECORDS are all built each Rebuild (for picking); the rendered MESH shows only this node's lanes.
        [System.NonSerialized] int _revealNode = -1;
        // Lane SELECTION: a CONTIGUOUS lane range on one (edge,end). Stored by identity so it survives rebuilds.
        [System.NonSerialized] int _selEdge = -1, _selEnd = -1, _selLo = -1, _selHi = -1;
        public bool HasLaneSelection => _selEdge >= 0;
        public int SelEdge => _selEdge; public int SelEnd => _selEnd; public int SelLaneLo => _selLo; public int SelLaneHi => _selHi;
        GameObject _laneSelGo; MeshFilter _laneSelMf; MeshRenderer _laneSelMr; Mesh _laneSelMesh; Material _laneSelMat;
        readonly List<Vector3> _lsv = new List<Vector3>();
        readonly List<Vector3> _lsn = new List<Vector3>();
        readonly List<int> _lsidx = new List<int>();
        static readonly Color _RoadLaneSelColor = new Color(0.45f, 0.5f, 1f, 0.5f);   // blue = cursor lane-node preview / snap indication (~50% alpha)
        // Hovered-node highlight: a SEPARATE per-frame overlay (golden, scaled) rebuilt only when the hover changes.
        GameObject _hoverGo; MeshFilter _hoverMf; MeshRenderer _hoverMr; Mesh _hoverMesh; Material _hoverMat;
        readonly List<Vector3> _hv = new List<Vector3>();
        readonly List<Vector3> _hn = new List<Vector3>();
        readonly List<int> _hidx = new List<int>();
        [System.NonSerialized] int _hoverNode = -1;
        public int HoverNode => _hoverNode;
        // Open-chain TAIL highlight (bright green): so an in-progress chain is never invisible — right-click finishes it.
        GameObject _tailGo; MeshFilter _tailMf; MeshRenderer _tailMr; Mesh _tailMesh; Material _tailMat;
        readonly List<Vector3> _tv = new List<Vector3>();
        readonly List<Vector3> _tn = new List<Vector3>();
        readonly List<int> _tidx = new List<int>();
        [System.NonSerialized] int _tailShown = -1;
        [System.NonSerialized] Vector2 _tailPosShown;
        static readonly Color _RoadTailColor = new Color(0.20f, 1f, 0.45f, 0.9f);   // vivid green, distinct from the golden hover + red base pucks
        public bool HasOpenChain => _chainTail >= 0;
        [System.NonSerialized] bool _linesHidden;   // user's manual "Plan lines" toggle (hides the overlay while in the Road palette)
        // The road plan overlay (line markings, node pucks, hover/tail/preview/guides) shows ONLY while the Road
        // palette is the active palette; everywhere else it's force-hidden. Independent of the manual toggle above so
        // returning to the Road palette restores whatever the user last chose. Starts hidden until the palette opens.
        [System.NonSerialized] bool _paletteActive;
        public bool PlanLinesHidden => _linesHidden;
        bool LinesVisible => !_linesHidden && _paletteActive;   // effective visibility = manual ON *and* palette active
        public void TogglePlanLines() => SetPlanLinesVisible(_linesHidden);
        public void SetPlanLinesVisible(bool visible) { _linesHidden = !visible; ApplyVisibility(); }
        // Driven by RoadPalette open/close: gate every road-plan visual on the Road palette being the active one.
        public void SetPaletteActive(bool active)
        {
            _paletteActive = active;
            ApplyVisibility();
            if (!active) { HidePreview(); HideConnectPreview(); }   // also drop any transient add-node cursor / guides
        }
        void ApplyVisibility()
        {
            bool vis = LinesVisible;
            if (_mr != null) _mr.enabled = vis || (_paletteActive && (SetbackEditMode || ClassEditMode));
            if (_nodeMr != null) _nodeMr.enabled = PlanGuides.ShowNodes && vis && Graph != null && Graph.Nodes.Count > 0;
            if (_laneMr != null) _laneMr.enabled = PlanGuides.ShowNodes && vis && _lnv.Count > 0;   // PROTOTYPE lane pucks
            if (_hoverMr != null && !vis) _hoverMr.enabled = false;
            if (_laneHoverMr != null && !vis) _laneHoverMr.enabled = false;
            if (_laneSelMr != null) _laneSelMr.enabled = PlanGuides.ShowNodes && vis && _lsv.Count > 0;
            if (_tailMr != null && !vis) _tailMr.enabled = false;
        }
        static readonly Color _RoadNodeHoverColor = new Color(1f, 0.85f, 0.3f, 0.85f);   // golden, matches rail pucks

        GameObject _pvGo; MeshFilter _pvMf; MeshRenderer _pvMr; Mesh _pvMesh; Material _pvBadMat;
        readonly List<Vector3> _pv = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();       // submesh 0: normal (plan colour)
        readonly List<int> _pvBadIdx = new List<int>();    // submesh 1: too-tight curve (red)

        // Dedicated reactive-guide mesh: thick, BRIGHT flat dashed ribbons (vs the thin 1px preview lines), with
        // their own materials — submesh 0 = available (bright amber), 1 = active/snapped (bright red).
        GameObject _gGo; MeshFilter _gMf; MeshRenderer _gMr; Mesh _gMesh;
        readonly List<Vector3> _gv = new List<Vector3>();
        readonly List<int> _gTriA = new List<int>();       // available (amber)
        readonly List<int> _gTriR = new List<int>();       // active / snapped (red)
        const float GuideWidth = 0.6f;                     // ribbon width (m) — "a little thicker" than 1px lines

        // Equal-leg "PAC" ring (vertex-coloured yellow=buildable / red=too-tight) + its 15° tick labels.
        GameObject _symGo; MeshFilter _symMf; MeshRenderer _symMr; Mesh _symMesh; Material _symMat;
        readonly List<Vector3> _symV = new List<Vector3>();
        readonly List<int> _symIdx = new List<int>();
        readonly List<Color32> _symCol = new List<Color32>();
        [System.NonSerialized] public readonly List<Vector3> CurveTickWorld = new List<Vector3>();
        [System.NonSerialized] public readonly List<int> CurveTickDeg = new List<int>();

        const int SubSteps = 48;
        static readonly Vector2[] _pts = new Vector2[SubSteps + 1];
        float _tStart = 0f, _tEnd = 1f;   // current edge's trim range (markings drawn over [tStart,tEnd])

        string RootName => "RoadPlan_" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- editing (chain drawing) ----

        public void AddNode(ITerrainSurface field, Vector3 hit)
        {
            _selEdges.Clear();   // drawing changes edge indices (append/split) → drop the stale action-queue selection
            Vector2 p = new Vector2(hit.x, hit.z);
            if (_chainTail < 0)   // start a chain: grab an existing node/edge so corridors branch + join
            {
                _pendingAttach = null; _pendingAttachNode = -1;
                // LANE-LEVEL start: with an N-lane profile, snap the start onto N contiguous lane nodes of an existing
                // road end (1-lane → 1 node, 2-lane → 2 contiguous), so the new road extends those specific lanes.
                if (TrySnapStartToLaneGroup(p, out int laneStart, out LaneAttach att))
                { _chainTail = laneStart; _pendingAttach = att; _pendingAttachNode = laneStart; _freshStartTail = true; _cornerPending = false; return; }
                // The screen-picked hovered node is parallax-proof (the elevated puck vs the terrain hit below it),
                // so prefer it over the world-radius pick — otherwise clicking a node drops a duplicate beside it.
                int near = (_hoverNode >= 0 && _hoverNode < Graph.Nodes.Count) ? _hoverNode : Graph.NearestNode(p, NodePickRadius);
                if (near >= 0 && SegmentNodeHidden(near)) near = -1;   // hidden (lane-handled) end → don't start on its corridor node
                if (near >= 0) { _chainTail = near; _freshStartTail = false; }                  // existing node → has edges
                else if (NearestRoadEdge(p, out int ei, out float tt)) { _chainTail = Graph.SplitEdge(ei, tt); _freshStartTail = false; Rebuild(field); }  // split → has edges
                else { _chainTail = Graph.AddNode(p); _freshStartTail = true; }                  // brand-new start node: keep until its first edge
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
                if ((p - Graph.Nodes[_chainTail]).sqrMagnitude < MinSegLenSq) return;   // end ≈ tail → degenerate curve, ignore
                int endc = NearestOrNew(p);
                if (endc == _chainTail || (Graph.Nodes[endc] - Graph.Nodes[_chainTail]).sqrMagnitude < MinSegLenSq) return;
                AddCurvedEdge(_chainTail, endc, _corner);
                _chainTail = AutoEndChain ? -1 : endc; _cornerPending = false; _freshStartTail = false;
                Rebuild(field);
                return;
            }
            if (CurveModifier) { _corner = p; _cornerPending = true; return; }   // arm the bend; the next click is the end
            int start = _chainTail;
            // Reject (with feedback, not silently) a degenerate near-zero-length segment. With the tail-snap guard in
            // TrySnapToGuides this should be rare; if it fires, the click landed essentially on the tail.
            if ((p - Graph.Nodes[start]).sqrMagnitude < MinSegLenSq)
            { Debug.Log("[Road] segment ignored: endpoint is on the start node (too short / snapped back to it)."); return; }
            int end = NearestOrNew(p);   // join an existing node → a real intersection
            if (end == start || (Graph.Nodes[end] - Graph.Nodes[start]).sqrMagnitude < MinSegLenSq)
            { Debug.Log("[Road] segment ignored: endpoints coincide (degenerate edge)."); return; }
            if (Graph.AddEdge(start, end))
            {
                LineEdge ne = Graph.Edges[Graph.Edges.Count - 1];
                ne.Profile = ActiveProfileId;   // tag the new segment
                if (_pendingAttach != null && start == _pendingAttachNode)   // first edge of a lane-snapped chain → carry the attach
                { ne.Attach = _pendingAttach; _pendingAttach = null; _pendingAttachNode = -1; }
                SplitSegmentCrossings(start, end, ActiveProfileId);             // drawn OVER existing roads → make intersection nodes
                if (AutoBridge) TryAutoBridge(field, start, end, ActiveProfileId);   // dip under the new straight span → bridge it
            }
            else Debug.Log("[Road] segment already exists between those nodes — extending the chain from there.");
            _chainTail = AutoEndChain ? -1 : end; _freshStartTail = false;   // end the chain (standalone segment) unless chaining is on
            Rebuild(field);
        }

        // If the straight segment just drawn from `start`→`end` crosses a terrain DIP (a gorge/valley), split it into
        // approach / BRIDGE / approach: insert two nodes BridgeApproachPad metres outside each rim and flag the middle
        // span as a bridge (rendered un-draped at deck grade, built on piers, never excavated). Skips curves and
        // segments split by crossings (the direct start→end edge no longer exists), and segments too short to span.
        void TryAutoBridge(ITerrainSurface field, int start, int end, string profile)
        {
            if (field == null || start < 0 || end < 0) return;
            int ei = -1;
            for (int i = 0; i < Graph.Edges.Count; i++)
            { LineEdge ed = Graph.Edges[i]; if (!ed.HasCurve && ed.A == start && ed.B == end) { ei = i; break; } }
            if (ei < 0) return;

            Vector2 A = Graph.Nodes[start], B = Graph.Nodes[end];
            float len = Vector2.Distance(A, B);
            float pad = Mathf.Max(0f, BridgeApproachPad);
            if (len < 2f * pad + 12f) return;   // no room for two approaches + a real span
            Vector2 dir = (B - A) / len;
            float yA = field.SampleHeight(A.x, A.y), yB = field.SampleHeight(B.x, B.y);

            int n = Mathf.Clamp(Mathf.CeilToInt(len / 2f), 8, 600);   // ~2 m terrain sampling
            float DepthAt(int i) { float t = i / (float)n; Vector2 q = A + dir * (len * t); return Mathf.Lerp(yA, yB, t) - field.SampleHeight(q.x, q.y); }

            float maxDepth = 0f; int maxI = -1;
            for (int i = 0; i <= n; i++) { float d = DepthAt(i); if (d > maxDepth) { maxDepth = d; maxI = i; } }
            if (maxI < 0 || maxDepth < BridgeTriggerDepth) return;   // no real gorge under the span

            // Walk out from the deepest point to the rim crossings (terrain returns to within `rim` of the chord).
            float rim = Mathf.Max(0.5f, BridgeTriggerDepth * 0.2f);
            int lo = maxI, hi = maxI;
            while (lo > 0 && DepthAt(lo - 1) > rim) lo--;
            while (hi < n && DepthAt(hi + 1) > rim) hi++;

            float minGap = 4f;
            float dropDist = Mathf.Clamp(len * lo / n - pad, minGap, len - minGap);
            float riseDist = Mathf.Clamp(len * hi / n + pad, minGap, len - minGap);
            if (riseDist - dropDist < minGap) return;

            Vector2 P1 = A + dir * dropDist, P2 = A + dir * riseDist;
            int n1 = Graph.AddNode(P1); Graph.SetNodeY(n1, field.SampleHeight(P1.x, P1.y));
            int n2 = Graph.AddNode(P2); Graph.SetNodeY(n2, field.SampleHeight(P2.x, P2.y));
            if (float.IsNaN(Graph.GetNodeY(start))) Graph.SetNodeY(start, yA);   // pin approach grades to terrain
            if (float.IsNaN(Graph.GetNodeY(end))) Graph.SetNodeY(end, yB);

            // Rewire A→B into A→n1 (reuse the original edge), n1→n2 (BRIDGE), n2→B.
            LineEdge e = Graph.Edges[ei];
            e.B = n1;
            Graph.Edges.Add(new LineEdge(n1, n2) { Profile = profile, Bridge = true });
            Graph.Edges.Add(new LineEdge(n2, end) { Profile = profile });
        }

        // Snap to an existing road's CENTRELINE when the click lands anywhere within its corridor footprint
        // (not just within NodePickRadius of the thin centreline) — so you can continue a plan by clicking the
        // road itself. Searches out to the widest corridor, then accepts only if within the matched edge's reach.
        bool NearestRoadEdge(Vector2 p, out int ei, out float tt)
        {
            ei = -1; tt = 0f;
            if (Graph == null || Graph.Edges.Count == 0) return false;
            float maxHalf = NodePickRadius;
            for (int i = 0; i < Graph.Edges.Count; i++) maxHalf = Mathf.Max(maxHalf, EdgeWidth(Graph.Edges[i]) * 0.5f);
            if (!Graph.NearestPointOnEdge(p, maxHalf + NodePickRadius, out ei, out tt, out Vector2 pt)) return false;
            float reach = EdgeWidth(Graph.Edges[ei]) * 0.5f + NodePickRadius;
            return Vector2.Distance(p, pt) <= reach;
        }

        // After laying a STRAIGHT edge start→end, split it (and every road it crosses) at each interior
        // crossing, so a segment drawn over another road creates real shared intersection nodes.
        void SplitSegmentCrossings(int start, int end, string profile)
        {
            if (start < 0 || end < 0 || start >= Graph.Nodes.Count || end >= Graph.Nodes.Count) return;
            Vector2 A = Graph.Nodes[start], B = Graph.Nodes[end];
            var hits = new List<(float t, Vector2 pt, int c, int d)>();
            foreach (LineEdge e in Graph.Edges)
            {
                if (e.HasCurve) continue;                                           // straight-only for now
                if (e.A == start || e.A == end || e.B == start || e.B == end) continue;  // shares an endpoint (incl. the new edge)
                Vector2 C = Graph.Nodes[e.A], D = Graph.Nodes[e.B];
                if (SegSegIntersect(A, B, C, D, out float tAB, out float tCD, out Vector2 X)
                    && tAB > 1e-3f && tAB < 1f - 1e-3f && tCD > 1e-3f && tCD < 1f - 1e-3f)
                    hits.Add((tAB, X, e.A, e.B));
            }
            if (hits.Count == 0) return;
            hits.Sort((x, y) => x.t.CompareTo(y.t));

            // Drop the straight start→end edge; we'll re-lay it in pieces through the crossing nodes.
            for (int i = Graph.Edges.Count - 1; i >= 0; i--)
            { LineEdge e = Graph.Edges[i]; if ((e.A == start && e.B == end) || (e.A == end && e.B == start)) { Graph.RemoveEdgeAt(i); break; } }

            int prev = start;
            foreach (var h in hits)
            {
                int ei = FindEdgeIndex(h.c, h.d);   // node indices are stable (SplitEdge appends, RemoveEdgeAt only drops edges)
                int x;
                if (ei >= 0)
                {
                    Vector2 C = Graph.Nodes[h.c], D = Graph.Nodes[h.d];
                    float tt = ParamOnSeg(C, D, h.pt);
                    x = Graph.SplitEdge(ei, tt);
                }
                else x = Graph.AddNode(h.pt);
                ConnectStraight(prev, x, profile);
                prev = x;
            }
            ConnectStraight(prev, end, profile);
        }

        void ConnectStraight(int a, int b, string profile)
        {
            if (a == b) return;
            if (Graph.AddEdge(a, b)) Graph.Edges[Graph.Edges.Count - 1].Profile = profile;
        }

        int FindEdgeIndex(int a, int b)
        {
            for (int i = 0; i < Graph.Edges.Count; i++)
            { LineEdge e = Graph.Edges[i]; if ((e.A == a && e.B == b) || (e.A == b && e.B == a)) return i; }
            return -1;
        }

        static float ParamOnSeg(Vector2 a, Vector2 b, Vector2 p)
        {
            Vector2 ab = b - a; float d = ab.sqrMagnitude;
            return d < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / d);
        }

        // 2D segment intersection: true + params/point when AB and CD cross at an interior-or-endpoint point.
        static bool SegSegIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float tAB, out float tCD, out Vector2 pt)
        {
            tAB = tCD = 0f; pt = a;
            Vector2 r = b - a, s = d - c;
            float denom = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(denom) < 1e-9f) return false;   // parallel / collinear
            Vector2 ca = c - a;
            tAB = (ca.x * s.y - ca.y * s.x) / denom;
            tCD = (ca.x * r.y - ca.y * r.x) / denom;
            if (tAB < 0f || tAB > 1f || tCD < 0f || tCD > 1f) return false;
            pt = a + r * tAB;
            return true;
        }

        int NearestOrNew(Vector2 p)
        {
            // Prefer the screen-picked hovered node (parallax-proof) so a segment END snaps onto an existing node.
            if (_hoverNode >= 0 && _hoverNode < Graph.Nodes.Count && _hoverNode != _chainTail) return _hoverNode;
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
            if (!Graph.AddEdge(a, b)) return;   // rejected / edge already existed (dedup) — leave it straight
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

        // ── external curve-guide drive (the lane-edge model owns its own start/corner but reuses this layer's
        // design-speed curve math + equal-leg ring / 15° ticks / leg+angle+radius labels) ──
        [System.NonSerialized] public bool ExternalCurveGuide;   // gates the OnGUI tick/radius labels for an external curve

        // Snap an externally-owned curve END onto the equal-leg PAC ring + buildable arc + 15° ticks. Mirrors
        // TrySnapCurveSymmetry but takes start/bend explicitly (no dependence on this layer's chain state).
        public bool SnapExternalCurveEnd(Vector2 start, Vector2 bend, Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (CurveSymmetrySnap <= 0f) return false;
            float legA = Vector2.Distance(start, bend);
            if (legA < 0.5f) return false;
            if (!LimitCurveRadius)
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

        Vector2 _extRingStart, _extRingBend; bool _extRingBuilt;   // cache: the ring only depends on (start,bend), not the moving end

        // Drive the equal-leg ring + ticks + leg/angle/radius labels for an externally-owned armed curve (start→bend→end).
        public void ShowExternalCurveGuide(ITerrainSurface field, Vector2 start, Vector2 bend, Vector2 end)
        {
            // The ring + 15° ticks depend only on start+bend (NOT the moving end), so rebuild the (expensive 512-seg +
            // buildable-arc-scan) mesh only when the bend actually moves; otherwise just re-show the cached mesh.
            if (!_extRingBuilt || (start - _extRingStart).sqrMagnitude > 0.01f || (bend - _extRingBend).sqrMagnitude > 0.01f)
            {
                BuildSymRing(field, start, bend);
                _extRingStart = start; _extRingBend = bend; _extRingBuilt = true;
            }
            else if (_symMr != null) _symMr.enabled = true;   // HidePreview disabled it this frame — re-show without rebuilding
            CurveControls(start, end, bend, out Vector2 c1, out Vector2 c2);
            LastPreviewRadius = MinCurveRadius(start, c1, c2, end);
            LastPreviewTooTight = LimitCurveRadius && LastPreviewRadius < MinRadiusForSpeed;
            PreviewCurveActive = true; PreviewStraightActive = false;
            PreviewTail = start; PreviewCorner = bend; PreviewEnd = end;
            PreviewLegA = Vector2.Distance(start, bend);
            PreviewLegB = Vector2.Distance(bend, end);
            PreviewDeflectionDeg = Vector2.Angle(bend - start, end - bend);
            ExternalCurveGuide = true;
        }

        // Drive the min-leg target ring while an externally-owned bend is being positioned (before the corner click).
        public void ShowExternalBendGuide(ITerrainSurface field, Vector2 start, Vector2 cursor)
        {
            BuildMinLegGuide(field, start, cursor);
            PreviewCurveActive = false;
            LastPreviewRadius = float.PositiveInfinity;   // no armed curve yet → suppress the radius readout
            ExternalCurveGuide = true;
        }

        public void ClearExternalCurveGuide()
        {
            _extRingBuilt = false;   // next armed curve rebuilds the ring
            if (!ExternalCurveGuide) return;
            ExternalCurveGuide = false; PreviewCurveActive = false;
            LastPreviewRadius = float.PositiveInfinity;
            HideSymRing();
        }

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
            _selEdges.Clear();
            Rebuild(field);
        }

        public void RemoveLastNode(ITerrainSurface field)
        {
            int last = Graph.Nodes.Count - 1;
            if (last < 0) return;
            _selEdges.Clear();   // indices shift → drop selection
            Graph.Edges.RemoveAll(e => e.A == last || e.B == last);
            Graph.Nodes.RemoveAt(last);
            if (_chainTail >= Graph.Nodes.Count) _chainTail = -1;
            Rebuild(field);
        }

        public bool DeleteNearNode(ITerrainSurface field, Vector3 hit, float radius)
        {
            int n = Graph.NearestNode(new Vector2(hit.x, hit.z), radius);
            if (n < 0) return false;
            _selEdges.Clear();   // indices shift → drop selection
            Graph.RemoveNode(n);
            if (_chainTail == n) _chainTail = -1;
            else if (_chainTail > n) _chainTail--;
            _cornerPending = false;
            PruneOrphanNodes();   // drop the far end(s) of the deleted segment(s) if now edgeless
            Rebuild(field);
            return true;
        }

        public void DropOrphanNodes() => PruneOrphanNodes();   // public: drop nodes left edgeless after a segment delete

        // Drop degenerate near-zero-length edges (coincident endpoints — a graph artifact that makes 0-length roads:
        // invisible segments, negative-length bodies, corrupt junctions) plus any node they orphan. No-op during a
        // setback drag (it would shift the dragged edge index). Returns the count removed; caller should Rebuild if > 0.
        public int RemoveDegenerateEdges()
        {
            if (Graph == null || _sbEdge >= 0) return 0;
            int removed = 0;
            for (int i = Graph.Edges.Count - 1; i >= 0; i--)
            {
                LineEdge e = Graph.Edges[i];
                bool bad = e == null || e.A < 0 || e.B < 0 || e.A >= Graph.Nodes.Count || e.B >= Graph.Nodes.Count
                           || (Graph.Nodes[e.A] - Graph.Nodes[e.B]).sqrMagnitude < MinSegLenSq;
                if (bad) { Graph.RemoveEdgeAt(i); removed++; }
            }
            if (removed > 0) PruneOrphanNodes();
            return removed;
        }

        // Remove any node left with no edges (e.g. the far end of a just-deleted segment),
        // keeping the active chain tail (a fresh, not-yet-connected start node).
        void PruneOrphanNodes()
        {
            for (int i = Graph.Nodes.Count - 1; i >= 0; i--)
            {
                if ((i == _chainTail && _freshStartTail) || NodeHasEdge(i)) continue;   // keep a FRESH start; prune an edgeless orphaned tail
                Graph.RemoveNode(i);
                if (i == _chainTail) _chainTail = -1;          // we just pruned the (orphaned) tail
                else if (_chainTail > i) _chainTail--;
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

        // The collinear extension of an existing ENDPOINT (degree-1 node) near `p`, excluding the chain tail:
        // origin = that node, dir = its outward heading (continuing its single edge). This is the guide you snap
        // a new road onto so it meets the EXISTING road straight-on. False if none is in range / ahead.
        bool TargetExtension(Vector2 p, out Vector2 origin, out Vector2 dir)
        {
            origin = default; dir = default;
            if (Graph == null) return false;
            float reach = Mathf.Max(1f, ExtensionGuideLength);
            float bestPerp2 = float.MaxValue;
            for (int i = 0; i < Graph.Nodes.Count; i++)
            {
                if (i == _chainTail) continue;
                int deg = 0, nb = -1;
                for (int e = 0; e < Graph.Edges.Count; e++)
                { var le = Graph.Edges[e]; if (le.A == i) { deg++; nb = le.B; } else if (le.B == i) { deg++; nb = le.A; } }
                if (deg != 1 || nb < 0) continue;                    // only endpoints have a clean collinear extension
                Vector2 np = Graph.Nodes[i];
                Vector2 ext = np - Graph.Nodes[nb];
                if (ext.sqrMagnitude < 1e-6f) continue;
                ext.Normalize();
                float along = Vector2.Dot(p - np, ext);
                if (along <= 0f || along > reach) continue;          // must be ahead of the node, within guide length
                float perp2 = (p - (np + ext * along)).sqrMagnitude;
                if (perp2 < bestPerp2) { bestPerp2 = perp2; origin = np; dir = ext; }
            }
            return bestPerp2 < float.MaxValue;
        }

        // SOFT-snap the cursor onto a nearby existing endpoint's collinear extension (within ExtensionSnapRadius)
        // so the new road can connect straight-on to it.
        public bool TrySnapToTargetExtension(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            float r = Mathf.Max(0f, ExtensionSnapRadius);
            if (r <= 0f || !TargetExtension(cursor, out Vector2 o, out Vector2 dir)) return false;
            float along = Vector2.Dot(cursor - o, dir);
            Vector2 proj = o + dir * along;
            if ((cursor - proj).sqrMagnitude > r * r) return false;
            snapped = proj; return true;
        }

        // ── "Reactive" node guides (CAD-style smart guides) ─────────────────────────────────────────────
        // Every existing node near the cursor projects guide rays: a COLLINEAR front ray (continue its road out
        // the open end — endpoints only) plus two PERPENDICULAR side rays (±90° to its road). The placed node snaps
        // to any of these lines, and PREFERS a point where two guides CROSS, so a new segment lines up with / tees
        // square into the existing network. Excludes the chain tail. Replaces the single-target collinear/perp snaps.
        struct GuideRay { public Vector2 O, D; public float Len; public bool Tail; public bool Colinear; }   // Tail=true → ray off the node being drawn FROM (priority over centerpoints); Colinear=true → the straight continuation (the tail's is exempt from the snap-range gate so long straight roads still lock)
        readonly List<GuideRay> _guideRays = new List<GuideRay>();
        // Segment centerpoint snap targets (within GuideRange of the cursor): snapping onto Mid shows a red guide
        // along Perp (perpendicular to the segment) so a new road tees into the MIDDLE of a segment square-on.
        struct GuideMid { public Vector2 Mid, Perp; public bool Center; }   // Center=true → its perpendicular is also a snappable LINE (snap "beside" the segment, aligned to the midpoint)
        readonly List<GuideMid> _guideMids = new List<GuideMid>();

        public void CollectTargetGuides(Vector2 p)
        {
            _guideRays.Clear();
            _guideMids.Clear();
            if (Graph == null) return;
            // Per the guide spec: every existing node within GuideRange of the cursor (Guides palette "Guide range",
            // separate from the EndSnapRadius node-snap) projects guides off EACH incident edge — a colinear (the road's
            // outward continuation past the node) + BOTH perpendiculars. Works whether STARTING a new segment (no
            // chain yet) or extending; the chain tail (the drawn-off node) always projects. ResolveGuideSnap then
            // picks which guide the cursor hard-snaps to (rendered red); the rest render amber.
            float react = Mathf.Max(1f, GuideRange);   // node emits guides within this (separate from node-snap EndSnapRadius)
            float len = Mathf.Max(1f, ExtensionGuideLength);   // configurable guide length (Guides palette "Guide length") — colinear AND perpendicular
            float r2 = react * react;
            for (int i = 0; i < Graph.Nodes.Count; i++)
            {
                Vector2 np = Graph.Nodes[i];
                // The chain tail (the node we're drawing OFF of) ALWAYS projects its guides — so a degree-3 junction
                // gives colinear/perpendicular references off EACH of its existing edges for the new segment. Other
                // nodes only react within the proximity-snap radius of the cursor.
                if (i != _chainTail && (np - p).sqrMagnitude > r2) continue;
                for (int e = 0; e < Graph.Edges.Count; e++)
                {
                    LineEdge le = Graph.Edges[e];
                    if (le.A != i && le.B != i) continue;
                    Vector2 outw = EdgeOutwardAtNode(le, i);
                    if (outw.sqrMagnitude < 1e-6f) continue;
                    outw.Normalize();
                    Vector2 perp = new Vector2(-outw.y, outw.x);
                    bool tail = (i == _chainTail);   // guides off the node we're drawing FROM stay sticky over centerpoints
                    _guideRays.Add(new GuideRay { O = np, D = outw, Len = len, Tail = tail, Colinear = true });   // colinear extension (outward)
                    _guideRays.Add(new GuideRay { O = np, D = perp, Len = len, Tail = tail });    // perpendicular (both sides;
                    _guideRays.Add(new GuideRay { O = np, D = -perp, Len = len, Tail = tail });   // snap/red picks the cursor's side)
                    // Length-mirror snap: a point on the colinear EXTENSION at the SAME distance from the node as this
                    // edge's far end — so a new segment can mirror the existing colinear segment's length. Shows a red
                    // perpendicular when snapped (reuses the GuideMid mechanism, same as centerpoints).
                    int other = (le.A == i) ? le.B : le.A;
                    float segL = (np - Graph.Nodes[other]).magnitude;
                    if (segL > 0.5f) _guideMids.Add(new GuideMid { Mid = np + outw * segL, Perp = perp });
                }
            }

            // Segment CENTERPOINT snap targets (toggle: "Midpoint guides"): each edge whose midpoint is within GuideRange
            // of the cursor (excluding the chain's own incoming edge). Snapping onto a midpoint shows a red perpendicular.
            if (PlanGuides.MidpointGuides)
            for (int e = 0; e < Graph.Edges.Count; e++)
            {
                LineEdge le = Graph.Edges[e];
                if (le.A == _chainTail || le.B == _chainTail) continue;
                EdgeBezier(le, out Vector2 p0, out Vector2 q1, out Vector2 q2, out Vector2 p3);
                Vector2 mid = LineGraph.Bezier(p0, q1, q2, p3, 0.5f);
                if ((mid - p).sqrMagnitude > r2) continue;
                Vector2 tan = LineGraph.BezierTangent(p0, q1, q2, p3, 0.5f);
                if (tan.sqrMagnitude < 1e-6f) tan = p3 - p0;
                if (tan.sqrMagnitude < 1e-6f) continue;
                tan.Normalize();
                _guideMids.Add(new GuideMid { Mid = mid, Perp = new Vector2(-tan.y, tan.x), Center = true });
            }
        }

        // Continuation direction of edge `e` PAST node `i` (away from the road), curve-aware via the bezier tangent.
        Vector2 EdgeOutwardAtNode(LineEdge e, int i)
        {
            EdgeBezier(e, out Vector2 p0, out Vector2 q1, out Vector2 q2, out Vector2 p3);
            Vector2 tan = i == e.A ? -LineGraph.BezierTangent(p0, q1, q2, p3, 0f)
                                   :  LineGraph.BezierTangent(p0, q1, q2, p3, 1f);
            if (tan.sqrMagnitude < 1e-6f) tan = i == e.A ? p0 - p3 : p3 - p0;
            return tan;
        }

        // Draw every candidate guide as a thick, bright, flat dashed ribbon into the dedicated guide mesh: the one(s)
        // the cursor is hard-snapped to render RED (submesh 1), the rest amber (submesh 0) — spec: snapped=red, available=yellow.
        public void DrawTargetGuides(ITerrainSurface field, Vector2 cursor)
        {
            EnsureGuideMesh();
            _gv.Clear(); _gTriA.Clear(); _gTriR.Clear();
            ResolveGuideSnap(cursor, out _, out int aIdx, out int bIdx, out int midIdx);
            // Only DRAW a guide when the cursor is roughly IN LINE with it (within `show` of the line, alongside it),
            // so distant in-range nodes don't clutter the map. The snapped guide(s) always draw. Snapping itself still
            // considers every collected guide (ResolveGuideSnap above).
            float show2 = Mathf.Max(0.01f, GuideSnapRadius * 2f); show2 *= show2;
            for (int i = 0; i < _guideRays.Count; i++)
            {
                bool active = (i == aIdx || i == bIdx);
                if (!active)
                {
                    GuideRay gr = _guideRays[i];
                    float t = Vector2.Dot(cursor - gr.O, gr.D);
                    if (t < 0f || t > gr.Len) continue;                                 // cursor not alongside this ray
                    if ((cursor - (gr.O + gr.D * t)).sqrMagnitude > show2) continue;    // not in line with it
                }
                GuideRibbon(field, _guideRays[i].O, _guideRays[i].D, _guideRays[i].Len, active ? _gTriR : _gTriA);
            }
            // Amber: a segment centerpoint's perpendicular line, when the cursor is in-line beside the segment but not
            // (yet) the snapped guide — so the "snap beside the segment" line is discoverable, like the rays above.
            float midLen = Mathf.Max(1f, ExtensionGuideLength);
            for (int k = 0; k < _guideMids.Count; k++)
            {
                if (k == midIdx || !_guideMids[k].Center) continue;
                GuideMid m = _guideMids[k];
                float t = Vector2.Dot(cursor - m.Mid, m.Perp);
                if (t < -midLen || t > midLen) continue;
                if ((cursor - (m.Mid + m.Perp * t)).sqrMagnitude > show2) continue;   // not in line with it
                GuideRibbon(field, m.Mid, m.Perp, midLen, _gTriA);
                GuideRibbon(field, m.Mid, -m.Perp, midLen, _gTriA);
            }
            if (midIdx >= 0)   // snapped to a segment centerpoint → red perpendicular guide through it (both ways)
            {
                GuideMid m = _guideMids[midIdx];
                float len = Mathf.Max(1f, ExtensionGuideLength);
                GuideRibbon(field, m.Mid, m.Perp, len, _gTriR);
                GuideRibbon(field, m.Mid, -m.Perp, len, _gTriR);
            }
            _gMesh.Clear();
            _gMesh.SetVertices(_gv);
            _gMesh.subMeshCount = 2;
            _gMesh.SetTriangles(_gTriA, 0);
            _gMesh.SetTriangles(_gTriR, 1);
            _gMesh.RecalculateBounds();
            _gMr.enabled = _gv.Count > 0;
        }

        // A dashed flat ribbon (quads) of width GuideWidth from `o` along `dir` for `len`, draped on the terrain.
        void GuideRibbon(ITerrainSurface field, Vector2 o, Vector2 dir, float len, List<int> tris)
        {
            int gn = Mathf.Clamp(Mathf.CeilToInt(len / 1.2f), 4, 800);
            Vector2 perp = new Vector2(-dir.y, dir.x) * (GuideWidth * 0.5f);
            Vector3 up = Vector3.up * 0.05f;   // float clearly above the terrain so the bright ribbon reads
            for (int i = 0; i < gn; i += 2)    // dashed: draw even cells, skip odd (gap)
            {
                Vector2 a = o + dir * ((float)i / gn * len);
                Vector2 b = o + dir * ((float)(i + 1) / gn * len);
                Vector3 a0 = Drape(field, a - perp) + up, a1 = Drape(field, a + perp) + up;
                Vector3 b0 = Drape(field, b - perp) + up, b1 = Drape(field, b + perp) + up;
                int s = _gv.Count;
                _gv.Add(a0); _gv.Add(a1); _gv.Add(b1); _gv.Add(b0);
                tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);          // up
                tris.Add(s); tris.Add(s + 2); tris.Add(s + 1); tris.Add(s); tris.Add(s + 3); tris.Add(s + 2);          // down (2-sided)
            }
        }

        void EnsureGuideMesh()
        {
            if (_gMf != null) return;
            _gGo = new GameObject(RootName + "_Guides") { hideFlags = HideFlags.DontSave };
            _gMf = _gGo.AddComponent<MeshFilter>();
            _gMr = _gGo.AddComponent<MeshRenderer>();
            _gMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _gMr.receiveShadows = false;
            _gMesh = new Mesh { name = "RoadPlanGuideMesh" };
            _gMf.sharedMesh = _gMesh;
            _gMr.sharedMaterials = new[]
            {
                NetworkDesigner.PipelineMaterials.CreateUnlitColor(new Color(1f, 0.92f, 0.12f, 1f), "RoadGuideAmber"),  // available = bright yellow
                NetworkDesigner.PipelineMaterials.CreateUnlitColor(new Color(1f, 0.18f, 0.10f, 1f), "RoadGuideRed"),    // snapped = bright red
            };
        }

        // Resolve the cursor's snap against the current _guideRays: nearest guide-line INTERSECTION within
        // ExtensionSnapRadius, else nearest guide LINE. Reports the winning ray index(es) so the renderer can colour
        // them red (bIdx >= 0 only for an intersection snap). Does NOT recollect — caller collects first.
        bool ResolveGuideSnap(Vector2 cursor, out Vector2 point, out int aIdx, out int bIdx, out int midIdx)
        {
            point = cursor; aIdx = -1; bIdx = -1; midIdx = -1;
            float r = Mathf.Max(0f, GuideSnapRadius);   // "guide snap strength" — dedicated, separate from Colinear snap
            if (r <= 0f) return false;
            float r2 = r * r;
            float mlen = Mathf.Max(1f, ExtensionGuideLength);   // length of a centerpoint's perpendicular (midline) guide
            float range2 = Mathf.Max(1f, GuideRange); range2 *= range2;
            float best2; bool found;
            // A guide only SNAPS when its SOURCE is within Guide range of the cursor, so a far/stale source (e.g. an
            // abandoned chain tail clear across the map) can't grab the endpoint. The active tail's COLINEAR continuation
            // is exempt so a long straight road still locks along its whole length. (Non-tail nodes only emit within Guide
            // range anyway, so this only constrains the tail.)
            bool RaySnappable(GuideRay g) => (g.Tail && g.Colinear) || (cursor - g.O).sqrMagnitude <= range2;
            bool MidSnappable(GuideMid m) => (cursor - m.Mid).sqrMagnitude <= range2;

            // (1) Nearest guide-line CROSSING — most specific; snap to BOTH guides at their intersection. Covers ray ×
            // ray AND ray × midline (a colinear/perpendicular guide off a node crossing a segment-centerpoint perpendicular).
            best2 = r2; found = false;
            for (int a = 0; a < _guideRays.Count; a++)
                for (int b = a + 1; b < _guideRays.Count; b++)
                {
                    if (!RaySnappable(_guideRays[a]) || !RaySnappable(_guideRays[b])) continue;
                    if (RayCross(_guideRays[a], _guideRays[b], out Vector2 x))
                    {
                        float d2 = (cursor - x).sqrMagnitude;
                        if (d2 < best2) { best2 = d2; point = x; aIdx = a; bIdx = b; midIdx = -1; found = true; }
                    }
                }
            for (int i = 0; i < _guideRays.Count; i++)              // ray × centerpoint-perpendicular (midline) crossing
                for (int k = 0; k < _guideMids.Count; k++)
                {
                    if (!_guideMids[k].Center || !RaySnappable(_guideRays[i]) || !MidSnappable(_guideMids[k])) continue;
                    if (!RayMidCross(_guideRays[i], _guideMids[k].Mid, _guideMids[k].Perp, mlen, out Vector2 xm)) continue;
                    float d2 = (cursor - xm).sqrMagnitude;
                    if (d2 < best2) { best2 = d2; point = xm; aIdx = i; bIdx = -1; midIdx = k; found = true; }
                }
            if (found) return true;

            // (2) The chain-tail extension you're actively pulling ALONG keeps priority over loose centerpoints: if the
            // cursor is on a Tail ray, stay on it. (Fixes a passing centerpoint hijacking the cursor off the extension.)
            best2 = r2; found = false;
            for (int i = 0; i < _guideRays.Count; i++)
            {
                if (!_guideRays[i].Tail) continue;
                GuideRay g = _guideRays[i];
                if (!RaySnappable(g)) continue;                     // a stale tail's non-colinear guides stop reaching from afar
                float t = Vector2.Dot(cursor - g.O, g.D);
                if (t < 0f || t > g.Len) continue;                  // half-line from the node, within length
                Vector2 proj = g.O + g.D * t;
                float d2 = (cursor - proj).sqrMagnitude;
                if (d2 < best2) { best2 = d2; point = proj; aIdx = i; found = true; }
            }
            if (found) return true;

            // (3) Segment CENTERPOINT point snap.
            best2 = r2; found = false;
            for (int k = 0; k < _guideMids.Count; k++)
            {
                if (!MidSnappable(_guideMids[k])) continue;
                float d2 = (cursor - _guideMids[k].Mid).sqrMagnitude;
                if (d2 < best2) { best2 = d2; point = _guideMids[k].Mid; midIdx = k; found = true; }
            }
            if (found) return true;

            // (4) Else snap onto the nearest single guide LINE — any ray, plus the perpendicular line through a segment
            // centerpoint (both ways: snap "beside" the segment, aligned to the midpoint, not only right on it).
            best2 = r2; found = false;
            for (int i = 0; i < _guideRays.Count; i++)
            {
                GuideRay g = _guideRays[i];
                if (!RaySnappable(g)) continue;
                float t = Vector2.Dot(cursor - g.O, g.D);
                if (t < 0f || t > g.Len) continue;
                Vector2 proj = g.O + g.D * t;
                float d2 = (cursor - proj).sqrMagnitude;
                if (d2 < best2) { best2 = d2; point = proj; aIdx = i; bIdx = -1; midIdx = -1; found = true; }
            }
            for (int k = 0; k < _guideMids.Count; k++)
            {
                GuideMid m = _guideMids[k];
                if (!m.Center || !MidSnappable(m)) continue;
                float t = Vector2.Dot(cursor - m.Mid, m.Perp);   // bidirectional line through Mid ⟂ to the segment
                if (t < -mlen || t > mlen) continue;
                Vector2 proj = m.Mid + m.Perp * t;
                float d2 = (cursor - proj).sqrMagnitude;
                if (d2 < best2) { best2 = d2; point = proj; midIdx = k; aIdx = -1; bIdx = -1; found = true; }
            }
            return found;
        }

        // Crossing of two guide rays (both as half-lines within their Len); false if parallel or off either ray.
        static bool RayCross(GuideRay a, GuideRay b, out Vector2 x)
        {
            x = default;
            float det = a.D.x * (-b.D.y) - a.D.y * (-b.D.x);
            if (Mathf.Abs(det) < 1e-6f) return false;
            Vector2 diff = b.O - a.O;
            float ta = (diff.x * (-b.D.y) - diff.y * (-b.D.x)) / det;
            float tb = (a.D.x * diff.y - a.D.y * diff.x) / det;
            if (ta < 0f || ta > a.Len || tb < 0f || tb > b.Len) return false;
            x = a.O + a.D * ta; return true;
        }

        // Crossing of a guide ray (half-line [0,Len]) with a centerpoint's perpendicular MIDLINE (the bidirectional
        // line through `mid` along `perp`, bounded ±perpLen). False if parallel or the intersection is off either.
        static bool RayMidCross(GuideRay g, Vector2 mid, Vector2 perp, float perpLen, out Vector2 x)
        {
            x = default;
            float det = g.D.x * (-perp.y) - g.D.y * (-perp.x);
            if (Mathf.Abs(det) < 1e-6f) return false;
            Vector2 diff = mid - g.O;
            float ts = (diff.x * (-perp.y) - diff.y * (-perp.x)) / det;   // along the ray (half-line)
            float tu = (g.D.x * diff.y - g.D.y * diff.x) / det;           // along the perpendicular (bidirectional)
            if (ts < 0f || ts > g.Len) return false;
            if (tu < -perpLen || tu > perpLen) return false;
            x = g.O + g.D * ts; return true;
        }

        // Hard-snap the cursor to the nearest guide line / crossing (see ResolveGuideSnap).
        public bool TrySnapToGuides(Vector2 cursor, out Vector2 snapped)
        {
            CollectTargetGuides(cursor);
            if (!ResolveGuideSnap(cursor, out snapped, out _, out _, out _)) return false;
            // While extending a chain, never snap the new endpoint back onto (or within a segment-length of) the tail:
            // that collapses to a 0-length segment the draw code then silently rejects — the "can't start a segment off
            // a node" bug. Reject the snap so the raw cursor (where the user actually clicked) is used instead.
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count
                && (snapped - Graph.Nodes[_chainTail]).sqrMagnitude < MinSegLenSq) { snapped = cursor; return false; }
            return true;
        }

        // ════ Auto-connect (mirrors the rail auto-connect): pick node A and node B, and a tangent-matched fillet
        // — bend at the extension intersection P, equal legs, a straight auto-filling the longer side — joins them.
        public float ConnectHoverRadius = 30f;

        public struct ConnectResult
        {
            public bool Valid; public string Reason;
            public int NodeA, NodeB;
            public Vector2 Apos, Bpos, P, CurveStartA, CurveEndB;
            public float LegA, LegB, Leg, Radius;
            public bool NeedStraightA, NeedStraightB, HasP, DirectTangent;
        }

        // Shortest straight (m) the auto-connect will emit to fill a leg overhang; below this the curve just starts at
        // the node, so we don't litter the plan with sub-metre sliver edges that break setbacks/intersection logic.
        const float MinConnectStraight = 2f;

        public int NodeDegree(int n)
        {
            if (Graph == null || n < 0 || n >= Graph.Nodes.Count) return 0;
            int deg = 0;
            for (int i = 0; i < Graph.Edges.Count; i++) { LineEdge e = Graph.Edges[i]; if (e.A == n || e.B == n) deg++; }
            return deg;
        }
        public bool IsEndpoint(int n) => NodeDegree(n) == 1;

        // Outgoing tangent from node n along edge e (unit, pointing AWAY from n into open space).
        bool EdgeTangentAtNode(LineEdge e, int n, out Vector2 dir)
        {
            dir = Vector2.zero;
            if (e.A != n && e.B != n) return false;
            EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
            dir = (e.B == n) ? LineGraph.BezierTangent(p0, p1, p2, p3, 1f) : -LineGraph.BezierTangent(p0, p1, p2, p3, 0f);
            if (dir.sqrMagnitude < 1e-6f) dir = (e.B == n) ? (p3 - p0) : (p0 - p3);
            if (dir.sqrMagnitude < 1e-6f) return false;
            dir.Normalize(); return true;
        }

        static readonly System.Collections.Generic.List<Vector2> _connTangents = new System.Collections.Generic.List<Vector2>();
        // Heading the connect should leave/arrive along at node n. Endpoint → its outgoing heading; through-node
        // (degree ≥ 2) → the trunk axis (a near-opposite pair of incident edges), so a branch curves off the trunk.
        bool NodeTangent(int n, out Vector2 dir)
        {
            dir = Vector2.zero;
            int deg = NodeDegree(n);
            if (deg == 0) return false;
            if (deg == 1)
            {
                for (int i = 0; i < Graph.Edges.Count; i++) { LineEdge e = Graph.Edges[i]; if (e.A == n || e.B == n) return EdgeTangentAtNode(e, n, out dir); }
                return false;
            }
            _connTangents.Clear();
            for (int i = 0; i < Graph.Edges.Count; i++) { LineEdge e = Graph.Edges[i]; if ((e.A == n || e.B == n) && EdgeTangentAtNode(e, n, out Vector2 t)) _connTangents.Add(t); }
            for (int i = 0; i < _connTangents.Count; i++)
                for (int j = i + 1; j < _connTangents.Count; j++)
                    if (Vector2.Dot(_connTangents[i], _connTangents[j]) < -0.9f) { dir = _connTangents[i]; return true; }
            if (_connTangents.Count > 0) { dir = _connTangents[0]; return true; }
            return false;
        }

        static bool LineIntersect(Vector2 a, Vector2 da, Vector2 b, Vector2 db, out Vector2 p)
        {
            p = default;
            float denom = da.x * db.y - da.y * db.x;
            if (Mathf.Abs(denom) < 1e-9f) return false;               // parallel / collinear
            float t = ((b.x - a.x) * db.y - (b.y - a.y) * db.x) / denom;
            p = a + da * t; return true;
        }

        // Compute the fillet joining nodes a and b. Through-node involved → a single tangent curve between them;
        // else a symmetric fillet (equal legs, straight filling the longer side). Honours the design-speed radius.
        public bool TryConnectGeometry(int a, int b, out ConnectResult r)
        {
            r = new ConnectResult { NodeA = a, NodeB = b };
            if (Graph == null || a < 0 || b < 0 || a == b || a >= Graph.Nodes.Count || b >= Graph.Nodes.Count) { r.Reason = "pick two nodes"; return false; }
            int degA = NodeDegree(a), degB = NodeDegree(b);
            if (degA < 1 || degB < 1) { r.Reason = "pick two road nodes"; return false; }
            bool through = degA >= 2 || degB >= 2;
            r.Apos = Graph.Nodes[a]; r.Bpos = Graph.Nodes[b];
            if (!NodeTangent(a, out Vector2 extA) || !NodeTangent(b, out Vector2 extB)) { r.Reason = "no heading"; return false; }
            if (!LineIntersect(r.Apos, extA, r.Bpos, extB, out Vector2 P)) { r.Reason = "lines parallel"; return false; }
            if (degA == 1 && Vector2.Dot(P - r.Apos, extA) <= 0f) { r.Reason = "intersection behind A"; return false; }
            if (degB == 1 && Vector2.Dot(P - r.Bpos, extB) <= 0f) { r.Reason = "intersection behind B"; return false; }
            r.P = P; r.HasP = true;
            r.LegA = Vector2.Distance(r.Apos, P); r.LegB = Vector2.Distance(r.Bpos, P);
            if (Mathf.Max(r.LegA, r.LegB) > 4000f) { r.Reason = "ends too far apart"; return false; }
            Vector2 c1, c2;
            if (through)
            {
                r.DirectTangent = true; r.CurveStartA = r.Apos; r.CurveEndB = r.Bpos;
                CurveControls(r.Apos, r.Bpos, P, out c1, out c2);
                r.Radius = MinCurveRadius(r.Apos, c1, c2, r.Bpos);
            }
            else
            {
                r.Leg = Mathf.Min(r.LegA, r.LegB);
                r.CurveStartA = P - extA * r.Leg; r.CurveEndB = P - extB * r.Leg;
                // A straight only fills the LONGER leg's overhang. If that overhang is sub-MinConnectStraight, don't
                // emit a tiny sliver edge — start the curve at the node itself and let the (slightly asymmetric) fillet
                // absorb the small offset. Threshold (not 0.01 m) is what stopped the occasional micro-segment.
                r.NeedStraightA = r.LegA > r.Leg + MinConnectStraight;
                r.NeedStraightB = r.LegB > r.Leg + MinConnectStraight;
                if (!r.NeedStraightA) r.CurveStartA = r.Apos;
                if (!r.NeedStraightB) r.CurveEndB = r.Bpos;
                CurveControls(r.CurveStartA, r.CurveEndB, P, out c1, out c2);
                r.Radius = MinCurveRadius(r.CurveStartA, c1, c2, r.CurveEndB);
            }
            if (LimitCurveRadius && r.Radius < MinRadiusForSpeed) { r.Reason = $"too tight (R {r.Radius:0} < {MinRadiusForSpeed:0} m)"; return false; }
            r.Valid = true; r.Reason = "OK"; return true;
        }

        public void CommitConnect(ITerrainSurface field, in ConnectResult r)
        {
            if (!r.Valid) return;
            string prof = ActiveProfileId;
            if (r.DirectTangent)
            {
                Graph.AddEdge(r.NodeA, r.NodeB);
                int bei = FindEdgeIndex(r.NodeA, r.NodeB);
                if (bei >= 0) { LineEdge be = Graph.Edges[bei]; CurveControls(r.Apos, r.Bpos, r.P, out Vector2 bc1, out Vector2 bc2); be.HasCurve = true; be.ControlA = bc1; be.ControlB = bc2; be.Profile = prof; }
                Rebuild(field); return;
            }
            int sa = r.NodeA;
            if (r.NeedStraightA) { sa = Graph.AddNode(r.CurveStartA); Graph.AddEdge(r.NodeA, sa); int ei = FindEdgeIndex(r.NodeA, sa); if (ei >= 0) Graph.Edges[ei].Profile = prof; }
            int sb = r.NodeB;
            if (r.NeedStraightB) { sb = Graph.AddNode(r.CurveEndB); Graph.AddEdge(r.NodeB, sb); int ei = FindEdgeIndex(r.NodeB, sb); if (ei >= 0) Graph.Edges[ei].Profile = prof; }
            Graph.AddEdge(sa, sb);
            int cei = FindEdgeIndex(sa, sb);
            if (cei >= 0) { LineEdge ce = Graph.Edges[cei]; CurveControls(Graph.Nodes[sa], Graph.Nodes[sb], r.P, out Vector2 c1, out Vector2 c2); ce.HasCurve = true; ce.ControlA = c1; ce.ControlB = c2; ce.Profile = prof; }
            Rebuild(field);
        }

        // ---- connect preview overlay (vertex-coloured: green buildable / red not) ----
        GameObject _connGo; MeshFilter _connMf; MeshRenderer _connMr; Mesh _connMesh; Material _connMat;
        readonly System.Collections.Generic.List<Vector3> _connV = new System.Collections.Generic.List<Vector3>();
        readonly System.Collections.Generic.List<int> _connIdx = new System.Collections.Generic.List<int>();
        readonly System.Collections.Generic.List<Color32> _connCol = new System.Collections.Generic.List<Color32>();

        void EnsureConnectOverlay()
        {
            if (_connMf != null) return;
            _connGo = new GameObject(RootName + "_Connect") { hideFlags = HideFlags.DontSave };
            if (_root != null) _connGo.transform.SetParent(_root.transform, false);
            _connMf = _connGo.AddComponent<MeshFilter>();
            _connMr = _connGo.AddComponent<MeshRenderer>();
            _connMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _connMr.receiveShadows = false;
            _connMesh = new Mesh { name = "RoadConnectMesh" };
            _connMf.sharedMesh = _connMesh;
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            _connMat = sh != null ? new Material(sh) { name = "RoadConnectMat" } : NetworkDesigner.PipelineMaterials.CreateUnlitColor(Color.green, "RoadConnectMat");
            _connMr.sharedMaterial = _connMat;
        }

        public void HideConnectPreview() { if (_connMr != null) _connMr.enabled = false; }

        public void RenderConnectPreview(ITerrainSurface field, in ConnectResult r)
        {
            EnsureConnectOverlay();
            _connV.Clear(); _connIdx.Clear(); _connCol.Clear();
            Color32 col = r.Valid ? new Color32(60, 220, 90, 235) : new Color32(235, 70, 55, 235);
            const float lift = 0.4f;
            if (!r.HasP) ConnSeg(field, r.Apos, r.Bpos, lift, col);
            else
            {
                if (r.NeedStraightA) ConnSeg(field, r.Apos, r.CurveStartA, lift, col);
                CurveControls(r.CurveStartA, r.CurveEndB, r.P, out Vector2 c1, out Vector2 c2);
                Vector2 prev = r.CurveStartA; const int N = 24;
                for (int i = 1; i <= N; i++) { Vector2 cur = LineGraph.Bezier(r.CurveStartA, c1, c2, r.CurveEndB, i / (float)N); ConnSeg(field, prev, cur, lift, col); prev = cur; }
                if (r.NeedStraightB) ConnSeg(field, r.CurveEndB, r.Bpos, lift, col);
            }
            _connMesh.Clear(); _connMesh.SetVertices(_connV); _connMesh.SetColors(_connCol);
            _connMesh.SetIndices(_connIdx, MeshTopology.Lines, 0); _connMesh.RecalculateBounds();
            _connMr.enabled = true;
        }

        void ConnSeg(ITerrainSurface field, Vector2 a, Vector2 b, float lift, Color32 col)
        {
            int s = _connV.Count;
            _connV.Add(Drape(field, a) + Vector3.up * lift); _connV.Add(Drape(field, b) + Vector3.up * lift);
            _connCol.Add(col); _connCol.Add(col); _connIdx.Add(s); _connIdx.Add(s + 1);
        }

        // Snap onto the currently HOVERED node (the golden-highlighted puck) so snapping always matches what the
        // cursor is over — any node, toggle-independent. Excludes the active chain tail (else a continuing segment
        // would collapse back onto its own start).
        public bool TrySnapToHoverNode(out Vector2 snapped)
        {
            snapped = default;
            if (Graph == null || _hoverNode < 0 || _hoverNode >= Graph.Nodes.Count || _hoverNode == _chainTail) return false;
            snapped = Graph.Nodes[_hoverNode];
            return true;
        }

        // When STARTING a chain (no anchor yet), snap onto the nearest existing road END (a degree-1 node) within
        // EndSnapRadius so a new plan resumes cleanly off a built/laid road — independent of the proximity toggle.
        // Once grabbed, the next click continues from it exactly like any subsequent node (extension guide + locks).
        public bool TrySnapToRoadEnd(Vector2 p, out Vector2 snapped)
        {
            snapped = p;
            if (_chainTail >= 0 || Graph == null) return false;      // only at chain start
            float r = Mathf.Max(0f, EndSnapRadius);
            if (r <= 0f) return false;
            int best = -1; float bestSq = r * r;
            for (int i = 0; i < Graph.Nodes.Count; i++)
            {
                if (NodeDegree(i) != 1) continue;                    // road ends only
                float d = (Graph.Nodes[i] - p).sqrMagnitude;
                if (d <= bestSq) { bestSq = d; best = i; }
            }
            if (best >= 0) { snapped = Graph.Nodes[best]; return true; }
            return false;
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

        // For the off-axis commit gate: a DELIBERATE connection that lands on an existing node (or road edge) should be
        // allowed at ANY angle — the off-axis guard only exists to stop freehand kinks in OPEN space. Uses a generous
        // SCREEN-space node pick (zoom-robust, unlike the world-meters EndSnapRadius) + a world edge fallback. Excludes
        // the chain tail. Returns the join position.
        public bool TryOffAxisJoin(Camera cam, ITerrainSurface field, Vector2 screenPos, Vector2 cursorXZ, out Vector2 pos)
        {
            pos = cursorXZ;
            if (Graph == null) return false;
            int n = PickNodeScreen(cam, field, screenPos, 36f);
            if (n >= 0 && n != _chainTail) { pos = Graph.Nodes[n]; return true; }
            if (Graph.NearestPointOnEdge(cursorXZ, Mathf.Max(2f, NodePickRadius), out _, out _, out Vector2 pt))
            { pos = pt; return true; }
            return false;
        }

        // When EXTENDING straight toward an existing road, keep the colinear heading instead of just grabbing the
        // nearest road point: snap to where the tail's straight extension actually CROSSES the road. Fires only when
        // the cursor is near that crossing AND the extension line genuinely meets the road — otherwise the caller
        // falls back to the plain road-segment snap. Priority OVER TrySnapToOwnNode so the segment stays straight.
        public bool TrySnapExtensionToRoad(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (Graph == null || !PlanGuides.ProximitySnapOn) return false;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            float r = Mathf.Max(0f, EndSnapRadius);
            if (r <= 0f) return false;
            if (!IncomingDirection(cursor, out Vector2 dir)) return false;          // need a colinear heading to extend along
            if (!Graph.NearestPointOnEdge(cursor, r, out int ei, out _, out _)) return false;  // the road the cursor is over
            LineEdge e = Graph.Edges[ei];
            if (e.A == _chainTail || e.B == _chainTail) return false;               // not our own incoming edge
            Vector2 origin = Graph.Nodes[_chainTail];
            EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
            int K = 32; float bestPerp2 = float.MaxValue; Vector2 bestPt = cursor;
            for (int i = 0; i <= K; i++)
            {
                Vector2 q = LineGraph.Bezier(p0, p1, p2, p3, i / (float)K);
                float along = Vector2.Dot(q - origin, dir);
                if (along <= 0.1f || along > ExtensionGuideLength) continue;        // ahead of the tail, within the guide
                float perp2 = (q - (origin + dir * along)).sqrMagnitude;
                if (perp2 < bestPerp2) { bestPerp2 = perp2; bestPt = q; }
            }
            if (bestPerp2 >= float.MaxValue) return false;
            float tol = Mathf.Max(r, EdgeWidth(e) * 0.5f);
            if (bestPerp2 > tol * tol) return false;                                // extension line must actually meet the road
            if ((cursor - bestPt).sqrMagnitude > r * r) return false;              // and the cursor must be near the crossing
            snapped = bestPt; return true;
        }

        // Foot of the perpendicular dropped from the chain tail onto the nearest road near `probe`. `foot` is where a
        // 90° tee off the tail would land on that road (tail→foot ⟂ the road's tangent there). Drives the perpendicular
        // proximity guide + snap so a new road can meet an existing one square-on.
        public bool PerpendicularFoot(Vector2 probe, out Vector2 tail, out Vector2 foot)
        {
            tail = default; foot = default;
            if (Graph == null || _chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            float reach = Mathf.Max(1f, EndSnapRadius);
            if (!Graph.NearestPointOnEdge(probe, reach, out int ei, out float tt, out Vector2 np)) return false;
            LineEdge e = Graph.Edges[ei];
            if (e.A == _chainTail || e.B == _chainTail) return false;               // not our own incoming edge
            EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
            Vector2 t = LineGraph.BezierTangent(p0, p1, p2, p3, tt);
            if (t.sqrMagnitude < 1e-8f) t = p3 - p0;
            if (t.sqrMagnitude < 1e-8f) return false;
            t.Normalize();
            tail = Graph.Nodes[_chainTail];
            foot = np + t * Vector2.Dot(tail - np, t);                              // project tail onto the road's local line
            return true;
        }

        // Snap the cursor onto the perpendicular foot when it's nearby, so the new segment tees into the road at 90°.
        public bool TrySnapPerpendicularToRoad(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (!PlanGuides.ProximitySnapOn) return false;
            float r = Mathf.Max(0f, EndSnapRadius);
            if (r <= 0f || !PerpendicularFoot(cursor, out _, out Vector2 foot)) return false;
            if ((cursor - foot).sqrMagnitude > r * r) return false;
            snapped = foot; return true;
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

        // ---- excavation: per-edge smoothed, sunken roadbed targets ----

        // One bed per edge: the centreline sampled as (X, bedY, Z) targets — bedY = the NODE-TO-NODE grade
        // line (straight in elevation from node A's ground to node B's ground) dropped ExcavationDepth below
        // — plus that edge's flat half-width (its profile footprint). Both edges at a shared node anchor to
        // that node's DESIGN elevation, so adjacent segments meet flush. The caller carves each bed flat to
        // bedY. Node elevations come from DesignElevation (the per-node design height, captured from the
        // SHAPED surface and stored) so the cut respects terrain you carved/flattened and is idempotent.
        public void CollectExcavationBeds(ITerrainSurface field, List<(List<Vector3> pts, float flatHalf)> outBeds)
        {
            if (Graph == null || outBeds == null) return;
            foreach (LineEdge e in Graph.Edges)
            {
                if (e.Bridge) continue;   // bridge segments span the terrain — never cut a bed for them
                BuildEdgeBed(field, e, out var pts, out float flatHalf); if (pts != null) outBeds.Add((pts, flatHalf));
            }
        }

        // The smoothed sunken roadbed for ONE edge: centreline sampled at the node-to-node grade line minus depth.
        void BuildEdgeBed(ITerrainSurface field, LineEdge e, out List<Vector3> pts, out float flatHalf)
        {
            float s = Mathf.Max(1f, GradeSampleStep);
            float depth = Mathf.Max(0f, ExcavationDepth);
            EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
            float yA = DesignElevation(e.A, field), yB = DesignElevation(e.B, field);
            float chord = Vector2.Distance(p0, p3);
            int n = Mathf.Max(2, Mathf.CeilToInt(chord / s));
            pts = new List<Vector3>(n + 1);
            // The bed rides the SAME profile as the swept road (straight grade, or terrain-follow) so the cut matches
            // where the road sits. EdgeBezier returns a cubic for straight edges too, so sample it as a curve.
            var bedY = NetworkDesigner.Roads.RoadSweep.ElevationProfile(p0, p3, true, p1, p2, n + 1, yA, yB,
                FollowTerrain > 0.001f && field != null ? (System.Func<Vector2, float>)(q => field.SampleHeight(q.x, q.y)) : null,
                FollowTerrain);
            for (int i = 0; i <= n; i++)
            {
                Vector2 xz = LineGraph.Bezier(p0, p1, p2, p3, i / (float)n);
                pts.Add(new Vector3(xz.x, bedY[i] - depth, xz.y));
            }
            flatHalf = Mathf.Max(1f, EdgeWidth(e) * 0.5f + Mathf.Max(0f, ExcavationMargin));
        }

        // ── per-segment excavate support (drives the in-world Excavate/Build buttons) ──
        public int EdgeCount => Graph?.Edges?.Count ?? 0;
        public bool IsEdgeExcavated(int i) => Graph != null && i >= 0 && i < Graph.Edges.Count && Graph.Edges[i].Excavated;
        public void SetEdgeExcavated(int i, bool on) { if (Graph != null && i >= 0 && i < Graph.Edges.Count) Graph.Edges[i].Excavated = on; }
        public bool IsEdgeBridge(int i) => Graph != null && i >= 0 && i < Graph.Edges.Count && Graph.Edges[i].Bridge;
        public void SetEdgeBridge(int i, bool on) { if (Graph != null && i >= 0 && i < Graph.Edges.Count) Graph.Edges[i].Bridge = on; }
        // "Built" = this segment has a 3D road swept on it. A per-segment flag (not an index set) so it survives the
        // edge-index renumbering that drawing/splitting causes — built roads stay built when you add a crossing.
        public bool IsEdgeBuilt(int i) => Graph != null && i >= 0 && i < Graph.Edges.Count && Graph.Edges[i].Built;
        public void SetEdgeBuilt(int i, bool on) { if (Graph != null && i >= 0 && i < Graph.Edges.Count) Graph.Edges[i].Built = on; }
        public void ClearAllBuilt() { if (Graph == null) return; foreach (LineEdge e in Graph.Edges) e.Built = false; }
        public bool AnyEdgeBuilt() { if (Graph == null) return false; foreach (LineEdge e in Graph.Edges) if (e.Built) return true; return false; }

        // Append straight sub-segments (a→b, with a half-width) of every BUILT edge whose footprint comes within
        // `reach` of `center`, so the sculpt brush can skip cells under a built road (terrain edits don't disturb the
        // road's bed). Only built edges count — un-built plan lines aren't protected.
        public void CollectBuiltFootprints(Vector2 center, float reach, List<(Vector2 a, Vector2 b, float half)> outSegs)
        {
            if (Graph == null || outSegs == null) return;
            for (int i = 0; i < Graph.Edges.Count; i++)
            {
                LineEdge e = Graph.Edges[i];
                if (e == null || !e.Built) continue;
                float half = EdgeWidth(e) * 0.5f;
                float gate = reach + half; float gateSq = gate * gate;
                EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
                float chord = Vector2.Distance(p0, p3);
                // Cheap far-edge reject: if even the edge midpoint is farther than the whole edge could reach, skip
                // sampling it entirely (keeps sculpting fast when most built roads are nowhere near the brush).
                if (((p0 + p3) * 0.5f - center).sqrMagnitude > (gate + chord) * (gate + chord)) continue;
                int n = Mathf.Clamp(Mathf.CeilToInt(chord / 4f), 1, 256);
                Vector2 prev = p0;
                for (int k = 1; k <= n; k++)
                {
                    Vector2 cur = LineGraph.Bezier(p0, p1, p2, p3, k / (float)n);
                    if (((prev + cur) * 0.5f - center).sqrMagnitude <= gateSq) outSegs.Add((prev, cur, half));
                    prev = cur;
                }
            }
        }

        // Bridge (trestle) build params: deck slab thickness, pier spacing along the span, pier cross-section size.
        public float BridgeDeckDepth = 1.0f;
        public float BridgePierSpacing = 14f;
        public float BridgePierWidth = 1.2f;
        public bool BridgeParapets = true;       // side barrier walls along the deck edges
        public float BridgeParapetHeight = 1.0f;

        // Auto-bridge: when a freshly-drawn STRAIGHT segment crosses a terrain dip, auto-split it and flag the
        // middle span as a bridge. Trigger = terrain falls > BridgeTriggerDepth below the segment's chord; the
        // approach nodes land BridgeApproachPad metres outside each rim (the "10 m before/after" in the mockup).
        public bool AutoBridge = true;
        public float BridgeTriggerDepth = 4f;
        public float BridgeApproachPad = 10f;

        // Geometry accessors for the 3D trestle builder (reconstructs the swept centreline + width per edge).
        public void EdgeBezierWorld(int i, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3)
        { p0 = p1 = p2 = p3 = default; if (Graph == null || i < 0 || i >= Graph.Edges.Count) return; EdgeBezier(Graph.Edges[i], out p0, out p1, out p2, out p3); }
        public float EdgeCorridorWidth(int i) => (Graph != null && i >= 0 && i < Graph.Edges.Count) ? EdgeWidth(Graph.Edges[i]) : RoadWidth;

        // ── segment selection (the action queue): Cmd/Ctrl-click inside a corridor toggles it; Excavate!/Build!/
        // Force-Bridge act on the set. Indices are valid only between topology changes, so any draw/delete clears it.
        [System.NonSerialized] readonly HashSet<int> _selEdges = new HashSet<int>();
        public int SelectedEdgeCount => _selEdges.Count;
        public bool IsEdgeSelected(int e) => _selEdges.Contains(e);
        public void ClearEdgeSelection() { _selEdges.Clear(); }
        public void ToggleEdgeSelected(int e) { if (e < 0) return; if (!_selEdges.Remove(e)) _selEdges.Add(e); }
        public List<int> SelectedEdgesList() { var l = new List<int>(_selEdges); l.Sort(); return l; }

        // Nearest edge whose corridor (its own footprint half-width + a small grab margin) contains the click; -1 if
        // the click is outside every corridor. Lets the user select by clicking ANYWHERE inside a segment, not a node.
        public int PickEdgeInCorridor(Vector2 xz)
        {
            if (Graph == null || Graph.Edges.Count == 0) return -1;
            if (!Graph.NearestPointOnEdge(xz, 80f, out int ei, out _, out Vector2 cp)) return -1;
            float half = Mathf.Max(2f, EdgeWidth(Graph.Edges[ei]) * 0.5f + 2f);
            return (cp - xz).sqrMagnitude <= half * half ? ei : -1;
        }

        public bool EdgeExcavationBed(ITerrainSurface field, int i, out List<Vector3> pts, out float flatHalf)
        {
            pts = null; flatHalf = 0f;
            if (Graph == null || i < 0 || i >= Graph.Edges.Count) return false;
            BuildEdgeBed(field, Graph.Edges[i], out pts, out flatHalf);
            return pts != null && pts.Count >= 2;
        }

        // World position over a segment's midpoint (draped + lifted), for the in-world Excavate/Build button.
        public bool EdgeMidpointWorld(ITerrainSurface field, int i, out Vector3 mid)
        {
            mid = default;
            if (Graph == null || i < 0 || i >= Graph.Edges.Count) return false;
            EdgeBezier(Graph.Edges[i], out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
            Vector2 xz = LineGraph.Bezier(p0, p1, p2, p3, 0.5f);
            mid = Drape(field, xz) + Vector3.up * 2f;
            return true;
        }

        // The road grade elevation (world Y) at a node — the per-node DESIGN height. Captured lazily from the
        // CURRENT (shaped) surface the first time it's needed and stored, so it reflects terrain the user
        // carved/flattened, stays consistent between Excavate and Build, and is idempotent (re-running reuses
        // it). Edit it via the node elevation handles, or ClearNodeElevations to re-capture after re-shaping.
        public float DesignElevation(int nodeIdx, ITerrainSurface field)
        {
            if (nodeIdx < 0 || nodeIdx >= Graph.Nodes.Count) return 0f;
            float y = Graph.GetNodeY(nodeIdx);
            if (!float.IsNaN(y)) return y;
            Vector2 nd = Graph.Nodes[nodeIdx];
            y = field != null ? field.SampleHeight(nd.x, nd.y) : 0f;
            Graph.SetNodeY(nodeIdx, y);
            return y;
        }

        // Drop all captured design elevations so they re-capture from the current surface on the next run.
        public void ClearNodeElevations() { for (int i = 0; i < Graph.NodeY.Count; i++) Graph.NodeY[i] = float.NaN; }

        // ---- elevation-edit sub-mode: drag a node puck to set its height; multi-select + right-click to level ----

        [System.NonSerialized] public bool ElevationEditMode;
        [System.NonSerialized] readonly HashSet<int> _selected = new HashSet<int>();
        [System.NonSerialized] int _elevDrag = -1;
        [System.NonSerialized] float _elevGrabOffset;
        [System.NonSerialized] Vector2 _elevStartMouse;
        [System.NonSerialized] bool _elevMoved;

        public void SetElevationEditMode(bool on) { ElevationEditMode = on; if (!on) { _selected.Clear(); _elevDrag = -1; } }

        // ── path excavation mode: click a start node, then an end node → excavate the connecting path ──
        [System.NonSerialized] public bool ExcavateSelectMode;
        [System.NonSerialized] public int ExcavateStartNode = -1;
        public void SetExcavateSelectMode(bool on) { ExcavateSelectMode = on; if (!on) ExcavateStartNode = -1; }

        // ── build-segment mode: click a start node, then an end node → build the path between (like Excavate) ──
        [System.NonSerialized] public bool BuildSegmentMode;
        [System.NonSerialized] public int BuildStartNode = -1;
        public void SetBuildSegmentMode(bool on) { BuildSegmentMode = on; if (!on) BuildStartNode = -1; }

        // ── bridge mode: click a start node, then an end node → flag the path's segments as a bridge span ──
        [System.NonSerialized] public bool BridgeSelectMode;
        [System.NonSerialized] public int BridgeStartNode = -1;
        public void SetBridgeSelectMode(bool on) { BridgeSelectMode = on; if (!on) BridgeStartNode = -1; }

        // Nearest edge whose bezier passes within maxDist of a world XZ; -1 if none.
        public int PickEdge(Vector2 xz, float maxDist)
            => Graph != null && Graph.NearestPointOnEdge(xz, maxDist, out int ei, out _, out _) ? ei : -1;

        // SCREEN-SPACE node pick: nearest node whose puck projects within `pixelRadius` of the cursor. Robust to
        // camera angle and zoom (terrain-XZ picking drifts under oblique views and shrinks to sub-pixel when zoomed
        // out — the cause of "click a few times to select"). -1 if none.
        public int PickNodeScreen(Camera cam, ITerrainSurface field, Vector2 screenPos, float pixelRadius, bool preferLaneNodes = false)
        {
            if (cam == null || Graph == null || Graph.Nodes.Count == 0) return -1;
            int best = -1; float bestSq = pixelRadius * pixelRadius;
            float puck = Mathf.Max(0.02f, PlanGuides.NodePuckHeight) * 0.5f;
            for (int i = 0; i < Graph.Nodes.Count; i++)
            {
                if (preferLaneNodes && SegmentNodeHidden(i)) continue;   // end/continuation → grab its lane node, not the (hidden) segment node
                Vector2 c = Graph.Nodes[i];
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(c.x, DesignElevation(i, field) + puck, c.y));
                if (sp.z <= 0f) continue;   // behind the camera
                float dsq = (new Vector2(sp.x, sp.y) - screenPos).sqrMagnitude;
                if (dsq < bestSq) { bestSq = dsq; best = i; }
            }
            return best;
        }

        // Shortest path of EDGE indices from node a to node b (BFS by edge count). Empty if same/unreachable.
        public List<int> EdgePathBetween(int a, int b)
        {
            var path = new List<int>();
            if (Graph == null || a < 0 || b < 0 || a == b || a >= Graph.Nodes.Count || b >= Graph.Nodes.Count) return path;
            var cameFrom = new Dictionary<int, int>();   // node → previous node
            var viaEdge = new Dictionary<int, int>();     // node → edge index used to reach it
            var q = new Queue<int>(); q.Enqueue(a); cameFrom[a] = -1;
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (cur == b) break;
                for (int ei = 0; ei < Graph.Edges.Count; ei++)
                {
                    LineEdge e = Graph.Edges[ei];
                    int nb = e.A == cur ? e.B : (e.B == cur ? e.A : -1);
                    if (nb < 0 || cameFrom.ContainsKey(nb)) continue;
                    cameFrom[nb] = cur; viaEdge[nb] = ei; q.Enqueue(nb);
                }
            }
            if (!cameFrom.ContainsKey(b)) return path;    // unreachable
            for (int n = b; n != a && viaEdge.ContainsKey(n); n = cameFrom[n]) path.Add(viaEdge[n]);
            path.Reverse();
            return path;
        }

        public int PickNode(Vector2 xz) => Graph.NearestNode(xz, NodePickRadius);
        public bool IsDraggingElevation => _elevDrag >= 0;
        public Vector2 DragNodeXZ => (_elevDrag >= 0 && _elevDrag < Graph.Nodes.Count) ? Graph.Nodes[_elevDrag] : Vector2.zero;

        // Start a potential drag on a node. `axisY0` = the world Y of the vertical axis at the node under the
        // cursor right now; the grab offset keeps the puck from jumping to the cursor when the drag begins.
        public void BeginElevationDrag(ITerrainSurface field, int node, float axisY0, Vector2 mouseScreen)
        {
            if (node < 0 || node >= Graph.Nodes.Count) return;
            _elevDrag = node;
            _elevGrabOffset = DesignElevation(node, field) - axisY0;
            _elevStartMouse = mouseScreen;
            _elevMoved = false;
        }

        // Update the dragged node to follow the vertical axis (axisY = world Y on that axis under the cursor).
        // Returns true once it's a real drag (moved past a small threshold) so the caller knows to rebuild.
        public bool UpdateElevationDrag(float axisY, Vector2 mouseScreen)
        {
            if (_elevDrag < 0) return false;
            if (!_elevMoved && (mouseScreen - _elevStartMouse).sqrMagnitude > 16f) _elevMoved = true;
            if (_elevMoved) { Graph.SetNodeY(_elevDrag, axisY + _elevGrabOffset); return true; }
            return false;
        }

        // End the drag. A no-move drag is treated as a CLICK → toggle the node's selection.
        public void EndElevationDrag()
        {
            if (_elevDrag >= 0 && !_elevMoved) ToggleSelect(_elevDrag);
            _elevDrag = -1;
        }

        public void ToggleSelect(int node) { if (node >= 0 && !_selected.Remove(node)) _selected.Add(node); }

        // Set every selected node's design elevation to the reference node's height (level a set to one node).
        public void LevelSelectedTo(ITerrainSurface field, int refNode)
        {
            if (refNode < 0 || refNode >= Graph.Nodes.Count) return;
            float targetY = DesignElevation(refNode, field);
            foreach (int s in _selected) if (s >= 0 && s < Graph.Nodes.Count) Graph.SetNodeY(s, targetY);
        }

        // ── setback-edit sub-mode: a draggable handle per junction approach sets that road-end's setback override
        // (LineEdge.SetbackA/SetbackB; <0 = auto). Mirrors the old NetworkDesigner's SetbackHandle UX. ──
        [System.NonSerialized] public bool SetbackEditMode;
        [System.NonSerialized] int _sbEdge = -1; [System.NonSerialized] bool _sbEndA;
        public void SetSetbackEditMode(bool on) { SetbackEditMode = on; if (!on) _sbEdge = -1; }
        public bool IsDraggingSetback => _sbEdge >= 0;

        // ── road-class sub-mode: recolour the corridor by intersection precedence and click an edge to cycle its
        // class (Auto → Primary → Secondary → Auto). Older corridors auto-resolve to Primary; crossers to Secondary. ──
        [System.NonSerialized] public bool ClassEditMode;
        public void SetClassEditMode(bool on) { ClassEditMode = on; }
        static readonly Color32 ColPrimary   = new Color32(60, 200, 120, 255);  // green — primary (through) road
        static readonly Color32 ColSecondary = new Color32(255, 140, 40, 255);  // orange — secondary (yielding) road

        public RoadClass GetEdgeClass(int ei) => (Graph != null && ei >= 0 && ei < Graph.Edges.Count) ? Graph.Edges[ei].Class : RoadClass.Auto;
        public void SetEdgeClass(int ei, RoadClass c) { if (Graph != null && ei >= 0 && ei < Graph.Edges.Count) Graph.Edges[ei].Class = c; }
        public void CycleEdgeClass(int ei)
        {
            if (Graph == null || ei < 0 || ei >= Graph.Edges.Count) return;
            LineEdge e = Graph.Edges[ei];
            e.Class = e.Class == RoadClass.Auto ? RoadClass.Primary
                    : (e.Class == RoadClass.Primary ? RoadClass.Secondary : RoadClass.Auto);
        }

        // Resolved precedence of an edge (manual class, or age-derived when Auto). Lives on LineGraph so the build
        // bridge resolves it identically; this is just the overlay's accessor.
        public RoadClass EffectiveClass(int ei) => Graph != null ? Graph.EffectiveClass(ei) : RoadClass.Primary;

        // Extra arm (metres) the handle ring sticks out past the road's LEFT edge, so it's off the asphalt and easy to grab.
        const float HandleArm = 4f;
        // A handle exists for a (edge, end) only where the road meets a real intersection (node degree >= 3).
        bool HandleApplies(int ei, bool endA) { LineEdge e = Graph.Edges[ei]; return NodeDegree(endA ? e.A : e.B) >= 3; }
        // Setback used to POSITION a handle: the override if set, else the intersection default (matches the resolver's 10 m floor).
        // Setback used to POSITION a handle: the live manual override (so it tracks a drag) → else the resolver's
        // ACTUAL computed setback captured at the last build (acute/secondary boosts make this >> the flat default) →
        // else the bare default before any build. Keyed by edge index, invalidated when the edge count changes.
        [System.NonSerialized] readonly Dictionary<int, float> _resolvedSetback = new Dictionary<int, float>();
        [System.NonSerialized] int _resolvedSetbackEdges = -1;
        public void SetResolvedSetbacks(Dictionary<int, float> map)
        {
            _resolvedSetback.Clear();
            if (map != null) foreach (var kv in map) _resolvedSetback[kv.Key] = kv.Value;
            _resolvedSetbackEdges = Graph != null ? Graph.Edges.Count : -1;
        }
        public void ClearResolvedSetbacks() { _resolvedSetback.Clear(); _resolvedSetbackEdges = -1; }
        float HandleSetback(int ei, bool endA)
        {
            LineEdge e = Graph.Edges[ei];
            float ov = endA ? e.SetbackA : e.SetbackB;
            if (ov >= 0f) return ov;                                                   // live override (tracks the drag)
            if (Graph != null && _resolvedSetbackEdges == Graph.Edges.Count
                && _resolvedSetback.TryGetValue(ei * 2 + (endA ? 0 : 1), out float rs) && rs > 0f)
                return rs;                                                             // resolver's actual setback
            return NetworkDesigner.Geometry.GeometryResolver.IntersectionSetback;      // default (pre-build)
        }
        // Left-perpendicular of the outward direction (XZ, top-down): facing away from the node, this points to the road's left.
        Vector2 HandleLeft(LineEdge e, int node) { Vector2 o = EdgeDirAtNode(e, node); return new Vector2(-o.y, o.x); }
        // Anchor = the LEFT edge of the road at the setback line (where the ring's arm attaches).
        Vector2 HandleAnchorXZ(int ei, bool endA)
        {
            LineEdge e = Graph.Edges[ei]; int node = endA ? e.A : e.B;
            return Graph.Nodes[node] + EdgeDirAtNode(e, node) * HandleSetback(ei, endA) + HandleLeft(e, node) * (EdgeWidth(e) * 0.5f);
        }
        // World XZ of the handle ring: the left-edge anchor pushed a short arm further out to the left, clear of the asphalt.
        Vector2 HandleXZ(int ei, bool endA)
        {
            LineEdge e = Graph.Edges[ei]; int node = endA ? e.A : e.B;
            return HandleAnchorXZ(ei, endA) + HandleLeft(e, node) * HandleArm;
        }

        // Nearest setback handle under the cursor (screen-space, parallax-proof). edge/endA identify the (road,end).
        public bool PickSetbackHandle(Camera cam, ITerrainSurface field, Vector2 screenPos, float pixelRadius, out int edge, out bool endA)
        {
            edge = -1; endA = false;
            if (cam == null || Graph == null) return false;
            float bestSq = pixelRadius * pixelRadius;
            for (int i = 0; i < Graph.Edges.Count; i++)
                for (int s = 0; s < 2; s++)
                {
                    bool ea = s == 0;
                    if (!HandleApplies(i, ea)) continue;
                    int node = ea ? Graph.Edges[i].A : Graph.Edges[i].B;
                    Vector2 h = HandleXZ(i, ea);
                    Vector3 sp = cam.WorldToScreenPoint(new Vector3(h.x, DesignElevation(node, field) + 1f, h.y));
                    if (sp.z <= 0f) continue;
                    float dsq = (new Vector2(sp.x, sp.y) - screenPos).sqrMagnitude;
                    if (dsq < bestSq) { bestSq = dsq; edge = i; endA = ea; }
                }
            return edge >= 0;
        }

        public void BeginSetbackDrag(int edge, bool endA) { _sbEdge = edge; _sbEndA = endA; }
        // Project the cursor onto the approach's outward axis → the new setback (clamped within the edge length).
        public bool UpdateSetbackDrag(Vector2 cursorXZ)
        {
            if (_sbEdge < 0 || _sbEdge >= Graph.Edges.Count) return false;
            LineEdge e = Graph.Edges[_sbEdge];
            int node = _sbEndA ? e.A : e.B;
            Vector2 outward = EdgeDirAtNode(e, node);
            float chord = Vector2.Distance(Graph.Nodes[e.A], Graph.Nodes[e.B]);
            float sb = Mathf.Clamp(Vector2.Dot(cursorXZ - Graph.Nodes[node], outward), 0f, chord * 0.45f);
            if (_sbEndA) e.SetbackA = sb; else e.SetbackB = sb;
            return true;
        }
        public void EndSetbackDrag() { _sbEdge = -1; }
        public void ResetSetback(int edge, bool endA)
        {
            if (edge < 0 || edge >= Graph.Edges.Count) return;
            if (endA) Graph.Edges[edge].SetbackA = -1f; else Graph.Edges[edge].SetbackB = -1f;
        }

        static readonly Color32 ColSetbackHandle = new Color32(255, 140, 40, 255);    // orange ring + stem
        static readonly Color32 ColSetbackActive = new Color32(255, 220, 60, 255);    // brighter while dragging
        void DrawSetbackHandles(ITerrainSurface field)
        {
            for (int i = 0; i < Graph.Edges.Count; i++)
                for (int s = 0; s < 2; s++)
                {
                    bool ea = s == 0;
                    if (!HandleApplies(i, ea)) continue;
                    LineEdge e = Graph.Edges[i];
                    int node = ea ? e.A : e.B;
                    Vector2 left = HandleLeft(e, node);
                    Vector2 mid = Graph.Nodes[node] + EdgeDirAtNode(e, node) * HandleSetback(i, ea); // setback point on the centreline
                    float half = EdgeWidth(e) * 0.5f;
                    Vector2 anchor = HandleAnchorXZ(i, ea);      // left edge at the setback line
                    Vector2 h = HandleXZ(i, ea);                 // ring, out past the left edge
                    bool active = _sbEdge == i && _sbEndA == ea;
                    Color32 col = active ? ColSetbackActive : ColSetbackHandle;
                    AddSeg(Drape(field, mid - left * half), Drape(field, anchor), col);  // setback line across the road
                    AddSeg(Drape(field, anchor), Drape(field, h), col);                  // arm out to the ring
                    float rr = active ? 2.2f : 1.6f; Vector3 pr = default;
                    for (int k = 0; k <= 20; k++)
                    {
                        float a = k / 20f * Mathf.PI * 2f;
                        Vector3 p = Drape(field, new Vector2(h.x + Mathf.Cos(a) * rr, h.y + Mathf.Sin(a) * rr));
                        if (k > 0) AddSeg(pr, p, col);
                        pr = p;
                    }
                }
        }

        // ---- rendering: a draped corridor ribbon (centreline + both edges + cross-ties) + node pucks ----

        public void Rebuild(ITerrainSurface field)
        {
            EnsureRoot();
            _v.Clear(); _idx.Clear(); _col.Clear(); _nv.Clear(); _nn.Clear(); _nidx.Clear();
            _lnv.Clear(); _lnn.Clear(); _lnidx.Clear(); _laneNodes.Clear(); _nodesWithLaneNodes.Clear(); _segmentPuckNodes.Clear();

            int nc = Graph.Nodes.Count;
            var treated = new bool[nc];   // node gets a junction box (trim + outline)
            var isX = new bool[nc];       // true = intersection (3+ roads) → gets stop bars / crosswalks
            ComputeJunctions(treated, isX);
            for (int v = 0; v < nc; v++) if (treated[v]) _segmentPuckNodes.Add(v);   // corners/transitions/intersections keep a segment puck
            var boxHalf = new float[nc];  // square junction box: half-side + alignment axis (largest corridor)
            var boxAx = new Vector2[nc];
            for (int v = 0; v < nc; v++) if (treated[v]) ComputeBox(v, out boxHalf[v], out boxAx[v]);

            for (int ei = 0; ei < Graph.Edges.Count; ei++)
            {
                LineEdge e = Graph.Edges[ei];
                if (e.Bridge) { BuildBridgeEdge(field, ei, e); continue; }   // straight, un-draped deck at grade
                EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
                float ta = e.A < nc && treated[e.A] ? ApproachTrim(e.A, e, boxHalf[e.A], boxAx[e.A]) : 0f;
                float tb = e.B < nc && treated[e.B] ? ApproachTrim(e.B, e, boxHalf[e.B], boxAx[e.B]) : 0f;
                BuildCorridorEdge(field, p0, p1, p2, p3, e, ei, ta, tb);   // lane schematic, trimmed back to the box
            }
            for (int v = 0; v < nc; v++) if (treated[v]) BuildJunction(field, v, boxHalf[v], boxAx[v], isX[v]);
            DrawLaneNodes(field);   // record lane nodes FIRST so SegmentNodeHidden knows which ends are lane-handled
            // Segment-node pucks: only at junctions (corners/transitions/intersections) + special edit modes. At plain
            // ends and colinear continuations the lane nodes are the handles, so the segment node is hidden there.
            for (int i = 0; i < Graph.Nodes.Count; i++) if (!SegmentNodeHidden(i)) DrawPuck(field, i);
            // Setback rings show (and stay draggable) alongside the plan lines — no edit-mode button needed. Hidden
            // only when the plan overlay itself is hidden, or while another sub-mode owns the corridor colours.
            if (SetbackEditMode || (!_linesHidden && !ClassEditMode && !ElevationEditMode
                && !ExcavateSelectMode && !BuildSegmentMode && !BridgeSelectMode))
                DrawSetbackHandles(field);

            _mesh.Clear(); _mesh.SetVertices(_v); _mesh.SetColors(_col); _mesh.SetIndices(_idx, MeshTopology.Lines, 0); _mesh.RecalculateBounds();
            _nodeMesh.Clear(); _nodeMesh.SetVertices(_nv); _nodeMesh.SetNormals(_nn); _nodeMesh.SetTriangles(_nidx, 0); _nodeMesh.RecalculateBounds();
            _nodeMr.enabled = PlanGuides.ShowNodes && LinesVisible && Graph.Nodes.Count > 0;   // "Show nodes" + "Plan lines" + palette gate the pucks
            _laneMesh.Clear(); _laneMesh.SetVertices(_lnv); _laneMesh.SetNormals(_lnn); _laneMesh.SetTriangles(_lnidx, 0); _laneMesh.RecalculateBounds();
            if (_laneMr != null) _laneMr.enabled = PlanGuides.ShowNodes && LinesVisible && _lnv.Count > 0;   // gated like the node pucks
            if (_mr != null) _mr.enabled = LinesVisible || (_paletteActive && (SetbackEditMode || ClassEditMode));   // overlay for setback handles / class colours (palette-active only)
            if (_nodeMat != null) _nodeMat.color = PlanGuides.RoadNodeColor;   // live colour
            // Topology may have shifted node indices — clear the hover so the per-frame driver re-resolves it cleanly
            // next frame (avoids a stale index highlighting the wrong node).
            _hoverNode = -1;
            if (_hoverMr != null) { _hoverMr.enabled = false; _hoverMesh.Clear(); }
            _hoverLane = -1;   // lane-node indices shifted — force the hover overlay to re-resolve next frame
            if (_laneHoverMr != null) { _laneHoverMr.enabled = false; _laneHoverMesh.Clear(); }
            _revealNode = -1;  // lane records rebuilt — force the proximity reveal to re-emit next frame
            RebuildLaneSelOverlay(field);   // re-resolve the green selection against the fresh lane-node list
        }

        // ---- intersections / junctions ----

        // Classify each node: degree ≥ 3 = intersection (box + stop bars/crosswalks); degree 2 with a real
        // straight corner or a profile change = transition (box only); collinear runs untouched.
        void ComputeJunctions(bool[] treated, bool[] isX)
        {
            int nc = Graph.Nodes.Count;
            var deg = new int[nc];
            foreach (LineEdge e in Graph.Edges) { if (e.A < nc) deg[e.A]++; if (e.B < nc) deg[e.B]++; }
            for (int v = 0; v < nc; v++)
            {
                if (deg[v] >= 3) { isX[v] = true; treated[v] = true; continue; }
                if (deg[v] != 2) continue;
                LineEdge e1 = null, e2 = null;
                foreach (LineEdge e in Graph.Edges)
                    if (e.A == v || e.B == v) { if (e1 == null) e1 = e; else { e2 = e; break; } }
                if (e1 == null || e2 == null) continue;
                if (e1.HasCurve || e2.HasCurve) continue;   // leave smooth curve connections alone
                float turn = Vector2.Angle(EdgeDirAtNode(e1, v), -EdgeDirAtNode(e2, v));   // 0 = colinear through
                bool diffProfile = (e1.Profile ?? "") != (e2.Profile ?? "");
                if (turn > 20f || diffProfile) treated[v] = true;   // corner / transition: box only
            }
        }

        // The junction box is a SQUARE whose side = the largest corridor's full width, aligned to that
        // corridor. The major road sets the box; everything else connects into it. The roomy box also
        // leaves clear space for inspect-mode analysis details.
        void ComputeBox(int v, out float half, out Vector2 ax)
        {
            float maxW = 0f; LineEdge wide = null;
            foreach (LineEdge e in Graph.Edges)
                if (e.A == v || e.B == v) { float w = EdgeWidth(e); if (w > maxW) { maxW = w; wide = e; } }
            half = Mathf.Max(0.5f, maxW * 0.5f);
            ax = wide != null ? EdgeDirAtNode(wide, v) : Vector2.right;
        }

        // Distance to pull edge `e`'s markings back at node `v`: where its centreline exits the square box.
        float ApproachTrim(int v, LineEdge e, float half, Vector2 ax)
        {
            Vector2 d = EdgeDirAtNode(e, v);
            Vector2 ay = new Vector2(-ax.y, ax.x);
            float m = Mathf.Max(Mathf.Abs(Vector2.Dot(d, ax)), Mathf.Abs(Vector2.Dot(d, ay)));
            return half / Mathf.Max(0.2f, m);
        }

        // Unit direction pointing AWAY from node `v` along edge `e`.
        Vector2 EdgeDirAtNode(LineEdge e, int v)
        {
            EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
            Vector2 d = e.A == v ? LineGraph.BezierTangent(p0, p1, p2, p3, 0f) : -LineGraph.BezierTangent(p0, p1, p2, p3, 1f);
            if (d.sqrMagnitude < 1e-8f) d = e.A == v ? (Graph.Nodes[e.B] - Graph.Nodes[e.A]) : (Graph.Nodes[e.A] - Graph.Nodes[e.B]);
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.right;
        }

        // Draw the closed square junction box (side = largest corridor width, aligned to it), plus stop
        // bars + crosswalks at real intersections. Roads trim to the box; the box leaves clear space for
        // inspect-mode analysis details.
        void BuildJunction(ITerrainSurface field, int v, float half, Vector2 ax, bool isX)
        {
            Vector2 c = Graph.Nodes[v];
            Vector2 ay = new Vector2(-ax.y, ax.x);
            Vector3 b0 = Drape(field, c + ax * half + ay * half);
            Vector3 b1 = Drape(field, c + ax * half - ay * half);
            Vector3 b2 = Drape(field, c - ax * half - ay * half);
            Vector3 b3 = Drape(field, c - ax * half + ay * half);
            AddSeg(b0, b1, ColEdge); AddSeg(b1, b2, ColEdge); AddSeg(b2, b3, ColEdge); AddSeg(b3, b0, ColEdge);

            if (!isX) return;
            foreach (LineEdge e in Graph.Edges)
            {
                if (e.A != v && e.B != v) continue;
                Vector2 d = EdgeDirAtNode(e, v);
                float hw = Mathf.Max(0.5f, EdgeWidth(e) * 0.5f);
                float r = ApproachTrim(v, e, half, ax);
                if (ShowStopBars) EmitStopBar(field, c, d, hw, r);
                if (ShowCrosswalks) EmitCrosswalk(field, c, d, hw, r);
            }
        }

        // A (doubled) white bar across the approach pavement at the box edge.
        void EmitStopBar(ITerrainSurface field, Vector2 c, Vector2 d, float hw, float r)
        {
            Vector2 perp = new Vector2(-d.y, d.x);
            for (float off = 0f; off <= 0.5f; off += 0.5f)
            {
                Vector2 b = c + d * (r + off);
                AddSeg(Drape(field, b + perp * hw), Drape(field, b - perp * hw), ColEdge);
            }
        }

        // Continental crosswalk: stripes parallel to traffic, just inside the box edge, across the pavement.
        void EmitCrosswalk(ITerrainSurface field, Vector2 c, Vector2 d, float hw, float r)
        {
            Vector2 perp = new Vector2(-d.y, d.x);
            float depth = 2.2f, inner = Mathf.Max(0.5f, r - depth);
            Vector2 a = c + d * inner, b = c + d * r;
            for (float u = -hw + 0.5f; u <= hw - 0.5f; u += 0.9f)
                AddSeg(Drape(field, a + perp * u), Drape(field, b + perp * u), ColEdge);
        }

        void EdgeBezier(LineEdge e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3)
        {
            if (e.HasCurve)   // an explicit shift-curve renders as its own bezier
            {
                p0 = Graph.Nodes[e.A]; p3 = Graph.Nodes[e.B]; p1 = e.ControlA; p2 = e.ControlB;
            }
            else   // every other connection is a hard-angle straight (no auto-smoothing)
            {
                p0 = Graph.Nodes[e.A]; p3 = Graph.Nodes[e.B];
                Vector2 d = p3 - p0; p1 = p0 + d / 3f; p2 = p0 + d * (2f / 3f);
            }
        }

        // ---- lane schematic: draw the segment's cross-section as real lane markings ----

        static readonly Color32 ColEdge   = new Color32(235, 235, 235, 195);   // pavement / lane edge line (white, solid)
        static readonly Color32 ColLane   = new Color32(205, 205, 205, 165);   // same-direction lane divider (dashed)
        static readonly Color32 ColCenter = new Color32(245, 205, 45, 255);    // opposing centreline (yellow, double)
        static readonly Color32 ColTurn   = new Color32(245, 205, 45, 255);    // turn-lane boundary (yellow)
        static readonly Color32 ColMedian = new Color32(200, 165, 110, 220);   // median hatch (warm tan — reads as raised, not drivable)
        Color32 ColFoot => new Color32((byte)(PlanColor.r * 255f), (byte)(PlanColor.g * 255f), (byte)(PlanColor.b * 255f), 170); // shoulder/footprint (plan amber, dashed)
        static readonly Color32 ColSkirt = new Color32(70, 190, 235, 150);  // excavation skirt at ±(footprint + margin) — cyan, dashed, distinct from the amber footprint
        // Whole-segment STATE tint: every marking on a segment is recoloured by its state so you can read the plan
        // at a glance — red = planned (not yet cut), yellow = excavated (ready to build), blue = bridge span. The
        // per-marking ALPHA is preserved (so dashed footprint stays fainter than solid edges); only the hue swaps.
        static readonly Color32 ColPlanned   = new Color32(225, 55, 45, 255);   // red  — drawn, not excavated
        static readonly Color32 ColExcavated = new Color32(255, 232, 0, 255);   // bright yellow — bed cut, ready to build
        static readonly Color32 ColBridge    = new Color32(55, 120, 240, 255);  // blue — bridge span (not cut, on piers)
        [System.NonSerialized] Color32 _tint; [System.NonSerialized] bool _tintSel;

        // Pick the state tint for an edge (+ whether it's selected → brighten + force a strong alpha).
        void SetEdgeTint(LineEdge e, int edgeIndex)
        {
            Color32 baseCol;
            if (ClassEditMode)
            {
                // Class overlay: green = primary, orange = secondary. Auto-derived edges read dimmer (lerped to grey)
                // than manually-set ones so you can tell which precedence you've pinned vs. what the age rule inferred.
                Color32 cc = EffectiveClass(edgeIndex) == RoadClass.Primary ? ColPrimary : ColSecondary;
                baseCol = (e != null && e.Class != RoadClass.Auto) ? cc : (Color32)Color.Lerp(cc, Color.gray, 0.5f);
            }
            else
            {
                baseCol = e != null && e.Bridge ? ColBridge : (e != null && e.Excavated ? ColExcavated : ColPlanned);
            }
            _tintSel = IsEdgeSelected(edgeIndex);
            _tint = _tintSel ? (Color32)Color.Lerp(baseCol, Color.white, 0.45f) : baseCol;   // selected reads brighter
        }
        // Recolour a marking to the segment's state tint, keeping the marking's own alpha (or a strong floor when selected).
        Color32 Tn(Color32 c) => new Color32(_tint.r, _tint.g, _tint.b, _tintSel ? (byte)Mathf.Max(c.a, 210) : c.a);

        // The two skirt lines marking the full excavated corridor (road footprint + ExcavationMargin per side).
        void EmitSkirt(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int n, float footHalf)
        {
            if (ExcavationMargin <= 0.01f) return;
            float s = footHalf + ExcavationMargin;
            EmitOffsetLine(field, p0, p1, p2, p3, n,  s, Tn(ColSkirt), 3f, 2.2f);
            EmitOffsetLine(field, p0, p1, p2, p3, n, -s, Tn(ColSkirt), 3f, 2.2f);
        }

        // strip kinds across the cross-section (BA side → centre → AB side)
        const int KOut = -1;   // strip kinds come from the shared NetworkDesigner.Roads.RoadLayout

        // Lay the segment's lanes/median/turn-lane/shoulders out as draped markings, draped along the bezier.
        // A BRIDGE segment, drawn UN-DRAPED: straight lines held at the deck grade (the chord between the two nodes'
        // design elevations) rather than draped on the gorge floor, so it reads as spanning the gap. Blue via the tint.
        void BuildBridgeEdge(ITerrainSurface field, int edgeIndex, LineEdge e)
        {
            SetEdgeTint(e, edgeIndex);
            Vector2 a = Graph.Nodes[e.A], b = Graph.Nodes[e.B];
            float yA = DesignElevation(e.A, field), yB = DesignElevation(e.B, field);
            Vector2 d = b - a; float len = d.magnitude;
            if (len < 1e-3f) return;
            Vector2 dir = d / len, perp = new Vector2(-dir.y, dir.x);
            float half = Mathf.Max(0.3f, EdgeWidth(e) * 0.5f);
            int n = Mathf.Clamp(Mathf.CeilToInt(len / 2f), 2, 600);
            EmitChordLine(a, b, yA, yB, perp,  half, n, Tn(ColEdge));    // deck edges (solid)
            EmitChordLine(a, b, yA, yB, perp, -half, n, Tn(ColEdge));
            EmitChordLine(a, b, yA, yB, perp,   0f, n, Tn(ColCenter));   // deck centreline
        }
        void EmitChordLine(Vector2 a, Vector2 b, float yA, float yB, Vector2 perp, float off, int n, Color32 col)
        {
            Vector3 prev = default; bool have = false;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                Vector2 xz = Vector2.Lerp(a, b, t) + perp * off;
                Vector3 w = new Vector3(xz.x, Mathf.Lerp(yA, yB, t) + 0.15f, xz.y);   // held at deck grade (not draped)
                if (have) AddSeg(prev, w, col);
                prev = w; have = true;
            }
        }

        void BuildCorridorEdge(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, LineEdge e, int edgeIndex, float trimA, float trimB)
        {
            SetEdgeTint(e, edgeIndex);   // every marking below is recoloured to the segment's state (red/yellow/blue)
            float len = 0f;
            _pts[0] = p0;
            for (int i = 1; i <= SubSteps; i++) { _pts[i] = LineGraph.Bezier(p0, p1, p2, p3, i / (float)SubSteps); len += Vector2.Distance(_pts[i - 1], _pts[i]); }
            if (len < 1e-3f) return;
            // Pull the markings back from junctions so approaches don't pile on top of each other — but CAP each end's
            // trim at 45% of the length so a short segment between two big junctions still shows its middle (≥10%)
            // instead of rendering nothing. A real edge must never be invisible (the "segment doesn't show up" bug).
            float cap = len * 0.45f;
            _tStart = Mathf.Clamp01(Mathf.Min(trimA, cap) / len);
            _tEnd = 1f - Mathf.Clamp01(Mathf.Min(trimB, cap) / len);
            int n = Mathf.Clamp(Mathf.CeilToInt(len * (_tEnd - _tStart) / 1.5f), 2, 2048);   // fine enough for dashed markings

            NetworkDesigner.Model.RoadProfile prof = NetworkDesigner.Roads.RoadProfileLibrary.Resolve(e?.Profile);
            if (prof == null || prof.TotalWidth < 0.5f)
            {
                float half = Mathf.Max(0.1f, EdgeWidth(e) * 0.5f);
                EmitOffsetLine(field, p0, p1, p2, p3, n, half, Tn(ColEdge), 0f, 0f);   // generic: two solid edges + dashed centre
                EmitOffsetLine(field, p0, p1, p2, p3, n, -half, Tn(ColEdge), 0f, 0f);
                EmitOffsetLine(field, p0, p1, p2, p3, n, 0f, Tn(ColCenter), 2f, 2f);
                EmitSkirt(field, p0, p1, p2, p3, n, half);
                return;
            }

            // Cross-section layout: the authored corridor STACK when present (so the plan lines match rail/bike/etc.),
            // else the legacy parametric layout.
            var cfg = NetworkDesigner.Roads.RoadProfileLibrary.ResolveConfig(e?.Profile);
            var lay = cfg?.Corridor != null
                ? NetworkDesigner.Roads.RoadCrossSectionBuilder.StackLayout(cfg.Corridor)
                : NetworkDesigner.Roads.RoadLayout.Of(prof);
            float W = 0f; foreach (var (sw, _) in lay) W += sw;

            // Reference the schematic on the road's A↔B CENTRELINE (matching the swept body, which FromStack centres
            // on CenterU = SplitU), not the geometric middle — so the plan lines sit on the built pavement for an
            // asymmetric one-way road instead of floating off to one side.
            float center = cfg?.Corridor != null
                ? NetworkDesigner.Roads.RoadCrossSectionBuilder.FromStack(cfg.Corridor).Center()
                : W * 0.5f;

            float u = -center;
            EmitBoundary(field, p0, p1, p2, p3, n, u, KOut, lay.Count > 0 ? lay[0].k : KOut);
            for (int i = 0; i < lay.Count; i++)
            {
                if (lay[i].k == NetworkDesigner.Roads.RoadLayout.Median) EmitMedianHatch(field, p0, p1, p2, p3, n, len, u, u + lay[i].w);
                u += lay[i].w;
                EmitBoundary(field, p0, p1, p2, p3, n, u, lay[i].k, (i + 1 < lay.Count) ? lay[i + 1].k : KOut);
            }
            // Excavation skirt at the TRUE (possibly asymmetric) footprint edges + margin per side.
            if (ExcavationMargin > 0.01f)
            {
                EmitOffsetLine(field, p0, p1, p2, p3, n, (W - center) + ExcavationMargin, Tn(ColSkirt), 3f, 2.2f);
                EmitOffsetLine(field, p0, p1, p2, p3, n, -center - ExcavationMargin, Tn(ColSkirt), 3f, 2.2f);
            }
        }

        // Pick the marking style for the line between two RoadLayout strip kinds, then emit it.
        void EmitBoundary(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int n, float u, int left, int right)
        {
            int Med = NetworkDesigner.Roads.RoadLayout.Median, Trn = NetworkDesigner.Roads.RoadLayout.TurnLane;
            bool isLn(int kk) => NetworkDesigner.Roads.RoadLayout.IsLane(kk);
            if (left == KOut || right == KOut)
            { EmitOffsetLine(field, p0, p1, p2, p3, n, u, Tn(ColFoot), 3f, 2.2f); return; }           // corridor footprint edge (dashed)
            if (isLn(left) && isLn(right))
            {
                if (left == right) EmitOffsetLine(field, p0, p1, p2, p3, n, u, Tn(ColLane), 1.5f, 2.2f);   // same dir → dashed lane divider
                else { EmitOffsetLine(field, p0, p1, p2, p3, n, u - 0.25f, Tn(ColCenter), 0f, 0f);          // opposing → double centreline
                       EmitOffsetLine(field, p0, p1, p2, p3, n, u + 0.25f, Tn(ColCenter), 0f, 0f); }
                return;
            }
            if (left == Trn || right == Trn) { EmitOffsetLine(field, p0, p1, p2, p3, n, u, Tn(ColTurn), 0f, 0f); return; }   // turn-lane edge
            if (left == Med || right == Med) { EmitOffsetLine(field, p0, p1, p2, p3, n, u, Tn(ColEdge), 0f, 0f); return; }   // median edge
            // lane meets shoulder/sidewalk/curb → pavement edge; structure boundaries (edge|curb, edge|guard,
            // edge|parapet) drawn as a thin solid line so curbs/sidewalks/guards read in the plan.
            EmitOffsetLine(field, p0, p1, p2, p3, n, u, Tn(ColEdge), 0f, 0f);
        }

        // A line at constant lateral offset `u`, draped, dashed when gap>0 (solid when gap<=0).
        void EmitOffsetLine(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int n, float u, Color32 col, float dash, float gap)
        {
            float period = dash + gap, walked = 0f;
            Vector3 prev = default; bool have = false;
            for (int i = 0; i <= n; i++)
            {
                float t = Mathf.Lerp(_tStart, _tEnd, (float)i / n);   // trimmed range
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
            int m = Mathf.Clamp(Mathf.CeilToInt(len * (_tEnd - _tStart) / 3f), 1, 1024);
            float dt = Mathf.Max(1e-4f, band / Mathf.Max(1f, len));   // ~45° lead in t for the diagonal
            for (int j = 0; j < m; j++)
            {
                float t0 = Mathf.Lerp(_tStart, _tEnd, (float)j / m);   // trimmed range
                float t1 = Mathf.Min(_tEnd, t0 + dt);
                AddSeg(OffsetPt(field, p0, p1, p2, p3, t0, uA), OffsetPt(field, p0, p1, p2, p3, t1, uB), Tn(ColMedian));
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
        static readonly Color32 StemCol = new Color32(200, 200, 210, 200);   // node lifted off the terrain → vertical stem
        static readonly Color32 SelCol  = new Color32(90, 220, 255, 255);    // selected node ring (cyan)
        static readonly Color32 ExcStartCol = new Color32(80, 230, 120, 255); // path-excavation START node ring (green)
        static readonly Color32 BuildStartCol = new Color32(245, 150, 40, 255); // path-build START node ring (amber)

        // A short 3D cylinder puck (top cap + side wall, manual outward normals) at a node — the visible
        // handle you grab to move / curve / delete. Sits at the node's DESIGN elevation (so an elevation
        // drag is visible); in elevation-edit mode a stem drops to the terrain when lifted and selected nodes
        // get a cyan ring. Lit-transparent so the alpha shows. Mirrors rail.
        void DrawPuck(ITerrainSurface field, int idx)
        {
            Vector2 c = Graph.Nodes[idx];
            float radius = Mathf.Max(0.2f, NodePuckRadius);
            float terrainY = field != null ? field.SampleHeight(c.x, c.y) : 0f;
            float designY = Graph.GetNodeY(idx);
            float dispY = float.IsNaN(designY) ? terrainY : designY;
            float baseY = dispY + Lift;
            float topY = baseY + Mathf.Max(0.02f, PlanGuides.NodePuckHeight);

            // Elevation-edit affordances (into the line mesh): a stem to the terrain when lifted + a selection ring.
            if (ElevationEditMode)
            {
                if (Mathf.Abs(dispY - terrainY) > 0.05f)
                    AddSeg(new Vector3(c.x, terrainY + Lift, c.y), new Vector3(c.x, baseY, c.y), StemCol);
                if (_selected.Contains(idx))
                {
                    float rr = radius * 1.7f; Vector3 pr = default;
                    for (int i = 0; i <= 24; i++)
                    {
                        float a = i / 24f * Mathf.PI * 2f;
                        Vector3 p = new Vector3(c.x + Mathf.Cos(a) * rr, topY + 0.05f, c.y + Mathf.Sin(a) * rr);
                        if (i > 0) AddSeg(pr, p, SelCol);
                        pr = p;
                    }
                }
            }

            // Path-excavation / path-build: ring the armed START node so it's clear which end you've picked.
            if ((ExcavateSelectMode && idx == ExcavateStartNode) || (BuildSegmentMode && idx == BuildStartNode))
            {
                Color32 ringCol = ExcavateSelectMode ? ExcStartCol : BuildStartCol;
                float rr = radius * 1.9f; Vector3 pr = default;
                for (int i = 0; i <= 24; i++)
                {
                    float a = i / 24f * Mathf.PI * 2f;
                    Vector3 p = new Vector3(c.x + Mathf.Cos(a) * rr, topY + 0.06f, c.y + Mathf.Sin(a) * rr);
                    if (i > 0) AddSeg(pr, p, ringCol);
                    pr = p;
                }
            }

            EmitPuck(_nv, _nn, _nidx, c, radius, baseY, topY);
        }

        // Build the lane-node RECORDS (one per traffic lane at each corridor-edge end). Records only — the rendered
        // pucks are emitted on demand by SetRevealNode for the corridor node nearest the cursor (proximity reveal).
        void DrawLaneNodes(ITerrainSurface field)
        {
            float puckTop = Mathf.Max(0.02f, PlanGuides.NodePuckHeight);
            var bands = new List<NetworkDesigner.Roads.RoadCrossSectionBuilder.StackBand>();
            for (int ei = 0; ei < Graph.Edges.Count; ei++)
            {
                LineEdge e = Graph.Edges[ei];
                if (e == null || e.A < 0 || e.B < 0 || e.A >= Graph.Nodes.Count || e.B >= Graph.Nodes.Count) continue;
                var cfg = NetworkDesigner.Roads.RoadProfileLibrary.ResolveConfig(e.Profile);
                if (cfg == null || cfg.Corridor == null) continue;                   // corridor roads only
                bands.Clear();
                var xs = NetworkDesigner.Roads.RoadCrossSectionBuilder.FromStack(cfg.Corridor, bands);
                float center = xs.Center();
                EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
                float chord = Vector2.Distance(p0, p3);
                float inset = chord > 0.01f ? Mathf.Clamp01(2.5f / chord) : 0f;       // ~2.5 m in from each node end
                RecordLaneRow(field, p0, p1, p2, p3, inset, bands, center, puckTop, ei, 0);        // A end
                RecordLaneRow(field, p0, p1, p2, p3, 1f - inset, bands, center, puckTop, ei, 1);   // B end
                _nodesWithLaneNodes.Add(e.A); _nodesWithLaneNodes.Add(e.B);   // both ends are handled at the lane level
            }
        }

        void RecordLaneRow(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t,
                           List<NetworkDesigner.Roads.RoadCrossSectionBuilder.StackBand> bands, float center, float puckTop,
                           int edgeIndex, int end)
        {
            Vector2 pos = LineGraph.Bezier(p0, p1, p2, p3, t);
            Vector2 tan = LineGraph.BezierTangent(p0, p1, p2, p3, t);
            Vector2 perp = tan.sqrMagnitude > 1e-8f ? new Vector2(-tan.y, tan.x).normalized : Vector2.right;
            int lane = 0;
            float radius = Mathf.Max(0.09f, NodePuckRadius * 0.375f);
            foreach (var b in bands)
            {
                if (b.Type != NetworkDesigner.Model.CorridorType.Traffic) continue;
                float off = (b.U0 + b.U1) * 0.5f - center;
                Vector2 lp = pos + perp * off;
                float by = (field != null ? field.SampleHeight(lp.x, lp.y) : 0f) + Lift;
                _laneNodes.Add(new LaneNode { Edge = edgeIndex, End = end, Lane = lane++, Pos = lp, Y = by + puckTop });
                EmitPuck(_lnv, _lnn, _lnidx, lp, radius, by, by + puckTop);   // always-on: every lane node renders (segment nodes are hidden)
            }
        }

        // True if this lane node sits at graph node `nodeIdx` (its edge's A or B end).
        bool LaneNodeAtGraphNode(LaneNode ln, int nodeIdx)
        {
            if (ln.Edge < 0 || ln.Edge >= Graph.Edges.Count) return false;
            LineEdge e = Graph.Edges[ln.Edge];
            return ln.End == 0 ? e.A == nodeIdx : e.B == nodeIdx;
        }

        // Graph node to REVEAL lane pucks for: the node owning the lane record nearest `xz` (within maxDist), else -1.
        public int NearestLaneRevealNode(Vector2 xz, float maxDist)
        {
            int best = -1; float bestSq = maxDist * maxDist;
            foreach (LaneNode ln in _laneNodes)
            {
                float dsq = (ln.Pos - xz).sqrMagnitude;
                if (dsq < bestSq) { bestSq = dsq; best = ln.Edge >= 0 && ln.Edge < Graph.Edges.Count ? (ln.End == 0 ? Graph.Edges[ln.Edge].A : Graph.Edges[ln.Edge].B) : -1; }
            }
            return best;
        }

        // Render the lane pucks ONLY for the corridor node nearest the cursor (proximity reveal). Rebuilt on change.
        public void SetRevealNode(ITerrainSurface field, int nodeIdx)
        {
            if (_laneMr == null || nodeIdx == _revealNode) return;
            _revealNode = nodeIdx;
            _lnv.Clear(); _lnn.Clear(); _lnidx.Clear();
            int shown = 0;
            if (nodeIdx >= 0)
            {
                float radius = Mathf.Max(0.09f, NodePuckRadius * 0.375f);
                float puckTop = Mathf.Max(0.02f, PlanGuides.NodePuckHeight);
                foreach (LaneNode ln in _laneNodes)
                    if (LaneNodeAtGraphNode(ln, nodeIdx)) { EmitPuck(_lnv, _lnn, _lnidx, ln.Pos, radius, ln.Y - puckTop, ln.Y); shown++; }
            }
            _laneMesh.Clear();
            _laneMesh.SetVertices(_lnv); _laneMesh.SetNormals(_lnn); _laneMesh.SetTriangles(_lnidx, 0); _laneMesh.RecalculateBounds();
            _laneMr.enabled = shown > 0 && PlanGuides.ShowNodes && LinesVisible;
        }

        // A short low-poly cylinder puck (16-sided cap + side wall) into the given vertex/normal/triangle lists.
        // Shared by the base node mesh (DrawPuck) and the per-frame hover overlay.
        static void EmitPuck(List<Vector3> v, List<Vector3> nrm, List<int> idx, Vector2 c, float radius, float baseY, float topY)
        {
            const int N = 16;
            int capC = v.Count;
            v.Add(new Vector3(c.x, topY, c.y)); nrm.Add(Vector3.up);
            int capRim = v.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                v.Add(new Vector3(c.x + Mathf.Cos(a) * radius, topY, c.y + Mathf.Sin(a) * radius)); nrm.Add(Vector3.up);
            }
            for (int i = 0; i < N; i++) { idx.Add(capC); idx.Add(capRim + i + 1); idx.Add(capRim + i); }   // cap up

            int wTop = v.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f; float nx = Mathf.Cos(a), nz = Mathf.Sin(a);
                v.Add(new Vector3(c.x + nx * radius, topY, c.y + nz * radius)); nrm.Add(new Vector3(nx, 0f, nz));
            }
            int wBot = v.Count;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f; float nx = Mathf.Cos(a), nz = Mathf.Sin(a);
                v.Add(new Vector3(c.x + nx * radius, baseY, c.y + nz * radius)); nrm.Add(new Vector3(nx, 0f, nz));
            }
            for (int i = 0; i < N; i++)
            {
                int ti = wTop + i, tj = wTop + i + 1, bi = wBot + i, bj = wBot + i + 1;
                idx.Add(bi); idx.Add(ti); idx.Add(tj);   // outward
                idx.Add(bi); idx.Add(tj); idx.Add(bj);
            }
        }

        // Highlight the node under the cursor (golden, ~1.3× scale) in the per-frame overlay. Rebuilds the overlay
        // mesh ONLY when the hovered index changes, so it's cheap to call every frame. node < 0 hides it.
        public void SetHoverNode(ITerrainSurface field, int node)
        {
            if (_hoverMr == null || Graph == null) return;          // overlay not built yet (no Rebuild has run)
            if (node >= Graph.Nodes.Count) node = -1;
            if (node == _hoverNode) return;
            _hoverNode = node;
            _hoverMr.enabled = node >= 0 && PlanGuides.ShowNodes && LinesVisible;   // hover puck follows "Show nodes" + "Plan lines" + palette gate
            _hoverMesh.Clear();
            if (node < 0) return;
            Vector2 c = Graph.Nodes[node];
            float radius = Mathf.Max(0.2f, NodePuckRadius) * 1.3f;
            float terrainY = field != null ? field.SampleHeight(c.x, c.y) : 0f;
            float designY = Graph.GetNodeY(node);
            float dispY = float.IsNaN(designY) ? terrainY : designY;
            float baseY = dispY + Lift;
            float topY = baseY + Mathf.Max(0.02f, PlanGuides.NodePuckHeight) * 1.3f;
            _hv.Clear(); _hn.Clear(); _hidx.Clear();
            EmitPuck(_hv, _hn, _hidx, c, radius, baseY, topY);
            _hoverMesh.SetVertices(_hv); _hoverMesh.SetNormals(_hn); _hoverMesh.SetTriangles(_hidx, 0); _hoverMesh.RecalculateBounds();
        }

        // Screen-space pick of the nearest LANE node (blue puck) within pixelRadius; -1 if none. Mirrors PickNodeScreen.
        public int PickLaneNodeScreen(Camera cam, ITerrainSurface field, Vector2 screenPos, float pixelRadius)
        {
            if (cam == null || _laneNodes.Count == 0) return -1;
            int best = -1; float bestSq = pixelRadius * pixelRadius;
            for (int i = 0; i < _laneNodes.Count; i++)
            {
                LaneNode ln = _laneNodes[i];
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(ln.Pos.x, ln.Y, ln.Pos.y));
                if (sp.z <= 0f) continue;   // behind the camera
                float dsq = (new Vector2(sp.x, sp.y) - screenPos).sqrMagnitude;
                if (dsq < bestSq) { bestSq = dsq; best = i; }
            }
            return best;
        }

        // Hover-highlight a lane node (golden, scaled up). idx is into LaneNodes; -1 clears. Mirrors SetHoverNode.
        // showOverlay=false keeps the hover INDEX updated (the snap anchor needs it) but draws no golden puck — used in
        // road draw mode where the N-lane snap halo is the only highlight (per-lane golden hover would compete).
        public void SetHoverLane(ITerrainSurface field, int idx, bool showOverlay = true)
        {
            if (_laneHoverMr == null) return;                       // overlay not built yet
            if (idx >= _laneNodes.Count) idx = -1;
            bool changed = idx != _hoverLane;
            _hoverLane = idx;                                       // always track the index (snap anchor)
            if (!showOverlay) { if (_laneHoverMr.enabled) { _laneHoverMr.enabled = false; _laneHoverMesh.Clear(); } return; }
            if (!changed) return;
            _laneHoverMr.enabled = idx >= 0 && PlanGuides.ShowNodes && LinesVisible;
            _laneHoverMesh.Clear();
            if (idx < 0) return;
            LaneNode ln = _laneNodes[idx];
            float radius = Mathf.Max(0.09f, NodePuckRadius * 0.375f) * 1.5f;   // a touch larger than the lane puck
            float topY = ln.Y;                                                 // ln.Y is already the puck top
            float baseY = topY - Mathf.Max(0.02f, PlanGuides.NodePuckHeight);
            _lhv.Clear(); _lhn.Clear(); _lhidx.Clear();
            EmitPuck(_lhv, _lhn, _lhidx, ln.Pos, radius, baseY, topY + 0.03f);
            _laneHoverMesh.SetVertices(_lhv); _laneHoverMesh.SetNormals(_lhn); _laneHoverMesh.SetTriangles(_lhidx, 0); _laneHoverMesh.RecalculateBounds();
        }

        // Select a lane node. additive (same edge+end) EXTENDS the contiguous range; otherwise starts a fresh
        // single-lane selection. idx is into LaneNodes.
        public void SelectLaneNode(int idx, bool additive, ITerrainSurface field)
        {
            if (idx < 0 || idx >= _laneNodes.Count) return;
            LaneNode ln = _laneNodes[idx];
            if (!additive || ln.Edge != _selEdge || ln.End != _selEnd)
            {
                _selEdge = ln.Edge; _selEnd = ln.End; _selLo = _selHi = ln.Lane;
            }
            else
            {
                _selLo = Mathf.Min(_selLo, ln.Lane);   // range [lo..hi] is contiguous by construction
                _selHi = Mathf.Max(_selHi, ln.Lane);
            }
            RebuildLaneSelOverlay(field);
        }

        public void ClearLaneSelection(ITerrainSurface field)
        {
            _selEdge = _selEnd = _selLo = _selHi = -1;
            RebuildLaneSelOverlay(field);
        }

        // (Re)build the green selection overlay from the current selection range, matching lane nodes by identity
        // (edge,end,lane) so it survives index shifts after a Rebuild.
        void RebuildLaneSelOverlay(ITerrainSurface field)
        {
            if (_laneSelMr == null) return;
            _lsv.Clear(); _lsn.Clear(); _lsidx.Clear();
            float radius = Mathf.Max(0.09f, NodePuckRadius * 0.375f) * 1.35f;
            float puckTop = Mathf.Max(0.02f, PlanGuides.NodePuckHeight);
            int n = 0;
            if (_selEdge >= 0)
                foreach (LaneNode ln in _laneNodes)
                    if (ln.Edge == _selEdge && ln.End == _selEnd && ln.Lane >= _selLo && ln.Lane <= _selHi)
                    {
                        EmitPuck(_lsv, _lsn, _lsidx, ln.Pos, radius, ln.Y - puckTop, ln.Y + 0.04f);
                        n++;
                    }
            _laneSelMesh.Clear();
            _laneSelMesh.SetVertices(_lsv); _laneSelMesh.SetNormals(_lsn); _laneSelMesh.SetTriangles(_lsidx, 0); _laneSelMesh.RecalculateBounds();
            _laneSelMr.enabled = n > 0 && PlanGuides.ShowNodes && LinesVisible;
        }

        // PHASE 3: extend a new segment off the selected lane group to `endXZ`. The new edge starts at the selected
        // lanes' CENTROID (so it diverges from those lanes), carries the active design profile, and records a
        // LaneAttach back to the source lanes (drives connectivity in Phase 4). Returns false if nothing's selected
        // or the span is degenerate. Fork semantics: the source keeps all its lanes; this is an additional branch.
        public bool ExtendFromLaneSelection(ITerrainSurface field, Vector2 endXZ)
        {
            if (_selEdge < 0) return false;
            Vector2 sum = Vector2.zero; int cnt = 0;
            foreach (LaneNode ln in _laneNodes)
                if (ln.Edge == _selEdge && ln.End == _selEnd && ln.Lane >= _selLo && ln.Lane <= _selHi) { sum += ln.Pos; cnt++; }
            if (cnt == 0) return false;
            Vector2 start = sum / cnt;
            if ((endXZ - start).sqrMagnitude < 1f) return false;   // too short to be a segment

            int k = _selHi - _selLo + 1;   // lanes pulled off
            int newLanes = ProfileTrafficLaneCount(ActiveProfileId);
            if (newLanes >= 0 && newLanes != k)
                Debug.LogWarning($"[Road] Lane-extend: pulled {k} lane(s) but the active profile '{ActiveProfileId}' has " +
                                 $"{newLanes} traffic lane(s). The new segment's free end will expose {newLanes} lane node(s), " +
                                 $"not {k}. Pick a {k}-lane profile to match the lanes you're extending.");

            int a = Graph.AddNode(start);
            int b = Graph.AddNode(endXZ);
            if (!Graph.AddEdge(a, b)) return false;
            LineEdge e = Graph.Edges[Graph.Edges.Count - 1];
            e.Profile = ActiveProfileId;
            e.Attach = new LaneAttach { SourceEdge = _selEdge, SourceEnd = _selEnd, FirstLane = _selLo, LaneCount = k };

            _selEdge = _selEnd = _selLo = _selHi = -1;   // consume the selection
            Rebuild(field);
            return true;
        }

        // Traffic-lane count of a profile's corridor stack; -1 if the profile has no corridor (can't count lanes).
        static int ProfileTrafficLaneCount(string profileId)
        {
            var cfg = NetworkDesigner.Roads.RoadProfileLibrary.ResolveConfig(profileId);
            if (cfg == null || cfg.Corridor == null) return -1;
            var bands = new List<NetworkDesigner.Roads.RoadCrossSectionBuilder.StackBand>();
            NetworkDesigner.Roads.RoadCrossSectionBuilder.FromStack(cfg.Corridor, bands);
            int n = 0;
            foreach (var b in bands) if (b.Type == NetworkDesigner.Model.CorridorType.Traffic) n++;
            return n;
        }

        // With an N-lane profile active, snap a new road's START onto the N contiguous lane nodes of an existing road
        // end nearest the cursor (1-lane → 1 node, 2-lane → 2 contiguous, …). Creates the start node at their centroid
        // and returns the attach. False if the profile has no lanes, no lane node is near, or the end has fewer than N.
        public bool PlainDrawMode => !ShowAllSegmentPucks;
        readonly List<Vector2> _snapTargets = new List<Vector2>();   // scratch: the N existing lane positions a snap lands on
        readonly List<float> _snapOffsets = new List<float>();       // scratch: the active profile's traffic-lane lateral offsets
        public float CursorHeading;   // road heading (radians) for the free-floating cursor preview; Alt+scroll rotates it

        // Non-destructive lane snap: for the active N-lane profile, find the N contiguous lane nodes of an existing road
        // end nearest the cursor; output their centroid (the new road's start), the attach, and (optionally) the N target
        // positions. Anchors on the hovered lane node (zoom-independent 26px pick) or a generous world search.
        bool ComputeLaneSnap(Vector2 cursor, out Vector2 centroid, out LaneAttach attach, List<Vector2> targetsOut)
        {
            centroid = default; attach = null; targetsOut?.Clear();
            int n = ProfileTrafficLaneCount(ActiveProfileId);
            if (n <= 0 || _laneNodes.Count == 0) return false;
            // Anchor STRICTLY on the hovered lane node (the 26px screen pick) so preview and commit are identical and
            // position-independent. A world-radius fallback diverged here: the commit cursor is the centre-snapped
            // 'place', so it grabbed the centre lanes and centred the new road. Removed — hover a lane to snap.
            if (_hoverLane < 0 || _hoverLane >= _laneNodes.Count) return false;
            int best = _hoverLane;
            LaneNode b = _laneNodes[best];
            int laneCountAtEnd = 0;
            foreach (LaneNode ln in _laneNodes) if (ln.Edge == b.Edge && ln.End == b.End) laneCountAtEnd++;
            if (laneCountAtEnd < n) return false;                          // end has fewer lanes than the new road
            int lo = Mathf.Clamp(b.Lane - n / 2, 0, laneCountAtEnd - n);   // contiguous window of n lanes containing the nearest
            int hi = lo + n - 1;
            Vector2 sum = Vector2.zero; int cnt = 0;
            foreach (LaneNode ln in _laneNodes)
                if (ln.Edge == b.Edge && ln.End == b.End && ln.Lane >= lo && ln.Lane <= hi) { sum += ln.Pos; cnt++; targetsOut?.Add(ln.Pos); }
            if (cnt != n) return false;
            centroid = sum / cnt;
            attach = new LaneAttach { SourceEdge = b.Edge, SourceEnd = b.End, FirstLane = lo, LaneCount = n };
            return true;
        }

        bool TrySnapStartToLaneGroup(Vector2 cursor, out int startNode, out LaneAttach attach)
        {
            startNode = -1;
            if (!ComputeLaneSnap(cursor, out Vector2 c, out attach, null)) return false;
            startNode = Graph.AddNode(c);
            return true;
        }

        // The active profile's traffic-lane lateral offsets from the corridor centre (for the free-floating ghost row).
        void ProfileTrafficLaneOffsets(string profileId, List<float> offsetsOut)
        {
            offsetsOut.Clear();
            var cfg = NetworkDesigner.Roads.RoadProfileLibrary.ResolveConfig(profileId);
            if (cfg == null || cfg.Corridor == null) return;
            var bands = new List<NetworkDesigner.Roads.RoadCrossSectionBuilder.StackBand>();
            var xs = NetworkDesigner.Roads.RoadCrossSectionBuilder.FromStack(cfg.Corridor, bands);
            float center = xs.Center();
            foreach (var bd in bands) if (bd.Type == NetworkDesigner.Model.CorridorType.Traffic) offsetsOut.Add((bd.U0 + bd.U1) * 0.5f - center);
        }

        // Cursor preview: render one ghost lane node per traffic lane of the active profile. When the cursor is near an
        // existing road end the ghosts SNAP onto that end's matching contiguous lane nodes; otherwise they follow the
        // cursor in a row (spread along camera-right). Reuses the (now-free) green selection overlay mesh.
        public void UpdateLaneSnapPreview(ITerrainSurface field, Camera cam, Vector2 cursor, bool active)
        {
            if (_laneSelMr == null) return;
            _lsv.Clear(); _lsn.Clear(); _lsidx.Clear();
            int shown = 0;
            if (active)
            {
                float baseR = Mathf.Max(0.09f, NodePuckRadius * 0.375f);
                float puckTop = Mathf.Max(0.02f, PlanGuides.NodePuckHeight);
                if (ComputeLaneSnap(cursor, out _, out _, _snapTargets) && _snapTargets.Count > 0)
                {
                    float halo = baseR * 1.8f;   // snapped → a clear halo on each of the N target lane nodes
                    foreach (Vector2 p in _snapTargets)
                    { float by = (field != null ? field.SampleHeight(p.x, p.y) : 0f) + Lift; EmitPuck(_lsv, _lsn, _lsidx, p, halo, by, by + puckTop); shown++; }
                }
                else
                {
                    float r = baseR * 1.2f;
                    ProfileTrafficLaneOffsets(ActiveProfileId, _snapOffsets);
                    Vector2 dir = new Vector2(Mathf.Cos(CursorHeading), Mathf.Sin(CursorHeading));   // road heading (Alt+scroll rotates)
                    Vector2 perp = new Vector2(-dir.y, dir.x);                                        // across the road = lane spread
                    foreach (float off in _snapOffsets)
                    { Vector2 p = cursor + perp * off; float by = (field != null ? field.SampleHeight(p.x, p.y) : 0f) + Lift; EmitPuck(_lsv, _lsn, _lsidx, p, r, by, by + puckTop); shown++; }
                }
            }
            _laneSelMesh.Clear();
            _laneSelMesh.SetVertices(_lsv); _laneSelMesh.SetNormals(_lsn); _laneSelMesh.SetTriangles(_lsidx, 0); _laneSelMesh.RecalculateBounds();
            _laneSelMr.enabled = shown > 0 && PlanGuides.ShowNodes && LinesVisible;
        }

        // Highlight the OPEN chain's tail (vivid green, ~1.5×) so an in-progress chain is never invisible — right-click
        // ends it. Rebuilds only when the tail node (or its position, after an index shift) changes. `active`=false (road
        // not the live layer) hides it. Call every frame alongside SetHoverNode.
        public void RefreshTailHighlight(ITerrainSurface field, bool active)
        {
            if (_tailMr == null || Graph == null) return;
            int node = (active && _chainTail >= 0 && _chainTail < Graph.Nodes.Count) ? _chainTail : -1;
            Vector2 pos = node >= 0 ? Graph.Nodes[node] : Vector2.zero;
            if (node == _tailShown && (node < 0 || pos == _tailPosShown)) return;
            _tailShown = node; _tailPosShown = pos;
            _tailMr.enabled = node >= 0 && PlanGuides.ShowNodes && LinesVisible;
            _tailMesh.Clear();
            if (node < 0) return;
            float radius = Mathf.Max(0.09f, NodePuckRadius * 0.45f);   // small tail dot (lane-node scale) — not the old giant disc
            float terrainY = field != null ? field.SampleHeight(pos.x, pos.y) : 0f;
            float designY = Graph.GetNodeY(node);
            float baseY = (float.IsNaN(designY) ? terrainY : designY) + Lift;
            float topY = baseY + Mathf.Max(0.02f, PlanGuides.NodePuckHeight);
            _tv.Clear(); _tn.Clear(); _tidx.Clear();
            EmitPuck(_tv, _tn, _tidx, pos, radius, baseY, topY);
            _tailMesh.SetVertices(_tv); _tailMesh.SetNormals(_tn); _tailMesh.SetTriangles(_tidx, 0); _tailMesh.RecalculateBounds();
        }

        // Edge indices touching node `n` (for deleting a node + its segments and remapping the built-road set).
        public List<int> EdgesTouchingNode(int n)
        {
            var list = new List<int>();
            if (Graph == null) return list;
            for (int i = 0; i < Graph.Edges.Count; i++) { LineEdge e = Graph.Edges[i]; if (e.A == n || e.B == n) list.Add(i); }
            return list;
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

            // PROTOTYPE: lane-node puck layer (one blue puck per traffic lane at each segment end).
            _laneGo = new GameObject(RootName + "_LaneNodes") { hideFlags = HideFlags.DontSave };
            _laneGo.transform.SetParent(_root.transform, false);
            _laneMf = _laneGo.AddComponent<MeshFilter>();
            _laneMr = _laneGo.AddComponent<MeshRenderer>();
            _laneMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _laneMr.receiveShadows = false;
            _laneMesh = new Mesh { name = "RoadPlanLaneNodesMesh" };
            _laneMf.sharedMesh = _laneMesh;
            _laneMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(_RoadLaneNodeColor, 0.2f, "RoadPlanLaneNodeMat");
            _laneMr.sharedMaterial = _laneMat;

            // Lane-node hover overlay (golden, like the corridor node hover).
            _laneHoverGo = new GameObject(RootName + "_LaneHover") { hideFlags = HideFlags.DontSave };
            _laneHoverGo.transform.SetParent(_root.transform, false);
            _laneHoverMf = _laneHoverGo.AddComponent<MeshFilter>();
            _laneHoverMr = _laneHoverGo.AddComponent<MeshRenderer>();
            _laneHoverMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _laneHoverMr.receiveShadows = false;
            _laneHoverMesh = new Mesh { name = "RoadPlanLaneHoverMesh" };
            _laneHoverMf.sharedMesh = _laneHoverMesh;
            _laneHoverMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(_RoadNodeHoverColor, 0.2f, "RoadPlanLaneHoverMat");
            _laneHoverMr.sharedMaterial = _laneHoverMat;
            _laneHoverMr.enabled = false;

            // Lane-node SELECTION overlay (bright green, persistent).
            _laneSelGo = new GameObject(RootName + "_LaneSel") { hideFlags = HideFlags.DontSave };
            _laneSelGo.transform.SetParent(_root.transform, false);
            _laneSelMf = _laneSelGo.AddComponent<MeshFilter>();
            _laneSelMr = _laneSelGo.AddComponent<MeshRenderer>();
            _laneSelMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _laneSelMr.receiveShadows = false;
            _laneSelMesh = new Mesh { name = "RoadPlanLaneSelMesh" };
            _laneSelMf.sharedMesh = _laneSelMesh;
            _laneSelMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(_RoadLaneSelColor, 0.2f, "RoadPlanLaneSelMat");
            _laneSelMr.sharedMaterial = _laneSelMat;
            _laneSelMr.enabled = false;

            _hoverGo = new GameObject(RootName + "_NodeHover") { hideFlags = HideFlags.DontSave };
            _hoverGo.transform.SetParent(_root.transform, false);
            _hoverMf = _hoverGo.AddComponent<MeshFilter>();
            _hoverMr = _hoverGo.AddComponent<MeshRenderer>();
            _hoverMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _hoverMr.receiveShadows = false;
            _hoverMesh = new Mesh { name = "RoadPlanNodeHoverMesh" };
            _hoverMf.sharedMesh = _hoverMesh;
            _hoverMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(_RoadNodeHoverColor, 0.2f, "RoadPlanNodeHoverMat");
            _hoverMr.sharedMaterial = _hoverMat;
            _hoverMr.enabled = false;
            _hoverNode = -1;   // overlay mesh is fresh → force a rebuild on the next SetHoverNode

            _tailGo = new GameObject(RootName + "_NodeTail") { hideFlags = HideFlags.DontSave };
            _tailGo.transform.SetParent(_root.transform, false);
            _tailMf = _tailGo.AddComponent<MeshFilter>();
            _tailMr = _tailGo.AddComponent<MeshRenderer>();
            _tailMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _tailMr.receiveShadows = false;
            _tailMesh = new Mesh { name = "RoadPlanNodeTailMesh" };
            _tailMf.sharedMesh = _tailMesh;
            _tailMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(_RoadTailColor, 0.2f, "RoadPlanNodeTailMat");
            _tailMr.sharedMaterial = _tailMat;
            _tailMr.enabled = false;
            _tailShown = -1;   // force a rebuild on the next RefreshTailHighlight
        }

        Material MakeMat(Color c, string name)
        {
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            return sh != null ? new Material(sh) { name = name, color = c }
                              : NetworkDesigner.PipelineMaterials.CreateUnlitColor(c, name);
        }

        // ---- placement preview (ghost puck + dashed pending edge) ----

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; if (_gMr != null) _gMr.enabled = false; HideSymRing(); }

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
            if (_gMr != null) _gMr.enabled = false;   // default off each frame; DrawTargetGuides re-enables when it draws
            LastPreviewRadius = float.PositiveInfinity; LastPreviewTooTight = false;
            PreviewCurveActive = false; PreviewStraightActive = false;
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

            // Collinear extension guide of a nearby existing endpoint (the road you'd meet head-on) — shown in
            // every placement mode so you can align the new corridor to it; TrySnapToTargetExtension snaps to it.
            // Reactive node guides (ported from NetworkDesigner): existing nodes within the proximity-snap radius
            // project a collinear extension + a perpendicular guide on the cursor's side; the cursor snaps to the
            // nearest such line via TrySnapToGuides. Only active while a new segment is being drawn.
            CollectTargetGuides(new Vector2(cursor.x, cursor.z));
            DrawTargetGuides(field, new Vector2(cursor.x, cursor.z));

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
                Vector2 curXZ = new Vector2(cursor.x, cursor.z);
                DashLeg(field, tnode, curXZ);
                // Live distance label feed (also the FIRST leg while arming a curve, which routes through here).
                PreviewStraightActive = true; PreviewStraightFrom = tnode; PreviewStraightTo = curXZ;
                PreviewStraightDist = Vector2.Distance(tnode, curXZ);
            }
            // (The chain tail's colinear/perpendicular guides now come from the reactive guide system above —
            // CollectTargetGuides includes the chain tail and emits a guide per incident edge.)
            _pvMesh.Clear(); _pvMesh.subMeshCount = 2; _pvMesh.SetVertices(_pv);
            _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0); _pvMesh.SetIndices(_pvBadIdx, MeshTopology.Lines, 1);
            _pvMesh.RecalculateBounds();
        }

        // A dashed ray from origin `o` along unit `dir` for `len` metres (a heading guide).
        void DashGuide(ITerrainSurface field, Vector2 o, Vector2 dir, float len)
        {
            int gn = Mathf.Clamp(Mathf.CeilToInt(len / 1.2f), 4, 800);   // ~1.2 m dash + 1.2 m gap (small dashes)
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

        public LineGraphSave CollectData() => new LineGraphSave { Nodes = new List<Vector2>(Graph.Nodes), Edges = new List<LineEdge>(Graph.Edges), NodeY = new List<float>(Graph.NodeY) };

        public void LoadState(LineGraphSave save)
        {
            _graph = new LineGraph();
            _chainTail = -1;
            if (save != null)
            {
                if (save.Nodes != null) _graph.Nodes.AddRange(save.Nodes);
                if (save.Edges != null) _graph.Edges.AddRange(save.Edges);
                if (save.NodeY != null) _graph.NodeY.AddRange(save.NodeY);
            }
            // Keep NodeY strictly parallel to Nodes (older saves / partial data → pad/trim with NaN).
            while (_graph.NodeY.Count < _graph.Nodes.Count) _graph.NodeY.Add(float.NaN);
            if (_graph.NodeY.Count > _graph.Nodes.Count) _graph.NodeY.RemoveRange(_graph.Nodes.Count, _graph.NodeY.Count - _graph.Nodes.Count);
        }
    }
}
