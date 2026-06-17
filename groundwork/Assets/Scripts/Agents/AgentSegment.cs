// One traversable piece of an agent's path. Two kinds:
//
//   AgentRoadSegment        — along a single road's lane centerline,
//                              between the two setback midpoints (so
//                              we skip the intersection asphalt).
//   AgentIntersectionSegment — cubic bezier through an intersection,
//                              from the inbound lane's setback-line
//                              endpoint to the outbound lane's
//                              setback-line endpoint.
//
// The Agent's full path is an alternating list:
//   Road, Intersection, Road, Intersection, ..., Road.
// First and last entries are always Road segments (the path starts
// AT a vertex and ends AT a vertex). Each segment knows how to
// sample (position, tangent) at t ∈ [0, 1] and reports its arc
// length so the agent ticks at constant m/s across the full path.

using UnityEngine;
using NetworkDesigner.Geometry;
using NetworkDesigner.Model;

namespace NetworkDesigner.Agents
{
    public abstract class AgentSegment
    {
        public float ArcLength;

        // Vertex ID the agent is AT when this segment completes.
        // - Road segment: the road's end vertex in travel direction.
        // - Intersection segment: the intersection vertex itself.
        // Used by AgentSystem's invalidation/rebuild handler to know
        // where the agent is at a segment boundary so it can re-route
        // from there.
        public string ToVertexId;

        // Returns world-XZ position + (unnormalized) tangent in the
        // direction of travel at parameter t ∈ [0, 1].
        public abstract void Sample(float t, out Vector2 pos, out Vector2 tangent);

        // Signed curvature κ (1/radius) of the centerline at t. Positive
        // and negative distinguish turn direction (for body roll). Road
        // segments add their lane-offset perpendicular shift, but to
        // first order the centerline curvature dominates, so we use the
        // centerline bezier directly. Default 0 (straight).
        public virtual float CurvatureAt(float t) => 0f;
    }

    public class AgentRoadSegment : AgentSegment
    {
        // Sub-bezier of the road centerline, parameterized in the
        // agent's direction of travel (so t=0 is the start setback,
        // t=1 is the end setback). For straight roads, controls are
        // colinear with the endpoints — SampleCubic still produces
        // the correct lerp.
        public Vector2 P0, C1, C2, P3;

        // Lane offsets at the START and END of the segment. Equal
        // values = agent stays in one lane; different values = lane
        // change happens within this segment over the t-window
        // [LaneChangeStartT, LaneChangeEndT]. Both are signed in
        // PerpRight-of-travel-direction coordinates.
        public float LaneOffsetTravelStart;
        public float LaneOffsetTravelEnd;
        // t window in which the lane-change interpolation runs. For
        // pre-baked changes the end is 1 (merge by segment boundary).
        // For mid-segment overtakes / yield-rights the end is
        // start + LaneChangeDistanceMeters/ArcLength so the merge
        // completes in a fixed metric distance regardless of where on
        // the road it began. If start == end of OFFSETS, both are
        // ignored.
        public float LaneChangeStartT;
        public float LaneChangeEndT = 1f;

        // Lane indices that built this segment (in the road's own
        // AB/BA convention). Stashed so the loop-closing intersection
        // segment can recover the first road's start lane when wrapping
        // back.
        public int SourceStartLaneIndex;
        public int SourceEndLaneIndex;

        // Id of the road this segment traverses — used by AgentSystem's
        // scoped invalidation (InvalidateAgentsForRoad) to mark only
        // agents whose remaining path uses the mutated road.
        public string RoadId;

        public override void Sample(float t, out Vector2 pos, out Vector2 tangent)
        {
            SamplePos(t, out pos);
            // Finite-difference tangent so the agent's heading picks
            // up the perpendicular component during a lane change
            // (otherwise the agent crab-walks sideways instead of
            // angling into the new lane).
            const float dt = 0.005f;
            SamplePos(Mathf.Min(1f, t + dt), out Vector2 posNext);
            Vector2 delta = posNext - pos;
            tangent = delta.sqrMagnitude < 1e-8f ? Vector2.right : delta.normalized;
        }

        void SamplePos(float t, out Vector2 pos)
        {
            t = Mathf.Clamp01(t);
            Vector2 centerPos = GeometryResolver.SampleCubic(P0, C1, C2, P3, t);
            Vector2 rawTangent = GeometryResolver.CubicTangent(P0, C1, C2, P3, t);
            if (rawTangent.sqrMagnitude < 1e-6f) rawTangent = Vector2.right;
            Vector2 unit = rawTangent.normalized;
            Vector2 perpRight = new Vector2(unit.y, -unit.x);
            pos = centerPos + perpRight * OffsetAt(t);
        }

        // Exposed so the overtaking lane-change logic can snapshot
        // the current interpolated offset and start a new change from
        // there, avoiding a visual jump.
        public float OffsetAt(float t)
        {
            if (Mathf.Approximately(LaneOffsetTravelStart, LaneOffsetTravelEnd))
                return LaneOffsetTravelStart;
            float endT = LaneChangeEndT > LaneChangeStartT ? LaneChangeEndT : 1f;
            if (t <= LaneChangeStartT) return LaneOffsetTravelStart;
            if (t >= endT) return LaneOffsetTravelEnd;
            float u = (t - LaneChangeStartT) / Mathf.Max(endT - LaneChangeStartT, 1e-4f);
            u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u));
            return Mathf.Lerp(LaneOffsetTravelStart, LaneOffsetTravelEnd, u);
        }

        public override float CurvatureAt(float t)
            => GeometryResolver.CubicCurvature(P0, C1, C2, P3, Mathf.Clamp01(t));
    }

    public class AgentIntersectionSegment : AgentSegment
    {
        // Cubic bezier from inbound lane setback endpoint to outbound
        // lane setback endpoint, with controls along the inbound/
        // outbound flow directions (same construction as the lane-flow
        // arrow rendering, so agents visually follow the painted flow).
        public Vector2 P0, C1, C2, P3;

        public override void Sample(float t, out Vector2 pos, out Vector2 tangent)
        {
            pos = GeometryResolver.SampleCubic(P0, C1, C2, P3, t);
            Vector2 raw = GeometryResolver.CubicTangent(P0, C1, C2, P3, t);
            tangent = raw.sqrMagnitude < 1e-6f ? Vector2.right : raw.normalized;
        }

        public override float CurvatureAt(float t)
            => GeometryResolver.CubicCurvature(P0, C1, C2, P3, Mathf.Clamp01(t));
    }
}
