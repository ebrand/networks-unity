// Shared, lazily-loaded library of named road profiles from road-config.json (the React Road Designer's
// output) — so the road plan + Road palette can pick a profile without coupling to the React-bridged
// NetworkDesigner. Path matches NetworkDesigner.ResolveRoadConfigPath (project root).

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using NetworkDesigner.Import;
using NetworkDesigner.Model;

namespace NetworkDesigner.Roads
{
    public static class RoadProfileLibrary
    {
        static List<SavedConfig> _configs;

        public static IReadOnlyList<SavedConfig> Configs => _configs ??= Load();
        public static void Reload() => _configs = null;

        public static RoadProfile Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var c in Configs)
                if (c != null && (c.Id == id || c.Name == id)) return c.Road;
            return null;
        }

        // The chosen profile's total cross-section width, or `fallback` if none/invalid.
        public static float TotalWidth(string id, float fallback)
        {
            var p = Resolve(id);
            return p != null && p.TotalWidth > 0.1f ? p.TotalWidth : fallback;
        }

        static List<SavedConfig> Load()
        {
            try
            {
                string path = Path.Combine(Application.dataPath, "..", "road-config.json");
                if (File.Exists(path)) return ConfigImporter.LoadFromFile(path).Configs ?? new List<SavedConfig>();
                Debug.LogWarning($"[RoadProfileLibrary] road-config.json not found at '{path}'.");
            }
            catch (Exception e) { Debug.LogWarning($"[RoadProfileLibrary] load failed: {e.Message}"); }
            return new List<SavedConfig>();
        }
    }
}
