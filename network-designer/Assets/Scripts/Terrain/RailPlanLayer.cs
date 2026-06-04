// Rail PLANNING layer — an unconstrained survey alignment drawn on the terrain
// before any earthworks. You sketch straight/curved segments (same click-to-
// chain UX as the rail tool, Shift = curve) with NO grade or radius limits, so
// you can trace exactly where you intend to build and read the lay of the land.
//
// It renders only as draped yellow lines (no 3D track): the planned centreline
// (or two track lines, for a double-track corridor) plus dashed corridor-edge
// lines offset to each side. Phase 1 is survey-only; the analyzer (cut / fill /
// bridge / tunnel classification) lands in Phase 2.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class RailPlanLayer : ITerrainLineLayer
    {
        [Tooltip("Display name + GameObject root name + hotkey label.")]
        public string Name = "Plan";
        [Tooltip("Total graded corridor width (m) — the dashed edge lines sit this " +
                 "far apart, centred on the alignment. The footprint you plan to clear/grade.")]
        public float CorridorWidth = 30f;
        [Tooltip("Tracks in the corridor: 1 = single centreline, 2 = a double-track " +
                 "pair drawn at +/- half the track gap.")]
        [Range(1, 2)] public int Tracks = 1;
        [Tooltip("Distance between the two tracks (m) when planning a double-track " +
                 "corridor (track centre to track centre).")]
        public float TrackGap = 4f;
        [Tooltip("Curve mode (held-Shift): how far the bezier controls sit from each " +
                 "node toward the guide corner (0 = sharp, 1 = wide arc).")]
        [Range(0.1f, 0.95f)] public float CurveLever = 0.55f;
        [Tooltip("Sampling step (m) for draping the lines onto the terrain surface. " +
                 "Smaller = smoother but more segments.")]
        public float SampleStep = 2f;
        [Tooltip("Metres the plan lines float above the terrain so they don't z-fight.")]
        public float Lift = 0.2f;
        [Tooltip("Length (m) of the straight-ahead alignment guide — the dashed " +
                 "collinear extension out of the chain tail. Also bounds the snap reach.")]
        public float ExtensionGuideLength = 120f;
        [Tooltip("Snap radius (m) to the straight-ahead alignment guide.")]
        public float ExtensionSnapRadius = 4f;
        [Tooltip("Snap radius (m) for resuming/joining the plan's OWN nodes (the end " +
                 "of the corridor you already drew).")]
        public float EndSnapRadius = 8f;
        public Color PlanColor = new Color(1f, 0.92f, 0.2f, 0.85f);

        [System.NonSerialized] public bool CurveModifier; // Shift held this frame
        // Extension-guide heading seeded from the rail this plan starts on, used
        // when the chain tail has no plan edge of its own yet (set by the host).
        [System.NonSerialized] public bool HasSeedDir;
        [System.NonSerialized] public Vector2 SeedDir;

        LineGraph _graph;
        public LineGraph Graph => _graph ??= new LineGraph();
        int _chainTail = -1;
        Vector2 _corner;
        bool _cornerPending;

        // Rendered draped lines (persistent) + the placement preview.
        GameObject _go; MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; Material _mat;
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _idx = new List<int>();
        GameObject _pvGo; MeshFilter _pvMf; MeshRenderer _pvMr; Mesh _pvMesh; Material _pvMat;
        readonly List<Vector3> _pvVerts = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();

        public string LayerName => Name;
        string RootName => "TerrainRailPlan_" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- editing (unconstrained: it's a plan, so no grade/radius refusal) ----

        public void AddNode(TerrainField field, Vector3 hit)
        {
            Vector2 p = new Vector2(hit.x, hit.z);
            if (_chainTail < 0)
            {
                int near = Graph.NearestNode(p, NodePickRadius);
                if (near >= 0) _chainTail = near;
                else if (Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _))
                { _chainTail = Graph.SplitEdge(ei, tt); Rebuild(field); }
                else _chainTail = Graph.AddNode(p);
                _cornerPending = false;
                return;
            }
            if (_cornerPending)
            {
                int end = NearestOrNew(p);
                AddCurvedEdge(_chainTail, end, _corner);
                _chainTail = end; _cornerPending = false;
                Rebuild(field);
                return;
            }
            if (CurveModifier) { _corner = p; _cornerPending = true; return; }
            int idx = NearestOrNew(p);
            Graph.AddEdge(_chainTail, idx);
            _chainTail = idx;
            Rebuild(field);
        }

        const float NodePickRadius = 5f;

        int NearestOrNew(Vector2 p)
        {
            int n = Graph.NearestNode(p, NodePickRadius);
            if (n >= 0 && n != _chainTail) return n;
            if (n < 0 && Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _))
                return Graph.SplitEdge(ei, tt);
            return Graph.AddNode(p);
        }

        void CurveControls(Vector2 a, Vector2 b, Vector2 corner, out Vector2 c1, out Vector2 c2)
        {
            float f = Mathf.Clamp(CurveLever, 0.1f, 0.95f);
            c1 = Vector2.Lerp(a, corner, f);
            c2 = Vector2.Lerp(b, corner, f);
        }

        void AddCurvedEdge(int a, int b, Vector2 corner)
        {
            Graph.AddEdge(a, b);
            LineEdge e = FindEdge(a, b);
            if (e == null) return;
            CurveControls(Graph.Nodes[e.A], Graph.Nodes[e.B], corner, out Vector2 c1, out Vector2 c2);
            e.HasCurve = true; e.ControlA = c1; e.ControlB = c2;
        }

        LineEdge FindEdge(int a, int b)
        {
            foreach (LineEdge e in Graph.Edges)
                if ((e.A == a && e.B == b) || (e.A == b && e.B == a)) return e;
            return null;
        }

        public void EndChain() { _chainTail = -1; _cornerPending = false; }

        // Heading continuing straight out of the chain tail's incoming edge (180° /
        // collinear). False when the tail has no edge.
        bool IncomingDirection(out Vector2 dir)
        {
            dir = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            for (int i = Graph.Edges.Count - 1; i >= 0; i--)
            {
                LineEdge e = Graph.Edges[i];
                if (e.A != _chainTail && e.B != _chainTail) continue;
                GetBezier(e, out Vector2 p0, out Vector2 q1, out Vector2 q2, out Vector2 p3);
                dir = e.B == _chainTail ? LineGraph.BezierTangent(p0, q1, q2, p3, 1f)
                                        : -LineGraph.BezierTangent(p0, q1, q2, p3, 0f);
                if (dir.sqrMagnitude < 1e-6f) dir = e.B == _chainTail ? p3 - p0 : p0 - p3;
                if (dir.sqrMagnitude < 1e-6f) return false;
                dir = dir.normalized;
                return true;
            }
            // No own edge yet (first segment off the rail): use the seeded heading.
            if (HasSeedDir && SeedDir.sqrMagnitude > 1e-6f) { dir = SeedDir.normalized; return true; }
            return false;
        }

        // The chain tail's XZ (the node the next segment grows from), for the host to
        // seed the extension guide from the rail it sits on. False if not drawing.
        public bool TryGetTailXZ(out Vector2 pos)
        {
            pos = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            pos = Graph.Nodes[_chainTail];
            return true;
        }

        // Snap a cursor onto the straight-ahead alignment guide (collinear extension
        // of the incoming edge) when within ExtensionSnapRadius and ahead of the tail.
        public bool TrySnapToExtension(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            float r = Mathf.Max(0f, ExtensionSnapRadius);
            if (r <= 0f || !IncomingDirection(out Vector2 dir)) return false;
            Vector2 origin = Graph.Nodes[_chainTail];
            float along = Vector2.Dot(cursor - origin, dir);
            if (along <= 0f || along > ExtensionGuideLength) return false;
            Vector2 proj = origin + dir * along;
            if ((cursor - proj).sqrMagnitude > r * r) return false;
            snapped = proj;
            return true;
        }

        // Snap to the plan's OWN nearest node (corridor end) / edge within EndSnapRadius,
        // excluding the active chain anchor — so you can stop drawing and resume.
        public bool TrySnapToOwnNode(Vector2 p, out Vector2 snapped)
        {
            snapped = p;
            float r = Mathf.Max(0f, EndSnapRadius);
            if (r <= 0f) return false;
            int best = -1; float bestSq = r * r;
            for (int i = 0; i < Graph.Nodes.Count; i++)
            {
                if (i == _chainTail) continue;
                float d = (Graph.Nodes[i] - p).sqrMagnitude;
                if (d <= bestSq) { bestSq = d; best = i; }
            }
            if (best >= 0) { snapped = Graph.Nodes[best]; return true; }
            if (Graph.NearestPointOnEdge(p, r, out _, out _, out Vector2 pt)) { snapped = pt; return true; }
            return false;
        }

        public void RemoveLastNode(TerrainField field)
        {
            if (_cornerPending) { _cornerPending = false; return; }
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return;
            Graph.RemoveNode(_chainTail);
            _chainTail = -1;
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

        // ---- draped rendering ----

        void GetBezier(LineEdge e, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3)
        {
            q0 = Graph.Nodes[e.A]; q3 = Graph.Nodes[e.B];
            if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
            else { Vector2 d = q3 - q0; q1 = q0 + d / 3f; q2 = q0 + d * (2f / 3f); }
        }

        public void Rebuild(TerrainField field)
        {
            EnsureRender();
            _verts.Clear(); _idx.Clear();
            if (field != null)
                foreach (LineEdge e in Graph.Edges)
                    EmitEdge(field, e, _verts, _idx);
            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetIndices(_idx, MeshTopology.Lines, 0);
            _mesh.RecalculateBounds();
            if (_mat != null) _mat.color = PlanColor;
        }

        // One planned edge: the track centreline(s) (solid) + the two corridor edges
        // (dashed), all draped onto the terrain.
        void EmitEdge(TerrainField field, LineEdge e, List<Vector3> verts, List<int> idx)
        {
            GetBezier(e, out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3);
            if (Tracks >= 2)
            {
                EmitOffsetLine(field, q0, q1, q2, q3, TrackGap * 0.5f, false, verts, idx);
                EmitOffsetLine(field, q0, q1, q2, q3, -TrackGap * 0.5f, false, verts, idx);
            }
            else EmitOffsetLine(field, q0, q1, q2, q3, 0f, false, verts, idx);
            float hw = Mathf.Max(0.5f, CorridorWidth * 0.5f);
            EmitOffsetLine(field, q0, q1, q2, q3, hw, true, verts, idx);
            EmitOffsetLine(field, q0, q1, q2, q3, -hw, true, verts, idx);
        }

        // Drape a polyline offset `lateral` metres to the side of the bezier onto the
        // terrain. dashed = emit every other segment (corridor edges).
        void EmitOffsetLine(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3,
                            float lateral, bool dashed, List<Vector3> verts, List<int> idx)
        {
            float chord = Vector2.Distance(q0, q3);
            int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(chord, 1f) / Mathf.Max(0.5f, SampleStep)), 2, 4000);
            Vector3 prev = default; bool havePrev = false;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                Vector2 c = LineGraph.Bezier(q0, q1, q2, q3, t);
                Vector2 pt = c;
                if (Mathf.Abs(lateral) > 1e-4f)
                {
                    Vector2 tan = LineGraph.BezierTangent(q0, q1, q2, q3, t);
                    if (tan.sqrMagnitude > 1e-6f)
                        pt = c + new Vector2(-tan.y, tan.x).normalized * lateral;
                }
                float y = field.SampleHeight(pt.x, pt.y) + Lift;
                Vector3 w = new Vector3(pt.x, y, pt.y);
                if (havePrev && (!dashed || (i % 2 == 1)))
                {
                    int s = verts.Count;
                    verts.Add(prev); verts.Add(w);
                    idx.Add(s); idx.Add(s + 1);
                }
                prev = w; havePrev = true;
            }
        }

        void EnsureRender()
        {
            if (_mf != null) return;
            _go = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            _mf = _go.AddComponent<MeshFilter>();
            _mr = _go.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mesh = new Mesh { name = "RailPlanMesh" };
            _mf.sharedMesh = _mesh;
            _mat = MakeLineMat("RailPlanMat");
            _mr.sharedMaterial = _mat;
        }

        Material MakeLineMat(string name)
        {
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            return sh != null
                ? new Material(sh) { name = name, color = PlanColor }
                : NetworkDesigner.PipelineMaterials.CreateUnlitColor(PlanColor, name);
        }

        // ---- placement preview ----

        public void UpdatePreview(TerrainField field, Vector3 cursor, bool show)
        {
            EnsurePreview();
            _pvMr.enabled = show;
            if (!show) return;
            _pvVerts.Clear(); _pvIdx.Clear();

            // Cursor ring.
            DrawRing(field, new Vector2(cursor.x, cursor.z), 0.9f);

            bool haveStart = _chainTail >= 0 && _chainTail < Graph.Nodes.Count;
            if (haveStart)
            {
                Vector2 start = Graph.Nodes[_chainTail];
                Vector2 cur = new Vector2(cursor.x, cursor.z);
                // Straight-ahead alignment guide (collinear continuation), dashed.
                if (IncomingDirection(out Vector2 ext))
                {
                    Vector2 gend = start + ext * ExtensionGuideLength;
                    Vector2 gd = gend - start;
                    EmitOffsetLine(field, start, start + gd / 3f, start + gd * (2f / 3f), gend, 0f, true, _pvVerts, _pvIdx);
                }
                if (_cornerPending)
                {
                    CurveControls(start, cur, _corner, out Vector2 c1, out Vector2 c2);
                    EmitPendingEdge(field, start, c1, c2, cur);
                }
                else if (CurveModifier)
                {
                    // Arming the corner: just a guide leg to the cursor.
                    EmitOffsetLine(field, start, Vector2.Lerp(start, cur, 1f / 3f),
                                   Vector2.Lerp(start, cur, 2f / 3f), cur, 0f, true, _pvVerts, _pvIdx);
                }
                else
                {
                    Vector2 d = cur - start;
                    EmitPendingEdge(field, start, start + d / 3f, start + d * (2f / 3f), cur);
                }
            }

            _pvMesh.Clear();
            _pvMesh.SetVertices(_pvVerts);
            _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0);
            _pvMesh.RecalculateBounds();
            if (_pvMat != null) _pvMat.color = PlanColor;
        }

        void EmitPendingEdge(TerrainField field, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3)
        {
            if (Tracks >= 2)
            {
                EmitOffsetLine(field, q0, q1, q2, q3, TrackGap * 0.5f, false, _pvVerts, _pvIdx);
                EmitOffsetLine(field, q0, q1, q2, q3, -TrackGap * 0.5f, false, _pvVerts, _pvIdx);
            }
            else EmitOffsetLine(field, q0, q1, q2, q3, 0f, false, _pvVerts, _pvIdx);
            float hw = Mathf.Max(0.5f, CorridorWidth * 0.5f);
            EmitOffsetLine(field, q0, q1, q2, q3, hw, true, _pvVerts, _pvIdx);
            EmitOffsetLine(field, q0, q1, q2, q3, -hw, true, _pvVerts, _pvIdx);
        }

        void DrawRing(TerrainField field, Vector2 c, float r)
        {
            const int n = 20;
            Vector3 prev = default;
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                float wx = c.x + Mathf.Cos(a) * r, wz = c.y + Mathf.Sin(a) * r;
                float y = (field != null ? field.SampleHeight(wx, wz) : 0f) + Lift;
                Vector3 w = new Vector3(wx, y, wz);
                if (i > 0) { int s = _pvVerts.Count; _pvVerts.Add(prev); _pvVerts.Add(w); _pvIdx.Add(s); _pvIdx.Add(s + 1); }
                prev = w;
            }
        }

        void EnsurePreview()
        {
            if (_pvMf != null) return;
            _pvGo = new GameObject(RootName + "_Preview") { hideFlags = HideFlags.DontSave };
            _pvMf = _pvGo.AddComponent<MeshFilter>();
            _pvMr = _pvGo.AddComponent<MeshRenderer>();
            _pvMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _pvMr.receiveShadows = false;
            _pvMesh = new Mesh { name = "RailPlanPreviewMesh" };
            _pvMf.sharedMesh = _pvMesh;
            _pvMat = MakeLineMat("RailPlanPreviewMat");
            _pvMr.sharedMaterial = _pvMat;
        }

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; }

        public void ClearAll(TerrainField field)
        {
            _graph = new LineGraph();
            _chainTail = -1; _cornerPending = false;
            Rebuild(field);
        }

        // ---- save / load (the plan graph; lines regenerate on load) ----

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
            _chainTail = -1; _cornerPending = false;
            if (save != null)
            {
                if (save.Nodes != null) _graph.Nodes.AddRange(save.Nodes);
                if (save.Edges != null) _graph.Edges.AddRange(save.Edges);
            }
        }
    }
}
