// Per-vertex painted-line overlay: each Vertex.LaneMarkings entry
// becomes a cubic-bezier strip on the asphalt connecting two lane
// endpoints, with two user-draggable interior control points and
// per-marking color (white/yellow) and style (solid/dashed).
//
// Each marking is spawned as its own child GameObject with a
// MarkingClickTarget tag + a loose BoxCollider so NetworkDesigner can
// raycast to cycle style (left-click) or delete (right-click).
//
// Endpoint positions are resolved fresh on every Rebuild from
// VertexGeometry's LaneEndsAB/LaneEndsBA (matching the lane this
// marking references). If a referenced lane has disappeared (road
// deleted, profile changed lane count), the marking renders nothing
// — the bookkeeping cleanup lives in the designer.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Designer;
using NetworkDesigner.Geometry;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    public class LaneMarkingsRenderer : MonoBehaviour
    {
        [Header("Source")]
        public VertexGeometry Geometry;
        /// <summary>
        /// The list of markings to render for this vertex (i.e.,
        /// Vertex.LaneMarkings). Pushed by NetworkRenderer.
        /// </summary>
        public List<LaneMarking> Markings;

        [Header("Style")]
        public Color WhiteColor = Color.white;
        public Color YellowColor = new Color(1f, 0.85f, 0.2f, 1f);
        [Range(0f, 1f)] public float Alpha = 1.0f;
        public float Width = 0.18f;
        public float Height = 0.013f;
        public float DashLength = 0.7f;
        public float DashGap = 0.6f;
        [Range(8, 64)] public int BezierSamples = 24;

        readonly List<GameObject> _spawned = new List<GameObject>();
        Material _mat;

        void Start()
        {
            if (Geometry != null) Rebuild();
        }

        public void Rebuild()
        {
            EnsureMaterial();
            ClearSpawned();

            if (Geometry == null || Geometry.Approaches == null) return;
            if (Markings == null || Markings.Count == 0) return;

            foreach (LaneMarking m in Markings)
            {
                if (m == null) continue;
                Vector2? fromPos = GeometryResolver.ResolveLaneNode(Geometry, m.From, m.FromNode);
                Vector2? toPos = GeometryResolver.ResolveLaneNode(Geometry, m.To, m.ToNode);
                if (!fromPos.HasValue || !toPos.HasValue) continue;

                Color baseColor;
                if (m.Color == LaneMarkingColor.Yellow) baseColor = YellowColor;
                else if (m.Color == LaneMarkingColor.Auto)
                {
                    // Match the corner-marker color the user clicked.
                    // Yellow only when the FROM corner is an inner-side
                    // node (Origin/Tertiary) of the innermost lane —
                    // i.e., it sits on the road centerline. Every other
                    // corner is on a white lane divider or fog line.
                    bool innerSide = m.FromNode == LaneNode.Origin || m.FromNode == LaneNode.Tertiary;
                    bool innermostLane = m.From != null && m.From.Index == 0;
                    baseColor = (innerSide && innermostLane) ? YellowColor : WhiteColor;
                }
                else baseColor = WhiteColor;
                baseColor.a *= Mathf.Clamp01(Alpha);
                float dash = m.Style == LaneMarkingStyle.Dashed ? DashLength : 0f;
                float gap = m.Style == LaneMarkingStyle.Dashed ? DashGap : 0f;

                SpawnMarkingGameObject(m, fromPos.Value, toPos.Value, baseColor, dash, gap);
            }
        }

        void SpawnMarkingGameObject(LaneMarking m, Vector2 from, Vector2 to, Color color, float dashLen, float gapLen)
        {
            GameObject go = new GameObject($"Marking_{m.Id}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position = Vector3.zero;

            MeshFilter mf = go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;

            // Build the strip mesh.
            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();
            List<Color32> colors = new List<Color32>();
            EditorGeometry.AppendBezierStripe(verts, tris, colors,
                from, m.Primary, m.Secondary, to,
                Width, dashLen, gapLen, Height, color,
                BezierSamples,
                out _, out _);

            Mesh mesh = new Mesh { name = $"MarkingMesh_{m.Id}" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.SetColors(colors);
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;

            // BoxCollider sized to the mesh's bounds, expanded a bit
            // so the user can click near (not exactly on) the line.
            BoxCollider bc = go.AddComponent<BoxCollider>();
            Bounds b = mesh.bounds;
            b.Expand(new Vector3(0.5f, 0.05f, 0.5f)); // forgiving in XZ
            bc.center = b.center;
            bc.size = b.size;

            MarkingClickTarget tag = go.AddComponent<MarkingClickTarget>();
            tag.VertexId = Geometry.VertexId;
            tag.MarkingId = m.Id;

            _spawned.Add(go);
        }

        void ClearSpawned()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] == null) continue;
                if (Application.isPlaying) Destroy(_spawned[i]);
                else DestroyImmediate(_spawned[i]);
            }
            _spawned.Clear();
        }

        void EnsureMaterial()
        {
            if (_mat != null) return;
            Shader sh = Shader.Find("NetworkDesigner/EditorOverlay");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            _mat = new Material(sh) { name = "LaneMarkingMat" };
        }
    }
}
