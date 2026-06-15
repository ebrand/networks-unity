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

        // Returns a GameObject holding the deck+pier mesh (parented under `parent`), or null if nothing was built.
        public static GameObject Build(RoadPlanLayer rd, HashSet<int> bridgeEdges, System.Func<int, float> nodeElev,
                                       ITerrainSurface field, float deckDepth, float pierSpacing, float pierWidth,
                                       Transform parent)
        {
            if (rd == null || rd.Graph == null || bridgeEdges == null || bridgeEdges.Count == 0) return null;
            deckDepth = Mathf.Max(0.1f, deckDepth);
            pierSpacing = Mathf.Max(2f, pierSpacing);
            pierWidth = Mathf.Max(0.1f, pierWidth);

            var verts = new List<Vector3>();
            var tris = new List<int>();

            foreach (int ei in bridgeEdges)
            {
                if (ei < 0 || ei >= rd.Graph.Edges.Count) continue;
                LineEdge e = rd.Graph.Edges[ei];
                rd.EdgeBezierWorld(ei, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
                float yA = nodeElev != null ? nodeElev(e.A) : 0f;
                float yB = nodeElev != null ? nodeElev(e.B) : 0f;   // leveled equal for a true bridge, but lerp anyway
                float width = Mathf.Max(2f, rd.EdgeCorridorWidth(ei)) + 1.5f;   // deck lip past the carriageway edges
                const float sink = 0.1f;                                        // sit the deck just under the road surface (no z-fight)

                float chord = Vector2.Distance(p0, p3);
                int n = Mathf.Clamp(Mathf.CeilToInt(chord / 4f), 2, 1024);   // ~4 m deck sub-spans

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
                    }

                    // Pier: from the deck soffit down to the terrain, at start, end, and every pierSpacing along.
                    float soffit = deckTop - deckDepth;
                    float ground = field != null ? field.SampleHeight(xz.x, xz.y) : 0f;
                    bool endpoint = (i == 0 || i == n);
                    if ((endpoint || arc >= nextPier) && soffit - ground > 0.3f)
                    {
                        float h = soffit - ground;
                        AddBox(verts, tris, new Vector3(xz.x, (soffit + ground) * 0.5f, xz.y),
                               Vector3.forward, Vector3.right, Vector3.up, pierWidth, pierWidth, h);
                        if (!endpoint) nextPier = arc + pierSpacing;
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
