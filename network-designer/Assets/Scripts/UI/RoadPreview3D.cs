// Off-screen 3D preview rig for the Road Designer: sweeps a short straight road from a RoadProfile,
// parks it far from the world, and renders it with an orbit camera into a RenderTexture the modal shows.
// Placing the stub 100 km away means the preview camera sees only it (no dedicated render layer needed)
// and the main camera never frames it; the scene's directional sun lights it.

using UnityEngine;
using NetworkDesigner.Model;
using NetworkDesigner.Roads;

namespace NetworkDesigner.UI
{
    public class RoadPreview3D : MonoBehaviour
    {
        static readonly Vector3 Far = new Vector3(100000f, 0f, 100000f);

        Camera _cam;
        RenderTexture _rt;
        GameObject _road;
        float _yaw = 35f, _pitch = 28f, _dist = 45f;

        public RenderTexture Texture { get { EnsureRig(); return _rt; } }

        void EnsureRig()
        {
            if (_cam != null) return;
            _rt = new RenderTexture(1024, 720, 24) { name = "RoadPreviewRT" };
            var camGo = new GameObject("RoadPreviewCam") { hideFlags = HideFlags.DontSave };
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.targetTexture = _rt;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.08f, 0.09f, 0.10f);
            _cam.fieldOfView = 35f;
            _cam.nearClipPlane = 0.3f;
            _cam.farClipPlane = 500f;
            _cam.enabled = false;
        }

        public void SetProfile(RoadProfile p)
        {
            EnsureRig();
            if (_road != null) DestroySafe(_road);
            if (p == null) return;
            RoadCrossSection xs = BuildXS(p);
            Vector2 a = new Vector2(Far.x - 20f, Far.z), b = new Vector2(Far.x + 20f, Far.z);
            _road = RoadSweep.Build(xs, a, b, false, default, default, transform, "RoadPreviewMesh", Far.y, Far.y);
            _road.hideFlags = HideFlags.DontSave;
            _dist = Mathf.Clamp(p.TotalWidth * 1.8f + 18f, 22f, 140f);
        }

        // RoadProfile → RoadCrossSection: shoulders + lanes + raised median / flat turn lane.
        static RoadCrossSection BuildXS(RoadProfile p)
        {
            var xs = new RoadCrossSection();
            if (p.ShoulderBA != null && p.ShoulderBA.Width > 0.01f) xs.ShoulderBand(p.ShoulderBA.Width);
            for (int i = p.BA.Lanes.Count - 1; i >= 0; i--) xs.Lane(p.BA.Lanes[i].Width);
            if (p.Median != null) xs.Median(p.Median.Width);              // raised planted median
            else if (p.TurnLane != null) xs.Lane(p.TurnLane.Width);      // flat drivable turn lane
            for (int i = 0; i < p.AB.Lanes.Count; i++) xs.Lane(p.AB.Lanes[i].Width);
            if (p.ShoulderAB != null && p.ShoulderAB.Width > 0.01f) xs.ShoulderBand(p.ShoulderAB.Width);
            return xs;
        }

        public void Orbit(float dx, float dy) { _yaw += dx * 0.3f; _pitch = Mathf.Clamp(_pitch - dy * 0.3f, 5f, 85f); }
        public void Zoom(float d) { _dist = Mathf.Clamp(_dist * (1f + d * 0.08f), 8f, 220f); }
        public void SetActive(bool on) { EnsureRig(); _cam.enabled = on; if (_road != null) _road.SetActive(on); }

        void Update()
        {
            if (_cam == null || !_cam.enabled) return;
            Vector3 center = Far + Vector3.up * 1f;
            Vector3 pos = center + Quaternion.Euler(_pitch, _yaw, 0f) * (Vector3.back * _dist);
            _cam.transform.position = pos;
            _cam.transform.rotation = Quaternion.LookRotation(center - pos, Vector3.up);
        }

        static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        void OnDestroy() { if (_rt != null) _rt.Release(); }
    }
}
