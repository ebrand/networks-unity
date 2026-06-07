// The terrain abstraction seam. Everything that reads the ground (rail, fences,
// power lines, scatter, agents, the generator's draping) will go through
// ITerrainSurface instead of touching TerrainField directly — so the same
// consumers work whether the ground is the current low-poly chunked field, a
// tiled Unity Terrain, or a streaming DEM/procedural world.
//
// STEP 1a (this file): define the read surface and a zero-behaviour-change
// adapter over the existing TerrainField. Nothing is rewired to it yet, so this
// is purely additive and cannot regress anything. Consumers get routed through
// it in a later step (the mechanical-but-broad change), and a UnityTerrain
// implementation comes after that. The height-WRITE path (sculpt/carve) and
// grid-cell mapping are deliberately NOT here yet — that's a later extension.
//
// See the "dual-backend-terrain" design note for the full plan.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    // The minimal read surface today's consumers actually use: sample the ground
    // height / slope at a world XZ, plus the world extent for draping/framing.
    public interface ITerrainSurface
    {
        // Ground height (world Y) at a world XZ. Bilinear within the surface.
        float SampleHeight(float worldX, float worldZ);

        // Terrain slope in degrees at a world XZ (0 = flat).
        float SampleSlopeDegrees(float worldX, float worldZ);

        // World position of the surface's lower-left corner.
        Vector3 Origin { get; }

        // Extent in metres along X / Z.
        float WidthX { get; }
        float LengthZ { get; }
    }

    // Adapter over the current low-poly heightfield. A thin pass-through — the
    // existing TerrainField IS the implementation; this just presents it behind
    // the interface so consumers can be ground-agnostic.
    public sealed class LowPolyTerrainSurface : ITerrainSurface
    {
        readonly TerrainField _field;

        public LowPolyTerrainSurface(TerrainField field) { _field = field; }

        public float SampleHeight(float worldX, float worldZ) => _field.SampleHeight(worldX, worldZ);
        public float SampleSlopeDegrees(float worldX, float worldZ) => _field.SampleSlopeDegrees(worldX, worldZ);
        public Vector3 Origin => _field.Origin;
        public float WidthX => _field.WidthX;
        public float LengthZ => _field.LengthZ;
    }

    // Surface over the DEM Unity-Terrain grid. Forwards to DemTerrainWorld's tile-aware
    // sampling, so the same rail/road consumers drape onto real-world terrain when handed
    // this instead of a low-poly field.
    public sealed class DemTerrainSurface : ITerrainSurface
    {
        public float SampleHeight(float worldX, float worldZ) => DemTerrainWorld.SampleHeight(worldX, worldZ);
        public float SampleSlopeDegrees(float worldX, float worldZ) => DemTerrainWorld.SampleSlopeDegrees(worldX, worldZ);
        public Vector3 Origin => DemTerrainWorld.WorldOrigin;
        public float WidthX => DemTerrainWorld.WorldWidthX;
        public float LengthZ => DemTerrainWorld.WorldLengthZ;
    }
}
