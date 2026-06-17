// Reads and writes road-config.json files produced by the React
// road-designer tool.
//
// Wire format: JSON keys are camelCase. We use Newtonsoft with a
// CamelCaseNamingStrategy contract resolver to bridge PascalCase C#
// fields to camelCase JSON without per-field annotations, EXCEPT where
// the naming strategy can't recover the JSON name from the C# name
// (e.g. two-letter acronyms like AB/BA). Those fields carry an
// explicit [JsonProperty("ab")] / [JsonProperty("ba")] override.
//
// Enum mapping for DriveSide is handled by a bespoke converter rather
// than a global StringEnumConverter, so Direction ("AB"/"BA") and
// RoadClassification ("primary"/"secondary") remain free to use their
// own casing when they enter the JSON later.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NetworkDesigner.Model;

namespace NetworkDesigner.Import
{
    /// <summary>
    /// Static helper for loading/saving road-config.json. Throws on
    /// missing files, malformed JSON, or unsupported file versions —
    /// callers should catch <see cref="JsonException"/>,
    /// <see cref="IOException"/>, and
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    public static class ConfigImporter
    {
        /// <summary>Schema version this importer understands.</summary>
        public const int SupportedVersion = 1;

        /// <summary>Read and deserialize a road-config.json file.</summary>
        public static ExportedConfigFile LoadFromFile(string path)
        {
            string json = File.ReadAllText(path);
            return LoadFromJson(json);
        }

        /// <summary>Deserialize a road-config JSON string.</summary>
        public static ExportedConfigFile LoadFromJson(string json)
        {
            ExportedConfigFile result =
                JsonConvert.DeserializeObject<ExportedConfigFile>(json, Settings);
            if (result == null)
                throw new JsonException("road-config.json deserialized to null.");
            if (result.Version != SupportedVersion)
                throw new NotSupportedException(
                    $"road-config.json schema version {result.Version} is not supported " +
                    $"(this importer expects version {SupportedVersion}). Either update the " +
                    $"importer or re-export from a compatible road-designer build.");
            return result;
        }

        /// <summary>Serialize back to the same JSON format the React tool exports.</summary>
        public static string SaveToJson(ExportedConfigFile config)
        {
            return JsonConvert.SerializeObject(config, Formatting.Indented, Settings);
        }

        /// <summary>Serialize and write to disk.</summary>
        public static void SaveToFile(string path, ExportedConfigFile config)
        {
            File.WriteAllText(path, SaveToJson(config));
        }

        // Settings cached because rebuilding on every call would be wasteful.
        static readonly JsonSerializerSettings Settings = BuildSettings();

        static JsonSerializerSettings BuildSettings()
        {
            return new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy
                    {
                        // Don't clobber explicit [JsonProperty("...")] names.
                        OverrideSpecifiedNames = false,
                    },
                },
                Converters = new List<JsonConverter>
                {
                    new DriveSideConverter(),
                },
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
            };
        }
    }

    /// <summary>
    /// Maps <see cref="DriveSide"/> to/from the lowercase strings the
    /// React tool emits ("right", "left").
    /// </summary>
    internal class DriveSideConverter : JsonConverter<DriveSide>
    {
        public override void WriteJson(JsonWriter writer, DriveSide value, JsonSerializer serializer)
        {
            writer.WriteValue(value switch
            {
                DriveSide.Right => "right",
                DriveSide.Left => "left",
                _ => throw new JsonSerializationException($"Unhandled DriveSide value: {value}"),
            });
        }

        public override DriveSide ReadJson(JsonReader reader, Type objectType,
            DriveSide existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (reader.Value as string)?.ToLowerInvariant();
            return s switch
            {
                "right" => DriveSide.Right,
                "left" => DriveSide.Left,
                _ => throw new JsonSerializationException($"Unknown DriveSide value: '{s}'."),
            };
        }
    }
}
