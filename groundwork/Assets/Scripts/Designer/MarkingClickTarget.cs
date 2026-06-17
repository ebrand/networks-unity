// Tag component on the per-marking GameObject inside an intersection's
// LaneMarkingsRenderer. Picked via Physics.Raycast in NetworkDesigner;
// left-click cycles style, right-click deletes.

using UnityEngine;

namespace NetworkDesigner.Designer
{
    public class MarkingClickTarget : MonoBehaviour
    {
        public string VertexId;
        public string MarkingId;
    }
}
