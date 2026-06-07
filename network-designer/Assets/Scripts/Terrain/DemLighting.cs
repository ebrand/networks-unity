// Cinematic lighting/post-processing for the DEM world — the single biggest reason our
// terrain looks flat next to the Rocky Hills demo. SceneAmbiance already drives the sun, but
// nothing sets up URP post-processing. This drops in a global Volume using one of the pack's
// authored URP profiles (color grading / tonemapping / bloom / AO / vignette), enables
// post-processing on the camera, and punches up the sun + shadow distance for real contrast.
//
// Editor-time tool (loads the pack's VolumeProfile assets via AssetDatabase, like
// DemTerrainWorld loads its TerrainLayers). Apply / Clear from the System palette.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NetworkDesigner.Terrain
{
    public static class DemLighting
    {
        const string VolName = "DEM PostFX (auto)";
        const string ProfileDir =
            "Assets/Toby Fredson/Rocky Hills Environment - Whitebark Pine/RHEWP_Core/Post Processing/PP_URP";

        public const string Custom = "Custom (light)";
        static VolumeProfile _customProfile;   // runtime-built grade (destroyed on Clear)
        static Material _origSkybox, _moodSky;  // swapped-in moody sky + the original to restore

        // "Custom" first (a tasteful light-touch grade), then the pack's authored profiles —
        // those are tuned for the demo's exact sun/sky and tend to look drab + add CA fringing
        // on ours, so they're alternatives, not the default.
        public static List<string> ListProfiles()
        {
            var list = new List<string> { Custom };
#if UNITY_EDITOR
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:VolumeProfile", new[] { ProfileDir }))
                list.Add(System.IO.Path.GetFileNameWithoutExtension(UnityEditor.AssetDatabase.GUIDToAssetPath(guid)));
#endif
            return list;
        }

        // Hand-built grade: neutral tonemap + slight exposure/contrast/saturation lift + gentle
        // bloom + soft vignette. NO chromatic aberration (that was the orange ridge fringing).
        static VolumeProfile BuildCustomProfile()
        {
            if (_customProfile != null) Object.DestroyImmediate(_customProfile);
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            p.name = "DEM Custom Grade";

            var tone = p.Add<Tonemapping>();
            tone.mode.overrideState = true; tone.mode.value = TonemappingMode.Neutral;

            var col = p.Add<ColorAdjustments>();
            col.postExposure.overrideState = true; col.postExposure.value = -0.15f;   // pull brightness DOWN (was too bright)
            col.contrast.overrideState = true; col.contrast.value = 14f;
            col.saturation.overrideState = true; col.saturation.value = 6f;
            col.colorFilter.overrideState = true;
            col.colorFilter.value = new Color(1f, 0.95f, 0.86f);                       // warm tint → ambiance

            var bloom = p.Add<Bloom>();
            bloom.intensity.overrideState = true; bloom.intensity.value = 0.25f;
            bloom.threshold.overrideState = true; bloom.threshold.value = 1.3f;        // high → only real highlights

            var vig = p.Add<Vignette>();
            vig.intensity.overrideState = true; vig.intensity.value = 0.28f;           // a touch more → mood at frame edges
            vig.smoothness.overrideState = true; vig.smoothness.value = 0.7f;

            _customProfile = p;
            return p;
        }

        public static void Apply(string profileName, float sunIntensity = 1.6f, float shadowDistance = 300f)
        {
            // ── Sun: warm, angled, soft shadows. Reuse the existing directional light if present.
            var sun = FindSun();
            if (sun != null)
            {
                sun.type = LightType.Directional;
                sun.intensity = sunIntensity;
                sun.color = new Color(1f, 0.89f, 0.74f);             // warm, golden-hour
                sun.transform.rotation = Quaternion.Euler(32f, 35f, 0f);  // lower sun → longer shadows, more depth
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 0.75f;
            }
            else Debug.LogWarning("[DemLighting] no directional light found — shadows/sun unchanged.");

            // Skybox-based ambient so shadowed ground isn't flat/black.
            if (RenderSettings.skybox != null)
            {
                RenderSettings.ambientMode = AmbientMode.Skybox;
                DynamicGI.UpdateEnvironment();
            }

            // Atmospheric haze — the biggest "ambiance" lever: distant hills fade into aerial
            // perspective (depth) and it softens the far-field texture repeat. Kicks in over km,
            // so near grass stays clear; tuned for the DEM's real-world scale.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.42f, 0.50f, 0.60f);   // deeper blue-grey → moodier, darker distance
            RenderSettings.fogDensity = 0.00012f;

            // Fog only tints geometry, not the sky — so the default skybox's bright horizon and
            // brown ground hemisphere don't match the fogged terrain. Swap in a dimmer procedural
            // sky whose ground = the haze, so distance/sky/below-horizon all blend.
            SetupMoodSky(RenderSettings.fogColor);

            // URP clips shadows at the pipeline's shadow distance (default too short for terrain).
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp && shadowDistance > 0f)
                urp.shadowDistance = shadowDistance;

            // ── Post-processing volume (custom light-touch grade, or a pack profile).
            var profile = (profileName == Custom) ? BuildCustomProfile() : LoadProfile(profileName);
            if (profile == null) { Debug.LogWarning($"[DemLighting] profile '{profileName}' not found in {ProfileDir}."); return; }

            var go = GameObject.Find(VolName) ?? new GameObject(VolName);
            var vol = go.GetComponent<Volume>() ?? go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10f;                                       // win over any scene volume
            vol.sharedProfile = profile;

            // ── Enable post-processing on the camera (off by default on a bare URP camera).
            var cam = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
            if (cam != null) cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            Debug.Log($"[DemLighting] applied '{profileName}' (sun {sunIntensity:0.0}, shadow {shadowDistance:0}m). " +
                      "If it looks too dark/bright, that's the profile's exposure — try another profile or tune sun.");
        }

        public static void Clear()
        {
            var go = GameObject.Find(VolName);
            if (go != null) { if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go); }
            if (_customProfile != null) { Object.DestroyImmediate(_customProfile); _customProfile = null; }
            RenderSettings.fog = false;
            if (_origSkybox != null) { RenderSettings.skybox = _origSkybox; _origSkybox = null; DynamicGI.UpdateEnvironment(); }
            if (_moodSky != null) { Object.DestroyImmediate(_moodSky); _moodSky = null; }
            var cam = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
            if (cam != null) cam.GetUniversalAdditionalCameraData().renderPostProcessing = false;
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
            _moodSky.SetColor("_GroundColor", haze);          // below-horizon matches the haze, not brown
            _moodSky.SetFloat("_Exposure", 0.75f);            // dimmer → no blown-out white horizon
            _moodSky.SetFloat("_AtmosphereThickness", 1.15f);
            RenderSettings.skybox = _moodSky;
            DynamicGI.UpdateEnvironment();
        }

        static Light FindSun()
        {
            Light best = null;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && (best == null || l.intensity > best.intensity)) best = l;
            return best != null ? best : Object.FindFirstObjectByType<Light>();
        }

        static VolumeProfile LoadProfile(string name)
        {
#if UNITY_EDITOR
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:VolumeProfile", new[] { ProfileDir }))
            {
                string p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(p) == name)
                    return UnityEditor.AssetDatabase.LoadAssetAtPath<VolumeProfile>(p);
            }
#endif
            return null;
        }
    }
}
