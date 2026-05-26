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
        //   NaturalSpeed — the agent's preferred cruise speed (m/s).
        //     Sampled at spawn from a bell curve around
        //     AgentSystem.DefaultSpeed (stdev = SpeedVariationStdDev).
        //     Constant for the life of the agent.
        //   TargetSpeed  — NaturalSpeed capped by the current road's
        //     posted SpeedLimit. Recomputed each frame in
        //     UpdateFollowingSpeeds. Equal to NaturalSpeed when on an
        //     intersection segment or a road without a posted limit.
        //   Speed        — the CURRENT speed (m/s). Adjusted each frame
        //     toward a "desired" speed via following / intersection /
        //     sign logic. Capped at TargetSpeed.
        public float NaturalSpeed = 12f;
        public float TargetSpeed = 12f;
        public float Speed = 12f;
        public bool Loop = true;

        public string StartVertexId;
        public string EndVertexId;

        // Set by AgentSystem.InvalidateAllAgents() whenever the network
        // mutates (geometry, lane connectivity, profile, etc.). On the
        // next segment boundary, AgentSystem re-plans the remaining
        // path from the agent's current vertex to EndVertexId and
        // rebuilds Segments from there. Avoids mid-segment teleports.
        public bool NeedsRebuild;

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
