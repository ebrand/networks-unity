// Converts an in-game road PLAN (a Terrain.LineGraph of nodes/edges, each edge carrying a road-profile id)
// into the project's Model.Network — the input the GeometryResolver brain consumes to resolve setbacks,
// intersection outlines and lane connectivity. This is the bridge that lets "Build Plan" reuse the original
// road geometry algorithms instead of a naive cross-section loft.
//
// Mapping: node i ↔ Vertex "v{i}"; edge e ↔ NetworkRoad "r{e}". A curved edge carries through as a RoadCurve
// (absolute world-XZ controls, exactly how NetworkRoad.Curve stores them). Profiles resolve via
// RoadProfileLibrary; an edge with no/unknown profile falls back to a plain symmetric road of its plan width.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Roads
{
    public static class RoadNetworkBridge
    {
        // Build a Network from the plan graph. `fallbackWidth` is the plan's custom RoadWidth, used when an
        // edge has no resolvable profile. Vertex/road ids are positional ("v{i}"/"r{e}") and stable per build.
        public static Network Build(Terrain.LineGraph graph, DriveSide driveSide, float fallbackWidth)
        {
            var net = new Network
            {
                DriveSide = driveSide,
                Vertices = new List<Vertex>(),
                Roads = new List<NetworkRoad>(),
            };
            if (graph == null) return net;

            for (int i = 0; i < graph.Nodes.Count; i++)
                net.Vertices.Add(new Vertex { Id = "v" + i, Position = graph.Nodes[i] });

            for (int e = 0; e < graph.Edges.Count; e++)
            {
                Terrain.LineEdge le = graph.Edges[e];
                if (le == null) continue;
                if (le.A < 0 || le.B < 0 || le.A >= graph.Nodes.Count || le.B >= graph.Nodes.Count || le.A == le.B) continue;

                RoadProfile prof = RoadProfileLibrary.Resolve(le.Profile);
                if (prof == null || prof.TotalWidth < 0.5f) prof = FallbackProfile(Mathf.Max(2f, fallbackWidth));
                // Authored corridor stack (rendering source of truth), null for legacy profiles.
                CorridorStack corridor = RoadProfileLibrary.ResolveConfig(le.Profile)?.Corridor;

                // Intersection precedence (primary runs through at the base setback; secondary yields — its setback
                // grows on acute approaches). Resolved on the graph so the overlay and the resolver agree exactly.
                RoadClassification cls = graph.EffectiveClass(e) == Terrain.RoadClass.Primary
                    ? RoadClassification.Primary : RoadClassification.Secondary;

                var road = new NetworkRoad
                {
                    Id = "r" + e,
                    EndA = "v" + le.A,
                    EndB = "v" + le.B,
                    Profile = prof,
                    Corridor = corridor,
                    Classification = cls,
                    SpeedLimit = le.SpeedLimit > 0f ? le.SpeedLimit : (float?)null,
                    SetbackA = le.SetbackA >= 0f ? le.SetbackA : (float?)null,   // <0 = auto (resolver computes it)
                    SetbackB = le.SetbackB >= 0f ? le.SetbackB : (float?)null,
                };
                if (le.HasCurve) road.Curve = new RoadCurve { ControlA = le.ControlA, ControlB = le.ControlB };
                net.Roads.Add(road);
            }
            return net;
        }

        // Sub-network of only the BUILT roads (+ the vertices they touch), for the car-agent sim — agents must
        // drive only on roads that actually have 3D geometry, never planned-but-unbuilt segments. Reuses the same
        // Vertex/NetworkRoad instances (read-only). Drops degenerate ~0-length roads, mirroring the filter
        // RoadPlanBuilder applies before sweeping, so the agent graph matches the built geometry exactly.
        public static Network FilterBuilt(Network full, HashSet<string> builtRoadIds)
        {
            var net = new Network { DriveSide = full != null ? full.DriveSide : DriveSide.Right };
            if (full == null || builtRoadIds == null) return net;

            var vpos = new Dictionary<string, Vector2>();
            foreach (Vertex v in full.Vertices) if (v != null) vpos[v.Id] = v.Position;

            var keepV = new HashSet<string>();
            foreach (NetworkRoad r in full.Roads)
            {
                if (r == null || !builtRoadIds.Contains(r.Id)) continue;
                if (vpos.TryGetValue(r.EndA, out Vector2 pa) && vpos.TryGetValue(r.EndB, out Vector2 pb)
                    && (pa - pb).sqrMagnitude < 0.25f) continue;   // degenerate stub
                net.Roads.Add(r); keepV.Add(r.EndA); keepV.Add(r.EndB);
            }
            foreach (Vertex v in full.Vertices) if (v != null && keepV.Contains(v.Id)) net.Vertices.Add(v);
            return net;
        }

        // A plain symmetric two-way road (one lane each direction, no shoulders) whose total width matches the
        // plan's fallback width — just enough lane structure for the resolver when an edge has no real profile.
        static RoadProfile FallbackProfile(float width)
        {
            float lane = Mathf.Max(2f, width * 0.5f);
            return new RoadProfile
            {
                Id = "_fallback",
                AB = new Side { Lanes = new List<Lane> { new Lane { Id = "a0", Width = lane } } },
                BA = new Side { Lanes = new List<Lane> { new Lane { Id = "b0", Width = lane } } },
                ShoulderAB = new Shoulder { Width = 0f },
                ShoulderBA = new Shoulder { Width = 0f },
            };
        }
    }
}
