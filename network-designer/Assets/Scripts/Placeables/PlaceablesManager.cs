// Loads placeable prefabs from Resources/Placeables and runs two modes while the
// Placeables palette is open (cursor not over UI):
//   - a prefab SELECTED -> ghost mode: a translucent preview follows the cursor on the
//     terrain; hold RIGHT mouse and pan left/right to rotate (yaw); LEFT-click places a
//     real one at the ghost's position + rotation (place as many as you like).
//   - NO prefab selected -> grab mode: click a placed object and drag to slide it along
//     the terrain; release drops it (re-settles if it's a physics object).
// DropPhysics on -> placed objects get a Rigidbody (+ auto box collider if none); placed
// objects always get a collider so they stay grabbable.

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Placeables
{
    public class PlaceablesManager : MonoBehaviour
    {
        public Camera PickCamera;          // optional; falls back to Camera.main
        public bool DropPhysics = true;
        [Tooltip("Lift (m) above the hit point to spawn when dropping with physics.")]
        public float DropLift = 0.5f;
        [Tooltip("Small ground clearance (m) for the ghost + static placement, so it doesn't z-fight the terrain.")]
        public float GroundHover = 0.1f;
        [Tooltip("Ghost rotation speed: degrees of yaw per unit of horizontal mouse motion (RMB).")]
        public float RotateSpeed = 8f;
        [Range(0.05f, 1f)] [Tooltip("Opacity of the placement ghost (its real materials, alpha'd). 1 = opaque.")]
        public float GhostAlpha = 1f;

        GameObject _selected;
        GameObject[] _prefabs;
        readonly List<GameObject> _placed = new List<GameObject>();

        // Ghost (placement preview)
        GameObject _ghost, _ghostFor;
        float _ghostYaw;

        // Grab (move)
        GameObject _grabbed;
        Vector3 _grabOffset;
        bool _grabbedWasDynamic;

        public GameObject[] Prefabs => _prefabs ??= LoadDistinct();

        // Resources.LoadAll returns every GameObject under Resources/Placeables — which
        // includes raw model files (.fbx) AND their prefabs, so the same asset shows up
        // twice. De-dupe by name so each appears once. (Best practice: keep only prefabs
        // in Resources/Placeables — whole asset packs with their meshes bloat builds.)
        static GameObject[] LoadDistinct()
        {
            var all = Resources.LoadAll<GameObject>("Placeables");
            var seen = new HashSet<string>();
            var list = new List<GameObject>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && seen.Add(all[i].name)) list.Add(all[i]);
            return list.ToArray();
        }
        public GameObject Selected { get => _selected; set => _selected = value; }

        void Update()
        {
            if (!NetworkDesigner.UI.PaletteBase.IsOpenId("Placeables")) { EndGrab(); DestroyGhost(); return; }
            Camera cam = PickCamera != null ? PickCamera : Camera.main;
            if (cam == null) return;

            if (_selected != null) { GhostMode(cam); return; }
            DestroyGhost();
            GrabMode(cam);
        }

        // --- ghost / place ---

        void GhostMode(Camera cam)
        {
            EndGrab();
            EnsureGhost();
            bool over = RaycastTerrain(cam, out Vector3 tp) && !NetworkDesigner.UI.PaletteBase.PointerOverUI;
            _ghost.SetActive(over);
            if (!over) return;

            _ghost.transform.position = tp + Vector3.up * GroundHover;
            if (Input.GetMouseButton(1)) _ghostYaw -= Input.GetAxis("Mouse X") * RotateSpeed;   // RMB pan = rotate (reversed)
            _ghost.transform.rotation = Quaternion.Euler(0f, _ghostYaw, 0f);
            if (Input.GetMouseButtonDown(0)) PlaceAt(tp, _ghost.transform.rotation);
        }

        void EnsureGhost()
        {
            if (_ghost != null && _ghostFor == _selected) return;
            DestroyGhost();
            _ghost = Instantiate(_selected);
            _ghostFor = _selected;
            _ghost.name = _selected.name + " (ghost)";
            foreach (var c in _ghost.GetComponentsInChildren<Collider>()) c.enabled = false;
            var rb = _ghost.GetComponent<Rigidbody>(); if (rb != null) Destroy(rb);
            // Keep the prefab's REAL look. Below full opacity, instance the materials (so
            // the prefab asset isn't touched) and flip them to translucent; at 1 it's just
            // the opaque prefab.
            if (GhostAlpha < 1f)
                foreach (var r in _ghost.GetComponentsInChildren<Renderer>())
                {
                    var mats = r.materials;   // instanced copies
                    for (int i = 0; i < mats.Length; i++) MakeTransparent(mats[i], GhostAlpha);
                    r.materials = mats;
                }
        }

        // Switch a material to alpha-blended transparency at runtime (URP Lit recipe) and
        // drop its base-colour alpha, so the ghost shows the real look but see-through.
        static void MakeTransparent(Material m, float alpha)
        {
            if (m == null) return;
            m.SetOverrideTag("RenderType", "Transparent");
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);   // URP: transparent
            m.SetFloat("_SrcBlend", (float)(int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)(int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            if (m.HasProperty("_BaseColor")) { Color c = m.GetColor("_BaseColor"); c.a = alpha; m.SetColor("_BaseColor", c); }
            else if (m.HasProperty("_Color")) { Color c = m.GetColor("_Color"); c.a = alpha; m.SetColor("_Color", c); }
        }

        void DestroyGhost()
        {
            if (_ghost != null) Destroy(_ghost);
            _ghost = null; _ghostFor = null;
        }

        void PlaceAt(Vector3 pos, Quaternion rot)
        {
            Vector3 spawn = DropPhysics ? pos + Vector3.up * Mathf.Max(0f, DropLift)
                                        : pos + Vector3.up * GroundHover;
            GameObject go = Instantiate(_selected, spawn, rot);
            go.name = _selected.name;
            EnsureCollider(go);
            if (DropPhysics && go.GetComponent<Rigidbody>() == null) go.AddComponent<Rigidbody>();
            _placed.Add(go);
        }

        // --- grab / move ---

        void GrabMode(Camera cam)
        {
            if (_grabbed != null)
            {
                if (Input.GetMouseButton(0))
                {
                    if (RaycastTerrain(cam, out Vector3 tp)) _grabbed.transform.position = tp + _grabOffset;
                }
                else EndGrab();
                return;
            }
            if (!Input.GetMouseButtonDown(0) || NetworkDesigner.UI.PaletteBase.PointerOverUI) return;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 100000f)) return;
            GameObject placed = FindPlaced(hit.collider.transform);
            if (placed != null) BeginGrab(placed, hit.point);
        }

        void BeginGrab(GameObject go, Vector3 hitPoint)
        {
            _grabbed = go;
            _grabOffset = go.transform.position - hitPoint;
            var rb = go.GetComponent<Rigidbody>();
            _grabbedWasDynamic = rb != null && !rb.isKinematic;
            if (rb != null) rb.isKinematic = true;
            SetColliders(go, false);
        }

        void EndGrab()
        {
            if (_grabbed == null) return;
            SetColliders(_grabbed, true);
            var rb = _grabbed.GetComponent<Rigidbody>();
            if (rb != null && _grabbedWasDynamic) { rb.isKinematic = false; rb.WakeUp(); }
            _grabbed = null;
        }

        GameObject FindPlaced(Transform t)
        {
            while (t != null) { if (_placed.Contains(t.gameObject)) return t.gameObject; t = t.parent; }
            return null;
        }

        // --- shared helpers ---

        static void SetColliders(GameObject go, bool on)
        {
            var cols = go.GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++) cols[i].enabled = on;
        }

        bool RaycastTerrain(Camera cam, out Vector3 point)
        {
            point = default;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            // Low-poly mesh terrain, OR the DEM Unity-Terrain (TerrainCollider) when one's built.
            if (Physics.Raycast(ray, out RaycastHit hit, 100000f)
                && (hit.collider is MeshCollider
                    || (NetworkDesigner.Terrain.DemTerrainWorld.HasWorld && hit.collider is TerrainCollider)))
            { point = hit.point; return true; }
            return false;
        }

        static void EnsureCollider(GameObject go)
        {
            if (go.GetComponentInChildren<Collider>() != null) return;
            var rends = go.GetComponentsInChildren<Renderer>();
            var bc = go.AddComponent<BoxCollider>();
            if (rends.Length == 0) return;
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            Vector3 ls = go.transform.lossyScale;
            bc.center = go.transform.InverseTransformPoint(b.center);
            bc.size = new Vector3(
                Mathf.Abs(ls.x) > 1e-4f ? b.size.x / Mathf.Abs(ls.x) : b.size.x,
                Mathf.Abs(ls.y) > 1e-4f ? b.size.y / Mathf.Abs(ls.y) : b.size.y,
                Mathf.Abs(ls.z) > 1e-4f ? b.size.z / Mathf.Abs(ls.z) : b.size.z);
        }

        public void ClearPlaced()
        {
            EndGrab();
            for (int i = 0; i < _placed.Count; i++) if (_placed[i] != null) Destroy(_placed[i]);
            _placed.Clear();
        }
    }
}
