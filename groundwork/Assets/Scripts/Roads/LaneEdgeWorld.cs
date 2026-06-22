using System;
using System.Collections.Generic;
using UnityEngine;

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
        public static bool Drawing => _drawStart >= 0;
        public static Vector2 DrawStartPos => _drawStart >= 0 ? Net.Nodes[_drawStart] : Vector2.zero;

        static GameObject Root() => _root != null ? _root : (_root = new GameObject("LaneEdgeWorld"));

        // Render every corridor, draped via groundAt. Rebuilds the whole render root (simple; optimise later).
        public struct LaneEndpoint { public Vector2 Pos; public float Y; public int Edge; public int Node; public bool Incoming; }
        public static readonly List<LaneEndpoint> Endpoints = new List<LaneEndpoint>();   // rebuilt each Rebuild; for picking + flow render

        public static void Rebuild(Func<Vector2, float> groundAt)
        {
            ComputeEndpoints(groundAt);
            GameObject root = Root();
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(root.transform.GetChild(i).gameObject);
            foreach (Corridor c in Net.Corridors) LaneEdgeCorridorBuilder.RenderCorridor(Net, c, root.transform, groundAt);
            RenderNodes(root.transform, groundAt);
            RenderEndpointSpheres(root.transform);
            RenderFlows(root.transform);
        }

        static Material _inMat, _outMat;
        static Material InMat() => _inMat != null ? _inMat : (_inMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(0.30f, 0.55f, 1f, 1f), 0f, "LaneEndIn"));
        static Material OutMat() => _outMat != null ? _outMat : (_outMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(0.30f, 1f, 0.5f, 1f), 0f, "LaneEndOut"));

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
            Vector2 toO = O - N; float len = toO.magnitude; if (len < 1e-3f) return; toO /= len;
            // Lateral must match the body: RoadSweep lays U along Cross(up, A→B) = (dir.y, -dir.x). Use the corridor's
            // FIXED A→B direction (not O−N, which flips between ends) so the offsets don't mirror.
            Vector2 cdir = Net.Nodes[e.B] - Net.Nodes[e.A]; if (cdir.sqrMagnitude < 1e-6f) return; cdir.Normalize();
            Vector2 fr = new Vector2(cdir.y, -cdir.x);
            float inset = Mathf.Min(4f, len * 0.25f);
            Vector2 pos = N + toO * inset + fr * e.Offset;
            bool incoming = (e.Direction == 2 && atNode == e.B) || (e.Direction == 0 && atNode == e.A);
            float y = (groundAt != null ? groundAt(pos) : 0f) + 0.6f;
            Endpoints.Add(new LaneEndpoint { Pos = pos, Y = y, Edge = edgeIndex, Node = atNode, Incoming = incoming });
        }

        static void RenderEndpointSpheres(Transform parent)
        {
            foreach (LaneEndpoint ep in Endpoints)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
                go.GetComponent<MeshRenderer>().sharedMaterial = ep.Incoming ? InMat() : OutMat();
                go.transform.SetParent(parent, false);
                go.transform.position = new Vector3(ep.Pos.x, ep.Y, ep.Pos.y);
                go.transform.localScale = Vector3.one * 1.2f;
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
        static Material FlowMat() => _flowMat != null ? _flowMat : (_flowMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(1f, 1f, 1f, 1f), 0f, "LaneFlow"));

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
            : (_markerMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(1f, 0.85f, 0.15f, 1f), 0f, "LaneEdgeClusterMarker"));

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

        public static bool Click(Vector2 xz, string profileId, Func<Vector2, float> groundAt)
        {
            int near = NearestCluster(xz, ClusterSnap);   // reuse an existing cluster → corridors meet at shared nodes
            if (_drawStart < 0)
            {
                _drawStart = near >= 0 ? near : Net.AddNode(xz);
                Debug.Log(near >= 0 ? $"[LaneEdgeWorld] start SNAPPED to cluster {near}" : "[LaneEdgeWorld] start = new cluster");
                return false;
            }
            int b = near >= 0 ? near : Net.AddNode(xz);
            Debug.Log(near >= 0 ? $"[LaneEdgeWorld] end SNAPPED to cluster {near}" : "[LaneEdgeWorld] end = new cluster");
            if (b == _drawStart || (Net.Nodes[b] - Net.Nodes[_drawStart]).sqrMagnitude < 1f) return false;   // degenerate
            AddCorridorFromProfile(_drawStart, b, profileId);
            _drawStart = -1;
            RegenerateDefaultFlows(groundAt);   // straight-through defaults at any newly-connected node
            Rebuild(groundAt);
            return true;
        }

        public static void CancelDraw() => _drawStart = -1;

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
        public static Corridor AddCorridorFromProfile(int ca, int cb, string profileId)
        {
            Corridor c = Net.AddCorridor();
            c.Profile = profileId;
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
