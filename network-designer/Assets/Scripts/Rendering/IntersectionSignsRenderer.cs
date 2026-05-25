// Per-vertex overlay that paints a traffic-control sign beside every
// inbound approach. The sign image is chosen by VertexApproach.Control
// (none.png / stop.png / yield.png from Resources/signs/). Each sign
// is spawned as its own GameObject with a BoxCollider + SignClickTarget
// component so the NetworkDesigner can raycast it and cycle Control on
// click (Stop → Yield → None → Stop).
//
// Position: at the setback midpoint, offset perpendicular-LEFT of
// outward (driver's right side as they approach) by halfWidth + a
// configurable shoulder clearance. Nudged along outward by a small
// amount. Image's UP direction points -outward (toward the vertex)
// so an approaching driver sees the sign right-side-up.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Designer;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    public class IntersectionSignsRenderer : MonoBehaviour
    {
        [Header("Source")]
        public VertexGeometry Geometry;

        [Header("Style")]
        public Color SignTint = Color.white;
        [Range(0f, 1f)] public float SignAlpha = 1.0f;
        public float Height = 0.03f;
        public float SignSize = 2.5f;
        public float SignShoulderClearance = 1.5f;
        public float SignAlongOffset = 0.5f;

        Material[] _matByControl;       // index by (int)StopYieldControl: 0=None, 1=Stop, 2=Yield
        Texture2D[] _texByControl;
        bool _resourcesLoaded;

        readonly List<GameObject> _spawned = new List<GameObject>();

        void Start()
        {
            if (Geometry != null) Rebuild();
        }

        public void Rebuild()
        {
            EnsureResources();
            ClearSpawned();

            if (Geometry == null || Geometry.Approaches == null || Geometry.Approaches.Count == 0) return;

            Color effective = SignTint;
            effective.a *= Mathf.Clamp01(SignAlpha);

            foreach (VertexApproach a in Geometry.Approaches)
            {
                if (a.OuterEdgeDir.sqrMagnitude < 1e-6f) continue;

                Direction inboundDir = a.End == RoadEnd.A ? Direction.BA : Direction.AB;
                List<Vector2> inboundLanes = inboundDir == Direction.AB ? a.LaneEndsAB : a.LaneEndsBA;
                if (inboundLanes == null || inboundLanes.Count == 0) continue;

                Vector2 outward = a.OuterEdgeDir.normalized;
                Vector2 setbackMidpoint = (a.OuterLeft + a.OuterRight) * 0.5f;
                float halfRoadWidth = (a.OuterRight - a.OuterLeft).magnitude * 0.5f;
                // Perp-LEFT of outward = driver's right as they approach.
                Vector2 perpLeft = new Vector2(-outward.y, outward.x);
                Vector2 signCenter = setbackMidpoint
                    + perpLeft * (halfRoadWidth + SignShoulderClearance)
                    + outward * SignAlongOffset;

                Vector2 imageUp = -outward;
                int ctrlIdx = (int)a.Control;
                SpawnSignGameObject(a.RoadId, a.End, signCenter, imageUp, SignSize, Height, effective, ctrlIdx);
            }
        }

        void SpawnSignGameObject(string roadId, RoadEnd end, Vector2 center, Vector2 imageUp,
            float size, float y, Color color, int ctrlIdx)
        {
            GameObject go = new GameObject($"Sign_{roadId}_{end}_{(StopYieldControl)ctrlIdx}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position = new Vector3(center.x, y, center.y);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            BoxCollider bc = go.AddComponent<BoxCollider>();
            SignClickTarget tag = go.AddComponent<SignClickTarget>();
            tag.RoadId = roadId;
            tag.End = end;

            // Build the quad mesh in LOCAL coords so we can use the
            // BoxCollider for picking.
            Mesh mesh = new Mesh { name = $"SignMesh_{roadId}_{end}" };
            BuildLocalQuad(mesh, imageUp, size, color);
            mf.sharedMesh = mesh;

            // Collider: thin box around the quad's local extent (XZ
            // halfsize = size/2; Y halfsize tiny since the sign is flat).
            bc.size = new Vector3(size, 0.05f, size);
            bc.center = Vector3.zero;

            mr.sharedMaterial = _matByControl[ctrlIdx] != null
                ? _matByControl[ctrlIdx]
                : _matByControl[0]; // fallback to None material

            _spawned.Add(go);
        }

        static void BuildLocalQuad(Mesh mesh, Vector2 imageUp, float size, Color color)
        {
            if (imageUp.sqrMagnitude < 1e-6f) imageUp = Vector2.up;
            Vector2 up = imageUp.normalized;
            // Image RIGHT = +90° CW rotation of UP.
            Vector2 right = new Vector2(up.y, -up.x);
            float half = size * 0.5f;
            // Local coords: parent transform is at sign center.
            Vector2 bl = -right * half - up * half;
            Vector2 br =  right * half - up * half;
            Vector2 tr =  right * half + up * half;
            Vector2 tl = -right * half + up * half;
            Vector3[] verts = new Vector3[]
            {
                new Vector3(bl.x, 0f, bl.y),
                new Vector3(br.x, 0f, br.y),
                new Vector3(tr.x, 0f, tr.y),
                new Vector3(tl.x, 0f, tl.y),
            };
            Vector2[] uvs = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            Color32 c = (Color32)color;
            Color32[] colors = new Color32[] { c, c, c, c };
            // CW from above (+Y normal): (tl, tr, br) + (tl, br, bl).
            int[] tris = new int[] { 3, 2, 1, 3, 1, 0 };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.colors32 = colors;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
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

        void EnsureResources()
        {
            if (_resourcesLoaded) return;
            _resourcesLoaded = true;

            _texByControl = new Texture2D[3];
            _texByControl[(int)StopYieldControl.None]  = Resources.Load<Texture2D>("signs/none");
            _texByControl[(int)StopYieldControl.Stop]  = Resources.Load<Texture2D>("signs/stop");
            _texByControl[(int)StopYieldControl.Yield] = Resources.Load<Texture2D>("signs/yield");

            Shader textured = Shader.Find("NetworkDesigner/EditorOverlayTextured");
            _matByControl = new Material[3];
            string[] names = { "NoneSignMat", "StopSignMat", "YieldSignMat" };
            for (int i = 0; i < 3; i++)
            {
                if (textured != null && _texByControl[i] != null)
                {
                    _matByControl[i] = new Material(textured)
                    {
                        name = names[i],
                        mainTexture = _texByControl[i],
                    };
                }
            }
        }
    }
}
