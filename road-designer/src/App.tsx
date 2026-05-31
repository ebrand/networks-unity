import { useEffect, useMemo, useRef, useState } from "react";
import {
  AppShell,
  Box,
  Divider,
  Group,
  ScrollArea,
  SegmentedControl,
  Stack,
  Switch,
  Text,
  Title,
} from "@mantine/core";
import { modals } from "@mantine/modals";
import { Navigate, Route, Routes, useLocation, useNavigate } from "react-router-dom";
import "./App.css";
import { ConfigsPanel } from "./components/ConfigsPanel";
import { RoadConfigForm } from "./components/RoadConfigForm";
import { RoadVisualizer } from "./components/RoadVisualizer";
import { TuningPanel } from "./components/TuningPanel";
import type { DriveSide, Road } from "./model/types";
import {
  deleteConfig as deleteStoredConfig,
  downloadExport,
  getActiveId,
  listConfigs,
  makeConfigId,
  type SavedConfig,
  setActiveId,
  upsertConfig,
} from "./storage/configs";

function defaultRoad(): Road {
  return {
    id: "road-1",
    ab: {
      lanes: [
        { id: "ab-0", width: 4.0 },
        { id: "ab-1", width: 4.0 },
      ],
    },
    ba: {
      lanes: [
        { id: "ba-0", width: 4.0 },
        { id: "ba-1", width: 4.0 },
      ],
    },
    shoulderAB: { width: 1 },
    shoulderBA: { width: 1 },
  };
}

function bootstrap(): {
  active: SavedConfig;
  list: SavedConfig[];
} {
  const list = listConfigs();
  const activeId = getActiveId();
  const found = activeId ? list.find((c) => c.id === activeId) : undefined;
  if (found) return { active: found, list };

  // First run: synthesize a default config and persist it.
  const seeded: SavedConfig = {
    id: makeConfigId(),
    name: "Untitled road",
    road: defaultRoad(),
    driveSide: "right",
    updatedAt: Date.now(),
  };
  upsertConfig(seeded);
  setActiveId(seeded.id);
  return { active: seeded, list: [seeded] };
}

function App() {
  const initial = useMemo(bootstrap, []);

  const [activeId, setActiveIdState] = useState<string>(initial.active.id);
  const [name, setName] = useState<string>(initial.active.name);
  const [category, setCategory] = useState<string>(initial.active.category ?? "");
  const [road, setRoad] = useState<Road>(initial.active.road);
  const [driveSide, setDriveSide] = useState<DriveSide>(initial.active.driveSide);
  const [configs, setConfigs] = useState<SavedConfig[]>(initial.list);
  const [showCenterline, setShowCenterline] = useState<boolean>(true);

  // View is derived from the route so /designer and /tuning are
  // directly navigable (deep-linkable, refresh-safe via Vite's SPA
  // history fallback). The header's SegmentedControl just navigates.
  const location = useLocation();
  const navigate = useNavigate();
  const isTuning = location.pathname.startsWith("/tuning");

  // Autosave: any change to name/road/driveSide while a config is active
  // persists to localStorage and refreshes the in-memory list. We guard
  // against the initial render with `firstRender` so we don't overwrite
  // updatedAt the instant we bootstrap.
  const firstRender = useRef(true);
  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false;
      return;
    }
    const snapshot: SavedConfig = {
      id: activeId,
      name,
      category: category.trim() || undefined,
      road,
      driveSide,
      updatedAt: Date.now(),
    };
    upsertConfig(snapshot);
    setActiveId(activeId);
    setConfigs(listConfigs());
  }, [activeId, name, category, road, driveSide]);

  function handleLoad(id: string) {
    const cfg = listConfigs().find((c) => c.id === id);
    if (!cfg) return;
    // Suppress the autosave that would otherwise fire from the state
    // changes below — loading is not a user edit.
    firstRender.current = true;
    setActiveIdState(cfg.id);
    setName(cfg.name);
    setCategory(cfg.category ?? "");
    setRoad(cfg.road);
    setDriveSide(cfg.driveSide);
    setActiveId(cfg.id);
  }

  function handleNew() {
    const fresh: SavedConfig = {
      id: makeConfigId(),
      name: "Untitled road",
      road: defaultRoad(),
      driveSide: "right",
      updatedAt: Date.now(),
    };
    upsertConfig(fresh);
    setActiveId(fresh.id);
    firstRender.current = true; // don't double-write on the next effect
    setActiveIdState(fresh.id);
    setName(fresh.name);
    setCategory("");
    setRoad(fresh.road);
    setDriveSide(fresh.driveSide);
    setConfigs(listConfigs());
  }

  function handleDelete(id: string) {
    const target = configs.find((c) => c.id === id);
    const label = target?.name?.trim() || "Untitled road";
    modals.openConfirmModal({
      title: "Delete configuration?",
      centered: true,
      children: (
        <Text size="sm">
          Delete <b>{label}</b>? This can't be undone.
        </Text>
      ),
      labels: { confirm: "Delete", cancel: "Cancel" },
      confirmProps: { color: "red" },
      onConfirm: () => {
        deleteStoredConfig(id);
        const remaining = listConfigs();
        if (id === activeId) {
          if (remaining.length > 0) {
            handleLoad(remaining[0].id);
          } else {
            handleNew();
            return;
          }
        }
        setConfigs(remaining);
      },
    });
  }

  return (
    <AppShell
      header={{ height: 64 }}
      navbar={isTuning ? undefined : { width: 440, breakpoint: 0 }}
      padding="md"
    >
      <AppShell.Header>
        <Group justify="space-between" align="center" h="100%" px="md">
          <Box>
            <Title order={3}>
              {isTuning ? "Live tuning" : "Road Designer"}
            </Title>
            <Text size="xs" c="dimmed">
              {isTuning
                ? "Adjust runtime parameters in the Unity scene. Requires Play mode."
                : "Configure a road's cross-section. Toggle drive side to see the AB/BA sides swap across the centerline without touching lane data."}
            </Text>
          </Box>
          <SegmentedControl
            size="xs"
            value={isTuning ? "tuning" : "designer"}
            onChange={(v) => navigate(`/${v}`)}
            data={[
              { value: "designer", label: "Designer" },
              { value: "tuning", label: "Tuning" },
            ]}
          />
        </Group>
      </AppShell.Header>

      {!isTuning && (
        <AppShell.Navbar p="md">
          <ScrollArea h="100%" type="auto" offsetScrollbars>
            <Stack gap="lg">
              <ConfigsPanel
                configs={configs}
                activeId={activeId}
                activeName={name}
                activeCategory={category}
                onActiveNameChange={setName}
                onActiveCategoryChange={setCategory}
                onLoad={handleLoad}
                onDelete={handleDelete}
                onNew={handleNew}
                onExport={downloadExport}
              />
              <Divider />
              <Switch
                checked={showCenterline}
                onChange={(e) => setShowCenterline(e.currentTarget.checked)}
                label="Show centerline (A–B axis)"
                size="sm"
              />
              <RoadConfigForm
                road={road}
                driveSide={driveSide}
                onChange={setRoad}
                onDriveSideChange={setDriveSide}
              />
            </Stack>
          </ScrollArea>
        </AppShell.Navbar>
      )}

      <AppShell.Main>
        <Routes>
          <Route path="/" element={<Navigate to="/designer" replace />} />
          <Route
            path="/designer"
            element={
              <RoadVisualizer
                road={road}
                driveSide={driveSide}
                showCenterline={showCenterline}
              />
            }
          />
          <Route path="/tuning" element={<TuningPanel />} />
          <Route path="*" element={<Navigate to="/designer" replace />} />
        </Routes>
      </AppShell.Main>
    </AppShell>
  );
}

export default App;
