// A road cross-section as a transverse top-surface polyline (U = lateral metres, Y = height metres), each
// segment carrying a surface kind. Built by stacking parametric components — lane, shoulder, curb,
// sidewalk, median — left → right. RoadSweep lofts this polyline along a path. This generalises the flat
// strip model in Model/RoadProfile.cs to 3D-shaped pieces (curbs/medians get real height) and is the
// foundation for the in-game 3D road designer.
//
// Convention: the section is authored left → right. U grows rightward, Y grows up; the drivable surface
// sits at Y = 0 so the road drapes onto the terrain, and curbs/sidewalks/medians rise above it.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Roads
{
    public enum RoadSurface { Asphalt, Shoulder, Concrete, Grass, Dirt, Deck, Curb, Sidewalk, Guardrail, Rail, Fence, Parapet, Bike }

    public class RoadCrossSection
    {
        // Top-surface polyline, left → right. Pts[i] = (U, Y); Segs[i] is the surface of the band Pts[i]→Pts[i+1].
        public readonly List<Vector2> Pts = new List<Vector2>();
        public readonly List<RoadSurface> Segs = new List<RoadSurface>();

        float _u, _y;             // running lateral + height cursor
        public float Width => _u; // total lateral width once built
        public float CenterU = -1f;   // where the path centreline sits in U; < 0 = auto (geometric centre)
        public float SplitU = -1f;    // the A→B / B→A boundary in U (the directional midline); < 0 = unset
        public float Thickness = 0f;  // deck depth below the road surface; > 0 closes the solid (bottom + edges
                                      // + end caps) so a RAISED road reads as a slab. 0 = open top skin (on-ground).

        public RoadCrossSection() { Pts.Add(new Vector2(0f, 0f)); }

        // A flat band of `width` at the current height (lane, shoulder, sidewalk top, median top).
        public RoadCrossSection Flat(float width, RoadSurface s)
        {
            _u += Mathf.Max(0f, width);
            Pts.Add(new Vector2(_u, _y)); Segs.Add(s);
            return this;
        }

        // A vertical step of `rise` (+ up / − down) at the current U — a curb face or median wall (no width).
        public RoadCrossSection Step(float rise, RoadSurface s)
        {
            _y += rise;
            Pts.Add(new Vector2(_u, _y)); Segs.Add(s);
            return this;
        }

        // A sloped band: rises by `rise` over `width` (a ramped curb / batter / verge).
        public RoadCrossSection Slope(float width, float rise, RoadSurface s)
        {
            _u += Mathf.Max(0f, width); _y += rise;
            Pts.Add(new Vector2(_u, _y)); Segs.Add(s);
            return this;
        }

        // ── convenience components (sensible default dims; pass your own to override) ──
        public RoadCrossSection Lane(float width = 3.5f)        => Flat(width, RoadSurface.Asphalt);
        public RoadCrossSection ShoulderBand(float width = 2.0f) => Flat(width, RoadSurface.Shoulder);
        public RoadCrossSection Sidewalk(float width = 2.0f)    => Flat(width, RoadSurface.Concrete);
        public RoadCrossSection CurbUp(float h = 0.15f)         => Step(h, RoadSurface.Concrete);   // road → kerb top
        public RoadCrossSection CurbDown(float h = 0.15f)       => Step(-h, RoadSurface.Concrete);  // kerb top → road
        public RoadCrossSection Median(float width = 2.0f, float h = 0.15f)
            => Step(h, RoadSurface.Concrete).Flat(width, RoadSurface.Grass).Step(-h, RoadSurface.Concrete);

        public float Center() => CenterU >= 0f ? CenterU : _u * 0.5f;
    }
}
