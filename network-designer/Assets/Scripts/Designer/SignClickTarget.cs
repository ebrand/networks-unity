// Tag component on the per-sign GameObject. Picked via Physics.Raycast
// in NetworkDesigner.PickSignClickTarget; click handler reads RoadId +
// End to cycle the road's ControlA / ControlB.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Designer
{
    public class SignClickTarget : MonoBehaviour
    {
        public string RoadId;
        public RoadEnd End;
    }
}
