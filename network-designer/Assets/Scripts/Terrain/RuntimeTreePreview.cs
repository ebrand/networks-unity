// Renders a small thumbnail of a prefab at RUNTIME by snapping an off-screen
// camera into a RenderTexture. Works under URP and in player builds — unlike
// UnityEditor.AssetPreview, which renders MAGENTA under URP (its internal
// preview render doesn't set up the SRP for most shaders) and is editor-only.
//
// Synchronous and a little heavy (instantiate + render + readback per call),
// so the caller should cache the returned Texture2D and generate at most a few
// per frame. The temporary camera/light/instance live for a single manual
// Render() far below the world, then are destroyed before the main camera
// renders that frame — so the scene never sees the rig or its light.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public static class RuntimeTreePreview
    {
        const int PreviewLayer = 31; // assumed unused in the terrain scene

        public static Texture2D Generate(GameObject prefab, int size)
        {
            if (prefab == null || size < 8) return null;

            // Stage the rig far below the world so it can't intersect anything.
            Vector3 stage = new Vector3(0f, -10000f, 0f);
            GameObject instance = Object.Instantiate(prefab, stage, Quaternion.identity);
            SetLayerRecursive(instance, PreviewLayer);

            if (!TryGetBounds(instance, out Bounds b))
            {
                Object.DestroyImmediate(instance);
                return null;
            }

            float radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
            if (radius < 1e-3f) radius = 1f;

            GameObject camGo = new GameObject("__TreePreviewCam");
            Camera cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << PreviewLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f); // transparent
            cam.orthographic = true;
            cam.orthographicSize = radius * 1.15f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = radius * 40f + 100f;
            cam.enabled = false; // rendered manually

            // 3/4 view from front-upper-right.
            Quaternion viewRot = Quaternion.Euler(18f, -28f, 0f);
            Vector3 dir = viewRot * Vector3.forward;
            camGo.transform.SetPositionAndRotation(b.center - dir * (radius * 8f), viewRot);

            // A directional light so the thumbnail isn't flat ambient. Exists
            // only for this synchronous render; destroyed before frame end.
            GameObject lightGo = new GameObject("__TreePreviewLight");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -25f, 0f);

            RenderTexture rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevActive = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();

            RenderTexture.active = prevActive;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);

            Object.DestroyImmediate(lightGo);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(instance);
            return tex;
        }

        static bool TryGetBounds(GameObject go, out Bounds bounds)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            bounds = new Bounds();
            bool has = false;
            foreach (Renderer r in rs)
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!has) { bounds = r.bounds; has = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return has;
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }
    }
}
