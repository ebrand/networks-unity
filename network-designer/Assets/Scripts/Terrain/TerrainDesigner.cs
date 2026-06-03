// Terrain designer — LOW-POLY chunked flat-shaded mesh with sculpt brushes.
//
// `TerrainField` is the working heightfield. It's rendered as a grid of CHUNKS
// (each a flat-shaded mesh with un-shared verts -> per-face normals = visible
// facets) plus a MeshCollider per chunk. Sculpting edits the field, then
// rebuilds only the brush-touched chunks (mesh + collider) — so it stays
// interactive at 5 m over 2 km without rebuilding the whole ~1M-vert mesh.
// Coordinates are corner-anchored (field.Origin = the centered world corner).
// Single lit color for now; height/slope vertex-color bands are a follow-up.
//
// Sculpting runs in Play mode: hold left mouse over the terrain and drag.
// Brush: 1=Raise 2=Lower 3=Smooth 4=Flatten; [ / ] resize the brush.
//
// (Replaced an earlier Unity-Terrain heightmap renderer; we went low-poly,
// which is coarse enough that a custom flat-shaded mesh is the right tool.)

using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using NetworkDesigner.Designer; // SceneAmbiance
using NetworkDesigner.Model;    // PlacedTreeData

namespace NetworkDesigner.Terrain
{
    public class TerrainDesigner : MonoBehaviour
    {
        public enum BrushMode { Raise, Lower, Smooth, Flatten }

        [Header("Terrain (low-poly chunked mesh)")]
        [Tooltip("Terrain width/length in metres (square).")]
        public float TerrainSizeMeters = 2000f;
        [Tooltip("Metres between grid vertices = facet size. 5 m is low-poly + road-usable.")]
        public float CellSize = 5f;
        [Tooltip("Cells per chunk side. Chunks rebuild independently on sculpt; keep <= ~100.")]
        public int ChunkCells = 50;
        [Tooltip("Flat terrain color — the 'grass' band, used on flat/gentle faces.")]
        public Color TerrainColor = new Color(0.40f, 0.5f, 0.30f);
        [Tooltip("Color blended onto steep faces (the 'rock' band). Live; no rebuild.")]
        public Color RockColor = new Color(0.42f, 0.40f, 0.38f);
        [Tooltip("Slope angle (deg) where rock starts blending in. 0 = flat, 90 = vertical.")]
        public float SlopeStartDeg = 26f;
        [Tooltip("Slope angle (deg) at which a face is fully rock.")]
        public float SlopeFullDeg = 45f;
        [Tooltip("Optional low-poly rock texture, sampled triplanar (needs no UVs). Null = flat rock color.")]
        public Texture2D RockTexture;
        [Tooltip("Rock texture tiling (1/world-units). Lower = larger features.")]
        public float RockTextureScale = 0.12f;

        [Header("World grid overlay")]
        [Tooltip("Paint a world grid on the terrain surface (G toggles). Drapes perfectly — " +
                 "it's shaded on the surface, so it follows relief and never needs rebuilding.")]
        public bool GridEnabled = false;
        [Tooltip("Minor grid line spacing (m). 5 matches the cell size.")]
        public float GridSpacing = 5f;
        [Tooltip("Draw a brighter major line every N minor lines (e.g. 10 = every 50 m).")]
        public float GridMajorEvery = 10f;
        public Color GridColor = new Color(0.14f, 0.15f, 0.17f, 1f);
        [Range(0f, 1f)] public float GridStrength = 0.5f;
        [Tooltip("Grid line width in pixels (constant on screen at any distance).")]
        public float GridLineWidth = 1f;
        [Tooltip("Snap line/rail/fence node placement to grid intersections (Shift+G). " +
                 "Uses the grid spacing whether or not the grid is shown.")]
        public bool SnapToGrid = false;

        // Vertex counts, derived from TerrainSizeMeters / CellSize in EnsureField.
        [HideInInspector] public int ColumnsX = 401;
        [HideInInspector] public int RowsZ = 401;

        [Header("Sculpt brush")]
        public BrushMode Brush = BrushMode.Raise;
        [Tooltip("Brush radius in metres. Resize live with [ (smaller) and ] (larger).")]
        public float BrushRadius = 10f;
        [Tooltip("Brush resize speed (metres/second, while [ or ] is held).")]
        public float BrushResizeRate = 50f;
        [Tooltip("Upper clamp for the brush radius (metres).")]
        public float MaxBrushRadius = 500f;
        [Tooltip("Height change rate (metres/second) at the brush centre.")]
        public float BrushStrength = 20f;
        [Tooltip("Exponent on Strength for Raise/Lower: effective rate = Strength^exp. " +
                 "1 = linear (unchanged). >1 makes higher Strength values ramp up " +
                 "dramatically (e.g. exp 2 turns Strength 50 into 2500 m/s).")]
        [Range(1f, 4f)] public float BrushStrengthExponent = 1f;
        [Tooltip("0 = hard edge, 1 = soft (smoothstep) falloff to the rim.")]
        [Range(0f, 1f)] public float BrushFalloff = 0.7f;
        [Tooltip("Camera used for the sculpt raycast. Defaults to Camera.main.")]
        public Camera PickCamera;

        [Header("Brush cursor (ring)")]
        public bool ShowBrushCursor = true;
        public Color BrushCursorColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        [Range(8, 128)] public int BrushCursorSegments = 48;
        [Tooltip("Metres the ring floats above the surface so it doesn't z-fight.")]
        public float BrushCursorLift = 0.15f;
        [Tooltip("Draw the cursor ring dashed instead of solid.")]
        public bool BrushCursorDashed = false;
        [Tooltip("Dash length (m) when Brush Cursor Dashed is on.")]
        public float BrushCursorDashLength = 1.5f;
        [Tooltip("Gap length (m) between dashes when Brush Cursor Dashed is on.")]
        public float BrushCursorDashGap = 1.5f;

        [Header("Topographic lines")]
        public bool ShowContours = false;
        [Tooltip("Elevation between contour lines, in metres.")]
        public float ContourInterval = 1f;
        public Color ContourColor = new Color(0.22f, 0.15f, 0.08f, 1f); // dark brown
        [Tooltip("Metres the lines float above the surface to avoid z-fighting.")]
        public float ContourLift = 0.05f;
        [Tooltip("Draw the contour lines dashed instead of solid.")]
        public bool ContourDashed = false;
        [Tooltip("Dash length (m) when Contour Dashed is on.")]
        public float ContourDashLength = 2f;
        [Tooltip("Gap length (m) between dashes when Contour Dashed is on.")]
        public float ContourDashGap = 2f;
        [Tooltip("Rebuild contours every sculpt frame so they track the terrain " +
                 "in real time. Turn off (rebuild only on stroke-end) if it " +
                 "hitches on very large grids.")]
        public bool LiveContours = true;

        [Header("Scatter brushes  (T = trees, R = rocks)")]
        [Tooltip("Tree scatter layer. Press T to toggle tree mode: left-drag " +
                 "PAINTS, right-drag ERASES. Sculpt is disabled while on.")]
        public ScatterLayer TreeLayer = new ScatterLayer
        { Name = "Trees", Folder = "Assets/Trees", PaintRate = 25f, Spacing = 4f, ScaleRange = new Vector2(1.2f, 1.95f) };
        [Tooltip("Rock scatter layer. Press R to toggle rock mode (same controls).")]
        public ScatterLayer RockLayer = new ScatterLayer
        { Name = "Rocks", Folder = "Assets/Rocks", PaintRate = 15f, Spacing = 6f, ScaleRange = new Vector2(0.5f, 1.6f) };

        [Header("Linework  (F = fence, P = power line)")]
        [Tooltip("Fence linework layer. Press F to toggle: left-click adds a node " +
                 "and connects from the last (chain); right-click ends the chain. " +
                 "Edges auto-curve through the nodes; the Asset renders in series.")]
        public LineworkLayer FenceLayer = new LineworkLayer { Name = "Fence", Spacing = 3f, YawOffset = -90f };
        [Tooltip("Power-line layer (P). Assign a socketed POLE prefab as Asset and set " +
                 "ParentOverride to a PoleChain that carries a cable generator — the " +
                 "placed poles then auto-connect with cables. Use a wide Spacing (span).")]
        public LineworkLayer PowerLineLayer = new LineworkLayer { Name = "PowerLine", Spacing = 35f };
        [Tooltip("Rail-track layer (L). Procedural rails + ties generated along each " +
                 "edge — drawn like a fence; gauge/tie-spacing under the Rail tunables.")]
        public RailTrackLayer RailLayer = new RailTrackLayer { Name = "Rail" };

        [Header("Initial relief (stamped once)")]
        [Tooltip("Stamp a smooth gaussian hill when the field is first built, " +
                 "so there's something to sculpt. Does NOT re-apply on rebuild.")]
        public bool TestHill = false;
        public float TestHillHeight = 80f;

        [Header("Heightmap import (context-menu 'Import Heightmap')")]
        [Tooltip("Grayscale heightmap file. Relative paths resolve to the project " +
                 "root in the Editor (persistentDataPath in a build). Any size — it's " +
                 "bilinear-sampled to the grid. Black = 0, white = Max height. " +
                 "REPLACES the current heights. Run in Play mode.")]
        public string HeightmapPath = "terrain1.png";
        [Tooltip("Metres of elevation that pure white (1.0) maps to; black maps to 0.")]
        public float HeightmapMaxHeight = 250f;
        [Tooltip("Box-blur passes applied after import. Softens the 8-bit terracing " +
                 "(staircase steps on slopes) and single-pixel spikes. 0 = raw; " +
                 "2-4 looks natural; high values flatten real detail.")]
        [Range(0, 12)] public int HeightmapSmoothPasses = 3;

        [Header("Autosave (terrain persistence across Play stop/start)")]
        public bool Autosave = true;
        [Tooltip("Where the terrain is saved. Empty → project_root/TerrainAutosave.json " +
                 "in the Editor, persistentDataPath in a Player build.")]
        public string AutosavePath = "";
        [Tooltip("Seconds of no sculpting before the terrain is written to disk.")]
        public float AutosaveDebounceSeconds = 1f;

        TerrainField _field;
        float _dirtySince = -1f; // realtime when last edited; -1 = clean
        System.Threading.Tasks.Task _saveTask; // in-flight async autosave (serialize+write off-thread)
        GameObject _chunkRoot;
        Mesh[] _chunkMesh;
        MeshCollider[] _chunkCol;
        Material _mat;
        int _chunksX, _chunksZ;
        ScatterLayer _active;      // scatter layer being painted (null = not scattering)
        ITerrainLineLayer _lineActive; // line layer being drawn (null = not drawing)
        MeshFilter _cursorMf;
        MeshRenderer _cursorMr;
        Mesh _cursorMesh;
        Material _cursorMat;
        readonly List<Vector3> _ring = new List<Vector3>();
        readonly List<Vector3> _cursorVerts = new List<Vector3>();
        readonly List<int> _cursorIdx = new List<int>();
        MeshFilter _contourMf;
        MeshRenderer _contourMr;
        Mesh _contourMesh;
        Material _contourMat;
        bool _hasFlattenTarget;
        float _flattenTarget; // height offset (field space) captured on mouse-down

        public TerrainField Field => _field;

        [Header("Scene lighting")]
        [Tooltip("On Start, if the scene has no SceneAmbiance, create one that " +
                 "lights itself: a directional sun, soft shadows, ambient fill, " +
                 "and a large URP shadow distance. Turn off to manage lighting " +
                 "yourself in the Lighting window.")]
        public bool AutoLighting = true;
        [Tooltip("URP shadow distance (metres) the auto-lighting requests — " +
                 "should comfortably exceed the terrain footprint.")]
        public float ShadowDistance = 300f;

        [Header("Camera")]
        [Tooltip("On Start, add an OrbitCameraController to the pick camera if it " +
                 "has none, framed on the terrain. Middle-drag = orbit, " +
                 "shift+middle = pan, scroll = zoom, WASD = pan. Sculpt is " +
                 "left-drag, so they don't conflict. Off = manage the camera yourself.")]
        public bool AutoCameraControl = true;

        [Header("Live tuning")]
        [Tooltip("On Start, stand up a TuningServer + TerrainTuningSetup if the " +
                 "scene has none, so the React tuning panel can adjust the " +
                 "terrain live (ws://localhost:8787). Off = no tuning server.")]
        public bool AutoTuning = true;

        void Start()
        {
            if (PickCamera == null) PickCamera = Camera.main;
            if (PickCamera == null) PickCamera = FindFirstObjectByType<Camera>();

            StripStaleHostMesh();
            EnsureScatterDefaults();
#if UNITY_EDITOR
            // Self-heal lost prefab references: if a layer's list is empty,
            // repopulate from its Folder so a broken link doesn't silently
            // disable the brush.
            if (TreeLayer.IsEmpty && TreeLayer.LoadFromFolder()) UnityEditor.EditorUtility.SetDirty(this);
            if (RockLayer.IsEmpty && RockLayer.LoadFromFolder()) UnityEditor.EditorUtility.SetDirty(this);
#endif
            EnsureField(forceRebuild: true);

            // Adopt a saved heightfield only if it matches the current grid.
            if (Autosave)
            {
                TerrainField loaded = TryLoadTerrain();
                if (loaded != null && loaded.ColumnsX == _field.ColumnsX
                                   && loaded.RowsZ == _field.RowsZ)
                {
                    loaded.Origin = _field.Origin;
                    _field = loaded;
                }
            }

            BuildAllChunks();
            TreeLayer.SpawnPending(_field); // scatter from the save (heights now known)
            RockLayer.SpawnPending(_field);
            FenceLayer.Rebuild(_field);     // linework from the save
            PowerLineLayer.Rebuild(_field);
            RailLayer.Rebuild(_field);
            RebuildContours();

            // Stand up scene services, sized to the actual terrain.
            if (AutoLighting) EnsureAmbiance();
            if (AutoCameraControl) EnsureCameraControl();
            if (AutoTuning) EnsureTuning();
        }

        // Stand up the live-tuning endpoint (TuningServer + registration) if
        // the scene has none, so the React panel can tune the terrain.
        void EnsureTuning()
        {
            if (FindFirstObjectByType<TerrainTuningSetup>() != null) return;
            TerrainTuningSetup setup = new GameObject("TerrainTuning")
                .AddComponent<TerrainTuningSetup>(); // RequireComponent adds TuningServer
            setup.Terrain = this;
        }

        // The terrain renders via child "TerrainChunks" objects, so a
        // MeshRenderer/Filter on THIS GameObject is a frozen baked mesh from the
        // old single-mesh era — it masks the live chunks and never updates.
        // Strip it so what you see is always the current heightfield.
        void StripStaleHostMesh()
        {
            MeshRenderer mr = GetComponent<MeshRenderer>();
            if (mr != null) { mr.enabled = false; DestroyComp(mr); }
            MeshFilter mf = GetComponent<MeshFilter>();
            if (mf != null) DestroyComp(mf);
        }

        static void DestroyComp(Component c)
        {
            if (c == null) return;
            if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
        }

        // --- Chunked flat-shaded mesh ---

        void EnsureMaterial()
        {
            if (_mat == null)
            {
                Shader sh = Shader.Find("NetworkDesigner/TerrainSlope");
                // Slope-blend shader if present; otherwise fall back to the flat
                // matte material so the terrain is never invisible/magenta.
                _mat = sh != null ? new Material(sh) { name = "TerrainSlopeMat" }
                                  : PipelineMaterials.CreateLitMatte(TerrainColor, "TerrainMat");
            }
            ApplyTerrainMaterial();
        }

        // Live material tweak from the tuning panel — no mesh rebuild needed
        // (the slope blend is computed in-shader from the per-face normal).
        public void ApplyTerrainMaterial()
        {
            if (_mat == null) return;
            if (_mat.HasProperty("_RockColor")) // slope shader
            {
                _mat.SetColor("_GrassColor", TerrainColor);
                _mat.SetColor("_RockColor", RockColor);
                _mat.SetFloat("_SlopeStart", SlopeStartDeg);
                _mat.SetFloat("_SlopeFull", SlopeFullDeg);
                _mat.SetFloat("_RockTexScale", RockTextureScale);
                _mat.SetFloat("_UseRockTex", RockTexture != null ? 1f : 0f);
                if (RockTexture != null) _mat.SetTexture("_RockTex", RockTexture);
                _mat.SetColor("_GridColor", GridColor);
                _mat.SetFloat("_GridSpacing", Mathf.Max(0.5f, GridSpacing));
                _mat.SetFloat("_GridMajorEvery", Mathf.Max(1f, GridMajorEvery));
                _mat.SetFloat("_GridStrength", GridEnabled ? GridStrength : 0f);
                _mat.SetFloat("_GridLineWidth", Mathf.Max(0.1f, GridLineWidth));
            }
            else _mat.color = TerrainColor; // matte fallback
        }

        // Back-compat name still referenced by the color tunable.
        public void ApplyTerrainColor() => ApplyTerrainMaterial();

        // Round a world point to the nearest grid intersection (same world-aligned
        // lattice the grid shader draws), when snap-to-grid is on. Y is left as-is
        // (the line layers re-derive height from the terrain). Off = unchanged.
        Vector3 ApplyGridSnap(Vector3 p)
        {
            if (!SnapToGrid) return p;
            float s = Mathf.Max(0.5f, GridSpacing);
            return new Vector3(Mathf.Round(p.x / s) * s, p.y, Mathf.Round(p.z / s) * s);
        }

        int ChunkSide => Mathf.Clamp(ChunkCells, 8, 100);

        // Build (or rebuild) all chunk meshes from the field. Recreates the
        // chunk GameObjects only when the grid/chunk count changed.
        void BuildAllChunks()
        {
            EnsureMaterial();
            int cells = _field.ColumnsX - 1; // quads per side
            int cc = ChunkSide;
            int chunksX = Mathf.Max(1, Mathf.CeilToInt(cells / (float)cc));
            int n = chunksX * chunksX;

            bool recreate = _chunkRoot == null || _chunkMesh == null
                            || _chunkMesh.Length != n || _chunksX != chunksX;
            if (recreate)
            {
                DestroyAllChunkRoots(); // tracked root + any orphans from edit-mode/reload
                _chunkRoot = new GameObject("TerrainChunks");
                _chunksX = _chunksZ = chunksX;
                _chunkMesh = new Mesh[n];
                _chunkCol = new MeshCollider[n];
                for (int cz = 0; cz < _chunksZ; cz++)
                    for (int cx = 0; cx < _chunksX; cx++)
                        CreateChunk(cx, cz, cc);
            }
            else
            {
                for (int cz = 0; cz < _chunksZ; cz++)
                    for (int cx = 0; cx < _chunksX; cx++)
                        RebuildChunk(cx, cz, cc);
            }
        }

        void CreateChunk(int cx, int cz, int cc)
        {
            int idx = cz * _chunksX + cx;
            GameObject go = new GameObject($"Chunk_{cx}_{cz}");
            go.transform.SetParent(_chunkRoot.transform, worldPositionStays: false);
            MeshFilter mf = go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            MeshCollider mc = go.AddComponent<MeshCollider>();
            Mesh mesh = new Mesh { name = go.name };
            BuildChunkMesh(cx, cz, cc, mesh);
            mf.sharedMesh = mesh;
            mc.sharedMesh = mesh;
            _chunkMesh[idx] = mesh;
            _chunkCol[idx] = mc;
        }

        void RebuildChunk(int cx, int cz, int cc)
        {
            int idx = cz * _chunksX + cx;
            Mesh mesh = _chunkMesh[idx];
            if (mesh == null) { CreateChunk(cx, cz, cc); return; }
            BuildChunkMesh(cx, cz, cc, mesh);
            _chunkCol[idx].sharedMesh = null;     // force collider re-cook
            _chunkCol[idx].sharedMesh = mesh;
        }

        void BuildChunkMesh(int cx, int cz, int cc, Mesh mesh)
        {
            int x0 = cx * cc, z0 = cz * cc;
            int x1 = Mathf.Min(x0 + cc, _field.ColumnsX - 1);
            int z1 = Mathf.Min(z0 + cc, _field.RowsZ - 1);
            TerrainChunkBuilder.Build(_field, x0, z0, x1, z1, mesh);
        }

        // Rebuild only the chunks overlapping a cell region (after a sculpt).
        void RebuildChunkRegion(int x0, int z0, int w, int h)
        {
            if (_chunkMesh == null) return;
            int cc = ChunkSide;
            int cxa = Mathf.Clamp(x0 / cc, 0, _chunksX - 1);
            int cxb = Mathf.Clamp((x0 + w) / cc, 0, _chunksX - 1);
            int cza = Mathf.Clamp(z0 / cc, 0, _chunksZ - 1);
            int czb = Mathf.Clamp((z0 + h) / cc, 0, _chunksZ - 1);
            for (int cz = cza; cz <= czb; cz++)
                for (int cx = cxa; cx <= cxb; cx++)
                    RebuildChunk(cx, cz, cc);
        }

        static void DestroySafe(GameObject go)
        {
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        // Destroy the tracked chunk root AND any orphaned "TerrainChunks" objects.
        // _chunkRoot isn't serialized, so an edit-mode build or domain reload
        // leaves prior roots with no live reference — they stack up and mask the
        // newest terrain. Sweep them all by name before building a fresh one.
        void DestroyAllChunkRoots()
        {
            _chunkRoot = null;
            GameObject[] all = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == "TerrainChunks") DestroySafe(all[i]);
        }

        // --- Scatter brushes (trees, rocks) via ScatterLayer ---

        // The TreeLayer/RockLayer field initializers only apply to a freshly
        // added component (Unity serialization footgun); a component saved before
        // these fields existed deserializes them blank. Fill sane Name/Folder
        // when unset so the brushes work without a manual component Reset.
        void EnsureScatterDefaults()
        {
            if (TreeLayer == null) TreeLayer = new ScatterLayer();
            if (RockLayer == null) RockLayer = new ScatterLayer();
            if (string.IsNullOrEmpty(TreeLayer.Name) || TreeLayer.Name == "Scatter") TreeLayer.Name = "Trees";
            if (string.IsNullOrEmpty(RockLayer.Name) || RockLayer.Name == "Scatter") RockLayer.Name = "Rocks";
            if (string.IsNullOrEmpty(TreeLayer.Folder)) TreeLayer.Folder = "Assets/Trees";
            if (string.IsNullOrEmpty(RockLayer.Folder)) RockLayer.Folder = "Assets/Rocks";
            // New field on the serialized layer: components saved before it
            // existed deserialize MaxSlopeDeg as 0, which would block ALL
            // placement (nothing is flatter than 0 deg). Treat <=0 as "unset".
            if (TreeLayer.MaxSlopeDeg <= 0f) TreeLayer.MaxSlopeDeg = 35f;
            if (RockLayer.MaxSlopeDeg <= 0f) RockLayer.MaxSlopeDeg = 35f;
            if (FenceLayer == null) FenceLayer = new LineworkLayer();
            if (string.IsNullOrEmpty(FenceLayer.Name) || FenceLayer.Name == "Line") FenceLayer.Name = "Fence";
            if (PowerLineLayer == null) PowerLineLayer = new LineworkLayer();
            if (string.IsNullOrEmpty(PowerLineLayer.Name) || PowerLineLayer.Name == "Line") PowerLineLayer.Name = "PowerLine";
            if (RailLayer == null) RailLayer = new RailTrackLayer();
            if (string.IsNullOrEmpty(RailLayer.Name)) RailLayer.Name = "Rail";
            // New fields on the serialized layer can deserialize as 0 (Unity
            // footgun); 0 lateral-g would blow the required radius up ~100x.
            if (RailLayer.MaxLateralG <= 0f) RailLayer.MaxLateralG = 0.15f;
            if (RailLayer.SpeedLimitKmh <= 0f) RailLayer.SpeedLimitKmh = 40f;
        }

        // Global IMGUI scale so panels/text don't shrink to nothing at high
        // resolution / high-DPI. Applied via GUI.matrix; all panel layout uses
        // the "virtual" screen (Vw/Vh) so right-anchored panels stay flush and
        // MouseOverActivePanel converts the mouse back into the same space.
        public static float UiScale = 1.5f;
        static float Vw => Screen.width / Mathf.Max(0.25f, UiScale);
        static float Vh => Screen.height / Mathf.Max(0.25f, UiScale);

        // Palette IMGUI for the active scatter layer, or a hint for linework.
        void OnGUI()
        {
            if (_lineActive == null && _active == null) return;
            Matrix4x4 prev = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(UiScale, UiScale, 1f));
            DrawPanels();
            GUI.matrix = prev;
        }

        void DrawPanels()
        {
            if (_lineActive != null)
            {
                bool rail = _lineActive is RailTrackLayer;
                GUILayout.BeginArea(new Rect(Vw - 308f, 8f, 300f, rail ? 150f : 104f), GUI.skin.box);
                GUILayout.Label(_lineActive.LayerName + " mode");
                GUILayout.Label(rail
                    ? "Click: straight segment. Hold Shift: click a corner, then the end = curve."
                    : "Left-click: add node (chains)\nRight-click: delete near node / end chain\nBackspace: undo last node");
                if (_lineActive is LineworkLayer lw && lw.Asset == null)
                    GUILayout.Label("Assign an Asset prefab on the\nlayer to see it render.");
                if (_lineActive is RailTrackLayer rt)
                {
                    GUILayout.Label($"Speed {rt.SpeedLimitKmh:0} km/h  →  min radius {rt.MinRadiusForSpeed:0} m");
                    if (rt.LastPreviewRadius < float.PositiveInfinity)
                        GUILayout.Label(rt.LastPreviewTooTight
                            ? $"Curve {rt.LastPreviewRadius:0} m — TOO TIGHT (lower speed or widen)"
                            : $"Curve radius {rt.LastPreviewRadius:0} m — ok");
                }
                GUILayout.EndArea();
                return;
            }
            if (_active == null) return;
            if (_active.DrawPalette()) _dirtySince = Time.realtimeSinceStartup;
        }

        // True when the cursor is over the active layer's palette (so paint/erase
        // and camera zoom are suppressed there). Y is flipped (GUI rect is
        // top-left origin, mouse is bottom-left) and divided by UiScale because
        // PanelRect is stored in the unscaled virtual-screen space.
        bool MouseOverActivePanel()
        {
            if (_active == null || _active.PanelRect.width <= 0f) return false;
            float s = Mathf.Max(0.25f, UiScale);
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / s;
            return _active.PanelRect.Contains(m);
        }

        // Linework rebuild/clear — used by the tuning-panel actions and on edits.
        public void RebuildFence() { FenceLayer.Rebuild(Field); }
        [ContextMenu("Clear Fence")]
        public void ClearFence() { FenceLayer.ClearAll(Field); _dirtySince = Time.realtimeSinceStartup; }
        public void RebuildPowerLine() { PowerLineLayer.Rebuild(Field); }
        [ContextMenu("Clear Power Line")]
        public void ClearPowerLine() { PowerLineLayer.ClearAll(Field); _dirtySince = Time.realtimeSinceStartup; }
        public void RebuildRail() { RailLayer.Rebuild(Field); }
        [ContextMenu("Clear Rail")]
        public void ClearRail() { RailLayer.ClearAll(Field); _dirtySince = Time.realtimeSinceStartup; }

        // Mode switching (mutually exclusive; same key again returns to sculpt).
        void SetScatterMode(ScatterLayer s) { _active = _active == s ? null : s; _lineActive = null; HideLinePreviews(); }
        void SetLineMode(ITerrainLineLayer l) { _lineActive = _lineActive == l ? null : l; _active = null; HideLinePreviews(); }
        void HideLinePreviews() { FenceLayer.HidePreview(); PowerLineLayer.HidePreview(); RailLayer.HidePreview(); }

#if UNITY_EDITOR
        [ContextMenu("Load Trees From Folder")]
        public void LoadTreesFromFolder()
        {
            if (TreeLayer.LoadFromFolder()) UnityEditor.EditorUtility.SetDirty(this);
        }

        [ContextMenu("Load Rocks From Folder")]
        public void LoadRocksFromFolder()
        {
            if (RockLayer.LoadFromFolder()) UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        // If the (empty) scene has no SceneAmbiance, create one configured to
        // light itself — sun + soft shadows + ambient fill + URP shadow range.
        void EnsureAmbiance()
        {
            if (FindFirstObjectByType<SceneAmbiance>() != null) return;
            SceneAmbiance amb = new GameObject("SceneAmbiance").AddComponent<SceneAmbiance>();
            amb.CreateSunIfMissing = true;
            amb.ManageAmbient = true;
            amb.ShadowDistance = ShadowDistance;
            amb.Apply();
        }

        // If the pick camera has no orbit controller, add one framed on the
        // terrain. Left alone if one already exists (respect manual setup).
        void EnsureCameraControl()
        {
            // Prefer the assigned camera, then the tagged main, then ANY camera
            // (an untagged camera is the common scene-setup footgun), and as a
            // last resort create one so even a bare scene is usable.
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) cam = FindFirstObjectByType<Camera>();
            if (cam == null)
            {
                GameObject camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = camGo.AddComponent<Camera>();
            }
            PickCamera = cam; // sculpt raycast uses the same camera

            // Free-fly camera for roaming (the orbit camera fought zooming over a
            // large terrain). Remove any stray orbit controller from a prior setup.
            OrbitCameraController stray = cam.GetComponent<OrbitCameraController>();
            if (stray != null) { if (Application.isPlaying) Destroy(stray); else DestroyImmediate(stray); }

            FlyCameraController fly = cam.GetComponent<FlyCameraController>();
            bool fresh = fly == null;
            if (fresh) fly = cam.gameObject.AddComponent<FlyCameraController>();
            fly.ScrollSuppressor = MouseOverActivePanel;
            fly.GroundHeight = WorldGroundHeight; // terrain-aware altitude clamp
            if (fresh) FrameFly(fly);
        }

        float WorldGroundHeight(Vector3 p) => _field != null ? _field.SampleHeight(p.x, p.z) : 0f;

        // URP render scale — the biggest lever for fill-rate-bound (high-res /
        // full-screen) rendering. Set at runtime on the pipeline asset (in-memory,
        // no asset-file churn). 1 = native; <1 renders fewer pixels and upscales.
        public float RenderScaleValue
        {
            get
            {
                var a = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                return a != null ? a.renderScale : 1f;
            }
            set
            {
                var a = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                if (a != null) a.renderScale = Mathf.Clamp(value, 0.3f, 2f);
            }
        }

        void FrameFly(FlyCameraController fly)
        {
            if (fly == null) return;
            float span = Mathf.Max((Mathf.Max(2, ColumnsX) - 1) * CellSize,
                                   (Mathf.Max(2, RowsZ) - 1) * CellSize);
            fly.Frame(transform.position, span);
        }

        // Drop the fly camera back to a sensible vantage over the terrain — escape
        // hatch if you've roamed off into space.
        [ContextMenu("Reset Camera")]
        public void ResetCamera()
        {
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            FlyCameraController fly = cam != null ? cam.GetComponent<FlyCameraController>() : null;
            if (fly == null) fly = FindFirstObjectByType<FlyCameraController>();
            FrameFly(fly);
        }

        void Update()
        {
            // Brush-mode hotkeys.
            if (Input.GetKeyDown(KeyCode.Alpha1)) Brush = BrushMode.Raise;
            else if (Input.GetKeyDown(KeyCode.Alpha2)) Brush = BrushMode.Lower;
            else if (Input.GetKeyDown(KeyCode.Alpha3)) Brush = BrushMode.Smooth;
            else if (Input.GetKeyDown(KeyCode.Alpha4)) Brush = BrushMode.Flatten;
            // T/R/F toggle the active mode (mutually exclusive; press the same
            // key again to return to sculpt).
            if (Input.GetKeyDown(KeyCode.T)) SetScatterMode(TreeLayer);
            if (Input.GetKeyDown(KeyCode.R)) SetScatterMode(RockLayer);
            if (Input.GetKeyDown(KeyCode.F)) SetLineMode(FenceLayer);
            if (Input.GetKeyDown(KeyCode.P)) SetLineMode(PowerLineLayer);
            if (Input.GetKeyDown(KeyCode.L)) SetLineMode(RailLayer);
            if (Input.GetKeyDown(KeyCode.G))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (shift) SnapToGrid = !SnapToGrid;            // Shift+G: snap toggle
                else { GridEnabled = !GridEnabled; ApplyTerrainMaterial(); } // G: grid toggle
            }
            // Bake thumbnails only while NOT painting — the first render of each
            // prefab compiles its shader variant (a one-time editor stall), and
            // we don't want that landing mid-stroke.
            if (_active != null && !Input.GetMouseButton(0)) _active.EnsureOneThumb();

            // Brush resize: ] bigger, [ smaller (held = continuous).
            if (Input.GetKey(KeyCode.RightBracket)) BrushRadius += BrushResizeRate * Time.deltaTime;
            if (Input.GetKey(KeyCode.LeftBracket)) BrushRadius -= BrushResizeRate * Time.deltaTime;
            BrushRadius = Mathf.Clamp(BrushRadius, 0.5f, MaxBrushRadius);

            if (_field == null) return;

            // Debounced autosave: write once sculpting has paused.
            if (Autosave && _dirtySince >= 0f
                && Time.realtimeSinceStartup - _dirtySince >= AutosaveDebounceSeconds)
            {
                SaveTerrain(); // clears _dirtySince only if a write actually starts
            }

            if (Input.GetMouseButtonDown(0)) _hasFlattenTarget = false;

            // One hover raycast per frame (against the TerrainCollider), shared
            // by the brush cursor and the sculpt itself.
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            bool overTerrain = false;
            RaycastHit hit = default;
            if (cam != null)
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                overTerrain = Physics.Raycast(ray, out hit, 100000f)
                              && hit.collider is MeshCollider;
            }

            UpdateBrushCursor(ShowBrushCursor && overTerrain, hit.point);

            // Linework mode (fence/…): click adds a node + connects from the last
            // (chain); right-click ends the chain; Backspace undoes the last node.
            if (_lineActive != null)
            {
                // Rail: hold Shift = curve mode (else straight). Set before preview
                // and click so both reflect the modifier this frame.
                if (_lineActive is RailTrackLayer railMod)
                    railMod.CurveModifier = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

                // Snap node placement to grid intersections when enabled (the
                // preview shows the snapped point too). Deletes use the raw hit.
                Vector3 place = ApplyGridSnap(hit.point);
                _lineActive.UpdatePreview(_field, place, overTerrain);
                if (overTerrain && Input.GetMouseButtonDown(0))
                {
                    _lineActive.AddNode(_field, place);
                    _dirtySince = Time.realtimeSinceStartup;
                }
                if (Input.GetMouseButtonDown(1))
                {
                    // Right-click near a node deletes it; otherwise ends the chain.
                    if (overTerrain && _lineActive.DeleteNearNode(_field, hit.point, 3f))
                        _dirtySince = Time.realtimeSinceStartup;
                    else _lineActive.EndChain();
                }
                if (Input.GetKeyDown(KeyCode.Backspace))
                {
                    _lineActive.RemoveLastNode(_field);
                    _dirtySince = Time.realtimeSinceStartup;
                }
                return;
            }

            // Scatter mode (trees/rocks): left-drag paints, right-drag erases;
            // no sculpting. Suppressed while the cursor is over the palette.
            if (_active != null)
            {
                bool overPanel = MouseOverActivePanel();
                if (!overPanel && overTerrain && Input.GetMouseButton(0)
                    && _active.Paint(_field, hit.point, Time.deltaTime, BrushRadius))
                    _dirtySince = Time.realtimeSinceStartup;
                if (!overPanel && overTerrain && Input.GetMouseButton(1)
                    && _active.Erase(hit.point, BrushRadius))
                    _dirtySince = Time.realtimeSinceStartup;
                return;
            }

            // Refresh contours when a stroke ends (cheap path); live rebuild
            // during the drag is opt-in via LiveContours.
            if (Input.GetMouseButtonUp(0)) RebuildContours();

            if (!overTerrain || !Input.GetMouseButton(0)) return;

            if (!_hasFlattenTarget)
            {
                GridFromWorld(hit.point, out float cfx, out float cfz);
                _flattenTarget = HeightAtGrid(cfx, cfz);
                _hasFlattenTarget = true;
            }

            // Sculpt the field, then push ONLY the brush-affected heightmap
            // region to the Terrain (cheap — never the whole 4M-cell map).
            ApplyBrush(hit.point, Time.deltaTime);
            GridFromWorld(hit.point, out float bfx, out float bfz);
            int rad = Mathf.CeilToInt(BrushRadius / Mathf.Max(0.01f, _field.CellSize)) + 1;
            int rx0 = Mathf.RoundToInt(bfx) - rad, rz0 = Mathf.RoundToInt(bfz) - rad;
            int rw = rad * 2 + 1;
            RebuildChunkRegion(rx0, rz0, rw, rw);   // rebuild touched chunk meshes + colliders
            _dirtySince = Time.realtimeSinceStartup;
            if (LiveContours) RebuildContours();
        }

        // World hit -> fractional grid coords, relative to the terrain corner
        // (Origin). Unity Terrain is axis-aligned and corner-anchored.
        void GridFromWorld(Vector3 worldHit, out float fx, out float fz)
        {
            float cs = _field.CellSize;
            fx = (worldHit.x - _field.Origin.x) / cs;
            fz = (worldHit.z - _field.Origin.z) / cs;
        }

        // Bilinear height (field offset space) at fractional grid coords.
        float HeightAtGrid(float fx, float fz)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, _field.ColumnsX - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, _field.RowsZ - 1);
            int x1 = Mathf.Min(x0 + 1, _field.ColumnsX - 1);
            int z1 = Mathf.Min(z0 + 1, _field.RowsZ - 1);
            float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
            float h0 = Mathf.Lerp(_field.GetHeight(x0, z0), _field.GetHeight(x1, z0), tx);
            float h1 = Mathf.Lerp(_field.GetHeight(x0, z1), _field.GetHeight(x1, z1), tx);
            return Mathf.Lerp(h0, h1, tz);
        }

        // A ring at the hovered point showing the brush footprint, conforming
        // to the terrain surface (each point sampled via HeightAtGrid) and
        // transform-correct (built in local space, then TransformPoint'd).
        // Rendered as a line mesh (like the contours) so it can be dashed.
        void UpdateBrushCursor(bool visible, Vector3 worldCenter)
        {
            EnsureCursor();
            _cursorMr.enabled = visible;
            if (!visible) return;
            if (_cursorMat != null) _cursorMat.color = BrushCursorColor;

            int n = Mathf.Max(8, BrushCursorSegments);

            // Conforming ring points in world space; the cursor mesh object
            // lives at world identity so these render as-is.
            _ring.Clear();
            for (int i = 0; i < n; i++)
            {
                float ang = (i / (float)n) * Mathf.PI * 2f;
                float wx = worldCenter.x + Mathf.Cos(ang) * BrushRadius;
                float wz = worldCenter.z + Mathf.Sin(ang) * BrushRadius;
                float wy = _field.SampleHeight(wx, wz) + BrushCursorLift;
                _ring.Add(new Vector3(wx, wy, wz));
            }

            // Build the closed-loop line mesh, optionally dashed.
            _cursorVerts.Clear();
            _cursorIdx.Clear();
            float dash = BrushCursorDashed ? BrushCursorDashLength : 0f;
            for (int i = 0; i < n; i++)
                EmitCursorSegment(_ring[i], _ring[(i + 1) % n], dash, BrushCursorDashGap);

            _cursorMesh.Clear();
            _cursorMesh.SetVertices(_cursorVerts);
            _cursorMesh.SetIndices(_cursorIdx, MeshTopology.Lines, 0);
            _cursorMesh.RecalculateBounds();
            _cursorMf.sharedMesh = _cursorMesh;
        }

        // Like TerrainContourBuilder.EmitSegment: one line a->b, or dash/gap
        // pieces. Phase restarts per ring edge — even spacing on a regular ring.
        void EmitCursorSegment(Vector3 a, Vector3 b, float dash, float gap)
        {
            if (dash <= 0f)
            {
                int s = _cursorVerts.Count;
                _cursorVerts.Add(a); _cursorVerts.Add(b);
                _cursorIdx.Add(s); _cursorIdx.Add(s + 1);
                return;
            }
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-5f) return;
            Vector3 dir = d / len;
            float period = dash + Mathf.Max(0f, gap);
            for (float pos = 0f; pos < len; pos += period)
            {
                float e0 = pos, e1 = Mathf.Min(pos + dash, len);
                int s = _cursorVerts.Count;
                _cursorVerts.Add(a + dir * e0); _cursorVerts.Add(a + dir * e1);
                _cursorIdx.Add(s); _cursorIdx.Add(s + 1);
            }
        }

        void EnsureCursor()
        {
            if (_cursorMf != null) return;
            // Root object at world identity — the ring verts are world-space.
            GameObject go = new GameObject("BrushCursor");
            _cursorMf = go.AddComponent<MeshFilter>();
            _cursorMr = go.AddComponent<MeshRenderer>();
            _cursorMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cursorMr.receiveShadows = false;
            _cursorMesh = new Mesh { name = "BrushCursorMesh" };
            _cursorMf.sharedMesh = _cursorMesh;
            // Always-on-top so the ring isn't occluded by terrain relief.
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            _cursorMat = sh != null
                ? new Material(sh) { name = "BrushCursorMat", color = BrushCursorColor }
                : PipelineMaterials.CreateUnlitColor(BrushCursorColor, "BrushCursorMat");
            _cursorMr.sharedMaterial = _cursorMat;
        }

        // Rebuild the topographic contour lines from the current field.
        [ContextMenu("Rebuild Contours")]
        public void RebuildContours()
        {
            EnsureContours();
            // Contours over the full 2 km / 1 m heightmap (~4M cells) are far
            // too heavy to rebuild whole; that's a later region-based pass.
            // Skip above a cell budget for now.
            long cells = _field != null ? (long)_field.ColumnsX * _field.RowsZ : 0;
            if (_field == null || !ShowContours || ContourInterval <= 0f || cells > 300000)
            {
                if (_contourMr != null) _contourMr.enabled = false;
                return;
            }
            _contourMr.enabled = true;
            if (_contourMat != null) _contourMat.color = ContourColor;
            TerrainContourBuilder.Build(_field, ContourInterval, ContourLift,
                ContourDashed ? ContourDashLength : 0f, ContourDashGap, _contourMesh);
            _contourMf.sharedMesh = _contourMesh;
        }

        void EnsureContours()
        {
            if (_contourMf != null) return;
            GameObject go = new GameObject("ContourLines");
            go.transform.SetParent(transform, worldPositionStays: false);
            _contourMf = go.AddComponent<MeshFilter>();
            _contourMr = go.AddComponent<MeshRenderer>();
            _contourMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _contourMr.receiveShadows = false;
            _contourMesh = new Mesh { name = "TerrainContours" };
            _contourMf.sharedMesh = _contourMesh;
            _contourMat = PipelineMaterials.CreateUnlitColor(ContourColor, "ContourMat");
            _contourMr.sharedMaterial = _contourMat;
        }

        // Modify the heightfield under the brush, in field (height-offset) space.
        void ApplyBrush(Vector3 worldHit, float dt)
        {
            float cs = _field.CellSize;
            // Map the world hit to grid space through the GameObject transform
            // (handles any position/rotation/scale), NOT world-Origin algebra.
            GridFromWorld(worldHit, out float fx, out float fz);
            int cx0 = Mathf.RoundToInt(fx);
            int cz0 = Mathf.RoundToInt(fz);
            int rad = Mathf.Max(1, Mathf.CeilToInt(BrushRadius / cs));

            // Effective Raise/Lower rate = Strength^exp. exp is floored at 1 so a
            // stale/zero serialized value can't collapse it to a constant 1 m/s.
            float exp = Mathf.Max(1f, BrushStrengthExponent);
            float effStrength = Mathf.Pow(Mathf.Max(0f, BrushStrength), exp);

            for (int dz = -rad; dz <= rad; dz++)
            {
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int x = cx0 + dx, z = cz0 + dz;
                    if (!_field.InRange(x, z)) continue;

                    // Metres from the brush centre (use the float centre).
                    float mx = (x - fx) * cs, mz = (z - fz) * cs;
                    float dist = Mathf.Sqrt(mx * mx + mz * mz);
                    if (dist > BrushRadius) continue;

                    float tEdge = BrushRadius > 0f ? dist / BrushRadius : 0f;
                    // BrushFalloff blends between a flat (hard) profile and a
                    // smoothstep that eases to zero at the rim.
                    float soft = 1f - Mathf.SmoothStep(0f, 1f, tEdge);
                    float w = Mathf.Lerp(1f - tEdge, soft, BrushFalloff);

                    float h = _field.GetHeight(x, z);
                    switch (Brush)
                    {
                        case BrushMode.Raise:
                            h += effStrength * dt * w;
                            break;
                        case BrushMode.Lower:
                            h -= effStrength * dt * w;
                            break;
                        case BrushMode.Flatten:
                            h = Mathf.Lerp(h, _flattenTarget, Mathf.Clamp01(dt * 4f * w));
                            break;
                        case BrushMode.Smooth:
                            h = Mathf.Lerp(h, NeighborAverage(x, z), Mathf.Clamp01(dt * 4f * w));
                            break;
                    }
                    _field.SetHeight(x, z, h);
                }
            }
        }

        // Average of the in-range 4-neighbours (falls back to self at edges).
        float NeighborAverage(int x, int z)
        {
            float sum = 0f; int n = 0;
            if (_field.InRange(x - 1, z)) { sum += _field.GetHeight(x - 1, z); n++; }
            if (_field.InRange(x + 1, z)) { sum += _field.GetHeight(x + 1, z); n++; }
            if (_field.InRange(x, z - 1)) { sum += _field.GetHeight(x, z - 1); n++; }
            if (_field.InRange(x, z + 1)) { sum += _field.GetHeight(x, z + 1); n++; }
            return n > 0 ? sum / n : _field.GetHeight(x, z);
        }

        // (Re)create the field to match the Terrain's heightmap. Origin is the
        // terrain's world corner; CellSize = terrain size / (resolution - 1).
        void EnsureField(bool forceRebuild)
        {
            float size = Mathf.Max(1f, TerrainSizeMeters);
            float cs = Mathf.Max(0.1f, CellSize);
            int res = Mathf.Max(2, Mathf.RoundToInt(size / cs) + 1); // vertices per side
            float half = (res - 1) * cs * 0.5f;
            Vector3 origin = transform.position - new Vector3(half, 0f, half); // centered on this object

            bool fresh = _field == null || _field.ColumnsX != res || _field.RowsZ != res;
            if (fresh)
            {
                _field = new TerrainField(res, res, cs, origin);
                if (TestHill) StampTestHill();
            }
            else if (forceRebuild)
            {
                _field.CellSize = cs;
                _field.Origin = origin;
            }

            // Keep the (hidden) public dims in sync for camera framing etc.
            ColumnsX = _field.ColumnsX;
            RowsZ = _field.RowsZ;
            CellSize = _field.CellSize;
        }

        // Full reset: new flat field (+ optional test hill) and rebuild.
        [ContextMenu("Reset Terrain")]
        public void ResetTerrain()
        {
            _field = null;
            EnsureField(forceRebuild: true);
            BuildAllChunks();
            RebuildContours();
            _dirtySince = Time.realtimeSinceStartup; // persist the reset
        }

        // Zero all heights in place (keeps the current grid size). Reliable
        // flat slate regardless of the test-hill / size settings.
        [ContextMenu("Flatten Terrain")]
        public void FlattenTerrain()
        {
            if (_field == null) EnsureField(forceRebuild: true);
            System.Array.Clear(_field.Heights, 0, _field.Heights.Length);
            BuildAllChunks();
            RebuildContours();
            _dirtySince = Time.realtimeSinceStartup;
        }

        // Bake a grayscale heightmap into the current field (REPLACES all
        // heights), then rebuild + conform trees. Read via File+LoadImage so the
        // source needn't be a Read/Write-Enabled asset (or even under Assets/).
        // Bilinear-sampled, so any image size maps onto the grid.
        [ContextMenu("Import Heightmap")]
        public void ImportHeightmap()
        {
            if (_field == null) EnsureField(forceRebuild: true);
            string path = ResolveHeightmapPath();
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"[TerrainDesigner] Heightmap not found: {path}");
                return;
            }
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            bool decoded;
            try { decoded = tex.LoadImage(System.IO.File.ReadAllBytes(path)); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Heightmap read failed: {ex.Message}");
                DestroyTex(tex);
                return;
            }
            if (!decoded)
            {
                Debug.LogWarning($"[TerrainDesigner] Heightmap decode failed: {path}");
                DestroyTex(tex);
                return;
            }

            int iw = tex.width, ih = tex.height;
            Color[] px = tex.GetPixels();
            DestroyTex(tex); // px is a managed copy; safe to free the texture now
            if (px == null || iw < 1 || ih < 1 || px.Length < iw * ih)
            {
                Debug.LogWarning($"[TerrainDesigner] Heightmap pixel read mismatch: " +
                                 $"{iw}x{ih}, {(px == null ? 0 : px.Length)} px.");
                return;
            }

            // Rebuild the field at the CONFIGURED resolution (TerrainSizeMeters /
            // CellSize) before writing. A stale/corrupt _field is what produced
            // both the earlier IndexOutOfRange and the wrong 64x64 grid — don't
            // trust its dims; re-derive them.
            EnsureField(forceRebuild: true);
            int cx = _field.ColumnsX, rz = _field.RowsZ;
            if (_field.Heights == null || _field.Heights.Length != cx * rz)
            {
                Debug.LogWarning($"[TerrainDesigner] Field still inconsistent " +
                                 $"({cx}x{rz} vs {(_field.Heights == null ? 0 : _field.Heights.Length)} heights); rebuilding.");
                _field = new TerrainField(cx, rz, _field.CellSize, _field.Origin);
                cx = _field.ColumnsX; rz = _field.RowsZ;
            }

            for (int z = 0; z < rz; z++)
            {
                float v = rz > 1 ? (float)z / (rz - 1) : 0f;
                for (int x = 0; x < cx; x++)
                {
                    float u = cx > 1 ? (float)x / (cx - 1) : 0f;
                    _field.SetHeight(x, z, SampleBilinearGray(px, iw, ih, u, v) * HeightmapMaxHeight);
                }
            }
            SmoothHeights(HeightmapSmoothPasses);

            // Force the recreate path (which sweeps ALL chunk roots, including
            // orphans) so the mesh can't lag behind the new heights or stack up.
            _chunkMesh = null; _chunkCol = null;
            BuildAllChunks();
            TreeLayer.ConformToSurface(_field);
            RockLayer.ConformToSurface(_field);
            FenceLayer.Rebuild(_field); // re-place linework on the new surface
            PowerLineLayer.Rebuild(_field);
            RailLayer.Rebuild(_field);
            RebuildContours();
            _dirtySince = Time.realtimeSinceStartup;

            float minH = float.MaxValue, maxH = float.MinValue;
            float[] hh = _field.Heights;
            for (int i = 0; i < hh.Length; i++) { if (hh[i] < minH) minH = hh[i]; if (hh[i] > maxH) maxH = hh[i]; }
            Debug.Log($"[TerrainDesigner] Imported {iw}x{ih} → grid {cx}x{rz}, white={HeightmapMaxHeight} m, " +
                      $"{HeightmapSmoothPasses} smooth pass(es); actual height range {minH:F1}..{maxH:F1} m.");
        }

        // Resolve to a FULLY NORMALIZED absolute path (Mono's File.Exists won't
        // reliably resolve an embedded ".."). Searches: project folder, repo
        // root (one up), then Assets/ — returns the first that exists, else the
        // primary candidate (so the warning shows a clean path).
        string ResolveHeightmapPath()
        {
            if (System.IO.Path.IsPathRooted(HeightmapPath))
                return System.IO.Path.GetFullPath(HeightmapPath);
#if UNITY_EDITOR
            string baseDir = System.IO.Path.Combine(Application.dataPath, "..");
#else
            string baseDir = Application.persistentDataPath;
#endif
            string[] candidates =
            {
                System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, HeightmapPath)),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", HeightmapPath)),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, HeightmapPath)),
            };
            foreach (string c in candidates)
                if (System.IO.File.Exists(c)) return c;
            return candidates[0];
        }

        // Bilinear gray sample (uses the red channel; grayscale has r=g=b) at
        // normalized (u,v) over a w×h pixel buffer.
        static float SampleBilinearGray(Color[] px, int w, int h, float u, float v)
        {
            float fx = Mathf.Clamp01(u) * (w - 1);
            float fy = Mathf.Clamp01(v) * (h - 1);
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = fx - x0, ty = fy - y0;
            float g00 = px[y0 * w + x0].r, g10 = px[y0 * w + x1].r;
            float g01 = px[y1 * w + x0].r, g11 = px[y1 * w + x1].r;
            return Mathf.Lerp(Mathf.Lerp(g00, g10, tx), Mathf.Lerp(g01, g11, tx), ty);
        }

        // In-place box blur of the height grid (self + 4 neighbours per pass).
        // Edge cells average only their existing neighbours.
        void SmoothHeights(int passes)
        {
            if (passes <= 0 || _field == null) return;
            int cx = _field.ColumnsX, rz = _field.RowsZ;
            float[] src = _field.Heights;
            float[] tmp = new float[src.Length];
            for (int p = 0; p < passes; p++)
            {
                for (int z = 0; z < rz; z++)
                {
                    for (int x = 0; x < cx; x++)
                    {
                        int i = z * cx + x;
                        float sum = src[i];
                        int n = 1;
                        if (x > 0)      { sum += src[i - 1];  n++; }
                        if (x < cx - 1) { sum += src[i + 1];  n++; }
                        if (z > 0)      { sum += src[i - cx]; n++; }
                        if (z < rz - 1) { sum += src[i + cx]; n++; }
                        tmp[i] = sum / n;
                    }
                }
                System.Array.Copy(tmp, src, src.Length);
            }
        }

        static void DestroyTex(Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying) Destroy(tex); else DestroyImmediate(tex);
        }

        void StampTestHill()
        {
            int cx = _field.ColumnsX, rz = _field.RowsZ;
            float cxh = (cx - 1) * 0.5f, rzh = (rz - 1) * 0.5f;
            float sigma = Mathf.Max(1f, Mathf.Min(cx, rz) * 0.18f);
            float twoSigSq = 2f * sigma * sigma;
            for (int z = 0; z < rz; z++)
            {
                for (int x = 0; x < cx; x++)
                {
                    float dx = x - cxh, dz = z - rzh;
                    float g = Mathf.Exp(-(dx * dx + dz * dz) / twoSigSq);
                    _field.SetHeight(x, z, g * TestHillHeight);
                }
            }
        }

        // -----------------------------------------------------------------
        // Save / load (JSON, mirrors the road designer's autosave)
        // -----------------------------------------------------------------

        void OnDisable()
        {
            // Let any in-flight async write finish so the file isn't half-written.
            try { _saveTask?.Wait(3000); } catch { /* ignore */ }
            // Flush any pending edits synchronously when Play stops / disabled.
            if (Autosave && _dirtySince >= 0f)
            {
                WriteSave(BuildSnapshot(), ResolveAutosavePath());
                _dirtySince = -1f;
            }
        }

        string ResolveAutosavePath()
        {
            if (!string.IsNullOrEmpty(AutosavePath)) return AutosavePath;
#if UNITY_EDITOR
            return System.IO.Path.Combine(Application.dataPath, "..", "TerrainAutosave.json");
#else
            return System.IO.Path.Combine(Application.persistentDataPath, "TerrainAutosave.json");
#endif
        }

        // Debounced autosave entry point. The expensive part (JSON serialize +
        // disk write) runs on a background thread so it doesn't hitch the frame;
        // the snapshot (which touches Unity/field state) is built on the main
        // thread first. Skips if a previous async write is still running — the
        // dirty flag will re-trigger shortly, and overlapping writes are avoided.
        public void SaveTerrain()
        {
            if (_field == null) return;
            if (_saveTask != null && !_saveTask.IsCompleted) return;
            try
            {
                TerrainSave save = BuildSnapshot();
                string path = ResolveAutosavePath();
                _saveTask = System.Threading.Tasks.Task.Run(() => WriteSave(save, path));
                _dirtySince = -1f; // a write is now in flight for the current state
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Save failed: {ex.Message}");
            }
        }

        // Snapshot the current field + trees + packs into an owned, immutable
        // payload safe to serialize off the main thread. Main-thread only.
        TerrainSave BuildSnapshot()
        {
            // Sparse: store only altered (non-zero) heights; zeros implied.
            float[] heights = _field.Heights;
            var idx = new List<int>();
            var hs = new List<float>();
            for (int i = 0; i < heights.Length; i++)
            {
                if (Mathf.Abs(heights[i]) > 1e-4f) { idx.Add(i); hs.Add(heights[i]); }
            }
            return new TerrainSave
            {
                ColumnsX = _field.ColumnsX,
                RowsZ = _field.RowsZ,
                CellSize = _field.CellSize,
                Idx = idx.ToArray(),
                H = hs.ToArray(),
                Trees = TreeLayer.CollectData(),
                Packs = TreeLayer.CollectPacks(),
                Rocks = RockLayer.CollectData(),
                RockPacks = RockLayer.CollectPacks(),
                Fences = FenceLayer.CollectData(),
                PowerLines = PowerLineLayer.CollectData(),
                Rails = RailLayer.CollectData(),
            };
        }

        const int SaveMagic = 0x54524E33; // "TRN3"

        // BINARY serialize + write — primitives straight to a byte buffer, so a
        // dense (heightmap-imported) terrain doesn't allocate megabytes of JSON
        // strings and trip a stop-the-world GC mid-frame. Thread-safe.
        static void WriteSave(TerrainSave save, string path)
        {
            try
            {
                int n = (save.Idx != null && save.H != null)
                    ? Mathf.Min(save.Idx.Length, save.H.Length) : 0;
                int cap = 64 + n * 8
                          + TreeBytes(save.Trees) + TreeBytes(save.Rocks)
                          + GraphBytes(save.Fences) + GraphBytes(save.PowerLines)
                          + GraphBytes(save.Rails) + 256;
                using var ms = new System.IO.MemoryStream(cap);
                using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                {
                    w.Write(SaveMagic);
                    w.Write(4); // version (4 added per-edge SpeedLimit to graphs)
                    w.Write(save.ColumnsX);
                    w.Write(save.RowsZ);
                    w.Write(save.CellSize);
                    w.Write(n);
                    for (int i = 0; i < n; i++) w.Write(save.Idx[i]);
                    for (int i = 0; i < n; i++) w.Write(save.H[i]);
                    WriteTrees(w, save.Trees);
                    WritePacks(w, save.Packs);
                    WriteTrees(w, save.Rocks);
                    WritePacks(w, save.RockPacks);
                    WriteGraph(w, save.Fences);
                    WriteGraph(w, save.PowerLines);
                    WriteGraph(w, save.Rails);
                }
                System.IO.File.WriteAllBytes(path, ms.ToArray());
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Save write failed: {ex.Message}");
            }
        }

        static int TreeBytes(List<PlacedTreeData> t) => (t?.Count ?? 0) * 40 + 8;
        static int GraphBytes(LineGraphSave g) =>
            ((g?.Nodes?.Count ?? 0) * 8) + ((g?.Edges?.Count ?? 0) * 32) + 16;

        static void WriteTrees(System.IO.BinaryWriter w, List<PlacedTreeData> list)
        {
            int c = list?.Count ?? 0;
            w.Write(c);
            for (int i = 0; i < c; i++)
            {
                PlacedTreeData d = list[i];
                w.Write(d?.Prefab ?? "");
                w.Write(d != null ? d.Position.x : 0f);
                w.Write(d != null ? d.Position.y : 0f);
                w.Write(d != null ? d.RotationY : 0f);
                w.Write(d != null ? d.Scale : 1f);
            }
        }

        static void WritePacks(System.IO.BinaryWriter w, List<TreePack> packs)
        {
            int c = packs?.Count ?? 0;
            w.Write(c);
            for (int i = 0; i < c; i++)
            {
                TreePack p = packs[i];
                w.Write(p?.Name ?? "");
                int m = p?.Trees?.Count ?? 0;
                w.Write(m);
                for (int j = 0; j < m; j++) w.Write(p.Trees[j] ?? "");
            }
        }

        static void WriteGraph(System.IO.BinaryWriter w, LineGraphSave g)
        {
            int nn = g?.Nodes?.Count ?? 0;
            w.Write(nn);
            for (int i = 0; i < nn; i++) { w.Write(g.Nodes[i].x); w.Write(g.Nodes[i].y); }
            int ne = g?.Edges?.Count ?? 0;
            w.Write(ne);
            for (int i = 0; i < ne; i++)
            {
                LineEdge e = g.Edges[i];
                w.Write(e.A); w.Write(e.B);
                w.Write(e.HasCurve);                       // save format v3+
                w.Write(e.ControlA.x); w.Write(e.ControlA.y);
                w.Write(e.ControlB.x); w.Write(e.ControlB.y);
                w.Write(e.SpeedLimit);                     // v4+
            }
        }

        TerrainField TryLoadTerrain()
        {
            try
            {
                string path = ResolveAutosavePath();
                if (!System.IO.File.Exists(path)) return null;
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                TerrainSave save = ReadSaveBinary(bytes);
                if (save == null)
                {
                    // Migrate an old JSON autosave: read it once (slow path is fine
                    // on a one-time load) — it re-saves as binary thereafter.
                    try
                    {
                        save = JsonConvert.DeserializeObject<TerrainSave>(
                            System.Text.Encoding.UTF8.GetString(bytes), TerrainJsonSettings);
                    }
                    catch { save = null; }
                }
                if (save == null || save.ColumnsX < 2 || save.RowsZ < 2) return null;

                float cs = save.CellSize > 0f ? save.CellSize : 1f;
                TerrainField f = new TerrainField(save.ColumnsX, save.RowsZ, cs, Vector3.zero);
                if (save.Idx != null && save.H != null)
                {
                    int n = Mathf.Min(save.Idx.Length, save.H.Length);
                    for (int k = 0; k < n; k++)
                    {
                        int i = save.Idx[k];
                        if (i >= 0 && i < f.Heights.Length) f.Heights[i] = save.H[k];
                    }
                }
                // Stage scatter data + packs; layers SpawnPending after chunks build.
                TreeLayer.LoadState(save.Trees, save.Packs);
                RockLayer.LoadState(save.Rocks, save.RockPacks);
                FenceLayer.LoadState(save.Fences); // Rebuilt after chunks
                PowerLineLayer.LoadState(save.PowerLines);
                RailLayer.LoadState(save.Rails);
                return f;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Load failed: {ex.Message} — starting fresh.");
                return null;
            }
        }

        // Parse the binary save. Returns null if the bytes aren't our format
        // (e.g. an old JSON autosave) — caller then starts fresh.
        static TerrainSave ReadSaveBinary(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8) return null;
            try
            {
                using var ms = new System.IO.MemoryStream(bytes);
                using var r = new System.IO.BinaryReader(ms, System.Text.Encoding.UTF8);
                if (r.ReadInt32() != SaveMagic) return null;
                int version = r.ReadInt32();
                var s = new TerrainSave
                {
                    ColumnsX = r.ReadInt32(),
                    RowsZ = r.ReadInt32(),
                    CellSize = r.ReadSingle(),
                };
                int n = r.ReadInt32();
                s.Idx = new int[n];
                for (int i = 0; i < n; i++) s.Idx[i] = r.ReadInt32();
                s.H = new float[n];
                for (int i = 0; i < n; i++) s.H[i] = r.ReadSingle();
                s.Trees = ReadTrees(r);
                s.Packs = ReadPacks(r);
                s.Rocks = ReadTrees(r);
                s.RockPacks = ReadPacks(r);
                s.Fences = ReadGraph(r, version);
                s.PowerLines = ReadGraph(r, version);
                if (version >= 2) s.Rails = ReadGraph(r, version); // older saves have no rails
                return s;
            }
            catch { return null; }
        }

        static List<PlacedTreeData> ReadTrees(System.IO.BinaryReader r)
        {
            int c = r.ReadInt32();
            var list = new List<PlacedTreeData>(c);
            for (int i = 0; i < c; i++)
                list.Add(new PlacedTreeData
                {
                    Prefab = r.ReadString(),
                    Position = new Vector2(r.ReadSingle(), r.ReadSingle()),
                    RotationY = r.ReadSingle(),
                    Scale = r.ReadSingle(),
                });
            return list;
        }

        static List<TreePack> ReadPacks(System.IO.BinaryReader r)
        {
            int c = r.ReadInt32();
            var list = new List<TreePack>(c);
            for (int i = 0; i < c; i++)
            {
                var p = new TreePack { Name = r.ReadString(), Trees = new List<string>() };
                int m = r.ReadInt32();
                for (int j = 0; j < m; j++) p.Trees.Add(r.ReadString());
                list.Add(p);
            }
            return list;
        }

        static LineGraphSave ReadGraph(System.IO.BinaryReader r, int version)
        {
            var g = new LineGraphSave { Nodes = new List<Vector2>(), Edges = new List<LineEdge>() };
            int nn = r.ReadInt32();
            for (int i = 0; i < nn; i++) g.Nodes.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
            int ne = r.ReadInt32();
            for (int i = 0; i < ne; i++)
            {
                var e = new LineEdge(r.ReadInt32(), r.ReadInt32());
                if (version >= 3) // curve controls added in v3
                {
                    e.HasCurve = r.ReadBoolean();
                    e.ControlA = new Vector2(r.ReadSingle(), r.ReadSingle());
                    e.ControlB = new Vector2(r.ReadSingle(), r.ReadSingle());
                }
                if (version >= 4) e.SpeedLimit = r.ReadSingle(); // section speed
                g.Edges.Add(e);
            }
            return g;
        }

        static JsonSerializerSettings _terrainJsonSettings;
        static JsonSerializerSettings TerrainJsonSettings
        {
            get
            {
                if (_terrainJsonSettings == null)
                    _terrainJsonSettings = new JsonSerializerSettings
                    {
                        Formatting = Formatting.Indented,
                        Converters = new List<JsonConverter>
                            { new Vector3JsonConverter(), new Vector2JsonConverter() },
                        NullValueHandling = NullValueHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                    };
                return _terrainJsonSettings;
            }
        }

        // Vector3 as { x, y, z } — keeps Newtonsoft from chasing the derived
        // properties (normalized/magnitude) on UnityEngine.Vector3.
        class Vector3JsonConverter : JsonConverter<Vector3>
        {
            public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("x"); writer.WriteValue(value.x);
                writer.WritePropertyName("y"); writer.WriteValue(value.y);
                writer.WritePropertyName("z"); writer.WriteValue(value.z);
                writer.WriteEndObject();
            }

            public override Vector3 ReadJson(JsonReader reader, System.Type objectType,
                Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                Newtonsoft.Json.Linq.JObject jo = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new Vector3(
                    jo["x"]?.ToObject<float>() ?? 0f,
                    jo["y"]?.ToObject<float>() ?? 0f,
                    jo["z"]?.ToObject<float>() ?? 0f);
            }
        }

        // Vector2 as { x, y } — for PlacedTreeData.Position (tree XZ).
        class Vector2JsonConverter : JsonConverter<Vector2>
        {
            public override void WriteJson(JsonWriter writer, Vector2 value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("x"); writer.WriteValue(value.x);
                writer.WritePropertyName("y"); writer.WriteValue(value.y);
                writer.WriteEndObject();
            }

            public override Vector2 ReadJson(JsonReader reader, System.Type objectType,
                Vector2 existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                Newtonsoft.Json.Linq.JObject jo = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new Vector2(
                    jo["x"]?.ToObject<float>() ?? 0f,
                    jo["y"]?.ToObject<float>() ?? 0f);
            }
        }
    }
}
