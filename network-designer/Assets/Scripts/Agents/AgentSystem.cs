// Runtime ticker for all agents in the scene. Owns the agent pool;
// updates each agent's position/heading along its segmented path
// every frame; loops back to the start when the path is exhausted
// (or despawns if Loop=false).
//
// The path is a pre-baked list of AgentSegments — alternating road
// + intersection. Road segments traverse a road's lane centerline
// between the two setback midpoints (so we skip the intersection
// asphalt — that's the next segment's job). Intersection segments
// are cubic beziers from one lane's setback endpoint to another,
// matching the painted lane-flow arrows. Lane choice at each
// transition uses the vertex's authored connectivity (defaults +
// overrides); falls back to a random valid lane if no connection
// matches.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;
using NetworkDesigner.Geometry;

namespace NetworkDesigner.Agents
{
    [AddComponentMenu("NetworkDesigner/Agent System")]
    [DisallowMultipleComponent]
    public class AgentSystem : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Network the agents traverse. Assigned at runtime by NetworkDesigner.")]
        // Same [NonSerialized] trap as the other back-references —
        // Unity Inspector data-binding would otherwise auto-instantiate
        // null [Serializable] sub-objects on the Network graph.
        [System.NonSerialized] public Network Network;

        [Header("Simulation")]
        [Tooltip("When true, all agent ticking and spawn-queue draining is suspended. Positions freeze; the speed pre-pass still runs once on the next unpause so following state is consistent. Toggled by NetworkDesigner via the Pause hotkey.")]
        public bool Paused;

        [Header("Defaults")]
        [Tooltip("Default forward speed (m/s) for newly spawned agents.")]
        public float DefaultSpeed = 12f;
        [Tooltip("Per-agent speed variation as standard deviation (m/s) of a bell curve around DefaultSpeed. 0 = every agent gets exactly DefaultSpeed. Try 2–4 for natural-looking traffic with some agents passing others.")]
        public float SpeedVariationStdDev = 0f;
        [Tooltip("Minimum distance (m) between a new agent's spawn position and any existing agent. Prevents stacked spawns where two agents start in the same lane on the same road and travel forever together.")]
        public float MinSpawnGap = 6f;
        [Tooltip("Maximum spawn attempts per frame from the pending queue. Larger = faster ramp-up; smaller = smoother visual entry. Most attempts succeed early then taper as dead-ends fill.")]
        public int MaxSpawnAttemptsPerFrame = 20;
        [Tooltip("Visual capsule diameter (m).")]
        public float AgentDiameter = 1.6f;
        [Tooltip("Visual capsule height (m) — vertical extent above the road surface.")]
        public float AgentHeight = 1.8f;
        [Tooltip("Y-lift (m) of the agent's center above ground so the capsule sits ON the road, not inside it.")]
        public float AgentYLift = 0.9f;
        public Color AgentColor = new Color(0.3f, 0.85f, 0.4f, 1f);

        [Header("Intersection bezier")]
        [Tooltip("Bezier control-point length as a fraction of the chord between the inbound and outbound lane endpoints at a vertex. Matches the lane-flow arrow rendering when set to the same value.")]
        [Range(0f, 1f)] public float IntersectionBezierControlFraction = 0.45f;

        [Header("Lane changing")]
        [Tooltip("Distance (m) before the end of a road over which an agent smoothly drifts from its entering lane to its exiting lane when those differ. Short roads get a proportionally shorter merge.")]
        public float LaneChangeDistanceMeters = 30f;

        [Header("Overtaking")]
        [Tooltip("Enable mid-road lane changes to pass slower traffic. Only changes to lanes that still let the agent make their next turn. Disable to revert to follow-only Tier 1 behavior.")]
        public bool OvertakingEnabled = true;
        [Tooltip("Consider overtaking when current Speed is below this fraction of TargetSpeed. 0.7 = consider overtaking when going 70% or slower than desired.")]
        [Range(0f, 1f)] public float OvertakeSpeedRatio = 0.7f;
        [Tooltip("Minimum remaining road length (m) required to attempt an overtake — short of this, there's not enough room to lane-change safely before the intersection.")]
        public float OvertakeMinRemainingMeters = 20f;
        [Tooltip("Distance (m) ahead in the candidate adjacent lane that must be clear of other agents.")]
        public float OvertakeClearAhead = 15f;

        [Header("Following (agent-to-agent awareness)")]
        [Tooltip("Maximum distance (m) to look ahead for another agent. Agents farther than this are ignored.")]
        public float FollowLookAhead = 25f;
        [Tooltip("Closer than this (m) → agent comes to a full stop. The 'bumper distance'.")]
        public float FollowMinDistance = 4f;
        [Tooltip("Farther than this (m) → agent cruises at TargetSpeed. Between MinDistance and ComfortDistance the speed scales linearly.")]
        public float FollowComfortDistance = 15f;
        [Tooltip("Cone half-angle (degrees) for 'is this other agent ahead of me?'. Wider catches agents on curving roads / lane changes; narrower ignores side traffic.")]
        [Range(0f, 90f)] public float FollowConeAngleDeg = 60f;
        [Tooltip("How fast the agent can speed up (m/s² when a gap opens).")]
        public float FollowAcceleration = 6f;
        [Tooltip("How fast the agent can slow down (m/s² when closing a gap). Typically higher than acceleration — cars brake faster than they accelerate.")]
        public float FollowDeceleration = 12f;

        [Header("Intersection right-of-way")]
        [Tooltip("Enable intersection right-of-way: an approaching agent waits if its PLANNED intersection bezier would cross another agent's currently-traversing bezier. Diverging paths (Y-fork) and non-crossing paths through the same vertex are allowed to proceed simultaneously.")]
        public bool IntersectionRightOfWay = true;
        [Tooltip("Two intersection-bezier paths are considered conflicting if any pair of sampled points along them is within this distance (meters). ~lane width is a good default.")]
        public float IntersectionConflictDistance = 3f;
        [Tooltip("Extra safety distance (m) added to the kinematic brake distance when deciding how far out from an occupied intersection to start braking. Larger = earlier braking, smoother stops.")]
        public float IntersectionBrakeMargin = 4f;
        [Tooltip("How close to the entry (m) the agent stops when blocked. Smaller = agent stops nearer the entry; larger = bigger buffer.")]
        public float IntersectionStopDistance = 1.5f;
        [Tooltip("Once the agent is within this distance (m) of the intersection entry it COMMITS — ignores occupancy and proceeds. Prevents the 'stuck in oscillation when cross-traffic flickers occupancy' bug.")]
        public float IntersectionCommitDistance = 1.5f;

        [Header("Traffic signs")]
        [Tooltip("Agents respect STOP signs (full stop + wait + proceed if clear) and YIELD signs (cap approach speed + proceed if clear). Disable to ignore all signs.")]
        public bool ObeyTrafficSigns = true;
        [Tooltip("Seconds an agent must remain at full stop at a STOP sign before being allowed to proceed.")]
        public float StopWaitSeconds = 1.0f;
        [Tooltip("Speed (m/s) cap on approach to a YIELD-controlled intersection (agent slows but doesn't fully stop unless cross-traffic is present).")]
        public float YieldApproachSpeed = 4f;

        readonly List<Agent> _agents = new List<Agent>();

        // Pending swarm spawns. SpawnRandomAgents queues into this;
        // each Update call drains up to MaxSpawnAttemptsPerFrame
        // tries. Lets large batches (400+) ramp up over time as
        // earlier-spawned agents move out of their dead-ends and
        // free spawn space for new ones.
        int _pendingSpawnCount;
        readonly List<Vertex> _pendingSpawnDeadEnds = new List<Vertex>();
        // Reusable per-spawn cache: vertex ID → resolved geometry.
        // Avoids re-resolving the same vertex N times when building a
        // multi-step path.
        readonly Dictionary<string, VertexGeometry> _vgCache = new Dictionary<string, VertexGeometry>();

        public IReadOnlyList<Agent> Agents => _agents;

        public Agent SpawnAgent(string startVertexId, string endVertexId)
        {
            if (Network == null)
            {
                Debug.LogWarning("[AgentSystem] Spawn refused: no Network assigned.");
                return null;
            }
            var path = AgentPathfinder.FindPath(Network, startVertexId, endVertexId);
            if (path == null || path.Count == 0)
            {
                Debug.LogWarning($"[AgentSystem] No route from '{startVertexId}' to '{endVertexId}' — agent not spawned.");
                return null;
            }
            List<AgentSegment> segments = BuildSegments(path, startVertexId);
            if (segments == null || segments.Count == 0)
            {
                Debug.LogWarning($"[AgentSystem] Segment build failed for path '{startVertexId}' → '{endVertexId}'.");
                return null;
            }

            // Compute the spawn world position from the first segment
            // so both the gap check AND the initial visual placement
            // below see the same value.
            Vector3 spawnPos = Vector3.zero;
            Vector2 spawnTangent = Vector2.right;
            if (segments[0] != null)
            {
                segments[0].Sample(0f, out Vector2 spawnPos2D, out spawnTangent);
                spawnPos = new Vector3(spawnPos2D.x, AgentYLift, spawnPos2D.y);
            }

            // Spawn-gap check: refuse to spawn on top of another agent.
            // Tier 1 follow excludes distance=0 from its cone, so a
            // stacked-spawn pair would travel together at identical
            // speed forever. Reject the spawn instead; SpawnRandomAgents'
            // retry loop will try a different (start, end) pair.
            if (MinSpawnGap > 0f && segments[0] != null)
            {
                float minGapSq = MinSpawnGap * MinSpawnGap;
                for (int i = 0; i < _agents.Count; i++)
                {
                    Agent existing = _agents[i];
                    if (existing.Visual == null) continue;
                    if ((existing.Visual.transform.position - spawnPos).sqrMagnitude < minGapSq)
                        return null;
                }
            }

            // Sample this agent's natural cruise speed. With stdev > 0,
            // a bell curve around DefaultSpeed; clamped to a positive
            // floor so we never end up with stationary "agents".
            float naturalSpeed = SampleGaussian(DefaultSpeed, SpeedVariationStdDev);
            naturalSpeed = Mathf.Max(0.5f, naturalSpeed);
            Agent a = new Agent
            {
                Id = $"a-{System.Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Segments = segments,
                SegmentIndex = 0,
                T = 0f,
                NaturalSpeed = naturalSpeed,
                TargetSpeed = naturalSpeed,
                Speed = naturalSpeed,
                Loop = true,
                StartVertexId = startVertexId,
                EndVertexId = endVertexId,
            };
            a.Visual = CreateVisual(a);
            // Position the visual right away so subsequent same-frame
            // spawn-gap checks see the actual world position (not
            // (0,0,0), which would let stacked spawns through).
            a.Visual.transform.position = spawnPos;
            if (spawnTangent.sqrMagnitude > 1e-6f)
            {
                Vector3 fwd = new Vector3(spawnTangent.x, 0f, spawnTangent.y).normalized;
                a.Visual.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            }
            _agents.Add(a);
            return a;
        }

        public int SpawnRandomAgents(int count)
        {
            if (Network == null || Network.Vertices == null || Network.Vertices.Count < 2)
            {
                Debug.LogWarning("[AgentSystem] SpawnRandomAgents needs at least 2 vertices.");
                return 0;
            }

            // Refresh dead-end list. Anything that changed (network
            // mutations between G presses) is re-snapshotted here.
            _pendingSpawnDeadEnds.Clear();
            Dictionary<string, int> degree = new Dictionary<string, int>();
            foreach (Vertex v in Network.Vertices) degree[v.Id] = 0;
            foreach (NetworkRoad r in Network.Roads)
            {
                if (degree.ContainsKey(r.EndA)) degree[r.EndA]++;
                if (degree.ContainsKey(r.EndB)) degree[r.EndB]++;
            }
            foreach (Vertex v in Network.Vertices)
                if (degree.TryGetValue(v.Id, out int d) && d == 1) _pendingSpawnDeadEnds.Add(v);

            if (_pendingSpawnDeadEnds.Count < 2)
            {
                Debug.LogWarning($"[AgentSystem] SpawnRandomAgents needs at least 2 dead-end vertices " +
                                 $"(degree=1); found {_pendingSpawnDeadEnds.Count}.");
                return 0;
            }

            // Queue the requested count. Update() drains the queue
            // over time, giving earlier-spawned agents room to clear
            // their dead-ends. Press G again to add more on top.
            _pendingSpawnCount += count;
            Debug.Log($"[AgentSystem] Swarm: queued {count} more agents " +
                      $"(total pending: {_pendingSpawnCount}) across {_pendingSpawnDeadEnds.Count} dead-ends.");
            return 0;
        }

        // Drain a few entries from the pending spawn queue each frame.
        // Failed attempts (gap rejected, no route) just decrement the
        // attempt budget; the count stays pending so we retry next
        // frame when positions may have cleared.
        void DrainSpawnQueue()
        {
            if (_pendingSpawnCount <= 0) return;
            if (_pendingSpawnDeadEnds.Count < 2) return;
            int budget = Mathf.Max(1, MaxSpawnAttemptsPerFrame);
            int n = _pendingSpawnDeadEnds.Count;
            while (budget-- > 0 && _pendingSpawnCount > 0)
            {
                int i = Random.Range(0, n);
                int j = Random.Range(0, n);
                if (i == j) continue;
                Agent a = SpawnAgent(_pendingSpawnDeadEnds[i].Id, _pendingSpawnDeadEnds[j].Id);
                if (a != null) _pendingSpawnCount--;
            }
        }

        /// <summary>
        /// Populate `followBlockers` with agents in `a`'s forward cone
        /// within FollowComfortDistance (the ones causing follow-speed
        /// slowdowns) and `intersectionBlockers` with agents currently
        /// on a conflicting bezier at `a`'s upcoming intersection
        /// (the ones causing right-of-way blocking). Both lists are
        /// cleared before populating. Used by the designer's
        /// blocker-visualization for the selected agent.
        /// </summary>
        public void ComputeBlockers(Agent a, List<Agent> followBlockers, List<Agent> intersectionBlockers)
        {
            if (followBlockers != null) followBlockers.Clear();
            if (intersectionBlockers != null) intersectionBlockers.Clear();
            if (a == null || a.Visual == null) return;

            // Follow blockers: mirror the speed pre-pass's adaptive
            // look-ahead so the viz matches what's actually slowing
            // the agent. The speed loop uses
            //   lookAhead = max(FollowLookAhead, minDist + v²/(2·decel))
            // and we report any agent inside that range in cone.
            Vector3 myPos = a.Visual.transform.position;
            Vector3 myFwd = a.Visual.transform.forward;
            float minCos = Mathf.Cos(Mathf.Clamp(FollowConeAngleDeg, 0f, 89.999f) * Mathf.Deg2Rad);
            float decel = Mathf.Max(FollowDeceleration, 0.1f);
            float kinematicLook = FollowMinDistance + (a.Speed * a.Speed) / (2f * decel);
            float lookAhead = Mathf.Max(FollowLookAhead, kinematicLook);
            float lookSq = lookAhead * lookAhead;
            for (int i = 0; i < _agents.Count; i++)
            {
                Agent o = _agents[i];
                if (o == a || o.Visual == null) continue;
                Vector3 toOther = o.Visual.transform.position - myPos;
                float distSq = toOther.sqrMagnitude;
                if (distSq > lookSq || distSq < 1e-6f) continue;
                float dist = Mathf.Sqrt(distSq);
                float dot = Vector3.Dot(toOther / dist, myFwd);
                if (dot < minCos) continue;
                followBlockers?.Add(o);
            }

            // Intersection blockers: agents currently on an intersection
            // segment at the next vertex whose bezier conflicts with
            // a's upcoming intersection bezier.
            if (a.Segments == null || a.SegmentIndex >= a.Segments.Count - 1) return;
            var nextSeg = a.Segments[a.SegmentIndex + 1] as AgentIntersectionSegment;
            if (nextSeg == null || string.IsNullOrEmpty(nextSeg.ToVertexId)) return;
            for (int i = 0; i < _agents.Count; i++)
            {
                Agent o = _agents[i];
                if (o == a || o.Segments == null || o.SegmentIndex >= o.Segments.Count) continue;
                var otherSeg = o.Segments[o.SegmentIndex] as AgentIntersectionSegment;
                if (otherSeg == null) continue;
                if (otherSeg.ToVertexId != nextSeg.ToVertexId) continue;
                if (BeziersConflict(nextSeg, otherSeg, IntersectionConflictDistance))
                    intersectionBlockers?.Add(o);
            }
        }

        /// <summary>Cancel any pending swarm spawns. Called by H (clear all).</summary>
        public void CancelPendingSpawns()
        {
            _pendingSpawnCount = 0;
            _pendingSpawnDeadEnds.Clear();
        }

        // ----------------------------------------------------------
        // Segment construction
        // ----------------------------------------------------------

        // Build the full alternating segment list for a path. Returns
        // null on degenerate inputs.
        //
        // Lane planning: forward pass with mid-road lane changes.
        // - inLane[0] = random.
        // - At each transition vertex, pick a CONNECTION that satisfies
        //   the path's (current road, current dir) → (next road, next
        //   dir). Prefer connections where From.Index == current
        //   in-lane (no lane change needed). If no such connection,
        //   any matching connection — forces a lane change within
        //   this road.
        // - The chosen connection determines outLane[i] (where the
        //   agent exits this road) and inLane[i+1] (where the agent
        //   enters the next road).
        // - When outLane[i] != inLane[i], the road segment smoothly
        //   interpolates the perpendicular offset over the last
        //   LaneChangeDistanceMeters of the road.
        List<AgentSegment> BuildSegments(List<(NetworkRoad Road, Direction Dir)> path, string startVertexId)
        {
            _vgCache.Clear();
            var segments = new List<AgentSegment>(path.Count * 2);
            int N = path.Count;

            // Precompute the list of transition vertex IDs.
            string[] stepEndVertexIds = new string[N];
            string fromVertexId = startVertexId;
            for (int i = 0; i < N; i++)
            {
                stepEndVertexIds[i] = OtherEndOf(path[i].Road, path[i].Dir, fromVertexId);
                fromVertexId = stepEndVertexIds[i];
            }

            // Forward pass: pick (outLane[i], inLane[i+1]) connection
            // at each vertex, preferring no-lane-change.
            int[] inLane = new int[N];
            int[] outLane = new int[N];
            inLane[0] = PickRandomLaneIndex(path[0].Road, path[0].Dir);
            for (int i = 0; i < N - 1; i++)
            {
                var conn = PickConnectionAtVertex(
                    stepEndVertexIds[i],
                    path[i].Road, path[i].Dir, inLane[i],
                    path[i + 1].Road, path[i + 1].Dir);
                if (conn != null)
                {
                    outLane[i] = conn.From.Index;
                    inLane[i + 1] = conn.To.Index;
                }
                else
                {
                    // No connection at all matches this path step
                    // (resolver produced nothing for this in→out pair,
                    // including defaults — user has explicitly removed
                    // every connection from this road's lanes to the
                    // next road). FAIL the build: the agent literally
                    // can't legally make this turn. SpawnAgent /
                    // RebuildAgent will despawn the agent (or, in
                    // rebuild's case, re-run the pathfinder from the
                    // current vertex which may find an alternate
                    // route).
                    Debug.LogWarning($"[AgentSystem] Path step {i} → {i+1} at vertex " +
                                     $"'{stepEndVertexIds[i]}' has no valid lane connection " +
                                     $"({path[i].Road.Id}|{path[i].Dir} → {path[i+1].Road.Id}|{path[i+1].Dir}). " +
                                     $"Aborting segment build.");
                    return null;
                }
            }
            outLane[N - 1] = PickRandomLaneIndex(path[N - 1].Road, path[N - 1].Dir);

            // Forward pass: build segments using inLane[i] → outLane[i].
            for (int i = 0; i < N; i++)
            {
                var step = path[i];
                AgentRoadSegment roadSeg = BuildRoadSegment(step.Road, step.Dir, inLane[i], outLane[i]);
                if (roadSeg == null) return null;
                // Tag the segment with the vertex it ends at, so the
                // rebuild handler knows where the agent is at a
                // segment boundary.
                roadSeg.ToVertexId = stepEndVertexIds[i];
                segments.Add(roadSeg);

                if (i + 1 < N)
                {
                    var next = path[i + 1];
                    AgentIntersectionSegment isxSeg = BuildIntersectionSegment(
                        stepEndVertexIds[i],
                        step.Road, step.Dir, outLane[i],
                        next.Road, next.Dir, inLane[i + 1]);
                    if (isxSeg != null)
                    {
                        isxSeg.ToVertexId = stepEndVertexIds[i];
                        segments.Add(isxSeg);
                    }
                }
            }

            // Bookkeeping for the loop-back logic below.
            fromVertexId = N > 0 ? stepEndVertexIds[N - 1] : startVertexId;
            int currentLane = outLane[N - 1];

            // If the agent loops, we also need an intersection segment
            // from the LAST road segment back to the FIRST road
            // segment's start. Without it, the agent would teleport
            // from end-of-path back to start-of-path on the next
            // iteration of the loop.
            if (path.Count > 0)
            {
                var lastStep = path[path.Count - 1];
                var firstStep = path[0];
                string lastEndVertexId = fromVertexId; // already updated above
                if (lastEndVertexId == startVertexId)
                {
                    // Start == end: the path forms a cycle. Connect
                    // last road's end → first road's start through
                    // an intersection bezier at the shared vertex.
                    int firstLane = ((AgentRoadSegment)segments[0]).SourceStartLaneIndex;
                    AgentIntersectionSegment loopSeg = BuildIntersectionSegment(
                        startVertexId,
                        lastStep.Road, lastStep.Dir, currentLane,
                        firstStep.Road, firstStep.Dir, firstLane);
                    if (loopSeg != null) segments.Add(loopSeg);
                }
                // If startVertexId != lastEndVertexId, the loop wraps
                // by teleporting — acceptable; the agent visually jumps
                // when it loops back. Future: re-route end→start
                // through the network for a clean loop.
            }
            return segments;
        }

        AgentRoadSegment BuildRoadSegment(NetworkRoad road, Direction dir, int startLane, int endLane)
        {
            Vertex va = FindVertex(road.EndA);
            Vertex vb = FindVertex(road.EndB);
            if (va == null || vb == null) return null;

            // Resolve both endpoints to get setback distances. Cache
            // the geometries so subsequent steps don't re-resolve.
            VertexGeometry vgA = ResolveCached(va);
            VertexGeometry vgB = ResolveCached(vb);
            float setbackA = FindSetback(vgA, road.Id, RoadEnd.A);
            float setbackB = FindSetback(vgB, road.Id, RoadEnd.B);

            // Lateral-offset-aware effective endpoints (same shift the
            // resolver + renderer apply).
            Vector2 pA = GeometryResolver.EffectiveEndpoint(road, RoadEnd.A, va, vb);
            Vector2 pB = GeometryResolver.EffectiveEndpoint(road, RoadEnd.B, vb, va);

            // Centerline sub-bezier between the two setback midpoints.
            // For straight roads, controls are linear interpolations
            // (still produces correct lerp via SampleCubic).
            Vector2 p0, c1, c2, p3;
            if (road.Curve == null)
            {
                // Straight: setback midpoint is endpoint + outward*setback.
                Vector2 dirAB = (pB - pA);
                float len = dirAB.magnitude;
                if (len < 1e-4f) return null;
                Vector2 unit = dirAB / len;
                Vector2 sA = pA + unit * setbackA;
                Vector2 sB = pB - unit * setbackB;
                p0 = sA; p3 = sB;
                c1 = Vector2.Lerp(sA, sB, 1f / 3f);
                c2 = Vector2.Lerp(sA, sB, 2f / 3f);
            }
            else
            {
                Vector2 cA = GeometryResolver.EffectiveControl(road, RoadEnd.A, va, vb);
                Vector2 cB = GeometryResolver.EffectiveControl(road, RoadEnd.B, vb, va);
                float tStart = GeometryResolver.ArcLengthToT(pA, cA, cB, pB, setbackA);
                float tBfromB = GeometryResolver.ArcLengthToT(pB, cB, cA, pA, setbackB);
                float tEnd = 1f - tBfromB;
                if (tEnd <= tStart + 1e-4f)
                {
                    // Setbacks ate the whole curve — degenerate road
                    // body. Skip; agent will basically teleport across.
                    p0 = pA; p3 = pB;
                    c1 = Vector2.Lerp(pA, pB, 1f / 3f);
                    c2 = Vector2.Lerp(pA, pB, 2f / 3f);
                }
                else
                {
                    GeometryResolver.SubCubic(pA, cA, cB, pB, tStart, tEnd,
                        out p0, out c1, out c2, out p3);
                }
            }

            // For BA direction, the agent travels from setback_B to
            // setback_A — reverse the curve so t=0 is at travel start.
            if (dir == Direction.BA)
            {
                var tmp0 = p0; p0 = p3; p3 = tmp0;
                var tmp1 = c1; c1 = c2; c2 = tmp1;
            }

            // Lane offsets in PerpRight-of-AB coords for both endpoints.
            // For BA travel direction the perp basis flips, so we negate.
            float startOffsetAB = GeometryResolver.LaneCenterOffsetSigned(
                road, dir, startLane, Network.DriveSide);
            float endOffsetAB = GeometryResolver.LaneCenterOffsetSigned(
                road, dir, endLane, Network.DriveSide);
            float signFlip = dir == Direction.AB ? 1f : -1f;

            var seg = new AgentRoadSegment
            {
                P0 = p0, C1 = c1, C2 = c2, P3 = p3,
                LaneOffsetTravelStart = startOffsetAB * signFlip,
                LaneOffsetTravelEnd = endOffsetAB * signFlip,
                SourceStartLaneIndex = startLane,
                SourceEndLaneIndex = endLane,
                RoadId = road.Id,
            };
            seg.ArcLength = GeometryResolver.CubicArcLength(p0, c1, c2, p3);
            // Lane change starts LaneChangeDistanceMeters back from the
            // end of the segment, clamped to [0, 1]. Shorter roads get
            // a proportionally shorter merge — the whole segment is the
            // merge if it's shorter than the requested distance.
            seg.LaneChangeStartT = startLane == endLane
                ? 0f
                : Mathf.Clamp01(1f - LaneChangeDistanceMeters / Mathf.Max(seg.ArcLength, 1e-4f));
            return seg;
        }

        AgentIntersectionSegment BuildIntersectionSegment(
            string vertexId,
            NetworkRoad inRoad, Direction inDir, int inLane,
            NetworkRoad outRoad, Direction outDir, int outLane)
        {
            Vertex v = FindVertex(vertexId);
            if (v == null) return null;
            VertexGeometry vg = ResolveCached(v);
            if (vg == null) return null;

            VertexApproach approachIn = FindApproach(vg, inRoad.Id);
            VertexApproach approachOut = FindApproach(vg, outRoad.Id);
            if (approachIn == null || approachOut == null) return null;

            Vector2? inPos = LaneEndpointAt(approachIn, inDir, inLane);
            Vector2? outPos = LaneEndpointAt(approachOut, outDir, outLane);
            if (!inPos.HasValue || !outPos.HasValue) return null;

            // Same bezier construction as LaneFlowRenderer's arrows so
            // agents trace the painted flow exactly.
            Vector2 inboundFlow = approachIn.OuterEdgeDir.sqrMagnitude > 1e-6f
                ? -approachIn.OuterEdgeDir.normalized : Vector2.right;
            Vector2 outboundFlow = approachOut.OuterEdgeDir.sqrMagnitude > 1e-6f
                ? approachOut.OuterEdgeDir.normalized : Vector2.right;
            float chord = Vector2.Distance(inPos.Value, outPos.Value);
            float ctrlLen = chord * IntersectionBezierControlFraction;

            var seg = new AgentIntersectionSegment
            {
                P0 = inPos.Value,
                C1 = inPos.Value + inboundFlow * ctrlLen,
                C2 = outPos.Value - outboundFlow * ctrlLen,
                P3 = outPos.Value,
            };
            seg.ArcLength = GeometryResolver.CubicArcLength(seg.P0, seg.C1, seg.C2, seg.P3);
            return seg;
        }

        // Pick a connection at `vertexId` matching the (inRoad,inDir)
        // → (outRoad,outDir) path step. PREFERS a connection where
        // From.Index == preferredInLane (the agent's current lane —
        // so no lane change is needed). If no such connection exists,
        // falls back to any matching connection (forces a lane change
        // within the inbound road). Returns null if no connection at
        // all matches the (inRoad,inDir) → (outRoad,outDir) pair —
        // means resolver produced nothing for this turn, including
        // defaults.
        LaneConnection PickConnectionAtVertex(
            string vertexId,
            NetworkRoad inRoad, Direction inDir, int preferredInLane,
            NetworkRoad outRoad, Direction outDir)
        {
            Vertex v = FindVertex(vertexId);
            if (v == null) return null;
            VertexGeometry vg = ResolveCached(v);
            if (vg == null || vg.Connectivity == null) return null;

            List<LaneConnection> preferred = null;
            List<LaneConnection> anyMatch = null;
            foreach (LaneConnection c in vg.Connectivity)
            {
                if (c == null || c.From == null || c.To == null) continue;
                if (c.From.RoadId != inRoad.Id) continue;
                if (c.From.Direction != inDir) continue;
                if (c.To.RoadId != outRoad.Id) continue;
                if (c.To.Direction != outDir) continue;
                if (anyMatch == null) anyMatch = new List<LaneConnection>();
                anyMatch.Add(c);
                if (c.From.Index == preferredInLane)
                {
                    if (preferred == null) preferred = new List<LaneConnection>();
                    preferred.Add(c);
                }
            }
            if (preferred != null && preferred.Count > 0)
                return preferred[Random.Range(0, preferred.Count)];
            if (anyMatch != null && anyMatch.Count > 0)
                return anyMatch[Random.Range(0, anyMatch.Count)];
            return null;
        }

        VertexGeometry ResolveCached(Vertex v)
        {
            if (v == null) return null;
            if (_vgCache.TryGetValue(v.Id, out VertexGeometry cached)) return cached;
            VertexGeometry vg = GeometryResolver.ResolveVertex(Network, v);
            _vgCache[v.Id] = vg;
            return vg;
        }

        static VertexApproach FindApproach(VertexGeometry vg, string roadId)
        {
            if (vg == null) return null;
            foreach (VertexApproach a in vg.Approaches) if (a.RoadId == roadId) return a;
            return null;
        }

        static Vector2? LaneEndpointAt(VertexApproach a, Direction dir, int laneIndex)
        {
            if (a == null) return null;
            List<Vector2> lanes = dir == Direction.AB ? a.LaneEndsAB : a.LaneEndsBA;
            if (lanes == null || laneIndex < 0 || laneIndex >= lanes.Count) return null;
            return lanes[laneIndex];
        }

        static float FindSetback(VertexGeometry vg, string roadId, RoadEnd end)
        {
            if (vg == null) return 0f;
            foreach (VertexApproach a in vg.Approaches)
                if (a.RoadId == roadId && a.End == end) return a.Setback;
            return 0f;
        }

        static string OtherEndOf(NetworkRoad road, Direction dir, string fromVertexId)
        {
            // For travel direction AB the agent moves EndA → EndB; for
            // BA, EndB → EndA. The "from" vertex must match one of the
            // two; "to" is the other.
            if (dir == Direction.AB) return road.EndB;
            return road.EndA;
        }

        static int PickRandomLaneIndex(NetworkRoad road, Direction dir)
        {
            if (road == null || road.Profile == null) return 0;
            var lanes = dir == Direction.AB ? road.Profile.AB?.Lanes : road.Profile.BA?.Lanes;
            int count = lanes?.Count ?? 0;
            return count > 0 ? Random.Range(0, count) : 0;
        }

        // ----------------------------------------------------------
        // Per-frame ticking
        // ----------------------------------------------------------

        void Update()
        {
            if (Network == null) return;
            if (Paused) return; // freeze everything (positions, spawn drain, follow recompute)

            // Drain pending swarm spawns first — even when _agents is
            // empty (initial G press). Per-frame budget keeps the
            // ramp-up smooth.
            DrainSpawnQueue();
            if (_agents.Count == 0) return;
            float dt = Time.deltaTime;

            // Pre-pass: update each agent's Speed based on the gap to
            // the nearest agent ahead. Uses last frame's visual
            // positions (the new positions get written at the END of
            // each agent's TickAgent below). O(N²) but trivial at
            // typical agent counts.
            UpdateFollowingSpeeds(dt);

            for (int i = _agents.Count - 1; i >= 0; i--)
            {
                Agent a = _agents[i];
                if (a.Segments == null || a.Segments.Count == 0)
                {
                    DespawnAgent(a);
                    continue;
                }
                TickAgent(a, dt);
            }
        }

        // For each agent, scan all others for the closest one AHEAD
        // (within FollowLookAhead AND within ±FollowConeAngleDeg of
        // the agent's heading). Convert the gap into a "desired
        // speed". Also apply intersection right-of-way: an agent
        // approaching an occupied intersection brakes to a stop at
        // the entry. The actual speed update uses the MIN of the two
        // desired speeds (whichever asks for a bigger slowdown wins).
        void UpdateFollowingSpeeds(float dt)
        {
            float minCos = Mathf.Cos(Mathf.Clamp(FollowConeAngleDeg, 0f, 89.999f) * Mathf.Deg2Rad);
            // Use the configured FollowLookAhead as a floor, but extend
            // it per-agent based on current speed so an agent moving
            // faster than the static range can see leaders far enough
            // out to brake in time. Brake distance = v²/(2·decel).
            float decel = Mathf.Max(FollowDeceleration, 0.1f);

            // Overtaking pass — runs before speed update so a successful
            // overtake might immediately let the agent break out of
            // the cone and stop slowing this same frame.
            if (OvertakingEnabled)
            {
                for (int i = 0; i < _agents.Count; i++) TryOvertake(_agents[i]);
            }

            // First: per-vertex occupancy. List the actual intersection
            // beziers currently being traversed at each vertex.
            // Approaching agents will check their proposed bezier
            // against this list and only block on actual path
            // conflicts (sampled distance), not on mere presence.
            _vertexOccupancyBeziers.Clear();
            if (IntersectionRightOfWay)
            {
                for (int k = 0; k < _agents.Count; k++)
                {
                    Agent a = _agents[k];
                    if (a.Segments == null || a.SegmentIndex >= a.Segments.Count) continue;
                    if (a.Segments[a.SegmentIndex] is AgentIntersectionSegment isx
                        && !string.IsNullOrEmpty(isx.ToVertexId))
                    {
                        if (!_vertexOccupancyBeziers.TryGetValue(isx.ToVertexId, out var list))
                        {
                            list = new List<AgentIntersectionSegment>();
                            _vertexOccupancyBeziers[isx.ToVertexId] = list;
                        }
                        list.Add(isx);
                    }
                }
            }

            // Build a roadId → SpeedLimit dictionary once per frame so
            // the inner loop's per-agent TargetSpeed update is O(1).
            _roadSpeedLimitScratch.Clear();
            if (Network != null && Network.Roads != null)
            {
                for (int r = 0; r < Network.Roads.Count; r++)
                {
                    NetworkRoad nr = Network.Roads[r];
                    if (nr != null && nr.SpeedLimit.HasValue)
                        _roadSpeedLimitScratch[nr.Id] = nr.SpeedLimit.Value;
                }
            }

            for (int i = 0; i < _agents.Count; i++)
            {
                Agent a = _agents[i];
                if (a.Visual == null) continue;
                Vector3 myPos = a.Visual.transform.position;
                Vector3 myFwd = a.Visual.transform.forward;

                // Effective TargetSpeed = NaturalSpeed capped by the
                // posted SpeedLimit of the road the agent is currently
                // on. Intersection segments and unlimited roads → no cap.
                float effectiveTarget = a.NaturalSpeed;
                if (a.Segments != null && a.SegmentIndex < a.Segments.Count
                    && a.Segments[a.SegmentIndex] is AgentRoadSegment ars)
                {
                    if (_roadSpeedLimitScratch.TryGetValue(ars.RoadId, out float lim))
                        effectiveTarget = Mathf.Min(effectiveTarget, lim);
                }
                a.TargetSpeed = effectiveTarget;

                // Per-agent adaptive look-ahead. Take the larger of the
                // configured FollowLookAhead and the kinematic brake
                // distance at this agent's current speed — otherwise a
                // fast agent might not see a stopped leader until it's
                // too close to stop.
                float kinematicLook = FollowMinDistance + (a.Speed * a.Speed) / (2f * decel);
                float lookAhead = Mathf.Max(FollowLookAhead, kinematicLook);
                float lookSq = lookAhead * lookAhead;

                // (1) Following distance.
                float closestGap = float.MaxValue;
                float closestLeaderSpeed = 0f;
                for (int j = 0; j < _agents.Count; j++)
                {
                    if (i == j) continue;
                    Agent o = _agents[j];
                    if (o.Visual == null) continue;
                    Vector3 toOther = o.Visual.transform.position - myPos;
                    float distSq = toOther.sqrMagnitude;
                    if (distSq > lookSq || distSq < 1e-6f) continue;
                    float dist = Mathf.Sqrt(distSq);
                    float forwardDot = Vector3.Dot(toOther / dist, myFwd);
                    if (forwardDot < minCos) continue;
                    if (dist < closestGap)
                    {
                        closestGap = dist;
                        closestLeaderSpeed = o.Speed;
                    }
                }
                // Kinematic follow speed using RELATIVE velocity:
                //   v_max = sqrt(v_lead² + 2·decel·(gap − minDist))
                // Meaning: at the leader's current speed, I can be at
                // v_max and still match the leader's position+speed if
                // they brake at the same decel I do. If the leader is
                // already at my target speed, v_max naturally exceeds
                // TargetSpeed → cruise. If the leader is stopped,
                // collapses to the simple "can I stop in time?" form.
                float followDesired;
                if (closestGap == float.MaxValue)
                    followDesired = a.TargetSpeed;
                else if (closestGap <= FollowMinDistance)
                    followDesired = 0f;
                else
                {
                    float availableBrake = closestGap - FollowMinDistance;
                    float kinematicMaxSq = closestLeaderSpeed * closestLeaderSpeed
                        + 2f * decel * availableBrake;
                    float kinematicMax = Mathf.Sqrt(Mathf.Max(0f, kinematicMaxSq));
                    followDesired = Mathf.Min(a.TargetSpeed, kinematicMax);
                }

                // (2) Intersection behavior: combines right-of-way
                //     (FIFO) with traffic-sign rules (Stop/Yield).
                //     Only fires when the agent is on a road segment
                //     AND the next segment is an intersection.
                float intersectionDesired = a.TargetSpeed;
                if (a.Segments != null && a.SegmentIndex < a.Segments.Count - 1)
                {
                    var current = a.Segments[a.SegmentIndex];
                    var nextSeg = a.Segments[a.SegmentIndex + 1];
                    if (current is AgentRoadSegment currentRoad
                        && nextSeg is AgentIntersectionSegment
                        && !string.IsNullOrEmpty(nextSeg.ToVertexId))
                    {
                        string vertexId = nextSeg.ToVertexId;
                        float remaining = (1f - a.T) * current.ArcLength;

                        // Commit threshold — once the agent is very
                        // close to the entry, ignore everything and
                        // let them in. Prevents the "oscillating
                        // stop/go as cross-traffic flickers
                        // occupancy" wedge.
                        if (remaining > IntersectionCommitDistance)
                        {
                            // Conflict check: does my planned bezier
                            // actually cross any currently-traversing
                            // bezier at this vertex? Y-fork: paths
                            // diverge, no conflict, both proceed.
                            // 4-way crossing: paths cross at center,
                            // conflict, second waits.
                            bool occupied = false;
                            if (IntersectionRightOfWay
                                && nextSeg is AgentIntersectionSegment myIsx
                                && _vertexOccupancyBeziers.TryGetValue(vertexId, out var occupants))
                            {
                                for (int q = 0; q < occupants.Count; q++)
                                {
                                    if (BeziersConflict(myIsx, occupants[q], IntersectionConflictDistance))
                                    {
                                        occupied = true;
                                        break;
                                    }
                                }
                            }

                            StopYieldControl sign = ObeyTrafficSigns
                                ? LookupApproachSign(currentRoad.RoadId, vertexId)
                                : StopYieldControl.None;

                            // Stop-sign state machine:
                            //   stopSignArriving: agent hasn't stopped
                            //     at this vertex yet — must brake to a
                            //     full stop at the entry.
                            //   stopSignWaiting: agent has come to a
                            //     stop here (StoppedAtVertexId is set)
                            //     but the wait timer hasn't elapsed —
                            //     hold at 0.
                            //   neither: agent has cleared the stop
                            //     (waited long enough). Subject only
                            //     to occupancy + yield logic below.
                            bool stopSignArriving = false;
                            bool stopSignWaiting = false;
                            if (sign == StopYieldControl.Stop)
                            {
                                if (a.StoppedAtVertexId != vertexId)
                                {
                                    stopSignArriving = true;
                                }
                                else
                                {
                                    float waited = Time.realtimeSinceStartup - a.StoppedAtRealtime;
                                    if (waited < StopWaitSeconds) stopSignWaiting = true;
                                }
                            }

                            bool mustBrake = stopSignArriving || occupied;

                            // Yield: don't fully stop unless occupied
                            // — instead cap speed during approach.
                            float yieldCap = (sign == StopYieldControl.Yield && !occupied)
                                ? YieldApproachSpeed
                                : float.PositiveInfinity;

                            if (mustBrake)
                            {
                                float brakeDist = (a.Speed * a.Speed)
                                    / (2f * Mathf.Max(FollowDeceleration, 0.1f))
                                    + IntersectionBrakeMargin + IntersectionStopDistance;
                                if (remaining <= brakeDist)
                                {
                                    float entryRange = Mathf.Max(brakeDist - IntersectionStopDistance, 1e-4f);
                                    intersectionDesired = remaining <= IntersectionStopDistance
                                        ? 0f
                                        : a.TargetSpeed * (remaining - IntersectionStopDistance) / entryRange;
                                }
                            }

                            // Hold-at-zero while the stop-sign wait
                            // timer is running.
                            if (stopSignWaiting) intersectionDesired = 0f;

                            // Apply yield speed cap.
                            if (intersectionDesired > yieldCap)
                                intersectionDesired = yieldCap;

                            // Start the wait timer at the moment the
                            // agent first comes to rest at the entry
                            // while still in "arriving" state. Once
                            // set, the next frame transitions to
                            // "waiting" until the timer elapses.
                            if (stopSignArriving
                                && a.Speed < 0.1f
                                && remaining <= IntersectionStopDistance + 0.5f)
                            {
                                a.StoppedAtVertexId = vertexId;
                                a.StoppedAtRealtime = Time.realtimeSinceStartup;
                            }
                        }
                    }

                    // Clear stop-sign state when the agent advances
                    // past the vertex they stopped at. Detect: if
                    // we're now on a different segment than the
                    // approach road, the previous stop is finished.
                    if (a.StoppedAtVertexId != null)
                    {
                        // If our current segment isn't a road approaching
                        // StoppedAtVertexId, the agent has moved on.
                        bool stillApproaching = current is AgentRoadSegment
                            && nextSeg is AgentIntersectionSegment
                            && nextSeg.ToVertexId == a.StoppedAtVertexId;
                        if (!stillApproaching)
                        {
                            a.StoppedAtVertexId = null;
                            a.StoppedAtRealtime = 0f;
                        }
                    }
                }

                float desired = Mathf.Min(followDesired, intersectionDesired);
                float rate = desired > a.Speed ? FollowAcceleration : FollowDeceleration;
                a.Speed = Mathf.MoveTowards(a.Speed, desired, rate * dt);
            }
        }

        // Scratch dictionary reused across frames. Built each pre-pass
        // when IntersectionRightOfWay is enabled. Lists the actual
        // intersection beziers currently traversed at each vertex.
        readonly Dictionary<string, List<AgentIntersectionSegment>> _vertexOccupancyBeziers
            = new Dictionary<string, List<AgentIntersectionSegment>>();

        // Per-frame scratch: roadId → posted SpeedLimit (m/s). Only
        // contains entries for roads with a non-null SpeedLimit.
        // Rebuilt at the start of each UpdateFollowingSpeeds.
        readonly Dictionary<string, float> _roadSpeedLimitScratch = new Dictionary<string, float>();

        // Box-Muller Gaussian. Returns mean when stdDev <= 0.
        static float SampleGaussian(float mean, float stdDev)
        {
            if (stdDev <= 0f) return mean;
            float u1 = Mathf.Max(1e-6f, UnityEngine.Random.value);
            float u2 = UnityEngine.Random.value;
            float z = Mathf.Sqrt(-2f * Mathf.Log(u1))
                * Mathf.Cos(2f * Mathf.PI * u2);
            return mean + stdDev * z;
        }

        // Sample-based bezier-vs-bezier conflict check. Two paths
        // "conflict" only if they actually CROSS (different headings
        // AND close in space). Parallel paths (similar headings, e.g.
        // two lanes going through the same intersection side by side)
        // are NOT a conflict — Tier 1 following handles lane-relative
        // spacing for those. Cross-traffic at intersections has very
        // different midpoint tangents → flagged as conflict when
        // also spatially close.
        //
        // Samples taken in t ∈ [0.15, 0.85] (interior only) so beziers
        // sharing an endpoint at the vertex don't trigger a false
        // positive on their shared start/end position.
        static bool BeziersConflict(
            AgentIntersectionSegment a, AgentIntersectionSegment b, float threshold)
        {
            if (a == null || b == null) return false;
            // Reject parallel-direction pairs first using midpoint
            // tangents. If both beziers are heading roughly the same
            // direction at their midpoints, they're parallel paths
            // through the intersection — no actual crossing.
            Vector2 tanA = GeometryResolver.CubicTangent(a.P0, a.C1, a.C2, a.P3, 0.5f);
            Vector2 tanB = GeometryResolver.CubicTangent(b.P0, b.C1, b.C2, b.P3, 0.5f);
            if (tanA.sqrMagnitude > 1e-6f && tanB.sqrMagnitude > 1e-6f)
            {
                float dot = Vector2.Dot(tanA.normalized, tanB.normalized);
                // >0.7 → tangents within ~45° → parallel-ish.
                if (dot > 0.7f) return false;
            }

            const int SAMPLES = 6;
            float threshSq = threshold * threshold;
            for (int i = 0; i < SAMPLES; i++)
            {
                float ta = Mathf.Lerp(0.15f, 0.85f, i / (float)(SAMPLES - 1));
                Vector2 pa = GeometryResolver.SampleCubic(a.P0, a.C1, a.C2, a.P3, ta);
                for (int j = 0; j < SAMPLES; j++)
                {
                    float tb = Mathf.Lerp(0.15f, 0.85f, j / (float)(SAMPLES - 1));
                    Vector2 pb = GeometryResolver.SampleCubic(b.P0, b.C1, b.C2, b.P3, tb);
                    if ((pa - pb).sqrMagnitude < threshSq) return true;
                }
            }
            return false;
        }

        // Consider whether `a` should lane-change to pass a slower
        // leader. Conditions:
        //   - Currently on a road segment (not intersection).
        //   - Not already mid-lane-change (Start != End).
        //   - Speed has been throttled below TargetSpeed * OvertakeSpeedRatio.
        //   - At least OvertakeMinRemainingMeters of road left.
        //   - At least one adjacent lane (currentLane ± 1) exists.
        //   - That adjacent lane has a valid connection at the next
        //     vertex matching the path's next outbound (so the turn
        //     isn't broken).
        //   - That adjacent lane has no other agent within
        //     OvertakeClearAhead meters of the agent's projected lane
        //     position.
        // On success: rewrites the current road segment's lane-change
        // target + LaneChangeStartT (starting from current interpolated
        // offset for visual continuity), AND rebuilds the upcoming
        // intersection segment + following road segment so the new
        // lane choice flows cleanly through.
        void TryOvertake(Agent a)
        {
            if (a.Segments == null || a.SegmentIndex >= a.Segments.Count) return;
            var current = a.Segments[a.SegmentIndex] as AgentRoadSegment;
            if (current == null) return;
            // Already mid-change → don't double up.
            if (current.SourceStartLaneIndex != current.SourceEndLaneIndex) return;
            // Are we actually being slowed?
            if (a.Speed >= a.TargetSpeed * OvertakeSpeedRatio) return;
            // Enough road left to maneuver?
            float remaining = (1f - a.T) * current.ArcLength;
            if (remaining < OvertakeMinRemainingMeters) return;

            NetworkRoad road = FindRoadById(current.RoadId);
            if (road == null || road.Profile == null) return;
            int currentLane = current.SourceEndLaneIndex;
            // Need the (Direction) — recover from the lane offset's
            // sign relative to the AB convention. Cleaner: we stored
            // it implicitly via the offsets but didn't expose
            // direction directly. Find direction by checking which
            // direction has the current end-lane offset.
            Direction dir = InferRoadSegmentDirection(road, current);
            var lanes = dir == Direction.AB ? road.Profile.AB?.Lanes : road.Profile.BA?.Lanes;
            if (lanes == null || lanes.Count <= 1) return;

            // The upcoming intersection segment + the road after it.
            AgentIntersectionSegment upcomingIsx = a.SegmentIndex + 1 < a.Segments.Count
                ? a.Segments[a.SegmentIndex + 1] as AgentIntersectionSegment : null;
            AgentRoadSegment nextRoad = a.SegmentIndex + 2 < a.Segments.Count
                ? a.Segments[a.SegmentIndex + 2] as AgentRoadSegment : null;
            NetworkRoad nextRoadObj = nextRoad != null ? FindRoadById(nextRoad.RoadId) : null;
            Direction nextDir = nextRoadObj != null
                ? InferRoadSegmentDirection(nextRoadObj, nextRoad)
                : Direction.AB;

            int[] candidates = { currentLane - 1, currentLane + 1 };
            foreach (int candIn in candidates)
            {
                if (candIn < 0 || candIn >= lanes.Count) continue;

                // Validate the candidate still permits the next turn.
                LaneConnection conn = null;
                if (upcomingIsx != null && nextRoadObj != null)
                {
                    conn = PickConnectionAtVertex(
                        upcomingIsx.ToVertexId,
                        road, dir, candIn,
                        nextRoadObj, nextDir);
                    if (conn == null) continue;
                }

                // Adjacent lane occupied check.
                if (!IsAdjacentLaneClearAhead(a, road, dir, candIn)) continue;

                // Commit the lane change.
                ExecuteLaneChange(a, candIn, conn, road, dir, upcomingIsx, nextRoad);
                return;
            }
        }

        bool IsAdjacentLaneClearAhead(Agent a, NetworkRoad road, Direction dir, int candidateLane)
        {
            for (int i = 0; i < _agents.Count; i++)
            {
                Agent o = _agents[i];
                if (o == a) continue;
                if (o.Segments == null || o.SegmentIndex >= o.Segments.Count) continue;
                var otherRoad = o.Segments[o.SegmentIndex] as AgentRoadSegment;
                if (otherRoad == null) continue;
                if (otherRoad.RoadId != road.Id) continue;
                if (otherRoad.SourceEndLaneIndex != candidateLane
                    && otherRoad.SourceStartLaneIndex != candidateLane) continue;
                Direction otherDir = InferRoadSegmentDirection(road, otherRoad);
                if (otherDir != dir) continue;
                // Same road + same direction + (will be) in the
                // candidate lane. Are they within OvertakeClearAhead?
                if (a.Visual == null || o.Visual == null) continue;
                Vector3 toOther = o.Visual.transform.position - a.Visual.transform.position;
                float forward = Vector3.Dot(toOther, a.Visual.transform.forward);
                if (forward < 0f) continue; // behind us — they don't block our forward pass
                if (toOther.magnitude < OvertakeClearAhead) return false;
            }
            return true;
        }

        void ExecuteLaneChange(Agent a, int newEndLane, LaneConnection conn,
            NetworkRoad road, Direction dir,
            AgentIntersectionSegment upcomingIsx, AgentRoadSegment nextRoad)
        {
            var current = (AgentRoadSegment)a.Segments[a.SegmentIndex];
            // Snapshot current visual offset so the lane-change ramp
            // begins from where we ARE, not from a discontinuity.
            float currentOffset = current.OffsetAt(a.T);

            float newEndOffsetAB = GeometryResolver.LaneCenterOffsetSigned(
                road, dir, newEndLane, Network.DriveSide);
            float newEndOffsetTravel = dir == Direction.AB ? newEndOffsetAB : -newEndOffsetAB;

            current.LaneOffsetTravelStart = currentOffset;
            current.LaneOffsetTravelEnd = newEndOffsetTravel;
            current.SourceEndLaneIndex = newEndLane;
            // Start the change NOW from current T (so the ramp uses
            // the remaining road for the merge).
            current.LaneChangeStartT = a.T;

            // Cascade: the intersection segment after this road was
            // built with the OLD endLane. Replace it with one keyed
            // on the new lane → new outbound lane (from conn). Then
            // the next-road segment's start lane also changes.
            if (upcomingIsx != null && conn != null && nextRoad != null)
            {
                NetworkRoad nextRoadObj = FindRoadById(nextRoad.RoadId);
                Direction nextDir = InferRoadSegmentDirection(nextRoadObj, nextRoad);
                var newIsx = BuildIntersectionSegment(
                    upcomingIsx.ToVertexId,
                    road, dir, newEndLane,
                    nextRoadObj, nextDir, conn.To.Index);
                if (newIsx != null)
                {
                    newIsx.ToVertexId = upcomingIsx.ToVertexId;
                    a.Segments[a.SegmentIndex + 1] = newIsx;
                }
                var newNextRoad = BuildRoadSegment(
                    nextRoadObj, nextDir, conn.To.Index, nextRoad.SourceEndLaneIndex);
                if (newNextRoad != null)
                {
                    newNextRoad.ToVertexId = nextRoad.ToVertexId;
                    a.Segments[a.SegmentIndex + 2] = newNextRoad;
                }
            }
        }

        // Infer travel direction from a road segment by checking which
        // direction's lane offset matches the segment's stored end
        // offset. Cheap; only called inside the overtake path.
        Direction InferRoadSegmentDirection(NetworkRoad road, AgentRoadSegment seg)
        {
            float abOffset = GeometryResolver.LaneCenterOffsetSigned(
                road, Direction.AB, seg.SourceEndLaneIndex, Network.DriveSide);
            float diffAB = Mathf.Abs(seg.LaneOffsetTravelEnd - abOffset);
            float diffBA = Mathf.Abs(seg.LaneOffsetTravelEnd - (-abOffset));
            return diffAB <= diffBA ? Direction.AB : Direction.BA;
        }

        // Look up the stop/yield/none control sign an agent on `roadId`
        // sees as they approach `vertexId`. The road's ControlA applies
        // at EndA (sign drivers see going B→A); ControlB at EndB. So
        // an agent approaching `vertexId` reads the control at the
        // END that matches that vertex.
        StopYieldControl LookupApproachSign(string roadId, string vertexId)
        {
            if (Network == null || Network.Roads == null) return StopYieldControl.None;
            foreach (NetworkRoad r in Network.Roads)
            {
                if (r.Id != roadId) continue;
                if (r.EndA == vertexId) return r.ControlA;
                if (r.EndB == vertexId) return r.ControlB;
                return StopYieldControl.None;
            }
            return StopYieldControl.None;
        }

        void TickAgent(Agent a, float dt)
        {
            int safety = 32; // tolerate many short segments per frame
            while (safety-- > 0 && dt > 0f)
            {
                AgentSegment seg = a.Segments[a.SegmentIndex];
                float len = Mathf.Max(seg.ArcLength, 1e-4f);
                float remainingMeters = (1f - a.T) * len;
                float travel = a.Speed * dt;
                if (travel < remainingMeters)
                {
                    a.T += travel / len;
                    dt = 0f;
                }
                else
                {
                    dt -= remainingMeters / Mathf.Max(a.Speed, 1e-4f);
                    // Hit a segment boundary. Capture the vertex we're
                    // AT (the just-completed segment's ToVertexId), then
                    // either advance or rebuild.
                    string atVertex = seg.ToVertexId;
                    a.SegmentIndex++;
                    a.T = 0f;
                    // Re-plan triggers:
                    //   - NeedsRebuild: explicit invalidation (vertex
                    //     moved, road deleted, etc.).
                    //   - End of looping path: always re-plan on loop
                    //     so agents naturally pick up network changes
                    //     (lane connectivity edits, etc.) that don't
                    //     fire invalidation. Costs one pathfind +
                    //     segment build per loop per agent — cheap.
                    bool endOfLoop = a.SegmentIndex >= a.Segments.Count && a.Loop;
                    if (a.NeedsRebuild || endOfLoop)
                    {
                        if (!RebuildAgent(a, atVertex)) return;
                        continue;
                    }
                    if (a.SegmentIndex >= a.Segments.Count)
                    {
                        DespawnAgent(a);
                        return;
                    }
                }
            }
            if (a.Visual != null) PositionVisual(a);
        }

        void PositionVisual(Agent a)
        {
            AgentSegment seg = a.Segments[a.SegmentIndex];
            seg.Sample(a.T, out Vector2 pos, out Vector2 tangent);
            a.Visual.transform.position = new Vector3(pos.x, AgentYLift, pos.y);
            if (tangent.sqrMagnitude > 1e-6f)
            {
                Vector3 fwd = new Vector3(tangent.x, 0f, tangent.y).normalized;
                a.Visual.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            }
        }

        // ----------------------------------------------------------
        // Pool management
        // ----------------------------------------------------------

        public void DespawnAgent(Agent a)
        {
            if (a == null) return;
            if (a.Visual != null)
            {
                if (Application.isPlaying) Destroy(a.Visual);
                else DestroyImmediate(a.Visual);
            }
            _agents.Remove(a);
        }

        /// <summary>
        /// Flag every agent as needing a path rebuild on its next
        /// segment boundary. Use only for topology-wide changes (full
        /// network reload, drive-side flip). For targeted mutations
        /// prefer the scoped variants below — they avoid re-routing
        /// agents that don't traverse the mutated entity.
        /// </summary>
        public void InvalidateAllAgents()
        {
            for (int i = 0; i < _agents.Count; i++) _agents[i].NeedsRebuild = true;
        }

        /// <summary>
        /// Flag agents whose REMAINING path uses the given road.
        /// Use after: road deleted, road split, road reversed, road
        /// profile changed, lateral offset / setback changed.
        /// </summary>
        public void InvalidateAgentsForRoad(string roadId)
        {
            if (string.IsNullOrEmpty(roadId)) return;
            for (int i = 0; i < _agents.Count; i++)
            {
                Agent a = _agents[i];
                if (a.NeedsRebuild || a.Segments == null) continue;
                for (int s = a.SegmentIndex; s < a.Segments.Count; s++)
                {
                    if (a.Segments[s] is AgentRoadSegment rs && rs.RoadId == roadId)
                    {
                        a.NeedsRebuild = true;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Flag agents whose REMAINING path traverses the given vertex.
        /// Use after: vertex moved, vertex deleted, connectivity
        /// overrides changed at this vertex, lane markings changed (if
        /// they ever drive behavior — currently visual only).
        /// </summary>
        public void InvalidateAgentsForVertex(string vertexId)
        {
            if (string.IsNullOrEmpty(vertexId)) return;
            for (int i = 0; i < _agents.Count; i++)
            {
                Agent a = _agents[i];
                if (a.NeedsRebuild || a.Segments == null) continue;
                for (int s = a.SegmentIndex; s < a.Segments.Count; s++)
                {
                    if (a.Segments[s].ToVertexId == vertexId)
                    {
                        a.NeedsRebuild = true;
                        break;
                    }
                }
            }
        }

        // Re-route an agent from its current vertex to its end vertex
        // using current network state. Replaces its Segments list and
        // resets SegmentIndex/T. If no route exists from current vertex,
        // despawns the agent (network change orphaned it). Called at
        // segment boundaries when NeedsRebuild is set.
        // Returns true if the agent was successfully rebuilt, false if
        // it was despawned.
        bool RebuildAgent(Agent a, string currentVertexId)
        {
            a.NeedsRebuild = false;
            if (currentVertexId == null)
            {
                DespawnAgent(a);
                return false;
            }
            // If currentVertex == endVertex, the agent's at its
            // destination — just loop (re-plan start → end), or
            // despawn if not looping.
            string targetVertex = (currentVertexId == a.EndVertexId && a.Loop)
                ? a.StartVertexId
                : a.EndVertexId;
            if (currentVertexId == targetVertex)
            {
                // Trivial 0-step path — nothing to do, hand off to
                // the loop/despawn logic by leaving Segments empty.
                if (a.Loop)
                {
                    // Try to plan start → end. If currentVertex is
                    // both start and end (degenerate), give up.
                    targetVertex = currentVertexId == a.StartVertexId ? a.EndVertexId : a.StartVertexId;
                    if (currentVertexId == targetVertex) { DespawnAgent(a); return false; }
                }
                else { DespawnAgent(a); return false; }
            }

            var newPath = AgentPathfinder.FindPath(Network, currentVertexId, targetVertex);
            if (newPath == null || newPath.Count == 0)
            {
                Debug.LogWarning($"[AgentSystem] Agent '{a.Id}' rebuild: no route from " +
                                 $"'{currentVertexId}' to '{targetVertex}' after network change. Despawning.");
                DespawnAgent(a);
                return false;
            }
            List<AgentSegment> newSegs = BuildSegments(newPath, currentVertexId);
            if (newSegs == null || newSegs.Count == 0)
            {
                DespawnAgent(a);
                return false;
            }
            a.Segments = newSegs;
            a.SegmentIndex = 0;
            a.T = 0f;
            return true;
        }

        public void ApplyDefaultSpeedToAllAgents()
        {
            // Re-sample NaturalSpeed for each existing agent so the
            // tuning slider's effect is visible immediately. With
            // SpeedVariationStdDev == 0 every agent gets exactly the new
            // DefaultSpeed; otherwise each agent gets a fresh draw.
            // TargetSpeed itself is recomputed per frame from NaturalSpeed
            // capped by the current road's SpeedLimit, so no need to set
            // it directly here.
            for (int i = 0; i < _agents.Count; i++)
            {
                float n = SampleGaussian(DefaultSpeed, SpeedVariationStdDev);
                _agents[i].NaturalSpeed = Mathf.Max(0.5f, n);
            }
        }

        public void RefreshAllAgentVisuals()
        {
            for (int i = 0; i < _agents.Count; i++)
            {
                GameObject v = _agents[i].Visual;
                if (v == null) continue;
                v.transform.localScale = new Vector3(AgentDiameter, AgentHeight * 0.5f, AgentDiameter);
                MeshRenderer mr = v.GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != null) mr.sharedMaterial.color = AgentColor;
            }
        }

        public void DespawnAll()
        {
            // Also cancel pending swarm spawns — H should fully reset
            // the agent population, not leave a queue dripping new
            // ones after the user clears.
            CancelPendingSpawns();
            for (int i = _agents.Count - 1; i >= 0; i--)
            {
                if (_agents[i].Visual != null)
                {
                    if (Application.isPlaying) Destroy(_agents[i].Visual);
                    else DestroyImmediate(_agents[i].Visual);
                }
            }
            _agents.Clear();
        }

        GameObject CreateVisual(Agent a)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = $"Agent_{a.Id}";
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localScale = new Vector3(AgentDiameter, AgentHeight * 0.5f, AgentDiameter);
            // KEEP the capsule's default collider so right-click can
            // pick the agent. Marked as trigger so it doesn't
            // contribute to physics (agents don't collide with
            // anything — Tier 1 following is speed-based, not
            // physics-based).
            Collider col = go.GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
            AgentClickTarget tag = go.AddComponent<AgentClickTarget>();
            tag.Agent = a;
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                Material mat = new Material(Shader.Find("Standard")) { color = AgentColor, name = "AgentMat" };
                mr.sharedMaterial = mat;
            }
            return go;
        }

        Vertex FindVertex(string id)
        {
            foreach (Vertex v in Network.Vertices) if (v.Id == id) return v;
            return null;
        }

        NetworkRoad FindRoadById(string id)
        {
            if (Network == null || Network.Roads == null || string.IsNullOrEmpty(id)) return null;
            foreach (NetworkRoad r in Network.Roads) if (r.Id == id) return r;
            return null;
        }
    }
}
