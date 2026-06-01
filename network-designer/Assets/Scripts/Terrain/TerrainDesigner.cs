// Terrain designer — grid-heightfield terrain with sculpt brushes (URP).
//
// Setup: put this on an empty GameObject (ideally at world origin, like the
// road designer's GroundGrid). RequireComponent adds the MeshFilter /
// MeshRenderer; a MeshCollider is added for the sculpt raycast. The mesh is
// centered on the GameObject, so its transform positions the terrain.
//
// Sculpting runs in Play mode: hold the left mouse button over the terrain
// and drag. Brush mode: 1=Raise, 2=Lower, 3=Smooth, 4=Flatten (or set in the
// Inspector). Save/load is a later slice.
//
// The "test hill" is stamped ONCE when the field is first created, so it's
// just starting relief you can sculpt on top of — it is not re-applied on
// rebuilds.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TerrainDesigner : MonoBehaviour
    {
        public enum BrushMode { Raise, Lower, Smooth, Flatten }

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

        [Header("Sculpt brush")]
        public BrushMode Brush = BrushMode.Raise;
        [Tooltip("Brush radius in metres.")]
        public float BrushRadius = 10f;
        [Tooltip("Height change rate (metres/second) at the brush centre.")]
        public float BrushStrength = 20f;
        [Tooltip("0 = hard edge, 1 = soft (smoothstep) falloff to the rim.")]
        [Range(0f, 1f)] public float BrushFalloff = 0.7f;
        [Tooltip("Camera used for the sculpt raycast. Defaults to Camera.main.")]
        public Camera PickCamera;

        [Header("Initial relief (stamped once)")]
        [Tooltip("Stamp a smooth gaussian hill when the field is first built, " +
                 "so there's something to sculpt. Does NOT re-apply on rebuild.")]
        public bool TestHill = true;
        public float TestHillHeight = 25f;

        TerrainField _field;
        Mesh _mesh;
        Material _mat;
        MeshCollider _collider;
        bool _hasFlattenTarget;
        float _flattenTarget; // height offset (field space) captured on mouse-down

        public TerrainField Field => _field;

        void Start()
        {
            if (PickCamera == null) PickCamera = Camera.main;
            EnsureField(forceRebuild: true);
            RebuildMesh();
        }

        void Update()
        {
            // Brush-mode hotkeys.
            if (Input.GetKeyDown(KeyCode.Alpha1)) Brush = BrushMode.Raise;
            else if (Input.GetKeyDown(KeyCode.Alpha2)) Brush = BrushMode.Lower;
            else if (Input.GetKeyDown(KeyCode.Alpha3)) Brush = BrushMode.Smooth;
            else if (Input.GetKeyDown(KeyCode.Alpha4)) Brush = BrushMode.Flatten;

            if (_field == null) return;
            if (Input.GetMouseButtonDown(0)) _hasFlattenTarget = false;
            if (!Input.GetMouseButton(0)) return;

            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (_collider == null || !_collider.Raycast(ray, out RaycastHit hit, 100000f)) return;

            if (!_hasFlattenTarget)
            {
                _flattenTarget = _field.SampleHeight(hit.point.x, hit.point.z) - _field.Origin.y;
                _hasFlattenTarget = true;
            }

            ApplyBrush(hit.point, Time.deltaTime);
            RebuildMesh();
        }

        // Modify the heightfield under the brush, in field (height-offset) space.
        void ApplyBrush(Vector3 worldHit, float dt)
        {
            float cs = _field.CellSize;
            float fx = (worldHit.x - _field.Origin.x) / cs;
            float fz = (worldHit.z - _field.Origin.z) / cs;
            int cx0 = Mathf.RoundToInt(fx);
            int cz0 = Mathf.RoundToInt(fz);
            int rad = Mathf.Max(1, Mathf.CeilToInt(BrushRadius / cs));

            for (int dz = -rad; dz <= rad; dz++)
            {
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int x = cx0 + dx, z = cz0 + dz;
                    if (!_field.InRange(x, z)) continue;

                    // Metres from the brush centre (use the float centre).
                    float mx = (x - fx) * cs, mz = (z - fz) * cs;
                    float dist = Mathf.Sqrt(mx * mx + mz * mz);
                    if (dist > BrushRadius) continue;

                    float tEdge = BrushRadius > 0f ? dist / BrushRadius : 0f;
                    // BrushFalloff blends between a flat (hard) profile and a
                    // smoothstep that eases to zero at the rim.
                    float soft = 1f - Mathf.SmoothStep(0f, 1f, tEdge);
                    float w = Mathf.Lerp(1f - tEdge, soft, BrushFalloff);

                    float h = _field.GetHeight(x, z);
                    switch (Brush)
                    {
                        case BrushMode.Raise:
                            h += BrushStrength * dt * w;
                            break;
                        case BrushMode.Lower:
                            h -= BrushStrength * dt * w;
                            break;
                        case BrushMode.Flatten:
                            h = Mathf.Lerp(h, _flattenTarget, Mathf.Clamp01(dt * 4f * w));
                            break;
                        case BrushMode.Smooth:
                            h = Mathf.Lerp(h, NeighborAverage(x, z), Mathf.Clamp01(dt * 4f * w));
                            break;
                    }
                    _field.SetHeight(x, z, h);
                }
            }
        }

        // Average of the in-range 4-neighbours (falls back to self at edges).
        float NeighborAverage(int x, int z)
        {
            float sum = 0f; int n = 0;
            if (_field.InRange(x - 1, z)) { sum += _field.GetHeight(x - 1, z); n++; }
            if (_field.InRange(x + 1, z)) { sum += _field.GetHeight(x + 1, z); n++; }
            if (_field.InRange(x, z - 1)) { sum += _field.GetHeight(x, z - 1); n++; }
            if (_field.InRange(x, z + 1)) { sum += _field.GetHeight(x, z + 1); n++; }
            return n > 0 ? sum / n : _field.GetHeight(x, z);
        }

        // (Re)create the field. Stamps the test hill only on a fresh field.
        void EnsureField(bool forceRebuild)
        {
            int cx = Mathf.Max(2, ColumnsX);
            int rz = Mathf.Max(2, RowsZ);
            float cs = Mathf.Max(0.01f, CellSize);
            float halfW = (cx - 1) * cs * 0.5f;
            float halfL = (rz - 1) * cs * 0.5f;
            Vector3 origin = transform.position - new Vector3(halfW, 0f, halfL);

            bool fresh = _field == null || _field.ColumnsX != cx || _field.RowsZ != rz;
            if (fresh || forceRebuild)
            {
                if (fresh)
                {
                    _field = new TerrainField(cx, rz, cs, origin);
                    if (TestHill) StampTestHill();
                }
                else
                {
                    _field.CellSize = cs;
                    _field.Origin = origin;
                }
            }
        }

        // Full reset: new flat field (+ optional test hill) and rebuild.
        [ContextMenu("Reset Terrain")]
        public void ResetTerrain()
        {
            _field = null;
            EnsureField(forceRebuild: true);
            RebuildMesh();
        }

        // Rebuild the render mesh + collider from the current field. Cheap
        // enough to call every drag frame at MVP grid sizes.
        public void RebuildMesh()
        {
            if (_field == null) EnsureField(forceRebuild: true);

            if (_mesh == null) _mesh = new Mesh { name = "TerrainMesh" };
            TerrainMeshBuilder.Build(_field, _mesh);
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            if (_mat == null)
                _mat = PipelineMaterials.CreateLit(TerrainColor, Smoothness, "TerrainMat");
            else
                _mat.color = TerrainColor;
            GetComponent<MeshRenderer>().sharedMaterial = _mat;

            if (_collider == null)
            {
                _collider = GetComponent<MeshCollider>();
                if (_collider == null) _collider = gameObject.AddComponent<MeshCollider>();
            }
            _collider.sharedMesh = null;
            _collider.sharedMesh = _mesh;
        }

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
