// Mesh builders shared by Edit-mode UI overlays — setback handle rings,
// lane endpoint marker rings, lane-flow arrows, dashed connector stems.
//
// All shapes are flat in the XZ plane at a constant Y. Triangle winding
// is CW viewed from above (+Y) so the outward normal points +Y and the
// orbit camera (which lives above the ground plane) sees the front
// face. Color32 per vertex carries alpha so hover/select state can be
// updated by overwriting just the color array on the Mesh — no need
// to rebuild verts/tris.
//
// All Append* methods take grow-lists (verts, tris, colors) and append
// to them; this lets a single Mesh be built from many primitives in
// one allocation. Callers should reset/clear the lists between Rebuilds.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Geometry;

namespace NetworkDesigner.Designer
{
    public static class EditorGeometry
    {
        /// <summary>
        /// Append a flat annulus (hollow ring) to the given mesh buffers.
        /// Outer radius outerR, inner radius innerR, tessellated into
        /// <paramref name="segments"/> circumferential steps (32 reads
        /// smooth at typical edit-mode zoom). Lies at Y, CW from above.
        /// </summary>
        public static void AppendRing(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 center, float outerR, float innerR, int segments, float y, Color color)
        {
            if (segments < 8) segments = 8;
            if (innerR >= outerR) innerR = outerR * 0.5f;
            Color32 c = (Color32)color;
            int baseIdx = verts.Count;

            // 2*segments vertices: outer_i, inner_i, outer_{i+1}, inner_{i+1}, …
            // Layout: [outer_0, inner_0, outer_1, inner_1, … outer_{n-1}, inner_{n-1}]
            for (int i = 0; i < segments; i++)
            {
                float a = (i / (float)segments) * 2f * Mathf.PI;
                float cs = Mathf.Cos(a), sn = Mathf.Sin(a);
                verts.Add(new Vector3(center.x + outerR * cs, y, center.y + outerR * sn));
                colors.Add(c);
                verts.Add(new Vector3(center.x + innerR * cs, y, center.y + innerR * sn));
                colors.Add(c);
            }

            for (int i = 0; i < segments; i++)
            {
                int o0 = baseIdx + i * 2;
                int i0 = baseIdx + i * 2 + 1;
                int o1 = baseIdx + ((i + 1) % segments) * 2;
                int i1 = baseIdx + ((i + 1) % segments) * 2 + 1;
                // CW from above for +Y normal:
                //   (o0, i0, o1) and (o1, i0, i1)
                tris.Add(o0); tris.Add(i0); tris.Add(o1);
                tris.Add(o1); tris.Add(i0); tris.Add(i1);
            }
        }

        /// <summary>
        /// Append a dashed straight line from <paramref name="from"/> to
        /// <paramref name="to"/> as a sequence of small quads. Each dash
        /// is <paramref name="dashLen"/> long with <paramref name="gapLen"/>
        /// gaps between. Width <paramref name="lineWidth"/> measured
        /// perpendicular to the line.
        /// </summary>
        public static void AppendDashedLine(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 from, Vector2 to, float lineWidth, float dashLen, float gapLen, float y, Color color)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 1e-4f) return;
            Vector2 dir = delta / length;
            Vector2 perp = new Vector2(dir.y, -dir.x); // CW perpendicular
            float step = dashLen + gapLen;
            float t = 0f;
            float halfW = lineWidth * 0.5f;
            Color32 c = (Color32)color;
            while (t < length - 1e-4f)
            {
                float t0 = t;
                float t1 = Mathf.Min(t + dashLen, length);
                Vector2 a = from + dir * t0;
                Vector2 b = from + dir * t1;
                AppendQuadInternal(verts, tris, colors, a, b, perp, halfW, y, c);
                t += step;
            }
        }

        /// <summary>
        /// Append a dashed arrow: dashed-line shaft from
        /// <paramref name="from"/> toward <paramref name="to"/>, stopping
        /// <paramref name="headLen"/> short, then a filled triangle
        /// arrowhead at <paramref name="to"/>. Returns the vertex range
        /// (firstVertexIndex + vertexCount) so callers can rewrite alpha
        /// in place on just this arrow.
        /// </summary>
        public static void AppendDashedArrow(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 from, Vector2 to,
            float shaftWidth, float dashLen, float gapLen,
            float headLen, float headWidth, float y, Color color,
            out int firstVertexIndex, out int vertexCount)
        {
            firstVertexIndex = verts.Count;
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 1e-4f) { vertexCount = 0; return; }
            Vector2 dir = delta / length;
            Vector2 perp = new Vector2(dir.y, -dir.x);
            float effShaft = Mathf.Max(0f, length - headLen);
            Vector2 shaftEnd = from + dir * effShaft;

            // Dashes along the shaft.
            float step = dashLen + gapLen;
            float t = 0f;
            float halfW = shaftWidth * 0.5f;
            Color32 c = (Color32)color;
            while (t < effShaft - 1e-4f)
            {
                float t0 = t;
                float t1 = Mathf.Min(t + dashLen, effShaft);
                Vector2 a = from + dir * t0;
                Vector2 b = from + dir * t1;
                AppendQuadInternal(verts, tris, colors, a, b, perp, halfW, y, c);
                t += step;
            }

            // Arrowhead triangle: tip at `to`, back corners at shaftEnd
            // ± perp*headWidth/2. CW from above.
            int tipIdx = verts.Count;
            float halfHead = headWidth * 0.5f;
            Vector2 backLeft = shaftEnd + perp * halfHead;
            Vector2 backRight = shaftEnd - perp * halfHead;
            verts.Add(new Vector3(to.x, y, to.y));        colors.Add(c);
            verts.Add(new Vector3(backLeft.x, y, backLeft.y)); colors.Add(c);
            verts.Add(new Vector3(backRight.x, y, backRight.y)); colors.Add(c);
            // Winding (tip, backLeft, backRight) gives +Y normal.
            tris.Add(tipIdx); tris.Add(tipIdx + 1); tris.Add(tipIdx + 2);

            vertexCount = verts.Count - firstVertexIndex;
        }

        /// <summary>
        /// Append a dashed arrow that follows a cubic bezier from p0 to
        /// p3 with control points p1, p2. Shaft is sampled into N
        /// polyline segments and dashes are laid along the polyline by
        /// arc length. Arrowhead at p3 oriented along the bezier's
        /// tangent at t=1. Returns the vertex range so callers can
        /// rewrite alpha in place.
        /// </summary>
        public static void AppendDashedBezierArrow(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
            float shaftWidth, float dashLen, float gapLen,
            float headLen, float headWidth, float y, Color color,
            int sampleCount,
            out int firstVertexIndex, out int vertexCount)
        {
            firstVertexIndex = verts.Count;
            if (sampleCount < 8) sampleCount = 8;
            Color32 c = (Color32)color;

            // Sample the bezier into a polyline.
            Vector2[] pts = new Vector2[sampleCount + 1];
            for (int i = 0; i <= sampleCount; i++)
            {
                float t = i / (float)sampleCount;
                pts[i] = GeometryResolver.SampleCubic(p0, p1, p2, p3, t);
            }

            // Cumulative arc length along the polyline.
            float[] cum = new float[sampleCount + 1];
            for (int i = 1; i <= sampleCount; i++)
            {
                cum[i] = cum[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
            }
            float totalLen = cum[sampleCount];
            float shaftEnd = Mathf.Max(0f, totalLen - headLen);
            float halfW = shaftWidth * 0.5f;
            float step = dashLen + gapLen;

            // Lay dashes along the polyline by arc-length intervals.
            float pos = 0f;
            while (pos < shaftEnd - 1e-4f)
            {
                float d0 = pos;
                float d1 = Mathf.Min(pos + dashLen, shaftEnd);
                AppendPolylineDashRibbon(verts, tris, colors, pts, cum, d0, d1, halfW, y, c);
                pos += step;
            }

            // Arrowhead: tangent at end derived from the bezier near t=1.
            Vector2 tail = GeometryResolver.SampleCubic(p0, p1, p2, p3, 0.99f);
            Vector2 finalDir = (p3 - tail);
            if (finalDir.sqrMagnitude < 1e-6f) finalDir = (p3 - p2);
            if (finalDir.sqrMagnitude < 1e-6f) finalDir = (p3 - p0);
            if (finalDir.sqrMagnitude < 1e-6f) { vertexCount = verts.Count - firstVertexIndex; return; }
            finalDir.Normalize();
            Vector2 perpFinal = new Vector2(finalDir.y, -finalDir.x);
            Vector2 backCenter = p3 - finalDir * headLen;
            float halfHead = headWidth * 0.5f;
            Vector2 backLeft = backCenter + perpFinal * halfHead;
            Vector2 backRight = backCenter - perpFinal * halfHead;
            int tipIdx = verts.Count;
            verts.Add(new Vector3(p3.x, y, p3.y));        colors.Add(c);
            verts.Add(new Vector3(backLeft.x, y, backLeft.y)); colors.Add(c);
            verts.Add(new Vector3(backRight.x, y, backRight.y)); colors.Add(c);
            tris.Add(tipIdx); tris.Add(tipIdx + 1); tris.Add(tipIdx + 2);

            vertexCount = verts.Count - firstVertexIndex;
        }

        // Lay a ribbon of quads along [startD, endD] of the polyline
        // defined by pts/cum. One quad per overlapping polyline segment
        // (with clipping to the requested arc-length window).
        static void AppendPolylineDashRibbon(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2[] pts, float[] cum, float startD, float endD,
            float halfW, float y, Color32 c)
        {
            for (int i = 0; i < pts.Length - 1; i++)
            {
                float segStart = cum[i];
                float segEnd = cum[i + 1];
                if (segEnd <= startD) continue;
                if (segStart >= endD) break;
                float segLen = segEnd - segStart;
                if (segLen < 1e-6f) continue;
                float t0 = Mathf.Max(0f, (startD - segStart) / segLen);
                float t1 = Mathf.Min(1f, (endD - segStart) / segLen);
                Vector2 a = Vector2.Lerp(pts[i], pts[i + 1], t0);
                Vector2 b = Vector2.Lerp(pts[i], pts[i + 1], t1);
                Vector2 dir = pts[i + 1] - pts[i];
                if (dir.sqrMagnitude < 1e-6f) continue;
                dir.Normalize();
                Vector2 perp = new Vector2(dir.y, -dir.x);
                AppendQuadInternal(verts, tris, colors, a, b, perp, halfW, y, c);
            }
        }

        // Helper: emit one quad spanning (a → b) with `perp` perpendicular
        // and half-width `halfW`. CW from above.
        static void AppendQuadInternal(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 a, Vector2 b, Vector2 perp, float halfW, float y, Color32 c)
        {
            Vector2 v0 = a + perp * halfW;   // start-right
            Vector2 v1 = b + perp * halfW;   // end-right
            Vector2 v2 = b - perp * halfW;   // end-left
            Vector2 v3 = a - perp * halfW;   // start-left
            int baseIdx = verts.Count;
            verts.Add(new Vector3(v0.x, y, v0.y)); colors.Add(c);
            verts.Add(new Vector3(v1.x, y, v1.y)); colors.Add(c);
            verts.Add(new Vector3(v2.x, y, v2.y)); colors.Add(c);
            verts.Add(new Vector3(v3.x, y, v3.y)); colors.Add(c);
            // Winding (v0, v3, v2) and (v0, v2, v1) — CW from above.
            tris.Add(baseIdx + 0); tris.Add(baseIdx + 3); tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 0); tris.Add(baseIdx + 2); tris.Add(baseIdx + 1);
        }

        /// <summary>
        /// Append a stop line: thin rectangle spanning leftEdge → rightEdge
        /// (the setback line endpoints), extruded by lineWidth along
        /// outward (so it sits flat along the setback and has a small
        /// thickness toward the road body).
        /// </summary>
        public static void AppendStopLine(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 leftEdge, Vector2 rightEdge, Vector2 outward,
            float lineWidth, float insetFromSetback, float y, Color color)
        {
            if (outward.sqrMagnitude < 1e-6f) return;
            Vector2 dir = outward.normalized;
            // Inset INTO the road body by insetFromSetback so the line
            // doesn't sit exactly on the setback (where the intersection
            // mesh starts — would z-fight).
            Vector2 a = leftEdge + dir * insetFromSetback;
            Vector2 b = rightEdge + dir * insetFromSetback;
            Vector2 perp = (b - a).normalized;
            // The "width" of the stop line is in the OUTWARD direction
            // (toward the road body) — picture a paint stripe perp to
            // traffic, with thickness = lineWidth.
            Vector2 along = dir * (lineWidth * 0.5f);
            Color32 c = (Color32)color;
            int baseIdx = verts.Count;
            verts.Add(new Vector3(a.x - along.x, y, a.y - along.y)); colors.Add(c);
            verts.Add(new Vector3(b.x - along.x, y, b.y - along.y)); colors.Add(c);
            verts.Add(new Vector3(b.x + along.x, y, b.y + along.y)); colors.Add(c);
            verts.Add(new Vector3(a.x + along.x, y, a.y + along.y)); colors.Add(c);
            // CW from above (winding to put normal +Y).
            tris.Add(baseIdx + 0); tris.Add(baseIdx + 3); tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 0); tris.Add(baseIdx + 2); tris.Add(baseIdx + 1);
        }

        /// <summary>
        /// Append a row of "sharks teeth" yield triangles across the
        /// setback line. Triangle bases lie along the line; apices point
        /// -outward (into the intersection, in the direction of travel).
        /// Triangles repeat with spacing along the cross-direction.
        /// </summary>
        public static void AppendYieldTriangles(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 leftEdge, Vector2 rightEdge, Vector2 outward,
            float triangleBase, float triangleHeight, float triangleSpacing,
            float insetFromSetback, float y, Color color)
        {
            if (outward.sqrMagnitude < 1e-6f) return;
            Vector2 dir = outward.normalized;
            Vector2 a = leftEdge + dir * insetFromSetback;
            Vector2 b = rightEdge + dir * insetFromSetback;
            Vector2 along = b - a;
            float span = along.magnitude;
            if (span < 1e-4f) return;
            Vector2 alongDir = along / span;
            float step = triangleBase + triangleSpacing;
            int count = Mathf.Max(1, Mathf.FloorToInt((span + triangleSpacing) / step));
            // Center the row of triangles within [a, b].
            float used = count * step - triangleSpacing;
            float startOffset = (span - used) * 0.5f;
            Color32 c = (Color32)color;
            for (int i = 0; i < count; i++)
            {
                float baseStart = startOffset + i * step;
                Vector2 baseL = a + alongDir * baseStart;
                Vector2 baseR = a + alongDir * (baseStart + triangleBase);
                // Apex points along +outward (into the road body),
                // toward the approaching driver. Sharks-teeth markings
                // face oncoming traffic: bases on the intersection side,
                // points aimed at the driver.
                Vector2 apex = (baseL + baseR) * 0.5f + dir * triangleHeight;
                int baseIdx = verts.Count;
                verts.Add(new Vector3(baseL.x, y, baseL.y)); colors.Add(c);
                verts.Add(new Vector3(baseR.x, y, baseR.y)); colors.Add(c);
                verts.Add(new Vector3(apex.x, y, apex.y));   colors.Add(c);
                // CW from above: (baseL, apex, baseR) puts normal +Y.
                tris.Add(baseIdx + 0); tris.Add(baseIdx + 2); tris.Add(baseIdx + 1);
            }
        }

        /// <summary>
        /// Append a painted straight-arrow glyph centered at
        /// <paramref name="center"/>, pointing along
        /// <paramref name="forward"/> (the direction of travel). Shaft
        /// is a rectangle along forward; arrowhead is a triangle at the
        /// front.
        /// </summary>
        public static void AppendStraightArrowGlyph(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 center, Vector2 forward,
            float length, float shaftWidth, float headLen, float headWidth,
            float y, Color color)
        {
            if (forward.sqrMagnitude < 1e-6f) return;
            Vector2 dir = forward.normalized;
            Vector2 perp = new Vector2(dir.y, -dir.x);
            float halfLen = length * 0.5f;
            // Shaft: from center - dir*halfLen to center + dir*(halfLen - headLen)
            Vector2 shaftStart = center - dir * halfLen;
            Vector2 shaftEnd = center + dir * (halfLen - headLen);
            AppendQuadInternal(verts, tris, colors, shaftStart, shaftEnd, perp, shaftWidth * 0.5f, y, (Color32)color);
            // Arrowhead.
            Vector2 tip = center + dir * halfLen;
            Vector2 backLeft = shaftEnd + perp * (headWidth * 0.5f);
            Vector2 backRight = shaftEnd - perp * (headWidth * 0.5f);
            Color32 c = (Color32)color;
            int baseIdx = verts.Count;
            verts.Add(new Vector3(tip.x, y, tip.y));            colors.Add(c);
            verts.Add(new Vector3(backLeft.x, y, backLeft.y));  colors.Add(c);
            verts.Add(new Vector3(backRight.x, y, backRight.y)); colors.Add(c);
            tris.Add(baseIdx + 0); tris.Add(baseIdx + 1); tris.Add(baseIdx + 2);
        }

        /// <summary>
        /// Append an L-shaped painted turn-arrow glyph centered at
        /// <paramref name="center"/>, pointing along
        /// <paramref name="forward"/> with the turn going to one side
        /// (sideSign +1 for right turn, -1 for left turn). The glyph is
        /// an inbound shaft along forward, an outbound shaft perpendicular
        /// to it, and a triangular arrowhead at the outbound end.
        /// </summary>
        public static void AppendTurnArrowGlyph(
            List<Vector3> verts, List<int> tris, List<Color32> colors,
            Vector2 center, Vector2 forward, float sideSign,
            float length, float shaftWidth, float headLen, float headWidth,
            float y, Color color)
        {
            if (forward.sqrMagnitude < 1e-6f) return;
            Vector2 dir = forward.normalized;
            Vector2 perp = new Vector2(dir.y, -dir.x) * sideSign; // sideSign>0 → CW-right of forward
            float halfLen = length * 0.5f;
            // Inbound shaft (back portion): along -forward from center.
            Vector2 inStart = center - dir * halfLen;
            Vector2 corner = center - dir * (halfLen * 0.25f); // bend point
            AppendQuadInternal(verts, tris, colors, inStart, corner, perp, shaftWidth * 0.5f, y, (Color32)color);
            // Outbound shaft (perpendicular): from corner toward perp side,
            // stopping headLen short of the tip.
            float outLen = halfLen + (halfLen * 0.25f); // total turn length
            Vector2 outEndShaft = corner + perp * (outLen - headLen);
            AppendQuadInternal(verts, tris, colors, corner, outEndShaft, dir, shaftWidth * 0.5f, y, (Color32)color);
            // Arrowhead at outbound tip, pointing along perp.
            Vector2 tip = corner + perp * outLen;
            Vector2 backLeft = outEndShaft + dir * (headWidth * 0.5f);
            Vector2 backRight = outEndShaft - dir * (headWidth * 0.5f);
            Color32 c = (Color32)color;
            int baseIdx = verts.Count;
            verts.Add(new Vector3(tip.x, y, tip.y));            colors.Add(c);
            // Wind so normal is +Y regardless of sideSign.
            if (sideSign > 0f)
            {
                verts.Add(new Vector3(backLeft.x, y, backLeft.y));  colors.Add(c);
                verts.Add(new Vector3(backRight.x, y, backRight.y)); colors.Add(c);
            }
            else
            {
                verts.Add(new Vector3(backRight.x, y, backRight.y)); colors.Add(c);
                verts.Add(new Vector3(backLeft.x, y, backLeft.y));  colors.Add(c);
            }
            tris.Add(baseIdx + 0); tris.Add(baseIdx + 1); tris.Add(baseIdx + 2);
        }

        /// <summary>
        /// In-place alpha rewrite for a previously-appended vertex range.
        /// Use this when the mesh's color array is shared by many
        /// primitives (rings, arrows) and you want to change one
        /// primitive's alpha without rebuilding the mesh. Caller should
        /// then assign the modified array back via mesh.colors32 = ….
        /// </summary>
        public static void SetRangeAlpha(Color32[] colors, int firstVertexIndex, int vertexCount, byte alpha)
        {
            int end = firstVertexIndex + vertexCount;
            for (int i = firstVertexIndex; i < end; i++)
            {
                Color32 c = colors[i];
                c.a = alpha;
                colors[i] = c;
            }
        }

        /// <summary>
        /// Deterministic hash → hue color for visually distinguishing
        /// different lanes. The same input always yields the same color
        /// across the network (so an inbound lane has the same color in
        /// every UI element where it appears).
        /// </summary>
        public static Color HashToColor(string seed, float saturation = 0.75f, float value = 1.0f, float alpha = 1.0f)
        {
            if (string.IsNullOrEmpty(seed)) return Color.white;
            // FNV-1a hash for stability across runs.
            uint hash = 2166136261u;
            for (int i = 0; i < seed.Length; i++)
            {
                hash ^= seed[i];
                hash *= 16777619u;
            }
            float hue = (hash % 360u) / 360f;
            Color rgb = Color.HSVToRGB(hue, saturation, value);
            rgb.a = alpha;
            return rgb;
        }
    }
}
