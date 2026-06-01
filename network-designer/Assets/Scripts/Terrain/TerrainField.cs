// The terrain data model: a regular grid heightfield. One float height (Y =
// elevation) per grid vertex; the grid lies in the XZ plane. Y-axis-first by
// design, so this stays compatible with the future stacked/3D-roadway work.
//
// Plain [Serializable] POCO (no UnityEngine rendering deps beyond Vector
// types) so it can be JSON-saved/loaded like the road Network later.

using System;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [Serializable]
    public class TerrainField
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

        // Nearest grid vertex (clamped) to a world XZ — handy for sculpt
        // brushes that work in grid space.
        public void WorldToNearestVertex(float worldX, float worldZ, out int x, out int z)
        {
            x = Mathf.Clamp(Mathf.RoundToInt((worldX - Origin.x) / CellSize), 0, ColumnsX - 1);
            z = Mathf.Clamp(Mathf.RoundToInt((worldZ - Origin.z) / CellSize), 0, RowsZ - 1);
        }
    }
}
