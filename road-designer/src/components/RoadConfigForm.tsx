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
      median: becomesOneWay ? undefined : road.median,
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
      median: on ? { width: road.median?.width ?? 1 } : undefined,
    });
  }

  function setMedianWidth(width: number) {
    if (!road.median) return;
    onChange({ ...road, median: { width } });
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
        <SectionTitle>Median</SectionTitle>
        <Checkbox
          label="Has median"
          checked={!!road.median}
          disabled={oneWay}
          onChange={(e) => toggleMedian(e.currentTarget.checked)}
          description={
            oneWay ? "Disabled on one-way roads" : undefined
          }
        />
        {road.median && (
          <NumberInput
            mt="sm"
            label="Width"
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
