// A PERSISTENT off-screen prefab viewer for the pack-management modal's live 3D
// preview. Unlike RuntimeTreePreview (a one-shot thumbnail bake), this keeps a
// camera + a single instantiated prefab alive and re-renders into a RenderTexture
// each frame, so the prefab can be orbited (drag-to-rotate / auto-spin).
//
// The instance lives on an assumed-unused layer (31), staged far below the world,
// and the preview camera renders ONLY that layer — so the main scene never shows
// it. The directional light is toggled on only for the synchronous render call,
// the same trick RuntimeTreePreview uses, so it can't bleed into scene lighting.
//
// URP: renders via SubmitRenderRequest (no per-frame "Camera.Render" warning),
// falling back to Camera.Render() if render requests aren't supported.

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NetworkDesigner.Terrain
{
    public class RuntimePrefabViewer
    {
        const int Layer = 31; // assumed unused in the terrain scene (matches RuntimeTreePreview)

        readonly Camera _cam;
        readonly GameObject _camGo, _lightGo;
        readonly RenderTexture _rt;
        GameObject _instance, _prefab;
        Bounds _bounds;
        float _radius = 1f;
        bool _disposed;

        public float Yaw = -28f;
        public float Pitch = 16f;
        public RenderTexture Texture => _rt;

        public RuntimePrefabViewer(int width, int height, Color background)
        {
            int w = Mathf.Max(16, width), hgt = Mathf.Max(16, height);
            _rt = new RenderTexture(w, hgt, 24, RenderTextureFormat.ARGB32) { name = "__PackPreviewRT" };
            _rt.Create();

            _camGo = new GameObject("__PackPreviewCam") { hideFlags = HideFlags.DontSave };
            _cam = _camGo.AddComponent<Camera>();
            _cam.cullingMask = 1 << Layer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = background;
            _cam.fieldOfView = 28f;                 // vertical FOV; framing fits the object vertically
            _cam.aspect = (float)w / hgt;           // match the wide RT so the view isn't squashed
            _cam.nearClipPlane = 0.01f;
            _cam.farClipPlane = 5000f;
            _cam.enabled = false;          // never auto-renders in the main loop; driven manually
            _cam.targetTexture = _rt;

            _lightGo = new GameObject("__PackPreviewLight") { hideFlags = HideFlags.DontSave };
            Light light = _lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.cullingMask = 1 << Layer; // belt-and-suspenders with the active-toggle below
            _lightGo.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
            _lightGo.SetActive(false);      // on only for the synchronous render, so the scene is untouched
        }

        // Swap the previewed prefab (no-op if unchanged). Instantiates a staged copy
        // on the preview layer and measures its bounds for framing.
        public void SetPrefab(GameObject prefab)
        {
            if (_disposed || prefab == _prefab) return;
            _prefab = prefab;
            if (_instance != null) Object.Destroy(_instance);
            _instance = null;
            if (prefab == null) return;

            _instance = Object.Instantiate(prefab, new Vector3(0f, -10000f, 0f), Quaternion.identity);
            SetLayerRecursive(_instance, Layer);
            // Strip colliders so the staged copy can't be hit by anything in the scene.
            Collider[] cols = _instance.GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++) Object.Destroy(cols[i]);

            if (!TryGetBounds(_instance, out _bounds))
                _bounds = new Bounds(_instance.transform.position, Vector3.one);
            _radius = Mathf.Max(_bounds.extents.x, _bounds.extents.y, _bounds.extents.z);
            if (_radius < 1e-3f) _radius = 1f;
        }

        // Re-render the current orbit into the RenderTexture. Cheap (low-poly) — safe
        // to call every frame while the modal is open.
        public void Render()
        {
            if (_disposed || _instance == null) return;
            float dist = _radius * 3.6f;
            Quaternion rot = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 dir = rot * Vector3.forward;
            _camGo.transform.SetPositionAndRotation(_bounds.center - dir * dist, rot);

            _lightGo.SetActive(true);
            var req = new UniversalRenderPipeline.SingleCameraRequest();
            if (RenderPipeline.SupportsRenderRequest(_cam, req))
            {
                req.destination = _rt;
                RenderPipeline.SubmitRenderRequest(_cam, req);
            }
            else
            {
                _cam.Render();
            }
            _lightGo.SetActive(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_instance != null) Object.Destroy(_instance);
            if (_camGo != null) Object.Destroy(_camGo);
            if (_lightGo != null) Object.Destroy(_lightGo);
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); }
        }

        static bool TryGetBounds(GameObject go, out Bounds bounds)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            bounds = new Bounds();
            bool has = false;
            foreach (Renderer r in rs)
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!has) { bounds = r.bounds; has = true; } else bounds.Encapsulate(r.bounds);
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
