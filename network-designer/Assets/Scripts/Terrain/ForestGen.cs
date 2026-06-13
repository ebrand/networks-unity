// Procedural FOREST generator for the chunk world. Two stages:
//   1. SELECT a region by ELEVATION flood-fill — magic-wand: click a spot, flood contiguous terrain
//      within ±tolerance of that height, staying inside loaded chunks; organic, biome-band shaped.
//      The selection is shown as a translucent draped highlight.
//   2. GROW a forest in the selection: a jittered-lattice blue-noise of candidate points modulated
//      by fBM density × Worley clearings, filtered by slope / elevation band / water, placed into the
//      tree ScatterLayer (so the trees persist/render/conform like brush-placed ones).
//
// Bounded to a SELECTED region (not map-wide), so the flat-list tree layer can handle it. Reuses the
// Sea tool's flood BFS + ChunkWorld surface queries + the existing tree layer + a self-contained
// Perlin-fBM / Worley noise. v1.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Designer;   // DayCycle.SunLight01 for night canopy darkening

namespace NetworkDesigner.Terrain
{
    public static class ForestGen
    {
        // ── Selection (elevation flood-fill) ──────────────────────────────────────────────────
        public static float SelCell = 20f;          // flood-fill cell size (m) — coarse region grid
        public static float ElevTolerance = 25f;    // ± height band the flood stays within (m)
        static readonly HashSet<Vector2Int> _sel = new HashSet<Vector2Int>();
        public static int SelectedCells => _sel.Count;
        public static bool HasSelection => _sel.Count > 0;

        static GameObject _hiGo;
        static Mesh _hiMesh;
        static Material _hiMat;

        public static bool Contains(float wx, float wz)
            => _sel.Contains(new Vector2Int(Mathf.FloorToInt(wx / SelCell), Mathf.FloorToInt(wz / SelCell)));

        // Flood-fill the contiguous region whose height is within ±ElevTolerance of the clicked point,
        // staying inside loaded chunks. Mirrors ChunkWorld.FloodLower's BFS, but just collects cells.
        public static void SelectByElevation(Vector3 hit, int maxCells = 300000)
        {
            _sel.Clear();
            if (!ChunkWorld.HasWorld) { Rehighlight(); return; }
            float cell = Mathf.Max(2f, SelCell);
            float seed = ChunkWorld.SampleHeight(hit.x, hit.z);
            var start = new Vector2Int(Mathf.FloorToInt(hit.x / cell), Mathf.FloorToInt(hit.z / cell));
            if (!CellOk(start, cell, seed)) { Rehighlight(); return; }

            var q = new Queue<Vector2Int>();
            _sel.Add(start); q.Enqueue(start);
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
            while (q.Count > 0 && _sel.Count < maxCells)
            {
                var c = q.Dequeue();
                for (int k = 0; k < 4; k++)
                {
                    var nc = c + dirs[k];
                    if (_sel.Contains(nc) || !CellOk(nc, cell, seed)) continue;
                    _sel.Add(nc); q.Enqueue(nc);
                }
            }
            Rehighlight();
        }

        static bool CellOk(Vector2Int c, float cell, float seed)
        {
            float wx = (c.x + 0.5f) * cell, wz = (c.y + 0.5f) * cell;
            if (!ChunkWorld.IsLoaded(ChunkWorld.ChunkAt(wx, wz))) return false;   // flood stops at the bubble edge
            return Mathf.Abs(ChunkWorld.SampleHeight(wx, wz) - seed) <= ElevTolerance;
        }

        public static void ClearSelection() { _sel.Clear(); Rehighlight(); }

        public static void Teardown()
        {
            if (_hiGo != null) Object.Destroy(_hiGo);
            _hiGo = null; _hiMesh = null; _hiMat = null;
            _sel.Clear();
            ClearForest();
        }

        // ── Highlight overlay (translucent draped quads over the selected cells) ───────────────
        static void EnsureHighlight()
        {
            if (_hiGo != null) return;
            _hiGo = new GameObject("ForestSelection");
            _hiMesh = new Mesh { name = "ForestSelection" };
            _hiMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _hiGo.AddComponent<MeshFilter>().sharedMesh = _hiMesh;
            _hiMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(0.3f, 0.85f, 0.35f, 0.28f), 0f, "ForestSelection");
            var mr = _hiGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _hiMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        static readonly List<Vector3> _hv = new List<Vector3>();
        static readonly List<int> _hi = new List<int>();

        static void Rehighlight()
        {
            EnsureHighlight();
            _hv.Clear(); _hi.Clear();
            float cell = Mathf.Max(2f, SelCell);
            const float lift = 1.5f;
            foreach (var c in _sel)
            {
                float x0 = c.x * cell, z0 = c.y * cell, x1 = x0 + cell, z1 = z0 + cell;
                int b = _hv.Count;
                _hv.Add(new Vector3(x0, ChunkWorld.SampleHeight(x0, z0) + lift, z0));
                _hv.Add(new Vector3(x1, ChunkWorld.SampleHeight(x1, z0) + lift, z0));
                _hv.Add(new Vector3(x1, ChunkWorld.SampleHeight(x1, z1) + lift, z1));
                _hv.Add(new Vector3(x0, ChunkWorld.SampleHeight(x0, z1) + lift, z1));
                _hi.Add(b); _hi.Add(b + 2); _hi.Add(b + 1);
                _hi.Add(b); _hi.Add(b + 3); _hi.Add(b + 2);
            }
            _hiMesh.Clear();
            if (_hv.Count > 0)
            {
                _hiMesh.SetVertices(_hv);
                _hiMesh.SetTriangles(_hi, 0);
                _hiMesh.RecalculateNormals();
                _hiMesh.RecalculateBounds();
            }
        }

        // ── Grow (GPU-INSTANCED forest) ───────────────────────────────────────────────────────
        // The forest is stored as per-species transform matrices and drawn with Graphics.RenderMeshInstanced
        // every frame — NO GameObjects, so it scales to whole-map counts (the old per-GameObject scatter
        // capped at ~10k). CPU frustum-culled. NOTE: no LOD/impostors yet — true CS2-scale density still
        // needs distance LOD + billboards; this is the instanced foundation.
        public static float Density = 1f;              // tree packing; >1 tightens spacing (more trees)
        public static float DensityFreq = 0.0016f;    // clump scale (1/m); LOWER = bigger clumps (~600 m)
        public static float DensityWarp = 1.3f;        // domain-warp strength → flowing, running seams
        public static float SeamStrength = 0.55f;      // 0 = round blobs, 1 = ridged veins/seams
        public static float Threshold = 0.42f;         // coverage cutoff; HIGHER = sparser & clumpier
        public static float EdgeFeather = 0.12f;       // clump-edge softness (probabilistic feather)
        public static float ClearingScale = 220f;      // Worley meadow/clearing spacing (m)
        public static float ClearingDepth = 0.5f;      // how hard Worley meadows punch holes (0..1)
        public static int MaxTrees = 1000000;          // cap on stored instances (~64 MB of matrices)
        public static bool ShowForest = true;          // master draw toggle

        // Trees are binned into a coarse world grid so culling is O(cells), not O(trees): each frame we
        // frustum-test whole cells and draw only the visible ones' matrices (rendered straight from the
        // List, no per-frame copy). CellSize trades cull tightness vs. test count.
        public const float CellSize = 96f;

        sealed class Cell
        {
            public readonly List<Matrix4x4> items = new List<Matrix4x4>();
            public Bounds bounds;
            public bool boundsDirty = true;
        }

        public static float MaxRenderDistance = 2200f;  // trees in cells past this (m) aren't drawn
        public static int ShadowCasterMaxLod = 1;        // only LOD indices ≤ this cast shadows (near trees only) — big perf lever
        // Far LODs are flat cross-card impostors that catch full light (no self-shadowing) and read much
        // BRIGHTER than the self-shadowed near mesh — a jarring pop as you approach. Tint each LOD toward
        // this factor (1 at LOD0 → FarLodDarken at the last) so the brightness matches across the transition.
        public static float FarLodDarken = 0.6f;
        public static float NearLodTint = 0.8f;        // even the close LOD0 mesh catches full sun + reads a touch bright; knock it down
        public static float NightFoliageTint = 0.12f;  // canopy albedo at full night (sun fully down) — kills the glow against a black sky

        // Combined darken scalar for a LOD: near→far tint (1 at LOD0 → FarLodDarken at the last) × day→night
        // factor (DayCycle.SunLight01: 1 day → 0 night). 1 = no darkening. Read live each frame so it tracks the clock.
        static float LodDarken(int li, int lodN)
        {
            float t = lodN > 1 ? li / (float)(lodN - 1) : 1f;
            float d = Mathf.Lerp(Mathf.Clamp01(NearLodTint), Mathf.Clamp01(FarLodDarken), t);
            d *= Mathf.Lerp(Mathf.Clamp01(NightFoliageTint), 1f, Mathf.Clamp01(DayCycle.SunLight01));
            return d;
        }

        // Colour properties we darken. Different tree shaders expose different ones, so we MULTIPLY each
        // material's OWN base value (preserving hue + alpha) rather than slamming a grey _BaseColor that only
        // URP Lit (conifers) honours: URP Lit → _BaseColor/_Color; NatureManufacture foliage → _HealthyColor/
        // _DryColor (+ _EmissionColor, so darkening also kills the night glow); NM bark → _Trunk/_BarkBaseColor.
        static readonly string[] TintProps =
            { "_BaseColor", "_Color", "_HealthyColor", "_DryColor", "_TrunkBaseColor", "_BarkBaseColor", "_EmissionColor" };

        // Darkens submesh `s` of `lod` by `d`, multiplying that material's captured base colours. Each
        // (species,LOD,submesh) owns a PERSISTENT block (allocated at extract) so reuse can't cross-contaminate
        // if RenderMeshInstanced defers the read — different submeshes (leaves/trunk) carry different tints.
        static MaterialPropertyBlock TintMpb(in LodLevel lod, int s, float d)
        {
            if (d >= 0.999f || lod.tintProp == null || s >= lod.tintProp.Length) return null;
            string[] props = lod.tintProp[s]; Color[] bases = lod.tintBase[s];
            if (props == null || props.Length == 0) return null;
            MaterialPropertyBlock mpb = lod.tintMpb[s];
            mpb.Clear();
            for (int k = 0; k < props.Length; k++)
            {
                Color c = bases[k];
                mpb.SetColor(props[k], new Color(c.r * d, c.g * d, c.b * d, c.a));
            }
            return mpb;
        }

        struct LodLevel
        {
            public Mesh mesh;
            public Material[] mats;
            public float threshold;       // LODGroup screenRelativeTransitionHeight (LOD0 largest → last smallest)
            public string[][] tintProp;   // [submesh][k] = darken-able colour property on that material's shader
            public Color[][]  tintBase;   // [submesh][k] = that property's base value (multiplied by LodDarken)
            public MaterialPropertyBlock[] tintMpb;  // [submesh] persistent block, updated per frame
        }

        const int MaxLods = 8;

        sealed class Species
        {
            public GameObject prefab;   // source prefab (for save/restore by name)
            public LodLevel[] lods;     // LOD0..n from the prefab's LODGroup (or one synthetic level)
            public Vector3 baseScale;
            public float height;        // world height of LOD0 (drives screen-size LOD selection)
            public readonly Dictionary<long, Cell> cells = new Dictionary<long, Cell>();
            public int count;           // total instances across cells
            public List<Matrix4x4>[] vis;   // PERSISTENT per-LOD visible set (rebuilt only when the view moves)
        }

        // Visible-set cache: the cull + matrix-copy is the forest's main-thread cost, but the result only
        // changes when the camera moves. We rebuild the per-species `vis` buffers when the camera crosses a
        // deadband (or the forest / draw distance changes) and otherwise just re-submit the cached draws.
        static Vector3 _lastCamPos = new Vector3(1e9f, 1e9f, 1e9f);
        static Quaternion _lastCamRot = Quaternion.identity;
        static float _lastMaxDist = -1f;
        static bool _visDirty = true;
        const float CamMoveDeadband = 6f;   // metres the camera may drift before a re-cull
        const float CamRotDeadband = 2f;    // degrees it may rotate before a re-cull

        // Compact per-species save record (parallel arrays; one tree = X[i],Y[i],Z[i],Rot[i],Scale[i]).
        public sealed class ForestSpeciesSave
        {
            public string Prefab;
            public float[] X, Y, Z, Rot, Scale;
        }
        static readonly List<Species> _species = new List<Species>();
        static readonly Dictionary<GameObject, Species> _byPrefab = new Dictionary<GameObject, Species>();
        public static int TreeCount { get; private set; }
        public static int CellCount   // diagnostics: how many live grid cells (≈ broad-phase tests/frame)
        {
            get { int n = 0; foreach (var sp in _species) n += sp.cells.Count; return n; }
        }

        static long CellKey(float wx, float wz)
        {
            int cx = Mathf.FloorToInt(wx / CellSize), cz = Mathf.FloorToInt(wz / CellSize);
            return ((long)cx << 32) ^ (uint)cz;
        }

        // Add one instance to its species' grid cell (invalidates that cell's cached bounds).
        static void AddInstance(Species sp, float wx, float wz, Matrix4x4 m)
        {
            long key = CellKey(wx, wz);
            if (!sp.cells.TryGetValue(key, out var cell)) sp.cells[key] = cell = new Cell();
            cell.items.Add(m);
            cell.boundsDirty = true;
            sp.count++;
            TreeCount++;
            _hadLiveTrees = true;   // we've held a real instance this session — a later empty is a user erase, not a failed import
            _visDirty = true;   // force a re-cull so the new tree shows
        }

        // Sparse, finely-broken scatter for valley floors — sets the distribution sliders, then the user
        // re-grows (Clear trees → Grow) to apply.
        public static void PresetLightDusting()
        {
            Density = 0.37f;
            DensityFreq = 0.0030f;
            Threshold = 0.16f;
            SeamStrength = 0.43f;
            DensityWarp = 1.2f;
        }

        // Mid-size rounded stands with broad gaps.
        public static void PresetClumps()
        {
            Density = 1.02f;
            DensityFreq = 0.0042f;
            Threshold = 0.42f;
            SeamStrength = 0.65f;
            DensityWarp = 0.3f;
        }

        // Plant a forest across the current selection. Returns how many instances were added. Pulls the
        // species (active pack), spacing + slope/water rules from the tree layer; adds fBM density +
        // Worley clearings + the elevation-selection mask. Trees ACCUMULATE across grows until ClearForest.
        public static int Grow(ScatterLayer layer, ITerrainSurface surf, float waterLevel)
        {
            if (layer == null || surf == null || !HasSelection) return 0;
            var prefabs = layer.EnabledPrefabs();
            if (prefabs.Count == 0) { Debug.LogWarning("[Forest] the active pack has no trees enabled."); return 0; }

            float spacing = Mathf.Max(1f, layer.Spacing / Mathf.Max(0.1f, Density));
            float maxSlope = layer.MaxSlopeDeg;
            bool avoidWater = layer.AvoidWater;
            float waterMargin = layer.WaterlineMargin;

            int minx = int.MaxValue, maxx = int.MinValue, minz = int.MaxValue, maxz = int.MinValue;
            foreach (var c in _sel)
            { if (c.x < minx) minx = c.x; if (c.x > maxx) maxx = c.x; if (c.y < minz) minz = c.y; if (c.y > maxz) maxz = c.y; }
            float wx0 = minx * SelCell, wx1 = (maxx + 1) * SelCell, wz0 = minz * SelCell, wz1 = (maxz + 1) * SelCell;
            int ix0 = Mathf.FloorToInt(wx0 / spacing), ix1 = Mathf.CeilToInt(wx1 / spacing);
            int iz0 = Mathf.FloorToInt(wz0 / spacing), iz1 = Mathf.CeilToInt(wz1 / spacing);

            float lo = Mathf.Min(layer.ScaleRange.x, layer.ScaleRange.y);
            float hi = Mathf.Max(layer.ScaleRange.x, layer.ScaleRange.y);
            int placed = 0;
            for (int iz = iz0; iz <= iz1 && TreeCount < MaxTrees; iz++)
                for (int ix = ix0; ix <= ix1 && TreeCount < MaxTrees; ix++)
                {
                    // Jittered blue-noise point in this lattice cell (deterministic per cell).
                    float jx = (Hash(ix, iz, 0) - 0.5f) * spacing * 0.85f;
                    float jz = (Hash(ix, iz, 1) - 0.5f) * spacing * 0.85f;
                    float px = ix * spacing + spacing * 0.5f + jx;
                    float pz = iz * spacing + spacing * 0.5f + jz;
                    if (!Contains(px, pz)) continue;                          // outside the elevation selection
                    // Clump field: domain-warped + ridged fBM → solid clumps with flowing/running seams;
                    // Worley meadows punch holes. Hard threshold = empty gaps (no lone scatter), then a
                    // probabilistic feather softens the clump edges.
                    float field = ClumpField(px, pz) - Clearing(px, pz) * ClearingDepth;
                    if (field < Threshold) continue;
                    if (Hash(ix, iz, 2) > Mathf.SmoothStep(Threshold, Threshold + EdgeFeather, field)) continue;
                    if (maxSlope < 89f && surf.SampleSlopeDegrees(px, pz) > maxSlope) continue;
                    float y = surf.SampleHeight(px, pz);
                    if (avoidWater && y < waterLevel + waterMargin) continue;

                    GameObject prefab = prefabs[Mathf.Clamp((int)(Hash(ix, iz, 5) * prefabs.Count), 0, prefabs.Count - 1)];
                    Species sp = SpeciesFor(prefab);
                    if (sp == null) continue;
                    float rotY = Hash(ix, iz, 3) * 360f;
                    float scale = Mathf.Lerp(lo, hi, Hash(ix, iz, 4));
                    var s3 = new Vector3(sp.baseScale.x * scale, sp.baseScale.y * scale, sp.baseScale.z * scale);
                    AddInstance(sp, px, pz, Matrix4x4.TRS(new Vector3(px, y, pz), Quaternion.Euler(0f, rotY, 0f), s3));
                    placed++;
                }
            return placed;
        }

        static Species SpeciesFor(GameObject prefab)
        {
            if (prefab == null) return null;
            if (_byPrefab.TryGetValue(prefab, out var sp)) return sp;     // cached (incl. cached null = unextractable)
            if (!ExtractLods(prefab, out var lods, out var baseScale, out var height)) { _byPrefab[prefab] = null; return null; }
            sp = new Species { prefab = prefab, lods = lods, baseScale = baseScale, height = height };
            _byPrefab[prefab] = sp; _species.Add(sp);
            Debug.Log($"[Forest] species '{prefab.name}': {lods.Length} LOD level(s), ~{height:0.#} m tall"
                      + (lods.Length == 1 ? " — NO LODs/billboard; far trees stay full-detail (GPU-heavy)." : "."));
            return sp;
        }

        // Pull EVERY LOD level (mesh + materials + screen-size threshold) out of a tree prefab's LODGroup,
        // forcing GPU instancing on each material. A prefab's last LOD is often a billboard impostor — using
        // it as the far LOD is how we cut triangles at distance. Falls back to a single level if no LODGroup.
        // GOTCHA: ignores a non-root renderer transform (offset/scale not baked) — fine for root-mesh trees.
        static bool ExtractLods(GameObject prefab, out LodLevel[] lods, out Vector3 baseScale, out float height)
        {
            lods = null; baseScale = prefab.transform.localScale; height = 10f;
            var list = new List<LodLevel>();
            var lodGroup = prefab.GetComponentInChildren<LODGroup>();
            if (lodGroup != null)
            {
                foreach (var L in lodGroup.GetLODs())
                {
                    MeshRenderer mr = null;
                    if (L.renderers != null) foreach (var r in L.renderers) { mr = r as MeshRenderer; if (mr != null) break; }
                    if (mr == null) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;   // billboard LODs use BillboardRenderer (no MeshFilter) → skipped
                    var mats = mr.sharedMaterials;
                    if (mats != null) for (int i = 0; i < mats.Length; i++) if (mats[i] != null) mats[i].enableInstancing = true;
                    list.Add(new LodLevel { mesh = mf.sharedMesh, mats = mats, threshold = L.screenRelativeTransitionHeight });
                }
            }
            if (list.Count == 0)   // no LODGroup (or only a BillboardRenderer): grab any mesh renderer
            {
                var mr = prefab.GetComponentInChildren<MeshRenderer>();
                if (mr == null) return false;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return false;
                var mats = mr.sharedMaterials;
                if (mats != null) for (int i = 0; i < mats.Length; i++) if (mats[i] != null) mats[i].enableInstancing = true;
                list.Add(new LodLevel { mesh = mf.sharedMesh, mats = mats, threshold = 0f });
            }
            lods = list.ToArray();
            height = Mathf.Max(1f, lods[0].mesh.bounds.size.y * Mathf.Abs(baseScale.y));

            // Capture each LOD material's darken-able tint properties + base colours (see TintProps) so the
            // LOD/night darkening can MULTIPLY them per draw. Without this, only URP-Lit trees (conifers, via
            // _BaseColor) darken; NM trees tint via _HealthyColor/_DryColor/_Trunk/_Bark and would stay bright.
            for (int i = 0; i < lods.Length; i++)
            {
                Material[] ms = lods[i].mats; int mc = ms?.Length ?? 0;
                // Cover the mesh's actual submesh count too (a draw indexes submesh s; mats can be shorter).
                int sc = Mathf.Max(mc, lods[i].mesh != null ? lods[i].mesh.subMeshCount : 0);
                var tp = new string[sc][]; var tb = new Color[sc][]; var mb = new MaterialPropertyBlock[sc];
                for (int mi = 0; mi < sc; mi++)
                {
                    var props = new List<string>(); var cols = new List<Color>();
                    Material mat = mi < mc ? ms[mi] : (mc > 0 ? ms[0] : null);   // mirror the draw-time material fallback
                    if (mat != null) for (int p = 0; p < TintProps.Length; p++)
                        if (mat.HasProperty(TintProps[p])) { props.Add(TintProps[p]); cols.Add(mat.GetColor(TintProps[p])); }
                    tp[mi] = props.ToArray(); tb[mi] = cols.ToArray(); mb[mi] = new MaterialPropertyBlock();
                }
                lods[i].tintProp = tp; lods[i].tintBase = tb; lods[i].tintMpb = mb;
            }

            // Threshold sanity: RebuildVisible selects the LOD by comparing a real screen-height FRACTION
            // (always ≤ ~1) against each LOD's screenRelativeHeight. Some packs (Vegetation Studio /
            // NatureManufacture beech) author values > 1 (e.g. 2.1, 1.5) which our selector can never satisfy,
            // so LOD0/LOD1 get skipped and every tree pins to one low LOD at all distances — looks like "no
            // LODs". If the set isn't a clean descending (0,1] sequence, replace it with an even geometric
            // progression so LOD switches with distance. Sane packs (conifers: 0.30/0.10/0.01) are kept as-is.
            bool sane = lods.Length > 0;
            for (int i = 0; i < lods.Length && sane; i++)
                if (lods[i].threshold <= 0f || lods[i].threshold > 1f ||
                    (i > 0 && lods[i].threshold >= lods[i - 1].threshold)) sane = false;
            if (!sane)
                for (int i = 0; i < lods.Length; i++)
                    lods[i].threshold = (i == lods.Length - 1) ? 0f : 0.5f * Mathf.Pow(0.4f, i); // 0.5, 0.2, 0.08, … ; last = 0 (keep to horizon)
            return true;
        }

        // Draw the forest GPU-instanced. The expensive cull + per-LOD gather (`RebuildVisible`) runs ONLY
        // when the camera crosses a deadband (or the forest / draw distance changed); otherwise we skip it
        // and just re-submit the cached per-LOD buffers. Immediate-mode draws must still issue every frame.
        public static void RenderForest(Camera cam)
        {
            if (!ShowForest || cam == null || _species.Count == 0) return;
            Vector3 camPos = cam.transform.position;
            Quaternion camRot = cam.transform.rotation;
            bool rebuild = _visDirty
                || MaxRenderDistance != _lastMaxDist
                || (camPos - _lastCamPos).sqrMagnitude > CamMoveDeadband * CamMoveDeadband
                || Quaternion.Angle(camRot, _lastCamRot) > CamRotDeadband;
            if (rebuild)
            {
                RebuildVisible(cam, camPos);
                _lastCamPos = camPos; _lastCamRot = camRot; _lastMaxDist = MaxRenderDistance; _visDirty = false;
            }

            var bigBounds = new Bounds(camPos, new Vector3(MaxRenderDistance * 2f + 200f, 200000f, MaxRenderDistance * 2f + 200f));
            foreach (var sp in _species)
            {
                if (sp == null || sp.lods == null || sp.vis == null) continue;
                int lodN = Mathf.Min(sp.lods.Length, MaxLods);
                for (int li = 0; li < lodN; li++)
                {
                    var acc = sp.vis[li];
                    int total = acc.Count;
                    if (total == 0) continue;
                    LodLevel lod = sp.lods[li];
                    float darken = LodDarken(li, lodN);   // near→far LOD match × day→night dim (1 = none)
                    int subCount = Mathf.Max(1, lod.mesh.subMeshCount);
                    for (int s = 0; s < subCount; s++)
                    {
                        Material mat = (lod.mats != null && s < lod.mats.Length && lod.mats[s] != null) ? lod.mats[s]
                                     : (lod.mats != null && lod.mats.Length > 0 ? lod.mats[0] : null);
                        if (mat == null) continue;
                        // Only NEAR LODs cast shadows. Casting from all 20k+ trees out to the shadow distance
                        // (re-rendered per cascade) is the forest's dominant cost; distant trees' shadows are
                        // imperceptible. ShadowCasterMaxLod caps which LOD indices cast.
                        var rp = new RenderParams(mat)
                        {
                            shadowCastingMode = li <= ShadowCasterMaxLod
                                ? UnityEngine.Rendering.ShadowCastingMode.On
                                : UnityEngine.Rendering.ShadowCastingMode.Off,
                            receiveShadows = true,                                            // still darken in shadow like the terrain
                            worldBounds = bigBounds,  // already CPU-culled; keep Unity from re-culling the batch
                            matProps = TintMpb(lod, s, darken)
                        };
                        for (int off = 0; off < total; off += 1023)
                            Graphics.RenderMeshInstanced(rp, lod.mesh, s, acc, Mathf.Min(1023, total - off), off);
                    }
                }
            }
        }

        // Cull cells (frustum + distance) and bucket survivors by LOD into each species' persistent `vis`.
        static void RebuildVisible(Camera cam, Vector3 camPos)
        {
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            float fovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float maxD2 = MaxRenderDistance * MaxRenderDistance;
            foreach (var sp in _species)
            {
                if (sp == null || sp.lods == null || sp.count == 0) continue;
                int lodN = Mathf.Min(sp.lods.Length, MaxLods);
                if (sp.vis == null)
                {
                    sp.vis = new List<Matrix4x4>[MaxLods];
                    for (int i = 0; i < MaxLods; i++) sp.vis[i] = new List<Matrix4x4>();
                }
                for (int i = 0; i < MaxLods; i++) sp.vis[i].Clear();
                foreach (var cell in sp.cells.Values)
                {
                    if (cell.items.Count == 0) continue;
                    if (cell.boundsDirty) { cell.bounds = ComputeCellBounds(cell.items); cell.boundsDirty = false; }
                    float d2 = (cell.bounds.center - camPos).sqrMagnitude;
                    if (d2 > maxD2) continue;                                          // distance cap
                    if (!GeometryUtility.TestPlanesAABB(planes, cell.bounds)) continue; // off-screen
                    float screenH = sp.height / (2f * Mathf.Max(1f, Mathf.Sqrt(d2)) * fovTan); // ~viewport fraction
                    int li = 0;
                    while (li < lodN - 1 && screenH < sp.lods[li].threshold) li++;     // first LOD small enough
                    sp.vis[li].AddRange(cell.items);
                }
            }
        }

        // Tight AABB over a cell's tree positions, grown for trunk radius + canopy height so nothing pops.
        static Bounds ComputeCellBounds(List<Matrix4x4> items)
        {
            Vector3 mn = new Vector3(1e9f, 1e9f, 1e9f), mx = new Vector3(-1e9f, -1e9f, -1e9f);
            for (int i = 0; i < items.Count; i++)
            {
                Vector3 p = items[i].GetColumn(3);
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
            var b = new Bounds();
            b.SetMinMax(mn, mx);
            b.Expand(new Vector3(8f, 80f, 8f));   // generous so tall/edge trees aren't wrongly culled
            return b;
        }

        // Last forest we were asked to RESTORE (ImportForest) or successfully exported — the anti-clobber
        // guard. A failed import (prefabs unresolved) or a transition teardown empties _species; without this
        // the next autosave would ExportForest()==empty and overwrite a good save with nothing. ExportForest
        // re-emits this when the live forest is unexpectedly empty. Reset only on an INTENTIONAL clear.
        static List<ForestSpeciesSave> _lastSave;
        static bool _hadLiveTrees;   // has a real instance existed since the last clear? distinguishes failed-import-empty from user-erased-empty

        // forget: true only for the user's deliberate "clear forest" — drops the preserved save so the empty
        // state actually persists. Teardown/import clears pass false so a transition can't lose the data.
        public static void ClearForest(bool forget = false)
        {
            _species.Clear(); _byPrefab.Clear(); TreeCount = 0; _visDirty = true;
            _hadLiveTrees = false;
            if (forget) _lastSave = null;
        }

        // Erase forest instances within `radius` (m) of a world XZ point — the Forest-tool brush eraser
        // (the trees are GPU-instanced data, not GameObjects, so the scatter palette can't remove them).
        public static int EraseAt(float wx, float wz, float radius)
        {
            if (radius <= 0f || _species.Count == 0) return 0;
            float r2 = radius * radius;
            int cx0 = Mathf.FloorToInt((wx - radius) / CellSize), cx1 = Mathf.FloorToInt((wx + radius) / CellSize);
            int cz0 = Mathf.FloorToInt((wz - radius) / CellSize), cz1 = Mathf.FloorToInt((wz + radius) / CellSize);
            int removed = 0;
            foreach (var sp in _species)
            {
                int spRemoved = 0;
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        long key = ((long)cx << 32) ^ (uint)cz;
                        if (!sp.cells.TryGetValue(key, out var cell)) continue;
                        var items = cell.items;
                        for (int i = items.Count - 1; i >= 0; i--)
                        {
                            Vector3 p = items[i].GetColumn(3);
                            float dx = p.x - wx, dz = p.z - wz;
                            if (dx * dx + dz * dz <= r2)
                            {
                                items[i] = items[items.Count - 1];   // swap-remove
                                items.RemoveAt(items.Count - 1);
                                spRemoved++;
                            }
                        }
                        if (spRemoved > 0) cell.boundsDirty = true;
                    }
                sp.count -= spRemoved;
                removed += spRemoved;
            }
            if (removed > 0) { TreeCount -= removed; _visDirty = true; }
            return removed;
        }

        // ── Persistence (autosave) ────────────────────────────────────────────────────────────
        // Decompose the baked instance matrices into compact per-species records. Rotation is Y-only and
        // scale uniform (how we built them), so this is exact.
        public static List<ForestSpeciesSave> ExportForest()
        {
            var outp = new List<ForestSpeciesSave>(_species.Count);
            foreach (var sp in _species)
            {
                if (sp == null || sp.prefab == null || sp.count == 0) continue;
                int n = sp.count;
                var rec = new ForestSpeciesSave
                {
                    Prefab = sp.prefab.name,
                    X = new float[n], Y = new float[n], Z = new float[n], Rot = new float[n], Scale = new float[n]
                };
                int i = 0;
                foreach (var cell in sp.cells.Values)
                    for (int k = 0; k < cell.items.Count && i < n; k++, i++)
                    {
                        Matrix4x4 m = cell.items[k];
                        Vector3 c0 = m.GetColumn(0);
                        rec.X[i] = m.m03; rec.Y[i] = m.m13; rec.Z[i] = m.m23;
                        rec.Rot[i] = Mathf.Atan2(-c0.z, c0.x) * Mathf.Rad2Deg;
                        rec.Scale[i] = c0.magnitude;
                    }
                outp.Add(rec);
            }
            int total = 0; foreach (var r in outp) total += r.X?.Length ?? 0;
            // Anti-clobber: if the live forest is empty but we loaded a non-empty one this session (import
            // couldn't resolve the prefabs, or a transition tore it down), DON'T overwrite the good save with
            // nothing — re-emit what we loaded. A deliberate "clear forest" sets _lastSave=null, so a genuine
            // empty still persists.
            if (total == 0 && !_hadLiveTrees && _lastSave != null && _lastSave.Count > 0)
            {
                Debug.LogWarning("[Forest] live forest empty but a saved forest was loaded this session and never rendered — preserving it instead of overwriting the save with empty (prefabs unresolved on import?).");
                return _lastSave;
            }
            Debug.Log($"[Forest] export: {outp.Count} species / {total} instances (live TreeCount={TreeCount}, _species={_species.Count}).");
            _lastSave = outp;
            return outp;
        }

        // Rebuild the forest from saved records. Resolves each species' prefab by name off the tree layer
        // (so the mesh/material re-extract); restores exact world transforms (Y stored, no re-sampling).
        public static void ImportForest(ScatterLayer layer, List<ForestSpeciesSave> data)
        {
            ClearForest();
            _lastSave = data;   // remember what we were ASKED to restore, even if prefabs don't resolve below
            if (layer == null || data == null) return;
            var ps = layer.Prefabs;
            int missing = 0;
            var missingNames = new List<string>();
            foreach (var rec in data)
            {
                if (rec == null || string.IsNullOrEmpty(rec.Prefab) || rec.X == null) continue;
                GameObject prefab = null;
                for (int i = 0; i < ps.Count; i++) if (ps[i] != null && ps[i].name == rec.Prefab) { prefab = ps[i]; break; }
                if (prefab == null) { missing++; if (missingNames.Count < 8) missingNames.Add(rec.Prefab); continue; }    // pack/prefab gone — skip that species
                Species sp = SpeciesFor(prefab);
                if (sp == null) { missing++; continue; }
                int n = rec.X.Length;
                if (rec.Y == null || rec.Z == null || rec.Rot == null || rec.Scale == null) continue;
                for (int i = 0; i < n && TreeCount < MaxTrees; i++)
                {
                    float sc = rec.Scale[i];
                    AddInstance(sp, rec.X[i], rec.Z[i], Matrix4x4.TRS(
                        new Vector3(rec.X[i], rec.Y[i], rec.Z[i]),
                        Quaternion.Euler(0f, rec.Rot[i], 0f),
                        new Vector3(sc, sc, sc)));
                }
            }
            if (missing > 0) Debug.LogWarning($"[Forest] {missing} saved species had no matching prefab among the tree layer's {ps.Count} prefab(s) — skipped. " +
                $"Unresolved: {string.Join(", ", missingNames)}{(missing > missingNames.Count ? ", …" : "")}. " +
                "(The saved forest data is preserved and will re-appear once these prefabs are loaded.)");
        }

        // Domain-warped + ridged fBM, in ~[0,1]. Warp gives flowing structure; the ridge blend turns
        // round blobs into running veins/seams. SeamStrength morphs between the two.
        static float ClumpField(float wx, float wz)
        {
            float x = wx * DensityFreq, z = wz * DensityFreq;
            float qx = Fbm(x, z, 4, 0.5f, 2f);
            float qz = Fbm(x + 5.2f, z + 1.3f, 4, 0.5f, 2f);
            float n = Fbm(x + DensityWarp * (qx - 0.5f), z + DensityWarp * (qz - 0.5f), 5, 0.5f, 2.1f);
            float ridged = 1f - Mathf.Abs(2f * n - 1f);
            return Mathf.Lerp(n, ridged, SeamStrength);
        }

        static float Fbm(float x, float y, int oct, float pers, float lac)
        {
            float amp = 1f, freq = 1f, sum = 0f, norm = 0f;
            for (int o = 0; o < oct; o++) { sum += amp * Mathf.PerlinNoise(x * freq, y * freq); norm += amp; amp *= pers; freq *= lac; }
            return norm > 0f ? sum / norm : 0f;
        }

        // Worley F1 → "openness": 1 near a feature point (clearing/meadow), fading to 0 (forest).
        static float Clearing(float wx, float wz)
        {
            float fx = wx / ClearingScale, fz = wz / ClearingScale;
            int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
            float best = 9f;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = ix + dx, cz = iz + dz;
                    float ox = Hash(cx, cz, 7), oz = Hash(cx, cz, 8);
                    float ddx = (cx + ox) - fx, ddz = (cz + oz) - fz;
                    float d = ddx * ddx + ddz * ddz;
                    if (d < best) best = d;
                }
            return 1f - Mathf.SmoothStep(0.16f, 0.46f, Mathf.Sqrt(best));
        }

        static float Hash(int x, int y, int seed)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return (h & 0xFFFFFFu) / (float)0x1000000;
        }
    }
}
