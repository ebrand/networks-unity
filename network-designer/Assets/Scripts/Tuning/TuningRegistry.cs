// Central registry of "tunable" settings exposed to external clients
// (currently the React control panel via TuningServer's WebSocket).
//
// Each entry has:
//   - a stable string key (e.g. "designer.markerDiameter")
//   - a type tag ("float", "color", "bool", "vector3") so the client
//     knows how to render and serialize
//   - getter + setter delegates that read/write the actual scene state
//   - optional metadata (min/max/step for floats, label, category)
//
// Registration is explicit and component-side (each component that owns
// tunable fields calls RegisterFloat / RegisterColor / etc. in its
// OnEnable). Explicit beats reflection here — easier to grep, easier to
// reason about update semantics, no attribute boilerplate.
//
// All set-paths invoke the setter on the Unity main thread (the calls
// originate from TuningServer.Update which runs on the main thread).

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NetworkDesigner.Model;

namespace NetworkDesigner.Tuning
{
    public static class TuningRegistry
    {
        // Fires after a successful Set so external code (TuningSetup) can
        // debounce + persist to disk without polling.
        public static event Action OnValueChanged;

        // Default location of the persistence file. In Editor: project
        // root (next to Assets/). In a built player: persistent data path.
        public static string DefaultPersistencePath
        {
            get
            {
#if UNITY_EDITOR
                return Path.Combine(Application.dataPath, "..", "TuningOverrides.json");
#else
                return Path.Combine(Application.persistentDataPath, "TuningOverrides.json");
#endif
            }
        }

        public class Entry
        {
            public string Key;
            public string Type;        // "float" | "color" | "bool" | "vector3"
            public string Category;    // grouping hint for UI
            public string Label;       // display label (defaults to Key)
            public Func<object> Get;
            public Action<object> Set;
            public Dictionary<string, object> Meta = new Dictionary<string, object>();
        }

        // Order-preserving so the React panel can render in registration order.
        static readonly List<Entry> _orderedKeys = new List<Entry>();
        static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();

        public static IReadOnlyList<Entry> Entries => _orderedKeys;

        public static void Clear()
        {
            _entries.Clear();
            _orderedKeys.Clear();
        }

        public static bool TrySet(string key, object value, out string error)
        {
            error = null;
            if (!_entries.TryGetValue(key, out Entry e))
            {
                error = $"unknown key '{key}'";
                return false;
            }
            try
            {
                e.Set(value);
                OnValueChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static object TryGet(string key)
        {
            if (_entries.TryGetValue(key, out Entry e)) return e.Get();
            return null;
        }

        // -----------------------------------------------------------------
        // Registration helpers
        // -----------------------------------------------------------------

        public static void RegisterFloat(string key, string category, string label,
            Func<float> get, Action<float> set,
            float min, float max, float step = 0f)
        {
            Add(new Entry
            {
                Key = key,
                Type = "float",
                Category = category,
                Label = label ?? key,
                Get = () => get(),
                Set = v => set(ToFloat(v)),
                Meta =
                {
                    { "min", min },
                    { "max", max },
                    { "step", step <= 0f ? Mathf.Max(0.001f, (max - min) / 200f) : step },
                },
            });
        }

        public static void RegisterColor(string key, string category, string label,
            Func<Color> get, Action<Color> set)
        {
            Add(new Entry
            {
                Key = key,
                Type = "color",
                Category = category,
                Label = label ?? key,
                Get = () => ColorToHex(get()),
                Set = v => set(HexToColor(v as string)),
            });
        }

        public static void RegisterBool(string key, string category, string label,
            Func<bool> get, Action<bool> set)
        {
            Add(new Entry
            {
                Key = key,
                Type = "bool",
                Category = category,
                Label = label ?? key,
                Get = () => get(),
                Set = v => set(Convert.ToBoolean(v)),
            });
        }

        // Registers a RoadProfile-typed setting. The wire form is the
        // same JSON shape the React tool exports (camelCase, AB/BA stored
        // under "ab"/"ba", etc.) so a Road from the React side drops
        // straight into a Unity RoadProfile without conversion.
        public static void RegisterProfile(string key, string category, string label,
            Func<RoadProfile> get, Action<RoadProfile> set)
        {
            Add(new Entry
            {
                Key = key,
                Type = "profile",
                Category = category,
                Label = label ?? key,
                Get = () =>
                {
                    RoadProfile p = get();
                    if (p == null) return null;
                    // Round-trip through the camelCase contract so the
                    // wire shape matches the React side exactly.
                    string json = JsonConvert.SerializeObject(p, ProfileSerializerSettings);
                    return JObject.Parse(json);
                },
                Set = v =>
                {
                    if (v == null) { set(null); return; }
                    JObject jo = v as JObject ?? JObject.FromObject(v);
                    RoadProfile p = jo.ToObject<RoadProfile>(JsonSerializer.Create(ProfileSerializerSettings));
                    set(p);
                },
            });
        }

        // Shared camelCase contract so RoadProfile round-trips identically
        // to what the React tool's road-config.json importer expects.
        static JsonSerializerSettings _profileSerializerSettings;
        static JsonSerializerSettings ProfileSerializerSettings
        {
            get
            {
                if (_profileSerializerSettings == null)
                {
                    _profileSerializerSettings = new JsonSerializerSettings
                    {
                        ContractResolver = new DefaultContractResolver
                        {
                            NamingStrategy = new CamelCaseNamingStrategy
                            {
                                OverrideSpecifiedNames = false,
                            },
                        },
                        NullValueHandling = NullValueHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                    };
                }
                return _profileSerializerSettings;
            }
        }

        public static void RegisterVector3(string key, string category, string label,
            Func<Vector3> get, Action<Vector3> set,
            float min, float max, float step = 0f)
        {
            Add(new Entry
            {
                Key = key,
                Type = "vector3",
                Category = category,
                Label = label ?? key,
                Get = () =>
                {
                    Vector3 vv = get();
                    return new float[] { vv.x, vv.y, vv.z };
                },
                Set = v =>
                {
                    Vector3 nv = ToVector3(v);
                    set(nv);
                },
                Meta =
                {
                    { "min", min },
                    { "max", max },
                    { "step", step <= 0f ? Mathf.Max(0.001f, (max - min) / 200f) : step },
                },
            });
        }

        static void Add(Entry e)
        {
            if (_entries.ContainsKey(e.Key))
            {
                // Replace — supports re-registration on script reload.
                Entry old = _entries[e.Key];
                int idx = _orderedKeys.IndexOf(old);
                _orderedKeys[idx] = e;
                _entries[e.Key] = e;
                return;
            }
            _entries[e.Key] = e;
            _orderedKeys.Add(e);
        }

        // -----------------------------------------------------------------
        // Persistence
        // -----------------------------------------------------------------

        // Snapshot every registered value to a JSON file. Values are read
        // through the same accessors the WS server uses, so the file format
        // matches what the React client sends back over the wire.
        public static bool SaveToFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                Dictionary<string, object> dict = new Dictionary<string, object>();
                for (int i = 0; i < _orderedKeys.Count; i++)
                {
                    Entry e = _orderedKeys[i];
                    dict[e.Key] = e.Get();
                }
                string json = JsonConvert.SerializeObject(dict, Formatting.Indented);
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TuningRegistry] SaveToFile('{path}') failed: {ex.Message}");
                return false;
            }
        }

        // Apply overrides from a JSON file. Missing keys are silently
        // ignored (so the file can outlive code changes that add/remove
        // tunables). Unknown keys in the file are also ignored.
        public static int LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
            int applied = 0;
            try
            {
                string json = File.ReadAllText(path);
                JObject obj = JObject.Parse(json);
                foreach (KeyValuePair<string, JToken> kv in obj)
                {
                    if (!_entries.ContainsKey(kv.Key)) continue;
                    object val = kv.Value?.ToObject<object>();
                    if (TrySet(kv.Key, val, out _)) applied++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TuningRegistry] LoadFromFile('{path}') failed: {ex.Message}");
            }
            return applied;
        }

        // -----------------------------------------------------------------
        // Value conversion helpers
        // -----------------------------------------------------------------

        static float ToFloat(object v)
        {
            // Newtonsoft typically delivers numbers as double or long.
            if (v is float f) return f;
            if (v is double d) return (float)d;
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is string s && float.TryParse(s, out float fs)) return fs;
            return Convert.ToSingle(v);
        }

        static Vector3 ToVector3(object v)
        {
            if (v is Vector3 vv) return vv;
            if (v is float[] fa && fa.Length >= 3) return new Vector3(fa[0], fa[1], fa[2]);
            if (v is double[] da && da.Length >= 3) return new Vector3((float)da[0], (float)da[1], (float)da[2]);
            if (v is System.Collections.IList list && list.Count >= 3)
            {
                return new Vector3(ToFloat(list[0]), ToFloat(list[1]), ToFloat(list[2]));
            }
            throw new ArgumentException("expected [x,y,z] array");
        }

        public static string ColorToHex(Color c)
        {
            // Always opaque hex for now; tuning UI doesn't need alpha yet.
            Color32 c32 = c;
            return $"#{c32.r:X2}{c32.g:X2}{c32.b:X2}";
        }

        public static Color HexToColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.magenta;
            if (hex[0] == '#') hex = hex.Substring(1);
            if (hex.Length != 6 && hex.Length != 8) return Color.magenta;
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            byte a = hex.Length == 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
            return new Color32(r, g, b, a);
        }
    }
}
