// Tag component the NetworkRenderer attaches to each spawned road
// GameObject, so a raycast hit on the road mesh can be mapped back to
// the underlying NetworkRoad.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    public class RoadMarker : MonoBehaviour
    {
        public NetworkRoad Road;
    }
}
