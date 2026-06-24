using System;
using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Geometry;   // GeometryResolver (cubic bezier sampling + radius checks)

namespace NetworkDesigner.Roads
{
    // Phase 1/2: a persistent lane-edge world (network + live render) with a minimal 2-click straight-corridor draw.
    // Active behind LaneEdgeModel.Enabled. Builds clusters + lane-edges + bands from the active profile and renders via
    // LaneEdgeCorridorBuilder. Divergence (ramps/splits) and lane-level ops come in later phases.
    public static class LaneEdgeWorld
    {
        public static readonly LaneEdgeNetwork Net = new LaneEdgeNetwork();
        static GameObject _root;
        static int _drawStart = -1;                 // first-click cluster while drawing a corridor
        static Vector2 _corner;                      // armed bend corner of a shift-curve (between start and end)
        static bool _cornerPending;                  // a bend is armed; the next click is the curve's end
        public static bool Drawing => _drawStart >= 0;
        public static bool CornerPending => _cornerPending;
        public static Vector2 CornerPos => _corner;
        public static Vector2 DrawStartPos => _drawStart >= 0 ? Net.Nodes[_drawStart] : Vector2.zero;

        static GameObject Root() => _root != null ? _root : (_root = new GameObject("LaneEdgeWorld"));

        // ── persistence (JSON via JsonUtility; stored as one string in the binary autosave) ──
        [System.Serializable]
        public class NetSave
        {
            public List<Vector2> Nodes = new List<Vector2>();
            public List<float> NodeY = new List<float>();
            public List<LaneEdge> Edges = new List<LaneEdge>();
            public List<Corridor> Corridors = new List<Corridor>();
            public List<LaneFlow> Flows = new List<LaneFlow>();
        }

        public static string ToJson()
        {
            if (Net.Edges.Count == 0 && Net.Corridors.Count == 0) return "";
            return JsonUtility.ToJson(new NetSave
            {
                Nodes = new List<Vector2>(Net.Nodes),
                NodeY = new List<float>(Net.NodeY),
                Edges = new List<LaneEdge>(Net.Edges),
                Corridors = new List<Corridor>(Net.Corridors),
                Flows = new List<LaneFlow>(Net.Flows),
            });
        }

        // Restore the network data (no render — caller rebuilds once the terrain is ready).
        public static void LoadData(string json)
        {
            if (string.IsNullOrEmpty(json)) { Net.Clear(); return; }
            NetSave s = JsonUtility.FromJson<NetSave>(json);
            if (s == null) { Net.Clear(); return; }
            Net.LoadFrom(s.Nodes, s.NodeY, s.Edges, s.Corridors, s.Flows);
        }

        public static bool HasData => Net.Corridors.Count > 0 || Net.Edges.Count > 0;

        // Render every corridor, draped via groundAt. Rebuilds the whole render root (simple; optimise later).
        public struct LaneEndpoint { public Vector2 Pos; public Vector2 NodePos; public float Y; public int Edge; public int Node; public bool Incoming; }
        public static readonly List<LaneEndpoint> Endpoints = new List<LaneEndpoint>();   // rebuilt each Rebuild; for picking + flow render

        public static void Rebuild(Func<Vector2, float> groundAt)
        {
            ComputeEndpoints(groundAt);
            DetectLaneDropTapers();   // derive lane-drop tapers from lane-count mismatches at shared nodes
            GameObject root = Root();
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(root.transform.GetChild(i).gameObject);
            foreach (Corridor c in Net.Corridors)
            {
                if (c.Built) LaneEdgeCorridorBuilder.RenderCorridor(Net, c, root.transform, groundAt);   // real swept body
                else RenderPlanOverlay(c, root.transform, groundAt);                                     // schematic plan line
            }
            // The segment (cluster) node + flow connectors are intersection-routing visuals — only show them while mapping
            // flows. In plain draw mode the lanes + endpoint pucks are the handles; the centreline node is internal.
            if (LaneEdgeModel.MappingMode)
            {
                RenderNodes(root.transform, groundAt);
                RenderFlows(root.transform);
            }
            RenderEndpointSpheres(root.transform);
        }

        static readonly Color PlanCol = new Color(0.9f, 0.35f, 0.2f, 1f), ExcCol = new Color(0.95f, 0.85f, 0.2f, 1f);
        static Material _plannedMat, _excavatedMat;
        static Material PlannedMat() => _plannedMat != null ? _plannedMat : (_plannedMat = NetworkDesigner.PipelineMaterials.CreateUnlitColor(PlanCol, "LanePlanLine"));
        static Material ExcavatedMat() => _excavatedMat != null ? _excavatedMat : (_excavatedMat = NetworkDesigner.PipelineMaterials.CreateUnlitColor(ExcCol, "LaneExcavLine"));

        // Plan/lane line styling (m), per the reference: OUTSIDE edges = small dashes, lane lines = large dashes,
        // the A→B / B→A divider (direction flip) = a double solid line. Drawn red (planned) / yellow (excavated).
        const float LineHalfW = 0.16f, LineLift = 0.12f;
        const float OuterDash = 0.6f, OuterGap = 0.5f;     // shoulder outside lines → small dashes
        const float LaneDash = 3.0f, LaneGap = 2.5f;       // lane lines → long dashes (match the real lane markings 3 / 2.5)
        const float DblSep = 0.3f;                          // half-separation of the double centre line

        // Reusable buffers (avoid per-frame GC in the live preview).
        static readonly List<(float off, float w, int dir)> _laneBuf = new List<(float, float, int)>();
        static readonly List<(float off, float dash, float gap)> _guideBuf = new List<(float, float, float)>();

        // Lane set (offset, width, direction) of a corridor or a profile, + outer shoulder widths — the input to the line styling.
        static void CorridorLanes(Corridor c, out float shBA, out float shAB)
        {
            _laneBuf.Clear(); shBA = c.ShoulderBA; shAB = c.ShoulderAB;
            if (ShoulderSuppressed(c, -1f)) shBA = 0f;   // a taper owns this shoulder → drawn along the wedge instead
            if (ShoulderSuppressed(c, 1f)) shAB = 0f;
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= Net.Edges.Count) continue;
                LaneEdge e = Net.Edges[li];
                if (e.Kind == LaneKind.Sidewalk) continue;
                if (LaneIsTapered(c, li)) continue;   // rendered as a taper wedge, not a uniform lane
                _laneBuf.Add((e.Offset, e.Width, e.Direction));
            }
        }

        static void ProfileLanes(string profileId, out float shBA, out float shAB)
        {
            _laneBuf.Clear(); shBA = 0f; shAB = 0f;
            var cfg = RoadProfileLibrary.ResolveConfig(profileId);
            if (cfg == null || cfg.Corridor == null) return;
            var bands = new List<RoadCrossSectionBuilder.StackBand>();
            RoadCrossSectionBuilder.FromStack(cfg.Corridor, bands);
            float total = 0f; foreach (var bd in bands) total = Mathf.Max(total, bd.U1);
            float center = total * 0.5f;
            foreach (var bd in bands)
            {
                float w = bd.U1 - bd.U0, off = (bd.U0 + bd.U1) * 0.5f - center;
                if (bd.Type == Model.CorridorType.Traffic || bd.Type == Model.CorridorType.Turn || bd.Type == Model.CorridorType.Bike)
                    _laneBuf.Add((off, w, bd.Zone == 0 ? 0 : 2));
                else if (bd.Type == Model.CorridorType.Shoulder) { if (off < 0f) shBA = w; else shAB = w; }
            }
        }

        // Turn the lane set into styled guide lines (offset, dash, gap): small-dash outer edges, large-dash same-direction
        // lane dividers, and a DOUBLE solid line at any direction flip (the A→B/B→A divider).
        static float _shBA, _shAB;   // lane-set shoulder widths, set before CollectGuideLines
        static void CollectGuideLines()
        {
            _guideBuf.Clear();
            if (_laneBuf.Count == 0) return;
            _laneBuf.Sort((a, b) => a.off.CompareTo(b.off));
            float laneLeft = _laneBuf[0].off - _laneBuf[0].w * 0.5f;                                  // outermost lane edges → SOLID
            float laneRight = _laneBuf[_laneBuf.Count - 1].off + _laneBuf[_laneBuf.Count - 1].w * 0.5f;
            _guideBuf.Add((laneLeft, 0f, 0f));
            _guideBuf.Add((laneRight, 0f, 0f));
            if (_shBA > 0.01f) _guideBuf.Add((laneLeft - _shBA, OuterDash, OuterGap));                // shoulder outside → small dashes
            if (_shAB > 0.01f) _guideBuf.Add((laneRight + _shAB, OuterDash, OuterGap));
            for (int i = 0; i < _laneBuf.Count - 1; i++)
            {
                float boundary = (_laneBuf[i].off + _laneBuf[i].w * 0.5f + _laneBuf[i + 1].off - _laneBuf[i + 1].w * 0.5f) * 0.5f;
                if (_laneBuf[i].dir != _laneBuf[i + 1].dir)
                { _guideBuf.Add((boundary - DblSep, 0f, 0f)); _guideBuf.Add((boundary + DblSep, 0f, 0f)); }   // double solid divider
                else _guideBuf.Add((boundary, LaneDash, LaneGap));   // large-dash lane line
            }
        }

        // Committed un-built corridor: draped styled guide-line mesh (red planned / yellow excavated), matching the live preview.
        static void RenderPlanOverlay(Corridor c, Transform parent, Func<Vector2, float> groundAt)
        {
            if (c.Lanes.Count == 0) return;
            float pathLen = LaneEdgeCorridorBuilder.PathLength(Net, c);
            if (pathLen < 1e-2f) return;
            CorridorLanes(c, out _shBA, out _shAB);
            CollectGuideLines();
            bool hasTaper = c.Tapers != null && c.Tapers.Count > 0;
            if (_guideBuf.Count == 0 && !hasTaper) return;   // still render even if every lane is a taper wedge

            int frames = Mathf.Clamp(Mathf.CeilToInt(pathLen / 1.5f) + 1, 2, 1024);
            var cp = new Vector2[frames]; var rg = new Vector2[frames];
            for (int f = 0; f < frames; f++)
            {
                float t = (float)f / (frames - 1);
                cp[f] = LaneEdgeCorridorBuilder.PathPoint(Net, c, t);
                Vector2 tan = LaneEdgeCorridorBuilder.PathTangent(Net, c, t);
                rg[f] = new Vector2(tan.y, -tan.x);
            }

            var verts = new List<Vector3>(); var tris = new List<int>();
            BuildStyledGuides(verts, tris, cp, rg, groundAt);
            BuildPlanArrows(c, pathLen, verts, tris, cp, rg, groundAt);   // travel triangles on one-way plans (~every 200 m)
            if (c.Tapers != null)
                foreach (var tp in c.Tapers) AppendTaperMesh(c, tp, verts, tris, groundAt);   // paved lane-drop wedge
            if (verts.Count == 0) return;

            var mesh = new Mesh { name = $"LanePlanGuides_{c.Id}" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.RecalculateBounds();
            var go = new GameObject($"LanePlanGuides_{c.Id}"); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = c.Excavated ? ExcavatedMat() : PlannedMat();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        // ── lane-drop taper wedge ── the dropped lane's slot opens from ZERO width at the junction, S-curves up to full
        // lane width over `Length`, then stays full to the far corridor end (the lane grows into the larger segment). The
        // inner edge (toward the surviving lanes) is straight; the outer edge is the S-curve. AtA = junction at the A end.
        static readonly List<Vector2> _twIn = new List<Vector2>(), _twOut = new List<Vector2>(), _twSh = new List<Vector2>();
        static void TaperCross(Corridor c, float t, float innerOff, float outerOff, float shoulderOff, bool wantSh,
                               List<Vector2> inner, List<Vector2> outer, List<Vector2> shoulder)
        {
            Vector2 p = LaneEdgeCorridorBuilder.PathPoint(Net, c, t);
            Vector2 fr = LaneEdgeCorridorBuilder.PathRight(LaneEdgeCorridorBuilder.PathTangent(Net, c, t));
            inner.Add(p + fr * innerOff); outer.Add(p + fr * outerOff);
            if (wantSh) shoulder.Add(p + fr * shoulderOff);
        }
        // Samples the taper wedge edges. When `shoulder` > 0, also fills `shOut` with the outer SHOULDER edge (it follows
        // the wedge's outer S-curve, offset further out by the shoulder width).
        public static void TaperWedge(Corridor c, LaneDropTaper tp, int M, List<Vector2> inner, List<Vector2> outer,
                                      float shoulder = 0f, List<Vector2> shOut = null)
        {
            inner.Clear(); outer.Clear(); shOut?.Clear();
            float pathLen = LaneEdgeCorridorBuilder.PathLength(Net, c);
            if (pathLen < 1e-2f || M < 1) return;
            float frac = Mathf.Clamp01(tp.Length / pathLen);          // taper run as a fraction of the path
            float sgn = tp.Offset >= 0f ? 1f : -1f;
            float innerOff = tp.Offset - sgn * tp.Width * 0.5f;       // straight edge toward the surviving lanes
            bool wantSh = shoulder > 0.01f && shOut != null;
            for (int k = 0; k <= M; k++)                              // S-curve region: zero at junction → full over `frac`
            {
                float a = k / (float)M;                              // 0 at the junction → 1 at the end of the taper
                float u = a * frac;                                  // distance fraction from the junction
                float t = tp.AtA ? u : (1f - u);                     // junction at A → grow toward B; else toward A
                float e = a * a * (3f - 2f * a);                     // smoothstep → S-curve outer edge
                float outerOff = innerOff + sgn * tp.Width * e;
                TaperCross(c, t, innerOff, outerOff, outerOff + sgn * shoulder, wantSh, inner, outer, shOut);
            }
            if (frac < 0.999f)                                       // full-width tail from the taper end to the far end
            {
                float outerOff = innerOff + sgn * tp.Width;
                TaperCross(c, tp.AtA ? 1f : 0f, innerOff, outerOff, outerOff + sgn * shoulder, wantSh, inner, outer, shOut);
            }
        }

        // Draped OUTLINE of the taper wedge into the plan-overlay mesh: thin ribbons along the outer S-curve edge, the
        // inner (straight) edge, and the end cap at the full-width end. The tip end is a point (outer==inner), so no cap.
        static void AppendTaperMesh(Corridor c, LaneDropTaper tp, List<Vector3> verts, List<int> tris, Func<Vector2, float> groundAt)
        {
            const int M = 14;
            float sh = TaperOuterShoulder(c, tp);
            TaperWedge(c, tp, M, _twIn, _twOut, sh, _twSh);
            int n = _twIn.Count;
            if (n < 2) return;
            EmitDashedPolyline(_twOut, LaneDash, LaneGap, verts, tris, groundAt);     // outer S-curve edge (lane-line dash)
            EmitDashedPolyline(_twIn, LaneDash, LaneGap, verts, tris, groundAt);      // inner straight edge
            EmitLineSeg(_twIn[n - 1], _twOut[n - 1], verts, tris, groundAt);          // full-width end cap (solid)
            if (sh > 0.01f && _twSh.Count == n)                                       // shoulder edge following the taper
            {
                EmitDashedPolyline(_twSh, OuterDash, OuterGap, verts, tris, groundAt);
                EmitLineSeg(_twOut[n - 1], _twSh[n - 1], verts, tris, groundAt);      // shoulder end cap (solid)
            }
        }

        // Dashed version of EmitPolyline: walks the polyline by arc length, emitting only the dash-on portions.
        static void EmitDashedPolyline(List<Vector2> pts, float dash, float gap, List<Vector3> verts, List<int> tris, Func<Vector2, float> groundAt)
        {
            float period = dash + gap; if (period < 0.01f) { EmitPolyline(pts, verts, tris, groundAt); return; }
            float walked = 0f;
            for (int k = 0; k < pts.Count - 1; k++)
            {
                Vector2 a = pts[k], b = pts[k + 1];
                Vector2 seg = b - a; float segLen = seg.magnitude; if (segLen < 1e-4f) continue;
                Vector2 dir = seg / segLen; float pos = 0f;
                while (pos < segLen)
                {
                    float phase = walked % period;
                    if (phase < dash)
                    {
                        float piece = Mathf.Min(dash - phase, segLen - pos);
                        EmitLineSeg(a + dir * pos, a + dir * (pos + piece), verts, tris, groundAt);
                        pos += piece; walked += piece;
                    }
                    else { float piece = Mathf.Min(period - phase, segLen - pos); pos += piece; walked += piece; }
                }
            }
        }

        // A thin, draped, double-sided line ribbon for one segment (LineHalfW wide) — the plan-line primitive used for the
        // taper outline.
        static void EmitLineSeg(Vector2 a, Vector2 b, List<Vector3> verts, List<int> tris, Func<Vector2, float> groundAt)
        {
            Vector2 dir = b - a; if (dir.sqrMagnitude < 1e-8f) return; dir.Normalize();
            Vector2 nrm = new Vector2(-dir.y, dir.x) * LineHalfW;
            int s = verts.Count;
            verts.Add(Drape(a - nrm, groundAt)); verts.Add(Drape(a + nrm, groundAt));
            verts.Add(Drape(b + nrm, groundAt)); verts.Add(Drape(b - nrm, groundAt));
            tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
            tris.Add(s); tris.Add(s + 2); tris.Add(s + 1); tris.Add(s); tris.Add(s + 3); tris.Add(s + 2);   // 2-sided
        }

        static void EmitPolyline(List<Vector2> pts, List<Vector3> verts, List<int> tris, Func<Vector2, float> groundAt)
        {
            for (int k = 0; k < pts.Count - 1; k++) EmitLineSeg(pts[k], pts[k + 1], verts, tris, groundAt);
        }

        // Emit the current _guideBuf lines as a draped thin-quad mesh along precomputed frames cp/rg.
        static void BuildStyledGuides(List<Vector3> verts, List<int> tris, Vector2[] cp, Vector2[] rg, Func<Vector2, float> groundAt)
        {
            foreach (var ln in _guideBuf) EmitGuideLine(verts, tris, cp, rg, ln.off, ln.dash, ln.gap, groundAt);
        }

        // Emit a draped thin-quad strip at lateral `off` along the precomputed frames; dashed if dash>0 (else solid).
        static void EmitGuideLine(List<Vector3> verts, List<int> tris, Vector2[] cp, Vector2[] rg, float off, float dash, float gap, Func<Vector2, float> groundAt)
        {
            bool dashed = dash > 0.01f; float period = dash + gap; float walked = 0f;
            for (int f = 0; f < cp.Length - 1; f++)
            {
                Vector2 a = cp[f] + rg[f] * off, b = cp[f + 1] + rg[f + 1] * off;
                float segLen = (b - a).magnitude;
                bool on = !dashed || (walked % period) < dash;
                walked += segLen;
                if (!on) continue;
                Vector2 fa = rg[f] * LineHalfW, fb = rg[f + 1] * LineHalfW;
                Vector3 l0 = Drape(a - fa, groundAt), r0 = Drape(a + fa, groundAt);
                Vector3 l1 = Drape(b - fb, groundAt), r1 = Drape(b + fb, groundAt);
                int s = verts.Count;
                verts.Add(l0); verts.Add(r0); verts.Add(r1); verts.Add(l1);
                tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
                tris.Add(s); tris.Add(s + 2); tris.Add(s + 1); tris.Add(s); tris.Add(s + 3); tris.Add(s + 2);   // 2-sided
            }
        }

        static Vector3 Drape(Vector2 p, Func<Vector2, float> groundAt) => new Vector3(p.x, (groundAt != null ? groundAt(p) : 0f) + LineLift, p.y);

        // Travel-direction triangles on a ONE-WAY plan (every navigable lane the same direction): one per drivable lane,
        // ~every 200 m, pointing the way traffic flows. Appended to the plan mesh so they share the red/yellow plan colour.
        static void BuildPlanArrows(Corridor c, float pathLen, List<Vector3> verts, List<int> tris, Vector2[] cp, Vector2[] rg, Func<Vector2, float> groundAt)
        {
            int dir = -1, nav = 0;
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= Net.Edges.Count) continue;
                LaneEdge e = Net.Edges[li];
                if (e.Kind == LaneKind.Sidewalk) continue;
                nav++;
                if (dir < 0) dir = e.Direction; else if (e.Direction != dir) return;   // mixed directions → two-way → no arrows
            }
            if (nav == 0 || cp.Length < 2) return;
            int numArrows = Mathf.Max(1, Mathf.RoundToInt(pathLen / 200f));
            const float fwd = 1.6f, back = 1.0f, half = 0.9f;
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= Net.Edges.Count) continue;
                LaneEdge e = Net.Edges[li];
                if (e.Kind != LaneKind.Traffic && e.Kind != LaneKind.Turn) continue;
                for (int k = 0; k < numArrows; k++)
                {
                    int fi = Mathf.Clamp(Mathf.RoundToInt((k + 0.5f) / numArrows * (cp.Length - 1)), 0, cp.Length - 1);
                    Vector2 rgN = rg[fi];
                    Vector2 tan = new Vector2(-rgN.y, rgN.x);             // tangent recovered from the right vector
                    Vector2 along = (e.Direction == 2 ? tan : -tan).normalized;
                    Vector2 lat = new Vector2(along.y, -along.x);
                    Vector2 ctr = cp[fi] + rgN * e.Offset;
                    int s = verts.Count;
                    verts.Add(Drape(ctr + along * fwd, groundAt));
                    verts.Add(Drape(ctr - along * back - lat * half, groundAt));
                    verts.Add(Drape(ctr - along * back + lat * half, groundAt));
                    tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 1);   // 2-sided
                }
            }
        }

        static Material _inMat, _outMat;
        static Material InMat() => _inMat != null ? _inMat : (_inMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.30f, 0.55f, 1f, 1f), "LaneEndIn"));
        static Material OutMat() => _outMat != null ? _outMat : (_outMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.30f, 1f, 0.5f, 1f), "LaneEndOut"));

        // Per-node lane endpoints (#149): for each lane-edge, a puck at each end, coloured by whether traffic flows INTO
        // that node (incoming, blue) or OUT of it (outgoing, green). Laid across the road width, inset into the road.
        // Compute the per-node lane endpoint records (no rendering) — used for picking, flow render, and default flows.
        // ── lane-drop taper detection ── general, geometry-driven, run each Rebuild AFTER ComputeEndpoints. A lane that
        // meets a junction node where NO colinear, in-line lane of another corridor continues it is a drop/merge: it tapers
        // to zero AT that node (and is excluded from the uniform body, rendered as the taper wedge instead). A lane at a
        // plain terminus (no other corridor at the node) does NOT taper — the road just ends.
        static Vector2 LaneTravelDir(LaneEdge L, Corridor c)
        {
            Vector2 tan = LaneEdgeCorridorBuilder.PathTangent(Net, c, 0.5f);
            if (tan.sqrMagnitude < 1e-6f)
            { Vector2 d = Net.Nodes[L.B] - Net.Nodes[L.A]; tan = d.sqrMagnitude < 1e-6f ? Vector2.right : d.normalized; }
            tan.Normalize();
            return L.Direction == 2 ? tan : -tan;
        }

        static void DetectLaneDropTapers()
        {
            foreach (Corridor c in Net.Corridors) { if (c.Tapers == null) c.Tapers = new List<LaneDropTaper>(); else c.Tapers.Clear(); }
            for (int li = 0; li < Net.Edges.Count; li++)
            {
                LaneEdge L = Net.Edges[li];
                if (L.Kind == LaneKind.Sidewalk) continue;
                Corridor c = (L.CorridorId >= 0 && L.CorridorId < Net.Corridors.Count) ? Net.Corridors[L.CorridorId] : null;
                if (c == null) continue;
                Vector2 travel = LaneTravelDir(L, c);
                if (travel.sqrMagnitude < 1e-6f) continue;
                Vector2 perp = new Vector2(-travel.y, travel.x);
                for (int endSel = 0; endSel < 2; endSel++)
                {
                    int N = endSel == 0 ? L.A : L.B;
                    if (N < 0 || N >= Net.Nodes.Count) continue;
                    if (!TryEndpointPos(N, li, out Vector2 pL, out _)) continue;
                    bool throughExists = false, continued = false;   // throughExists = the road continues STRAIGHT past N
                    for (int mj = 0; mj < Net.Edges.Count && !continued; mj++)
                    {
                        if (mj == li) continue;
                        LaneEdge M = Net.Edges[mj];
                        if (M.A != N && M.B != N) continue;
                        if (M.CorridorId == L.CorridorId) continue;        // a sibling lane isn't a through-connection
                        Corridor mc = (M.CorridorId >= 0 && M.CorridorId < Net.Corridors.Count) ? Net.Corridors[M.CorridorId] : null;
                        if (mc == null) continue;
                        if (Vector2.Dot(travel, LaneTravelDir(M, mc)) < 0.8f) continue;   // not colinear → a turn, not a through lane
                        throughExists = true;                              // a colinear corridor continues the road past N
                        if (TryEndpointPos(N, mj, out Vector2 pM, out _) && Mathf.Abs(Vector2.Dot(pM - pL, perp)) < 0.6f * L.Width)
                            continued = true;                              // an in-line lane continues L specifically → no taper
                    }
                    // Taper ONLY when the road continues straight (a colinear corridor) but THIS lane has no in-line
                    // counterpart — a genuine lane-count mismatch. Corners / T-junctions / termini (no colinear corridor) don't taper.
                    if (throughExists && !continued)
                        c.Tapers.Add(new LaneDropTaper { AtA = (N == L.A), Offset = L.Offset, Width = L.Width, Length = TaperLength(TaperSpeedKmh, L.Width), LaneEdge = li });
                }
            }
        }

        // True if lane `li` is rendered as a taper wedge (so it should be excluded from the corridor's uniform body/lines).
        public static bool LaneIsTapered(Corridor c, int li)
        {
            if (c.Tapers == null) return false;
            for (int i = 0; i < c.Tapers.Count; i++) if (c.Tapers[i].LaneEdge == li) return true;
            return false;
        }

        // If the tapering lane is the OUTERMOST drivable lane on its side, the corridor's shoulder on that side follows the
        // wedge (drawn alongside it, and suppressed on the uniform body). Returns that shoulder width, else 0.
        public static float TaperOuterShoulder(Corridor c, LaneDropTaper tp)
        {
            float sgn = tp.Offset >= 0f ? 1f : -1f;
            float sh = sgn > 0f ? c.ShoulderAB : c.ShoulderBA;
            if (sh <= 0.01f) return 0f;
            float tpOuter = Mathf.Abs(tp.Offset) + tp.Width * 0.5f;
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= Net.Edges.Count || li == tp.LaneEdge) continue;
                LaneEdge e = Net.Edges[li];
                if (e.Kind == LaneKind.Sidewalk) continue;
                if ((e.Offset >= 0f ? 1f : -1f) != sgn) continue;                  // same side only
                if (Mathf.Abs(e.Offset) + e.Width * 0.5f > tpOuter + 0.01f) return 0f;   // a lane sits further out → not outermost
            }
            return sh;
        }

        // True if a taper on side `sgn` (+1 = AB, −1 = BA) owns that shoulder, so the uniform body must NOT draw it there.
        public static bool ShoulderSuppressed(Corridor c, float sgn)
        {
            if (c.Tapers == null) return false;
            foreach (var tp in c.Tapers)
                if ((tp.Offset >= 0f ? 1f : -1f) == sgn && TaperOuterShoulder(c, tp) > 0f) return true;
            return false;
        }

        static void ComputeEndpoints(Func<Vector2, float> groundAt)
        {
            Endpoints.Clear();
            for (int ei = 0; ei < Net.Edges.Count; ei++)
            {
                LaneEdge e = Net.Edges[ei];
                AddEndpointRecord(groundAt, e, ei, e.A, e.B);
                AddEndpointRecord(groundAt, e, ei, e.B, e.A);
            }
        }

        static void AddEndpointRecord(Func<Vector2, float> groundAt, LaneEdge e, int edgeIndex, int atNode, int otherNode)
        {
            if (atNode < 0 || atNode >= Net.Nodes.Count || otherNode < 0 || otherNode >= Net.Nodes.Count) return;
            Vector2 N = Net.Nodes[atNode], O = Net.Nodes[otherNode];
            float len = (O - N).magnitude; if (len < 1e-3f) return;
            // Lateral + inset follow the corridor's A→B tangent at THIS end (curve-aware). Straight corridors fall back to
            // the chord. Using the fixed A→B direction (not O−N) keeps the offsets from mirroring between the two ends.
            Corridor c = (e.CorridorId >= 0 && e.CorridorId < Net.Corridors.Count) ? Net.Corridors[e.CorridorId] : null;
            Vector2 tan;
            if (c != null) tan = LaneEdgeCorridorBuilder.PathTangent(Net, c, atNode == e.A ? 0f : 1f);
            else { Vector2 cd = Net.Nodes[e.B] - Net.Nodes[e.A]; if (cd.sqrMagnitude < 1e-6f) return; tan = cd.normalized; }
            Vector2 into = atNode == e.A ? tan : -tan;            // point inward along the path from this end
            Vector2 fr = LaneEdgeCorridorBuilder.PathRight(tan);
            float inset = Mathf.Min(4f, len * 0.25f);
            // A peeled lane endpoint sits on its OWN node placed at the lane puck (cluster + fr*Offset). Re-adding fr*Offset
            // there would DOUBLE-offset it — a skewed puck and a flow-match position that no longer coincides with the
            // connector at the shared peel node. Detect a peeled end (node differs from the corridor's path node at this
            // end) and treat the node itself as the puck (lat = 0).
            float lat = e.Offset;
            if (c != null && c.Lanes.Count > 0 && c.Lanes[0] != edgeIndex)
            {
                LaneEdge path = Net.Edges[c.Lanes[0]];
                int pathNode = (atNode == e.A) ? path.A : path.B;
                if (atNode != pathNode) lat = 0f;   // this end was peeled onto its own node, already at the lane position
            }
            Vector2 pos = N + into * inset + fr * lat;
            Vector2 nodePos = N + fr * lat;   // lateral-only (no inset): the in/out of a through-lane coincide here → one unified puck
            bool incoming = (e.Direction == 2 && atNode == e.B) || (e.Direction == 0 && atNode == e.A);
            float y = (groundAt != null ? groundAt(pos) : 0f) + 0.6f;
            Endpoints.Add(new LaneEndpoint { Pos = pos, NodePos = nodePos, Y = y, Edge = edgeIndex, Node = atNode, Incoming = incoming });
        }

        static Material _extSelMat;
        static Material ExtSelMat() => _extSelMat != null ? _extSelMat : (_extSelMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 0.4f, 1f, 1f), "LaneExtSel"));

        static Material _laneNodeMat;
        static Material LaneNodeMat() => _laneNodeMat != null ? _laneNodeMat : (_laneNodeMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 0.95f, 0.35f, 1f), "LaneNode"));

        // One UNIFIED puck per lane at each node (the in/out endpoints of a through-lane share a NodePos, so they coincide
        // and draw once). The blue/green in-vs-out distinction is hidden — which one you extend is resolved from the drag
        // direction at pull time (ResolveExtBySwap), so you never have to pick the right node.
        static readonly HashSet<long> _drawnPucks = new HashSet<long>();
        static void RenderEndpointSpheres(Transform parent)
        {
            _drawnPucks.Clear();
            foreach (LaneEndpoint ep in Endpoints)
            {
                long key = ((long)ep.Node << 20) ^ ((long)Mathf.RoundToInt(ep.NodePos.x * 4f) << 10) ^ (long)Mathf.RoundToInt(ep.NodePos.y * 4f);
                if (!_drawnPucks.Add(key)) continue;   // in/out (and the two corridors) coincide at NodePos → draw once
                bool sel = _extNode == ep.Node && _extLanes.Contains(ep.Edge);
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
                go.GetComponent<MeshRenderer>().sharedMaterial = sel ? ExtSelMat() : LaneNodeMat();
                go.transform.SetParent(parent, false);
                go.transform.position = new Vector3(ep.NodePos.x, ep.Y, ep.NodePos.y);
                go.transform.localScale = Vector3.one * (sel ? 2.6f : 1.6f);
                go.name = $"laneNode_e{ep.Edge}_n{ep.Node}";
            }
        }

        // Auto default flows: straight-through, lane-aligned, same-direction. For each incoming endpoint with no MANUAL
        // flow, map it to the best-continuing outgoing endpoint at the same node. Regenerated on any topology change.
        public static void RegenerateDefaultFlows(Func<Vector2, float> groundAt)
        {
            ComputeEndpoints(groundAt);
            Net.Flows.RemoveAll(f => f.Auto);
            for (int i = 0; i < Endpoints.Count; i++)
            {
                LaneEndpoint inc = Endpoints[i];
                if (!inc.Incoming) continue;
                if (Net.Flows.Exists(f => f.Node == inc.Node && f.FromEdge == inc.Edge)) continue;   // a manual flow already routes this lane
                int best = BestOutgoingMatch(i);
                if (best >= 0) Net.Flows.Add(new LaneFlow { Node = inc.Node, FromEdge = inc.Edge, ToEdge = Endpoints[best].Edge, Auto = true });
            }
        }

        // Fork/drop matching. Score by WORLD continuation, not per-corridor offset: the best outgoing lane is the
        // forward-heading lane whose entry sits closest to the incoming lane's straight continuation line. This is
        // cross-corridor correct — a parent lane forks onto whichever branch (trunk or ramp) physically lines up with
        // it, and an offset shared by two corridors with different centrelines no longer mis-scores. Lateral world
        // distance is the primary discriminator (so lanes assign by POSITION); heading is a gate + light tiebreak.
        // A lane with no straight continuation simply takes its nearest forward outgoing → that's the lane-drop/merge:
        // traffic carries into the neighbour instead of dead-ending. (Geometry/taper of the drop is Phase-2.)
        const float FlowAlignWeight = 2f;        // heading weight; lateral metres dominate the score
        const float FlowMinForwardAlign = 0.1f;  // reject U-turns / sharp reversals only

        static int BestOutgoingMatch(int incIdx)
        {
            LaneEndpoint inc = Endpoints[incIdx];
            LaneEdge ie = Net.Edges[inc.Edge];
            int io = inc.Node == ie.A ? ie.B : ie.A;
            Vector2 inDir = Net.Nodes[inc.Node] - Net.Nodes[io]; if (inDir.sqrMagnitude < 1e-6f) return -1; inDir.Normalize();
            Vector2 nrm = new Vector2(-inDir.y, inDir.x);   // lateral axis of the incoming lane's continuation line
            int best = -1; float bestScore = -999f;
            for (int j = 0; j < Endpoints.Count; j++)
            {
                LaneEndpoint outg = Endpoints[j];
                if (outg.Incoming || outg.Node != inc.Node) continue;
                LaneEdge oe = Net.Edges[outg.Edge];
                int oo = outg.Node == oe.A ? oe.B : oe.A;
                Vector2 outDir = Net.Nodes[oo] - Net.Nodes[outg.Node]; if (outDir.sqrMagnitude < 1e-6f) continue; outDir.Normalize();
                float align = Vector2.Dot(inDir, outDir);
                if (align <= FlowMinForwardAlign) continue;   // never default-map a U-turn / sharp reversal
                float lateral = Mathf.Abs(Vector2.Dot(outg.Pos - inc.Pos, nrm));   // world ⊥ distance from the continuation line
                float score = align * FlowAlignWeight - lateral;
                if (score > bestScore) { bestScore = score; best = j; }
            }
            return best;
        }

        static Material _flowMat;
        static Material FlowMat() => _flowMat != null ? _flowMat : (_flowMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 1f, 1f, 1f), "LaneFlow"));

        // Draw each mapped flow as a thin bar from its incoming endpoint to its outgoing endpoint (the #149 connectors).
        static void RenderFlows(Transform parent)
        {
            foreach (LaneFlow f in Net.Flows)
            {
                int inc = FindEndpoint(f.Node, f.FromEdge, true);
                int outg = FindEndpoint(f.Node, f.ToEdge, false);
                if (inc < 0 || outg < 0) continue;
                LaneEndpoint a = Endpoints[inc], b = Endpoints[outg];
                Vector3 p0 = new Vector3(a.Pos.x, a.Y, a.Pos.y), p1 = new Vector3(b.Pos.x, b.Y, b.Pos.y);
                Vector3 dir = p1 - p0; float len = dir.magnitude; if (len < 1e-3f) continue;
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
                go.GetComponent<MeshRenderer>().sharedMaterial = FlowMat();
                go.transform.SetParent(parent, false);
                go.transform.position = (p0 + p1) * 0.5f;
                go.transform.rotation = Quaternion.LookRotation(dir / len, Vector3.up);
                go.transform.localScale = new Vector3(0.3f, 0.3f, len);
                go.name = "flow";
            }
        }

        static int FindEndpoint(int node, int edge, bool incoming)
        {
            for (int i = 0; i < Endpoints.Count; i++)
                if (Endpoints[i].Node == node && Endpoints[i].Edge == edge && Endpoints[i].Incoming == incoming) return i;
            return -1;
        }

        static Material _markerMat;
        static Material MarkerMat() => _markerMat != null ? _markerMat
            : (_markerMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 0.85f, 0.15f, 1f), "LaneEdgeClusterMarker"));

        // Cluster markers (yellow spheres) so nodes are visible to connect to. Collider stripped so they don't block
        // the drape raycast or terrain picking.
        static void RenderNodes(Transform parent, Func<Vector2, float> groundAt)
        {
            for (int i = 0; i < Net.Nodes.Count; i++)
            {
                Vector2 p = Net.Nodes[i];
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
                go.GetComponent<MeshRenderer>().sharedMaterial = MarkerMat();
                go.transform.SetParent(parent, false);
                float y = groundAt != null ? groundAt(p) : 0f;
                go.transform.position = new Vector3(p.x, y + 1.5f, p.y);
                go.transform.localScale = Vector3.one * 3f;
                go.name = $"cluster_{i}";
            }
        }

        // 2-click straight-corridor draw: first click sets the start cluster, second builds a corridor from the active
        // profile between the two clusters and renders. Returns true once a corridor was placed.
        public const float ClusterSnap = 12f;   // world radius for reusing an existing cluster (so corridors connect)

        static int NearestCluster(Vector2 xz, float radius)
        {
            int best = -1; float bestSq = radius * radius;
            for (int i = 0; i < Net.Nodes.Count; i++)
            { float d = (Net.Nodes[i] - xz).sqrMagnitude; if (d < bestSq) { bestSq = d; best = i; } }
            return best;
        }

        // ── colinear extension guide: a new segment off an existing node snaps to continue a connected corridor straight ──
        static readonly HashSet<int> _colSeen = new HashSet<int>();

        // The connected-corridor tangent axis at `node` whose orientation best continues toward `cursor` (so the guide/
        // snap follows whichever road you're pulling along at a multi-road node). False if the node has no corridor.
        static bool BestColinearAxis(int node, Vector2 cursor, out Vector2 axisToward)
        {
            axisToward = Vector2.zero;
            if (node < 0 || node >= Net.Nodes.Count) return false;
            Vector2 start = Net.Nodes[node];
            Vector2 toCur = cursor - start; float d = toCur.magnitude;
            if (d < 1e-2f) return false;
            Vector2 dir = toCur / d;
            float bestDot = -2f; _colSeen.Clear();
            for (int i = 0; i < Net.Edges.Count; i++)
            {
                LaneEdge e = Net.Edges[i];
                if (e.A != node && e.B != node) continue;
                if (e.CorridorId < 0 || e.CorridorId >= Net.Corridors.Count) continue;
                if (!_colSeen.Add(e.CorridorId)) continue;
                Corridor c = Net.Corridors[e.CorridorId];
                if (c.Lanes.Count == 0) continue;
                LaneEdge l0 = Net.Edges[c.Lanes[0]];
                float t = node == l0.A ? 0f : (node == l0.B ? 1f : -1f);
                if (t < 0f) continue;
                Vector2 tan = LaneEdgeCorridorBuilder.PathTangent(Net, c, t);   // corridor A→B tangent at this end
                float d1 = Vector2.Dot(dir, tan), d2 = Vector2.Dot(dir, -tan);  // a line → try both orientations
                if (d1 > bestDot) { bestDot = d1; axisToward = tan; }
                if (d2 > bestDot) { bestDot = d2; axisToward = -tan; }
            }
            return bestDot > -2f;
        }

        public static bool HasColinearGuide => _drawStart >= 0;

        // Snap the (straight or first-leg) extension end onto the source road's colinear line, when within snapAngleDeg.
        public static bool TryColinearSnap(Vector2 cursor, float snapAngleDeg, out Vector2 snapped)
        {
            snapped = cursor;
            if (_drawStart < 0 || _cornerPending) return false;
            if (!BestColinearAxis(_drawStart, cursor, out Vector2 axis)) return false;
            Vector2 start = Net.Nodes[_drawStart];
            Vector2 toCur = cursor - start; float d = toCur.magnitude;
            if (d < 1e-2f) return false;
            if (Vector2.Angle(toCur / d, axis) > snapAngleDeg) return false;
            snapped = start + axis * d;
            return true;
        }

        // Lane-subset extension lock: an extension off an existing road may only go straight-ahead (colinear, through the
        // centre of the picked lanes) or square (90° either way) — never a free angle. Hard snap to the nearest of those.
        public static bool TryColinearSnapExtend(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            if (_extNode < 0 || _extCornerPending) return false;
            if (!BestColinearAxis(_extNode, cursor, out Vector2 axis)) return false;
            Vector2 start = Net.Nodes[_extNode];
            Vector2 toCur = cursor - start; float d = toCur.magnitude;
            if (d < 1e-2f) return false;
            Vector2 dir = toCur / d;
            Vector2 perp = new Vector2(-axis.y, axis.x);
            Vector2 best = axis; float bestDot = Vector2.Dot(dir, axis);   // straight ahead
            if (Vector2.Dot(dir, perp) > bestDot) { bestDot = Vector2.Dot(dir, perp); best = perp; }     // 90° one way
            if (Vector2.Dot(dir, -perp) > bestDot) { bestDot = Vector2.Dot(dir, -perp); best = -perp; }  // 90° the other
            snapped = start + best * d;
            return true;
        }

        // The picked-lanes' lateral centre for an extension heading `startDir`: frNew = its right vector, mid = the lateral
        // offset of the lane group from the node. The curve guide (legs/ring) is anchored at node + frNew*mid (the lane
        // centre) for display, while the build keeps the node so AlignLanes starts the lanes there.
        public static bool ExtendLaneOffset(Vector2 startDir, out Vector2 frNew, out float mid)
        {
            frNew = Vector2.right; mid = 0f;
            if (_extNode < 0 || _extLanes.Count == 0 || startDir.sqrMagnitude < 1e-6f) return false;
            startDir.Normalize();
            frNew = new Vector2(startDir.y, -startDir.x);
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (int sl in _extLanes)
            {
                if (sl < 0 || sl >= Net.Edges.Count) continue;
                LaneEdge s = Net.Edges[sl];
                Corridor sc = (s.CorridorId >= 0 && s.CorridorId < Net.Corridors.Count) ? Net.Corridors[s.CorridorId] : null;
                Vector2 frS;
                if (sc != null) { Vector2 ts = LaneEdgeCorridorBuilder.PathTangent(Net, sc, _extNode == s.A ? 0f : 1f); frS = new Vector2(ts.y, -ts.x); }
                else { Vector2 cd = Net.Nodes[s.B] - Net.Nodes[s.A]; frS = cd.sqrMagnitude < 1e-6f ? Vector2.right : new Vector2(cd.normalized.y, -cd.normalized.x); }
                float oNew = (s.Offset * (Vector2.Dot(frS, frNew) >= 0f ? 1f : -1f));
                lo = Mathf.Min(lo, oNew); hi = Mathf.Max(hi, oNew);
            }
            if (lo > hi) return false;
            mid = (lo + hi) * 0.5f;
            return true;
        }

        // Bend-arming lock for an extension curve: the FIRST leg must continue the source road straight (colinear), so the
        // bend corner is hard-projected onto the colinear line ahead of the node. The curve then turns AFTER the bend.
        public static bool SnapBendColinearExtend(Vector2 cursor, float minLeg, out Vector2 snapped)
        {
            snapped = cursor;
            if (_extNode < 0) return false;
            if (!BestColinearAxis(_extNode, cursor, out Vector2 axis)) return false;
            Vector2 start = Net.Nodes[_extNode];
            // Constrain the bend to the colinear line, at the min-leg ring (red circle) or beyond — never closer.
            float along = Mathf.Max(Mathf.Max(0.5f, minLeg), Vector2.Dot(cursor - start, axis));
            snapped = start + axis * along;
            return true;
        }

        // 2-click straight (or 3-click shift-curve) corridor draw. curveModifier (Shift): the click AFTER the start arms a
        // bend corner; the next click is the end and the corridor follows a cubic bezier through that corner. The design-
        // speed turn-radius limit (limitRadius/minRadius, from the road plan layer) refuses a curve tighter than allowed.
        public static bool Click(Vector2 xz, string profileId, Func<Vector2, float> groundAt,
                                 bool curveModifier = false, bool limitRadius = false, float minRadius = 0f)
        {
            // A normal draw never snaps to an existing SEGMENT node — you expand/connect an existing road ONLY via its lane
            // pucks (extension). So start/bend/end are always fresh nodes; the segment-node centreline is never a draw handle.
            if (_drawStart < 0)
            {
                _drawStart = Net.AddNode(xz);
                return false;
            }

            Vector2 start = Net.Nodes[_drawStart];

            if (_cornerPending)   // END click of a shift-curve: build the curve start→end through the armed corner
            {
                Vector2 endPos = xz;   // PAC owns the end while a bend is armed — no cluster snap (it'd break the equal-leg radius)
                if ((endPos - start).sqrMagnitude < 1f) return false;   // end ≈ start → degenerate
                CurveControls(start, endPos, _corner, out Vector2 c1, out Vector2 c2);
                if (limitRadius && MinCurveRadius(start, c1, c2, endPos) < minRadius)
                {
                    Debug.LogWarning($"[LaneEdgeWorld] curve too tight (R {MinCurveRadius(start, c1, c2, endPos):0} < {minRadius:0} m) — pick a wider end");
                    return false;   // keep the corner armed so the next click can finish a buildable curve
                }
                int bc = Net.AddNode(xz);
                AddCorridorFromProfile(_drawStart, bc, profileId, true, c1, c2);
                _drawStart = -1; _cornerPending = false;
                RegenerateDefaultFlows(groundAt);
                Rebuild(groundAt);
                return true;
            }

            if (curveModifier)   // arm the bend; the next click is the end
            {
                _corner = xz; _cornerPending = true;
                return false;
            }

            int b = Net.AddNode(xz);
            if (b == _drawStart || (Net.Nodes[b] - start).sqrMagnitude < 1f) return false;   // degenerate
            AddCorridorFromProfile(_drawStart, b, profileId);
            _drawStart = -1;
            RegenerateDefaultFlows(groundAt);   // straight-through defaults at any newly-connected node
            Rebuild(groundAt);
            return true;
        }

        public static void CancelDraw() { _drawStart = -1; _cornerPending = false; }

        // Cubic controls that pull the curve toward the bend corner (CurveLever ≈ 0.55 ≈ circular arc) — same lever the
        // road plan uses, so lane-edge curves match the corridor-edge ones.
        static void CurveControls(Vector2 a, Vector2 b, Vector2 corner, out Vector2 c1, out Vector2 c2)
        {
            float f = Mathf.Clamp(NetworkDesigner.Terrain.PlanGuides.CurveLever, 0.1f, 0.95f);
            c1 = Vector2.Lerp(a, corner, f);
            c2 = Vector2.Lerp(b, corner, f);
        }

        // Tightest radius (m) along a cubic bezier, via 3-point circumradius over samples (+inf for a straight). Mirrors
        // RoadPlanLayer.MinCurveRadius so the lane-edge design-speed limit matches.
        static float MinCurveRadius(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            const int N = 24;
            float minR = float.PositiveInfinity;
            Vector2 a = GeometryResolver.SampleCubic(p0, p1, p2, p3, 0f);
            Vector2 b = GeometryResolver.SampleCubic(p0, p1, p2, p3, 1f / N);
            for (int i = 2; i <= N; i++)
            {
                Vector2 c = GeometryResolver.SampleCubic(p0, p1, p2, p3, i / (float)N);
                float ab = Vector2.Distance(a, b), bc = Vector2.Distance(b, c), ca = Vector2.Distance(c, a);
                float area2 = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
                if (area2 > 1e-6f) minR = Mathf.Min(minR, ab * bc * ca / (2f * area2));
                a = b; b = c;
            }
            return minR;
        }

        // ── live draw preview (host calls UpdatePreview each frame while in lane-edge draw mode) ──
        static GameObject _pvRoot;
        static LineRenderer _pvC, _pvL, _pvR, _pvLegA, _pvLegB, _pvGuide;   // path centre/left/right + bend legs + colinear guide
        static GameObject _pvStartM, _pvCornerM, _pvEndM;          // start / armed-corner / end markers
        static Material _pvOkM, _pvBadM, _pvLegM, _pvNodeM, _pvSnapM, _pvGuideM;
        static Material PvOk()   => _pvOkM   != null ? _pvOkM   : (_pvOkM   = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.30f, 1f, 0.55f, 0.85f), "LanePvOk"));
        static Material PvBad()  => _pvBadM  != null ? _pvBadM  : (_pvBadM  = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 0.30f, 0.25f, 0.9f), "LanePvBad"));
        static Material PvLeg()  => _pvLegM  != null ? _pvLegM  : (_pvLegM  = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.8f, 0.8f, 0.85f, 0.6f), "LanePvLeg"));
        static Material PvNode() => _pvNodeM != null ? _pvNodeM : (_pvNodeM = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 0.85f, 0.15f, 0.9f), "LanePvNode"));
        static Material PvSnap() => _pvSnapM != null ? _pvSnapM : (_pvSnapM = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.30f, 0.6f, 1f, 0.95f), "LanePvSnap"));
        static Material PvGuide() => _pvGuideM != null ? _pvGuideM : (_pvGuideM = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.35f, 0.95f, 1f, 0.5f), "LanePvGuide"));

        static LineRenderer MakePvLine(Transform parent, float width, Material mat)
        {
            var go = new GameObject("pvLine"); go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true; lr.widthMultiplier = width; lr.numCapVertices = 2; lr.sharedMaterial = mat;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false;
            return lr;
        }

        static GameObject MakePvMarker(Transform parent, float scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.transform.SetParent(parent, false); go.transform.localScale = Vector3.one * scale;
            return go;
        }

        static void EnsurePreview()
        {
            if (_pvRoot != null) return;
            _pvRoot = new GameObject("LaneEdgePreview");
            _pvC = MakePvLine(_pvRoot.transform, 0.6f, PvOk());
            _pvL = MakePvLine(_pvRoot.transform, 0.35f, PvOk());
            _pvR = MakePvLine(_pvRoot.transform, 0.35f, PvOk());
            _pvLegA = MakePvLine(_pvRoot.transform, 0.25f, PvLeg());
            _pvLegB = MakePvLine(_pvRoot.transform, 0.25f, PvLeg());
            _pvGuide = MakePvLine(_pvRoot.transform, 0.2f, PvGuide());
            _pvStartM = MakePvMarker(_pvRoot.transform, 2.5f, PvNode());
            _pvCornerM = MakePvMarker(_pvRoot.transform, 2.2f, PvLeg());
            _pvEndM = MakePvMarker(_pvRoot.transform, 2.5f, PvNode());
            _pvMeshGo = new GameObject("pvStyled"); _pvMeshGo.transform.SetParent(_pvRoot.transform, false);
            _pvMeshData = new Mesh { name = "pvStyledMesh" };
            _pvMeshGo.AddComponent<MeshFilter>().sharedMesh = _pvMeshData;
            _pvMeshMr = _pvMeshGo.AddComponent<MeshRenderer>();
            _pvMeshMr.sharedMaterial = PlannedMat();
            _pvMeshMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _pvMeshMr.receiveShadows = false;
        }

        static GameObject _pvMeshGo; static Mesh _pvMeshData; static MeshRenderer _pvMeshMr;
        static readonly List<Vector3> _sgVerts = new List<Vector3>(); static readonly List<int> _sgTris = new List<int>();   // reused per-frame styled-preview buffers

        // Build the styled guide-line mesh (small-dash edges, large-dash lanes, double centre line — matching the committed
        // plan) into the persistent preview mesh, along a straight or curved path a→b from the active profile. `bad` = tint.
        static void DrawStyledPreview(Vector2 a, Vector2 b, bool curved, Vector2 c1, Vector2 c2, string profileId, Func<Vector2, float> groundAt, bool bad)
        {
            ProfileLanes(profileId, out _shBA, out _shAB);
            CollectGuideLines();
            float len = curved ? GeometryResolver.CubicArcLength(a, c1, c2, b) : (b - a).magnitude;
            int frames = Mathf.Clamp(Mathf.CeilToInt(len / 1.5f) + 1, 2, 256);
            var cp = new Vector2[frames]; var rg = new Vector2[frames];
            for (int f = 0; f < frames; f++)
            {
                float t = (float)f / (frames - 1);
                Vector2 tan = curved ? GeometryResolver.CubicTangent(a, c1, c2, b, t) : (b - a);
                if (tan.sqrMagnitude < 1e-8f) tan = Vector2.right; else tan.Normalize();
                cp[f] = curved ? GeometryResolver.SampleCubic(a, c1, c2, b, t) : Vector2.Lerp(a, b, t);
                rg[f] = new Vector2(tan.y, -tan.x);
            }
            _sgVerts.Clear(); _sgTris.Clear();
            BuildStyledGuides(_sgVerts, _sgTris, cp, rg, groundAt);
            _pvMeshData.Clear();
            if (_sgVerts.Count > 0) { _pvMeshData.SetVertices(_sgVerts); _pvMeshData.SetTriangles(_sgTris, 0); _pvMeshData.RecalculateBounds(); }
            _pvMeshMr.sharedMaterial = bad ? PvBad() : PlannedMat();
            _pvMeshGo.SetActive(_sgVerts.Count > 0);
        }

        static readonly List<GameObject> _pvHover = new List<GameObject>();   // lane-snap hover halos (extension pick preview)
        static GameObject HoverHalo(int i)
        {
            while (_pvHover.Count <= i)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
                go.transform.SetParent(_pvRoot.transform, false);
                go.transform.localScale = Vector3.one * 2.4f;
                go.GetComponent<MeshRenderer>().sharedMaterial = ExtSelMat();
                _pvHover.Add(go);
            }
            return _pvHover[i];
        }

        static void PlaceMarker(GameObject m, Vector2 p, Func<Vector2, float> groundAt, Material mat)
        {
            m.GetComponent<MeshRenderer>().sharedMaterial = mat;
            float y = (groundAt != null ? groundAt(p) : 0f) + 0.6f;
            m.transform.position = new Vector3(p.x, y, p.y);
            m.SetActive(true);
        }

        // Fill a preview LineRenderer along a straight or cubic path at lateral offset `off`, draped + lit by `mat`.
        static void FillPvLine(LineRenderer lr, Vector2 a, Vector2 b, bool curved, Vector2 c1, Vector2 c2, float off, Func<Vector2, float> groundAt, Material mat)
        {
            float len = curved ? GeometryResolver.CubicArcLength(a, c1, c2, b) : (b - a).magnitude;
            int n = Mathf.Clamp(Mathf.CeilToInt(len / 6f), 2, 48);   // kept light — preview only, still drapes over terrain
            lr.sharedMaterial = mat; lr.positionCount = n + 1;
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                Vector2 tan = curved ? GeometryResolver.CubicTangent(a, c1, c2, b, t) : (b - a);
                if (tan.sqrMagnitude < 1e-8f) tan = Vector2.right; else tan.Normalize();
                Vector2 fr = new Vector2(tan.y, -tan.x);
                Vector2 p = (curved ? GeometryResolver.SampleCubic(a, c1, c2, b, t) : Vector2.Lerp(a, b, t)) + fr * off;
                float y = (groundAt != null ? groundAt(p) : 0f) + 0.1f;
                lr.SetPosition(i, new Vector3(p.x, y, p.y));
            }
            lr.gameObject.SetActive(true);
        }

        static string _phwId; static float _phwVal;   // cache so the per-frame preview doesn't re-resolve the profile config
        static float ProfileHalfWidth(string profileId)
        {
            if (profileId == _phwId) return _phwVal;
            float half = 6f;
            var cfg = RoadProfileLibrary.ResolveConfig(profileId);
            if (cfg != null && cfg.Corridor != null)
            {
                var bands = new List<RoadCrossSectionBuilder.StackBand>();
                RoadCrossSectionBuilder.FromStack(cfg.Corridor, bands);
                float total = 0f; foreach (var bd in bands) total = Mathf.Max(total, bd.U1);
                if (total > 0.1f) half = total * 0.5f;
            }
            _phwId = profileId; _phwVal = half;
            return half;
        }

        // Movement/state cache: skip the (LineRenderer + drape) rebuild when nothing changed. With the PAC snap the end is
        // quantised to the ring/ticks, so the cursor often holds still frame-to-frame — this is the main draw-lag fix.
        static bool _pvShown; static Vector2 _pvCursorShown; static int _pvStartShown = -2;
        static bool _pvCornerShown, _pvCurveModShown; static string _pvProfileShown;
        static bool _pvGuideBuilt; static Vector2 _pvGuideStart, _pvGuideAxis;   // colinear guide is fixed per draw → cache it

        // Live preview of the corridor about to be drawn: ghost end marker (with cluster-snap colour), and once a start is
        // set, the straight/curved path (centre + both edges) to the cursor, plus the bend legs while a curve is armed.
        public static void UpdatePreview(Vector2 cursor, bool curveModifier, bool limitRadius, float minRadius,
                                         Func<Vector2, float> groundAt, string profileId)
        {
            if (_pvShown && _drawStart == _pvStartShown && _cornerPending == _pvCornerShown
                && curveModifier == _pvCurveModShown && profileId == _pvProfileShown
                && (cursor - _pvCursorShown).sqrMagnitude < 0.04f)   // <0.2 m + same phase → nothing to redraw
                return;
            _pvShown = true; _pvCursorShown = cursor; _pvStartShown = _drawStart;
            _pvCornerShown = _cornerPending; _pvCurveModShown = curveModifier; _pvProfileShown = profileId;

            EnsurePreview();
            _pvRoot.SetActive(true);
            _pvC.gameObject.SetActive(false); _pvL.gameObject.SetActive(false); _pvR.gameObject.SetActive(false);
            _pvLegA.gameObject.SetActive(false); _pvLegB.gameObject.SetActive(false); _pvGuide.gameObject.SetActive(false);
            _pvCornerM.SetActive(false); _pvStartM.SetActive(false);
            if (_pvMeshGo != null) _pvMeshGo.SetActive(false);
            for (int i = 0; i < _pvHover.Count; i++) _pvHover[i].SetActive(false);

            // No segment-node snap: the normal draw always lands a fresh node (connection is via lane pucks only), so the
            // preview end marker just follows the cursor.
            Vector2 endPos = cursor;
            PlaceMarker(_pvEndM, endPos, groundAt, PvNode());

            if (_drawStart < 0)   // hovering to start: snap-highlight the lane group a click would PULL OFF an existing road.
            {
                int shown = 0;
                if (ComputeExtendGroup(cursor, profileId, 5f, out int hNode, out _))
                    foreach (int edge in _grpBuf)
                    {
                        if (!TryEndpointPos(hNode, edge, out Vector2 hp, out float hy)) continue;
                        GameObject h = HoverHalo(shown++);
                        h.transform.position = new Vector3(hp.x, hy, hp.y); h.SetActive(true);
                    }
                if (shown > 0) _pvEndM.SetActive(false);   // snapping to lanes → drop the free ghost marker
                return;
            }

            Vector2 start = Net.Nodes[_drawStart];
            PlaceMarker(_pvStartM, start, groundAt, PvNode());

            // Colinear extension guide: a long line through the start along the connected road's tangent (both ways).
            // It's FIXED during a draw (independent of the moving cursor), so build it once and just re-show it — the
            // 600 m draped line was being rebuilt every frame for nothing (a big chunk of the straight-draw lag).
            // Only SHOW it when the cursor is roughly IN FRONT of the road (within GuideConeDeg of its forward axis) so it
            // stops cluttering the view when you're pulling well off to the side.
            bool showGuide = false;
            if (!_cornerPending && BestColinearAxis(_drawStart, cursor, out Vector2 gax))
            {
                Vector2 toCur = cursor - start; float dCur = toCur.magnitude;
                if (dCur > 1e-2f && Vector2.Angle(toCur / dCur, gax) <= NetworkDesigner.Terrain.PlanGuides.GuideConeDeg)
                {
                    showGuide = true;
                    if (!_pvGuideBuilt || _pvGuideStart != start || Vector2.Dot(_pvGuideAxis, gax) < 0.999f)
                    {
                        const float L = 300f;
                        FillPvLine(_pvGuide, start - gax * L, start + gax * L, false, default, default, 0f, groundAt, PvGuide());
                        _pvGuideBuilt = true; _pvGuideStart = start; _pvGuideAxis = gax;
                    }
                    else _pvGuide.gameObject.SetActive(true);   // reuse cached geometry
                }
            }
            if (!showGuide && _pvGuide != null) _pvGuide.gameObject.SetActive(false);

            if (_cornerPending)
            {
                CurveControls(start, endPos, _corner, out Vector2 c1, out Vector2 c2);
                bool tooTight = limitRadius && MinCurveRadius(start, c1, c2, endPos) < minRadius;
                DrawStyledPreview(start, endPos, true, c1, c2, profileId, groundAt, tooTight);   // styled per-lane guides
                FillPvLine(_pvLegA, start, _corner, false, default, default, 0f, groundAt, PvLeg());
                FillPvLine(_pvLegB, _corner, endPos, false, default, default, 0f, groundAt, PvLeg());
                PlaceMarker(_pvCornerM, _corner, groundAt, PvLeg());
            }
            else if (curveModifier)   // about to drop a bend here → show the first leg only (no corridor yet)
            {
                FillPvLine(_pvLegA, start, endPos, false, default, default, 0f, groundAt, PvLeg());
            }
            else                      // straight preview
            {
                DrawStyledPreview(start, endPos, false, default, default, profileId, groundAt, false);
            }
        }

        public static void ClearPreview() { if (_pvRoot != null) _pvRoot.SetActive(false); _pvShown = false; _pvGuideBuilt = false; }

        // Live preview while pulling out a lane-subset extension: the new road outline (centre + edges), offset onto the
        // selected lanes exactly as it will build, from the source node to the cursor (straight, or curved via shift-bend).
        public static void UpdateExtendPreview(Vector2 cursor, bool curveModifier, Func<Vector2, float> groundAt, string profileId)
        {
            EnsurePreview();
            _pvRoot.SetActive(true);
            _pvC.gameObject.SetActive(false); _pvL.gameObject.SetActive(false); _pvR.gameObject.SetActive(false);
            _pvLegA.gameObject.SetActive(false); _pvLegB.gameObject.SetActive(false); _pvGuide.gameObject.SetActive(false);
            _pvCornerM.SetActive(false); _pvStartM.SetActive(false); _pvEndM.SetActive(false);
            _pvShown = false;   // invalidate the normal-preview cache so it rebuilds when we leave extend mode
            if (!Extending) return;

            Vector2 N = Net.Nodes[_extNode];
            PlaceMarker(_pvStartM, N, groundAt, PvNode());
            if ((cursor - N).sqrMagnitude < 1f) return;

            // First-leg tangent at N (toward the armed corner, else the cursor) → the frame the offsets project into.
            Vector2 tanN = _extCornerPending ? (_extCorner - N) : (cursor - N);
            if (tanN.sqrMagnitude < 1e-6f) tanN = cursor - N;
            tanN = tanN.normalized;
            Vector2 frNew = new Vector2(tanN.y, -tanN.x);

            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            int gotBA = 0, gotAB = 0; float baW = 3.5f, abW = 3.5f, baMax = -1f, abMax = -1f;
            foreach (int sl in _extLanes)
            {
                if (sl < 0 || sl >= Net.Edges.Count) continue;
                LaneEdge s = Net.Edges[sl];
                Corridor sc = (s.CorridorId >= 0 && s.CorridorId < Net.Corridors.Count) ? Net.Corridors[s.CorridorId] : null;
                Vector2 frS;
                if (sc != null) { Vector2 ts = LaneEdgeCorridorBuilder.PathTangent(Net, sc, _extNode == s.A ? 0f : 1f); frS = new Vector2(ts.y, -ts.x); }
                else { Vector2 cd = Net.Nodes[s.B] - Net.Nodes[s.A]; frS = cd.sqrMagnitude < 1e-6f ? Vector2.right : new Vector2(cd.normalized.y, -cd.normalized.x); }
                float oNew = (s.Offset * (Vector2.Dot(frS, frNew) >= 0f ? 1f : -1f));
                lo = Mathf.Min(lo, oNew - s.Width * 0.5f); hi = Mathf.Max(hi, oNew + s.Width * 0.5f);
                // Count by the EXTENSION-frame direction (dirNew), matching BuildExtensionCorridor — off the A end the
                // source direction inverts, so source-direction counts would put the surplus on the wrong side.
                bool incN = (s.Direction == 2 && _extNode == s.B) || (s.Direction == 0 && _extNode == s.A);
                if ((incN ? 2 : 0) == 0) { gotBA++; if (Mathf.Abs(oNew) >= baMax) { baMax = Mathf.Abs(oNew); baW = s.Width; } }
                else { gotAB++; if (Mathf.Abs(oNew) >= abMax) { abMax = Mathf.Abs(oNew); abW = s.Width; } }
            }
            if (lo > hi) return;
            // Lane addition (two-way only — mirrors BuildExtensionCorridor's gate): widen the band on the side that gains
            // lanes (BA → lo, AB → hi) so the preview matches the built corridor, not just the grabbed-lane span.
            ProfileLaneSplit(profileId, out int wantBA, out int wantAB);
            if (_extLanes.Count > 0 && _extLanes[0] >= 0 && _extLanes[0] < Net.Edges.Count)   // A-end pull inverts direction (match build)
            {
                LaneEdge sf = Net.Edges[_extLanes[0]];
                bool inc0 = (sf.Direction == 2 && _extNode == sf.B) || (sf.Direction == 0 && _extNode == sf.A);
                if ((inc0 ? 2 : 0) != sf.Direction) { int t = wantBA; wantBA = wantAB; wantAB = t; }
            }
            if (ExtFlipSide) { int t = wantBA; wantBA = wantAB; wantAB = t; }   // mirror — match the built side (F flip)
            if (wantBA > 0 && wantAB > 0)
            {
                if (gotAB > 0 && wantAB > gotAB) hi += (wantAB - gotAB) * abW;
                if (gotBA > 0 && wantBA > gotBA) lo -= (wantBA - gotBA) * baW;
            }
            // The built extension carries the source road's shoulders (BuildExtensionCorridor) — include them so the
            // preview width matches the source plan + the built body (not just the bare lane span).
            float shoulder = 0f;
            if (_extLanes.Count > 0 && _extLanes[0] >= 0 && _extLanes[0] < Net.Edges.Count)
            {
                LaneEdge s0 = Net.Edges[_extLanes[0]];
                Corridor sc0 = (s0.CorridorId >= 0 && s0.CorridorId < Net.Corridors.Count) ? Net.Corridors[s0.CorridorId] : null;
                if (sc0 != null) shoulder = (sc0.ShoulderBA + sc0.ShoulderAB) * 0.5f;
            }
            float mid = (lo + hi) * 0.5f, halfW = (hi - lo) * 0.5f + shoulder;

            PlaceMarker(_pvEndM, cursor, groundAt, PvSnap());
            if (_extCornerPending)
            {
                CurveControls(N, cursor, _extCorner, out Vector2 c1, out Vector2 c2);
                FillPvLine(_pvC, N, cursor, true, c1, c2, mid, groundAt, PvOk());
                FillPvLine(_pvL, N, cursor, true, c1, c2, mid + halfW, groundAt, PvOk());
                FillPvLine(_pvR, N, cursor, true, c1, c2, mid - halfW, groundAt, PvOk());
                // Construction legs + bend marker ride the PICKED-LANES' centre (offset mid), not the road centre N.
                FillPvLine(_pvLegA, N, _extCorner, false, default, default, mid, groundAt, PvLeg());
                FillPvLine(_pvLegB, _extCorner, cursor, false, default, default, mid, groundAt, PvLeg());
                PlaceMarker(_pvCornerM, _extCorner + frNew * mid, groundAt, PvLeg());
            }
            else if (curveModifier)   // about to drop a bend → show the first leg off the lane centre
                FillPvLine(_pvLegA, N, cursor, false, default, default, mid, groundAt, PvLeg());
            else
            {
                FillPvLine(_pvC, N, cursor, false, default, default, mid, groundAt, PvOk());
                FillPvLine(_pvL, N, cursor, false, default, default, mid + halfW, groundAt, PvOk());
                FillPvLine(_pvR, N, cursor, false, default, default, mid - halfW, groundAt, PvOk());
            }
        }

        // Per-corridor excavation beds (centreline grade dropped by `depth`; flatHalf = section½ + margin) — same shape as
        // RoadPlanLayer.CollectExcavationBeds, so the existing GradeBatter/FlattenStamp terrain grading is reused verbatim.
        // Captures the centreline grade into NodeY so Build sits the body on the same design grade the bed was cut to.
        public static void CollectExcavationBeds(Func<Vector2, float> groundAt, float depth, float margin, List<(List<Vector3> pts, float flatHalf)> beds)
        {
            foreach (Corridor c in Net.Corridors)
            {
                if (c.Built || c.Lanes.Count == 0) continue;
                LaneEdge l0 = Net.Edges[c.Lanes[0]];
                Vector2 a = Net.Nodes[l0.A], b = Net.Nodes[l0.B];
                float pathLen = LaneEdgeCorridorBuilder.PathLength(Net, c);
                if (pathLen < 1e-2f) continue;
                float halfW = LaneEdgeCorridorBuilder.BuildCrossSection(c, Net).Width * 0.5f;
                // AlignLanes corridors sit OFF the centreline (lanes to one side), so shift the cut onto the lane span.
                float lat = 0f;
                if (c.AlignLanes)
                {
                    float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                    foreach (int li in c.Lanes) { LaneEdge e = Net.Edges[li]; lo = Mathf.Min(lo, e.Offset - e.Width * 0.5f); hi = Mathf.Max(hi, e.Offset + e.Width * 0.5f); }
                    lat = (lo + hi) * 0.5f;
                }
                int n = Mathf.Clamp(Mathf.CeilToInt(pathLen / 2f), 2, 256);
                float[] grade = RoadSweep.ElevationProfile(a, b, c.Curved, c.ControlA, c.ControlB, n + 1,
                                    groundAt != null ? groundAt(a) : 0f, groundAt != null ? groundAt(b) : 0f, groundAt, 1f);
                var pts = new List<Vector3>(n + 1);
                for (int i = 0; i <= n; i++)
                {
                    float t = (float)i / n;
                    Vector2 p = LaneEdgeCorridorBuilder.PathPoint(Net, c, t);
                    if (lat != 0f) p += LaneEdgeCorridorBuilder.PathRight(LaneEdgeCorridorBuilder.PathTangent(Net, c, t)) * lat;
                    pts.Add(new Vector3(p.x, grade[i] - depth, p.y));
                }
                beds.Add((pts, halfW + margin));
                Net.SetNodeY(l0.A, grade[0]); Net.SetNodeY(l0.B, grade[n]);   // design grade captured for Build
                c.Excavated = true; c.BedDepth = depth;
            }
        }

        // ── lane-flow mapping (#149): click an incoming (blue) endpoint to arm, then an outgoing (green) endpoint at the
        // SAME node to map a through/turn movement ──
        static int _armEdge = -1, _armNode = -1;
        public static bool Mapping => _armEdge >= 0;

        static int PickEndpoint(Camera cam, Vector2 screenPos, float pixR, bool incoming)
        {
            int best = -1; float bestSq = pixR * pixR;
            for (int i = 0; i < Endpoints.Count; i++)
            {
                if (Endpoints[i].Incoming != incoming) continue;
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(Endpoints[i].Pos.x, Endpoints[i].Y, Endpoints[i].Pos.y));
                if (sp.z <= 0f) continue;
                float d = (new Vector2(sp.x, sp.y) - screenPos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = i; }
            }
            return best;
        }

        public static void MapClick(Camera cam, Vector2 screenPos, Func<Vector2, float> groundAt)
        {
            if (cam == null) return;
            if (_armEdge < 0)
            {
                int inc = PickEndpoint(cam, screenPos, 28f, true);
                if (inc >= 0) { _armEdge = Endpoints[inc].Edge; _armNode = Endpoints[inc].Node; Debug.Log($"[LaneFlow] armed incoming edge {_armEdge} at node {_armNode} — now click an outgoing (green) endpoint"); }
                else Debug.Log("[LaneFlow] no incoming (blue) endpoint under the cursor");
                return;
            }
            int outg = PickEndpoint(cam, screenPos, 28f, false);
            if (outg >= 0 && Endpoints[outg].Node == _armNode)
            { Net.Flows.Add(new LaneFlow { Node = _armNode, FromEdge = _armEdge, ToEdge = Endpoints[outg].Edge }); Debug.Log($"[LaneFlow] mapped edge {_armEdge} → edge {Endpoints[outg].Edge} at node {_armNode}"); }
            else Debug.Log("[LaneFlow] cancelled — outgoing endpoint must be at the SAME node");
            _armEdge = -1; _armNode = -1;
            RegenerateDefaultFlows(groundAt);   // refill defaults around the new manual flow (manual lanes are skipped)
            Rebuild(groundAt);
        }

        public static void CancelMap() { _armEdge = -1; _armNode = -1; }

        // Build a corridor between two clusters from a profile's cross-section: traffic/turn/sidewalk/bike bands become
        // lane-edges; median/shoulder bands become corridor metadata.
        public static Corridor AddCorridorFromProfile(int ca, int cb, string profileId, bool curved = false, Vector2 ctrlA = default, Vector2 ctrlB = default)
        {
            Corridor c = Net.AddCorridor();
            c.Profile = profileId;
            c.Curved = curved; c.ControlA = ctrlA; c.ControlB = ctrlB;
            var cfg = RoadProfileLibrary.ResolveConfig(profileId);
            if (cfg == null || cfg.Corridor == null) return c;
            var bands = new List<RoadCrossSectionBuilder.StackBand>();
            RoadCrossSectionBuilder.FromStack(cfg.Corridor, bands);
            float total = 0f; foreach (var bd in bands) total = Mathf.Max(total, bd.U1);
            float center = total * 0.5f;
            foreach (var bd in bands)
            {
                float w = bd.U1 - bd.U0;
                float off = (bd.U0 + bd.U1) * 0.5f - center;
                switch (bd.Type)
                {
                    case Model.CorridorType.Traffic:
                    case Model.CorridorType.Turn:
                    case Model.CorridorType.Sidewalk:
                    case Model.CorridorType.Bike:
                        LaneKind k = bd.Type == Model.CorridorType.Sidewalk ? LaneKind.Sidewalk
                                   : bd.Type == Model.CorridorType.Turn ? LaneKind.Turn
                                   : bd.Type == Model.CorridorType.Bike ? LaneKind.Bike : LaneKind.Traffic;
                        int li = Net.AddLane(new LaneEdge
                        {
                            A = ca, B = cb, CorridorId = c.Id, Kind = k,
                            Direction = bd.Zone == 0 ? 0 : 2, Width = w, Offset = off
                        });
                        c.Lanes.Add(li);
                        break;
                    case Model.CorridorType.Median: c.MedianWidth = w; break;
                    case Model.CorridorType.Shoulder: if (off < 0f) c.ShoulderBA = w; else c.ShoulderAB = w; break;
                }
            }
            Net.SortCorridorLanes(c);
            return c;
        }

        // ── lane-subset extension (fork / lane-drop): pick lane endpoint pucks at a node, then draw a narrower corridor
        // that continues exactly those lanes (straight or curved). Unselected lanes merge into the nearest continuation
        // via the default-flow regen, so they "drop". ──
        static int _extNode = -1;
        static readonly List<int> _extLanes = new List<int>();        // selected lane-edge indices to extend
        static Vector2 _extCorner; static bool _extCornerPending;
        public static bool ExtFlipSide;                               // F while extending: append the surplus lane on the OTHER side
        public static void ToggleExtFlip() => ExtFlipSide = !ExtFlipSide;
        // On a lane pull, what happens to the SOURCE segment's pulled lanes: Keep (continuation — today's behaviour), or
        // delete them (an exit). DeleteOuter removes only the outermost pulled lane, so an inner pulled lane stays in the
        // source (shared — continues AND feeds the ramp). Never empties a corridor (guards full/colinear continuations).
        public enum ExitPullMode { Keep, DeleteAll, DeleteOuter }
        public static ExitPullMode ExitMode = ExitPullMode.DeleteOuter;   // default: an exit pull drops the outer downstream lane
        public static bool Extending => _extNode >= 0 && _extLanes.Count > 0;
        public static int ExtendNode => _extNode;
        public static IReadOnlyList<int> ExtendLanes => _extLanes;
        public static bool ExtCornerPending => _extCornerPending;
        public static Vector2 ExtCornerPos => _extCorner;
        public static Vector2 ExtendStartPos => _extNode >= 0 && _extNode < Net.Nodes.Count ? Net.Nodes[_extNode] : Vector2.zero;

        // Pick the lane endpoint nearest the cursor and toggle it into the extension selection. All picks must share a
        // node (picking at a new node restarts the selection). Returns true if a puck was hit (so the caller swallows the click).
        // World-space pick: click ON a lane near its end (a wide, easy target) — far more forgiving than aiming at the
        // small endpoint sphere. Picks the nearest lane endpoint within worldR metres of the clicked ground point.
        static readonly List<int> _grpBuf = new List<int>(), _grpCand = new List<int>();

        // The lane group a click/hover at worldXz would grab: nearest lane endpoint → a contiguous block of `profile lane
        // count` lanes (same corridor + direction) around it (or the single lane for a 1-lane profile). Read-only → into _grpBuf.
        static bool ComputeExtendGroup(Vector2 worldXz, string profileId, float worldR, out int node, out bool single)
        {
            node = -1; single = false; _grpBuf.Clear();
            int best = -1; float bestSq = worldR * worldR;
            for (int i = 0; i < Endpoints.Count; i++)
            {
                float d = (Endpoints[i].NodePos - worldXz).sqrMagnitude;   // pick where the unified puck is drawn
                if (d < bestSq) { bestSq = d; best = i; }
            }
            if (best < 0) return false;
            LaneEndpoint ep = Endpoints[best]; node = ep.Node;
            LaneEdge picked = Net.Edges[ep.Edge];
            if (ProfileLaneCount(profileId) <= 1) { _grpBuf.Add(ep.Edge); single = true; return true; }

            // A two-way profile extends the FULL cross-section (both travel directions + the median divider), so we don't
            // restrict the group to the clicked lane's direction. A one-way profile is a fork → same-direction lanes only.
            bool twoWay = ProfileIsTwoWay(profileId);
            _grpCand.Clear();
            for (int i = 0; i < Net.Edges.Count; i++)
            {
                LaneEdge e = Net.Edges[i];
                if ((e.A != node && e.B != node) || e.CorridorId != picked.CorridorId) continue;
                if (e.Kind == LaneKind.Sidewalk) continue;
                if (!twoWay && e.Direction != picked.Direction) continue;
                _grpCand.Add(i);
            }
            if (twoWay)
            {
                // Direction-balanced grab: take the profile's per-direction lane counts, the lanes NEAREST THE MEDIAN on
                // each side. Inner-first keeps the extension centred on the source centreline (centrelines align) and makes
                // a mis-split (e.g. 3+1 off a 3x2) impossible — surplus outer lanes simply drop. Pick position is ignored.
                ProfileLaneSplit(profileId, out int nBA, out int nAB);
                _grpCand.Sort((x, y) => Mathf.Abs(Net.Edges[x].Offset).CompareTo(Mathf.Abs(Net.Edges[y].Offset)));   // median-hugging first
                int gotBA = 0, gotAB = 0;
                foreach (int ei in _grpCand)
                {
                    if (Net.Edges[ei].Direction == 0) { if (gotBA < nBA) { _grpBuf.Add(ei); gotBA++; } }
                    else { if (gotAB < nAB) { _grpBuf.Add(ei); gotAB++; } }
                }
                if (_grpBuf.Count == 0) _grpBuf.Add(ep.Edge);
                return true;
            }

            // one-way fork: contiguous same-direction block centred on the pick.
            _grpCand.Sort((x, y) => Net.Edges[x].Offset.CompareTo(Net.Edges[y].Offset));
            int pi = _grpCand.IndexOf(ep.Edge);
            if (pi < 0) { _grpBuf.Add(ep.Edge); return true; }
            int g = Mathf.Min(ProfileLaneCount(profileId), _grpCand.Count);
            int startIdx = Mathf.Clamp(pi - g / 2, 0, _grpCand.Count - g);
            for (int k = 0; k < g; k++) _grpBuf.Add(_grpCand[startIdx + k]);
            return true;
        }

        public static bool ToggleExtendPick(Vector2 worldXz, string profileId, float worldR = 5f)
        {
            if (!ComputeExtendGroup(worldXz, profileId, worldR, out int node, out bool single)) return false;
            if (_extNode >= 0 && node != _extNode) _extLanes.Clear();   // switched node → restart selection
            _extNode = node;
            if (single)   // 1-lane profile → toggle the single lane (build up multi-lane by clicking each)
            {
                int edge = _grpBuf[0];
                if (_extLanes.Contains(edge)) _extLanes.Remove(edge); else _extLanes.Add(edge);
                if (_extLanes.Count == 0) _extNode = -1;
            }
            else { _extLanes.Clear(); _extLanes.AddRange(_grpBuf); }   // multi-lane → grab the contiguous group
            return true;
        }

        static bool TryEndpointPos(int node, int edge, out Vector2 pos, out float y)
        {
            for (int i = 0; i < Endpoints.Count; i++)
                if (Endpoints[i].Node == node && Endpoints[i].Edge == edge) { pos = Endpoints[i].NodePos; y = Endpoints[i].Y; return true; }
            pos = Vector2.zero; y = 0f; return false;
        }

        // The lane that continues `lane` straight THROUGH `node` (same travel direction, opposite in/out endpoint, nearest
        // by world position) — its through-pair partner on the adjoining segment. -1 if none (a road end). Used to swap a
        // picked lane to its incoming/outgoing twin so the drag direction decides which one we extend.
        static int ThroughPartner(int node, int lane)
        {
            if (lane < 0 || lane >= Net.Edges.Count) return -1;
            LaneEdge s = Net.Edges[lane];
            bool sIn = (s.Direction == 2 && node == s.B) || (s.Direction == 0 && node == s.A);
            Vector2 sPos = Vector2.zero; bool have = false;
            for (int i = 0; i < Endpoints.Count; i++)
                if (Endpoints[i].Node == node && Endpoints[i].Edge == lane && Endpoints[i].Incoming == sIn) { sPos = Endpoints[i].NodePos; have = true; break; }
            if (!have) return -1;
            int best = -1; float bestD = s.Width * s.Width;   // partner sits at ~the same lateral (NodePos) → small distance
            for (int i = 0; i < Endpoints.Count; i++)
            {
                LaneEndpoint e = Endpoints[i];
                if (e.Node != node || e.Edge == lane || e.Incoming == sIn) continue;
                if (Net.Edges[e.Edge].Direction != s.Direction) continue;   // same world travel direction
                float d = (e.NodePos - sPos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = e.Edge; }
            }
            return best;
        }

        // Resolve which in/out lane each selection extends, from the drag direction: pulling in a lane's travel direction
        // uses the INCOMING (blue) lane; pulling against it uses the OUTGOING (green) lane. Swaps each _extLanes entry to
        // its through-partner when its in/out type doesn't match the drag (symmetric → safe to re-run every frame).
        static void ResolveExtBySwap(Vector2 dragDir)
        {
            if (_extNode < 0 || dragDir.sqrMagnitude < 1e-6f) return;
            for (int i = 0; i < _extLanes.Count; i++)
            {
                int li = _extLanes[i];
                if (li < 0 || li >= Net.Edges.Count) continue;
                LaneEdge s = Net.Edges[li];
                int other = _extNode == s.A ? s.B : s.A;
                if (other < 0 || other >= Net.Nodes.Count) continue;
                Vector2 travel = s.Direction == 2 ? (Net.Nodes[s.B] - Net.Nodes[s.A]) : (Net.Nodes[s.A] - Net.Nodes[s.B]);
                if (travel.sqrMagnitude < 1e-6f) continue;
                bool wantIn = Vector2.Dot(dragDir, travel) > 0f;            // pulling in travel direction → incoming lane
                bool sIn = (s.Direction == 2 && _extNode == s.B) || (s.Direction == 0 && _extNode == s.A);
                if (sIn != wantIn) { int p = ThroughPartner(_extNode, li); if (p >= 0) _extLanes[i] = p; }
            }
        }

        // Navigable (drivable) lane count of a profile — drives how many lanes an extension click grabs.
        static int ProfileLaneCount(string profileId) { ProfileLaneSplit(profileId, out int nBA, out int nAB); return Mathf.Max(1, nBA + nAB); }

        // Does the profile carry navigable lanes in BOTH travel directions (a two-way road)? Drives whether an extension
        // click grabs both sides of the cross-section (two-way continuation) or one direction only (one-way fork).
        static bool ProfileIsTwoWay(string profileId) { ProfileLaneSplit(profileId, out int nBA, out int nAB); return nBA > 0 && nAB > 0; }

        // Per-direction navigable lane counts of a profile (nBA = BA/left zone, nAB = AB/right zone). Drives a direction-
        // balanced extension grab so a two-way pull can't mis-split (e.g. 3+1 off a 3x2) — see ComputeExtendGroup.
        static void ProfileLaneSplit(string profileId, out int nBA, out int nAB)
        {
            nBA = 0; nAB = 0;
            var cfg = RoadProfileLibrary.ResolveConfig(profileId);
            if (cfg == null || cfg.Corridor == null) return;
            var bands = new List<RoadCrossSectionBuilder.StackBand>();
            RoadCrossSectionBuilder.FromStack(cfg.Corridor, bands);
            foreach (var bd in bands)
                if (bd.Type == Model.CorridorType.Traffic || bd.Type == Model.CorridorType.Turn || bd.Type == Model.CorridorType.Bike)
                { if (bd.Zone == 0) nBA++; else nAB++; }
        }

        public static void CancelExtend() { _extNode = -1; _extLanes.Clear(); _extCornerPending = false; ExtFlipSide = false; }

        // ── C-connect: hold C, click two existing nodes → a smooth curve between them. The cubic leaves node A tangent to
        // A's road and arrives at node B tangent to B's road, so an S-curve (parallel-offset ends) or a simple bend (angled
        // ends) falls out of the geometry automatically — no mode to pick. Carries node A's lanes; built as a plan corridor.
        static int _connA = -1, _connEdgeA = -1;
        public static bool Connecting => _connA >= 0;
        public static int ConnectStage => _connA < 0 ? 1 : 2;   // cursor numeral: 1 before the first click, 2 after
        public static void CancelConnect() { _connA = -1; _connEdgeA = -1; }

        // Nearest lane endpoint (the unified puck position) within r — gives the specific lane + node you're pointing at.
        static bool NearestLaneEndpoint(Vector2 xz, float r, out int node, out int edge)
        {
            node = -1; edge = -1; int bi = -1; float best = r * r;
            for (int i = 0; i < Endpoints.Count; i++)
            { float d = (Endpoints[i].NodePos - xz).sqrMagnitude; if (d < best) { best = d; bi = i; } }
            if (bi < 0) return false;
            node = Endpoints[bi].Node; edge = Endpoints[bi].Edge; return true;
        }

        // Cursor snap: world position of the nearest lane puck within r (for the ring to jump onto a node).
        public static bool TrySnapCursor(Vector2 xz, float r, out Vector2 pos)
        {
            pos = xz;
            if (!NearestLaneEndpoint(xz, r, out _, out int e) || e < 0) return false;
            for (int i = 0; i < Endpoints.Count; i++) if (Endpoints[i].Edge == e) { pos = Endpoints[i].NodePos; return true; }
            return false;
        }

        // Puck snap: among lane pucks within m metres of the mouse line-of-sight, pick the one nearest the mouse ON SCREEN
        // — i.e. the puck literally under the cursor, robust to camera angle/zoom/parallax. Returns its xz + world pos + ids.
        public static bool SnapLanePuckToRay(Camera cam, Vector2 mouseScreen, float m, Func<Vector2, float> groundAt, out Vector2 xz, out Vector3 worldPos, out int node, out int edge)
        {
            xz = Vector2.zero; worldPos = Vector3.zero; node = -1; edge = -1;
            if (cam == null) return false;
            int best = -1; float bestScreenSq = float.MaxValue, m2 = m * m;
            Ray ray = cam.ScreenPointToRay(mouseScreen);
            Vector3 ro = ray.origin, rd = ray.direction.normalized;
            for (int i = 0; i < Endpoints.Count; i++)
            {
                Vector2 np = Endpoints[i].NodePos;
                Vector3 wp = new Vector3(np.x, (groundAt != null ? groundAt(np) : 0f) + 0.6f, np.y);
                Vector3 to = wp - ro; float t = Vector3.Dot(to, rd);
                if (t <= 0f) continue;
                if ((to - rd * t).sqrMagnitude >= m2) continue;        // gate: within M of the line of sight
                Vector3 sp = cam.WorldToScreenPoint(wp); if (sp.z <= 0f) continue;
                float dsq = (new Vector2(sp.x, sp.y) - mouseScreen).sqrMagnitude;   // pick the closest ON SCREEN
                if (dsq < bestScreenSq) { bestScreenSq = dsq; best = i; worldPos = wp; }
            }
            if (best < 0) return false;
            xz = Endpoints[best].NodePos; node = Endpoints[best].Node; edge = Endpoints[best].Edge; return true;
        }

        // node/edge come straight from the ray snap (the exact puck under the cursor) — no re-derivation, so the curve
        // ends on the node you clicked, not a coincident neighbour.
        public static bool ConnectClick(int node, int edge, Func<Vector2, float> groundAt)
        {
            if (node < 0) return false;   // both clicks must land on a snapped lane puck
            if (_connA < 0) { _connA = node; _connEdgeA = edge; Rebuild(groundAt); return true; }
            if (node != _connA) BuildConnectCurve(_connA, _connEdgeA, node, edge, groundAt);
            _connA = -1; _connEdgeA = -1;
            return true;
        }

        static Vector2 SafeDir(Vector2 v) => v.sqrMagnitude < 1e-6f ? Vector2.right : v.normalized;
        static bool IsIncomingAt(int edge, int node)
        { LaneEdge e = Net.Edges[edge]; return (e.Direction == 2 && node == e.B) || (e.Direction == 0 && node == e.A); }

        // Force a manual (non-auto) through-flow between two lanes at a node — one must be incoming, the other outgoing.
        static void AddManualFlow(int node, int e1, int e2)
        {
            if (e1 < 0 || e2 < 0 || e1 >= Net.Edges.Count || e2 >= Net.Edges.Count) return;
            bool in1 = IsIncomingAt(e1, node), in2 = IsIncomingAt(e2, node);
            if (in1 == in2) return;                                  // need one in + one out to form a movement
            int from = in1 ? e1 : e2, to = in1 ? e2 : e1;
            Net.Flows.RemoveAll(f => f.Node == node && (f.FromEdge == from || f.ToEdge == to));
            Net.Flows.Add(new LaneFlow { Node = node, FromEdge = from, ToEdge = to, Auto = false });
        }

        // Peel: when a clicked lane sits laterally off its cluster centre, give it a graph endpoint AT its puck position so
        // the connector can attach there. Returns the new node (or the cluster node when no peel is needed). Keeps the
        // source corridor's path stable (never lets the peeled lane define Lanes[0]) and drops the lane's old movements.
        static bool NeedsPeel(int edge)
        {
            if (edge < 0 || edge >= Net.Edges.Count) return false;
            LaneEdge e = Net.Edges[edge];
            if (Mathf.Abs(e.Offset) <= 0.05f) return false;
            Corridor c = (e.CorridorId >= 0 && e.CorridorId < Net.Corridors.Count) ? Net.Corridors[e.CorridorId] : null;
            return c != null && c.Lanes.Count > 1;   // a 1-lane corridor's only lane IS its path → peeling moves the road (skew) and strands the peel node on delete
        }

        static int PeelLaneEndpoint(int clusterNode, int edge, Func<Vector2, float> groundAt)
        {
            if (edge < 0 || edge >= Net.Edges.Count) return clusterNode;
            LaneEdge e = Net.Edges[edge];
            if (e.A != clusterNode && e.B != clusterNode) return clusterNode;
            Vector2 puck; if (!TryEndpointPos(clusterNode, edge, out puck, out _)) puck = Net.Nodes[clusterNode];
            int pn = Net.AddNode(puck);
            if (e.A == clusterNode) e.A = pn; else e.B = pn;             // re-point the clicked end onto the puck node
            if (e.CorridorId >= 0 && e.CorridorId < Net.Corridors.Count)
            {
                Corridor c = Net.Corridors[e.CorridorId];
                if (c.Lanes.Count > 1 && c.Lanes[0] == edge)             // don't let the peeled lane define the corridor path
                    for (int i = 1; i < c.Lanes.Count; i++)
                    { LaneEdge sib = Net.Edges[c.Lanes[i]]; if (sib.A == clusterNode || sib.B == clusterNode) { c.Lanes[0] = c.Lanes[i]; c.Lanes[i] = edge; break; } }
                c.AlignLanes = true;                                     // remaining lanes keep their offsets
            }
            Net.Flows.RemoveAll(f => f.Node == clusterNode && (f.FromEdge == edge || f.ToEdge == edge));
            return pn;
        }

        // A centred 1-lane connector corridor whose single lane IS the cubic nodeA→nodeB (Offset 0, not AlignLanes) so the
        // body runs puck-A → puck-B. Copies kind/width/shoulders from the source lane. Returns the connector lane id.
        static int BuildConnectorCorridor(int nodeA, int nodeB, int srcEdge, bool curved, Vector2 ctrlA, Vector2 ctrlB)
        {
            Corridor cc = Net.AddCorridor();
            cc.Curved = curved; cc.ControlA = ctrlA; cc.ControlB = ctrlB; cc.AlignLanes = false;
            LaneEdge s = (srcEdge >= 0 && srcEdge < Net.Edges.Count) ? Net.Edges[srcEdge] : null;
            Corridor sc = (s != null && s.CorridorId >= 0 && s.CorridorId < Net.Corridors.Count) ? Net.Corridors[s.CorridorId] : null;
            if (sc != null) { cc.Profile = sc.Profile; cc.ShoulderBA = sc.ShoulderBA; cc.ShoulderAB = sc.ShoulderAB; }
            int dir = (s != null && IsIncomingAt(srcEdge, nodeA)) ? 2 : 0;   // arriving at A → continue outward (A→B)
            int li = Net.AddLane(new LaneEdge { A = nodeA, B = nodeB, CorridorId = cc.Id, Kind = s != null ? s.Kind : LaneKind.Traffic, Direction = dir, Width = s != null ? s.Width : 3.5f, Offset = 0f });
            cc.Lanes.Add(li);
            return li;
        }

        static void BuildConnectCurve(int a, int edgeA, int b, int edgeB, Func<Vector2, float> groundAt)
        {
            if (a < 0 || b < 0 || a == b || a >= Net.Nodes.Count || b >= Net.Nodes.Count) return;
            if (edgeA < 0 || edgeA >= Net.Edges.Count) return;
            // Road tangents from the CLICKED lanes, read BEFORE peeling (peeling moves the lane off its cluster, so a
            // cluster-keyed lookup would fail for a 1-lane source and fall back to a straight line). Oriented toward the
            // other end so the curve leaves each lane along its own road → a smooth tangent-continuous S / bend.
            Vector2 pa0 = Net.Nodes[a], pb0 = Net.Nodes[b];
            TryEndpointPos(a, edgeA, out pa0, out _);
            if (edgeB >= 0 && edgeB < Net.Edges.Count) TryEndpointPos(b, edgeB, out pb0, out _);
            Vector2 tanA = SafeDir(pb0 - pa0);
            if (LaneGuideFrame(a, edgeA, out _, out Vector2 ta)) tanA = Vector2.Dot(ta, pb0 - pa0) >= 0f ? ta : -ta;
            Vector2 tanB = SafeDir(pa0 - pb0);
            if (edgeB >= 0 && edgeB < Net.Edges.Count && LaneGuideFrame(b, edgeB, out _, out Vector2 tb)) tanB = Vector2.Dot(tb, pa0 - pb0) >= 0f ? tb : -tb;
            // Peel each clicked lane onto a node at its puck so the connector lands on BOTH clicked lanes (visual realign).
            bool peelA = NeedsPeel(edgeA), peelB = edgeB >= 0 && edgeB < Net.Edges.Count && NeedsPeel(edgeB);
            int nodeA = peelA ? PeelLaneEndpoint(a, edgeA, groundAt) : a;
            int nodeB = peelB ? PeelLaneEndpoint(b, edgeB, groundAt) : b;
            Vector2 pa = Net.Nodes[nodeA], pb = Net.Nodes[nodeB];
            float d = (pb - pa).magnitude * 0.4f;
            int cl = BuildConnectorCorridor(nodeA, nodeB, edgeA, true, pa + tanA * d, pb + tanB * d);
            RegenerateDefaultFlows(groundAt);                            // auto-connects at the (shared) peel nodes
            if (!peelA) AddManualFlow(a, cl, edgeA);                     // unpeeled cluster ends still need an explicit route
            if (!peelB && edgeB >= 0 && edgeB < Net.Edges.Count) AddManualFlow(b, cl, edgeB);
            Rebuild(groundAt);
        }

        // ── connect guides: colinear + perpendicular dashed-yellow guides off the nodes near the cursor while connecting.
        static GameObject _connGuideGo; static Mesh _connGuideMesh; static Material _connGuideMat;
        static Material ConnGuideMat() => _connGuideMat != null ? _connGuideMat
            : (_connGuideMat = NetworkDesigner.PipelineMaterials.CreateUnlitColor(new Color(1f, 0.92f, 0.2f, 1f), "LaneConnectGuide"));
        public static void ClearConnectGuides() { if (_connGuideGo != null) _connGuideGo.SetActive(false); }

        // Live preview of the would-be connector curve from the first-clicked lane puck to the cursor / snapped target lane
        // (same tangent-continuous cubic BuildConnectCurve will create). Sampled as short segments into the guide mesh.
        static void AppendConnectPreview(Vector2 toXz, int toNode, int toEdge, Func<Vector2, float> groundAt)
        {
            if (!TryEndpointPos(_connA, _connEdgeA, out Vector2 fromP, out _)) return;
            Vector2 toP = toXz;
            if (toNode >= 0 && toEdge >= 0 && toEdge < Net.Edges.Count && TryEndpointPos(toNode, toEdge, out Vector2 tp, out _)) toP = tp;
            if ((toP - fromP).sqrMagnitude < 1f) return;
            Vector2 tanA = SafeDir(toP - fromP);
            if (LaneGuideFrame(_connA, _connEdgeA, out _, out Vector2 ta)) tanA = Vector2.Dot(ta, toP - fromP) >= 0f ? ta : -ta;
            Vector2 tanB = SafeDir(fromP - toP);
            if (toNode >= 0 && toEdge >= 0 && toEdge < Net.Edges.Count && LaneGuideFrame(toNode, toEdge, out _, out Vector2 tb)) tanB = Vector2.Dot(tb, fromP - toP) >= 0f ? tb : -tb;
            float d = (toP - fromP).magnitude * 0.4f;
            Vector2 c1 = fromP + tanA * d, c2 = toP + tanB * d;
            Vector2 prev = fromP; const int N = 24;
            for (int i = 1; i <= N; i++)
            {
                float t = i / (float)N, u = 1f - t;
                Vector2 pt = u * u * u * fromP + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * toP;
                EmitDashGuide(prev, pt, groundAt);
                prev = pt;
            }
        }

        // A node emits guides when within N (PlanGuides.GuideRange) of the cursor; the first-clicked node always emits so
        // the 2nd click can align colinear/perpendicular to it. Guide length = PlanGuides.ExtensionGuideLength.
        public static void UpdateConnectGuides(Vector2 cursor, int snapNode, int snapEdge, Func<Vector2, float> groundAt)
        {
            if (_connGuideGo == null)
            {
                _connGuideGo = new GameObject("LaneConnectGuides");
                _connGuideGo.AddComponent<MeshFilter>();
                var mr0 = _connGuideGo.AddComponent<MeshRenderer>();
                mr0.sharedMaterial = ConnGuideMat();
                mr0.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr0.receiveShadows = false;
                _connGuideMesh = new Mesh { name = "LaneConnectGuides" };
                _connGuideGo.GetComponent<MeshFilter>().sharedMesh = _connGuideMesh;
            }
            float n = Mathf.Max(1f, NetworkDesigner.Terrain.PlanGuides.GuideRange);
            float len = Mathf.Max(1f, NetworkDesigner.Terrain.PlanGuides.ExtensionGuideLength);
            _sgVerts.Clear(); _sgTris.Clear();
            int bi = -1; float bestSq = n * n;   // nearest LANE PUCK within N (guides emit from the lane node, not the cluster)
            for (int i = 0; i < Endpoints.Count; i++) { float d = (Endpoints[i].NodePos - cursor).sqrMagnitude; if (d < bestSq) { bestSq = d; bi = i; } }
            // Each emitting node draws a colinear line to the cursor; the arrow shows that lane's direction of travel.
            if (bi >= 0) AddLaneGuides(Endpoints[bi].Node, Endpoints[bi].Edge, cursor, len, groundAt);
            if (_connA >= 0 && _connEdgeA >= 0)
            {
                AddLaneGuides(_connA, _connEdgeA, cursor, len, groundAt);
                AppendConnectPreview(cursor, snapNode, snapEdge, groundAt);   // live preview of the would-be connector curve
            }
            _connGuideMesh.Clear();
            if (_sgVerts.Count == 0) { _connGuideGo.SetActive(false); return; }
            _connGuideMesh.SetVertices(_sgVerts); _connGuideMesh.SetTriangles(_sgTris, 0); _connGuideMesh.RecalculateBounds();
            _connGuideGo.SetActive(true);
        }

        // Tangent of a lane (node, edge) at the node, and the lane puck position. False if degenerate.
        static bool LaneGuideFrame(int node, int edge, out Vector2 p, out Vector2 tan)
        {
            p = Vector2.zero; tan = Vector2.zero;
            if (edge < 0 || edge >= Net.Edges.Count) return false;
            LaneEdge e = Net.Edges[edge];
            Corridor c = (e.CorridorId >= 0 && e.CorridorId < Net.Corridors.Count) ? Net.Corridors[e.CorridorId] : null;
            if (c == null) return false;
            bool have = false;
            for (int i = 0; i < Endpoints.Count; i++)
                if (Endpoints[i].Node == node && Endpoints[i].Edge == edge) { p = Endpoints[i].NodePos; have = true; break; }
            if (!have) return false;
            tan = LaneEdgeCorridorBuilder.PathTangent(Net, c, node == e.A ? 0f : 1f);
            if (tan.sqrMagnitude < 1e-6f) return false;
            tan.Normalize(); return true;
        }

        // Guides off an individual LANE puck (not the cluster centre), along that lane's direction. Shows only the guide
        // the cursor is lined up with — colinear if more along the road, else perpendicular.
        static void AddLaneGuides(int node, int edge, Vector2 cursor, float len, Func<Vector2, float> groundAt)
        {
            if (!LaneGuideFrame(node, edge, out Vector2 p, out Vector2 tan)) return;
            Vector2 perp = new Vector2(-tan.y, tan.x);
            Vector2 to = cursor - p; if (to.sqrMagnitude < 1e-4f) return; Vector2 toN = to.normalized;
            // Only show a guide when the cursor is actually LINED UP with the node — within GuideConeDeg of the lane axis
            // (colinear) or of its perpendicular. Outside both cones emit nothing, instead of the old 45° split that lit a
            // colinear guide whenever the cursor was merely nearby.
            float coneCos = Mathf.Cos(NetworkDesigner.Terrain.PlanGuides.GuideConeDeg * Mathf.Deg2Rad);
            float axisDot = Mathf.Abs(Vector2.Dot(toN, tan)), perpDot = Mathf.Abs(Vector2.Dot(toN, perp));
            if (axisDot >= coneCos && axisDot >= perpDot)
            {
                // Colinear extension: a line ALWAYS along the lane axis (cursor projected onto it, so it never angles
                // off), out to the cursor's projection. Arrow points along the lane's DIRECTION OF TRAVEL.
                Vector2 proj = p + tan * Vector2.Dot(cursor - p, tan);
                EmitDashGuide(p, proj, groundAt);
                Vector2 travel = Net.Edges[edge].Direction == 2 ? tan : -tan;   // tan is A→B; AB travels +tan, BA travels −tan
                EmitGuideArrow((p + proj) * 0.5f, travel, groundAt);
            }
            else if (perpDot >= coneCos)
            {
                // Perpendicular guide also tracks the cursor (projected onto the perpendicular axis) instead of a full
                // ExtensionGuideLength line spanning the screen.
                Vector2 projp = p + perp * Vector2.Dot(cursor - p, perp);
                EmitDashGuide(p, projp, groundAt);
            }
        }

        // Small filled triangle (into the guide mesh) at ctr pointing along `along` — the colinear guide's direction arrow.
        static void EmitGuideArrow(Vector2 ctr, Vector2 along, Func<Vector2, float> groundAt)
        {
            Vector2 lat = new Vector2(along.y, -along.x);
            const float fwd = 1.8f, back = 1.1f, half = 1.0f;
            Vector3 tip = Drape(ctr + along * fwd, groundAt), bl = Drape(ctr - along * back - lat * half, groundAt), br = Drape(ctr - along * back + lat * half, groundAt);
            int s = _sgVerts.Count; _sgVerts.Add(tip); _sgVerts.Add(bl); _sgVerts.Add(br);
            _sgTris.Add(s); _sgTris.Add(s + 1); _sgTris.Add(s + 2); _sgTris.Add(s); _sgTris.Add(s + 2); _sgTris.Add(s + 1);
        }

        // Snap the cursor onto the nearest lane puck's colinear/perpendicular guide line, within GuideSnapRadius.
        public static bool SnapToGuideLine(Vector2 cursor, out Vector2 snapped)
        {
            snapped = cursor;
            float n = Mathf.Max(1f, NetworkDesigner.Terrain.PlanGuides.GuideRange);
            float r = Mathf.Max(0.01f, NetworkDesigner.Terrain.PlanGuides.GuideSnapRadius); float r2 = r * r;
            int bi = -1; float bestSq = n * n;
            for (int i = 0; i < Endpoints.Count; i++) { float d = (Endpoints[i].NodePos - cursor).sqrMagnitude; if (d < bestSq) { bestSq = d; bi = i; } }
            if (bi < 0) return false;
            if (!LaneGuideFrame(Endpoints[bi].Node, Endpoints[bi].Edge, out Vector2 p, out Vector2 tan)) return false;
            Vector2 perp = new Vector2(-tan.y, tan.x);
            Vector2 projC = p + tan * Vector2.Dot(cursor - p, tan);     // onto the colinear line
            Vector2 projP = p + perp * Vector2.Dot(cursor - p, perp);   // onto the perpendicular line
            float dC = (projC - cursor).sqrMagnitude, dP = (projP - cursor).sqrMagnitude;
            if (dC <= dP && dC < r2) { snapped = projC; return true; }
            if (dP < dC && dP < r2) { snapped = projP; return true; }
            return false;
        }

        static void EmitDashGuide(Vector2 a, Vector2 b, Func<Vector2, float> groundAt)
        {
            Vector2 dir = b - a; float len = dir.magnitude; if (len < 0.1f) return; dir /= len;
            Vector2 pr = new Vector2(-dir.y, dir.x) * (LineHalfW * 0.5f);   // thin guide line
            const float dash = 2.5f, gap = 2f; float walked = 0f;
            while (walked < len)
            {
                float seg = Mathf.Min(dash, len - walked);
                Vector2 q0 = a + dir * walked, q1 = a + dir * (walked + seg);
                Vector3 l0 = Drape(q0 - pr, groundAt), r0 = Drape(q0 + pr, groundAt), l1 = Drape(q1 - pr, groundAt), r1 = Drape(q1 + pr, groundAt);
                int s = _sgVerts.Count; _sgVerts.Add(l0); _sgVerts.Add(r0); _sgVerts.Add(r1); _sgVerts.Add(l1);
                _sgTris.Add(s); _sgTris.Add(s + 1); _sgTris.Add(s + 2); _sgTris.Add(s); _sgTris.Add(s + 2); _sgTris.Add(s + 3);
                _sgTris.Add(s); _sgTris.Add(s + 2); _sgTris.Add(s + 1); _sgTris.Add(s); _sgTris.Add(s + 3); _sgTris.Add(s + 2);
                walked += dash + gap;
            }
        }

        // Draw the extension end (straight, or shift-curve through a bend). Builds the aligned subset corridor + lets the
        // default-flow regen wire continuations (selected) and merges (dropped). Mirrors Click's straight/curve flow.
        public static bool ExtendClick(Vector2 xz, Func<Vector2, float> groundAt, bool curveModifier, bool limitRadius, float minRadius, string profileId, float designSpeedKmh = 40f)
        {
            if (!Extending) return false;
            Vector2 start = Net.Nodes[_extNode];
            // Resolve in/out from the pull direction (first leg): pulling in a lane's travel direction extends its incoming
            // lane, against it the outgoing — so it never matters which unified puck you clicked.
            ResolveExtBySwap((_extCornerPending ? _extCorner : xz) - start);

            if (_extCornerPending)
            {
                Vector2 end = xz;
                if ((end - start).sqrMagnitude < 1f) return false;
                CurveControls(start, end, _extCorner, out Vector2 c1, out Vector2 c2);
                if (limitRadius && MinCurveRadius(start, c1, c2, end) < minRadius)
                { Debug.LogWarning("[LaneEdgeWorld] extension curve too tight — pick a wider end"); return false; }
                int bm = Net.AddNode(end);
                BuildExtensionCorridor(_extNode, bm, true, c1, c2, profileId);
                ApplyExitPull(Net.Corridors.Count - 1, groundAt, designSpeedKmh);
                CancelExtend(); RegenerateDefaultFlows(groundAt); Rebuild(groundAt); return true;
            }
            if (curveModifier) { _extCorner = xz; _extCornerPending = true; return false; }

            if ((xz - start).sqrMagnitude < 1f) return false;
            int b = Net.AddNode(xz);
            BuildExtensionCorridor(_extNode, b, false, default, default, profileId);
            ApplyExitPull(Net.Corridors.Count - 1, groundAt, designSpeedKmh);
            CancelExtend(); RegenerateDefaultFlows(groundAt); Rebuild(groundAt); return true;
        }

        // Build a corridor (nodeN→nodeM) whose lanes copy the selected source lanes, positioned (via Offset projection +
        // AlignLanes/CenterU) to sit exactly on the source lanes at nodeN so the fork is geometrically continuous. If the
        // active profile asks for MORE lanes per direction than the source provided (e.g. continue a 2x2 with a 2x3 to
        // replace a lane that peeled off to a ramp), the surplus is appended OUTBOARD — see AppendOutboard.
        static void BuildExtensionCorridor(int nodeN, int nodeM, bool curved, Vector2 c1, Vector2 c2, string profileId)
        {
            Corridor nc = Net.AddCorridor();
            nc.Curved = curved; nc.ControlA = c1; nc.ControlB = c2; nc.AlignLanes = true;
            Vector2 N = Net.Nodes[nodeN], M = Net.Nodes[nodeM];
            Vector2 tanN = curved ? GeometryResolver.CubicTangent(N, c1, c2, M, 0f) : (M - N);
            tanN = tanN.sqrMagnitude < 1e-8f ? Vector2.right : tanN.normalized;
            Vector2 frNew = new Vector2(tanN.y, -tanN.x);
            foreach (int sl in _extLanes)
            {
                if (sl < 0 || sl >= Net.Edges.Count) continue;
                LaneEdge s = Net.Edges[sl];
                Corridor sc = (s.CorridorId >= 0 && s.CorridorId < Net.Corridors.Count) ? Net.Corridors[s.CorridorId] : null;
                Vector2 frS;
                if (sc != null) { Vector2 ts = LaneEdgeCorridorBuilder.PathTangent(Net, sc, nodeN == s.A ? 0f : 1f); frS = new Vector2(ts.y, -ts.x); }
                else { Vector2 cd = Net.Nodes[s.B] - Net.Nodes[s.A]; frS = cd.sqrMagnitude < 1e-6f ? Vector2.right : new Vector2(cd.normalized.y, -cd.normalized.x); }
                float oNew = (s.Offset * (Vector2.Dot(frS, frNew) >= 0f ? 1f : -1f));   // preserve the lane's offset magnitude (re-spread on the new frame) so a turn doesn't collapse lanes to centre
                bool incomingAtN = (s.Direction == 2 && nodeN == s.B) || (s.Direction == 0 && nodeN == s.A);
                int dirNew = incomingAtN ? 2 : 0;                  // arriving lane → continue outward (A'=N→B'); return lane → inbound
                int li = Net.AddLane(new LaneEdge { A = nodeN, B = nodeM, CorridorId = nc.Id, Kind = s.Kind, Direction = dirNew, Width = s.Width, Offset = oNew });
                nc.Lanes.Add(li);
                if (string.IsNullOrEmpty(nc.Profile) && sc != null) { nc.Profile = sc.Profile; nc.ShoulderBA = sc.ShoulderBA; nc.ShoulderAB = sc.ShoulderAB; nc.MedianWidth = sc.MedianWidth; }
            }
            // Lane addition: append surplus profile lanes outboard on each side (median stays aligned, through-lanes stay
            // put). Scoped to TWO-WAY profiles so one-way forks (confirmed working) keep their copy-grabbed-lanes behaviour.
            ProfileLaneSplit(profileId, out int wantBA, out int wantAB);
            // Pulling off the source's A end inverts each lane's direction label in the extension (incomingAtN flips
            // dirNew), so the profile's per-direction counts must be swapped to match — else the surplus lands on the
            // already-full side and a 2x3 pulled off that end wrongly grows to 3x3. Independent of (and composed with) F.
            if (_extLanes.Count > 0 && _extLanes[0] >= 0 && _extLanes[0] < Net.Edges.Count)
            {
                LaneEdge s0 = Net.Edges[_extLanes[0]];
                bool inc0 = (s0.Direction == 2 && nodeN == s0.B) || (s0.Direction == 0 && nodeN == s0.A);
                if ((inc0 ? 2 : 0) != s0.Direction) { int t = wantBA; wantBA = wantAB; wantAB = t; }
            }
            if (ExtFlipSide) { int t = wantBA; wantBA = wantAB; wantAB = t; }   // F: mirror which side gains the surplus lane
            if (wantBA > 0 && wantAB > 0) { AppendOutboard(nc, 0, wantBA); AppendOutboard(nc, 2, wantAB); }
            Net.SortCorridorLanes(nc);
        }

        // Append (want − current) navigable lanes in travel direction `dir` OUTBOARD of the corridor's outermost lane on
        // that side, matching its width (continuity). dir 0 = BA (offsets < 0, outboard = more negative); dir 2 = AB
        // (offsets > 0, outboard = more positive). The added lane begins here (no source feed) — the flow matcher leaves
        // it unconnected at nodeN and traffic merges into it downstream. No-op when there's no anchor lane or already ≥want.
        static void AppendOutboard(Corridor nc, int dir, int want)
        {
            if (nc.Lanes.Count == 0) return;
            int a = Net.Edges[nc.Lanes[0]].A, b = Net.Edges[nc.Lanes[0]].B;
            int cur = 0; float outerOff = 0f, outerW = 3.5f; bool any = false;
            foreach (int li in nc.Lanes)
            {
                LaneEdge e = Net.Edges[li];
                if (e.Direction != dir || e.Kind == LaneKind.Sidewalk) continue;
                cur++;
                if (!any || Mathf.Abs(e.Offset) > Mathf.Abs(outerOff)) { outerOff = e.Offset; outerW = e.Width; any = true; }
            }
            if (!any || cur >= want) return;
            float sign = outerOff < 0f ? -1f : 1f;
            float edge = Mathf.Abs(outerOff) + outerW * 0.5f;   // outer edge of the current outermost lane on this side
            for (int k = cur; k < want; k++)
            {
                float off = sign * (edge + outerW * 0.5f);
                int ni = Net.AddLane(new LaneEdge { A = a, B = b, CorridorId = nc.Id, Kind = LaneKind.Traffic, Direction = dir, Width = outerW, Offset = off });
                nc.Lanes.Add(ni);
                edge = Mathf.Abs(off) + outerW * 0.5f;
            }
        }

        // Flip the corridor under the cursor in place: mirror its cross-section about the centreline (negate each lane's
        // offset + swap its travel direction, swap the shoulders), so an asymmetric segment's surplus/asymmetry moves to
        // the other side while each direction stays on its proper side (a 2x3 → 3x2). Symmetric roads look unchanged.
        public static bool FlipCorridorAt(Vector2 xz, Func<Vector2, float> groundAt)
        {
            int best = -1; float bestD = float.PositiveInfinity;
            for (int ci = 0; ci < Net.Corridors.Count; ci++)
            {
                if (Net.Corridors[ci].Lanes.Count == 0) continue;
                float halfW = LaneEdgeCorridorBuilder.BuildCrossSection(Net.Corridors[ci], Net).Width * 0.5f + 1.5f;
                float d = CorridorDistSq(Net.Corridors[ci], xz);
                if (d < halfW * halfW && d < bestD) { bestD = d; best = ci; }
            }
            if (best < 0) return false;
            Corridor c = Net.Corridors[best];
            foreach (int li in c.Lanes)
            {
                LaneEdge e = Net.Edges[li];
                e.Offset = -e.Offset;
                e.Direction = e.Direction == 0 ? 2 : 0;
            }
            float t = c.ShoulderBA; c.ShoulderBA = c.ShoulderAB; c.ShoulderAB = t;
            Net.SortCorridorLanes(c);
            RegenerateDefaultFlows(groundAt);
            Rebuild(groundAt);
            return true;
        }

        // ── deletion: right-click a node (deletes its corridors) or a segment body (deletes that corridor) ──
        // Rebuilds the whole net with compacted indices (edges/nodes/corridors/flows all reindex), so it's robust.
        public static bool DeleteAt(Vector2 xz, Func<Vector2, float> groundAt)
        {
            var dead = new HashSet<int>();
            int node = NearestCluster(xz, 8f);
            if (node >= 0)   // near a node → delete every corridor touching it
            {
                for (int ci = 0; ci < Net.Corridors.Count; ci++)
                {
                    Corridor c = Net.Corridors[ci]; if (c.Lanes.Count == 0) continue;
                    LaneEdge l0 = Net.Edges[c.Lanes[0]];
                    if (l0.A == node || l0.B == node) dead.Add(ci);
                }
            }
            if (dead.Count == 0)   // else nearest corridor body
            {
                int best = -1; float bestD = float.PositiveInfinity;
                for (int ci = 0; ci < Net.Corridors.Count; ci++)
                {
                    float halfW = LaneEdgeCorridorBuilder.BuildCrossSection(Net.Corridors[ci], Net).Width * 0.5f + 1.5f;
                    float d = CorridorDistSq(Net.Corridors[ci], xz);
                    if (d < halfW * halfW && d < bestD) { bestD = d; best = ci; }
                }
                if (best >= 0) dead.Add(best);
            }
            if (dead.Count == 0) return false;
            DeleteCorridors(dead, groundAt);
            return true;
        }

        static float CorridorDistSq(Corridor c, Vector2 xz)
        {
            if (c.Lanes.Count == 0) return float.PositiveInfinity;
            // An AlignLanes corridor (extension/ramp) lays its lanes at their actual Offset, so the body sits OFFSET from
            // the centreline (which runs through the node). Sampling the bare centreline misses it — worst for a lone
            // 1-lane ramp whose body is metres to the side. Shift samples to the lane-span centre to measure to the real
            // asphalt. Centred corridors have bodyMid≈0, so this is a no-op for them (no regression).
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (int li in c.Lanes)
            { LaneEdge e = Net.Edges[li]; lo = Mathf.Min(lo, e.Offset - e.Width * 0.5f); hi = Mathf.Max(hi, e.Offset + e.Width * 0.5f); }
            float bodyMid = (lo + hi) * 0.5f;
            float len = LaneEdgeCorridorBuilder.PathLength(Net, c);
            int n = Mathf.Clamp(Mathf.CeilToInt(len / 3f), 2, 96);
            float best = float.PositiveInfinity;
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                Vector2 p = LaneEdgeCorridorBuilder.PathPoint(Net, c, t)
                          + LaneEdgeCorridorBuilder.PathRight(LaneEdgeCorridorBuilder.PathTangent(Net, c, t)) * bodyMid;
                best = Mathf.Min(best, (p - xz).sqrMagnitude);
            }
            return best;
        }

        // Exit pull: after the ramp is built, the pulled lane (which FEEDS the exit) STAYS — what gets deleted is the lane
        // it used to continue into on the DOWNSTREAM mainline segment (the through-capacity the exit replaces). For each
        // pulled lane we find that downstream continuation (a lane at the fork node on a corridor that is NOT the source
        // feeder and NOT the ramp) and delete it. DeleteOuter deletes only the outermost, so an inner continuation stays
        // (shared lane). Guards against emptying the downstream corridor; sets it AlignLanes so the split stays put.
        public static float TaperSpeedKmh = 40f;   // design speed used to size lane-drop tapers at Rebuild (set by the UI/pull)

        // MUTCD-ish lane-drop taper length (metric approximation). W = lane width (m), S = design speed (km/h).
        // High speed (≥70 km/h): L ≈ 0.62·W·S (imperial L=WS). Low speed: L ≈ W·S²/155 (imperial L=WS²/60). Clamped sane.
        static float TaperLength(float speedKmh, float width)
        {
            float w = Mathf.Max(1f, width), s = Mathf.Max(5f, speedKmh);
            float L = s >= 70f ? 0.62f * w * s : w * s * s / 155f;
            return Mathf.Clamp(L, 12f, 300f);
        }

        static void ApplyExitPull(int rampCorr, Func<Vector2, float> groundAt, float designSpeedKmh = 40f)
        {
            TaperSpeedKmh = designSpeedKmh;   // remember the design speed so the Rebuild-time taper detection sizes correctly
            if (ExitMode == ExitPullMode.Keep || _extLanes.Count == 0 || _extNode < 0) return;
            int srcCorr = (_extLanes[0] >= 0 && _extLanes[0] < Net.Edges.Count) ? Net.Edges[_extLanes[0]].CorridorId : -1;
            ComputeEndpoints(groundAt);   // include the just-built ramp lanes

            var cand = new List<int>();
            foreach (int sl in _extLanes)
            {
                int d = FindContinuationLane(_extNode, sl, srcCorr, rampCorr);
                if (d >= 0 && !cand.Contains(d)) cand.Add(d);
            }
            if (cand.Count == 0) return;

            var del = new HashSet<int>();
            if (ExitMode == ExitPullMode.DeleteAll)
                foreach (int d in cand) del.Add(d);
            else   // DeleteOuter: only the outermost downstream continuation (largest |Offset|)
            {
                int outer = -1; float mx = -1f;
                foreach (int d in cand) { float a = Mathf.Abs(Net.Edges[d].Offset); if (a > mx) { mx = a; outer = d; } }
                if (outer >= 0) del.Add(outer);
            }
            if (del.Count == 0) return;

            // Guard: never empty a downstream corridor (protects full/colinear continuations). Then flag the affected
            // corridors AlignLanes so the surviving lanes keep their offsets and the split stays aligned.
            var survive = new Dictionary<int, int>();
            foreach (int dl in del) { int ci = Net.Edges[dl].CorridorId; if (ci < 0) continue; if (!survive.ContainsKey(ci)) survive[ci] = Net.Corridors[ci].Lanes.Count; survive[ci]--; }
            foreach (var kv in survive) if (kv.Value <= 0) return;
            foreach (var kv in survive) Net.Corridors[kv.Key].AlignLanes = true;

            // Tapers are NOT recorded here — they're derived generally at Rebuild (DetectLaneDropTapers) from lane-count
            // mismatches at shared nodes, wherever they occur, not pinned to this pull.
            DeleteLanes(del, groundAt);
        }

        // The lane that `pickedLane` continues into THROUGH `node` — same travel direction, best world-position match,
        // on a corridor other than the feeder (srcCorr) or the ramp (rampCorr). Used to delete the downstream through
        // lane an exit replaces. -1 if there's no such continuation (e.g. the node is the end of the mainline).
        static int FindContinuationLane(int node, int pickedLane, int srcCorr, int rampCorr)
        {
            if (pickedLane < 0 || pickedLane >= Net.Edges.Count) return -1;
            LaneEdge p = Net.Edges[pickedLane];
            bool pIncoming = (p.Direction == 2 && node == p.B) || (p.Direction == 0 && node == p.A);
            int po = node == p.A ? p.B : p.A;
            if (po < 0 || po >= Net.Nodes.Count) return -1;
            Vector2 pMotion = pIncoming ? (Net.Nodes[node] - Net.Nodes[po]) : (Net.Nodes[po] - Net.Nodes[node]);
            if (pMotion.sqrMagnitude < 1e-6f) return -1; pMotion.Normalize();
            Vector2 nrm = new Vector2(-pMotion.y, pMotion.x);
            Vector2 pPos = Vector2.zero; bool havePos = false;
            for (int i = 0; i < Endpoints.Count; i++)
                if (Endpoints[i].Node == node && Endpoints[i].Edge == pickedLane && Endpoints[i].Incoming == pIncoming) { pPos = Endpoints[i].Pos; havePos = true; break; }
            if (!havePos) return -1;
            int best = -1; float bestScore = -999f;
            for (int j = 0; j < Endpoints.Count; j++)
            {
                LaneEndpoint e = Endpoints[j];
                if (e.Node != node || e.Incoming == pIncoming) continue;   // continuation is the opposite endpoint type
                LaneEdge oe = Net.Edges[e.Edge];
                if (oe.CorridorId == srcCorr || oe.CorridorId == rampCorr) continue;   // not the feeder, not the ramp
                int oo = e.Node == oe.A ? oe.B : oe.A;
                if (oo < 0 || oo >= Net.Nodes.Count) continue;
                Vector2 eMotion = e.Incoming ? (Net.Nodes[node] - Net.Nodes[oo]) : (Net.Nodes[oo] - Net.Nodes[node]);
                if (eMotion.sqrMagnitude < 1e-6f) continue; eMotion.Normalize();
                float align = Vector2.Dot(pMotion, eMotion);
                if (align <= 0.1f) continue;
                float lateral = Mathf.Abs(Vector2.Dot(e.Pos - pPos, nrm));
                // Only the lane DIRECTLY in line with the pulled lane is its continuation. If that slot was already
                // deleted by a prior pull (e.g. you removed the ramp and re-pulled), the nearest survivor is a lane-width
                // away → rejected here, so a re-pull deletes nothing extra ("delete what's there, no more").
                if (lateral >= p.Width * 0.6f) continue;
                float score = align * 2f - lateral;
                if (score > bestScore) { bestScore = score; best = j; }
            }
            return best >= 0 ? Endpoints[best].Edge : -1;
        }

        // Delete specific lane-edges and rebuild with compacted indices (lane-granular sibling of DeleteCorridors).
        // Corridors keep their surviving lanes; a corridor left with no lanes is dropped.
        public static void DeleteLanes(HashSet<int> deadLanes, Func<Vector2, float> groundAt)
        {
            if (deadLanes == null || deadLanes.Count == 0) return;

            var edgeMap = new Dictionary<int, int>(); var newEdges = new List<LaneEdge>();
            for (int i = 0; i < Net.Edges.Count; i++) { if (deadLanes.Contains(i)) continue; edgeMap[i] = newEdges.Count; newEdges.Add(Net.Edges[i]); }

            var usedNodes = new HashSet<int>(); foreach (LaneEdge e in newEdges) { usedNodes.Add(e.A); usedNodes.Add(e.B); }
            var nodeMap = new Dictionary<int, int>(); var newNodes = new List<Vector2>(); var newNodeY = new List<float>();
            for (int i = 0; i < Net.Nodes.Count; i++) { if (!usedNodes.Contains(i)) continue; nodeMap[i] = newNodes.Count; newNodes.Add(Net.Nodes[i]); newNodeY.Add(Net.GetNodeY(i)); }

            var corrMap = new Dictionary<int, int>(); var newCorridors = new List<Corridor>();
            for (int ci = 0; ci < Net.Corridors.Count; ci++)
            {
                Corridor c = Net.Corridors[ci];
                c.Lanes.RemoveAll(li => deadLanes.Contains(li));
                if (c.Lanes.Count == 0) continue;                    // emptied → drop the corridor
                corrMap[ci] = newCorridors.Count; newCorridors.Add(c);
            }

            foreach (LaneEdge e in newEdges)
            {
                e.A = nodeMap[e.A]; e.B = nodeMap[e.B];
                e.CorridorId = corrMap.TryGetValue(e.CorridorId, out int nc) ? nc : -1;
            }
            for (int i = 0; i < newCorridors.Count; i++)
            {
                Corridor c = newCorridors[i]; c.Id = i;
                for (int k = 0; k < c.Lanes.Count; k++) c.Lanes[k] = edgeMap[c.Lanes[k]];
            }
            var newFlows = new List<LaneFlow>();
            foreach (LaneFlow f in Net.Flows)
            {
                if (deadLanes.Contains(f.FromEdge) || deadLanes.Contains(f.ToEdge) || !nodeMap.ContainsKey(f.Node)) continue;
                f.Node = nodeMap[f.Node]; f.FromEdge = edgeMap[f.FromEdge]; f.ToEdge = edgeMap[f.ToEdge];
                newFlows.Add(f);
            }

            Net.LoadFrom(newNodes, newNodeY, newEdges, newCorridors, newFlows);
            RegenerateDefaultFlows(groundAt);
            Rebuild(groundAt);
        }

        public static void DeleteCorridors(HashSet<int> deadCorr, Func<Vector2, float> groundAt)
        {
            if (deadCorr == null || deadCorr.Count == 0) return;
            var deadLanes = new HashSet<int>();
            foreach (int ci in deadCorr)
                if (ci >= 0 && ci < Net.Corridors.Count)
                    foreach (int li in Net.Corridors[ci].Lanes) deadLanes.Add(li);

            var edgeMap = new Dictionary<int, int>(); var newEdges = new List<LaneEdge>();
            for (int i = 0; i < Net.Edges.Count; i++) { if (deadLanes.Contains(i)) continue; edgeMap[i] = newEdges.Count; newEdges.Add(Net.Edges[i]); }

            var usedNodes = new HashSet<int>(); foreach (LaneEdge e in newEdges) { usedNodes.Add(e.A); usedNodes.Add(e.B); }
            var nodeMap = new Dictionary<int, int>(); var newNodes = new List<Vector2>(); var newNodeY = new List<float>();
            for (int i = 0; i < Net.Nodes.Count; i++) { if (!usedNodes.Contains(i)) continue; nodeMap[i] = newNodes.Count; newNodes.Add(Net.Nodes[i]); newNodeY.Add(Net.GetNodeY(i)); }

            var corrMap = new Dictionary<int, int>(); var newCorridors = new List<Corridor>();
            for (int ci = 0; ci < Net.Corridors.Count; ci++) { if (deadCorr.Contains(ci)) continue; corrMap[ci] = newCorridors.Count; newCorridors.Add(Net.Corridors[ci]); }

            foreach (LaneEdge e in newEdges)
            {
                e.A = nodeMap[e.A]; e.B = nodeMap[e.B];
                e.CorridorId = corrMap.TryGetValue(e.CorridorId, out int nc) ? nc : -1;
            }
            for (int i = 0; i < newCorridors.Count; i++)
            {
                Corridor c = newCorridors[i]; c.Id = i;
                for (int k = 0; k < c.Lanes.Count; k++) c.Lanes[k] = edgeMap[c.Lanes[k]];
            }
            var newFlows = new List<LaneFlow>();
            foreach (LaneFlow f in Net.Flows)
            {
                if (deadLanes.Contains(f.FromEdge) || deadLanes.Contains(f.ToEdge) || !nodeMap.ContainsKey(f.Node)) continue;
                f.Node = nodeMap[f.Node]; f.FromEdge = edgeMap[f.FromEdge]; f.ToEdge = edgeMap[f.ToEdge];
                newFlows.Add(f);
            }

            Net.LoadFrom(newNodes, newNodeY, newEdges, newCorridors, newFlows);
            RegenerateDefaultFlows(groundAt);
            Rebuild(groundAt);
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("Tools/Lane-Edge Spike/Toggle Draw Mode (lane-edge model)")]
        static void ToggleDrawMode()
        {
            LaneEdgeModel.Enabled = !LaneEdgeModel.Enabled;
            CancelDraw();
            Debug.Log($"[LaneEdgeWorld] draw mode {(LaneEdgeModel.Enabled ? "ON — in road-plan mode, click-click draws a lane-edge corridor from the active profile" : "OFF")}");
        }

        [UnityEditor.MenuItem("Tools/Lane-Edge Spike/Toggle Mapping Mode (lane flows)")]
        static void ToggleMappingMode()
        {
            LaneEdgeModel.MappingMode = !LaneEdgeModel.MappingMode;
            if (LaneEdgeModel.MappingMode) LaneEdgeModel.Enabled = true;   // mapping implies the lane-edge model is active
            CancelMap();
            Debug.Log($"[LaneEdgeWorld] mapping mode {(LaneEdgeModel.MappingMode ? "ON — click an incoming (blue) endpoint then an outgoing (green) one at the same node" : "OFF")}");
        }
#endif
    }
}
