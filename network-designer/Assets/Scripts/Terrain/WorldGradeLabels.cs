// World-space 3D grade-% labels for the rail plan — replaces the IMGUI GUI.Box labels. Pooled
// TextMeshPro objects sit at each segment midpoint, billboarded to face the camera and depth-tested
// so hills occlude them. Self-spawns at runtime; TerrainDesigner pushes the grade list each frame.

using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public class WorldGradeLabels : MonoBehaviour
    {
        public static WorldGradeLabels Instance { get; private set; }
        public static float Size = 30f;   // world text height (≈ m at scale 1) — tune to taste
        public static float Lift = 1f;    // metres lifted above the route midpoint
        public static bool LieFlat = true; // true = painted flat on the ground along the track; false = billboard

        static readonly Color Normal = Color.white;
        static readonly Color Over = new Color(1f, 0.45f, 0.4f);

        readonly List<TextMeshPro> _pool = new List<TextMeshPro>();
        readonly List<Vector3> _dir = new List<Vector3>();   // per-label track direction (for flat orient)
        Transform _root;
        Camera _cam;
        int _cleanTick;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            // Kill stale objects left by a hot-reload (the orphaned pool shows as magenta). Match by
            // NAME, not component type — a reload can strip the component but keep the GameObjects.
            foreach (var go0 in FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (go0 != null && (go0.name == "WorldGradeLabels" || go0.name == "GradeLabel"))
                    Destroy(go0);
            var go = new GameObject("WorldGradeLabels") { hideFlags = HideFlags.DontSave };
            Instance = go.AddComponent<WorldGradeLabels>();
            Instance._root = go.transform;
        }

        // Show one label per grade (hide the rest). Empty/null hides all. Call every frame.
        public void Show(List<RailPlanLayer.EdgeGrade> grades, Camera cam)
        {
            if (cam != null) _cam = cam;
            int n = grades?.Count ?? 0;
            while (_pool.Count < n) { _pool.Add(NewLabel()); _dir.Add(Vector3.forward); }
            for (int i = 0; i < _pool.Count; i++)
            {
                var lbl = _pool[i];
                bool on = i < n;
                if (lbl.gameObject.activeSelf != on) lbl.gameObject.SetActive(on);
                if (!on) continue;
                var g = grades[i];
                lbl.fontSize = Size;
                lbl.text = $"{Mathf.Abs(g.GradePct):0.0}%";
                lbl.color = g.Over ? Over : Normal;
                lbl.transform.position = g.Mid + Vector3.up * Lift;
                _dir[i] = g.Dir;     // orientation applied in LateUpdate (camera-aware)
            }
        }

        void LateUpdate()
        {
            // Self-heal across editor hot-reloads: re-adopt after a static reset, drop stale duplicates,
            // and periodically destroy any orphaned GradeLabel not in the current pool (they go magenta
            // when a reload breaks their material). This is what "Clear plan" can't reach.
            if (Instance == null) { Instance = this; _root = transform; SweepOrphans(); }
            else if (Instance != this) { Destroy(gameObject); return; }
            if (++_cleanTick >= 60) { _cleanTick = 0; SweepOrphans(); }

            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            Vector3 camPos = _cam.transform.position;
            for (int i = 0; i < _pool.Count; i++)
            {
                var lbl = _pool[i];
                if (!lbl.gameObject.activeSelf) continue;
                if (LieFlat)
                {
                    // Flat on the ground, reading along the track — but flip 180° so it reads upright
                    // from the camera's side (ground text only reads right from one side).
                    Vector3 d = _dir[i]; d.y = 0f;
                    if (d.sqrMagnitude < 1e-6f) d = Vector3.forward; else d.Normalize();
                    // TMP's readable face is -Z, so point -Z UP (forward = down) at the overhead camera —
                    // otherwise you see the mirrored back. Glyph tops away from camera = right-side up.
                    Vector3 textUp = Vector3.Cross(Vector3.down, d);
                    Vector3 toCam = camPos - lbl.transform.position; toCam.y = 0f;
                    if (Vector3.Dot(textUp, toCam) > 0f) textUp = -textUp;
                    lbl.transform.rotation = Quaternion.LookRotation(Vector3.down, textUp);
                }
                else
                {
                    Vector3 dir = lbl.transform.position - camPos;   // upright billboard facing the camera
                    if (dir.sqrMagnitude > 1e-4f) lbl.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                }
            }
        }

        // Destroy duplicate managers and any GradeLabel object the current pool no longer owns
        // (orphaned hot-reload leftovers that render magenta).
        void SweepOrphans()
        {
            foreach (var w in FindObjectsByType<WorldGradeLabels>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (w != null && w != this) Destroy(w.gameObject);
            foreach (var t in FindObjectsByType<TextMeshPro>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t != null && t.gameObject.name == "GradeLabel" && !_pool.Contains(t))
                    Destroy(t.gameObject);
        }

        TextMeshPro NewLabel()
        {
            var go = new GameObject("GradeLabel") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(_root, false);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.fontSize = Size;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = Normal;
            tmp.outlineWidth = 0.25f;                 // dark outline → readable over the terrain
            tmp.outlineColor = new Color32(0, 0, 0, 230);
            tmp.rectTransform.sizeDelta = new Vector2(12f, 4f);
            return tmp;
        }
    }
}
