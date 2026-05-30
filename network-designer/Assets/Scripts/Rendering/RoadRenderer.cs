// Procedural mesh builder for a single straight road. Top-down flat
// quads in the XZ plane: one quad per lane, shoulder, and median.
// Five sub-meshes / materials: asphalt, shoulder, median,
// centerline (yellow), lane markings (white dashes).
//
// Coordinate convention:
//   - World up = +Y. Road lives at Y = 0.
//   - The centerline runs from EndpointA to EndpointB in the XZ plane.
//   - "right" (driver's right) = Cross(up, forward).
//
// One-way handling mirrors the React tool: when one side has zero lanes
// the entire strip stack is shifted so the asphalt midpoint sits on the
// centerline, instead of the centerline running along the asphalt edge.
//
// Lane markings:
//   - Yellow solid line at the AB/BA traffic boundary, ONLY on two-way
//     roads with no median (when there's a median, the yellow median
//     strip already separates the two flows).
//   - White dashed lines at each interior lane-to-lane boundary on each
//     side (i.e., between lane[i] and lane[i+1] within either AB or BA).
//   - Both lifted by MarkingHeight above Y=0 to avoid Z-fighting with
//     the asphalt and shoulder meshes below.
//   - For asymmetric two-way roads, the centering shift moves the
//     visual asphalt midpoint to the world centerline but the painted
//     yellow line stays at the (shifted) AB/BA boundary, not the
//     geometric middle. That matches real-world asymmetric layouts.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Geometry;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class RoadRenderer : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Cross-section profile to render. Assigned from code (e.g. RoadSceneTest).")]
        public RoadProfile Profile;

        [Header("Geometry")]
        public Vector3 EndpointA = Vector3.zero;
        public Vector3 EndpointB = new Vector3(30f, 0f, 0f);
        public DriveSide DriveSide = DriveSide.Right;

        [Header("Curve (optional — straight body when HasCurve = false)")]
        [Tooltip("If true, the road body is tessellated along a cubic bezier from EndpointA to EndpointB " +
                 "with CurveControlA / CurveControlB. Lane markings are skipped for curved roads " +
                 "(arc-length parameterization is Phase B.2).")]
        public bool HasCurve = false;
        public Vector3 CurveControlA = Vector3.zero;
        public Vector3 CurveControlB = Vector3.zero;
        [Tooltip("Number of segments used to tessellate a curved road body. Higher = smoother but more vertices.")]
        [Range(2, 64)] public int CurveTessellation = 24;

        [Header("Materials (optional — auto-created when null)")]
        public Material AsphaltMaterial;
        public Material ShoulderMaterial;
        public Material MedianMaterial;
        public Material CenterlineMaterial;
        public Material LaneMarkingMaterial;
        public Material ArrowMaterial;

        [Header("Default colors when materials are auto-created")]
        public Color AsphaltColor = new Color(0.18f, 0.18f, 0.19f);
        public Color ShoulderColor = new Color(0.55f, 0.55f, 0.55f);
        public Color MedianColor = new Color(0.95f, 0.78f, 0.25f);
        public Color CenterlineColor = new Color(0.95f, 0.78f, 0.15f);
        public Color LaneMarkingColor = new Color(0.95f, 0.95f, 0.95f);
        public Color ArrowColor = new Color(0.95f, 0.95f, 0.95f);

        [Header("UVs")]
        [Tooltip("Real-world distance (m) per UV unit. Textures repeat once every UvTileSize meters in both world-X and world-Z. Used for tiling PBR asphalt / surface textures.")]
        public float UvTileSize = 2f;

        [Header("One-way arrows")]
        [Tooltip("When true and the profile is one-way (zero lanes on one side), draw direction arrows in each active lane.")]
        public bool DrawArrows = true;
        [Tooltip("Arrow length along the travel direction (m).")]
        public float ArrowLength = 3f;
        [Tooltip("Arrow width perpendicular to travel (m).")]
        public float ArrowWidth = 1.5f;
        [Tooltip("Distance between consecutive arrows along the road (m).")]
        public float ArrowSpacing = 25f;

        [Header("Lane markings")]
        public bool DrawMarkings = true;
        [Tooltip("Painted line width (m). Typical highway markings are ~0.10-0.15m.")]
        public float LineWidth = 0.15f;
        [Tooltip("Length of each white dash (m). Typical highway = 3m.")]
        public float DashLength = 3f;
        [Tooltip("Gap between white dashes (m). Typical highway = 9m.")]
        public float DashGap = 9f;
        [Tooltip("Inset (m) between a turn lane's outer SOLID yellow line and the inner DASHED yellow line. Standard TWLTL paint puts these close together; ~0.3m reads well at typical zoom.")]
        public float TurnLaneStripeInset = 0.3f;
        [Tooltip("Vertical lift above road surface to avoid Z-fighting (m).")]
        public float MarkingHeight = 0.01f;
        [Tooltip("How far short of each setback line (each end of the road body) the markings stop. " +
                 "0 = markings extend right to the setback line. (m).")]
        public float MarkingEndInset = 0f;
        [Tooltip("Quad-strip step length (m) when emitting curved markings. Smaller = smoother but more verts. 0.5m is fine for typical road curvature.")]
        public float CurveLineSegmentLength = 0.5f;

        enum MaterialKind { Asphalt = 0, Shoulder = 1, Median = 2, Centerline = 3, LaneMarking = 4, Arrow = 5 }
        const int SubMeshCount = 6;

        struct Strip
        {
            public float LowOffset;
            public float HighOffset;
            public MaterialKind Kind;
        }

        void Start()
        {
            if (Profile != null) Rebuild();
        }

        public void Rebuild()
        {
            if (Profile == null)
            {
                Debug.LogWarning($"[RoadRenderer] No profile assigned on '{name}'; skipping rebuild.");
                return;
            }

            Vector3 forwardRaw = EndpointB - EndpointA;
            if (forwardRaw.sqrMagnitude < 1e-6f)
            {
                Debug.LogWarning($"[RoadRenderer] Zero-length centerline on '{name}'; skipping rebuild.");
                return;
            }

            // BuildStrips returns the strips AND the centering-shift midpoint
            // we applied — markings need the same midpoint to land at the
            // correct world offsets.
            BuildStrips(out List<Strip> strips, out float midpoint);

            var verts = new List<Vector3>();
            var triByKind = new List<int>[SubMeshCount];
            for (int i = 0; i < SubMeshCount; i++) triByKind[i] = new List<int>();

            if (HasCurve)
            {
                BuildCurvedStrips(strips, verts, triByKind);
                if (DrawMarkings) BuildCurvedMarkings(midpoint, verts, triByKind);
                if (DrawArrows && Profile.IsOneWay) BuildCurvedArrows(midpoint, verts, triByKind);
            }
            else
            {
                float roadLength = forwardRaw.magnitude;
                Vector3 forward = forwardRaw / roadLength;
                Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
                BuildStraightStrips(strips, forward, right, verts, triByKind);
                if (DrawMarkings) BuildStraightMarkings(forward, right, midpoint, roadLength, verts, triByKind);
                if (DrawArrows && Profile.IsOneWay)
                    BuildStraightArrows(forward, right, midpoint, roadLength, verts, triByKind);
            }

            // Reuse the existing Mesh if one was already created on this
            // GameObject. Allocating new Meshes every Rebuild during a
            // drag was a measurable cause of frame lag.
            MeshFilter mf = GetComponent<MeshFilter>();
            Mesh mesh = mf.sharedMesh;
            if (mesh == null)
            {
                mesh = new Mesh { name = $"Road_{Profile.Id ?? "anon"}" };
                mf.sharedMesh = mesh;
            }
            else
            {
                mesh.Clear();
            }
            mesh.SetVertices(verts);
            mesh.SetUVs(0, BuildWorldXZUVs(verts, UvTileSize));
            mesh.subMeshCount = SubMeshCount;
            for (int i = 0; i < SubMeshCount; i++) mesh.SetTriangles(triByKind[i], i);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            MeshRenderer mr = GetComponent<MeshRenderer>();
            // Only rebuild the material array if it's empty (first-time)
            // or its length changed. Keeps drag frames from re-allocating
            // and re-creating fallback materials every tick.
            if (mr.sharedMaterials == null || mr.sharedMaterials.Length != SubMeshCount)
            {
                mr.sharedMaterials = new[]
                {
                    AsphaltMaterial    != null ? AsphaltMaterial    : CreateFlatMaterial(AsphaltColor,    "AsphaltMat"),
                    ShoulderMaterial   != null ? ShoulderMaterial   : CreateFlatMaterial(ShoulderColor,   "ShoulderMat"),
                    MedianMaterial     != null ? MedianMaterial     : CreateFlatMaterial(MedianColor,     "MedianMat"),
                    CenterlineMaterial != null ? CenterlineMaterial : CreateFlatMaterial(CenterlineColor, "CenterlineMat"),
                    LaneMarkingMaterial!= null ? LaneMarkingMaterial: CreateFlatMaterial(LaneMarkingColor,"LaneMarkingMat"),
                    ArrowMaterial      != null ? ArrowMaterial      : CreateFlatMaterial(ArrowColor,      "ArrowMat"),
                };
            }

            // MeshCollider so the road body can be raycast-picked by the
            // designer (for right-click delete and any future road-level
            // interactions). Non-convex is fine for a static collider.
            // Force re-bake by reassigning sharedMesh.
            MeshCollider mc = GetComponent<MeshCollider>();
            if (mc == null) mc = gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = null;
            mc.sharedMesh = mesh;
        }

        // -----------------------------------------------------------------
        // Strip / marking builders
        // -----------------------------------------------------------------

        void BuildStraightStrips(List<Strip> strips, Vector3 forward, Vector3 right,
            List<Vector3> verts, List<int>[] triByKind)
        {
            foreach (Strip s in strips)
            {
                if (Mathf.Abs(s.HighOffset - s.LowOffset) < 1e-5f) continue;

                int baseIdx = verts.Count;
                verts.Add(EndpointA + right * s.LowOffset);
                verts.Add(EndpointB + right * s.LowOffset);
                verts.Add(EndpointB + right * s.HighOffset);
                verts.Add(EndpointA + right * s.HighOffset);

                List<int> tris = triByKind[(int)s.Kind];
                tris.Add(baseIdx + 0);
                tris.Add(baseIdx + 1);
                tris.Add(baseIdx + 2);
                tris.Add(baseIdx + 0);
                tris.Add(baseIdx + 2);
                tris.Add(baseIdx + 3);
            }
        }

        // Tessellate a cubic Bezier into N+1 cross-section frames (position +
        // right perpendicular) and stitch quads per strip between consecutive
        // frames. Same triangle topology / winding as the straight version,
        // just with a moving local frame.
        void BuildCurvedStrips(List<Strip> strips, List<Vector3> verts, List<int>[] triByKind)
        {
            int N = Mathf.Max(2, CurveTessellation);
            Vector3[] samplePos = new Vector3[N + 1];
            Vector3[] sampleRight = new Vector3[N + 1];

            // Bezier evaluated in 2D (XZ); body sits at EndpointA.y.
            Vector2 p0 = new Vector2(EndpointA.x, EndpointA.z);
            Vector2 c1 = new Vector2(CurveControlA.x, CurveControlA.z);
            Vector2 c2 = new Vector2(CurveControlB.x, CurveControlB.z);
            Vector2 p3 = new Vector2(EndpointB.x, EndpointB.z);
            float y = EndpointA.y;

            for (int i = 0; i <= N; i++)
            {
                float t = i / (float)N;
                Vector2 pos = GeometryResolver.SampleCubic(p0, c1, c2, p3, t);
                Vector2 tan = GeometryResolver.CubicTangent(p0, c1, c2, p3, t);
                if (tan.sqrMagnitude < 1e-8f) tan = p3 - p0;
                tan.Normalize();
                samplePos[i] = new Vector3(pos.x, y, pos.y);
                // Right perpendicular in XZ matching Vector3.Cross(up, forward):
                // for forward = (tan.x, 0, tan.y) and up = (0,1,0),
                // cross = (tan.y, 0, -tan.x).
                sampleRight[i] = new Vector3(tan.y, 0f, -tan.x);
            }

            foreach (Strip s in strips)
            {
                if (Mathf.Abs(s.HighOffset - s.LowOffset) < 1e-5f) continue;

                int baseIdx = verts.Count;
                for (int i = 0; i <= N; i++)
                {
                    verts.Add(samplePos[i] + sampleRight[i] * s.LowOffset);
                    verts.Add(samplePos[i] + sampleRight[i] * s.HighOffset);
                }

                List<int> tris = triByKind[(int)s.Kind];
                for (int i = 0; i < N; i++)
                {
                    int v0 = baseIdx + i * 2;            // low_i
                    int v1 = baseIdx + i * 2 + 1;        // high_i
                    int v2 = baseIdx + (i + 1) * 2;      // low_{i+1}
                    int v3 = baseIdx + (i + 1) * 2 + 1;  // high_{i+1}
                    // Match straight winding: corners CW from +Y are
                    // low_i → low_{i+1} → high_{i+1} → high_i.
                    tris.Add(v0); tris.Add(v2); tris.Add(v3);
                    tris.Add(v0); tris.Add(v3); tris.Add(v1);
                }
            }
        }

        // Lane markings for a straight road body. Curved-road markings are
        // Phase B.2 and skipped today.
        void BuildStraightMarkings(Vector3 forward, Vector3 right, float midpoint,
            float roadLength, List<Vector3> verts, List<int>[] triByKind)
        {
            Vector3 origin = EndpointA + Vector3.up * MarkingHeight;
            float inset = Mathf.Max(0f, MarkingEndInset);
            float startAlong = inset;
            float endAlong = roadLength - inset;

            if (endAlong <= startAlong + 1e-4f) return;

            // Yellow centerline / turn-lane edges (two-way, no median).
            //   No median + no turn lane → single yellow line at center.
            //   Turn lane → two yellow lines at the turn lane's edges.
            //   Median → no yellow marking (median fills the gap).
            if (Profile.AB.Lanes.Count > 0 && Profile.BA.Lanes.Count > 0 && Profile.Median == null)
            {
                if (Profile.TurnLane != null && Profile.TurnLane.Width > 0f)
                {
                    // Standard TWLTL paint: SOLID yellow on each outer
                    // edge of the turn lane, plus a DASHED yellow line
                    // just inside (toward the turn lane). Each side
                    // gets two parallel yellow lines.
                    float half = Profile.TurnLane.Width * 0.5f;
                    float stripeInset = TurnLaneStripeInset;
                    // Outer solid lines — at the turn lane's edges.
                    AppendSolidLine(verts, triByKind[(int)MaterialKind.Centerline],
                        forward, right, origin, -half - midpoint, startAlong, endAlong, LineWidth);
                    AppendSolidLine(verts, triByKind[(int)MaterialKind.Centerline],
                        forward, right, origin,  half - midpoint, startAlong, endAlong, LineWidth);
                    // Inner dashed lines — offset by stripeInset toward
                    // the turn lane's centerline (so the negative-side
                    // dashed line is at -half + stripeInset, etc.).
                    AppendDashedLine(verts, triByKind[(int)MaterialKind.Centerline],
                        forward, right, origin, -half + stripeInset - midpoint, startAlong, endAlong,
                        LineWidth, DashLength, DashGap);
                    AppendDashedLine(verts, triByKind[(int)MaterialKind.Centerline],
                        forward, right, origin,  half - stripeInset - midpoint, startAlong, endAlong,
                        LineWidth, DashLength, DashGap);
                }
                else
                {
                    // Single centerline at the AB/BA boundary (offset 0
                    // pre-shift = -midpoint post-shift).
                    AppendSolidLine(verts, triByKind[(int)MaterialKind.Centerline],
                        forward, right, origin, -midpoint, startAlong, endAlong, LineWidth);
                }
            }

            // White dashed interior lane boundaries.
            foreach (float offset in InteriorLaneOffsets(midpoint))
            {
                AppendDashedLine(verts, triByKind[(int)MaterialKind.LaneMarking],
                    forward, right, origin, offset, startAlong, endAlong,
                    LineWidth, DashLength, DashGap);
            }
        }

        // Curved version: arc-length-parameterized centerline + dashed
        // interior lane lines, sampled along the cubic Bezier.
        void BuildCurvedMarkings(float midpoint, List<Vector3> verts, List<int>[] triByKind)
        {
            Vector2 p0 = new Vector2(EndpointA.x, EndpointA.z);
            Vector2 c1 = new Vector2(CurveControlA.x, CurveControlA.z);
            Vector2 c2 = new Vector2(CurveControlB.x, CurveControlB.z);
            Vector2 p3 = new Vector2(EndpointB.x, EndpointB.z);
            float y = EndpointA.y + MarkingHeight;

            // Precompute a curve table once for this road. Number of samples
            // scales with chord length so very long curves stay accurate
            // and very short ones don't burn cycles.
            float chord = Vector2.Distance(p0, p3);
            int curveSamples = Mathf.Clamp(Mathf.CeilToInt(chord / 1f), 32, 256);
            CurveSample[] tbl = BuildCurveTable(p0, c1, c2, p3, curveSamples);
            float totalLen = tbl[tbl.Length - 1].CumLen;
            if (totalLen < 1e-3f) return;

            float inset = Mathf.Max(0f, MarkingEndInset);
            float startAlong = inset;
            float endAlong = totalLen - inset;
            if (endAlong <= startAlong + 1e-4f) return;

            float step = Mathf.Max(0.05f, CurveLineSegmentLength);

            // Yellow centerline / turn-lane edges (two-way, no median).
            // Same logic as the straight path — see BuildStraightMarkings.
            if (Profile.AB.Lanes.Count > 0 && Profile.BA.Lanes.Count > 0 && Profile.Median == null)
            {
                if (Profile.TurnLane != null && Profile.TurnLane.Width > 0f)
                {
                    // TWLTL: solid yellow outer edges + dashed yellow
                    // inside lines (mirroring the straight path).
                    float half = Profile.TurnLane.Width * 0.5f;
                    float stripeInset = TurnLaneStripeInset;
                    EmitCurvedLine(tbl, y, -half - midpoint, startAlong, endAlong, step,
                        LineWidth, dashed: false,
                        verts, triByKind[(int)MaterialKind.Centerline]);
                    EmitCurvedLine(tbl, y,  half - midpoint, startAlong, endAlong, step,
                        LineWidth, dashed: false,
                        verts, triByKind[(int)MaterialKind.Centerline]);
                    EmitCurvedLine(tbl, y, -half + stripeInset - midpoint, startAlong, endAlong, step,
                        LineWidth, dashed: true,
                        verts, triByKind[(int)MaterialKind.Centerline]);
                    EmitCurvedLine(tbl, y,  half - stripeInset - midpoint, startAlong, endAlong, step,
                        LineWidth, dashed: true,
                        verts, triByKind[(int)MaterialKind.Centerline]);
                }
                else
                {
                    EmitCurvedLine(tbl, y, -midpoint, startAlong, endAlong, step,
                        LineWidth, dashed: false,
                        verts, triByKind[(int)MaterialKind.Centerline]);
                }
            }

            // White dashed interior lane boundaries.
            foreach (float offset in InteriorLaneOffsets(midpoint))
            {
                EmitCurvedLine(tbl, y, offset, startAlong, endAlong, step,
                    LineWidth, dashed: true,
                    verts, triByKind[(int)MaterialKind.LaneMarking]);
            }
        }

        struct CurveSample
        {
            public Vector2 Pos;
            public Vector2 Tan; // unit
            public float CumLen;
        }

        static CurveSample[] BuildCurveTable(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, int samples)
        {
            CurveSample[] tbl = new CurveSample[samples + 1];
            Vector2 prev = p0;
            Vector2 initTan = GeometryResolver.CubicTangent(p0, c1, c2, p3, 0f);
            if (initTan.sqrMagnitude < 1e-8f) initTan = p3 - p0;
            tbl[0].Pos = p0;
            tbl[0].Tan = initTan.normalized;
            tbl[0].CumLen = 0f;
            for (int i = 1; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector2 pos = GeometryResolver.SampleCubic(p0, c1, c2, p3, t);
                Vector2 tan = GeometryResolver.CubicTangent(p0, c1, c2, p3, t);
                if (tan.sqrMagnitude < 1e-8f) tan = pos - prev;
                tbl[i].Pos = pos;
                tbl[i].Tan = tan.normalized;
                tbl[i].CumLen = tbl[i - 1].CumLen + Vector2.Distance(prev, pos);
                prev = pos;
            }
            return tbl;
        }

        // Linear interp lookup of position + tangent at arc length `along`.
        static void SampleAtArcLength(CurveSample[] tbl, float along, out Vector2 pos, out Vector2 tan)
        {
            int n = tbl.Length;
            if (along <= 0f) { pos = tbl[0].Pos; tan = tbl[0].Tan; return; }
            float endLen = tbl[n - 1].CumLen;
            if (along >= endLen) { pos = tbl[n - 1].Pos; tan = tbl[n - 1].Tan; return; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (tbl[mid].CumLen <= along) lo = mid; else hi = mid;
            }
            float segLen = tbl[hi].CumLen - tbl[lo].CumLen;
            float frac = segLen > 1e-6f ? (along - tbl[lo].CumLen) / segLen : 0f;
            pos = Vector2.Lerp(tbl[lo].Pos, tbl[hi].Pos, frac);
            Vector2 t = Vector2.Lerp(tbl[lo].Tan, tbl[hi].Tan, frac);
            tan = t.sqrMagnitude < 1e-8f ? tbl[lo].Tan : t.normalized;
        }

        // Walks the curve in `step` increments, emitting connected-quad
        // strip segments for the marking line. For dashed lines, segments
        // inside the gap phase are skipped (and the strip seam is reset
        // so the next dash starts a fresh strip).
        void EmitCurvedLine(CurveSample[] tbl, float y, float lateralOffset,
            float startAlong, float endAlong, float step, float lineWidth,
            bool dashed,
            List<Vector3> verts, List<int> tris)
        {
            float halfW = lineWidth * 0.5f;
            float period = DashLength + DashGap;
            int prevLowIdx = -1, prevHighIdx = -1;
            float along = startAlong;

            while (along < endAlong - 1e-5f)
            {
                float nextAlong = Mathf.Min(along + step, endAlong);

                bool emit = true;
                if (dashed)
                {
                    // Sample at segment midpoint so quads that straddle a
                    // dash/gap boundary go to whichever phase covers most
                    // of the segment.
                    float mid = (along + nextAlong) * 0.5f - startAlong;
                    float phase = mid - Mathf.Floor(mid / period) * period;
                    emit = phase < DashLength;
                }

                if (!emit)
                {
                    prevLowIdx = prevHighIdx = -1;
                    along = nextAlong;
                    continue;
                }

                // Start of a new strip — emit the two seed vertices at `along`.
                if (prevLowIdx < 0)
                {
                    SampleAtArcLength(tbl, along, out Vector2 posS, out Vector2 tanS);
                    Vector2 perpS = new Vector2(tanS.y, -tanS.x);
                    Vector2 a = posS + perpS * (lateralOffset - halfW);
                    Vector2 b = posS + perpS * (lateralOffset + halfW);
                    prevLowIdx = verts.Count;
                    verts.Add(new Vector3(a.x, y, a.y));
                    prevHighIdx = verts.Count;
                    verts.Add(new Vector3(b.x, y, b.y));
                }

                SampleAtArcLength(tbl, nextAlong, out Vector2 posE, out Vector2 tanE);
                Vector2 perpE = new Vector2(tanE.y, -tanE.x);
                Vector2 c = posE + perpE * (lateralOffset - halfW);
                Vector2 d = posE + perpE * (lateralOffset + halfW);
                int curLowIdx = verts.Count;
                verts.Add(new Vector3(c.x, y, c.y));
                int curHighIdx = verts.Count;
                verts.Add(new Vector3(d.x, y, d.y));

                // Winding matches the rest of the road mesh: CW from +Y so
                // the normal points up under default backface culling.
                tris.Add(prevLowIdx);  tris.Add(curLowIdx);   tris.Add(curHighIdx);
                tris.Add(prevLowIdx);  tris.Add(curHighIdx);  tris.Add(prevHighIdx);

                prevLowIdx = curLowIdx;
                prevHighIdx = curHighIdx;
                along = nextAlong;
            }
        }

        // -----------------------------------------------------------------
        // One-way direction arrows
        // -----------------------------------------------------------------

        // Determine which travel direction has lanes (only valid on one-way
        // profiles — caller checks Profile.IsOneWay first).
        Direction OneWayTravelDirection()
        {
            return Profile.AB.Lanes.Count > 0 ? Direction.AB : Direction.BA;
        }

        // Cross-section offsets for the centers of each lane in the given
        // travel direction, with the centering shift applied.
        List<float> ComputeLaneCenters(Direction dir, float midpoint)
        {
            List<float> centers = new List<float>();
            int sign = SpatialOffsetSign(dir);
            float centerHalf = Profile.CenterStripWidth * 0.5f;
            List<Lane> lanes = dir == Direction.AB ? Profile.AB.Lanes : Profile.BA.Lanes;
            float cursor = sign * centerHalf;
            foreach (Lane l in lanes)
            {
                float center = cursor + sign * (l.Width * 0.5f) - midpoint;
                centers.Add(center);
                cursor += sign * l.Width;
            }
            return centers;
        }

        void BuildStraightArrows(Vector3 forward, Vector3 right, float midpoint,
            float roadLength, List<Vector3> verts, List<int>[] triByKind)
        {
            Direction travelDir = OneWayTravelDirection();
            Vector3 arrowForward = travelDir == Direction.AB ? forward : -forward;
            List<float> centers = ComputeLaneCenters(travelDir, midpoint);
            if (centers.Count == 0) return;

            // Arrows lift a hair above other markings so they don't z-fight
            // with the asphalt sub-mesh that sits at body Y.
            Vector3 origin = EndpointA + Vector3.up * (MarkingHeight + 0.001f);

            float inset = Mathf.Max(0f, MarkingEndInset);
            float startAlong = inset + ArrowSpacing * 0.5f;
            float endAlong = roadLength - inset;
            if (endAlong <= startAlong) return;

            for (float along = startAlong; along < endAlong; along += ArrowSpacing)
            {
                Vector3 alongPos = origin + forward * along;
                foreach (float laneCenter in centers)
                {
                    Vector3 center = alongPos + right * laneCenter;
                    AppendArrow(verts, triByKind[(int)MaterialKind.Arrow],
                        center, arrowForward, right, ArrowLength, ArrowWidth);
                }
            }
        }

        // Curved version: walks arc length along the sub-curve in
        // ArrowSpacing steps. Each step samples curve position + local
        // tangent so arrows align with the road body at that point.
        void BuildCurvedArrows(float midpoint, List<Vector3> verts, List<int>[] triByKind)
        {
            Direction travelDir = OneWayTravelDirection();
            List<float> centers = ComputeLaneCenters(travelDir, midpoint);
            if (centers.Count == 0) return;

            // Sub-curve as 2D (XZ).
            Vector2 p0 = new Vector2(EndpointA.x, EndpointA.z);
            Vector2 c1 = new Vector2(CurveControlA.x, CurveControlA.z);
            Vector2 c2 = new Vector2(CurveControlB.x, CurveControlB.z);
            Vector2 p3 = new Vector2(EndpointB.x, EndpointB.z);
            float y = EndpointA.y + MarkingHeight + 0.001f;

            // Total arc length (sample dense enough for stable spacing).
            const int LEN_SAMPLES = 32;
            float totalLen = 0f;
            Vector2 prev = p0;
            for (int i = 1; i <= LEN_SAMPLES; i++)
            {
                Vector2 curr = GeometryResolver.SampleCubic(p0, c1, c2, p3, i / (float)LEN_SAMPLES);
                totalLen += Vector2.Distance(prev, curr);
                prev = curr;
            }
            if (totalLen < 1e-3f) return;

            float inset = Mathf.Max(0f, MarkingEndInset);
            float startAlong = inset + ArrowSpacing * 0.5f;
            float endAlong = totalLen - inset;
            if (endAlong <= startAlong) return;

            int abSign = travelDir == Direction.AB ? 1 : -1;

            for (float along = startAlong; along < endAlong; along += ArrowSpacing)
            {
                float t = GeometryResolver.ArcLengthToT(p0, c1, c2, p3, along);
                Vector2 pos = GeometryResolver.SampleCubic(p0, c1, c2, p3, t);
                Vector2 tan = GeometryResolver.CubicTangent(p0, c1, c2, p3, t);
                if (tan.sqrMagnitude < 1e-8f) tan = p3 - p0;
                tan.Normalize();

                Vector3 forward = new Vector3(tan.x, 0f, tan.y);
                Vector3 right = new Vector3(tan.y, 0f, -tan.x); // PerpRight
                Vector3 arrowForward = forward * abSign;
                Vector3 alongPos = new Vector3(pos.x, y, pos.y);
                foreach (float laneCenter in centers)
                {
                    Vector3 center = alongPos + right * laneCenter;
                    AppendArrow(verts, triByKind[(int)MaterialKind.Arrow],
                        center, arrowForward, right, ArrowLength, ArrowWidth);
                }
            }
        }

        // Single filled-triangle arrowhead at `center`, pointing along
        // `forward`. Triangle order matches the marking quads' CW-from-+Y
        // winding so the normal points up and the arrow is visible from
        // above with default back-face culling.
        static void AppendArrow(List<Vector3> verts, List<int> tris,
            Vector3 center, Vector3 forward, Vector3 right,
            float length, float width)
        {
            Vector3 halfFwd = forward * (length * 0.5f);
            Vector3 halfRight = right * (width * 0.5f);
            int baseIdx = verts.Count;
            verts.Add(center + halfFwd);              // 0 tip
            verts.Add(center - halfFwd - halfRight);  // 1 back, -right side
            verts.Add(center - halfFwd + halfRight);  // 2 back, +right side
            // 0-2-1 (not 0-1-2) so the normal cross product points +Y.
            tris.Add(baseIdx + 0);
            tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 1);
        }

        void BuildStrips(out List<Strip> strips, out float midpoint)
        {
            strips = new List<Strip>();
            float centerHalf = Profile.CenterStripWidth * 0.5f;

            int signAB = SpatialOffsetSign(Direction.AB);
            int signBA = SpatialOffsetSign(Direction.BA);

            float cursorAB = signAB * centerHalf;
            foreach (Lane l in Profile.AB.Lanes)
            {
                float next = cursorAB + signAB * l.Width;
                AddStrip(strips, cursorAB, next, MaterialKind.Asphalt);
                cursorAB = next;
            }
            float abOuter = cursorAB + signAB * Profile.ShoulderAB.Width;
            AddStrip(strips, cursorAB, abOuter, MaterialKind.Shoulder);

            float cursorBA = signBA * centerHalf;
            foreach (Lane l in Profile.BA.Lanes)
            {
                float next = cursorBA + signBA * l.Width;
                AddStrip(strips, cursorBA, next, MaterialKind.Asphalt);
                cursorBA = next;
            }
            float baOuter = cursorBA + signBA * Profile.ShoulderBA.Width;
            AddStrip(strips, cursorBA, baOuter, MaterialKind.Shoulder);

            // Center strip. Median = grass (non-drivable). Turn lane =
            // asphalt (drivable). Both only meaningful on two-way roads.
            if (!Profile.IsOneWay && centerHalf > 0f)
            {
                MaterialKind kind = Profile.Median != null
                    ? MaterialKind.Median
                    : MaterialKind.Asphalt;
                AddStrip(strips, -centerHalf, centerHalf, kind);
            }

            midpoint = (abOuter + baOuter) * 0.5f;
            if (Mathf.Abs(midpoint) > 1e-5f)
            {
                for (int i = 0; i < strips.Count; i++)
                {
                    Strip s = strips[i];
                    s.LowOffset -= midpoint;
                    s.HighOffset -= midpoint;
                    strips[i] = s;
                }
            }
        }

        // Interior lane-to-lane boundaries (one less per side than lane count).
        // Returned offsets are in the FINAL world frame — i.e. with the
        // centering shift already applied.
        IEnumerable<float> InteriorLaneOffsets(float midpoint)
        {
            float centerHalf = Profile.CenterStripWidth * 0.5f;
            int signAB = SpatialOffsetSign(Direction.AB);
            int signBA = SpatialOffsetSign(Direction.BA);

            float cursor = signAB * centerHalf;
            int abCount = Profile.AB.Lanes.Count;
            for (int i = 0; i < abCount; i++)
            {
                cursor += signAB * Profile.AB.Lanes[i].Width;
                if (i < abCount - 1) yield return cursor - midpoint;
            }

            cursor = signBA * centerHalf;
            int baCount = Profile.BA.Lanes.Count;
            for (int i = 0; i < baCount; i++)
            {
                cursor += signBA * Profile.BA.Lanes[i].Width;
                if (i < baCount - 1) yield return cursor - midpoint;
            }
        }

        // Appends a single rectangular quad spanning [startAlong, endAlong]
        // along the road at the given perpendicular offset and width.
        // `origin` is the world-space anchor (typically EndpointA + lift).
        static void AppendSolidLine(List<Vector3> verts, List<int> tris,
            Vector3 forward, Vector3 right, Vector3 origin,
            float offset, float startAlong, float endAlong, float lineWidth)
        {
            AppendQuad(verts, tris,
                origin: origin,
                forward: forward,
                right: right,
                startAlong: startAlong,
                endAlong: endAlong,
                offset: offset,
                lineWidth: lineWidth);
        }

        // Appends a sequence of short quads (one per dash) starting at
        // startAlong, spaced by dashLength + dashGap, never extending past
        // endAlong.
        static void AppendDashedLine(List<Vector3> verts, List<int> tris,
            Vector3 forward, Vector3 right, Vector3 origin,
            float offset, float startAlong, float endAlong,
            float lineWidth, float dashLength, float dashGap)
        {
            if (dashLength <= 0f) return;
            float period = dashLength + Mathf.Max(0f, dashGap);
            float position = startAlong;
            int safety = 0;
            while (position < endAlong && safety < 100000)
            {
                float ds = position;
                float de = Mathf.Min(position + dashLength, endAlong);
                if (de > ds)
                {
                    AppendQuad(verts, tris,
                        origin: origin,
                        forward: forward,
                        right: right,
                        startAlong: ds,
                        endAlong: de,
                        offset: offset,
                        lineWidth: lineWidth);
                }
                position += period;
                safety++;
            }
        }

        // Single quad: 4 vertices + 2 triangles. Origin lets us lift the
        // marking off the road surface; the quad sits at
        //   origin + forward·(startAlong..endAlong) + right·(offset ± lineWidth/2).
        static void AppendQuad(List<Vector3> verts, List<int> tris,
            Vector3 origin, Vector3 forward, Vector3 right,
            float startAlong, float endAlong,
            float offset, float lineWidth)
        {
            float half = lineWidth * 0.5f;
            int baseIdx = verts.Count;
            // CW winding from +Y → front face up. Vertex order:
            // [start_low, end_low, end_high, start_high].
            verts.Add(origin + forward * startAlong + right * (offset - half));
            verts.Add(origin + forward * endAlong + right * (offset - half));
            verts.Add(origin + forward * endAlong + right * (offset + half));
            verts.Add(origin + forward * startAlong + right * (offset + half));

            tris.Add(baseIdx + 0);
            tris.Add(baseIdx + 1);
            tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 0);
            tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 3);
        }

        static void AddStrip(List<Strip> strips, float a, float b, MaterialKind kind)
        {
            strips.Add(new Strip
            {
                LowOffset = Mathf.Min(a, b),
                HighOffset = Mathf.Max(a, b),
                Kind = kind,
            });
        }

        int SpatialOffsetSign(Direction d)
        {
            if (DriveSide == DriveSide.Right) return d == Direction.AB ? 1 : -1;
            return d == Direction.AB ? -1 : 1;
        }

        // World-aligned tiled UVs: U = world X / tileSize, V = world Z / tileSize.
        // Since the road body sits in the XZ plane, this gives consistent
        // tiling regardless of road orientation and tile-count along a
        // road that matches its physical length.
        static List<Vector2> BuildWorldXZUVs(List<Vector3> verts, float tileSize)
        {
            float s = 1f / Mathf.Max(0.001f, tileSize);
            List<Vector2> uvs = new List<Vector2>(verts.Count);
            for (int i = 0; i < verts.Count; i++)
            {
                uvs.Add(new Vector2(verts[i].x * s, verts[i].z * s));
            }
            return uvs;
        }

        static Material CreateFlatMaterial(Color c, string name)
        {
            // Standard shader so the surface picks up directional + ambient
            // lighting and casts/receives shadows. Glossiness 0 + metallic 0
            // keeps the look matte (asphalt-ish) regardless of color.
            Shader sh = Shader.Find("Standard");
            Material m = new Material(sh) { name = name, color = c };
            m.SetFloat("_Glossiness", 0f);
            m.SetFloat("_Metallic", 0f);
            return m;
        }
    }
}
