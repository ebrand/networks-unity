// Lofts a RoadCrossSection along a path (straight A→B, or a cubic bezier) into a Unity mesh — one submesh
// per surface kind. The path lives in world XZ (Vector2), matching GeometryResolver; per-end heights raise
// the road off the ground (linear A→B), and cross-section Y shapes the surface. Reuses the project's
// "right = Cross(up, forward)" frame convention (see RoadRenderer) and GeometryResolver's bezier sampling.
//
// When the cross-section has Thickness > 0 the road is closed into a solid slab: a flat underside at
// (road − Thickness), the two outer edges dropped to it, and end caps at both ends — so a RAISED road
// reads as a deck rather than a one-sided skin. Deck faces are double-sided (winding-proof).

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Geometry;
using NetworkDesigner.Rendering;   // PipelineMaterials

namespace NetworkDesigner.Roads
{
    public static class RoadSweep
    {
        const float UvTile = 2f;          // metres per UV unit (PBR tiling)
        const float SampleEvery = 2f;     // ~one cross-section frame per this many metres along a curve
        const float Eps = 1e-4f;

        public static GameObject Build(RoadCrossSection xs, Vector2 a, Vector2 b, bool curve, Vector2 cA, Vector2 cB,
                                       Transform parent, string name = "Road", float heightA = 0f, float heightB = 0f)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            var mesh = BuildMesh(xs, a, b, curve, cA, cB, out var surfaces, heightA, heightB);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = MaterialsFor(surfaces);
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return go;
        }

        public static Mesh BuildMesh(RoadCrossSection xs, Vector2 a, Vector2 b, bool curve, Vector2 cA, Vector2 cB,
                                     out RoadSurface[] surfaces, float heightA = 0f, float heightB = 0f)
        {
            int nPts = xs.Pts.Count;
            float centerU = xs.Center();
            bool deck = xs.Thickness > 0f;
            float bottomY = -xs.Thickness;

            // Path frames: position (with per-end height) + right vector + arc distance.
            int frames = curve
                ? Mathf.Clamp(Mathf.CeilToInt(GeometryResolver.CubicArcLength(a, cA, cB, b) / SampleEvery), 2, 512)
                : 2;
            var fp = new Vector3[frames];
            var fr = new Vector3[frames];
            var fs = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float t = (float)f / (frames - 1);
                Vector2 p, tan;
                if (curve) { p = GeometryResolver.SampleCubic(a, cA, cB, b, t); tan = GeometryResolver.CubicTangent(a, cA, cB, b, t); }
                else { p = Vector2.Lerp(a, b, t); tan = b - a; }
                Vector3 fwd = new Vector3(tan.x, 0f, tan.y);
                fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;
                fp[f] = new Vector3(p.x, Mathf.Lerp(heightA, heightB, t), p.y);
                fr[f] = Vector3.Cross(Vector3.up, fwd).normalized;
                fs[f] = f == 0 ? 0f : fs[f - 1] + (fp[f] - fp[f - 1]).magnitude;
            }

            // Vertex grid: top (cross-section Y) and, for a deck, a flat bottom at bottomY.
            var verts = new List<Vector3>(frames * nPts * (deck ? 2 : 1));
            var uvs = new List<Vector2>(verts.Capacity);
            for (int f = 0; f < frames; f++)
                for (int i = 0; i < nPts; i++)
                {
                    Vector2 cp = xs.Pts[i];
                    verts.Add(fp[f] + fr[f] * (cp.x - centerU) + Vector3.up * cp.y);
                    uvs.Add(new Vector2(fs[f] / UvTile, cp.x / UvTile));
                }
            int bottomBase = verts.Count;
            if (deck)
                for (int f = 0; f < frames; f++)
                    for (int i = 0; i < nPts; i++)
                    {
                        float u = xs.Pts[i].x;
                        verts.Add(fp[f] + fr[f] * (u - centerU) + Vector3.up * bottomY);
                        uvs.Add(new Vector2(fs[f] / UvTile, u / UvTile));
                    }

            var present = new List<RoadSurface>();
            var triByKind = new Dictionary<RoadSurface, List<int>>();
            List<int> Tris(RoadSurface k)
            {
                if (!triByKind.TryGetValue(k, out var t)) { t = new List<int>(); triByKind[k] = t; present.Add(k); }
                return t;
            }
            void Quad(List<int> t, int v0, int v1, int v2, int v3, bool two)
            {
                t.Add(v0); t.Add(v1); t.Add(v2); t.Add(v0); t.Add(v2); t.Add(v3);
                if (two) { t.Add(v0); t.Add(v2); t.Add(v1); t.Add(v0); t.Add(v3); t.Add(v2); }
            }
            int T(int f, int i) => f * nPts + i;
            int B(int f, int i) => bottomBase + f * nPts + i;

            // Top surface: one band per cross-section segment; vertical bands (kerb/median walls) double-sided.
            for (int seg = 0; seg < xs.Segs.Count; seg++)
            {
                var t = Tris(xs.Segs[seg]);
                bool vertical = Mathf.Abs(xs.Pts[seg].x - xs.Pts[seg + 1].x) < Eps;
                for (int f = 0; f < frames - 1; f++)
                    Quad(t, T(f, seg), T(f + 1, seg), T(f + 1, seg + 1), T(f, seg + 1), vertical);
            }

            // Deck solid: flat underside + outer edges + end caps. All double-sided (winding-proof).
            if (deck)
            {
                var t = Tris(RoadSurface.Deck);
                for (int seg = 0; seg < xs.Segs.Count; seg++)   // underside (coplanar bands; skip zero-width)
                {
                    if (Mathf.Abs(xs.Pts[seg].x - xs.Pts[seg + 1].x) < Eps) continue;
                    for (int f = 0; f < frames - 1; f++)
                        Quad(t, B(f, seg), B(f + 1, seg), B(f + 1, seg + 1), B(f, seg + 1), true);
                }
                for (int f = 0; f < frames - 1; f++)            // left + right outer edges (top ↔ bottom)
                {
                    Quad(t, T(f, 0), T(f + 1, 0), B(f + 1, 0), B(f, 0), true);
                    Quad(t, T(f, nPts - 1), T(f + 1, nPts - 1), B(f + 1, nPts - 1), B(f, nPts - 1), true);
                }
                foreach (int cf in new[] { 0, frames - 1 })     // end caps: top profile down to the flat bottom
                    for (int seg = 0; seg < xs.Segs.Count; seg++)
                    {
                        if (Mathf.Abs(xs.Pts[seg].x - xs.Pts[seg + 1].x) < Eps) continue;
                        Quad(t, T(cf, seg), T(cf, seg + 1), B(cf, seg + 1), B(cf, seg), true);
                    }
            }

            var mesh = new Mesh { name = "RoadSweep" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = present.Count;
            for (int s = 0; s < present.Count; s++) mesh.SetTriangles(triByKind[present[s]], s);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            surfaces = present.ToArray();
            return mesh;
        }

        static Material[] MaterialsFor(RoadSurface[] surfaces)
        {
            var mats = new Material[surfaces.Length];
            for (int i = 0; i < surfaces.Length; i++) mats[i] = SurfaceMat(surfaces[i]);
            return mats;
        }

        static Material SurfaceMat(RoadSurface s)
        {
            Color c = s switch
            {
                RoadSurface.Asphalt  => new Color(0.18f, 0.18f, 0.20f),
                RoadSurface.Shoulder => new Color(0.30f, 0.30f, 0.32f),
                RoadSurface.Concrete => new Color(0.62f, 0.62f, 0.60f),
                RoadSurface.Grass    => new Color(0.30f, 0.45f, 0.22f),
                RoadSurface.Dirt     => new Color(0.40f, 0.30f, 0.20f),
                RoadSurface.Deck     => new Color(0.45f, 0.45f, 0.48f),
                RoadSurface.Curb     => new Color(0.72f, 0.72f, 0.72f),   // light gray
                RoadSurface.Sidewalk => new Color(0.85f, 0.85f, 0.86f),   // white
                RoadSurface.Guardrail => new Color(0.56f, 0.57f, 0.60f),  // steel
                _ => Color.magenta
            };
            return PipelineMaterials.CreateLitMatte(c, "RoadSurface_" + s);
        }
    }
}
