// Ports the setback + bezier fillet math from
// road-designer/src/model/network.ts into C#. Produces VertexGeometry
// for each vertex in a Network.
//
// Conventions (mirrors network.ts):
//   - Vector2 = world XZ position (Vector2.x → Unity X, Vector2.y → Unity Z).
//   - Vertex bearings are FROM the vertex, in radians, math convention
//     (CCW positive, 0 = +x).
//   - Approaches are sorted CW around the vertex (decreasing bearing).
//   - "OuterRight" = the corner on the CW side of the approach (the side
//     facing the next CW neighbor). "OuterLeft" = CCW side.
//   - Setback formula: setback_R(V) = max(W_R, max over neighbors of
//     d_R_at_corner), where d = (hW_N + hW_R · cos θ) / sin θ.
//   - Bezier control point = OE intersection (same math as d, expressed
//     as a 2D point rather than a distance).

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Geometry
{
    public static class GeometryResolver
    {
        const float Eps = 1e-4f;
        // Angles within this distance of π are treated as collinear
        // "joints" — the fillet collapses to a straight segment.
        const float JointAngleEpsilon = 0.5f * Mathf.Deg2Rad;

        /// <summary>
        /// Setback multiplier applied to curved-road approaches only.
        /// With a small setback the local tangent at the setback point is
        /// still close to the chord direction, so the OE bezier control
        /// (intersection of tangent rays) lands near-collinear with the
        /// outline endpoints, producing an almost-straight transition.
        /// Multiplying the setback moves the setback point further along
        /// the curve where the tangent has rotated further, restoring a
        /// visibly curved transition. 1.0 = current behavior. Straight
        /// roads are unaffected (no Curve, no multiplier).
        /// Skipped on explicit per-road setback overrides (those are
        /// authoritative by design).
        /// </summary>
        public static float CurvedSetbackMultiplier = 1.5f;

        /// <summary>
        /// Compute the resolved geometry at a single vertex. Returns an
        /// empty VertexGeometry if no roads reach the vertex.
        /// </summary>
        public static VertexGeometry ResolveVertex(Network network, Vertex vertex)
        {
            List<ApproachData> data = CollectApproaches(network, vertex);

            VertexGeometry result = new VertexGeometry
            {
                VertexId = vertex.Id,
                Approaches = new List<VertexApproach>(),
                Outline = new List<OutlineSegment>(),
                Connectivity = new List<LaneConnection>(), // defer
            };

            if (data.Count == 0) return result;

            // Sort CW (decreasing angle in math convention).
            data.Sort((a, b) => b.Bearing.CompareTo(a.Bearing));

            ComputeSetbacks(data);

            // Build the public VertexApproach list (with lane endpoints
            // and outer-edge corners materialized in world space).
            foreach (ApproachData d in data)
            {
                // Pass the lateral-offset-shifted endpoint as the
                // "vertex position" for this approach. With offset=0
                // this is just vertex.Position (no behavior change);
                // with non-zero offset the approach's centerline,
                // setback midpoint, lane endpoints, and outer corners
                // all shift perpendicular to the road's direction.
                result.Approaches.Add(BuildApproach(d, d.SelfShiftedPos, network.DriveSide));
            }

            // Build outline. Order CW: for each approach i, emit its
            // setback line, then a transition (bezier or straight joint)
            // to the next CW approach.
            int n = data.Count;
            for (int i = 0; i < n; i++)
            {
                VertexApproach self = result.Approaches[i];
                VertexApproach next = result.Approaches[(i + 1) % n];
                ApproachData dataSelf = data[i];
                ApproachData dataNext = data[(i + 1) % n];

                // Setback line traversed CW: OuterLeft → OuterRight.
                result.Outline.Add(new OutlineSegment
                {
                    Kind = SegmentKind.Line,
                    From = self.OuterLeft,
                    To = self.OuterRight,
                });

                // Skip the corner segment entirely if there's only one
                // approach (dead-end — Q6 special case, will be its own
                // future thing; for now the "outline" of a 1-approach
                // vertex is just the single setback line).
                if (n == 1) break;

                float theta = CWAngleDelta(self.Bearing, next.Bearing);

                // "Joint" (collinear pass-through) is a straight-road
                // concept. Two curved approaches whose at-vertex tangents
                // happen to be 180° apart (e.g. two ring arcs meeting at
                // a roundabout ring vertex) are NOT joints — their outer
                // edges at the setback points diverge as each arc bends
                // away, so the OE bezier branch produces the correct
                // curved transition.
                bool bothStraight = dataSelf.Road.Curve == null
                                 && dataNext.Road.Curve == null;

                if (bothStraight && Mathf.Abs(theta - Mathf.PI) < JointAngleEpsilon)
                {
                    // Joint: outer edges are collinear, no fillet.
                    // Connect with a straight segment along the outer edge.
                    result.Outline.Add(new OutlineSegment
                    {
                        Kind = SegmentKind.Line,
                        From = self.OuterRight,
                        To = next.OuterLeft,
                    });
                }
                else
                {
                    // Bezier fillet. Control point = OE intersection of
                    // self's CW-side outer edge with next's CCW-side
                    // outer edge. The edges are parameterized starting at
                    // the corner and going AWAY from V along the
                    // approach's outer-edge direction — which equals the
                    // bearing for straight roads but is the curve's local
                    // tangent at the setback point for curved roads.
                    Vector2? control = LineIntersect(
                        self.OuterRight, self.OuterEdgeDir,
                        next.OuterLeft, next.OuterEdgeDir);

                    if (control.HasValue)
                    {
                        result.Outline.Add(new OutlineSegment
                        {
                            Kind = SegmentKind.QuadraticBezier,
                            From = self.OuterRight,
                            Control = control.Value,
                            To = next.OuterLeft,
                        });
                    }
                    else
                    {
                        // Lines parallel but not collinear — shouldn't
                        // normally happen if JointAngleEpsilon was tight
                        // enough, but fall back to a straight line so the
                        // outline closes.
                        result.Outline.Add(new OutlineSegment
                        {
                            Kind = SegmentKind.Line,
                            From = self.OuterRight,
                            To = next.OuterLeft,
                        });
                    }
                }
            }

            ComputeDefaultConnectivity(result.Approaches, network.DriveSide, result.Connectivity);
            ApplyConnectivityOverrides(vertex.ConnectivityOverrides, result.Connectivity);

            return result;
        }

        /// <summary>
        /// Layer per-from-lane overrides on top of the resolver-computed
        /// defaults using REPLACE semantics: if any override in
        /// <paramref name="overrides"/> shares a from-lane (same RoadId,
        /// Direction, Index) with one or more defaults, ALL defaults
        /// from that from-lane are dropped and only the overrides for
        /// that from-lane remain. From-lanes with no overrides keep
        /// every default they had. This matches the editorial intent
        /// "if I've touched a lane, my edits are authoritative."
        /// </summary>
        static void ApplyConnectivityOverrides(
            List<LaneConnection> overrides, List<LaneConnection> effective)
        {
            if (overrides == null || overrides.Count == 0) return;

            // Collect the unique from-lanes mentioned in overrides.
            // Tuple-keyed HashSet would be cleaner but C# 7+ value tuples
            // need correct GetHashCode; a list scan is fine for typical
            // override counts (single digits per vertex).
            for (int oi = 0; oi < overrides.Count; oi++)
            {
                LaneRef of = overrides[oi].From;
                if (of == null) continue;
                // Only the FIRST occurrence of each from-lane needs to
                // trigger the default-drop; subsequent overrides for the
                // same lane just contribute their own To.
                bool alreadyHandled = false;
                for (int prev = 0; prev < oi; prev++)
                {
                    if (LaneRefEquals(overrides[prev].From, of)) { alreadyHandled = true; break; }
                }
                if (alreadyHandled) continue;
                effective.RemoveAll(c => LaneRefEquals(c.From, of));
            }

            // Append authoritative override entries. SENTINEL entries
            // (To == null or empty RoadId) exist solely to trigger the
            // per-from-lane REPLACE behavior above and are NOT emitted
            // as real connections — used by NetworkDesigner to fully
            // suppress all connections from a given from-lane.
            foreach (LaneConnection o in overrides)
            {
                if (o == null) continue;
                if (o.To == null || string.IsNullOrEmpty(o.To.RoadId)) continue;
                effective.Add(o);
            }
        }

        static bool LaneRefEquals(LaneRef a, LaneRef b)
        {
            if (a == null || b == null) return false;
            return a.RoadId == b.RoadId
                && a.Direction == b.Direction
                && a.Index == b.Index;
        }

        /// <summary>
        /// Angular tolerance for the "straight-through" lane connection
        /// rule. An incident road counts as opposite-of-self when its
        /// bearing is within this many radians of self.Bearing + π.
        /// Tighter = fewer false-positive straights at Y-intersections;
        /// looser = more aggressive matching.
        /// </summary>
        public static float StraightConnectionToleranceRad = 15f * Mathf.Deg2Rad;

        /// <summary>
        /// Populate Connectivity with default lane connections derived
        /// from intersection geometry. Two rules at this revision:
        ///
        ///   (1) Straight-through: for each approach, find the opposite
        ///       approach (bearing within StraightConnectionToleranceRad
        ///       of antiparallel). For each inbound lane index k that
        ///       has a matching outbound lane index k on that opposite
        ///       road, emit inbound→outbound. Lane count mismatch:
        ///       connect only min(inboundCount, outboundCount) lanes.
        ///
        ///   (2) Curbside-to-curbside turn — the curbside (outermost)
        ///       inbound lane of each approach connects to the curbside
        ///       outbound lane of the right-turn target (RHD) or
        ///       left-turn target (LHD). The "easy" turn (no opposing
        ///       traffic to yield to). Only applied at intersections
        ///       with 3+ approaches (at 2-way joints the straight rule
        ///       already covers it).
        ///
        /// "Inbound" = the lane direction whose traffic flows TOWARD
        /// this vertex. At EndA's vertex that's BA; at EndB's that's AB.
        /// "Outbound" is the opposite direction at the OTHER approach's end.
        /// </summary>
        static void ComputeDefaultConnectivity(
            List<VertexApproach> apps, DriveSide driveSide, List<LaneConnection> outConnections)
        {
            int n = apps.Count;
            if (n < 2) return;

            for (int i = 0; i < n; i++)
            {
                VertexApproach self = apps[i];
                Direction inboundDir = self.End == RoadEnd.A ? Direction.BA : Direction.AB;
                List<Vector2> inboundLanes = inboundDir == Direction.AB ? self.LaneEndsAB : self.LaneEndsBA;
                if (inboundLanes == null || inboundLanes.Count == 0) continue;

                // ---- (1) Straight-through ----
                int oppIdx = FindOppositeApproach(apps, i, StraightConnectionToleranceRad);
                if (oppIdx >= 0)
                {
                    AddIndexMatchedConnections(self, apps[oppIdx], inboundDir, inboundLanes, outConnections);
                }

                // ---- (2) Curbside turn ----
                // RHD: easy turn is RIGHT, which is the previous approach
                // in the CW-sorted list (= one step CCW = (i-1+n)%n).
                // LHD: easy turn is LEFT, which is the next-CW approach
                // = (i+1)%n. The geometric reasoning is symmetric.
                // Skipped for n<3 since at a 2-way joint the straight
                // rule already exhausts the available connections.
                if (n >= 3)
                {
                    int turnIdx = driveSide == DriveSide.Right
                        ? (i - 1 + n) % n
                        : (i + 1) % n;
                    // Avoid duplicating the straight connection if it
                    // happens to coincide (degenerate 3-way where the
                    // straight target IS the curbside neighbor).
                    if (turnIdx != oppIdx)
                    {
                        AddCurbsideTurnConnection(self, apps[turnIdx], inboundDir, inboundLanes, outConnections);
                    }
                }
            }
        }

        // Index-matched lane-to-lane on the opposite road. Inbound lane k
        // connects to outbound lane k (innermost-to-innermost, etc.)
        // capped by the smaller of the two lane counts.
        static void AddIndexMatchedConnections(
            VertexApproach self, VertexApproach opp, Direction inboundDir,
            List<Vector2> inboundLanes, List<LaneConnection> outConnections)
        {
            Direction outboundDir = opp.End == RoadEnd.A ? Direction.AB : Direction.BA;
            List<Vector2> outboundLanes = outboundDir == Direction.AB ? opp.LaneEndsAB : opp.LaneEndsBA;
            if (outboundLanes == null || outboundLanes.Count == 0) return;

            int connectCount = Mathf.Min(inboundLanes.Count, outboundLanes.Count);
            for (int k = 0; k < connectCount; k++)
            {
                outConnections.Add(new LaneConnection
                {
                    From = new LaneRef { RoadId = self.RoadId, Direction = inboundDir, Index = k },
                    To = new LaneRef { RoadId = opp.RoadId, Direction = outboundDir, Index = k },
                });
            }
        }

        // Curbside (outermost) inbound lane → curbside outbound lane
        // of the turn-target road. A single connection per approach.
        // Skipped if either side has zero lanes in the relevant direction.
        static void AddCurbsideTurnConnection(
            VertexApproach self, VertexApproach turnTarget, Direction inboundDir,
            List<Vector2> inboundLanes, List<LaneConnection> outConnections)
        {
            Direction outboundDir = turnTarget.End == RoadEnd.A ? Direction.AB : Direction.BA;
            List<Vector2> outboundLanes = outboundDir == Direction.AB ? turnTarget.LaneEndsAB : turnTarget.LaneEndsBA;
            if (outboundLanes == null || outboundLanes.Count == 0) return;

            int curbInbound = inboundLanes.Count - 1;
            int curbOutbound = outboundLanes.Count - 1;
            outConnections.Add(new LaneConnection
            {
                From = new LaneRef { RoadId = self.RoadId, Direction = inboundDir, Index = curbInbound },
                To = new LaneRef { RoadId = turnTarget.RoadId, Direction = outboundDir, Index = curbOutbound },
            });
        }

        /// <summary>
        /// Return the index in <paramref name="apps"/> of the approach
        /// whose bearing is closest to antiparallel to apps[selfIdx],
        /// within <paramref name="toleranceRad"/> of exact 180°. Returns
        /// -1 when no candidate qualifies.
        /// </summary>
        static int FindOppositeApproach(List<VertexApproach> apps, int selfIdx, float toleranceRad)
        {
            float target = apps[selfIdx].Bearing + Mathf.PI;
            int best = -1;
            float bestDelta = float.MaxValue;
            for (int j = 0; j < apps.Count; j++)
            {
                if (j == selfIdx) continue;
                float delta = Mathf.Abs(NormalizeAngle(apps[j].Bearing - target));
                if (delta < bestDelta && delta <= toleranceRad)
                {
                    bestDelta = delta;
                    best = j;
                }
            }
            return best;
        }

        static float NormalizeAngle(float a)
        {
            while (a > Mathf.PI) a -= 2f * Mathf.PI;
            while (a <= -Mathf.PI) a += 2f * Mathf.PI;
            return a;
        }

        /// <summary>
        /// Resolve all vertices in a network. Also runs post-resolution
        /// validation that warns (via Debug.LogWarning) when a road's
        /// two end setbacks add up to more than the road's centerline
        /// length — i.e. the setback lines have crossed and the road
        /// body has zero or negative length.
        /// </summary>
        public static List<VertexGeometry> ResolveNetwork(Network network)
        {
            var result = new List<VertexGeometry>();
            foreach (Vertex v in network.Vertices)
            {
                result.Add(ResolveVertex(network, v));
            }
            ValidateSetbacks(network, result);
            return result;
        }

        /// <summary>
        /// Warns about each road whose two end-setbacks overlap (sum
        /// exceeds the centerline length). Each offending road gets one
        /// warning per resolve. Caller can re-implement validation
        /// elsewhere — this is a convenience for the default resolver.
        /// </summary>
        static void ValidateSetbacks(Network network, List<VertexGeometry> geometries)
        {
            foreach (NetworkRoad road in network.Roads)
            {
                Vertex vA = FindVertex(network, road.EndA);
                Vertex vB = FindVertex(network, road.EndB);
                if (vA == null || vB == null) continue;

                float centerline = Vector2.Distance(vA.Position, vB.Position);
                if (centerline <= Eps) continue; // skip degenerate zero-length edges

                float? sA = FindResolvedSetback(geometries, vA.Id, road.Id, RoadEnd.A);
                float? sB = FindResolvedSetback(geometries, vB.Id, road.Id, RoadEnd.B);
                if (!sA.HasValue || !sB.HasValue) continue;

                float sum = sA.Value + sB.Value;
                if (sum > centerline + Eps)
                {
                    Debug.LogWarning(
                        $"[GeometryResolver] Road '{road.Id}' setbacks overlap: " +
                        $"endA@'{vA.Id}'={sA.Value:0.##}m + " +
                        $"endB@'{vB.Id}'={sB.Value:0.##}m = {sum:0.##}m, " +
                        $"but centerline length is only {centerline:0.##}m. " +
                        $"Road body has negative length ({centerline - sum:0.##}m); " +
                        $"increase the vertex spacing, narrow the road, or set " +
                        $"explicit SetbackA/SetbackB overrides.");
                }
            }
        }

        static float? FindResolvedSetback(List<VertexGeometry> geometries,
            string vertexId, string roadId, RoadEnd end)
        {
            foreach (VertexGeometry vg in geometries)
            {
                if (vg.VertexId != vertexId) continue;
                foreach (VertexApproach a in vg.Approaches)
                {
                    if (a.RoadId == roadId && a.End == end) return a.Setback;
                }
            }
            return null;
        }

        // -----------------------------------------------------------------
        // Internal data
        // -----------------------------------------------------------------

        struct ApproachData
        {
            public NetworkRoad Road;
            public RoadEnd End;
            public Vector2 BearingDir;    // unit vector from V along centerline (away)
            public float Bearing;         // atan2 of BearingDir
            public float RoadWidth;       // W (full cross-section width)
            public float Setback;         // resolved
            public bool HasSetbackOverride;
            public float SetbackOverride;
            public Vector2 OtherVertexPos; // position of the road's far endpoint (used by curved approaches) — already lateral-offset-shifted
            public Vector2 SelfShiftedPos; // this end's effective centerline endpoint after LateralOffset
            public Vector2 ShiftedControlA; // road.Curve.ControlA shifted by EndA's lateral offset (zeros for straight roads)
            public Vector2 ShiftedControlB; // road.Curve.ControlB shifted by EndB's lateral offset
        }

        // -----------------------------------------------------------------
        // Steps
        // -----------------------------------------------------------------

        static List<ApproachData> CollectApproaches(Network network, Vertex vertex)
        {
            var list = new List<ApproachData>();
            foreach (NetworkRoad road in network.Roads)
            {
                RoadEnd end;
                Vertex other;
                bool hasOverride;
                float overrideVal;
                if (road.EndA == vertex.Id)
                {
                    end = RoadEnd.A;
                    other = FindVertex(network, road.EndB);
                    hasOverride = road.SetbackA.HasValue;
                    overrideVal = road.SetbackA.GetValueOrDefault();
                }
                else if (road.EndB == vertex.Id)
                {
                    end = RoadEnd.B;
                    other = FindVertex(network, road.EndA);
                    hasOverride = road.SetbackB.HasValue;
                    overrideVal = road.SetbackB.GetValueOrDefault();
                }
                else continue;

                if (other == null) continue;

                // Curve-aware outward direction. For curved roads this is
                // the bezier tangent at the endpoint, so setback math,
                // OE fillet intersection, and lane endpoints all use the
                // correct direction at the vertex instead of the chord.
                Vector2 dir = OutwardDirection(road, end, vertex, other);
                if (dir.sqrMagnitude < Eps * Eps) continue;

                // Lateral-offset shifted endpoints. Both ends' offsets
                // are applied so curved-road bezier sampling and chord
                // direction match what the renderer will draw. perpRight
                // is taken from the UN-SHIFTED direction (one-shot —
                // no recursive recompute), which is the convention the
                // drag handle uses to interpret the offset.
                RoadEnd otherEnd = end == RoadEnd.A ? RoadEnd.B : RoadEnd.A;
                Vector2 shiftedSelfPos = EffectiveEndpoint(road, end, vertex, other);
                Vector2 shiftedOtherPos = EffectiveEndpoint(road, otherEnd, other, vertex);

                // Lateral-offset-shifted bezier controls. EffectiveControl
                // shifts the control by the same perpendicular vector as
                // the endpoint, so curved roads with offsets don't kink
                // at the endpoint. For straight roads (Curve == null)
                // these are zero and unused.
                Vertex vertexAtAObj = end == RoadEnd.A ? vertex : other;
                Vertex vertexAtBObj = end == RoadEnd.A ? other : vertex;
                Vector2 shiftedControlA = EffectiveControl(road, RoadEnd.A, vertexAtAObj, vertexAtBObj);
                Vector2 shiftedControlB = EffectiveControl(road, RoadEnd.B, vertexAtBObj, vertexAtAObj);

                list.Add(new ApproachData
                {
                    Road = road,
                    End = end,
                    BearingDir = dir,
                    Bearing = Mathf.Atan2(dir.y, dir.x),
                    RoadWidth = road.Profile.TotalWidth,
                    HasSetbackOverride = hasOverride,
                    SetbackOverride = overrideVal,
                    OtherVertexPos = shiftedOtherPos,
                    SelfShiftedPos = shiftedSelfPos,
                    ShiftedControlA = shiftedControlA,
                    ShiftedControlB = shiftedControlB,
                });
            }
            return list;
        }

        static void ComputeSetbacks(List<ApproachData> data)
        {
            int n = data.Count;
            for (int i = 0; i < n; i++)
            {
                ApproachData self = data[i];
                if (self.HasSetbackOverride)
                {
                    self.Setback = self.SetbackOverride;
                    data[i] = self;
                    continue;
                }

                float setback;

                if (n == 1)
                {
                    // Dead-end vertex (only one approach). There's no
                    // adjacent road to fillet against, so no fillet-related
                    // setback is needed. The cul-de-sac cap (deferred per
                    // Q6) will extend BEYOND the vertex away from the road
                    // body, not between the vertex and the body — so the
                    // road body should reach right to the vertex.
                    setback = 0f;
                }
                else
                {
                    int prevIdx = (i - 1 + n) % n;
                    int nextIdx = (i + 1) % n;

                    // CW angle from self to next: theta of the corner CW of self.
                    float thetaCW = CWAngleDelta(self.Bearing, data[nextIdx].Bearing);
                    // CW angle from prev to self: theta of the corner CCW of self.
                    float thetaCCW = CWAngleDelta(data[prevIdx].Bearing, self.Bearing);

                    // 2-way "joint" vertex (collinear approaches): just a
                    // passthrough waypoint along an otherwise-straight
                    // run. No intersection corner needed → setback = 0 so
                    // the road bodies meet right at the vertex without
                    // eating chord length around it.
                    if (n == 2
                        && Mathf.Abs(thetaCW - Mathf.PI) < JointAngleEpsilon
                        && Mathf.Abs(thetaCCW - Mathf.PI) < JointAngleEpsilon)
                    {
                        setback = 0f;
                    }
                    else
                    {
                        setback = self.RoadWidth; // base floor: W

                        setback = Mathf.Max(setback,
                            RequiredSetback(self.RoadWidth * 0.5f,
                                            data[nextIdx].RoadWidth * 0.5f, thetaCW));
                        setback = Mathf.Max(setback,
                            RequiredSetback(self.RoadWidth * 0.5f,
                                            data[prevIdx].RoadWidth * 0.5f, thetaCCW));

                        // Curved approaches need extra setback so the
                        // local tangent at the setback point bends enough
                        // away from the chord direction for the OE bezier
                        // to look visibly curved. See CurvedSetbackMultiplier
                        // docs above.
                        if (self.Road.Curve != null)
                        {
                            setback *= Mathf.Max(1f, CurvedSetbackMultiplier);
                        }
                    }
                }

                self.Setback = setback;
                data[i] = self;
            }
        }

        static VertexApproach BuildApproach(ApproachData d, Vector2 vertexPos, DriveSide driveSide)
        {
            Vector2 dir = d.BearingDir;

            // Curve-aware setback geometry. For straight roads everything
            // collapses to (vertexPos + setback * dir) and PerpRight(dir),
            // matching the original linear math. For curved roads we walk
            // the bezier's arc length to find the t value that puts the
            // setback point on the curve, and use the LOCAL tangent at that
            // point for the right perpendicular and the outer-edge dir.
            // This makes the road body (which starts at the same curve t)
            // meet the intersection corner exit at the same position and
            // orientation — no more "step" or "wedge" at the join.
            Vector2 setbackPoint;
            Vector2 outerEdgeDir;
            Vector2 rightFromV;
            if (d.Road.Curve != null && d.Setback > Eps)
            {
                Vector2 p0 = vertexPos;
                // Lateral-offset-shifted controls so the curve's tangent
                // at the (shifted) endpoint matches the un-shifted
                // tangent direction. Without shifting controls in
                // lock-step with the endpoint, an offset road's curve
                // kinks at the endpoint instead of continuing smoothly.
                Vector2 c1, c2;
                if (d.End == RoadEnd.A)
                {
                    c1 = d.ShiftedControlA;
                    c2 = d.ShiftedControlB;
                }
                else
                {
                    // Reverse the curve so t=0 is at THIS vertex (the
                    // approach's vertex) regardless of which end of the
                    // road we are.
                    c1 = d.ShiftedControlB;
                    c2 = d.ShiftedControlA;
                }
                Vector2 p3 = d.OtherVertexPos;
                float t = ArcLengthToT(p0, c1, c2, p3, d.Setback);
                setbackPoint = SampleCubic(p0, c1, c2, p3, t);
                Vector2 tangent = CubicTangent(p0, c1, c2, p3, t);
                if (tangent.sqrMagnitude < Eps * Eps) tangent = dir;
                outerEdgeDir = tangent.normalized;
                rightFromV = PerpRight(outerEdgeDir);
            }
            else
            {
                setbackPoint = vertexPos + d.Setback * dir;
                outerEdgeDir = dir;
                rightFromV = PerpRight(dir);
            }

            float halfW = d.RoadWidth * 0.5f;
            Vector2 outerRight = setbackPoint + halfW * rightFromV;
            Vector2 outerLeft = setbackPoint - halfW * rightFromV;

            // Determine the intrinsic A→B direction. The cross-section
            // is laid out relative to this, not to V's outward direction.
            // For curved approaches use the LOCAL outer-edge direction so
            // lane endpoints sit on the curve's local cross-section.
            Vector2 abForward = (d.End == RoadEnd.A) ? outerEdgeDir : -outerEdgeDir;
            Vector2 abRight = PerpRight(abForward); // driver's right when going A→B
            int abSign = driveSide == DriveSide.Right ? 1 : -1;
            int baSign = -abSign;

            float medianHalf = d.Road.Profile.Median != null
                ? d.Road.Profile.Median.Width * 0.5f
                : 0f;

            // Compute the centering shift so the asphalt midpoint sits
            // on the centerline. Matches RoadRenderer's shift — keeps
            // the resolver's lane endpoints aligned with where the road
            // body actually draws each lane. See RoadRenderer.cs for the
            // trade-off discussion (we lose the painted-centerline
            // convention but gain symmetric rendering).
            float abOuter = abSign * (medianHalf + ShoulderPlusLanesWidth(d.Road.Profile.AB.Lanes, d.Road.Profile.ShoulderAB.Width));
            float baOuter = baSign * (medianHalf + ShoulderPlusLanesWidth(d.Road.Profile.BA.Lanes, d.Road.Profile.ShoulderBA.Width));
            float midpoint = (abOuter + baOuter) * 0.5f;

            // Lane endpoints: each is the CENTER of the lane's setback edge
            // (the lane-width midpoint between its inner and outer edges).
            // Widths are emitted in parallel so downstream renderers can
            // compute lane edges without looking up the road profile.
            var laneEndsAB = new List<Vector2>();
            var laneWidthsAB = new List<float>();
            float cursor = abSign * medianHalf;
            foreach (Lane lane in d.Road.Profile.AB.Lanes)
            {
                float center = cursor + abSign * lane.Width * 0.5f - midpoint;
                laneEndsAB.Add(setbackPoint + center * abRight);
                laneWidthsAB.Add(lane.Width);
                cursor += abSign * lane.Width;
            }

            var laneEndsBA = new List<Vector2>();
            var laneWidthsBA = new List<float>();
            cursor = baSign * medianHalf;
            foreach (Lane lane in d.Road.Profile.BA.Lanes)
            {
                float center = cursor + baSign * lane.Width * 0.5f - midpoint;
                laneEndsBA.Add(setbackPoint + center * abRight);
                laneWidthsBA.Add(lane.Width);
                cursor += baSign * lane.Width;
            }

            // Shoulder widths on each side of this approach (CW = PerpRight,
            // CCW = -PerpRight). Depends on which RoadEnd this is + drive-side.
            // RHD@endA: AB is on PerpRight (driver's right when going outward).
            // RHD@endB: AB is on -PerpRight. LHD flips both.
            bool abOnCW = driveSide == DriveSide.Right
                ? d.End == RoadEnd.A
                : d.End == RoadEnd.B;
            float shoulderCW = abOnCW
                ? d.Road.Profile.ShoulderAB.Width
                : d.Road.Profile.ShoulderBA.Width;
            float shoulderCCW = abOnCW
                ? d.Road.Profile.ShoulderBA.Width
                : d.Road.Profile.ShoulderAB.Width;

            return new VertexApproach
            {
                RoadId = d.Road.Id,
                End = d.End,
                Bearing = d.Bearing,
                Setback = d.Setback,
                OuterLeft = outerLeft,
                OuterRight = outerRight,
                OuterEdgeDir = outerEdgeDir,
                LaneEndsAB = laneEndsAB,
                LaneEndsBA = laneEndsBA,
                LaneWidthsAB = laneWidthsAB,
                LaneWidthsBA = laneWidthsBA,
                Control = d.End == RoadEnd.A ? d.Road.ControlA : d.Road.ControlB,
                ShoulderWidthCW = shoulderCW,
                ShoulderWidthCCW = shoulderCCW,
            };
        }

        // -----------------------------------------------------------------
        // Math helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Required setback for self at the corner with neighbor N at
        /// angle θ. Per the spec: d = (hW_N + hW_self · cos θ) / sin θ.
        /// Returns 0 (no contribution) when θ is too close to 0 or π
        /// (degenerate/joint cases handled separately).
        /// </summary>
        static float RequiredSetback(float halfWSelf, float halfWNeighbor, float theta)
        {
            if (theta < JointAngleEpsilon) return 0f;          // ~0, would yield huge values; bail
            if (theta > Mathf.PI - JointAngleEpsilon) return 0f; // ~π joint, no fillet contribution
            return (halfWNeighbor + halfWSelf * Mathf.Cos(theta)) / Mathf.Sin(theta);
        }

        /// <summary>
        /// Angle traversed going CW from fromAngle to toAngle, in (0, 2π].
        /// </summary>
        static float CWAngleDelta(float fromAngle, float toAngle)
        {
            float d = fromAngle - toAngle;
            while (d <= 0f) d += 2f * Mathf.PI;
            while (d > 2f * Mathf.PI) d -= 2f * Mathf.PI;
            return d;
        }

        /// <summary>
        /// Intersection of two parametric lines (p1 + t·d1) and
        /// (p2 + s·d2). Returns null when the lines are parallel.
        /// </summary>
        static Vector2? LineIntersect(Vector2 p1, Vector2 d1, Vector2 p2, Vector2 d2)
        {
            float det = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(det) < 1e-6f) return null;
            Vector2 diff = p2 - p1;
            float t = (diff.x * d2.y - diff.y * d2.x) / det;
            return p1 + t * d1;
        }

        /// <summary>Right perpendicular (90° CW). PerpRight((1,0)) = (0,-1).</summary>
        static Vector2 PerpRight(Vector2 v) => new Vector2(v.y, -v.x);

        /// <summary>
        /// World XZ position of a named LaneNode on a specific lane at
        /// a specific approach. Returns null when the node isn't
        /// physically present at this vertex (e.g. asking for Tertiary
        /// at an end-A vertex returns null, because Tertiary lives at
        /// the road's B end).
        ///
        /// Layout (per the model docs):
        ///   End-A vertex → Origin / A / Primary are at the setback.
        ///   End-B vertex → Secondary / B / Tertiary are at the setback.
        /// Origin + Tertiary sit on the INNER side (toward centerline);
        /// Primary + Secondary on the OUTER side (toward shoulder).
        /// </summary>
        public static Vector2? ResolveLaneNode(VertexApproach a, Direction dir, int laneIndex, LaneNode node)
        {
            if (a == null) return null;
            List<Vector2> centers = dir == Direction.AB ? a.LaneEndsAB : a.LaneEndsBA;
            List<float> widths = dir == Direction.AB ? a.LaneWidthsAB : a.LaneWidthsBA;
            if (centers == null || widths == null) return null;
            if (laneIndex < 0 || laneIndex >= centers.Count) return null;
            if (laneIndex >= widths.Count) return null;

            // Validate this node lives on the end-of-road at this vertex.
            bool aEnd = a.End == RoadEnd.A;
            bool nodeAtA = node == LaneNode.Origin || node == LaneNode.A || node == LaneNode.Primary;
            bool nodeAtB = node == LaneNode.Secondary || node == LaneNode.B || node == LaneNode.Tertiary;
            if (aEnd && !nodeAtA) return null;
            if (!aEnd && !nodeAtB) return null;

            Vector2 center = centers[laneIndex];
            // Midpoint nodes (A / B) are just the lane centerline endpoint.
            if (node == LaneNode.A || node == LaneNode.B) return center;

            // Corner nodes need the cross-direction along the setback
            // line and the "inner" side (toward the road centerline =
            // toward the midpoint of the setback line).
            Vector2 crossDir = a.OuterRight - a.OuterLeft;
            float crossLen = crossDir.magnitude;
            if (crossLen < 1e-4f) return center;
            crossDir /= crossLen;
            Vector2 setbackMidpoint = (a.OuterLeft + a.OuterRight) * 0.5f;
            float toMidpointProj = Vector2.Dot(setbackMidpoint - center, crossDir);
            Vector2 innerDir = toMidpointProj >= 0f ? crossDir : -crossDir;

            float half = widths[laneIndex] * 0.5f;
            bool isInner = node == LaneNode.Origin || node == LaneNode.Tertiary;
            return center + (isInner ? +half : -half) * innerDir;
        }

        /// <summary>
        /// Convenience: resolve a (LaneRef, LaneNode) against the
        /// approach in <paramref name="vg"/> matching the lane's RoadId.
        /// </summary>
        public static Vector2? ResolveLaneNode(VertexGeometry vg, LaneRef lr, LaneNode node)
        {
            if (vg == null || lr == null) return null;
            foreach (VertexApproach a in vg.Approaches)
            {
                if (a.RoadId == lr.RoadId)
                    return ResolveLaneNode(a, lr.Direction, lr.Index, node);
            }
            return null;
        }

        static float ShoulderPlusLanesWidth(List<Lane> lanes, float shoulderWidth)
        {
            float w = shoulderWidth;
            foreach (Lane l in lanes) w += l.Width;
            return w;
        }

        static Vertex FindVertex(Network n, string id)
        {
            foreach (Vertex v in n.Vertices)
                if (v.Id == id) return v;
            return null;
        }

        // -----------------------------------------------------------------
        // Bezier sampling helper (used by visualizers and mesh builders)
        // -----------------------------------------------------------------

        /// <summary>Sample a quadratic bezier at <paramref name="t"/> ∈ [0,1].</summary>
        public static Vector2 SampleQuadratic(Vector2 p0, Vector2 c, Vector2 p2, float t)
        {
            float u = 1f - t;
            return u * u * p0 + 2f * u * t * c + t * t * p2;
        }

        /// <summary>
        /// Tangent (unnormalized derivative) of a quadratic bezier at
        /// <paramref name="t"/>. B'(t) = 2(1-t)(c - p0) + 2t(p2 - c).
        /// </summary>
        public static Vector2 QuadraticTangent(Vector2 p0, Vector2 c, Vector2 p2, float t)
        {
            float u = 1f - t;
            return 2f * u * (c - p0) + 2f * t * (p2 - c);
        }

        /// <summary>Sample a cubic bezier at <paramref name="t"/> ∈ [0,1].</summary>
        public static Vector2 SampleCubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float t)
        {
            float u = 1f - t;
            return (u * u * u) * p0
                 + (3f * u * u * t) * c1
                 + (3f * u * t * t) * c2
                 + (t * t * t) * p3;
        }

        /// <summary>
        /// Find the parameter t ∈ [0,1] at which the arc length along the
        /// cubic bezier from p0 reaches <paramref name="targetLength"/>.
        /// Sampled at 64 segments and linearly interpolated within the
        /// segment that crosses the target. Returns 0 for non-positive
        /// targets and 1 if the target exceeds the full arc length.
        /// </summary>
        public static float ArcLengthToT(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float targetLength)
        {
            if (targetLength <= 0f) return 0f;
            const int SAMPLES = 64;
            Vector2 prev = p0;
            float cumLen = 0f;
            for (int i = 1; i <= SAMPLES; i++)
            {
                float t = (float)i / SAMPLES;
                Vector2 curr = SampleCubic(p0, c1, c2, p3, t);
                float seg = Vector2.Distance(prev, curr);
                if (cumLen + seg >= targetLength)
                {
                    if (seg < 1e-6f) return (i - 1) / (float)SAMPLES;
                    float frac = (targetLength - cumLen) / seg;
                    return ((i - 1) + frac) / SAMPLES;
                }
                cumLen += seg;
                prev = curr;
            }
            return 1f;
        }

        /// <summary>Unnormalized tangent of a cubic bezier at <paramref name="t"/>. B'(t) = 3(1-t)²(c1-p0) + 6(1-t)t(c2-c1) + 3t²(p3-c2).</summary>
        public static Vector2 CubicTangent(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float t)
        {
            float u = 1f - t;
            return (3f * u * u) * (c1 - p0)
                 + (6f * u * t) * (c2 - c1)
                 + (3f * t * t) * (p3 - c2);
        }

        /// <summary>
        /// Find intersections between a cubic bezier and a line segment.
        /// Uses recursive De Casteljau subdivision with bounding-box
        /// culling; subdivides until each sub-curve is flat (control
        /// points within `flatnessThreshold` of the chord), then treats
        /// it as a line and does segment-segment intersection. Each
        /// result is (t on the original curve, s on the segment, world
        /// position of the hit). Order of results is by subdivision
        /// walk order, not by t — sort if you need ascending t.
        /// </summary>
        public static void IntersectCubicSegment(
            Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3,
            Vector2 segA, Vector2 segB,
            float flatnessThreshold,
            List<(float t, float s, Vector2 point)> results,
            int maxDepth = 20)
        {
            IntersectCubicSegmentRec(p0, c1, c2, p3, segA, segB,
                flatnessThreshold, 0f, 1f, maxDepth, results);
        }

        static void IntersectCubicSegmentRec(
            Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3,
            Vector2 segA, Vector2 segB,
            float flatnessThreshold,
            float tStart, float tEnd, int depth,
            List<(float t, float s, Vector2 point)> results)
        {
            // Bounding-box cull: if curve's AABB doesn't overlap the
            // segment's AABB, no intersection possible in this sub-range.
            Vector2 minB = Vector2.Min(Vector2.Min(p0, c1), Vector2.Min(c2, p3));
            Vector2 maxB = Vector2.Max(Vector2.Max(p0, c1), Vector2.Max(c2, p3));
            Vector2 minS = Vector2.Min(segA, segB);
            Vector2 maxS = Vector2.Max(segA, segB);
            if (maxB.x < minS.x || maxS.x < minB.x) return;
            if (maxB.y < minS.y || maxS.y < minB.y) return;

            // Flatness: distance from each control point to the chord
            // p0→p3. If both are within threshold (or we've recursed
            // too deep), approximate the sub-curve as the chord.
            float dC1 = PerpendicularDistance(c1, p0, p3);
            float dC2 = PerpendicularDistance(c2, p0, p3);
            if (depth <= 0 || (dC1 < flatnessThreshold && dC2 < flatnessThreshold))
            {
                if (TrySegSegIntersect(p0, p3, segA, segB,
                        out float tLocal, out float s, out Vector2 hit))
                {
                    float tGlobal = tStart + tLocal * (tEnd - tStart);
                    results.Add((tGlobal, s, hit));
                }
                return;
            }

            // De Casteljau split at t=0.5 → two halves; recurse.
            SplitCubic(p0, c1, c2, p3, 0.5f,
                out Vector2 lp0, out Vector2 lc1, out Vector2 lc2, out Vector2 lp3,
                out Vector2 rp0, out Vector2 rc1, out Vector2 rc2, out Vector2 rp3);
            float tMid = (tStart + tEnd) * 0.5f;
            IntersectCubicSegmentRec(lp0, lc1, lc2, lp3, segA, segB,
                flatnessThreshold, tStart, tMid, depth - 1, results);
            IntersectCubicSegmentRec(rp0, rc1, rc2, rp3, segA, segB,
                flatnessThreshold, tMid, tEnd, depth - 1, results);
        }

        // Perpendicular distance from point P to line through A and B.
        static float PerpendicularDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-9f) return Vector2.Distance(p, a);
            // 2D cross product magnitude / |ab| = perpendicular distance.
            float cross = ab.x * (p.y - a.y) - ab.y * (p.x - a.x);
            return Mathf.Abs(cross) / Mathf.Sqrt(len2);
        }

        // Segment-segment intersection. Returns true and (tA, tB, hit)
        // for an interior crossing; endpoint-touches return false so
        // shared-vertex meetings don't fire as crossings. Mirrors the
        // designer's TrySegmentIntersection but lives here so the
        // curve-segment finder can be self-contained.
        static bool TrySegSegIntersect(Vector2 a0, Vector2 a1,
            Vector2 b0, Vector2 b1,
            out float tA, out float tB, out Vector2 point)
        {
            tA = 0f; tB = 0f; point = Vector2.zero;
            Vector2 r = a1 - a0;
            Vector2 s = b1 - b0;
            float denom = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(denom) < 1e-9f) return false;
            Vector2 diff = b0 - a0;
            tA = (diff.x * s.y - diff.y * s.x) / denom;
            tB = (diff.x * r.y - diff.y * r.x) / denom;
            const float eps = 1e-4f;
            if (tA <= eps || tA >= 1f - eps) return false;
            if (tB <= eps || tB >= 1f - eps) return false;
            point = a0 + r * tA;
            return true;
        }

        /// <summary>
        /// Numerical closest-point projection onto a cubic bezier. Two
        /// passes: (1) sample N points along the curve, find the closest;
        /// (2) refine with a few binary searches around the best sample.
        /// For road-scale work (curves up to ~200m, target accuracy
        /// sub-meter), N=64 + 6 refinement steps is plenty fast and
        /// accurate. Returns the bezier t parameter, the closest point
        /// on the curve, and the squared distance to the target.
        /// </summary>
        public static void ClosestPointOnCubic(
            Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, Vector2 target,
            out float tBest, out Vector2 pointBest, out float distSqBest,
            int samples = 64, int refineSteps = 6)
        {
            tBest = 0f;
            pointBest = p0;
            distSqBest = (p0 - target).sqrMagnitude;
            float step = 1f / samples;
            for (int i = 1; i <= samples; i++)
            {
                float t = i * step;
                Vector2 q = SampleCubic(p0, c1, c2, p3, t);
                float d2 = (q - target).sqrMagnitude;
                if (d2 < distSqBest)
                {
                    distSqBest = d2;
                    tBest = t;
                    pointBest = q;
                }
            }
            // Binary refinement around tBest.
            float halfStep = step;
            for (int i = 0; i < refineSteps; i++)
            {
                halfStep *= 0.5f;
                float tL = Mathf.Max(0f, tBest - halfStep);
                float tR = Mathf.Min(1f, tBest + halfStep);
                Vector2 qL = SampleCubic(p0, c1, c2, p3, tL);
                Vector2 qR = SampleCubic(p0, c1, c2, p3, tR);
                float dL2 = (qL - target).sqrMagnitude;
                float dR2 = (qR - target).sqrMagnitude;
                if (dL2 < distSqBest) { distSqBest = dL2; tBest = tL; pointBest = qL; }
                if (dR2 < distSqBest) { distSqBest = dR2; tBest = tR; pointBest = qR; }
            }
        }

        /// <summary>De Casteljau split: cubic at <paramref name="t"/> → left [0..t] + right [t..1], each as 4 control points.</summary>
        public static void SplitCubic(
            Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float t,
            out Vector2 lp0, out Vector2 lc1, out Vector2 lc2, out Vector2 lp3,
            out Vector2 rp0, out Vector2 rc1, out Vector2 rc2, out Vector2 rp3)
        {
            Vector2 q0 = Vector2.Lerp(p0, c1, t);
            Vector2 q1 = Vector2.Lerp(c1, c2, t);
            Vector2 q2 = Vector2.Lerp(c2, p3, t);
            Vector2 r0 = Vector2.Lerp(q0, q1, t);
            Vector2 r1 = Vector2.Lerp(q1, q2, t);
            Vector2 s  = Vector2.Lerp(r0, r1, t);
            lp0 = p0; lc1 = q0; lc2 = r0; lp3 = s;
            rp0 = s;  rc1 = r1; rc2 = q2; rp3 = p3;
        }

        /// <summary>
        /// Extract the sub-curve of a cubic bezier over parameter range
        /// [<paramref name="t0"/>, <paramref name="t1"/>] as four control
        /// points. Two de Casteljau splits — slice off [0..t0], then slice
        /// off [t1..1] from what remains.
        /// </summary>
        public static void SubCubic(
            Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float t0, float t1,
            out Vector2 sp0, out Vector2 sc1, out Vector2 sc2, out Vector2 sp3)
        {
            t0 = Mathf.Clamp01(t0);
            t1 = Mathf.Clamp01(t1);
            if (t1 <= t0)
            {
                sp0 = sc1 = sc2 = sp3 = SampleCubic(p0, c1, c2, p3, t0);
                return;
            }
            SplitCubic(p0, c1, c2, p3, t0,
                out _, out _, out _, out _,
                out Vector2 rp0, out Vector2 rc1, out Vector2 rc2, out Vector2 rp3);
            float tAdj = t1 >= 1f ? 1f : (t1 - t0) / (1f - t0);
            SplitCubic(rp0, rc1, rc2, rp3, tAdj,
                out sp0, out sc1, out sc2, out sp3,
                out _, out _, out _, out _);
        }

        /// <summary>
        /// Unit direction the road extends from `thisVertex` (away from
        /// the vertex, into the road body). For straight roads this is
        /// the chord direction to the other endpoint; for curved roads
        /// it's the bezier tangent at the appropriate endpoint —
        /// (Control - thisVertex.Position).normalized.
        ///
        /// Returns Vector2.zero only when the road is degenerate (both
        /// endpoints coincident and no curve, or curve control coincident
        /// with the endpoint). Callers should treat zero as "skip this
        /// approach".
        /// </summary>
        /// <summary>
        /// Effective centerline endpoint at a vertex, accounting for the
        /// road's per-end LateralOffset (perpendicular shift to the road's
        /// outward direction at that end). When offsets are 0 this is
        /// just vertex.Position. Used by the resolver (intersection
        /// math), the renderer (curved-road bezier sampling), and the
        /// lateral-offset drag handle UI — keep them in sync by going
        /// through this helper instead of recomputing the shift in
        /// each call site.
        /// </summary>
        public static Vector2 EffectiveEndpoint(NetworkRoad road, RoadEnd end,
            Vertex thisVertex, Vertex otherVertex)
        {
            if (thisVertex == null) return Vector2.zero;
            float offset = end == RoadEnd.A ? road.LateralOffsetA : road.LateralOffsetB;
            if (Mathf.Abs(offset) < 1e-6f) return thisVertex.Position;
            Vector2 dir = OutwardDirection(road, end, thisVertex, otherVertex);
            if (dir.sqrMagnitude < 1e-8f) return thisVertex.Position;
            // perpRight in (x, z) = CW 90° rotation of dir.
            Vector2 perpRight = new Vector2(dir.y, -dir.x);
            return thisVertex.Position + perpRight * offset;
        }

        /// <summary>
        /// Effective bezier control point at a vertex, accounting for
        /// the road's per-end LateralOffset. The control is shifted by
        /// the SAME perpendicular vector as the endpoint — that keeps
        /// the tangent direction at the shifted endpoint identical to
        /// the original (control - endpoint vector is unchanged), so
        /// curved roads with lateral offsets don't kink at the endpoint.
        /// Returns Vector2.zero if the road has no curve. For straight
        /// roads this isn't called (no controls to shift).
        /// </summary>
        public static Vector2 EffectiveControl(NetworkRoad road, RoadEnd end,
            Vertex thisVertex, Vertex otherVertex)
        {
            if (road == null || road.Curve == null || thisVertex == null) return Vector2.zero;
            Vector2 control = end == RoadEnd.A ? road.Curve.ControlA : road.Curve.ControlB;
            float offset = end == RoadEnd.A ? road.LateralOffsetA : road.LateralOffsetB;
            if (Mathf.Abs(offset) < 1e-6f) return control;
            Vector2 dir = OutwardDirection(road, end, thisVertex, otherVertex);
            if (dir.sqrMagnitude < 1e-8f) return control;
            Vector2 perpRight = new Vector2(dir.y, -dir.x);
            return control + perpRight * offset;
        }

        public static Vector2 OutwardDirection(NetworkRoad road, RoadEnd end,
            Vertex thisVertex, Vertex otherVertex)
        {
            if (road.Curve != null)
            {
                Vector2 ctrl = end == RoadEnd.A ? road.Curve.ControlA : road.Curve.ControlB;
                Vector2 v = ctrl - thisVertex.Position;
                if (v.sqrMagnitude >= 1e-8f) return v.normalized;
                // Degenerate: control == endpoint. Fall back to chord.
            }
            if (otherVertex == null) return Vector2.zero;
            Vector2 chord = otherVertex.Position - thisVertex.Position;
            if (chord.sqrMagnitude < 1e-8f) return Vector2.zero;
            return chord.normalized;
        }
    }
}
