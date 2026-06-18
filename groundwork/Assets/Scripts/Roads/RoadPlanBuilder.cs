// Build Plan, phase 1: resolve a road Network with the GeometryResolver brain, then sweep each road BODY as
// terrain-draped 3D between its SETBACK-TRIMMED endpoints — so roads pull back from intersections exactly
// like the original renderer, but as a 3D slab laid into the excavated bed instead of a flat Y=0 ribbon.
//
// The setback per end comes from VertexApproach.Setback (the resolver's intersection algo); a curved road is
// clipped to its [setbackA, len-setbackB] arc-length range via a de Casteljau sub-bezier. Road elevation
// follows the same node-to-node grade line the excavation cut to, and Thickness fills the cut. Phase 2 adds
// the intersection fill from VertexGeometry.Outline; phase 3 adds lane markings / flow.

using System;
using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;
using NetworkDesigner.Geometry;
using NetworkDesigner.Rendering;   // PipelineMaterials

namespace NetworkDesigner.Roads
{
    public static class RoadPlanBuilder
    {
        // Resolve `net` and sweep every road body, parented under a fresh "RoadPlanBuild" GO (returned).
        // `vertexElev(vertexId)` gives the per-node DESIGN elevation (the same height Excavate graded/cut to,
        // captured from the shaped surface) so the road sits in its cut. `excavationDepth` sets the minimum
        // slab thickness so the body fills the cut. Returns the root even when nothing builds.
        // `onlyRoads` (road ids "r{e}") restricts which roads get built. The geometry is resolved over JUST those roads
        // (and their vertices), so PLANNED/un-built segments never shape a built junction — drawing or deleting plan
        // lines elsewhere can't make intersection geometry appear before you Build. Pass null to build the whole plan.
        public static GameObject Build(Network net, Func<string, float> vertexElev, float excavationDepth, Transform parent,
                                       HashSet<string> onlyRoads = null, Func<Vector2, float> groundAt = null, float follow = 0f)
        {
            var root = new GameObject("RoadPlanBuild");
            if (parent != null) root.transform.SetParent(parent, false);
            if (net == null || net.Roads == null || net.Roads.Count == 0) return root;

            // Resolve only the BUILT sub-network (the roads in onlyRoads + the vertices they touch). Resolving the full
            // net would let un-built plan segments push setbacks / grow junction outlines on roads that ARE built —
            // surfacing as "intersections before you build". Reuses the same Vertex/Road objects (read-only resolve).
            Network rnet = net;
            if (onlyRoads != null)
            {
                var vpos = new Dictionary<string, Vector2>();
                foreach (Vertex v in net.Vertices) if (v != null) vpos[v.Id] = v.Position;
                rnet = new Network { DriveSide = net.DriveSide };
                var keepV = new HashSet<string>();
                foreach (NetworkRoad r in net.Roads)
                {
                    if (r == null || !onlyRoads.Contains(r.Id)) continue;
                    // Drop a degenerate (coincident-endpoint, ~0-length) road: resolving it warns and corrupts the shared
                    // junction (negative-length body → far-flung vertices → giant collider triangles on the NEIGHBOURS).
                    if (vpos.TryGetValue(r.EndA, out Vector2 pa) && vpos.TryGetValue(r.EndB, out Vector2 pb)
                        && (pa - pb).sqrMagnitude < 0.25f) continue;
                    rnet.Roads.Add(r); keepV.Add(r.EndA); keepV.Add(r.EndB);
                }
                foreach (Vertex v in net.Vertices) if (v != null && keepV.Contains(v.Id)) rnet.Vertices.Add(v);
            }

            List<VertexGeometry> resolved = GeometryResolver.ResolveNetwork(rnet);
            var vgById = new Dictionary<string, VertexGeometry>();
            if (resolved != null) foreach (VertexGeometry vg in resolved) if (vg != null) vgById[vg.VertexId] = vg;
            var vById = new Dictionary<string, Vertex>();
            foreach (Vertex v in net.Vertices) vById[v.Id] = v;
            var profById = new Dictionary<string, RoadProfile>();   // road id -> profile, for the junction edge band
            foreach (NetworkRoad r in net.Roads) if (r != null && r.Profile != null) profById[r.Id] = r.Profile;

            float depth = Mathf.Max(0f, excavationDepth);
            int built = 0, skipped = 0;
            foreach (NetworkRoad road in net.Roads)
            {
                if (road == null || road.Profile == null) { skipped++; continue; }
                if (onlyRoads != null && !onlyRoads.Contains(road.Id)) { skipped++; continue; }   // segment-build filter
                if (!vById.TryGetValue(road.EndA, out Vertex va) || !vById.TryGetValue(road.EndB, out Vertex vb)) { skipped++; continue; }

                Vector2 p0 = va.Position, p3 = vb.Position, c1, c2;
                bool curve = road.Curve != null;
                if (curve) { c1 = road.Curve.ControlA; c2 = road.Curve.ControlB; }
                else { Vector2 d = p3 - p0; c1 = p0 + d / 3f; c2 = p0 + d * (2f / 3f); }

                float len = curve ? GeometryResolver.CubicArcLength(p0, c1, c2, p3) : Vector2.Distance(p0, p3);
                if (len < 0.5f) { skipped++; continue; }

                float sA = ApproachSetback(vgById, road.EndA, road.Id);
                float sB = ApproachSetback(vgById, road.EndB, road.Id);
                float tA = curve ? GeometryResolver.ArcLengthToT(p0, c1, c2, p3, sA) : Mathf.Clamp01(sA / len);
                float tB = curve ? GeometryResolver.ArcLengthToT(p0, c1, c2, p3, len - sB) : Mathf.Clamp01(1f - sB / len);
                if (tB - tA < 0.02f) { skipped++; continue; }   // setbacks meet → road wholly inside the junction boxes

                SubCubic(p0, c1, c2, p3, tA, tB, out Vector2 a, out Vector2 ca, out Vector2 cb, out Vector2 b);

                // Road surface rides the node-to-node DESIGN grade (same as the excavation); thickness fills the cut.
                float yA = vertexElev != null ? vertexElev(road.EndA) : 0f;
                float yB = vertexElev != null ? vertexElev(road.EndB) : 0f;
                float hA = Mathf.Lerp(yA, yB, tA), hB = Mathf.Lerp(yA, yB, tB);

                RoadCrossSection xs = RoadCrossSectionBuilder.FromProfile(road.Profile);
                if (xs.Thickness < depth) xs.Thickness = depth;

                var segGo = RoadSweep.Build(xs, a, b, curve, ca, cb, root.transform, road.Id, hA, hB, groundAt, follow);
                WarnIfHugeMesh(segGo, road.Id);
                BuildRoadMarkings(road.Profile, a, ca, cb, b, curve, hA, hB, root.transform, road.Id + "_marks", groundAt, follow);
                built++;
            }

            // Intersection fill: extrude each resolved vertex's asphalt OUTLINE (setback edges + bezier
            // fillets — the resolver's junction algo) into a flat pad at the node grade, Thickness deep, so
            // it fills the cut and the gap the road setbacks left. Degenerate outlines (lone termini) skip.
            int pads = 0;
            if (resolved != null)
                foreach (VertexGeometry vg in resolved)
                {
                    if (vg == null || vg.Outline == null || vg.Outline.Count < 2) continue;
                    // Only fill a junction whose roads are actually built — so a partial/incremental build doesn't
                    // drop pads at intersections where nothing is swept yet (matches the whole-plan look).
                    if (onlyRoads != null && !VertexHasBuiltApproach(vg, onlyRoads)) continue;
                    if (!vById.TryGetValue(vg.VertexId, out Vertex v)) continue;
                    List<Vector2> ring = SampleOutlineRing(vg.Outline);
                    if (ring.Count < 3) continue;
                    float gy = vertexElev != null ? vertexElev(vg.VertexId) : 0f;
                    BuildIntersectionPad(ring, v.Position, gy, depth, root.transform, "X_" + vg.VertexId);
                    // Overlay the raised edge stack (shoulder/sidewalk + curb + parapet) around corners whose
                    // flanking approaches share a profile. Additive on top of the flat pad; no-op (no GO) when
                    // nothing matches or there's no edge stack, so disparate/edgeless junctions are unchanged.
                    BuildJunctionEdgeBands(vg, profById, gy, root.transform, "XE_" + vg.VertexId);
                    pads++;
                }

            Debug.Log($"[Road] Build Plan: swept {built}/{net.Roads.Count} road bodies + {pads} intersection pads"
                      + (skipped > 0 ? $", {skipped} skipped (no profile / too short / fully set back)." : "."));
            return root;
        }

        // True if any road approaching this vertex is in the built set (so its junction pad should be filled).
        static bool VertexHasBuiltApproach(VertexGeometry vg, HashSet<string> onlyRoads)
        {
            if (vg.Approaches == null) return false;
            foreach (VertexApproach ap in vg.Approaches) if (ap != null && onlyRoads.Contains(ap.RoadId)) return true;
            return false;
        }

        // Setback (m along centerline) the resolver computed for `roadId` at `vertexId`, 0 if not found.
        static float ApproachSetback(Dictionary<string, VertexGeometry> vgById, string vertexId, string roadId)
        {
            if (vgById.TryGetValue(vertexId, out VertexGeometry g) && g.Approaches != null)
                foreach (VertexApproach ap in g.Approaches)
                    if (ap.RoadId == roadId) return Mathf.Max(0f, ap.Setback);
            return 0f;
        }

        // The cubic sub-segment of (p0,c1,c2,p3) over [t0,t1], as (a, cA, cB, b). Two de Casteljau splits:
        // clip to [0,t1], then take the [t0/t1, 1] tail of that. (a/b are the points at t0/t1.)
        static void SubCubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float t0, float t1,
                             out Vector2 a, out Vector2 cA, out Vector2 cB, out Vector2 b)
        {
            // Split at t1 → left cubic (l0,l1,l2,l3) covering [0,t1].
            Vector2 a01 = V.L(p0, c1, t1), a12 = V.L(c1, c2, t1), a23 = V.L(c2, p3, t1);
            Vector2 a012 = V.L(a01, a12, t1), a123 = V.L(a12, a23, t1);
            Vector2 l0 = p0, l1 = a01, l2 = a012, l3 = V.L(a012, a123, t1);   // l3 = point at t1
            // Split the left cubic at u = t0/t1 → right tail = sub [t0,t1].
            float u = t1 > 1e-6f ? Mathf.Clamp01(t0 / t1) : 0f;
            Vector2 b01 = V.L(l0, l1, u), b12 = V.L(l1, l2, u), b23 = V.L(l2, l3, u);
            Vector2 b012 = V.L(b01, b12, u), b123 = V.L(b12, b23, u);
            a = V.L(b012, b123, u);   // point at t0
            cA = b123; cB = b23; b = l3;
        }

        static class V { public static Vector2 L(Vector2 p, Vector2 q, float t) => p + (q - p) * t; }

        // The vertex's closed asphalt outline sampled to a ring of XZ points. Each segment contributes its
        // From (+ interior points for fillets); the next segment's From == this segment's To, so the ring
        // closes without duplicates.
        static List<Vector2> SampleOutlineRing(List<OutlineSegment> outline)
        {
            var pts = new List<Vector2>();
            foreach (OutlineSegment s in outline)
            {
                if (s == null) continue;
                switch (s.Kind)
                {
                    case SegmentKind.QuadraticBezier:
                        // The fillet control is the OE-edge intersection; at near-parallel (acute) corners it flies far
                        // off and the bezier bulges into a spike. Bound its reach so the corner stays a compact fillet.
                        Vector2 qc = ClampFilletControl(s.From, s.Control, s.To);
                        for (int i = 0; i < 8; i++) pts.Add(GeometryResolver.SampleQuadratic(s.From, qc, s.To, i / 8f));
                        break;
                    case SegmentKind.CubicBezier:
                        for (int i = 0; i < 10; i++) pts.Add(GeometryResolver.SampleCubic(s.From, s.Control, s.Control2, s.To, i / 10f));
                        break;
                    default:   // Line
                        pts.Add(s.From);
                        break;
                }
            }
            return pts;
        }

        // Cap how far a quadratic fillet control may sit from its chord midpoint. A normal convex corner keeps its
        // control within ~1×chord; a near-parallel acute corner's OE intersection runs to infinity → spike. Clamping
        // toward the chord flattens that corner to a bounded fillet instead of letting it shoot off the junction.
        static Vector2 ClampFilletControl(Vector2 from, Vector2 ctrl, Vector2 to)
        {
            Vector2 mid = (from + to) * 0.5f;
            float maxReach = Mathf.Max((to - from).magnitude * 1.5f, 4f);
            Vector2 d = ctrl - mid; float r = d.magnitude;
            return (r > maxReach && r > 1e-4f) ? mid + d * (maxReach / r) : ctrl;
        }

        static readonly Color AsphaltColor = new Color(0.18f, 0.18f, 0.20f);

        // Build the junction pad: a flat asphalt fan over the outline ring at gradeY, extruded `depth` down
        // (rim wall + bottom fan) so it fills the excavated cut. SINGLE-sided with consistent winding (top up,
        // bottom down, rim out) so RecalculateNormals lights it correctly — double-siding cancels the normals
        // and renders the pad black. The ring is first oriented CW in (x,z) so the fixed windings face right.
        static void BuildIntersectionPad(List<Vector2> ring, Vector2 center, float gradeY, float depth, Transform parent, string name)
        {
            // DIAGNOSTIC (throttled once per junction): dump the raw outline so a protruding/stray point can be located.
            if (_xfillWarned.Add(name))
                Debug.Log($"[Road] pad '{name}' center=({center.x:0.0},{center.y:0.0}) ring({ring.Count})={RingToStr(ring)}");

            // Defense-in-depth against the triangular-spike artifact: first drop NaN/∞ points, then drop OUTLIERS —
            // points far beyond the junction's TYPICAL ring radius. A single bad outline point (e.g. a near-parallel
            // fillet intersection) would otherwise fan into a thin spike off the road edge. Median-based so it adapts
            // to the junction size (catches a spike at any scale without clipping legit wide junctions).
            for (int i = ring.Count - 1; i >= 0; i--)
            {
                Vector2 q = ring[i];
                if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsInfinity(q.x) || float.IsInfinity(q.y)) ring.RemoveAt(i);
            }
            if (ring.Count >= 4)
            {
                var rad = new List<float>(ring.Count);
                for (int i = 0; i < ring.Count; i++) rad.Add((ring[i] - center).magnitude);
                var sorted = new List<float>(rad); sorted.Sort();
                float median = sorted[sorted.Count / 2];
                float cap = Mathf.Max(median * 6f, 30f);   // outlier = >6× the typical ring radius (30 m floor for tiny junctions)
                for (int i = ring.Count - 1; i >= 0; i--) if (rad[i] > cap) ring.RemoveAt(i);
            }
            // Drop consecutive coincident points (ear clipping chokes on zero-area edges).
            for (int i = ring.Count - 1; i >= 0; i--)
            {
                int j = (i + 1) % ring.Count;
                if (ring.Count > 3 && (ring[i] - ring[j]).sqrMagnitude < 1e-6f) ring.RemoveAt(i);
            }
            int n = ring.Count;
            if (n < 3) return;
            double sa = 0.0;   // signed area in (x,z); >0 = CCW → reverse so the ring is CW
            for (int i = 0; i < n; i++) { int j = (i + 1) % n; sa += (double)ring[i].x * ring[j].y - (double)ring[j].x * ring[i].y; }
            if (sa > 0.0) ring.Reverse();

            // A self-intersecting outline (typically a tight degree-2 90° bend whose concave-corner OE-fillet control
            // bulges back across the ring) makes EarClipCW bail to a centroid fan that renders TORN/jagged. Replace the
            // ring with its convex hull: always simple, so it triangulates cleanly. The only cost is a slight over-fill
            // at a concave corner, which is hidden under the abutting road bodies. Logged (throttled) so the underlying
            // bend outline can still be root-fixed in the resolver.
            if (RingSelfIntersects(ring))
            {
                Debug.LogWarning($"[Road] junction outline '{name}' self-intersects ({n} pts) — using convex-hull fill.");
                ring = ConvexHull(ring);
                n = ring.Count;
                if (n < 3) return;
                double sah = 0.0;   // hull comes out CCW from the monotone chain → re-orient CW like the rest of the code expects
                for (int i = 0; i < n; i++) { int j = (i + 1) % n; sah += (double)ring[i].x * ring[j].y - (double)ring[j].x * ring[i].y; }
                if (sah > 0.0) ring.Reverse();
            }

            // Centroid (always interior for a convex/near-convex ring) — fan fallback centre and the rim's inner anchor.
            Vector2 cen = Vector2.zero; for (int i = 0; i < n; i++) cen += ring[i]; cen *= 1f / n;

            float botY = gradeY - Mathf.Max(0f, depth);
            var verts = new List<Vector3>(2 * n + 2);
            for (int i = 0; i < n; i++) verts.Add(new Vector3(ring[i].x, gradeY, ring[i].y));   // 0..n-1   top ring
            for (int i = 0; i < n; i++) verts.Add(new Vector3(ring[i].x, botY, ring[i].y));      // n..2n-1  bottom ring
            int ct = verts.Count; verts.Add(new Vector3(cen.x, gradeY, cen.y));                  // 2n       top centre
            int cb = verts.Count; verts.Add(new Vector3(cen.x, botY, cen.y));                    // 2n+1     bottom centre

            // Top face: ear-clip the ring so a non-star-shaped (asymmetric/acute) outline triangulates without the
            // spikes a vertex/centroid FAN throws when the centre isn't visible to every edge. Fall back to a centroid
            // fan only if ear-clipping can't complete (self-intersecting ring — shouldn't happen after the clamp).
            List<int> topFace = EarClipCW(ring);
            var tris = new List<int>(n * 12);
            if (topFace.Count >= (n - 2) * 3)
            {
                for (int t = 0; t < topFace.Count; t += 3)
                {
                    int a = topFace[t], b = topFace[t + 1], c = topFace[t + 2];
                    tris.Add(a); tris.Add(b); tris.Add(c);                       // top (faces up)
                    tris.Add(n + c); tris.Add(n + b); tris.Add(n + a);          // bottom (faces down)
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    tris.Add(ct); tris.Add(i); tris.Add(j);                      // top fan (faces up)
                    tris.Add(cb); tris.Add(n + j); tris.Add(n + i);             // bottom fan (faces down)
                }
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                tris.Add(i); tris.Add(n + i); tris.Add(n + j);                   // rim wall (faces out)
                tris.Add(i); tris.Add(n + j); tris.Add(j);
            }

            var mesh = new Mesh { name = "RoadXFill" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = PipelineMaterials.CreateLitMatte(AsphaltColor, "RoadXFill");
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            WarnIfHugeMesh(go, name);
        }

        // Junction outlines we've already warned about (per-vertex GameObject name), so the per-rebuild log doesn't spam.
        static readonly HashSet<string> _xfillWarned = new HashSet<string>();

        static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        // True if any two NON-adjacent edges of the closed ring properly cross (strict — touching endpoints don't count).
        static bool RingSelfIntersects(List<Vector2> r)
        {
            int n = r.Count;
            if (n < 4) return false;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = r[i], b = r[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;   // edges 0 and n-1 share vertex 0 — adjacent, skip
                    Vector2 c = r[j], d = r[(j + 1) % n];
                    float d1 = Cross2(d - c, a - c), d2 = Cross2(d - c, b - c);
                    float d3 = Cross2(b - a, c - a), d4 = Cross2(b - a, d - a);
                    if (((d1 > 0f) != (d2 > 0f)) && ((d3 > 0f) != (d4 > 0f)) &&
                        d1 != 0f && d2 != 0f && d3 != 0f && d4 != 0f)
                        return true;
                }
            }
            return false;
        }

        // Andrew's monotone-chain convex hull (XZ). Returns the hull CCW; caller re-orients as needed.
        static List<Vector2> ConvexHull(List<Vector2> pts)
        {
            var p = new List<Vector2>(pts);
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            int n = p.Count;
            if (n < 3) return p;
            var h = new Vector2[2 * n];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Cross2(h[k - 1] - h[k - 2], p[i] - h[k - 2]) <= 0f) k--;
                h[k++] = p[i];
            }
            int lower = k + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (k >= lower && Cross2(h[k - 1] - h[k - 2], p[i] - h[k - 2]) <= 0f) k--;
                h[k++] = p[i];
            }
            var res = new List<Vector2>(k - 1);
            for (int i = 0; i < k - 1; i++) res.Add(h[i]);
            return res;
        }

        static string RingToStr(List<Vector2> r)
        {
            var sb = new System.Text.StringBuilder();
            int lim = Mathf.Min(r.Count, 64);
            for (int i = 0; i < lim; i++) sb.Append($"({r[i].x:0.0},{r[i].y:0.0}) ");
            if (r.Count > lim) sb.Append("...");
            return sb.ToString();
        }

        // Ear-clipping triangulation of a CW-wound simple polygon (XZ). Returns CW triangle index triples (into the
        // input list) — same winding as the old centre fan, so they face up after RecalculateNormals. Returns an
        // incomplete list if the polygon self-intersects (caller falls back to a fan).
        static List<int> EarClipCW(List<Vector2> poly)
        {
            var tris = new List<int>();
            int n = poly.Count;
            if (n < 3) return tris;
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);
            int guard = 0;
            while (idx.Count > 3 && guard++ < 20000)
            {
                bool clipped = false;
                int m = idx.Count;
                for (int i = 0; i < m; i++)
                {
                    int i0 = idx[(i - 1 + m) % m], i1 = idx[i], i2 = idx[(i + 1) % m];
                    Vector2 a = poly[i0], b = poly[i1], c = poly[i2];
                    // CW convex vertex: cross < 0. Reflex / collinear → not an ear.
                    float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (cross >= -1e-7f) continue;
                    bool contains = false;
                    for (int k = 0; k < m; k++)
                    {
                        int p = idx[k];
                        if (p == i0 || p == i1 || p == i2) continue;
                        if (PointInTri(poly[p], a, b, c)) { contains = true; break; }
                    }
                    if (contains) continue;
                    tris.Add(i0); tris.Add(i1); tris.Add(i2);
                    idx.RemoveAt(i); clipped = true; break;
                }
                if (!clipped) break;   // no ear found → degenerate/self-intersecting; bail to fan
            }
            if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
            return tris;
        }

        static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        // ---- edge stack carried through the junction (shoulder/sidewalk + curb + parapet) ----
        //
        // Segments already sweep their full edge stack up to the setback line. This overlays a RAISED edge
        // band around each junction CORNER whose two flanking approaches share an edge profile, so the
        // curb/sidewalk/shoulder wraps continuously through. Disparate approaches get no band — their edge
        // stops at the setback (the flat asphalt pad fills past it). Mirrors the designer's 2D corner strips
        // (Rendering/IntersectionRenderer.cs:62-258) but in 3D with real heights + per-surface materials
        // matching the swept body (RoadSweep.SurfaceMat). Purely additive on top of BuildIntersectionPad's
        // flat pad — no regression on disparate or edgeless junctions.
        const int BandRungs = 8;      // samples along each corner fillet
        const float EdgeBandLift = 0.02f;   // lift above the asphalt pad so a FLAT verge (sidewalk/shoulder, no curb) doesn't z-fight

        static void BuildJunctionEdgeBands(VertexGeometry vg, Dictionary<string, RoadProfile> profById,
                                           float gradeY, Transform parent, string name)
        {
            int n = vg.Approaches != null ? vg.Approaches.Count : 0;
            if (n < 2 || vg.Outline == null) return;

            // Lane-area corners per approach (outline inset inward by the full edge-stack width) — the inner
            // boundary the band's curb face meets. Same construction as IntersectionRenderer (lines 84-86).
            var shrunkStart = new Vector2[n];
            var shrunkEnd = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                VertexApproach a = vg.Approaches[i];
                Vector2 dir = a.OuterRight - a.OuterLeft;
                dir = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector2.right;
                shrunkStart[i] = a.OuterLeft + dir * a.EdgeStackWidthCCW;
                shrunkEnd[i] = a.OuterRight - dir * a.EdgeStackWidthCW;
            }

            var verts = new List<Vector3>();
            var triBySurf = new Dictionary<RoadSurface, List<int>>();
            var order = new List<RoadSurface>();
            List<int> Tris(RoadSurface s)
            {
                if (!triBySurf.TryGetValue(s, out var t)) { t = new List<int>(); triBySurf[s] = t; order.Add(s); }
                return t;
            }

            for (int i = 0; i < n; i++)
            {
                VertexApproach a = vg.Approaches[i];
                VertexApproach b = vg.Approaches[(i + 1) % n];
                if (!profById.TryGetValue(a.RoadId, out RoadProfile pa) || pa == null) continue;
                if (!profById.TryGetValue(b.RoadId, out RoadProfile pb) || pb == null) continue;
                if (EdgeSignature(pa) != EdgeSignature(pb)) continue;   // disparate corner → edge stops at setback

                int transIdx = 2 * i + 1;
                if (transIdx >= vg.Outline.Count) continue;
                OutlineSegment trans = vg.Outline[transIdx];
                if (trans == null) continue;

                if (!EdgeSlice(pa, out List<Vector2> slice, out List<RoadSurface> sliceSegs, out float edgeW)) continue;

                // Matched outer (footprint) + inner (lane) curves for this corner. The inner curve is the outer
                // offset inward by the edge-stack width — mirror IntersectionRenderer's three cases (119-163):
                // QuadraticBezier (normal fillet), CubicBezier (S-curve taper joint), Line (collinear joint).
                Vector2 innerFrom = shrunkEnd[i], innerTo = shrunkStart[(i + 1) % n];
                Vector2 outerCtrl = Vector2.zero, innerMiter = Vector2.zero;   // quadratic / line
                Vector2 outerCtrl2 = Vector2.zero, innerCtrl2 = Vector2.zero;  // cubic only
                if (trans.Kind == SegmentKind.QuadraticBezier)
                {
                    // Clamp BOTH controls (matches SampleOutlineRing's outer clamp): at near-parallel/acute
                    // corners the OE intersection runs to infinity → a giant stretched sidewalk triangle that
                    // HDR bloom washes the scene white. ClampFilletControl bounds the reach to a compact fillet.
                    outerCtrl = ClampFilletControl(trans.From, trans.Control, trans.To);
                    Vector2 e1 = SafeNorm(trans.Control - a.OuterRight);
                    Vector2 e2 = SafeNorm(b.OuterLeft - trans.Control);
                    Vector2 p1 = a.OuterRight + PerpRight2(e1) * a.EdgeStackWidthCW;
                    Vector2 p2 = b.OuterLeft + PerpRight2(e2) * b.EdgeStackWidthCCW;
                    Vector2 mit = LineIntersect2(p1, e1, p2, e2) ?? ((innerFrom + innerTo) * 0.5f);
                    innerMiter = ClampFilletControl(innerFrom, mit, innerTo);
                }
                else if (trans.Kind == SegmentKind.CubicBezier)
                {
                    // Inner cubic = outer cubic shifted inward by the same vector that insets each end to its
                    // shrunk lane corner, so the band parallels the outer S-curve (IntersectionRenderer 131-145).
                    outerCtrl = trans.Control; outerCtrl2 = trans.Control2;
                    innerMiter = trans.Control + (innerFrom - trans.From);
                    innerCtrl2 = trans.Control2 + (innerTo - trans.To);
                }
                else // Line (collinear joint): straight edge continues through
                {
                    outerCtrl = (trans.From + trans.To) * 0.5f;
                    Vector2 joinDir = SafeNorm(trans.To - trans.From);
                    Vector2 il = a.OuterRight + PerpRight2(joinDir) * a.EdgeStackWidthCW;
                    Vector2 ir = b.OuterLeft + PerpRight2(joinDir) * b.EdgeStackWidthCCW;
                    innerMiter = (il + ir) * 0.5f;
                }

                // Sample the matched outer/inner curves, VALIDATING first: a runaway control (unbounded
                // miter at an acute/near-parallel corner, or a wild cubic) flings a sample point to infinity →
                // one giant white triangle that HDR-blooms the scene. Bound every sample to a sane radius of the
                // corner chord; if anything escapes, skip this corner entirely (and log it) rather than emit a spike.
                int sliceN = slice.Count;
                var outPs = new Vector2[BandRungs + 1];
                var inPs = new Vector2[BandRungs + 1];
                Vector2 refC = (trans.From + trans.To) * 0.5f;
                float cap = Mathf.Max((trans.To - trans.From).magnitude * 3f, 20f);
                bool bad = false;
                for (int r = 0; r <= BandRungs && !bad; r++)
                {
                    float t = (float)r / BandRungs;
                    Vector2 outP, inP;
                    if (trans.Kind == SegmentKind.CubicBezier)
                    {
                        outP = GeometryResolver.SampleCubic(trans.From, outerCtrl, outerCtrl2, trans.To, t);
                        inP = GeometryResolver.SampleCubic(innerFrom, innerMiter, innerCtrl2, innerTo, t);
                    }
                    else
                    {
                        outP = GeometryResolver.SampleQuadratic(trans.From, outerCtrl, trans.To, t);
                        inP = GeometryResolver.SampleQuadratic(innerFrom, innerMiter, innerTo, t);
                    }
                    if (!Finite(outP) || !Finite(inP) || (outP - refC).magnitude > cap || (inP - refC).magnitude > cap)
                        bad = true;
                    outPs[r] = outP; inPs[r] = inP;
                }
                if (bad)
                {
                    Debug.LogWarning($"[RoadXEdge] skipped runaway edge band at vertex {vg.VertexId}, corner {i} (kind={trans.Kind}, cap={cap:F1}m).");
                    continue;
                }

                // Loft the edge slice radially between the matched outer (footprint) and inner (lane) curves.
                int baseStart = verts.Count;
                for (int r = 0; r <= BandRungs; r++)
                    for (int k = 0; k < sliceN; k++)
                    {
                        float u = edgeW > 1e-5f ? slice[k].x / edgeW : 0f;   // 0 = outer foot, 1 = lane edge
                        Vector2 xz = Vector2.Lerp(outPs[r], inPs[r], u);
                        verts.Add(new Vector3(xz.x, gradeY + slice[k].y + EdgeBandLift, xz.y));
                    }
                int VID(int r, int k) => baseStart + r * sliceN + k;
                for (int r = 0; r < BandRungs; r++)
                    for (int k = 0; k < sliceSegs.Count; k++)
                    {
                        List<int> t = Tris(sliceSegs[k]);
                        bool vertical = Mathf.Abs(slice[k].x - slice[k + 1].x) < 1e-4f;   // curb / outer face
                        int v0 = VID(r, k), v1 = VID(r + 1, k), v2 = VID(r + 1, k + 1), v3 = VID(r, k + 1);
                        t.Add(v0); t.Add(v1); t.Add(v2); t.Add(v0); t.Add(v2); t.Add(v3);
                        if (vertical) { t.Add(v0); t.Add(v2); t.Add(v1); t.Add(v0); t.Add(v3); t.Add(v2); }
                    }
            }

            if (verts.Count == 0) return;

            var mesh = new Mesh { name = "RoadXEdge" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.subMeshCount = order.Count;
            for (int s = 0; s < order.Count; s++) mesh.SetTriangles(triBySurf[order[s]], s);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mats = new Material[order.Count];
            for (int s = 0; s < order.Count; s++) mats[s] = RoadSweep.SurfaceMat(order[s]);
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        // Edge-stack slice from a profile: the transverse polyline from the OUTER footprint edge inward to
        // the first lane — (lateralFromOuter, height) points + per-segment surfaces. Reuses the exact section
        // the segment body sweeps (RoadCrossSectionBuilder.FromProfile) so the band matches it at the seam.
        // False when there's no edge stack (first band is already a lane).
        static bool EdgeSlice(RoadProfile p, out List<Vector2> pts, out List<RoadSurface> segs, out float width)
        {
            pts = null; segs = null; width = 0f;
            RoadCrossSection xs = RoadCrossSectionBuilder.FromProfile(p);
            int firstLane = -1;
            for (int s = 0; s < xs.Segs.Count; s++) if (xs.Segs[s] == RoadSurface.Asphalt) { firstLane = s; break; }
            if (firstLane <= 0) return false;
            pts = new List<Vector2>(firstLane + 1);
            for (int i = 0; i <= firstLane; i++) pts.Add(xs.Pts[i]);
            segs = new List<RoadSurface>(firstLane);
            for (int i = 0; i < firstLane; i++) segs.Add(xs.Segs[i]);
            width = pts[pts.Count - 1].x;
            return width > 0.01f;
        }

        // Two approaches' edges "match" (continue through their shared corner) when these edge-relevant fields agree.
        static string EdgeSignature(RoadProfile p)
            => $"{p.Curbs}|{p.Sidewalks}|{p.Elevated}|{p.Guardrails}|{(p.ShoulderAB != null ? p.ShoulderAB.Width : 0f):F2}|{(p.ShoulderBA != null ? p.ShoulderBA.Width : 0f):F2}";

        // Diagnostic: a normal road sub-mesh spans at most a few hundred metres. A runaway/degenerate vertex
        // (NaN, or a stray default like the world origin while the road sits at thousands-coords) blows the
        // bounds up into a giant white-blooming wedge. Log the offending object name + bounds so we can pin it.
        static void WarnIfHugeMesh(GameObject go, string what)
        {
            if (go == null) return;
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            Bounds b = mf.sharedMesh.bounds;
            Vector3 s = b.size;
            bool nan = float.IsNaN(s.x) || float.IsNaN(s.y) || float.IsNaN(s.z) || float.IsInfinity(s.x) || float.IsInfinity(s.y) || float.IsInfinity(s.z);
            if (nan || s.magnitude > 2000f)
                Debug.LogWarning($"[Road] SUSPICIOUS mesh '{what}': bounds size={s}, center={b.center} (runaway vertex → white-bloom wedge).");
        }

        static bool Finite(Vector2 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.x) || float.IsInfinity(v.y));
        static Vector2 PerpRight2(Vector2 v) => new Vector2(v.y, -v.x);
        static Vector2 SafeNorm(Vector2 v) => v.sqrMagnitude > 1e-8f ? v.normalized : Vector2.right;
        static Vector2? LineIntersect2(Vector2 p1, Vector2 d1, Vector2 p2, Vector2 d2)
        {
            float det = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(det) < 1e-6f) return null;
            Vector2 diff = p2 - p1;
            float tt = (diff.x * d2.y - diff.y * d2.x) / det;
            return p1 + tt * d1;
        }

        // ---- lane markings on the built asphalt ----

        static readonly Color MarkWhite = new Color(0.93f, 0.93f, 0.93f);
        static readonly Color MarkYellow = new Color(0.96f, 0.80f, 0.18f);
        const float MarkWidth = 0.15f, MarkLift = 0.05f, MarkDash = 3f, MarkGap = 2.5f, DblYellowSep = 0.18f;

        // Paint lane markings on the swept road surface from the profile's RoadLayout: dashed white divider
        // between same-direction lanes, double-yellow centre between opposing lanes, solid white edge/median
        // lines. Thin double-sided UNLIT quads lifted just above the asphalt, following the same trimmed curve
        // + design grade as the road body so they sit flush. (Lane-flow arrows come with phase 3 proper.)
        static void BuildRoadMarkings(RoadProfile prof, Vector2 a, Vector2 ca, Vector2 cb, Vector2 b, bool curve,
                                      float hA, float hB, Transform parent, string name,
                                      Func<Vector2, float> groundAt = null, float follow = 0f)
        {
            if (prof == null) return;
            List<(float w, int k)> lay = RoadLayout.Of(prof);
            if (lay.Count < 2) return;
            float W = 0f; foreach (var s in lay) W += s.w;
            float half = W * 0.5f;
            int Med = RoadLayout.Median, Trn = RoadLayout.TurnLane;

            // Markings as (lateral offset from path centre, yellow?, dashed?).
            var marks = new List<(float u, bool yellow, bool dashed)>();
            float acc = 0f;
            for (int i = 0; i < lay.Count - 1; i++)
            {
                acc += lay[i].w;
                int L = lay[i].k, R = lay[i + 1].k;
                bool lnL = RoadLayout.IsLane(L), lnR = RoadLayout.IsLane(R);
                float off = acc - half;
                if (lnL && lnR)
                {
                    if (L == R) marks.Add((off, false, true));                                  // same dir → dashed white
                    else { marks.Add((off - DblYellowSep, true, false)); marks.Add((off + DblYellowSep, true, false)); }  // opposing → double yellow
                }
                else if (L == Trn || R == Trn)                                                 // TWLTL turn-lane edge:
                {
                    marks.Add((off, true, false));                                              //   solid yellow outer (lane edge)
                    marks.Add((R == Trn ? off + DblYellowSep : off - DblYellowSep, true, true)); //   dashed yellow inner (turn-lane side)
                }
                else if (L == Med || R == Med) marks.Add((off, false, false));                  // median edge (white)
                else if (lnL || lnR) marks.Add((off, false, false));                            // lane ↔ shoulder/curb → pavement edge
            }
            if (marks.Count == 0) return;

            // Subdivide by LENGTH (straight too) at ~0.5 m so dashed markings have segments to alternate over —
            // a 2-frame straight would draw each divider as one solid quad.
            float mlen = curve ? GeometryResolver.CubicArcLength(a, ca, cb, b) : Vector2.Distance(a, b);
            int frames = Mathf.Clamp(Mathf.CeilToInt(mlen / 0.5f) + 1, 2, 2048);
            var fp = new Vector3[frames]; var fr = new Vector3[frames];
            // Markings ride the SAME profile as the road body (straight grade or terrain-follow) so they sit flush.
            var markY = RoadSweep.ElevationProfile(a, b, curve, ca, cb, frames, hA, hB, groundAt, follow);
            for (int f = 0; f < frames; f++)
            {
                float t = f / (float)(frames - 1);
                Vector2 p, tan;
                if (curve) { p = GeometryResolver.SampleCubic(a, ca, cb, b, t); tan = GeometryResolver.CubicTangent(a, ca, cb, b, t); }
                else { p = Vector2.Lerp(a, b, t); tan = b - a; }
                Vector3 fwd = new Vector3(tan.x, 0f, tan.y); fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;
                fp[f] = new Vector3(p.x, markY[f] + MarkLift, p.y);
                fr[f] = Vector3.Cross(Vector3.up, fwd).normalized;
            }

            var verts = new List<Vector3>();
            var triW = new List<int>(); var triY = new List<int>();
            float hw = MarkWidth * 0.5f, period = MarkDash + MarkGap;
            foreach (var m in marks)
            {
                List<int> tl = m.yellow ? triY : triW;
                float walked = 0f;
                for (int f = 0; f < frames - 1; f++)
                {
                    float segLen = (fp[f + 1] - fp[f]).magnitude;
                    bool on = !m.dashed || (walked % period) < MarkDash;
                    walked += segLen;
                    if (!on) continue;
                    Vector3 l0 = fp[f] + fr[f] * (m.u - hw), r0 = fp[f] + fr[f] * (m.u + hw);
                    Vector3 l1 = fp[f + 1] + fr[f + 1] * (m.u - hw), r1 = fp[f + 1] + fr[f + 1] * (m.u + hw);
                    int s = verts.Count;
                    verts.Add(l0); verts.Add(r0); verts.Add(r1); verts.Add(l1);
                    tl.Add(s); tl.Add(s + 1); tl.Add(s + 2); tl.Add(s); tl.Add(s + 2); tl.Add(s + 3);   // up
                    tl.Add(s); tl.Add(s + 2); tl.Add(s + 1); tl.Add(s); tl.Add(s + 3); tl.Add(s + 2);   // down (unlit, 2-sided)
                }
            }
            if (verts.Count == 0) return;

            var mesh = new Mesh { name = "RoadMarks" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(triW, 0);
            mesh.SetTriangles(triY, 1);
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                PipelineMaterials.CreateUnlitColor(MarkWhite, "RoadMarkWhite"),
                PipelineMaterials.CreateUnlitColor(MarkYellow, "RoadMarkYellow"),
            };
            WarnIfHugeMesh(go, name);
        }
    }
}
