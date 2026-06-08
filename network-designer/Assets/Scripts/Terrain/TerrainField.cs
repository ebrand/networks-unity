// The terrain data model: a regular grid heightfield. One float height (Y =
// elevation) per grid vertex; the grid lies in the XZ plane. Y-axis-first by
// design, so this stays compatible with the future stacked/3D-roadway work.
//
// Plain [Serializable] POCO (no UnityEngine rendering deps beyond Vector
// types) so it can be JSON-saved/loaded like the road Network later.

using System;
using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model; // PlacedTreeData

namespace NetworkDesigner.Terrain
{
    [Serializable]
    // Implements ITerrainSurface so it can be passed wherever a ground surface is read
    // (rail/road draping etc.) interchangeably with a DEM Unity-Terrain surface.
    public class TerrainField : ITerrainSurface
    {
        public int ColumnsX = 64;          // vertex count along X (>= 2)
        public int RowsZ = 64;             // vertex count along Z (>= 2)
        public float CellSize = 2f;        // metres between adjacent vertices
        public Vector3 Origin = Vector3.zero; // world position of vertex (0,0)
        public float[] Heights;            // length ColumnsX*RowsZ, row-major: z*ColumnsX + x

        public TerrainField() { }

        public TerrainField(int columnsX, int rowsZ, float cellSize, Vector3 origin)
        {
            ColumnsX = Mathf.Max(2, columnsX);
            RowsZ = Mathf.Max(2, rowsZ);
            CellSize = Mathf.Max(0.01f, cellSize);
            Origin = origin;
            Heights = new float[ColumnsX * RowsZ];
        }

        public int VertexCount => ColumnsX * RowsZ;
        public float WidthX => (ColumnsX - 1) * CellSize;
        public float LengthZ => (RowsZ - 1) * CellSize;
        Vector3 ITerrainSurface.Origin => Origin;   // Origin is a field; expose it for the interface

        public bool InRange(int x, int z) => x >= 0 && x < ColumnsX && z >= 0 && z < RowsZ;
        public int Index(int x, int z) => z * ColumnsX + x;

        public float GetHeight(int x, int z) => Heights[Index(x, z)];
        public void SetHeight(int x, int z, float h) => Heights[Index(x, z)] = h;

        // Bilinear elevation sample at a world XZ position (for the sculpt
        // cursor, and later for road-on-terrain conformance). Returns the base
        // elevation (Origin.y) outside the field footprint.
        public float SampleHeight(float worldX, float worldZ)
        {
            float fx = (worldX - Origin.x) / CellSize;
            float fz = (worldZ - Origin.z) / CellSize;
            if (fx < 0f || fz < 0f || fx > ColumnsX - 1 || fz > RowsZ - 1)
                return Origin.y;
            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);
            int x1 = Mathf.Min(x0 + 1, ColumnsX - 1);
            int z1 = Mathf.Min(z0 + 1, RowsZ - 1);
            float tx = fx - x0;
            float tz = fz - z0;
            float h0 = Mathf.Lerp(GetHeight(x0, z0), GetHeight(x1, z0), tx);
            float h1 = Mathf.Lerp(GetHeight(x0, z1), GetHeight(x1, z1), tx);
            return Origin.y + Mathf.Lerp(h0, h1, tz);
        }

        // Terrain steepness (degrees) at a world XZ: the angle of the surface
        // normal from straight up, via central-difference of the height field.
        // Matches the slope the TerrainSlope shader blends rock on. 0 = flat.
        public float SampleSlopeDegrees(float worldX, float worldZ)
        {
            float d = CellSize;
            float dhdx = (SampleHeight(worldX + d, worldZ) - SampleHeight(worldX - d, worldZ)) / (2f * d);
            float dhdz = (SampleHeight(worldX, worldZ + d) - SampleHeight(worldX, worldZ - d)) / (2f * d);
            // Surface normal = (-dh/dx, 1, -dh/dz) before normalising; its Y
            // component over its length gives cos(slope).
            float invLen = 1f / Mathf.Sqrt(dhdx * dhdx + dhdz * dhdz + 1f);
            return Mathf.Acos(Mathf.Clamp01(invLen)) * Mathf.Rad2Deg;
        }

        // Nearest grid vertex (clamped) to a world XZ — handy for sculpt
        // brushes that work in grid space.
        public void WorldToNearestVertex(float worldX, float worldZ, out int x, out int z)
        {
            x = Mathf.Clamp(Mathf.RoundToInt((worldX - Origin.x) / CellSize), 0, ColumnsX - 1);
            z = Mathf.Clamp(Mathf.RoundToInt((worldZ - Origin.z) / CellSize), 0, RowsZ - 1);
        }
    }

    // Sparse on-disk form of a TerrainField: grid dimensions + only the
    // altered (non-zero) heights, keyed by row-major index. Zeros are implied,
    // so a mostly-flat large terrain serializes tiny. Origin is NOT stored —
    // it's recomputed from the GameObject placement on load.
    [Serializable]
    public class TerrainSave
    {
        public int ColumnsX;
        public int RowsZ;
        public float CellSize;
        public int[] Idx;    // row-major indices of non-zero heights
        public float[] H;    // parallel heights for each Idx
        public List<PlacedTreeData> Trees; // painted trees (XZ + rotation + scale + prefab)
        public List<TreePack> Packs;       // saved tree-brush include/exclude presets
        public List<PlacedTreeData> Rocks; // painted rocks (same shape as Trees)
        public List<TreePack> RockPacks;   // saved rock-brush include/exclude presets
        public LineGraphSave Fences;       // fence linework graph (nodes + edges)
        public LineGraphSave PowerLines;   // power-line linework graph
        public LineGraphSave Rails;        // rail-track graph (procedural rails + ties)
        public LineGraphSave Plan;         // v7+: rail-planning survey alignment
        public bool HasCamera;             // v5+: a saved fly-camera pose follows
        public Vector3 CamPos;             // camera world position
        public float CamYaw;               // fly-camera yaw / pitch (its look state)
        public float CamPitch;
        public bool DemBackend;            // v8+: was the DEM backend active at save time
        public string DemCity;             // v8+: loaded DEM city (empty if none) — reloaded on restore
        public DemTerrainWorld.Edits DemEdits; // v9+: sparse DEM sculpt/carve height diff (null if none)
    }

    // A named tree-brush preset: the subset of TerrainDesigner.TreePrefabs (by
    // prefab name) that the brush paints when this pack is selected. Persisted
    // in TerrainSave so packs survive across Play sessions.
    [Serializable]
    public class TreePack
    {
        public string Name;
        public List<string> Trees = new List<string>(); // included prefab names
        // Brush settings captured with the pack (restored when it's applied).
        // HasParams=false for legacy packs saved before params existed -> applying
        // them leaves the current brush settings alone.
        public bool HasParams;
        public float PaintRate = 25f;
        public float Spacing = 4f;
        public float MaxSlopeDeg = 35f;
        public bool AvoidWater = true;
        public float WaterlineMargin = 1f;
    }
}
