import type { DriveSide, Road } from "../model/types";
import {
  buildRoadGeometry,
  spatialOffsetSign,
  type RoadGeometry,
} from "../model/geometry";

interface Props {
  road: Road;
  driveSide: DriveSide;
  showCenterline: boolean;
}

// Visual constants, in meters (the SVG viewBox unit).
const ROAD_LENGTH_M = 30;
const PADDING_LEFT_M = 8;   // width-label column + A vertex
const PADDING_RIGHT_M = 5;  // B vertex
const PADDING_VERT_M = 1.5;
const PX_PER_M = 22;

// Surface palette.
const COLOR_BORDER = "#000";
const COLOR_SHOULDER = "#e6e6e6";
const COLOR_ASPHALT = "#c0c0c0";
const COLOR_MEDIAN = "#888";
const COLOR_LANE_DIVIDER = "#ffffff";
const COLOR_ARROW = "#ffffff";
const COLOR_YELLOW = "#f4d030";
const COLOR_TICK = "#888";
const COLOR_AXIS = "#666";
const COLOR_LABEL = "#333";

// Strokes (meters).
const STROKE_BORDER = 0.35;
const STROKE_LANE_DIVIDER = 0.18;
const STROKE_YELLOW = 0.16;
const STROKE_TICK = 0.05;
const STROKE_AXIS = 0.06;
const STROKE_VERTEX = 0.1;

// Type (meters).
const FONT_SIZE = 0.65;
const VERTEX_FONT = 0.95;
const VERTEX_R = 1.1;

function widthLabel(w: number): string {
  const s = w.toFixed(1);
  return (s.endsWith(".0") ? s.slice(0, -2) : s) + "m";
}

interface Strip {
  topY: number;
  bottomY: number;
  label: string;
  fill: string;
  kind: "shoulder" | "lane" | "median" | "turnLane";
  laneDirection?: "AB" | "BA";
}

export function RoadVisualizer({ road, driveSide, showCenterline }: Props) {
  const ax = 0;
  const bx = ROAD_LENGTH_M;
  const cy = 0;

  let geo: RoadGeometry;
  try {
    geo = buildRoadGeometry(road, driveSide, {
      centerlineA: { x: ax, y: cy },
      centerlineB: { x: bx, y: cy },
    });
  } catch (e) {
    return <div style={{ color: "crimson" }}>{(e as Error).message}</div>;
  }

  // Flatten the geometry into stacking-order strips. Each strip is a
  // horizontal band running from x=ax to x=bx; sorting by topY gives
  // top-to-bottom render order regardless of which side hosts AB/BA.
  const strips: Strip[] = [];

  function pushLaneStrips(direction: "AB" | "BA") {
    const lanesGeo = direction === "AB" ? geo.ab : geo.ba;
    const lanes = direction === "AB" ? road.ab.lanes : road.ba.lanes;
    for (let i = 0; i < lanesGeo.length; i++) {
      const g = lanesGeo[i];
      strips.push({
        topY: Math.min(g.primary.y, g.origin.y),
        bottomY: Math.max(g.primary.y, g.origin.y),
        label: widthLabel(lanes[i].width),
        fill: COLOR_ASPHALT,
        kind: "lane",
        laneDirection: direction,
      });
    }
  }
  function pushShoulder(direction: "AB" | "BA") {
    const g = direction === "AB" ? geo.shoulderAB : geo.shoulderBA;
    const w = direction === "AB" ? road.shoulderAB.width : road.shoulderBA.width;
    strips.push({
      topY: Math.min(g.primary.y, g.origin.y),
      bottomY: Math.max(g.primary.y, g.origin.y),
      label: widthLabel(w),
      fill: COLOR_SHOULDER,
      kind: "shoulder",
    });
  }
  pushLaneStrips("AB");
  pushLaneStrips("BA");
  pushShoulder("AB");
  pushShoulder("BA");
  if (geo.median && road.median) {
    const g = geo.median;
    strips.push({
      topY: Math.min(g.primary.y, g.origin.y),
      bottomY: Math.max(g.primary.y, g.origin.y),
      label: widthLabel(road.median.width),
      fill: COLOR_MEDIAN,
      kind: "median",
    });
  }
  if (geo.turnLane && road.turnLane) {
    const g = geo.turnLane;
    strips.push({
      topY: Math.min(g.primary.y, g.origin.y),
      bottomY: Math.max(g.primary.y, g.origin.y),
      label: widthLabel(road.turnLane.width),
      fill: COLOR_ASPHALT,
      kind: "turnLane",
    });
  }
  strips.sort((a, b) => a.topY - b.topY);

  const allMinY = strips.length ? strips[0].topY : -1;
  const allMaxY = strips.length ? strips[strips.length - 1].bottomY : 1;

  const minX = ax - PADDING_LEFT_M;
  const maxX = bx + PADDING_RIGHT_M;
  const minY = allMinY - PADDING_VERT_M;
  const maxY = allMaxY + PADDING_VERT_M;
  const vbW = maxX - minX;
  const vbH = maxY - minY;

  // Lane-divider line endpoints (insets a bit so they don't touch the
  // road ends — matches the in-game striped look).
  const stripeInsetX = 1.5;

  return (
    <div className="visualizer">
      <svg
        width={vbW * PX_PER_M}
        height={vbH * PX_PER_M}
        viewBox={`${minX} ${minY} ${vbW} ${vbH}`}
        style={{ background: "white" }}
      >
        {/* Strip fills */}
        {strips.map((s, i) => (
          <rect
            key={`fill-${i}`}
            x={ax}
            y={s.topY}
            width={bx - ax}
            height={s.bottomY - s.topY}
            fill={s.fill}
          />
        ))}

        {/* Lane dividers (white dashed) between same-direction lanes */}
        {(["AB", "BA"] as const).map((dir) => {
          const lanesGeo = dir === "AB" ? geo.ab : geo.ba;
          const sign = spatialOffsetSign(dir, driveSide);
          const elems: React.ReactNode[] = [];
          for (let i = 0; i < lanesGeo.length - 1; i++) {
            const lane = lanesGeo[i];
            const boundY =
              sign === 1
                ? Math.max(lane.primary.y, lane.origin.y)
                : Math.min(lane.primary.y, lane.origin.y);
            elems.push(
              <line
                key={`${dir}-div-${i}`}
                x1={ax + stripeInsetX}
                y1={boundY}
                x2={bx - stripeInsetX}
                y2={boundY}
                stroke={COLOR_LANE_DIVIDER}
                strokeWidth={STROKE_LANE_DIVIDER}
                strokeDasharray="2 1.4"
                strokeLinecap="butt"
              />,
            );
          }
          return <g key={`div-${dir}`}>{elems}</g>;
        })}

        {/* Undivided two-way road — double solid yellow centerline */}
        {!geo.median && !geo.turnLane && geo.ab.length > 0 && geo.ba.length > 0 && (() => {
          const halfGap = 0.12;
          return (
            <g key="double-yellow">
              <line x1={ax} y1={cy - halfGap} x2={bx} y2={cy - halfGap}
                stroke={COLOR_YELLOW} strokeWidth={STROKE_YELLOW} />
              <line x1={ax} y1={cy + halfGap} x2={bx} y2={cy + halfGap}
                stroke={COLOR_YELLOW} strokeWidth={STROKE_YELLOW} />
            </g>
          );
        })()}

        {/* TWLTL yellow markings — solid on edge, dashed just inside */}
        {geo.turnLane && (() => {
          const g = geo.turnLane;
          const topY = Math.min(g.primary.y, g.origin.y);
          const botY = Math.max(g.primary.y, g.origin.y);
          const inset = 0.3;
          return (
            <g key="twltl">
              <line x1={ax} y1={topY} x2={bx} y2={topY}
                stroke={COLOR_YELLOW} strokeWidth={STROKE_YELLOW} />
              <line x1={ax} y1={topY + inset} x2={bx} y2={topY + inset}
                stroke={COLOR_YELLOW} strokeWidth={STROKE_YELLOW}
                strokeDasharray="1.6 1" />
              <line x1={ax} y1={botY} x2={bx} y2={botY}
                stroke={COLOR_YELLOW} strokeWidth={STROKE_YELLOW} />
              <line x1={ax} y1={botY - inset} x2={bx} y2={botY - inset}
                stroke={COLOR_YELLOW} strokeWidth={STROKE_YELLOW}
                strokeDasharray="1.6 1" />
            </g>
          );
        })()}

        {/* Directional arrows per lane */}
        {(["AB", "BA"] as const).map((dir) => {
          const lanesGeo = dir === "AB" ? geo.ab : geo.ba;
          const out: React.ReactNode[] = [];
          for (let i = 0; i < lanesGeo.length; i++) {
            const lg = lanesGeo[i];
            const yMid = (lg.primary.y + lg.origin.y) / 2;
            for (const fx of [0.3, 0.7]) {
              const x = ax + (bx - ax) * fx;
              out.push(arrowGlyph(x, yMid, dir, `arrow-${dir}-${i}-${fx}`));
            }
          }
          return <g key={`arrows-${dir}`}>{out}</g>;
        })}

        {/* Outer road-edge borders (top + bottom black bars) */}
        <line x1={ax} y1={allMinY} x2={bx} y2={allMinY}
          stroke={COLOR_BORDER} strokeWidth={STROKE_BORDER} />
        <line x1={ax} y1={allMaxY} x2={bx} y2={allMaxY}
          stroke={COLOR_BORDER} strokeWidth={STROKE_BORDER} />

        {/* Width labels + ticks on the left margin */}
        <g>
          {strips.map((s, i) => {
            const tickX = ax - 2.5;
            const railX = ax - 1.8;
            const labelX = ax - 3.2;
            return (
              <g key={`label-${i}`}>
                <line x1={tickX} y1={s.topY} x2={ax - 0.3} y2={s.topY}
                  stroke={COLOR_TICK} strokeWidth={STROKE_TICK} />
                <line x1={tickX} y1={s.bottomY} x2={ax - 0.3} y2={s.bottomY}
                  stroke={COLOR_TICK} strokeWidth={STROKE_TICK} />
                <line x1={railX} y1={s.topY} x2={railX} y2={s.bottomY}
                  stroke={COLOR_TICK} strokeWidth={STROKE_TICK} />
                <text
                  x={labelX}
                  y={(s.topY + s.bottomY) / 2 + FONT_SIZE * 0.35}
                  fontFamily="ui-monospace, Menlo, monospace"
                  fontSize={FONT_SIZE}
                  fill={COLOR_LABEL}
                  textAnchor="end"
                >
                  {s.label}
                </text>
              </g>
            );
          })}
        </g>

        {/* Centerline (A→B axis) — circles sit at the viewBox margins so
            they don't overlap the width-label column on the left or the
            road on the right. */}
        {showCenterline && (() => {
          const aCx = minX + VERTEX_R + 0.3;
          const bCx = maxX - VERTEX_R - 0.3;
          return (
            <g>
              <line
                x1={aCx}
                y1={cy}
                x2={bCx}
                y2={cy}
                stroke={COLOR_AXIS}
                strokeWidth={STROKE_AXIS}
                strokeDasharray="0.9 0.6"
              />
              <circle cx={aCx} cy={cy} r={VERTEX_R} fill="white"
                stroke={COLOR_AXIS} strokeWidth={STROKE_VERTEX} />
              <text x={aCx} y={cy + VERTEX_FONT * 0.35}
                fontFamily="ui-monospace, Menlo, monospace"
                fontSize={VERTEX_FONT} fill={COLOR_AXIS}
                textAnchor="middle">A</text>
              <circle cx={bCx} cy={cy} r={VERTEX_R} fill="white"
                stroke={COLOR_AXIS} strokeWidth={STROKE_VERTEX} />
              <text x={bCx} y={cy + VERTEX_FONT * 0.35}
                fontFamily="ui-monospace, Menlo, monospace"
                fontSize={VERTEX_FONT} fill={COLOR_AXIS}
                textAnchor="middle">B</text>
            </g>
          );
        })()}
      </svg>
    </div>
  );
}

function arrowGlyph(
  cx: number,
  cy: number,
  dir: "AB" | "BA",
  key: string,
) {
  const half = 0.85;
  const pts =
    dir === "AB"
      ? `${cx - half},${cy - half} ${cx + half},${cy} ${cx - half},${cy + half}`
      : `${cx + half},${cy - half} ${cx - half},${cy} ${cx + half},${cy + half}`;
  return <polygon key={key} points={pts} fill={COLOR_ARROW} />;
}
