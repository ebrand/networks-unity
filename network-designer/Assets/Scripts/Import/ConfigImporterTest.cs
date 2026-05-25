// Smoke-test MonoBehaviour for the JSON importer. Attach to any
// GameObject in your scene, press Play, and watch the Console: it
// should log the configurations found in road-config.json.
//
// If the file lives at the project root (alongside Assets/, Packages/,
// ProjectSettings/), leave RelativePath at its default. For other
// locations, either tweak RelativePath or set AbsolutePath in the
// inspector.

using System.IO;
using UnityEngine;
using NetworkDesigner.Import;
using NetworkDesigner.Model;

namespace NetworkDesigner.Import
{
    public class ConfigImporterTest : MonoBehaviour
    {
        [Tooltip("Path relative to the Unity project root (the folder containing Assets/). " +
                 "Ignored when AbsolutePath is set.")]
        public string RelativePath = "road-config.json";

        [Tooltip("Optional absolute path to the road-config.json file. Overrides RelativePath when non-empty.")]
        public string AbsolutePath = "";

        void Start()
        {
            string path = ResolvePath();
            if (!File.Exists(path))
            {
                Debug.LogError($"[ConfigImporterTest] File not found: {path}");
                return;
            }

            ExportedConfigFile file;
            try
            {
                file = ConfigImporter.LoadFromFile(path);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ConfigImporterTest] Failed to load {path}: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            Debug.Log(
                $"[ConfigImporterTest] Loaded {file.Configs.Count} configuration(s) from {path}. " +
                $"version={file.Version}, exportedAt={file.ExportedAt}, activeId={file.ActiveId}");

            foreach (SavedConfig cfg in file.Configs)
            {
                RoadProfile r = cfg.Road;
                int abLanes = r?.AB?.Lanes?.Count ?? 0;
                int baLanes = r?.BA?.Lanes?.Count ?? 0;
                bool hasMedian = r?.Median != null;
                Debug.Log(
                    $"  • \"{cfg.Name}\" [{cfg.Id}] driveSide={cfg.DriveSide} " +
                    $"AB={abLanes}lanes BA={baLanes}lanes median={hasMedian} " +
                    $"totalWidth={r?.TotalWidth:0.##}m");
            }
        }

        string ResolvePath()
        {
            if (!string.IsNullOrEmpty(AbsolutePath)) return AbsolutePath;
            // Application.dataPath points at the Assets/ folder; the project
            // root is one level above. Path.GetFullPath collapses the "..".
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", RelativePath));
        }
    }
}
