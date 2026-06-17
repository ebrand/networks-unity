// Tag component attached to each spawned intersection mesh GameObject
// so the designer's raycast picking can recover the underlying Vertex
// from an asphalt-mesh hit (separate from the VertexMarker tag on the
// vertex puck — the puck and the intersection mesh are different
// GameObjects with different lifecycles).

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    public class IntersectionMarker : MonoBehaviour
    {
        // [NonSerialized]: prevent Unity's Inspector data-binding from
        // walking the Vertex's graph and auto-instantiating null
        // [Serializable] sub-objects. See NetworkRenderer.Network.
        [System.NonSerialized] public Vertex Vertex;
    }
}
