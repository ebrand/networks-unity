// Tag component for an edit-mode draggable handle that adjusts the
// setback override of a specific (road, end) at the currently-selected
// vertex. Picked via Physics.Raycast, then GetComponentInParent.
//
// As of the ring-rework, the handle is a hollow ring mesh positioned
// OUTSIDE the road body at vertex + outward * offset, connected back
// to the actual setback line (Anchor) by a dashed stem rendered on a
// child GameObject. AnchorXZ and OutwardXZ are cached at spawn /
// refresh time so the drag handler can project cursor → setback
// without re-resolving vertex geometry.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Designer
{
    public class SetbackHandle : MonoBehaviour
    {
        public NetworkRoad Road;
        public RoadEnd End;

        /// <summary>True setback midpoint on the road body — the anchor end of the dashed stem.</summary>
        public Vector2 AnchorXZ;
        /// <summary>Unit outward bearing from the vertex (curve-aware). Drag projects cursor onto this.</summary>
        public Vector2 OutwardXZ;
    }
}
