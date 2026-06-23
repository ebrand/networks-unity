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
        public struct LaneEndpoint { public Vector2 Pos; public float Y; public int Edge; public int Node; public bool Incoming; }
        public static readonly List<LaneEndpoint> Endpoints = new List<LaneEndpoint>();   // rebuilt each Rebuild; for picking + flow render

        public static void Rebuild(Func<Vector2, float> groundAt)
        {
            ComputeEndpoints(groundAt);
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
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= Net.Edges.Count) continue;
                LaneEdge e = Net.Edges[li];
                if (e.Kind == LaneKind.Sidewalk) continue;
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
            if (_guideBuf.Count == 0) return;

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

        static Material _inMat, _outMat;
        static Material InMat() => _inMat != null ? _inMat : (_inMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.30f, 0.55f, 1f, 1f), "LaneEndIn"));
        static Material OutMat() => _outMat != null ? _outMat : (_outMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(0.30f, 1f, 0.5f, 1f), "LaneEndOut"));

        // Per-node lane endpoints (#149): for each lane-edge, a puck at each end, coloured by whether traffic flows INTO
        // that node (incoming, blue) or OUT of it (outgoing, green). Laid across the road width, inset into the road.
        // Compute the per-node lane endpoint records (no rendering) — used for picking, flow render, and default flows.
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
            Vector2 pos = N + into * inset + fr * e.Offset;
            bool incoming = (e.Direction == 2 && atNode == e.B) || (e.Direction == 0 && atNode == e.A);
            float y = (groundAt != null ? groundAt(pos) : 0f) + 0.6f;
            Endpoints.Add(new LaneEndpoint { Pos = pos, Y = y, Edge = edgeIndex, Node = atNode, Incoming = incoming });
        }

        static Material _extSelMat;
        static Material ExtSelMat() => _extSelMat != null ? _extSelMat : (_extSelMat = NetworkDesigner.PipelineMaterials.CreateUnlitTransparent(new Color(1f, 0.4f, 1f, 1f), "LaneExtSel"));

        static void RenderEndpointSpheres(Transform parent)
        {
            foreach (LaneEndpoint ep in Endpoints)
            {
                bool sel = _extNode == ep.Node && _extLanes.Contains(ep.Edge);   // selected for extension → highlight
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
                go.GetComponent<MeshRenderer>().sharedMaterial = sel ? ExtSelMat() : (ep.Incoming ? InMat() : OutMat());
                go.transform.SetParent(parent, false);
                go.transform.position = new Vector3(ep.Pos.x, ep.Y, ep.Pos.y);
                go.transform.localScale = Vector3.one * (sel ? 2.6f : 1.4f);
                go.name = $"laneEnd_{(ep.Incoming ? "in" : "out")}_e{ep.Edge}_n{ep.Node}";
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

        static int BestOutgoingMatch(int incIdx)
        {
            LaneEndpoint inc = Endpoints[incIdx];
            LaneEdge ie = Net.Edges[inc.Edge];
            int io = inc.Node == ie.A ? ie.B : ie.A;
            Vector2 inDir = Net.Nodes[inc.Node] - Net.Nodes[io]; if (inDir.sqrMagnitude < 1e-6f) return -1; inDir.Normalize();
            int best = -1; float bestScore = -999f;
            for (int j = 0; j < Endpoints.Count; j++)
            {
                LaneEndpoint outg = Endpoints[j];
                if (outg.Incoming || outg.Node != inc.Node) continue;
                LaneEdge oe = Net.Edges[outg.Edge];
                int oo = outg.Node == oe.A ? oe.B : oe.A;
                Vector2 outDir = Net.Nodes[oo] - Net.Nodes[outg.Node]; if (outDir.sqrMagnitude < 1e-6f) continue; outDir.Normalize();
                float align = Vector2.Dot(inDir, outDir);
                if (align <= 0.1f) continue;   // never default-map a U-turn / sharp reversal
                float score = align * 10f - Mathf.Abs(ie.Offset - oe.Offset);   // most aligned, then closest lane offset
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
                float oNew = Vector2.Dot(frS * s.Offset, frNew);
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
            if (!_cornerPending && BestColinearAxis(_drawStart, cursor, out Vector2 gax))
            {
                if (!_pvGuideBuilt || _pvGuideStart != start || Vector2.Dot(_pvGuideAxis, gax) < 0.999f)
                {
                    const float L = 300f;
                    FillPvLine(_pvGuide, start - gax * L, start + gax * L, false, default, default, 0f, groundAt, PvGuide());
                    _pvGuideBuilt = true; _pvGuideStart = start; _pvGuideAxis = gax;
                }
                else _pvGuide.gameObject.SetActive(true);   // reuse cached geometry
            }

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
        public static void UpdateExtendPreview(Vector2 cursor, bool curveModifier, Func<Vector2, float> groundAt)
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
            foreach (int sl in _extLanes)
            {
                if (sl < 0 || sl >= Net.Edges.Count) continue;
                LaneEdge s = Net.Edges[sl];
                Corridor sc = (s.CorridorId >= 0 && s.CorridorId < Net.Corridors.Count) ? Net.Corridors[s.CorridorId] : null;
                Vector2 frS;
                if (sc != null) { Vector2 ts = LaneEdgeCorridorBuilder.PathTangent(Net, sc, _extNode == s.A ? 0f : 1f); frS = new Vector2(ts.y, -ts.x); }
                else { Vector2 cd = Net.Nodes[s.B] - Net.Nodes[s.A]; frS = cd.sqrMagnitude < 1e-6f ? Vector2.right : new Vector2(cd.normalized.y, -cd.normalized.x); }
                float oNew = Vector2.Dot(frS * s.Offset, frNew);
                lo = Mathf.Min(lo, oNew - s.Width * 0.5f); hi = Mathf.Max(hi, oNew + s.Width * 0.5f);
            }
            if (lo > hi) return;
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
                float d = (Endpoints[i].Pos - worldXz).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = i; }
            }
            if (best < 0) return false;
            LaneEndpoint ep = Endpoints[best]; node = ep.Node;
            LaneEdge picked = Net.Edges[ep.Edge];
            if (ProfileLaneCount(profileId) <= 1) { _grpBuf.Add(ep.Edge); single = true; return true; }

            _grpCand.Clear();
            for (int i = 0; i < Net.Edges.Count; i++)
            {
                LaneEdge e = Net.Edges[i];
                if ((e.A != node && e.B != node) || e.CorridorId != picked.CorridorId) continue;
                if (e.Direction != picked.Direction || e.Kind == LaneKind.Sidewalk) continue;
                _grpCand.Add(i);
            }
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
                if (Endpoints[i].Node == node && Endpoints[i].Edge == edge) { pos = Endpoints[i].Pos; y = Endpoints[i].Y; return true; }
            pos = Vector2.zero; y = 0f; return false;
        }

        // Navigable (drivable) lane count of a profile — drives how many lanes an extension click grabs.
        static int ProfileLaneCount(string profileId)
        {
            var cfg = RoadProfileLibrary.ResolveConfig(profileId);
            if (cfg == null || cfg.Corridor == null) return 1;
            var bands = new List<RoadCrossSectionBuilder.StackBand>();
            RoadCrossSectionBuilder.FromStack(cfg.Corridor, bands);
            int n = 0;
            foreach (var bd in bands)
                if (bd.Type == Model.CorridorType.Traffic || bd.Type == Model.CorridorType.Turn || bd.Type == Model.CorridorType.Bike) n++;
            return Mathf.Max(1, n);
        }

        public static void CancelExtend() { _extNode = -1; _extLanes.Clear(); _extCornerPending = false; }

        // Draw the extension end (straight, or shift-curve through a bend). Builds the aligned subset corridor + lets the
        // default-flow regen wire continuations (selected) and merges (dropped). Mirrors Click's straight/curve flow.
        public static bool ExtendClick(Vector2 xz, Func<Vector2, float> groundAt, bool curveModifier, bool limitRadius, float minRadius)
        {
            if (!Extending) return false;
            Vector2 start = Net.Nodes[_extNode];

            if (_extCornerPending)
            {
                Vector2 end = xz;
                if ((end - start).sqrMagnitude < 1f) return false;
                CurveControls(start, end, _extCorner, out Vector2 c1, out Vector2 c2);
                if (limitRadius && MinCurveRadius(start, c1, c2, end) < minRadius)
                { Debug.LogWarning("[LaneEdgeWorld] extension curve too tight — pick a wider end"); return false; }
                int bm = Net.AddNode(end);
                BuildExtensionCorridor(_extNode, bm, true, c1, c2);
                CancelExtend(); RegenerateDefaultFlows(groundAt); Rebuild(groundAt); return true;
            }
            if (curveModifier) { _extCorner = xz; _extCornerPending = true; return false; }

            if ((xz - start).sqrMagnitude < 1f) return false;
            int b = Net.AddNode(xz);
            BuildExtensionCorridor(_extNode, b, false, default, default);
            CancelExtend(); RegenerateDefaultFlows(groundAt); Rebuild(groundAt); return true;
        }

        // Build a corridor (nodeN→nodeM) whose lanes copy the selected source lanes, positioned (via Offset projection +
        // AlignLanes/CenterU) to sit exactly on the source lanes at nodeN so the fork is geometrically continuous.
        static void BuildExtensionCorridor(int nodeN, int nodeM, bool curved, Vector2 c1, Vector2 c2)
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
                float oNew = Vector2.Dot(frS * s.Offset, frNew);   // keep the lane's world lateral, in the new corridor's frame
                bool incomingAtN = (s.Direction == 2 && nodeN == s.B) || (s.Direction == 0 && nodeN == s.A);
                int dirNew = incomingAtN ? 2 : 0;                  // arriving lane → continue outward (A'=N→B'); return lane → inbound
                int li = Net.AddLane(new LaneEdge { A = nodeN, B = nodeM, CorridorId = nc.Id, Kind = s.Kind, Direction = dirNew, Width = s.Width, Offset = oNew });
                nc.Lanes.Add(li);
                if (string.IsNullOrEmpty(nc.Profile) && sc != null) { nc.Profile = sc.Profile; nc.ShoulderBA = sc.ShoulderBA; nc.ShoulderAB = sc.ShoulderAB; }
            }
            Net.SortCorridorLanes(nc);
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
            float len = LaneEdgeCorridorBuilder.PathLength(Net, c);
            int n = Mathf.Clamp(Mathf.CeilToInt(len / 3f), 2, 96);
            float best = float.PositiveInfinity;
            for (int i = 0; i <= n; i++)
            {
                Vector2 p = LaneEdgeCorridorBuilder.PathPoint(Net, c, (float)i / n);
                best = Mathf.Min(best, (p - xz).sqrMagnitude);
            }
            return best;
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
