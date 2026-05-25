import {
  ActionIcon,
  Box,
  Button,
  Group,
  Stack,
  Text,
  TextInput,
  Title,
  Tooltip,
} from "@mantine/core";
import type { SavedConfig } from "../storage/configs";

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
  // Known categories from existing saved configs — surfaced as a
  // datalist on the category input so the user can re-use existing
  // category strings instead of risking typos that split a group.
  const knownCategories = Array.from(
    new Set(
      configs
        .map((c) => (c.category ?? "").trim())
        .filter((s) => s.length > 0),
    ),
  ).sort((a, b) => a.localeCompare(b));

  return (
    <Stack gap="xs">
      <Title order={6} c="dimmed" tt="uppercase" fz="xs" fw={600}>
        Configuration
      </Title>
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

      {configs.length > 0 && (
        <Box mt="sm">
          <Text size="xs" c="dimmed" mb={4}>
            Saved ({configs.length})
          </Text>
          <Stack gap={2}>
            {configs.map((c) => {
              const isActive = c.id === activeId;
              return (
                <Group
                  key={c.id}
                  justify="space-between"
                  wrap="nowrap"
                  gap="xs"
                  px="xs"
                  py={4}
                  style={{
                    borderRadius: 4,
                    background: isActive
                      ? "var(--mantine-color-indigo-0)"
                      : undefined,
                    cursor: isActive ? "default" : "pointer",
                  }}
                  onClick={() => {
                    if (!isActive) onLoad(c.id);
                  }}
                >
                  <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                    <Text
                      size="sm"
                      truncate
                      fw={isActive ? 600 : 400}
                    >
                      {c.name || "Untitled road"}
                    </Text>
                    {c.category && c.category.trim().length > 0 && (
                      <Text size="xs" c="dimmed" truncate>
                        {c.category}
                      </Text>
                    )}
                  </Stack>
                  <Tooltip label="Delete" withArrow>
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      size="sm"
                      onClick={(e) => {
                        e.stopPropagation();
                        onDelete(c.id);
                      }}
                    >
                      ×
                    </ActionIcon>
                  </Tooltip>
                </Group>
              );
            })}
          </Stack>
        </Box>
      )}
    </Stack>
  );
}
