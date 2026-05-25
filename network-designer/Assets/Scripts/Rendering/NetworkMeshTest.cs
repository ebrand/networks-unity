// Mesh equivalent of NetworkGizmosTest: builds the same hand-authored
// 4-way intersection programmatically, then drives a NetworkRenderer
// to produce actual mesh geometry.
//
// Attach to any GameObject. Pressing Play (or just toggling inspector
// values in Edit mode) rebuilds the network and re-spawns child
// renderers under this GameObject.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    [ExecuteAlways]
    [RequireComponent(typeof(NetworkRenderer))]
    public class NetworkMeshTest : MonoBehaviour
    {
        [Header("Profile (used by every road in the test network)")]
        public int AbLanes = 2;
        public int BaLanes = 2;
        public float LaneWidth = 4f;
        public float ShoulderWidth = 1f;
        public bool HasMedian = false;
        public float MedianWidth = 1f;

        [Header("Layout")]
        [Tooltip("Distance from the center vertex to each leaf vertex. " +
                 "Must exceed sum-of-setbacks (~2×W) or the road body collapses.")]
        public float ArmLength = 50f;

        public DriveSide DriveSide = DriveSide.Right;

        NetworkRenderer _renderer;
        int _lastInputHash;

        void OnEnable()
        {
            // Reset state so the first Update() forces a rebuild. We
            // intentionally do NOT rebuild here — Unity's mesh-rendering
            // state isn't always settled during OnEnable, especially in
            // [ExecuteAlways] mode, and rebuilding too early can leave
            // some sub-meshes (e.g. the centerline) not picked up by
            // MeshRenderer until the next manual change. Deferring to
            // Update() avoids that quirk.
            _renderer = GetComponent<NetworkRenderer>();
            _lastInputHash = 0;
        }

        void Update()
        {
            // ExecuteAlways: poll the inspector for changes and rebuild
            // when something moved. Same pattern as NetworkGizmosTest.
            ApplyIfChanged();
        }

        void ApplyIfChanged()
        {
            if (_renderer == null) _renderer = GetComponent<NetworkRenderer>();
            int hash = ComputeInputHash();
            if (hash == _lastInputHash) return;
            _lastInputHash = hash;

            _renderer.Network = BuildTestNetwork();
            _renderer.Rebuild();
        }

        Network BuildTestNetwork()
        {
            Vertex center = new Vertex { Id = "v-center", Position = Vector2.zero, Name = "Center" };
            Vertex east   = new Vertex { Id = "v-east",   Position = new Vector2(+ArmLength, 0f) };
            Vertex west   = new Vertex { Id = "v-west",   Position = new Vector2(-ArmLength, 0f) };
            Vertex north  = new Vertex { Id = "v-north",  Position = new Vector2(0f, +ArmLength) };
            Vertex south  = new Vertex { Id = "v-south",  Position = new Vector2(0f, -ArmLength) };

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
    }
}
