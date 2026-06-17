// Lighting/atmosphere setup for the designer scene.
//
// Tweaks the existing directional light (intensity, color, angle, soft
// shadows) and, optionally, the camera background color. All values are
// exposed for tuning in the Inspector.
//
// By default this only drives the sun (skybox + ambient stay owned by the
// Window > Rendering > Lighting settings). For code-managed scenes (e.g. an
// empty TerrainDesigner scene), the opt-in "Auto-setup" flags let it also
// create a sun if none exists, drive ambient fill, and set the URP shadow
// distance — so the scene lights itself with no manual Lighting-window work.
//
// Attach to ANY GameObject in the scene (typically a dedicated empty
// "Ambiance" GameObject, but the Designer GameObject works fine too).
// If `Sun` is left null, the first Light in the scene is auto-picked.
// If `MainCamera` is left null, Camera.main is used.
//
// Re-applies on inspector changes in Play mode (OnValidate), so you can
// tune the look live.

using UnityEngine;
using UnityEngine.Rendering;

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

        [Header("Auto-setup (opt-in; for code-managed scenes)")]
        [Tooltip("If no Light exists in the scene, create a directional sun.")]
        public bool CreateSunIfMissing = false;
        [Tooltip("Drive ambient lighting from here so shadowed areas get fill " +
                 "(Skybox source if a skybox is set, else a flat fill color). " +
                 "Leave OFF to let the Lighting window own ambient.")]
        public bool ManageAmbient = false;
        public Color FillAmbientColor = new Color(0.40f, 0.44f, 0.50f);
        [Tooltip("If > 0 and URP is active, set the pipeline's shadow distance " +
                 "(metres) so shadows reach across large scenes. 0 = leave it.")]
        public float ShadowDistance = 0f;

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
            if (Sun == null && CreateSunIfMissing)
            {
                Sun = new GameObject("Sun").AddComponent<Light>();
                Sun.gameObject.hideFlags = HideFlags.DontSave; // runtime auto-created light
                Sun.type = LightType.Directional;
            }
            if (Sun != null)
            {
                Sun.intensity = SunIntensity;
                Sun.color = SunColor;
                Sun.transform.rotation = Quaternion.Euler(SunEulerAngles);
                Sun.shadows = Shadows;
                Sun.shadowStrength = ShadowStrength;
            }

            if (ManageAmbient)
            {
                if (RenderSettings.skybox != null)
                {
                    RenderSettings.ambientMode = AmbientMode.Skybox;
                    DynamicGI.UpdateEnvironment();
                }
                else
                {
                    // No skybox assigned (empty scene) — a flat fill keeps
                    // shadowed areas from going pure black.
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = FillAmbientColor;
                }
            }

            // URP clips shadows at the pipeline asset's shadow distance, which
            // defaults too short for large scenes. Push it out (URP only).
            if (ShadowDistance > 0f
                && GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
            {
                urp.shadowDistance = ShadowDistance;
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
