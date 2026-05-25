// Tag + cached state for the edit-mode handle that adjusts a road's
// cubic-bezier control point (Curve.ControlA or Curve.ControlB).
// Visually a small ring at the control's world XZ; dragging moves
// the control point so the curve reshapes in real time.
//
// For STRAIGHT roads (road.Curve == null) the handle is spawned at a
// "phantom" position on the chord (~1/3 or 2/3 from this end) so the
// user can grab it and bend the road into a curve — the drag handler
// materializes road.Curve on first movement.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Designer
{
    public class BezierControlHandle : MonoBehaviour
    {
        // [NonSerialized]: same Unity Inspector auto-instantiation trap
        // as the other back-reference tags. See NetworkRenderer.Network.
        [System.NonSerialized] public NetworkRoad Road;
        public RoadEnd End;
    }
}
