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
        public static void Rebuild(Func<Vector2, float> groundAt)
        {
            GameObject root = Root();
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(root.transform.GetChild(i).gameObject);
            foreach (Corridor c in Net.Corridors) LaneEdgeCorridorBuilder.RenderCorridor(Net, c, root.transform, groundAt);
            RenderNodes(root.transform, groundAt);
            RenderLaneEndpoints(root.transform, groundAt);
        }

        static Material _inMat, _outMat;
        static Material InMat() => _inMat != null ? _inMat : (_inMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(0.30f, 0.55f, 1f, 1f), 0f, "LaneEndIn"));
        static Material OutMat() => _outMat != null ? _outMat : (_outMat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(new Color(0.30f, 1f, 0.5f, 1f), 0f, "LaneEndOut"));

        // Per-node lane endpoints (#149): for each lane-edge, a puck at each end, coloured by whether traffic flows INTO
        // that node (incoming, blue) or OUT of it (outgoing, green). Laid across the road width, inset into the road.
        static void RenderLaneEndpoints(Transform parent, Func<Vector2, float> groundAt)
        {
            foreach (LaneEdge e in Net.Edges)
            {
                RenderEndpoint(parent, groundAt, e, e.A, e.B);
                RenderEndpoint(parent, groundAt, e, e.B, e.A);
            }
        }

        static void RenderEndpoint(Transform parent, Func<Vector2, float> groundAt, LaneEdge e, int atNode, int otherNode)
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
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
            go.GetComponent<MeshRenderer>().sharedMaterial = incoming ? InMat() : OutMat();
            go.transform.SetParent(parent, false);
            float y = groundAt != null ? groundAt(pos) : 0f;
            go.transform.position = new Vector3(pos.x, y + 0.6f, pos.y);
            go.transform.localScale = Vector3.one * 1.2f;
            go.name = $"laneEnd_{(incoming ? "in" : "out")}_e{e.Serial}_n{atNode}";
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
            Rebuild(groundAt);
            return true;
        }

        public static void CancelDraw() => _drawStart = -1;

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
#endif
    }
}
