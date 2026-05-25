// Live-tuning panel. Connects to Unity's TuningServer over WebSocket and
// renders one control per registered entry, grouped by category.
//
// Controls are intentionally minimal — Mantine Slider for floats (with
// a NumberInput for precise entry), ColorInput for colors, Switch for
// bools, three sliders for vector3. Adding a new type is a matter of
// adding a renderer below.

import { useMemo, useState } from "react";
import {
  Accordion,
  Alert,
  Badge,
  Box,
  ColorInput,
  Group,
  NumberInput,
  Select,
  Slider,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
  Button,
  ScrollArea,
} from "@mantine/core";
import type { TuningEntry } from "../tuning/types";
import { useTuningSocket } from "../tuning/useTuningSocket";
import { listConfigs } from "../storage/configs";

const DEFAULT_URL = "ws://localhost:8787/";

export function TuningPanel() {
  const { status, entries, setValue, lastError, reconnect } =
    useTuningSocket(DEFAULT_URL);

  const grouped = useMemo(() => groupByCategory(entries), [entries]);

  const [search, setSearch] = useState("");
  // User's manual open/close state. Null until they touch the accordion;
  // then it tracks their choices. Cleared back to null when search activates.
  const [userOpen, setUserOpen] = useState<string[] | null>(null);

  const filteredGroups = useMemo(() => {
    const needle = search.trim().toLowerCase();
    if (!needle) return grouped;
    return grouped
      .map(([cat, items]) => {
        const matches = items.filter(
          (e) =>
            e.label.toLowerCase().includes(needle) ||
            e.key.toLowerCase().includes(needle) ||
            cat.toLowerCase().includes(needle),
        );
        return [cat, matches] as [string, TuningEntry[]];
      })
      .filter(([, items]) => items.length > 0);
  }, [search, grouped]);

  const allVisibleCategories = useMemo(
    () => filteredGroups.map(([cat]) => cat),
    [filteredGroups],
  );

  // When a search is active, force every visible category open so the
  // user can see what matched. Otherwise honor their manual state,
  // defaulting to "all open" before they've touched anything.
  const effectiveOpen = search.trim()
    ? allVisibleCategories
    : (userOpen ?? allVisibleCategories);

  return (
    <Stack gap="md" h="100%">
      <Group justify="space-between" align="center">
        <Group gap="sm">
          <Title order={4}>Live tuning</Title>
          <StatusBadge status={status} />
        </Group>
        <Button size="xs" variant="light" onClick={reconnect}>
          Reconnect
        </Button>
      </Group>
      <Text size="xs" c="dimmed">
        Connected to {DEFAULT_URL}. Changes apply live in Unity.
      </Text>

      {lastError && (
        <Alert color="red" variant="light" title="Last error">
          {lastError}
        </Alert>
      )}

      {status !== "open" && (
        <Alert color="yellow" variant="light">
          Not connected. Make sure Unity is in Play mode and the
          TuningServer is listening on port 8787.
        </Alert>
      )}

      {entries.length === 0 && status === "open" && (
        <Alert color="blue" variant="light">
          Connected, but no tunables have been registered. Add a
          TuningSetup component to your scene.
        </Alert>
      )}

      {entries.length > 0 && (
        <Group gap="xs">
          <TextInput
            placeholder="Filter…"
            value={search}
            onChange={(e) => setSearch(e.currentTarget.value)}
            size="xs"
            style={{ flex: 1 }}
          />
          <Button
            size="xs"
            variant="subtle"
            onClick={() => setUserOpen(allVisibleCategories)}
          >
            Expand all
          </Button>
          <Button
            size="xs"
            variant="subtle"
            onClick={() => setUserOpen([])}
          >
            Collapse all
          </Button>
        </Group>
      )}

      <ScrollArea h="100%" type="auto" offsetScrollbars>
        <Accordion
          multiple
          value={effectiveOpen}
          onChange={(value) => {
            // Ignore user toggles while a search is active — the open
            // set is forced to "all visible" so we shouldn't overwrite
            // their pre-search choice.
            if (search.trim()) return;
            setUserOpen(value as string[]);
          }}
          variant="separated"
          chevronPosition="left"
        >
          {filteredGroups.map(([category, items]) => (
            <Accordion.Item key={category} value={category}>
              <Accordion.Control>
                <Group gap="xs">
                  <Text size="sm" fw={600}>
                    {category}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {items.length}
                  </Text>
                </Group>
              </Accordion.Control>
              <Accordion.Panel>
                <Stack gap="sm">
                  {items.map((e) => (
                    <EntryControl key={e.key} entry={e} onChange={setValue} />
                  ))}
                </Stack>
              </Accordion.Panel>
            </Accordion.Item>
          ))}
        </Accordion>
        {filteredGroups.length === 0 && search.trim() && (
          <Text size="xs" c="dimmed" ta="center" mt="md">
            No tunables match "{search}".
          </Text>
        )}
      </ScrollArea>
    </Stack>
  );
}

function StatusBadge({
  status,
}: {
  status: "connecting" | "open" | "closed" | "error";
}) {
  const color =
    status === "open"
      ? "green"
      : status === "connecting"
        ? "blue"
        : status === "error"
          ? "red"
          : "gray";
  return (
    <Badge color={color} variant="light" size="sm">
      {status}
    </Badge>
  );
}

function groupByCategory(entries: TuningEntry[]): Array<[string, TuningEntry[]]> {
  const map = new Map<string, TuningEntry[]>();
  for (const e of entries) {
    const arr = map.get(e.category) ?? [];
    arr.push(e);
    map.set(e.category, arr);
  }
  // Alphabetize by category name; entry order within each category
  // stays as Unity registered them.
  return Array.from(map.entries()).sort(([a], [b]) =>
    a.localeCompare(b, undefined, { sensitivity: "base" }),
  );
}

function EntryControl({
  entry,
  onChange,
}: {
  entry: TuningEntry;
  onChange: (key: string, value: unknown) => void;
}) {
  switch (entry.type) {
    case "float":
      return <FloatControl entry={entry} onChange={onChange} />;
    case "color":
      return <ColorControl entry={entry} onChange={onChange} />;
    case "bool":
      return <BoolControl entry={entry} onChange={onChange} />;
    case "vector3":
      return <Vector3Control entry={entry} onChange={onChange} />;
    case "profile":
      return <ProfileControl entry={entry} onChange={onChange} />;
    default:
      return (
        <Text size="xs" c="dimmed">
          Unsupported type: {String(entry.type)} for {entry.key}
        </Text>
      );
  }
}

function readNum(v: unknown, fallback: number): number {
  if (typeof v === "number") return v;
  if (typeof v === "string") {
    const n = parseFloat(v);
    return isNaN(n) ? fallback : n;
  }
  return fallback;
}

function FloatControl({
  entry,
  onChange,
}: {
  entry: TuningEntry;
  onChange: (key: string, value: unknown) => void;
}) {
  const min = readNum(entry.meta.min, 0);
  const max = readNum(entry.meta.max, 1);
  const step = readNum(entry.meta.step, (max - min) / 100);
  const value = readNum(entry.value, min);

  return (
    <Box>
      <Group justify="space-between" gap="xs" mb={4}>
        <Text size="sm">{entry.label}</Text>
        <Text size="xs" c="dimmed" ff="monospace">
          {value.toFixed(stepDecimals(step))}
        </Text>
      </Group>
      <Group gap="xs" wrap="nowrap">
        <Box style={{ flex: 1 }}>
          <Slider
            min={min}
            max={max}
            step={step}
            value={value}
            onChange={(v) => onChange(entry.key, v)}
            label={null}
          />
        </Box>
        <NumberInput
          size="xs"
          w={90}
          min={min}
          max={max}
          step={step}
          value={value}
          onChange={(v) =>
            onChange(entry.key, typeof v === "number" ? v : parseFloat(String(v)))
          }
        />
      </Group>
    </Box>
  );
}

function stepDecimals(step: number): number {
  if (step >= 1) return 0;
  if (step >= 0.1) return 1;
  if (step >= 0.01) return 2;
  return 3;
}

function ColorControl({
  entry,
  onChange,
}: {
  entry: TuningEntry;
  onChange: (key: string, value: unknown) => void;
}) {
  const value = typeof entry.value === "string" ? entry.value : "#888888";
  return (
    <ColorInput
      label={entry.label}
      value={value}
      onChange={(v) => onChange(entry.key, v)}
      format="hex"
      size="xs"
    />
  );
}

function BoolControl({
  entry,
  onChange,
}: {
  entry: TuningEntry;
  onChange: (key: string, value: unknown) => void;
}) {
  const checked = Boolean(entry.value);
  return (
    <Switch
      label={entry.label}
      checked={checked}
      onChange={(ev) => onChange(entry.key, ev.currentTarget.checked)}
    />
  );
}

function Vector3Control({
  entry,
  onChange,
}: {
  entry: TuningEntry;
  onChange: (key: string, value: unknown) => void;
}) {
  const arr = Array.isArray(entry.value) ? (entry.value as unknown[]) : [0, 0, 0];
  const xs = [
    readNum(arr[0], 0),
    readNum(arr[1], 0),
    readNum(arr[2], 0),
  ];
  const min = readNum(entry.meta.min, -180);
  const max = readNum(entry.meta.max, 180);
  const step = readNum(entry.meta.step, (max - min) / 200);
  const labels = ["X", "Y", "Z"] as const;
  return (
    <Box>
      <Text size="sm" mb={4}>
        {entry.label}
      </Text>
      <Stack gap={4}>
        {labels.map((lab, i) => (
          <Group key={lab} gap="xs" wrap="nowrap">
            <Text size="xs" c="dimmed" w={12}>
              {lab}
            </Text>
            <Box style={{ flex: 1 }}>
              <Slider
                min={min}
                max={max}
                step={step}
                value={xs[i]}
                onChange={(v) => {
                  const next = [...xs];
                  next[i] = v;
                  onChange(entry.key, next);
                }}
                label={null}
              />
            </Box>
            <Text size="xs" c="dimmed" ff="monospace" w={50} ta="right">
              {xs[i].toFixed(stepDecimals(step))}
            </Text>
          </Group>
        ))}
      </Stack>
    </Box>
  );
}

// Dropdown of saved road configs from localStorage. Selecting one sends
// its Road over the WebSocket — Unity drops it into its RoadProfile and
// re-applies to all existing roads (plus future new ones).
//
// Reads localStorage once on mount; if you've edited configs in the
// Designer view while the Tuning view is open, switch away and back to
// refresh the dropdown.
function ProfileControl({
  entry,
  onChange,
}: {
  entry: TuningEntry;
  onChange: (key: string, value: unknown) => void;
}) {
  const configs = useMemo(() => listConfigs(), []);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const options = configs.map((c) => ({
    value: c.id,
    label: c.name?.trim() || "Untitled road",
  }));

  return (
    <Box>
      <Text size="sm" mb={4}>
        {entry.label}
      </Text>
      <Select
        size="xs"
        data={options}
        value={selectedId}
        placeholder={
          configs.length === 0
            ? "No saved configs"
            : "Pick a road type…"
        }
        disabled={configs.length === 0}
        onChange={(value) => {
          setSelectedId(value);
          if (!value) return;
          const cfg = configs.find((c) => c.id === value);
          if (cfg) onChange(entry.key, cfg.road);
        }}
        searchable
        clearable
      />
      {configs.length === 0 && (
        <Text size="xs" c="dimmed" mt={4}>
          Save a config in the Designer view to use it here.
        </Text>
      )}
    </Box>
  );
}
