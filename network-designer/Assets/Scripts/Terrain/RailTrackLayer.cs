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

            // First click of a chain: resume from an existing node, or branch off an
            // existing track mid-span (split it into a junction), else drop a fresh
            // anchor. This is how you start a turnout off an existing line.
            if (_chainTail < 0)
            {
                int near = Graph.NearestNode(p, NodePickRadius);
                if (near >= 0) _chainTail = near;
                else if (Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _))
                {
                    _chainTail = Graph.SplitEdge(ei, tt);   // branch off existing track
                    Rebuild(field);                          // split changed the geometry
                }
                else _chainTail = Graph.AddNode(p);
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
                CurveControls(start, p, _corner, out Vector2 c1, out Vector2 c2);
                if (MinCurveRadius(start, c1, c2, p) < MinRadiusForSpeed) return; // too tight
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

        public void EndChain() { _chainTail = -1; _cornerPending = false; }

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
                _root = new GameObject(RootName);
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
            go = new GameObject(childName);
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
            _netGo = new GameObject(RootName + "_Network");
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

        // Heading that continues straight out of the chain tail's incoming edge
        // (i.e. a 180° / collinear continuation). False when the tail has no edge.
        bool IncomingDirection(out Vector2 dir)
        {
            dir = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            for (int i = Graph.Edges.Count - 1; i >= 0; i--) // most recent edge first
            {
                LineEdge e = Graph.Edges[i];
                if (e.A != _chainTail && e.B != _chainTail) continue;
                Vector2 p0 = Graph.Nodes[e.A], p3 = Graph.Nodes[e.B], q1, q2;
                if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
                else { Vector2 d = p3 - p0; q1 = p0 + d / 3f; q2 = p0 + d * (2f / 3f); }
                // Direction leaving the tail, continuing the way the track arrived.
                dir = e.B == _chainTail ? LineGraph.BezierTangent(p0, q1, q2, p3, 1f)
                                        : -LineGraph.BezierTangent(p0, q1, q2, p3, 0f);
                if (dir.sqrMagnitude < 1e-6f) dir = e.B == _chainTail ? p3 - p0 : p0 - p3;
                if (dir.sqrMagnitude < 1e-6f) return false;
                dir = dir.normalized;
                return true;
            }
            return false;
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
            if (r <= 0f || !IncomingDirection(out Vector2 dir)) return false;
            Vector2 origin = Graph.Nodes[_chainTail];
            float along = Vector2.Dot(cursor - origin, dir);
            if (along <= 0f || along > ExtensionGuideLength) return false; // behind tail / past guide
            Vector2 proj = origin + dir * along; // foot of the perpendicular on the ray
            if ((cursor - proj).sqrMagnitude > r * r) return false;
            snapped = proj;
            return true;
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
                if (IncomingDirection(out Vector2 ext))
                    EmitDashed(field, start, start + ext * ExtensionGuideLength, Vector2.zero, lift);

                if (_cornerPending)
                {
                    EmitDashed(field, start, _corner, Vector2.zero, lift); // construction legs
                    EmitDashed(field, _corner, cur, Vector2.zero, lift);
                    CurveControls(start, cur, _corner, out Vector2 c1, out Vector2 c2);
                    LastPreviewRadius = MinCurveRadius(start, c1, c2, cur);
                    LastPreviewTooTight = LastPreviewRadius < MinRadiusForSpeed;
                    float bLen = PreviewScan(field, start, c1, c2, cur);
                    DrawGradedRails(start, c1, c2, cur, yA, yB, lift, bLen);
                }
                else if (CurveModifier)
                {
                    EmitDashed(field, start, cur, Vector2.zero, lift); // arming the corner
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

        void EmitDashed(TerrainField field, Vector2 a, Vector2 b, Vector2 offset, float lift)
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
                AddSeg(p0, p1);
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
            _pvGo = new GameObject(RootName + "_Preview");
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
