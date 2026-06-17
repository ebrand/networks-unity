// Builds a FLAT-SHADED mesh for a rectangular cell region of a TerrainField —
// the low-poly look. Each quad's two triangles get their own un-shared
// vertices, so RecalculateNormals yields per-face (hard) normals: you see the
// facets. Vertices are world-space (the chunk GameObject sits at identity).
//
// Region = cells [cx0..cx1) x [cz0..cz1); vertex columns cx0..cx1 inclusive.
// Keep a chunk under ~104 cells/side so vert count stays in UInt16 range.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkDesigner.Terrain
{
    public static class TerrainChunkBuilder
    {
        static readonly List<Vector3> _verts = new List<Vector3>();
        static readonly List<int> _tris = new List<int>();

        public static void Build(TerrainField f, int cx0, int cz0, int cx1, int cz1, Mesh mesh)
        {
            float cs = f.CellSize;
            Vector3 o = f.Origin;
            _verts.Clear();
            _tris.Clear();

            for (int z = cz0; z < cz1; z++)
            {
                for (int x = cx0; x < cx1; x++)
                {
                    Vector3 v00 = new Vector3(o.x + x * cs,       o.y + f.GetHeight(x, z),       o.z + z * cs);
                    Vector3 v10 = new Vector3(o.x + (x + 1) * cs, o.y + f.GetHeight(x + 1, z),   o.z + z * cs);
                    Vector3 v01 = new Vector3(o.x + x * cs,       o.y + f.GetHeight(x, z + 1),   o.z + (z + 1) * cs);
                    Vector3 v11 = new Vector3(o.x + (x + 1) * cs, o.y + f.GetHeight(x + 1, z + 1), o.z + (z + 1) * cs);

                    int b = _verts.Count;
                    // Two tris wound for +Y face normals (see TerrainField layout).
                    _verts.Add(v00); _verts.Add(v01); _verts.Add(v10);
                    _verts.Add(v10); _verts.Add(v01); _verts.Add(v11);
                    _tris.Add(b);     _tris.Add(b + 1); _tris.Add(b + 2);
                    _tris.Add(b + 3); _tris.Add(b + 4); _tris.Add(b + 5);
                }
            }

            mesh.Clear();
            mesh.indexFormat = _verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_verts);
            mesh.SetTriangles(_tris, 0);
            mesh.RecalculateNormals(); // un-shared verts -> flat per-face normals
            mesh.RecalculateBounds();
        }
    }
}
