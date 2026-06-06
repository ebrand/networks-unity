// A simple draped route/path tool for the DEM terrain (the seed of rail/road planning).
// While active: left-click drops a control point on the surface (raycast), right-click
// removes the last. The line is draped — sampled along each segment and snapped to the
// terrain so it hugs the ground — and the total surface distance is exposed for a HUD.
//
// Self-contained: raycasts whatever collider is under the cursor (the DEM TerrainCollider),
// so it doesn't touch the existing rail/sculpt tools (which filter to MeshCollider).

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public class TerrainRouteTool : MonoBehaviour
    {
        public static bool Active;
        public static float TotalLength { get; private set; }   // metres, along the draped surface
        public static int PointCount { get; private set; }

        static TerrainRouteTool _inst;
        readonly List<Vector3> _pts = new List<Vector3>();       // control points (on surface)
        LineRenderer _line;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (FindFirstObjectByType<TerrainRouteTool>() != null) return;
            new GameObject("TerrainRouteTool").AddComponent<TerrainRouteTool>();
        }

        void Awake()
        {
            _inst = this;
            _line = gameObject.AddComponent<LineRenderer>();
            _line.widthMultiplier = 20f;                          // ~20m so it reads at moderate zoom
            _line.numCornerVertices = 2;
            _line.numCapVertices = 2;
            _line.alignment = LineAlignment.View;
            _line.textureMode = LineTextureMode.Stretch;
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            var mat = new Material(sh);
            mat.color = new Color(1f, 0.45f, 0.1f);
            mat.SetColor("_BaseColor", new Color(1f, 0.45f, 0.1f));
            _line.material = mat;
            _line.startColor = _line.endColor = Color.white;
            _line.positionCount = 0;
        }

        public static void Clear() { if (_inst != null) { _inst._pts.Clear(); _inst.Rebuild(); } }

        void Update()
        {
            if (!Active) return;
            if (NetworkDesigner.UI.PaletteBase.PointerOverUI || NetworkDesigner.UI.PaletteBase.ModalOpen) return;
            var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            if (cam == null) return;

            if (Input.GetMouseButtonDown(0))
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                if (Physics.Raycast(ray, out RaycastHit hit, 200000f))
                {
                    _pts.Add(hit.point);
                    Rebuild();
                }
            }
            else if (Input.GetMouseButtonDown(1) && _pts.Count > 0)
            {
                _pts.RemoveAt(_pts.Count - 1);
                Rebuild();
            }
        }

        void Rebuild()
        {
            var verts = new List<Vector3>();
            for (int i = 0; i < _pts.Count; i++)
            {
                if (i == 0) { verts.Add(Drape(_pts[0])); continue; }
                Vector3 a = _pts[i - 1], b = _pts[i];
                float run = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                int steps = Mathf.Clamp(Mathf.CeilToInt(run / 50f), 1, 4000);  // sample ~every 50m
                for (int s = 1; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    verts.Add(Drape(new Vector3(Mathf.Lerp(a.x, b.x, t), 0f, Mathf.Lerp(a.z, b.z, t))));
                }
            }
            _line.positionCount = verts.Count;
            _line.SetPositions(verts.ToArray());

            float len = 0f;
            for (int i = 1; i < verts.Count; i++) len += Vector3.Distance(verts[i - 1], verts[i]);
            TotalLength = len;
            PointCount = _pts.Count;
        }

        // Snap an XZ to the terrain surface (a hair above so it isn't buried in the texture).
        static Vector3 Drape(Vector3 p)
        {
            if (Physics.Raycast(new Vector3(p.x, 12000f, p.z), Vector3.down, out RaycastHit hit, 30000f))
                return hit.point + Vector3.up * 3f;
            return new Vector3(p.x, p.y + 3f, p.z);
        }
    }
}
