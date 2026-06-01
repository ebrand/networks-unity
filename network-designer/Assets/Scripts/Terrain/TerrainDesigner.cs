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

using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using NetworkDesigner.Designer; // SceneAmbiance

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

        [Header("Autosave (terrain persistence across Play stop/start)")]
        public bool Autosave = true;
        [Tooltip("Where the terrain is saved. Empty → project_root/TerrainAutosave.json " +
                 "in the Editor, persistentDataPath in a Player build.")]
        public string AutosavePath = "";
        [Tooltip("Seconds of no sculpting before the terrain is written to disk.")]
        public float AutosaveDebounceSeconds = 1f;

        TerrainField _field;
        float _dirtySince = -1f; // realtime when last edited; -1 = clean
        Mesh _mesh;
        Material _mat;
        MeshCollider _collider;
        bool _hasFlattenTarget;
        float _flattenTarget; // height offset (field space) captured on mouse-down

        public TerrainField Field => _field;

        [Header("Scene lighting")]
        [Tooltip("On Start, if the scene has no SceneAmbiance, create one that " +
                 "lights itself: a directional sun, soft shadows, ambient fill, " +
                 "and a large URP shadow distance. Turn off to manage lighting " +
                 "yourself in the Lighting window.")]
        public bool AutoLighting = true;
        [Tooltip("URP shadow distance (metres) the auto-lighting requests — " +
                 "should comfortably exceed the terrain footprint.")]
        public float ShadowDistance = 300f;

        void Start()
        {
            if (PickCamera == null) PickCamera = Camera.main;
            if (AutoLighting) EnsureAmbiance();

            if (Autosave) _field = TryLoadTerrain();
            if (_field == null)
            {
                EnsureField(forceRebuild: true); // fresh field (+ test hill)
            }
            else
            {
                // Adopt loaded dimensions; refresh Origin to the current
                // GameObject placement so sculpt mapping stays correct even if
                // the object moved between sessions.
                ColumnsX = _field.ColumnsX;
                RowsZ = _field.RowsZ;
                CellSize = _field.CellSize;
                float halfW = (ColumnsX - 1) * CellSize * 0.5f;
                float halfL = (RowsZ - 1) * CellSize * 0.5f;
                _field.Origin = transform.position - new Vector3(halfW, 0f, halfL);
            }
            RebuildMesh();
        }

        // If the (empty) scene has no SceneAmbiance, create one configured to
        // light itself — sun + soft shadows + ambient fill + URP shadow range.
        void EnsureAmbiance()
        {
            if (FindFirstObjectByType<SceneAmbiance>() != null) return;
            SceneAmbiance amb = new GameObject("SceneAmbiance").AddComponent<SceneAmbiance>();
            amb.CreateSunIfMissing = true;
            amb.ManageAmbient = true;
            amb.ShadowDistance = ShadowDistance;
            amb.Apply();
        }

        void Update()
        {
            // Brush-mode hotkeys.
            if (Input.GetKeyDown(KeyCode.Alpha1)) Brush = BrushMode.Raise;
            else if (Input.GetKeyDown(KeyCode.Alpha2)) Brush = BrushMode.Lower;
            else if (Input.GetKeyDown(KeyCode.Alpha3)) Brush = BrushMode.Smooth;
            else if (Input.GetKeyDown(KeyCode.Alpha4)) Brush = BrushMode.Flatten;

            if (_field == null) return;

            // Debounced autosave: write once sculpting has paused.
            if (Autosave && _dirtySince >= 0f
                && Time.realtimeSinceStartup - _dirtySince >= AutosaveDebounceSeconds)
            {
                SaveTerrain();
                _dirtySince = -1f;
            }

            if (Input.GetMouseButtonDown(0)) _hasFlattenTarget = false;
            if (!Input.GetMouseButton(0)) return;

            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (_collider == null || !_collider.Raycast(ray, out RaycastHit hit, 100000f)) return;

            if (!_hasFlattenTarget)
            {
                GridFromWorld(hit.point, out float cfx, out float cfz);
                _flattenTarget = HeightAtGrid(cfx, cfz);
                _hasFlattenTarget = true;
            }

            ApplyBrush(hit.point, Time.deltaTime);
            RebuildMesh();
            _dirtySince = Time.realtimeSinceStartup;
        }

        // World hit -> fractional grid coords, through the GameObject transform
        // so it's correct under any position/rotation/scale. The mesh is built
        // centered-local, so local (0,0) is the grid centre.
        void GridFromWorld(Vector3 worldHit, out float fx, out float fz)
        {
            float cs = _field.CellSize;
            float halfW = (_field.ColumnsX - 1) * cs * 0.5f;
            float halfL = (_field.RowsZ - 1) * cs * 0.5f;
            Vector3 local = transform.InverseTransformPoint(worldHit);
            fx = (local.x + halfW) / cs;
            fz = (local.z + halfL) / cs;
        }

        // Bilinear height (field offset space) at fractional grid coords.
        float HeightAtGrid(float fx, float fz)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, _field.ColumnsX - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, _field.RowsZ - 1);
            int x1 = Mathf.Min(x0 + 1, _field.ColumnsX - 1);
            int z1 = Mathf.Min(z0 + 1, _field.RowsZ - 1);
            float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
            float h0 = Mathf.Lerp(_field.GetHeight(x0, z0), _field.GetHeight(x1, z0), tx);
            float h1 = Mathf.Lerp(_field.GetHeight(x0, z1), _field.GetHeight(x1, z1), tx);
            return Mathf.Lerp(h0, h1, tz);
        }

        // Modify the heightfield under the brush, in field (height-offset) space.
        void ApplyBrush(Vector3 worldHit, float dt)
        {
            float cs = _field.CellSize;
            // Map the world hit to grid space through the GameObject transform
            // (handles any position/rotation/scale), NOT world-Origin algebra.
            GridFromWorld(worldHit, out float fx, out float fz);
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
            _dirtySince = Time.realtimeSinceStartup; // persist the reset
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

        // -----------------------------------------------------------------
        // Save / load (JSON, mirrors the road designer's autosave)
        // -----------------------------------------------------------------

        void OnDisable()
        {
            // Flush any pending edits when Play stops / the object is disabled.
            if (Autosave && _dirtySince >= 0f)
            {
                SaveTerrain();
                _dirtySince = -1f;
            }
        }

        string ResolveAutosavePath()
        {
            if (!string.IsNullOrEmpty(AutosavePath)) return AutosavePath;
#if UNITY_EDITOR
            return System.IO.Path.Combine(Application.dataPath, "..", "TerrainAutosave.json");
#else
            return System.IO.Path.Combine(Application.persistentDataPath, "TerrainAutosave.json");
#endif
        }

        public void SaveTerrain()
        {
            if (_field == null) return;
            try
            {
                string json = JsonConvert.SerializeObject(_field, TerrainJsonSettings);
                System.IO.File.WriteAllText(ResolveAutosavePath(), json);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Save failed: {ex.Message}");
            }
        }

        TerrainField TryLoadTerrain()
        {
            try
            {
                string path = ResolveAutosavePath();
                if (!System.IO.File.Exists(path)) return null;
                TerrainField f = JsonConvert.DeserializeObject<TerrainField>(
                    System.IO.File.ReadAllText(path), TerrainJsonSettings);
                // Reject missing/corrupt/mismatched data rather than crash.
                if (f == null || f.Heights == null) return null;
                if (f.ColumnsX < 2 || f.RowsZ < 2) return null;
                if (f.Heights.Length != f.ColumnsX * f.RowsZ) return null;
                return f;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TerrainDesigner] Load failed: {ex.Message} — starting fresh.");
                return null;
            }
        }

        static JsonSerializerSettings _terrainJsonSettings;
        static JsonSerializerSettings TerrainJsonSettings
        {
            get
            {
                if (_terrainJsonSettings == null)
                    _terrainJsonSettings = new JsonSerializerSettings
                    {
                        Formatting = Formatting.Indented,
                        Converters = new List<JsonConverter> { new Vector3JsonConverter() },
                        NullValueHandling = NullValueHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                    };
                return _terrainJsonSettings;
            }
        }

        // Vector3 as { x, y, z } — keeps Newtonsoft from chasing the derived
        // properties (normalized/magnitude) on UnityEngine.Vector3.
        class Vector3JsonConverter : JsonConverter<Vector3>
        {
            public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("x"); writer.WriteValue(value.x);
                writer.WritePropertyName("y"); writer.WriteValue(value.y);
                writer.WritePropertyName("z"); writer.WriteValue(value.z);
                writer.WriteEndObject();
            }

            public override Vector3 ReadJson(JsonReader reader, System.Type objectType,
                Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                Newtonsoft.Json.Linq.JObject jo = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new Vector3(
                    jo["x"]?.ToObject<float>() ?? 0f,
                    jo["y"]?.ToObject<float>() ?? 0f,
                    jo["z"]?.ToObject<float>() ?? 0f);
            }
        }
    }
}
