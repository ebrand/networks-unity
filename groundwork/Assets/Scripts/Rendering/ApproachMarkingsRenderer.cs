// Per-vertex pavement-markings overlay: stop lines, yield triangles,
// and painted lane arrows. Sits ON the asphalt (slightly above to
// avoid z-fight) and reads as actual road paint rather than UI
// chrome. One mesh per vertex; rebuilt when network geometry changes.
//
// Pulls everything from VertexGeometry — approaches give the setback
// line and lane endpoints; Connectivity gives per-lane destinations
// for the arrow classification (L/S/R bitmask → arrow kind).
//
// Lane arrows are textured quads when the matching PNG exists under
// Resources/lane-arrows/ (one per L/S/R combination), and fall back
// to mesh-built glyphs when the texture is missing. The mesh uses 8
// sub-meshes — sub 0 = untextured paint (stop lines, yields, fallback
// glyphs); subs 1-7 = textured arrow quads, one sub-mesh per arrow
// kind so each can carry its own _MainTex.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Designer;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class ApproachMarkingsRenderer : MonoBehaviour
    {
        [Header("Source")]
        public VertexGeometry Geometry;

        [Header("Toggles")]
        [Tooltip("Master toggle for per-approach control paint. When on, each inbound approach paints a stop line (Control=Stop), sharks teeth (Control=Yield), or nothing (Control=None).")]
        public bool ShowPaintedControls = true;
        public bool ShowLaneArrows = true;
        /// <summary>
        /// Per-vertex suppression — set by NetworkDesigner while the
        /// user holds Shift for intersection-marking authoring. Hides
        /// stop lines + sharks teeth + lane arrows at this vertex
        /// without disturbing the global ShowPaintedControls toggle.
        /// </summary>
        public bool SuppressForMarkingMode = false;

        [Header("Style")]
        public Color MarkingColor = new Color(0.95f, 0.95f, 0.95f, 1f);
        [Tooltip("World Y for the marking mesh — above asphalt, below edit overlays.")]
        public float Height = 0.012f;

        [Header("Stop line")]
        public float StopLineWidth = 0.6f;
        public float StopLineInsetFromSetback = 0.2f;

        [Header("Yield triangles")]
        public float YieldTriangleBase = 0.6f;
        public float YieldTriangleHeight = 0.9f;
        public float YieldTriangleSpacing = 0.4f;
        public float YieldInsetFromSetback = 0.2f;

        [Header("Lane arrows")]
        [Tooltip("Side length of the textured arrow quad (meters). Square.")]
        public float LaneArrowSize = 3.5f;
        [Tooltip("Distance from the setback line into the road body where the arrow center sits.")]
        public float LaneArrowOffset = 4.0f;
        [Tooltip("Connections within ±this many degrees of straight render as 'straight'; outside become left/right.")]
        public float StraightTurnThresholdDeg = 30f;
        [Tooltip("Fallback glyph length when no PNG texture is found for a given arrow kind.")]
        public float FallbackGlyphLength = 3.0f;
        public float FallbackGlyphShaftWidth = 0.4f;
        public float FallbackGlyphHeadLength = 0.9f;
        public float FallbackGlyphHeadWidth = 1.3f;
        public float FallbackGlyphStackSpacing = 4.0f;

        // 8 arrow kinds: index 0 = None (no arrow), 1..7 = combinations.
        // Indices match the layout of the sub-mesh + materials arrays.
        public enum ArrowKind { None = 0, L = 1, S = 2, R = 3, LS = 4, SR = 5, LR = 6, LSR = 7 }

        // -------- Mesh build --------

        Material _untexturedMat;
        Material[] _texturedMats; // length 8; element 0 unused
        bool _resourcesLoaded;
        Texture2D[] _arrowTextures; // length 8; element 0 unused

        Mesh _mesh;
        readonly List<Vector3>[] _verts = new List<Vector3>[8];
        readonly List<int>[] _tris = new List<int>[8];
        readonly List<Color32>[] _colors = new List<Color32>[8];
        readonly List<Vector2>[] _uvs = new List<Vector2>[8];

        public ApproachMarkingsRenderer()
        {
            for (int i = 0; i < 8; i++)
            {
                _verts[i] = new List<Vector3>();
                _tris[i] = new List<int>();
                _colors[i] = new List<Color32>();
                _uvs[i] = new List<Vector2>();
            }
        }

        void Start()
        {
            if (Geometry != null) Rebuild();
        }

        public void Rebuild()
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();
            EnsureResources();

            for (int i = 0; i < 8; i++)
            {
                _verts[i].Clear();
                _tris[i].Clear();
                _colors[i].Clear();
                _uvs[i].Clear();
            }

            if (Geometry == null || Geometry.Approaches == null || Geometry.Approaches.Count == 0)
            {
                if (_mesh != null) _mesh.Clear();
                mf.sharedMesh = _mesh;
                return;
            }

            foreach (VertexApproach a in Geometry.Approaches)
            {
                if (a.OuterEdgeDir.sqrMagnitude < 1e-6f) continue;
                Vector2 outward = a.OuterEdgeDir.normalized;

                Direction inboundDir = a.End == RoadEnd.A ? Direction.BA : Direction.AB;
                List<Vector2> inboundLanes = inboundDir == Direction.AB ? a.LaneEndsAB : a.LaneEndsBA;
                List<float> inboundWidths = inboundDir == Direction.AB ? a.LaneWidthsAB : a.LaneWidthsBA;
                bool hasInbound = inboundLanes != null && inboundLanes.Count > 0;
                if (!hasInbound) continue;

                // Stop lines + yield triangles paint ONLY across the
                // inbound asphalt — not the shoulder, not the outbound
                // lanes. Compute the leftmost+rightmost lane edges of
                // the inbound block along the setback cross-direction,
                // then narrow the span accordingly.
                ComputeInboundSpan(a, inboundLanes, inboundWidths,
                    out Vector2 inboundLeft, out Vector2 inboundRight);

                if (ShowPaintedControls && !SuppressForMarkingMode)
                {
                    // Per-approach paint driven by Control:
                    //   Stop  → stop line
                    //   Yield → sharks teeth
                    //   None  → nothing
                    if (a.Control == StopYieldControl.Stop)
                    {
                        EditorGeometry.AppendStopLine(_verts[0], _tris[0], _colors[0],
                            inboundLeft, inboundRight, outward,
                            StopLineWidth, StopLineInsetFromSetback, Height, MarkingColor);
                        PadUVs(_uvs[0], _verts[0].Count);
                    }
                    else if (a.Control == StopYieldControl.Yield)
                    {
                        EditorGeometry.AppendYieldTriangles(_verts[0], _tris[0], _colors[0],
                            inboundLeft, inboundRight, outward,
                            YieldTriangleBase, YieldTriangleHeight, YieldTriangleSpacing,
                            YieldInsetFromSetback, Height, MarkingColor);
                        PadUVs(_uvs[0], _verts[0].Count);
                    }
                }
                if (ShowLaneArrows && !SuppressForMarkingMode && Geometry.Connectivity != null)
                {
                    EmitLaneArrows(a, outward, inboundDir, inboundLanes);
                }
            }

            // -------- Assemble multi-submesh mesh --------
            if (_mesh == null)
            {
                _mesh = new Mesh { name = $"ApproachMarkings_{Geometry.VertexId}" };
                mf.sharedMesh = _mesh;
            }
            else
            {
                _mesh.Clear();
            }

            // Concatenate all sub-mesh verts/colors/uvs into one mesh
            // with sub-mesh triangle indices remapped to the combined
            // vertex buffer.
            List<Vector3> allVerts = new List<Vector3>();
            List<Color32> allColors = new List<Color32>();
            List<Vector2> allUVs = new List<Vector2>();
            int[] subBaseVertex = new int[8];
            for (int s = 0; s < 8; s++)
            {
                subBaseVertex[s] = allVerts.Count;
                allVerts.AddRange(_verts[s]);
                allColors.AddRange(_colors[s]);
                // Ensure uvs are aligned to verts count.
                while (_uvs[s].Count < _verts[s].Count) _uvs[s].Add(Vector2.zero);
                allUVs.AddRange(_uvs[s]);
            }
            _mesh.SetVertices(allVerts);
            _mesh.SetColors(allColors);
            _mesh.SetUVs(0, allUVs);
            _mesh.subMeshCount = 8;
            for (int s = 0; s < 8; s++)
            {
                int[] remapped = new int[_tris[s].Count];
                int baseIdx = subBaseVertex[s];
                for (int i = 0; i < remapped.Length; i++) remapped[i] = _tris[s][i] + baseIdx;
                _mesh.SetTriangles(remapped, s);
            }
            _mesh.RecalculateBounds();

            // Materials array — 8 slots, one per sub-mesh. Slot 0 =
            // untextured paint, slots 1-7 = textured per ArrowKind.
            Material[] mats = new Material[8];
            mats[0] = _untexturedMat;
            for (int i = 1; i < 8; i++)
            {
                // Fall back to the untextured material when a kind's
                // texture is missing — its sub-mesh should be empty in
                // that case (the glyph fallback went into sub 0
                // instead), so the slot just renders nothing.
                mats[i] = _texturedMats[i] != null ? _texturedMats[i] : _untexturedMat;
            }
            mr.sharedMaterials = mats;
        }

        static void PadUVs(List<Vector2> uvs, int targetCount)
        {
            while (uvs.Count < targetCount) uvs.Add(Vector2.zero);
        }

        void EmitLaneArrows(VertexApproach a, Vector2 outward, Direction inboundDir, List<Vector2> inboundLanes)
        {
            for (int laneIdx = 0; laneIdx < inboundLanes.Count; laneIdx++)
            {
                Vector2 laneEnd = inboundLanes[laneIdx];

                // Classify this lane's outbound destinations as L/S/R.
                Vector2 inboundFlow = -outward; // direction the driver is heading
                bool hasL = false, hasS = false, hasR = false;
                int connCount = 0;
                foreach (LaneConnection c in Geometry.Connectivity)
                {
                    if (c == null || c.From == null) continue;
                    if (c.From.RoadId != a.RoadId) continue;
                    if (c.From.Direction != inboundDir) continue;
                    if (c.From.Index != laneIdx) continue;
                    VertexApproach toAppr = FindApproach(c.To.RoadId);
                    if (toAppr == null || toAppr.OuterEdgeDir.sqrMagnitude < 1e-6f) continue;
                    Vector2 outboundFlow = toAppr.OuterEdgeDir.normalized;
                    float angle = Vector2.SignedAngle(inboundFlow, outboundFlow);
                    if (Mathf.Abs(angle) < StraightTurnThresholdDeg) hasS = true;
                    else if (angle > 0f) hasL = true;
                    else hasR = true;
                    connCount++;
                }
                if (connCount == 0) continue;

                ArrowKind kind = KindFromFlags(hasL, hasS, hasR);
                if (kind == ArrowKind.None) continue;

                int kindIdx = (int)kind;
                Vector2 center = laneEnd + outward * LaneArrowOffset;

                if (_arrowTextures != null && _arrowTextures[kindIdx] != null)
                {
                    // Textured quad on the per-kind sub-mesh.
                    AppendTexturedArrowQuad(
                        _verts[kindIdx], _tris[kindIdx], _colors[kindIdx], _uvs[kindIdx],
                        center, inboundFlow, LaneArrowSize, Height, MarkingColor);
                }
                else
                {
                    // No texture — fall back to mesh glyph(s) on sub 0.
                    // For composite kinds (e.g., LS), stack one fallback
                    // glyph per category present so the user still sees
                    // all destinations.
                    int stackPos = 0;
                    if (hasS)
                    {
                        EmitFallbackStraight(center, inboundFlow, outward, stackPos);
                        stackPos++;
                    }
                    if (hasL)
                    {
                        EmitFallbackTurn(center, inboundFlow, outward, -1f, stackPos);
                        stackPos++;
                    }
                    if (hasR)
                    {
                        EmitFallbackTurn(center, inboundFlow, outward, +1f, stackPos);
                        stackPos++;
                    }
                }
            }
        }

        void EmitFallbackStraight(Vector2 center, Vector2 forward, Vector2 outward, int stackPos)
        {
            Vector2 c = center + outward * (stackPos * FallbackGlyphStackSpacing);
            int beforeVerts = _verts[0].Count;
            EditorGeometry.AppendStraightArrowGlyph(_verts[0], _tris[0], _colors[0],
                c, forward,
                FallbackGlyphLength, FallbackGlyphShaftWidth,
                FallbackGlyphHeadLength, FallbackGlyphHeadWidth,
                Height, MarkingColor);
            PadUVs(_uvs[0], _verts[0].Count);
        }

        void EmitFallbackTurn(Vector2 center, Vector2 forward, Vector2 outward, float sideSign, int stackPos)
        {
            Vector2 c = center + outward * (stackPos * FallbackGlyphStackSpacing);
            EditorGeometry.AppendTurnArrowGlyph(_verts[0], _tris[0], _colors[0],
                c, forward, sideSign,
                FallbackGlyphLength, FallbackGlyphShaftWidth,
                FallbackGlyphHeadLength, FallbackGlyphHeadWidth,
                Height, MarkingColor);
            PadUVs(_uvs[0], _verts[0].Count);
        }

        static ArrowKind KindFromFlags(bool l, bool s, bool r)
        {
            if (l && s && r) return ArrowKind.LSR;
            if (l && s) return ArrowKind.LS;
            if (s && r) return ArrowKind.SR;
            if (l && r) return ArrowKind.LR;
            if (l) return ArrowKind.L;
            if (s) return ArrowKind.S;
            if (r) return ArrowKind.R;
            return ArrowKind.None;
        }

        VertexApproach FindApproach(string roadId)
        {
            foreach (VertexApproach a in Geometry.Approaches)
                if (a.RoadId == roadId) return a;
            return null;
        }

        // Compute the world-space endpoints of the inbound-lane block
        // along the cross-direction of the setback line. Projects each
        // inbound lane center onto crossDir (relative to OuterLeft),
        // takes the min/max, then extends by half the corresponding
        // lane's width to land on the lane's outer edge (not its
        // center). Result excludes shoulders + outbound lanes.
        static void ComputeInboundSpan(
            VertexApproach a, List<Vector2> inboundLanes, List<float> inboundWidths,
            out Vector2 leftEdge, out Vector2 rightEdge)
        {
            Vector2 crossDir = a.OuterRight - a.OuterLeft;
            float crossLen = crossDir.magnitude;
            if (crossLen < 1e-4f || inboundLanes.Count == 0)
            {
                leftEdge = a.OuterLeft;
                rightEdge = a.OuterRight;
                return;
            }
            crossDir /= crossLen;

            int minIdx = 0, maxIdx = 0;
            float minProj = float.MaxValue, maxProj = float.MinValue;
            for (int i = 0; i < inboundLanes.Count; i++)
            {
                float proj = Vector2.Dot(inboundLanes[i] - a.OuterLeft, crossDir);
                if (proj < minProj) { minProj = proj; minIdx = i; }
                if (proj > maxProj) { maxProj = proj; maxIdx = i; }
            }

            float halfMin = (inboundWidths != null && minIdx < inboundWidths.Count)
                ? inboundWidths[minIdx] * 0.5f : 1.5f;
            float halfMax = (inboundWidths != null && maxIdx < inboundWidths.Count)
                ? inboundWidths[maxIdx] * 0.5f : 1.5f;

            // Clamp so we don't accidentally extend beyond the actual
            // setback line if the math overshoots due to lane-overlap or
            // wide outer lanes.
            float leftScalar = Mathf.Max(0f, minProj - halfMin);
            float rightScalar = Mathf.Min(crossLen, maxProj + halfMax);

            leftEdge = a.OuterLeft + crossDir * leftScalar;
            rightEdge = a.OuterLeft + crossDir * rightScalar;
        }

        // Append a flat textured quad centered at `center`, oriented
        // along `forward` (forward direction = +UV-Y in the texture, so
        // an arrow image pointing UP in its image projects to "arrow
        // pointing forward" in world).
        static void AppendTexturedArrowQuad(
            List<Vector3> verts, List<int> tris, List<Color32> colors, List<Vector2> uvs,
            Vector2 center, Vector2 forward, float size, float y, Color color)
        {
            Vector2 dir = forward.normalized;
            Vector2 perp = new Vector2(dir.y, -dir.x); // CW perp (right)
            float half = size * 0.5f;
            Vector2 backLeft   = center - dir * half - perp * half;
            Vector2 backRight  = center - dir * half + perp * half;
            Vector2 frontRight = center + dir * half + perp * half;
            Vector2 frontLeft  = center + dir * half - perp * half;
            Color32 c = (Color32)color;
            int baseIdx = verts.Count;
            verts.Add(new Vector3(backLeft.x, y, backLeft.y));     colors.Add(c); uvs.Add(new Vector2(0f, 0f));
            verts.Add(new Vector3(backRight.x, y, backRight.y));   colors.Add(c); uvs.Add(new Vector2(1f, 0f));
            verts.Add(new Vector3(frontRight.x, y, frontRight.y)); colors.Add(c); uvs.Add(new Vector2(1f, 1f));
            verts.Add(new Vector3(frontLeft.x, y, frontLeft.y));   colors.Add(c); uvs.Add(new Vector2(0f, 1f));
            // CW from above for +Y normal:
            //   (frontLeft, frontRight, backRight) and (frontLeft, backRight, backLeft)
            tris.Add(baseIdx + 3); tris.Add(baseIdx + 2); tris.Add(baseIdx + 1);
            tris.Add(baseIdx + 3); tris.Add(baseIdx + 1); tris.Add(baseIdx + 0);
        }

        // Load all 7 arrow textures + create materials. Called lazily
        // on first Rebuild. Missing PNGs leave the cache slot null
        // (fallback path kicks in for that ArrowKind).
        void EnsureResources()
        {
            if (_resourcesLoaded) return;
            _resourcesLoaded = true;

            // Untextured material (slot 0).
            Shader plain = Shader.Find("NetworkDesigner/EditorOverlay");
            if (plain == null) plain = Shader.Find("Sprites/Default");
            _untexturedMat = new Material(plain) { name = "ApproachMarkingsMat" };

            // Per-kind textured materials.
            Shader textured = Shader.Find("NetworkDesigner/EditorOverlayTextured");
            _arrowTextures = new Texture2D[8];
            _texturedMats = new Material[8];
            string[] suffixes = { null, "L", "S", "R", "LS", "SR", "LR", "LSR" };
            for (int i = 1; i < 8; i++)
            {
                Texture2D tex = Resources.Load<Texture2D>($"lane-arrows/lane-arrow-{suffixes[i]}");
                _arrowTextures[i] = tex;
                if (tex != null && textured != null)
                {
                    _texturedMats[i] = new Material(textured)
                    {
                        name = $"LaneArrowMat_{suffixes[i]}",
                        mainTexture = tex,
                    };
                }
            }
        }
    }
}
