// A drawable node/edge "line" layer on the terrain — fences, power lines, rail
// track, etc. TerrainDesigner drives every one of them through this interface,
// so the click-to-chain drawing, ghost preview, and save/load input handling is
// written once and each layer just decides what geometry an edge produces.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public interface ITerrainLineLayer
    {
        string LayerName { get; }

        // Editing (chain drawing): add a node at a world hit and connect from the
        // last; end the current chain; undo the last node; delete the nearest.
        void AddNode(TerrainField field, Vector3 hit);
        void EndChain();
        void RemoveLastNode(TerrainField field);
        bool DeleteNearNode(TerrainField field, Vector3 hit, float radius);

        // Ghost placement preview at the cursor; hide it when leaving the mode.
        void UpdatePreview(TerrainField field, Vector3 cursor, bool show);
        void HidePreview();

        // (Re)generate the rendered result from the graph; wipe the graph.
        void Rebuild(TerrainField field);
        void ClearAll(TerrainField field);

        // Persistence (the node/edge graph; geometry is regenerated on load).
        LineGraphSave CollectData();
        void LoadState(LineGraphSave save);
    }
}
