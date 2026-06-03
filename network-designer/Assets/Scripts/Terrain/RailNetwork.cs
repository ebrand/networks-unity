// Navigable view of a rail LineGraph: node<->edge adjacency, per-edge arc
// length, A* routing, and connected components. The train system routes on this;
// it's rebuilt (cheaply) whenever the track graph changes. Pure data/graph — no
// Unity objects, no rendering.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public class RailNetwork
    {
        LineGraph _g;
        readonly List<List<int>> _nodeEdges = new List<List<int>>(); // node -> edge indices touching it
        float[] _edgeLen = System.Array.Empty<float>();

        public LineGraph Graph => _g;
        public int NodeCount => _g != null ? _g.Nodes.Count : 0;
        public int EdgeCount => _g != null ? _g.Edges.Count : 0;
        public float EdgeLength(int e) => (e >= 0 && e < _edgeLen.Length) ? _edgeLen[e] : 0f;
        public IReadOnlyList<int> EdgesAt(int node)
            => (node >= 0 && node < _nodeEdges.Count) ? (IReadOnlyList<int>)_nodeEdges[node] : System.Array.Empty<int>();

        public void Build(LineGraph g)
        {
            _g = g;
            _nodeEdges.Clear();
            int nodes = g != null ? g.Nodes.Count : 0;
            for (int i = 0; i < nodes; i++) _nodeEdges.Add(new List<int>());
            int edges = g != null ? g.Edges.Count : 0;
            _edgeLen = new float[edges];
            for (int ei = 0; ei < edges; ei++)
            {
                LineEdge e = g.Edges[ei];
                if (e.A >= 0 && e.A < _nodeEdges.Count) _nodeEdges[e.A].Add(ei);
                if (e.B >= 0 && e.B < _nodeEdges.Count) _nodeEdges[e.B].Add(ei);
                _edgeLen[ei] = ArcLength(g, e);
            }
        }

        static float ArcLength(LineGraph g, LineEdge e)
        {
            Vector2 p0 = g.Nodes[e.A], p3 = g.Nodes[e.B], q1, q2;
            if (e.HasCurve) { q1 = e.ControlA; q2 = e.ControlB; }
            else { Vector2 d = p3 - p0; q1 = p0 + d / 3f; q2 = p0 + d * (2f / 3f); }
            const int N = 24;
            float len = 0f;
            Vector2 prev = p0;
            for (int i = 1; i <= N; i++)
            {
                Vector2 cur = LineGraph.Bezier(p0, q1, q2, p3, i / (float)N);
                len += Vector2.Distance(prev, cur);
                prev = cur;
            }
            return len;
        }

        int Other(int edge, int node) { LineEdge e = _g.Edges[edge]; return e.A == node ? e.B : e.A; }

        // A* shortest route (by arc length) startNode -> goalNode. Returns the
        // ordered edge indices to traverse, or null if unreachable. (Linear-scan
        // open set — fine for hobby-scale networks.)
        public List<int> FindRoute(int startNode, int goalNode)
        {
            if (_g == null || startNode < 0 || goalNode < 0 ||
                startNode >= _g.Nodes.Count || goalNode >= _g.Nodes.Count) return null;
            if (startNode == goalNode) return new List<int>();
            int n = _g.Nodes.Count;
            var g = new float[n];
            var f = new float[n];
            var cameNode = new int[n];
            var cameEdge = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = float.PositiveInfinity; cameNode[i] = -1; cameEdge[i] = -1; }
            g[startNode] = 0f;
            f[startNode] = Heur(startNode, goalNode);
            var open = new List<int> { startNode };
            while (open.Count > 0)
            {
                int bi = 0;
                for (int i = 1; i < open.Count; i++) if (f[open[i]] < f[open[bi]]) bi = i;
                int cur = open[bi];
                open.RemoveAt(bi);
                if (cur == goalNode) return Reconstruct(cameNode, cameEdge, goalNode);
                if (closed[cur]) continue;
                closed[cur] = true;
                foreach (int ei in _nodeEdges[cur])
                {
                    int nb = Other(ei, cur);
                    if (closed[nb]) continue;
                    float tentative = g[cur] + _edgeLen[ei];
                    if (tentative < g[nb])
                    {
                        g[nb] = tentative;
                        cameNode[nb] = cur;
                        cameEdge[nb] = ei;
                        f[nb] = tentative + Heur(nb, goalNode);
                        if (!open.Contains(nb)) open.Add(nb);
                    }
                }
            }
            return null; // unreachable (different component)
        }

        float Heur(int a, int b) => Vector2.Distance(_g.Nodes[a], _g.Nodes[b]);

        List<int> Reconstruct(int[] cameNode, int[] cameEdge, int goal)
        {
            var route = new List<int>();
            int cur = goal;
            while (cameEdge[cur] >= 0) { route.Add(cameEdge[cur]); cur = cameNode[cur]; }
            route.Reverse();
            return route;
        }

        // Connected-component label per node (BFS). Same label = mutually
        // reachable. `count` = number of separate components (1 = one network).
        public int[] ComponentLabels(out int count)
        {
            count = 0;
            int n = NodeCount;
            var label = new int[n];
            for (int i = 0; i < n; i++) label[i] = -1;
            var stack = new Stack<int>();
            for (int s = 0; s < n; s++)
            {
                if (label[s] >= 0) continue;
                int c = count++;
                label[s] = c;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    foreach (int ei in _nodeEdges[cur])
                    {
                        int nb = Other(ei, cur);
                        if (label[nb] < 0) { label[nb] = c; stack.Push(nb); }
                    }
                }
            }
            return label;
        }

        // The label of the component with the most nodes (the "main" network),
        // or -1 if empty. Used to flag everything else as disconnected.
        public int LargestComponent(int[] label, int count)
        {
            if (count <= 0) return -1;
            var size = new int[count];
            for (int i = 0; i < label.Length; i++) if (label[i] >= 0) size[label[i]]++;
            int best = 0;
            for (int c = 1; c < count; c++) if (size[c] > size[best]) best = c;
            return best;
        }
    }
}
