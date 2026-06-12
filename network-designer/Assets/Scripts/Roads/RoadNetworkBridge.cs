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

                var road = new NetworkRoad
                {
                    Id = "r" + e,
                    EndA = "v" + le.A,
                    EndB = "v" + le.B,
                    Profile = prof,
                    SpeedLimit = le.SpeedLimit > 0f ? le.SpeedLimit : (float?)null,
                };
                if (le.HasCurve) road.Curve = new RoadCurve { ControlA = le.ControlA, ControlB = le.ControlB };
                net.Roads.Add(road);
            }
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
