// Logical direction along a road: A→B or B→A.
// This is direction of TRAVEL, not a physical side of the road.
export type Direction = "AB" | "BA";

// Drive-side convention. Determines which physical side of the centerline
// each direction's lanes occupy. Right-hand-drive (RHD) = "right" (US, EU);
// left-hand-drive (LHD) = "left" (UK, JP, AU).
export type DriveSide = "right" | "left";

// A lane is a rectangle in the road's local frame. It carries no spatial
// awareness — only its width and (eventually) attributes like surface,
// markings, allowed maneuvers, etc.
export interface Lane {
  id: string;
  width: number; // meters
}

// A side of the road carries 0..n lanes for a single travel direction.
// lanes[0] is innermost (closest to centerline); lanes grow outward.
export interface Side {
  lanes: Lane[];
}

// Shoulders sit on the outer edge of each side (away from centerline).
export interface Shoulder {
  width: number; // meters
}

// Optional median between the AB and BA sides, centered on the centerline.
export interface Median {
  width: number; // meters
}

// Optional two-way left-turn lane (TWLTL / "suicide lane") centered on
// the centerline. Drivable asphalt strip flanked by yellow markings —
// used for in-segment left turns and curb cuts. MUTUALLY EXCLUSIVE with
// `median` (a road has either a median, a turn lane, or neither).
// Only valid on two-way roads.
export interface TurnLane {
  width: number; // meters
}

// A road is an edge between two graph vertices (A and B). Geometry lives
// elsewhere — this is the cross-section definition. The sides are named
// by logical direction; their physical position is resolved at render time
// using the Network's driveSide.
export interface Road {
  id: string;
  ab: Side;
  ba: Side;
  median?: Median;
  turnLane?: TurnLane;
  shoulderAB: Shoulder;
  shoulderBA: Shoulder;
}

// Top-level network owns the drive-side convention. Flipping this single
// flag re-orients every road in the network without touching lane data.
export interface Network {
  driveSide: DriveSide;
  roads: Road[];
}

// The six well-known points on a lane's rectangle, in clockwise order
// when viewed from above with vertex A on the left and vertex B on the
// right. Naming is geometric (world-aligned), not travel-relative.
//
//   primary ──────────── secondary
//      │                     │
//      A                     B
//      │                     │
//    origin ─────────────── tertiary
//
// For BA lanes (which appear above the centerline in RHD), `origin` is the
// centerline-adjacent corner. For AB lanes (below the centerline in RHD),
// `primary` is the centerline-adjacent corner. Use the helper accessors
// in geometry.ts when you need "the centerline edge" without caring which
// side you're on.
export type WellKnownPointName =
  | "origin"
  | "A"
  | "primary"
  | "secondary"
  | "B"
  | "tertiary";

export interface Point2D {
  x: number;
  y: number;
}

export interface LaneGeometry {
  origin: Point2D;
  A: Point2D;
  primary: Point2D;
  secondary: Point2D;
  B: Point2D;
  tertiary: Point2D;
}
