// Tag component attached to each in-scene vertex marker so the
// designer's raycast picking can recover the underlying network Vertex
// from a GameObject hit.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Designer
{
    public class VertexMarker : MonoBehaviour
    {
        public Vertex Vertex;
    }
}
