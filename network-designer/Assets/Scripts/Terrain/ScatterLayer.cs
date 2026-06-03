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

        // Destroy any stray roots by name (a prior edit-mode build / domain reload
        // leaves the runtime _root reference null while the GameObject lives on).
        void EnsureRoot()
        {
            if (_root != null) return;
            GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == RootName) DestroySafe(all[i]);
            _root = new GameObject(RootName);
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
            List<string> names = _packs[idx].Trees;
            for (int i = 0; i < Prefabs.Count; i++)
                if (Prefabs[i] != null) _enabled[i] = names.Contains(Prefabs[i].name);
            _activePack = idx;
        }

        // Returns true if a pack was added/replaced (so the host can mark dirty).
        bool CreatePack(string name)
        {
            SyncEnabled();
            name = (name ?? "").Trim();
            if (name.Length == 0) name = "Pack " + (_packs.Count + 1);
            TreePack pack = new TreePack { Name = name, Trees = new List<string>() };
            for (int i = 0; i < Prefabs.Count; i++)
                if (Prefabs[i] != null && _enabled[i]) pack.Trees.Add(Prefabs[i].name);
            int existing = _packs.FindIndex(p => p != null && p.Name == name);
            if (existing >= 0) _packs[existing] = pack; else _packs.Add(pack);
            _activePack = _packs.IndexOf(pack);
            return true;
        }

        bool DeletePack(int idx)
        {
            if (idx < 0 || idx >= _packs.Count) return false;
            _packs.RemoveAt(idx);
            if (_activePack == idx) _activePack = -1;
            else if (_activePack > idx) _activePack--;
            return true;
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
        public bool Paint(TerrainField field, Vector3 center, float dt, float brushRadius,
            float waterLevel = float.NegativeInfinity)
        {
            if (field == null || Prefabs == null || Prefabs.Count == 0) return false;
            float s = Mathf.Max(0.5f, Spacing);

            _accum += PaintRate * dt;
            int budget = Mathf.FloorToInt(_accum);
            if (budget <= 0) return false;
            if (budget > 60) budget = 60;

            float r2 = brushRadius * brushRadius;
            int reach = Mathf.CeilToInt(brushRadius / s) + 1;
            int ccx = Mathf.FloorToInt(center.x / s);
            int ccz = Mathf.FloorToInt(center.z / s);

            _candKey.Clear();
            _candPos.Clear();
            for (int gz = ccz - reach; gz <= ccz + reach; gz++)
                for (int gx = ccx - reach; gx <= ccx + reach; gx++)
                {
                    long key = CellKey(gx, gz);
                    if (_byCell.TryGetValue(key, out List<PlacedTree> occ) && occ.Count > 0) continue;
                    CellPoint(gx, gz, s, out float px, out float pz);
                    float dx = px - center.x, dz = pz - center.z;
                    if (dx * dx + dz * dz > r2) continue;
                    // Skip faces steeper than the limit (>= 89 deg = no limit).
                    if (MaxSlopeDeg < 89f && field.SampleSlopeDegrees(px, pz) > MaxSlopeDeg) continue;
                    // Skip cells below the water surface (+ shoreline margin).
                    if (AvoidWater && field.SampleHeight(px, pz) < waterLevel + WaterlineMargin) continue;
                    _candKey.Add(key);
                    _candPos.Add(new Vector2(px, pz));
                }
            if (_candKey.Count == 0) return false;

            float lo = Mathf.Min(ScaleRange.x, ScaleRange.y);
            float hi = Mathf.Max(ScaleRange.x, ScaleRange.y);
            int place = Mathf.Min(budget, _candKey.Count);
            for (int i = 0; i < place; i++)
            {
                int j = UnityEngine.Random.Range(i, _candKey.Count);
                (_candKey[i], _candKey[j]) = (_candKey[j], _candKey[i]);
                (_candPos[i], _candPos[j]) = (_candPos[j], _candPos[i]);
                Vector2 p = _candPos[i];
                Spawn(field, RandomPrefab(), _candKey[i], p.x, p.y,
                      UnityEngine.Random.Range(0f, 360f), UnityEngine.Random.Range(lo, hi));
            }
            _accum -= place;
            return true;
        }

        // The first item to claim a cell keeps it.
        PlacedTree Spawn(TerrainField field, GameObject prefab, long cellKey,
                         float wx, float wz, float rotY, float scale)
        {
            if (prefab == null) return null;
            EnsureRoot();
            float wy = field != null ? field.SampleHeight(wx, wz) : 0f;
            GameObject go = UnityEngine.Object.Instantiate(prefab, new Vector3(wx, wy, wz),
                Quaternion.Euler(0f, rotY, 0f), _root.transform);
            if (scale > 0f && !Mathf.Approximately(scale, 1f)) go.transform.localScale *= scale;
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
            _placed.Add(pt);
            if (!_byCell.TryGetValue(cellKey, out List<PlacedTree> bucket))
                _byCell[cellKey] = bucket = new List<PlacedTree>();
            bucket.Add(pt);
            return pt;
        }

        // Erase items within the brush. Visits only overlapping lattice cells.
        public bool Erase(Vector3 center, float brushRadius)
        {
            float s = Mathf.Max(0.5f, Spacing);
            float r2 = brushRadius * brushRadius;
            int reach = Mathf.CeilToInt(brushRadius / s) + 1;
            int ccx = Mathf.FloorToInt(center.x / s);
            int ccz = Mathf.FloorToInt(center.z / s);
            bool any = false;
            for (int gz = ccz - reach; gz <= ccz + reach; gz++)
                for (int gx = ccx - reach; gx <= ccx + reach; gx++)
                {
                    long key = CellKey(gx, gz);
                    if (!_byCell.TryGetValue(key, out List<PlacedTree> bucket)) continue;
                    for (int i = bucket.Count - 1; i >= 0; i--)
                    {
                        PlacedTree t = bucket[i];
                        if (t == null) { bucket.RemoveAt(i); continue; }
                        Vector3 p = t.transform.position;
                        float dx = p.x - center.x, dz = p.z - center.z;
                        if (dx * dx + dz * dz <= r2)
                        {
                            DestroySafe(t.gameObject);
                            bucket.RemoveAt(i);
                            _placed.Remove(t);
                            any = true;
                        }
                    }
                    if (bucket.Count == 0) _byCell.Remove(key);
                }
            return any;
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

        // ---- save / load / conform ----

        public List<PlacedTreeData> CollectData()
        {
            var list = new List<PlacedTreeData>(_placed.Count);
            foreach (PlacedTree t in _placed)
                if (t != null && t.Data != null) list.Add(t.Data);
            return list;
        }

        public List<TreePack> CollectPacks() => new List<TreePack>(_packs);

        // Stage loaded data + packs; SpawnPending() instantiates after chunks exist.
        public void LoadState(List<PlacedTreeData> data, List<TreePack> packs)
        {
            _pending = data;
            _packs.Clear();
            if (packs != null) _packs.AddRange(packs);
        }

        public void SpawnPending(TerrainField field)
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
        public void ConformToSurface(TerrainField field)
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

        static bool SamePrefabList(List<GameObject> a, List<GameObject> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public bool IsEmpty => Prefabs == null || Prefabs.Count == 0;
#endif

        // ---- palette UI (IMGUI) ----

        static readonly GUILayoutOption[] GlThumb = { GUILayout.Width(38), GUILayout.Height(38) };
        static readonly GUILayoutOption[] GlRow = { GUILayout.Height(38) };
        static readonly GUILayoutOption[] GlDel = { GUILayout.Width(24) };
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
                bool nowSel = GUILayout.Toggle(sel, _packs[i].Name + "  (" + _packs[i].Trees.Count + ")",
                                               GUI.skin.button);
                if (nowSel && !sel) ApplyPack(i);
                if (GUILayout.Button("x", GlDel)) { dirty |= DeletePack(i); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            _newPackName = GUILayout.TextField(_newPackName, GlField);
            if (GUILayout.Button("Save pack")) { dirty |= CreatePack(_newPackName); _newPackName = ""; }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            _scroll = GUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < Prefabs.Count; i++)
            {
                if (Prefabs[i] == null) continue;
                GUILayout.BeginHorizontal();
                _thumbs.TryGetValue(Prefabs[i], out Texture preview);
                if (preview != null) GUILayout.Label(preview, GlThumb);
                else GUILayout.Box("…", GlThumb);
                bool before = _enabled[i];
                string label = (_labels != null && i < _labels.Length) ? _labels[i] : "";
                _enabled[i] = GUILayout.Toggle(before, label, GlRow);
                if (_enabled[i] != before) _activePack = -1;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            return dirty;
        }
    }
}
