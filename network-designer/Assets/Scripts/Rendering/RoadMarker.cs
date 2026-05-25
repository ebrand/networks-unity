// Tag component the NetworkRenderer attaches to each spawned road
// GameObject, so a raycast hit on the road mesh can be mapped back to
// the underlying NetworkRoad.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    public class RoadMarker : MonoBehaviour
    {
        // [NonSerialized]: prevent Unity's Inspector data-binding from
        // walking the Road's graph and auto-instantiating null
        // [Serializable] sub-objects (notably RoadCurve). See
        // NetworkRenderer.Network for the full explanation.
        [System.NonSerialized] public NetworkRoad Road;
    }
}
