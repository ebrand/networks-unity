// Tag component attached to each in-scene vertex marker so the
// designer's raycast picking can recover the underlying network Vertex
// from a GameObject hit.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Designer
{
    public class VertexMarker : MonoBehaviour
    {
        // [NonSerialized]: prevent Unity's Inspector data-binding from
        // walking the Vertex's graph and auto-instantiating null
        // [Serializable] sub-objects (the bug that turned straight
        // roads into wild curves). See NetworkRenderer.Network for
        // the full explanation. Vertex is set programmatically by
        // NetworkDesigner.SpawnVertexMarker, never in the Inspector.
        [System.NonSerialized] public Vertex Vertex;
    }
}
