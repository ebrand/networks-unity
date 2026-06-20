// Builds a RoadCrossSection (the transverse polyline RoadSweep lofts) from a Model.RoadProfile. This is the
// single shared profile→section translation used by BOTH the 3D designer preview (RoadPreview3D) and the
// in-situ "Build Plan" sweep, so the authored profile and the laid road always match. Mirrors the strip
// order of Roads.RoadLayout (Guard/Parapet · Edge · Curb · BA lanes · centre · AB lanes · Curb · Edge ·
// Guard) but in 3D-shaped components: curbs/parapets get real height; elevated adds a 1 m deck slab.
//
// Convention (see RoadCrossSection): authored left→right, drivable surface at Y = 0, raised features above.

using System.Collections.Generic;
using NetworkDesigner.Model;

namespace NetworkDesigner.Roads
{
    public static class RoadCrossSectionBuilder
    {
        // A swept band's final U-span + what it is — captured during FromStack so lane markings can be placed at
        // the EXACT band boundaries the body uses. Zone: 0 = BA, 1 = Center, 2 = AB (for same/opposing-dir markings).
        public struct StackBand { public float U0, U1; public CorridorType Type; public int Zone; public bool Parapet, Fence, Guardrail; public float ParapetH; }

        // With curbs the shoulder/sidewalk is RAISED even with the curb top (the curb is the step down to the
        // road). Elevated adds a deck + 1 m concrete parapet (unless the road has guardrails, which are built
        // as separate post/rail geometry). Elevated forces shoulders (no curbs/sidewalk edge).
        public static RoadCrossSection FromProfile(RoadProfile p)
        {
            var xs = new RoadCrossSection();
            if (p.Elevated) xs.Thickness = 1f;   // 1 m elevated road bed (deck slab); supports/haunch/trestle run every 100 m (path-level)
            RoadSurface edge = (p.Sidewalks && !p.Elevated) ? RoadSurface.Sidewalk : RoadSurface.Shoulder;
            float shBA = p.ShoulderBA != null ? p.ShoulderBA.Width : 0f;
            float shAB = p.ShoulderAB != null ? p.ShoulderAB.Width : 0f;
            bool curbs = p.Curbs;
            bool par = p.Elevated && !p.Guardrails;
            const float cH = 0.25f, cW = 0.5f;

            if (par) { xs.Step(1f, RoadSurface.Concrete); xs.Flat(0.3f, RoadSurface.Concrete); xs.Step(-1f, RoadSurface.Concrete); }
            if (curbs)
            {
                xs.Step(cH, edge);                        // outer face up to the raised verge
                if (shBA > 0.01f) xs.Flat(shBA, edge);    // shoulder/sidewalk, raised even with the curb
                xs.Flat(cW, RoadSurface.Curb);            // curb top (light gray)
                xs.Step(-cH, RoadSurface.Curb);           // curb face down to the lane
            }
            else if (shBA > 0.01f) xs.Flat(shBA, edge);

            for (int i = p.BA.Lanes.Count - 1; i >= 0; i--) xs.Lane(p.BA.Lanes[i].Width);
            if (p.Median != null) xs.Median(p.Median.Width, 0.25f);
            else if (p.TurnLane != null) xs.Lane(p.TurnLane.Width);
            for (int i = 0; i < p.AB.Lanes.Count; i++) xs.Lane(p.AB.Lanes[i].Width);

            if (curbs)
            {
                xs.Step(cH, RoadSurface.Curb);            // curb face up
                xs.Flat(cW, RoadSurface.Curb);            // curb top
                if (shAB > 0.01f) xs.Flat(shAB, edge);    // raised shoulder/sidewalk
                xs.Step(-cH, edge);                       // outer face down
            }
            else if (shAB > 0.01f) xs.Flat(shAB, edge);
            if (par) { xs.Step(1f, RoadSurface.Concrete); xs.Flat(0.3f, RoadSurface.Concrete); xs.Step(-1f, RoadSurface.Concrete); }
            return xs;
        }

        // Build a cross-section straight from the authored corridor STACK (the Transportation Corridor Designer's
        // source of truth). Authored left→right: BA reversed (outer→centre), Center, then AB (centre→outer) — so BA
        // sits left of the axis and AB right, matching FromProfile's ordering. CenterU is left auto (geometric
        // centre) to match FromProfile. Phase 1 renders all types as simple swept bands; per-segment fence/parapet/
        // guardrail render as thin vertical walls (real meshes are Phase 2).
        public static RoadCrossSection FromStack(CorridorStack st) => FromStack(st, null);

        // `bandsOut` (optional) receives each emitted band's U-span/type/zone, for lane-marking placement.
        public static RoadCrossSection FromStack(CorridorStack st, List<StackBand> bandsOut)
        {
            var xs = new RoadCrossSection();
            if (st == null) return xs;
            for (int i = st.BA.Count - 1; i >= 0; i--) EmitRec(xs, st.BA[i], 0, bandsOut);
            float beforeCenter = xs.Width;
            foreach (CorridorSegment s in st.Center) EmitRec(xs, s, 1, bandsOut);   // straddles the axis
            xs.SplitU = (beforeCenter + xs.Width) * 0.5f;   // midline runs through the MIDDLE of the centre band(s)
            foreach (CorridorSegment s in st.AB) EmitRec(xs, s, 2, bandsOut);
            // CenterU left auto = GEOMETRIC centre, so the drawn node sits in the middle of the corridor (intuitive
            // placement). Lane alignment across a width change is handled locally by the junction taper, not by
            // sliding the whole section onto its A↔B axis.
            return xs;
        }

        // The corridor stack as an ordered (width, RoadLayout-kind) strip — for the plan overlay's lane-marking
        // styles. Left→right: BA reversed, Center, AB. Traffic → lane (BA/AB by zone); Turn → turn lane; Median →
        // median; Sidewalk → curb; shoulder/bike/rail → generic edge.
        public static List<(float w, int k)> StackLayout(CorridorStack st)
        {
            var lay = new List<(float w, int k)>();
            if (st == null) return lay;
            for (int i = st.BA.Count - 1; i >= 0; i--) lay.Add((st.BA[i].Width, LayoutKind(st.BA[i].Type, 0)));
            foreach (CorridorSegment s in st.Center) lay.Add((s.Width, LayoutKind(s.Type, 1)));
            foreach (CorridorSegment s in st.AB) lay.Add((s.Width, LayoutKind(s.Type, 2)));
            return lay;
        }

        static int LayoutKind(CorridorType t, int zone) => t switch
        {
            CorridorType.Traffic => zone == 0 ? RoadLayout.LaneBA : RoadLayout.LaneAB,
            CorridorType.Turn => RoadLayout.TurnLane,
            CorridorType.Median => RoadLayout.Median,
            CorridorType.Sidewalk => RoadLayout.Curb,
            _ => RoadLayout.Edge,   // shoulder / bike / rail
        };

        static void EmitRec(RoadCrossSection xs, CorridorSegment s, int zone, List<StackBand> bandsOut)
        {
            float u0 = xs.Width;
            EmitSegment(xs, s);
            bandsOut?.Add(new StackBand { U0 = u0, U1 = xs.Width, Type = s.Type, Zone = zone,
                                          Parapet = s.Parapet, Fence = s.Fence, Guardrail = s.Guardrail, ParapetH = s.ParapetHeight });
        }

        // Lane markings for a corridor stack, as (lateral offset from geometric centre · yellow? · dashed?), computed
        // from the captured bands so they line up with the swept body. Same-direction Traffic → dashed white divider;
        // opposing Traffic (no median) → double yellow; Traffic↔Turn → solid+dashed yellow; Traffic↔other → white edge.
        public static List<(float u, bool yellow, bool dashed)> StackMarkings(List<StackBand> bands, float totalWidth)
        {
            var marks = new List<(float u, bool yellow, bool dashed)>();
            if (bands == null || bands.Count < 2) return marks;
            const float sep = 0.16f;
            float half = totalWidth * 0.5f;
            for (int i = 0; i < bands.Count - 1; i++)
            {
                StackBand L = bands[i], R = bands[i + 1];
                float off = L.U1 - half;
                bool lnL = L.Type == CorridorType.Traffic, lnR = R.Type == CorridorType.Traffic;
                bool turn = L.Type == CorridorType.Turn || R.Type == CorridorType.Turn;
                if (lnL && lnR)
                {
                    if (L.Zone == R.Zone) marks.Add((off, false, true));                                 // same direction → dashed white
                    else { marks.Add((off - sep, true, false)); marks.Add((off + sep, true, false)); }   // opposing → double yellow
                }
                else if (turn && (lnL || lnR))
                {
                    marks.Add((off, true, false));                                                       // solid yellow lane/turn edge
                    marks.Add((R.Type == CorridorType.Turn ? off + sep : off - sep, true, true));        // dashed yellow on the turn side
                }
                else if (lnL || lnR) marks.Add((off, false, false));                                     // lane ↔ shoulder/bike/median/rail → white edge
            }
            return marks;
        }

        static void EmitSegment(RoadCrossSection xs, CorridorSegment s)
        {
            float w = UnityEngine.Mathf.Max(0f, s.Width);
            switch (s.Type)
            {
                // Median/Sidewalk have their own raised structure → walls (if any) sit centred on the flat top.
                case CorridorType.Median:   xs.Step(0.25f, RoadSurface.Concrete); FlatMaybeWall(xs, w, RoadSurface.Grass, s); xs.Step(-0.25f, RoadSurface.Concrete); break;
                case CorridorType.Sidewalk: xs.Step(0.12f, RoadSurface.Curb); FlatMaybeWall(xs, w, RoadSurface.Sidewalk, s); xs.Step(-0.12f, RoadSurface.Curb); break;
                case CorridorType.Rail:     FlatMaybeWall(xs, w, RoadSurface.Rail, s); break;
                case CorridorType.Bike:     FlatMaybeWall(xs, w, RoadSurface.Bike, s); break;
                case CorridorType.Shoulder: FlatMaybeWall(xs, w, RoadSurface.Shoulder, s); break;
                default:                    FlatMaybeWall(xs, w, RoadSurface.Asphalt, s); break;   // Traffic / Turn
            }
        }

        // Emit the band's flat top, with any parapet/fence/guardrail wall CENTRED in the corridor (split the flat
        // into two halves around the wall). No wall → one flat band.
        static void FlatMaybeWall(RoadCrossSection xs, float w, RoadSurface surf, CorridorSegment s)
        {
            // Parapets (extruded) and fences (instanced chainlink) are separate geometry, NOT bands. Guardrail is
            // still a thin placeholder wall in the cross-section for now (Phase 2 gives it a real mesh too).
            if (!s.Guardrail) { xs.Flat(w, surf); return; }
            xs.Flat(w * 0.5f, surf);
            Wall(xs, 0.8f, 0.1f, RoadSurface.Guardrail);
            xs.Flat(w * 0.5f, surf);
        }

        // A thin raised box (up face · top · down face) — a real wall with thickness, so its faces don't coincide
        // (two zero-width steps at the same U z-fight). thick is small (parapet/fence width).
        static void Wall(RoadCrossSection xs, float h, float thick, RoadSurface s)
        {
            xs.Step(h, s); xs.Flat(UnityEngine.Mathf.Max(0.02f, thick), s); xs.Step(-h, s);
        }
    }
}
