// GPU-instanced GRASS / detail-mesh scatter for the chunk world — the same instanced engine as ForestGen
// (per-species transform matrices binned into a coarse grid, frustum + distance culled, multi-LOD drawn with
// Graphics.RenderMeshInstanced, visible-set cached across small camera moves), but driven differently:
//   • GrowEverywhere(coverage): fill ALL loaded chunks at a coverage level (the "everywhere" slider).
//   • GrowBrush(x,z,radius): paint grass into a circular area to fill spots in.
// No elevation selection/highlight (grass is map-wide + brush). Placement uses the grass layer's slope/water
// rules so it stays in the grass zone (gentle slopes, above the waterline) matching the TerrainGround blend.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class GrassGen
    {
        // ── distribution tunables ──
        public static float Coverage = 0.6f;        // "everywhere" level 0..1 (fraction of the grass zone filled)
        public static float PatchFreq = 0.01f;      // clump scale (1/m) for natural patchiness (LOWER = bigger patches)
        public static int MaxGrass = 2000000;       // instance cap (~128 MB of matrices; grass is dense)
        public static bool ShowGrass = true;        // master draw toggle
        public const float CellSize = 64f;          // grid cell for broad-phase culling (tighter than trees — grass is short)
        public static float MaxRenderDistance = 600f; // grass past this (m) isn't drawn (short → cull near)
        public static float FarLodDarken = 0.7f;       // tint far LODs down to match the near mesh's self-shadowing
        static MaterialPropertyBlock[] _lodMpb;
        static MaterialPropertyBlock LodTint(int li, int lodN)
        {
            if (li <= 0 || FarLodDarken >= 0.999f) return null;
            if (_lodMpb == null) { _lodMpb = new MaterialPropertyBlock[MaxLods]; for (int i = 0; i < MaxLods; i++) _lodMpb[i] = new MaterialPropertyBlock(); }
            float t = lodN > 1 ? li / (float)(lodN - 1) : 1f;
            float d = Mathf.Lerp(1f, Mathf.Clamp01(FarLodDarken), t);
            _lodMpb[li].SetColor("_BaseColor", new Color(d, d, d, 1f));
            return _lodMpb[li];
        }

        sealed class Cell
        {
            public readonly List<Matrix4x4> items = new List<Matrix4x4>();
            public Bounds bounds;
            public bool boundsDirty = true;
        }

        struct LodLevel { public Mesh mesh; public Material[] mats; public float threshold; }
        const int MaxLods = 8;

        sealed class Species
        {
            public GameObject prefab;
            public LodLevel[] lods;
            public Vector3 baseScale;
            public float height;
            public readonly Dictionary<long, Cell> cells = new Dictionary<long, Cell>();
            public int count;
            public List<Matrix4x4>[] vis;
        }

        static Vector3 _lastCamPos = new Vector3(1e9f, 1e9f, 1e9f);
        static Quaternion _lastCamRot = Quaternion.identity;
        static float _lastMaxDist = -1f;
        static bool _visDirty = true;
        const float CamMoveDeadband = 6f, CamRotDeadband = 2f;

        public sealed class GrassSpeciesSave { public string Prefab; public float[] X, Y, Z, Rot, Scale; }
        static readonly List<Species> _species = new List<Species>();
        static readonly Dictionary<GameObject, Species> _byPrefab = new Dictionary<GameObject, Species>();
        public static int GrassCount { get; private set; }
        public static int CellCount { get { int n = 0; foreach (var sp in _species) n += sp.cells.Count; return n; } }

        static long CellKey(float wx, float wz)
        {
            int cx = Mathf.FloorToInt(wx / CellSize), cz = Mathf.FloorToInt(wz / CellSize);
            return ((long)cx << 32) ^ (uint)cz;
        }

        static void AddInstance(Species sp, float wx, float wz, Matrix4x4 m)
        {
            long key = CellKey(wx, wz);
            if (!sp.cells.TryGetValue(key, out var cell)) sp.cells[key] = cell = new Cell();
            cell.items.Add(m); cell.boundsDirty = true; sp.count++; GrassCount++; _visDirty = true;
        }

        // ── Grow ──────────────────────────────────────────────────────────────────────────────
        // Fill the whole loaded area at `coverage` (0..1). Returns instances added (accumulates until Clear).
        public static int GrowEverywhere(ScatterLayer layer, ITerrainSurface surf, float waterLevel, float coverage)
        {
            if (layer == null || surf == null || coverage <= 0f) return 0;
            if (!ChunkWorld.TryLoadedBounds(out Vector3 center, out float sx, out float sz)) return 0;
            return GrowRect(layer, surf, waterLevel, coverage,
                            center.x - sx * 0.5f, center.x + sx * 0.5f, center.z - sz * 0.5f, center.z + sz * 0.5f,
                            float.NegativeInfinity, 0f, 0f);
        }

        // Paint grass into a circle (brush fill) at `coverage`.
        public static int GrowBrush(ScatterLayer layer, ITerrainSurface surf, float waterLevel, float coverage, float wx, float wz, float radius)
        {
            if (radius <= 0f) return 0;
            return GrowRect(layer, surf, waterLevel, coverage, wx - radius, wx + radius, wz - radius, wz + radius, radius * radius, wx, wz);
        }

        // Lattice scatter over a world rect, gated by coverage × patch-noise + slope/water + (optional) a brush
        // circle (r2 >= 0). Loaded-chunk only (can't sample height elsewhere).
        static int GrowRect(ScatterLayer layer, ITerrainSurface surf, float waterLevel, float coverage,
                            float wx0, float wx1, float wz0, float wz1, float r2, float cx, float cz)
        {
            var prefabs = layer.EnabledPrefabs();
            if (prefabs.Count == 0) { Debug.LogWarning("[Grass] the active pack has no grass enabled."); return 0; }
            float spacing = Mathf.Max(0.4f, layer.Spacing);
            float maxSlope = layer.MaxSlopeDeg;
            bool avoidWater = layer.AvoidWater;
            float waterMargin = layer.WaterlineMargin;
            float lo = Mathf.Min(layer.ScaleRange.x, layer.ScaleRange.y), hi = Mathf.Max(layer.ScaleRange.x, layer.ScaleRange.y);
            bool brush = r2 >= 0f;

            int ix0 = Mathf.FloorToInt(wx0 / spacing), ix1 = Mathf.CeilToInt(wx1 / spacing);
            int iz0 = Mathf.FloorToInt(wz0 / spacing), iz1 = Mathf.CeilToInt(wz1 / spacing);
            int placed = 0;
            for (int iz = iz0; iz <= iz1 && GrassCount < MaxGrass; iz++)
                for (int ix = ix0; ix <= ix1 && GrassCount < MaxGrass; ix++)
                {
                    float jx = (Hash(ix, iz, 0) - 0.5f) * spacing * 0.9f;
                    float jz = (Hash(ix, iz, 1) - 0.5f) * spacing * 0.9f;
                    float px = ix * spacing + spacing * 0.5f + jx;
                    float pz = iz * spacing + spacing * 0.5f + jz;
                    if (brush) { float dx = px - cx, dz = pz - cz; if (dx * dx + dz * dz > r2) continue; }
                    if (!ChunkWorld.IsLoaded(ChunkWorld.ChunkAt(px, pz))) continue;
                    // coverage × patchiness: higher coverage fills more; the noise breaks it into natural patches.
                    float patch = 0.55f + 0.6f * Patch(px, pz);
                    if (Hash(ix, iz, 2) > coverage * patch) continue;
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
            if (_byPrefab.TryGetValue(prefab, out var sp)) return sp;
            if (!ExtractLods(prefab, out var lods, out var baseScale, out var height)) { _byPrefab[prefab] = null; return null; }
            sp = new Species { prefab = prefab, lods = lods, baseScale = baseScale, height = height };
            _byPrefab[prefab] = sp; _species.Add(sp);
            return sp;
        }

        static bool ExtractLods(GameObject prefab, out LodLevel[] lods, out Vector3 baseScale, out float height)
        {
            lods = null; baseScale = prefab.transform.localScale; height = 1f;
            var list = new List<LodLevel>();
            var lodGroup = prefab.GetComponentInChildren<LODGroup>();
            if (lodGroup != null)
                foreach (var L in lodGroup.GetLODs())
                {
                    MeshRenderer mr = null;
                    if (L.renderers != null) foreach (var r in L.renderers) { mr = r as MeshRenderer; if (mr != null) break; }
                    if (mr == null) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mats = mr.sharedMaterials;
                    if (mats != null) for (int i = 0; i < mats.Length; i++) if (mats[i] != null) mats[i].enableInstancing = true;
                    list.Add(new LodLevel { mesh = mf.sharedMesh, mats = mats, threshold = L.screenRelativeTransitionHeight });
                }
            if (list.Count == 0)
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
            height = Mathf.Max(0.2f, lods[0].mesh.bounds.size.y * Mathf.Abs(baseScale.y));
            return true;
        }

        // ── Render (GPU-instanced; visible-set cached across small camera moves) ──
        public static void RenderGrass(Camera cam)
        {
            if (!ShowGrass || cam == null || _species.Count == 0) return;
            Vector3 camPos = cam.transform.position;
            Quaternion camRot = cam.transform.rotation;
            bool rebuild = _visDirty || MaxRenderDistance != _lastMaxDist
                || (camPos - _lastCamPos).sqrMagnitude > CamMoveDeadband * CamMoveDeadband
                || Quaternion.Angle(camRot, _lastCamRot) > CamRotDeadband;
            if (rebuild) { RebuildVisible(cam, camPos); _lastCamPos = camPos; _lastCamRot = camRot; _lastMaxDist = MaxRenderDistance; _visDirty = false; }

            var bigBounds = new Bounds(camPos, new Vector3(MaxRenderDistance * 2f + 200f, 200000f, MaxRenderDistance * 2f + 200f));
            foreach (var sp in _species)
            {
                if (sp == null || sp.lods == null || sp.vis == null) continue;
                int lodN = Mathf.Min(sp.lods.Length, MaxLods);
                for (int li = 0; li < lodN; li++)
                {
                    var acc = sp.vis[li]; int total = acc.Count;
                    if (total == 0) continue;
                    LodLevel lod = sp.lods[li];
                    MaterialPropertyBlock mpb = LodTint(li, lodN);
                    int subCount = Mathf.Max(1, lod.mesh.subMeshCount);
                    for (int s = 0; s < subCount; s++)
                    {
                        Material mat = (lod.mats != null && s < lod.mats.Length && lod.mats[s] != null) ? lod.mats[s]
                                     : (lod.mats != null && lod.mats.Length > 0 ? lod.mats[0] : null);
                        if (mat == null) continue;
                        var rp = new RenderParams(mat)
                        {
                            shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,   // grass: cheap, no self-shadowing
                            receiveShadows = true,                                             // but darken in shadow like the terrain
                            worldBounds = bigBounds,
                            matProps = mpb
                        };
                        for (int off = 0; off < total; off += 1023)
                            Graphics.RenderMeshInstanced(rp, lod.mesh, s, acc, Mathf.Min(1023, total - off), off);
                    }
                }
            }
        }

        static void RebuildVisible(Camera cam, Vector3 camPos)
        {
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            float fovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float maxD2 = MaxRenderDistance * MaxRenderDistance;
            foreach (var sp in _species)
            {
                if (sp == null || sp.lods == null || sp.count == 0) continue;
                int lodN = Mathf.Min(sp.lods.Length, MaxLods);
                if (sp.vis == null) { sp.vis = new List<Matrix4x4>[MaxLods]; for (int i = 0; i < MaxLods; i++) sp.vis[i] = new List<Matrix4x4>(); }
                for (int i = 0; i < MaxLods; i++) sp.vis[i].Clear();
                foreach (var cell in sp.cells.Values)
                {
                    if (cell.items.Count == 0) continue;
                    if (cell.boundsDirty) { cell.bounds = ComputeCellBounds(cell.items); cell.boundsDirty = false; }
                    float d2 = (cell.bounds.center - camPos).sqrMagnitude;
                    if (d2 > maxD2) continue;
                    if (!GeometryUtility.TestPlanesAABB(planes, cell.bounds)) continue;
                    float screenH = sp.height / (2f * Mathf.Max(1f, Mathf.Sqrt(d2)) * fovTan);
                    int li = 0;
                    while (li < lodN - 1 && screenH < sp.lods[li].threshold) li++;
                    sp.vis[li].AddRange(cell.items);
                }
            }
        }

        static Bounds ComputeCellBounds(List<Matrix4x4> items)
        {
            Vector3 mn = new Vector3(1e9f, 1e9f, 1e9f), mx = new Vector3(-1e9f, -1e9f, -1e9f);
            for (int i = 0; i < items.Count; i++) { Vector3 p = items[i].GetColumn(3); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            var b = new Bounds(); b.SetMinMax(mn, mx); b.Expand(new Vector3(4f, 8f, 4f)); return b;
        }

        public static void ClearGrass() { _species.Clear(); _byPrefab.Clear(); GrassCount = 0; _visDirty = true; }
        public static void Teardown() => ClearGrass();

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
                            if (dx * dx + dz * dz <= r2) { items[i] = items[items.Count - 1]; items.RemoveAt(items.Count - 1); spRemoved++; }
                        }
                        if (spRemoved > 0) cell.boundsDirty = true;
                    }
                sp.count -= spRemoved; removed += spRemoved;
            }
            if (removed > 0) { GrassCount -= removed; _visDirty = true; }
            return removed;
        }

        // ── Persistence ──
        public static List<GrassSpeciesSave> ExportGrass()
        {
            var outp = new List<GrassSpeciesSave>(_species.Count);
            foreach (var sp in _species)
            {
                if (sp == null || sp.prefab == null || sp.count == 0) continue;
                int n = sp.count;
                var rec = new GrassSpeciesSave { Prefab = sp.prefab.name, X = new float[n], Y = new float[n], Z = new float[n], Rot = new float[n], Scale = new float[n] };
                int i = 0;
                foreach (var cell in sp.cells.Values)
                    for (int k = 0; k < cell.items.Count && i < n; k++, i++)
                    {
                        Matrix4x4 m = cell.items[k]; Vector3 c0 = m.GetColumn(0);
                        rec.X[i] = m.m03; rec.Y[i] = m.m13; rec.Z[i] = m.m23;
                        rec.Rot[i] = Mathf.Atan2(-c0.z, c0.x) * Mathf.Rad2Deg; rec.Scale[i] = c0.magnitude;
                    }
                outp.Add(rec);
            }
            return outp;
        }

        public static void ImportGrass(ScatterLayer layer, List<GrassSpeciesSave> data)
        {
            ClearGrass();
            if (layer == null || data == null) return;
            var ps = layer.Prefabs;
            foreach (var rec in data)
            {
                if (rec == null || string.IsNullOrEmpty(rec.Prefab) || rec.X == null) continue;
                GameObject prefab = null;
                for (int i = 0; i < ps.Count; i++) if (ps[i] != null && ps[i].name == rec.Prefab) { prefab = ps[i]; break; }
                if (prefab == null) continue;
                Species sp = SpeciesFor(prefab);
                if (sp == null) continue;
                int n = rec.X.Length;
                if (rec.Y == null || rec.Z == null || rec.Rot == null || rec.Scale == null) continue;
                for (int i = 0; i < n && GrassCount < MaxGrass; i++)
                {
                    float sc = rec.Scale[i];
                    AddInstance(sp, rec.X[i], rec.Z[i], Matrix4x4.TRS(new Vector3(rec.X[i], rec.Y[i], rec.Z[i]), Quaternion.Euler(0f, rec.Rot[i], 0f), new Vector3(sc, sc, sc)));
                }
            }
        }

        // Patchiness noise (fBM), ~[0,1] — breaks the everywhere fill into natural denser/sparser patches.
        static float Patch(float wx, float wz)
        {
            float x = wx * PatchFreq, z = wz * PatchFreq, amp = 1f, freq = 1f, sum = 0f, norm = 0f;
            for (int o = 0; o < 4; o++) { sum += amp * Mathf.PerlinNoise(x * freq, z * freq); norm += amp; amp *= 0.5f; freq *= 2f; }
            return norm > 0f ? sum / norm : 0f;
        }

        static float Hash(int x, int y, int seed)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return (h & 0xFFFFFFu) / (float)0x1000000;
        }
    }
}
