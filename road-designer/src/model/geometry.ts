import type {
  Direction,
  DriveSide,
  LaneGeometry,
  Point2D,
  Road,
} from "./types";

// Resolves which physical side of the centerline a logical direction
// occupies. The renderer's coordinate convention: looking down from above
// with vertex A on the left and vertex B on the right, +y points DOWN
// (standard SVG). So "above the centerline" is y < centerline.y, and
// "below" is y > centerline.y.
//
// RHD: if you're traveling A→B (left to right), you stay on the right,
// which is +y (below centerline in SVG terms). So AB → below.
// LHD: flipped. AB → above.
export function spatialOffsetSign(
  direction: Direction,
  driveSide: DriveSide,
): 1 | -1 {
  if (driveSide === "right") return direction === "AB" ? 1 : -1;
  return direction === "AB" ? -1 : 1;
}

export interface RoadGeometry {
  // Endpoints of the road's centerline, in render coordinates (meters).
  centerlineA: Point2D;
  centerlineB: Point2D;
  // Per-side resolved lane geometries, in render order from innermost to
  // outermost. Index here matches `side.lanes[i]`.
  ab: LaneGeometry[];
  ba: LaneGeometry[];
  // Median rectangle if present.
  median?: LaneGeometry;
  // Shoulder rectangles, one per side.
  shoulderAB: LaneGeometry;
  shoulderBA: LaneGeometry;
  // Signed outermost y of each side (relative to centerline). In RHD,
  // outerYAB > 0 and outerYBA < 0; in LHD they swap. Use the bbox below
  // for viewBox sizing — it's drive-side-agnostic.
  outerYAB: number;
  outerYBA: number;
  // Tight bounding box that contains every rectangle (shoulders, lanes,
  // median). The visualizer should pad this for labels.
  bbox: { minX: number; maxX: number; minY: number; maxY: number };
}

// Builds a lane rectangle aligned to a horizontal centerline (A on the
// left at x = ax, B on the right at x = bx). `innerEdgeY` is the y-coord
// of the edge closer to the road centerline; `width` extends OUTWARD from
// there in the direction given by `outwardSign` (+1 = +y, -1 = -y).
//
// Well-known points use world-aligned naming (clockwise with A on left):
//   primary (top-left) ───── secondary (top-right)
//        │                         │
//        A (left-mid)        B (right-mid)
//        │                         │
//   origin (bottom-left) ── tertiary (bottom-right)
function buildRect(
  ax: number,
  bx: number,
  innerEdgeY: number,
  width: number,
  outwardSign: 1 | -1,
): LaneGeometry {
  const outerEdgeY = innerEdgeY + outwardSign * width;
  const topY = Math.min(innerEdgeY, outerEdgeY);
  const bottomY = Math.max(innerEdgeY, outerEdgeY);
  const midY = (topY + bottomY) / 2;
  return {
    origin: { x: ax, y: bottomY },
    A: { x: ax, y: midY },
    primary: { x: ax, y: topY },
    secondary: { x: bx, y: topY },
    B: { x: bx, y: midY },
    tertiary: { x: bx, y: bottomY },
  };
}

// Convenience accessors that hide the AB/BA asymmetry. Use these from
// rendering code that wants "the edge touching the centerline" without
// caring which side the lane is on.
export function centerlineEdge(
  lane: LaneGeometry,
  direction: Direction,
  driveSide: DriveSide,
): { left: Point2D; right: Point2D } {
  const sign = spatialOffsetSign(direction, driveSide);
  // Inner edge is the one closer to the centerline. With +y outward, the
  // inner edge is the top (smaller y). With -y outward, inner is bottom.
  if (sign === 1) {
    return { left: lane.primary, right: lane.secondary };
  }
  return { left: lane.origin, right: lane.tertiary };
}

export function outerEdge(
  lane: LaneGeometry,
  direction: Direction,
  driveSide: DriveSide,
): { left: Point2D; right: Point2D } {
  const sign = spatialOffsetSign(direction, driveSide);
  if (sign === 1) {
    return { left: lane.origin, right: lane.tertiary };
  }
  return { left: lane.primary, right: lane.secondary };
}

export interface BuildRoadGeometryOptions {
  // Centerline endpoints in render coordinates.
  centerlineA: Point2D;
  centerlineB: Point2D;
}

function shiftLaneY(g: LaneGeometry, dy: number): LaneGeometry {
  return {
    origin: { x: g.origin.x, y: g.origin.y + dy },
    A: { x: g.A.x, y: g.A.y + dy },
    primary: { x: g.primary.x, y: g.primary.y + dy },
    secondary: { x: g.secondary.x, y: g.secondary.y + dy },
    B: { x: g.B.x, y: g.B.y + dy },
    tertiary: { x: g.tertiary.x, y: g.tertiary.y + dy },
  };
}

export function isOneWay(road: Road): boolean {
  return road.ab.lanes.length === 0 || road.ba.lanes.length === 0;
}

// Builds the full geometry for a road, laying out lanes from the
// centerline outward on each side. Currently assumes a horizontal
// centerline (y = centerlineA.y === centerlineB.y); a future revision
// can rotate the result for arbitrary orientations.
//
// For two-way roads, the centerline is at the boundary between AB and BA
// flows (one side starts where the other ends). For one-way roads, the
// "centerline" as a graph edge runs through the middle of the asphalt
// (where the single traffic flow goes), so we shift all rendered geometry
// vertically to put the asphalt midpoint at the input centerline y.
export function buildRoadGeometry(
  road: Road,
  driveSide: DriveSide,
  opts: BuildRoadGeometryOptions,
): RoadGeometry {
  const { centerlineA, centerlineB } = opts;
  if (centerlineA.y !== centerlineB.y) {
    throw new Error(
      "buildRoadGeometry currently assumes a horizontal centerline",
    );
  }
  const ax = centerlineA.x;
  const bx = centerlineB.x;
  const cy = centerlineA.y;

  const medianHalf = road.median ? road.median.width / 2 : 0;

  function buildSide(direction: Direction): {
    lanes: LaneGeometry[];
    shoulder: LaneGeometry;
    outerY: number;
  } {
    const sign = spatialOffsetSign(direction, driveSide);
    const side = direction === "AB" ? road.ab : road.ba;
    const shoulder = direction === "AB" ? road.shoulderAB : road.shoulderBA;

    // Start at the inner edge of this side (centerline + median half).
    let cursor = cy + sign * medianHalf;
    const lanes: LaneGeometry[] = [];
    for (const lane of side.lanes) {
      lanes.push(buildRect(ax, bx, cursor, lane.width, sign));
      cursor += sign * lane.width;
    }
    const shoulderRect = buildRect(ax, bx, cursor, shoulder.width, sign);
    cursor += sign * shoulder.width;
    return { lanes, shoulder: shoulderRect, outerY: cursor };
  }

  let abSide = buildSide("AB");
  let baSide = buildSide("BA");

  // One-way roads have no median (enforced at the form layer); we don't
  // shift two-way layouts even if asymmetric, because the centerline's
  // semantic position is the AB/BA boundary, not the visual middle.
  if (isOneWay(road)) {
    const asphaltMid = (abSide.outerY + baSide.outerY) / 2;
    const dy = cy - asphaltMid;
    if (dy !== 0) {
      abSide = {
        lanes: abSide.lanes.map((l) => shiftLaneY(l, dy)),
        shoulder: shiftLaneY(abSide.shoulder, dy),
        outerY: abSide.outerY + dy,
      };
      baSide = {
        lanes: baSide.lanes.map((l) => shiftLaneY(l, dy)),
        shoulder: shiftLaneY(baSide.shoulder, dy),
        outerY: baSide.outerY + dy,
      };
    }
  }

  let median: LaneGeometry | undefined;
  if (road.median) {
    median = buildRect(ax, bx, cy - medianHalf, road.median.width, 1);
  }

  const minY = Math.min(abSide.outerY, baSide.outerY);
  const maxY = Math.max(abSide.outerY, baSide.outerY);

  return {
    centerlineA,
    centerlineB,
    ab: abSide.lanes,
    ba: baSide.lanes,
    median,
    shoulderAB: abSide.shoulder,
    shoulderBA: baSide.shoulder,
    outerYAB: abSide.outerY,
    outerYBA: baSide.outerY,
    bbox: { minX: Math.min(ax, bx), maxX: Math.max(ax, bx), minY, maxY },
  };
}
