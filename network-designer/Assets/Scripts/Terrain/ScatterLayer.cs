// One scatter type (trees, rocks, …): a prefab palette + named include/exclude
// packs + a jittered-lattice paint/erase brush. All the state and behaviour is
// shared here so adding a scatter type is just another ScatterLayer instance on
// TerrainDesigner (no duplicated lattice/palette/pack/thumbnail code).
//
// Placement is a JITTERED LATTICE keyed by cell: at most one item per
// Spacing-sized cell at a deterministic jittered point, so spacing is
// structural and painting never scans neighbours — it fills the unoccupied
// cells under the brush. _byCell is the occupancy + erase index; _placed is the
// canonical list for save.
//
// Reuses PlacedTree/PlacedTreeData (Model) and TreePack (member-name list) and
// the runtime preview baker. Editor-only AssetDatabase bits are #if-guarded.

using System;
using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;    // PlacedTreeData
using NetworkDesigner.Designer; // PlacedTree

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class ScatterLayer
    {
        [Tooltip("Display name + GameObject root name (e.g. Trees, Rocks) + hotkey label.")]
        public string Name = "Scatter";
        [Tooltip("Prefabs to scatter (one picked at random per placement).")]
        public List<GameObject> Prefabs = new List<GameObject>();
        [Tooltip("Asset folder scanned by 'Load … From Folder' (recursive, Editor only). " +
                 "Auto-loaded on Start when the list is empty, so lost references self-heal.")]
        public string Folder = "";
        [Tooltip("Items painted per second while dragging (the brush 'strength').")]
        public float PaintRate = 25f;
        [Tooltip("Lattice spacing (m): items fill a jittered grid of this cell size — " +
                 "controls density (smaller = denser). One item per cell.")]
        public float Spacing = 4f;
        [Tooltip("Random uniform scale range applied to each item.")]
        public Vector2 ScaleRange = new Vector2(1f, 1.5f);
        [Tooltip("Don't place on terrain steeper than this (degrees, 0 = flat). 90 = no limit.")]
        public float MaxSlopeDeg = 35f;
        [Tooltip("Don't place on terrain below the water surface (underwater).")]
        public bool AvoidWater = true;
        [Tooltip("Keep this many metres of shoreline above the water clear of items too.")]
        public float WaterlineMargin = 1f;

        const float Jitter = 0.4f; // max per-axis jitter as a fraction of the cell (<0.5 keeps it in-cell)

        // ---- runtime (not serialized) ----
        GameObject _root;
        readonly List<PlacedTree> _placed = new List<PlacedTree>();
        // cell -> ALL items in that cell. A multimap (not one-per-cell) so items
        // loaded from a save that collide in a cell (older non-lattice data, or a
        // changed spacing) are still tracked and erasable.
        readonly Dictionary<long, List<PlacedTree>> _byCell = new Dictionary<long, List<PlacedTree>>();
        readonly List<long> _candKey = new List<long>();
        readonly List<Vector2> _candPos = new List<Vector2>();
        float _accum;
        List<PlacedTreeData> _pending;
        bool[] _enabled;
        string[] _labels;
        Vector2 _scroll;
        Rect _panelRect;
        readonly Dictionary<GameObject, Texture> _thumbs = new Dictionary<GameObject, Texture>();
        readonly Dictionary<GameObject, Texture> _bigThumbs = new Dictionary<GameObject, Texture>(); // hi-res, for the modal
        GameObject _previewModal; // prefab shown enlarged in the modal (null = closed)
        readonly List<TreePack> _packs = new List<TreePack>();
        int _activePack = -1;
        string _newPackName = "";

        public Rect PanelRect => _panelRect;
        string RootName => "Terrain" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // Acquire our container root. After a domain reload (script recompile while
        // in Play) the runtime _root/_placed reset but the spawned tree GameObjects
        // survive — so ADOPT the existing root and re-track its trees (rather than
        // destroying them), which would otherwise leave untrackable "phantom" trees
        // the eraser can't see. Duplicate roots are merged into the first.
        void EnsureRoot()
        {
            if (_root == null)
            {
                GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == RootName) { _root = all[i]; break; }
                if (_root == null) _root = new GameObject(RootName);
            }
            ReadoptOrphans();
        }

        // True if this scene item belongs to THIS layer (its prefab is in our list) —
        // distinguishes trees from rocks (both use PlacedTree).
        bool IsMine(PlacedTree pt)
        {
            if (pt == null || pt.Data == null || Prefabs == null) return false;
            for (int i = 0; i < Prefabs.Count; i++)
                if (Prefabs[i] != null && Prefabs[i].name == pt.Data.Prefab) return true;
            return false;
        }

        // Re-track every item of ours found ANYWHERE in the scene that isn't already
        // tracked — survivors of a domain reload (which wipes _placed but not the
        // GameObjects), regardless of where they're parented. Reparents them under
        // our root and rebuilds the spatial hash. No-op once tracked.
        void ReadoptOrphans()
        {
            if (_placed.Count > 0) return;
            // Resources.FindObjectsOfTypeAll finds HIDDEN / HideFlags.DontSave
            // instances that FindObjectsByType misses (e.g. trees spawned with
            // DontSave by an older build, which then survive forever and can't be
            // erased). Filter to scene objects (not prefab assets).
            PlacedTree[] all = Resources.FindObjectsOfTypeAll<PlacedTree>();
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                PlacedTree pt = all[i];
                if (pt == null || !pt.gameObject.scene.IsValid() || !IsMine(pt)) continue;
                pt.gameObject.hideFlags = HideFlags.None; // strip the persistent flag
                if (_root != null && pt.transform.parent != _root.transform)
                    pt.transform.SetParent(_root.transform, true);
                long key = CellKeyFromWorld(pt.Data.Position.x, pt.Data.Position.y);
                pt.Cell = key;
                _placed.Add(pt);
                if (!_byCell.TryGetValue(key, out List<PlacedTree> b)) _byCell[key] = b = new List<PlacedTree>();
                b.Add(pt);
                n++;
            }
            if (n > 0) Debug.Log($"[{Name}] re-adopted {n} orphaned item(s)");
        }

        // ---- palette enable state + packs ----

        void SyncEnabled()
        {
            int n = Prefabs != null ? Prefabs.Count : 0;
            if (_enabled != null && _enabled.Length == n) return;
            bool[] old = _enabled;
            _enabled = new bool[n];
            for (int i = 0; i < n; i++) _enabled[i] = (old != null && i < old.Length) ? old[i] : true;
            _labels = new string[n];
            for (int i = 0; i < n; i++)
                _labels[i] = Prefabs[i] != null ? " " + Prefabs[i].name : " (missing)";
        }

        void SetAll(bool on)
        {
            SyncEnabled();
            for (int i = 0; i < _enabled.Length; i++) _enabled[i] = on;
            _activePack = -1;
        }

        void ApplyPack(int idx)
        {
            SyncEnabled();
            if (idx < 0 || idx >= _packs.Count) { _activePack = -1; return; }
            TreePack pack = _packs[idx];
            List<string> names = pack.Trees;
            for (int i = 0; i < Prefabs.Count; i++)
                if (Prefabs[i] != null) _enabled[i] = names.Contains(Prefabs[i].name);
            // Restore the pack's saved brush settings (legacy packs leave them alone).
            if (pack.HasParams)
            {
                PaintRate = pack.PaintRate;
                Spacing = pack.Spacing;
                MaxSlopeDeg = pack.MaxSlopeDeg;
                AvoidWater = pack.AvoidWater;
                WaterlineMargin = pack.WaterlineMargin;
            }
            _activePack = idx;
        }

        // Fill a pack's members from the current include toggles + capture the
        // current brush settings.
        void FillPack(TreePack pack)
        {
            pack.Trees = new List<string>();
            for (int i = 0; i < Prefabs.Count; i++)
                if (Prefabs[i] != null && _enabled[i]) pack.Trees.Add(Prefabs[i].name);
            pack.HasParams = true;
            pack.PaintRate = PaintRate;
            pack.Spacing = Spacing;
            pack.MaxSlopeDeg = MaxSlopeDeg;
            pack.AvoidWater = AvoidWater;
            pack.WaterlineMargin = WaterlineMargin;
        }

        // The currently-enabled species (the active pack's trees) — for the GPU-instanced forest renderer.
        public List<GameObject> EnabledPrefabs()
        {
            SyncEnabled();
            var l = new List<GameObject>();
            for (int i = 0; i < Prefabs.Count; i++) if (Prefabs[i] != null && _enabled[i]) l.Add(Prefabs[i]);
            return l;
        }

        // Returns true if a pack was added/replaced (so the host can mark dirty).
        // Public so the UI Toolkit pack-management modal can save/replace by name.
        public bool CreatePack(string name)
        {
            SyncEnabled();
            name = (name ?? "").Trim();
            if (name.Length == 0) name = "Pack " + (_packs.Count + 1);
            TreePack pack = new TreePack { Name = name };
            FillPack(pack);
            int existing = _packs.FindIndex(p => p != null && p.Name == name);
            if (existing >= 0) _packs[existing] = pack; else _packs.Add(pack);
            _activePack = _packs.IndexOf(pack);
            return true;
        }

        // Overwrite an existing pack's members + brush settings from the current
        // state — for adding/removing meshes (or re-tuning) without re-typing a name.
        bool UpdatePack(int idx)
        {
            SyncEnabled();
            if (idx < 0 || idx >= _packs.Count || _packs[idx] == null) return false;
            FillPack(_packs[idx]);
            _activePack = idx;
            return true;
        }

        // True if the current include toggles no longer match the pack's saved
        // members (drives the "*" modified marker).
        bool PackDiffersFromCurrent(int idx)
        {
            if (idx < 0 || idx >= _packs.Count || _packs[idx] == null) return false;
            List<string> names = _packs[idx].Trees;
            for (int i = 0; i < Prefabs.Count; i++)
            {
                if (Prefabs[i] == null) continue;
                if (_enabled[i] != names.Contains(Prefabs[i].name)) return true;
            }
            return false;
        }

        // True if the named pack's saved state differs from the CURRENT live state —
        // membership and the brush params a save would capture. Drives the "*" dirty
        // marker in the UI Toolkit pack modal. Unknown name => not dirty.
        public bool PackIsDirty(string name)
        {
            SyncEnabled();
            int idx = _packs.FindIndex(p => p != null && p.Name == name);
            if (idx < 0) return false;
            if (PackDiffersFromCurrent(idx)) return true;
            TreePack pack = _packs[idx];
            if (pack.HasParams)
            {
                if (!Mathf.Approximately(pack.PaintRate, PaintRate)) return true;
                if (!Mathf.Approximately(pack.Spacing, Spacing)) return true;
                if (!Mathf.Approximately(pack.MaxSlopeDeg, MaxSlopeDeg)) return true;
                if (pack.AvoidWater != AvoidWater) return true;
                if (!Mathf.Approximately(pack.WaterlineMargin, WaterlineMargin)) return true;
            }
            return false;
        }

        bool DeletePack(int idx)
        {
            if (idx < 0 || idx >= _packs.Count) return false;
            _packs.RemoveAt(idx);
            if (_activePack == idx) _activePack = -1;
            else if (_activePack > idx) _activePack--;
            return true;
        }

        // Delete the pack with this name (for the UI Toolkit pack modal). Returns
        // true if one was removed.
        public bool DeletePackByName(string name)
        {
            int idx = _packs.FindIndex(p => p != null && p.Name == name);
            return idx >= 0 && DeletePack(idx);
        }

        // Bake the hi-res modal preview for the clicked prefab (driven from Update,
        // NOT OnGUI — rendering a preview touches the GPU). Caches the result
        // (incl. null) so it bakes once.
        public void EnsureModalThumb()
        {
            if (_previewModal == null || _bigThumbs.ContainsKey(_previewModal)) return;
            _bigThumbs[_previewModal] = RuntimeTreePreview.Generate(_previewModal, 512);
        }

        // Bake at most one missing thumbnail per call (driven from Update, NOT OnGUI).
        public void EnsureOneThumb()
        {
            if (Prefabs == null) return;
            for (int i = 0; i < Prefabs.Count; i++)
            {
                GameObject p = Prefabs[i];
                if (p != null && !_thumbs.ContainsKey(p))
                {
                    _thumbs[p] = RuntimeTreePreview.Generate(p, 96);
                    return;
                }
            }
        }

        // --- grid access for the UI Toolkit pack-management modal ---
        public int PrefabCount => Prefabs != null ? Prefabs.Count : 0;
        public GameObject PrefabAt(int i) =>
            (Prefabs != null && i >= 0 && i < Prefabs.Count) ? Prefabs[i] : null;
        // Per-prefab include state (the live brush membership a pack save captures).
        public bool IsEnabled(int i)
        {
            SyncEnabled();
            return _enabled != null && i >= 0 && i < _enabled.Length && _enabled[i];
        }
        public void SetEnabled(int i, bool on)
        {
            SyncEnabled();
            if (_enabled != null && i >= 0 && i < _enabled.Length) _enabled[i] = on;
        }
        // Include/exclude every prefab (the modal's All / None buttons). Leaves the
        // active-pack selection alone so the dirty "*" marker reflects the change.
        public void SetAllEnabled(bool on)
        {
            SyncEnabled();
            if (_enabled == null) return;
            for (int i = 0; i < _enabled.Length; i++) _enabled[i] = on;
        }
        // Cached thumbnail (null until EnsureOneThumb has baked it).
        public Texture GetThumb(GameObject prefab) =>
            (prefab != null && _thumbs.TryGetValue(prefab, out Texture t)) ? t : null;

        GameObject RandomPrefab()
        {
            if (Prefabs == null || Prefabs.Count == 0) return null;
            SyncEnabled();
            int enabled = 0;
            for (int i = 0; i < Prefabs.Count; i++)
                if (Prefabs[i] != null && _enabled[i]) enabled++;
            if (enabled == 0) return null;
            int pick = UnityEngine.Random.Range(0, enabled);
            for (int i = 0; i < Prefabs.Count; i++)
                if (Prefabs[i] != null && _enabled[i] && pick-- == 0) return Prefabs[i];
            return null;
        }

        // ---- lattice math ----

        static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        static float CellHash01(int a, int b, int salt)
        {
            unchecked
            {
                uint h = (uint)(a * 73856093) ^ (uint)(b * 19349663) ^ (uint)(salt * 83492791);
                h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        void CellPoint(int cx, int cz, float s, out float px, out float pz)
        {
            px = (cx + 0.5f) * s + (CellHash01(cx, cz, 1) - 0.5f) * 2f * Jitter * s;
            pz = (cz + 0.5f) * s + (CellHash01(cx, cz, 2) - 0.5f) * 2f * Jitter * s;
        }

        long CellKeyFromWorld(float wx, float wz)
        {
            float s = Mathf.Max(0.5f, Spacing);
            return CellKey(Mathf.FloorToInt(wx / s), Mathf.FloorToInt(wz / s));
        }

        // ---- paint / erase ----

        // Fill unoccupied lattice cells under the brush. Returns true if anything
        // was placed (host marks dirty). No neighbour scan; rate-limited.
        public bool Paint(ITerrainSurface field, Vector3 center, float dt, float brushRadius,
            float waterLevel = float.NegativeInfinity)
        {
            if (field == null || Prefabs == null || Prefabs.Count == 0) return false;
            float s = Mathf.Max(0.5f, Spacing);

            _accum += PaintRate * dt;
            int budget = Mathf.FloorToInt(_accum);
            if (budget <= 0) return false;
            if (budget > 60) budget = 60;

            // Place by RANDOMLY SAMPLING points in the brush disc rather than scanning every cell
            // — cost is O(budget), independent of brush radius. The old full scan was O((r/s)^2),
            // which on the DEM meant hundreds of thousands of height samples per frame on a big brush.
            float lo = Mathf.Min(ScaleRange.x, ScaleRange.y);
            float hi = Mathf.Max(ScaleRange.x, ScaleRange.y);
            int placed = 0;
            int tries = budget * 10;   // attempts to find empty/valid cells before giving up this frame
            for (int t = 0; t < tries && placed < budget; t++)
            {
                float ang = UnityEngine.Random.value * Mathf.PI * 2f;
                float rad = brushRadius * Mathf.Sqrt(UnityEngine.Random.value);   // uniform over the disc
                int gx = Mathf.FloorToInt((center.x + Mathf.Cos(ang) * rad) / s);
                int gz = Mathf.FloorToInt((center.z + Mathf.Sin(ang) * rad) / s);
                long key = CellKey(gx, gz);
                if (_byCell.TryGetValue(key, out List<PlacedTree> occ) && occ.Count > 0) continue;  // cell taken
                CellPoint(gx, gz, s, out float px, out float pz);
                if (MaxSlopeDeg < 89f && field.SampleSlopeDegrees(px, pz) > MaxSlopeDeg) continue;   // too steep
                if (AvoidWater && field.SampleHeight(px, pz) < waterLevel + WaterlineMargin) continue; // underwater
                Spawn(field, RandomPrefab(), key, px, pz,
                      UnityEngine.Random.Range(0f, 360f), UnityEngine.Random.Range(lo, hi));
                placed++;
            }
            _accum -= placed;
            return placed > 0;
        }

        // Public single-plant for the forest generator: place a random enabled species at (wx,wz) with
        // the given rotation/scale IF the lattice cell is free and slope/water allow. Returns true if
        // placed. (The generator does its own density/clearing/selection filtering before calling this.)
        public bool TryPlant(ITerrainSurface field, float wx, float wz, float rotY, float scale, float waterLevel)
        {
            if (field == null || Prefabs == null || Prefabs.Count == 0) return false;
            float s = Mathf.Max(0.5f, Spacing);
            long key = CellKey(Mathf.FloorToInt(wx / s), Mathf.FloorToInt(wz / s));
            if (_byCell.TryGetValue(key, out List<PlacedTree> occ) && occ.Count > 0) return false;   // cell taken
            if (MaxSlopeDeg < 89f && field.SampleSlopeDegrees(wx, wz) > MaxSlopeDeg) return false;     // too steep
            if (AvoidWater && field.SampleHeight(wx, wz) < waterLevel + WaterlineMargin) return false; // underwater
            return Spawn(field, RandomPrefab(), key, wx, wz, rotY, scale) != null;
        }

        // The first item to claim a cell keeps it.
        PlacedTree Spawn(ITerrainSurface field, GameObject prefab, long cellKey,
                         float wx, float wz, float rotY, float scale)
        {
            if (prefab == null) return null;
            EnsureRoot();
            float wy = field != null ? field.SampleHeight(wx, wz) : 0f;
            GameObject go = UnityEngine.Object.Instantiate(prefab, new Vector3(wx, wy, wz),
                Quaternion.Euler(0f, rotY, 0f), _root.transform);
            if (scale > 0f && !Mathf.Approximately(scale, 1f)) go.transform.localScale *= scale;
            // (LOD thresholds are fixed once on the prefab at load — Instantiate inherits them.)
            // No physics: strip colliders so they cost nothing and can't be hit by
            // the sculpt/cursor raycast (which treats any MeshCollider as terrain).
            Collider[] cols = go.GetComponentsInChildren<Collider>();
            for (int c = 0; c < cols.Length; c++) DestroySafe(cols[c]);
            PlacedTree pt = go.GetComponent<PlacedTree>();
            if (pt == null) pt = go.AddComponent<PlacedTree>();
            pt.Data = new PlacedTreeData
            {
                Prefab = prefab.name,
                Position = new Vector2(wx, wz),
                RotationY = rotY,
                Scale = scale,
            };
            pt.Cell = cellKey;
            _placed.Add(pt);
            if (!_byCell.TryGetValue(cellKey, out List<PlacedTree> bucket))
                _byCell[cellKey] = bucket = new List<PlacedTree>();
            bucket.Add(pt);
            return pt;
        }

        // Some packs (NatureManufacture / Vegetation Studio beech) author LODGroup screenRelativeHeight > 1
        // (e.g. 2.1, 1.5) which Unity can never satisfy, so every instance stays pinned at LOD0 — dense + heavy,
        // and visibly different from the forest tool (which fixes the same bug in code). If the set isn't a sane
        // descending (0,1] sequence, replace it with the same geometric progression ForestGen uses.
        static void FixLodGroup(GameObject go)
        {
            var lg = go.GetComponentInChildren<LODGroup>();
            if (lg == null) return;
            var lods = lg.GetLODs();
            if (lods == null || lods.Length == 0) return;
            bool sane = true;
            for (int i = 0; i < lods.Length && sane; i++)
                if (lods[i].screenRelativeTransitionHeight <= 0f || lods[i].screenRelativeTransitionHeight > 1f ||
                    (i > 0 && lods[i].screenRelativeTransitionHeight >= lods[i - 1].screenRelativeTransitionHeight)) sane = false;
            if (sane) return;
            // Sane descending (0,1] thresholds; Unity's lodBias then holds higher LODs. The forest tool applies
            // the same lodBias in code, so brush- and forest-placed trees now switch LODs at the same on-screen size.
            for (int i = 0; i < lods.Length; i++)
                lods[i].screenRelativeTransitionHeight = (i == lods.Length - 1) ? 0.01f : 0.5f * Mathf.Pow(0.4f, i); // 0.5,0.2,0.08,…
            lg.SetLODs(lods);
        }

        // Brush-placed TREES rendered a hair darker to match the forest tool (whose night-darkening factor dims
        // it below full noon). Multiply-preserving per-instance block keeps the prefab's hue/alpha. 1 = off.
        public static float TreeBrushDarken = 0.92f;
        static MaterialPropertyBlock _darkenMpb;
        static readonly string[] _darkenProps =
            { "_BaseColor", "_Color", "_HealthyColor", "_DryColor", "_TrunkBaseColor", "_BarkBaseColor", "_EmissionColor" };
        void DarkenInstance(GameObject go)
        {
            if (Name != "Trees" || TreeBrushDarken >= 0.999f) return;
            float f = TreeBrushDarken;
            _darkenMpb ??= new MaterialPropertyBlock();
            var rends = go.GetComponentsInChildren<MeshRenderer>();
            for (int ri = 0; ri < rends.Length; ri++)
            {
                var mat = rends[ri].sharedMaterial;
                if (mat == null) continue;
                _darkenMpb.Clear();
                bool any = false;
                for (int p = 0; p < _darkenProps.Length; p++)
                    if (mat.HasProperty(_darkenProps[p]))
                    { Color c = mat.GetColor(_darkenProps[p]); _darkenMpb.SetColor(_darkenProps[p], new Color(c.r * f, c.g * f, c.b * f, c.a)); any = true; }
                if (any) rends[ri].SetPropertyBlock(_darkenMpb);
            }
        }

        // Erase items within the brush. Visits only overlapping lattice cells.
        public bool Erase(Vector3 center, float brushRadius)
        {
            EnsureRoot(); // re-adopt post-reload survivors so they're erasable
            // Scan the full placed list (so the brush also removes items placed under a different
            // spacing), but use the CACHED Data.Position (managed) instead of transform.position (a
            // native call per item), and COMPACT in a single pass instead of O(n) RemoveAt per hit.
            float r2 = brushRadius * brushRadius, cx = center.x, cz = center.z;
            bool any = false;
            int w = 0;
            for (int i = 0; i < _placed.Count; i++)
            {
                PlacedTree t = _placed[i];
                if (t == null) continue;                                  // drop a destroyed survivor
                float dx = t.Data.Position.x - cx, dz = t.Data.Position.y - cz;  // Position = world XZ
                if (dx * dx + dz * dz <= r2)
                {
                    RemoveFromCell(t);
                    DestroySafe(t.gameObject);
                    any = true;
                    continue;                                             // remove (don't keep)
                }
                _placed[w++] = t;                                         // keep
            }
            if (w < _placed.Count) _placed.RemoveRange(w, _placed.Count - w);
            return any;
        }

        // Drop a tree from its (placement-time) spatial-hash bucket.
        void RemoveFromCell(PlacedTree t)
        {
            if (t != null && _byCell.TryGetValue(t.Cell, out List<PlacedTree> bucket))
            {
                bucket.Remove(t);
                if (bucket.Count == 0) _byCell.Remove(t.Cell);
            }
        }

        // Remove placed items sitting below (waterLevel + WaterlineMargin) — e.g.
        // after the water level rises over them. No-op when AvoidWater is off.
        // Returns true if anything was culled.
        public bool CullBelow(float waterLevel)
        {
            if (!AvoidWater) return false;
            float threshold = waterLevel + WaterlineMargin;
            bool any = false;
            var emptyKeys = new List<long>();
            foreach (KeyValuePair<long, List<PlacedTree>> kv in _byCell)
            {
                List<PlacedTree> bucket = kv.Value;
                for (int i = bucket.Count - 1; i >= 0; i--)
                {
                    PlacedTree t = bucket[i];
                    if (t == null) { bucket.RemoveAt(i); continue; }
                    if (t.transform.position.y >= threshold) continue;
                    DestroySafe(t.gameObject);
                    bucket.RemoveAt(i);
                    _placed.Remove(t);
                    any = true;
                }
                if (bucket.Count == 0) emptyKeys.Add(kv.Key);
            }
            for (int i = 0; i < emptyKeys.Count; i++) _byCell.Remove(emptyKeys[i]);
            return any;
        }

        // Destroy every item of this layer in the scene (tracked, orphaned, or
        // hidden/DontSave). Resources.FindObjectsOfTypeAll catches the hidden ones.
        public void ClearAll()
        {
            PlacedTree[] all = Resources.FindObjectsOfTypeAll<PlacedTree>();
            for (int i = 0; i < all.Length; i++)
            {
                PlacedTree pt = all[i];
                if (pt == null || !pt.gameObject.scene.IsValid() || !IsMine(pt)) continue;
                pt.gameObject.hideFlags = HideFlags.None;
                DestroySafe(pt.gameObject);
            }
            _placed.Clear();
            _byCell.Clear();
        }

        // ---- save / load / conform ----

        List<PlacedTreeData> _lastData;   // last good collect — teardown fallback (see below)

        public List<PlacedTreeData> CollectData()
        {
            var list = new List<PlacedTreeData>(_placed.Count);
            foreach (PlacedTree t in _placed)
                if (t != null && t.Data != null) list.Add(t.Data);
            // Teardown guard: on Play-stop Unity may destroy the tree GameObjects BEFORE
            // the designer's final OnDisable save runs. _placed still holds the (now
            // Unity-null) component refs, so the live collect comes back bogus-empty and
            // would clobber the autosave with zero trees. If we still TRACK trees but the
            // live collect is empty, fall back to the last good snapshot instead.
            if (list.Count == 0 && _placed.Count > 0 && _lastData != null && _lastData.Count > 0)
                return _lastData;
            _lastData = list;
            return list;
        }

        public List<TreePack> CollectPacks() => new List<TreePack>(_packs);

        // --- pack access for the UI Toolkit Scatter/Fence palette + pack modal ---
        public int ActivePack => _activePack;
        public string ActivePackName =>
            _activePack >= 0 && _activePack < _packs.Count && _packs[_activePack] != null
                ? _packs[_activePack].Name : "";
        public List<string> PackNames()
        {
            var names = new List<string>(_packs.Count);
            for (int i = 0; i < _packs.Count; i++) names.Add(_packs[i] != null ? _packs[i].Name : "");
            return names;
        }
        public void SelectPackByName(string name)
        {
            int idx = _packs.FindIndex(p => p != null && p.Name == name);
            if (idx >= 0) ApplyPack(idx);
        }

        // Replace the pack presets (e.g. from the standalone packs file, which is
        // authoritative over the terrain autosave so packs survive a terrain reset).
        public void SetPacks(List<TreePack> packs)
        {
            _packs.Clear();
            if (packs != null) _packs.AddRange(packs);
            _activePack = -1;
        }

        // Stage loaded data + packs; SpawnPending() instantiates after chunks exist.
        public void LoadState(List<PlacedTreeData> data, List<TreePack> packs)
        {
            _pending = data;
            _packs.Clear();
            if (packs != null) _packs.AddRange(packs);
        }

        public void SpawnPending(ITerrainSurface field)
        {
            if (_pending == null) return;
            foreach (PlacedTreeData d in _pending)
            {
                if (d == null) continue;
                GameObject prefab = FindPrefab(d.Prefab);
                if (prefab != null)
                    Spawn(field, prefab, CellKeyFromWorld(d.Position.x, d.Position.y),
                          d.Position.x, d.Position.y, d.RotationY, d.Scale);
            }
            _pending = null;
        }

        GameObject FindPrefab(string name)
        {
            if (string.IsNullOrEmpty(name) || Prefabs == null) return null;
            foreach (GameObject p in Prefabs)
                if (p != null && p.name == name) return p;
            return null;
        }

        // Re-seat every placed item onto the (possibly changed) surface.
        public void ConformToSurface(ITerrainSurface field)
        {
            if (field == null) return;
            for (int i = 0; i < _placed.Count; i++)
            {
                PlacedTree t = _placed[i];
                if (t == null) continue;
                Vector3 p = t.transform.position;
                p.y = field.SampleHeight(p.x, p.z);
                t.transform.position = p;
            }
        }

        // Runtime load: populate Prefabs from a Resources/<resFolder> folder (works in
        // play AND builds — no editor APIs), so prefabs needn't be assigned on the
        // GameObject. De-duped by name (a folder can hold model + prefab pairs). Keeps the
        // existing list if the folder is empty. Returns true if the list changed.
        public bool LoadFromResources(string resFolder)
        {
            if (string.IsNullOrEmpty(resFolder)) return false;
            var loaded = Resources.LoadAll<GameObject>(resFolder);
            // A pack folder typically holds BOTH the source model (.dae/.fbx, imported
            // with importMaterials off → no/default material → renders GRAY) and the real
            // .prefab (which references the URP .mat). They share a name, so dedupe by
            // name but KEEP the candidate that actually has materials — otherwise the
            // bare model can win and every scattered item paints gray.
            var best = new Dictionary<string, GameObject>();
            foreach (var go in loaded)
            {
                if (go == null || go.GetComponentInChildren<Renderer>() == null) continue;
                if (!best.TryGetValue(go.name, out GameObject cur)
                    || MaterialScore(go) > MaterialScore(cur))
                    best[go.name] = go;
            }
            var list = new List<GameObject>(best.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            if (list.Count == 0 || SamePrefabList(list, Prefabs)) return false;
            Prefabs = list;
            // Remap broken LOD thresholds ONCE per prefab — Instantiate then inherits the fix, so painting
            // doesn't pay a per-tree hierarchy traversal. Also fixes the forest tool (ExtractLods reads these).
            for (int i = 0; i < Prefabs.Count; i++) FixLodGroup(Prefabs[i]);
            _enabled = null;   // re-sync include toggles
            Debug.Log($"[ScatterLayer:{Name}] loaded {list.Count} prefab(s) from Resources/{resFolder}.");
            return true;
        }

        // Count renderer slots that carry a REAL (assigned, non-default) material.
        // A model imported with materials off has only null / built-in-default slots
        // (which render gray), so it scores 0 and loses the dedupe to its prefab.
        static int MaterialScore(GameObject go)
        {
            int score = 0;
            Renderer[] rends = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                Material[] mats = rends[i].sharedMaterials;
                if (mats == null) continue;
                for (int m = 0; m < mats.Length; m++)
                    if (mats[m] != null && !IsDefaultMaterialName(mats[m].name)) score++;
            }
            return score;
        }

        // Names Unity gives the built-in fallback/default materials (the gray ones a
        // material-less import falls back to). URP's default is literally "Lit".
        static bool IsDefaultMaterialName(string n) =>
            string.IsNullOrEmpty(n) || n == "Lit" || n == "Default-Material"
            || n == "Default-Diffuse" || n.StartsWith("Default-");

        // True if two prefab lists hold the same references in the same order.
        static bool SamePrefabList(List<GameObject> a, List<GameObject> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

#if UNITY_EDITOR
        // Populate Prefabs from every renderable prefab under Folder (recursive),
        // sorted by name. Idempotent: identical set → no-op (preserves toggles).
        // Returns true if the list changed (host marks dirty).
        public bool LoadFromFolder()
        {
            string folder = (Folder ?? "").TrimEnd('/');
            if (string.IsNullOrEmpty(folder) || !UnityEditor.AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning($"[ScatterLayer:{Name}] folder not found: '{folder}'");
                return false;
            }
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            var list = new List<GameObject>(guids.Length);
            foreach (string g in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
                GameObject go = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null && go.GetComponentInChildren<Renderer>() != null) list.Add(go);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            if (SamePrefabList(list, Prefabs))
            {
                Debug.Log($"[ScatterLayer:{Name}] list unchanged ({list.Count} from '{folder}').");
                return false;
            }
            Prefabs = list;
            _enabled = null; // re-sync toggles; thumbs kept (survivors reuse, new bake lazily)
            Debug.Log($"[ScatterLayer:{Name}] loaded {list.Count} prefab(s) from '{folder}'.");
            return true;
        }

        public bool IsEmpty => Prefabs == null || Prefabs.Count == 0;
#endif

        // ---- palette UI (IMGUI) ----

        static readonly GUILayoutOption[] GlThumb = { GUILayout.Width(38), GUILayout.Height(38) };
        static readonly GUILayoutOption[] GlRow = { GUILayout.Height(38) };
        static readonly GUILayoutOption[] GlDel = { GUILayout.Width(24) };
        static readonly GUILayoutOption[] GlUpd = { GUILayout.Width(40) };
        static readonly GUILayoutOption[] GlField = { GUILayout.Width(190) };

        // Draw the right-side palette panel. `slot` shifts the panel left so two
        // layers' panels don't overlap if ever shown together (0 = rightmost).
        // Returns true if a change happened that should be persisted.
        public bool DrawPalette(int slot = 0)
        {
            SyncEnabled();
            bool dirty = false;
            const float w = 300f, pad = 8f;
            // Lay out in the same virtual-screen space TerrainDesigner scales the
            // GUI by, so the panel stays flush to the right edge under UiScale.
            float s = Mathf.Max(0.25f, TerrainDesigner.UiScale);
            float vw = Screen.width / s, vh = Screen.height / s;
            _panelRect = new Rect(vw - (w + pad) * (slot + 1), pad, w, vh - 2f * pad);
            GUILayout.BeginArea(_panelRect, GUI.skin.box);
            GUILayout.Label($"{Name} brush — include:");
            if (Prefabs == null || Prefabs.Count == 0)
            {
                GUILayout.Label($"Assign {Name} prefabs (or set Folder\nand Load From Folder).");
                GUILayout.EndArea();
                return false;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("All")) SetAll(true);
            if (GUILayout.Button("None")) SetAll(false);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label(_activePack >= 0 && _activePack < _packs.Count
                ? "Packs (active: " + _packs[_activePack].Name + "):" : "Packs:");
            for (int i = 0; i < _packs.Count; i++)
            {
                GUILayout.BeginHorizontal();
                bool sel = _activePack == i;
                // "*" marks a pack whose saved members differ from the current toggles.
                string tag = (sel && PackDiffersFromCurrent(i)) ? " *" : "";
                bool nowSel = GUILayout.Toggle(sel, _packs[i].Name + "  (" + _packs[i].Trees.Count + ")" + tag,
                                               GUI.skin.button);
                if (nowSel && !sel) ApplyPack(i);
                // "Upd" overwrites this pack with the current selection + brush settings.
                if (GUILayout.Button("Upd", GlUpd)) { dirty |= UpdatePack(i); }
                if (GUILayout.Button("x", GlDel)) { dirty |= DeletePack(i); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            // Update the active pack in place (add/remove meshes + re-save brush
            // settings) without re-typing its name.
            if (_activePack >= 0 && _activePack < _packs.Count
                && GUILayout.Button("Update '" + _packs[_activePack].Name + "' (members + brush settings)"))
                dirty |= UpdatePack(_activePack);
            GUILayout.BeginHorizontal();
            _newPackName = GUILayout.TextField(_newPackName, GlField);
            if (GUILayout.Button("Save new pack")) { dirty |= CreatePack(_newPackName); _newPackName = ""; }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            _scroll = GUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < Prefabs.Count; i++)
            {
                if (Prefabs[i] == null) continue;
                GUILayout.BeginHorizontal();
                _thumbs.TryGetValue(Prefabs[i], out Texture preview);
                // Click the thumbnail to open a large preview modal.
                if (preview != null) { if (GUILayout.Button(preview, GlThumb)) _previewModal = Prefabs[i]; }
                else if (GUILayout.Button("…", GlThumb)) _previewModal = Prefabs[i];
                bool before = _enabled[i];
                string label = (_labels != null && i < _labels.Length) ? _labels[i] : "";
                _enabled[i] = GUILayout.Toggle(before, label, GlRow);
                // Keep the active pack selected while editing — the "*" + Update
                // button let you commit the add/remove back to the pack.
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            if (_previewModal != null) DrawPreviewModal(vw, vh);
            return dirty;
        }

        // Full-screen dimmed modal with a large preview of the clicked prefab.
        // Click the Close button or anywhere outside the panel to dismiss.
        void DrawPreviewModal(float vw, float vh)
        {
            Event e = Event.current;
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, vw, vh), Texture2D.whiteTexture);
            GUI.color = prev;

            float ps = Mathf.Clamp(Mathf.Min(vw, vh) - 140f, 96f, 512f);
            Rect box = new Rect((vw - (ps + 32f)) * 0.5f, (vh - (ps + 96f)) * 0.5f, ps + 32f, ps + 96f);
            GUI.Box(box, GUIContent.none);
            GUILayout.BeginArea(new Rect(box.x + 8f, box.y + 6f, box.width - 16f, box.height - 12f));
            GUILayout.Label(_previewModal != null ? _previewModal.name : "");
            Rect imgRect = GUILayoutUtility.GetRect(ps, ps);
            if (_bigThumbs.TryGetValue(_previewModal, out Texture big) && big != null)
                GUI.DrawTexture(imgRect, big, ScaleMode.ScaleToFit);
            else
                GUI.Box(imgRect, _bigThumbs.ContainsKey(_previewModal) ? "Preview unavailable" : "Rendering…");
            if (GUILayout.Button("Close")) _previewModal = null;
            GUILayout.EndArea();

            // Click outside the panel closes it.
            if (e.type == EventType.MouseDown && !box.Contains(e.mousePosition)) { _previewModal = null; e.Use(); }
        }
    }
}
