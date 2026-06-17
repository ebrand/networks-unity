// Drops a row of KNOWN-real-world-size markers on the terrain surface below the camera,
// so you can eyeball scale against the landscape: a 1.8m person, a 10m house, a 100m
// tower, and a 1km pole. Each is snapped to the ground by raycasting the TerrainCollider,
// so it works on the DEM Unity Terrain (or any colliding surface). Pure scale-check tool.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class ScaleMarkers
    {
        const string RootName = "ScaleMarkers";

        public static void Clear()
        {
            var ex = GameObject.Find(RootName);
            if (ex != null) { if (Application.isPlaying) Object.Destroy(ex); else Object.DestroyImmediate(ex); }
        }

        public static void Drop()
        {
            var cam = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
            if (cam == null) { Debug.LogWarning("[ScaleMarkers] no camera."); return; }
            Clear();
            var root = new GameObject(RootName);

            Vector3 c = cam.transform.position;
            // size (W,H,D) metres, colour, X offset from the camera's XZ
            Add(root, "Person 1.8m", new Vector3(0.6f, 1.8f, 0.6f),  new Color(0.90f, 0.10f, 0.10f), c.x + 0f,    c.z);
            Add(root, "House 10m",   new Vector3(10f, 8f, 10f),      new Color(0.20f, 0.45f, 0.95f), c.x + 25f,   c.z);
            Add(root, "Tower 100m",  new Vector3(15f, 100f, 15f),    new Color(0.95f, 0.80f, 0.20f), c.x + 70f,   c.z);
            Add(root, "Pole 1km",    new Vector3(8f, 1000f, 8f),     new Color(0.25f, 0.85f, 0.35f), c.x + 700f,  c.z);

            Debug.Log($"[ScaleMarkers] dropped near X={c.x:0} Z={c.z:0}: Person 1.8m, House 10m, Tower 100m, Pole 1km " +
                      "(descend to ground level to compare).");
        }

        static void Add(GameObject root, string name, Vector3 size, Color col, float x, float z)
        {
            float groundY = GroundY(x, z);
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(root.transform, false);
            go.transform.localScale = size;                       // cube is 1m^3 → scale = exact metres
            go.transform.position = new Vector3(x, groundY + size.y * 0.5f, z);   // sit base on the ground

            var col2 = go.GetComponent<Collider>();
            if (col2 != null) Object.Destroy(col2);               // don't block the height raycast / picking

            var sh = Shader.Find("Universal Render Pipeline/Lit");
            var mat = sh != null ? new Material(sh) : new Material(Shader.Find("Sprites/Default"));
            mat.color = col;
            mat.SetColor("_BaseColor", col);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // Terrain height at a world XZ via a downward raycast (above any peak, well below base).
        static float GroundY(float x, float z)
        {
            if (Physics.Raycast(new Vector3(x, 12000f, z), Vector3.down, out RaycastHit hit, 30000f))
                return hit.point.y;
            return 0f;
        }
    }
}
