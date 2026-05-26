// Tag component on each spawned agent visual so the designer's
// raycast picker can recover the underlying Agent from a GameObject
// hit. Attached by AgentSystem.CreateVisual; the capsule's default
// CapsuleCollider (set as trigger so it doesn't affect physics)
// provides the raycast surface.

using UnityEngine;

namespace NetworkDesigner.Agents
{
    public class AgentClickTarget : MonoBehaviour
    {
        // [NonSerialized]: same Unity Inspector auto-instantiation trap
        // as the other back-reference tags. See NetworkRenderer.Network.
        [System.NonSerialized] public Agent Agent;
    }
}
