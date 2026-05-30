// Runtime-only agent that traverses the road network from a start
// vertex to an end vertex along a pre-baked segment list. Not
// persisted — agents live in-session only.
//
// The path is a sequence of alternating AgentRoadSegment +
// AgentIntersectionSegment instances (built at spawn time in
// AgentSystem). Each segment knows how to sample (position, tangent)
// at t ∈ [0, 1]. The agent ticks through segments in order, looping
// back when Loop=true.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Agents
{
    public class Agent
    {
        public string Id;

        // Alternating list: Road, Intersection, Road, ..., Road.
        // First + last are always road segments; intersection segments
        // sit between consecutive road segments.
        public List<AgentSegment> Segments = new List<AgentSegment>();

        public int SegmentIndex;     // current entry in Segments
        public float T;              // 0..1 along current segment
        // Three-speed model.
        //   SpeedBias    — signed per-agent offset (m/s) applied to the
        //     CURRENT base speed (either the road's SpeedLimit or, on
        //     unlimited roads, AgentSystem.DefaultSpeed). Sampled once at
        //     spawn from N(0, SpeedVariationStdDev). Stable for the life
        //     of the agent — same agent stays relatively fast or slow
        //     across every road it traverses, hovering around each
        //     road's posted limit ± bias.
        //   TargetSpeed  — max(0.5, base + SpeedBias) where base is the
        //     current road's SpeedLimit (or DefaultSpeed if unlimited).
        //     Recomputed each frame in UpdateFollowingSpeeds.
        //   Speed        — the CURRENT speed (m/s). Adjusted each frame
        //     toward a "desired" speed via following / intersection /
        //     sign logic. Capped at TargetSpeed.
        public float SpeedBias = 0f;
        public float TargetSpeed = 12f;
        public float Speed = 12f;

        // Current longitudinal acceleration (m/s²). Carried between
        // frames so the speed controller can jerk-limit — ramp the
        // acceleration itself rather than snapping to full accel/decel
        // — giving S-curve speed profiles that read as "weight".
        public float Accel = 0f;
        // Smoothed visual roll (lean into turns) and pitch (nose dive
        // on braking / squat on acceleration), in degrees. Eased toward
        // their physics-derived targets each frame so the body doesn't
        // snap. Visual-only — does not affect path or speed.
        public float VisualRollDeg = 0f;
        public float VisualPitchDeg = 0f;
        public bool Loop = true;

        public string StartVertexId;
        public string EndVertexId;

        // Set by AgentSystem.InvalidateAllAgents() whenever the network
        // mutates (geometry, lane connectivity, profile, etc.). On the
        // next segment boundary, AgentSystem re-plans the remaining
        // path from the agent's current vertex to EndVertexId and
        // rebuilds Segments from there. Avoids mid-segment teleports.
        public bool NeedsRebuild;

        // Realtime timestamp of the agent's last lane change (overtake
        // OR yield-right OR reroute). Compared against
        // AgentSystem.LaneChangeCooldownSeconds to suppress chained
        // lane changes — prevents the overtake↔yield-right ping-pong
        // that otherwise happens when an agent is faster than its lead
        // but slower than someone behind it.
        public float LastLaneChangeRealtime = -1000f;

        // Per-agent stop-sign state. When the agent is approaching an
        // intersection vertex governed by a STOP sign and has come to
        // a full stop (Speed ≈ 0) at the entry, we record when they
        // stopped + which vertex. After AgentSystem.StopWaitSeconds
        // elapses they're cleared to proceed (subject to occupancy).
        // Cleared when the agent passes the vertex.
        public string StoppedAtVertexId;
        public float StoppedAtRealtime;

        // The spawned visual GameObject — animated by AgentSystem.
        [System.NonSerialized] public GameObject Visual;
    }
}
