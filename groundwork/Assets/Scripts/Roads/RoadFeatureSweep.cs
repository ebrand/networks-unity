// Sweeps free-standing corridor FEATURES (parapets, and later fences/guardrails) along a road path. Unlike the
// road surface — an open top polyline lofted by RoadSweep — these are CLOSED 2D silhouettes extruded into solid
// walls that stand on the road, so they get their own meshes rather than being bands in the cross-section.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Geometry;

namespace NetworkDesigner.Roads
{
    public static class RoadFeatureSweep
    {
        // Concrete parapet silhouette (a New-Jersey-style barrier), as (lateral-from-its-own-centre, height) points
        // around the closed outline. For total height h the diagram's 1 m proportions scale: 0.125h vertical base
        // (0.75 m wide) → 0.25h batter narrowing to 0.25 m → 0.625h vertical stem. Widths are fixed (exact at h=1).
        static Vector2[] ParapetProfile(float h)
        {
            float baseY = 0.125f * h, midY = 0.375f * h, topY = h;   // 0.125 / 0.125+0.25 / 1.0
            const float bh = 0.375f, th = 0.125f;                    // base half-width, top (stem) half-width
            return new[]
            {
                new Vector2(-bh, 0f), new Vector2(-bh, baseY), new Vector2(-th, midY), new Vector2(-th, topY),
                new Vector2(th, topY), new Vector2(th, midY), new Vector2(bh, baseY), new Vector2(bh, 0f),
            };
        }

        static readonly Color Concrete = new Color(0.62f, 0.62f, 0.60f);

        // Extrude the parapet along the path (a→b, cubic via ca/cb when curve) at lateral offset `latOffset` from the
        // road centre, standing on the same grade as the road body (hA→hB, terrain-follow blend). Returns the mesh GO.
        public static GameObject BuildParapet(Vector2 a, Vector2 ca, Vector2 cb, Vector2 b, bool curve,
            float latOffset, float height, float hA, float hB,
            System.Func<Vector2, float> groundAt, float follow, Transform parent, string name)
        {
            if (height < 0.05f) return null;
            Vector2[] prof = ParapetProfile(height);
            int N = prof.Length;

            float len = curve ? GeometryResolver.CubicArcLength(a, ca, cb, b) : Vector2.Distance(a, b);
            int frames = Mathf.Clamp(Mathf.CeilToInt(len / 1.0f) + 1, 2, 1024);
            var baseY = RoadSweep.ElevationProfile(a, b, curve, ca, cb, frames, hA, hB, groundAt, follow);

            var verts = new List<Vector3>(frames * (N + 1));
            for (int f = 0; f < frames; f++)
            {
                float t = f / (float)(frames - 1);
                Vector2 p, tan;
                if (curve) { p = GeometryResolver.SampleCubic(a, ca, cb, b, t); tan = GeometryResolver.CubicTangent(a, ca, cb, b, t); }
                else { p = Vector2.Lerp(a, b, t); tan = b - a; }
                Vector3 fwd = new Vector3(tan.x, 0f, tan.y); fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 baseC = new Vector3(p.x, baseY[f], p.y) + right * latOffset;
                for (int i = 0; i < N; i++) verts.Add(baseC + right * prof[i].x + Vector3.up * prof[i].y);
            }

            var tris = new List<int>(frames * N * 6);
            for (int f = 0; f < frames - 1; f++)
                for (int i = 0; i < N; i++)
                {
                    int j = (i + 1) % N;
                    int s0 = f * N, s1 = (f + 1) * N;
                    tris.Add(s0 + i); tris.Add(s1 + j); tris.Add(s0 + j);
                    tris.Add(s0 + i); tris.Add(s1 + i); tris.Add(s1 + j);
                }
            // End caps: centroid fan (the silhouette is star-convex from its centroid).
            AddCap(verts, tris, prof, 0, false);
            AddCap(verts, tris, prof, frames - 1, true);
            OrientOutward(verts, tris);

            var mesh = new Mesh { name = name };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();

            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = NetworkDesigner.PipelineMaterials.CreateLitMatte(Concrete, "ParapetConcrete");
            return go;
        }

        // ── chainlink fence: instance a panel prefab tiled along the path ────────────────────────────────
        static GameObject _fencePrefab; static bool _fenceTried; static float _fenceLen = 2f; static bool _fenceAxisX = true;

        static GameObject FencePrefab()
        {
            if (_fenceTried) return _fencePrefab;
            _fenceTried = true;
            // Loaded from Resources (path relative to a Resources/ folder, no extension). Swap "chainlink_single"
            // for "chainlink_barbwire-1" to get the barbwire-topped variant.
            _fencePrefab = Resources.Load<GameObject>("Fences/Assets_FenceChained/prefabs/chainlink_single");
            if (_fencePrefab == null)
                Debug.LogWarning("[RoadFeatureSweep] chainlink fence prefab not found at " +
                                 "Resources/Fences/Assets_FenceChained/prefabs/chainlink_single — fences won't render.");
            if (_fencePrefab != null)
            {
                // Auto-measure one panel so we tile at its real length without hardcoding dimensions/orientation.
                var t = Object.Instantiate(_fencePrefab);
                t.hideFlags = HideFlags.HideAndDontSave;
                var rends = t.GetComponentsInChildren<Renderer>();
                if (rends.Length > 0)
                {
                    Bounds bb = rends[0].bounds;
                    for (int i = 1; i < rends.Length; i++) bb.Encapsulate(rends[i].bounds);
                    _fenceLen = Mathf.Max(0.5f, Mathf.Max(bb.size.x, bb.size.z));
                    _fenceAxisX = bb.size.x >= bb.size.z;   // which local horizontal axis runs along the panel
                }
                Object.DestroyImmediate(t);
            }
            return _fencePrefab;
        }

        // Tile the chainlink panel along the path at lateral offset `latOffset`, standing on the road grade. Returns
        // a holder GO (children = panel instances), or null if the prefab couldn't be loaded.
        public static GameObject BuildFence(Vector2 a, Vector2 ca, Vector2 cb, Vector2 b, bool curve,
            float latOffset, float hA, float hB, System.Func<Vector2, float> groundAt, float follow,
            Transform parent, string name)
        {
            GameObject prefab = FencePrefab();
            if (prefab == null) return null;
            float len = curve ? GeometryResolver.CubicArcLength(a, ca, cb, b) : Vector2.Distance(a, b);
            if (len < 0.1f) return null;
            int n = Mathf.Clamp(Mathf.CeilToInt(len / _fenceLen), 1, 512);
            var baseY = RoadSweep.ElevationProfile(a, b, curve, ca, cb, n, hA, hB, groundAt, follow);

            var holder = new GameObject(name) { hideFlags = HideFlags.DontSave };
            if (parent != null) holder.transform.SetParent(parent, false);
            Quaternion axisFix = _fenceAxisX ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;  // align panel length to tangent
            for (int k = 0; k < n; k++)
            {
                float t = (k + 0.5f) / n;
                Vector2 p, tan;
                if (curve) { p = GeometryResolver.SampleCubic(a, ca, cb, b, t); tan = GeometryResolver.CubicTangent(a, ca, cb, b, t); }
                else { p = Vector2.Lerp(a, b, t); tan = b - a; }
                Vector3 fwd = new Vector3(tan.x, 0f, tan.y); fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                int yi = Mathf.Clamp(k, 0, baseY.Length - 1);
                Vector3 pos = new Vector3(p.x, baseY[yi], p.y) + right * latOffset;
                var inst = Object.Instantiate(prefab, pos, Quaternion.LookRotation(fwd, Vector3.up) * axisFix, holder.transform);
                inst.hideFlags = HideFlags.DontSave;
            }
            return holder;
        }

        // ── rail: two rails + ties extruded along the path (the Rail band itself is the gravel ballast base) ──
        static readonly Color RailSteel = new Color(0.28f, 0.28f, 0.30f);   // = RailTrackLayer.RailColor
        static readonly Color TieWood = new Color(0.32f, 0.22f, 0.14f);     // = RailTrackLayer.TieColor

        // Lay a single standard-gauge track (rails + sleepers) centred at lateral `latOffset`, on the road grade.
        public static GameObject BuildRail(Vector2 a, Vector2 ca, Vector2 cb, Vector2 b, bool curve,
            float latOffset, float hA, float hB, System.Func<Vector2, float> groundAt, float follow,
            Transform parent, string name)
        {
            const float gauge = 1.5f, railW = 0.1f, railH = 0.14f, tieH = 0.12f, tieLen = 2.0f,
                        tieThick = 0.24f, tieSpacing = 0.7f, lift = 0.03f;
            float len = curve ? GeometryResolver.CubicArcLength(a, ca, cb, b) : Vector2.Distance(a, b);
            if (len < 0.2f) return null;
            int frames = Mathf.Clamp(Mathf.CeilToInt(len / 1.0f) + 1, 2, 1024);
            var baseY = RoadSweep.ElevationProfile(a, b, curve, ca, cb, frames, hA, hB, groundAt, follow);

            var P = new Vector3[frames]; var R = new Vector3[frames];
            for (int f = 0; f < frames; f++)
            {
                float t = f / (float)(frames - 1);
                Vector2 p, tan;
                if (curve) { p = GeometryResolver.SampleCubic(a, ca, cb, b, t); tan = GeometryResolver.CubicTangent(a, ca, cb, b, t); }
                else { p = Vector2.Lerp(a, b, t); tan = b - a; }
                Vector3 fwd = new Vector3(tan.x, 0f, tan.y); fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;
                P[f] = new Vector3(p.x, baseY[f], p.y); R[f] = Vector3.Cross(Vector3.up, fwd).normalized;
            }

            var rv = new List<Vector3>(); var rt = new List<int>();   // rails (steel)
            var tv = new List<Vector3>(); var tt = new List<int>();   // ties (wood)

            // Two continuous rails, on top of the ties.
            for (int side = -1; side <= 1; side += 2)
            {
                float c = latOffset + side * gauge * 0.5f;
                var ring = new[]
                {
                    new Vector2(c - railW * 0.5f, tieH), new Vector2(c + railW * 0.5f, tieH),
                    new Vector2(c + railW * 0.5f, tieH + railH), new Vector2(c - railW * 0.5f, tieH + railH),
                };
                ExtrudeRing(rv, rt, P, R, lift, ring);
            }

            // Sleepers: a box every tieSpacing across the track.
            int nTies = Mathf.Max(1, Mathf.FloorToInt(len / tieSpacing));
            for (int k = 0; k <= nTies; k++)
            {
                float arc = Mathf.Min(k * tieSpacing, len);
                float t = curve ? GeometryResolver.ArcLengthToT(a, ca, cb, b, arc) : (len > 0f ? arc / len : 0f);
                Vector2 p, tan;
                if (curve) { p = GeometryResolver.SampleCubic(a, ca, cb, b, t); tan = GeometryResolver.CubicTangent(a, ca, cb, b, t); }
                else { p = Vector2.Lerp(a, b, t); tan = b - a; }
                Vector3 fwd = new Vector3(tan.x, 0f, tan.y); fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                int yi = Mathf.Clamp(Mathf.RoundToInt(t * (frames - 1)), 0, frames - 1);
                Vector3 ctr = new Vector3(p.x, baseY[yi] + lift + tieH * 0.5f, p.y) + right * latOffset;
                AddBox(tv, tt, ctr, fwd, right, Vector3.up, tieThick * 0.5f, tieLen * 0.5f, tieH * 0.5f);
            }

            // Combine into one mesh, two submeshes (0 = rails, 1 = ties).
            var verts = new List<Vector3>(rv.Count + tv.Count);
            verts.AddRange(rv); verts.AddRange(tv);
            int off = rv.Count;
            var tieTris = new List<int>(tt.Count);
            for (int i = 0; i < tt.Count; i++) tieTris.Add(tt[i] + off);

            var mesh = new Mesh { name = name };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(rt, 0);
            mesh.SetTriangles(tieTris, 1);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();

            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                NetworkDesigner.PipelineMaterials.CreateLit(RailSteel, 0.6f),
                NetworkDesigner.PipelineMaterials.CreateLitMatte(TieWood, "RailTie"),
            };
            return go;
        }

        // Extrude a closed (lateral, height) ring along the frames (open ends — rail ends sit inside junctions).
        static void ExtrudeRing(List<Vector3> verts, List<int> tris, Vector3[] P, Vector3[] R, float lift, Vector2[] ring)
        {
            int N = ring.Length, baseIdx = verts.Count, frames = P.Length;
            for (int f = 0; f < frames; f++)
                for (int i = 0; i < N; i++)
                    verts.Add(P[f] + R[f] * ring[i].x + Vector3.up * (lift + ring[i].y));
            for (int f = 0; f < frames - 1; f++)
                for (int i = 0; i < N; i++)
                {
                    int j = (i + 1) % N, s0 = baseIdx + f * N, s1 = baseIdx + (f + 1) * N;
                    // Wound so normals face OUTWARD of the ring (the rail box). For a CCW (lateral,height) ring
                    // extruded along +path, this is the order that puts the rail's outer/inner/top faces lit.
                    tris.Add(s0 + i); tris.Add(s0 + j); tris.Add(s1 + j);
                    tris.Add(s0 + i); tris.Add(s1 + j); tris.Add(s1 + i);
                }
        }

        // An oriented solid box (12 tris) centred at `c` with the given half-extents along fwd/right/up.
        static void AddBox(List<Vector3> v, List<int> t, Vector3 c, Vector3 fwd, Vector3 right, Vector3 up,
                           float hf, float hr, float hu)
        {
            int s = v.Count;
            for (int sf = -1; sf <= 1; sf += 2)
                for (int sr = -1; sr <= 1; sr += 2)
                    for (int su = -1; su <= 1; su += 2)
                        v.Add(c + fwd * (hf * sf) + right * (hr * sr) + up * (hu * su));
            // 8 corners indexed by (sf,sr,su) bits: idx = ((sf+1)/2)*4 + ((sr+1)/2)*2 + ((su+1)/2)
            int[] f =
            {
                0,1,3, 0,3,2,  4,7,5, 4,6,7,   // -fwd, +fwd
                0,4,5, 0,5,1,  2,3,7, 2,7,6,   // -right, +right
                0,2,6, 0,6,4,  1,5,7, 1,7,3,   // -up, +up
            };
            for (int i = 0; i < f.Length; i++) t.Add(s + f[i]);
        }

        // Flip any triangle whose face normal points toward the mesh centroid, so all faces wind OUTWARD before
        // RecalculateNormals. Reliable for convex-ish swept solids (parapet, rail) regardless of ring winding.
        static void OrientOutward(List<Vector3> verts, List<int> tris)
        {
            if (verts.Count == 0 || tris.Count < 3) return;
            Vector3 c = Vector3.zero;
            for (int i = 0; i < verts.Count; i++) c += verts[i];
            c /= verts.Count;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], d = verts[tris[i + 2]];
                Vector3 n = Vector3.Cross(b - a, d - a);
                if (Vector3.Dot(n, (a + b + d) / 3f - c) < 0f) { int tmp = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = tmp; }
            }
        }

        // Triangulate one end ring as a fan from a centre vertex appended at the end of `verts`.
        static void AddCap(List<Vector3> verts, List<int> tris, Vector2[] prof, int frame, bool flip)
        {
            int N = prof.Length, ring = frame * N;
            Vector3 c = Vector3.zero;
            for (int i = 0; i < N; i++) c += verts[ring + i];
            c /= N;
            int ci = verts.Count; verts.Add(c);
            for (int i = 0; i < N; i++)
            {
                int j = (i + 1) % N;
                if (flip) { tris.Add(ci); tris.Add(ring + i); tris.Add(ring + j); }
                else { tris.Add(ci); tris.Add(ring + j); tris.Add(ring + i); }
            }
        }
    }
}
