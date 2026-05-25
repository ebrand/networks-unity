// Hand-authors a small Network programmatically and draws the resolver
// output in the Scene view using Gizmos. No mesh rendering yet — this is
// a math-correctness check.
//
// Default scene: a perpendicular 4-way intersection at the origin with
// four 30m roads going N/S/E/W. All roads use a simple 2x2 profile
// (2 lanes each direction, 4m each, 1m shoulders, no median).
//
// What to look for in the Scene view:
//   - Blue line: road centerlines (vertex to vertex).
//   - White circles: vertex positions.
//   - Yellow lines: setback lines (perpendicular cuts where each
//     approach's geometry ends).
//   - Cyan lines: outline LINE segments (joint connectors).
//   - Magenta lines: bezier fillets (tessellated into 16 short segments).
//   - Green dots: lane endpoints on each setback line.
//
// The whole intersection should look like a + shape with four quarter-
// circle-ish fillets joining the road bodies.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Geometry
{
    [ExecuteAlways]
    public class NetworkGizmosTest : MonoBehaviour
    {
        [Header("Profile (used by every road in the test network)")]
        public int AbLanes = 2;
        public int BaLanes = 2;
        public float LaneWidth = 4f;
        public float ShoulderWidth = 1f;
        public bool HasMedian = false;
        public float MedianWidth = 1f;

        [Header("Layout")]
        [Tooltip("Distance from the center vertex to each outer (leaf) vertex. " +
                 "Must be at least sum-of-setbacks (≈ 2×W) or the road body collapses. " +
                 "With the default 2x2 profile (W=18m), keep this ≥ ~40m.")]
        public float ArmLength = 50f;

        public DriveSide DriveSide = DriveSide.Right;

        [Header("Gizmo tessellation")]
        [Range(2, 64)] public int BezierSamples = 16;
        public bool DrawCenterlines = true;
        public bool DrawSetbackLines = true;
        public bool DrawOutline = true;
        public bool DrawLaneEndpoints = true;
        public bool DrawApproachLabels = true;

        Network _network;
        List<VertexGeometry> _geometry;
        int _lastInputHash;

        void OnDrawGizmos()
        {
            // Only rebuild when the inspector fields actually changed.
            // OnDrawGizmos fires many times per second in the Scene view,
            // and rebuilding every frame would (a) waste cycles and (b)
            // spam the console with validation warnings.
            int hash = ComputeInputHash();
            if (_network == null || hash != _lastInputHash)
            {
                Rebuild();
                _lastInputHash = hash;
            }
            if (_network == null || _geometry == null) return;
            DrawCenterlinesIfWanted();
            DrawVertices();
            DrawResolved();
        }

        void Rebuild()
        {
            _network = BuildTestNetwork();
            _geometry = GeometryResolver.ResolveNetwork(_network);
        }

        int ComputeInputHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + AbLanes;
                h = h * 31 + BaLanes;
                h = h * 31 + LaneWidth.GetHashCode();
                h = h * 31 + ShoulderWidth.GetHashCode();
                h = h * 31 + HasMedian.GetHashCode();
                h = h * 31 + MedianWidth.GetHashCode();
                h = h * 31 + ArmLength.GetHashCode();
                h = h * 31 + (int)DriveSide;
                return h;
            }
        }

        Network BuildTestNetwork()
        {
            // Center vertex + 4 leaves, hand-laid for a perpendicular +.
            Vertex center = new Vertex { Id = "v-center", Position = Vector2.zero, Name = "Center" };
            Vertex east   = new Vertex { Id = "v-east",   Position = new Vector2(+ArmLength, 0f) };
            Vertex west   = new Vertex { Id = "v-west",   Position = new Vector2(-ArmLength, 0f) };
            Vertex north  = new Vertex { Id = "v-north",  Position = new Vector2(0f, +ArmLength) };
            Vertex south  = new Vertex { Id = "v-south",  Position = new Vector2(0f, -ArmLength) };

            // First-drawn convention: classify east-west as primary,
            // north-south as secondary (matches a typical "main road
            // crossed by side road" mental model).
            NetworkRoad roadE = MakeRoad("road-e", center.Id, east.Id,  RoadClassification.Primary);
            NetworkRoad roadW = MakeRoad("road-w", center.Id, west.Id,  RoadClassification.Primary);
            NetworkRoad roadN = MakeRoad("road-n", center.Id, north.Id, RoadClassification.Secondary);
            NetworkRoad roadS = MakeRoad("road-s", center.Id, south.Id, RoadClassification.Secondary);

            return new Network
            {
                DriveSide = DriveSide,
                Vertices = new List<Vertex> { center, east, west, north, south },
                Roads    = new List<NetworkRoad> { roadE, roadW, roadN, roadS },
            };
        }

        RoadProfile BuildProfile(string id)
        {
            RoadProfile p = new RoadProfile
            {
                Id = id,
                AB = new Side(),
                BA = new Side(),
                ShoulderAB = new Shoulder { Width = ShoulderWidth },
                ShoulderBA = new Shoulder { Width = ShoulderWidth },
                Median = HasMedian ? new Median { Width = MedianWidth } : null,
            };
            for (int i = 0; i < AbLanes; i++)
                p.AB.Lanes.Add(new Lane { Id = $"{id}-ab-{i}", Width = LaneWidth });
            for (int i = 0; i < BaLanes; i++)
                p.BA.Lanes.Add(new Lane { Id = $"{id}-ba-{i}", Width = LaneWidth });
            return p;
        }

        NetworkRoad MakeRoad(string id, string endA, string endB, RoadClassification cls)
        {
            return new NetworkRoad
            {
                Id = id,
                EndA = endA,
                EndB = endB,
                Classification = cls,
                Profile = BuildProfile(id),
            };
        }

        // -----------------------------------------------------------------
        // Drawing
        // -----------------------------------------------------------------

        static Vector3 ToVec3(Vector2 v) => new Vector3(v.x, 0f, v.y);

        void DrawCenterlinesIfWanted()
        {
            if (!DrawCenterlines) return;
            Gizmos.color = new Color(0.4f, 0.6f, 1f);
            foreach (NetworkRoad r in _network.Roads)
            {
                Vertex a = _network.Vertices.Find(v => v.Id == r.EndA);
                Vertex b = _network.Vertices.Find(v => v.Id == r.EndB);
                if (a == null || b == null) continue;
                Gizmos.DrawLine(ToVec3(a.Position), ToVec3(b.Position));
            }
        }

        void DrawVertices()
        {
            Gizmos.color = Color.white;
            foreach (Vertex v in _network.Vertices)
            {
                Gizmos.DrawWireSphere(ToVec3(v.Position), 0.4f);
            }
        }

        void DrawResolved()
        {
            foreach (VertexGeometry vg in _geometry)
            {
                if (DrawSetbackLines)
                {
                    Gizmos.color = new Color(1f, 0.85f, 0.2f);
                    foreach (VertexApproach a in vg.Approaches)
                    {
                        Gizmos.DrawLine(ToVec3(a.OuterLeft), ToVec3(a.OuterRight));
                    }
                }

                if (DrawLaneEndpoints)
                {
                    Gizmos.color = new Color(0.3f, 1f, 0.4f);
                    foreach (VertexApproach a in vg.Approaches)
                    {
                        foreach (Vector2 p in a.LaneEndsAB) Gizmos.DrawSphere(ToVec3(p), 0.18f);
                        foreach (Vector2 p in a.LaneEndsBA) Gizmos.DrawSphere(ToVec3(p), 0.18f);
                    }
                }

                if (DrawOutline)
                {
                    foreach (OutlineSegment seg in vg.Outline)
                    {
                        if (seg.Kind == SegmentKind.Line)
                        {
                            Gizmos.color = Color.cyan;
                            Gizmos.DrawLine(ToVec3(seg.From), ToVec3(seg.To));
                        }
                        else
                        {
                            Gizmos.color = Color.magenta;
                            Vector2 prev = seg.From;
                            for (int i = 1; i <= BezierSamples; i++)
                            {
                                float t = i / (float)BezierSamples;
                                Vector2 cur = GeometryResolver.SampleQuadratic(
                                    seg.From, seg.Control, seg.To, t);
                                Gizmos.DrawLine(ToVec3(prev), ToVec3(cur));
                                prev = cur;
                            }
                        }
                    }
                }

                if (DrawApproachLabels)
                {
#if UNITY_EDITOR
                    foreach (VertexApproach a in vg.Approaches)
                    {
                        Vector2 setbackPoint =
                            (a.OuterLeft + a.OuterRight) * 0.5f;
                        UnityEditor.Handles.Label(
                            ToVec3(setbackPoint) + Vector3.up * 0.5f,
                            $"{a.RoadId}.{a.End} sb={a.Setback:0.##}m");
                    }
#endif
                }
            }
        }
    }
}
