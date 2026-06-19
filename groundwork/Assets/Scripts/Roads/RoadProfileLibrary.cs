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
        static List<SavedConfig> _configs;   // merged: React road-config.json + in-game profiles
        static List<SavedConfig> _user;       // in-game profiles (authored by the Road Designer)

        public static IReadOnlyList<SavedConfig> Configs => _configs ??= LoadMerged();
        // In-game profiles only — editable + persisted to a separate file (React's road-config.json stays read-only).
        public static List<SavedConfig> UserConfigs => _user ??= LoadFrom(UserPath);
        public static void Reload() { _configs = null; _user = null; }

        public static RoadProfile Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var c in Configs)
                if (c != null && (c.Id == id || c.Name == id)) return c.Road;
            return null;
        }

        // The full saved config (id + Road + Corridor stack + drive side) by id or name.
        public static SavedConfig ResolveConfig(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var c in Configs)
                if (c != null && (c.Id == id || c.Name == id)) return c;
            return null;
        }

        // The authored corridor STACK for a profile — the rendering source of truth. Falls back to migrating the
        // derived RoadProfile for legacy (pre-stack) profiles so they still render via the stack pipeline.
        public static CorridorStack ResolveStack(string id)
        {
            SavedConfig c = ResolveConfig(id);
            if (c == null) return null;
            return c.Corridor ?? CorridorStack.FromRoadProfile(c.Road);
        }

        // The chosen profile's total cross-section width, or `fallback` if none/invalid.
        public static float TotalWidth(string id, float fallback)
        {
            var p = Resolve(id);
            return p != null && p.TotalWidth > 0.1f ? p.TotalWidth : fallback;
        }

        static string ReactPath => Path.Combine(Application.dataPath, "..", "road-config.json");
        static string UserPath => Path.Combine(Application.dataPath, "..", "road-profiles-ingame.json");

        static List<SavedConfig> LoadFrom(string path)
        {
            try { if (File.Exists(path)) return ConfigImporter.LoadFromFile(path).Configs ?? new List<SavedConfig>(); }
            catch (Exception e) { Debug.LogWarning($"[RoadProfileLibrary] load failed ({path}): {e.Message}"); }
            return new List<SavedConfig>();
        }

        // React profiles first, then in-game ones (in-game overrides a same-id React profile).
        static List<SavedConfig> LoadMerged()
        {
            var merged = new List<SavedConfig>(LoadFrom(ReactPath));
            foreach (var u in UserConfigs)
            {
                if (u == null) continue;
                merged.RemoveAll(c => c != null && c.Id == u.Id);
                merged.Add(u);
            }
            return merged;
        }

        // Add or update an in-game profile (keyed by Id) and persist to the in-game file.
        public static void SaveUserConfig(SavedConfig cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.Id)) return;
            UserConfigs.RemoveAll(c => c != null && c.Id == cfg.Id);
            UserConfigs.Add(cfg);
            Persist();
        }

        public static void DeleteUserConfig(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            UserConfigs.RemoveAll(c => c != null && c.Id == id);
            Persist();
        }

        static void Persist()
        {
            try { ConfigImporter.SaveToFile(UserPath, new ExportedConfigFile { Configs = new List<SavedConfig>(UserConfigs) }); }
            catch (Exception e) { Debug.LogWarning($"[RoadProfileLibrary] save failed: {e.Message}"); }
            _configs = null;   // force a re-merge so the road planner's dropdown sees the change
        }
    }
}
