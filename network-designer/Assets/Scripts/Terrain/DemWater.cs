// A flat water plane over the DEM world at a chosen elevation. Because the DEM has real-world
// heights, a single level naturally floods coasts / valleys / lake basins. The plane is parented
// under the DEM root, so it shows/hides automatically with the DEM backend (DemTerrainWorld
// .SetVisible), and it carries no collider so it never blocks sculpt / rail / placeable raycasts.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class DemWater
    {
        const string Name = "DemWater (auto)";

        public static bool Show;
        public static float Level = 0f;                                   // world Y of the water surface
        public static Color Color = new Color(0.20f, 0.45f, 0.55f, 0.6f); // transparent blue
        public static float Smoothness = 0.85f;                           // reflective ocean by default

        static GameObject _go;
        static Material _mat;

        // (Re)build/position the plane from the current settings + DEM extent. Call on any control
        // change and after (re)building the DEM world.
        public static void Apply()
        {
            var go = EnsurePlane();
            if (go == null) return;
            go.SetActive(Show);
            if (!Show) return;
            float w = DemTerrainWorld.WorldWidthX, l = DemTerrainWorld.WorldLengthZ;
            go.transform.position = new Vector3(w * 0.5f, Level, l * 0.5f);
            go.transform.localScale = new Vector3(w / 10f + 20f, 1f, l / 10f + 20f);  // Unity Plane = 10 units
            if (_mat != null)
            {
                _mat.color = Color;
                if (_mat.HasProperty("_Smoothness")) _mat.SetFloat("_Smoothness", Smoothness);
            }
        }

        static GameObject EnsurePlane()
        {
            if (_go != null) return _go;   // Unity null-check: destroyed-with-the-DEM-root → null
            var root = DemTerrainWorld.Root;
            if (root == null) return null;  // no DEM world built yet
            _go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            _go.name = Name;
            var col = _go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            var mr = _go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            if (_mat == null) _mat = NetworkDesigner.PipelineMaterials.CreateLitTransparent(Color, Smoothness, "DemWaterMat");
            mr.sharedMaterial = _mat;
            _go.transform.SetParent(root.transform, worldPositionStays: true);   // hides/shows with the DEM
            return _go;
        }
    }
}
