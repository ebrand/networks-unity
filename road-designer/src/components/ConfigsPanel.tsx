import {
  ActionIcon,
  Box,
  Button,
  Group,
  Select,
  Stack,
  Text,
  TextInput,
  Title,
  Tooltip,
} from "@mantine/core";
import type { SavedConfig } from "../storage/configs";
import type { Road } from "../model/types";

// Compact cross-section formula for the dropdown label. Walks across the
// road from the AB shoulder to the BA shoulder:
//   |ab-shoulder|ab-lane-widths (outermost→innermost)|m|ba-lane-widths (innermost→outermost)|ba-shoulder|
function formatCrossSection(road: Road): string {
  const fmt = (n: number) => {
    const s = n.toFixed(1);
    return s.endsWith(".0") ? s.slice(0, -2) : s;
  };
  const sA = fmt(road.shoulderAB.width);
  const sB = fmt(road.shoulderBA.width);
  const ab = road.ab.lanes;
  const ba = road.ba.lanes;
  const abPart = ab.length > 0
    ? [...ab].reverse().map((l) => fmt(l.width)).join(",")
    : "";
  const baPart = ba.length > 0
    ? ba.map((l) => fmt(l.width)).join(",")
    : "";
  if (abPart && baPart) return `|${sA}|${abPart}|m|${baPart}|${sB}|`;
  if (abPart) return `|${sA}|${abPart}|${sB}|`;
  if (baPart) return `|${sA}|${baPart}|${sB}|`;
  return `|${sA}||${sB}|`;
}

interface Props {
  configs: SavedConfig[];
  activeId: string;
  activeName: string;
  activeCategory: string;
  onActiveNameChange: (name: string) => void;
  onActiveCategoryChange: (category: string) => void;
  onLoad: (id: string) => void;
  onDelete: (id: string) => void;
  onNew: () => void;
  onExport: () => void;
}

export function ConfigsPanel({
  configs,
  activeId,
  activeName,
  activeCategory,
  onActiveNameChange,
  onActiveCategoryChange,
  onLoad,
  onDelete,
  onNew,
  onExport,
}: Props) {
  const knownCategories = Array.from(
    new Set(
      configs
        .map((c) => (c.category ?? "").trim())
        .filter((s) => s.length > 0),
    ),
  ).sort((a, b) => a.localeCompare(b));

  // Group saved configs by category for the Select. Each entry's label
  // is "name — cross-section" so the dropdown reads like the old inline list.
  const grouped = (() => {
    const byCat = new Map<string, SavedConfig[]>();
    for (const c of configs) {
      const cat = (c.category ?? "").trim() || "Uncategorized";
      const arr = byCat.get(cat) ?? [];
      arr.push(c);
      byCat.set(cat, arr);
    }
    const cats = Array.from(byCat.keys()).sort((a, b) => {
      // Uncategorized last.
      if (a === "Uncategorized") return 1;
      if (b === "Uncategorized") return -1;
      return a.localeCompare(b);
    });
    return cats.map((cat) => ({
      group: cat,
      items: byCat
        .get(cat)!
        .slice()
        .sort((a, b) => (a.name || "").localeCompare(b.name || ""))
        .map((c) => ({
          value: c.id,
          label: `${c.name || "Untitled road"}  ${formatCrossSection(c.road)}`,
        })),
    }));
  })();

  return (
    <Stack gap="xs">
      <Title order={6} c="dimmed" tt="uppercase" fz="xs" fw={600}>
        Configuration
      </Title>

      {configs.length > 0 && (
        <Group gap="xs" align="flex-end" wrap="nowrap">
          <Box style={{ flex: 1, minWidth: 0 }}>
            <Select
              label={`Saved (${configs.length})`}
              data={grouped}
              value={activeId}
              onChange={(id) => {
                if (id && id !== activeId) onLoad(id);
              }}
              allowDeselect={false}
              searchable
              nothingFoundMessage="No matches"
              checkIconPosition="right"
              comboboxProps={{ withinPortal: true }}
            />
          </Box>
          <Tooltip label="Delete current" withArrow>
            <ActionIcon
              variant="subtle"
              color="red"
              size="lg"
              onClick={() => onDelete(activeId)}
            >
              ×
            </ActionIcon>
          </Tooltip>
        </Group>
      )}

      <TextInput
        label="Name"
        value={activeName}
        onChange={(e) => onActiveNameChange(e.currentTarget.value)}
        placeholder="Untitled road"
      />
      <TextInput
        label="Category"
        value={activeCategory}
        onChange={(e) => onActiveCategoryChange(e.currentTarget.value)}
        placeholder="(uncategorized)"
        list="config-categories"
      />
      <datalist id="config-categories">
        {knownCategories.map((c) => (
          <option key={c} value={c} />
        ))}
      </datalist>
      <Group gap="xs" mt="xs" wrap="wrap">
        <Button variant="light" size="xs" onClick={onNew}>
          New
        </Button>
        <Button
          variant="light"
          size="xs"
          onClick={onExport}
          disabled={configs.length === 0}
        >
          Export road-config.json
        </Button>
        <Text size="xs" c="dimmed">
          Autosaved
        </Text>
      </Group>
    </Stack>
  );
}
