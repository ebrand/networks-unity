// Visual guide: a flat grid drawn on the ground plane.
//
// Implementation: two child GameObjects (minor + major lines), each
// holding a single procedural mesh with MeshTopology.Lines. Splitting
// minor vs major into two meshes (rather than one mesh with vertex
// colors) keeps the shader requirement trivial — we just use the
// built-in Unlit/Color and tint via material.color.
//
// Rebuild() is called automatically on OnEnable / OnValidate, and also
// by external callers (the tuning panel) after live-tweaking fields.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkDesigner.Designer
{
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class GroundGrid : MonoBehaviour
    {
        [Header("Visibility")]
        public bool Enabled = true;
        [Tooltip("When off, minor grid lines are skipped. Useful at large extents where minor lines vanish into pixel noise anyway.")]
        public bool ShowMinor = true;
        [Tooltip("When off, major grid lines are skipped. Combine with ShowMinor=off to hide everything via per-class toggles instead of the global Enabled.")]
        public bool ShowMajor = true;

        [Header("Extent / spacing")]
        [Tooltip("Half-extent of the grid in meters. The grid runs from -Extent to +Extent on both X and Z.")]
        public float Extent = 100f;
        [Tooltip("Distance between minor lines (m).")]
        public float Spacing = 1f;
        [Tooltip("Every N-th line is a major line (drawn with MajorColor / thicker feel).")]
        public int MajorEvery = 10;

        [Header("Colors")]
        public Color MinorColor = new Color(0.30f, 0.32f, 0.36f, 1f);
        public Color MajorColor = new Color(0.55f, 0.58f, 0.65f, 1f);

        [Header("Placement")]
        [Tooltip("Y offset of the grid so it doesn't fight with the ground plane at Y=0. Stays below MeshLift on roads.")]
        public float YOffset = 0.001f;

        GameObject _minorGo;
        GameObject _majorGo;

        void OnEnable()
        {
            Rebuild();
        }

        void OnDisable()
        {
            TearDown();
        }

        void OnValidate()
        {
            // Defer to next tick so we don't try to destroy children
            // during the inspector's serialize pass.
            if (!isActiveAndEnabled) return;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null) Rebuild();
            };
#else
            Rebuild();
#endif
        }

        public void Rebuild()
        {
            TearDown();
            if (!Enabled) return;
            if (Extent <= 0f || Spacing <= 0f) return;

            if (ShowMinor)
            {
                _minorGo = BuildLineGroup("GroundGrid_Minor", MinorColor, wantMajor: false);
            }
            if (ShowMajor)
            {
                _majorGo = BuildLineGroup("GroundGrid_Major", MajorColor, wantMajor: true);
            }
        }

        void TearDown()
        {
            DestroySafe(ref _minorGo);
            DestroySafe(ref _majorGo);

            // Also sweep any child our previous instances might have left
            // behind (e.g. from a hot-reload).
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = transform.GetChild(i).gameObject;
                if (!child.name.StartsWith("GroundGrid_")) continue;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
        }

        void DestroySafe(ref GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
            go = null;
        }

        GameObject BuildLineGroup(string name, Color color, bool wantMajor)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, YOffset, 0f);
            go.hideFlags = HideFlags.DontSave;

            MeshFilter mf = go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            List<Vector3> verts = new List<Vector3>();
            List<int> indices = new List<int>();

            // Count of lines per direction across the half-extent. Always
            // include i=0 (the axis line, which is a major line whenever
            // MajorEvery >= 1).
            int n = Mathf.FloorToInt(Extent / Spacing);
            int every = Mathf.Max(1, MajorEvery);

            for (int i = -n; i <= n; i++)
            {
                bool isMajorLine = (i % every) == 0;
                if (isMajorLine != wantMajor) continue;

                float p = i * Spacing;

                // Line parallel to X axis (varies in X, fixed Z = p).
                int v0 = verts.Count;
                verts.Add(new Vector3(-Extent, 0f, p));
                verts.Add(new Vector3(+Extent, 0f, p));
                indices.Add(v0);
                indices.Add(v0 + 1);

                // Line parallel to Z axis (varies in Z, fixed X = p).
                int v1 = verts.Count;
                verts.Add(new Vector3(p, 0f, -Extent));
                verts.Add(new Vector3(p, 0f, +Extent));
                indices.Add(v1);
                indices.Add(v1 + 1);
            }

            Mesh mesh = new Mesh { name = name + "_Mesh" };
            mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;

            Shader sh = Shader.Find("Unlit/Color");
            if (sh == null)
            {
                Debug.LogWarning("[GroundGrid] Unlit/Color shader not found; grid will be invisible.");
                return go;
            }
            Material mat = new Material(sh)
            {
                name = name + "_Mat",
                color = color,
            };
            mr.sharedMaterial = mat;

            return go;
        }
    }
}
