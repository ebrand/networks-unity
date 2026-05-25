// Network-level data model: vertices (graph nodes), roads (graph edges),
// and the lane-level connectivity that lives at each vertex.
//
// This file is intentionally STRAWMAN: the shapes are meant to be argued
// with before any rendering or geometry code is written. The existing
// cross-section model in `./types.ts` continues to describe a single
// road's profile (lanes/shoulders/median) and is reused here under the
// alias `RoadProfile`.

import type { DriveSide, Point2D, Road } from "./types";

// ---------------------------------------------------------------------------
// Identifiers (string-typed for now; could promote to branded types later)
// ---------------------------------------------------------------------------

export type VertexId = string;
export type RoadId = string;

// ---------------------------------------------------------------------------
// Cross-section profile alias
// ---------------------------------------------------------------------------

// `Road` from ./types.ts has always meant "the cross-section profile of
// a road" — its lanes, shoulders, optional median. In a network, a road
// is more than its cross-section (it has endpoints, classification,
// setbacks), so we rename here for clarity.
export type RoadProfile = Road;

// ---------------------------------------------------------------------------
// Vertices
// ---------------------------------------------------------------------------

// A graph vertex. Every road end terminates at a vertex; even a dead-end
// has a vertex at its terminus (it just happens to have only one road).
// The vertex's world position is the conceptual point at which all
// connecting road centerlines meet — render geometry sets back from here.
export interface Vertex {
  id: VertexId;
  position: Point2D;
  // Optional display label.
  name?: string;
  // Explicit lane connectivity overrides. Anything not listed here
  // falls back to the default connectivity rules (see DEFAULT_CONNECTIVITY
  // below). Storing only overrides keeps the file small and lets the
  // defaults change without breaking saved networks.
  connectivityOverrides: LaneConnection[];
}

// ---------------------------------------------------------------------------
// Roads (graph edges)
// ---------------------------------------------------------------------------

// Road classification affects setback at the vertices the road
// terminates at. Primary roads keep their straight geometry and other
// roads bend setbacks around them; secondary roads absorb the angular
// compromise.
//
// Bootstrap rule from the design doc: the FIRST road added between any
// pair of vertices is primary; later additions are secondary by default.
// The tool will let the user re-classify any road manually.
export type RoadClassification = "primary" | "secondary";

// A road = a graph edge between two vertices, carrying a cross-section
// profile. `endA` and `endB` correspond to the profile's A and B sides —
// so AB-direction traffic flows from endA's vertex toward endB's vertex.
export interface NetworkRoad {
  id: RoadId;
  endA: VertexId;
  endB: VertexId;
  classification: RoadClassification;
  profile: RoadProfile;
  // Per-end setback override (meters along centerline). If absent at an
  // end, the resolver computes the setback from classification + the
  // approach angles at that vertex. See SETBACK RULES below.
  setbackA?: number;
  setbackB?: number;
}

// ---------------------------------------------------------------------------
// Lane references and connectivity
// ---------------------------------------------------------------------------

// A specific lane on a specific road, by direction and index.
// (Direction is travel direction; index 0 is innermost — closest to the
// road's centerline.)
export interface LaneRef {
  roadId: RoadId;
  direction: "AB" | "BA";
  index: number;
}

// At a given vertex, a lane is either inbound (traffic arrives) or
// outbound (traffic departs). Whether a lane is inbound or outbound at
// a vertex is derived from the road's endpoints + the lane's direction:
//
//   At road.endA's vertex:  BA-lanes are INBOUND, AB-lanes are OUTBOUND.
//   At road.endB's vertex:  AB-lanes are INBOUND, BA-lanes are OUTBOUND.
//
// A connection is "incoming lane → outgoing lane" at a single vertex.
// Many-to-many is allowed: one inbound lane can fan out to multiple
// outbound lanes (straight + right turn), and one outbound lane can
// gather from multiple inbound lanes.
export interface LaneConnection {
  from: LaneRef;
  to: LaneRef;
}

// ---------------------------------------------------------------------------
// The network
// ---------------------------------------------------------------------------

export interface Network {
  // Drive-side is network-wide (you can't have one road LHD and another
  // RHD in the same network). This matches the original cross-section
  // designer.
  driveSide: DriveSide;
  vertices: Vertex[];
  roads: NetworkRoad[];
}

// ---------------------------------------------------------------------------
// Computed geometry — outputs of a future `buildNetworkGeometry()`
// ---------------------------------------------------------------------------

// One road approaching a vertex. The geometry resolver produces one of
// these per (vertex, road-end) pair. Ordering within `VertexGeometry`
// is clockwise by bearing so adjacent indices share a fillet.
export interface VertexApproach {
  roadId: RoadId;
  end: "A" | "B";
  // Bearing of the road's centerline FROM the vertex, in radians.
  // 0 = +x (east); CCW positive (math convention).
  bearing: number;
  // Setback distance along the centerline. Resolver picks the per-end
  // override if present, otherwise applies the rule below.
  setback: number;
  // The lane endpoints on the SETBACK line. These are where each lane's
  // A or B well-known point lives — pulled inward from the vertex itself.
  // Indexing matches the road's profile.
  laneEndsAB: Point2D[];
  laneEndsBA: Point2D[];
  // The outer edges of the entire road (shoulder-to-shoulder) at the
  // setback line. Used as the endpoints of the bezier fillets that
  // connect adjacent approaches.
  outerLeft: Point2D;
  outerRight: Point2D;
}

// A vertex's asphalt outline is a closed path of mixed segments.
// Currently two kinds:
//   - line: the straight setback edge that crosses one road
//   - quadraticBezier: the fillet between two adjacent approaches
// Dead-ends use a single quadratic (or two stitched quadratics) for the
// cul-de-sac cap.
export type OutlineSegment =
  | { kind: "line"; from: Point2D; to: Point2D }
  | {
      kind: "quadraticBezier";
      from: Point2D;
      // Control point sits at the intersection of the two road outer
      // edges, extended past the setback. This makes the curve tangent
      // to both edges at its endpoints.
      control: Point2D;
      to: Point2D;
    };

export interface VertexGeometry {
  vertexId: VertexId;
  approaches: VertexApproach[]; // clockwise by bearing
  outline: OutlineSegment[];    // closed path
  // The connectivity table actually in effect (defaults + overrides
  // merged). Drawn as lane-flow curves inside the asphalt.
  connectivity: LaneConnection[];
}

// ---------------------------------------------------------------------------
// SETBACK RULES (defaults — overridable per road end)
// ---------------------------------------------------------------------------
//
// Notation at vertex V:
//   R          = the road we're computing the setback for
//   N          = a road adjacent to R at V (its CW or CCW neighbor in
//                bearing-order around V; every road has up to 2 such
//                neighbors)
//   W_R, W_N   = full cross-section widths
//   hW_R, hW_N = half-widths
//   θ          = the angle between R's centerline (from V) and N's
//                centerline (from V), measured through the asphalt
//                corner that R and N share. 0 < θ ≤ π.
//
// Per-corner geometric minimum (the "OE intersection distance"):
//
//   d_R_at_corner_with_N = (hW_N + hW_R · cos θ) / sin θ
//
// This is the distance from V, along R's centerline, to the point where
// R's outer edge (on the N-facing side) meets N's outer edge (on the
// R-facing side) when both are extended. It is also the control point
// of the bezier fillet at that corner (per your spec: "intersection
// point placed where the road edges intersect").
//
// For the fillet to be a proper convex bezier, R's setback at V must
// satisfy: setback_R ≥ d_R_at_corner_with_N. (If R sets back less, the
// bezier control sits BEYOND R's outer-edge endpoint and the fillet
// folds back on itself.) The same constraint binds N independently.
//
// Default rule:
//
//   setback_R(V) =
//     overrideAt(R, V)
//     ?? max(
//          W_R,                                         // base default
//          max over R's neighbors N at V of
//            d_R_at_corner_with_N
//        )
//
// Worked examples (equal half-widths hW for simplicity):
//   θ = π/2 (perpendicular):   d = hW              → setback = max(W, hW) = W
//   θ = π/3 (60°):             d ≈ 1.73 · hW       → setback = max(W, 1.73·hW) = W
//   θ = π/6 (30°):             d ≈ 3.73 · hW       → setback = 3.73·hW ≈ 1.87·W
//   θ = π (joint, 180°):       d = 0 (limit)       → setback = W (degenerate; see Q5)
//
// MULTI-ROAD CASE: just take the max over both neighbors (CW and CCW).
// A road in a 4-way intersection whose CW neighbor is perpendicular but
// whose CCW neighbor is at 30° gets the larger setback driven by the
// acute side. The other side's fillet is still convex (the road set
// back further than that neighbor required, which is fine — the
// bezier endpoints just move away from the OE intersection toward
// the road's far end).
//
// CLASSIFICATION does NOT enter this formula. Geometrically, all
// roads at an acute corner must satisfy the same minimum or the
// asphalt outline breaks. What classification influences:
//   - Defaults bootstrap. First-drawn road at a vertex is primary;
//     subsequent ones default to secondary.
//   - Connectivity defaults (primary's straight-through wins ties).
//   - Possibly aesthetic rendering (e.g., painted centerline only on
//     primary). Decide later.
//
// So in practice, the multi-primary case ("two primaries crossing
// perpendicular + secondaries at angles") collapses to: each road's
// setback is the max over its two corners' OE distances. Primaries
// usually live near π/2 to each other so their OE distances are
// ~hW (smaller than the W default), and they sit at the W floor.
// Secondaries that come in at acute angles drive their own setbacks
// up; they don't push the primaries.
//
// ---------------------------------------------------------------------------
// DEFAULT CONNECTIVITY RULES (overridable on the vertex)
// ---------------------------------------------------------------------------
//
// Given a vertex with N approaches, default connections per inbound lane:
//
//   1. Straight-through: inbound lane i on road R connects to the
//      same-index outbound lane on the road most directly opposite
//      (largest angle from R's centerline, modulo π).
//
//   2. Curb-to-curb turns: the outermost inbound lane (highest index)
//      connects to the outermost outbound lane of each adjacent road
//      (the right-turn and left-turn nearest to the curb).
//
//   3. Cross-intersection turns: the innermost inbound lane (index 0)
//      connects to the innermost outbound lane on the "opposite-left"
//      direction — i.e. the left turn across the intersection.
//
// All three rules generate LaneConnection entries; the
// `connectivityOverrides` on the vertex replace matching defaults
// (by `from` LaneRef equality).

// ---------------------------------------------------------------------------
// Open questions worth tagging now so we don't forget
// ---------------------------------------------------------------------------
//
// Q1. Should `RoadProfile.id` (which currently exists on the cross-
//     section Road) be removed, since the network's `NetworkRoad.id`
//     is now the canonical identifier? Probably yes — collapse them.
//
// Q2. How do we handle a vertex with exactly 2 roads at near-180°
//     angles? That's not really an intersection — it's a "joint" between
//     two roads with potentially different cross-sections. Worth treating
//     as a special case (no setback, just stitch the two profiles).
//
// Q3. RESOLVED — see SETBACK RULES above. Classification doesn't enter
//     the geometric formula; the formula is just max-over-neighbors of
//     the per-corner OE distance, floored at W. Classification is a
//     defaults / connectivity / aesthetics concept, not a setback one.
//
// Q4. Connectivity defaults assume drive-side (right vs left). Rule #3
//     says "opposite-left" but in LHD networks the natural cross-turn
//     is opposite-right. Need to fold driveSide into the resolver.
//
// Q5. The θ = π "joint" case is degenerate in the formula above
//     (sin θ = 0). Geometrically, the outer edges are parallel and
//     never intersect, so there's no fillet and no setback is needed.
//     Special-case it: a vertex with exactly 2 roads at bearings
//     differing by π skips fillets entirely and just stitches the two
//     profiles together at the vertex. If profiles differ (e.g.,
//     lane-drop), that stitching is a separate problem.
//
// Q6. Dead-ends. A vertex with 1 road has no neighbors and no corners.
//     The setback default of W still applies (so the cul-de-sac cap
//     starts W from the vertex), but the cap geometry is its own
//     shape — probably a semicircle of radius W/2 + something, or
//     a pair of bezier quadrants meeting at the apex behind the
//     vertex. Open: half-circle vs flattened cap vs configurable.
