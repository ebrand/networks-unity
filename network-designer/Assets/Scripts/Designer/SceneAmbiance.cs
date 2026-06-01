// Lighting/atmosphere setup for the designer scene.
//
// Tweaks the existing directional light (intensity, color, angle, soft
// shadows) and, optionally, the camera background color. All values are
// exposed for tuning in the Inspector.
//
// NOTE: skybox + ambient (environment) lighting are intentionally NOT
// managed here — set those in Window > Rendering > Lighting so that
// window stays authoritative. This component only drives the sun.
//
// Attach to ANY GameObject in the scene (typically a dedicated empty
// "Ambiance" GameObject, but the Designer GameObject works fine too).
// If `Sun` is left null, the first Light in the scene is auto-picked.
// If `MainCamera` is left null, Camera.main is used.
//
// Re-applies on inspector changes in Play mode (OnValidate), so you can
// tune the look live.

using UnityEngine;

namespace NetworkDesigner.Designer
{
    [DisallowMultipleComponent]
    public class SceneAmbiance : MonoBehaviour
    {
        [Header("Sun (directional light)")]
        public Light Sun;
        [Range(0f, 2f)] public float SunIntensity = 0.9f;
        public Color SunColor = new Color(1f, 0.96f, 0.88f); // warm white
        [Tooltip("Light rotation in degrees. X is pitch (sun height); a low " +
                 "value gives long shadows. Y is yaw (sun direction).")]
        public Vector3 SunEulerAngles = new Vector3(50f, 40f, 0f);
        public LightShadows Shadows = LightShadows.Soft;
        [Range(0f, 1f)] public float ShadowStrength = 0.7f;

        [Header("Background")]
        [Tooltip("When OFF, the existing camera background/skybox is left alone " +
                 "(recommended for a daytime look). Turn ON for a moody solid bg.")]
        public bool OverrideBackground = false;
        public Camera MainCamera;
        public Color BackgroundColor = new Color(0.08f, 0.10f, 0.14f);

        void OnEnable()
        {
            Apply();
        }

        void OnValidate()
        {
            // Live-tweak from the inspector during Play mode.
            if (!Application.isPlaying) return;
            Apply();
        }

        // Public so the tuning system (and any other live-tweaker) can
        // force a re-application after mutating a field at runtime.
        public void Apply()
        {
            if (Sun == null) Sun = FindFirstObjectByType<Light>();
            if (Sun != null)
            {
                Sun.intensity = SunIntensity;
                Sun.color = SunColor;
                Sun.transform.rotation = Quaternion.Euler(SunEulerAngles);
                Sun.shadows = Shadows;
                Sun.shadowStrength = ShadowStrength;
            }

            if (OverrideBackground)
            {
                if (MainCamera == null) MainCamera = Camera.main;
                if (MainCamera != null)
                {
                    MainCamera.clearFlags = CameraClearFlags.SolidColor;
                    MainCamera.backgroundColor = BackgroundColor;
                }
            }
        }
    }
}
