// Editor menu helpers that turn a folder of PBR textures into a
// Standard-shader material in one click. Scans the folder for textures
// by name pattern (albedo / normal / ao / height / metallic), flips
// import settings on the normal map, builds the material, and (for
// asphalt) wires it into the scene's NetworkRenderer.
//
// Source folders are expected to be:
//   Assets/asphalt/   → "Asphalt.mat"   → NetworkRenderer.AsphaltMaterial
//   Assets/grass/     → "Grass.mat"     → user assigns to their ground plane
//
// Texture set naming is flexible — the scan matches any file whose name
// CONTAINS one of: albedo / basecolor / diffuse, normal, ao / occlusion,
// height / displacement, metallic. The "preview" jpg is ignored.

using System.IO;
using NetworkDesigner.Designer;
using NetworkDesigner.Rendering;
using UnityEditor;
using UnityEngine;

namespace NetworkDesigner.EditorTools
{
    public static class SurfaceMaterialBuilder
    {
        [MenuItem("NetworkDesigner/Surface materials/Build asphalt material")]
        public static void BuildAsphalt()
        {
            Material mat = BuildFromFolder("Assets/asphalt", "Asphalt");
            if (mat == null) return;

            NetworkRenderer nr = Object.FindFirstObjectByType<NetworkRenderer>();
            if (nr != null)
            {
                Undo.RecordObject(nr, "Assign asphalt material");
                nr.AsphaltMaterial = mat;
                EditorUtility.SetDirty(nr);
                Debug.Log($"[SurfaceMaterialBuilder] Created {AssetDatabase.GetAssetPath(mat)} " +
                          $"and assigned to NetworkRenderer on '{nr.name}'. " +
                          $"You may want to call Rebuild() on the designer (or restart Play).");
            }
            else
            {
                Debug.Log($"[SurfaceMaterialBuilder] Created {AssetDatabase.GetAssetPath(mat)}. " +
                          $"No NetworkRenderer found in the active scene — drag the material " +
                          $"manually into NetworkRenderer.AsphaltMaterial.");
            }
        }

        [MenuItem("NetworkDesigner/Surface materials/Build grass material")]
        public static void BuildGrass()
        {
            Material mat = BuildFromFolder("Assets/grass", "Grass");
            if (mat == null) return;

            // Try to find a ground GameObject and assign + tile automatically.
            GameObject ground = GameObject.Find("Ground");
            if (ground == null) ground = GameObject.Find("ground");
            if (ground == null)
            {
                Debug.Log($"[SurfaceMaterialBuilder] Created {AssetDatabase.GetAssetPath(mat)}. " +
                          $"No GameObject named 'Ground' found in the active scene — " +
                          $"drag the material onto your ground plane's MeshRenderer manually, " +
                          $"then set Tiling on the material to roughly (worldSize / 2) on each axis.");
                return;
            }

            Renderer r = ground.GetComponent<Renderer>();
            if (r == null)
            {
                Debug.LogWarning($"[SurfaceMaterialBuilder] '{ground.name}' has no Renderer — can't assign.");
                return;
            }

            Undo.RecordObject(r, "Assign grass material");
            r.sharedMaterial = mat;
            EditorUtility.SetDirty(r);

            // Hand off tiling to a GroundTiler component on this GameObject.
            // It owns the TileSize and applies tiling to all PBR slots on
            // the material whenever it (or the plane scale) changes. The
            // tuning panel registers ground.tileSize so it's live-adjustable
            // independent of the road UvTileSize.
            GroundTiler tiler = ground.GetComponent<GroundTiler>();
            if (tiler == null)
            {
                tiler = Undo.AddComponent<GroundTiler>(ground);
                tiler.TileSize = 2f;
            }
            tiler.Apply();
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            Debug.Log($"[SurfaceMaterialBuilder] Assigned grass to '{ground.name}' and attached a " +
                      $"GroundTiler (TileSize={tiler.TileSize}). Adjust via the tuning panel " +
                      $"(Ground → Tile size).");
        }

        // -----------------------------------------------------------------
        // Generic builder
        // -----------------------------------------------------------------

        static Material BuildFromFolder(string folderPath, string materialName)
        {
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                Debug.LogWarning($"[SurfaceMaterialBuilder] Folder '{folderPath}' does not exist.");
                return null;
            }

            // Refresh so newly-dropped texture files are visible in the
            // asset database before we scan.
            AssetDatabase.Refresh();

            Texture2D albedo = null, normal = null, ao = null, height = null, metallic = null;

            string[] guids = AssetDatabase.FindAssets("t:Texture", new[] { folderPath });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string lower = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

                // Skip preview shots — they aren't usable textures.
                if (lower.Contains("preview")) continue;

                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null) continue;

                if (albedo == null && (lower.Contains("albedo") || lower.Contains("basecolor")
                                       || lower.Contains("diffuse"))) albedo = tex;
                else if (normal == null && lower.Contains("normal")) normal = tex;
                else if (ao == null && (lower.Contains("ao") || lower.Contains("occlusion"))) ao = tex;
                else if (height == null && (lower.Contains("height") || lower.Contains("displacement"))) height = tex;
                else if (metallic == null && lower.Contains("metallic")) metallic = tex;
            }

            if (albedo == null)
            {
                Debug.LogWarning($"[SurfaceMaterialBuilder] No albedo texture found in '{folderPath}'. " +
                                 $"Expected a file with 'albedo', 'basecolor', or 'diffuse' in its name.");
                return null;
            }

            // Make sure the normal map is marked as a Normal Map so the
            // Standard shader interprets it as XYZ-in-RG instead of color.
            if (normal != null) EnsureNormalMapImport(AssetDatabase.GetAssetPath(normal));

            // Update IN PLACE so any existing references in the scene
            // (NetworkRenderer.AsphaltMaterial, your ground plane's
            // MeshRenderer, etc.) keep pointing at the same asset and just
            // pick up the new textures.
            string matPath = $"{folderPath}/{materialName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool created = false;
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard")) { name = materialName };
                AssetDatabase.CreateAsset(mat, matPath);
                created = true;
            }
            else
            {
                // Make sure shader is right (in case it was changed manually).
                if (mat.shader == null || mat.shader.name != "Standard")
                {
                    mat.shader = Shader.Find("Standard");
                }
                // Clear any stale slot from a previous texture set.
                mat.SetTexture("_BumpMap", null);
                mat.SetTexture("_OcclusionMap", null);
                mat.SetTexture("_ParallaxMap", null);
                mat.SetTexture("_MetallicGlossMap", null);
                mat.DisableKeyword("_NORMALMAP");
                mat.DisableKeyword("_PARALLAXMAP");
                mat.DisableKeyword("_METALLICGLOSSMAP");
            }

            mat.SetTexture("_MainTex", albedo);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_BumpScale", 1f);
            }
            if (ao != null) mat.SetTexture("_OcclusionMap", ao);
            if (height != null)
            {
                mat.SetTexture("_ParallaxMap", height);
                mat.EnableKeyword("_PARALLAXMAP");
                mat.SetFloat("_Parallax", 0.02f);
            }
            if (metallic != null)
            {
                mat.SetTexture("_MetallicGlossMap", metallic);
                mat.EnableKeyword("_METALLICGLOSSMAP");
            }
            mat.SetFloat("_Glossiness", 0.15f);
            mat.SetFloat("_Metallic", 0f);

            // Log exactly what got picked, so if you have multiple sets in
            // the folder you can tell which one won.
            string picked =
                $"  albedo:   {SafePath(albedo)}\n" +
                $"  normal:   {SafePath(normal)}\n" +
                $"  ao:       {SafePath(ao)}\n" +
                $"  height:   {SafePath(height)}\n" +
                $"  metallic: {SafePath(metallic)}";
            Debug.Log($"[SurfaceMaterialBuilder] {(created ? "Created" : "Updated")} '{matPath}'.\n{picked}");

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        static string SafePath(Object obj)
        {
            return obj == null ? "(none)" : AssetDatabase.GetAssetPath(obj);
        }

        static void EnsureNormalMapImport(string assetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;
            if (importer.textureType == TextureImporterType.NormalMap) return;
            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();
        }
    }
}
