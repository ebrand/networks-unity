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
        [Tooltip("Conform the track to the terrain height at the edge endpoints.")]
        public bool Conform = true;
        public Color RailColor = new Color(0.28f, 0.28f, 0.30f);
        public Color TieColor = new Color(0.32f, 0.22f, 0.14f);

        // ---- runtime (not serialized) ----
        LineGraph _graph = new LineGraph();
        GameObject _root, _railObj, _tieObj;
        Mesh _railMesh, _tieMesh;
        Material _railMat, _tieMat;
        int _chainTail = -1;
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

        // ---- editing (identical chain UX to the linework layers) ----

        public void AddNode(TerrainField field, Vector3 hit)
        {
            int idx = Graph.AddNode(new Vector2(hit.x, hit.z));
            if (_chainTail >= 0) Graph.AddEdge(_chainTail, idx);
            _chainTail = idx;
            Rebuild(field);
        }

        public void EndChain() { _chainTail = -1; }

        public void ClearAll(TerrainField field)
        {
            Graph.Clear();
            _chainTail = -1;
            Rebuild(field);
        }

        public void RemoveLastNode(TerrainField field)
        {
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
                BuildEdge(field, Graph.Nodes[e.A], Graph.Nodes[e.B]);
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

        // One straight edge: two rail boxes (offset +/- gauge/2) spanning A->B,
        // and a tie box every TieSpacing. Endpoints sit on the terrain; the edge
        // is linear (and slope-tilted) between them.
        void BuildEdge(TerrainField field, Vector2 a2, Vector2 b2)
        {
            Vector3 a = Conformed(field, a2);
            Vector3 b = Conformed(field, b2);
            Vector3 along = b - a;
            float len = along.magnitude;
            if (len < 1e-3f) return;
            Vector3 fwd = along / len;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;

            float halfG = Gauge * 0.5f;
            float railCY = TieHeight + RailHeight * 0.5f; // rail centre sits on the ties

            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 off = right * (halfG * side) + up * railCY;
                Vector3 c = (a + b) * 0.5f + off;
                AddBox(_rv, _rt, c, fwd, right, up, len, RailWidth, RailHeight);
            }

            float s = Mathf.Max(0.1f, TieSpacing);
            for (float d = 0f; d <= len + 1e-3f; d += s)
            {
                float u = len > 1e-4f ? Mathf.Clamp01(d / len) : 0f;
                Vector3 p = Vector3.Lerp(a, b, u) + up * (TieHeight * 0.5f);
                AddBox(_tv, _tt, p, fwd, right, up, TieThickness, TieLength, TieHeight);
            }
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

            const int N = 24;
            const float Rr = 0.9f, lift = 0.15f;
            Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float ang = i / (float)N * Mathf.PI * 2f;
                float x = cursor.x + Mathf.Cos(ang) * Rr;
                float z = cursor.z + Mathf.Sin(ang) * Rr;
                float y = (field != null ? field.SampleHeight(x, z) : cursor.y) + lift;
                Vector3 cur = new Vector3(x, y, z);
                if (i > 0) AddSeg(prev, cur);
                prev = cur;
            }

            // Dashed pending edge: centreline + the two rail edges (previews gauge).
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count)
            {
                Vector2 tail = Graph.Nodes[_chainTail];
                Vector2 cz = new Vector2(cursor.x, cursor.z);
                Vector2 dir = cz - tail;
                float len = dir.magnitude;
                if (len > 1e-4f)
                {
                    Vector2 perp = new Vector2(-dir.y, dir.x) / len * (Gauge * 0.5f);
                    EmitDashed(field, tail, cz, Vector2.zero, lift);
                    EmitDashed(field, tail, cz, perp, lift);
                    EmitDashed(field, tail, cz, -perp, lift);
                }
            }

            _pvMesh.Clear();
            _pvMesh.SetVertices(_pvVerts);
            _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0);
            _pvMesh.RecalculateBounds();
        }

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
