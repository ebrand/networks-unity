// Terrain designer — Slice 1: build + render a grid-heightfield terrain in
// URP. Sculpt brushes and save/load come in later slices.
//
// Setup: put this on an empty GameObject (ideally at world origin, like the
// road designer's GroundGrid). RequireComponent adds the MeshFilter /
// MeshRenderer. The mesh is centered on the GameObject, so its transform
// positions the terrain. A MeshCollider is added for the upcoming sculpt
// raycasts.
//
// The "test hill" is a temporary Slice-1 affordance so there's visible 3D
// relief to confirm the mesh, normals, and URP lighting before any editing
// tools exist. Turn it off (or it'll fight your edits) once sculpting lands.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TerrainDesigner : MonoBehaviour
    {
        [Header("Grid")]
        [Tooltip("Vertex count along X / Z. Total verts = ColumnsX * RowsZ. " +
                 "Keep the product under ~250k for a snappy MVP.")]
        public int ColumnsX = 64;
        public int RowsZ = 64;
        [Tooltip("Metres between adjacent grid vertices.")]
        public float CellSize = 2f;

        [Header("Appearance")]
        public Color TerrainColor = new Color(0.42f, 0.5f, 0.30f); // grassy
        [Range(0f, 1f)] public float Smoothness = 0f;

        [Header("Slice-1 test relief (temporary)")]
        [Tooltip("Stamp a smooth gaussian hill so there's visible 3D relief " +
                 "before sculpt tools exist. Turn OFF once sculpting lands.")]
        public bool TestHill = true;
        public float TestHillHeight = 25f;

        TerrainField _field;
        Mesh _mesh;
        Material _mat;

        public TerrainField Field => _field;

        void Start() { Rebuild(); }

        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            int cx = Mathf.Max(2, ColumnsX);
            int rz = Mathf.Max(2, RowsZ);
            float cs = Mathf.Max(0.01f, CellSize);

            // World position of vertex (0,0): the field is centered on this
            // GameObject, so the corner sits at -half-extents from it.
            float halfW = (cx - 1) * cs * 0.5f;
            float halfL = (rz - 1) * cs * 0.5f;
            Vector3 origin = transform.position - new Vector3(halfW, 0f, halfL);

            if (_field == null || _field.ColumnsX != cx || _field.RowsZ != rz)
            {
                _field = new TerrainField(cx, rz, cs, origin);
            }
            else
            {
                _field.CellSize = cs;
                _field.Origin = origin;
            }

            if (TestHill) StampTestHill();

            if (_mesh == null) _mesh = new Mesh { name = "TerrainMesh" };
            TerrainMeshBuilder.Build(_field, _mesh);
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            if (_mat == null)
                _mat = PipelineMaterials.CreateLit(TerrainColor, Smoothness, "TerrainMat");
            else
                _mat.color = TerrainColor;
            GetComponent<MeshRenderer>().sharedMaterial = _mat;

            // Collider for upcoming sculpt raycasts. Reassign to force refresh.
            MeshCollider mc = GetComponent<MeshCollider>();
            if (mc == null) mc = gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = null;
            mc.sharedMesh = _mesh;
        }

        // Temporary: a single gaussian hill centered in the grid.
        void StampTestHill()
        {
            int cx = _field.ColumnsX, rz = _field.RowsZ;
            float cxh = (cx - 1) * 0.5f, rzh = (rz - 1) * 0.5f;
            float sigma = Mathf.Max(1f, Mathf.Min(cx, rz) * 0.18f);
            float twoSigSq = 2f * sigma * sigma;
            for (int z = 0; z < rz; z++)
            {
                for (int x = 0; x < cx; x++)
                {
                    float dx = x - cxh, dz = z - rzh;
                    float g = Mathf.Exp(-(dx * dx + dz * dz) / twoSigSq);
                    _field.SetHeight(x, z, g * TestHillHeight);
                }
            }
        }
    }
}
