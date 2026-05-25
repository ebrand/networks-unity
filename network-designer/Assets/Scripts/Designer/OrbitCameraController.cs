// Runtime orbit camera for the network designer.
//
// Mouse bindings are chosen so that left-click and right-click stay
// FREE for designer interactions (placing vertices, deleting things).
// All mouse-driven camera control is on middle mouse and the scroll
// wheel; keyboard pan is on WASD.
//
//   - Middle-mouse drag         : orbit around Target
//   - Shift + middle-mouse drag : pan Target (and the camera with it)
//   - Mouse scroll              : zoom in/out (clamped to Min/MaxDistance)
//   - W / S                     : pan Target forward / backward along
//                                 the camera's ground-plane heading
//   - A / D                     : pan Target left / right along the
//                                 camera's ground-plane right vector
//
// Target is stored as a Vector3 (not a Transform) so panning can move
// the focus point freely through empty space. Pitch is clamped above
// horizon to keep the ground plane visible.

using UnityEngine;

namespace NetworkDesigner.Designer
{
    [DisallowMultipleComponent]
    public class OrbitCameraController : MonoBehaviour
    {
        [Header("Focus")]
        [Tooltip("World-space point the camera orbits and looks at.")]
        public Vector3 Target = Vector3.zero;
        [Tooltip("Distance from camera to Target along the view vector.")]
        public float Distance = 50f;
        [Tooltip("Horizontal angle (degrees, around Y).")]
        public float Yaw = 0f;
        [Tooltip("Pitch (degrees from horizon, 0 = level, 90 = top-down).")]
        [Range(0f, 89f)] public float Pitch = 45f;

        [Header("Limits")]
        public float MinDistance = 2f;
        public float MaxDistance = 5000f;
        [Range(0f, 89f)] public float MinPitch = 5f;
        [Range(0f, 89f)] public float MaxPitch = 89f;

        [Header("Camera clip planes (applied on enable / via tuning)")]
        public float NearClipPlane = 0.1f;
        public float FarClipPlane = 20000f;

        [Header("Sensitivity")]
        [Tooltip("Degrees per mouse-axis unit.")]
        public float RotateSensitivity = 5f;
        [Tooltip("Pan speed scales with Distance, so panning feels the " +
                 "same regardless of zoom level.")]
        public float PanSensitivity = 0.05f;
        [Tooltip("Each scroll tick changes Distance by this fraction of " +
                 "the current Distance.")]
        public float ZoomSensitivity = 5f;
        [Tooltip("WASD pan speed, in (meters per second) per (meter of Distance). " +
                 "Scales with Distance so panning feels the same regardless of " +
                 "zoom level — e.g. 1.5 means holding W moves Target forward by " +
                 "75 m/s at Distance=50.")]
        public float KeyboardPanSensitivity = 1.5f;
        [Tooltip("Multiplier applied to keyboard pan speed while Shift is held.")]
        public float KeyboardPanShiftMultiplier = 3f;

        // External predicate that, when set and returning true, causes
        // HandleInput to ignore the scroll wheel this frame. Used by
        // NetworkDesigner to prevent camera zoom while the cursor is over
        // the palette (so the palette's own scroll view consumes it).
        public System.Func<bool> ScrollSuppressor;

        void OnEnable()
        {
            ApplyClipPlanes();
        }

        void LateUpdate()
        {
            HandleInput();
            ApplyTransform();
            ApplyClipPlanes();
        }

        void ApplyClipPlanes()
        {
            Camera cam = GetComponent<Camera>();
            if (cam == null) return;
            cam.nearClipPlane = NearClipPlane;
            cam.farClipPlane = FarClipPlane;
        }

        void HandleInput()
        {
            bool middle = Input.GetMouseButton(2);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float dx = Input.GetAxis("Mouse X");
            float dy = Input.GetAxis("Mouse Y");
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (ScrollSuppressor != null && ScrollSuppressor()) scroll = 0f;

            if (middle && !shift)
            {
                Yaw += dx * RotateSensitivity;
                Pitch -= dy * RotateSensitivity;
                Pitch = Mathf.Clamp(Pitch, MinPitch, MaxPitch);
            }
            else if (middle && shift)
            {
                // Pan in screen space, then convert to world-space target
                // motion. Scale by Distance so the same pixel-drag moves
                // the target the same amount on screen at any zoom level.
                Vector3 right = transform.right;
                Vector3 up = transform.up;
                Target -= right * (dx * PanSensitivity * Distance);
                Target -= up * (dy * PanSensitivity * Distance);
            }

            if (Mathf.Abs(scroll) > 1e-4f)
            {
                Distance -= scroll * ZoomSensitivity * Distance;
                Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);
            }

            HandleKeyboardPan();
        }

        // WASD pan in camera-relative ground-plane coordinates. W/S
        // moves Target along the camera's heading projected onto the
        // XZ plane (so straight-down camera also gets sensible
        // forward); A/D moves perpendicular. Scaled by Distance for
        // zoom-independent feel, and by Time.deltaTime so speed is
        // framerate-independent.
        void HandleKeyboardPan()
        {
            // Skip if an IMGUI text field has keyboard focus — typing
            // in a field would otherwise scroll the camera.
            if (GUIUtility.keyboardControl != 0) return;

            float kbX = 0f, kbZ = 0f;
            if (Input.GetKey(KeyCode.W)) kbZ += 1f;
            if (Input.GetKey(KeyCode.S)) kbZ -= 1f;
            if (Input.GetKey(KeyCode.A)) kbX -= 1f;
            if (Input.GetKey(KeyCode.D)) kbX += 1f;
            if (kbX == 0f && kbZ == 0f) return;

            Vector3 fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward; // looking straight down
            fwd.Normalize();
            Vector3 right = transform.right; right.y = 0f;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();

            float speed = KeyboardPanSensitivity * Distance;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift) speed *= KeyboardPanShiftMultiplier;

            Target += (right * kbX + fwd * kbZ) * (speed * Time.deltaTime);
        }

        void ApplyTransform()
        {
            Quaternion rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 offset = rotation * Vector3.back * Distance;
            transform.position = Target + offset;
            transform.rotation = Quaternion.LookRotation(Target - transform.position, Vector3.up);
        }
    }
}
