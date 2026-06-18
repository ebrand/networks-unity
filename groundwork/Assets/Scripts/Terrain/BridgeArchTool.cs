// "Bridge arch" editing mode (Increment 1: selection + live preview, no build yet).
// Hover an existing bridge → its trestles highlight; click a START trestle then an END trestle; a preview arch is
// drawn from the start trestle's foot to the end trestle's foot, peaking a few metres below the deck; scroll adjusts
// the rise; Enter confirms (records a BridgeArch on the edge), Esc/right-click cancels. The actual arch geometry +
// truncating the in-between trestles is Increment 2 (in RoadBridgeBuilder).
//
// Driven entirely by TerrainDesigner.Tick each frame. Trestle stations come from RoadBridgeBuilder.Stations so the
// picks line up exactly with the real piers. Overlay = unlit coloured line mesh (markers + preview arc).

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class BridgeArchTool
    {
        public static bool Active { get; private set; }
        // True once both trestles are chosen → scroll tunes the rise (camera zoom is suppressed meanwhile).
        public static bool AdjustingRise => Active && _start >= 0 && _end >= 0;

        const float PickPx = 34f;   // screen-pixel radius to grab a trestle

        static int _edge = -1;
        static List<RoadBridgeBuilder.TrestleStation> _st = new List<RoadBridgeBuilder.TrestleStation>();
        static int _hover = -1, _start = -1, _end = -1;
        static float _rise = 5f;
        static bool _logged;
        static int _dbgEdge = -2, _dbgCount = -1;
        static bool _confirmed;
        // TerrainDesigner consumes this the frame a span is confirmed → rebuild the bridge so the arch appears.
        public static bool TakeConfirmed() { bool c = _confirmed; _confirmed = false; return c; }

        // overlay: submeshes 0=markers(grey) 1=hover(yellow) 2=start(green) 3=end(red) 4=preview arch(blue)
        static GameObject _go; static MeshRenderer _mr; static Mesh _mesh; static Material[] _mats;
        static readonly List<Vector3> _v = new List<Vector3>();
        static readonly List<int>[] _idx = { new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>() };

        public static void Enter() { Active = true; _edge = -1; _hover = _start = _end = -1; _logged = false; Debug.Log("[BridgeArch] hover a bridge, click the START trestle then the END trestle; scroll = rise, Enter = confirm, Esc = cancel."); }
        public static void Exit() { Active = false; _edge = -1; _hover = _start = _end = -1; if (_mr != null) _mr.enabled = false; }
        public static void Teardown() { Exit(); if (_go != null) Object.Destroy(_go); _go = null; _mesh = null; _mr = null; _mats = null; }

        public static void Tick(RoadPlanLayer rd, System.Func<int, float> nodeElev, ITerrainSurface field,
                                float deckDepth, float pierSpacing, Camera cam, Vector2 screen,
                                bool click, float scroll, bool confirm, bool cancel)
        {
            if (!Active) return;
            if (cancel) { Exit(); return; }
            if (rd == null || rd.Graph == null || cam == null) { Exit(); return; }
            if (!_logged) { _logged = true; int nb = 0; foreach (LineEdge e in rd.Graph.Edges) if (e.Bridge) nb++; Debug.Log($"[BridgeArch] {nb} bridge-flagged edge(s) in the plan." + (nb == 0 ? " Force-Bridge a span first (Road palette) — only bridges have trestles." : "")); }
            bool haveSpan = _start >= 0 && _end >= 0;

            if (haveSpan && Mathf.Abs(scroll) > 0.01f) _rise = Mathf.Clamp(_rise + scroll, 1f, 1000f);

            if (!haveSpan)   // screen-space pick of the nearest trestle (deck/piers have no collider, so no ground ray)
            {
                int lockEdge = _start < 0 ? -1 : _edge;
                if (PickHover(rd, nodeElev, field, deckDepth, pierSpacing, cam, screen, lockEdge, out int e, out int s, out var st))
                { _edge = e; _hover = s; _st = st; }
                else { _hover = -1; if (_start < 0) { _edge = -1; _st = new List<RoadBridgeBuilder.TrestleStation>(); } }
            }

            if (click)
            {
                if (_start < 0) { if (_hover >= 0) _start = _hover; }
                else if (_end < 0 && _hover >= 0 && _hover != _start)
                {
                    _end = _hover;
                    if (_end < _start) { int t = _start; _start = _end; _end = t; }
                    var a = _st[_start]; var b = _st[_end];
                    float midDeck = (a.DeckTop + b.DeckTop) * 0.5f, midBase = (a.Ground + b.Ground) * 0.5f;
                    _rise = Mathf.Max(1f, (midDeck - 3f) - midBase);   // default peak ~3 m below the deck
                }
            }

            if (confirm && haveSpan && _edge >= 0 && _edge < rd.Graph.Edges.Count)
            {
                LineEdge e = rd.Graph.Edges[_edge];
                if (e.Arches == null) e.Arches = new List<BridgeArch>();
                e.Arches.Add(new BridgeArch { StartArc = _st[_start].Arc, EndArc = _st[_end].Arc, Rise = _rise });
                Debug.Log($"[BridgeArch] built on edge {_edge}: {_st[_start].Arc:0}→{_st[_end].Arc:0} m, rise {_rise:0} m.");
                _confirmed = true;
                Exit();
                return;
            }

            if (_edge != _dbgEdge || _st.Count != _dbgCount) { _dbgEdge = _edge; _dbgCount = _st.Count; Debug.Log($"[BridgeArch] hover edge {_edge}: {_st.Count} trestle(s), hover #{_hover}."); }
            Rebuild();
        }

        // Resolve the hovered bridge + trestle by SCREEN projection (deck/piers have no collider). First the EDGE,
        // by nearest projected DECK CENTERLINE (so the markers show whenever you're over the bridge), then the nearest
        // trestle on it, by its projected FOOT→DECK segment (so hovering the deck near a pier picks it). When lockEdge
        // >= 0 (end-pick), the edge is fixed. Returns true with `stations` populated whenever an edge is hovered;
        // `station` may be -1 if the cursor isn't near a specific pier.
        static bool PickHover(RoadPlanLayer rd, System.Func<int, float> nodeElev, ITerrainSurface field,
                              float deckDepth, float pierSpacing, Camera cam, Vector2 screen, int lockEdge,
                              out int edge, out int station, out List<RoadBridgeBuilder.TrestleStation> stations)
        {
            edge = lockEdge; station = -1; stations = null;
            if (lockEdge < 0)
            {
                float best = 60f * 60f;   // px² to the deck centerline
                for (int ei = 0; ei < rd.Graph.Edges.Count; ei++)
                {
                    LineEdge e = rd.Graph.Edges[ei];
                    if (!e.Bridge) continue;
                    rd.EdgeBezierWorld(ei, out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d);
                    float yA = nodeElev != null ? nodeElev(e.A) : 0f, yB = nodeElev != null ? nodeElev(e.B) : 0f;
                    float dmin = float.MaxValue;
                    for (int k = 0; k <= 24; k++)
                    {
                        float u = k / 24f; Vector2 xz = LineGraph.Bezier(a, b, c, d, u);
                        Vector3 sp = cam.WorldToScreenPoint(new Vector3(xz.x, Mathf.Lerp(yA, yB, u) - 0.1f, xz.y));
                        if (sp.z <= 0f) continue;
                        dmin = Mathf.Min(dmin, (screen - new Vector2(sp.x, sp.y)).sqrMagnitude);
                    }
                    if (dmin < best) { best = dmin; edge = ei; }
                }
            }
            if (edge < 0 || edge >= rd.Graph.Edges.Count) return false;

            stations = RoadBridgeBuilder.Stations(rd, edge, nodeElev, field, deckDepth, pierSpacing);
            float bestSt = PickPx * PickPx;
            for (int k = 0; k < stations.Count; k++)
            {
                var t = stations[k];
                Vector3 sf = cam.WorldToScreenPoint(new Vector3(t.Xz.x, t.Ground, t.Xz.y));
                Vector3 sd = cam.WorldToScreenPoint(new Vector3(t.Xz.x, t.DeckTop, t.Xz.y));
                if (sd.z <= 0f) continue;
                float d2 = sf.z > 0f ? DistSegSq(screen, new Vector2(sf.x, sf.y), new Vector2(sd.x, sd.y))
                                     : (screen - new Vector2(sd.x, sd.y)).sqrMagnitude;
                if (d2 < bestSt) { bestSt = d2; station = k; }
            }
            return true;
        }

        static float DistSegSq(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a; float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * t)).sqrMagnitude;
        }

        static void Rebuild()
        {
            Ensure();
            _v.Clear(); foreach (var l in _idx) l.Clear();
            for (int i = 0; i < _st.Count; i++)   // a solid vertical bar per trestle, foot → deck
            {
                var s = _st[i];
                int sm = (i == _start) ? 2 : (i == _end) ? 3 : (i == _hover) ? 1 : 0;
                AddBar(sm, new Vector3(s.Xz.x, s.Ground, s.Xz.y), new Vector3(s.Xz.x, s.DeckTop, s.Xz.y), 1.4f);
            }
            if (_start >= 0 && _end >= 0 && _start < _st.Count && _end < _st.Count)   // preview arch (solid tube)
            {
                var a = _st[_start]; var b = _st[_end];
                Vector3 prev = default; bool hp = false;
                for (int k = 0; k <= 48; k++)
                {
                    float t = k / 48f;
                    Vector2 xz = Vector2.Lerp(a.Xz, b.Xz, t);
                    float y = Mathf.Lerp(a.Ground, b.Ground, t) + _rise * 4f * t * (1f - t);
                    Vector3 cur = new Vector3(xz.x, y, xz.y);
                    if (hp) AddBar(4, prev, cur, 1.2f);
                    prev = cur; hp = true;
                }
            }
            _mesh.Clear();
            _mesh.SetVertices(_v);
            _mesh.subMeshCount = 5;
            for (int s = 0; s < 5; s++) _mesh.SetTriangles(_idx[s], s);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _mr.enabled = _v.Count > 0;
        }

        // A solid square-section bar from p0→p1 added to submesh `sm` (8 verts, 6 faces; material is double-sided).
        static void AddBar(int sm, Vector3 p0, Vector3 p1, float hw)
        {
            Vector3 dir = p1 - p0; float len = dir.magnitude;
            if (len < 1e-4f) return;
            dir /= len;
            Vector3 up = Mathf.Abs(dir.y) > 0.9f ? Vector3.forward : Vector3.up;
            Vector3 r = Vector3.Cross(dir, up).normalized * hw, u = Vector3.Cross(r, dir).normalized * hw;
            int s = _v.Count;
            _v.Add(p0 - r - u); _v.Add(p0 + r - u); _v.Add(p0 + r + u); _v.Add(p0 - r + u);
            _v.Add(p1 - r - u); _v.Add(p1 + r - u); _v.Add(p1 + r + u); _v.Add(p1 - r + u);
            int[] q = { 0, 1, 5, 4, 1, 2, 6, 5, 2, 3, 7, 6, 3, 0, 4, 7, 3, 2, 1, 0, 4, 5, 6, 7 };
            var idx = _idx[sm];
            for (int f = 0; f < 6; f++)
            {
                int a = s + q[f * 4], b = s + q[f * 4 + 1], c = s + q[f * 4 + 2], d = s + q[f * 4 + 3];
                idx.Add(a); idx.Add(b); idx.Add(c); idx.Add(a); idx.Add(c); idx.Add(d);
            }
        }

        static void Ensure()
        {
            if (_go != null) { _mr.enabled = true; return; }
            _go = new GameObject("BridgeArchTool") { hideFlags = HideFlags.DontSave };
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh = new Mesh { name = "BridgeArchOverlay" };
            _mr = _go.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _mr.receiveShadows = false;
            _mats = new[]
            {
                Unlit(new Color(0.70f, 0.70f, 0.72f, 1f), "BArchMark"),
                Unlit(new Color(1f, 0.90f, 0.10f, 1f), "BArchHover"),
                Unlit(new Color(0.20f, 1f, 0.35f, 1f), "BArchStart"),
                Unlit(new Color(1f, 0.30f, 0.20f, 1f), "BArchEnd"),
                Unlit(new Color(0.20f, 0.55f, 1f, 1f), "BArchArc"),
            };
            _mr.sharedMaterials = _mats;
        }

        static Material Unlit(Color c, string name)
        {
            var m = NetworkDesigner.PipelineMaterials.CreateUnlitColor(c, name);
            if (m.HasProperty("_Cull")) m.SetInt("_Cull", 0);   // double-sided → bar winding doesn't matter
            return m;
        }
    }
}
