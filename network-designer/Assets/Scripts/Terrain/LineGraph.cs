// A lightweight node/edge graph for LINEWORK (fences, power lines, pipelines):
// nodes are world XZ points, edges connect two nodes. Each edge is a CUBIC
// BEZIER whose control points are auto-derived (Catmull-Rom) from the node's
// neighbours, so a drawn chain curves smoothly through its points (s-curves
// emerge from placement). Manual per-node handle editing can layer on later by
// overriding the derived controls.
//
// Plain serializable data (XZ only; Y comes from the terrain at render time).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class LineEdge
    {
        public int A;
        public int B;
        public LineEdge() { }
        public LineEdge(int a, int b) { A = a; B = b; }
    }

    [Serializable]
    public class LineGraph
    {
        public List<Vector2> Nodes = new List<Vector2>();
        public List<LineEdge> Edges = new List<LineEdge>();

        public int AddNode(Vector2 p) { Nodes.Add(p); return Nodes.Count - 1; }

        public void AddEdge(int a, int b)
        {
            if (a == b || a < 0 || b < 0 || a >= Nodes.Count || b >= Nodes.Count) return;
            foreach (LineEdge e in Edges)
                if ((e.A == a && e.B == b) || (e.A == b && e.B == a)) return; // no dup
            Edges.Add(new LineEdge(a, b));
        }

        public void Clear() { Nodes.Clear(); Edges.Clear(); }

        // Remove a node and every edge touching it, fixing up the indices of
        // edges that referenced higher-numbered nodes.
        public void RemoveNode(int idx)
        {
            if (idx < 0 || idx >= Nodes.Count) return;
            Edges.RemoveAll(e => e.A == idx || e.B == idx);
            Nodes.RemoveAt(idx);
            foreach (LineEdge e in Edges)
            {
                if (e.A > idx) e.A--;
                if (e.B > idx) e.B--;
            }
        }

        // Nearest node to a world XZ within maxDist; -1 if none.
        public int NearestNode(Vector2 p, float maxDist)
        {
            int best = -1;
            float bestSq = maxDist * maxDist;
            for (int i = 0; i < Nodes.Count; i++)
            {
                float d = (Nodes[i] - p).sqrMagnitude;
                if (d <= bestSq) { bestSq = d; best = i; }
            }
            return best;
        }

        public bool IsEmpty => Edges.Count == 0 && Nodes.Count == 0;

        // First neighbour of `node` that isn't `exclude` (for Catmull-Rom
        // tangents). -1 if none. Junctions just pick the first — good enough.
        int OtherNeighbor(int node, int exclude)
        {
            for (int i = 0; i < Edges.Count; i++)
            {
                LineEdge e = Edges[i];
                if (e.A == node && e.B != exclude) return e.B;
                if (e.B == node && e.A != exclude) return e.A;
            }
            return -1;
        }

        // Cubic-bezier control points for an edge, Catmull-Rom-smoothed using
        // the neighbouring nodes. Endpoints mirror so the curve doesn't kink.
        public void EdgeControls(LineEdge e, out Vector2 p0, out Vector2 p1, out Vector2 p2, out Vector2 p3)
        {
            p0 = Nodes[e.A];
            p3 = Nodes[e.B];
            int prevA = OtherNeighbor(e.A, e.B);
            int nextB = OtherNeighbor(e.B, e.A);
            Vector2 prev = prevA >= 0 ? Nodes[prevA] : p0 - (p3 - p0);
            Vector2 next = nextB >= 0 ? Nodes[nextB] : p3 + (p3 - p0);
            p1 = p0 + (p3 - prev) / 6f;
            p2 = p3 - (next - p0) / 6f;
        }

        public static Vector2 Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        public static Vector2 BezierTangent(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float u = 1f - t;
            return 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
        }
    }

    // Sparse on-disk form: just the nodes + edges (XZ).
    [Serializable]
    public class LineGraphSave
    {
        public List<Vector2> Nodes;
        public List<LineEdge> Edges;
    }
}
