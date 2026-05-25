// Top-level renderer for a Network. Resolves the network, then for each
// vertex spawns an IntersectionRenderer (the asphalt polygon at that
// vertex) and for each road spawns a RoadRenderer (the road body
// between the two end-setback midpoints).
//
// Rebuild() destroys any existing child renderers before re-spawning,
// so it's safe to call repeatedly.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Geometry;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    public class NetworkRenderer : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Network to render. Assigned from external code (e.g. NetworkMeshTest).")]
        // [System.NonSerialized] is load-bearing. Unity's UIElements
        // Inspector data-binding walks every [Serializable] field of a
        // public reference and AUTO-INSTANTIATES null class fields into
        // default instances when SerializedObject.UpdateIfRequiredOrScript
        // touches them. With this field plain-public, selecting this
        // GameObject in the Inspector silently stamps `new RoadCurve()`
        // (zero controls) onto every NetworkRoad whose Curve was null —
        // turning straight roads into wild curves through the origin.
        // [NonSerialized] hides the field from Unity's serializer (which
        // is what we want here — the Network is wired up programmatically
        // by NetworkDesigner.Rebuild(), not edited in the Inspector).
        [System.NonSerialized] public Network Network;

        [Header("Materials (shared across all children when set)")]
        public Material AsphaltMaterial;
        public Material ShoulderMaterial;
        public Material MedianMaterial;
        public Material CenterlineMaterial;
        public Material LaneMarkingMaterial;
        public Material ArrowMaterial;

        [Header("Default colors when materials are auto-created")]
        public Color AsphaltColor = new Color(0.18f, 0.18f, 0.19f);
        public Color ShoulderColor = new Color(0.55f, 0.55f, 0.55f);
        public Color MedianColor = new Color(0.95f, 0.78f, 0.25f);
        public Color CenterlineColor = new Color(0.95f, 0.78f, 0.15f);
        public Color LaneMarkingColor = new Color(0.95f, 0.95f, 0.95f);
        public Color ArrowColor = new Color(0.95f, 0.95f, 0.95f);

        [Header("Lane markings (applied to every road)")]
        public bool DrawLaneMarkings = true;
        public float LineWidth = 0.15f;
        public float DashLength = 3f;
        public float DashGap = 9f;
        public float MarkingHeight = 0.01f;
        [Tooltip("How far short of the setback line markings stop, at each road end. 0 = stop right at the setback.")]
        public float MarkingEndInset = 0f;

        [Header("One-way arrows (applied to every road)")]
        public bool DrawArrows = true;
        public float ArrowLength = 3f;
        public float ArrowWidth = 1.5f;
        public float ArrowSpacing = 25f;

        [Header("Surface UVs (applied to every road + intersection)")]
        [Tooltip("World meters per UV unit. Lower = denser tiling. PBR ground textures usually want 1–4m.")]
        public float UvTileSize = 2f;

        [Header("Z-fight prevention")]
        [Tooltip("Small Y offset applied to every spawned intersection/road GameObject so it doesn't fight with the ground plane at Y=0. Stays below the lane markings' own MarkingHeight.")]
        public float MeshLift = 0.005f;

        [Header("Render mode")]
        [Tooltip("If true, render each road as a simple LineRenderer between its two vertices " +
                 "and skip all asphalt/intersection meshes. Use during graph-topology work; " +
                 "switch off to get the full road-body + intersection mesh visualization.")]
        public bool SimpleRender = false;

        [Header("Curve tessellation")]
        [Tooltip("Number of segments used to tessellate curved road bodies. Forwarded to RoadRenderer.")]
        [Range(2, 64)] public int CurveTessellation = 24;

        [Header("Simple-render line styling")]
        public Color RoadLineColor = new Color(0.85f, 0.85f, 0.95f);
        public float RoadLineWidth = 0.4f;

        [Header("Tessellation")]
        [Range(2, 64)] public int BezierSamplesPerSegment = 16;

        // Spawned-object pools keyed by entity id so successive Rebuilds
        // can reuse GameObjects + Meshes instead of churning them.
        readonly Dictionary<string, GameObject> _intersectionPool = new Dictionary<string, GameObject>();
        readonly Dictionary<string, GameObject> _roadPool = new Dictionary<string, GameObject>();
        readonly Dictionary<string, GameObject> _roadLinePool = new Dictionary<string, GameObject>();
        readonly Dictionary<string, GameObject> _culDeSacPool = new Dictionary<string, GameObject>();
        readonly Dictionary<string, GameObject> _laneFlowPool = new Dictionary<string, GameObject>();
        readonly Dictionary<string, GameObject> _approachMarkingsPool = new Dictionary<string, GameObject>();
        readonly Dictionary<string, GameObject> _signsPool = new Dictionary<string, GameObject>();
        readonly Dictionary<string, GameObject> _laneMarkingsPool = new Dictionary<string, GameObject>();
        readonly Dictionary<string, GameObject> _deadEndCapPool = new Dictionary<string, GameObject>();

        readonly HashSet<string> _liveVerticesScratch = new HashSet<string>();
        readonly HashSet<string> _liveRoadsScratch = new HashSet<string>();
        readonly HashSet<string> _liveCulDeSacScratch = new HashSet<string>();
        readonly HashSet<string> _liveLaneFlowScratch = new HashSet<string>();
        readonly HashSet<string> _liveApproachMarkingsScratch = new HashSet<string>();
        readonly HashSet<string> _liveSignsScratch = new HashSet<string>();
        readonly HashSet<string> _liveLaneMarkingsScratch = new HashSet<string>();
        readonly HashSet<string> _liveDeadEndCapScratch = new HashSet<string>();

        [Header("Dead-end caps")]
        public bool ShowDeadEndCaps = true;
        [Range(8, 64)] public int DeadEndCapSegments = 24;

        [Header("Lane markings (painted intersection guides)")]
        public bool ShowLaneMarkings = true;
        public Color LaneMarkingWhite = Color.white;
        public Color LaneMarkingYellow = new Color(1f, 0.85f, 0.2f, 1f);
        [Range(0f, 1f)] public float LaneMarkingAlpha = 1f;
        public float LaneMarkingWidth = 0.18f;
        public float LaneMarkingHeight = 0.013f;
        public float LaneMarkingDashLength = 0.7f;
        public float LaneMarkingDashGap = 0.6f;
        [Range(8, 64)] public int LaneMarkingBezierSamples = 24;

        [Header("Traffic signs")]
        public bool ShowSigns = true;
        public Color SignTint = Color.white;
        [Range(0f, 1f)] public float SignAlpha = 1f;
        public float SignSize = 2.5f;
        public float SignShoulderClearance = 1.5f;
        public float SignAlongOffset = 0.5f;
        public float SignHeight = 0.03f;
        [Tooltip("Optional 3D prefab for Stop signs (e.g. CS - Signs Free Sign_Stop). When assigned, instantiated instead of the flat top-down quad. Pivot is expected at ground level.")]
        public GameObject StopSignPrefab;
        [Tooltip("Optional 3D prefab for Yield signs.")]
        public GameObject YieldSignPrefab;
        [Tooltip("Optional sign-post prefab (e.g. CS - Signs Free SignPost_1) — instantiated as a child of each sign so the panel has a real pole holding it up. Same pivot conventions as the sign prefab.")]
        public GameObject SignPostPrefab;

        [Header("Pavement markings")]
        [Tooltip("Master toggle for per-approach Control paint. Each inbound approach paints based on its Control field (Stop→stop line, Yield→sharks teeth, None→nothing).")]
        public bool ShowPaintedControls = true;
        public bool ShowLaneArrows = true;
        public Color PavementMarkingColor = new Color(0.95f, 0.95f, 0.95f, 1f);
        [Tooltip("Master alpha for all pavement markings (stop lines, yield triangles, lane arrows). Multiplies the alpha channel of PavementMarkingColor so you can dim everything without re-picking the color.")]
        [Range(0f, 1f)] public float PavementMarkingAlpha = 1.0f;
        public float PavementMarkingHeight = 0.012f;
        public float StopLineWidth = 0.6f;
        public float StopLineInsetFromSetback = 0.2f;
        public float YieldTriangleBase = 0.6f;
        public float YieldTriangleHeight = 0.9f;
        public float YieldTriangleSpacing = 0.4f;
        public float YieldInsetFromSetback = 0.2f;
        [Tooltip("Side length of the textured lane-arrow quad (meters). Used when the PNG for the lane's destination combination is found under Resources/lane-arrows/.")]
        public float LaneArrowSize = 3.5f;
        [Tooltip("Distance from setback line into road body where the arrow center sits.")]
        public float LaneArrowOffset = 4f;
        // Fallback mesh-glyph dimensions, used when the matching texture
        // for an arrow kind isn't present in Resources/lane-arrows/.
        public float LaneArrowLength = 3.0f;
        public float LaneArrowShaftWidth = 0.4f;
        public float LaneArrowHeadLength = 0.9f;
        public float LaneArrowHeadWidth = 1.3f;
        public float LaneArrowStackSpacing = 4.0f;
        public float StraightTurnThresholdDeg = 30f;

        [Header("Lane-flow overlay")]
        [Tooltip("When on, draws bezier-shaped dashed arrows per lane connection at the selected vertex (Edit mode).")]
        public bool ShowLaneFlow = true;
        [Tooltip("World Y for flow-arrow geometry (above asphalt to avoid z-fight).")]
        public float LaneFlowHeight = 0.02f;
        [Range(0f, 1f)] public float LaneFlowDimAlpha = 0.25f;
        [Range(0f, 1f)] public float LaneFlowFullAlpha = 1.0f;
        [Tooltip("Arrow shaft width (meters).")]
        public float LaneFlowShaftWidth = 0.8f;
        [Tooltip("Length of each dash along the shaft.")]
        public float LaneFlowDashLength = 1.2f;
        [Tooltip("Gap between dashes.")]
        public float LaneFlowDashGap = 0.9f;
        [Tooltip("Length of the arrowhead triangle.")]
        public float LaneFlowHeadLength = 2.5f;
        [Tooltip("Width of the arrowhead triangle base.")]
        public float LaneFlowHeadWidth = 2.5f;
        [Range(8, 64)] public int LaneFlowBezierSamples = 24;
        [Tooltip("Control-point distance per endpoint as a fraction of the chord length. 0 = straight; ~0.5 = pronounced curve through the intersection.")]
        [Range(0f, 1f)] public float LaneFlowBezierControlFraction = 0.45f;

        // Cached asphalt material used when AsphaltMaterial wasn't set in
        // the inspector. IntersectionRenderer/RoadRenderer create their
        // own when needed; cul-de-sac discs share one here so all bulbs
        // use the same instance.
        Material _autoAsphaltMaterial;

        [Header("Cul-de-sac fill")]
        [Range(8, 96)] public int CulDeSacFillSegments = 48;

        void Start()
        {
            if (Network != null) Rebuild();
        }

        public void Rebuild()
        {
            if (Network == null)
            {
                ClearPool(_intersectionPool);
                ClearPool(_roadPool);
                ClearPool(_roadLinePool);
                ClearPool(_culDeSacPool);
                ClearPool(_laneFlowPool);
                ClearPool(_approachMarkingsPool);
                ClearPool(_signsPool);
                ClearPool(_laneMarkingsPool);
                ClearPool(_deadEndCapPool);
                return;
            }

            _liveVerticesScratch.Clear();
            _liveRoadsScratch.Clear();
            _liveCulDeSacScratch.Clear();
            _liveLaneFlowScratch.Clear();
            _liveApproachMarkingsScratch.Clear();
            _liveSignsScratch.Clear();
            _liveLaneMarkingsScratch.Clear();
            _liveDeadEndCapScratch.Clear();

            if (SimpleRender)
            {
                // Switching into SimpleRender: nuke full-render pools (they
                // shouldn't be visible). The line pool gets reused below.
                ClearPool(_intersectionPool);
                ClearPool(_roadPool);
                ClearPool(_culDeSacPool);
                ClearPool(_laneFlowPool);
                ClearPool(_approachMarkingsPool);
                ClearPool(_signsPool);
                ClearPool(_laneMarkingsPool);
                ClearPool(_deadEndCapPool);

                foreach (NetworkRoad road in Network.Roads)
                {
                    Vertex a = FindVertex(road.EndA);
                    Vertex b = FindVertex(road.EndB);
                    if (a == null || b == null) continue;
                    Vector2 lineEndA = GeometryResolver.EffectiveEndpoint(road, RoadEnd.A, a, b);
                    Vector2 lineEndB = GeometryResolver.EffectiveEndpoint(road, RoadEnd.B, b, a);
                    SpawnOrUpdateRoadLine(road, lineEndA, lineEndB);
                    _liveRoadsScratch.Add(road.Id);
                }
                DestroyOrphans(_roadLinePool, _liveRoadsScratch);
                return;
            }

            // Switching out of SimpleRender: nuke the line pool.
            ClearPool(_roadLinePool);

            List<VertexGeometry> geos = GeometryResolver.ResolveNetwork(Network);

            // One IntersectionRenderer per vertex — except joint vertices
            // (2-way passthroughs), where the outline degenerates to a
            // zero-area line and the two road bodies meet seamlessly with
            // setback=0 anyway.
            foreach (Vertex v in Network.Vertices)
            {
                VertexGeometry vg = FindGeometry(geos, v.Id);
                if (vg == null) continue;
                if (IsJointVertex(vg)) continue;
                SpawnOrUpdateIntersection(v, vg);
                _liveVerticesScratch.Add(v.Id);

                if (ShowLaneFlow)
                {
                    SpawnOrUpdateLaneFlow(v, vg);
                    _liveLaneFlowScratch.Add(v.Id);
                }

                if (ShowPaintedControls || ShowLaneArrows)
                {
                    SpawnOrUpdateApproachMarkings(v, vg);
                    _liveApproachMarkingsScratch.Add(v.Id);
                }

                if (ShowSigns)
                {
                    SpawnOrUpdateSigns(v, vg);
                    _liveSignsScratch.Add(v.Id);
                }

                if (ShowLaneMarkings && v.LaneMarkings != null && v.LaneMarkings.Count > 0)
                {
                    SpawnOrUpdateLaneMarkings(v, vg);
                    _liveLaneMarkingsScratch.Add(v.Id);
                }

                // Dead-end vertices (exactly 1 incident approach) get a
                // semicircular cap past the road end so the asphalt
                // visually terminates rather than ending in a flat
                // perpendicular cut.
                if (ShowDeadEndCaps && vg.Approaches != null && vg.Approaches.Count == 1)
                {
                    SpawnOrUpdateDeadEndCap(v, vg.Approaches[0]);
                    _liveDeadEndCapScratch.Add(v.Id);
                }
            }

            // One RoadRenderer per road, with endpoints at the setback midpoints.
            foreach (NetworkRoad road in Network.Roads)
            {
                Vector2? endA = FindSetbackMidpoint(geos, road.EndA, road.Id, RoadEnd.A);
                Vector2? endB = FindSetbackMidpoint(geos, road.EndB, road.Id, RoadEnd.B);
                if (!endA.HasValue || !endB.HasValue) continue;
                float? setA = FindSetback(geos, road.EndA, road.Id, RoadEnd.A);
                float? setB = FindSetback(geos, road.EndB, road.Id, RoadEnd.B);
                SpawnOrUpdateRoad(road, endA.Value, endB.Value, setA ?? 0f, setB ?? 0f);
                _liveRoadsScratch.Add(road.Id);
            }

            // Cul-de-sac discs sit UNDER the road/intersection meshes
            // (lower MeshLift) so lane markings on the ring stay visible
            // on top. They render even if the ring vertices/roads were
            // partially deleted — the bulb entry's Network.CulDeSacs
            // entry is what drives this pool, not vertex topology.
            if (Network.CulDeSacs != null)
            {
                foreach (CulDeSacBulb bulb in Network.CulDeSacs)
                {
                    SpawnOrUpdateCulDeSacFill(bulb);
                    _liveCulDeSacScratch.Add(bulb.Id);
                }
            }

            DestroyOrphans(_intersectionPool, _liveVerticesScratch);
            DestroyOrphans(_roadPool, _liveRoadsScratch);
            DestroyOrphans(_culDeSacPool, _liveCulDeSacScratch);
            // When ShowLaneFlow is toggled off, _liveLaneFlowScratch is
            // empty so all overlays get destroyed. Re-toggle on to respawn.
            DestroyOrphans(_laneFlowPool, _liveLaneFlowScratch);
            DestroyOrphans(_approachMarkingsPool, _liveApproachMarkingsScratch);
            DestroyOrphans(_signsPool, _liveSignsScratch);
            DestroyOrphans(_laneMarkingsPool, _liveLaneMarkingsScratch);
            DestroyOrphans(_deadEndCapPool, _liveDeadEndCapScratch);
        }

        void SpawnOrUpdateDeadEndCap(Vertex v, VertexApproach approach)
        {
            if (!_deadEndCapPool.TryGetValue(v.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"DeadEndCap_{v.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                // Sit at MeshLift Y like the road body so they composite
                // continuously with the road's asphalt + shoulder.
                go.transform.position = new Vector3(0f, MeshLift, 0f);
                go.AddComponent<DeadEndCapRenderer>();
                _deadEndCapPool[v.Id] = go;
            }
            DeadEndCapRenderer dr = go.GetComponent<DeadEndCapRenderer>();
            dr.Approach = approach;
            dr.AsphaltMaterial = AsphaltMaterial;
            dr.ShoulderMaterial = ShoulderMaterial;
            dr.AsphaltColor = AsphaltColor;
            dr.ShoulderColor = ShoulderColor;
            dr.Segments = DeadEndCapSegments;
            dr.UvTileSize = UvTileSize;
            dr.Rebuild();
        }

        void SpawnOrUpdateLaneMarkings(Vertex v, VertexGeometry vg)
        {
            if (!_laneMarkingsPool.TryGetValue(v.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"LaneMarkings_{v.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = Vector3.zero;
                go.AddComponent<LaneMarkingsRenderer>();
                _laneMarkingsPool[v.Id] = go;
            }
            LaneMarkingsRenderer lmr = go.GetComponent<LaneMarkingsRenderer>();
            lmr.Geometry = vg;
            lmr.Markings = v.LaneMarkings;
            lmr.WhiteColor = LaneMarkingWhite;
            lmr.YellowColor = LaneMarkingYellow;
            lmr.Alpha = LaneMarkingAlpha;
            lmr.Width = LaneMarkingWidth;
            lmr.Height = LaneMarkingHeight;
            lmr.DashLength = LaneMarkingDashLength;
            lmr.DashGap = LaneMarkingDashGap;
            lmr.BezierSamples = LaneMarkingBezierSamples;
            lmr.Rebuild();
        }

        /// <summary>
        /// Push lane-marking style fields to every spawned LaneMarkingsRenderer
        /// + Rebuild them. Used by tuning when a style field changes.
        /// </summary>
        public void RefreshLaneMarkings()
        {
            foreach (KeyValuePair<string, GameObject> kv in _laneMarkingsPool)
            {
                if (kv.Value == null) continue;
                LaneMarkingsRenderer lmr = kv.Value.GetComponent<LaneMarkingsRenderer>();
                if (lmr == null) continue;
                lmr.WhiteColor = LaneMarkingWhite;
                lmr.YellowColor = LaneMarkingYellow;
                lmr.Alpha = LaneMarkingAlpha;
                lmr.Width = LaneMarkingWidth;
                lmr.Height = LaneMarkingHeight;
                lmr.DashLength = LaneMarkingDashLength;
                lmr.DashGap = LaneMarkingDashGap;
                lmr.BezierSamples = LaneMarkingBezierSamples;
                lmr.Rebuild();
            }
        }

        void SpawnOrUpdateSigns(Vertex v, VertexGeometry vg)
        {
            if (!_signsPool.TryGetValue(v.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"IntersectionSigns_{v.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = Vector3.zero;
                go.AddComponent<IntersectionSignsRenderer>();
                _signsPool[v.Id] = go;
            }
            IntersectionSignsRenderer sr = go.GetComponent<IntersectionSignsRenderer>();
            sr.Geometry = vg;
            sr.SignTint = SignTint;
            sr.SignAlpha = SignAlpha;
            sr.SignSize = SignSize;
            sr.SignShoulderClearance = SignShoulderClearance;
            sr.SignAlongOffset = SignAlongOffset;
            sr.Height = SignHeight;
            sr.StopPrefab = StopSignPrefab;
            sr.YieldPrefab = YieldSignPrefab;
            sr.PostPrefab = SignPostPrefab;
            sr.Rebuild();
        }

        /// <summary>
        /// Push sign style fields to every spawned IntersectionSignsRenderer
        /// + Rebuild them. Called by tuning when a style field changes.
        /// </summary>
        public void RefreshSigns()
        {
            foreach (KeyValuePair<string, GameObject> kv in _signsPool)
            {
                if (kv.Value == null) continue;
                IntersectionSignsRenderer sr = kv.Value.GetComponent<IntersectionSignsRenderer>();
                if (sr == null) continue;
                sr.SignTint = SignTint;
                sr.SignAlpha = SignAlpha;
                sr.SignSize = SignSize;
                sr.SignShoulderClearance = SignShoulderClearance;
                sr.SignAlongOffset = SignAlongOffset;
                sr.Height = SignHeight;
                sr.StopPrefab = StopSignPrefab;
                sr.YieldPrefab = YieldSignPrefab;
                sr.PostPrefab = SignPostPrefab;
                sr.Rebuild();
            }
        }

        void SpawnOrUpdateApproachMarkings(Vertex v, VertexGeometry vg)
        {
            if (!_approachMarkingsPool.TryGetValue(v.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"ApproachMarkings_{v.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = Vector3.zero;
                go.AddComponent<ApproachMarkingsRenderer>();
                _approachMarkingsPool[v.Id] = go;
            }
            ApproachMarkingsRenderer amr = go.GetComponent<ApproachMarkingsRenderer>();
            amr.Geometry = vg;
            amr.ShowPaintedControls = ShowPaintedControls;
            amr.ShowLaneArrows = ShowLaneArrows;
            amr.MarkingColor = EffectivePavementColor();
            amr.Height = PavementMarkingHeight;
            amr.StopLineWidth = StopLineWidth;
            amr.StopLineInsetFromSetback = StopLineInsetFromSetback;
            amr.YieldTriangleBase = YieldTriangleBase;
            amr.YieldTriangleHeight = YieldTriangleHeight;
            amr.YieldTriangleSpacing = YieldTriangleSpacing;
            amr.YieldInsetFromSetback = YieldInsetFromSetback;
            amr.LaneArrowSize = LaneArrowSize;
            amr.LaneArrowOffset = LaneArrowOffset;
            amr.FallbackGlyphLength = LaneArrowLength;
            amr.FallbackGlyphShaftWidth = LaneArrowShaftWidth;
            amr.FallbackGlyphHeadLength = LaneArrowHeadLength;
            amr.FallbackGlyphHeadWidth = LaneArrowHeadWidth;
            amr.FallbackGlyphStackSpacing = LaneArrowStackSpacing;
            amr.StraightTurnThresholdDeg = StraightTurnThresholdDeg;
            amr.Rebuild();
        }

        // PavementMarkingColor with PavementMarkingAlpha multiplied in.
        // Lets a tunable alpha slider dim every marking type without
        // having to re-pick the color.
        Color EffectivePavementColor()
        {
            Color c = PavementMarkingColor;
            c.a *= Mathf.Clamp01(PavementMarkingAlpha);
            return c;
        }

        /// <summary>
        /// Push pavement-marking style fields to every spawned
        /// ApproachMarkingsRenderer + Rebuild them. Called by tuning
        /// when a style field changes so the effect is visible without
        /// forcing a full network rebuild.
        /// </summary>
        public void RefreshPavementMarkings()
        {
            foreach (KeyValuePair<string, GameObject> kv in _approachMarkingsPool)
            {
                if (kv.Value == null) continue;
                ApproachMarkingsRenderer amr = kv.Value.GetComponent<ApproachMarkingsRenderer>();
                if (amr == null) continue;
                amr.ShowPaintedControls = ShowPaintedControls;
                amr.ShowLaneArrows = ShowLaneArrows;
                amr.MarkingColor = EffectivePavementColor();
                amr.Height = PavementMarkingHeight;
                amr.StopLineWidth = StopLineWidth;
                amr.StopLineInsetFromSetback = StopLineInsetFromSetback;
                amr.YieldTriangleBase = YieldTriangleBase;
                amr.YieldTriangleHeight = YieldTriangleHeight;
                amr.YieldTriangleSpacing = YieldTriangleSpacing;
                amr.YieldInsetFromSetback = YieldInsetFromSetback;
                amr.LaneArrowSize = LaneArrowSize;
                amr.LaneArrowOffset = LaneArrowOffset;
                amr.FallbackGlyphLength = LaneArrowLength;
                amr.FallbackGlyphShaftWidth = LaneArrowShaftWidth;
                amr.FallbackGlyphHeadLength = LaneArrowHeadLength;
                amr.FallbackGlyphHeadWidth = LaneArrowHeadWidth;
                amr.FallbackGlyphStackSpacing = LaneArrowStackSpacing;
                amr.StraightTurnThresholdDeg = StraightTurnThresholdDeg;
                amr.Rebuild();
            }
        }

        void SpawnOrUpdateLaneFlow(Vertex v, VertexGeometry vg)
        {
            if (!_laneFlowPool.TryGetValue(v.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"LaneFlow_{v.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = Vector3.zero;
                go.AddComponent<LaneFlowRenderer>();
                _laneFlowPool[v.Id] = go;
            }
            LaneFlowRenderer lfr = go.GetComponent<LaneFlowRenderer>();
            lfr.Geometry = vg;
            lfr.Height = LaneFlowHeight;
            lfr.DimAlpha = LaneFlowDimAlpha;
            lfr.FullAlpha = LaneFlowFullAlpha;
            lfr.ShaftWidth = LaneFlowShaftWidth;
            lfr.DashLength = LaneFlowDashLength;
            lfr.DashGap = LaneFlowDashGap;
            lfr.HeadLength = LaneFlowHeadLength;
            lfr.HeadWidth = LaneFlowHeadWidth;
            lfr.BezierSamples = LaneFlowBezierSamples;
            lfr.BezierControlFraction = LaneFlowBezierControlFraction;
            // Edit-state fields (HasSelection / ArmedFromLaneKey /
            // HoveredLaneKey) are pushed directly by the designer via
            // TryGetLaneFlowRenderer when those values change. Default
            // here is "not selected" so a freshly-spawned LFR renders
            // empty until the designer pokes it.
            lfr.Rebuild();
        }

        /// <summary>
        /// Push lane-flow style fields to every spawned LFR + Rebuild
        /// them. Called by tuning when a style field changes so the
        /// effect is visible without forcing a full network rebuild.
        /// </summary>
        public void RefreshLaneFlowStyle()
        {
            foreach (KeyValuePair<string, GameObject> kv in _laneFlowPool)
            {
                if (kv.Value == null) continue;
                LaneFlowRenderer lfr = kv.Value.GetComponent<LaneFlowRenderer>();
                if (lfr == null) continue;
                lfr.Height = LaneFlowHeight;
                lfr.DimAlpha = LaneFlowDimAlpha;
                lfr.FullAlpha = LaneFlowFullAlpha;
                lfr.ShaftWidth = LaneFlowShaftWidth;
                lfr.DashLength = LaneFlowDashLength;
                lfr.DashGap = LaneFlowDashGap;
                lfr.HeadLength = LaneFlowHeadLength;
                lfr.HeadWidth = LaneFlowHeadWidth;
                lfr.BezierSamples = LaneFlowBezierSamples;
                lfr.BezierControlFraction = LaneFlowBezierControlFraction;
                lfr.Rebuild();
            }
        }

        /// <summary>
        /// Look up the LaneFlowRenderer for a specific vertex (if one
        /// is currently spawned). Used by NetworkDesigner to push
        /// edit-state changes (armed/hovered lane) into a single
        /// overlay without forcing a full network rebuild.
        /// </summary>
        public bool TryGetLaneFlowRenderer(string vertexId, out LaneFlowRenderer lfr)
        {
            if (_laneFlowPool.TryGetValue(vertexId, out GameObject go) && go != null)
            {
                lfr = go.GetComponent<LaneFlowRenderer>();
                return lfr != null;
            }
            lfr = null;
            return false;
        }

        public bool TryGetApproachMarkingsRenderer(string vertexId, out ApproachMarkingsRenderer amr)
        {
            if (_approachMarkingsPool.TryGetValue(vertexId, out GameObject go) && go != null)
            {
                amr = go.GetComponent<ApproachMarkingsRenderer>();
                return amr != null;
            }
            amr = null;
            return false;
        }

        // Build/refresh a flat circular asphalt disc for a cul-de-sac bulb.
        // Triangle fan with CulDeSacFillSegments triangles. The disc sits
        // slightly BELOW the road/intersection meshes (MeshLift * 0.5)
        // so lane markings + intersection asphalt render cleanly on top.
        void SpawnOrUpdateCulDeSacFill(CulDeSacBulb bulb)
        {
            if (!_culDeSacPool.TryGetValue(bulb.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"CulDeSacFill_{bulb.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.AddComponent<MeshFilter>();
                go.AddComponent<MeshRenderer>();
                _culDeSacPool[bulb.Id] = go;
            }

            // Sit just below the asphalt MeshLift so the ring road bodies
            // + the entry-vertex intersection mesh draw on top without
            // z-fighting.
            float discY = MeshLift * 0.5f;
            go.transform.position = new Vector3(0f, discY, 0f);

            int segs = Mathf.Max(8, CulDeSacFillSegments);
            float r = Mathf.Max(0.1f, bulb.Radius);
            Vector3 c3 = new Vector3(bulb.Center.x, 0f, bulb.Center.y);

            Vector3[] verts = new Vector3[segs + 1];
            Vector2[] uvs = new Vector2[segs + 1];
            int[] tris = new int[segs * 3];
            verts[0] = c3;
            uvs[0] = new Vector2(c3.x, c3.z) / Mathf.Max(0.001f, UvTileSize);
            for (int i = 0; i < segs; i++)
            {
                float a = (i / (float)segs) * 2f * Mathf.PI;
                Vector3 p = c3 + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                verts[i + 1] = p;
                uvs[i + 1] = new Vector2(p.x, p.z) / Mathf.Max(0.001f, UvTileSize);
                // Triangle wound CW viewed from above (+Y down) so the
                // normal points +Y and the disc faces the camera. Going
                // CCW around the center in math convention (angle
                // increases) means vertex (i+1) is "ahead" of vertex i —
                // emit (center, next, current) to get the CW visual order.
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = 1 + ((i + 1) % segs);
                tris[i * 3 + 2] = 1 + i;
            }

            MeshFilter mf = go.GetComponent<MeshFilter>();
            Mesh mesh = mf.sharedMesh;
            if (mesh == null)
            {
                mesh = new Mesh { name = $"CulDeSacFill_{bulb.Id}" };
                mf.sharedMesh = mesh;
            }
            else
            {
                mesh.Clear();
            }
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            Material asphalt = AsphaltMaterial;
            if (asphalt == null)
            {
                if (_autoAsphaltMaterial == null)
                {
                    _autoAsphaltMaterial = new Material(Shader.Find("Standard"))
                    {
                        name = "CulDeSacAsphaltMat",
                        color = AsphaltColor,
                    };
                    _autoAsphaltMaterial.SetFloat("_Glossiness", 0f);
                    _autoAsphaltMaterial.SetFloat("_Metallic", 0f);
                }
                asphalt = _autoAsphaltMaterial;
            }
            mr.sharedMaterial = asphalt;
        }

        static void ClearPool(Dictionary<string, GameObject> pool)
        {
            foreach (KeyValuePair<string, GameObject> kvp in pool)
            {
                if (kvp.Value == null) continue;
                if (Application.isPlaying) Destroy(kvp.Value);
                else DestroyImmediate(kvp.Value);
            }
            pool.Clear();
        }

        static void DestroyOrphans(Dictionary<string, GameObject> pool, HashSet<string> liveIds)
        {
            List<string> dead = null;
            foreach (string id in pool.Keys)
            {
                if (liveIds.Contains(id)) continue;
                if (dead == null) dead = new List<string>();
                dead.Add(id);
            }
            if (dead == null) return;
            foreach (string id in dead)
            {
                GameObject go = pool[id];
                pool.Remove(id);
                if (go == null) continue;
                if (Application.isPlaying) Destroy(go);
                else DestroyImmediate(go);
            }
        }

        Vertex FindVertex(string id)
        {
            foreach (Vertex v in Network.Vertices)
                if (v.Id == id) return v;
            return null;
        }

        // Lightweight line representation of an edge — used while building
        // out the graph (before/instead of mesh-based road rendering).
        // Handles both straight (road.Curve == null) and cubic-bezier
        // curved roads. Pickable via child BoxCollider approximations
        // (one per polyline segment for curves; one for the whole road
        // when straight).
        void SpawnOrUpdateRoadLine(NetworkRoad road, Vector2 endA, Vector2 endB)
        {
            if (!_roadLinePool.TryGetValue(road.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"RoadLine_{road.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = new Vector3(0f, MeshLift, 0f);
                go.AddComponent<RoadMarker>();
                LineRenderer newLr = go.AddComponent<LineRenderer>();
                newLr.useWorldSpace = true;
                newLr.sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = RoadLineColor, name = "RoadLineMat" };
                _roadLinePool[road.Id] = go;
            }

            go.GetComponent<RoadMarker>().Road = road;

            Vector3[] points = SampleRoadLine(road, endA, endB);
            LineRenderer lr = go.GetComponent<LineRenderer>();
            lr.startWidth = RoadLineWidth;
            lr.endWidth = RoadLineWidth;
            lr.positionCount = points.Length;
            lr.SetPositions(points);
            if (lr.sharedMaterial != null) lr.sharedMaterial.color = RoadLineColor;

            // Rebuild collider segments (count varies with sample density).
            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = go.transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
            for (int i = 0; i < points.Length - 1; i++)
            {
                AddRoadLineColliderSegment(go.transform, points[i], points[i + 1]);
            }
        }

        // Cubic-bezier road if road.Curve != null; otherwise two-point line.
        // Sample count is tuned for visual smoothness without overkill.
        Vector3[] SampleRoadLine(NetworkRoad road, Vector2 endA, Vector2 endB)
        {
            if (road.Curve == null)
            {
                return new Vector3[]
                {
                    new Vector3(endA.x, MeshLift, endA.y),
                    new Vector3(endB.x, MeshLift, endB.y),
                };
            }

            const int N = 24;
            Vector3[] pts = new Vector3[N];
            Vector2 c1 = road.Curve.ControlA;
            Vector2 c2 = road.Curve.ControlB;
            for (int i = 0; i < N; i++)
            {
                float t = i / (float)(N - 1);
                Vector2 p = GeometryResolver.SampleCubic(endA, c1, c2, endB, t);
                pts[i] = new Vector3(p.x, MeshLift, p.y);
            }
            return pts;
        }

        void AddRoadLineColliderSegment(Transform parent, Vector3 a, Vector3 b)
        {
            GameObject seg = new GameObject("RoadLineSeg");
            seg.transform.SetParent(parent, worldPositionStays: false);
            Vector3 dir = b - a;
            float len = dir.magnitude;
            if (len < 1e-4f) return;
            seg.transform.position = (a + b) * 0.5f;
            seg.transform.rotation = Quaternion.LookRotation(dir / len, Vector3.up);
            BoxCollider bc = seg.AddComponent<BoxCollider>();
            bc.size = new Vector3(RoadLineWidth * 2f, 0.1f, len);
            bc.center = Vector3.zero;
        }

        // -----------------------------------------------------------------
        // Spawning
        // -----------------------------------------------------------------

        void SpawnOrUpdateIntersection(Vertex v, VertexGeometry vg)
        {
            if (!_intersectionPool.TryGetValue(v.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"Intersection_{v.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                // World position (not local) — mesh vertices coming from the
                // resolver are in absolute world coords, so the GameObject
                // must sit at world (0, MeshLift, 0) regardless of where
                // the parent (Designer) lives.
                go.transform.position = new Vector3(0f, MeshLift, 0f);
                go.AddComponent<IntersectionRenderer>();
                go.AddComponent<IntersectionMarker>();
                _intersectionPool[v.Id] = go;
            }
            IntersectionMarker im = go.GetComponent<IntersectionMarker>();
            if (im != null) im.Vertex = v;
            IntersectionRenderer ir = go.GetComponent<IntersectionRenderer>();
            ir.Geometry = vg;
            ir.VertexCenter = v.Position;
            ir.AsphaltMaterial = AsphaltMaterial;
            ir.ShoulderMaterial = ShoulderMaterial;
            ir.AsphaltColor = AsphaltColor;
            ir.ShoulderColor = ShoulderColor;
            ir.BezierSamplesPerSegment = BezierSamplesPerSegment;
            ir.UvTileSize = UvTileSize;
            ir.Rebuild();
        }

        void SpawnOrUpdateRoad(NetworkRoad road, Vector2 endA, Vector2 endB, float setbackA, float setbackB)
        {
            if (!_roadPool.TryGetValue(road.Id, out GameObject go) || go == null)
            {
                go = new GameObject($"Road_{road.Id}");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = new Vector3(0f, MeshLift, 0f);
                go.AddComponent<RoadMarker>();
                go.AddComponent<RoadRenderer>();
                _roadPool[road.Id] = go;
            }
            go.GetComponent<RoadMarker>().Road = road;
            RoadRenderer rr = go.GetComponent<RoadRenderer>();
            rr.Profile = road.Profile;
            rr.DriveSide = Network.DriveSide;

            // Curve-aware body: slice the bezier between the two setback
            // points (by arc length, matching what the resolver did for
            // its setbackPoint computation) and pass that sub-curve to
            // RoadRenderer. The body's t=0 lands exactly on the resolver's
            // setback-midpoint at end A, t=1 on end B — so the body's
            // first/last cross-sections align with the intersection
            // corner's exits.
            bool curveAssigned = false;
            if (road.Curve != null)
            {
                Vertex va = FindVertex(road.EndA);
                Vertex vb = FindVertex(road.EndB);
                if (va != null && vb != null)
                {
                    // Apply LateralOffset at each endpoint so the bezier
                    // start/end track the same shifted positions the
                    // resolver used for setback math and intersection
                    // geometry. Controls stay in world space — that
                    // changes the bezier's tangent at the endpoints
                    // (acceptable side-effect for now).
                    Vector2 p0 = GeometryResolver.EffectiveEndpoint(road, RoadEnd.A, va, vb);
                    Vector2 c1 = road.Curve.ControlA;
                    Vector2 c2 = road.Curve.ControlB;
                    Vector2 p3 = GeometryResolver.EffectiveEndpoint(road, RoadEnd.B, vb, va);

                    float tStart = GeometryResolver.ArcLengthToT(p0, c1, c2, p3, setbackA);
                    // For end B we parameterize from vb backward. Walk the
                    // reversed curve (vb, c2, c1, va) by setbackB and
                    // convert that t' back to original-curve t.
                    float tBfromB = GeometryResolver.ArcLengthToT(p3, c2, c1, p0, setbackB);
                    float tEnd = 1f - tBfromB;

                    if (tEnd > tStart + 1e-4f)
                    {
                        GeometryResolver.SubCubic(p0, c1, c2, p3, tStart, tEnd,
                            out Vector2 sp0, out Vector2 sc1, out Vector2 sc2, out Vector2 sp3);
                        rr.HasCurve = true;
                        rr.EndpointA = new Vector3(sp0.x, 0f, sp0.y);
                        rr.EndpointB = new Vector3(sp3.x, 0f, sp3.y);
                        rr.CurveControlA = new Vector3(sc1.x, 0f, sc1.y);
                        rr.CurveControlB = new Vector3(sc2.x, 0f, sc2.y);
                        rr.CurveTessellation = CurveTessellation;
                        curveAssigned = true;
                    }
                    // else: setbacks consumed the whole curve. Fall back
                    // to a degenerate straight body (will render as a
                    // very short or zero-length strip).
                }
            }
            if (!curveAssigned)
            {
                rr.HasCurve = false;
                rr.EndpointA = ToVec3(endA);
                rr.EndpointB = ToVec3(endB);
            }
            rr.AsphaltMaterial = AsphaltMaterial;
            rr.ShoulderMaterial = ShoulderMaterial;
            rr.MedianMaterial = MedianMaterial;
            rr.CenterlineMaterial = CenterlineMaterial;
            rr.LaneMarkingMaterial = LaneMarkingMaterial;
            rr.ArrowMaterial = ArrowMaterial;
            rr.AsphaltColor = AsphaltColor;
            rr.ShoulderColor = ShoulderColor;
            rr.MedianColor = MedianColor;
            rr.CenterlineColor = CenterlineColor;
            rr.LaneMarkingColor = LaneMarkingColor;
            rr.ArrowColor = ArrowColor;
            rr.DrawMarkings = DrawLaneMarkings;
            rr.LineWidth = LineWidth;
            rr.DashLength = DashLength;
            rr.DashGap = DashGap;
            rr.MarkingHeight = MarkingHeight;
            rr.DrawArrows = DrawArrows;
            rr.ArrowLength = ArrowLength;
            rr.ArrowWidth = ArrowWidth;
            rr.ArrowSpacing = ArrowSpacing;
            rr.UvTileSize = UvTileSize;
            rr.MarkingEndInset = MarkingEndInset;
            rr.Rebuild();
        }


        // -----------------------------------------------------------------
        // Lookups
        // -----------------------------------------------------------------

        static VertexGeometry FindGeometry(List<VertexGeometry> geos, string vertexId)
        {
            foreach (VertexGeometry vg in geos)
                if (vg.VertexId == vertexId) return vg;
            return null;
        }

        // A "joint" vertex is a 2-way passthrough whose two approaches are
        // collinear (~180° apart). Bearings live in (-π, π]; the wrapped
        // absolute difference being ≈ π means anti-parallel.
        static bool IsJointVertex(VertexGeometry vg)
        {
            if (vg.Approaches.Count != 2) return false;
            float diff = Mathf.Abs(vg.Approaches[0].Bearing - vg.Approaches[1].Bearing);
            if (diff > Mathf.PI) diff = 2f * Mathf.PI - diff;
            return Mathf.Abs(diff - Mathf.PI) < 0.5f * Mathf.Deg2Rad;
        }

        static float? FindSetback(List<VertexGeometry> geos,
            string vertexId, string roadId, RoadEnd end)
        {
            foreach (VertexGeometry vg in geos)
            {
                if (vg.VertexId != vertexId) continue;
                foreach (VertexApproach a in vg.Approaches)
                {
                    if (a.RoadId == roadId && a.End == end) return a.Setback;
                }
            }
            return null;
        }

        static Vector2? FindSetbackMidpoint(List<VertexGeometry> geos,
            string vertexId, string roadId, RoadEnd end)
        {
            foreach (VertexGeometry vg in geos)
            {
                if (vg.VertexId != vertexId) continue;
                foreach (VertexApproach a in vg.Approaches)
                {
                    if (a.RoadId == roadId && a.End == end)
                        return (a.OuterLeft + a.OuterRight) * 0.5f;
                }
            }
            return null;
        }

        static Vector3 ToVec3(Vector2 v) => new Vector3(v.x, 0f, v.y);
    }
}
