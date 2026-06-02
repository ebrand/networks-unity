// One linework type (fence, power line, pipeline, …): a node/edge bezier graph
// drawn on the terrain, with a prefab rendered IN SERIES along each edge —
// posts at every node plus evenly-spaced instances along the curve, oriented to
// the tangent and conformed to the terrain surface. Same spirit as ScatterLayer
// (config + runtime root + save/restore), so new line types are just another
// instance on TerrainDesigner.
//
// Phase 1: draw chains (click to add a node + connect from the last; right-click
// ends the chain). Edges auto-smooth into bezier s-curves through the nodes.
// Draggable per-node handles + node editing are the next increment.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class LineworkLayer
    {
        [Tooltip("Display name + GameObject root name + hotkey label (e.g. Fence).")]
        public string Name = "Line";
        [Tooltip("Prefab rendered in series along each edge (e.g. a fence section).")]
        public GameObject Asset;
        [Tooltip("Metres between instances along the curve (posts also placed at every node).")]
        public float Spacing = 3f;
        [Tooltip("Metres raised above the terrain surface (0 = sit on the ground).")]
        public float VerticalOffset = 0f;
        [Tooltip("Conform instances to the terrain height. Off = flat at VerticalOffset.")]
        public bool Conform = true;
        [Tooltip("Degrees added to each instance's yaw — correct for an asset that " +
                 "doesn't face along +Z by default.")]
        public float YawOffset = 0f;
        [Tooltip("Uniform scale applied to each instance.")]
        public float Scale = 1f;
        [Tooltip("Straight edges with hard corners (recommended). Off = auto-smoothed " +
                 "bezier curves through the nodes.")]
        public bool Straight = true;
        [Tooltip("Optional: parent placed instances under this Transform instead of an " +
                 "auto-created root. Point it at a PoleChain that carries a cable " +
                 "generator (e.g. ElectricCables) and the poles you place auto-connect.")]
        public Transform ParentOverride;

        // ---- runtime (not serialized) ----
        LineGraph _graph = new LineGraph();
        GameObject _root;
        readonly List<GameObject> _instances = new List<GameObject>();
        int _chainTail = -1; // node index the next click connects from (-1 = new chain)

        // Placement preview (ghost puck at the cursor + dashed pending edge).
        GameObject _pvGo;
        MeshFilter _pvMf;
        MeshRenderer _pvMr;
        Mesh _pvMesh;
        Material _pvMat;
        readonly List<Vector3> _pvVerts = new List<Vector3>();
        readonly List<int> _pvIdx = new List<int>();

        public LineGraph Graph => _graph ??= new LineGraph();
        string RootName => "TerrainLine_" + Name;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        void EnsureRoot()
        {
            if (_root != null) return;
            GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == RootName) DestroySafe(all[i]);
            _root = new GameObject(RootName);
        }

        // Where placed instances are parented: the override (e.g. a PoleChain) if
        // set, else the layer's own auto-managed root.
        Transform ParentTransform()
        {
            if (ParentOverride != null) return ParentOverride;
            EnsureRoot();
            return _root.transform;
        }

        // ---- editing ----

        // Add a node at a world hit; connect from the chain tail (chain drawing).
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

        // Remove the most-recently-added node + its edges (simple undo).
        public void RemoveLastNode(TerrainField field)
        {
            int last = Graph.Nodes.Count - 1;
            if (last < 0) return;
            Graph.Edges.RemoveAll(e => e.A == last || e.B == last);
            Graph.Nodes.RemoveAt(last);
            if (_chainTail >= Graph.Nodes.Count) _chainTail = -1;
            Rebuild(field);
        }

        // ---- rendering ----

        public void Rebuild(TerrainField field)
        {
            // With a parent override (cable generator), destroy IMMEDIATELY so the
            // generator doesn't see the old + new poles together when we poke it
            // this frame. Plain fences can use deferred destroy.
            bool immediate = ParentOverride != null;
            for (int i = 0; i < _instances.Count; i++)
            {
                GameObject g = _instances[i];
                if (g == null) continue;
                if (immediate) UnityEngine.Object.DestroyImmediate(g);
                else DestroySafe(g);
            }
            _instances.Clear();
            if (Asset != null && Graph.Edges.Count > 0)
            {
                float s = Mathf.Max(0.25f, Spacing);
                var postedNodes = new HashSet<int>();
                foreach (LineEdge e in Graph.Edges)
                {
                    Vector2 p0, p1, p2, p3;
                    if (Straight)
                    {
                        // Straight bezier (control points on the line) → hard corners.
                        p0 = Graph.Nodes[e.A]; p3 = Graph.Nodes[e.B];
                        Vector2 d = p3 - p0;
                        p1 = p0 + d / 3f; p2 = p0 + d * (2f / 3f);
                    }
                    else
                    {
                        Graph.EdgeControls(e, out p0, out p1, out p2, out p3);
                    }
                    PlaceAlongEdge(field, e, p0, p1, p2, p3, s, postedNodes);
                }
            }
            // If parented under an external chain (e.g. a cable generator), poke
            // it to regenerate — its own auto-rebuild is edit-mode only, so
            // runtime-placed poles wouldn't cable otherwise. No hard dependency.
            if (ParentOverride != null)
                ParentOverride.SendMessage("RebuildCables", SendMessageOptions.DontRequireReceiver);
        }

        // Resample the edge's bezier by arc length, emitting an instance every
        // `s` (plus a deduped post at each shared node A/B).
        static readonly Vector2[] _pts = new Vector2[SubSteps + 1];
        static readonly float[] _cum = new float[SubSteps + 1];
        const int SubSteps = 48;

        void PlaceAlongEdge(TerrainField field, LineEdge e,
                            Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
                            float s, HashSet<int> postedNodes)
        {
            _pts[0] = p0; _cum[0] = 0f;
            for (int i = 1; i <= SubSteps; i++)
            {
                _pts[i] = LineGraph.Bezier(p0, p1, p2, p3, i / (float)SubSteps);
                _cum[i] = _cum[i - 1] + Vector2.Distance(_pts[i - 1], _pts[i]);
            }
            float len = _cum[SubSteps];
            if (len < 1e-4f) return;

            for (float d = 0f; d <= len + 1e-4f; d += s)
            {
                float dd = Mathf.Min(d, len);
                int seg = 1;
                while (seg < SubSteps && _cum[seg] < dd) seg++;
                float segLen = _cum[seg] - _cum[seg - 1];
                float f = segLen > 1e-5f ? (dd - _cum[seg - 1]) / segLen : 0f;
                Vector2 pos = Vector2.Lerp(_pts[seg - 1], _pts[seg], f);
                Vector2 tan = LineGraph.BezierTangent(p0, p1, p2, p3, (seg - 1 + f) / SubSteps);

                bool atA = dd <= 1e-4f;
                bool atB = dd >= len - 1e-4f;
                if (atA && !postedNodes.Add(e.A)) continue;     // node A already posted
                if (atB && !atA && !postedNodes.Add(e.B)) continue;
                Spawn(field, pos, tan);
            }
            // The d-loop may stop short of len; guarantee a post at node B.
            if (postedNodes.Add(e.B))
                Spawn(field, p3, LineGraph.BezierTangent(p0, p1, p2, p3, 1f));
        }

        void Spawn(TerrainField field, Vector2 xz, Vector2 tangent)
        {
            float y = (Conform && field != null ? field.SampleHeight(xz.x, xz.y) : 0f) + VerticalOffset;
            float yaw = (tangent.sqrMagnitude > 1e-6f
                ? Mathf.Atan2(tangent.x, tangent.y) * Mathf.Rad2Deg : 0f) + YawOffset;
            GameObject go = UnityEngine.Object.Instantiate(Asset, new Vector3(xz.x, y, xz.y),
                Quaternion.Euler(0f, yaw, 0f), ParentTransform());
            if (Scale > 0f && !Mathf.Approximately(Scale, 1f)) go.transform.localScale *= Scale;
            Collider[] cols = go.GetComponentsInChildren<Collider>();
            for (int c = 0; c < cols.Length; c++) DestroySafe(cols[c]);
            _instances.Add(go);
        }

        // Delete the nearest node (and its edges) within `radius` of a world hit.
        // Returns true if one was removed.
        public bool DeleteNearNode(TerrainField field, Vector3 hit, float radius)
        {
            int n = Graph.NearestNode(new Vector2(hit.x, hit.z), radius);
            if (n < 0) return false;
            Graph.RemoveNode(n);
            if (_chainTail == n) _chainTail = -1;
            else if (_chainTail > n) _chainTail--;
            Rebuild(field);
            return true;
        }

        // ---- placement preview ----

        public void HidePreview() { if (_pvMr != null) _pvMr.enabled = false; }

        // Ghost puck at the cursor + a dashed line for the pending edge.
        public void UpdatePreview(TerrainField field, Vector3 cursor, bool show)
        {
            EnsurePreview();
            _pvMr.enabled = show;
            if (!show) return;
            _pvVerts.Clear();
            _pvIdx.Clear();

            const int N = 24;
            const float R = 0.9f, lift = 0.1f;
            Vector3 prev = default;
            for (int i = 0; i <= N; i++)
            {
                float ang = i / (float)N * Mathf.PI * 2f;
                float x = cursor.x + Mathf.Cos(ang) * R;
                float z = cursor.z + Mathf.Sin(ang) * R;
                float y = (field != null ? field.SampleHeight(x, z) : cursor.y) + lift;
                Vector3 cur = new Vector3(x, y, z);
                if (i > 0) AddSeg(prev, cur);
                prev = cur;
            }

            // Dashed pending edge from the chain tail to the cursor.
            if (_chainTail >= 0 && _chainTail < Graph.Nodes.Count)
            {
                Vector2 t = Graph.Nodes[_chainTail];
                Vector3 a = Conformed(field, t.x, t.y, lift);
                Vector3 b = Conformed(field, cursor.x, cursor.z, lift);
                EmitDashed(field, a, b, 1.0f, 0.6f, lift);
            }

            _pvMesh.Clear();
            _pvMesh.SetVertices(_pvVerts);
            _pvMesh.SetIndices(_pvIdx, MeshTopology.Lines, 0);
            _pvMesh.RecalculateBounds();
        }

        static Vector3 Conformed(TerrainField field, float x, float z, float lift)
            => new Vector3(x, (field != null ? field.SampleHeight(x, z) : 0f) + lift, z);

        void AddSeg(Vector3 a, Vector3 b)
        {
            int s = _pvVerts.Count;
            _pvVerts.Add(a); _pvVerts.Add(b);
            _pvIdx.Add(s); _pvIdx.Add(s + 1);
        }

        void EmitDashed(TerrainField field, Vector3 a, Vector3 b, float dash, float gap, float lift)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            Vector3 dir = d / len;
            float period = dash + gap;
            for (float pos = 0f; pos < len; pos += period)
            {
                float e0 = pos, e1 = Mathf.Min(pos + dash, len);
                Vector3 p0 = a + dir * e0, p1 = a + dir * e1;
                if (field != null)
                {
                    p0.y = field.SampleHeight(p0.x, p0.z) + lift;
                    p1.y = field.SampleHeight(p1.x, p1.z) + lift;
                }
                AddSeg(p0, p1);
            }
        }

        void EnsurePreview()
        {
            if (_pvMf != null) return;
            _pvGo = new GameObject(RootName + "_Preview");
            _pvMf = _pvGo.AddComponent<MeshFilter>();
            _pvMr = _pvGo.AddComponent<MeshRenderer>();
            _pvMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _pvMr.receiveShadows = false;
            _pvMesh = new Mesh { name = "LinePreviewMesh" };
            _pvMf.sharedMesh = _pvMesh;
            Shader sh = Shader.Find("NetworkDesigner/CursorOverlay");
            Color col = new Color(0.35f, 0.9f, 1f, 1f);
            _pvMat = sh != null
                ? new Material(sh) { name = "LinePreviewMat", color = col }
                : NetworkDesigner.PipelineMaterials.CreateUnlitColor(col, "LinePreviewMat");
            _pvMr.sharedMaterial = _pvMat;
        }

        // ---- save / load ----

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
