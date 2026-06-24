using System;
using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Geometry;   // GeometryResolver (cubic bezier sampling)
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace NetworkDesigner.Roads
{
    // Phase-0 spike: build a RoadCrossSection directly from a Corridor's ordered lane-edges + non-navigable bands, then
    // sweep it with the EXISTING RoadSweep. Validates D1 (lane-edges grouped into a corridor), D2 (median/shoulder as
    // metadata, navigable kinds as lanes), and D3 (per-corridor sweep render). Reuses the whole render pipeline; the
    // only new bit is "lane-edges → cross-section".
    public static class LaneEdgeCorridorBuilder
    {
        // ── corridor reference-path samplers (A→B param 0..1; straight chord or cubic bezier per c.Curved) ──
        // Shared by render/overlay/endpoints/beds/agents so the lane geometry stays consistent across all of them.
        public static bool PathEndpoints(LaneEdgeNetwork net, Corridor c, out Vector2 a, out Vector2 b)
        {
            a = b = Vector2.zero;
            if (c == null || c.Lanes.Count == 0) return false;
            LaneEdge l0 = net.Edges[c.Lanes[0]];
            a = net.Nodes[l0.A]; b = net.Nodes[l0.B];
            return true;
        }

        public static Vector2 PathPoint(LaneEdgeNetwork net, Corridor c, float t)
        {
            if (!PathEndpoints(net, c, out Vector2 a, out Vector2 b)) return Vector2.zero;
            return c.Curved ? GeometryResolver.SampleCubic(a, c.ControlA, c.ControlB, b, t) : Vector2.Lerp(a, b, t);
        }

        // Unit tangent of the reference path at t, pointing A→B (or +X for a degenerate corridor).
        public static Vector2 PathTangent(LaneEdgeNetwork net, Corridor c, float t)
        {
            if (!PathEndpoints(net, c, out Vector2 a, out Vector2 b)) return Vector2.right;
            Vector2 tan = c.Curved ? GeometryResolver.CubicTangent(a, c.ControlA, c.ControlB, b, t) : (b - a);
            return tan.sqrMagnitude < 1e-8f ? Vector2.right : tan.normalized;
        }

        // Lane lateral right vector at t — matches RoadSweep's framing: fr = Cross(up, fwd) → (tan.y, -tan.x).
        public static Vector2 PathRight(Vector2 tangent) => new Vector2(tangent.y, -tangent.x);

        // Arc length of the corridor reference path (curve) or chord (straight).
        public static float PathLength(LaneEdgeNetwork net, Corridor c)
        {
            if (!PathEndpoints(net, c, out Vector2 a, out Vector2 b)) return 0f;
            return c.Curved ? GeometryResolver.CubicArcLength(a, c.ControlA, c.ControlB, b) : (b - a).magnitude;
        }

        // The corridor's cross-section, laid left→right from its lanes (ordered by lateral offset) with the median
        // inserted at the travel-direction flip and shoulders on the outer edges. Absolute Offset values only set the
        // ORDER; the section widths come from each lane/band's width.
        // A lane rendered as a taper wedge (BuildTaperBodies) is excluded from the uniform body/markings so the wedge isn't
        // drawn on top of a full lane.
        static bool LaneTapered(Corridor c, int li)
        {
            if (c.Tapers == null) return false;
            for (int i = 0; i < c.Tapers.Count; i++) if (c.Tapers[i].LaneEdge == li) return true;
            return false;
        }

        public static RoadCrossSection BuildCrossSection(Corridor c, LaneEdgeNetwork net)
        {
            var lanes = new List<LaneEdge>();
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= net.Edges.Count) continue;
                if (LaneTapered(c, li)) continue;   // rendered as a taper wedge (BuildTaperBodies), not a uniform lane
                lanes.Add(net.Edges[li]);
            }
            lanes.Sort((a, b) => a.Offset.CompareTo(b.Offset));   // left → right across the section

            var xs = new RoadCrossSection();
            // A taper that owns a side's shoulder draws it along the wedge (BuildTaperBodies), so drop it from the body.
            float shBA = LaneEdgeWorld.ShoulderSuppressed(c, -1f) ? 0f : c.ShoulderBA;
            float shAB = LaneEdgeWorld.ShoulderSuppressed(c, 1f) ? 0f : c.ShoulderAB;

            // Lane-subset extension: lay each lane at its ACTUAL Offset (fill any gaps between non-adjacent lanes with
            // asphalt) and shift the centreline (CenterU) so the swept body sits on the source lanes, not re-centred.
            if (c.AlignLanes && lanes.Count > 0)
            {
                float leftEdge = lanes[0].Offset - lanes[0].Width * 0.5f;   // lanes sorted ascending by offset
                if (shBA > 0f) xs.ShoulderBand(shBA);                       // outer shoulder before the lanes
                float cursor = leftEdge;
                int alPrevDir = lanes[0].Direction; bool alMedianPlaced = false;
                foreach (LaneEdge e in lanes)
                {
                    float laneLeft = e.Offset - e.Width * 0.5f;
                    float gap = laneLeft - cursor;
                    if (gap > 0.01f)
                    {
                        // The gap straddling the travel-direction flip IS the median (a two-way extension); fill it as a
                        // median band (paints the double-yellow + keeps it non-navigable). Same-direction gaps stay asphalt.
                        if (!alMedianPlaced && c.MedianWidth > 0f && e.Direction != alPrevDir)
                        { float u0 = xs.Width; xs.Median(gap); xs.SplitU = (u0 + xs.Width) * 0.5f; alMedianPlaced = true; }
                        else xs.Lane(gap);
                    }
                    AddBand(xs, e.Kind, e.Width);
                    cursor = e.Offset + e.Width * 0.5f;
                    alPrevDir = e.Direction;
                }
                if (shAB > 0f) xs.ShoulderBand(shAB);                       // outer shoulder after the lanes
                // body lateral of a lane = U − CenterU = Offset; the leading shoulder shifts every lane's U by shBA.
                xs.CenterU = shBA - leftEdge; xs.CenterUSet = true;
                return xs;
            }

            if (shBA > 0f) xs.ShoulderBand(shBA);
            int prevDir = lanes.Count > 0 ? lanes[0].Direction : -1;
            bool medianPlaced = false;
            foreach (LaneEdge e in lanes)
            {
                if (!medianPlaced && c.MedianWidth > 0f && e.Direction != prevDir)   // travel direction flips → median here
                {
                    float u0 = xs.Width; xs.Median(c.MedianWidth); xs.SplitU = (u0 + xs.Width) * 0.5f;
                    medianPlaced = true;
                }
                AddBand(xs, e.Kind, e.Width);
                prevDir = e.Direction;
            }
            if (shAB > 0f) xs.ShoulderBand(shAB);
            return xs;
        }

        static void AddBand(RoadCrossSection xs, LaneKind k, float w)
        {
            switch (k)
            {
                case LaneKind.Sidewalk: xs.CurbUp().Sidewalk(w).CurbDown(); break;   // raised walk
                default: xs.Lane(w); break;                                          // Traffic / Turn / Bike → asphalt lane
            }
        }

        // Render one corridor by sweeping its cross-section along the SHARED centreline nodes of its lanes (#144: the
        // lane-edges share the corridor's two nodes; the lateral position is the Offset field, not the node position).
        public static GameObject RenderCorridor(LaneEdgeNetwork net, Corridor c, Transform parent, Func<Vector2, float> groundAt)
        {
            if (c.Lanes.Count == 0) return null;
            LaneEdge l0 = net.Edges[c.Lanes[0]];
            Vector2 a = net.Nodes[l0.A], b = net.Nodes[l0.B];
            RoadCrossSection xs = BuildCrossSection(c, net);
            // Once excavated, sit the body on the CAPTURED design grade (NodeY) as a slab of the bed depth — so it fills
            // the cut bed at the original level instead of draping into the (now-lowered) terrain. Un-excavated → drape.
            float yA = net.GetNodeY(l0.A), yB = net.GetNodeY(l0.B);
            bool haveGrade = !float.IsNaN(yA) && !float.IsNaN(yB);
            if (haveGrade && c.BedDepth > 0f) xs.Thickness = c.BedDepth;
            float hA = haveGrade ? yA : (groundAt != null ? groundAt(a) : 0f);
            float hB = haveGrade ? yB : (groundAt != null ? groundAt(b) : 0f);
            float follow = haveGrade ? 0f : (groundAt != null ? 1f : 0f);   // design grade once excavated; else terrain-follow
            GameObject body = RoadSweep.Build(xs, a, b, c.Curved, c.ControlA, c.ControlB, parent, $"LaneEdgeCorridor_{c.Id}",
                                              hA, hB, groundAt, follow);
            BuildMarkings(net, c, parent, hA, hB, groundAt, follow);   // painted lane lines, riding the same path + grade
            BuildDirectionArrows(net, c, parent, hA, hB, groundAt, follow);   // travel arrows on one-way roads (~every 200 m)
            BuildTaperBodies(net, c, parent, hA, hB, groundAt, follow);   // paved lane-drop wedges in dropped-lane slots
            return body;
        }

        // ── lane-drop taper bodies ── a flat asphalt-grey wedge filling each dropped lane's slot, full lane width at the
        // drop end and narrowing to zero over Length, riding the same path + grade as the corridor body. Placeholder flat
        // material (not the textured surface shader). Mirrors LaneEdgeWorld.TaperWedge but carries the body's elevation.
        const float TaperLift = 0.03f;
        static Material _taperMat, _taperShoulderMat;
        static Material TaperMat() => _taperMat != null ? _taperMat : (_taperMat = RoadSweep.SurfaceMat(RoadSurface.Asphalt));
        static Material TaperShoulderMat() => _taperShoulderMat != null ? _taperShoulderMat : (_taperShoulderMat = RoadSweep.SurfaceMat(RoadSurface.Shoulder));

        static void BuildTaperBodies(LaneEdgeNetwork net, Corridor c, Transform parent, float hA, float hB, Func<Vector2, float> groundAt, float follow)
        {
            if (c.Tapers == null || c.Tapers.Count == 0) return;
            if (!PathEndpoints(net, c, out Vector2 a, out Vector2 b)) return;
            float pathLen = PathLength(net, c);
            if (pathLen < 1e-2f) return;
            int frames = Mathf.Clamp(Mathf.CeilToInt(pathLen / 0.5f) + 1, 2, 2048);
            float[] elev = RoadSweep.ElevationProfile(a, b, c.Curved, c.ControlA, c.ControlB, frames, hA, hB, groundAt, follow);
            foreach (var tp in c.Tapers)
            {
                const int M = 18;
                float frac = Mathf.Clamp01(tp.Length / pathLen);
                float sgn = tp.Offset >= 0f ? 1f : -1f;
                float innerOff = tp.Offset - sgn * tp.Width * 0.5f;
                float sh = LaneEdgeWorld.TaperOuterShoulder(c, tp);   // shoulder follows the wedge when it's the outer lane
                int cross = (frac < 0.999f) ? M + 2 : M + 1;          // S-curve region + full-width tail to the far end
                var inner = new List<Vector3>(cross); var outer = new List<Vector3>(cross); var shel = new List<Vector3>(cross);
                for (int k = 0; k < cross; k++)
                {
                    float t; float outerOff;
                    if (k <= M)
                    {
                        float s = k / (float)M, u = s * frac, e = s * s * (3f - 2f * s);
                        t = tp.AtA ? u : (1f - u);
                        outerOff = innerOff + sgn * tp.Width * e;
                    }
                    else { t = tp.AtA ? 1f : 0f; outerOff = innerOff + sgn * tp.Width; }   // full-width tail
                    Vector2 p = PathPoint(net, c, t);
                    Vector2 fr = PathRight(PathTangent(net, c, t));
                    float y = elev[Mathf.Clamp(Mathf.RoundToInt(t * (frames - 1)), 0, frames - 1)] + TaperLift;
                    Vector2 ip = p + fr * innerOff, op = p + fr * outerOff;
                    inner.Add(new Vector3(ip.x, y, ip.y));
                    outer.Add(new Vector3(op.x, y, op.y));
                    if (sh > 0.01f) { Vector2 spq = p + fr * (outerOff + sgn * sh); shel.Add(new Vector3(spq.x, y, spq.y)); }
                }
                BuildTaperStrip(inner, outer, parent, $"LaneDropTaper_{c.Id}", TaperMat());
                if (sh > 0.01f) BuildTaperStrip(outer, shel, parent, $"LaneDropTaperShoulder_{c.Id}", TaperShoulderMat());
            }
        }

        // A draped, double-sided triangle strip between two equal-length rails, with the given material.
        static void BuildTaperStrip(List<Vector3> left, List<Vector3> right, Transform parent, string name, Material mat)
        {
            int n = left.Count;
            if (n < 2 || right.Count != n) return;
            var verts = new List<Vector3>(n * 2); var tris = new List<int>();
            for (int k = 0; k < n; k++) { verts.Add(left[k]); verts.Add(right[k]); }
            for (int k = 0; k < n - 1; k++)
            {
                int b0 = k * 2;
                tris.Add(b0); tris.Add(b0 + 1); tris.Add(b0 + 3); tris.Add(b0); tris.Add(b0 + 3); tris.Add(b0 + 2);   // top
                tris.Add(b0); tris.Add(b0 + 3); tris.Add(b0 + 1); tris.Add(b0); tris.Add(b0 + 2); tris.Add(b0 + 3);   // back (2-sided)
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        // ---- lane markings (painted lines on the asphalt) ----
        static readonly Color MarkWhite = new Color(0.93f, 0.93f, 0.93f);
        static readonly Color MarkYellow = new Color(0.96f, 0.80f, 0.18f);
        const float MarkWidth = 0.15f, MarkLift = 0.05f, MarkDash = 3f, MarkGap = 2.5f, DblYellowSep = 0.18f;
        static Material _markWhiteMat, _markYellowMat;
        static Material MarkWhiteMat() => _markWhiteMat != null ? _markWhiteMat : (_markWhiteMat = NetworkDesigner.PipelineMaterials.CreateUnlitColor(MarkWhite, "LaneMarkWhite"));
        static Material MarkYellowMat() => _markYellowMat != null ? _markYellowMat : (_markYellowMat = NetworkDesigner.PipelineMaterials.CreateUnlitColor(MarkYellow, "LaneMarkYellow"));

        // Marking layout from the corridor's own lanes (offset, yellow?, dashed?): solid white outer edge lines,
        // dashed white between same-direction lanes, double-yellow between opposing directions, solid white beside a
        // bike lane. Offsets are in the body's frame (lane Offset = lateral from centreline), so they sit on the lanes.
        static List<(float u, bool yellow, bool dashed)> CorridorMarks(LaneEdgeNetwork net, Corridor c)
        {
            var marks = new List<(float, bool, bool)>();
            var lanes = new List<LaneEdge>();
            foreach (int li in c.Lanes)
                if (li >= 0 && li < net.Edges.Count && net.Edges[li].Kind != LaneKind.Sidewalk && !LaneTapered(c, li)) lanes.Add(net.Edges[li]);
            if (lanes.Count == 0) return marks;
            lanes.Sort((x, y) => x.Offset.CompareTo(y.Offset));
            // Outer edge lines (solid white) at the outer edges of the outermost roadway lanes.
            marks.Add((lanes[0].Offset - lanes[0].Width * 0.5f, false, false));
            marks.Add((lanes[lanes.Count - 1].Offset + lanes[lanes.Count - 1].Width * 0.5f, false, false));
            for (int i = 0; i < lanes.Count - 1; i++)
            {
                LaneEdge L = lanes[i], R = lanes[i + 1];
                float boundary = (L.Offset + L.Width * 0.5f + R.Offset - R.Width * 0.5f) * 0.5f;
                if (L.Direction != R.Direction)                                                  // opposing → double yellow
                { marks.Add((boundary - DblYellowSep, true, false)); marks.Add((boundary + DblYellowSep, true, false)); }
                else if (L.Kind == LaneKind.Bike || R.Kind == LaneKind.Bike) marks.Add((boundary, false, false));   // bike-lane edge → solid white
                else marks.Add((boundary, false, true));                                         // same dir → dashed white
            }
            return marks;
        }

        // Sweep thin double-sided unlit marking quads along the corridor path, riding the SAME path + grade as the body
        // (lifted MarkLift above it). Mirrors RoadPlanBuilder.BuildRoadMarkings, but curve-aware via the corridor path.
        static void BuildMarkings(LaneEdgeNetwork net, Corridor c, Transform parent, float hA, float hB, Func<Vector2, float> groundAt, float follow)
        {
            if (!PathEndpoints(net, c, out Vector2 a, out Vector2 b)) return;
            var marks = CorridorMarks(net, c);
            if (marks.Count == 0) return;
            float mlen = PathLength(net, c);
            int frames = Mathf.Clamp(Mathf.CeilToInt(mlen / 0.5f) + 1, 2, 2048);
            var fp = new Vector3[frames]; var fr = new Vector3[frames];
            float[] markY = RoadSweep.ElevationProfile(a, b, c.Curved, c.ControlA, c.ControlB, frames, hA, hB, groundAt, follow);
            for (int f = 0; f < frames; f++)
            {
                float t = f / (float)(frames - 1);
                Vector2 p = PathPoint(net, c, t);
                Vector2 tan = PathTangent(net, c, t);
                Vector3 fwd = new Vector3(tan.x, 0f, tan.y); fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;
                fp[f] = new Vector3(p.x, markY[f] + MarkLift, p.y);
                fr[f] = Vector3.Cross(Vector3.up, fwd).normalized;
            }

            var verts = new List<Vector3>(); var triW = new List<int>(); var triY = new List<int>();
            float hw = MarkWidth * 0.5f, period = MarkDash + MarkGap;
            foreach (var m in marks)
            {
                List<int> tl = m.yellow ? triY : triW;
                float walked = 0f;
                for (int f = 0; f < frames - 1; f++)
                {
                    float segLen = (fp[f + 1] - fp[f]).magnitude;
                    bool on = !m.dashed || (walked % period) < MarkDash;
                    walked += segLen;
                    if (!on) continue;
                    Vector3 l0 = fp[f] + fr[f] * (m.u - hw), r0 = fp[f] + fr[f] * (m.u + hw);
                    Vector3 l1 = fp[f + 1] + fr[f + 1] * (m.u - hw), r1 = fp[f + 1] + fr[f + 1] * (m.u + hw);
                    int s = verts.Count;
                    verts.Add(l0); verts.Add(r0); verts.Add(r1); verts.Add(l1);
                    tl.Add(s); tl.Add(s + 1); tl.Add(s + 2); tl.Add(s); tl.Add(s + 2); tl.Add(s + 3);   // up
                    tl.Add(s); tl.Add(s + 2); tl.Add(s + 1); tl.Add(s); tl.Add(s + 3); tl.Add(s + 2);   // down (2-sided)
                }
            }
            if (verts.Count == 0) return;

            var mesh = new Mesh { name = $"LaneMarks_{c.Id}" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(triW, 0);
            mesh.SetTriangles(triY, 1);
            mesh.RecalculateBounds();

            var go = new GameObject($"LaneMarks_{c.Id}");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { MarkWhiteMat(), MarkYellowMat() };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        // ---- direction arrows on one-way roads ----
        const float ArrowSpacing = 200f;   // metres between arrows along a lane
        // Draw a flat travel-direction arrow per traffic lane every ~200 m, but ONLY on a one-way corridor (every
        // navigable lane the same direction). Arrows ride the corridor path + grade like the markings, lifted just above.
        static void BuildDirectionArrows(LaneEdgeNetwork net, Corridor c, Transform parent, float hA, float hB, Func<Vector2, float> groundAt, float follow)
        {
            int dir = -1, nav = 0; bool twoWay = false;
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= net.Edges.Count) continue;
                LaneEdge e = net.Edges[li];
                if (e.Kind == LaneKind.Sidewalk) continue;
                nav++;
                if (dir < 0) dir = e.Direction; else if (e.Direction != dir) twoWay = true;
            }
            if (twoWay || nav == 0 || !PathEndpoints(net, c, out Vector2 a, out Vector2 b)) return;   // two-way → no arrows

            float len = PathLength(net, c);
            if (len < 4f) return;
            int frames = Mathf.Clamp(Mathf.CeilToInt(len / 0.5f) + 1, 2, 2048);
            float[] aY = RoadSweep.ElevationProfile(a, b, c.Curved, c.ControlA, c.ControlB, frames, hA, hB, groundAt, follow);
            int numArrows = Mathf.Max(1, Mathf.RoundToInt(len / ArrowSpacing));

            var verts = new List<Vector3>(); var tris = new List<int>();
            foreach (int li in c.Lanes)
            {
                if (li < 0 || li >= net.Edges.Count) continue;
                LaneEdge e = net.Edges[li];
                if (e.Kind != LaneKind.Traffic && e.Kind != LaneKind.Turn) continue;   // arrows on drivable lanes only
                for (int k = 0; k < numArrows; k++)
                {
                    float t = (k + 0.5f) / numArrows;
                    Vector2 tan = PathTangent(net, c, t);
                    if (tan.sqrMagnitude < 1e-8f) continue; tan.Normalize();
                    Vector2 rt = PathRight(tan);
                    Vector2 ctr = PathPoint(net, c, t) + rt * e.Offset;
                    float y = aY[Mathf.Clamp(Mathf.RoundToInt(t * (frames - 1)), 0, frames - 1)] + MarkLift + 0.02f;
                    Vector2 along = e.Direction == 2 ? tan : -tan;   // travel direction of this lane
                    EmitArrow(verts, tris, ctr, y, along, new Vector2(along.y, -along.x));
                }
            }
            if (verts.Count == 0) return;

            var mesh = new Mesh { name = $"LaneArrows_{c.Id}" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.RecalculateBounds();
            var go = new GameObject($"LaneArrows_{c.Id}");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = MarkWhiteMat();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        // A flat 2-sided triangle centred at ctr at height y, pointing along `along` (lat = perpendicular).
        static void EmitArrow(List<Vector3> verts, List<int> tris, Vector2 ctr, float y, Vector2 along, Vector2 lat)
        {
            const float fwd = 1.6f, back = 1.0f, half = 0.9f;
            Vector2 tip = ctr + along * fwd, bl = ctr - along * back - lat * half, br = ctr - along * back + lat * half;
            int s = verts.Count;
            verts.Add(new Vector3(tip.x, y, tip.y)); verts.Add(new Vector3(bl.x, y, bl.y)); verts.Add(new Vector3(br.x, y, br.y));
            tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 1);   // 2-sided
        }

#if UNITY_EDITOR
        [MenuItem("Tools/Lane-Edge Spike/Build Straight 4-Lane (one-way)")]
        static void SpikeOneWay() => BuildSpike(false);

        [MenuItem("Tools/Lane-Edge Spike/Build Straight 4-Lane (two-way + median)")]
        static void SpikeTwoWay() => BuildSpike(true);

        static void BuildSpike(bool twoWay)
        {
            Camera cam = Camera.main;
            // Place it where the camera is LOOKING on the terrain (raycast along forward) so it's in view.
            Vector3 origin = Vector3.zero;
            if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit look, 60000f))
                origin = look.point;
            else if (cam != null) origin = cam.transform.position + cam.transform.forward * 80f;
            // Replace any previous spike of this kind so repeated builds don't stack or interfere with the drape raycast.
            string rootName = $"LaneEdgeSpike_{(twoWay ? "2way" : "1way")}";
            var prev = GameObject.Find(rootName); if (prev != null) UnityEngine.Object.DestroyImmediate(prev);
            // Drape EACH vertex onto the terrain surface + 0.25 m (raycast down from well above) so the road follows the
            // ground instead of sitting flat and poking below it at the ends.
            Func<Vector2, float> groundAt = xz =>
                Physics.Raycast(new Vector3(xz.x, origin.y + 2000f, xz.y), Vector3.down, out RaycastHit gh, 10000f)
                    ? gh.point.y + 0.25f : origin.y + 0.25f;

            var net = new LaneEdgeNetwork();
            int na = net.AddNode(new Vector2(origin.x - 40f, origin.z));
            int nb = net.AddNode(new Vector2(origin.x + 40f, origin.z));
            Corridor c = net.AddCorridor();
            c.ShoulderBA = 2f; c.ShoulderAB = 2f;
            if (twoWay)
            {
                c.MedianWidth = 2f;
                AddLane(net, c, na, nb, 0, -5.25f); AddLane(net, c, na, nb, 0, -1.75f);   // BA side (←)
                AddLane(net, c, na, nb, 2, +1.75f); AddLane(net, c, na, nb, 2, +5.25f);   // AB side (→)
            }
            else
            {
                AddLane(net, c, na, nb, 2, -5.25f); AddLane(net, c, na, nb, 2, -1.75f);
                AddLane(net, c, na, nb, 2, +1.75f); AddLane(net, c, na, nb, 2, +5.25f);
            }
            net.SortCorridorLanes(c);

            var root = new GameObject(rootName);
            RenderCorridor(net, c, root.transform, groundAt);
            RoadCrossSection xs = BuildCrossSection(c, net);
            Selection.activeGameObject = root;   // so you can find/Frame (F) it in the hierarchy
            Debug.Log($"[LaneEdgeSpike] built {(twoWay ? "2-way" : "1-way")} 4-lane centred at {origin:F0} (draped to terrain +0.25m); " +
                      $"lanes={c.Lanes.Count} sectionWidth={xs.Width:F1}m — selected '{root.name}' in the hierarchy.");
        }

        static void AddLane(LaneEdgeNetwork net, Corridor c, int a, int b, int dir, float offset)
        {
            int li = net.AddLane(new LaneEdge { A = a, B = b, CorridorId = c.Id, Kind = LaneKind.Traffic, Direction = dir, Width = 3.5f, Offset = offset });
            c.Lanes.Add(li);
        }
#endif
    }
}
