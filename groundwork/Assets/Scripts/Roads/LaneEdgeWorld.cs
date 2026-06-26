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
        // (node, edge) → index into Endpoints. Rebuilt alongside Endpoints so TryEndpointPos is O(1) instead of a linear
        // scan — DetectLaneDropTapers calls it inside a double loop over all edges, so a linear scan made Rebuild O(E³).
        static readonly Dictionary<long, int> _endpointIndex = new Dictionary<long, int>();
        static long EpKey(int node, int edge) => ((long)node << 32) | (uint)edge;

        public static void Rebuild(Func<Vector2, float> groundAt)
        {
            ComputeEndpoints(groundAt);
            DetectLaneDropTapers();   // derive lane-drop tapers from lane-count mismatches at shared nodes
            DetectExitGores();        // derive exit-ramp gores (ramp diverging from a through corridor) → nose/gore convergence
            ComputeJunctions();       // 3+ way crossings → per-approach setback (so the overlays below clip) + footprint pads
            GameObject root = Root();
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(root.transform.GetChild(i).gameObject);
            foreach (Corridor c in Net.Corridors)
            {
                if (c.Built) LaneEdgeCorridorBuilder.RenderCorridor(Net, c, root.transform, groundAt);   // real swept body
                else RenderPlanOverlay(c, root.transform, groundAt);                                     // schematic plan line
            }
            RenderExitGores(root.transform, groundAt);       // ramp-gore shoulder-edge convergence (nose/gore) — plan overlay
            RenderJunctions(root.transform, groundAt);        // 3+ corridor crossings → paved footprint with curb-return fillets

            // The segment (cluster) node + flow connectors are intersection-routing visuals — only show them while mapping
            // flows. In plain draw mode the lanes + endpoint pucks are the handles; the centreline node is internal.
            if (LaneEdgeModel.MappingMode)
            {
                RenderNodes(root.transform, groundAt);
                RenderFlows(root.transform);
            }
            RenderEndpointSpheres(root.transform);
        }

        static readonly Color PlanCol = new Color(.5f, 0f, 0f, .25f), ExcCol = new Color(0.95f, 0.85f, 0.2f, 1f);   // white plan lines on green terrain
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
            // A taper that's the outermost lane owns the road's solid outer edge → don't draw the body's solid edge at the
            // surviving lanes' boundary (that's the dropped lane's DASHED inner divider, drawn by the taper).
            _suppLeftEdge = OuterEdgeSuppressed(c, -1f);   // BA / leftmost (most negative offset)
            _suppRightEdge = OuterEdgeSuppressed(c, 1f);   // AB / rightmost (most positive offset)
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
            _suppLeftEdge = _suppRightEdge = false; _goreSuppressEdgeMask = 0; _goreSuppressShoulderMask = 0;   // profile preview has no tapers/gores
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
        static bool _suppLeftEdge, _suppRightEdge;   // a taper owns the solid outer edge on this side → body skips it
        // Exit-gore suppression: the side whose LANE EDGE (ramp inner) and/or SHOULDER (ramp inner + through outer) is hidden
        // here and redrawn clipped at the nose/gore. -1=left, +1=right, 0=none.
        static int _goreSuppressEdgeMask, _goreSuppressShoulderMask;   // bit0 = BA/left side, bit1 = AB/right side; a corridor can carry gores on BOTH sides (exit one side + entrance the other)
        static void CollectGuideLines()
        {
            _guideBuf.Clear();
            if (_laneBuf.Count == 0) return;
            _laneBuf.Sort((a, b) => a.off.CompareTo(b.off));
            float laneLeft = _laneBuf[0].off - _laneBuf[0].w * 0.5f;                                  // outermost lane edges → SOLID
            float laneRight = _laneBuf[_laneBuf.Count - 1].off + _laneBuf[_laneBuf.Count - 1].w * 0.5f;
            if (!_suppLeftEdge && (_goreSuppressEdgeMask & 1) == 0) _guideBuf.Add((laneLeft, 0f, 0f));    // taper/gore owns this edge → skip (drawn clipped elsewhere)
            if (!_suppRightEdge && (_goreSuppressEdgeMask & 2) == 0) _guideBuf.Add((laneRight, 0f, 0f));
            if (_shBA > 0.01f && (_goreSuppressShoulderMask & 1) == 0) _guideBuf.Add((laneLeft - _shBA, OuterDash, OuterGap));   // shoulder outside → small dashes
            if (_shAB > 0.01f && (_goreSuppressShoulderMask & 2) == 0) _guideBuf.Add((laneRight + _shAB, OuterDash, OuterGap));
            for (int i = 0; i < _laneBuf.Count - 1; i++)
            {
                float boundary = (_laneBuf[i].off + _laneBuf[i].w * 0.5f + _laneBuf[i + 1].off - _laneBuf[i + 1].w * 0.5f) * 0.5f;
                if (_laneBuf[i].dir != _laneBuf[i + 1].dir)
                { _guideBuf.Add((boundary - DblSep, 0f, 0f)); _guideBuf.Add((boundary + DblSep, 0f, 0f)); }   // double solid divider
                else _guideBuf.Add((boundary, LaneDash, LaneGap));   // large-dash lane line
            }
        }

        // A corridor's path is sane to render only when its endpoint nodes are finite and within a reasonable span. Used to
        // refuse plan-overlay rendering of a corridor a bad peel/extend left degenerate (would otherwise hang the editor).
        static bool IsSanePath(Corridor c, float pathLen, out string why)
        {
            why = null;
            if (float.IsNaN(pathLen) || float.IsInfinity(pathLen)) { why = $"pathLen={pathLen}"; return false; }
            if (pathLen > 20000f) { why = $"pathLen={pathLen:F0} m (>20 km)"; return false; }
            if (c.Lanes.Count > 0 && c.Lanes[0] >= 0 && c.Lanes[0] < Net.Edges.Count)
            {
                LaneEdge l0 = Net.Edges[c.Lanes[0]];
                Vector2 a = Net.Nodes[l0.A], b = Net.Nodes[l0.B];
                if (!IsFinite(a) || !IsFinite(b)) { why = $"non-finite node A={a} B={b}"; return false; }
            }
            return true;
        }
        static bool IsFinite(Vector2 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.x) || float.IsInfinity(v.y));

        // Committed un-built corridor: draped styled guide-line mesh (red planned / yellow excavated), matching the live preview.
        static void RenderPlanOverlay(Corridor c, Transform parent, Func<Vector2, float> groundAt)
        {
            if (c.Lanes.Count == 0) return;
            float pathLen = LaneEdgeCorridorBuilder.PathLength(Net, c);
            if (pathLen < 1e-2f) return;
            // A degenerate corridor (a node placed at a NaN/huge coordinate by a bad peel/extend) gives an astronomical
            // pathLen → BuildPlanArrows emits pathLen/200 arrows and EmitDashedPolyline float-stalls → the editor HANGS.
            // Skip such a corridor (and log it once) instead of freezing. The logged nodes/length point at the source.
            if (!IsSanePath(c, pathLen, out string pathWhy))
            {
                Debug.LogError($"[LaneEdgeWorld] skipped plan overlay for corridor #{c.Id} ({c.Lanes.Count} lanes) — {pathWhy}");
                return;
            }
            CorridorLanes(c, out _shBA, out _shAB);
            GoreSuppress(c, out _goreSuppressEdgeMask, out _goreSuppressShoulderMask);   // hide gore ramp/through edges (drawn clipped at nose/gore)
            CollectGuideLines();
            bool hasTaper = c.Tapers != null && c.Tapers.Count > 0;
            if (_guideBuf.Count == 0 && !hasTaper) return;   // still render even if every lane is a taper wedge

            // Pull the overlay back from any junction end by that approach's setback, so lines stop at the intersection pad
            // instead of running through it (the crossing is paved separately).
            LaneEdge le0 = Net.Edges[c.Lanes[0]];
            float tA = Mathf.Clamp01(JunctionEndSetback(c, le0.A) / pathLen);
            float tB = Mathf.Clamp01(JunctionEndSetback(c, le0.B) / pathLen);
            if (tA + tB > 0.85f) { float k = 0.85f / (tA + tB); tA *= k; tB *= k; }   // never clip a short segment to nothing
            int frames = Mathf.Clamp(Mathf.CeilToInt(pathLen / 1.5f) + 1, 2, 1024);
            var cp = new Vector2[frames]; var rg = new Vector2[frames];
            for (int f = 0; f < frames; f++)
            {
                float t = Mathf.Lerp(tA, 1f - tB, (float)f / (frames - 1));
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
                // Shoulder rides at CONSTANT width just outside the wedge's outer edge (NOT scaled by e). Only the dropped
                // lane collapses to the gore tip; the shoulder keeps its 1.5 m and ends one shoulder-width off the tip, where
                // it meets the continuing road's shoulder. Scaling by e dragged the line inward across the dropped-lane slot
                // into the travel lanes and missed the neighbour's shoulder at both ends.
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
            // The outer S-curve edge is the ROAD BOUNDARY when this taper is the outermost lane → draw it SOLID, matching the
            // straight road's solid outermost-lane edge (which is solid whether or not a shoulder sits outside it; the
            // shoulder line itself is the small-dashed _twSh further out). Interior taper → dashed lane line.
            if (TaperIsOutermost(c, tp)) EmitPolyline(_twOut, verts, tris, groundAt);    // solid outermost road edge
            else EmitDashedPolyline(_twOut, LaneDash, LaneGap, verts, tris, groundAt);   // interior lane line (dashed)
            EmitDashedPolyline(_twIn, LaneDash, LaneGap, verts, tris, groundAt);      // inner straight edge (lane divider)
            // No end cap at the full-width end: the dropped lane MERGES into the road body there (it doesn't terminate), so a
            // solid perpendicular bar across the lane is spurious — the inner (dashed) + outer (solid) edges just continue.
            // Shoulder edge following the taper. The straight road's shoulder line is drawn frame-quantized (EmitGuideLine)
            // which renders OuterDash≈lane-length dashes; the taper uses true dashing, so match the visible plan by using the
            // lane dash here (small OuterDash would render as tiny dots that don't match the rest of the plan).
            if (sh > 0.01f && _twSh.Count == n)
                EmitDashedPolyline(_twSh, LaneDash, LaneGap, verts, tris, groundAt);
        }

        // Dashed version of EmitPolyline: walks the polyline by arc length, emitting only the dash-on portions. Dash-on
        // intervals are [m·period, m·period + dash] in global arc length, computed by integer index m — drift-free and
        // stall-proof. (The old `pos += dash-phase` walk could land `walked % period` infinitesimally below `dash`, making
        // the piece ≈ 0, so it spun without advancing and emitted almost nothing — the sparse-shoulder-dash bug.)
        static void EmitDashedPolyline(List<Vector2> pts, float dash, float gap, List<Vector3> verts, List<int> tris, Func<Vector2, float> groundAt)
        {
            float period = dash + gap; if (period < 0.01f) { EmitPolyline(pts, verts, tris, groundAt); return; }
            float gStart = 0f;   // arc length from the polyline start to the current segment's start (continuous dash phase)
            for (int k = 0; k < pts.Count - 1; k++)
            {
                Vector2 a = pts[k], b = pts[k + 1];
                Vector2 seg = b - a; float segLen = seg.magnitude;
                // A non-finite or absurdly long (>50 km) segment is degenerate (a node/control point at a huge coordinate) —
                // skip it (and don't advance the phase) rather than emit millions of dashes.
                if (float.IsNaN(segLen) || float.IsInfinity(segLen) || segLen > 50000f)
                {
                    if (segLen > 50000f) Debug.LogError($"[EmitDashedPolyline] skipped degenerate segment k={k} segLen={segLen:F0} (pts={pts.Count})");
                    continue;
                }
                if (segLen < 1e-4f) continue;
                Vector2 dir = seg / segLen;
                float segEnd = gStart + segLen;
                for (int m = Mathf.FloorToInt(gStart / period); m * period < segEnd; m++)   // each dash interval overlapping this segment
                {
                    float onLo = Mathf.Max(m * period, gStart);
                    float onHi = Mathf.Min(m * period + dash, segEnd);
                    if (onHi - onLo > 1e-4f)
                        EmitLineSeg(a + dir * (onLo - gStart), a + dir * (onHi - gStart), verts, tris, groundAt);
                }
                gStart = segEnd;
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
            int numArrows = Mathf.Clamp(Mathf.RoundToInt(pathLen / 200f), 1, 200);   // clamp: a degenerate pathLen must never explode the arrow count
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

        // Travel direction AT a specific node end of the corridor (curve-aware) — for colinearity tests at a junction, where the
        // midpoint tangent of a curved corridor can diverge from its heading at the node and wrongly read as "not a through lane".
        static Vector2 LaneTravelDirAt(LaneEdge L, Corridor c, int node)
        {
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            float t = (node == l0.A) ? 0f : (node == l0.B) ? 1f : 0.5f;
            Vector2 tan = LaneEdgeCorridorBuilder.PathTangent(Net, c, t);
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
                for (int endSel = 0; endSel < 2; endSel++)
                {
                    int N = endSel == 0 ? L.A : L.B;
                    if (N < 0 || N >= Net.Nodes.Count) continue;
                    if (CountCorridorsAtNode(N) >= 3) continue;   // a 3+ way junction resolves lane mismatches via the crossing, not a collapsing drop-taper
                    if (!TryEndpointPos(N, li, out Vector2 pL, out _)) continue;
                    Vector2 travel = LaneTravelDirAt(L, c, N);        // heading AT this node (curve-aware), not the midpoint
                    if (travel.sqrMagnitude < 1e-6f) continue;
                    Vector2 perp = new Vector2(-travel.y, travel.x);
                    bool throughExists = false, continued = false;   // throughExists = the road continues STRAIGHT past N
                    for (int mj = 0; mj < Net.Edges.Count && !continued; mj++)
                    {
                        if (mj == li) continue;
                        LaneEdge M = Net.Edges[mj];
                        if (M.A != N && M.B != N) continue;
                        if (M.CorridorId == L.CorridorId) continue;        // a sibling lane isn't a through-connection
                        Corridor mc = (M.CorridorId >= 0 && M.CorridorId < Net.Corridors.Count) ? Net.Corridors[M.CorridorId] : null;
                        if (mc == null) continue;
                        float al = Vector2.Dot(travel, LaneTravelDirAt(M, mc, N));
                        if (al < 0.25f) continue;                          // sharp turn / opposing → not a continuation of this lane
                        if (al >= 0.8f) throughExists = true;              // a colinear corridor continues the road straight past N
                        // An in-line forward lane continues L specifically: colinear (a parallel through) OR diverging (L bends off
                        // onto a ramp here). Either way L isn't dropped, so no taper. The diverging case stops a lane that EXITS to a
                        // ramp from being mistaken for a lane-drop — its spurious taper shoulder was leaking across the gore.
                        if (TryEndpointPos(N, mj, out Vector2 pM, out _) && Mathf.Abs(Vector2.Dot(pM - pL, perp)) < 0.6f * L.Width)
                            continued = true;
                    }
                    // Taper ONLY when the road continues straight (a colinear corridor) but THIS lane has no in-line
                    // counterpart — a genuine lane-count mismatch. Corners / T-junctions / termini (no colinear corridor) don't taper.
                    if (throughExists && !continued)
                        c.Tapers.Add(new LaneDropTaper { AtA = (N == L.A), Offset = L.Offset, Width = L.Width, Length = TaperLength(TaperSpeedKmh, L.Width), LaneEdge = li });
                }
            }
        }

        // ── exit-ramp gore convergence ── where a ramp corridor diverges from a through corridor, the ramp's inner-side
        // shoulder edges must converge with the through corridor's shoulder edges: the two INNER edges meet at the NOSE
        // (upstream), the two OUTER edges meet at the GORE (downstream). Derived geometrically each Rebuild (like tapers).
        public struct ExitGore { public int Through, Ramp; public float ThroughSide, RampSide, RampT; public Vector2 Nose, Gore; public bool HasNose, HasGore; }
        static readonly List<ExitGore> _gores = new List<ExitGore>();
        const float GoreSnap = 12f;   // a ramp end this close to a through path is a gore candidate

        // Outer lane edge (innerOff) and shoulder outer edge (outerOff) on side +1 (AB) / −1 (BA) of a corridor.
        static void SideEdges(Corridor c, float side, out float innerOff, out float outerOff)
        {
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= Net.Edges.Count) continue;
                LaneEdge e = Net.Edges[li]; if (e.Kind == LaneKind.Sidewalk) continue;
                lo = Mathf.Min(lo, e.Offset - e.Width * 0.5f); hi = Mathf.Max(hi, e.Offset + e.Width * 0.5f);
            }
            if (float.IsInfinity(lo)) { innerOff = outerOff = 0f; return; }
            if (side >= 0f) { innerOff = hi; outerOff = hi + c.ShoulderAB; }
            else            { innerOff = lo; outerOff = lo - c.ShoulderBA; }
        }

        // Lateral-offset polyline of a corridor's reference path (point = path + right·off), sampled n+1 times. `extend`
        // extrapolates the line straight past both ends (so a nose/gore landing just before the through corridor's start —
        // e.g. at a shared fork node — is still caught).
        static List<Vector2> OffsetPolyline(Corridor c, float off, int n, float extend = 0f)
        {
            var pts = new List<Vector2>(n + 3);
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                pts.Add(LaneEdgeCorridorBuilder.PathPoint(Net, c, t) + LaneEdgeCorridorBuilder.PathRight(LaneEdgeCorridorBuilder.PathTangent(Net, c, t)) * off);
            }
            if (extend > 0f && pts.Count >= 2)
            {
                Vector2 d0 = pts[0] - pts[1]; if (d0.sqrMagnitude > 1e-6f) pts.Insert(0, pts[0] + d0.normalized * extend);
                Vector2 d1 = pts[pts.Count - 1] - pts[pts.Count - 2]; if (d1.sqrMagnitude > 1e-6f) pts.Add(pts[pts.Count - 1] + d1.normalized * extend);
            }
            return pts;
        }

        // Nearest point on a corridor path to p (coarse sample). Always returns a point for a valid corridor.
        static bool NearestOnPath(Corridor c, Vector2 p, out Vector2 nearest, out float tBest)
        {
            nearest = Vector2.zero; tBest = 0f;
            if (c.Lanes.Count == 0) return false;
            const int n = 48; float best = float.PositiveInfinity;
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n; Vector2 q = LaneEdgeCorridorBuilder.PathPoint(Net, c, t);
                float d = (q - p).sqrMagnitude; if (d < best) { best = d; nearest = q; tBest = t; }
            }
            return true;
        }

        // Proper crossing of two segments (false if parallel or crossing outside either segment).
        static bool SegInt(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4, out Vector2 hit)
        {
            hit = Vector2.zero;
            Vector2 r = p2 - p1, s = p4 - p3; float rxs = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(rxs) < 1e-9f) return false;
            Vector2 qp = p3 - p1; float t = (qp.x * s.y - qp.y * s.x) / rxs, u = (qp.x * r.y - qp.y * r.x) / rxs;
            if (t < 0f || t > 1f || u < 0f || u > 1f) return false;
            hit = p1 + r * t; return true;
        }

        // First crossing of polyline a with polyline b (scanning a from its start), or false.
        static bool PolylineIntersect(List<Vector2> a, List<Vector2> b, out Vector2 hit)
        {
            hit = Vector2.zero;
            for (int i = 0; i < a.Count - 1; i++)
                for (int j = 0; j < b.Count - 1; j++)
                    if (SegInt(a[i], a[i + 1], b[j], b[j + 1], out hit)) return true;
            return false;
        }

        // True if some corridor (other than miSkip/riSkip) arrives at `forkNode` colinear with mDir — i.e. M continues an
        // upstream road through the fork (it's the through), whereas a diverging ramp has no such upstream continuation.
        static bool HasUpstreamColinear(int forkNode, int miSkip, int riSkip, Vector2 mDir, out Vector2 bestUInto)
        {
            bestUInto = Vector2.zero; float bestDot = 0.8f; bool found = false;
            if (forkNode < 0) return false;
            for (int ui = 0; ui < Net.Corridors.Count; ui++)
            {
                if (ui == miSkip || ui == riSkip) continue;
                Corridor U = Net.Corridors[ui];
                if (U.Lanes.Count == 0) continue;
                LaneEdge ul0 = Net.Edges[U.Lanes[0]];
                int uEnd = ul0.A == forkNode ? 0 : (ul0.B == forkNode ? 1 : -1);
                if (uEnd < 0) continue;
                Vector2 uFar = uEnd == 0 ? Net.Nodes[ul0.B] : Net.Nodes[ul0.A];
                Vector2 uInto = Net.Nodes[forkNode] - uFar; if (uInto.sqrMagnitude < 1e-6f) continue; uInto.Normalize();
                float d = Vector2.Dot(uInto, mDir);
                if (d > bestDot) { bestDot = d; bestUInto = uInto; found = true; }
            }
            return found;
        }

        static void DetectExitGores()
        {
            _gores.Clear();
            int nc = Net.Corridors.Count;
            for (int ri = 0; ri < nc; ri++)
            {
                Corridor R = Net.Corridors[ri];
                if (R.Lanes.Count == 0) continue;
                if (!LaneEdgeCorridorBuilder.PathEndpoints(Net, R, out Vector2 rA, out Vector2 rB)) continue;
                if (LaneEdgeCorridorBuilder.PathLength(Net, R) < 1e-2f) continue;
                for (int endSel = 0; endSel < 2; endSel++)
                {
                    float rampT = endSel == 0 ? 0f : 1f;
                    Vector2 Nr = endSel == 0 ? rA : rB, Nfar = endSel == 0 ? rB : rA;
                    Vector2 Tr = LaneEdgeCorridorBuilder.PathTangent(Net, R, rampT); if (endSel == 1) Tr = -Tr;   // into-ramp dir
                    LaneEdge rl0 = Net.Edges[R.Lanes[0]];
                    int forkNode = endSel == 0 ? rl0.A : rl0.B;
                    int bestM = -1; float bestSide = 0f, bestNear = GoreSnap; Vector2 bestPm = Vector2.zero;
                    for (int mi = 0; mi < nc; mi++)
                    {
                        if (mi == ri) continue;
                        Corridor M = Net.Corridors[mi];
                        if (!NearestOnPath(M, Nr, out Vector2 Pm, out float tm)) continue;
                        float dn = (Pm - Nr).magnitude; if (dn > GoreSnap) continue;
                        Vector2 Tm = LaneEdgeCorridorBuilder.PathTangent(Net, M, tm);
                        // Ramp must be roughly PARALLEL to the through axis — but sign-agnostic: a two-way road's BA-side lanes
                        // travel AGAINST the corridor's A→B tangent, so a BA exit runs anti-parallel to Tm. |dot| accepts both
                        // travel sides (was `<0.3` non-abs → rejected every BA-direction exit as wrong-way, so gores formed on
                        // one side of the road only). The side is derived from M's own A→B frame below, so this stays correct.
                        if (Mathf.Abs(Vector2.Dot(Tr, Tm)) < 0.3f) continue;
                        // M must CONTINUE downstream past the fork (so the nose/gore, which land downstream, are on M's path).
                        LaneEdgeCorridorBuilder.PathEndpoints(Net, M, out Vector2 mA, out Vector2 mB);
                        Vector2 mFar = Vector2.Dot(mB - Pm, Tr) > Vector2.Dot(mA - Pm, Tr) ? mB : mA;
                        if (Vector2.Dot(mFar - Pm, Tr) < 5f) continue;           // skip a through segment that ends at the fork
                        // M is the THROUGH only if it's colinear with an upstream corridor arriving at the fork (the ramp turns
                        // off, so it has none). Replaces a lane-count test that broke when the through ended with the same lane
                        // count as the ramp (2-lane → 1+1, 3-lane → 1+2, etc.).
                        Vector2 mDir = mFar - Pm; if (mDir.sqrMagnitude < 1e-6f) continue; mDir.Normalize();
                        if (!HasUpstreamColinear(forkNode, mi, ri, mDir, out Vector2 uInto)) continue;
                        // The THROUGH must be MORE colinear with the upstream than the ramp (the ramp diverges more). Without
                        // this, a gradually-curving ramp is also within the colinear threshold → the gore gets detected twice
                        // with through/ramp swapped, and the real through gets wrongly given ramp treatment.
                        Vector2 rChord = Nfar - Nr; if (rChord.sqrMagnitude < 1e-6f) continue; rChord.Normalize();
                        if (Vector2.Dot(uInto, mDir) <= Vector2.Dot(uInto, rChord)) continue;
                        NearestOnPath(M, Nfar, out Vector2 Pmf, out _);
                        if ((Pmf - Nfar).magnitude <= dn + 1f) continue;          // ramp must diverge (far end farther off M)
                        float side = Mathf.Sign(Vector2.Dot(LaneEdgeCorridorBuilder.PathRight(Tm), Nfar - Pmf));
                        if (dn < bestNear) { bestNear = dn; bestM = mi; bestSide = side; bestPm = Pm; }
                    }
                    if (bestM < 0) continue;
                    Corridor Mm = Net.Corridors[bestM];
                    Vector2 TrFrame = LaneEdgeCorridorBuilder.PathTangent(Net, R, rampT);
                    // Which side of the RAMP the mainline lies on — measured at the FAR end where the two roadways are well
                    // separated (at the near/fork end they're coincident, so the sign is just noise). Ramp inner shoulder = this side.
                    NearestOnPath(Mm, Nfar, out Vector2 PmFar, out _);
                    float rampSide = Mathf.Sign(Vector2.Dot(LaneEdgeCorridorBuilder.PathRight(TrFrame), PmFar - Nfar));
                    if (rampSide == 0f) rampSide = -bestSide;
                    SideEdges(Mm, bestSide, out float mInner, out float mOuter);
                    SideEdges(R, rampSide, out float rInner, out float rOuter);
                    int nR = Mathf.Clamp(Mathf.CeilToInt(LaneEdgeCorridorBuilder.PathLength(Net, R) / 2f), 2, 256);
                    int nM = Mathf.Clamp(Mathf.CeilToInt(LaneEdgeCorridorBuilder.PathLength(Net, Mm) / 2f), 2, 256);
                    // Intersect the ACTUAL (possibly curved) ramp shoulder edges with the through edges: nose = inner∩inner,
                    // gore = outer∩outer. Iterate the ramp polyline from its fork end so the FIRST crossing is the upstream one.
                    var rampIn = OffsetPolyline(R, rInner, nR); var rampOut = OffsetPolyline(R, rOuter, nR);
                    if (rampT > 0.5f) { rampIn.Reverse(); rampOut.Reverse(); }
                    var g = new ExitGore { Through = bestM, Ramp = ri, ThroughSide = bestSide, RampSide = rampSide, RampT = rampT };
                    g.HasNose = PolylineIntersect(rampIn, OffsetPolyline(Mm, mInner, nM, 40f), out g.Nose);
                    g.HasGore = PolylineIntersect(rampOut, OffsetPolyline(Mm, mOuter, nM, 40f), out g.Gore);
                    if (g.HasNose || g.HasGore) _gores.Add(g);
                }
            }
        }

        // Gore suppression for corridor c: a RAMP hides its inner lane edge + inner shoulder (rampSide); a THROUGH hides only
        // its exit-side OUTER shoulder (throughSide), keeping its lane edge continuous. Both are redrawn clipped in RenderExitGores.
        // True if corridor c is the diverging ramp of an exit gore (shares pavement with the through road near the fork).
        public static bool IsGoreRamp(Corridor c)
        {
            foreach (var g in _gores)
                if (g.Ramp >= 0 && g.Ramp < Net.Corridors.Count && ReferenceEquals(Net.Corridors[g.Ramp], c)) return true;
            return false;
        }

        // For a gore ramp, the inner side (toward the through road) whose shoulder overlaps the through lanes. Else 0.
        public static int GoreRampInnerSide(Corridor c)
        {
            foreach (var g in _gores)
                if (g.Ramp >= 0 && g.Ramp < Net.Corridors.Count && ReferenceEquals(Net.Corridors[g.Ramp], c)) return (int)g.RampSide;
            return 0;
        }

        // Marking-clip info for a gore corridor: the gore-side sign (ramp inner side / through exit side) and the NOSE point.
        // The outermost lane edge on that side is clipped to start at the nose; interior dividers stay continuous. False if neither.
        public static bool GoreMarkClip(Corridor c, out int side, out Vector2 nosePt)
        {
            foreach (var g in _gores)
            {
                if (!g.HasNose) continue;
                if (g.Ramp >= 0 && g.Ramp < Net.Corridors.Count && ReferenceEquals(Net.Corridors[g.Ramp], c)) { side = (int)g.RampSide; nosePt = g.Nose; return true; }
                if (g.Through >= 0 && g.Through < Net.Corridors.Count && ReferenceEquals(Net.Corridors[g.Through], c)) { side = (int)g.ThroughSide; nosePt = g.Nose; return true; }
            }
            side = 0; nosePt = Vector2.zero; return false;
        }

        // Gore-ramp inner-shoulder taper info: the inner side + the NOSE and GORE points (the suppressed inner shoulder grows
        // from zero at the nose to full at the gore). False if c is not a gore ramp with both points.
        public static bool GoreRampWedgeInfo(Corridor c, out float rampSide, out Vector2 nosePt, out Vector2 gorePt)
        {
            foreach (var g in _gores)
                if (g.Ramp >= 0 && g.Ramp < Net.Corridors.Count && ReferenceEquals(Net.Corridors[g.Ramp], c) && g.HasGore && g.HasNose)
                { rampSide = g.RampSide; nosePt = g.Nose; gorePt = g.Gore; return true; }
            rampSide = 0f; nosePt = Vector2.zero; gorePt = Vector2.zero; return false;
        }

        // Per-side suppression masks (bit0 = BA/left, bit1 = AB/right). Accumulated over ALL gores so a corridor that is the
        // through for an exit on one side AND an entrance on the other suppresses BOTH (a single side int let the second gore
        // clobber the first → the first side's shoulder/edge dashes came back unclipped).
        static int SideBit(float side) => side < 0f ? 1 : (side > 0f ? 2 : 0);
        static void GoreSuppress(Corridor c, out int edgeMask, out int shoulderMask)
        {
            edgeMask = 0; shoulderMask = 0;
            foreach (var g in _gores)
            {
                // Suppress the LANE EDGE only when there's a NOSE to redraw it from, and the SHOULDER only when there's a GORE
                // — RenderExitGores redraws the edge `if (HasNose)` and the shoulder `if (HasGore)`. Suppressing unconditionally
                // dropped the through's outer solid edge whenever a gore formed with no nose (gore=True, nose=False) → #57.
                if (g.Ramp >= 0 && g.Ramp < Net.Corridors.Count && ReferenceEquals(Net.Corridors[g.Ramp], c))
                { int b = SideBit(g.RampSide); if (g.HasNose) edgeMask |= b; if (g.HasGore) shoulderMask |= b; }   // ramp: inner lane edge (nose) + inner shoulder (gore)
                if (g.Through >= 0 && g.Through < Net.Corridors.Count && ReferenceEquals(Net.Corridors[g.Through], c))
                { int b = SideBit(g.ThroughSide); if (g.HasNose) edgeMask |= b; if (g.HasGore) shoulderMask |= b; }   // through: exit-side outer lane edge (nose) + shoulder (gore)
            }
        }

        static Vector2 ClosestOnSeg(Vector2 a, Vector2 b, Vector2 p)
        {
            Vector2 ab = b - a; float t = Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude);
            return a + ab * Mathf.Clamp01(t);
        }

        // Polyline starting AT `start` (assumed to lie on `poly`) and continuing to poly's end — the downstream remainder.
        static List<Vector2> ClipFrom(List<Vector2> poly, Vector2 start)
        {
            int bestSeg = 0; float best = float.PositiveInfinity;
            for (int i = 0; i < poly.Count - 1; i++)
            {
                float d = (ClosestOnSeg(poly[i], poly[i + 1], start) - start).sqrMagnitude;
                if (d < best) { best = d; bestSeg = i; }
            }
            var res = new List<Vector2> { start };
            for (int i = bestSeg + 1; i < poly.Count; i++) res.Add(poly[i]);
            return res;
        }

        // Draw the ramp's inner-side shoulder edges CLIPPED to begin at the NOSE (inner lane edge, solid) and GORE (outer
        // shoulder edge, dashed), so they converge with the through corridor's edges. Plan overlay only (skip built ramps).
        static void RenderExitGores(Transform parent, Func<Vector2, float> groundAt)
        {
            if (_gores.Count == 0) return;
            // Two buffers so each gore line takes its owning corridor's plan colour (red planned / yellow excavated).
            var vP = new List<Vector3>(); var tP = new List<int>(); var vE = new List<Vector3>(); var tE = new List<int>();
            foreach (var g in _gores)
            {
                Corridor R = Net.Corridors[g.Ramp];
                if (R.Built) continue;
                List<Vector3> rv = R.Excavated ? vE : vP; List<int> rt = R.Excavated ? tE : tP;
                SideEdges(R, g.RampSide, out float rInner, out float rOuter);
                int nR = Mathf.Clamp(Mathf.CeilToInt(LaneEdgeCorridorBuilder.PathLength(Net, R) / 2f), 2, 256);
                var rampIn = OffsetPolyline(R, rInner, nR); var rampOut = OffsetPolyline(R, rOuter, nR);
                if (g.RampT > 0.5f) { rampIn.Reverse(); rampOut.Reverse(); }   // fork end first
                if (g.HasNose) EmitPolyline(ClipFrom(rampIn, g.Nose), rv, rt, groundAt);   // ramp inner edge: SOLID, clipped at the nose (forms one side of the gore nose)
                if (g.HasGore) EmitDashedPolyline(ClipFrom(rampOut, g.Gore), LaneDash, LaneGap, rv, rt, groundAt);
                // The ramp's interior dividers are NOT redrawn here — they stay continuous (drawn full by the ramp's own overlay),
                // flowing uninterrupted from the upstream divider through the gore into the ramp (per the spec mockup).
                if (g.Through >= 0 && g.Through < Net.Corridors.Count)
                {
                    Corridor M = Net.Corridors[g.Through];
                    if (!M.Built)
                    {
                        List<Vector3> mv = M.Excavated ? vE : vP; List<int> mt = M.Excavated ? tE : tP;
                        SideEdges(M, g.ThroughSide, out float mInner, out float mOuter);
                        int nM = Mathf.Clamp(Mathf.CeilToInt(LaneEdgeCorridorBuilder.PathLength(Net, M) / 2f), 2, 256);
                        LaneEdgeCorridorBuilder.PathEndpoints(Net, M, out Vector2 mA, out Vector2 mB);
                        bool forkAtA = (mA - g.Nose).sqrMagnitude <= (mB - g.Nose).sqrMagnitude;
                        // Through's exit-side OUTER LANE EDGE: solid, clipped from the NOSE (forms the other side of the gore nose;
                        // upstream of the nose it's interior — the ramp's continuous divider covers it, so no solid runs back).
                        if (g.HasNose)
                        {
                            var mainEdge = OffsetPolyline(M, mInner, nM); if (!forkAtA) mainEdge.Reverse();
                            EmitPolyline(ClipFrom(mainEdge, g.Nose), mv, mt, groundAt);
                        }
                        // Through's exit-side OUTER SHOULDER edge: dashed, clipped from the GORE downstream.
                        if (g.HasGore)
                        {
                            var mainOut = OffsetPolyline(M, mOuter, nM); if (!forkAtA) mainOut.Reverse();
                            EmitDashedPolyline(ClipFrom(mainOut, g.Gore), LaneDash, LaneGap, mv, mt, groundAt);
                        }
                    }
                }
            }
            BuildGoreMesh(vP, tP, PlannedMat(), "ExitGores", parent);
            BuildGoreMesh(vE, tE, ExcavatedMat(), "ExitGoresExcavated", parent);
        }

        // ══ INTERSECTIONS (Phase 1a) ══ where 3+ corridors share a node, build a paved junction FOOTPRINT: each approach's
        // outer edges, rounded into the neighbours with constant-radius CURB-RETURN FILLETS. For now this renders the
        // footprint as a translucent pad (geometry validation) — body setback-trim + marking-clip + turn routing come next.
        public static float JunctionRadius = 6f;     // corner curb-return radius (m), tunable
        public static float JunctionSetback = 10f;   // minimum stop-line setback floor per approach (m), tunable
        static Material _junctionMat;
        static Material JunctionMat() => _junctionMat != null ? _junctionMat
            : (_junctionMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.15f, 0.15f, 0.18f, 0.55f), "LaneJunction"));

        struct JctApproach
        {
            public Vector2 P;            // body centreline endpoint at the node (CenterShift-aware)
            public Vector2 away;         // unit direction the road extends AWAY from the node
            public Vector2 fr;           // right-of-travel normal (A→B frame): body spans P+fr*leftOff .. P+fr*rightOff
            public float leftOff, rightOff;   // outer-edge offsets (left = BA = negative, right = AB = positive)
            public float bearing;        // atan2(away) for CCW sort
            public int corrId; public bool atA;   // which corridor end this approach is (for the per-end setback map)
        }
        static readonly List<JctApproach> _japp = new List<JctApproach>();

        // Per-junction results, computed once per Rebuild BEFORE corridors render so the plan overlay can clip to the setback.
        struct JunctionPad { public int node; public List<Vector2> loop; }
        static readonly List<JunctionPad> _junctions = new List<JunctionPad>();
        static readonly Dictionary<int, float> _setbackByEnd = new Dictionary<int, float>();   // corrId*2 + (atA?0:1) → setback (m)
        static int EndKey(int corrId, bool atA) => corrId * 2 + (atA ? 0 : 1);
        // The junction stop-line setback at the corridor end touching `node` (0 if that end isn't a junction). Used to pull
        // the road's body/markings back from the crossing so the junction pad reads as a clean intersection.
        public static float JunctionEndSetback(Corridor c, int node)
        {
            if (c.Lanes.Count == 0) return 0f;
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            if (l0.A != node && l0.B != node) return 0f;
            return _setbackByEnd.TryGetValue(EndKey(c.Id, l0.A == node), out float s) ? s : 0f;
        }
        static bool IsJunctionNode(int node)
        {
            for (int i = 0; i < _junctions.Count; i++) if (_junctions[i].node == node) return true;
            return false;
        }
        // How many corridors touch `node` at one of their endpoints (degree): 1 = free road end, 2 = split/through, 3+ = junction.
        static int CountCorridorsAtNode(int node)
        {
            int count = 0;
            for (int ci = 0; ci < Net.Corridors.Count; ci++)
            {
                Corridor c = Net.Corridors[ci]; if (c.Lanes.Count == 0) continue;
                LaneEdge l0 = Net.Edges[c.Lanes[0]];
                if (l0.A == node || l0.B == node) count++;
            }
            return count;
        }

        // Infinite-line intersection (false if parallel). Lines: p1+t·d1, p2+s·d2.
        static bool LineLineIntersect(Vector2 p1, Vector2 d1, Vector2 p2, Vector2 d2, out Vector2 hit)
        {
            hit = Vector2.zero;
            float denom = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(denom) < 1e-7f) return false;
            Vector2 dp = p2 - p1;
            hit = p1 + d1 * ((dp.x * d2.y - dp.y * d2.x) / denom);
            return true;
        }

        // Just-touching setback so this approach's face clears its neighbour across a corner of angle theta (the wedge between
        // the two roads): (hNeighbour + hSelf·cos θ)/sin θ. Straight-through (θ≈π) or reflex → 0 (no constraint on that side).
        static float RequiredSetback(float hSelf, float hNeigh, float theta)
        {
            if (theta <= 0.05f || theta >= Mathf.PI - 0.05f) return 0f;
            float s = Mathf.Sin(theta); if (s < 0.05f) return 0f;
            return Mathf.Max(0f, (hNeigh + hSelf * Mathf.Cos(theta)) / s);
        }

        // True if `node` is the diverging fork of a detected gore (so it's a ramp split, not a 3+ way crossing).
        static bool IsGoreForkNode(int node)
        {
            foreach (var g in _gores)
            {
                if (g.Ramp < 0 || g.Ramp >= Net.Corridors.Count) continue;
                Corridor R = Net.Corridors[g.Ramp]; if (R.Lanes.Count == 0) continue;
                LaneEdge l0 = Net.Edges[R.Lanes[0]];
                if ((g.RampT > 0.5f ? l0.B : l0.A) == node) return true;
            }
            return false;
        }

        // Detect every 3+ way crossing, compute its per-approach setback + footprint loop, and stash them (in _junctions and
        // _setbackByEnd) — run EARLY in Rebuild so corridor plan overlays can clip their lines back to the setback.
        static void ComputeJunctions()
        {
            _junctions.Clear(); _setbackByEnd.Clear();
            for (int node = 0; node < Net.Nodes.Count; node++)
            {
                if (IsGoreForkNode(node)) continue;   // a gore (ramp diverging from a through road) is also 3 ends at a node — not a crossing
                _japp.Clear();
                for (int ci = 0; ci < Net.Corridors.Count; ci++)
                {
                    Corridor c = Net.Corridors[ci];
                    if (c.Lanes.Count == 0) continue;
                    LaneEdge l0 = Net.Edges[c.Lanes[0]];
                    bool atA = l0.A == node, atB = l0.B == node;
                    if (!atA && !atB) continue;
                    if (!LaneEdgeCorridorBuilder.PathFrameShifted(Net, c, out Vector2 sa, out Vector2 sb, out _, out _)) continue;
                    Vector2 tan = LaneEdgeCorridorBuilder.PathTangent(Net, c, atA ? 0f : 1f);   // A→B
                    Vector2 away = (atA ? tan : -tan).normalized;
                    SideEdges(c, 1f, out _, out float rOff);
                    SideEdges(c, -1f, out _, out float lOff);
                    _japp.Add(new JctApproach
                    {
                        P = atA ? sa : sb,
                        away = away,
                        fr = LaneEdgeCorridorBuilder.PathRight(tan),
                        leftOff = lOff, rightOff = rOff,
                        bearing = Mathf.Atan2(away.y, away.x),
                        corrId = c.Id, atA = atA
                    });
                }
                if (_japp.Count < 3) continue;                       // 3+ approaches = a junction (2 = continuation / bend)
                _japp.Sort((x, y) => x.bearing.CompareTo(y.bearing)); // CCW order
                int n = _japp.Count;

                // Per-approach setback: a floor, or the just-touching requirement against each CCW neighbour (so each road's
                // stop-line face clears the crossing roads). The face then sits at the crossing-box edge.
                var S = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int ip = (i - 1 + n) % n, inx = (i + 1) % n;
                    float thetaNext = Mathf.Repeat(_japp[inx].bearing - _japp[i].bearing, Mathf.PI * 2f);   // CCW wedge to next
                    float thetaPrev = Mathf.Repeat(_japp[i].bearing - _japp[ip].bearing, Mathf.PI * 2f);    // CCW wedge from prev
                    float hi = (_japp[i].rightOff - _japp[i].leftOff) * 0.5f;
                    float hn = (_japp[inx].rightOff - _japp[inx].leftOff) * 0.5f;
                    float hp = (_japp[ip].rightOff - _japp[ip].leftOff) * 0.5f;
                    float req = Mathf.Max(RequiredSetback(hi, hn, thetaNext), RequiredSetback(hi, hp, thetaPrev));
                    S[i] = Mathf.Max(JunctionSetback, req);
                    _setbackByEnd[EndKey(_japp[i].corrId, _japp[i].atA)] = S[i];   // so the corridor's plan overlay clips here
                }

                // Face corners at each setback: full outer-edge points, plus corners pulled IN by the curb radius so the corner
                // bezier has room to round (control = the two roads' outer-edge intersection — the box corner).
                var leftEdge = new Vector2[n]; var rightEdge = new Vector2[n];
                var leftC = new Vector2[n]; var rightC = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    JctApproach ap = _japp[i];
                    Vector2 fc = ap.P + ap.away * S[i];
                    Vector2 perp = new Vector2(-ap.away.y, ap.away.x);   // left when looking outward from the node
                    Vector2 e1 = fc + ap.fr * ap.leftOff, e2 = fc + ap.fr * ap.rightOff;
                    bool e1IsLeft = Vector2.Dot(e1 - fc, perp) >= Vector2.Dot(e2 - fc, perp);
                    Vector2 lE = e1IsLeft ? e1 : e2, rE = e1IsLeft ? e2 : e1;
                    leftEdge[i] = lE; rightEdge[i] = rE;
                    float pull = Mathf.Min(JunctionRadius, (rE - lE).magnitude * 0.45f);
                    leftC[i]  = lE + (fc - lE).normalized * pull;
                    rightC[i] = rE + (fc - rE).normalized * pull;
                }

                // Closed CCW boundary: each approach's face (right corner → left corner), then a curb-return bezier to the next
                // approach's right corner (control = outer-edge intersection; straight if the edges are parallel / run away).
                var loop = new List<Vector2>();
                for (int i = 0; i < n; i++)
                {
                    loop.Add(rightC[i]); loop.Add(leftC[i]);
                    int j = (i + 1) % n;
                    if (LineLineIntersect(leftEdge[i], _japp[i].away, rightEdge[j], _japp[j].away, out Vector2 ctrl))
                    {
                        Vector2 a = leftC[i], b = rightC[j], mid = (a + b) * 0.5f;
                        float maxReach = Mathf.Max((b - a).magnitude * 2f, 6f);
                        if ((ctrl - mid).magnitude <= maxReach)   // skip a runaway control (near-parallel) → straight chamfer
                        {
                            const int steps = 12;
                            for (int k = 1; k < steps; k++) { float s = k / (float)steps, u = 1f - s; loop.Add(u * u * a + 2f * u * s * ctrl + s * s * b); }
                        }
                    }
                }
                if (loop.Count < 3) continue;
                _junctions.Add(new JunctionPad { node = node, loop = loop });
            }
        }

        // Fan each stored junction footprint from its node (star-shaped about it). Double-sided so winding never hides it.
        static void RenderJunctions(Transform parent, Func<Vector2, float> groundAt)
        {
            if (_junctions.Count == 0) return;
            var verts = new List<Vector3>(); var tris = new List<int>();
            foreach (var jp in _junctions)
            {
                List<Vector2> loop = jp.loop;
                Vector2 ctr = Net.Nodes[jp.node];
                float yC = (groundAt != null ? groundAt(ctr) : 0f) + 0.10f;
                int baseIdx = verts.Count;
                verts.Add(new Vector3(ctr.x, yC, ctr.y));
                foreach (var p in loop) verts.Add(new Vector3(p.x, (groundAt != null ? groundAt(p) : 0f) + 0.10f, p.y));
                for (int k = 0; k < loop.Count; k++)
                {
                    int a = baseIdx + 1 + k, b = baseIdx + 1 + (k + 1) % loop.Count;
                    tris.Add(baseIdx); tris.Add(a); tris.Add(b);          // one winding
                    tris.Add(baseIdx); tris.Add(b); tris.Add(a);          // and the reverse (double-sided)
                }
            }
            BuildGoreMesh(verts, tris, JunctionMat(), "Junctions", parent);
        }

        static void BuildGoreMesh(List<Vector3> verts, List<int> tris, Material mat, string name, Transform parent)
        {
            if (verts.Count == 0) return;
            var mesh = new Mesh { name = name };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.RecalculateBounds();
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        // True if lane `li` is rendered as a taper wedge (so it should be excluded from the corridor's uniform body/lines).
        public static bool LaneIsTapered(Corridor c, int li)
        {
            if (c.Tapers == null) return false;
            for (int i = 0; i < c.Tapers.Count; i++) if (c.Tapers[i].LaneEdge == li) return true;
            return false;
        }

        // True if the tapering lane is the OUTERMOST drivable lane on its side (no other same-side lane sits further out) —
        // i.e. its outer S-curve edge IS the road boundary, so it must be drawn SOLID (matching the straight road's solid
        // outermost-lane edge), not as a dashed lane line.
        public static bool TaperIsOutermost(Corridor c, LaneDropTaper tp)
        {
            float sgn = tp.Offset >= 0f ? 1f : -1f;
            float tpOuter = Mathf.Abs(tp.Offset) + tp.Width * 0.5f;
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= Net.Edges.Count || li == tp.LaneEdge) continue;
                LaneEdge e = Net.Edges[li];
                if (e.Kind == LaneKind.Sidewalk) continue;
                if ((e.Offset >= 0f ? 1f : -1f) != sgn) continue;                  // same side only
                if (Mathf.Abs(e.Offset) + e.Width * 0.5f > tpOuter + 0.01f) return false;   // a lane sits further out → not outermost
            }
            return true;
        }

        // If the tapering lane is the OUTERMOST drivable lane on its side, the corridor's shoulder on that side follows the
        // wedge (drawn alongside it, and suppressed on the uniform body). Returns that shoulder width, else 0.
        public static float TaperOuterShoulder(Corridor c, LaneDropTaper tp)
        {
            float sgn = tp.Offset >= 0f ? 1f : -1f;
            float sh = sgn > 0f ? c.ShoulderAB : c.ShoulderBA;
            if (sh <= 0.01f) return 0f;
            return TaperIsOutermost(c, tp) ? sh : 0f;
        }

        // True if a taper on side `sgn` (+1 = AB, −1 = BA) owns that shoulder, so the uniform body must NOT draw it there.
        public static bool ShoulderSuppressed(Corridor c, float sgn)
        {
            if (c.Tapers == null) return false;
            foreach (var tp in c.Tapers)
                if ((tp.Offset >= 0f ? 1f : -1f) == sgn && TaperOuterShoulder(c, tp) > 0f) return true;
            return false;
        }

        // True if a taper on side `sgn` is the OUTERMOST lane there → it owns the road's outer boundary (drawn SOLID along its
        // outer S-curve, with its inner edge a DASHED lane divider). The uniform body must then NOT draw its own solid outer
        // edge at the surviving lanes' boundary — that boundary is the dropped lane's INNER (dashed) divider, not a road edge.
        public static bool OuterEdgeSuppressed(Corridor c, float sgn)
        {
            if (c.Tapers == null) return false;
            foreach (var tp in c.Tapers)
                if ((tp.Offset >= 0f ? 1f : -1f) == sgn && TaperIsOutermost(c, tp)) return true;
            return false;
        }

        static void ComputeEndpoints(Func<Vector2, float> groundAt)
        {
            Endpoints.Clear();
            _endpointIndex.Clear();
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
            // Anchor the puck on the corridor BODY (CenterShift-aware): use the shifted-path endpoint, which applies the shift
            // EXACTLY as the body renders — a uniform shift is a RIGID translate along the A-end normal, so re-deriving it per
            // end (the old `+endShift` along this end's normal) drifted several metres off the body on a CURVED ramp. Add the
            // lane's own lateral offset along fr. Non-shifted corridors are unchanged (shifted endpoint == raw node). A PEELED
            // lane sits on its own node already → use the node directly (offset 0).
            bool peeled = false;
            if (c != null && c.Lanes.Count > 0 && c.Lanes[0] != edgeIndex)
            {
                LaneEdge path = Net.Edges[c.Lanes[0]];
                int pathNode = (atNode == e.A) ? path.A : path.B;
                if (atNode != pathNode) peeled = true;   // this end was peeled onto its own node
            }
            Vector2 baseN = N;
            if (!peeled && c != null && c.Lanes.Count > 0
                && LaneEdgeCorridorBuilder.PathFrameShifted(Net, c, out Vector2 sa, out Vector2 sb, out _, out _))
                baseN = (atNode == Net.Edges[c.Lanes[0]].A) ? sa : sb;
            float lat = peeled ? 0f : e.Offset;
            Vector2 pos = baseN + into * inset + fr * lat;
            Vector2 nodePos = baseN + fr * lat;   // lateral-only (no inset): the in/out of a through-lane coincide here → one unified puck
            bool incoming = (e.Direction == 2 && atNode == e.B) || (e.Direction == 0 && atNode == e.A);
            float y = (groundAt != null ? groundAt(pos) : 0f) + 0.6f;
            _endpointIndex[EpKey(atNode, edgeIndex)] = Endpoints.Count;
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
                float srcOff = s.Offset + ShiftAtNode(sc, _extNode);                       // body position at the PULLED end (per-end shift) so the start frame sits on the source's pavement
                float oNew = (srcOff * (Vector2.Dot(frS, frNew) >= 0f ? 1f : -1f));
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
            // A draw endpoint JOINS an existing road when it lands on one: it shares a nearby node, or splits the road body at
            // the click and shares the inserted node (auto-T/cross junction) — see ResolveJoinNode. Off any road it's a fresh
            // node. (Lane-subset extension off a road's pucks is still the separate ToggleExtendPick path, gated upstream.)
            if (_drawStart < 0)
            {
                _drawStart = ResolveJoinNode(xz, groundAt);
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
                int bc = ResolveJoinNode(xz, groundAt);
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

            int b = ResolveJoinNode(xz, groundAt);
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
        static LineRenderer _pvC, _pvL, _pvR, _pvLegA, _pvLegB, _pvGuide, _pvLaneHi;   // path centre/left/right + bend legs + colinear guide + Alt single-lane hover band
        static GameObject _pvStartM, _pvCornerM, _pvEndM;          // start / armed-corner / end markers
        static Material _pvOkM, _pvBadM, _pvLegM, _pvNodeM, _pvSnapM, _pvGuideM, _pvPendM, _pvLaneHiM;
        // Loud "armed bend" cue: bright opaque amber so a placed (but not-yet-finalised) corner reads as "click again", not dead.
        static Material PvPending() => _pvPendM != null ? _pvPendM : (_pvPendM = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 0.5f, 0.0f, 1f), "LanePvPending"));
        // Alt single-lane hover: translucent magenta band over the WHOLE lane the click will grab (matches the selection puck colour).
        static Material PvLaneHi() => _pvLaneHiM != null ? _pvLaneHiM : (_pvLaneHiM = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 0.4f, 1f, 0.45f), "LanePvLaneHi"));
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
            _pvLegA = MakePvLine(_pvRoot.transform, 0.4f, PvLeg());
            _pvLegB = MakePvLine(_pvRoot.transform, 0.4f, PvLeg());
            _pvGuide = MakePvLine(_pvRoot.transform, 0.2f, PvGuide());
            _pvLaneHi = MakePvLine(_pvRoot.transform, 3.5f, PvLaneHi());   // width set per-hover to the lane width
            _pvStartM = MakePvMarker(_pvRoot.transform, 2.5f, PvNode());
            _pvCornerM = MakePvMarker(_pvRoot.transform, 5f, PvPending());
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

        // Paint a translucent band over the WHOLE lane `edge` (its full A→B length at its offset), width = the lane width.
        static bool FillLaneBand(LineRenderer lr, int edge, Func<Vector2, float> groundAt)
        {
            if (lr == null || edge < 0 || edge >= Net.Edges.Count) return false;
            LaneEdge e = Net.Edges[edge];
            Corridor c = (e.CorridorId >= 0 && e.CorridorId < Net.Corridors.Count) ? Net.Corridors[e.CorridorId] : null;
            if (c == null) return false;
            // Use the CenterShift-aware (body) path, not the raw lane nodes — a pulled-off ramp has its lane node 0-offset
            // but its body shifted (CenterShift); drawing on the raw nodes put the highlight CenterShift metres off the road.
            if (!LaneEdgeCorridorBuilder.PathFrameShifted(Net, c, out Vector2 a, out Vector2 b, out Vector2 c1, out Vector2 c2)) return false;
            FillPvLine(lr, a, b, c.Curved, c1, c2, e.Offset, groundAt, PvLaneHi());
            lr.widthMultiplier = Mathf.Max(1f, e.Width);
            lr.gameObject.SetActive(true);
            return true;
        }

        // The UPSTREAM lane that feeds `edge` at `node` (flows TOWARD the node) — the lane an exit actually pulls / that
        // becomes the ramp. If `edge` already flows toward the node it IS upstream; else its through-partner on the segment
        // arriving at the node. Highlighting this (not the forward segment past the node) matches what gets pulled.
        static int UpstreamLane(int edge, int node)
        {
            if (edge < 0 || edge >= Net.Edges.Count) return edge;
            LaneEdge e = Net.Edges[edge];
            bool incoming = (e.Direction == 2 && node == e.B) || (e.Direction == 0 && node == e.A);
            if (incoming) return edge;
            int p = ThroughPartner(node, edge);
            return p >= 0 ? p : edge;
        }

        // Alt single-lane hover: highlight the lane a click will toggle into the pull. edge<0 hides the band.
        static void ShowLaneHover(int edge, Func<Vector2, float> groundAt)
        {
            if (_pvLaneHi == null) return;
            if (!FillLaneBand(_pvLaneHi, edge, groundAt)) _pvLaneHi.gameObject.SetActive(false);
        }

        // Keep every SELECTED lane highlighted (full-lane bands) for the whole pull, so you can see what you're pulling
        // while drawing the curve. Pooled like the hover halos; unused entries are hidden.
        static readonly List<LineRenderer> _pvLaneSel = new List<LineRenderer>();
        static void ShowSelectedLaneBands(Func<Vector2, float> groundAt)
        {
            int shown = 0;
            foreach (int li in _extLanes)
            {
                while (_pvLaneSel.Count <= shown) _pvLaneSel.Add(MakePvLine(_pvRoot.transform, 3.5f, PvLaneHi()));
                if (FillLaneBand(_pvLaneSel[shown], UpstreamLane(li, _extNode), groundAt)) shown++;   // highlight the upstream lane that gets pulled
            }
            for (int i = shown; i < _pvLaneSel.Count; i++) _pvLaneSel[i].gameObject.SetActive(false);
        }
        static void HideSelectedLaneBands() { for (int i = 0; i < _pvLaneSel.Count; i++) _pvLaneSel[i].gameObject.SetActive(false); }

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
            _pvLegA.widthMultiplier = 0.4f; _pvLegB.widthMultiplier = 0.4f;   // baseline; the armed-bend branch widens these
            if (_pvLaneHi != null) _pvLaneHi.gameObject.SetActive(false);
            HideSelectedLaneBands();
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
                // Alt: paint the whole UPSTREAM lane the click will grab so it's obvious which single lane you're picking.
                ShowLaneHover(ForceSingleLane && _grpBuf.Count > 0 ? UpstreamLane(_grpBuf[0], hNode) : -1, groundAt);
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
                FillPvLine(_pvLegA, start, _corner, false, default, default, 0f, groundAt, PvPending());
                FillPvLine(_pvLegB, _corner, endPos, false, default, default, 0f, groundAt, PvPending());
                _pvLegA.widthMultiplier = 0.8f; _pvLegB.widthMultiplier = 0.8f;
                PlaceMarker(_pvCornerM, _corner, groundAt, PvPending());
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
            _pvLegA.widthMultiplier = 0.4f; _pvLegB.widthMultiplier = 0.4f;   // baseline; the armed-bend (pending) branch widens these
            _pvCornerM.SetActive(false); _pvStartM.SetActive(false); _pvEndM.SetActive(false);
            _pvShown = false;   // invalidate the normal-preview cache so it rebuilds when we leave extend mode
            if (!Extending) { ShowLaneHover(-1, groundAt); HideSelectedLaneBands(); return; }
            // Keep the picked lanes lit for the whole pull so you see what you're pulling while drawing the curve.
            ShowSelectedLaneBands(groundAt);
            // Alt accumulate: while picking more lanes, paint the upstream lane the cursor is over (so each add is obvious).
            int hovEdge = -1;
            if (ForceSingleLane && !_extCornerPending && ComputeExtendGroup(cursor, profileId, 5f, out int hovNode, out _) && _grpBuf.Count > 0)
                hovEdge = UpstreamLane(_grpBuf[0], hovNode);
            ShowLaneHover(hovEdge, groundAt);

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
                float srcOff = s.Offset + ShiftAtNode(sc, _extNode);                      // body position at the PULLED end (per-end shift), to match BuildExtensionCorridor + the source's actual pavement
                float oNew = (srcOff * (Vector2.Dot(frS, frNew) >= 0f ? 1f : -1f));
                lo = Mathf.Min(lo, oNew - s.Width * 0.5f); hi = Mathf.Max(hi, oNew + s.Width * 0.5f);
                // Count by the EXTENSION-frame direction (dirNew), matching BuildExtensionCorridor — off the A end the
                // source direction inverts, so source-direction counts would put the surplus on the wrong side.
                bool incN = (s.Direction == 2 && _extNode == s.B) || (s.Direction == 0 && _extNode == s.A);
                if ((incN ? 2 : 0) == 0) { gotBA++; if (Mathf.Abs(oNew) >= baMax) { baMax = Mathf.Abs(oNew); baW = s.Width; } }
                else { gotAB++; if (Mathf.Abs(oNew) >= abMax) { abMax = Mathf.Abs(oNew); abW = s.Width; } }
            }
            if (lo > hi) return;
            // Lane addition: widen the band on the side that gains lanes (BA → lo, AB → hi) so the preview matches the built
            // corridor. Orient the target to the GRABBED lanes' direction majority (gotBA/gotAB), exactly like
            // BuildExtensionCorridor — else a direction-mirrored road previews wider than it builds.
            ProfileLaneSplit(profileId, out int pSplA, out int pSplB);
            int profBig = Mathf.Max(pSplA, pSplB), profSmall = Mathf.Min(pSplA, pSplB);
            int wantBA, wantAB;
            if (gotBA >= gotAB) { wantBA = profBig; wantAB = profSmall; } else { wantBA = profSmall; wantAB = profBig; }
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
                // LOUD armed-bend cue: bright opaque amber construction legs + a big amber bend marker so a placed-but-not-
                // finalised corner can't be mistaken for "nothing happened". Legs ride the PICKED-LANES' centre (offset mid).
                FillPvLine(_pvLegA, N, _extCorner, false, default, default, mid, groundAt, PvPending());
                FillPvLine(_pvLegB, _extCorner, cursor, false, default, default, mid, groundAt, PvPending());
                _pvLegA.widthMultiplier = 0.8f; _pvLegB.widthMultiplier = 0.8f;
                PlaceMarker(_pvCornerM, _extCorner + frNew * mid, groundAt, PvPending());
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
        public static bool ForceSingleLane;                           // Alt/Option held: pick lanes ONE AT A TIME (accumulate), not the profile's whole group
        static bool _extForceSingle;                                  // selection came from Alt-accumulate → build exactly those lanes (skip surplus-lane padding)
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
            if (ForceSingleLane || ProfileLaneCount(profileId) <= 1) { _grpBuf.Add(ep.Edge); single = true; return true; }   // Alt: grab ONLY the clicked lane

            // Whole-road grab: take the clicked corridor's ENTIRE cross-section at the node (every lane, both directions) —
            // "extend the road" should grab the road AS IT ACTUALLY IS, even if its lane count has drifted from the active
            // profile (lanes pulled off then added back). The build canonicalises the copy to the road's own profile. Use
            // Alt to pick a subset instead. (Previously this capped the grab at the active profile's lane split, which
            // under-grabbed any road whose lane count no longer matched the palette profile.)
            for (int i = 0; i < Net.Edges.Count; i++)
            {
                LaneEdge e = Net.Edges[i];
                if ((e.A != node && e.B != node) || e.CorridorId != picked.CorridorId || e.Kind == LaneKind.Sidewalk) continue;
                _grpBuf.Add(i);
            }
            if (_grpBuf.Count == 0) _grpBuf.Add(ep.Edge);
            return true;
        }

        public static bool ToggleExtendPick(Vector2 worldXz, string profileId, float worldR = 5f)
        {
            if (!ComputeExtendGroup(worldXz, profileId, worldR, out int node, out bool single)) return false;
            // At a CONNECTED node (already 2+ corridors — a split/junction), a whole-corridor grab is almost always "add a
            // leg", not "extend this road" — and grabbing steals lanes from a crossing corridor. Suppress it so the click
            // falls through to a fresh JOINED draw. Alt single-lane subset picks (ramps) and free road ends (degree 1) still
            // pull normally.
            int deg = CountCorridorsAtNode(node);
            if (deg >= 3) return false;                  // a true junction → always a fresh leg
            if (deg >= 2 && !single) return false;       // connected node + whole-corridor grab → junction leg, not an extend
            if (_extNode >= 0 && node != _extNode) _extLanes.Clear();   // switched node → restart selection
            _extNode = node;
            if (single)   // single-lane pick: Alt-accumulate (click each lane you want) OR a 1-lane profile → toggle in/out
            {
                int edge = _grpBuf[0];
                if (_extLanes.Contains(edge)) _extLanes.Remove(edge);
                else
                {
                    // Cap the accumulation at the largest one-way profile that exists — a pulled group of k lanes must
                    // canonicalise to a one-way "k", so you can't pull more lanes than the biggest one-way road you've defined.
                    int cap = ForceSingleLane ? RoadProfileLibrary.MaxOneWayLanes() : int.MaxValue;
                    if (cap > 0 && _extLanes.Count >= cap) return true;   // at the cap → ignore the add (click still consumed)
                    _extLanes.Add(edge);
                }
                if (_extLanes.Count == 0) _extNode = -1;
                _extForceSingle = ForceSingleLane && _extLanes.Count > 0;   // Alt subset → build exactly these lanes (skip profile surplus padding)
            }
            else { _extLanes.Clear(); _extLanes.AddRange(_grpBuf); _extForceSingle = false; }   // multi-lane → grab the whole corridor cross-section
            return true;
        }

        static bool TryEndpointPos(int node, int edge, out Vector2 pos, out float y)
        {
            if (_endpointIndex.TryGetValue(EpKey(node, edge), out int i))
            { pos = Endpoints[i].NodePos; y = Endpoints[i].Y; return true; }
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

        public static void CancelExtend() { _extNode = -1; _extLanes.Clear(); _extCornerPending = false; ExtFlipSide = false; _extForceSingle = false; }

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
        // True when (node,edge) is a FULL corridor end — the shared centreline node used by the path lane (lane[0]) — vs a
        // peeled/single lane that sits on its own node off the cluster. Whole-road connect (road-to-road) only applies between
        // two full ends; a peeled lane connects lane-to-lane instead. A peeled/subset corridor is flagged AlignLanes (its
        // lanes sit OFF the centreline at their real offsets), so cloning those offsets onto a centreline path would land the
        // connector off-centre at the far end — exclude it and let BuildConnectCurve join the actual lane pucks.
        static bool IsFullCorridorEnd(int node, int edge)
        {
            if (edge < 0 || edge >= Net.Edges.Count) return false;
            LaneEdge e = Net.Edges[edge];
            Corridor c = (e.CorridorId >= 0 && e.CorridorId < Net.Corridors.Count) ? Net.Corridors[e.CorridorId] : null;
            if (c == null || c.Lanes.Count == 0) return false;
            // A SINGLE peeled lane → lane-to-lane (puck connect): its lone lane IS its path, so BuildConnectCurve lands it on the
            // clicked puck. A MULTI-lane pull-off is still a road → road-to-road, which clones ALL its lanes onto the a/b nodes
            // with no peeling (so all lanes are drawn and no node is orphaned); routing it to the 1-lane path drew one lane and
            // stranded peel nodes.
            if (c.AlignLanes && c.Lanes.Count <= 1) return false;
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            return node == l0.A || node == l0.B;
        }

        public static bool ConnectClick(int node, int edge, Func<Vector2, float> groundAt)
        {
            if (node < 0) return false;   // both clicks must land on a snapped lane puck
            if (_connA < 0) { _connA = node; _connEdgeA = edge; Rebuild(groundAt); return true; }
            if (node != _connA)
            {
                if (IsFullCorridorEnd(_connA, _connEdgeA) && IsFullCorridorEnd(node, edge))
                    BuildCorridorConnectCurve(_connA, _connEdgeA, node, edge, groundAt);   // both full road ends → multi-lane curve
                else
                    BuildConnectCurve(_connA, _connEdgeA, node, edge, groundAt);           // a peeled/single lane → 1-lane curve
            }
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

        // Outward road tangent at the corridor end on `node` (the A→B path tangent, flipped to point toward `toward`).
        static Vector2 CorridorEndTangent(Corridor c, LaneEdge end, int node, Vector2 toward, Vector2 fallback)
        {
            if (c == null) return fallback;
            Vector2 tan = LaneEdgeCorridorBuilder.PathTangent(Net, c, node == end.A ? 0f : 1f);
            if (tan.sqrMagnitude < 1e-6f) return fallback;
            return Vector2.Dot(tan, toward) < 0f ? -tan.normalized : tan.normalized;
        }

        // Lane-span centre (offset) of a corridor's navigable lanes — the lateral middle of its cross-section.
        static float LaneSpanCentre(Corridor c)
        {
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            if (c != null)
                foreach (int li in c.Lanes)
                {
                    if (li < 0 || li >= Net.Edges.Count) continue;
                    LaneEdge e = Net.Edges[li]; if (e.Kind == LaneKind.Sidewalk) continue;
                    lo = Mathf.Min(lo, e.Offset - e.Width * 0.5f); hi = Mathf.Max(hi, e.Offset + e.Width * 0.5f);
                }
            return float.IsInfinity(lo) ? 0f : (lo + hi) * 0.5f;
        }

        // The body lateral shift at the corridor end touching `node` (A→CenterShift, B→CenterShiftB).
        static float ShiftAtNode(Corridor c, int node)
        {
            if (c == null || c.Lanes.Count == 0) return 0f;
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            return (node == l0.B) ? c.CenterShiftB : c.CenterShift;
        }

        // World position of corridor c's body CENTRE at `node` = node + right·(lane-span centre + body shift at this end).
        static Vector2 CorridorBodyCentre(Corridor c, int node)
        {
            if (node < 0 || node >= Net.Nodes.Count) return Vector2.zero;
            if (c == null || c.Lanes.Count == 0) return Net.Nodes[node];
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            float t = (node == l0.A) ? 0f : 1f;
            Vector2 fr = LaneEdgeCorridorBuilder.PathRight(LaneEdgeCorridorBuilder.PathTangent(Net, c, t));
            return Net.Nodes[node] + fr * (LaneSpanCentre(c) + ShiftAtNode(c, node));
        }

        // Split corridor c's navigable lanes at `node` into those flowing TOWARD the node (incoming) and AWAY (outgoing).
        static void SplitByFlow(Corridor c, int node, List<int> inc, List<int> outg)
        {
            if (c == null) return;
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= Net.Edges.Count) continue;
                LaneEdge e = Net.Edges[li]; if (e.Kind != LaneKind.Traffic) continue;
                if (e.A != node && e.B != node) continue;
                ((e.Direction == 2 && node == e.B) || (e.Direction == 0 && node == e.A) ? inc : outg).Add(li);
            }
        }

        // World body position of lane `li` (of corridor c) at `node` = node + right·(offset + shift at this end).
        static Vector2 LaneBodyPos(Corridor c, int li, int node)
        {
            if (node < 0 || node >= Net.Nodes.Count) return Vector2.zero;
            if (c == null || c.Lanes.Count == 0 || li < 0 || li >= Net.Edges.Count) return Net.Nodes[node];
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            float t = (node == l0.A) ? 0f : 1f;
            Vector2 fr = LaneEdgeCorridorBuilder.PathRight(LaneEdgeCorridorBuilder.PathTangent(Net, c, t));
            return Net.Nodes[node] + fr * (Net.Edges[li].Offset + ShiftAtNode(c, node));
        }

        // Body centre of a SUBSET of corridor c's lanes at `node` (world). Falls back to the whole-corridor centre if empty.
        static Vector2 SubsetBodyCentre(Corridor c, int node, List<int> lanes)
        {
            if (c == null || lanes == null || lanes.Count == 0 || node < 0 || node >= Net.Nodes.Count) return CorridorBodyCentre(c, node);
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (int li in lanes) { if (li < 0 || li >= Net.Edges.Count) continue; LaneEdge e = Net.Edges[li]; lo = Mathf.Min(lo, e.Offset - e.Width * 0.5f); hi = Mathf.Max(hi, e.Offset + e.Width * 0.5f); }
            if (float.IsInfinity(lo)) return CorridorBodyCentre(c, node);
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            float t = (node == l0.A) ? 0f : 1f;
            Vector2 fr = LaneEdgeCorridorBuilder.PathRight(LaneEdgeCorridorBuilder.PathTangent(Net, c, t));
            return Net.Nodes[node] + fr * ((lo + hi) * 0.5f + ShiftAtNode(c, node));
        }

        static void SortByLateral(List<int> lanes, Corridor c, int node, Vector2 perp)
        {
            Vector2 n = (node >= 0 && node < Net.Nodes.Count) ? Net.Nodes[node] : Vector2.zero;
            lanes.Sort((x, y) => Vector2.Dot(LaneBodyPos(c, x, node) - n, perp).CompareTo(Vector2.Dot(LaneBodyPos(c, y, node) - n, perp)));
        }

        // WHOLE-ROAD connect (road-to-road): ONE tangent-continuous connector joining node `a` to node `b`. CLEAN DIVIDED-HIGHWAY
        // SPLIT — only the lanes that actually CONTINUE through connect: A's lanes feeding the join (incoming to a) pair with B's
        // lanes carrying it onward (outgoing from b), and the return direction pairs B-incoming with A-outgoing; each direction
        // joins min(counts) lanes by lateral order. So a one-way ↔ ONE direction of a two-way joins only that direction's lanes,
        // aligned to its off-centre half. The connector adopts the matched cross-section's profile (one shoulder set), shares both
        // end nodes (flow auto-wires, no peeling), and lands flush via the TWO-ENDED body shift onto each MATCHED subset's centre.
        static void BuildCorridorConnectCurve(int a, int edgeA, int b, int edgeB, Func<Vector2, float> groundAt)
        {
            if (a < 0 || b < 0 || a == b || a >= Net.Nodes.Count || b >= Net.Nodes.Count) return;
            if (edgeA < 0 || edgeA >= Net.Edges.Count) return;
            LaneEdge eA = Net.Edges[edgeA];
            Corridor cA = (eA.CorridorId >= 0 && eA.CorridorId < Net.Corridors.Count) ? Net.Corridors[eA.CorridorId] : null;
            if (cA == null) return;
            LaneEdge eB0 = (edgeB >= 0 && edgeB < Net.Edges.Count) ? Net.Edges[edgeB] : null;
            Corridor cB = (eB0 != null && eB0.CorridorId >= 0 && eB0.CorridorId < Net.Corridors.Count) ? Net.Corridors[eB0.CorridorId] : null;
            Vector2 pa = Net.Nodes[a], pb = Net.Nodes[b];
            Vector2 tanA = CorridorEndTangent(cA, eA, a, pb - pa, SafeDir(pb - pa));
            Vector2 tanB = SafeDir(pa - pb);
            if (eB0 != null) tanB = CorridorEndTangent(cB, eB0, b, pa - pb, SafeDir(pa - pb));
            float d = (pb - pa).magnitude * 0.4f;
            Vector2 perp = LaneEdgeCorridorBuilder.PathRight(SafeDir(pb - pa));

            // Match lanes by flow direction: forward (A-incoming↔B-outgoing), return (A-outgoing↔B-incoming), paired by lateral order.
            var aIn = new List<int>(); var aOut = new List<int>(); SplitByFlow(cA, a, aIn, aOut);
            var bIn = new List<int>(); var bOut = new List<int>();
            if (cB != null) SplitByFlow(cB, b, bIn, bOut);
            else if (eB0 != null && eB0.Kind == LaneKind.Traffic) { bool inc = (eB0.Direction == 2 && b == eB0.B) || (eB0.Direction == 0 && b == eB0.A); (inc ? bIn : bOut).Add(edgeB); }
            SortByLateral(aIn, cA, a, perp); SortByLateral(aOut, cA, a, perp);
            SortByLateral(bOut, cB, b, perp); SortByLateral(bIn, cB, b, perp);
            int fc = Mathf.Min(aIn.Count, bOut.Count), rc = Mathf.Min(aOut.Count, bIn.Count);
            var matchA = new List<int>();
            for (int i = 0; i < fc; i++) matchA.Add(aIn[i]);
            for (int i = 0; i < rc; i++) matchA.Add(aOut[i]);
            if (matchA.Count == 0) { foreach (int sl in cA.Lanes) if (sl >= 0 && sl < Net.Edges.Count && Net.Edges[sl].Kind == LaneKind.Traffic) matchA.Add(sl); }   // no shared direction → clone all (degenerate)
            var matchB = new List<int>();
            for (int i = 0; i < fc; i++) matchB.Add(bOut[i]);
            for (int i = 0; i < rc; i++) matchB.Add(bIn[i]);

            Corridor nc = Net.AddCorridor();
            nc.Curved = true; nc.ControlA = pa + tanA * d; nc.ControlB = pb + tanB * d;
            nc.AlignLanes = false; nc.MedianWidth = cA.MedianWidth;
            bool sameSense = (a == eA.B);
            foreach (int sl in matchA)
            {
                if (sl < 0 || sl >= Net.Edges.Count) continue;
                LaneEdge s = Net.Edges[sl];
                float off = sameSense ? s.Offset : -s.Offset;
                int dir = sameSense ? s.Direction : (s.Direction == 2 ? 0 : s.Direction == 0 ? 2 : s.Direction);
                int li = Net.AddLane(new LaneEdge { A = a, B = b, CorridorId = nc.Id, Kind = s.Kind, Direction = dir, Width = s.Width, Offset = off });
                nc.Lanes.Add(li);
            }
            Net.SortCorridorLanes(nc);

            // Profile + shoulders for the connector's ACTUAL (matched) cross-section — e.g. a 4-lane half of a 4×4 becomes a "4".
            int cab = 0, cba = 0;
            foreach (int li in nc.Lanes) { LaneEdge e = Net.Edges[li]; if (e.Kind != LaneKind.Traffic) continue; if (e.Direction == 2) cab++; else if (e.Direction == 0) cba++; }
            string pk = RoadProfileLibrary.FindByConfig(cab, cba, cA.Profile);
            var rp = !string.IsNullOrEmpty(pk) ? RoadProfileLibrary.Resolve(pk) : null;
            if (rp != null) { nc.Profile = pk; nc.ShoulderBA = rp.ShoulderBA != null ? rp.ShoulderBA.Width : 0f; nc.ShoulderAB = rp.ShoulderAB != null ? rp.ShoulderAB.Width : 0f; }
            else { nc.Profile = cA.Profile; nc.ShoulderBA = cA.ShoulderBA; nc.ShoulderAB = cA.ShoulderAB; if (!sameSense) { float t = nc.ShoulderBA; nc.ShoulderBA = nc.ShoulderAB; nc.ShoulderAB = t; } }

            // Two-ended body shift onto each MATCHED subset's centre (the off-centre half, for a one-way↔two-way join).
            Vector2 frA = LaneEdgeCorridorBuilder.PathRight(tanA);
            Vector2 frB = LaneEdgeCorridorBuilder.PathRight(-tanB);
            float ncMid = LaneSpanCentre(nc);
            Vector2 srcC = SubsetBodyCentre(cA, a, matchA);
            Vector2 tgtC = cB != null ? SubsetBodyCentre(cB, b, matchB) : (eB0 != null ? pb + frB * eB0.Offset : pb);
            nc.CenterShift  = Vector2.Dot(srcC - pa, frA) - ncMid;
            nc.CenterShiftB = Vector2.Dot(tgtC - pb, frB) - ncMid;

            RegenerateDefaultFlows(groundAt);
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
            if (_connA < 0 || _connA >= Net.Nodes.Count || _connEdgeA < 0 || _connEdgeA >= Net.Edges.Count) return;
            LaneEdge eA = Net.Edges[_connEdgeA];
            Corridor cA = (eA.CorridorId >= 0 && eA.CorridorId < Net.Corridors.Count) ? Net.Corridors[eA.CorridorId] : null;
            if (cA == null) return;
            // Mirror ConnectClick's branch so the ghost matches what gets built. ROAD-TO-ROAD draws the connector's BODY centreline
            // (source body-centre → target body-centre, or the cursor while unsnapped) — the same slewed line the two-ended-shift
            // connector renders, so the dashed guide lands where the connector will (drawing from the clicked lane's puck put the
            // guide a lane off the body centre). Otherwise LANE-TO-LANE from the clicked lane pucks.
            bool srcFull = IsFullCorridorEnd(_connA, _connEdgeA);
            bool tgtFull = srcFull && toNode >= 0 && toEdge >= 0 && toEdge < Net.Edges.Count && IsFullCorridorEnd(toNode, toEdge);
            Vector2 fromP, toP, tanA, tanB;
            if (srcFull)
            {
                fromP = CorridorBodyCentre(cA, _connA);
                if (tgtFull)
                {
                    LaneEdge eB = Net.Edges[toEdge];
                    Corridor cB = (eB.CorridorId >= 0 && eB.CorridorId < Net.Corridors.Count) ? Net.Corridors[eB.CorridorId] : null;
                    toP = CorridorBodyCentre(cB, toNode);
                    if ((toP - fromP).sqrMagnitude < 1f) return;
                    tanA = CorridorEndTangent(cA, eA, _connA, toP - fromP, SafeDir(toP - fromP));
                    tanB = CorridorEndTangent(cB, eB, toNode, fromP - toP, SafeDir(fromP - toP));
                }
                else
                {
                    toP = toXz;
                    if ((toP - fromP).sqrMagnitude < 1f) return;
                    tanA = CorridorEndTangent(cA, eA, _connA, toP - fromP, SafeDir(toP - fromP));
                    tanB = SafeDir(fromP - toP);
                }
            }
            else
            {
                if (!TryEndpointPos(_connA, _connEdgeA, out fromP, out _)) return;
                toP = toXz;
                if (toNode >= 0 && toEdge >= 0 && toEdge < Net.Edges.Count && TryEndpointPos(toNode, toEdge, out Vector2 tp, out _)) toP = tp;
                if ((toP - fromP).sqrMagnitude < 1f) return;
                tanA = SafeDir(toP - fromP);
                if (LaneGuideFrame(_connA, _connEdgeA, out _, out Vector2 ta)) tanA = Vector2.Dot(ta, toP - fromP) >= 0f ? ta : -ta;
                tanB = SafeDir(fromP - toP);
                if (toNode >= 0 && toEdge >= 0 && toEdge < Net.Edges.Count && LaneGuideFrame(toNode, toEdge, out _, out Vector2 tb)) tanB = Vector2.Dot(tb, fromP - toP) >= 0f ? tb : -tb;
            }
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

        // ── parallel draw (hold Ctrl after placing the start on a node's perpendicular guide) ──
        // Workflow: click to place the new road's START on a lane node's PERPENDICULAR guide → hold Ctrl → drag. The road
        // grows from that start, runs PARALLEL to the corridor whose guide the start sits on (following its curvature), and
        // ends where you drag (length = the source param the cursor projects to). A second click commits it. The clone reuses
        // the source's own profile. Ghost = the offset centreline + a direction arrow.
        static GameObject _parGuideGo; static Mesh _parGuideMesh; static Material _parGuideMat;
        static Material ParGuideMat() => _parGuideMat != null ? _parGuideMat
            : (_parGuideMat = NetworkDesigner.PipelineMaterials.CreateUnlitColor(new Color(0.25f, 0.95f, 0.85f, 1f), "LaneParallelGuide"));
        static bool _parArmed; static Vector2 _parQ0, _parQ1, _parQ2, _parQ3; static bool _parCurved; static string _parProfile;
        static int _parStartEnd;   // which clone end coincides with the placed start: 0 = q0 (A), 1 = q3 (B)
        public static bool ParallelArmed => _parArmed;

        public static void ClearParallelPreview()
        {
            _parArmed = false;
            if (_parGuideGo != null) _parGuideGo.SetActive(false);
        }

        // Cubic sub-segment over [lo,hi] (0≤lo≤hi≤1) of the bezier (p0,c1,c2,p3), via two de Casteljau splits.
        static void SubCubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float lo, float hi,
                             out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3)
        {
            // split at hi → LEFT sub-cubic over [0,hi]
            Vector2 a01 = Vector2.Lerp(p0, c1, hi), a12 = Vector2.Lerp(c1, c2, hi), a23 = Vector2.Lerp(c2, p3, hi);
            Vector2 a012 = Vector2.Lerp(a01, a12, hi), a123 = Vector2.Lerp(a12, a23, hi);
            Vector2 a0123 = Vector2.Lerp(a012, a123, hi);
            Vector2 L0 = p0, L1 = a01, L2 = a012, L3 = a0123;
            // within L, original param lo maps to u = lo/hi → split L at u, take its RIGHT sub-cubic = [lo,hi]
            float u = hi > 1e-6f ? lo / hi : 0f;
            Vector2 b01 = Vector2.Lerp(L0, L1, u), b12 = Vector2.Lerp(L1, L2, u), b23 = Vector2.Lerp(L2, L3, u);
            Vector2 b012 = Vector2.Lerp(b01, b12, u), b123 = Vector2.Lerp(b12, b23, u);
            Vector2 b0123 = Vector2.Lerp(b012, b123, u);
            q0 = b0123; q1 = b123; q2 = b23; q3 = L3;
        }

        // Right-perpendicular unit normal of the leg a→b (zero if degenerate).
        static Vector2 LegNormal(Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-10f) return Vector2.zero;
            d.Normalize(); return new Vector2(d.y, -d.x);
        }

        // Intersection of infinite lines (p + s·dp) and (q + s·dq); fallback if near-parallel.
        static Vector2 LineX(Vector2 p, Vector2 dp, Vector2 q, Vector2 dq, Vector2 fallback)
        {
            float denom = dp.x * dq.y - dp.y * dq.x;
            if (Mathf.Abs(denom) < 1e-6f) return fallback;
            float t = ((q.x - p.x) * dq.y - (q.y - p.y) * dq.x) / denom;
            return p + dp * t;
        }

        // Parallel offset of cubic (p0,c1,c2,p3) to its right by signed d (Tiller–Hanson: offset each control-polygon leg by
        // d, intersect adjacent offset legs for the interior controls). Far closer to a true EQUIDISTANT parallel than
        // offsetting the control points by the endpoint normals. Endpoints/tangents are preserved (q0,q3 land on the legs).
        static void OffsetCubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float d,
                                out Vector2 q0, out Vector2 q1, out Vector2 q2, out Vector2 q3)
        {
            Vector2 n0 = LegNormal(p0, c1), n1 = LegNormal(c1, c2), n2 = LegNormal(c2, p3);
            if (n0 == Vector2.zero) n0 = n1; if (n2 == Vector2.zero) n2 = n1; if (n1 == Vector2.zero) n1 = n0;
            Vector2 a0 = p0 + n0 * d, a1 = c1 + n0 * d;     // offset leg L0
            Vector2 b0 = c1 + n1 * d, b1 = c2 + n1 * d;     // offset leg L1
            Vector2 g0 = c2 + n2 * d, g1 = p3 + n2 * d;     // offset leg L2
            q0 = a0; q3 = g1;
            q1 = LineX(a0, a1 - a0, b0, b1 - b0, c1 + n1 * d);
            q2 = LineX(b0, b1 - b0, g0, g1 - g0, c2 + n1 * d);
        }

        // One parallel-offset candidate: the segment that would be drawn paralleling corridor `c` off its `sourceNode` end.
        struct ParCand { public bool Ok; public Vector2 Q0, Q1, Q2, Q3, Far; public bool Curved; public string Profile; public int StartEnd; }
        static readonly System.Collections.Generic.HashSet<long> _parSeen = new System.Collections.Generic.HashSet<long>();

        // Build the parallel segment for corridor c off its `sourceNode` end, given the placed start + drag cursor. The new
        // road is laid with `profileId` (the active/selected profile) — NOT the source's — so you can parallel a 4-lane road
        // with a 1-lane one. Only the offset PATH comes from the source corridor.
        static ParCand BuildParallelSeg(Corridor c, int sourceNode, Vector2 startPos, Vector2 cursor, string profileId)
        {
            ParCand r = default;
            if (c == null || c.Lanes.Count == 0) return r;
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            if (sourceNode != l0.A && sourceNode != l0.B) return r;
            if (!LaneEdgeCorridorBuilder.PathFrameShifted(Net, c, out Vector2 A, out Vector2 B, out Vector2 scA, out Vector2 scB)) return r;
            float tN = (sourceNode == l0.A) ? 0f : 1f;
            Vector2 Ncl = (tN == 0f) ? A : B;
            Vector2 tanN = LaneEdgeCorridorBuilder.PathTangent(Net, c, tN);
            Vector2 to = startPos - Ncl;
            float offR = Vector2.Dot(to, LaneEdgeCorridorBuilder.PathRight(tanN));   // signed perpendicular offset of the start
            float along = Vector2.Dot(to, tanN);
            if (Mathf.Abs(offR) < 0.3f) return r;                                    // start basically on the road → no parallel
            if (Mathf.Abs(along) > Mathf.Abs(offR) + 6f) return r;                   // start must sit ~perpendicular off THIS end
            // Length: the source param whose offset point is nearest the cursor (offset rotates with the curve).
            const int K = 48; float tEnd = tN, bd = float.MaxValue;
            for (int i = 0; i <= K; i++)
            {
                float t = i / (float)K;
                Vector2 op = LaneEdgeCorridorBuilder.PathPoint(Net, c, t)
                           + LaneEdgeCorridorBuilder.PathRight(LaneEdgeCorridorBuilder.PathTangent(Net, c, t)) * offR;
                float d = (op - cursor).sqrMagnitude; if (d < bd) { bd = d; tEnd = t; }
            }
            float lo = Mathf.Min(tN, tEnd), hi = Mathf.Max(tN, tEnd);
            if (hi - lo <= 1e-3f) return r;
            r.Curved = c.Curved; r.Profile = profileId;
            if (c.Curved)
            {
                SubCubic(A, scA, scB, B, lo, hi, out Vector2 s0, out Vector2 s1, out Vector2 s2, out Vector2 s3);   // shifted controls (CenterShift-aware)
                OffsetCubic(s0, s1, s2, s3, offR, out r.Q0, out r.Q1, out r.Q2, out r.Q3);
            }
            else
            {
                Vector2 rr = LaneEdgeCorridorBuilder.PathRight((B - A).normalized);
                r.Q0 = Vector2.Lerp(A, B, lo) + rr * offR; r.Q3 = Vector2.Lerp(A, B, hi) + rr * offR;
                r.Q1 = r.Q0; r.Q2 = r.Q3;
            }
            // Orient the built road from the placed START toward the drawn END (the drag direction), so a one-way flows the
            // way you draw it instead of blindly inheriting the source's A→B. The quad above runs lo→hi; if the start sits at
            // the hi end, reverse it (P0,P1,P2,P3 → P3,P2,P1,P0) so q0 = start, q3 = far.
            if (tN != 0f)
            {
                Vector2 a = r.Q0, b = r.Q1, cc = r.Q2, dd = r.Q3;
                r.Q0 = dd; r.Q1 = cc; r.Q2 = b; r.Q3 = a;
            }
            r.StartEnd = 0;        // after the optional reverse the start is always q0…
            r.Far = r.Q3;          // …and the far (drawn) end is always q3
            r.Ok = true;
            return r;
        }

        // Build the parallel-offset segment for a placed start + drag cursor, and ghost it. Returns true when armed. Evaluates
        // every source corridor whose end sits near the start (excluding our own road), and picks the one whose far end is
        // nearest the cursor — i.e. the segment that CONTINUES FORWARD. This lets a run carry on past a junction or start
        // beside a mid-road node, not just off a road's free end.
        public static bool UpdateParallelDraw(Vector2 startPos, Vector2 cursor, Func<Vector2, float> groundAt, string profileId)
        {
            _parArmed = false;
            float rng = Mathf.Max(40f, NetworkDesigner.Terrain.PlanGuides.GuideRange); float rng2 = rng * rng;
            _parSeen.Clear();
            ParCand best = default; float bestFar = float.MaxValue;
            for (int i = 0; i < Endpoints.Count; i++)
            {
                if (Endpoints[i].Node == _drawStart) continue;                       // our own road's chained end → skip
                if ((Endpoints[i].NodePos - startPos).sqrMagnitude > rng2) continue;
                LaneEdge e = Net.Edges[Endpoints[i].Edge];
                if (e.CorridorId < 0 || e.CorridorId >= Net.Corridors.Count) continue;
                long key = ((long)e.CorridorId << 24) ^ (uint)Endpoints[i].Node;
                if (!_parSeen.Add(key)) continue;                                   // one eval per (corridor, end)
                ParCand cand = BuildParallelSeg(Net.Corridors[e.CorridorId], Endpoints[i].Node, startPos, cursor, profileId);
                if (!cand.Ok) continue;
                float d = (cand.Far - cursor).sqrMagnitude;
                if (d < bestFar) { bestFar = d; best = cand; }
            }
            if (best.Ok)
            {
                _parQ0 = best.Q0; _parQ1 = best.Q1; _parQ2 = best.Q2; _parQ3 = best.Q3;
                _parCurved = best.Curved; _parProfile = best.Profile; _parStartEnd = best.StartEnd;
                _parArmed = true;
            }

            if (_parGuideGo == null)
            {
                _parGuideGo = new GameObject("LaneParallelGuide");
                _parGuideGo.AddComponent<MeshFilter>();
                var mr = _parGuideGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = ParGuideMat();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                _parGuideMesh = new Mesh { name = "LaneParallelGuide" };
                _parGuideGo.GetComponent<MeshFilter>().sharedMesh = _parGuideMesh;
            }
            _sgVerts.Clear(); _sgTris.Clear();
            if (_parArmed)
            {
                Vector2 prev = _parQ0; const int N = 28;
                for (int i = 1; i <= N; i++)
                {
                    float t = i / (float)N;
                    Vector2 pt = _parCurved ? GeometryResolver.SampleCubic(_parQ0, _parQ1, _parQ2, _parQ3, t) : Vector2.Lerp(_parQ0, _parQ3, t);
                    EmitDashGuide(prev, pt, groundAt);
                    prev = pt;
                }
                Vector2 mid = _parCurved ? GeometryResolver.SampleCubic(_parQ0, _parQ1, _parQ2, _parQ3, 0.5f) : (_parQ0 + _parQ3) * 0.5f;
                Vector2 mtan = _parCurved ? GeometryResolver.CubicTangent(_parQ0, _parQ1, _parQ2, _parQ3, 0.5f) : (_parQ3 - _parQ0);
                // Arrow points from the placed start toward the drawn end.
                if (_parStartEnd == 1) mtan = -mtan;
                if (mtan.sqrMagnitude > 1e-6f) EmitGuideArrow(mid, mtan.normalized, groundAt);
            }
            _parGuideMesh.Clear();
            if (_sgVerts.Count == 0) { _parGuideGo.SetActive(false); return _parArmed; }
            _parGuideMesh.SetVertices(_sgVerts); _parGuideMesh.SetTriangles(_sgTris, 0); _parGuideMesh.RecalculateBounds();
            _parGuideGo.SetActive(true);
            return _parArmed;
        }

        // Commit the armed parallel segment: reuse the placed start node for its near end, add a node for the far end, build
        // the corridor with the source profile/curve, then CHAIN — keep drawing from the committed far end so the run can
        // continue alongside the next source segment past a junction. Press Escape to end the chain. Returns false if nothing
        // is armed / the segment is degenerate.
        public static bool CommitParallelDraw(Func<Vector2, float> groundAt)
        {
            if (!_parArmed || _drawStart < 0) return false;
            _parArmed = false;
            if (_parGuideGo != null) _parGuideGo.SetActive(false);
            if ((_parQ3 - _parQ0).sqrMagnitude < 1f) return false;
            int startNode = _drawStart;
            int q0Node, q3Node, farNode;
            if (_parStartEnd == 0) { Net.Nodes[startNode] = _parQ0; q0Node = startNode; q3Node = Net.AddNode(_parQ3); farNode = q3Node; }
            else { Net.Nodes[startNode] = _parQ3; q3Node = startNode; q0Node = Net.AddNode(_parQ0); farNode = q0Node; }
            AddCorridorFromProfile(q0Node, q3Node, _parProfile, _parCurved, _parQ1, _parQ2);
            _drawStart = farNode; _cornerPending = false;   // chain from the committed end
            Rebuild(groundAt);
            return true;
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
            // A guide drawn from a degenerate node (placed at a huge coordinate) to the cursor would be astronomically
            // long → millions of dash quads / float-stall → editor freeze. Skip such a guide.
            if (float.IsNaN(len) || float.IsInfinity(len) || len > 50000f) { Debug.LogError($"[EmitDashGuide] skipped degenerate guide len={len:F0} a={a} b={b}"); return; }
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
            // No-go: a pull-off must map BOTH resulting roads onto existing profiles. Check the pulled group's config AND the
            // remainder (= source road − pulled); refuse if either has no profile. The PULLED check rarely fires (you needed a
            // one-way "k" profile to select k lanes at all); the REMAINDER is the one that bites — pull 3 off a 4×4 and you need
            // a 1×4 to exist. A whole-road continuation has an empty remainder and is exempt.
            {
                int pab = 0, pba = 0;
                foreach (int sl in _extLanes)
                {
                    if (sl < 0 || sl >= Net.Edges.Count) continue;
                    LaneEdge e = Net.Edges[sl]; if (e.Kind != LaneKind.Traffic) continue;
                    if (e.Direction == 2) pab++; else if (e.Direction == 0) pba++;
                }
                if (pab + pba > 0 && RoadProfileLibrary.FindByConfig(pab, pba) == null)
                { Debug.LogWarning($"[Pull-off] refused: pulled group ({pab},{pba}) has no profile"); return false; }

                int sab = 0, sba = 0;
                if (_extLanes.Count > 0 && _extLanes[0] >= 0 && _extLanes[0] < Net.Edges.Count)
                {
                    int cid = Net.Edges[_extLanes[0]].CorridorId;
                    Corridor srcC = (cid >= 0 && cid < Net.Corridors.Count) ? Net.Corridors[cid] : null;
                    if (srcC != null)
                        foreach (int li in srcC.Lanes)
                        { if (li < 0 || li >= Net.Edges.Count) continue; LaneEdge e = Net.Edges[li]; if (e.Kind != LaneKind.Traffic) continue; if (e.Direction == 2) sab++; else if (e.Direction == 0) sba++; }
                }
                // Only a STRICT SUBSET pull-off reduces a downstream road. Selecting ALL the source's lanes is a whole-road
                // continuation (extend a 4×4 with a 4×4) — no remainder, exempt. (Also exempt when the source config is
                // indeterminate, sab=sba=0, so we never refuse on missing data.)
                bool wholeRoad = pab >= sab && pba >= sba;
                if (!wholeRoad)
                {
                    // Remainder reduction follows the per-pull ExitMode (cycled on the RoadPalette button): DeleteAll drops all
                    // k pulled continuations (remainder N−k), DeleteOuter drops just the outer one (N−1 in the pulled direction),
                    // Keep drops nothing. Source config approximates the downstream; ApplyExitPull re-profiles the actual one.
                    int delAb = 0, delBa = 0;
                    if (ExitMode == ExitPullMode.DeleteAll) { delAb = pab; delBa = pba; }
                    else if (ExitMode == ExitPullMode.DeleteOuter) { if (pab > 0) delAb = 1; else if (pba > 0) delBa = 1; }
                    int rab = sab - delAb, rba = sba - delBa;
                    if (delAb + delBa > 0 && rab + rba > 0 && (rab < 0 || rba < 0 || RoadProfileLibrary.FindByConfig(rab, rba) == null))
                    { Debug.LogWarning($"[Pull-off] refused: remainder ({rab},{rba}) has no profile (ExitMode {ExitMode})"); return false; }
                }
            }
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
                float srcOff = s.Offset + ShiftAtNode(sc, nodeN);                        // source lane's BODY position at the PULLED end (per-end shift) — extending off its node-relative offset would land it shift metres off the source
                float oNew = (srcOff * (Vector2.Dot(frS, frNew) >= 0f ? 1f : -1f));       // preserve the lane's offset magnitude (re-spread on the new frame) so a turn doesn't collapse lanes to centre
                bool incomingAtN = (s.Direction == 2 && nodeN == s.B) || (s.Direction == 0 && nodeN == s.A);
                int dirNew = incomingAtN ? 2 : 0;                  // arriving lane → continue outward (A'=N→B'); return lane → inbound
                int li = Net.AddLane(new LaneEdge { A = nodeN, B = nodeM, CorridorId = nc.Id, Kind = s.Kind, Direction = dirNew, Width = s.Width, Offset = oNew });
                nc.Lanes.Add(li);
                if (string.IsNullOrEmpty(nc.Profile) && sc != null) { nc.Profile = sc.Profile; nc.ShoulderBA = sc.ShoulderBA; nc.ShoulderAB = sc.ShoulderAB; nc.MedianWidth = sc.MedianWidth; }
            }
            // Lane addition: append surplus profile lanes outboard to reach the active profile's lane count. Orient the
            // per-direction target to the SOURCE ROAD'S actual direction majority (the copied lanes' BA/AB split), not the
            // profile's nominal sides — a direction-mirrored 2x4 (4 BA + 2 AB) must stay (4,2); otherwise AppendOutboard pads
            // the minority side up to the profile's BIG count and a "2x4" wrongly inflates to a "4x4". Counting nc's CURRENT
            // lanes also subsumes the A-end direction-label flip (incomingAtN). Symmetric profiles (4x4) are orientation-free.
            ProfileLaneSplit(profileId, out int pSplA, out int pSplB);
            int profBig = Mathf.Max(pSplA, pSplB), profSmall = Mathf.Min(pSplA, pSplB);
            int curBA = 0, curAB = 0;
            foreach (int ei in nc.Lanes)
            { if (ei < 0 || ei >= Net.Edges.Count) continue; LaneEdge e = Net.Edges[ei]; if (e.Kind != LaneKind.Traffic) continue; if (e.Direction == 0) curBA++; else if (e.Direction == 2) curAB++; }
            int wantBA, wantAB;
            if (curBA >= curAB) { wantBA = profBig; wantAB = profSmall; } else { wantBA = profSmall; wantAB = profBig; }
            if (ExtFlipSide) { int t = wantBA; wantBA = wantAB; wantAB = t; }   // F: mirror which side gains the surplus lane
            // Alt force-single pull: keep EXACTLY the grabbed lane(s) — skip the profile's surplus-lane padding so a 1-lane
            // pick off a 6-lane road stays a 1-lane ramp (canonicalised to "1" below) instead of being re-inflated to the profile.
            if (!_extForceSingle && wantBA > 0 && wantAB > 0) { AppendOutboard(nc, 0, wantBA); AppendOutboard(nc, 2, wantAB); }
            Net.SortCorridorLanes(nc);
            // Adopt the matching one-way profile for the pulled group AND canonicalise it. The group inherited the SOURCE profile
            // and the PARENT's lane offsets, so a 2-lane pull-off off a "4" carried profile "4" and offsets like +2.75/+6.75
            // instead of a standalone "2"'s −1.25/+2.75. FindByConfig picks the one-way k-lane profile ("1".."4"); we then replace
            // each lane's offset with that profile's CANONICAL offset and store the constant lateral Δ as CenterShift — so the
            // body stays on the same pavement while the lanes' nodes stay shared with the parent for flow. Result: a pulled "k" is
            // byte-for-byte a standalone "k" (same profile ⇒ same offsets) → the clone-connect lands flush.
            {
                int cab = 0, cba = 0;
                foreach (int li in nc.Lanes)
                {
                    if (li < 0 || li >= Net.Edges.Count) continue;
                    LaneEdge e = Net.Edges[li]; if (e.Kind != LaneKind.Traffic) continue;
                    if (e.Direction == 2) cab++; else if (e.Direction == 0) cba++;
                }
                string pk = RoadProfileLibrary.FindByConfig(cab, cba, nc.Profile);
                if (!string.IsNullOrEmpty(pk))
                {
                    nc.Profile = pk;
                    var rp = RoadProfileLibrary.Resolve(pk);
                    if (rp != null)
                    {
                        if (rp.ShoulderBA != null) nc.ShoulderBA = rp.ShoulderBA.Width;
                        if (rp.ShoulderAB != null) nc.ShoulderAB = rp.ShoulderAB.Width;
                    }
                    // Canonical offsets for pk (= what a standalone road uses), mirrored to the pulled group's travel side.
                    ProfileLanes(pk, out _, out _);
                    int pulledDir = cab > 0 ? 2 : 0;
                    int pkDir = _laneBuf.Count > 0 ? _laneBuf[0].dir : pulledDir;
                    float mir = pkDir == pulledDir ? 1f : -1f;
                    var canon = new List<float>();
                    foreach (var lb in _laneBuf) canon.Add(lb.off * mir);
                    canon.Sort();
                    var pulled = new List<int>();
                    foreach (int li in nc.Lanes) if (li >= 0 && li < Net.Edges.Count && Net.Edges[li].Kind == LaneKind.Traffic) pulled.Add(li);
                    pulled.Sort((x, y) => Net.Edges[x].Offset.CompareTo(Net.Edges[y].Offset));
                    if (pulled.Count == canon.Count && pulled.Count > 0)
                    {
                        float shift = 0f;
                        for (int i = 0; i < pulled.Count; i++) shift += Net.Edges[pulled[i]].Offset - canon[i];
                        shift /= pulled.Count;
                        for (int i = 0; i < pulled.Count; i++) Net.Edges[pulled[i]].Offset = canon[i];
                        nc.CenterShift = shift; nc.CenterShiftB = shift;   // uniform shift along the whole pull-off
                        nc.AlignLanes = false;        // it's now a canonical road, body sits on the shifted centreline
                        Net.SortCorridorLanes(nc);
                    }
                }
                else Debug.LogWarning($"[Pull-off] no profile with config ({cab},{cba}) — pulled group keeps profile '{nc.Profile}'");
            }
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

        // ── inline node insertion: click a segment body to split its corridor at the clicked point into TWO corridors that
        // share a NEW node (geometry unchanged). The clicked corridor keeps the A-side half; a fresh corridor takes the
        // B-side half. Every lane A→B becomes A→new (kept) + new→B (cloned into the new corridor); flow re-links through the
        // new node. Curved corridors de-Casteljau-split so both halves trace the original cubic exactly. ADD-only
        // (no removals) → no index compaction needed.
        public static bool SplitCorridorAt(Vector2 xz, Func<Vector2, float> groundAt)
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
            if (!NearestOnPath(c, xz, out _, out float t)) return false;
            if (t < 0.04f || t > 0.96f) return false;   // clicked at/near an existing end → nothing to split
            if (SplitCorridorAtCore(c, t) < 0) return false;
            RegenerateDefaultFlows(groundAt);
            Rebuild(groundAt);
            return true;
        }

        // Topology-only split of corridor c at param t → the inserted node index (NO flow-regen / rebuild, so callers can
        // batch). The clicked corridor keeps the A-side half; a fresh corridor takes the B-side half. Curved corridors
        // de-Casteljau-split so both halves trace the original cubic. Returns -1 if degenerate.
        static int SplitCorridorAtCore(Corridor c, float t)
        {
            if (c.Lanes.Count == 0) return -1;
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            Vector2 rawA = Net.Nodes[l0.A], rawB = Net.Nodes[l0.B];

            // Capture the uniform-shift translate direction up-front (before we mutate c.ControlA) so BOTH halves translate
            // along the SAME world normal. Without this, the second half would re-derive its normal from the seam tangent —
            // a different direction on a curve — and the two bodies would splay apart at the seam.
            bool shifted = Mathf.Abs(c.CenterShift) > 1e-4f || Mathf.Abs(c.CenterShiftB) > 1e-4f;
            bool uniform = Mathf.Abs(c.CenterShift - c.CenterShiftB) <= 1e-3f;
            Vector2 shiftDir = Vector2.zero;
            if (shifted && uniform)
            {
                shiftDir = c.ShiftDir;
                if (shiftDir.sqrMagnitude < 1e-8f)
                {
                    Vector2 tA = c.Curved && (c.ControlA - rawA).sqrMagnitude > 1e-8f ? (c.ControlA - rawA).normalized : (rawB - rawA).normalized;
                    shiftDir = new Vector2(tA.y, -tA.x);
                }
            }

            Vector2 splitPos, ac1, ac2, bc1, bc2;   // split point + first-half (ac*) and second-half (bc*) controls
            if (c.Curved)
                SplitCubic(rawA, c.ControlA, c.ControlB, rawB, t, out splitPos, out ac1, out ac2, out bc1, out bc2);
            else { splitPos = Vector2.Lerp(rawA, rawB, t); ac1 = ac2 = bc1 = bc2 = Vector2.zero; }

            int nn = Net.AddNode(splitPos);
            float ya = Net.GetNodeY(l0.A), yb = Net.GetNodeY(l0.B);   // interpolate the captured grade so the seam doesn't crater on build
            if (!float.IsNaN(ya) && !float.IsNaN(yb)) Net.SetNodeY(nn, Mathf.Lerp(ya, yb, t));

            // second-half corridor (B side) — clone the source's profile / bands / built-state
            Corridor c2 = Net.AddCorridor();
            c2.Curved = c.Curved; c2.Profile = c.Profile; c2.AlignLanes = c.AlignLanes;
            c2.MedianWidth = c.MedianWidth; c2.ShoulderBA = c.ShoulderBA; c2.ShoulderAB = c.ShoulderAB;
            c2.Planned = c.Planned; c2.Excavated = c.Excavated; c2.Built = c.Built; c2.BedDepth = c.BedDepth;
            float shiftAtT = Mathf.Lerp(c.CenterShift, c.CenterShiftB, t);   // body shift at the seam (uniform shift stays uniform)
            c2.CenterShift = shiftAtT; c2.CenterShiftB = c.CenterShiftB;
            c2.ShiftDir = shiftDir;                                          // both halves share the captured normal (zero if unshifted/two-ended)
            if (c.Curved) { c2.ControlA = bc1; c2.ControlB = bc2; }

            // split every lane: original A→B becomes A→nn (kept in c); a clone nn→(old B) joins c2
            var orig = new List<int>(c.Lanes);
            foreach (int li in orig)
            {
                LaneEdge e = Net.Edges[li];
                int oldB = e.B;
                e.B = nn;
                int li2 = Net.AddLane(new LaneEdge { A = nn, B = oldB, CorridorId = c2.Id, Kind = e.Kind, Direction = e.Direction, Width = e.Width, Offset = e.Offset });
                c2.Lanes.Add(li2);
            }
            // first-half corridor stays c: update its B-end controls + seam shift; tapers are rebuild-scoped lane indices → re-detect
            if (c.Curved) { c.ControlA = ac1; c.ControlB = ac2; }
            c.CenterShiftB = shiftAtT;
            c.ShiftDir = shiftDir;
            c.Tapers?.Clear();

            Net.SortCorridorLanes(c); Net.SortCorridorLanes(c2);
            return nn;
        }

        // Resolve a draw endpoint to the node it should CONNECT to so a fresh road can T/cross into an existing one without
        // stealing lanes (a normal draw otherwise always makes its own node — see Click). Order: (1) reuse a nearby existing
        // node; (2) land on a corridor BODY → split it there + share the inserted node (auto-junction); (3) a fresh node.
        public static float JoinNodeSnap = 7f;       // radius for reusing an existing node as a draw endpoint
        static int ResolveJoinNode(Vector2 xz, Func<Vector2, float> groundAt)
        {
            int near = NearestCluster(xz, JoinNodeSnap);
            if (near >= 0) return near;
            int best = -1; float bestD = float.PositiveInfinity;
            for (int ci = 0; ci < Net.Corridors.Count; ci++)
            {
                Corridor c = Net.Corridors[ci];
                if (c.Lanes.Count == 0) continue;
                float halfW = LaneEdgeCorridorBuilder.BuildCrossSection(c, Net).Width * 0.5f + 0.5f;   // tight: only join when actually ON the road
                float d = CorridorDistSq(c, xz);
                if (d < halfW * halfW && d < bestD) { bestD = d; best = ci; }
            }
            if (best < 0) return Net.AddNode(xz);
            Corridor cc = Net.Corridors[best];
            if (!NearestOnPath(cc, xz, out _, out float t)) return Net.AddNode(xz);
            if (t <= 0.04f) return Net.Edges[cc.Lanes[0]].A;   // near a real end → share it
            if (t >= 0.96f) return Net.Edges[cc.Lanes[0]].B;
            int split = SplitCorridorAtCore(cc, t);
            return split >= 0 ? split : Net.AddNode(xz);
        }

        // De Casteljau split of cubic (p0,p1,p2,p3) at t → the split point plus first-half controls (q1,q2) and
        // second-half controls (r1,r2): first half = (p0,q1,q2,split), second half = (split,r1,r2,p3).
        static void SplitCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t,
                               out Vector2 split, out Vector2 q1, out Vector2 q2, out Vector2 r1, out Vector2 r2)
        {
            Vector2 a = Vector2.Lerp(p0, p1, t);
            Vector2 b = Vector2.Lerp(p1, p2, t);
            Vector2 d = Vector2.Lerp(p2, p3, t);
            Vector2 e = Vector2.Lerp(a, b, t);
            Vector2 f = Vector2.Lerp(b, d, t);
            q1 = a; q2 = e; r1 = f; r2 = d;
            split = Vector2.Lerp(e, f, t);
        }

        // ── DEBUG: click a segment to dump its full structure (profile, centreline shift, lane indices/offsets/dirs, the
        // built cross-section, lane config). Lets two "identical-looking" segments be compared to find why they differ.
        public static bool InspectCorridorAt(Vector2 xz)
        {
            int best = -1; float bestD = float.PositiveInfinity;
            for (int ci = 0; ci < Net.Corridors.Count; ci++)
            {
                if (Net.Corridors[ci].Lanes.Count == 0) continue;
                float halfW = LaneEdgeCorridorBuilder.BuildCrossSection(Net.Corridors[ci], Net).Width * 0.5f + 1.5f;
                float d = CorridorDistSq(Net.Corridors[ci], xz);
                if (d < halfW * halfW && d < bestD) { bestD = d; best = ci; }
            }
            if (best < 0) { Debug.Log("[INSPECT] no corridor under click"); return false; }
            Corridor c = Net.Corridors[best];
            LaneEdge l0 = Net.Edges[c.Lanes[0]];
            RoadCrossSection xs = LaneEdgeCorridorBuilder.BuildCrossSection(c, Net);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[INSPECT] c{c.Id} profile='{c.Profile}' Curved={c.Curved} AlignLanes={c.AlignLanes} CenterShift={c.CenterShift:F2}/{c.CenterShiftB:F2} Median={c.MedianWidth:F1} ShBA/AB={c.ShoulderBA:F1}/{c.ShoulderAB:F1} Built={c.Built}\n");
            sb.Append($"  lane[0]={c.Lanes[0]} nodesA={l0.A}{Net.Nodes[l0.A]} B={l0.B}{Net.Nodes[l0.B]} ctrlA={c.ControlA} ctrlB={c.ControlB}\n");
            sb.Append($"  built cross-section: width={xs.Width:F2} center(U)={xs.Center():F2} CenterUSet={xs.CenterUSet} SplitU={xs.SplitU:F2} segs={xs.Segs.Count} pts={xs.Pts.Count}\n");
            int ab = 0, ba = 0;
            sb.Append($"  lanes [{c.Lanes.Count}] (in c.Lanes order):\n");
            foreach (int li in c.Lanes)
            {
                LaneEdge e = Net.Edges[li];
                if (e.Kind == LaneKind.Traffic) { if (e.Direction == 2) ab++; else if (e.Direction == 0) ba++; }
                bool tapered = LaneIsTapered(c, li);
                sb.Append($"    idx={li} serial={e.Serial} {e.Kind} dir={(e.Direction == 2 ? "AB" : e.Direction == 0 ? "BA" : "?")} off={e.Offset:F2} w={e.Width:F2} A={e.A} B={e.B}{(tapered ? " [TAPERED]" : "")}\n");
            }
            ProfileLanes(c.Profile, out float pShBA, out float pShAB);
            var canon = new List<string>(); foreach (var lb in _laneBuf) canon.Add($"{lb.off:F2}/{(lb.dir == 2 ? "AB" : "BA")}");
            sb.Append($"  config AB={ab} BA={ba} → FindByConfig='{RoadProfileLibrary.FindByConfig(ab, ba)}'\n");
            sb.Append($"  profile '{c.Profile}' canonical lanes=[{string.Join(", ", canon)}] shBA/AB={pShBA:F1}/{pShAB:F1}\n");
            if (c.Tapers != null) foreach (var t in c.Tapers) sb.Append($"  TAPER atA={t.AtA} off={t.Offset:F2} w={t.Width:F2} len={t.Length:F1} edge={t.LaneEdge}\n");
            // Why might an outer solid edge be missing? Taper suppression vs gore suppression (the gore-suppressed edge is
            // meant to be REDRAWN clipped in RenderExitGores — if that gore has no nose/gore the edge just disappears).
            GoreSuppress(c, out int em, out int sm);
            sb.Append($"  EDGE SUPPRESS: outerEdge L(BA)={OuterEdgeSuppressed(c, -1f)} R(AB)={OuterEdgeSuppressed(c, 1f)} | goreEdgeMask={em} (bit1=BA,bit2=AB) goreShoulderMask={sm}\n");
            int cidx = -1; for (int k = 0; k < Net.Corridors.Count; k++) if (ReferenceEquals(Net.Corridors[k], c)) { cidx = k; break; }
            foreach (var g in _gores)
                if (g.Ramp == cidx || g.Through == cidx)
                    sb.Append($"  GORE: thru c{g.Through} ramp c{g.Ramp} thruSide={g.ThroughSide} rampSide={g.RampSide} nose={g.HasNose} gore={g.HasGore} (this c{cidx} is {(g.Ramp == cidx ? "RAMP" : "THROUGH")})\n");
            Debug.Log(sb.ToString());
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

            // The through-lane the ramp replaces: for an EXIT (pulled lane incoming to the fork → diverges) it's the DOWNSTREAM
            // continuation; for an ENTRANCE (pulled lane outgoing → the ramp occupies the outer lane) it's the UPSTREAM lane,
            // which tapers out before the merge. FindContinuationLane returns the opposite-end lane either way, so one path
            // handles both — the entrance just drops/tapers its upstream side instead of the downstream.
            var cand = new List<int>();
            foreach (int sl in _extLanes)
            {
                if (sl < 0 || sl >= Net.Edges.Count) continue;
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

            // Propagate the drop DOWNSTREAM along the whole road, not just the adjacent segment. A real exit removes the lane
            // for the entire length past the fork; if we only drop the first segment, the lane reappears on the next colinear
            // segment (e.g. across a curve) → a one-segment "notch" and a spurious taper at the far join (#27). Walk each
            // deleted lane's continuation chain (its downstream end → next corridor's in-line lane) and drop the whole run.
            {
                var frontier = new List<(int lane, int entered)>();
                foreach (int d in del) frontier.Add((d, _extNode));
                int guard = 0;
                while (frontier.Count > 0 && guard++ < 128)
                {
                    var next = new List<(int, int)>();
                    foreach ((int dl, int entered) in frontier)
                    {
                        if (dl < 0 || dl >= Net.Edges.Count) continue;
                        LaneEdge de = Net.Edges[dl];
                        int downstream = entered == de.A ? de.B : de.A;       // far end of this lane = where it continues onward
                        int cont = FindContinuationLane(downstream, dl, de.CorridorId, rampCorr);   // exclude our own corridor
                        if (cont >= 0 && del.Add(cont)) next.Add((cont, downstream));
                    }
                    frontier = next;
                }
            }

            // Guard: never empty a downstream corridor (protects full/colinear continuations). Then flag the affected
            // corridors AlignLanes so the surviving lanes keep their offsets and the split stays aligned.
            var survive = new Dictionary<int, int>();
            foreach (int dl in del) { int ci = Net.Edges[dl].CorridorId; if (ci < 0) continue; if (!survive.ContainsKey(ci)) survive[ci] = Net.Corridors[ci].Lanes.Count; survive[ci]--; }
            foreach (var kv in survive) if (kv.Value <= 0) return;
            foreach (var kv in survive) Net.Corridors[kv.Key].AlignLanes = true;
            // Capture the remainder corridor OBJECTS before DeleteLanes — it re-indexes edges/nodes/corridors but REUSES the
            // Corridor instances and remaps their Lanes, so references stay valid while indices don't.
            var remainder = new List<Corridor>();
            foreach (var kv in survive) if (kv.Key >= 0 && kv.Key < Net.Corridors.Count) remainder.Add(Net.Corridors[kv.Key]);

            // Tapers are NOT recorded here — they're derived generally at Rebuild (DetectLaneDropTapers) from lane-count
            // mismatches at shared nodes, wherever they occur, not pinned to this pull.
            DeleteLanes(del, groundAt);

            // Remainder re-profiling: the reduced downstream road is now a real "N−k". Adopt the profile matching its NEW lane
            // config (one proper shoulder set + a real identity) so it's a "2" not a clipped "4". Keep AlignLanes — its survivors
            // keep their offsets and fan at the fork. The pulled-group no-go check already guaranteed this profile exists.
            foreach (Corridor rc in remainder)
            {
                if (rc == null) continue;
                int rab = 0, rba = 0;
                foreach (int li in rc.Lanes)
                { if (li < 0 || li >= Net.Edges.Count) continue; LaneEdge e = Net.Edges[li]; if (e.Kind != LaneKind.Traffic) continue; if (e.Direction == 2) rab++; else if (e.Direction == 0) rba++; }
                string pr = RoadProfileLibrary.FindByConfig(rab, rba, rc.Profile);
                if (string.IsNullOrEmpty(pr)) continue;
                rc.Profile = pr;
                var rp = RoadProfileLibrary.Resolve(pr);
                if (rp != null) { if (rp.ShoulderBA != null) rc.ShoulderBA = rp.ShoulderBA.Width; if (rp.ShoulderAB != null) rc.ShoulderAB = rp.ShoulderAB.Width; }
            }
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
            // Direction of travel through the node from the CURVE TANGENT (oriented by the node chord), not the chord itself —
            // near a curve the chord diverges from the lane's real heading, which made the align test below reject the genuine
            // downstream continuation (so an exit near a curve dropped no lane).
            Vector2 pChord = pIncoming ? (Net.Nodes[node] - Net.Nodes[po]) : (Net.Nodes[po] - Net.Nodes[node]);
            Vector2 pMotion = pChord;
            if (LaneGuideFrame(node, pickedLane, out _, out Vector2 pTan) && pTan.sqrMagnitude > 1e-6f) pMotion = Vector2.Dot(pTan, pChord) >= 0f ? pTan : -pTan;
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
                Vector2 eChord = e.Incoming ? (Net.Nodes[node] - Net.Nodes[oo]) : (Net.Nodes[oo] - Net.Nodes[node]);
                Vector2 eMotion = eChord;
                if (LaneGuideFrame(node, e.Edge, out _, out Vector2 eTan) && eTan.sqrMagnitude > 1e-6f) eMotion = Vector2.Dot(eTan, eChord) >= 0f ? eTan : -eTan;
                if (eMotion.sqrMagnitude < 1e-6f) continue; eMotion.Normalize();
                float align = Vector2.Dot(pMotion, eMotion);
                if (align <= 0.1f) continue;
                float lateral = Mathf.Abs(Vector2.Dot(e.Pos - pPos, nrm));
                // Only the lane DIRECTLY in line with the pulled lane is its continuation. Tolerance is just under one lane
                // width so a re-pull (nearest survivor a full lane away) still deletes nothing extra — but wide enough to
                // absorb the puck shift where a straight segment meets a curve (different tangents displace a lane's puck by
                // ~offset·kink, worst for the outer lane, which previously fell outside 0.6·width and never dropped).
                if (lateral >= p.Width * 0.9f) continue;
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
