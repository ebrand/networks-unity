import {
  Checkbox,
  Divider,
  Group,
  NumberInput,
  Radio,
  Stack,
  Title,
} from "@mantine/core";
import type { DriveSide, Lane, Road } from "../model/types";

interface Props {
  road: Road;
  driveSide: DriveSide;
  onChange: (next: Road) => void;
  onDriveSideChange: (next: DriveSide) => void;
}

let laneIdCounter = 1000;
function newLane(width = 4): Lane {
  return { id: `lane-${laneIdCounter++}`, width };
}

export function RoadConfigForm({
  road,
  driveSide,
  onChange,
  onDriveSideChange,
}: Props) {
  const abCount = road.ab.lanes.length;
  const baCount = road.ba.lanes.length;
  const oneWay = abCount === 0 || baCount === 0;
  const abMinCount = baCount === 0 ? 1 : 0;
  const baMinCount = abCount === 0 ? 1 : 0;

  function setLaneCount(direction: "ab" | "ba", count: number) {
    const side = road[direction];
    const otherCount = direction === "ab" ? baCount : abCount;
    const safeCount = Math.max(otherCount === 0 ? 1 : 0, count);
    const lanes = [...side.lanes];
    while (lanes.length < safeCount) lanes.push(newLane());
    while (lanes.length > safeCount) lanes.pop();

    const newAbCount = direction === "ab" ? safeCount : abCount;
    const newBaCount = direction === "ba" ? safeCount : baCount;
    const becomesOneWay = newAbCount === 0 || newBaCount === 0;

    onChange({
      ...road,
      [direction]: { lanes },
      // Both median and turn lane are two-way-only; strip whichever is
      // set when the road becomes one-way.
      median: becomesOneWay ? undefined : road.median,
      turnLane: becomesOneWay ? undefined : road.turnLane,
    });
  }

  function setLaneWidth(
    direction: "ab" | "ba",
    index: number,
    width: number,
  ) {
    const side = road[direction];
    const lanes = side.lanes.map((l, i) =>
      i === index ? { ...l, width } : l,
    );
    onChange({ ...road, [direction]: { lanes } });
  }

  function toggleMedian(on: boolean) {
    if (on && oneWay) return;
    onChange({
      ...road,
      // Mutually exclusive with turnLane: turning median on clears turnLane.
      median: on ? { width: road.median?.width ?? 1 } : undefined,
      turnLane: on ? undefined : road.turnLane,
    });
  }

  function setMedianWidth(width: number) {
    if (!road.median) return;
    onChange({ ...road, median: { width } });
  }

  function toggleTurnLane(on: boolean) {
    if (on && oneWay) return;
    onChange({
      ...road,
      // Mutually exclusive with median: turning turnLane on clears median.
      turnLane: on ? { width: road.turnLane?.width ?? 6 } : undefined,
      median: on ? undefined : road.median,
    });
  }

  function setTurnLaneWidth(width: number) {
    if (!road.turnLane) return;
    onChange({ ...road, turnLane: { width } });
  }

  function setShoulderWidth(side: "AB" | "BA", width: number) {
    if (side === "AB") onChange({ ...road, shoulderAB: { width } });
    else onChange({ ...road, shoulderBA: { width } });
  }

  return (
    <Stack gap="lg">
      <section>
        <SectionTitle>Drive side</SectionTitle>
        <Radio.Group
          value={driveSide}
          onChange={(v) => onDriveSideChange(v as DriveSide)}
        >
          <Stack gap="xs" mt="xs">
            <Radio
              value="right"
              label="Right-hand drive (AB on right of centerline)"
            />
            <Radio
              value="left"
              label="Left-hand drive (AB on left of centerline)"
            />
          </Stack>
        </Radio.Group>
      </section>

      <Divider />

      <section>
        <SectionTitle>AB lanes (A → B)</SectionTitle>
        <NumberInput
          label="Count"
          min={abMinCount}
          max={6}
          value={abCount}
          onChange={(v) => setLaneCount("ab", typeof v === "number" ? v : 0)}
          allowDecimal={false}
          description={
            abMinCount === 1 ? "BA = 0, so AB must be ≥ 1" : undefined
          }
        />
        <Stack gap="xs" mt="sm" pl="md">
          {road.ab.lanes.map((lane, i) => (
            <NumberInput
              key={lane.id}
              label={`lanesAB[${i}] width`}
              min={1}
              step={0.1}
              decimalScale={2}
              fixedDecimalScale
              suffix=" m"
              value={lane.width}
              onChange={(v) =>
                setLaneWidth("ab", i, typeof v === "number" ? v : 0)
              }
            />
          ))}
        </Stack>
      </section>

      <Divider />

      <section>
        <SectionTitle>BA lanes (B → A)</SectionTitle>
        <NumberInput
          label="Count"
          min={baMinCount}
          max={6}
          value={baCount}
          onChange={(v) => setLaneCount("ba", typeof v === "number" ? v : 0)}
          allowDecimal={false}
          description={
            baMinCount === 1 ? "AB = 0, so BA must be ≥ 1" : undefined
          }
        />
        <Stack gap="xs" mt="sm" pl="md">
          {road.ba.lanes.map((lane, i) => (
            <NumberInput
              key={lane.id}
              label={`lanesBA[${i}] width`}
              min={1}
              step={0.1}
              decimalScale={2}
              fixedDecimalScale
              suffix=" m"
              value={lane.width}
              onChange={(v) =>
                setLaneWidth("ba", i, typeof v === "number" ? v : 0)
              }
            />
          ))}
        </Stack>
      </section>

      <Divider />

      <section>
        <SectionTitle>Center strip</SectionTitle>
        <Checkbox
          label="Has median"
          checked={!!road.median}
          disabled={oneWay}
          onChange={(e) => toggleMedian(e.currentTarget.checked)}
          description={
            oneWay
              ? "Disabled on one-way roads"
              : road.turnLane
              ? "Enabling clears the turn lane (mutually exclusive)"
              : undefined
          }
        />
        {road.median && (
          <NumberInput
            mt="sm"
            label="Median width"
            min={0.1}
            step={0.1}
            decimalScale={2}
            fixedDecimalScale
            suffix=" m"
            value={road.median.width}
            onChange={(v) =>
              setMedianWidth(typeof v === "number" ? v : 0)
            }
          />
        )}
        <Checkbox
          mt="sm"
          label="Has turn lane (TWLTL)"
          checked={!!road.turnLane}
          disabled={oneWay}
          onChange={(e) => toggleTurnLane(e.currentTarget.checked)}
          description={
            oneWay
              ? "Disabled on one-way roads"
              : road.median
              ? "Enabling clears the median (mutually exclusive)"
              : "Drivable center lane for left turns / curb cuts"
          }
        />
        {road.turnLane && (
          <NumberInput
            mt="sm"
            label="Turn lane width"
            min={0.1}
            step={0.1}
            decimalScale={2}
            fixedDecimalScale
            suffix=" m"
            value={road.turnLane.width}
            onChange={(v) =>
              setTurnLaneWidth(typeof v === "number" ? v : 0)
            }
          />
        )}
      </section>

      <Divider />

      <section>
        <SectionTitle>Shoulders</SectionTitle>
        <Group grow align="flex-start">
          <NumberInput
            label="AB shoulder"
            min={0}
            step={0.1}
            decimalScale={2}
            fixedDecimalScale
            suffix=" m"
            value={road.shoulderAB.width}
            onChange={(v) =>
              setShoulderWidth("AB", typeof v === "number" ? v : 0)
            }
          />
          <NumberInput
            label="BA shoulder"
            min={0}
            step={0.1}
            decimalScale={2}
            fixedDecimalScale
            suffix=" m"
            value={road.shoulderBA.width}
            onChange={(v) =>
              setShoulderWidth("BA", typeof v === "number" ? v : 0)
            }
          />
        </Group>
      </section>
    </Stack>
  );
}

function SectionTitle({ children }: { children: React.ReactNode }) {
  return (
    <Title order={6} c="dimmed" tt="uppercase" mb="xs" fz="xs" fw={600}>
      {children}
    </Title>
  );
}
