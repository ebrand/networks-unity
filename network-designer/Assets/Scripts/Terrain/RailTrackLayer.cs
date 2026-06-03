// Procedural rail track drawn on a node/edge graph (same click-to-chain UX as
// the linework layers). Each STRAIGHT edge emits two parallel rails + evenly
// spaced ties as low-poly flat-shaded boxes, conformed to the terrain at the
// edge endpoints (flat between for now — curves & ballast are later slices).
// Implements ITerrainLineLayer so TerrainDesigner draws/saves it like a fence.
//
// Mesh split into two children (Rails, Ties) so each gets its own material.
// Boxes use un-shared per-face verts -> RecalculateNormals gives hard facets,
// matching the terrain's low-poly look.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class RailTrackLayer : ITerrainLineLayer
    {
        [Tooltip("Display name + GameObject root name + hotkey label.")]
        public string Name = "Rail";
        [Tooltip("Distance between the two rail centres (track gauge), metres. " +
                 "Standard gauge is 1.435; scale to taste for the low-poly world.")]
        public float Gauge = 1.5f;
        [Tooltip("Rail cross-section width (metres).")]
        public float RailWidth = 0.1f;
        [Tooltip("Rail height standing above the ties (metres).")]
        public float RailHeight = 0.14f;
        [Tooltip("Metres between ties (sleepers) along the track.")]
        public float TieSpacing = 0.7f;
        [Tooltip("Tie length across the track — should exceed the gauge (metres).")]
        public float TieLength = 2.0f;
        [Tooltip("Tie thickness along the direction of travel (metres).")]
        public float TieThickness = 0.24f;
        [Tooltip("Tie height (metres).")]
        public float TieHeight = 0.12f;
        [Tooltip("Metres the track is raised above the terrain surface.")]
        public float VerticalOffset = 0.02f;
        [Tooltip("Conform the track to the terrain surface (sampled along the curve).")]
        public bool Conform = true;
        [Tooltip("Curve mode (held-Shift): how far the bezier controls sit from each " +
                 "node toward the guide corner (0 = sharp through corner, 1 = wide arc).")]
        [Range(0.1f, 0.95f)] public float CurveLever = 0.55f;
        [Tooltip("Design speed (km/h) for sections laid now. Sets the minimum curve " +
                 "radius — tighter curves are refused (preview turns red). Lower it " +
                 "to lay tighter curves.")]
        public float SpeedLimitKmh = 40f;
        [Tooltip("Max comfortable lateral acceleration in g (with cant). Higher = " +
                 "tighter curves allowed for a given speed. Real rail ~0.1; raise it " +
                 "for game-scaled tighter curves.")]
        [Range(0.05f, 0.5f)] public float MaxLateralG = 0.15f;

        // Minimum curve radius (m) for the current design speed: R = v^2 / (g*a),
        // v in m/s, a the lateral-accel limit. Quadratic in speed, as for real rail.
        public float MinRadiusForSpeed
        {
            get { float v = SpeedLimitKmh / 3.6f; return v * v / (9.81f * Mathf.Max(0.01f, MaxLateralG)); }
        }
        // Last previewed curve's tightest radius + whether it violated the minimum
        // (read by the on-screen hint; updated each frame while a corner is armed).
        [System.NonSerialized] public float LastPreviewRadius = float.PositiveInfinity;
        [System.NonSerialized] public bool LastPreviewTooTight;
        public Color RailColor = new Color(0.28f, 0.28f, 0.30f);
        public Color TieColor = new Color(0.32f, 0.22f, 0.14f);

        // ---- runtime (not serialized) ----
        LineGraph _graph = new LineGraph();
        GameObject _root, _railObj, _tieObj;
        Mesh _railMesh, _tieMesh;
        Material _railMat, _tieMat;
        int _chainTail = -1;          // current anchor node (start of next segment)
        // Straight by default; while CurveModifier (Shift) is held, a click drops a
        // guide corner and the next click ends a curve through it.
        [System.NonSerialized] public bool CurveModifier;
        bool _cornerPending;          // a guide corner has been placed, awaiting the end click
        Vector2 _corner;              // the pending guide corner
        readonly List<Vector3> _rv = new List<Vector3>();
        readonly List<int> _rt = new List<int>();
        readonly List<Vector3> _tv = new List<Vector3>();
        readonly List<int> _tt = new List<int>();

        // Placement preview (ghost puck + dashed pending centreline + rail edges).
        GameObject _pvGo;
        MeshFilter _pvMf;
        MeshRenderer _pvMr;
        Mesh _pvMesh;
        Material _pvMat;
        readonly List<Vector3> _pvVerts = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();

        public LineGraph Graph => _graph ??= new LineGraph();
        string ITerrainLineLayer.LayerName => Name;
        string RootName => "TerrainRail_" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- editing ----

        // One click. Straight by default: chains a straight segment from the anchor.
        // Hold Shift (CurveModifier): the click drops a guide corner; the following
        // click ends a bezier curve through it. The anchor carries over either way,
        // so straight and curved segments mix freely in one chain.
        public void AddNode(TerrainField field, Vector3 hit)
        {
            Vector2 p = new Vector2(hit.x, hit.z);

            // First click of a chain just sets the anchor.
            if (_chainTail < 0)
            {
                _chainTail = Graph.AddNode(p);
                _cornerPending = false;
                return;
            }

            // A corner is armed: this click is the endpoint -> commit the curve,
            // unless it would be tighter than the speed's minimum radius (refused;
            // the corner stays armed so you can pick a wider endpoint).
            if (_cornerPending)
            {
                Vector2 start = Graph.Nodes[_chainTail];
                CurveControls(start, p, _corner, out Vector2 c1, out Vector2 c2);
                if (MinCurveRadius(start, c1, c2, p) < MinRadiusForSpeed) return;
                int end = Graph.AddNode(p);
                AddCurvedEdge(_chainTail, end, _corner);
                _chainTail = end;
                _cornerPending = false;
                Rebuild(field);
                return;
            }

            // Anchor present, no corner armed:
            if (CurveModifier)
            {
                _corner = p;            // Shift held -> this click arms the guide corner
                _cornerPending = true;
                return;
            }
            int idx = Graph.AddNode(p); // plain click -> straight segment
            Graph.AddEdge(_chainTail, idx);
            TagEdge(_chainTail, idx);
            _chainTail = idx;
            Rebuild(field);
        }

        // Bezier controls for a curve from a to b bending toward corner.
        void CurveControls(Vector2 a, Vector2 b, Vector2 corner, out Vector2 c1, out Vector2 c2)
        {
            float f = Mathf.Clamp(CurveLever, 0.1f, 0.95f);
            c1 = Vector2.Lerp(a, corner, f);
            c2 = Vector2.Lerp(b, corner, f);
        }

        // Connect a..b and tag it with bezier controls leaning toward the corner.
        void AddCurvedEdge(int a, int b, Vector2 corner)
        {
            Graph.AddEdge(a, b);
            LineEdge e = FindEdge(a, b);
            if (e == null) return;
            CurveControls(Graph.Nodes[e.A], Graph.Nodes[e.B], corner, out Vector2 c1, out Vector2 c2);
            e.HasCurve = true;
            e.ControlA = c1; e.ControlB = c2;
            e.SpeedLimit = SpeedLimitKmh;
        }

        void TagEdge(int a, int b) { var e = FindEdge(a, b); if (e != null) e.SpeedLimit = SpeedLimitKmh; }

        LineEdge FindEdge(int a, int b)
        {
            foreach (LineEdge e in Graph.Edges)
                if ((e.A == a && e.B == b) || (e.A == b && e.B == a)) return e;
            return null;
        }

        // Tightest radius (m) along a cubic bezier, via 3-point circumradius over
        // samples. Returns +inf for a straight line.
        static float MinCurveRadius(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            const int N = 24;
            float minR = float.PositiveInfinity;
            Vector2 a = LineGraph.Bezier(p0, p1, p2, p3, 0f);
            Vector2 b = LineGraph.Bezier(p0, p1, p2, p3, 1f / N);
            for (int i = 2; i <= N; i++)
            {
                Vector2 c = LineGraph.Bezier(p0, p1, p2, p3, i / (float)N);
                float ab = Vector2.Distance(a, b), bc = Vector2.Distance(b, c), ca = Vector2.Distance(c, a);
                float area2 = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
                if (area2 > 1e-6f) minR = Mathf.Min(minR, ab * bc * ca / (2f * area2));
                a = b; b = c;
            }
            return minR;
        }

        public void EndChain() { _chainTail = -1; _cornerPending = false; }

        public void ClearAll(TerrainField field)
        {
            Graph.Clear();
            _chainTail = -1;
            _cornerPending = false;
            Rebuild(field);
        }

        public void RemoveLastNode(TerrainField field)
        {
            // Backspace first cancels an armed (un-committed) guide corner.
            if (_cornerPending) { _cornerPending = false; return; }
            int last = Graph.Nodes.Count - 1;
            if (last < 0) return;
            Graph.Edges.RemoveAll(e => e.A == last || e.B == last);
            Graph.Nodes.RemoveAt(last);
            if (_chainTail >= Graph.Nodes.Count) _chainTail = -1;
            Rebuild(field);
        }

        public bool DeleteNearNode(TerrainField field, Vector3 hit, float radius)
        {
            int n = Graph.NearestNode(new Vector2(hit.x, hit.z), radius);
            if (n < 0) return false;
            Graph.RemoveNode(n);
            if (_chainTail == n) _chainTail = -1; else if (_chainTail > n) _chainTail--;
            _cornerPending = false;
            Rebuild(field);
            return true;
        }

        // ---- rendering ----

        void EnsureObjects()
        {
            if (_root == null)
            {
                GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == RootName) DestroySafe(all[i]);
                _root = new GameObject(RootName);
            }
            if (_railMat == null) _railMat = NetworkDesigner.PipelineMaterials.CreateLit(RailColor, 0.6f, "RailMat");
            if (_tieMat == null) _tieMat = NetworkDesigner.PipelineMaterials.CreateLitMatte(TieColor, "TieMat");
            _railObj = EnsureChild(_railObj, "Rails", ref _railMesh, _railMat);
            _tieObj = EnsureChild(_tieObj, "Ties", ref _tieMesh, _tieMat);
        }

        GameObject EnsureChild(GameObject go, string childName, ref Mesh mesh, Material mat)
        {
            if (go != null) return go;
            go = new GameObject(childName);
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            mesh = new Mesh { name = childName + "Mesh" };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        public void Rebuild(TerrainField field)
        {
            EnsureObjects();
            _rv.Clear(); _rt.Clear(); _tv.Clear(); _tt.Clear();
            foreach (LineEdge e in Graph.Edges)
                BuildEdge(field, e);
            Apply(_railMesh, _rv, _rt);
            Apply(_tieMesh, _tv, _tt);
            if (_railMat != null) _railMat.color = RailColor; // live colour tweaks
            if (_tieMat != null) _tieMat.color = TieColor;
        }

        static void Apply(Mesh m, List<Vector3> v, List<int> t)
        {
            if (m == null) return;
            m.Clear();
            m.indexFormat = v.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
        }

        Vector3 Conformed(TerrainField field, Vector2 xz)
            => new Vector3(xz.x,
                (Conform && field != null ? field.SampleHeight(xz.x, xz.y) : 0f) + VerticalOffset,
                xz.y);

        // Arc-length sampling of one edge (straight or auto-smoothed bezier).
        // Rails are emitted as a chain of short box segments following the curve;
        // ties at TieSpacing. Every sample is conformed to the terrain, so the
        // track drapes over relief rather than only touching down at the nodes.
        const int SubSteps = 32;            // bezier samples for the arc-length table
        const float RailSegment = 1.5f;     // target rail box length along the curve
        static readonly Vector2[] _pts = new Vector2[SubSteps + 1];
        static readonly float[] _cum = new float[SubSteps + 1];

        void BuildEdge(TerrainField field, LineEdge e)
        {
            // Per-edge: explicit bezier controls if drawn with the curve tool,
            // otherwise a straight segment (controls on the chord).
            Vector2 q0 = Graph.Nodes[e.A], q3 = Graph.Nodes[e.B];
            Vector2 q1, q2;
            if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
            else { Vector2 d = q3 - q0; q1 = q0 + d / 3f; q2 = q0 + d * (2f / 3f); }

            // Arc-length table over XZ (terrain relief adds negligible length).
            _pts[0] = q0; _cum[0] = 0f;
            for (int i = 1; i <= SubSteps; i++)
            {
                _pts[i] = LineGraph.Bezier(q0, q1, q2, q3, i / (float)SubSteps);
                _cum[i] = _cum[i - 1] + Vector2.Distance(_pts[i - 1], _pts[i]);
            }
            float len = _cum[SubSteps];
            if (len < 1e-3f) return;

            float railCY = TieHeight + RailHeight * 0.5f; // rails sit on the ties
            float halfG = Gauge * 0.5f;

            // Rails: walk the curve, build a box per side between samples.
            int segs = Mathf.Max(1, Mathf.CeilToInt(len / RailSegment));
            Vector3 prevL = default, prevR = default;
            for (int i = 0; i <= segs; i++)
            {
                SampleTrack(field, q0, q1, q2, q3, ArcToU(len * i / segs),
                            out Vector3 pos, out _, out Vector3 right, out Vector3 up);
                Vector3 cL = pos + right * halfG + up * railCY;
                Vector3 cR = pos - right * halfG + up * railCY;
                if (i > 0)
                {
                    AddRailSeg(prevL, cL);
                    AddRailSeg(prevR, cR);
                }
                prevL = cL; prevR = cR;
            }

            // Ties at fixed spacing along the curve.
            float s = Mathf.Max(0.1f, TieSpacing);
            for (float d = 0f; d <= len + 1e-3f; d += s)
            {
                SampleTrack(field, q0, q1, q2, q3, ArcToU(Mathf.Min(d, len)),
                            out Vector3 pos, out Vector3 fwd, out Vector3 right, out Vector3 up);
                Vector3 center = pos + up * (TieHeight * 0.5f);
                AddBox(_tv, _tt, center, fwd, right, up, TieThickness, TieLength, TieHeight);
            }
        }

        // A rail box between two consecutive rail-centre points on the curve.
        void AddRailSeg(Vector3 a, Vector3 b)
        {
            Vector3 along = b - a;
            float l = along.magnitude;
            if (l < 1e-4f) return;
            Vector3 fwd = along / l;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;
            AddBox(_rv, _rt, (a + b) * 0.5f, fwd, right, up, l, RailWidth, RailHeight);
        }

        // Arc length -> bezier parameter u, via the cumulative table.
        float ArcToU(float d)
        {
            if (d <= 0f) return 0f;
            float len = _cum[SubSteps];
            if (d >= len) return 1f;
            int seg = 1;
            while (seg < SubSteps && _cum[seg] < d) seg++;
            float segLen = _cum[seg] - _cum[seg - 1];
            float f = segLen > 1e-5f ? (d - _cum[seg - 1]) / segLen : 0f;
            return (seg - 1 + f) / SubSteps;
        }

        // Position + orientation basis at bezier parameter u. Forward comes from a
        // small conformed step along the curve, so it tilts with the terrain grade.
        void SampleTrack(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3, float u,
                         out Vector3 pos, out Vector3 fwd, out Vector3 right, out Vector3 up)
        {
            pos = Conformed(field, LineGraph.Bezier(q0, q1, q2, q3, u));
            const float du = 0.01f;
            Vector3 ahead = u <= 1f - du
                ? Conformed(field, LineGraph.Bezier(q0, q1, q2, q3, u + du))
                : pos + (pos - Conformed(field, LineGraph.Bezier(q0, q1, q2, q3, u - du)));
            fwd = ahead - pos;
            if (fwd.sqrMagnitude < 1e-8f)
            {
                Vector2 t = LineGraph.BezierTangent(q0, q1, q2, q3, u);
                fwd = new Vector3(t.x, 0f, t.y);
            }
            fwd = fwd.sqrMagnitude > 1e-8f ? fwd.normalized : Vector3.forward;
            right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            up = Vector3.Cross(fwd, right).normalized;
        }

        // Oriented box with un-shared per-face verts (flat low-poly normals).
        // lenF/lenR/lenU are full sizes along fwd/right/up. Each face is wound so
        // RecalculateNormals (normal ~ cross(b-a, c-a)) points outward.
        static void AddBox(List<Vector3> v, List<int> t, Vector3 c,
                           Vector3 fwd, Vector3 right, Vector3 up,
                           float lenF, float lenR, float lenU)
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

        // ---- placement preview ----

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; }

        public void UpdatePreview(TerrainField field, Vector3 cursor, bool show)
        {
            EnsurePreview();
            _pvMr.enabled = show;
            if (!show) return;
            _pvVerts.Clear();
            _pvIdx.Clear();
            const float lift = 0.15f;

            DrawPuck(field, cursor, lift);
            Vector2 cur = new Vector2(cursor.x, cursor.z);
            bool haveStart = _chainTail >= 0 && _chainTail < Graph.Nodes.Count;
            Vector2 start = haveStart ? Graph.Nodes[_chainTail] : Vector2.zero;

            LastPreviewRadius = float.PositiveInfinity;
            LastPreviewTooTight = false;

            if (haveStart && _cornerPending)
            {
                // Corner armed: legs start->corner->cursor + the live curve it makes.
                // Check the curve's tightest radius against the speed's minimum and
                // flag/recolour when it's too sharp to lay.
                EmitDashed(field, start, _corner, Vector2.zero, lift);
                EmitDashed(field, _corner, cur, Vector2.zero, lift);
                CurveControls(start, cur, _corner, out Vector2 c1, out Vector2 c2);
                LastPreviewRadius = MinCurveRadius(start, c1, c2, cur);
                LastPreviewTooTight = LastPreviewRadius < MinRadiusForSpeed;
                DrawBezierPreview(field, start, c1, c2, cur, lift);
            }
            else if (haveStart && CurveModifier)
            {
                // Shift held, no corner yet: cursor is the prospective guide corner.
                EmitDashed(field, start, cur, Vector2.zero, lift);
            }
            else if (haveStart)
            {
                // Straight (default): dashed gauge (centreline + both rails) to cursor.
                DrawDashedGauge(field, start, cur, lift);
            }

            // Red while a too-tight curve is armed, normal otherwise.
            if (_pvMat != null)
                _pvMat.color = LastPreviewTooTight ? new Color(1f, 0.25f, 0.2f, 1f)
                                                   : new Color(1f, 0.8f, 0.3f, 1f);

            _pvMesh.Clear();
            _pvMesh.SetVertices(_pvVerts);
            _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0);
            _pvMesh.RecalculateBounds();
        }

        // Cursor ring.
        void DrawPuck(TerrainField field, Vector3 cursor, float lift)
        {
            const int N = 24; const float Rr = 0.9f;
            Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float ang = i / (float)N * Mathf.PI * 2f;
                float x = cursor.x + Mathf.Cos(ang) * Rr;
                float z = cursor.z + Mathf.Sin(ang) * Rr;
                float y = (field != null ? field.SampleHeight(x, z) : cursor.y) + lift;
                Vector3 c = new Vector3(x, y, z);
                if (i > 0) AddSeg(prev, c);
                prev = c;
            }
        }

        // Dashed centreline + both rail edges for a straight pending segment.
        void DrawDashedGauge(TerrainField field, Vector2 a, Vector2 b, float lift)
        {
            Vector2 dir = b - a;
            float len = dir.magnitude;
            if (len < 1e-4f) return;
            Vector2 perp = new Vector2(-dir.y, dir.x) / len * (Gauge * 0.5f);
            EmitDashed(field, a, b, Vector2.zero, lift);
            EmitDashed(field, a, b, perp, lift);
            EmitDashed(field, a, b, -perp, lift);
        }

        // Solid preview of the curve the tool will lay: centreline + both rails.
        void DrawBezierPreview(TerrainField field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float lift)
        {
            const int N = 24;
            float halfG = Gauge * 0.5f;
            Vector3 pc = default, pl = default, pr = default;
            for (int i = 0; i <= N; i++)
            {
                float u = i / (float)N;
                Vector2 xz = LineGraph.Bezier(p0, p1, p2, p3, u);
                Vector2 t = LineGraph.BezierTangent(p0, p1, p2, p3, u);
                Vector2 perp = t.sqrMagnitude > 1e-6f
                    ? new Vector2(-t.y, t.x).normalized * halfG : Vector2.zero;
                Vector3 c = LiftPt(field, xz, lift);
                Vector3 l = LiftPt(field, xz + perp, lift);
                Vector3 r = LiftPt(field, xz - perp, lift);
                if (i > 0) { AddSeg(pc, c); AddSeg(pl, l); AddSeg(pr, r); }
                pc = c; pl = l; pr = r;
            }
        }

        static Vector3 LiftPt(TerrainField field, Vector2 xz, float lift)
            => new Vector3(xz.x, (field != null ? field.SampleHeight(xz.x, xz.y) : 0f) + lift, xz.y);

        void EmitDashed(TerrainField field, Vector2 a, Vector2 b, Vector2 offset, float lift)
        {
            Vector2 a2 = a + offset, b2 = b + offset;
            Vector3 start = new Vector3(a2.x, 0f, a2.y);
            Vector3 d = new Vector3(b2.x - a2.x, 0f, b2.y - a2.y);
            float len = d.magnitude;
            if (len < 1e-4f) return;
            Vector3 dir = d / len;
            const float dash = 1.0f, gap = 0.6f, period = dash + gap;
            for (float pos = 0f; pos < len; pos += period)
            {
                float e0 = pos, e1 = Mathf.Min(pos + dash, len);
                Vector3 p0 = start + dir * e0, p1 = start + dir * e1;
                if (field != null)
                {
                    p0.y = field.SampleHeight(p0.x, p0.z) + lift;
                    p1.y = field.SampleHeight(p1.x, p1.z) + lift;
                }
                AddSeg(p0, p1);
            }
        }

        void AddSeg(Vector3 a, Vector3 b)
        {
            int s = _pvVerts.Count;
            _pvVerts.Add(a); _pvVerts.Add(b);
            _pvIdx.Add(s); _pvIdx.Add(s + 1);
        }

        void EnsurePreview()
        {
            if (_pvMf != null) return;
            _pvGo = new GameObject(RootName + "_Preview");
            _pvMf = _pvGo.AddComponent<MeshFilter>();
            _pvMr = _pvGo.AddComponent<MeshRenderer>();
            _pvMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _pvMr.receiveShadows = false;
            _pvMesh = new Mesh { name = "RailPreviewMesh" };
            _pvMf.sharedMesh = _pvMesh;
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            Color col = new Color(1f, 0.8f, 0.3f, 1f);
            _pvMat = sh != null
                ? new Material(sh) { name = "RailPreviewMat", color = col }
                : NetworkDesigner.PipelineMaterials.CreateUnlitColor(col, "RailPreviewMat");
            _pvMr.sharedMaterial = _pvMat;
        }

        // ---- save / load (the graph; geometry regenerates on load) ----

        public LineGraphSave CollectData()
        {
            return new LineGraphSave
            {
                Nodes = new List<Vector2>(Graph.Nodes),
                Edges = new List<LineEdge>(Graph.Edges),
            };
        }

        public void LoadState(LineGraphSave save)
        {
            _graph = new LineGraph();
            _chainTail = -1;
            if (save != null)
            {
                if (save.Nodes != null) _graph.Nodes.AddRange(save.Nodes);
                if (save.Edges != null) _graph.Edges.AddRange(save.Edges);
            }
        }
    }
}
