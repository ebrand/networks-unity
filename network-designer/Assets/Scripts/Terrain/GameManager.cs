// Saved "games": one folder per game under Games/, holding its manifest (DEM set + decode range),
// its object snapshot (autosave.json), and its terrain sculpt edits (ChunkEditsDem/). The trick that
// makes autosave "just work" per-game: point Designer.AutosavePath at <folder>/autosave.json and BOTH
// the snapshot AND the chunk-edit directory follow it (the edit dir is derived from the autosave dir).

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    [System.Serializable]
    public class GameInfo
    {
        public string DemSet;        // Highres folder name the game is built on
        public float NormMin = -500f; // decode range (must match how the DEM PNGs were exported)
        public float NormMax = 2000f;
        public string Created;
    }

    public static class GameManager
    {
        const string LastKey = "NetworksLastGame";

        public static string ActiveGame { get; private set; }
        public static bool HasActiveGame => !string.IsNullOrEmpty(ActiveGame);

        public static string GamesDir =>
#if UNITY_EDITOR
            Path.Combine(Application.dataPath, "..", "Games");
#else
            Path.Combine(Application.persistentDataPath, "Games");
#endif

        public static string Folder(string name) => Path.Combine(GamesDir, Sanitize(name));
        public static string AutosaveFile(string name) => Path.Combine(Folder(name), "autosave.json");
        public static string ManifestFile(string name) => Path.Combine(Folder(name), "game.json");
        public static bool Exists(string name) => !string.IsNullOrWhiteSpace(name) && File.Exists(ManifestFile(name));

        // Folders under Games/ that contain a game.json manifest.
        public static List<string> ListGames()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(GamesDir))
                    foreach (var d in Directory.GetDirectories(GamesDir))
                        if (File.Exists(Path.Combine(d, "game.json")))
                            list.Add(Path.GetFileName(d));
            }
            catch { /* unreadable -> empty */ }
            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static GameInfo Read(string name)
        {
            try
            {
                string p = ManifestFile(name);
                if (!File.Exists(p)) return null;
                return JsonUtility.FromJson<GameInfo>(File.ReadAllText(p));
            }
            catch { return null; }
        }

        // Create a new game folder + manifest. Returns false on bad input or I/O failure.
        public static bool Create(string name, string demSet, float min, float max)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(demSet)) return false;
            try
            {
                Directory.CreateDirectory(Folder(name));
                var info = new GameInfo { DemSet = demSet, NormMin = min, NormMax = max, Created = System.DateTime.Now.ToString("o") };
                File.WriteAllText(ManifestFile(name), JsonUtility.ToJson(info, true));
                return true;
            }
            catch (System.Exception e) { Debug.LogWarning($"[GameManager] create failed: {e.Message}"); return false; }
        }

        public static void SetActive(string name)
        {
            ActiveGame = name;
            if (!string.IsNullOrEmpty(name)) { PlayerPrefs.SetString(LastKey, name); PlayerPrefs.Save(); }
        }

        public static string Last => PlayerPrefs.GetString(LastKey, "");

        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
