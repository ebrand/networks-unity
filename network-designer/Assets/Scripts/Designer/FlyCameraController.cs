// Free-fly camera for roaming a large terrain. No pivot (so it never "swings"
// around a stranded point the way an orbit camera does):
//   - Middle-mouse drag        : look around (yaw/pitch) — the camera "pointing"
//   - Shift + middle-mouse drag : pan
//   - W/A/S/D                   : move along the view, flattened to the ground
//   - E / Q                     : up / down (world)
//   - Left Shift (with WASD)    : move faster
//   - Mouse scroll              : dolly (zoom)
// LEFT and RIGHT mouse are left entirely for the terrain tools (sculpt /
// scatter / linework), so looking around never triggers a tool action.

using UnityEngine;

namespace NetworkDesigner.Designer
{
    public class FlyCameraController : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Base move speed (metres/second) for WASD.")]
        public float MoveSpeed = 12f;
        [Tooltip("Speed multiplier while Left Shift is held.")]
        public float FastMultiplier = 4f;
        [Tooltip("Movement inertia. Lower = more drift/glide through stops and direction changes; higher = snappier.")]
        public float MoveDamping = 6f;
        [Tooltip("Altitude (m above ground) at/above which full Move speed applies. Below it, " +
                 "WASD slows proportionally so close-up movement isn't too fast.")]
        public float SpeedAltitudeReference = 150f;
        [Tooltip("Floor for the low-altitude speed scale, so movement near the ground isn't unusably slow.")]
        [Range(0.05f, 1f)] public float MinSpeedFactor = 0.15f;
        [Tooltip("Metres the camera dollies (zooms) along the view per scroll notch.")]
        public float ZoomStep = 14f;
        public float MinMoveSpeed = 1f;
        public float MaxMoveSpeed = 3000f;

        [Header("Look")]
        [Tooltip("Mouse-look sensitivity (degrees per mouse unit) while right mouse is held.")]
        public float LookSensitivity = 3f;
        [Tooltip("Look-rotation easing toward the mouse target. Higher = snappier, lower = floatier; 0 = instant.")]
        public float Smoothing = 12f;
        [Tooltip("Zoom (scroll-dolly) easing. Higher = snappier, lower = floatier; 0 = instant.")]
        public float ZoomSmoothing = 12f;

        [Header("Limits (min/max zoom)")]
        [Tooltip("Max camera altitude (world Y) — caps how far you can zoom/fly out.")]
        public float MaxAltitude = 1500f;
        [Tooltip("Minimum metres above the ground — caps zoom-in so you can't go underground.")]
        public float MinClearance = 3f;
        // Ground height (world Y) at a world position; set by the host so the
        // altitude clamp is terrain-aware. Null = clamp against Y = 0.
        public System.Func<Vector3, float> GroundHeight;

        [Header("Lens")]
        [Tooltip("Vertical field of view (degrees). 60 is a good default.")]
        public float FieldOfView = 60f;
        public float NearClipPlane = 0.1f;
        public float FarClipPlane = 5000f;

        // Set true to swallow scroll this frame (e.g. cursor over a UI panel).
        public System.Func<bool> ScrollSuppressor;
        // Set true to swallow middle-mouse look this frame (e.g. dragging over a UI panel).
        public System.Func<bool> LookSuppressor;

        public float Yaw;
        public float Pitch = 35f;
        float _dollyPending; // metres still to glide along the view (eased zoom)
        Vector3 _moveVel;    // current WASD velocity (eased for drift/inertia)

        void OnEnable()
        {
            Vector3 e = transform.eulerAngles;
            Yaw = e.y;
            Pitch = NormalizePitch(e.x);
            ApplyCamera();
        }

        void Update()
        {
            HandleLook();
            ApplyLook();
            HandleZoomScroll();
            HandleMove();
            ApplyZoom();
            ClampAltitude();
            ApplyCamera();
        }

        // Ease the accumulated scroll-dolly out over a few frames (Smoothing
        // controls how snappy), so zoom glides instead of stepping per notch.
        void ApplyZoom()
        {
            if (Mathf.Abs(_dollyPending) < 1e-4f) { _dollyPending = 0f; return; }
            float k = ZoomSmoothing > 0f ? 1f - Mathf.Exp(-ZoomSmoothing * Time.deltaTime) : 1f;
            float step = _dollyPending * k;
            Vector3 newPos = transform.position + transform.forward * step;
            // Stop at the altitude limits rather than sliding along the ground/sky:
            // if the step would breach min-clearance or max-altitude, cancel it.
            float minY = (GroundHeight != null ? GroundHeight(newPos) : 0f) + MinClearance;
            if (newPos.y < minY || newPos.y > MaxAltitude) { _dollyPending = 0f; return; }
            transform.position = newPos;
            _dollyPending -= step;
        }

        // Ease the camera rotation toward the mouse-driven Yaw/Pitch target.
        void ApplyLook()
        {
            Quaternion target = Quaternion.Euler(Pitch, Yaw, 0f);
            if (Smoothing <= 0f) { transform.rotation = target; return; }
            float k = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, k);
        }

        // Keep the camera between MinClearance above the ground and MaxAltitude.
        void ClampAltitude()
        {
            Vector3 p = transform.position;
            float g = GroundHeight != null ? GroundHeight(p) : 0f;
            p.y = Mathf.Clamp(p.y, g + MinClearance, MaxAltitude);
            transform.position = p;
        }

        void HandleLook()
        {
            // Middle-mouse = look. Left/right stay free for tools; Shift only
            // affects move speed.
            if (!Input.GetMouseButton(2)) return;
            if (LookSuppressor != null && LookSuppressor()) return;  // cursor over a UI panel
            Yaw += Input.GetAxis("Mouse X") * LookSensitivity;
            Pitch -= Input.GetAxis("Mouse Y") * LookSensitivity;
            Pitch = Mathf.Clamp(Pitch, -89f, 89f);
            // Rotation is applied (eased) in ApplyLook.
        }

        void HandleZoomScroll()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (ScrollSuppressor != null && ScrollSuppressor()) return;
            if (Mathf.Abs(scroll) < 1e-4f) return;
            // ~0.1 per wheel notch -> ZoomStep metres. Accumulate; ApplyZoom eases it.
            _dollyPending += scroll * (ZoomStep / 0.1f);
        }

        void HandleMove()
        {
            Vector3 move = Vector3.zero;
            // Ignore WASD while typing in an IMGUI field (then target velocity is
            // zero, so the camera drifts to a stop instead of cutting out).
            if (GUIUtility.keyboardControl == 0)
            {
                // Heading-relative, flattened to the ground plane: W goes where
                // you're looking (projected onto XZ), never diving when pitched.
                Vector3 fwd = transform.forward; fwd.y = 0f;
                Vector3 right = transform.right; right.y = 0f;
                fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
                right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
                if (Input.GetKey(KeyCode.W)) move += fwd;
                if (Input.GetKey(KeyCode.S)) move -= fwd;
                if (Input.GetKey(KeyCode.D)) move += right;
                if (Input.GetKey(KeyCode.A)) move -= right;
                if (Input.GetKey(KeyCode.E)) move += Vector3.up;
                if (Input.GetKey(KeyCode.Q)) move += Vector3.down;
            }

            bool fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float speed = MoveSpeed * (fast ? FastMultiplier : 1f);
            // Scale speed by altitude so it dampens as you get close to the ground.
            float alt = transform.position.y - (GroundHeight != null ? GroundHeight(transform.position) : 0f);
            speed *= Mathf.Clamp(alt / Mathf.Max(1f, SpeedAltitudeReference), MinSpeedFactor, 1f);
            Vector3 targetVel = move.sqrMagnitude > 1e-6f ? move.normalized * speed : Vector3.zero;

            // Ease velocity toward the target so movement accelerates in, coasts
            // out, and drifts through direction changes (MoveDamping = snappiness).
            float k = MoveDamping > 0f ? 1f - Mathf.Exp(-MoveDamping * Time.deltaTime) : 1f;
            _moveVel = Vector3.Lerp(_moveVel, targetVel, k);
            if (_moveVel.sqrMagnitude > 1e-8f) transform.position += _moveVel * Time.deltaTime;
        }

        void ApplyCamera()
        {
            Camera cam = GetComponent<Camera>();
            if (cam == null) return;
            cam.nearClipPlane = NearClipPlane;
            cam.farClipPlane = FarClipPlane;
            cam.fieldOfView = FieldOfView;
        }

        // Drop the camera at a sensible vantage looking at `center`, far enough
        // back to see roughly `span` metres of terrain.
        public void Frame(Vector3 center, float span)
        {
            Pitch = 35f;
            Yaw = 0f;
            transform.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            float dist = Mathf.Max(10f, span * 0.8f);
            transform.position = center - transform.forward * dist;
        }

        static float NormalizePitch(float x) => x > 180f ? x - 360f : x;
    }
}
