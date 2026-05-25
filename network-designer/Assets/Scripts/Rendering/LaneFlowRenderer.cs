// Per-vertex lane-flow overlay (Edit mode only — the renderer only
// emits geometry when HasSelection is true; spawning is gated upstream).
//
// Renders one dashed arrow per LaneConnection from the resolved
// VertexGeometry, color-coded by a deterministic hash of the From-lane
// key so different inbounds are visually distinguishable. Arrows fan
// from the inbound endpoint to the outbound endpoint at this vertex.
//
// Alpha behavior:
//   - Default: every arrow at DimAlpha (0.25).
//   - Per-frame, if an arrow's From-lane key matches HoveredLaneKey or
//     ArmedFromLaneKey, that arrow renders at FullAlpha (1.0).
//
// To avoid full mesh rebuilds on hover changes, each arrow's vertex
// range is cached in an ArrowMeta list. RefreshAlphas() walks the
// list and mutates only the alpha channel of the shared color array,
// then writes it back as mesh.colors32.
//
// Filtering:
//   - !HasSelection → empty mesh.
//   - ArmedFromLaneKey == null → all arrows from VertexGeometry.Connectivity.
//   - ArmedFromLaneKey != null → only arrows whose From matches.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Designer;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LaneFlowRenderer : MonoBehaviour
    {
        [Header("Source")]
        public VertexGeometry Geometry;

        [Header("Edit state (pushed by NetworkDesigner)")]
        public bool HasSelection;
        public string ArmedFromLaneKey;
        public string HoveredLaneKey;

        [Header("Style")]
        [Range(0f, 1f)] public float DimAlpha = 0.25f;
        [Range(0f, 1f)] public float FullAlpha = 1.0f;
        [Tooltip("World Y for the overlay. Sits above asphalt to avoid z-fight.")]
        public float Height = 0.02f;
        [Tooltip("Arrow shaft width (meters).")]
        public float ShaftWidth = 0.8f;
        [Tooltip("Length of each dash along the shaft.")]
        public float DashLength = 1.2f;
        [Tooltip("Gap between dashes.")]
        public float DashGap = 0.9f;
        [Tooltip("Length of the arrowhead triangle.")]
        public float HeadLength = 2.5f;
        [Tooltip("Width of the arrowhead triangle base.")]
        public float HeadWidth = 2.5f;
        [Tooltip("Sample count for bezier curve tessellation. 24 is smooth at typical intersection scale.")]
        [Range(8, 64)] public int BezierSamples = 24;
        [Tooltip("Control-point distance from each endpoint as a fraction of the chord length. Higher = wider turn radius. 0 = straight line.")]
        [Range(0f, 1f)] public float BezierControlFraction = 0.45f;

        Material _mat;

        struct ArrowMeta
        {
            public string FromKey;
            public int FirstVertex;
            public int VertexCount;
        }

        readonly List<ArrowMeta> _arrowMeta = new List<ArrowMeta>();
        Color32[] _colorBuffer;
        Mesh _mesh;

        // Scratch lists reused across rebuilds.
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _tris = new List<int>();
        readonly List<Color32> _colors = new List<Color32>();

        void Start()
        {
            if (Geometry != null) Rebuild();
        }

        public void Rebuild()
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();

            _arrowMeta.Clear();
            _verts.Clear();
            _tris.Clear();
            _colors.Clear();

            if (!HasSelection || Geometry == null || Geometry.Approaches == null
                || Geometry.Connectivity == null || Geometry.Approaches.Count == 0)
            {
                if (_mesh != null) _mesh.Clear();
                mf.sharedMesh = _mesh;
                return;
            }

            foreach (LaneConnection c in Geometry.Connectivity)
            {
                if (c == null || c.From == null || c.To == null) continue;
                string fromKey = LaneKey(c.From);
                if (ArmedFromLaneKey != null && fromKey != ArmedFromLaneKey) continue;

                Vector2? fromPos = ResolveLaneEndpoint(c.From);
                Vector2? toPos = ResolveLaneEndpoint(c.To);
                if (!fromPos.HasValue || !toPos.HasValue) continue;

                // Bezier control points so the arrow tangent matches
                // traffic flow at both ends: at the inbound endpoint
                // traffic flows TOWARD the vertex (i.e. -outward of
                // that road), and at the outbound endpoint it flows
                // AWAY from the vertex (+outward). Straight-through
                // connections naturally collapse to a straight line
                // (the two tangents are colinear and opposite).
                Vector2 inboundFlow = GetInboundFlowDir(c.From);
                Vector2 outboundFlow = GetOutboundFlowDir(c.To);
                float chord = Vector2.Distance(fromPos.Value, toPos.Value);
                float ctrlLen = chord * BezierControlFraction;
                Vector2 p1 = fromPos.Value + inboundFlow * ctrlLen;
                Vector2 p2 = toPos.Value - outboundFlow * ctrlLen;

                Color color = EditorGeometry.HashToColor(fromKey);
                color.a = AlphaFor(fromKey);

                EditorGeometry.AppendDashedBezierArrow(_verts, _tris, _colors,
                    fromPos.Value, p1, p2, toPos.Value,
                    ShaftWidth, DashLength, DashGap,
                    HeadLength, HeadWidth, Height, color,
                    BezierSamples,
                    out int firstVert, out int vertCount);

                _arrowMeta.Add(new ArrowMeta
                {
                    FromKey = fromKey,
                    FirstVertex = firstVert,
                    VertexCount = vertCount,
                });
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = $"LaneFlow_{Geometry.VertexId}" };
                mf.sharedMesh = _mesh;
            }
            else
            {
                _mesh.Clear();
            }
            _mesh.SetVertices(_verts);
            _mesh.SetTriangles(_tris, 0);
            _mesh.SetColors(_colors);
            _mesh.RecalculateBounds();
            _colorBuffer = _colors.ToArray();

            EnsureMaterial();
            mr.sharedMaterial = _mat;
        }

        /// <summary>
        /// Per-frame alpha refresh. Walks the cached arrow metadata,
        /// computes the desired alpha for each arrow (FullAlpha when
        /// the arrow's From-lane matches the hover or armed key, else
        /// DimAlpha), and writes ONLY the alpha channel into the cached
        /// color array. Then uploads to the mesh. No vert/index churn.
        /// </summary>
        public void RefreshAlphas()
        {
            if (_mesh == null || _colorBuffer == null || _arrowMeta.Count == 0) return;
            for (int i = 0; i < _arrowMeta.Count; i++)
            {
                ArrowMeta meta = _arrowMeta[i];
                byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(AlphaFor(meta.FromKey) * 255f), 0, 255);
                EditorGeometry.SetRangeAlpha(_colorBuffer, meta.FirstVertex, meta.VertexCount, a);
            }
            _mesh.colors32 = _colorBuffer;
        }

        float AlphaFor(string fromKey)
        {
            if (fromKey == HoveredLaneKey || fromKey == ArmedFromLaneKey) return FullAlpha;
            return DimAlpha;
        }

        Vector2? ResolveLaneEndpoint(LaneRef lr)
        {
            if (lr == null || string.IsNullOrEmpty(lr.RoadId)) return null;
            foreach (VertexApproach a in Geometry.Approaches)
            {
                if (a.RoadId != lr.RoadId) continue;
                List<Vector2> lanes = lr.Direction == Direction.AB ? a.LaneEndsAB : a.LaneEndsBA;
                if (lanes == null || lr.Index < 0 || lr.Index >= lanes.Count) return null;
                return lanes[lr.Index];
            }
            return null;
        }

        // Direction inbound traffic on `lr` is HEADING at its endpoint
        // at this vertex — i.e. TOWARD the vertex, which is the opposite
        // of the approach's outward direction (which points away from
        // the vertex into the road body).
        Vector2 GetInboundFlowDir(LaneRef lr)
        {
            VertexApproach a = FindApproach(lr.RoadId);
            if (a == null || a.OuterEdgeDir.sqrMagnitude < 1e-6f) return Vector2.right;
            return -a.OuterEdgeDir.normalized;
        }

        // Direction outbound traffic on `lr` is HEADING at its endpoint
        // at this vertex — AWAY from the vertex, i.e. the approach's
        // outward direction.
        Vector2 GetOutboundFlowDir(LaneRef lr)
        {
            VertexApproach a = FindApproach(lr.RoadId);
            if (a == null || a.OuterEdgeDir.sqrMagnitude < 1e-6f) return Vector2.right;
            return a.OuterEdgeDir.normalized;
        }

        VertexApproach FindApproach(string roadId)
        {
            foreach (VertexApproach a in Geometry.Approaches)
                if (a.RoadId == roadId) return a;
            return null;
        }

        static string LaneKey(LaneRef lr)
        {
            return $"{lr.RoadId}|{(int)lr.Direction}|{lr.Index}";
        }

        void EnsureMaterial()
        {
            if (_mat != null) return;
            Shader sh = Shader.Find("NetworkDesigner/EditorOverlay");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            _mat = new Material(sh) { name = "LaneFlowMat" };
        }
    }
}
