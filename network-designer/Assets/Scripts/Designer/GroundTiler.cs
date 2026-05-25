// Owns the texture-tile sizing for the ground plane's material.
//
// Computes a tiling factor from the renderer's world bounds so the
// material's textures (albedo + normal + AO + height + metallic) repeat
// once every TileSize meters, regardless of the plane's scale.
//
// Drops onto any GameObject with a Renderer. SurfaceMaterialBuilder
// adds it automatically when it builds the grass material.

using UnityEngine;

namespace NetworkDesigner.Designer
{
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [RequireComponent(typeof(Renderer))]
    public class GroundTiler : MonoBehaviour
    {
        [Tooltip("World meters per texture tile. Texture repeats once every TileSize meters in both X and Z.")]
        public float TileSize = 2f;

        void OnEnable()
        {
            Apply();
        }

        void OnValidate()
        {
            if (!isActiveAndEnabled) return;
#if UNITY_EDITOR
            // Defer so we don't mutate assets during the inspector's
            // serialize pass.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null) Apply();
            };
#else
            Apply();
#endif
        }

        // Re-apply tiling to the current sharedMaterial. Writes to all the
        // PBR slots so albedo/normal/AO/height/metallic stay in sync.
        // sharedMaterial is intentional — we want the change to persist
        // to the .mat asset, not be a per-instance overlay.
        public void Apply()
        {
            Renderer r = GetComponent<Renderer>();
            if (r == null) return;
            Material mat = r.sharedMaterial;
            if (mat == null) return;

            float worldSize = Mathf.Max(r.bounds.size.x, r.bounds.size.z);
            float ts = Mathf.Max(0.001f, TileSize);
            float factor = Mathf.Max(1f, worldSize / ts);
            Vector2 tiling = new Vector2(factor, factor);

            string[] slots = { "_MainTex", "_BumpMap", "_OcclusionMap", "_ParallaxMap", "_MetallicGlossMap" };
            foreach (string slot in slots)
            {
                if (mat.HasProperty(slot)) mat.SetTextureScale(slot, tiling);
            }
        }
    }
}
