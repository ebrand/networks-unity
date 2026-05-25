// C# strawman port of road-designer/src/model/network.ts.
//
// Network-level data model: vertices (graph nodes), roads (graph edges),
// and the lane-level connectivity that lives at each vertex.
//
// See the .ts file for the SETBACK RULES and DEFAULT CONNECTIVITY RULES
// comment blocks — those are intentionally kept on the TypeScript side
// as the single source of truth for the spec while we iterate. When the
// rules are stable, port them into a Resolver.cs alongside this file.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Model
{
    // -----------------------------------------------------------------
    // Enums
    // -----------------------------------------------------------------

    /// <summary>Travel direction along a road's centerline.</summary>
    public enum Direction
    {
        AB,
        BA,
    }

    /// <summary>
    /// Network-wide drive-side convention. Determines which physical
    /// side of the centerline each direction's lanes occupy.
    /// </summary>
    public enum DriveSide
    {
        /// <summary>RHD: AB lanes are on the right of centerline (US, EU).</summary>
        Right,
        /// <summary>LHD: AB lanes are on the left of centerline (UK, JP, AU).</summary>
        Left,
    }

    /// <summary>
    /// Road classification. The bootstrap rule from the design doc: the
    /// first road added between any pair of vertices is Primary; later
    /// additions are Secondary. The user can re-classify any road.
    ///
    /// Per the resolved spec (see network.ts Q3), classification does
    /// NOT enter the setback formula — that's purely geometric. It
    /// influences default values, lane-connectivity priorities, and
    /// possibly aesthetic rendering.
    /// </summary>
    public enum RoadClassification
    {
        Primary,
        Secondary,
    }

    /// <summary>
    /// Sign control at a road end where it meets a vertex. Visualized
    /// as a textured sign (stop sign / yield triangle) placed beside
    /// the inbound approach. Only meaningful when the road actually
    /// carries inbound traffic at that end.
    /// </summary>
    public enum StopYieldControl
    {
        None,
        Stop,
        Yield,
    }

    // -----------------------------------------------------------------
    // Lane references and connectivity
    // -----------------------------------------------------------------

    /// <summary>
    /// A specific lane on a specific road, by direction and index.
    /// Used as the from/to of LaneConnections at a vertex.
    /// </summary>
    [Serializable]
    public class LaneRef
    {
        public string RoadId;
        public Direction Direction;
        public int Index;
    }

    /// <summary>
    /// A directed lane-to-lane connection inside a vertex: traffic
    /// arriving on <see cref="From"/> may continue out on <see cref="To"/>.
    /// Many-to-many is allowed (one inbound lane fans out to multiple
    /// outbound; one outbound gathers from multiple inbound).
    ///
    /// Inbound/outbound is derived from the road's endpoints + lane
    /// direction:
    ///   At road.EndA's vertex: BA lanes are inbound, AB lanes are outbound.
    ///   At road.EndB's vertex: AB lanes are inbound, BA lanes are outbound.
    ///
    /// The lane-change-zone discussion added a sub-distinction worth
    /// flagging: connections can be Required (mandatory, e.g., a
    /// dropped lane that must merge) or Permitted (optional lane
    /// change inside a transition zone). For the strawman both flavors
    /// share this type; add a Kind field when we lock the design.
    /// </summary>
    [Serializable]
    public class LaneConnection
    {
        public LaneRef From;
        public LaneRef To;
    }

    // -----------------------------------------------------------------
    // Vertex
    // -----------------------------------------------------------------

    /// <summary>
    /// A graph vertex. Every road end terminates at a vertex; even a
    /// dead-end has a vertex at its terminus (with only one road).
    /// The world position is the conceptual point where all connecting
    /// road centerlines meet — rendered geometry is set back from here.
    /// </summary>
    [Serializable]
    public class Vertex
    {
        public string Id;
        public Vector2 Position;
        /// <summary>Optional display label for the intersection.</summary>
        public string Name;
        /// <summary>
        /// Explicit connectivity overrides. Anything not listed here
        /// falls back to the default-connectivity rules computed from
        /// geometry (straight-through, curb-to-curb turns, opposite-left).
        /// Storing only overrides keeps saved networks small.
        /// </summary>
        public List<LaneConnection> ConnectivityOverrides = new List<LaneConnection>();
    }

    // -----------------------------------------------------------------
    // Network road (graph edge)
    // -----------------------------------------------------------------

    /// <summary>
    /// A road = a graph edge between two vertices, carrying a
    /// cross-section profile. EndA and EndB correspond to the profile's
    /// A and B sides — so AB-direction traffic flows from EndA's vertex
    /// toward EndB's vertex.
    /// </summary>
    [Serializable]
    public class NetworkRoad
    {
        public string Id;
        public string EndA;
        public string EndB;
        public RoadClassification Classification = RoadClassification.Secondary;
        public RoadProfile Profile = new RoadProfile();

        /// <summary>
        /// Per-end setback override (meters along centerline). Nullable;
        /// when null the resolver computes the setback from the rules
        /// in network.ts. SetbackA applies at EndA's vertex; SetbackB at
        /// EndB's vertex.
        /// </summary>
        public float? SetbackA;
        public float? SetbackB;

        /// <summary>
        /// Per-end traffic control sign. ControlA applies at EndA's
        /// vertex (so it's the sign drivers see as they approach EndA
        /// going B→A); ControlB at EndB's vertex. Default None.
        /// </summary>
        public StopYieldControl ControlA = StopYieldControl.None;
        public StopYieldControl ControlB = StopYieldControl.None;

        /// <summary>
        /// Optional cubic-bezier curvature. When null the road is a
        /// straight segment between EndA and EndB (legacy behavior).
        /// When non-null:
        ///   - ControlA is the bezier control point near EndA; the
        ///     outgoing tangent at EndA is (ControlA - EndA.Position).
        ///   - ControlB is the bezier control point near EndB; the
        ///     outgoing tangent at EndB is (ControlB - EndB.Position).
        ///
        /// Stored as absolute world XZ positions (not endpoint-relative
        /// offsets) so reading the geometry is a single field access.
        /// Side-effect: moving an endpoint vertex does NOT auto-adjust
        /// the controls; the curve has to be recreated or its controls
        /// edited explicitly. Acceptable for now since vertex drag in
        /// Edit mode doesn't apply to roads with a curve yet.
        /// </summary>
        public RoadCurve Curve;
    }

    /// <summary>
    /// Cubic-bezier curvature on a NetworkRoad. See NetworkRoad.Curve.
    /// </summary>
    [Serializable]
    public class RoadCurve
    {
        public Vector2 ControlA;
        public Vector2 ControlB;
    }

    // -----------------------------------------------------------------
    // Network (root)
    // -----------------------------------------------------------------

    /// <summary>
    /// A cul-de-sac bulb: a filled circular asphalt area at the end of a
    /// dead-end road. The ring of vertices around the bulb perimeter +
    /// the ring arcs between them are regular Vertices/NetworkRoads
    /// stored in the network; this struct just adds the FILLED interior
    /// disc (rendered as a flat asphalt circle inside the ring). Without
    /// it the bulb would render as a donut with grass in the middle.
    /// EntryVertexId is the ring vertex that the approach road connects
    /// to (used for cleanup / detection).
    /// </summary>
    [Serializable]
    public class CulDeSacBulb
    {
        public string Id;
        public Vector2 Center;
        public float Radius;
        public string EntryVertexId;
    }

    /// <summary>
    /// Top-level network. DriveSide is network-wide — flipping it
    /// re-orients every road across its centerline without touching
    /// any lane data.
    /// </summary>
    [Serializable]
    public class Network
    {
        public DriveSide DriveSide = DriveSide.Right;
        public List<Vertex> Vertices = new List<Vertex>();
        public List<NetworkRoad> Roads = new List<NetworkRoad>();
        public List<CulDeSacBulb> CulDeSacs = new List<CulDeSacBulb>();
    }
}
