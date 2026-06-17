// Marker for a tree placed via the designer's Tree mode. Lets right-click
// removal identify which scene object is a removable tree (walk up from the
// raycast hit to find this component, then destroy its GameObject).
//
// Trees are decorative scene objects parented under the tree container. They
// are mirrored in Network.Trees (PlacedTreeData) so they persist across
// autosave/load — see SpawnTreesFromModel / PlaceRandomTree / RemoveTreeUnderCursor.

using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Designer
{
    public class PlacedTree : MonoBehaviour
    {
        // The model entry this scene tree corresponds to, so right-click
        // removal can drop it from Network.Trees (and trigger autosave).
        public PlacedTreeData Data;
        // The spatial-hash cell this item was bucketed under at placement time
        // (depends on the spacing in effect then), so it can be removed from the
        // right bucket regardless of the current brush spacing.
        public long Cell;
    }
}
