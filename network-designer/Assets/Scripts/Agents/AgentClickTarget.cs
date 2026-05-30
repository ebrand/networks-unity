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
        // The MeshRenderer to tint for selection / blocker highlights.
        // - Capsule fallback: the root's own MeshRenderer.
        // - Vehicle prefab: the body's MeshRenderer (a child).
        // Cached at spawn so the per-frame tint code doesn't need to
        // re-search the hierarchy.
        [System.NonSerialized] public MeshRenderer BodyRenderer;
        // True when this agent's visual is a vehicle prefab instance
        // (as opposed to the capsule fallback). PositionVisual uses
        // this to pick the right Y placement (vehicle prefabs are
        // ground-anchored at their pivot; capsule needs AgentYLift).
        [System.NonSerialized] public bool IsVehicle;
    }
}
