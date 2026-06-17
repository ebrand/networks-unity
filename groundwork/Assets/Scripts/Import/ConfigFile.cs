// Mirrors road-designer/src/storage/configs.ts so road-config.json
// produced by the React tool round-trips into Unity.
//
// Note on field names: the React tool exports camelCase keys (e.g.
// "driveSide", "shoulderAB", "exportedAt"). To deserialize with Unity's
// built-in JsonUtility, the C# fields must match exactly — meaning the
// fields here would need to be camelCase. To deserialize with
// Newtonsoft.Json (shipped with Unity 6 via UPM) just add
// [JsonProperty("camelName")] attributes to each field.
//
// The strawman uses PascalCase + leaves serialization unattached. Pick
// the attribute style when the importer is wired up.

using System;
using System.Collections.Generic;
using NetworkDesigner.Model;

namespace NetworkDesigner.Import
{
    /// <summary>
    /// A named, persisted snapshot of a road profile plus its drive-side
    /// context. Same shape as SavedConfig in the React tool.
    ///
    /// IMPORTANT: today this references a single <see cref="RoadProfile"/>
    /// — a cross-section design, not a network. The React tool's saved
    /// "configurations" are libraries of cross-sections. When we move
    /// to authoring full networks, this type will gain a Network field
    /// (or be replaced by a SavedNetwork type that holds Network +
    /// metadata).
    /// </summary>
    [Serializable]
    public class SavedConfig
    {
        public string Id;
        public string Name;
        /// <summary>
        /// Optional free-form grouping label. Empty/null is treated as
        /// "Uncategorized" by the Unity tool palette.
        /// </summary>
        public string Category;
        public RoadProfile Road;
        public DriveSide DriveSide;
        /// <summary>
        /// Millis since the Unix epoch. The React tool writes
        /// <c>Date.now()</c> here; on the Unity side, convert to
        /// <see cref="DateTimeOffset.FromUnixTimeMilliseconds(long)"/>
        /// if you need a real datetime.
        /// </summary>
        public long UpdatedAt;
    }

    /// <summary>
    /// File format consumed by the runtime importer. Matches the
    /// pretty-printed JSON that the React tool downloads as
    /// "road-config.json".
    ///
    /// <see cref="Version"/> is bumped whenever the schema changes;
    /// the importer should branch on it instead of silently mis-parsing
    /// old files.
    /// </summary>
    [Serializable]
    public class ExportedConfigFile
    {
        public int Version = 1;
        /// <summary>ISO-8601 timestamp string from the exporter.</summary>
        public string ExportedAt;
        public string ActiveId;
        public List<SavedConfig> Configs = new List<SavedConfig>();
    }
}
