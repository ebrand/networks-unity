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
        // `onlyRoads` (road ids "r{e}") restricts which road bodies get swept — the WHOLE network still resolves
        // (so junction setbacks stay correct as segments are built one at a time), but only the listed roads emit
        // geometry. Pass null to sweep every road (the whole-plan build).
        public static GameObject Build(Network net, Func<string, float> vertexElev, float excavationDepth, Transform parent, HashSet<string> onlyRoads = null)
        {
            var root = new GameObject("RoadPlanBuild");
            if (parent != null) root.transform.SetParent(parent, false);
            if (net == null || net.Roads == null || net.Roads.Count == 0) return root;

            List<VertexGeometry> resolved = GeometryResolver.ResolveNetwork(net);
            var vgById = new Dictionary<string, VertexGeometry>();
            if (resolved != null) foreach (VertexGeometry vg in resolved) if (vg != null) vgById[vg.VertexId] = vg;
            var vById = new Dictionary<string, Vertex>();
            foreach (Vertex v in net.Vertices) vById[v.Id] = v;

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

                RoadSweep.Build(xs, a, b, curve, ca, cb, root.transform, road.Id, hA, hB);
                BuildRoadMarkings(road.Profile, a, ca, cb, b, curve, hA, hB, root.transform, road.Id + "_marks");
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
                    if (!vById.TryGetValue(vg.VertexId, out Vertex v)) continue;
                    List<Vector2> ring = SampleOutlineRing(vg.Outline);
                    if (ring.Count < 3) continue;
                    float gy = vertexElev != null ? vertexElev(vg.VertexId) : 0f;
                    BuildIntersectionPad(ring, v.Position, gy, depth, root.transform, "X_" + vg.VertexId);
                    pads++;
                }

            Debug.Log($"[Road] Build Plan: swept {built}/{net.Roads.Count} road bodies + {pads} intersection pads"
                      + (skipped > 0 ? $", {skipped} skipped (no profile / too short / fully set back)." : "."));
            return root;
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
                        for (int i = 0; i < 8; i++) pts.Add(GeometryResolver.SampleQuadratic(s.From, s.Control, s.To, i / 8f));
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

        static readonly Color AsphaltColor = new Color(0.18f, 0.18f, 0.20f);

        // Build the junction pad: a flat asphalt fan over the outline ring at gradeY, extruded `depth` down
        // (rim wall + bottom fan) so it fills the excavated cut. SINGLE-sided with consistent winding (top up,
        // bottom down, rim out) so RecalculateNormals lights it correctly — double-siding cancels the normals
        // and renders the pad black. The ring is first oriented CW in (x,z) so the fixed windings face right.
        static void BuildIntersectionPad(List<Vector2> ring, Vector2 center, float gradeY, float depth, Transform parent, string name)
        {
            int n = ring.Count;
            double sa = 0.0;   // signed area in (x,z); >0 = CCW → reverse so the ring is CW
            for (int i = 0; i < n; i++) { int j = (i + 1) % n; sa += (double)ring[i].x * ring[j].y - (double)ring[j].x * ring[i].y; }
            if (sa > 0.0) ring.Reverse();

            float botY = gradeY - Mathf.Max(0f, depth);
            var verts = new List<Vector3>(2 * n + 2);
            for (int i = 0; i < n; i++) verts.Add(new Vector3(ring[i].x, gradeY, ring[i].y));   // 0..n-1   top ring
            for (int i = 0; i < n; i++) verts.Add(new Vector3(ring[i].x, botY, ring[i].y));      // n..2n-1  bottom ring
            int ct = verts.Count; verts.Add(new Vector3(center.x, gradeY, center.y));            // 2n       top centre
            int cb = verts.Count; verts.Add(new Vector3(center.x, botY, center.y));              // 2n+1     bottom centre

            var tris = new List<int>(n * 12);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                tris.Add(ct); tris.Add(i); tris.Add(j);              // top fan (faces up)
                tris.Add(cb); tris.Add(n + j); tris.Add(n + i);      // bottom fan (faces down)
                tris.Add(i); tris.Add(n + i); tris.Add(n + j);       // rim wall (faces out)
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
                                      float hA, float hB, Transform parent, string name)
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
            for (int f = 0; f < frames; f++)
            {
                float t = f / (float)(frames - 1);
                Vector2 p, tan;
                if (curve) { p = GeometryResolver.SampleCubic(a, ca, cb, b, t); tan = GeometryResolver.CubicTangent(a, ca, cb, b, t); }
                else { p = Vector2.Lerp(a, b, t); tan = b - a; }
                Vector3 fwd = new Vector3(tan.x, 0f, tan.y); fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;
                fp[f] = new Vector3(p.x, Mathf.Lerp(hA, hB, t) + MarkLift, p.y);
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
        }
    }
}
