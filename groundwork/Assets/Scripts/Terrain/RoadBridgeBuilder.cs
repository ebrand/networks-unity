// Builds the 3D BRIDGE / TRESTLE structure (deck slab + piers) under road-plan segments flagged as a bridge.
// Mirrors the rail trestle generator (RailTrackLayer): sweep the segment centreline, lay a deck box per sub-span,
// and drop a pier from the deck soffit to the terrain at a fixed spacing. Unlike rail (auto by fill depth), road
// bridges are EXPLICIT per-segment — a flagged segment always builds a bridge, even over modest ground, and its
// ends are forced level (see TerrainDesigner.MarkRoadBridgePath) so the deck is flat. Built into its own child of
// the road-build root, so it's torn down with ClearRoadBuild. Concrete matte material (PipelineMaterials).

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class RoadBridgeBuilder
    {
        static Material _mat;

        // One trestle/pier position along a bridge edge (so the arch tool can pick + highlight trestles, and Increment
        // 2 can truncate them to an arch). MUST match Build's pier placement so picks line up with the real piers.
        public struct TrestleStation { public Vector2 Xz; public float DeckTop; public float Ground; public float Arc; }

        // Enumerate the pier stations of bridge edge `ei` — same logic as Build's pier loop (start, end, every
        // pierSpacing along the arc; only where the soffit clears the ground by > 0.3 m).
        public static List<TrestleStation> Stations(RoadPlanLayer rd, int ei, System.Func<int, float> nodeElev,
                                                    ITerrainSurface field, float deckDepth, float pierSpacing)
        {
            var outS = new List<TrestleStation>();
            if (rd == null || rd.Graph == null || ei < 0 || ei >= rd.Graph.Edges.Count) return outS;
            LineEdge e = rd.Graph.Edges[ei];
            rd.EdgeBezierWorld(ei, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
            float yA = nodeElev != null ? nodeElev(e.A) : 0f, yB = nodeElev != null ? nodeElev(e.B) : 0f;
            deckDepth = Mathf.Max(0.1f, deckDepth); pierSpacing = Mathf.Max(2f, pierSpacing);
            const float sink = 0.1f;
            float chord = Vector2.Distance(p0, p3);
            int n = Mathf.Clamp(Mathf.CeilToInt(chord / 4f), 2, 1024);
            float arc = 0f, nextPier = 0f; Vector2 prevXz = p0; bool hasPrev = false;
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n;
                Vector2 xz = LineGraph.Bezier(p0, p1, p2, p3, u);
                float deckTop = Mathf.Lerp(yA, yB, u) - sink;
                if (hasPrev) arc += Vector2.Distance(prevXz, xz);
                float soffit = deckTop - deckDepth;
                float ground = field != null ? field.SampleHeight(xz.x, xz.y) : 0f;
                bool endpoint = (i == 0 || i == n);
                if ((endpoint || arc >= nextPier) && soffit - ground > 0.3f)
                {
                    outS.Add(new TrestleStation { Xz = xz, DeckTop = deckTop, Ground = ground, Arc = arc });
                    if (!endpoint) nextPier = arc + pierSpacing;
                }
                prevXz = xz; hasPrev = true;
            }
            return outS;
        }

        // Returns a GameObject holding the deck+pier mesh (parented under `parent`), or null if nothing was built.
        public static GameObject Build(RoadPlanLayer rd, HashSet<int> bridgeEdges, System.Func<int, float> nodeElev,
                                       ITerrainSurface field, float deckDepth, float pierSpacing, float pierWidth,
                                       bool parapets, float parapetHeight, Transform parent)
        {
            if (rd == null || rd.Graph == null || bridgeEdges == null || bridgeEdges.Count == 0) return null;
            deckDepth = Mathf.Max(0.1f, deckDepth);
            pierSpacing = Mathf.Max(2f, pierSpacing);
            pierWidth = Mathf.Max(0.1f, pierWidth);
            parapetHeight = Mathf.Max(0.1f, parapetHeight);
            const float parapetThick = 0.35f;

            var verts = new List<Vector3>();
            var tris = new List<int>();

            foreach (int ei in bridgeEdges)
            {
                if (ei < 0 || ei >= rd.Graph.Edges.Count) continue;
                LineEdge e = rd.Graph.Edges[ei];
                // Suppress deck parapets when the corridor profile already carries its own parapet segments
                // (else they double up at the deck edges).
                bool edgeParapets = parapets && !CorridorHasParapet(rd, ei);
                rd.EdgeBezierWorld(ei, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
                float yA = nodeElev != null ? nodeElev(e.A) : 0f;
                float yB = nodeElev != null ? nodeElev(e.B) : 0f;   // leveled equal for a true bridge, but lerp anyway
                float baseW = Mathf.Max(2f, rd.EdgeCorridorWidth(ei));          // the carriageway (road base) width
                float width = baseW + 1.5f;                                     // deck lip past the carriageway edges
                float pierAcross = Mathf.Max(pierWidth, baseW * 0.75f);         // wall pier: 75% of the road base, ACROSS the road
                const float sink = 0.1f;                                        // sit the deck just under the road surface (no z-fight)

                float chord = Vector2.Distance(p0, p3);
                int n = Mathf.Clamp(Mathf.CeilToInt(chord / 4f), 2, 1024);   // ~4 m deck sub-spans

                // Under-deck arches recorded on this edge: precompute each arch's spring-foot heights (from the trestle
                // stations at its ends) so the in-between piers can be truncated to the arch and the band swept along it.
                List<ArchSpan> arches = null;
                if (e.Arches != null && e.Arches.Count > 0)
                {
                    var st = Stations(rd, ei, nodeElev, field, deckDepth, pierSpacing);
                    arches = new List<ArchSpan>();
                    foreach (BridgeArch ar in e.Arches)
                        arches.Add(new ArchSpan { StartArc = ar.StartArc, EndArc = ar.EndArc, Rise = ar.Rise,
                                                  FootStart = GroundAtArc(st, ar.StartArc), FootEnd = GroundAtArc(st, ar.EndArc) });
                }

                Vector3 prev = default; bool hasPrev = false;
                float arc = 0f, nextPier = 0f;
                for (int i = 0; i <= n; i++)
                {
                    float u = i / (float)n;
                    Vector2 xz = LineGraph.Bezier(p0, p1, p2, p3, u);
                    float deckTop = Mathf.Lerp(yA, yB, u) - sink;
                    Vector3 ctr = new Vector3(xz.x, deckTop, xz.y);

                    if (hasPrev)
                    {
                        AddBeam(verts, tris, prev, ctr, width, deckDepth);                // deck slab sub-span
                        arc += Vector2.Distance(new Vector2(prev.x, prev.z), xz);
                        if (edgeParapets)
                        {
                            Vector2 d2 = new Vector2(ctr.x - prev.x, ctr.z - prev.z);
                            Vector2 perp = d2.sqrMagnitude > 1e-8f ? new Vector2(-d2.y, d2.x).normalized : Vector2.right;
                            float lat = width * 0.5f - parapetThick * 0.5f;              // wall along each deck edge
                            AddParapetSeg(verts, tris, prev, ctr, perp,  lat, parapetThick, parapetHeight);
                            AddParapetSeg(verts, tris, prev, ctr, perp, -lat, parapetThick, parapetHeight);
                        }
                    }

                    // Pier: from the deck soffit down to the terrain — OR, if strictly inside an arch span, down to the
                    // ARCH (the in-between trestles sit on it). The springing trestles at the span ends stay full depth.
                    float soffit = deckTop - deckDepth;
                    float ground = field != null ? field.SampleHeight(xz.x, xz.y) : 0f;
                    float bottom = ground;
                    if (arches != null)
                        foreach (ArchSpan a in arches)
                            if (arc > a.StartArc + 0.01f && arc < a.EndArc - 0.01f) { bottom = ArchY(a, arc); break; }
                    bool endpoint = (i == 0 || i == n);
                    if ((endpoint || arc >= nextPier) && soffit - bottom > 0.3f)
                    {
                        float h = soffit - bottom;
                        // Wall pier oriented to the road: thin (pierWidth) along the road, wide (pierAcross) across it.
                        Vector2 tan = LineGraph.BezierTangent(p0, p1, p2, p3, u);
                        Vector3 fwd = tan.sqrMagnitude > 1e-8f ? new Vector3(tan.x, 0f, tan.y).normalized : Vector3.forward;
                        Vector3 rgt = Vector3.Cross(Vector3.up, fwd); rgt = rgt.sqrMagnitude > 1e-6f ? rgt.normalized : Vector3.right;
                        AddBox(verts, tris, new Vector3(xz.x, (soffit + bottom) * 0.5f, xz.y),
                               fwd, rgt, Vector3.up, pierWidth, pierAcross, h);
                        if (!endpoint) nextPier = arc + pierSpacing;
                    }
                    // Arch band: sweep a box section along the arch curve across each span.
                    if (arches != null)
                        foreach (ArchSpan a in arches)
                            if (arc >= a.StartArc - 0.01f && arc <= a.EndArc + 0.01f)
                            {
                                Vector3 ap = new Vector3(xz.x, ArchY(a, arc), xz.y);
                                if (a.HasPrev) AddArchSeg(verts, tris, a.Prev, ap, width * 0.85f, 1.6f);
                                a.Prev = ap; a.HasPrev = true;
                            }
                    prev = ctr; hasPrev = true;
                }
            }

            if (verts.Count == 0) return null;

            var go = new GameObject("RoadBridges") { hideFlags = HideFlags.DontSave };
            if (parent != null) go.transform.SetParent(parent, worldPositionStays: false);
            var mesh = new Mesh { name = "RoadBridgeMesh" };
            mesh.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            if (_mat == null) _mat = NetworkDesigner.PipelineMaterials.CreateLitMatte(new Color(0.62f, 0.62f, 0.64f), "RoadBridgeMat");
            go.AddComponent<MeshRenderer>().sharedMaterial = _mat;
            return go;
        }

        // Per-edge working copy of a recorded arch, with its resolved spring-foot heights + the previous swept point.
        class ArchSpan { public float StartArc, EndArc, Rise, FootStart, FootEnd; public Vector3 Prev; public bool HasPrev; }

        // Arch surface height at arc-distance `arc`: a parabola from FootStart to FootEnd, peaking Rise above that line.
        static float ArchY(ArchSpan a, float arc)
        {
            float t = a.EndArc > a.StartArc ? Mathf.Clamp01((arc - a.StartArc) / (a.EndArc - a.StartArc)) : 0f;
            return Mathf.Lerp(a.FootStart, a.FootEnd, t) + a.Rise * 4f * t * (1f - t);
        }

        // Terrain height at the trestle nearest arc-distance `arc` (the arch springs from a trestle's foot).
        // True if the edge's corridor profile carries any parapet segment (so the deck shouldn't add its own).
        static bool CorridorHasParapet(RoadPlanLayer rd, int ei)
        {
            if (rd?.Graph == null || ei < 0 || ei >= rd.Graph.Edges.Count) return false;
            var corridor = NetworkDesigner.Roads.RoadProfileLibrary.ResolveConfig(rd.Graph.Edges[ei].Profile)?.Corridor;
            if (corridor == null) return false;
            return AnyParapet(corridor.AB) || AnyParapet(corridor.BA) || AnyParapet(corridor.Center);
        }

        static bool AnyParapet(List<NetworkDesigner.Model.CorridorSegment> side)
        {
            if (side == null) return false;
            foreach (var s in side) if (s != null && s.Parapet) return true;
            return false;
        }

        static float GroundAtArc(List<TrestleStation> st, float arc)
        {
            float best = float.MaxValue, g = 0f;
            foreach (TrestleStation s in st) { float d = Mathf.Abs(s.Arc - arc); if (d < best) { best = d; g = s.Ground; } }
            return g;
        }

        // A box section between two points on the arch curve (oriented along the segment): the arch band.
        static void AddArchSeg(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, float width, float thick)
        {
            Vector3 along = b - a; float l = along.magnitude;
            if (l < 1e-4f) return;
            Vector3 fwd = along / l;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;
            AddBox(v, t, (a + b) * 0.5f, fwd, right, up, l, width, thick);
        }

        // A horizontal beam (box) between two centreline points, TOP at a/b's level, extending DOWN by `height`.
        static void AddBeam(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, float width, float height)
        {
            Vector3 along = b - a;
            float l = along.magnitude;
            if (l < 1e-4f) return;
            Vector3 fwd = along / l;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;
            AddBox(v, t, (a + b) * 0.5f - up * (height * 0.5f), fwd, right, up, l, width, height);
        }

        // A parapet (barrier wall) sub-span: a box sitting ON the deck top, offset laterally by `lat` from the
        // centreline, rising `height` above the deck. a/b are consecutive deck-top centreline points.
        static void AddParapetSeg(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector2 perp, float lat, float thick, float height)
        {
            Vector3 off = new Vector3(perp.x, 0f, perp.y) * lat;
            Vector3 a2 = a + off, b2 = b + off;
            Vector3 along = b2 - a2; float l = along.magnitude;
            if (l < 1e-4f) return;
            Vector3 fwd = along / l;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            Vector3 ctr = (a2 + b2) * 0.5f + Vector3.up * (height * 0.5f);   // bottom on the deck, extends up
            AddBox(v, t, ctr, fwd, right, Vector3.up, l, thick, height);
        }

        // Oriented box with un-shared per-face verts (flat low-poly normals). lenF/lenR/lenU are full sizes.
        static void AddBox(List<Vector3> v, List<int> t, Vector3 c,
                           Vector3 fwd, Vector3 right, Vector3 up, float lenF, float lenR, float lenU)
        {
            Vector3 F = fwd * (lenF * 0.5f);
            Vector3 R = right * (lenR * 0.5f);
            Vector3 U = up * (lenU * 0.5f);
            Quad(v, t, c + F - R - U, c + F + R - U, c + F + R + U, c + F - R + U); // +fwd
            Quad(v, t, c - F - U - R, c - F + U - R, c - F + U + R, c - F - U + R); // -fwd
            Quad(v, t, c + R - U - F, c + R + U - F, c + R + U + F, c + R - U + F); // +right
            Quad(v, t, c - R - F - U, c - R + F - U, c - R + F + U, c - R - F + U); // -right
            Quad(v, t, c + U - F - R, c + U + F - R, c + U + F + R, c + U - F + R); // +up
            Quad(v, t, c - U - R - F, c - U + R - F, c - U + R + F, c - U - R + F); // -up
        }

        static void Quad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }
    }
}
