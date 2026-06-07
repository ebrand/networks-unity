// Sculpt brush for the DEM Unity Terrain (raise / lower / smooth / flatten). While active,
// left-drag over the terrain edits the heights under the cursor; a draped ring shows the
// brush footprint. Raycasts only the DEM TerrainCollider, so it never fights the low-poly
// mesh-sculpt tools (which filter to MeshCollider). The actual height edit lives in
// DemTerrainWorld.Sculpt (it owns the tile grid); this is just input + the cursor ring.
//
// Edits are in-memory on the runtime TerrainData — rebuilding the DEM world resets them, and
// the source heightmaps are never touched. After big edits, re-apply Slope textures / Grass
// Detail so they match the new shape.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public class DemTerrainSculpt : MonoBehaviour
    {
        public static bool Active;
        public static DemTerrainWorld.SculptMode Mode = DemTerrainWorld.SculptMode.Raise;
        public static float Radius = 80f;     // world metres
        public static float Strength = 8f;    // metres/sec (raise/lower); approach speed (flatten/smooth)

        float _targetY;     // Flatten target — the height under the cursor when the stroke began
        bool _stroke;
        LineRenderer _ring;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (FindFirstObjectByType<DemTerrainSculpt>() != null) return;
            new GameObject("DemTerrainSculpt").AddComponent<DemTerrainSculpt>();
        }

        void Awake()
        {
            _ring = gameObject.AddComponent<LineRenderer>();
            _ring.widthMultiplier = 2.5f;
            _ring.loop = true;
            _ring.useWorldSpace = true;
            _ring.alignment = LineAlignment.View;
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            var m = new Material(sh);
            var col = new Color(1f, 0.85f, 0.2f);
            m.color = col; m.SetColor("_BaseColor", col);
            _ring.material = m;
            _ring.startColor = _ring.endColor = col;
            _ring.positionCount = 0;
        }

        void Update()
        {
            // Only sculpt in its own context — the System palette (where the sculpt controls
            // live) must be open. Otherwise switching to Rail/etc. would leave it flattening
            // the ground under your placement clicks and drawing a stray brush ring.
            if (!Active || !NetworkDesigner.UI.PaletteBase.IsOpenId("System")) { Hide(); return; }
            if (NetworkDesigner.UI.PaletteBase.PointerOverUI || NetworkDesigner.UI.PaletteBase.ModalOpen) { Hide(); return; }
            var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 200000f) || !(hit.collider is TerrainCollider))
            { Hide(); _stroke = false; return; }

            DrawRing(hit.point);

            if (Input.GetMouseButtonDown(0)) { _stroke = true; _targetY = hit.point.y; }
            if (Input.GetMouseButtonUp(0)) _stroke = false;
            if (_stroke && Input.GetMouseButton(0))
                DemTerrainWorld.Sculpt(hit.point, Radius, Strength, Time.deltaTime, Mode, _targetY);
        }

        void Hide() { if (_ring != null && _ring.positionCount != 0) _ring.positionCount = 0; }

        // A ring of `N` points around the cursor, each draped onto the terrain surface.
        void DrawRing(Vector3 center)
        {
            const int N = 48;
            if (_ring.positionCount != N) _ring.positionCount = N;
            for (int i = 0; i < N; i++)
            {
                float a = (float)i / N * Mathf.PI * 2f;
                float x = center.x + Mathf.Cos(a) * Radius;
                float z = center.z + Mathf.Sin(a) * Radius;
                float y = center.y;
                if (Physics.Raycast(new Vector3(x, center.y + 4000f, z), Vector3.down, out RaycastHit rh, 30000f))
                    y = rh.point.y + 2f;
                _ring.SetPosition(i, new Vector3(x, y, z));
            }
        }
    }
}
