// A road "plan" corridor: a node/edge bezier graph drawn on the terrain (mirrors the rail PlanLayer and
// the generic LineworkLayer through ITerrainLineLayer, so TerrainDesigner drives the click-to-chain
// drawing for free). It carries a road WIDTH so the plan shows the road's footprint — the centreline plus
// the two outer edges at ±halfWidth — draped on the terrain as a line overlay. The plan is the alignment
// you later excavate a bed for and sweep the parametric RoadSweep road along (see [[road-designer-3d]]).
//
// Phase 1: draw + visualise the corridor. Curve smoothing, snapping, profile binding and the excavate/lay
// pipeline come next, reusing the rail plan + GeometryResolver machinery.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class RoadPlanLayer : ITerrainLineLayer
    {
        public string Name = "Road Plan";
        string ITerrainLineLayer.LayerName => Name;

        [Tooltip("Profile (road-config.json id/name) applied to NEW segments as you draw them; empty = use RoadWidth.")]
        public string ActiveProfileId = "";
        [Tooltip("Fallback width (m) for segments with no profile — corridor footprint either side of the centreline.")]
        public float RoadWidth = 14f;

        // Footprint width for a single segment: its profile's total cross-section, else the fallback width.
        public float EdgeWidth(LineEdge e) => NetworkDesigner.Roads.RoadProfileLibrary.TotalWidth(e?.Profile, RoadWidth);
        // The active profile's width (for the placement preview).
        public float ActiveWidth() => NetworkDesigner.Roads.RoadProfileLibrary.TotalWidth(ActiveProfileId, RoadWidth);
        [Tooltip("Straight edges with hard corners. Off = auto-smoothed bezier through the nodes.")]
        public bool Straight = false;
        [Tooltip("Metres between draped samples along the curve.")]
        public float SampleStep = 2f;
        [Tooltip("Metres above the terrain (avoids z-fighting with the ground).")]
        public float Lift = 0.2f;
        [Tooltip("Metres between the cross-ties drawn across the corridor.")]
        public float TieSpacing = 8f;
        public Color PlanColor = new Color(1f, 0.55f, 0.12f, 0.95f);   // amber-orange (rail plan is yellow)
        [Tooltip("Radius (m) to grab an existing node when starting/joining a chain — forms intersections.")]
        public float NodePickRadius = 2.5f;
        [Tooltip("Cursor distance (m) to soft-snap onto the straight-ahead extension of the previous segment.")]
        public float ExtensionSnapRadius = 4f;
        [Tooltip("Radius (m) to snap the cursor onto the plan's own nodes/edges (resume/join).")]
        public float EndSnapRadius = 8f;
        [Tooltip("Length (m) of the collinear extension guide line.")]
        public float ExtensionGuideLength = 120f;
        [Tooltip("Radius (m) of the node puck rings drawn at each plan node.")]
        public float NodePuckRadius = 1.5f;

        // ---- runtime (not serialized) ----
        LineGraph _graph = new LineGraph();
        public LineGraph Graph => _graph ??= new LineGraph();
        int _chainTail = -1;

        GameObject _root; MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; Material _mat;
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<int> _idx = new List<int>();

        GameObject _pvGo; MeshFilter _pvMf; MeshRenderer _pvMr; Mesh _pvMesh;
        readonly List<Vector3> _pv = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();

        const int SubSteps = 48;
        static readonly Vector2[] _pts = new Vector2[SubSteps + 1];

        string RootName => "RoadPlan_" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- editing (chain drawing) ----

        public void AddNode(ITerrainSurface field, Vector3 hit)
        {
            Vector2 p = new Vector2(hit.x, hit.z);
            if (_chainTail < 0)   // start a chain: grab an existing node/edge so corridors branch + join
            {
                int near = Graph.NearestNode(p, NodePickRadius);
                if (near >= 0) _chainTail = near;
                else if (Graph.NearestPointOnEdge(p, NodePickRadius, out int ei, out float tt, out _)) { _chainTail = Graph.SplitEdge(ei, tt); Rebuild(field); }
                else _chainTail = Graph.AddNode(p);
                return;
            }
            int end = NearestOrNew(p);   // join an existing node → a real intersection
            int before = Graph.Edges.Count;
            Graph.AddEdge(_chainTail, end);
            if (Graph.Edges.Count > before) Graph.Edges[Graph.Edges.Count - 1].Profile = ActiveProfileId;   // tag the new segment
            _chainTail = end;
            Rebuild(field);
        }

        int NearestOrNew(Vector2 p)
        {
            int near = Graph.NearestNode(p, NodePickRadius);
            return (near >= 0 && near != _chainTail) ? near : Graph.AddNode(p);
        }

        public void EndChain()
        {
            // Cancelling a just-started chain leaves a lone node with no edges — drop it.
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count && !NodeHasEdge(_chainTail)) Graph.RemoveNode(_chainTail);
            _chainTail = -1;
        }

        bool NodeHasEdge(int n) { foreach (LineEdge e in Graph.Edges) if (e.A == n || e.B == n) return true; return false; }

        public void ClearAll(ITerrainSurface field)
        {
            Graph.Clear();
            _chainTail = -1;
            Rebuild(field);
        }

        public void RemoveLastNode(ITerrainSurface field)
        {
            int last = Graph.Nodes.Count - 1;
            if (last < 0) return;
            Graph.Edges.RemoveAll(e => e.A == last || e.B == last);
            Graph.Nodes.RemoveAt(last);
            if (_chainTail >= Graph.Nodes.Count) _chainTail = -1;
            Rebuild(field);
        }

        public bool DeleteNearNode(ITerrainSurface field, Vector3 hit, float radius)
        {
            int n = Graph.NearestNode(new Vector2(hit.x, hit.z), radius);
            if (n < 0) return false;
            Graph.RemoveNode(n);
            if (_chainTail == n) _chainTail = -1;
            else if (_chainTail > n) _chainTail--;
            Rebuild(field);
            return true;
        }

        // ---- snapping (extension guide + node join), mirroring the rail plan tool ----

        // Heading continuing straight out of the chain tail (collinear with the incoming segment);
        // the cursor side picks which leg to extend when the tail has several edges.
        bool IncomingDirection(Vector2 toward, out Vector2 dir)
        {
            dir = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            Vector2 side = toward - Graph.Nodes[_chainTail];
            float bestDot = float.NegativeInfinity; bool found = false;
            foreach (LineEdge e in Graph.Edges)
            {
                if (e.A != _chainTail && e.B != _chainTail) continue;
                EdgeBezier(e, out Vector2 p0, out Vector2 q1, out Vector2 q2, out Vector2 p3);
                Vector2 cont = e.B == _chainTail ? LineGraph.BezierTangent(p0, q1, q2, p3, 1f) : -LineGraph.BezierTangent(p0, q1, q2, p3, 0f);
                if (cont.sqrMagnitude < 1e-6f) cont = e.B == _chainTail ? p3 - p0 : p0 - p3;
                if (cont.sqrMagnitude < 1e-6f) continue;
                cont = cont.normalized;
                float dot = Vector2.Dot(cont, side);
                if (dot > bestDot) { bestDot = dot; dir = cont; found = true; }
            }
            return found;
        }

        public bool TryGetTailXZ(out Vector2 pos)
        {
            pos = Vector2.zero;
            if (_chainTail < 0 || _chainTail >= Graph.Nodes.Count) return false;
            pos = Graph.Nodes[_chainTail]; return true;
        }

        // SOFT-snap the cursor onto the straight-ahead extension of the previous segment (within
        // ExtensionSnapRadius, ahead of the tail). Roads turn freely, so this assists, it doesn't lock.
        public bool TrySnapToExtension(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            float r = Mathf.Max(0f, ExtensionSnapRadius);
            if (r <= 0f || !IncomingDirection(cursor, out Vector2 dir)) return false;
            Vector2 origin = Graph.Nodes[_chainTail];
            float along = Vector2.Dot(cursor - origin, dir);
            if (along <= 0f || along > ExtensionGuideLength) return false;
            Vector2 proj = origin + dir * along;
            if ((cursor - proj).sqrMagnitude > r * r) return false;
            snapped = proj; return true;
        }

        // Snap onto the plan's own nearest node/edge within EndSnapRadius (excluding the active anchor) —
        // so segments join existing nodes into intersections, and you can resume from any end.
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

        // ---- rendering: a draped corridor ribbon (centreline + both edges + cross-ties) + node pucks ----

        public void Rebuild(ITerrainSurface field)
        {
            EnsureRoot();
            _v.Clear(); _idx.Clear();
            float tieEvery = Mathf.Max(1f, TieSpacing);

            foreach (LineEdge e in Graph.Edges)
            {
                EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
                float half = Mathf.Max(0.1f, EdgeWidth(e) * 0.5f);   // each segment at its own profile width
                BuildCorridorEdge(field, p0, p1, p2, p3, half, tieEvery);
            }
            foreach (Vector2 n in Graph.Nodes) DrawPuck(field, n);   // visible node markers (move/curve/delete handles)

            _mesh.Clear();
            _mesh.SetVertices(_v);
            _mesh.SetIndices(_idx, MeshTopology.Lines, 0);
            _mesh.RecalculateBounds();
        }

        void EdgeBezier(LineEdge e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3)
        {
            if (Straight)
            {
                p0 = Graph.Nodes[e.A]; p3 = Graph.Nodes[e.B];
                Vector2 d = p3 - p0; p1 = p0 + d / 3f; p2 = p0 + d * (2f / 3f);
            }
            else Graph.EdgeControls(e, out p0, out p1, out p2, out p3);
        }

        // Sample the edge by arc length; lay down the centreline + both offset edges + periodic cross-ties.
        void BuildCorridorEdge(ITerrainSurface field, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
                               float half, float tieEvery)
        {
            float len = 0f;
            _pts[0] = p0;
            for (int i = 1; i <= SubSteps; i++) { _pts[i] = LineGraph.Bezier(p0, p1, p2, p3, i / (float)SubSteps); len += Vector2.Distance(_pts[i - 1], _pts[i]); }
            if (len < 1e-3f) return;

            int n = Mathf.Clamp(Mathf.CeilToInt(len / Mathf.Max(0.5f, SampleStep)), 2, 1024);
            float nextTie = 0f, walked = 0f;
            Vector3 cPrev = default, lPrev = default, rPrev = default;
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                Vector2 pos = LineGraph.Bezier(p0, p1, p2, p3, t);
                Vector2 tan = LineGraph.BezierTangent(p0, p1, p2, p3, t);
                Vector2 perp = tan.sqrMagnitude > 1e-8f ? new Vector2(-tan.y, tan.x).normalized : Vector2.right;
                Vector3 c = Drape(field, pos);
                Vector3 l = Drape(field, pos + perp * half);
                Vector3 r = Drape(field, pos - perp * half);
                if (i > 0)
                {
                    AddSeg(cPrev, c); AddSeg(lPrev, l); AddSeg(rPrev, r);
                    walked += Vector3.Distance(cPrev, c);
                }
                if (walked >= nextTie) { AddSeg(l, r); nextTie += tieEvery; }   // cross-tie
                cPrev = c; lPrev = l; rPrev = r;
            }
        }

        Vector3 Drape(ITerrainSurface field, Vector2 xz)
            => new Vector3(xz.x, (field != null ? field.SampleHeight(xz.x, xz.y) : 0f) + Lift, xz.y);

        void AddSeg(Vector3 a, Vector3 b) { int s = _v.Count; _v.Add(a); _v.Add(b); _idx.Add(s); _idx.Add(s + 1); }

        // A draped ring at a node — the visible marker you grab to move / curve / delete.
        void DrawPuck(ITerrainSurface field, Vector2 c)
        {
            const int N = 16; float r = Mathf.Max(0.2f, NodePuckRadius);
            Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float a = i / (float)N * Mathf.PI * 2f;
                Vector3 cur = Drape(field, new Vector2(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r));
                if (i > 0) AddSeg(prev, cur);
                prev = cur;
            }
        }

        void EnsureRoot()
        {
            if (_mf != null) { return; }
            GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].name == RootName) DestroySafe(all[i]);
            _root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            _mf = _root.AddComponent<MeshFilter>();
            _mr = _root.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _mr.receiveShadows = false;
            _mesh = new Mesh { name = "RoadPlanMesh" };
            _mf.sharedMesh = _mesh;
            _mat = MakeMat();
            _mr.sharedMaterial = _mat;
        }

        Material MakeMat()
        {
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            return sh != null ? new Material(sh) { name = "RoadPlanMat", color = PlanColor }
                              : NetworkDesigner.PipelineMaterials.CreateUnlitColor(PlanColor, "RoadPlanMat");
        }

        // ---- placement preview (ghost puck + dashed pending edge) ----

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; }

        public void UpdatePreview(ITerrainSurface field, Vector3 cursor, bool show)
        {
            EnsurePreview();
            _pvMr.enabled = show;
            if (!show) return;
            _pv.Clear(); _pvIdx.Clear();

            const int N = 24; const float R = 1.0f;
            Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float ang = i / (float)N * Mathf.PI * 2f;
                Vector3 cur = Drape(field, new Vector2(cursor.x + Mathf.Cos(ang) * R, cursor.z + Mathf.Sin(ang) * R));
                if (i > 0) AddPv(prev, cur);
                prev = cur;
            }
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count)
            {
                Vector2 tnode = Graph.Nodes[_chainTail];
                Vector2 c2 = new Vector2(cursor.x, cursor.z);
                float len = Vector2.Distance(tnode, c2);
                if (len > 1e-3f)
                {
                    int n = Mathf.Clamp(Mathf.CeilToInt(len / 3f), 1, 200);
                    Vector3 dPrev = default;
                    for (int i = 0; i <= n; i++)
                    {
                        Vector3 cur = Drape(field, Vector2.Lerp(tnode, c2, (float)i / n));
                        if (i > 0 && (i % 2 == 0)) AddPv(dPrev, cur);   // dashed
                        dPrev = cur;
                    }
                }
            }
            // Collinear extension guide: a dashed line straight out of the tail (where the cursor soft-snaps).
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count
                && IncomingDirection(new Vector2(cursor.x, cursor.z), out Vector2 gdir))
            {
                Vector2 o = Graph.Nodes[_chainTail];
                const int gn = 30;
                Vector3 gp = default;
                for (int i = 0; i <= gn; i++)
                {
                    Vector3 cur = Drape(field, o + gdir * ((float)i / gn * ExtensionGuideLength));
                    if (i > 0 && (i % 2 == 0)) AddPv(gp, cur);   // dashed
                    gp = cur;
                }
            }
            _pvMesh.Clear(); _pvMesh.SetVertices(_pv); _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0); _pvMesh.RecalculateBounds();
        }

        void AddPv(Vector3 a, Vector3 b) { int s = _pv.Count; _pv.Add(a); _pv.Add(b); _pvIdx.Add(s); _pvIdx.Add(s + 1); }

        void EnsurePreview()
        {
            if (_pvMf != null) return;
            _pvGo = new GameObject(RootName + "_Preview") { hideFlags = HideFlags.DontSave };
            _pvMf = _pvGo.AddComponent<MeshFilter>();
            _pvMr = _pvGo.AddComponent<MeshRenderer>();
            _pvMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _pvMr.receiveShadows = false;
            _pvMesh = new Mesh { name = "RoadPlanPreviewMesh" };
            _pvMf.sharedMesh = _pvMesh;
            _pvMr.sharedMaterial = MakeMat();
        }

        // ---- save / load (the node/edge graph; geometry regenerated on load) ----

        public LineGraphSave CollectData() => new LineGraphSave { Nodes = new List<Vector2>(Graph.Nodes), Edges = new List<LineEdge>(Graph.Edges) };

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
