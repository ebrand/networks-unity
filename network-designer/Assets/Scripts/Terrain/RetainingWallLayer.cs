// A drawable RETAINING WALL: a node/edge polyline (like a fence) that builds a solid 3m-thick concrete slab.
// Each endpoint is snapped to the ~1 m terrain mesh grid and carries its own top elevation (terrain there +
// WallRise), so the wall top is a STRAIGHT grade line between endpoints; the retained (back) side is graded
// to that line (ChunkWorld.GradeWallBack) so the seam stays tight. Winding order decides the exposed "front"
// face vs the filled "back"; FlipSide swaps them.
//
// Interaction (driven by TerrainDesigner): Cmd+wheel sets WallRise (how far the top sits above the natural
// grade); a translucent PREVIEW + dashed back-fill curtain shows the pending span live before you commit.
// Clicking places a grid-snapped node; the edge it forms is built (solid mesh) and its back-grade applied.
// Each node stores its top elevation in Graph.NodeY, so committed edges keep the height they were laid at.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class RetainingWallLayer : ITerrainLineLayer
    {
        public string Name = "RetainingWall";
        string ITerrainLineLayer.LayerName => Name;

        [Tooltip("Wall thickness (m). Real retaining walls are ~3m.")]
        public float WallThickness = 3f;
        [Tooltip("How far back the flat fill bench can reach (m) before giving up. The bench normally stops where the hillside rises to the wall top; this caps it so a wall with low/flat ground behind doesn't fill a huge plateau.")]
        public float BackFillCap = 120f;
        [Tooltip("How far the wall base sinks below the front ground (m), so it never floats.")]
        public float FootingDepth = 0.6f;
        [Tooltip("How far the wall top sits ABOVE the back-fill (m) — just enough to clear z-fighting with the terrain graded to the same level, without leaving a visible exposed strip.")]
        public float TopLip = 0.12f;
        [Tooltip("MAX distance back (m) the terrain CAP extends from the wall (a flat terrain-coloured strip over the platform that lays a crisp straight edge along the wall). Automatically clamped so it never runs past the platform onto the rising hillside.")]
        public float CapDepth = 12f;
        [Tooltip("Snap radius (m) to the end of an existing wall — click within this to continue it (adopting its elevation).")]
        public float SnapRadius = 6f;
        [Tooltip("Default wall height above the ground (m) used when no elevation has been dialled in yet.")]
        public float DefaultHeight = 6f;
        [Tooltip("Metres between samples along an edge (terrain-following wall base + grade).")]
        public float SampleStep = 2f;
        [Tooltip("Swap the exposed FRONT face to the other side of the drawn line.")]
        public bool FlipSide = false;
        [Tooltip("Snap each endpoint to the ~1 m terrain mesh grid so its back-top vertex lines up with a terrain vertex (tight seam).")]
        public bool GridSnap = true;

        // Each node's top elevation = terrain at its (grid-snapped) point + WallRise. The wall top between two
        // nodes is the STRAIGHT line of their elevations; the retained side is graded to that line. Cmd+wheel
        // sets WallRise — how far the wall top sits above the natural grade (0 = flush, >0 = retains fill).
        [NonSerialized] public float WallRise = 0f;

        // ---- runtime ----
        LineGraph _graph = new LineGraph();
        GameObject _root;
        MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; Material _mat;
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Vector3> _n = new List<Vector3>();
        readonly List<int> _t = new List<int>();
        // Sharp terrain "cap" along the wall back rail (terrain-coloured) — option 2 from the seam discussion.
        GameObject _capGo; MeshFilter _capMf; MeshRenderer _capMr; Mesh _capMesh; Material _capMat;
        readonly List<Vector3> _capV = new List<Vector3>();
        readonly List<Vector3> _capN = new List<Vector3>();
        readonly List<int> _capT = new List<int>();

        // ---- preview: dashed back-fill curtain (lines) + the in-progress wall slab (slight alpha) ----
        GameObject _pvGo; MeshFilter _pvMf; MeshRenderer _pvMr; Mesh _pvMesh; Material _pvMat;
        readonly List<Vector3> _pv = new List<Vector3>();
        readonly List<int> _pvI = new List<int>();
        GameObject _pvWallGo; MeshFilter _pvWallMf; MeshRenderer _pvWallMr; Mesh _pvWallMesh; Material _pvWallMat;
        readonly List<Vector3> _pvwV = new List<Vector3>();
        readonly List<Vector3> _pvwN = new List<Vector3>();
        readonly List<int> _pvwT = new List<int>();

        public LineGraph Graph => _graph ??= new LineGraph();
        string RootName => "RetainingWall_" + Name;
        float Half => Mathf.Max(0.1f, WallThickness) * 0.5f;
        float FrontSign => FlipSide ? -1f : 1f;   // mesh front rail offset sign; back-grade side == this sign

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        void EnsureRoot()
        {
            if (_root != null) return;
            var all = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == RootName) DestroySafe(all[i]);
            _root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            _mf = _root.AddComponent<MeshFilter>();
            _mr = _root.AddComponent<MeshRenderer>();
            _mesh = new Mesh { name = "RetainingWallMesh" };
            _mf.sharedMesh = _mesh;
            _mat = NetworkDesigner.PipelineMaterials.CreateLitMatte(new Color(0.45f, 0.45f, 0.46f), "RetainingWallConcrete");   // concrete gray
            _mr.sharedMaterial = _mat;

            _capGo = new GameObject(RootName + "_Cap") { hideFlags = HideFlags.DontSave };
            _capMf = _capGo.AddComponent<MeshFilter>();
            _capMr = _capGo.AddComponent<MeshRenderer>();
            _capMesh = new Mesh { name = "RetainingWallCap" };
            _capMf.sharedMesh = _capMesh;
            _capMat = NetworkDesigner.PipelineMaterials.CreateLitMatte(new Color(0.32f, 0.46f, 0.22f), "RetainingWallCapGreen");   // terrain green
            _capMr.sharedMaterial = _capMat;
        }

        // Mouse-wheel nudge of the wall RISE (how far the top sits above the natural grade).
        public void NudgeRise(float deltaMeters) => WallRise = Mathf.Clamp(WallRise + deltaMeters, -50f, 200f);

        // Snap a world XZ to the ~1 m terrain mesh grid (chunk world only), so the endpoint's back-top vertex
        // lines up with a terrain vertex.
        Vector2 SnapXZ(Vector2 xz) => (GridSnap && ChunkWorld.Active) ? ChunkWorld.SnapToMeshGrid(xz) : xz;

        // A node's top elevation: the terrain at its (snapped) point + the current rise.
        float NodeElev(ITerrainSurface field, Vector2 snappedXZ)
            => (field != null ? field.SampleHeight(snappedXZ.x, snappedXZ.y) : 0f) + WallRise;

        // ---- terrace editing (click back-start → click back-end → move to pull the depth → click commit) ----
        // _tState: 0 idle, 1 drawing the (contour-following) back edge, 2 pulling the depth out.
        [NonSerialized] int _tState = 0;
        [NonSerialized] Vector2 _tP0, _tP1;
        [NonSerialized] float _tLevel;
        [NonSerialized] List<Vector2> _backEdge;   // traced contour between the two clicks (preview guide for the inner edge)

        public void AddNode(ITerrainSurface field, Vector3 hit)
        {
            Vector2 raw = new Vector2(hit.x, hit.z);
            if (_tState == 0) { _tP0 = SnapToContour(field, raw, out _tLevel); _tState = 1; return; }
            if (_tState == 1)
            {
                Vector2 end = ProjectToLevel(field, raw, _tLevel);
                if ((end - _tP0).sqrMagnitude < 1f) return;   // ignore a stray click on the start
                _tP1 = end;
                _backEdge = TraceContour(field, _tP0, end, _tLevel);   // contour guide (visual)
                _tState = 2; return;
            }
            CommitTerrace(field, raw);   // 3rd click: pull depth → build
            _tState = 0;
        }

        // ---- contour helpers (work on any backend via field.SampleHeight) ----
        float ContourInterval => ChunkWorld.Active ? Mathf.Max(0.25f, ChunkWorld.ContourMinor) : 1f;
        static float Sample(ITerrainSurface f, Vector2 p) => f != null ? f.SampleHeight(p.x, p.y) : 0f;
        static Vector2 Grad(ITerrainSurface f, Vector2 p)
        {
            const float e = 1f;
            float hx = Sample(f, p + new Vector2(e, 0f)) - Sample(f, p - new Vector2(e, 0f));
            float hz = Sample(f, p + new Vector2(0f, e)) - Sample(f, p - new Vector2(0f, e));
            return new Vector2(hx, hz) / (2f * e);
        }
        // Snap a point onto the nearest contour iso-line; out `level` = that contour's elevation.
        Vector2 SnapToContour(ITerrainSurface f, Vector2 p, out float level)
        {
            level = Mathf.Round(Sample(f, p) / ContourInterval) * ContourInterval;
            return ProjectToLevel(f, p, level);
        }
        // Move a point onto the iso-line of `level` (a few Newton steps along the gradient).
        static Vector2 ProjectToLevel(ITerrainSurface f, Vector2 p, float level)
        {
            for (int it = 0; it < 6; it++)
            {
                Vector2 g = Grad(f, p); float gm2 = g.sqrMagnitude;
                if (gm2 < 1e-6f) break;
                p -= g * ((Sample(f, p) - level) / gm2);
            }
            return p;
        }
        // Trace the `level` contour from `start` toward `target`, returning a polyline along the iso-line.
        List<Vector2> TraceContour(ITerrainSurface f, Vector2 start, Vector2 target, float level)
        {
            var pts = new List<Vector2> { start };
            Vector2 cur = start; const float step = 3f;
            for (int i = 0; i < 240; i++)
            {
                if ((cur - target).sqrMagnitude < step * step) break;
                Vector2 g = Grad(f, cur); float gm = g.magnitude;
                if (gm < 1e-4f) break;                         // flat spot — give up
                Vector2 tan = new Vector2(-g.y, g.x) / gm;     // along the iso-line
                if (Vector2.Dot(tan, target - cur) < 0f) tan = -tan;
                cur = ProjectToLevel(f, cur + tan * step, level);
                pts.Add(cur);
            }
            pts.Add(target);
            return pts;
        }

        // Pull direction (unit, toward the cursor side) + depth from the back-edge CHORD (start→end). Depth snaps
        // to the 1 m grid so the wall's rear vertices land on grid vertices.
        bool PullFrom(Vector2 cursor, out Vector2 perp, out float depth)
        {
            perp = Vector2.zero; depth = 0f;
            Vector2 e = _tP1 - _tP0; float len = e.magnitude; if (len < 0.5f) return false;
            Vector2 perpL = new Vector2(-e.y, e.x) / len;
            float s = Vector2.Dot(cursor - _tP0, perpL);
            perp = s >= 0f ? perpL : -perpL;
            float g = (GridSnap && ChunkWorld.Active) ? ChunkWorld.MeshGridStep : 1f;
            depth = Mathf.Round(Mathf.Abs(s) / g) * g;
            return depth >= 0.5f;
        }

        // Outward normal of segment a→b on the pull `side`.
        static Vector2 SegNormal(Vector2 a, Vector2 b, float side)
        {
            Vector2 e = b - a; float l = e.magnitude; if (l < 1e-5f) return Vector2.right;
            Vector2 perpL = new Vector2(-e.y, e.x) / l;
            return side >= 0f ? perpL : -perpL;
        }
        // Offset a polyline outward by `depth` along per-vertex normals (the wall's rear edge).
        List<Vector2> OffsetPolyline(List<Vector2> poly, float side, float depth)
        {
            var outp = new List<Vector2>(poly.Count);
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 nrm = i == 0 ? SegNormal(poly[0], poly[1], side)
                    : i == poly.Count - 1 ? SegNormal(poly[poly.Count - 2], poly[poly.Count - 1], side)
                    : (SegNormal(poly[i - 1], poly[i], side) + SegNormal(poly[i], poly[i + 1], side)).normalized;
                outp.Add(SnapXZ(poly[i] + nrm * depth));        // grid-snap the rear vertices
            }
            return outp;
        }

        // Build the terrace: flatten the region bounded by the traced CONTOUR (inner edge) and the STRAIGHT wall
        // (outer edge) to the contour level, then a STRAIGHT 3 m wall on the grid-snapped outer edge.
        void CommitTerrace(ITerrainSurface field, Vector2 cursor)
        {
            if (_backEdge == null || _backEdge.Count < 2 || !PullFrom(cursor, out Vector2 perp, out float depth)) return;
            Vector2 o0 = SnapXZ(_tP0 + perp * depth);   // grid-snapped wall REAR line
            Vector2 o1 = SnapXZ(_tP1 + perp * depth);
            // Fill the polygon: the contour (inner) + the straight wall line pushed a little under the wall (outer).
            var poly = new List<Vector2>(_backEdge.Count + 2);
            poly.AddRange(_backEdge);                       // contour, _tP0 → _tP1 (the conforming inner edge)
            poly.Add(o1 + perp * Half);                     // wall line, under the back half of the wall
            poly.Add(o0 + perp * Half);
            ChunkWorld.FillPolygon(poly, _tLevel);
            // Wall centreline = outer edge + perp*Half so the BACK rail sits on the grid-snapped outer line.
            Vector2 c0 = o0 + perp * Half, c1 = o1 + perp * Half;
            int na = Graph.AddNode(c0); Graph.SetNodeY(na, _tLevel);
            int nb = Graph.AddNode(c1); Graph.SetNodeY(nb, _tLevel);
            Vector2 od = (c1 - c0).sqrMagnitude > 1e-6f ? (c1 - c0).normalized : Vector2.right;
            if (Vector2.Dot(new Vector2(od.y, -od.x), perp) >= 0f) Graph.AddEdge(na, nb); else Graph.AddEdge(nb, na);
            Rebuild(field);
        }

        public void EndChain() { _tState = 0; }   // right-click cancels the in-progress terrace

        public void RemoveLastNode(ITerrainSurface field)
        {
            if (_tState > 0) { _tState--; return; }   // step the in-progress terrace back a click
            int last = Graph.Nodes.Count - 1;          // else undo the last committed wall edge + its node
            if (last < 0) return;
            Graph.RemoveNode(last);
            Rebuild(field);
        }

        public bool DeleteNearNode(ITerrainSurface field, Vector3 hit, float radius)
        {
            int nIdx = Graph.NearestNode(new Vector2(hit.x, hit.z), radius);
            if (nIdx < 0) return false;
            Graph.RemoveNode(nIdx);
            Rebuild(field);
            return true;
        }

        public void ClearAll(ITerrainSurface field) { Graph.Clear(); _tState = 0; Rebuild(field); }

        // The wall top at a node: its stored design elevation, else ground + default.
        float NodeTop(ITerrainSurface field, int node)
        {
            float y = Graph.GetNodeY(node);
            if (!float.IsNaN(y)) return y;
            Vector2 p = Graph.Nodes[node];
            return (field != null ? field.SampleHeight(p.x, p.y) : 0f) + Mathf.Max(0.5f, DefaultHeight);
        }

        // ---- terrain regrade (back side) ----

        void ApplyBackGrade(ITerrainSurface field, int a, int b)
        {
            if (!ChunkWorld.Active) return;   // back-grade only on the chunk world for now
            Vector2 A = Graph.Nodes[a], B = Graph.Nodes[b];
            float nA = NodeTop(field, a), nB = NodeTop(field, b);
            float len = Vector2.Distance(A, B);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.5f, SampleStep)));
            var line = new List<Vector3>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                float u = i / (float)steps;
                Vector2 c = Vector2.Lerp(A, B, u);
                line.Add(new Vector3(c.x, Mathf.Lerp(nA, nB, u), c.y));   // (x, topN, z)
            }
            ChunkWorld.GradeWallBack(line, FrontSign, Half, BackFillCap);   // back side == FrontSign side
        }

        // Re-apply the back-grade for every edge (e.g. after a node delete shuffles geometry).
        public void RegradeAll(ITerrainSurface field)
        {
            foreach (LineEdge e in Graph.Edges) ApplyBackGrade(field, e.A, e.B);
        }

        // ---- wall mesh ----

        public void Rebuild(ITerrainSurface field)
        {
            EnsureRoot();
            _v.Clear(); _n.Clear(); _t.Clear();
            _capV.Clear(); _capN.Clear(); _capT.Clear();
            foreach (LineEdge e in Graph.Edges) { BuildEdge(field, e); BuildCap(field, e); }
            _mesh.Clear();
            if (_v.Count > 65000) _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.SetVertices(_v); _mesh.SetNormals(_n); _mesh.SetTriangles(_t, 0);
            _mesh.RecalculateBounds();
            _capMesh.Clear();
            if (_capV.Count > 65000) _capMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _capMesh.SetVertices(_capV); _capMesh.SetNormals(_capN); _capMesh.SetTriangles(_capT, 0);
            _capMesh.RecalculateBounds();
            // Match the terrain exactly (triplanar ground material, colour-tunable) when on the chunk world.
            if (ChunkWorld.Active) { var gm = ChunkWorld.SharedGroundMaterial(); if (gm != null) _capMr.sharedMaterial = gm; }
            else _capMr.sharedMaterial = _capMat;
        }

        void BuildEdge(ITerrainSurface field, LineEdge e)
            => BuildSlab(field, Graph.Nodes[e.A], Graph.Nodes[e.B], NodeTop(field, e.A), NodeTop(field, e.B), _v, _n, _t);

        // Option 2: a flat terrain-coloured CAP that lays a crisp STRAIGHT edge along the wall's back rail (the
        // grid-snapped outer line), extending back over the filled platform — so the wall's rear edge reads sharp
        // instead of a 1 m grid staircase. Sits a hair above the platform fill to avoid z-fighting. Extended a
        // little past each end so it also covers under the wall end caps.
        void BuildCap(ITerrainSurface field, LineEdge e)
        {
            Vector2 A = Graph.Nodes[e.A], B = Graph.Nodes[e.B];
            Vector2 d = B - A; float len = d.magnitude; if (len < 1e-3f) return;
            Vector2 dir = d / len;
            Vector2 right = new Vector2(dir.y, -dir.x) * FrontSign;   // FRONT; the platform/back is -right
            Vector2 back = -right;
            float eA = NodeTop(field, e.A), eB = NodeTop(field, e.B);
            float yA = eA + 0.05f, yB = eB + 0.05f;                   // just above the platform
            Vector2 r0 = A - right * Half, r1 = B - right * Half;     // back rail (= grid-snapped outer line); no end overshoot
            float capMax = Mathf.Max(0.5f, CapDepth);
            float d0 = CapReach(field, r0, back, eA, capMax);         // clamp each end to the platform's back edge
            float d1 = CapReach(field, r1, back, eB, capMax);
            CapQuad(new Vector3(r0.x, yA, r0.y), new Vector3(r1.x, yB, r1.y),
                    new Vector3((r1 + back * d1).x, yB, (r1 + back * d1).y),
                    new Vector3((r0 + back * d0).x, yA, (r0 + back * d0).y));
        }

        // How far back the cap can reach before the natural hillside rises above the platform level (so it never
        // spills onto the rising ground beyond the platform). Capped at maxD.
        float CapReach(ITerrainSurface field, Vector2 from, Vector2 back, float level, float maxD)
        {
            const float step = 1f;
            for (float dd = step; dd <= maxD; dd += step)
                if (Sample(field, from + back * dd) > level + 0.3f) return Mathf.Max(step, dd - step);
            return maxD;
        }

        // A flat (up-facing) quad into the cap mesh, emitted double-sided so winding never hides it.
        void CapQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int s = _capV.Count;
            _capV.Add(a); _capV.Add(b); _capV.Add(c); _capV.Add(d);
            for (int k = 0; k < 4; k++) _capN.Add(Vector3.up);
            _capT.Add(s); _capT.Add(s + 1); _capT.Add(s + 2); _capT.Add(s); _capT.Add(s + 2); _capT.Add(s + 3);
            _capT.Add(s); _capT.Add(s + 2); _capT.Add(s + 1); _capT.Add(s); _capT.Add(s + 3); _capT.Add(s + 2);
        }

        // Build one wall slab (A→B, tops nA/nB) into the given mesh lists. Reused by Rebuild (committed mesh)
        // and the preview (slight-alpha in-progress wall).
        void BuildSlab(ITerrainSurface field, Vector2 A, Vector2 B, float nA, float nB,
                       List<Vector3> v, List<Vector3> n, List<int> t)
        {
            Vector2 d = B - A; float len = d.magnitude;
            if (len < 1e-3f) return;
            Vector2 dir = d / len;
            Vector2 right = new Vector2(dir.y, -dir.x) * FrontSign;   // toward the FRONT (exposed) face
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.5f, SampleStep)));

            // Per-sample rails: front (exposed) + back (buried), top at N, base sunk below the FRONT ground.
            var fTop = new Vector3[steps + 1]; var bTop = new Vector3[steps + 1];
            var fBot = new Vector3[steps + 1]; var bBot = new Vector3[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float u = i / (float)steps;
                Vector2 c = Vector2.Lerp(A, B, u);
                Vector2 fr = c + right * Half, br = c - right * Half;
                float topY = Mathf.Lerp(nA, nB, u) + Mathf.Max(0f, TopLip);   // sit a lip proud of the fill (no z-fight)
                float gF = field != null ? field.SampleHeight(fr.x, fr.y) : 0f;
                float botY = gF - Mathf.Max(0f, FootingDepth);
                if (topY < botY + 0.1f) topY = botY + 0.1f;   // never invert
                fTop[i] = new Vector3(fr.x, topY, fr.y); bTop[i] = new Vector3(br.x, topY, br.y);
                fBot[i] = new Vector3(fr.x, botY, fr.y); bBot[i] = new Vector3(br.x, botY, br.y);
            }
            for (int i = 0; i < steps; i++)
            {
                Quad(v, n, t, fBot[i], fTop[i], fTop[i + 1], fBot[i + 1]);   // front face (exposed)
                Quad(v, n, t, bTop[i], bBot[i], bBot[i + 1], bTop[i + 1]);   // back face
                Quad(v, n, t, fTop[i], bTop[i], bTop[i + 1], fTop[i + 1]);   // top
                Quad(v, n, t, bBot[i], fBot[i], fBot[i + 1], bBot[i + 1]);   // bottom
            }
            Quad(v, n, t, fBot[0], bBot[0], bTop[0], fTop[0]);                       // start cap
            Quad(v, n, t, bBot[steps], fBot[steps], fTop[steps], bTop[steps]);       // end cap
        }

        // Quad a→b→c→d (two tris) into the given lists, flat normal from the winding.
        static void Quad(List<Vector3> v, List<Vector3> n, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 nrm = Vector3.Cross(b - a, c - a).normalized;
            int s = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            n.Add(nrm); n.Add(nrm); n.Add(nrm); n.Add(nrm);
            t.Add(s); t.Add(s + 1); t.Add(s + 2);
            t.Add(s); t.Add(s + 2); t.Add(s + 3);
        }

        // ---- preview ----

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; if (_pvWallMr != null) _pvWallMr.enabled = false; }

        public void UpdatePreview(ITerrainSurface field, Vector3 cursor, bool show)
        {
            EnsurePreview();
            _pvMr.enabled = show; _pvWallMr.enabled = show;
            if (!show) return;
            _pv.Clear(); _pvI.Clear();
            _pvwV.Clear(); _pvwN.Clear(); _pvwT.Clear();

            Vector2 raw = new Vector2(cursor.x, cursor.z);
            if (_tState == 0)
            {
                // Idle: snap the cursor onto the nearest contour, show a round cursor draped on the terrain.
                Vector2 s = SnapToContour(field, raw, out _);
                DrawRing(field, s, 3f);
            }
            else if (_tState == 1)
            {
                // Drawing the back edge: trace the contour from the start toward the cursor, dashed + round cursor.
                Vector2 end = ProjectToLevel(field, raw, _tLevel);
                var trace = TraceContour(field, _tP0, end, _tLevel);
                for (int i = 0; i + 1 < trace.Count; i++) EmitDashedFlat3(P(trace[i], _tLevel), P(trace[i + 1], _tLevel));
                DrawRing(field, end, 3f);
            }
            else if (_tState == 2 && PullFrom(raw, out Vector2 perp, out float depth))
            {
                // Pulling the depth: the contour inner-edge guide + the STRAIGHT platform + the wall ghost.
                Vector2 o0 = SnapXZ(_tP0 + perp * depth), o1 = SnapXZ(_tP1 + perp * depth);
                if (_backEdge != null)
                    for (int i = 0; i + 1 < _backEdge.Count; i++) EmitDashedFlat3(P(_backEdge[i], _tLevel), P(_backEdge[i + 1], _tLevel));
                AddSeg(P(o0, _tLevel), P(o1, _tLevel));                       // straight wall rear line
                EmitDashedFlat3(P(_tP0, _tLevel), P(o0, _tLevel));            // sides
                EmitDashedFlat3(P(_tP1, _tLevel), P(o1, _tLevel));
                Quad(_pvwV, _pvwN, _pvwT, P(_tP0, _tLevel), P(_tP1, _tLevel), P(o1, _tLevel), P(o0, _tLevel));   // translucent shelf
                Vector2 c0 = o0 + perp * Half, c1 = o1 + perp * Half;        // wall ghost (back rail on the outer line)
                Vector2 od = (c1 - c0).sqrMagnitude > 1e-6f ? (c1 - c0).normalized : Vector2.right;
                if (Vector2.Dot(new Vector2(od.y, -od.x), perp) >= 0f) BuildSlab(field, c0, c1, _tLevel, _tLevel, _pvwV, _pvwN, _pvwT);
                else BuildSlab(field, c1, c0, _tLevel, _tLevel, _pvwV, _pvwN, _pvwT);
            }

            _pvMesh.Clear();
            _pvMesh.SetVertices(_pv);
            _pvMesh.SetIndices(_pvI, MeshTopology.Lines, 0);
            _pvMesh.RecalculateBounds();

            _pvWallMesh.Clear();
            _pvWallMesh.SetVertices(_pvwV); _pvWallMesh.SetNormals(_pvwN); _pvWallMesh.SetTriangles(_pvwT, 0);
            _pvWallMesh.RecalculateBounds();
        }

        // A round cursor draped on the terrain: a ring of segments, each vertex lifted to the ground surface.
        void DrawRing(ITerrainSurface field, Vector2 c, float r)
        {
            const int N = 28; Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                Vector2 p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                Vector3 cur = new Vector3(p.x, Ground(field, p) + 0.3f, p.y);
                if (i > 0) AddSeg(prev, cur);
                prev = cur;
            }
        }

        // Dashed line between two world points (constant per-end height; used for the back/side edges).
        void EmitDashedFlat3(Vector3 a, Vector3 b)
        {
            const float dash = 1.2f, gap = 0.8f, period = dash + gap;
            Vector3 d = b - a; float len = d.magnitude; if (len < 1e-4f) { AddSeg(a, b); return; }
            Vector3 dir = d / len;
            for (float pos = 0f; pos < len; pos += period)
                AddSeg(a + dir * pos, a + dir * Mathf.Min(pos + dash, len));
        }

        // Dashed line at a constant height `y` from `from` along `dir` for `length` metres.
        void EmitDashedFlat(Vector2 from, Vector2 dir, float length, float y)
        {
            const float dash = 1.2f, gap = 0.8f, period = dash + gap;
            for (float pos = 0f; pos < length; pos += period)
            {
                Vector2 q0 = from + dir * pos, q1 = from + dir * Mathf.Min(pos + dash, length);
                AddSeg(P(q0, y), P(q1, y));
            }
        }

        void AddSeg(Vector3 a, Vector3 b) { int s = _pv.Count; _pv.Add(a); _pv.Add(b); _pvI.Add(s); _pvI.Add(s + 1); }

        // March back from the wall until the natural ground rises to the wall top N (the flat bench's daylight),
        // capped — mirrors GradeWallBack's fill extent so the preview band matches what gets graded.
        static float BackFillReach(ITerrainSurface field, Vector2 start, Vector2 backDir, float topY, float cap)
        {
            float c = Mathf.Clamp(cap, 5f, 1000f);
            for (float d = 4f; d <= c; d += 4f)
                if (Ground(field, start + backDir * d) >= topY) return d;
            return c;
        }

        static float Ground(ITerrainSurface field, Vector2 xz) => field != null ? field.SampleHeight(xz.x, xz.y) : 0f;
        static Vector3 P(Vector2 xz, float y) => new Vector3(xz.x, y, xz.y);

        void EnsurePreview()
        {
            if (_pvMf != null) return;
            _pvGo = new GameObject(RootName + "_Preview") { hideFlags = HideFlags.DontSave };
            _pvMf = _pvGo.AddComponent<MeshFilter>();
            _pvMr = _pvGo.AddComponent<MeshRenderer>();
            _pvMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _pvMr.receiveShadows = false;
            _pvMesh = new Mesh { name = "RetainingWallPreview" };
            _pvMf.sharedMesh = _pvMesh;
            _pvMat = NetworkDesigner.PipelineMaterials.CreateUnlitColor(new Color(1f, 0.97f, 0.15f), "RetainingWallPreviewMat");   // bright dashed-rib yellow
            _pvMr.sharedMaterial = _pvMat;

            // The in-progress wall slab, rendered at a slight alpha so you can see it forming as you draw.
            _pvWallGo = new GameObject(RootName + "_PreviewWall") { hideFlags = HideFlags.DontSave };
            _pvWallMf = _pvWallGo.AddComponent<MeshFilter>();
            _pvWallMr = _pvWallGo.AddComponent<MeshRenderer>();
            _pvWallMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _pvWallMr.receiveShadows = false;
            _pvWallMesh = new Mesh { name = "RetainingWallPreviewWall" };
            _pvWallMf.sharedMesh = _pvWallMesh;
            _pvWallMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(0.55f, 0.55f, 0.57f, 0.45f), 0f, "RetainingWallPreviewWallMat");
            _pvWallMr.sharedMaterial = _pvWallMat;
        }

        // ---- save / load (NodeY carries each node's wall-top elevation) ----

        public LineGraphSave CollectData() => new LineGraphSave
        {
            Nodes = new List<Vector2>(Graph.Nodes),
            Edges = new List<LineEdge>(Graph.Edges),
            NodeY = new List<float>(Graph.NodeY),
        };

        public void LoadState(LineGraphSave save)
        {
            _graph = new LineGraph();
            _tState = 0;
            if (save != null)
            {
                if (save.Nodes != null) _graph.Nodes.AddRange(save.Nodes);
                if (save.Edges != null) _graph.Edges.AddRange(save.Edges);
                if (save.NodeY != null) _graph.NodeY.AddRange(save.NodeY);
            }
        }
    }
}
