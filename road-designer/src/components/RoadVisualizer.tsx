import type { DriveSide, Road } from "../model/types";
import {
  buildRoadGeometry,
  type RoadGeometry,
} from "../model/geometry";
import type { LaneGeometry, Point2D } from "../model/types";

// Identifies which strip the visualizer should label "in focus" — i.e.
// in bright blue with text labels at every well-known point. Other strips
// still render their crosshairs (faintly) but without text, to avoid the
// label collisions at shared edges.
export type FocusTarget =
  | { kind: "lane"; direction: "AB" | "BA"; index: number }
  | { kind: "shoulder"; direction: "AB" | "BA" }
  | { kind: "median" }
  | { kind: "none" };

interface Props {
  road: Road;
  driveSide: DriveSide;
  focus: FocusTarget;
}

const ROAD_LENGTH_M = 30; // length of the depicted road segment in meters
const PADDING_M = 6; // extra space outside the outermost shoulder for labels
const PX_PER_M = 18; // scale factor for rendering

// All visual sizes below are in METERS (the SVG viewBox unit). They are
// chosen to read well at PX_PER_M ≈ 18 — small enough not to swamp lane
// rectangles, large enough to remain legible.
const CROSS_R = 0.18;
const CROSS_TICK = 0.32;
const STROKE_THIN = 0.04;
const STROKE_LANE = 0.05;
const STROKE_CENTERLINE = 0.08;
const LABEL_FONT = 0.55;
const LABEL_GAP = 0.45;
const TITLE_FONT = 0.7;
const VERTEX_R = 1.0;
const VERTEX_FONT = 1.1;

function rectPath(g: LaneGeometry): string {
  return `M ${g.origin.x},${g.origin.y} L ${g.primary.x},${g.primary.y} L ${g.secondary.x},${g.secondary.y} L ${g.tertiary.x},${g.tertiary.y} Z`;
}

interface CrosshairProps {
  point: Point2D;
  label?: string;
  align?: "left" | "right";
  faded?: boolean;
}

function Crosshair({ point, label, align = "left", faded }: CrosshairProps) {
  const color = faded ? "#cccccc" : "#1a4fff";
  const textX =
    align === "left" ? point.x - LABEL_GAP : point.x + LABEL_GAP;
  return (
    <g>
      <circle
        cx={point.x}
        cy={point.y}
        r={CROSS_R}
        fill="white"
        stroke={color}
        strokeWidth={STROKE_THIN}
      />
      <line
        x1={point.x - CROSS_TICK}
        y1={point.y}
        x2={point.x + CROSS_TICK}
        y2={point.y}
        stroke={color}
        strokeWidth={STROKE_THIN}
      />
      <line
        x1={point.x}
        y1={point.y - CROSS_TICK}
        x2={point.x}
        y2={point.y + CROSS_TICK}
        stroke={color}
        strokeWidth={STROKE_THIN}
      />
      {label && (
        <text
          x={textX}
          y={point.y + LABEL_FONT * 0.35}
          fontFamily="ui-monospace, Menlo, monospace"
          fontSize={LABEL_FONT}
          fill={color}
          textAnchor={align === "left" ? "end" : "start"}
        >
          {label}
        </text>
      )}
    </g>
  );
}

interface LaneLabelsProps {
  lane: LaneGeometry;
  prefix: string;
  // When true, the rectangle's points get bright-blue crosshairs and
  // full text labels. When false, only faded crosshairs (no text).
  focused: boolean;
}

function LaneLabels({ lane, prefix, focused }: LaneLabelsProps) {
  const label = (suffix: string) => (focused ? `${prefix} ${suffix}` : undefined);
  return (
    <g>
      <Crosshair point={lane.primary} label={label("primary")} align="left" faded={!focused} />
      <Crosshair point={lane.A} label={label("A")} align="left" faded={!focused} />
      <Crosshair point={lane.origin} label={label("origin")} align="left" faded={!focused} />
      <Crosshair point={lane.secondary} label={label("secondary")} align="right" faded={!focused} />
      <Crosshair point={lane.B} label={label("B")} align="right" faded={!focused} />
      <Crosshair point={lane.tertiary} label={label("tertiary")} align="right" faded={!focused} />
    </g>
  );
}

function isFocused(target: FocusTarget, kind: "lane" | "shoulder" | "median", direction?: "AB" | "BA", index?: number): boolean {
  if (target.kind !== kind) return false;
  if (kind === "lane" && target.kind === "lane") {
    return target.direction === direction && target.index === index;
  }
  if (kind === "shoulder" && target.kind === "shoulder") {
    return target.direction === direction;
  }
  return kind === "median" && target.kind === "median";
}

export function RoadVisualizer({ road, driveSide, focus }: Props) {
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

  // viewBox in meters, derived from the geometry's signed bbox so it
  // fits any combination of lane counts (including one-way roads where
  // one side has zero lanes). Padding adds room for labels that extend
  // beyond the road rectangles.
  const padX = 16; // room for the labels that hang off the A and B ends
  const minY = geo.bbox.minY - PADDING_M;
  const maxY = geo.bbox.maxY + PADDING_M;
  const minX = geo.bbox.minX - padX;
  const maxX = geo.bbox.maxX + padX;
  const vbW = maxX - minX;
  const vbH = maxY - minY;

  return (
    <div className="visualizer">
      <svg
        width={vbW * PX_PER_M}
        height={vbH * PX_PER_M}
        viewBox={`${minX} ${minY} ${vbW} ${vbH}`}
        style={{ background: "white", border: "1px solid #eee" }}
      >
        {/* Flip y so that, in our model, +y is "down on the page" matches
            the SVG default. We pass geometry in standard render coords
            (centerline y=0, AB direction +y in RHD), so no flip is needed. */}

        {/* Shoulders */}
        <path
          d={rectPath(geo.shoulderAB)}
          fill="#f3f3f3"
          stroke="#cccccc"
          strokeWidth={STROKE_LANE}
        />
        <path
          d={rectPath(geo.shoulderBA)}
          fill="#f3f3f3"
          stroke="#cccccc"
          strokeWidth={STROKE_LANE}
        />

        {/* Median */}
        {geo.median && (
          <path
            d={rectPath(geo.median)}
            fill="#fff5cc"
            stroke="#d4a017"
            strokeWidth={STROKE_LANE}
          />
        )}

        {/* Lanes */}
        {geo.ab.map((lane, i) => (
          <path
            key={`ab-${i}`}
            d={rectPath(lane)}
            fill={i === 0 ? "#e7efff" : "#f4f8ff"}
            stroke="#8899bb"
            strokeWidth={STROKE_LANE}
          />
        ))}
        {geo.ba.map((lane, i) => (
          <path
            key={`ba-${i}`}
            d={rectPath(lane)}
            fill={i === 0 ? "#e7efff" : "#f4f8ff"}
            stroke="#8899bb"
            strokeWidth={STROKE_LANE}
          />
        ))}

        {/* Road centerline */}
        <line
          x1={ax}
          y1={cy}
          x2={bx}
          y2={cy}
          stroke="#111111"
          strokeWidth={STROKE_CENTERLINE}
        />
        <text
          x={(ax + bx) / 2}
          y={cy - TITLE_FONT * 0.5}
          fontFamily="ui-monospace, Menlo, monospace"
          fontSize={TITLE_FONT}
          fill="#111111"
          textAnchor="middle"
        >
          road centerline
        </text>

        {/* Well-known points — non-focused strips render faint crosshairs
            only (no text), focused strip gets bright-blue labels. Order:
            faded first, then focused on top so labels aren't covered. */}
        {geo.ab.map((lane, i) => (
          <LaneLabels
            key={`ab-pts-${i}`}
            lane={lane}
            prefix={`lanesAB[${i}]`}
            focused={isFocused(focus, "lane", "AB", i)}
          />
        ))}
        {geo.ba.map((lane, i) => (
          <LaneLabels
            key={`ba-pts-${i}`}
            lane={lane}
            prefix={`lanesBA[${i}]`}
            focused={isFocused(focus, "lane", "BA", i)}
          />
        ))}
        <LaneLabels
          lane={geo.shoulderAB}
          prefix="shoulderAB"
          focused={isFocused(focus, "shoulder", "AB")}
        />
        <LaneLabels
          lane={geo.shoulderBA}
          prefix="shoulderBA"
          focused={isFocused(focus, "shoulder", "BA")}
        />
        {geo.median && (
          <LaneLabels
            lane={geo.median}
            prefix="median"
            focused={isFocused(focus, "median")}
          />
        )}

        {/* Vertex circles, last so they sit on top */}
        <circle
          cx={ax}
          cy={cy}
          r={VERTEX_R}
          fill="white"
          stroke="#111111"
          strokeWidth={STROKE_LANE * 1.5}
        />
        <text
          x={ax}
          y={cy + VERTEX_FONT * 0.35}
          fontFamily="ui-monospace, Menlo, monospace"
          fontSize={VERTEX_FONT}
          fill="#111111"
          textAnchor="middle"
        >
          A
        </text>
        <circle
          cx={bx}
          cy={cy}
          r={VERTEX_R}
          fill="white"
          stroke="#111111"
          strokeWidth={STROKE_LANE * 1.5}
        />
        <text
          x={bx}
          y={cy + VERTEX_FONT * 0.35}
          fontFamily="ui-monospace, Menlo, monospace"
          fontSize={VERTEX_FONT}
          fill="#111111"
          textAnchor="middle"
        >
          B
        </text>

        {/* Side labels — place each at the midpoint of its actual signed
            extent so the label sits inside the strip whether the side is
            above, below, or absent. If a side has 0-extent (no lanes, no
            shoulder), the label still renders at the inner edge — fine. */}
        <text
          x={minX + 1}
          y={geo.outerYAB / 2 + TITLE_FONT * 0.35}
          fontFamily="ui-monospace, Menlo, monospace"
          fontSize={TITLE_FONT}
          fill="#666666"
        >
          side AB
        </text>
        <text
          x={minX + 1}
          y={geo.outerYBA / 2 + TITLE_FONT * 0.35}
          fontFamily="ui-monospace, Menlo, monospace"
          fontSize={TITLE_FONT}
          fill="#666666"
        >
          side BA
        </text>
      </svg>
    </div>
  );
}
