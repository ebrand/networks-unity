import type { DriveSide, Road } from "../model/types";

// A named, persisted snapshot of a road + its drive-side context.
// `id` is generated at create-time and never changes; `name` is
// user-editable; `updatedAt` is a millis epoch for sorting the list.
// `category` groups configs in the Unity tool palette (free-form string,
// empty/undefined → "Uncategorized" tab).
export interface SavedConfig {
  id: string;
  name: string;
  category?: string;
  road: Road;
  driveSide: DriveSide;
  updatedAt: number;
}

// Versioned key so we can migrate later without clobbering data.
const KEY_CONFIGS = "road-designer:configs:v1";
const KEY_ACTIVE = "road-designer:active:v1";

function safeRead<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    if (raw == null) return fallback;
    return JSON.parse(raw) as T;
  } catch {
    return fallback;
  }
}

function safeWrite(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Quota or private-mode failure — swallow rather than crash the UI.
  }
}

export function listConfigs(): SavedConfig[] {
  const all = safeRead<SavedConfig[]>(KEY_CONFIGS, []);
  // Alphabetical by name (case-insensitive, locale-aware). Untitled
  // entries collapse to one bucket; ties break by updatedAt desc so a
  // freshly-saved "Untitled road" still floats above older same-name
  // entries.
  return [...all].sort((a, b) => {
    const an = (a.name ?? "").trim().toLowerCase();
    const bn = (b.name ?? "").trim().toLowerCase();
    const byName = an.localeCompare(bn);
    if (byName !== 0) return byName;
    return b.updatedAt - a.updatedAt;
  });
}

export function upsertConfig(config: SavedConfig): void {
  const all = safeRead<SavedConfig[]>(KEY_CONFIGS, []);
  const idx = all.findIndex((c) => c.id === config.id);
  if (idx >= 0) all[idx] = config;
  else all.push(config);
  safeWrite(KEY_CONFIGS, all);
}

export function deleteConfig(id: string): void {
  const all = safeRead<SavedConfig[]>(KEY_CONFIGS, []);
  safeWrite(
    KEY_CONFIGS,
    all.filter((c) => c.id !== id),
  );
}

export function getActiveId(): string | null {
  return safeRead<string | null>(KEY_ACTIVE, null);
}

export function setActiveId(id: string | null): void {
  if (id == null) {
    try {
      localStorage.removeItem(KEY_ACTIVE);
    } catch {
      // ignore
    }
    return;
  }
  safeWrite(KEY_ACTIVE, id);
}

export function makeConfigId(): string {
  // Short, URL-safe, collision-resistant enough for client-only IDs.
  return `cfg-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

// File format consumed by the Unity tool. Bump `version` whenever the
// shape changes so the importer can branch.
export interface ExportedConfigFile {
  version: 1;
  exportedAt: string; // ISO-8601, human-readable for diffs
  activeId: string | null;
  configs: SavedConfig[];
}

export function buildExportPayload(): ExportedConfigFile {
  return {
    version: 1,
    exportedAt: new Date().toISOString(),
    activeId: getActiveId(),
    configs: listConfigs(),
  };
}

export const EXPORT_FILENAME = "road-config.json";

// Triggers a browser download of the export payload as JSON. Pretty-
// printed for diff-friendliness when checked into a Unity project.
export function downloadExport(): void {
  const payload = buildExportPayload();
  const json = JSON.stringify(payload, null, 2);
  const blob = new Blob([json], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = EXPORT_FILENAME;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}
