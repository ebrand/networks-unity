# Hotkeys & Controls

Keyboard / mouse bindings for the in-game designer (Unity play mode). Keys are
ignored while a text field / modal is focused (e.g. the startup game picker), so
typing a name never fires a tool.

> Source of truth: `TerrainDesigner.Update()` (tool/brush/mode keys),
> `FlyCameraController` (camera), `ChunkStreamer` (Space), `LauncherPalette`
> (lock button). Update this doc when those change.

## Camera (fly)

| Input | Action |
|---|---|
| `W` / `S` | Move forward / back |
| `A` / `D` | Strafe left / right |
| `E` / `Q` | Move up / down |
| `Shift` (held) | Move faster |
| Middle-mouse drag | Look around |
| Scroll wheel | Zoom (dolly in/out) |

## Terrain brush modes

| Key | Brush |
|---|---|
| `1` | Raise |
| `2` | Lower |
| `3` | Smooth |
| `4` | Flatten |
| `5` | Slope |
| `6` | Sea (flood-lower) |
| `7` | Measure |

Brush size: `]` bigger / `[` smaller (held = continuous), or `Shift` + scroll wheel (~10 % per notch).

Flatten right-click = eyedropper (sample the target height). Measure right-click = clear the A→B line.

## Tool / placement modes

Press again to return to terrain-sculpt mode.

| Key | Mode |
|---|---|
| `T` | Trees (scatter) |
| `R` | Rocks (scatter) |
| `F` | Fences (line) |
| `P` | Power lines (line) |
| `L` | Rail (line) |
| `K` | Plan / survey lines (line) |
| `Backspace` | Remove last placed node (active line) |

## Palettes (launcher)

Radio toggle — opens that palette exclusively, or closes it if already open.
Same as clicking the launcher dock buttons.

| Key | Palette |
|---|---|
| `N` | Terrain |
| `Y` | System |
| `O` | Placeables |

## Grid

| Key | Action |
|---|---|
| `G` | Toggle grid |
| `Shift` + `G` | Toggle snap-to-grid |

## Chunk-streaming world (DEM)

| Key | Action |
|---|---|
| `M` | Toggle bubble **lock** (freeze the resident set to sculpt in place) |
| `Space` (held) | Stream while locked — reposition the frozen bubble |
| `V` | Toggle the corner minimap / 3D relief diorama |

The launcher dock also shows a lock button (🔒 locked / 🔓 streaming) in chunk worlds.

## Rail mode

Active only while in rail mode (`L`).

| Input | Action |
|---|---|
| `I` | Toggle curve-inspect overlay |
| `B` | Toggle grade override (build across terrain instead of truncating at the grade limit) |
| `Z` | Toggle parallel-track drawing |
| `X` | Flip which side the parallel tracks lay on |
| `C` (held) | Connect mode — chain-connect to a node |
| `Cmd` + scroll wheel | Design speed ±10 km/h per notch |
| `Option/Alt` + scroll wheel | ±1 parallel track per notch |
| `Shift` + right-click | Edge-chop: delete an edge, keep its nodes (bridge gaps) |

## Mouse (general)

| Input | Action |
|---|---|
| Left-click / drag | Sculpt / place / draw (depends on the active tool) |
| Right-click | Tool-dependent: eyedropper (Flatten), cancel/back (Slope, rail), clear (Measure), delete/end-chain (line tools) |
| Scroll over the minimap | Zoom the minimap (chunks shown) |
| `Esc` | Cancel the current action |

## Not hotkeys, but worth knowing

- **Map trimmer** — System palette → "Trim map (empty chunks)…" (DEM worlds): trim flat/ocean chunks out of the streamed set.
- **Save & exit to menu** — System palette: saves, tears down the world, returns to the startup picker.
