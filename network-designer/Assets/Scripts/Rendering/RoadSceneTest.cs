// End-to-end smoke test: load road-config.json, pick one configuration,
// instantiate a RoadRenderer in the scene with the chosen profile.
//
// Attach to an empty GameObject at the world origin. Press Play. You
// should see a flat road appear on the XZ plane stretching from
// EndpointA to EndpointB. Use the Scene view to navigate.

using System.IO;
using UnityEngine;
using NetworkDesigner.Import;
using NetworkDesigner.Model;

namespace NetworkDesigner.Rendering
{
    public class RoadSceneTest : MonoBehaviour
    {
        [Header("Source file")]
        [Tooltip("Path relative to the Unity project root. Ignored when AbsolutePath is set.")]
        public string RelativePath = "road-config.json";
        [Tooltip("Optional absolute path. Overrides RelativePath when non-empty.")]
        public string AbsolutePath = "";

        [Header("Which config to render")]
        [Tooltip("Config name to match (case-insensitive). Leave empty to use the file's activeId, " +
                 "or the first config if no activeId is set.")]
        public string ConfigName = "";

        [Header("Road placement")]
        public Vector3 EndpointA = Vector3.zero;
        public Vector3 EndpointB = new Vector3(30f, 0f, 0f);

        [Tooltip("Override the file's driveSide if needed. Leave at default (Right) to use the file's value.")]
        public bool OverrideDriveSide = false;
        public DriveSide ForcedDriveSide = DriveSide.Right;

        void Start()
        {
            string path = ResolvePath();
            if (!File.Exists(path))
            {
                Debug.LogError($"[RoadSceneTest] File not found: {path}");
                return;
            }

            ExportedConfigFile file;
            try { file = ConfigImporter.LoadFromFile(path); }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RoadSceneTest] Failed to load {path}: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            SavedConfig chosen = PickConfig(file);
            if (chosen == null)
            {
                Debug.LogError($"[RoadSceneTest] No suitable config found in {path}. " +
                               $"Available: {string.Join(", ", FormatNames(file))}");
                return;
            }

            Debug.Log($"[RoadSceneTest] Rendering \"{chosen.Name}\" (id={chosen.Id}) — " +
                      $"driveSide={chosen.DriveSide}, AB={chosen.Road.AB.Lanes.Count}, " +
                      $"BA={chosen.Road.BA.Lanes.Count}, median={(chosen.Road.Median != null)}, " +
                      $"totalWidth={chosen.Road.TotalWidth:0.##}m");

            GameObject roadGo = new GameObject($"Road_{chosen.Name}");
            roadGo.transform.SetParent(transform, worldPositionStays: false);
            RoadRenderer renderer = roadGo.AddComponent<RoadRenderer>();
            renderer.Profile = chosen.Road;
            renderer.EndpointA = EndpointA;
            renderer.EndpointB = EndpointB;
            renderer.DriveSide = OverrideDriveSide ? ForcedDriveSide : chosen.DriveSide;
            renderer.Rebuild();
        }

        SavedConfig PickConfig(ExportedConfigFile file)
        {
            if (file.Configs == null || file.Configs.Count == 0) return null;

            // Explicit name match wins.
            if (!string.IsNullOrEmpty(ConfigName))
            {
                foreach (SavedConfig c in file.Configs)
                {
                    if (string.Equals(c.Name, ConfigName, System.StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                Debug.LogWarning($"[RoadSceneTest] No config named \"{ConfigName}\"; falling back to activeId/first.");
            }

            // activeId next.
            if (!string.IsNullOrEmpty(file.ActiveId))
            {
                foreach (SavedConfig c in file.Configs)
                {
                    if (c.Id == file.ActiveId) return c;
                }
            }

            // Last resort: first config.
            return file.Configs[0];
        }

        static string[] FormatNames(ExportedConfigFile file)
        {
            if (file.Configs == null) return new string[0];
            string[] arr = new string[file.Configs.Count];
            for (int i = 0; i < file.Configs.Count; i++) arr[i] = $"\"{file.Configs[i].Name}\"";
            return arr;
        }

        string ResolvePath()
        {
            if (!string.IsNullOrEmpty(AbsolutePath)) return AbsolutePath;
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", RelativePath));
        }
    }
}
