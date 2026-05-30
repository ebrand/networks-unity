// Glue layer: finds the scene's tunable components and registers their
// fields with TuningRegistry on enable.
//
// Add this to the same GameObject as TuningServer (or anywhere — refs
// can be assigned in the Inspector, but if left null we'll auto-find
// the first matching component in the scene).
//
// Adding more tunables = adding lines to RegisterAll(). Keeping
// registration centralized here (rather than scattering it across each
// component's OnEnable) makes the surface obvious in one place.

using UnityEngine;
using NetworkDesigner.Agents;
using NetworkDesigner.Designer;
using NetworkDesigner.Geometry;
using NetworkDesigner.Rendering;

namespace NetworkDesigner.Tuning
{
    [DisallowMultipleComponent]
    public class TuningSetup : MonoBehaviour
    {
        // km/h ↔ m/s. Physics runs in m/s; the tuning UI displays km/h.
        const float KPH_PER_MPS = 3.6f;

        [Header("Scene refs (optional — auto-found if null)")]
        public NetworkDesigner.Designer.NetworkDesigner Designer;
        public NetworkRenderer Renderer;
        public SceneAmbiance Ambiance;
        public OrbitCameraController Orbit;
        public GroundGrid Grid;
        public GroundTiler GroundTiler;
        public AgentSystem Agents;

        [Header("Persistence")]
        [Tooltip("If true, tuning panel changes are written to disk and re-applied across Play stop/start.")]
        public bool PersistChanges = true;
        [Tooltip("Where to read/write persisted tuning values. Leave empty to use the default " +
                 "(project_root/TuningOverrides.json in Editor; persistentDataPath in Player).")]
        public string PersistencePath = "";
        [Tooltip("Debounce window (s) before pending changes are written to disk. Slider drags emit many events; this batches them into a single write.")]
        public float PersistDebounceSeconds = 0.5f;

        // Set by TuningRegistry.OnValueChanged; consumed by Update to time the save.
        float _dirtySinceRealtime = -1f;

        void OnEnable()
        {
            if (Designer == null) Designer = FindFirstObjectByType<NetworkDesigner.Designer.NetworkDesigner>();
            if (Renderer == null) Renderer = FindFirstObjectByType<NetworkRenderer>();
            if (Ambiance == null) Ambiance = FindFirstObjectByType<SceneAmbiance>();
            if (Orbit == null) Orbit = FindFirstObjectByType<OrbitCameraController>();
            if (Grid == null) Grid = FindFirstObjectByType<GroundGrid>();
            if (GroundTiler == null) GroundTiler = FindFirstObjectByType<GroundTiler>();
            if (Agents == null) Agents = FindFirstObjectByType<AgentSystem>();

            TuningRegistry.Clear();
            RegisterAll();

            int applied = 0;
            if (PersistChanges)
            {
                applied = TuningRegistry.LoadFromFile(ResolvePersistencePath());
                // Subscribe AFTER load so initial load's per-key sets don't
                // immediately mark the file dirty.
                TuningRegistry.OnValueChanged += OnTuningChanged;
            }

            Debug.Log($"[TuningSetup] Registered {TuningRegistry.Entries.Count} tunables" +
                      (applied > 0 ? $" (loaded {applied} overrides from disk)." : "."));
        }

        void OnDisable()
        {
            TuningRegistry.OnValueChanged -= OnTuningChanged;
            // Flush any pending changes so a quick Play→Stop doesn't lose them.
            if (PersistChanges && _dirtySinceRealtime > 0f)
            {
                TuningRegistry.SaveToFile(ResolvePersistencePath());
                _dirtySinceRealtime = -1f;
            }
        }

        void Update()
        {
            // The orbit camera mutates Yaw / Pitch / Distance / Target
            // directly (mouse drag, scroll, WASD pan) — those changes
            // never flow through TrySet, so OnValueChanged never fires
            // and the debounced save below stays asleep. Poll the
            // values each frame and mark dirty on drift so the on-disk
            // tuning file picks up camera moves automatically.
            PollOrbitCameraForChanges();

            if (!PersistChanges) return;
            if (_dirtySinceRealtime < 0f) return;
            if (Time.realtimeSinceStartup - _dirtySinceRealtime < PersistDebounceSeconds) return;
            TuningRegistry.SaveToFile(ResolvePersistencePath());
            _dirtySinceRealtime = -1f;
        }

        // Previous-frame snapshot of orbit state. Compared each frame
        // against current values to detect in-editor camera moves and
        // mark TuningSetup dirty (so the camera position survives
        // session restarts via TuningOverrides.json).
        Vector3 _prevOrbitTarget;
        float _prevOrbitYaw, _prevOrbitPitch, _prevOrbitDistance;
        bool _prevOrbitInit;

        void PollOrbitCameraForChanges()
        {
            if (Orbit == null) return;
            if (!_prevOrbitInit)
            {
                _prevOrbitTarget = Orbit.Target;
                _prevOrbitYaw = Orbit.Yaw;
                _prevOrbitPitch = Orbit.Pitch;
                _prevOrbitDistance = Orbit.DistanceTarget;
                _prevOrbitInit = true;
                return;
            }
            // Read DistanceTarget — the steady-state zoom — not the
            // animating Distance, so smoothing doesn't mark dirty every
            // frame. Threshold the other axes too so micro-noise
            // doesn't trigger spurious saves.
            const float EPS = 0.02f;
            if ((Orbit.Target - _prevOrbitTarget).sqrMagnitude < EPS * EPS
                && Mathf.Abs(Orbit.Yaw - _prevOrbitYaw) < EPS
                && Mathf.Abs(Orbit.Pitch - _prevOrbitPitch) < EPS
                && Mathf.Abs(Orbit.DistanceTarget - _prevOrbitDistance) < EPS) return;
            _prevOrbitTarget = Orbit.Target;
            _prevOrbitYaw = Orbit.Yaw;
            _prevOrbitPitch = Orbit.Pitch;
            _prevOrbitDistance = Orbit.DistanceTarget;
            // Mark dirty (only if not already), letting the existing
            // debounce window decide when to actually write to disk.
            if (_dirtySinceRealtime < 0f)
                _dirtySinceRealtime = Time.realtimeSinceStartup;
        }

        void OnTuningChanged()
        {
            _dirtySinceRealtime = Time.realtimeSinceStartup;
        }

        string ResolvePersistencePath()
        {
            return string.IsNullOrEmpty(PersistencePath)
                ? TuningRegistry.DefaultPersistencePath
                : PersistencePath;
        }

        void RegisterAll()
        {
            if (Designer != null)
            {
                TuningRegistry.RegisterBool(
                    "designer.snapToGrid", "Snap", "Snap to grid",
                    () => Designer.SnapToGrid,
                    v => Designer.SnapToGrid = v);

                TuningRegistry.RegisterFloat(
                    "designer.majorSnapRange", "Snap", "Major-line pull (× minor)",
                    () => Designer.MajorSnapRange,
                    v => Designer.MajorSnapRange = v,
                    0f, 20f);

                TuningRegistry.RegisterFloat(
                    "designer.edgeClickTolerance", "Snap", "Click-on-edge tolerance (m)",
                    () => Designer.EdgeClickTolerance,
                    v => Designer.EdgeClickTolerance = v,
                    0f, 2f);

                TuningRegistry.RegisterFloat(
                    "designer.vertexOnEdgeTolerance", "Snap", "Edge-through-vertex tolerance (m)",
                    () => Designer.VertexOnEdgeTolerance,
                    v => Designer.VertexOnEdgeTolerance = v,
                    0f, 2f);

                TuningRegistry.RegisterFloat(
                    "designer.collinearMergeAngleDeg", "Snap", "Collinear merge tolerance (°)",
                    () => Designer.CollinearMergeAngleDeg,
                    v => Designer.CollinearMergeAngleDeg = v,
                    0f, 30f);

                TuningRegistry.RegisterFloat(
                    "designer.curvedSetbackMultiplier", "Geometry", "Curved-road setback × (1=off)",
                    () => GeometryResolver.CurvedSetbackMultiplier,
                    v => {
                        GeometryResolver.CurvedSetbackMultiplier = Mathf.Max(1f, v);
                        if (Renderer != null) Renderer.Rebuild();
                    },
                    1f, 5f);

                TuningRegistry.RegisterFloat(
                    "designer.guideSnapDistance", "Snap guides", "Guide pull radius (m)",
                    () => Designer.GuideSnapDistance,
                    v => Designer.GuideSnapDistance = v,
                    0f, 50f);

                TuningRegistry.RegisterFloat(
                    "designer.angleSnapDegrees", "Snap guides", "Angle snap step (°)",
                    () => Designer.AngleSnapDegrees,
                    v => Designer.AngleSnapDegrees = v,
                    0f, 90f);

                TuningRegistry.RegisterFloat(
                    "designer.angleSnapDistance", "Snap guides", "Angle snap pull radius (m)",
                    () => Designer.AngleSnapDistance,
                    v => Designer.AngleSnapDistance = v,
                    0f, 100f);

                TuningRegistry.RegisterFloat(
                    "designer.guideDashPeriod", "Snap guides", "Guide dash period (m)",
                    () => Designer.GuideDashPeriod,
                    v => Designer.GuideDashPeriod = v,
                    0.1f, 5f);


                TuningRegistry.RegisterFloat(
                    "designer.extensionGuideRange", "Snap guides", "Extension range (m)",
                    () => Designer.ExtensionGuideRange,
                    v => Designer.ExtensionGuideRange = v,
                    0f, 1000f);

                TuningRegistry.RegisterFloat(
                    "designer.perpendicularGuideRange", "Snap guides", "Perpendicular range (m)",
                    () => Designer.PerpendicularGuideRange,
                    v => Designer.PerpendicularGuideRange = v,
                    0f, 1000f);

                TuningRegistry.RegisterFloat(
                    "designer.guideLineWidth", "Snap guides", "Guide line width (m)",
                    () => Designer.GuideLineWidth,
                    v => Designer.GuideLineWidth = v,
                    0.01f, 1f);

                TuningRegistry.RegisterColor(
                    "designer.guideLineColor", "Snap guides", "Guide line color",
                    () => Designer.GuideLineColor,
                    v => Designer.GuideLineColor = v);

                TuningRegistry.RegisterBool(
                    "designer.rangeCircleEnabled", "Range circle", "Show",
                    () => Designer.RangeCircleEnabled,
                    v => Designer.RangeCircleEnabled = v);

                TuningRegistry.RegisterFloat(
                    "designer.rangeCircleRadius", "Range circle", "Radius (m)",
                    () => Designer.RangeCircleRadius,
                    v => Designer.RangeCircleRadius = v,
                    0.5f, 1000f);

                TuningRegistry.RegisterFloat(
                    "designer.rangeCircleWidth", "Range circle", "Line width (m)",
                    () => Designer.RangeCircleWidth,
                    v => Designer.RangeCircleWidth = v,
                    0.01f, 1f);

                TuningRegistry.RegisterFloat(
                    "designer.rangeCircleDashPeriod", "Range circle", "Dash period (m)",
                    () => Designer.RangeCircleDashPeriod,
                    v => Designer.RangeCircleDashPeriod = v,
                    0.1f, 5f);

                TuningRegistry.RegisterColor(
                    "designer.rangeCircleColor", "Range circle", "Color",
                    () => Designer.RangeCircleColor,
                    v => Designer.RangeCircleColor = v);

                TuningRegistry.RegisterFloat(
                    "designer.rangeCircleAlpha", "Range circle", "Alpha (0–1)",
                    () => Designer.RangeCircleOpacity,
                    v => Designer.RangeCircleOpacity = v,
                    0f, 1f);

                TuningRegistry.RegisterFloat(
                    "designer.markerDiameter", "Vertex markers", "Diameter (m)",
                    () => Designer.MarkerDiameter,
                    v => { Designer.MarkerDiameter = v; RefreshExistingMarkers(); },
                    0.25f, 20f);

                TuningRegistry.RegisterFloat(
                    "designer.markerThickness", "Vertex markers", "Thickness (m)",
                    () => Designer.MarkerThickness,
                    v => { Designer.MarkerThickness = v; RefreshExistingMarkers(); },
                    0.05f, 1f);

                TuningRegistry.RegisterFloat(
                    "designer.markerHeight", "Vertex markers", "Height above ground (m)",
                    () => Designer.MarkerHeight,
                    v => { Designer.MarkerHeight = v; RefreshExistingMarkers(); },
                    0f, 2f);

                TuningRegistry.RegisterColor(
                    "designer.markerColor", "Vertex markers", "Default color",
                    () => Designer.MarkerColor,
                    v => { Designer.MarkerColor = v; Designer.InvalidateMarkerMaterials(); });

                TuningRegistry.RegisterColor(
                    "designer.markerHoverColor", "Vertex markers", "Hover color",
                    () => Designer.MarkerHoverColor,
                    v => { Designer.MarkerHoverColor = v; Designer.InvalidateMarkerMaterials(); });

                TuningRegistry.RegisterColor(
                    "designer.markerActiveColor", "Vertex markers", "Active color",
                    () => Designer.MarkerActiveColor,
                    v => { Designer.MarkerActiveColor = v; Designer.InvalidateMarkerMaterials(); });

                TuningRegistry.RegisterFloat(
                    "designer.ghostPuckAlpha", "Vertex markers", "Ghost puck opacity",
                    () => Designer.GhostPuckAlpha,
                    v => Designer.GhostPuckAlpha = v,
                    0f, 1f);

                TuningRegistry.RegisterFloat(
                    "designer.filletLeverFraction", "Fillet", "Lever fraction (0=tight, 1=loose)",
                    () => Designer.FilletLeverFraction,
                    v => Designer.FilletLeverFraction = v,
                    0.05f, 0.95f);

                TuningRegistry.RegisterProfile(
                    "designer.activeProfile", "Road profile", "Active profile",
                    () => Designer.GetActiveProfile(),
                    p => Designer.SetActiveProfile(p));

                TuningRegistry.RegisterFloat(
                    "designer.roundaboutRadius", "Roundabout", "Radius (m)",
                    () => Designer.RoundaboutRadius,
                    v => Designer.RoundaboutRadius = v,
                    2f, 50f);

                TuningRegistry.RegisterFloat(
                    "designer.roundaboutLanes", "Roundabout", "Lane count",
                    () => Designer.RoundaboutLanes,
                    v => Designer.RoundaboutLanes = Mathf.Clamp(Mathf.RoundToInt(v), 1, 4),
                    1f, 4f, 1f);

                TuningRegistry.RegisterBool(
                    "designer.showMeasurementTooltip", "Measurement", "Show distance tooltip",
                    () => Designer.ShowMeasurementTooltip,
                    v => Designer.ShowMeasurementTooltip = v);

                // -------- Intersection editing — setback handles --------
                TuningRegistry.RegisterFloat(
                    "designer.setbackHandleDiameter", "Intersection editing", "Setback handle diameter (m)",
                    () => Designer.SetbackHandleDiameter,
                    v => { Designer.SetbackHandleDiameter = v; Designer.RefreshEditOverlays(); },
                    0.25f, 10f);
                TuningRegistry.RegisterFloat(
                    "designer.setbackRingThickness", "Intersection editing", "Setback ring thickness (m)",
                    () => Designer.SetbackRingThickness,
                    v => { Designer.SetbackRingThickness = v; Designer.RefreshEditOverlays(); },
                    0.05f, 2f);
                TuningRegistry.RegisterFloat(
                    "designer.setbackHandleHeight", "Intersection editing", "Setback handle height (m)",
                    () => Designer.SetbackHandleHeight,
                    v => { Designer.SetbackHandleHeight = v; Designer.RefreshEditOverlays(); },
                    0f, 5f);
                TuningRegistry.RegisterFloat(
                    "designer.setbackHandleOffsetMin", "Intersection editing", "Setback handle offset min (m)",
                    () => Designer.SetbackHandleOffsetMin,
                    v => { Designer.SetbackHandleOffsetMin = v; Designer.RefreshEditOverlays(); },
                    0f, 20f);
                TuningRegistry.RegisterFloat(
                    "designer.setbackHandleOffsetWidthMult", "Intersection editing", "Setback offset × road width",
                    () => Designer.SetbackHandleOffsetWidthMultiplier,
                    v => { Designer.SetbackHandleOffsetWidthMultiplier = v; Designer.RefreshEditOverlays(); },
                    0f, 2f);
                TuningRegistry.RegisterFloat(
                    "designer.setbackStemDashLength", "Intersection editing", "Stem dash length (m)",
                    () => Designer.SetbackStemDashLength,
                    v => { Designer.SetbackStemDashLength = v; Designer.RefreshEditOverlays(); },
                    0.05f, 2f);
                TuningRegistry.RegisterFloat(
                    "designer.setbackStemGapLength", "Intersection editing", "Stem dash gap (m)",
                    () => Designer.SetbackStemGapLength,
                    v => { Designer.SetbackStemGapLength = v; Designer.RefreshEditOverlays(); },
                    0.05f, 2f);
                TuningRegistry.RegisterFloat(
                    "designer.setbackStemWidth", "Intersection editing", "Stem line width (m)",
                    () => Designer.SetbackStemWidth,
                    v => { Designer.SetbackStemWidth = v; Designer.RefreshEditOverlays(); },
                    0.02f, 1f);
                TuningRegistry.RegisterColor(
                    "designer.setbackHandleColor", "Intersection editing", "Setback handle color",
                    () => Designer.SetbackHandleColor,
                    v => { Designer.SetbackHandleColor = v; Designer.RefreshEditOverlays(); });
                TuningRegistry.RegisterColor(
                    "designer.setbackHandleActiveColor", "Intersection editing", "Setback handle active color",
                    () => Designer.SetbackHandleActiveColor,
                    v => { Designer.SetbackHandleActiveColor = v; Designer.RefreshEditOverlays(); });

                // -------- Intersection editing — lateral offset handles --------
                TuningRegistry.RegisterFloat(
                    "designer.lateralHandleDiameter", "Intersection editing", "Lateral handle diameter (m)",
                    () => Designer.LateralOffsetHandleDiameter,
                    v => { Designer.LateralOffsetHandleDiameter = v; Designer.RefreshEditOverlays(); },
                    0.25f, 5f);
                TuningRegistry.RegisterFloat(
                    "designer.lateralHandleRingThickness", "Intersection editing", "Lateral handle ring thickness (m)",
                    () => Designer.LateralOffsetHandleRingThickness,
                    v => { Designer.LateralOffsetHandleRingThickness = v; Designer.RefreshEditOverlays(); },
                    0.05f, 1f);
                TuningRegistry.RegisterFloat(
                    "designer.lateralHandleHeight", "Intersection editing", "Lateral handle height (m)",
                    () => Designer.LateralOffsetHandleHeight,
                    v => { Designer.LateralOffsetHandleHeight = v; Designer.RefreshEditOverlays(); },
                    0f, 5f);
                TuningRegistry.RegisterFloat(
                    "designer.lateralHandleInwardOffset", "Intersection editing", "Lateral handle inward offset (m)",
                    () => Designer.LateralOffsetHandleInwardOffset,
                    v => { Designer.LateralOffsetHandleInwardOffset = v; Designer.RefreshEditOverlays(); },
                    0f, 20f);
                TuningRegistry.RegisterColor(
                    "designer.lateralHandleColor", "Intersection editing", "Lateral handle color",
                    () => Designer.LateralOffsetHandleColor,
                    v => { Designer.LateralOffsetHandleColor = v; Designer.RefreshEditOverlays(); });

                // -------- Intersection editing — bezier control handles --------
                TuningRegistry.RegisterFloat(
                    "designer.bezierHandleDiameter", "Intersection editing", "Bezier handle diameter (m)",
                    () => Designer.BezierHandleDiameter,
                    v => { Designer.BezierHandleDiameter = v; Designer.RefreshEditOverlays(); },
                    0.25f, 5f);
                TuningRegistry.RegisterFloat(
                    "designer.bezierHandleRingThickness", "Intersection editing", "Bezier handle ring thickness (m)",
                    () => Designer.BezierHandleRingThickness,
                    v => { Designer.BezierHandleRingThickness = v; Designer.RefreshEditOverlays(); },
                    0.05f, 1f);
                TuningRegistry.RegisterFloat(
                    "designer.bezierHandleHeight", "Intersection editing", "Bezier handle height (m)",
                    () => Designer.BezierHandleHeight,
                    v => { Designer.BezierHandleHeight = v; Designer.RefreshEditOverlays(); },
                    0f, 5f);
                TuningRegistry.RegisterFloat(
                    "designer.bezierPhantomTangent", "Intersection editing", "Bezier phantom tangent (0-1)",
                    () => Designer.BezierPhantomTangent,
                    v => { Designer.BezierPhantomTangent = v; Designer.RefreshEditOverlays(); },
                    0.05f, 0.95f);
                TuningRegistry.RegisterFloat(
                    "designer.bezierStemDashLength", "Intersection editing", "Bezier stem dash length (m)",
                    () => Designer.BezierStemDashLength,
                    v => { Designer.BezierStemDashLength = v; Designer.RefreshEditOverlays(); },
                    0.05f, 2f);
                TuningRegistry.RegisterFloat(
                    "designer.bezierStemGapLength", "Intersection editing", "Bezier stem dash gap (m)",
                    () => Designer.BezierStemGapLength,
                    v => { Designer.BezierStemGapLength = v; Designer.RefreshEditOverlays(); },
                    0.05f, 2f);
                TuningRegistry.RegisterFloat(
                    "designer.bezierStemWidth", "Intersection editing", "Bezier stem line width (m)",
                    () => Designer.BezierStemWidth,
                    v => { Designer.BezierStemWidth = v; Designer.RefreshEditOverlays(); },
                    0f, 1f);
                TuningRegistry.RegisterColor(
                    "designer.bezierHandleColor", "Intersection editing", "Bezier handle color",
                    () => Designer.BezierHandleColor,
                    v => { Designer.BezierHandleColor = v; Designer.RefreshEditOverlays(); });

                // -------- Intersection editing — lane endpoint markers --------
                TuningRegistry.RegisterFloat(
                    "designer.laneMarkerDiameter", "Intersection editing", "Lane marker diameter (m)",
                    () => Designer.LaneMarkerDiameter,
                    v => { Designer.LaneMarkerDiameter = v; Designer.RefreshEditOverlays(); },
                    0.25f, 10f);
                TuningRegistry.RegisterFloat(
                    "designer.laneMarkerRingThickness", "Intersection editing", "Lane marker ring thickness (m)",
                    () => Designer.LaneMarkerRingThickness,
                    v => { Designer.LaneMarkerRingThickness = v; Designer.RefreshEditOverlays(); },
                    0.05f, 2f);
                TuningRegistry.RegisterFloat(
                    "designer.laneMarkerHeight", "Intersection editing", "Lane marker height (m)",
                    () => Designer.LaneMarkerHeight,
                    v => { Designer.LaneMarkerHeight = v; Designer.RefreshEditOverlays(); },
                    0f, 5f);
                TuningRegistry.RegisterFloat(
                    "designer.laneOverlayDimAlpha", "Intersection editing", "Marker dim alpha",
                    () => Designer.LaneOverlayDimAlpha,
                    v => { Designer.LaneOverlayDimAlpha = v; Designer.RefreshEditOverlays(); },
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "designer.laneOverlayFullAlpha", "Intersection editing", "Marker hover/armed alpha",
                    () => Designer.LaneOverlayFullAlpha,
                    v => { Designer.LaneOverlayFullAlpha = v; Designer.RefreshEditOverlays(); },
                    0f, 1f);
                TuningRegistry.RegisterColor(
                    "designer.laneOutboundMarkerColor", "Intersection editing", "Outbound marker color",
                    () => Designer.LaneOutboundMarkerColor,
                    v => { Designer.LaneOutboundMarkerColor = v; Designer.RefreshEditOverlays(); });
                TuningRegistry.RegisterColor(
                    "designer.laneMarkerArmedColor", "Intersection editing", "Armed marker color",
                    () => Designer.LaneMarkerArmedColor,
                    v => { Designer.LaneMarkerArmedColor = v; Designer.RefreshEditOverlays(); });
            }

            if (Renderer != null)
            {
                TuningRegistry.RegisterBool(
                    "renderer.simpleRender", "Rendering", "Simple lines (no asphalt)",
                    () => Renderer.SimpleRender,
                    v => { Renderer.SimpleRender = v; Renderer.Rebuild(); });

                TuningRegistry.RegisterFloat(
                    "renderer.curveTessellation", "Rendering", "Curve tessellation (segments)",
                    () => Renderer.CurveTessellation,
                    v => { Renderer.CurveTessellation = Mathf.Clamp(Mathf.RoundToInt(v), 2, 64); Renderer.Rebuild(); },
                    2f, 64f, 1f);

                TuningRegistry.RegisterFloat(
                    "renderer.roadLineWidth", "Road lines", "Line width (m)",
                    () => Renderer.RoadLineWidth,
                    v => { Renderer.RoadLineWidth = v; Renderer.Rebuild(); },
                    0.05f, 3f);

                TuningRegistry.RegisterColor(
                    "renderer.roadLineColor", "Road lines", "Line color",
                    () => Renderer.RoadLineColor,
                    v => { Renderer.RoadLineColor = v; Renderer.Rebuild(); });

                TuningRegistry.RegisterFloat(
                    "renderer.turnLaneStripeInset", "Road lines",
                    "Turn lane stripe inset (m)",
                    () => Renderer.TurnLaneStripeInset,
                    v => { Renderer.TurnLaneStripeInset = v; Renderer.Rebuild(); },
                    0f, 2f);

                TuningRegistry.RegisterBool(
                    "renderer.drawArrows", "One-way arrows", "Show direction arrows",
                    () => Renderer.DrawArrows,
                    v => { Renderer.DrawArrows = v; Renderer.Rebuild(); });

                TuningRegistry.RegisterFloat(
                    "renderer.arrowLength", "One-way arrows", "Length (m)",
                    () => Renderer.ArrowLength,
                    v => { Renderer.ArrowLength = v; Renderer.Rebuild(); },
                    0.5f, 20f);

                TuningRegistry.RegisterFloat(
                    "renderer.arrowWidth", "One-way arrows", "Width (m)",
                    () => Renderer.ArrowWidth,
                    v => { Renderer.ArrowWidth = v; Renderer.Rebuild(); },
                    0.3f, 10f);

                TuningRegistry.RegisterFloat(
                    "renderer.arrowSpacing", "One-way arrows", "Spacing (m)",
                    () => Renderer.ArrowSpacing,
                    v => { Renderer.ArrowSpacing = v; Renderer.Rebuild(); },
                    5f, 200f);

                TuningRegistry.RegisterColor(
                    "renderer.arrowColor", "One-way arrows", "Arrow color",
                    () => Renderer.ArrowColor,
                    v => { Renderer.ArrowColor = v; Renderer.Rebuild(); });

                TuningRegistry.RegisterFloat(
                    "renderer.uvTileSize", "Rendering", "Surface UV tile size (m)",
                    () => Renderer.UvTileSize,
                    v => { Renderer.UvTileSize = v; Renderer.Rebuild(); },
                    0.25f, 20f);

                // -------- Lane-flow arrows (Intersection editing) --------
                TuningRegistry.RegisterBool(
                    "renderer.showLaneFlow", "Intersection editing", "Show flow arrows",
                    () => Renderer.ShowLaneFlow,
                    v => { Renderer.ShowLaneFlow = v; Renderer.Rebuild(); });
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowShaftWidth", "Intersection editing", "Arrow shaft width (m)",
                    () => Renderer.LaneFlowShaftWidth,
                    v => { Renderer.LaneFlowShaftWidth = v; Renderer.RefreshLaneFlowStyle(); },
                    0.05f, 5f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowDashLength", "Intersection editing", "Arrow dash length (m)",
                    () => Renderer.LaneFlowDashLength,
                    v => { Renderer.LaneFlowDashLength = v; Renderer.RefreshLaneFlowStyle(); },
                    0.1f, 10f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowDashGap", "Intersection editing", "Arrow dash gap (m)",
                    () => Renderer.LaneFlowDashGap,
                    v => { Renderer.LaneFlowDashGap = v; Renderer.RefreshLaneFlowStyle(); },
                    0.05f, 10f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowHeadLength", "Intersection editing", "Arrowhead length (m)",
                    () => Renderer.LaneFlowHeadLength,
                    v => { Renderer.LaneFlowHeadLength = v; Renderer.RefreshLaneFlowStyle(); },
                    0.25f, 10f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowHeadWidth", "Intersection editing", "Arrowhead width (m)",
                    () => Renderer.LaneFlowHeadWidth,
                    v => { Renderer.LaneFlowHeadWidth = v; Renderer.RefreshLaneFlowStyle(); },
                    0.25f, 10f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowBezierSamples", "Intersection editing", "Bezier samples",
                    () => Renderer.LaneFlowBezierSamples,
                    v => { Renderer.LaneFlowBezierSamples = Mathf.Clamp(Mathf.RoundToInt(v), 8, 64); Renderer.RefreshLaneFlowStyle(); },
                    8f, 64f, 1f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowBezierControlFraction", "Intersection editing", "Bezier curve fraction (0=straight)",
                    () => Renderer.LaneFlowBezierControlFraction,
                    v => { Renderer.LaneFlowBezierControlFraction = v; Renderer.RefreshLaneFlowStyle(); },
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowDimAlpha", "Intersection editing", "Flow arrow dim alpha",
                    () => Renderer.LaneFlowDimAlpha,
                    v => { Renderer.LaneFlowDimAlpha = v; Renderer.RefreshLaneFlowStyle(); },
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowFullAlpha", "Intersection editing", "Flow arrow hover/armed alpha",
                    () => Renderer.LaneFlowFullAlpha,
                    v => { Renderer.LaneFlowFullAlpha = v; Renderer.RefreshLaneFlowStyle(); },
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneFlowHeight", "Intersection editing", "Flow arrow height above asphalt (m)",
                    () => Renderer.LaneFlowHeight,
                    v => { Renderer.LaneFlowHeight = v; Renderer.RefreshLaneFlowStyle(); },
                    0f, 1f);

                // -------- Pavement markings --------
                TuningRegistry.RegisterBool(
                    "renderer.showPaintedControls", "Pavement markings", "Show painted controls (stop/yield)",
                    () => Renderer.ShowPaintedControls,
                    v => { Renderer.ShowPaintedControls = v; Renderer.Rebuild(); });
                TuningRegistry.RegisterBool(
                    "renderer.showLaneArrows", "Pavement markings", "Show painted lane arrows",
                    () => Renderer.ShowLaneArrows,
                    v => { Renderer.ShowLaneArrows = v; Renderer.Rebuild(); });
                TuningRegistry.RegisterColor(
                    "renderer.pavementMarkingColor", "Pavement markings", "Marking color (paint)",
                    () => Renderer.PavementMarkingColor,
                    v => { Renderer.PavementMarkingColor = v; Renderer.RefreshPavementMarkings(); });
                TuningRegistry.RegisterFloat(
                    "renderer.pavementMarkingAlpha", "Pavement markings", "Marking alpha (master)",
                    () => Renderer.PavementMarkingAlpha,
                    v => { Renderer.PavementMarkingAlpha = v; Renderer.RefreshPavementMarkings(); },
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "renderer.pavementMarkingHeight", "Pavement markings", "Height above asphalt (m)",
                    () => Renderer.PavementMarkingHeight,
                    v => { Renderer.PavementMarkingHeight = v; Renderer.RefreshPavementMarkings(); },
                    0f, 0.1f);
                TuningRegistry.RegisterFloat(
                    "renderer.stopLineWidth", "Pavement markings", "Stop line width (m)",
                    () => Renderer.StopLineWidth,
                    v => { Renderer.StopLineWidth = v; Renderer.RefreshPavementMarkings(); },
                    0.1f, 2f);
                TuningRegistry.RegisterFloat(
                    "renderer.stopLineInset", "Pavement markings", "Stop line inset from setback (m)",
                    () => Renderer.StopLineInsetFromSetback,
                    v => { Renderer.StopLineInsetFromSetback = v; Renderer.RefreshPavementMarkings(); },
                    0f, 2f);
                TuningRegistry.RegisterFloat(
                    "renderer.yieldTriangleBase", "Pavement markings", "Yield triangle base (m)",
                    () => Renderer.YieldTriangleBase,
                    v => { Renderer.YieldTriangleBase = v; Renderer.RefreshPavementMarkings(); },
                    0.1f, 3f);
                TuningRegistry.RegisterFloat(
                    "renderer.yieldTriangleHeight", "Pavement markings", "Yield triangle height (m)",
                    () => Renderer.YieldTriangleHeight,
                    v => { Renderer.YieldTriangleHeight = v; Renderer.RefreshPavementMarkings(); },
                    0.1f, 3f);
                TuningRegistry.RegisterFloat(
                    "renderer.yieldTriangleSpacing", "Pavement markings", "Yield triangle spacing (m)",
                    () => Renderer.YieldTriangleSpacing,
                    v => { Renderer.YieldTriangleSpacing = v; Renderer.RefreshPavementMarkings(); },
                    0f, 3f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneArrowSize", "Pavement markings", "Textured lane arrow size (m)",
                    () => Renderer.LaneArrowSize,
                    v => { Renderer.LaneArrowSize = v; Renderer.RefreshPavementMarkings(); },
                    0.5f, 10f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneArrowOffset", "Pavement markings", "Lane arrow inset from setback (m)",
                    () => Renderer.LaneArrowOffset,
                    v => { Renderer.LaneArrowOffset = v; Renderer.RefreshPavementMarkings(); },
                    0f, 20f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneArrowLength", "Pavement markings", "Fallback glyph length (m)",
                    () => Renderer.LaneArrowLength,
                    v => { Renderer.LaneArrowLength = v; Renderer.RefreshPavementMarkings(); },
                    0.5f, 10f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneArrowShaftWidth", "Pavement markings", "Fallback glyph shaft width (m)",
                    () => Renderer.LaneArrowShaftWidth,
                    v => { Renderer.LaneArrowShaftWidth = v; Renderer.RefreshPavementMarkings(); },
                    0.1f, 2f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneArrowHeadLength", "Pavement markings", "Fallback glyph head length (m)",
                    () => Renderer.LaneArrowHeadLength,
                    v => { Renderer.LaneArrowHeadLength = v; Renderer.RefreshPavementMarkings(); },
                    0.1f, 4f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneArrowHeadWidth", "Pavement markings", "Fallback glyph head width (m)",
                    () => Renderer.LaneArrowHeadWidth,
                    v => { Renderer.LaneArrowHeadWidth = v; Renderer.RefreshPavementMarkings(); },
                    0.1f, 4f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneArrowStackSpacing", "Pavement markings", "Fallback stacked arrows spacing (m)",
                    () => Renderer.LaneArrowStackSpacing,
                    v => { Renderer.LaneArrowStackSpacing = v; Renderer.RefreshPavementMarkings(); },
                    0.5f, 20f);
                TuningRegistry.RegisterFloat(
                    "renderer.straightTurnThresholdDeg", "Pavement markings", "Straight vs turn threshold (°)",
                    () => Renderer.StraightTurnThresholdDeg,
                    v => { Renderer.StraightTurnThresholdDeg = v; Renderer.RefreshPavementMarkings(); },
                    5f, 75f);

                // -------- Traffic signs --------
                TuningRegistry.RegisterBool(
                    "renderer.showSigns", "Traffic signs", "Show traffic control signs",
                    () => Renderer.ShowSigns,
                    v => { Renderer.ShowSigns = v; Renderer.Rebuild(); });
                TuningRegistry.RegisterColor(
                    "renderer.signTint", "Traffic signs", "Sign tint",
                    () => Renderer.SignTint,
                    v => { Renderer.SignTint = v; Renderer.RefreshSigns(); });
                TuningRegistry.RegisterFloat(
                    "renderer.signAlpha", "Traffic signs", "Sign alpha",
                    () => Renderer.SignAlpha,
                    v => { Renderer.SignAlpha = v; Renderer.RefreshSigns(); },
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "renderer.signSize", "Traffic signs", "Sign size (m)",
                    () => Renderer.SignSize,
                    v => { Renderer.SignSize = v; Renderer.RefreshSigns(); },
                    0.5f, 8f);
                TuningRegistry.RegisterFloat(
                    "renderer.signShoulderClearance", "Traffic signs", "Sign offset from road edge (m)",
                    () => Renderer.SignShoulderClearance,
                    v => { Renderer.SignShoulderClearance = v; Renderer.RefreshSigns(); },
                    0f, 10f);
                TuningRegistry.RegisterFloat(
                    "renderer.signAlongOffset", "Traffic signs", "Sign nudge along road (m)",
                    () => Renderer.SignAlongOffset,
                    v => { Renderer.SignAlongOffset = v; Renderer.RefreshSigns(); },
                    -10f, 10f);
                TuningRegistry.RegisterFloat(
                    "renderer.signHeight", "Traffic signs", "Sign height above asphalt (m)",
                    () => Renderer.SignHeight,
                    v => { Renderer.SignHeight = v; Renderer.RefreshSigns(); },
                    0f, 0.5f);

                // -------- Lane markings (intersection painted guides) --------
                TuningRegistry.RegisterBool(
                    "renderer.showLaneMarkings", "Intersection lane markings", "Show painted lane markings",
                    () => Renderer.ShowLaneMarkings,
                    v => { Renderer.ShowLaneMarkings = v; Renderer.Rebuild(); });
                TuningRegistry.RegisterColor(
                    "renderer.laneMarkingWhite", "Intersection lane markings", "White color",
                    () => Renderer.LaneMarkingWhite,
                    v => { Renderer.LaneMarkingWhite = v; Renderer.RefreshLaneMarkings(); });
                TuningRegistry.RegisterColor(
                    "renderer.laneMarkingYellow", "Intersection lane markings", "Yellow color",
                    () => Renderer.LaneMarkingYellow,
                    v => { Renderer.LaneMarkingYellow = v; Renderer.RefreshLaneMarkings(); });
                TuningRegistry.RegisterFloat(
                    "renderer.laneMarkingAlpha", "Intersection lane markings", "Marking alpha (master)",
                    () => Renderer.LaneMarkingAlpha,
                    v => { Renderer.LaneMarkingAlpha = v; Renderer.RefreshLaneMarkings(); },
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneMarkingWidth", "Intersection lane markings", "Stripe width (m)",
                    () => Renderer.LaneMarkingWidth,
                    v => { Renderer.LaneMarkingWidth = v; Renderer.RefreshLaneMarkings(); },
                    0.03f, 1f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneMarkingHeight", "Intersection lane markings", "Stripe height above asphalt (m)",
                    () => Renderer.LaneMarkingHeight,
                    v => { Renderer.LaneMarkingHeight = v; Renderer.RefreshLaneMarkings(); },
                    0f, 0.1f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneMarkingDashLength", "Intersection lane markings", "Dash length (m)",
                    () => Renderer.LaneMarkingDashLength,
                    v => { Renderer.LaneMarkingDashLength = v; Renderer.RefreshLaneMarkings(); },
                    0.1f, 15f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneMarkingDashGap", "Intersection lane markings", "Dash gap (m)",
                    () => Renderer.LaneMarkingDashGap,
                    v => { Renderer.LaneMarkingDashGap = v; Renderer.RefreshLaneMarkings(); },
                    0.05f, 20f);
                TuningRegistry.RegisterFloat(
                    "renderer.laneMarkingBezierSamples", "Intersection lane markings", "Bezier samples",
                    () => Renderer.LaneMarkingBezierSamples,
                    v => { Renderer.LaneMarkingBezierSamples = Mathf.Clamp(Mathf.RoundToInt(v), 8, 64); Renderer.RefreshLaneMarkings(); },
                    8f, 64f, 1f);

                // -------- Dead-end caps --------
                TuningRegistry.RegisterBool(
                    "renderer.showDeadEndCaps", "Dead-end caps", "Show dead-end caps",
                    () => Renderer.ShowDeadEndCaps,
                    v => { Renderer.ShowDeadEndCaps = v; Renderer.Rebuild(); });
                TuningRegistry.RegisterFloat(
                    "renderer.deadEndCapSegments", "Dead-end caps", "Cap tessellation segments",
                    () => Renderer.DeadEndCapSegments,
                    v => { Renderer.DeadEndCapSegments = Mathf.Clamp(Mathf.RoundToInt(v), 8, 64); Renderer.Rebuild(); },
                    8f, 64f, 1f);
            }

            if (Ambiance != null)
            {
                TuningRegistry.RegisterFloat(
                    "ambiance.sunIntensity", "Ambiance", "Sun intensity",
                    () => Ambiance.SunIntensity,
                    v => { Ambiance.SunIntensity = v; ReapplyAmbiance(); },
                    0f, 2f);

                TuningRegistry.RegisterFloat(
                    "ambiance.shadowStrength", "Ambiance", "Shadow strength",
                    () => Ambiance.ShadowStrength,
                    v => { Ambiance.ShadowStrength = v; ReapplyAmbiance(); },
                    0f, 1f);

                TuningRegistry.RegisterColor(
                    "ambiance.sunColor", "Ambiance", "Sun color",
                    () => Ambiance.SunColor,
                    v => { Ambiance.SunColor = v; ReapplyAmbiance(); });

                TuningRegistry.RegisterColor(
                    "ambiance.ambientColor", "Ambiance", "Ambient color",
                    () => Ambiance.AmbientColor,
                    v => { Ambiance.AmbientColor = v; ReapplyAmbiance(); });

                TuningRegistry.RegisterColor(
                    "ambiance.backgroundColor", "Ambiance", "Background color",
                    () => Ambiance.BackgroundColor,
                    v => { Ambiance.BackgroundColor = v; ReapplyAmbiance(); });

                TuningRegistry.RegisterVector3(
                    "ambiance.sunEulerAngles", "Ambiance", "Sun rotation (deg)",
                    () => Ambiance.SunEulerAngles,
                    v => { Ambiance.SunEulerAngles = v; ReapplyAmbiance(); },
                    -180f, 180f);
            }

            if (Grid != null)
            {
                TuningRegistry.RegisterBool(
                    "grid.enabled", "Ground grid", "Visible",
                    () => Grid.Enabled,
                    v => { Grid.Enabled = v; Grid.Rebuild(); });

                TuningRegistry.RegisterBool(
                    "grid.showMinor", "Ground grid", "Show minor lines",
                    () => Grid.ShowMinor,
                    v => { Grid.ShowMinor = v; Grid.Rebuild(); });

                TuningRegistry.RegisterBool(
                    "grid.showMajor", "Ground grid", "Show major lines",
                    () => Grid.ShowMajor,
                    v => { Grid.ShowMajor = v; Grid.Rebuild(); });

                TuningRegistry.RegisterFloat(
                    "grid.spacing", "Ground grid", "Spacing (m)",
                    () => Grid.Spacing,
                    v => { Grid.Spacing = v; Grid.Rebuild(); },
                    0.1f, 50f);

                TuningRegistry.RegisterFloat(
                    "grid.extent", "Ground grid", "Half-extent (m, grid spans 2×)",
                    () => Grid.Extent,
                    v => { Grid.Extent = v; Grid.Rebuild(); },
                    10f, 2000f);

                TuningRegistry.RegisterFloat(
                    "grid.majorEvery", "Ground grid", "Major line every Nth",
                    () => Grid.MajorEvery,
                    v => { Grid.MajorEvery = Mathf.Max(1, Mathf.RoundToInt(v)); Grid.Rebuild(); },
                    1f, 100f, 1f);

                TuningRegistry.RegisterColor(
                    "grid.minorColor", "Ground grid", "Minor line color",
                    () => Grid.MinorColor,
                    v => { Grid.MinorColor = v; Grid.Rebuild(); });

                TuningRegistry.RegisterColor(
                    "grid.majorColor", "Ground grid", "Major line color",
                    () => Grid.MajorColor,
                    v => { Grid.MajorColor = v; Grid.Rebuild(); });
            }

            if (GroundTiler != null)
            {
                TuningRegistry.RegisterFloat(
                    "ground.tileSize", "Ground", "Tile size (m)",
                    () => GroundTiler.TileSize,
                    v => { GroundTiler.TileSize = v; GroundTiler.Apply(); },
                    0.25f, 50f);
            }

            if (Orbit != null)
            {
                TuningRegistry.RegisterFloat(
                    "orbit.distance", "Camera", "Distance",
                    // Bind to the smoothed target, not the animating
                    // Distance — per-frame ease would otherwise be read
                    // back as a "change" by the poll, and writes from
                    // the panel would fight the smoother.
                    () => Orbit.DistanceTarget,
                    v => Orbit.DistanceTarget = Mathf.Min(v, Orbit.MaxDistance),
                    1f, 5000f);
                TuningRegistry.RegisterFloat(
                    "orbit.maxDistance", "Camera", "Max distance",
                    () => Orbit.MaxDistance,
                    v => Orbit.MaxDistance = v,
                    100f, 10000f);
                TuningRegistry.RegisterFloat(
                    "orbit.farClipPlane", "Camera", "Far clip plane (m)",
                    () => Orbit.FarClipPlane,
                    v => Orbit.FarClipPlane = v,
                    100f, 50000f);
                TuningRegistry.RegisterFloat(
                    "orbit.pitch", "Camera", "Pitch (deg)",
                    () => Orbit.Pitch,
                    v => Orbit.Pitch = v,
                    -89f, 89f);
                TuningRegistry.RegisterFloat(
                    "orbit.yaw", "Camera", "Yaw (deg)",
                    () => Orbit.Yaw,
                    v => Orbit.Yaw = v,
                    -360f, 360f);
                TuningRegistry.RegisterVector3(
                    "orbit.target", "Camera", "Pivot target (world XYZ)",
                    () => Orbit.Target,
                    v => Orbit.Target = v,
                    -10000f, 10000f);
                TuningRegistry.RegisterFloat(
                    "orbit.keyboardPanSensitivity", "Camera", "WASD pan speed (× distance/s)",
                    () => Orbit.KeyboardPanSensitivity,
                    v => Orbit.KeyboardPanSensitivity = v,
                    0f, 10f);
                TuningRegistry.RegisterFloat(
                    "orbit.keyboardPanShiftMultiplier", "Camera", "WASD shift-boost multiplier",
                    () => Orbit.KeyboardPanShiftMultiplier,
                    v => Orbit.KeyboardPanShiftMultiplier = v,
                    1f, 10f);
                TuningRegistry.RegisterFloat(
                    "orbit.zoomSensitivity", "Camera", "Zoom per wheel notch (× distance)",
                    () => Orbit.ZoomSensitivity,
                    v => Orbit.ZoomSensitivity = v,
                    0f, 30f);
                TuningRegistry.RegisterFloat(
                    "orbit.zoomSmoothing", "Camera", "Zoom smoothing (1/s)",
                    () => Orbit.ZoomSmoothing,
                    v => Orbit.ZoomSmoothing = v,
                    0f, 40f);
            }

            if (Agents != null)
            {
                // -------- Agents --------
                // Speeds are displayed and persisted in km/h. The
                // physics simulation stays in m/s internally; getters
                // multiply by KPH_PER_MPS on read, setters divide on
                // write. Brake-distance + acceleration math (which
                // assume m/s²) is unaffected.
                TuningRegistry.RegisterFloat(
                    "agents.defaultSpeed", "Agents", "Default speed (km/h)",
                    () => Agents.DefaultSpeed * KPH_PER_MPS,
                    v =>
                    {
                        Agents.DefaultSpeed = v / KPH_PER_MPS;
                        // Push live to existing agents too — otherwise
                        // a slider drag only affects FUTURE spawns,
                        // which is confusing in a tuning panel.
                        Agents.ApplyDefaultSpeedToAllAgents();
                    },
                    0f, 200f);
                TuningRegistry.RegisterFloat(
                    "agents.speedVariationStdDev", "Agents", "Speed variation σ (km/h)",
                    () => Agents.SpeedVariationStdDev * KPH_PER_MPS,
                    v =>
                    {
                        Agents.SpeedVariationStdDev = v / KPH_PER_MPS;
                        // Re-sample SpeedBias for existing agents so
                        // the change is visible immediately.
                        Agents.ApplyDefaultSpeedToAllAgents();
                    },
                    0f, 50f);
                TuningRegistry.RegisterFloat(
                    "agents.minSpawnGap", "Agents", "Min spawn gap (m)",
                    () => Agents.MinSpawnGap,
                    v => Agents.MinSpawnGap = v,
                    0f, 30f);
                TuningRegistry.RegisterFloat(
                    "agents.maxSpawnPerFrame", "Agents", "Max spawn attempts / frame",
                    () => Agents.MaxSpawnAttemptsPerFrame,
                    v => Agents.MaxSpawnAttemptsPerFrame = Mathf.Max(1, Mathf.RoundToInt(v)),
                    1f, 100f);
                TuningRegistry.RegisterBool(
                    "agents.paused", "Agents", "Paused",
                    () => Agents.Paused,
                    v => Agents.Paused = v);
                TuningRegistry.RegisterFloat(
                    "agents.diameter", "Agents", "Capsule diameter (m)",
                    () => Agents.AgentDiameter,
                    v => { Agents.AgentDiameter = v; Agents.RefreshAllAgentVisuals(); },
                    0.2f, 10f);
                TuningRegistry.RegisterFloat(
                    "agents.height", "Agents", "Capsule height (m)",
                    () => Agents.AgentHeight,
                    v => { Agents.AgentHeight = v; Agents.RefreshAllAgentVisuals(); },
                    0.2f, 10f);
                TuningRegistry.RegisterFloat(
                    "agents.yLift", "Agents", "Y-lift above ground (m)",
                    () => Agents.AgentYLift,
                    v => { Agents.AgentYLift = v; Agents.RefreshAllAgentVisuals(); },
                    0f, 5f);
                TuningRegistry.RegisterFloat(
                    "agents.vehicleScale", "Agents", "Vehicle prefab scale",
                    () => Agents.AgentVehicleScale,
                    v => { Agents.AgentVehicleScale = v; Agents.RefreshAllAgentVisuals(); },
                    0.1f, 10f);
                TuningRegistry.RegisterFloat(
                    "agents.vehicleYLift", "Agents", "Vehicle Y-lift (m)",
                    () => Agents.AgentVehicleYLift,
                    v => Agents.AgentVehicleYLift = v,
                    -2f, 5f);
                TuningRegistry.RegisterFloat(
                    "agents.length", "Agents", "Agent hit-box length (m)",
                    () => Agents.AgentLength,
                    v => Agents.AgentLength = v,
                    0f, 12f);
                TuningRegistry.RegisterColor(
                    "agents.color", "Agents", "Color",
                    () => Agents.AgentColor,
                    v => { Agents.AgentColor = v; Agents.RefreshAllAgentVisuals(); });
                TuningRegistry.RegisterFloat(
                    "agents.intersectionCtrlFraction", "Agents", "Intersection bezier ctrl (0-1)",
                    () => Agents.IntersectionBezierControlFraction,
                    v => Agents.IntersectionBezierControlFraction = v,
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "agents.laneChangeDistance", "Agents", "Lane change distance (m)",
                    () => Agents.LaneChangeDistanceMeters,
                    v => Agents.LaneChangeDistanceMeters = v,
                    1f, 200f);
                TuningRegistry.RegisterBool(
                    "agents.overtakingEnabled", "Agents", "Overtaking enabled",
                    () => Agents.OvertakingEnabled,
                    v => Agents.OvertakingEnabled = v);
                TuningRegistry.RegisterFloat(
                    "agents.overtakeSpeedRatio", "Agents", "Overtake speed ratio (0-1)",
                    () => Agents.OvertakeSpeedRatio,
                    v => Agents.OvertakeSpeedRatio = v,
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "agents.overtakeMinRemaining", "Agents", "Overtake min remaining road (m)",
                    () => Agents.OvertakeMinRemainingMeters,
                    v => Agents.OvertakeMinRemainingMeters = v,
                    1f, 200f);
                TuningRegistry.RegisterFloat(
                    "agents.overtakeClearAhead", "Agents", "Overtake clear-ahead (m)",
                    () => Agents.OvertakeClearAhead,
                    v => Agents.OvertakeClearAhead = v,
                    1f, 100f);
                TuningRegistry.RegisterFloat(
                    "agents.overtakeClearBehind", "Agents", "Overtake clear-behind (m)",
                    () => Agents.OvertakeClearBehind,
                    v => Agents.OvertakeClearBehind = v,
                    0f, 50f);
                TuningRegistry.RegisterBool(
                    "agents.overtakeAllowReroute", "Agents", "Overtake allow reroute",
                    () => Agents.OvertakeAllowReroute,
                    v => Agents.OvertakeAllowReroute = v);
                TuningRegistry.RegisterFloat(
                    "agents.laneChangeCooldownSeconds", "Agents", "Lane change cool-down (s)",
                    () => Agents.LaneChangeCooldownSeconds,
                    v => Agents.LaneChangeCooldownSeconds = v,
                    0f, 15f);
                TuningRegistry.RegisterBool(
                    "agents.yieldRightEnabled", "Agents", "Yield-right enabled",
                    () => Agents.YieldRightEnabled,
                    v => Agents.YieldRightEnabled = v);
                TuningRegistry.RegisterFloat(
                    "agents.yieldRightSpeedDelta", "Agents", "Yield-right speed delta (km/h)",
                    () => Agents.YieldRightSpeedDelta * KPH_PER_MPS,
                    v => Agents.YieldRightSpeedDelta = v / KPH_PER_MPS,
                    0f, 40f);
                TuningRegistry.RegisterFloat(
                    "agents.yieldRightLookBehind", "Agents", "Yield-right look-behind (m)",
                    () => Agents.YieldRightLookBehind,
                    v => Agents.YieldRightLookBehind = v,
                    5f, 100f);
                TuningRegistry.RegisterFloat(
                    "agents.followLookAhead", "Agents", "Follow look-ahead (m)",
                    () => Agents.FollowLookAhead,
                    v => Agents.FollowLookAhead = v,
                    0f, 100f);
                TuningRegistry.RegisterFloat(
                    "agents.followMinDistance", "Agents", "Follow min distance (m)",
                    () => Agents.FollowMinDistance,
                    v => Agents.FollowMinDistance = v,
                    0f, 30f);
                TuningRegistry.RegisterFloat(
                    "agents.followComfortDistance", "Agents", "Follow comfort distance (m)",
                    () => Agents.FollowComfortDistance,
                    v => Agents.FollowComfortDistance = v,
                    0f, 60f);
                TuningRegistry.RegisterFloat(
                    "agents.followConeAngle", "Agents", "Follow cone half-angle (deg)",
                    () => Agents.FollowConeAngleDeg,
                    v => Agents.FollowConeAngleDeg = v,
                    0f, 90f);
                TuningRegistry.RegisterFloat(
                    "agents.followAcceleration", "Agents", "Acceleration (m/s²)",
                    () => Agents.FollowAcceleration,
                    v => Agents.FollowAcceleration = v,
                    0.5f, 30f);
                TuningRegistry.RegisterFloat(
                    "agents.followDeceleration", "Agents", "Deceleration (m/s²)",
                    () => Agents.FollowDeceleration,
                    v => Agents.FollowDeceleration = v,
                    0.5f, 50f);
                // -------- Physics feel --------
                TuningRegistry.RegisterFloat(
                    "agents.maxJerk", "Agents", "Max jerk (m/s³)",
                    () => Agents.MaxJerk,
                    v => Agents.MaxJerk = v,
                    0f, 40f);
                TuningRegistry.RegisterFloat(
                    "agents.maxLateralAccel", "Agents", "Max cornering lateral accel (m/s²)",
                    () => Agents.MaxLateralAccel,
                    v => Agents.MaxLateralAccel = v,
                    0.5f, 20f);
                TuningRegistry.RegisterFloat(
                    "agents.corneringLookahead", "Agents", "Cornering lookahead (0-1)",
                    () => Agents.CorneringLookaheadFraction,
                    v => Agents.CorneringLookaheadFraction = v,
                    0f, 1f);
                TuningRegistry.RegisterFloat(
                    "agents.rollPerLatAccel", "Agents", "Body roll per lat-accel (deg per m/s²)",
                    () => Agents.RollPerLateralAccel,
                    v => Agents.RollPerLateralAccel = v,
                    0f, 10f);
                TuningRegistry.RegisterFloat(
                    "agents.maxRollDeg", "Agents", "Max body roll (deg)",
                    () => Agents.MaxRollDeg,
                    v => Agents.MaxRollDeg = v,
                    0f, 30f);
                TuningRegistry.RegisterFloat(
                    "agents.pitchPerLongAccel", "Agents", "Body pitch per long-accel (deg per m/s²)",
                    () => Agents.PitchPerLongAccel,
                    v => Agents.PitchPerLongAccel = v,
                    0f, 5f);
                TuningRegistry.RegisterFloat(
                    "agents.maxPitchDeg", "Agents", "Max body pitch (deg)",
                    () => Agents.MaxPitchDeg,
                    v => Agents.MaxPitchDeg = v,
                    0f, 20f);
                TuningRegistry.RegisterFloat(
                    "agents.bodyEaseRate", "Agents", "Body roll/pitch ease rate (1/s)",
                    () => Agents.BodyEaseRate,
                    v => Agents.BodyEaseRate = v,
                    0.5f, 30f);
                TuningRegistry.RegisterBool(
                    "agents.intersectionRightOfWay", "Agents", "Intersection right-of-way",
                    () => Agents.IntersectionRightOfWay,
                    v => Agents.IntersectionRightOfWay = v);
                TuningRegistry.RegisterFloat(
                    "agents.intersectionBrakeMargin", "Agents", "Intersection brake margin (m)",
                    () => Agents.IntersectionBrakeMargin,
                    v => Agents.IntersectionBrakeMargin = v,
                    0f, 20f);
                TuningRegistry.RegisterFloat(
                    "agents.intersectionConflictDistance", "Agents", "Intersection conflict distance (m)",
                    () => Agents.IntersectionConflictDistance,
                    v => Agents.IntersectionConflictDistance = v,
                    0f, 10f);
                TuningRegistry.RegisterFloat(
                    "agents.intersectionStopDistance", "Agents", "Intersection stop distance (m)",
                    () => Agents.IntersectionStopDistance,
                    v => Agents.IntersectionStopDistance = v,
                    0f, 10f);
                TuningRegistry.RegisterFloat(
                    "agents.intersectionCommitDistance", "Agents", "Intersection commit distance (m)",
                    () => Agents.IntersectionCommitDistance,
                    v => Agents.IntersectionCommitDistance = v,
                    0f, 10f);
                TuningRegistry.RegisterBool(
                    "agents.obeyTrafficSigns", "Agents", "Obey traffic signs (stop/yield)",
                    () => Agents.ObeyTrafficSigns,
                    v => Agents.ObeyTrafficSigns = v);
                TuningRegistry.RegisterFloat(
                    "agents.stopWaitSeconds", "Agents", "Stop sign wait (seconds)",
                    () => Agents.StopWaitSeconds,
                    v => Agents.StopWaitSeconds = v,
                    0f, 10f);
                TuningRegistry.RegisterFloat(
                    "agents.yieldApproachSpeed", "Agents", "Yield approach speed (km/h)",
                    () => Agents.YieldApproachSpeed * KPH_PER_MPS,
                    v => Agents.YieldApproachSpeed = v / KPH_PER_MPS,
                    0f, 80f);

                if (Designer != null)
                {
                    TuningRegistry.RegisterFloat(
                        "agents.swarmBatchSize", "Agents", "Swarm batch size (G hotkey)",
                        () => Designer.AgentSpawnBatchSize,
                        v => Designer.AgentSpawnBatchSize = Mathf.Max(1, Mathf.RoundToInt(v)),
                        1f, 1000f);
                }
            }
        }

        // Force the designer to drop its cached marker materials so the
        // next RefreshMarker call (or Rebuild) picks up new colors. We
        // invoke via SendMessage to avoid coupling — the method exists on
        // NetworkDesigner (added as part of this change) but using
        // SendMessage means changing the name later won't break compile.
        void RefreshExistingMarkers()
        {
            if (Designer == null) return;
            Designer.RebuildMarkers();
        }

        void ReapplyAmbiance()
        {
            if (Ambiance == null) return;
            Ambiance.Apply();
        }
    }
}
