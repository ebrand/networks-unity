// Time-of-day SUN arc — drives the scene's directional light along a realistic daily arc from a single
// "Time (h)" value: elevation (sine arc, peaks at noon, dips below the horizon at night), azimuth
// (east → south → west), intensity (fades through twilight to 0 at night), and — the big light-QUALITY
// lever — physically-based COLOUR TEMPERATURE (warm ~2200K near the horizon → cool ~6200K at midday)
// instead of a flat tint. Optional auto-advance for a live day/night cycle.
//
// Sun-only and authoritative while Enabled (it overrides SceneAmbiance's static sun). Driven by the
// Environment palette; ticked by a self-spawned DayCycleTicker. Does not touch fog/sky/grade
// (DemLighting owns those).

using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkDesigner.Designer
{
    public static class DayCycle
    {
        public static bool Enabled;
        public static float TimeOfDay = 13f;        // hours, 0..24
        public static float PeakElevation = 60f;    // sun height at noon (deg) — stands in for season/latitude
        public static float NorthYaw = 0f;          // rotate the whole arc (compass offset), deg
        public static float Intensity = 1.35f;      // peak (midday) sun intensity
        public static bool AutoAdvance;
        public static float DayLengthMinutes = 5f;  // real minutes for a full 24 h
        public static bool ManageSky = true;        // own a sun-following procedural sky + skybox ambient

        const float WarmK = 2200f, CoolK = 6200f;

        static Material _sky, _origSky;
        static bool _skyInstalled;
        static float _lastGi = -999f;

        public static void SetEnabled(bool on) { Enabled = on; if (on) Apply(); else RestoreSky(); }

        // Advance the clock when auto-advancing; otherwise the palette drives Apply on edits.
        public static void Tick(float dt)
        {
            if (!Enabled || !AutoAdvance) return;
            TimeOfDay = Mathf.Repeat(TimeOfDay + dt * (24f / Mathf.Max(1f, DayLengthMinutes * 60f)), 24f);
            Apply();
        }

        public static void Apply()
        {
            Light sun = FindSun();
            if (sun == null) return;

            // Elevation: 0 at 6 h (rise), peak at 12 h, 0 at 18 h, negative at night.
            float elev = Mathf.Sin((TimeOfDay - 6f) / 12f * Mathf.PI) * PeakElevation;
            // Light forward yaw: west at sunrise (sun in east) → north at noon (sun in south) → east at sunset.
            float yaw = 270f + (TimeOfDay - 6f) / 12f * 180f + NorthYaw;
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(elev, yaw, 0f);

            // Intensity: full above ~6°, smooth twilight fade, 0 once the sun is below the horizon.
            float lit = Mathf.Clamp01((elev + 1.5f) / 7f);
            sun.intensity = Intensity * lit;

            // Quality: physically-based colour temperature, warm low → cool high. Let the Kelvin drive
            // the hue (white base) instead of a baked tint.
            sun.useColorTemperature = true;
            sun.colorTemperature = Mathf.Lerp(WarmK, CoolK, Mathf.Clamp01(elev / 35f));
            sun.color = Color.white;

            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.7f;

            // Sky + ambient follow the same sun: the procedural skybox scatters from this directional
            // light, so we get blue overhead / warm horizon / sunrise-sunset, and a cool sky-ambient
            // fill in shadows — the warm-sun/cool-shadow contrast.
            RenderSettings.sun = sun;
            if (ManageSky) UpdateSky(elev); else RestoreSky();
        }

        // Environment-palette ambient multiplier — scales the sky ambient so the bright daytime fill can be
        // tamed (high ambient washes out shadows/form on trees + ground). 1 = default.
        public static float AmbientScale = 1f;

        static void UpdateSky(float elev)
        {
            EnsureSky();
            if (_sky == null) return;
            float day = Mathf.Clamp01((elev + 2f) / 10f);                  // 0 night → 1 full day
            _sky.SetColor("_SkyTint", new Color(0.52f, 0.63f, 0.85f));     // daytime blue
            _sky.SetColor("_GroundColor", new Color(0.27f, 0.30f, 0.34f));
            _sky.SetFloat("_Exposure", Mathf.Lerp(0.12f, 1.3f, day));      // dark night → bright day
            _sky.SetFloat("_AtmosphereThickness", Mathf.Lerp(0.6f, 1.0f, day));
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = Mathf.Lerp(0.22f, 1f, day) * Mathf.Max(0f, AmbientScale);
            // GI environment is the expensive bit — refresh on manual edits, but throttle auto-advance.
            if (!AutoAdvance || Time.unscaledTime - _lastGi > 0.4f)
            { DynamicGI.UpdateEnvironment(); _lastGi = Time.unscaledTime; }
        }

        static void EnsureSky()
        {
            if (_skyInstalled) return;
            var sh = Shader.Find("Skybox/Procedural");
            if (sh == null) return;
            if (_sky == null) _sky = new Material(sh) { name = "DayCycle Sky" };
            if (RenderSettings.skybox != _sky) _origSky = RenderSettings.skybox;
            RenderSettings.skybox = _sky;
            _skyInstalled = true;
        }

        static void RestoreSky()
        {
            if (!_skyInstalled) return;
            RenderSettings.skybox = _origSky;
            DynamicGI.UpdateEnvironment();
            _skyInstalled = false;
        }

        static Light FindSun()
        {
            Light best = null;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && (best == null || l.intensity > best.intensity)) best = l;
            return best != null ? best : Object.FindFirstObjectByType<Light>();
        }
    }

    // Ticks DayCycle every frame (only does work while Enabled && AutoAdvance). Self-spawns at runtime.
    public class DayCycleTicker : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (FindFirstObjectByType<DayCycleTicker>() != null) return;
            var go = new GameObject("DayCycleTicker") { hideFlags = HideFlags.DontSave };
            go.AddComponent<DayCycleTicker>();
        }

        void Update() => DayCycle.Tick(Time.deltaTime);
    }
}
