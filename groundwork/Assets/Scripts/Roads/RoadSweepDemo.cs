// Phase-1 scaffold for the in-game 3D road designer. Drop this on an empty GameObject (at a spot you can
// see in the scene) and it builds a sample urban boulevard cross-section — raised sidewalks · kerbs · two
// lanes · planted median · two lanes — and lofts it down a straight segment AND a curved one, so you can
// confirm the parametric components + sweep render correctly in 3D. Use the "Rebuild" context menu to
// regenerate after tweaking. Nothing here touches the existing RoadRenderer / NetworkDesigner.

using UnityEngine;

namespace NetworkDesigner.Roads
{
    public class RoadSweepDemo : MonoBehaviour
    {
        public bool BuildCurved = true;

        void Start() => Rebuild();

        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                DestroyImmediate(transform.GetChild(i).gameObject);

            // Urban boulevard: drivable surface at Y=0, sidewalks + median raised above it.
            var xs = new RoadCrossSection()
                .Step(0.15f, RoadSurface.Concrete)   // left sidewalk outer face
                .Sidewalk(2.5f)
                .CurbDown()                          // step down to the road
                .Lane().Lane()
                .Median()                            // raised planted median (walls + grass top)
                .Lane().Lane()
                .CurbUp()                            // step up to the right sidewalk
                .Sidewalk(2.5f)
                .Step(-0.15f, RoadSurface.Concrete); // right sidewalk outer face

            Vector3 o = transform.position;
            RoadSweep.Build(xs, P(o, -30f, 0f), P(o, 30f, 0f), false, default, default, transform, "Straight");

            if (BuildCurved)
                RoadSweep.Build(xs, P(o, -30f, 40f), P(o, 30f, 40f), true,
                                P(o, -10f, 70f), P(o, 10f, 70f), transform, "Curved");

            // Raised deck: same profile + a 0.5 m slab thickness, lofted 5 m off the ground — shows the
            // underside / outer edges / end caps that an elevated road needs.
            xs.Thickness = 0.5f;
            RoadSweep.Build(xs, P(o, -30f, -40f), P(o, 30f, -40f), false, default, default, transform, "Raised", 5f, 5f);
        }

        // Local-to-world XZ helper (the demo's origin = this object's position).
        static Vector2 P(Vector3 origin, float x, float z) => new Vector2(origin.x + x, origin.z + z);
    }
}
