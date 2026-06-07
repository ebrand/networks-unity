// Proof-of-life for the Toby Foliage Engine (TTFE) on the DEM terrain.
//
// TTFE is a shader + wind system, not a placer: the grass is ordinary URP (Amplify)
// materials whose sway/season are driven by GLOBAL shader values pushed by a singleton
// "(TTFE) GLOBAL CONTROLLER". So getting real grass on screen is two steps:
//   1. make sure ONE Global Controller exists (Resources.Load it — it lives in a
//      Resources folder), so the TTFE materials get their wind/season globals;
//   2. scatter a patch of the pack's real PV_Grass LOD prefabs on the terrain surface.
//
// This is deliberately a small hand-scatter near where the camera is looking — enough to
// judge whether the TTFE look is what we want before committing to a scaled approach
// (terrain-detail instancing vs a streamed scatterer). Editor-time tool: it loads the
// pack prefabs via AssetDatabase, same as DemTerrainWorld loads its TerrainLayers.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class FoliageProbe
    {
        const string RootName = "FoliageProbe";
        const string ControllerResource = "(TTFE) GLOBAL CONTROLLER/(TTFE) GLOBAL CONTROLLER";
        const string GrassFolder =
            "Assets/Toby Fredson/Rocky Hills Environment - Whitebark Pine/RHEWP_Demo/Prefabs/Prefabs_Vegetation/PV_Grass";

        public static int LastCount { get; private set; }

        public static void Clear()
        {
            var ex = GameObject.Find(RootName);
            if (ex != null) { if (Application.isPlaying) Object.Destroy(ex); else Object.DestroyImmediate(ex); }
            LastCount = 0;
        }

        // Drop a jittered patch of real TTFE grass where the camera is looking.
        public static void Spawn(float radius = 30f, int count = 500)
        {
            var cam = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
            if (cam == null) { Debug.LogWarning("[FoliageProbe] no camera."); return; }

            EnsureController();

            var prefabs = LoadGrassPrefabs();
            if (prefabs.Count == 0)
            {
                Debug.LogWarning($"[FoliageProbe] no grass prefabs found under '{GrassFolder}'. " +
                                 "Is the Rocky Hills pack imported at that path?");
                return;
            }

            // Anchor the patch on the ground the camera is looking at (screen centre ray);
            // fall back to straight down from the camera if it isn't pointed at terrain.
            Vector3 center;
            Ray look = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            if (Physics.Raycast(look, out RaycastHit lookHit, 200000f)) center = lookHit.point;
            else if (Physics.Raycast(new Vector3(cam.transform.position.x, 12000f, cam.transform.position.z),
                                     Vector3.down, out RaycastHit downHit, 30000f)) center = downHit.point;
            else { Debug.LogWarning("[FoliageProbe] camera isn't looking at the terrain."); return; }

            Clear();
            var root = new GameObject(RootName);

            // A jittered square lattice covering the radius, deterministic (no Random global state).
            int side = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(count)));
            float step = (radius * 2f) / side;
            int placed = 0, skipped = 0;
            var rng = new System.Random(12345);

            for (int gz = 0; gz < side; gz++)
            for (int gx = 0; gx < side; gx++)
            {
                float jx = (float)(rng.NextDouble() - 0.5) * step;
                float jz = (float)(rng.NextDouble() - 0.5) * step;
                float wx = center.x - radius + (gx + 0.5f) * step + jx;
                float wz = center.z - radius + (gz + 0.5f) * step + jz;

                // keep it circular-ish
                if ((wx - center.x) * (wx - center.x) + (wz - center.z) * (wz - center.z) > radius * radius) continue;

                if (!Physics.Raycast(new Vector3(wx, center.y + 4000f, wz), Vector3.down, out RaycastHit hit, 30000f))
                { skipped++; continue; }

                var prefab = prefabs[rng.Next(prefabs.Count)];
                var go = Instantiate(prefab, root.transform);
                go.transform.position = hit.point;
                go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                float s = 0.8f + (float)rng.NextDouble() * 0.5f;        // 0.8–1.3x clump variation
                go.transform.localScale = Vector3.one * s;
                placed++;
            }

            LastCount = placed;
            Debug.Log($"[FoliageProbe] placed {placed} grass clumps (r={radius}m) at " +
                      $"X={center.x:0} Z={center.z:0}; {skipped} cells missed the ground.");
        }

        // Instantiate respecting whether we're an editor asset (keeps prefab connection in edit mode).
        static GameObject Instantiate(GameObject prefab, Transform parent)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetParent(parent, true);
                return go;
            }
#endif
            return Object.Instantiate(prefab, parent);
        }

        // Make sure exactly one TTFE Global Controller is in the scene so the grass shaders
        // get their wind/season globals (otherwise they render with default/zero globals).
        static void EnsureController()
        {
            if (GameObject.Find("(TTFE) GLOBAL CONTROLLER") != null) return;
            // any object whose name starts with the controller is good enough; also check by type-ish name
            var existing = GameObject.Find(ControllerResource);
            if (existing != null) return;

            var prefab = Resources.Load<GameObject>(ControllerResource);
            if (prefab == null)
            {
                Debug.LogWarning("[FoliageProbe] couldn't Resources.Load the (TTFE) GLOBAL CONTROLLER — " +
                                 "grass will still render but without wind/season globals.");
                return;
            }
            var inst = Object.Instantiate(prefab);
            inst.name = "(TTFE) GLOBAL CONTROLLER";
            Debug.Log("[FoliageProbe] spawned (TTFE) GLOBAL CONTROLLER.");
        }

        static List<GameObject> LoadGrassPrefabs()
        {
            var list = new List<GameObject>();
#if UNITY_EDITOR
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { GrassFolder }))
            {
                string p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var go = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go != null) list.Add(go);
            }
#endif
            return list;
        }
    }
}
