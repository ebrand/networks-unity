// Clickable marker placed at a single lane endpoint at the selected
// vertex (Edit mode only). NetworkDesigner spawns one per lane endpoint
// when a vertex is selected; the user clicks them to author lane-flow
// overrides in Vertex.ConnectivityOverrides.
//
// Mirrors the SetbackHandle pattern: sphere primitive + SphereCollider,
// owned/spawned by the designer rather than the renderer pool, so the
// markers come and go with the selection-and-edit-mode gate without
// the renderer needing to know about input modes.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Designer
{
    public class LaneEndpointMarker : MonoBehaviour
    {
        /// <summary>Vertex this endpoint belongs to.</summary>
        public string VertexId;
        /// <summary>The lane reference (road id + direction + index).</summary>
        public LaneRef Lane;
        /// <summary>
        /// Which of the 6 named nodes on the lane this marker
        /// represents. A and B are the centerline midpoint markers
        /// (used by lane FLOW arrows and connectivity overrides);
        /// Origin/Primary/Secondary/Tertiary are the lane corners
        /// (used by lane MARKINGS for finer placement).
        /// </summary>
        public LaneNode Node = LaneNode.A;
        /// <summary>True if traffic on this lane flows TOWARD the vertex (the click-to-edit "from" side).</summary>
        public bool IsInbound;
        /// <summary>World XZ position of the endpoint (cached so click logic doesn't re-query transform).</summary>
        public Vector2 WorldXZ;
    }
}
