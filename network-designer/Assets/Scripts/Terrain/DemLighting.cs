// Live ATMOSPHERE + COLOR GRADE for the world — exponential-squared fog (aerial perspective), a
// fog-matched mood sky, and a global post-processing Volume (neutral tonemap + exposure/contrast/
// saturation/warmth + bloom + vignette). All parameters are LIVE: change a field and call Apply()
// and it updates the running Volume / RenderSettings immediately. Driven by the Environment palette.
//
// Does NOT touch the sun — SceneAmbiance owns the directional light. The old editor-only pack-profile
// loader (Toby PP_URP) is gone; the grade is hand-built so it works in builds too.

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NetworkDesigner.Terrain
{
    public static class DemLighting
    {
        const string VolName = "DEM PostFX (auto)";

        // ── Live parameters (defaults = the prior hand-built "Custom" look) ──
        public static float Exposure = -0.15f;     // post-exposure (overall brightness)
        public static float Contrast = 14f;        // -100..100
        public static float Saturation = 6f;       // -100..100
        public static float Warmth = 0.5f;         // 0 = neutral colour filter, 1 = warm golden
        public static float BloomIntensity = 0.25f;
        public static float VignetteAmount = 0.28f;
        public static float FogDensity = 0.00012f; // aerial-perspective depth (exp² fog)
        public static bool Enabled { get; private set; }

        static readonly Color FogTint = new Color(0.42f, 0.50f, 0.60f);   // moody blue-grey haze
        static readonly Color WarmFilter = new Color(1f, 0.92f, 0.80f);

        static Volume _vol;
        static VolumeProfile _profile;
        static ColorAdjustments _col;
        static Bloom _bloom;
        static Vignette _vig;
        static Material _origSkybox, _moodSky;

        public static void SetEnabled(bool on) { if (on == Enabled) return; if (on) Enable(); else Clear(); }
        public static void Toggle() => SetEnabled(!Enabled);

        static void Enable()
        {
            // Atmospheric fog (the biggest aerial-perspective lever) + a fog-matched mood sky so the
            // sky/horizon/below-horizon all blend with the haze instead of clashing.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = FogTint;
            SetupMoodSky(FogTint);
            if (RenderSettings.skybox != null) { RenderSettings.ambientMode = AmbientMode.Skybox; DynamicGI.UpdateEnvironment(); }

            // Global post-processing Volume (built once; components cached for live tweaks).
            BuildProfile();
            var go = GameObject.Find(VolName) ?? new GameObject(VolName);
            _vol = go.GetComponent<Volume>() ?? go.AddComponent<Volume>();
            _vol.isGlobal = true;
            _vol.priority = 10f;                                       // win over any scene volume
            _vol.sharedProfile = _profile;

            var cam = MainCam();
            if (cam != null) cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            Enabled = true;
            Apply();
        }

        public static void Clear()
        {
            var go = GameObject.Find(VolName);
            if (go != null) { if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go); }
            if (_profile != null) { Object.DestroyImmediate(_profile); _profile = null; }
            _col = null; _bloom = null; _vig = null; _vol = null;

            RenderSettings.fog = false;
            if (_origSkybox != null) { RenderSettings.skybox = _origSkybox; _origSkybox = null; DynamicGI.UpdateEnvironment(); }
            if (_moodSky != null) { Object.DestroyImmediate(_moodSky); _moodSky = null; }

            var cam = MainCam();
            if (cam != null) cam.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            Enabled = false;
        }

        // Push the live parameters to the running Volume + fog. Call after changing any field.
        public static void Apply()
        {
            if (!Enabled) return;
            RenderSettings.fog = true;
            RenderSettings.fogDensity = FogDensity;
            if (_col != null)
            {
                _col.postExposure.value = Exposure;
                _col.contrast.value = Contrast;
                _col.saturation.value = Saturation;
                _col.colorFilter.value = Color.Lerp(Color.white, WarmFilter, Mathf.Clamp01(Warmth));
            }
            if (_bloom != null) _bloom.intensity.value = BloomIntensity;
            if (_vig != null) _vig.intensity.value = VignetteAmount;
        }

        static void BuildProfile()
        {
            if (_profile != null) Object.DestroyImmediate(_profile);
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            p.name = "DEM Custom Grade";

            var tone = p.Add<Tonemapping>();
            tone.mode.overrideState = true; tone.mode.value = TonemappingMode.Neutral;

            _col = p.Add<ColorAdjustments>();
            _col.postExposure.overrideState = true;
            _col.contrast.overrideState = true;
            _col.saturation.overrideState = true;
            _col.colorFilter.overrideState = true;

            _bloom = p.Add<Bloom>();
            _bloom.intensity.overrideState = true;
            _bloom.threshold.overrideState = true; _bloom.threshold.value = 1.3f;   // only real highlights

            _vig = p.Add<Vignette>();
            _vig.intensity.overrideState = true;
            _vig.smoothness.overrideState = true; _vig.smoothness.value = 0.7f;

            _profile = p;
        }

        // A dimmer procedural sky matched to the fog: ground hemisphere = haze (kills the brown),
        // lower exposure (kills the white horizon), cooler tint. Restored on Clear.
        static void SetupMoodSky(Color haze)
        {
            var sh = Shader.Find("Skybox/Procedural");
            if (sh == null) return;
            if (_origSkybox == null && RenderSettings.skybox != _moodSky) _origSkybox = RenderSettings.skybox;
            if (_moodSky == null) _moodSky = new Material(sh) { name = "DEM Mood Sky" };
            _moodSky.SetColor("_SkyTint", new Color(0.48f, 0.56f, 0.66f));
            _moodSky.SetColor("_GroundColor", haze);
            _moodSky.SetFloat("_Exposure", 0.75f);
            _moodSky.SetFloat("_AtmosphereThickness", 1.15f);
            RenderSettings.skybox = _moodSky;
            DynamicGI.UpdateEnvironment();
        }

        static Camera MainCam() => Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
    }
}
