// A "snake-follower" train: a head car (locomotive) plus trailing wagons that ride a
// world-space polyline (the rail centerline). The head advances by arc length at a set
// speed; each car sits a fixed arc-length behind the one ahead and faces the local
// tangent. Ping-pongs between the ends of the path, keeping the whole train on track.
//
// Increment 1 (network validation): no junction routing or physics — just follow the
// given path so we can watch a train run the rails and judge the geometry.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Trains
{
    public class TrainAgent : MonoBehaviour
    {
        public float Speed = 16f;       // m/s
        public float CarSpacing = 12f;  // arc length between consecutive car origins
        public float RideHeight = 0f;   // extra lift above the path Y

        readonly List<Transform> _cars = new List<Transform>();
        List<Vector3> _path;
        float[] _cum;     // cumulative arc length at each path point
        float _total;
        float _headS;     // head position along the path (arc length)
        float _minS;      // lower ping-pong bound (keeps the last car on track)
        int _dir = 1;

        // Build the train on `path`: car 0 = loco, then `wagonCount` wagons (falls back to
        // primitives if a prefab is null). Returns false if the path is unusable.
        public bool Init(List<Vector3> path, GameObject loco, GameObject wagon,
                         int wagonCount, float spacing, float speed)
        {
            if (path == null || path.Count < 2) return false;
            _path = path; CarSpacing = Mathf.Max(1f, spacing); Speed = speed;

            _cum = new float[_path.Count];
            for (int i = 1; i < _path.Count; i++)
                _cum[i] = _cum[i - 1] + Vector3.Distance(_path[i - 1], _path[i]);
            _total = _cum[_path.Count - 1];
            if (_total <= 0.01f) return false;

            SpawnCar(loco, "Locomotive");
            for (int i = 0; i < Mathf.Max(0, wagonCount); i++) SpawnCar(wagon, "Wagon" + (i + 1));

            // Keep the whole train on the track: head ranges [minS, total]; the rearmost
            // car trails by (count-1)*spacing. If the track is shorter than the train,
            // let the head use the whole length and cars clamp at the start.
            _minS = (_cars.Count - 1) * CarSpacing;
            if (_minS >= _total) _minS = 0f;
            _headS = Mathf.Clamp(_minS, 0f, _total);
            Place();
            return true;
        }

        void SpawnCar(GameObject prefab, string label)
        {
            GameObject go = prefab != null
                ? Instantiate(prefab)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = label;
            go.transform.SetParent(transform, false);
            _cars.Add(go.transform);
        }

        void Update()
        {
            if (_path == null || _total <= 0f || _cars.Count == 0) return;
            // Recompute the rear bound so live CarSpacing changes keep the train on track.
            _minS = (_cars.Count - 1) * Mathf.Max(1f, CarSpacing);
            if (_minS >= _total) _minS = 0f;
            _headS += Speed * _dir * Time.deltaTime;
            if (_headS >= _total) { _headS = _total; _dir = -1; }
            else if (_headS <= _minS) { _headS = _minS; _dir = 1; }
            Place();
        }

        void Place()
        {
            for (int i = 0; i < _cars.Count; i++)
            {
                float s = Mathf.Clamp(_headS - i * CarSpacing, 0f, _total);
                Vector3 pos = SampleAt(s, out Vector3 fwd);
                _cars[i].position = pos + Vector3.up * RideHeight;
                Vector3 dir = fwd * _dir;   // face the direction of travel (flip on reverse)
                if (dir.sqrMagnitude > 1e-6f)
                    _cars[i].rotation = Quaternion.LookRotation(dir, Vector3.up);
            }
        }

        // World position + forward (unit, toward increasing arc length) at arc length s.
        Vector3 SampleAt(float s, out Vector3 fwd)
        {
            int i = 1;
            while (i < _cum.Length - 1 && _cum[i] < s) i++;
            float segLen = _cum[i] - _cum[i - 1];
            float t = segLen > 1e-5f ? (s - _cum[i - 1]) / segLen : 0f;
            Vector3 a = _path[i - 1], b = _path[i];
            Vector3 d = b - a;
            fwd = d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward;
            return Vector3.Lerp(a, b, t);
        }
    }
}
