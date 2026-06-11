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

        [Tooltip("Total road width (m) drawn either side of the centreline — the corridor footprint.")]
        public float RoadWidth = 14f;
        [Tooltip("Straight edges with hard corners. Off = auto-smoothed bezier through the nodes.")]
        public bool Straight = false;
        [Tooltip("Metres between draped samples along the curve.")]
        public float SampleStep = 2f;
        [Tooltip("Metres above the terrain (avoids z-fighting with the ground).")]
        public float Lift = 0.2f;
        [Tooltip("Metres between the cross-ties drawn across the corridor.")]
        public float TieSpacing = 8f;
        public Color PlanColor = new Color(1f, 0.55f, 0.12f, 0.95f);   // amber-orange (rail plan is yellow)

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
            int idx = Graph.AddNode(new Vector2(hit.x, hit.z));
            if (_chainTail >= 0) Graph.AddEdge(_chainTail, idx);
            _chainTail = idx;
            Rebuild(field);
        }

        public void EndChain() { _chainTail = -1; }

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

        // ---- rendering: a draped corridor ribbon (centreline + both edges + cross-ties) ----

        public void Rebuild(ITerrainSurface field)
        {
            EnsureRoot();
            _v.Clear(); _idx.Clear();
            float half = Mathf.Max(0.1f, RoadWidth * 0.5f);
            float tieEvery = Mathf.Max(1f, TieSpacing);

            foreach (LineEdge e in Graph.Edges)
            {
                EdgeBezier(e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3);
                BuildCorridorEdge(field, p0, p1, p2, p3, half, tieEvery);
            }

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
