using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace NetworkDesigner.Roads
{
    // Phase-0 spike: build a RoadCrossSection directly from a Corridor's ordered lane-edges + non-navigable bands, then
    // sweep it with the EXISTING RoadSweep. Validates D1 (lane-edges grouped into a corridor), D2 (median/shoulder as
    // metadata, navigable kinds as lanes), and D3 (per-corridor sweep render). Reuses the whole render pipeline; the
    // only new bit is "lane-edges → cross-section".
    public static class LaneEdgeCorridorBuilder
    {
        // The corridor's cross-section, laid left→right from its lanes (ordered by lateral offset) with the median
        // inserted at the travel-direction flip and shoulders on the outer edges. Absolute Offset values only set the
        // ORDER; the section widths come from each lane/band's width.
        public static RoadCrossSection BuildCrossSection(Corridor c, LaneEdgeNetwork net)
        {
            var lanes = new List<LaneEdge>();
            foreach (int li in c.Lanes) if (li >= 0 && li < net.Edges.Count) lanes.Add(net.Edges[li]);
            lanes.Sort((a, b) => a.Offset.CompareTo(b.Offset));   // left → right across the section

            var xs = new RoadCrossSection();
            if (c.ShoulderBA > 0f) xs.ShoulderBand(c.ShoulderBA);
            int prevDir = lanes.Count > 0 ? lanes[0].Direction : -1;
            bool medianPlaced = false;
            foreach (LaneEdge e in lanes)
            {
                if (!medianPlaced && c.MedianWidth > 0f && e.Direction != prevDir)   // travel direction flips → median here
                {
                    float u0 = xs.Width; xs.Median(c.MedianWidth); xs.SplitU = (u0 + xs.Width) * 0.5f;
                    medianPlaced = true;
                }
                AddBand(xs, e.Kind, e.Width);
                prevDir = e.Direction;
            }
            if (c.ShoulderAB > 0f) xs.ShoulderBand(c.ShoulderAB);
            return xs;
        }

        static void AddBand(RoadCrossSection xs, LaneKind k, float w)
        {
            switch (k)
            {
                case LaneKind.Sidewalk: xs.CurbUp().Sidewalk(w).CurbDown(); break;   // raised walk
                default: xs.Lane(w); break;                                          // Traffic / Turn / Bike → asphalt lane
            }
        }

        // Render one corridor by sweeping its cross-section along the SHARED centreline nodes of its lanes (#144: the
        // lane-edges share the corridor's two nodes; the lateral position is the Offset field, not the node position).
        public static GameObject RenderCorridor(LaneEdgeNetwork net, Corridor c, Transform parent, Func<Vector2, float> groundAt)
        {
            if (c.Lanes.Count == 0) return null;
            LaneEdge l0 = net.Edges[c.Lanes[0]];
            Vector2 a = net.Nodes[l0.A], b = net.Nodes[l0.B];
            RoadCrossSection xs = BuildCrossSection(c, net);
            // Once excavated, sit the body on the CAPTURED design grade (NodeY) as a slab of the bed depth — so it fills
            // the cut bed at the original level instead of draping into the (now-lowered) terrain. Un-excavated → drape.
            float yA = net.GetNodeY(l0.A), yB = net.GetNodeY(l0.B);
            bool haveGrade = !float.IsNaN(yA) && !float.IsNaN(yB);
            if (haveGrade && c.BedDepth > 0f) xs.Thickness = c.BedDepth;
            float hA = haveGrade ? yA : (groundAt != null ? groundAt(a) : 0f);
            float hB = haveGrade ? yB : (groundAt != null ? groundAt(b) : 0f);
            float follow = haveGrade ? 0f : (groundAt != null ? 1f : 0f);   // design grade once excavated; else terrain-follow
            return RoadSweep.Build(xs, a, b, false, default, default, parent, $"LaneEdgeCorridor_{c.Id}",
                                   hA, hB, groundAt, follow);
        }

#if UNITY_EDITOR
        [MenuItem("Tools/Lane-Edge Spike/Build Straight 4-Lane (one-way)")]
        static void SpikeOneWay() => BuildSpike(false);

        [MenuItem("Tools/Lane-Edge Spike/Build Straight 4-Lane (two-way + median)")]
        static void SpikeTwoWay() => BuildSpike(true);

        static void BuildSpike(bool twoWay)
        {
            Camera cam = Camera.main;
            // Place it where the camera is LOOKING on the terrain (raycast along forward) so it's in view.
            Vector3 origin = Vector3.zero;
            if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit look, 60000f))
                origin = look.point;
            else if (cam != null) origin = cam.transform.position + cam.transform.forward * 80f;
            // Replace any previous spike of this kind so repeated builds don't stack or interfere with the drape raycast.
            string rootName = $"LaneEdgeSpike_{(twoWay ? "2way" : "1way")}";
            var prev = GameObject.Find(rootName); if (prev != null) UnityEngine.Object.DestroyImmediate(prev);
            // Drape EACH vertex onto the terrain surface + 0.25 m (raycast down from well above) so the road follows the
            // ground instead of sitting flat and poking below it at the ends.
            Func<Vector2, float> groundAt = xz =>
                Physics.Raycast(new Vector3(xz.x, origin.y + 2000f, xz.y), Vector3.down, out RaycastHit gh, 10000f)
                    ? gh.point.y + 0.25f : origin.y + 0.25f;

            var net = new LaneEdgeNetwork();
            int na = net.AddNode(new Vector2(origin.x - 40f, origin.z));
            int nb = net.AddNode(new Vector2(origin.x + 40f, origin.z));
            Corridor c = net.AddCorridor();
            c.ShoulderBA = 2f; c.ShoulderAB = 2f;
            if (twoWay)
            {
                c.MedianWidth = 2f;
                AddLane(net, c, na, nb, 0, -5.25f); AddLane(net, c, na, nb, 0, -1.75f);   // BA side (←)
                AddLane(net, c, na, nb, 2, +1.75f); AddLane(net, c, na, nb, 2, +5.25f);   // AB side (→)
            }
            else
            {
                AddLane(net, c, na, nb, 2, -5.25f); AddLane(net, c, na, nb, 2, -1.75f);
                AddLane(net, c, na, nb, 2, +1.75f); AddLane(net, c, na, nb, 2, +5.25f);
            }
            net.SortCorridorLanes(c);

            var root = new GameObject(rootName);
            RenderCorridor(net, c, root.transform, groundAt);
            RoadCrossSection xs = BuildCrossSection(c, net);
            Selection.activeGameObject = root;   // so you can find/Frame (F) it in the hierarchy
            Debug.Log($"[LaneEdgeSpike] built {(twoWay ? "2-way" : "1-way")} 4-lane centred at {origin:F0} (draped to terrain +0.25m); " +
                      $"lanes={c.Lanes.Count} sectionWidth={xs.Width:F1}m — selected '{root.name}' in the hierarchy.");
        }

        static void AddLane(LaneEdgeNetwork net, Corridor c, int a, int b, int dir, float offset)
        {
            int li = net.AddLane(new LaneEdge { A = a, B = b, CorridorId = c.Id, Kind = LaneKind.Traffic, Direction = dir, Width = 3.5f, Offset = offset });
            c.Lanes.Add(li);
        }
#endif
    }
}
