// Builds a RoadCrossSection (the transverse polyline RoadSweep lofts) from a Model.RoadProfile. This is the
// single shared profile→section translation used by BOTH the 3D designer preview (RoadPreview3D) and the
// in-situ "Build Plan" sweep, so the authored profile and the laid road always match. Mirrors the strip
// order of Roads.RoadLayout (Guard/Parapet · Edge · Curb · BA lanes · centre · AB lanes · Curb · Edge ·
// Guard) but in 3D-shaped components: curbs/parapets get real height; elevated adds a 1 m deck slab.
//
// Convention (see RoadCrossSection): authored left→right, drivable surface at Y = 0, raised features above.

using NetworkDesigner.Model;

namespace NetworkDesigner.Roads
{
    public static class RoadCrossSectionBuilder
    {
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
    }
}
