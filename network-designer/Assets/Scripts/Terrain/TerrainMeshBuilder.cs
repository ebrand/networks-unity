// Builds a renderable Mesh from a TerrainField grid heightfield.
//
// Vertices are emitted in LOCAL space CENTERED on the mesh origin (so the
// owning GameObject's transform positions the terrain; move the object and
// the terrain follows, no double-offset). Triangles are wound CW-from-above
// so face normals point +Y (lit from the sky, single-sided is fine).

using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkDesigner.Terrain
{
    public static class TerrainMeshBuilder
    {
        public static void Build(TerrainField field, Mesh mesh)
        {
            int cx = field.ColumnsX, rz = field.RowsZ;
            float cs = field.CellSize;
            float halfW = (cx - 1) * cs * 0.5f;
            float halfL = (rz - 1) * cs * 0.5f;

            int vcount = cx * rz;
            var verts = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            for (int z = 0; z < rz; z++)
            {
                for (int x = 0; x < cx; x++)
                {
                    int i = field.Index(x, z);
                    verts[i] = new Vector3(x * cs - halfW, field.Heights[i], z * cs - halfL);
                    uvs[i] = new Vector2((float)x / (cx - 1), (float)z / (rz - 1));
                }
            }

            int quadCols = cx - 1, quadRows = rz - 1;
            var tris = new int[quadCols * quadRows * 6];
            int t = 0;
            for (int z = 0; z < quadRows; z++)
            {
                for (int x = 0; x < quadCols; x++)
                {
                    int i0 = z * cx + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + cx;
                    int i3 = i2 + 1;
                    // (i0,i2,i1) and (i1,i2,i3) both yield +Y face normals.
                    tris[t++] = i0; tris[t++] = i2; tris[t++] = i1;
                    tris[t++] = i1; tris[t++] = i2; tris[t++] = i3;
                }
            }

            mesh.Clear();
            mesh.indexFormat = vcount > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
