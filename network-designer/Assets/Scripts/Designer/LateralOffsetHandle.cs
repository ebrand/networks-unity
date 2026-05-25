// Tag + cached drag state for the edit-mode handle that adjusts a
// road's per-end LateralOffset. Visually a small ring at the
// approach's effective centerline endpoint (i.e. the lane-flow
// midpoint at the vertex, shifted by current offset). Dragging it
// perpendicular to the road's outward direction at that end
// writes the projected signed distance into NetworkRoad.LateralOffsetA
// or LateralOffsetB.
//
// Separate from SetbackHandle: that one moves the setback midpoint
// along the road's outward direction. This one shifts the centerline
// perpendicular — orthogonal axes, distinct visual handle so they
// don't get conflated.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Designer
{
    public class LateralOffsetHandle : MonoBehaviour
    {
        // [NonSerialized]: same Unity Inspector auto-instantiation trap
        // as the other back-reference tags. See NetworkRenderer.Network.
        [System.NonSerialized] public NetworkRoad Road;
        public RoadEnd End;

        /// <summary>True (un-shifted) vertex world XZ position — the
        /// origin from which the perpendicular drag axis emanates and
        /// from which the resulting offset distance is measured.</summary>
        public Vector2 VertexXZ;

        /// <summary>Unit perpendicular-right direction (CW 90° of the
        /// road's outward bearing at this end). Drag projects cursor
        /// onto this axis; positive projection = LateralOffset writes
        /// a positive value (perp-right of A→B).</summary>
        public Vector2 PerpRightXZ;
    }
}
