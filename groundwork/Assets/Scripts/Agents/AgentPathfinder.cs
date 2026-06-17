// Lane-aware path planner for agents.
//
// Plain vertex-graph Dijkstra would pick shortest vertex paths
// regardless of whether the necessary turn connections exist at
// each intersection — the agent's segment-builder would then reject
// the path. We avoid that by expanding the search state from
// (vertex) to (vertex, previous-road-in-edge). Outgoing edges at
// each state are filtered: a transition (V, prevRoad → outRoad) is
// only allowed if vg.Connectivity at V contains a connection from
// prevRoad to outRoad (matching directions). The initial state
// (startVertex, NONE) has no constraint — the agent can leave the
// start on any incident road in any valid direction.
//
// Result: every step on the returned path has at least one valid
// lane connection at the entering vertex, so BuildSegments will
// succeed without aborting.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;
using NetworkDesigner.Geometry;

namespace NetworkDesigner.Agents
{
    public static class AgentPathfinder
    {
        public static List<(NetworkRoad Road, Direction Dir)> FindPath(
            Network network, string startVertexId, string endVertexId)
        {
            if (network == null || startVertexId == endVertexId) return null;
            if (!HasVertex(network, startVertexId) || !HasVertex(network, endVertexId)) return null;

            Dictionary<string, List<Edge>> adj = BuildAdjacency(network);

            // Per-call resolver cache so each vertex is resolved at most
            // once during the search.
            var vgCache = new Dictionary<string, VertexGeometry>();

            // State key: (vertexId, prevRoadId, prevDir). prevRoadId == ""
            // marks the initial state.
            var dist = new Dictionary<(string, string, Direction), float>();
            var prev = new Dictionary<
                (string, string, Direction),
                (string FromVertex, NetworkRoad Road, Direction Dir, string PrevRoad, Direction PrevDir)>();
            var visited = new HashSet<(string, string, Direction)>();
            var frontier = new SortedSet<(float Dist, string Vid, string PrevRoad, int PrevDirInt)>(
                new FrontierComparer());

            var startKey = (startVertexId, "", Direction.AB);
            dist[startKey] = 0f;
            frontier.Add((0f, startVertexId, "", 0));

            (string Vid, string PrevRoad, Direction PrevDir) goalState = (null, null, Direction.AB);

            while (frontier.Count > 0)
            {
                var top = frontier.Min;
                frontier.Remove(top);
                var topKey = (top.Vid, top.PrevRoad, (Direction)top.PrevDirInt);
                if (!visited.Add(topKey)) continue;

                if (top.Vid == endVertexId)
                {
                    goalState = (top.Vid, top.PrevRoad, (Direction)top.PrevDirInt);
                    break;
                }

                if (!adj.TryGetValue(top.Vid, out List<Edge> edges)) continue;

                // Connectivity check is only needed when there's a
                // previous road (non-initial state).
                VertexGeometry vg = null;
                if (!string.IsNullOrEmpty(top.PrevRoad))
                {
                    if (!vgCache.TryGetValue(top.Vid, out vg))
                    {
                        Vertex v = FindVertex(network, top.Vid);
                        vg = v != null ? GeometryResolver.ResolveVertex(network, v) : null;
                        vgCache[top.Vid] = vg;
                    }
                }

                foreach (Edge e in edges)
                {
                    var nextKey = (e.ToVertexId, e.Road.Id, e.Dir);
                    if (visited.Contains(nextKey)) continue;

                    // Connectivity constraint (skip for initial state).
                    if (vg != null
                        && !TransitionExists(vg, top.PrevRoad, (Direction)top.PrevDirInt, e.Road.Id, e.Dir))
                        continue;

                    float newDist = top.Dist + e.Weight;
                    if (!dist.TryGetValue(nextKey, out float existing) || newDist < existing)
                    {
                        dist[nextKey] = newDist;
                        prev[nextKey] = (top.Vid, e.Road, e.Dir, top.PrevRoad, (Direction)top.PrevDirInt);
                        frontier.Add((newDist, e.ToVertexId, e.Road.Id, (int)e.Dir));
                    }
                }
            }

            if (goalState.Vid == null) return null;

            // Reconstruct.
            var rev = new List<(NetworkRoad Road, Direction Dir)>();
            string curVertex = goalState.Vid;
            string curPrevRoad = goalState.PrevRoad;
            Direction curPrevDir = goalState.PrevDir;
            while (!string.IsNullOrEmpty(curPrevRoad))
            {
                var key = (curVertex, curPrevRoad, curPrevDir);
                if (!prev.TryGetValue(key, out var step)) return null;
                rev.Add((step.Road, step.Dir));
                curVertex = step.FromVertex;
                curPrevRoad = step.PrevRoad;
                curPrevDir = step.PrevDir;
            }
            rev.Reverse();
            return rev;
        }

        static bool TransitionExists(VertexGeometry vg,
            string fromRoadId, Direction fromDir,
            string toRoadId, Direction toDir)
        {
            if (vg == null || vg.Connectivity == null) return false;
            foreach (LaneConnection c in vg.Connectivity)
            {
                if (c == null || c.From == null || c.To == null) continue;
                if (c.From.RoadId != fromRoadId) continue;
                if (c.From.Direction != fromDir) continue;
                if (c.To.RoadId != toRoadId) continue;
                if (c.To.Direction != toDir) continue;
                return true;
            }
            return false;
        }

        static bool HasVertex(Network n, string id)
        {
            foreach (Vertex v in n.Vertices) if (v.Id == id) return true;
            return false;
        }

        struct Edge
        {
            public string ToVertexId;
            public NetworkRoad Road;
            public Direction Dir;
            public float Weight;
        }

        static Dictionary<string, List<Edge>> BuildAdjacency(Network network)
        {
            var adj = new Dictionary<string, List<Edge>>();
            foreach (NetworkRoad r in network.Roads)
            {
                Vertex va = FindVertex(network, r.EndA);
                Vertex vb = FindVertex(network, r.EndB);
                if (va == null || vb == null) continue;

                float length;
                if (r.Curve == null)
                {
                    length = Vector2.Distance(va.Position, vb.Position);
                }
                else
                {
                    length = GeometryResolver.CubicArcLength(
                        va.Position, r.Curve.ControlA, r.Curve.ControlB, vb.Position);
                }
                if (length < 1e-4f) continue;

                int abLanes = r.Profile?.AB?.Lanes?.Count ?? 0;
                int baLanes = r.Profile?.BA?.Lanes?.Count ?? 0;

                if (abLanes > 0)
                {
                    AddEdge(adj, r.EndA, new Edge
                    {
                        ToVertexId = r.EndB, Road = r, Dir = Direction.AB, Weight = length,
                    });
                }
                if (baLanes > 0)
                {
                    AddEdge(adj, r.EndB, new Edge
                    {
                        ToVertexId = r.EndA, Road = r, Dir = Direction.BA, Weight = length,
                    });
                }
            }
            return adj;
        }

        static void AddEdge(Dictionary<string, List<Edge>> adj, string from, Edge e)
        {
            if (!adj.TryGetValue(from, out List<Edge> list))
            {
                list = new List<Edge>();
                adj[from] = list;
            }
            list.Add(e);
        }

        static Vertex FindVertex(Network n, string id)
        {
            foreach (Vertex v in n.Vertices) if (v.Id == id) return v;
            return null;
        }

        class FrontierComparer : IComparer<(float Dist, string Vid, string PrevRoad, int PrevDirInt)>
        {
            public int Compare(
                (float Dist, string Vid, string PrevRoad, int PrevDirInt) x,
                (float Dist, string Vid, string PrevRoad, int PrevDirInt) y)
            {
                int c = x.Dist.CompareTo(y.Dist);
                if (c != 0) return c;
                c = string.Compare(x.Vid, y.Vid, System.StringComparison.Ordinal);
                if (c != 0) return c;
                c = string.Compare(x.PrevRoad, y.PrevRoad, System.StringComparison.Ordinal);
                if (c != 0) return c;
                return x.PrevDirInt.CompareTo(y.PrevDirInt);
            }
        }
    }
}
