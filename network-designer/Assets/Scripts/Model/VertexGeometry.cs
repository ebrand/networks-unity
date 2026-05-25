// Computed geometry — the OUTPUT shapes of a future resolver. The
// resolver itself isn't here yet; this file just pins down what the
// rest of the runtime (mesh builders, lane-flow renderers) will consume.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Model
{
    /// <summary>
    /// One road approaching a vertex. The resolver produces one of
    /// these per (vertex, road-end) pair. Order within
    /// <see cref="VertexGeometry.Approaches"/> is clockwise by bearing
    /// so adjacent indices share a fillet.
    /// </summary>
    [Serializable]
    public class VertexApproach
    {
        public string RoadId;
        /// <summary>Which end of the road meets this vertex.</summary>
        public RoadEnd End;
        /// <summary>
        /// Bearing of the road's centerline FROM the vertex, in radians.
        /// 0 = +x; CCW positive (math convention, not compass).
        /// </summary>
        public float Bearing;
        /// <summary>Setback distance along the centerline, meters.</summary>
        public float Setback;

        /// <summary>
        /// Per-lane endpoints on the setback line. The lane's well-known
        /// A or B point (whichever end meets this vertex) moves to here
        /// from the vertex itself.
        /// </summary>
        public List<Vector2> LaneEndsAB = new List<Vector2>();
        public List<Vector2> LaneEndsBA = new List<Vector2>();

        /// <summary>
        /// Per-lane widths in the same order as LaneEndsAB/LaneEndsBA.
        /// Lets renderers compute lane edges (not just centers) without
        /// looking up the road's profile.
        /// </summary>
        public List<float> LaneWidthsAB = new List<float>();
        public List<float> LaneWidthsBA = new List<float>();

        /// <summary>
        /// Outer edges of the entire road (shoulder-to-shoulder) at the
        /// setback line. Used as endpoints of the bezier fillets that
        /// connect adjacent approaches.
        /// </summary>
        public Vector2 OuterLeft;
        public Vector2 OuterRight;

        /// <summary>
        /// Unit direction the outer edges run AWAY from the vertex at the
        /// setback line. For straight roads this is identical to the
        /// bearing direction. For curved roads it's the local bezier
        /// tangent at the setback point — so the OE bezier-corner control
        /// lands on tangents that match where the curved body actually
        /// heads, not where the chord at the vertex points.
        /// </summary>
        public Vector2 OuterEdgeDir;

        /// <summary>
        /// Shoulder width on each side of this approach, expressed in
        /// V's local frame at this approach. ShoulderWidthCW is the
        /// width of the shoulder on the PerpRight(BearingDir) side (the
        /// CW corner of this approach from V's outward look); CCW is
        /// the opposite side. These resolve which of the road's
        /// ShoulderAB/ShoulderBA values ends up on which side, taking
        /// into account drive-side convention and which RoadEnd this is.
        /// </summary>
        public float ShoulderWidthCW;
        public float ShoulderWidthCCW;

        /// <summary>
        /// Per-end stop/yield/none control for this road at this vertex.
        /// Copied from NetworkRoad.ControlA / ControlB depending on End.
        /// Drives the IntersectionSignsRenderer.
        /// </summary>
        public StopYieldControl Control = StopYieldControl.None;
    }

    /// <summary>Which end of a road meets a vertex.</summary>
    public enum RoadEnd { A, B }

    /// <summary>
    /// One segment of a vertex's asphalt outline. The outline is a
    /// closed path consisting of straight setback edges (Line) and
    /// quadratic bezier fillets (QuadraticBezier) between adjacent
    /// road approaches.
    ///
    /// C# doesn't have native discriminated unions, so we model this
    /// as one type with a Kind enum + all fields. The unused control
    /// point is just (0,0) when Kind == Line.
    /// </summary>
    [Serializable]
    public class OutlineSegment
    {
        public SegmentKind Kind;
        public Vector2 From;
        public Vector2 To;
        /// <summary>
        /// Quadratic-bezier control point. Sits at the intersection of
        /// the two adjacent roads' outer edges extended past the setback
        /// line — making the curve tangent to both edges at its endpoints.
        /// Unused when Kind == Line.
        /// </summary>
        public Vector2 Control;
    }

    public enum SegmentKind
    {
        Line,
        QuadraticBezier,
    }

    /// <summary>
    /// Resolved geometry for one vertex: each approach plus the closed
    /// asphalt outline plus the effective lane connectivity (defaults
    /// merged with the vertex's overrides).
    /// </summary>
    [Serializable]
    public class VertexGeometry
    {
        public string VertexId;
        /// <summary>Clockwise by bearing.</summary>
        public List<VertexApproach> Approaches = new List<VertexApproach>();
        /// <summary>Closed path: alternating line setback-edges and bezier fillets.</summary>
        public List<OutlineSegment> Outline = new List<OutlineSegment>();
        /// <summary>Effective connectivity (defaults ∪ overrides).</summary>
        public List<LaneConnection> Connectivity = new List<LaneConnection>();
    }
}
