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
        [Tooltip("Flat terrain color (single lit color for now; height/slope vertex-color bands later).")]
        public Color TerrainColor = new Color(0.40f, 0.5f, 0.30f);

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

        [Header("Tree brush")]
        [Tooltip("Press T to toggle Tree mode: left-drag PAINTS trees in the " +
                 "brush, right-drag ERASES them. Sculpt is disabled while on.")]
        public bool TreeMode = false;
        [Tooltip("Low-poly tree prefabs to scatter (one picked at random per tree).")]
        public List<GameObject> TreePrefabs = new List<GameObject>();
        [Tooltip("Trees painted per second while dragging.")]
        public float TreePaintRate = 25f;
        [Tooltip("Lattice spacing (m): trees fill a jittered grid of this cell " +
                 "size — controls density (smaller = denser). At most one tree " +
                 "per cell, so repainting the same spot adds nothing.")]
        public float TreeMinSpacing = 4f;
        [Tooltip("Random uniform scale range applied to each tree.")]
        public Vector2 TreeScaleRange = new Vector2(1.2f, 1.95f);

        [Header("Initial relief (stamped once)")]
        [Tooltip("Stamp a smooth gaussian hill when the field is first built, " +
                 "so there's something to sculpt. Does NOT re-apply on rebuild.")]
        public bool TestHill = false;
        public float TestHillHeight = 80f;

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
        GameObject _treeRoot;
        readonly List<PlacedTree> _trees = new List<PlacedTree>();
        // Trees live on a JITTERED LATTICE keyed by cell: at most one tree per
        // TreeMinSpacing-sized cell, placed at a deterministic jittered point.
        // Even spacing is structural, so painting never scans neighbours — it
        // just fills the unoccupied cells under the brush. This dict is the
        // occupancy + erase index; _trees stays the canonical list for save.
        readonly Dictionary<long, PlacedTree> _treeByCell = new Dictionary<long, PlacedTree>();
        readonly List<long> _candKey = new List<long>();   // reused paint scratch
        readonly List<Vector2> _candPos = new List<Vector2>();
        const float TreeJitter = 0.4f; // max per-axis jitter as a fraction of the cell (<0.5 keeps the point in-cell)
        float _treeAccum;                         // fractional trees pending this frame
        List<PlacedTreeData> _pendingTrees;       // loaded trees, spawned after chunks build
        bool[] _treeEnabled;                      // which TreePrefabs the brush may use
        string[] _treeLabels;                     // cached toggle labels (avoid per-repaint concat)
        Vector2 _treeScroll;
        Rect _treePanelRect;                      // palette rect (screen space) — block painting over it
        readonly Dictionary<GameObject, Texture2D> _treeThumbs = new Dictionary<GameObject, Texture2D>();
        readonly List<TreePack> _packs = new List<TreePack>(); // saved include/exclude presets
        int _activePack = -1;                     // index into _packs; -1 = custom selection
        string _newPackName = "";                 // pack-name text field buffer
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
            SpawnLoadedTrees(); // trees from the save (surface heights now known)
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

        // --- Chunked flat-shaded mesh ---

        void EnsureMaterial()
        {
            if (_mat == null) _mat = PipelineMaterials.CreateLitMatte(TerrainColor, "TerrainMat");
            else _mat.color = TerrainColor;
        }

        // Live color tweak from the tuning panel — no mesh rebuild needed.
        public void ApplyTerrainColor()
        {
            if (_mat != null) _mat.color = TerrainColor;
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
                if (_chunkRoot != null) DestroySafe(_chunkRoot);
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

        // --- Tree brush ---

        void EnsureTreeRoot()
        {
            if (_treeRoot == null) _treeRoot = new GameObject("TerrainTrees");
        }

        // Keep _treeEnabled parallel to TreePrefabs; new entries default ON.
        void SyncTreeEnabled()
        {
            int n = TreePrefabs != null ? TreePrefabs.Count : 0;
            if (_treeEnabled != null && _treeEnabled.Length == n) return;
            bool[] old = _treeEnabled;
            _treeEnabled = new bool[n];
            for (int i = 0; i < n; i++) _treeEnabled[i] = (old != null && i < old.Length) ? old[i] : true;
            // Cache the toggle labels so OnGUI doesn't concat a string per row
            // per repaint (IMGUI garbage adds up).
            _treeLabels = new string[n];
            for (int i = 0; i < n; i++)
                _treeLabels[i] = TreePrefabs[i] != null ? " " + TreePrefabs[i].name : " (missing)";
        }

        void SetAllTrees(bool on)
        {
            SyncTreeEnabled();
            for (int i = 0; i < _treeEnabled.Length; i++) _treeEnabled[i] = on;
            _activePack = -1; // manual override no longer matches a pack
        }

        // --- Tree packs (named include/exclude presets) ---

        // Set the brush toggles to a pack's membership (prefabs not in the pack
        // are excluded). Unknown prefab names in the pack are simply ignored.
        void ApplyPack(int packIdx)
        {
            SyncTreeEnabled();
            if (packIdx < 0 || packIdx >= _packs.Count) { _activePack = -1; return; }
            List<string> names = _packs[packIdx].Trees;
            for (int i = 0; i < TreePrefabs.Count; i++)
                if (TreePrefabs[i] != null) _treeEnabled[i] = names.Contains(TreePrefabs[i].name);
            _activePack = packIdx;
        }

        // Snapshot the current enabled set into a (new or replaced) named pack.
        void CreatePack(string name)
        {
            SyncTreeEnabled();
            name = (name ?? "").Trim();
            if (name.Length == 0) name = "Pack " + (_packs.Count + 1);
            TreePack pack = new TreePack { Name = name, Trees = new List<string>() };
            for (int i = 0; i < TreePrefabs.Count; i++)
                if (TreePrefabs[i] != null && _treeEnabled[i]) pack.Trees.Add(TreePrefabs[i].name);
            int existing = _packs.FindIndex(p => p != null && p.Name == name);
            if (existing >= 0) _packs[existing] = pack; else _packs.Add(pack);
            _activePack = _packs.IndexOf(pack);
            _dirtySince = Time.realtimeSinceStartup; // persist via autosave
        }

        void DeletePack(int idx)
        {
            if (idx < 0 || idx >= _packs.Count) return;
            _packs.RemoveAt(idx);
            if (_activePack == idx) _activePack = -1;
            else if (_activePack > idx) _activePack--;
            _dirtySince = Time.realtimeSinceStartup;
        }

        // Build at most one missing tree thumbnail per call (cheap-amortized) —
        // driven from Update while in Tree mode, NOT from OnGUI.
        void EnsureOneTreeThumb()
        {
            if (TreePrefabs == null) return;
            for (int i = 0; i < TreePrefabs.Count; i++)
            {
                GameObject p = TreePrefabs[i];
                if (p != null && !_treeThumbs.ContainsKey(p))
                {
                    _treeThumbs[p] = RuntimeTreePreview.Generate(p, 96);
                    return;
                }
            }
        }

        // Pick a random prefab among the ENABLED, non-null trees.
        GameObject RandomTreePrefab()
        {
            if (TreePrefabs == null || TreePrefabs.Count == 0) return null;
            SyncTreeEnabled();
            int enabled = 0;
            for (int i = 0; i < TreePrefabs.Count; i++)
                if (TreePrefabs[i] != null && _treeEnabled[i]) enabled++;
            if (enabled == 0) return null;
            int pick = Random.Range(0, enabled);
            for (int i = 0; i < TreePrefabs.Count; i++)
                if (TreePrefabs[i] != null && _treeEnabled[i] && pick-- == 0) return TreePrefabs[i];
            return null;
        }

        static long TreeCellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        // Stable per-cell hash in [0,1). salt picks an independent stream so x
        // and z jitter (and any future use) don't correlate.
        static float CellHash01(int a, int b, int salt)
        {
            unchecked
            {
                uint h = (uint)(a * 73856093) ^ (uint)(b * 19349663) ^ (uint)(salt * 83492791);
                h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        // The deterministic jittered point for lattice cell (cx,cz), spacing s.
        void CellPoint(int cx, int cz, float s, out float px, out float pz)
        {
            px = (cx + 0.5f) * s + (CellHash01(cx, cz, 1) - 0.5f) * 2f * TreeJitter * s;
            pz = (cz + 0.5f) * s + (CellHash01(cx, cz, 2) - 0.5f) * 2f * TreeJitter * s;
        }

        // Fill the unoccupied lattice cells under the brush. No neighbour scan:
        // spacing is the lattice itself. Rate-limited so a big brush fills over
        // a few frames of dragging rather than freezing on one.
        void PaintTrees(Vector3 center, float dt)
        {
            if (TreePrefabs == null || TreePrefabs.Count == 0) return;
            float s = Mathf.Max(0.5f, TreeMinSpacing);

            _treeAccum += TreePaintRate * dt;
            int budget = Mathf.FloorToInt(_treeAccum);
            if (budget <= 0) return;
            if (budget > 60) budget = 60; // ceiling so a long-dt hitch can't dump everything at once

            float r2 = BrushRadius * BrushRadius;
            int reach = Mathf.CeilToInt(BrushRadius / s) + 1;
            int ccx = Mathf.FloorToInt(center.x / s);
            int ccz = Mathf.FloorToInt(center.z / s);

            _candKey.Clear();
            _candPos.Clear();
            for (int gz = ccz - reach; gz <= ccz + reach; gz++)
                for (int gx = ccx - reach; gx <= ccx + reach; gx++)
                {
                    long key = TreeCellKey(gx, gz);
                    if (_treeByCell.ContainsKey(key)) continue;
                    CellPoint(gx, gz, s, out float px, out float pz);
                    float dx = px - center.x, dz = pz - center.z;
                    if (dx * dx + dz * dz > r2) continue;
                    _candKey.Add(key);
                    _candPos.Add(new Vector2(px, pz));
                }
            if (_candKey.Count == 0) return;

            float lo = Mathf.Min(TreeScaleRange.x, TreeScaleRange.y);
            float hi = Mathf.Max(TreeScaleRange.x, TreeScaleRange.y);
            int place = Mathf.Min(budget, _candKey.Count);
            // Partial Fisher–Yates: place a random subset so fill looks even,
            // not corner-first.
            for (int i = 0; i < place; i++)
            {
                int j = Random.Range(i, _candKey.Count);
                (_candKey[i], _candKey[j]) = (_candKey[j], _candKey[i]);
                (_candPos[i], _candPos[j]) = (_candPos[j], _candPos[i]);
                Vector2 p = _candPos[i];
                SpawnTree(RandomTreePrefab(), _candKey[i], p.x, p.y,
                          Random.Range(0f, 360f), Random.Range(lo, hi));
            }
            _treeAccum -= place; // consume only what we actually placed
            _dirtySince = Time.realtimeSinceStartup;
        }

        // cellKey is the lattice cell the tree occupies (CellKeyFromWorld for
        // loaded trees). The first tree to claim a cell keeps it.
        PlacedTree SpawnTree(GameObject prefab, long cellKey, float wx, float wz, float rotY, float scale)
        {
            if (prefab == null) return null;
            EnsureTreeRoot();
            float wy = _field != null ? _field.SampleHeight(wx, wz) : 0f;
            GameObject go = Instantiate(prefab, new Vector3(wx, wy, wz),
                                        Quaternion.Euler(0f, rotY, 0f), _treeRoot.transform);
            if (scale > 0f && !Mathf.Approximately(scale, 1f)) go.transform.localScale *= scale;
            // Trees need no physics. Strip colliders so they (a) cost nothing in
            // the physics step and (b) can't be hit by the sculpt/cursor raycast
            // (which accepts any MeshCollider as "terrain").
            Collider[] cols = go.GetComponentsInChildren<Collider>();
            for (int c = 0; c < cols.Length; c++)
            {
                if (Application.isPlaying) Destroy(cols[c]); else DestroyImmediate(cols[c]);
            }
            PlacedTree pt = go.GetComponent<PlacedTree>();
            if (pt == null) pt = go.AddComponent<PlacedTree>();
            pt.Data = new PlacedTreeData
            {
                Prefab = prefab.name,
                Position = new Vector2(wx, wz),
                RotationY = rotY,
                Scale = scale,
            };
            _trees.Add(pt);
            if (!_treeByCell.ContainsKey(cellKey)) _treeByCell[cellKey] = pt; // keep first on collision
            return pt;
        }

        // Map a world XZ to its lattice cell key at the current spacing.
        long CellKeyFromWorld(float wx, float wz)
        {
            float s = Mathf.Max(0.5f, TreeMinSpacing);
            return TreeCellKey(Mathf.FloorToInt(wx / s), Mathf.FloorToInt(wz / s));
        }

        // Erase trees whose base is within the brush radius. Visits only lattice
        // cells overlapping the brush — no full-list scan.
        void EraseTrees(Vector3 center)
        {
            float s = Mathf.Max(0.5f, TreeMinSpacing);
            float r2 = BrushRadius * BrushRadius;
            int reach = Mathf.CeilToInt(BrushRadius / s) + 1;
            int ccx = Mathf.FloorToInt(center.x / s);
            int ccz = Mathf.FloorToInt(center.z / s);
            bool any = false;
            for (int gz = ccz - reach; gz <= ccz + reach; gz++)
                for (int gx = ccx - reach; gx <= ccx + reach; gx++)
                {
                    long key = TreeCellKey(gx, gz);
                    if (!_treeByCell.TryGetValue(key, out PlacedTree t)) continue;
                    if (t == null) { _treeByCell.Remove(key); continue; }
                    Vector3 p = t.transform.position;
                    float dx = p.x - center.x, dz = p.z - center.z;
                    if (dx * dx + dz * dz <= r2)
                    {
                        DestroySafe(t.gameObject);
                        _treeByCell.Remove(key);
                        _trees.Remove(t); // bounded: only erased trees
                        any = true;
                    }
                }
            if (any) _dirtySince = Time.realtimeSinceStartup;
        }

        // Re-instantiate trees loaded from the save (after chunks exist, so the
        // surface heights are known). Missing prefabs are skipped.
        void SpawnLoadedTrees()
        {
            if (_pendingTrees == null) return;
            foreach (PlacedTreeData d in _pendingTrees)
            {
                if (d == null) continue;
                GameObject prefab = FindTreePrefab(d.Prefab);
                if (prefab != null)
                    SpawnTree(prefab, CellKeyFromWorld(d.Position.x, d.Position.y),
                              d.Position.x, d.Position.y, d.RotationY, d.Scale);
            }
            _pendingTrees = null;
        }

        GameObject FindTreePrefab(string name)
        {
            if (string.IsNullOrEmpty(name) || TreePrefabs == null) return null;
            foreach (GameObject p in TreePrefabs)
                if (p != null && p.name == name) return p;
            return null;
        }

        // Cached GUILayoutOption arrays — passing GUILayout.Width(n) inline
        // allocates a fresh options array on EVERY call; these are reused.
        static readonly GUILayoutOption[] GlThumb = { GUILayout.Width(38), GUILayout.Height(38) };
        static readonly GUILayoutOption[] GlRow = { GUILayout.Height(38) };
        static readonly GUILayoutOption[] GlDel = { GUILayout.Width(24) };
        static readonly GUILayoutOption[] GlField = { GUILayout.Width(190) };

        // Tree palette (Play-mode IMGUI): a panel of every tree prefab with a
        // thumbnail + include/exclude toggle. Only shown in Tree mode.
        void OnGUI()
        {
            if (!TreeMode) { _treePanelRect = new Rect(); return; }
            SyncTreeEnabled();
            const float w = 300f, pad = 8f;
            _treePanelRect = new Rect(Screen.width - w - pad, pad, w, Screen.height - 2f * pad);
            GUILayout.BeginArea(_treePanelRect, GUI.skin.box);
            GUILayout.Label("Tree brush — include:");
            if (TreePrefabs == null || TreePrefabs.Count == 0)
            {
                GUILayout.Label("Assign Tree Prefabs on the\nTerrainDesigner component.");
                GUILayout.EndArea();
                return;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("All")) SetAllTrees(true);
            if (GUILayout.Button("None")) SetAllTrees(false);
            GUILayout.EndHorizontal();

            // --- Packs ---
            GUILayout.Space(4);
            GUILayout.Label(_activePack >= 0 && _activePack < _packs.Count
                ? "Packs (active: " + _packs[_activePack].Name + "):" : "Packs:");
            for (int i = 0; i < _packs.Count; i++)
            {
                GUILayout.BeginHorizontal();
                bool sel = _activePack == i;
                bool nowSel = GUILayout.Toggle(sel, _packs[i].Name + "  (" + _packs[i].Trees.Count + ")",
                                               GUI.skin.button);
                if (nowSel && !sel) ApplyPack(i);
                if (GUILayout.Button("x", GlDel)) { DeletePack(i); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            _newPackName = GUILayout.TextField(_newPackName, GlField);
            if (GUILayout.Button("Save pack")) { CreatePack(_newPackName); _newPackName = ""; }
            GUILayout.EndHorizontal();

            // --- Trees ---
            GUILayout.Space(4);
            _treeScroll = GUILayout.BeginScrollView(_treeScroll);
            for (int i = 0; i < TreePrefabs.Count; i++)
            {
                if (TreePrefabs[i] == null) continue;
                GUILayout.BeginHorizontal();
                _treeThumbs.TryGetValue(TreePrefabs[i], out Texture2D preview);
                if (preview != null) GUILayout.Label(preview, GlThumb);
                else GUILayout.Box("…", GlThumb);
                bool before = _treeEnabled[i];
                string label = (_treeLabels != null && i < _treeLabels.Length) ? _treeLabels[i] : "";
                _treeEnabled[i] = GUILayout.Toggle(before, label, GlRow);
                if (_treeEnabled[i] != before) _activePack = -1; // diverged from any pack
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

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

            OrbitCameraController orbit = cam.GetComponent<OrbitCameraController>();
            if (orbit == null)
            {
                orbit = cam.gameObject.AddComponent<OrbitCameraController>();
                orbit.Target = transform.position; // terrain centre
                float span = Mathf.Max((Mathf.Max(2, ColumnsX) - 1) * CellSize,
                                       (Mathf.Max(2, RowsZ) - 1) * CellSize);
                orbit.DistanceTarget = span * 1.2f; // frame the whole footprint
                orbit.Distance = orbit.DistanceTarget;
                orbit.Pitch = 45f;
            }
            // Don't zoom the camera when scrolling over the tree palette — let
            // its own scroll view consume the wheel instead.
            orbit.ScrollSuppressor = MouseOverTreePanel;
        }

        // True when the cursor is inside the (Tree-mode) palette rect. The rect
        // is reset to empty when the palette is hidden, so this is false outside
        // Tree mode. Y is flipped: GUI rects are top-left origin, mouse is
        // bottom-left.
        bool MouseOverTreePanel()
        {
            if (!TreeMode || _treePanelRect.width <= 0f) return false;
            return _treePanelRect.Contains(
                new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
        }

        void Update()
        {
            // Brush-mode hotkeys.
            if (Input.GetKeyDown(KeyCode.Alpha1)) Brush = BrushMode.Raise;
            else if (Input.GetKeyDown(KeyCode.Alpha2)) Brush = BrushMode.Lower;
            else if (Input.GetKeyDown(KeyCode.Alpha3)) Brush = BrushMode.Smooth;
            else if (Input.GetKeyDown(KeyCode.Alpha4)) Brush = BrushMode.Flatten;
            if (Input.GetKeyDown(KeyCode.T)) TreeMode = !TreeMode; // sculpt <-> trees
            // Bake thumbnails only while NOT painting — the first render of each
            // tree compiles its shader variant (a one-time editor stall), and we
            // don't want that landing mid-stroke.
            if (TreeMode && !Input.GetMouseButton(0)) EnsureOneTreeThumb();

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

            // Tree mode: left-drag paints, right-drag erases; no sculpting.
            // Don't act when the cursor is over the tree palette panel.
            if (TreeMode)
            {
                bool overPanel = _treePanelRect.Contains(
                    new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
                if (!overPanel && overTerrain && Input.GetMouseButton(0)) PaintTrees(hit.point, Time.deltaTime);
                else _treeAccum = 0f; // reset accumulation between strokes
                if (!overPanel && overTerrain && Input.GetMouseButton(1)) EraseTrees(hit.point);
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
                            h += BrushStrength * dt * w;
                            break;
                        case BrushMode.Lower:
                            h -= BrushStrength * dt * w;
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
                WriteSave(BuildSnapshot(), ResolveAutosavePath(), TerrainJsonSettings);
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
                JsonSerializerSettings settings = TerrainJsonSettings; // init on main thread
                _saveTask = System.Threading.Tasks.Task.Run(() => WriteSave(save, path, settings));
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
            var trees = new List<PlacedTreeData>(_trees.Count);
            foreach (PlacedTree t in _trees)
                if (t != null && t.Data != null) trees.Add(t.Data);

            return new TerrainSave
            {
                ColumnsX = _field.ColumnsX,
                RowsZ = _field.RowsZ,
                CellSize = _field.CellSize,
                Idx = idx.ToArray(),
                H = hs.ToArray(),
                Trees = trees,
                Packs = new List<TreePack>(_packs),
            };
        }

        // Serialize + write. Thread-safe (no Unity main-thread APIs besides
        // Debug.Log, which is itself thread-safe).
        static void WriteSave(TerrainSave save, string path, JsonSerializerSettings settings)
        {
            try
            {
                string json = JsonConvert.SerializeObject(save, settings);
                System.IO.File.WriteAllText(path, json);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Save write failed: {ex.Message}");
            }
        }

        TerrainField TryLoadTerrain()
        {
            try
            {
                string path = ResolveAutosavePath();
                if (!System.IO.File.Exists(path)) return null;
                TerrainSave save = JsonConvert.DeserializeObject<TerrainSave>(
                    System.IO.File.ReadAllText(path), TerrainJsonSettings);
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
                _pendingTrees = save.Trees; // spawned after chunks build (SpawnLoadedTrees)
                _packs.Clear();
                if (save.Packs != null) _packs.AddRange(save.Packs);
                return f;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Load failed: {ex.Message} — starting fresh.");
                return null;
            }
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
