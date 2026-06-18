// One shared set of design/guide controls — node display, snapping radii, the straight-ahead extension
// guide, and the curve lever/symmetry — that the rail-build, rail-plan AND road-plan tools all respect.
// The per-layer fields (RailTrackLayer/RailPlanLayer/RoadPlanLayer) delegate here, so the Design Controls
// palette (and the React tunables, which still reference the layers) move every tool at once.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class PlanGuides
    {
        // ── nodes ──
        public static bool  ShowNodes      = true;
        public static float NodePuckRadius = 1.5f;                          // node marker size (m), shared rail + road
        public static float NodePuckHeight = 0.6f;                          // 3D puck height (m), shared rail + road
        public static Color RailNodeColor  = new Color(0f, 1f, 1f, 0.5f);   // #00FFFF80
        public static Color RoadNodeColor  = new Color(1f, 0f, 0f, 0.5f);   // #FF000080

        // ── guides ──
        public static float ExtensionGuideLength = 120f;   // m: how far the collinear ("colinear") extension guide runs
        public static float ExtensionSnapRadius  = 4f;     // m: snap onto that guide
        public static bool  ProximitySnapOn      = true;   // snap onto a plan's own nodes/edges?
        public static float EndSnapRadius         = 8f;    // m: that proximity snap radius (snap the cursor ONTO a node)
        public static float NodePickRadius        = 2.5f;  // m: grab an existing node when starting/joining
        public static float GuideRange            = 40f;   // m: how close the cursor must be to a node for it to EMIT
                                                           // colinear/perpendicular guides (distinct from EndSnapRadius
                                                           // node-snap).
        public static float GuideSnapRadius       = 4f;    // m: how close the cursor must be to a reactive node-guide
                                                           // LINE (or crossing) to hard-snap onto it ("guide snap strength").

        // ── curves ──
        public static float CurveLever        = 0.55f;     // bezier control lever (0.55 ≈ circular arc)
        public static float CurveSymmetrySnap = 0.1f;      // equal-leg curve snapping strength (0 = off)
    }
}
