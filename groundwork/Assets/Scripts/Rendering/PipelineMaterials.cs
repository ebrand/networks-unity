// Centralizes built-in shader selection so a Built-in RP -> URP pipeline
// switch is contained to THIS ONE file. Detects the active render pipeline at
// runtime (GraphicsSettings.currentRenderPipeline) and returns the matching
// shader/material, so the same gameplay/rendering code works unchanged in
// both Built-in RP (Standard, Unlit/Color) and URP (Universal Render
// Pipeline/Lit, Universal Render Pipeline/Unlit).
//
// Sprites/Default (LineRenderers / TrailRenderers) is intentionally NOT routed
// through here: it renders in both pipelines, so those sites are left as-is.
//
// Lives in the root NetworkDesigner namespace so every sub-namespace
// (Rendering / Designer / Agents / Editor) can reference it without a using.

using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkDesigner
{
    public static class PipelineMaterials
    {
        // True when a Scriptable Render Pipeline (URP/HDRP) is active; false
        // for the Built-in Render Pipeline.
        static bool Srp => GraphicsSettings.currentRenderPipeline != null;

        // Lit, fully matte (no gloss, no metallic). Asphalt, road markings,
        // intersections, agents, ghosts — anything that should read as flat
        // diffuse paint and receive lighting + shadows. Material.color maps to
        // _Color (Built-in Standard) or _BaseColor (URP Lit) — both are tagged
        // [MainColor], so the setter is pipeline-agnostic.
        public static Material CreateLitMatte(Color color, string name = "LitMatte")
            => CreateLit(color, 0f, name);

        // Lit, with an explicit gloss/smoothness in [0,1] (matte = 0). The
        // gloss property name differs between pipelines; the 0..1 range is
        // comparable enough across them.
        public static Material CreateLit(Color color, float smoothness, string name = "Lit")
        {
            Shader sh = Srp ? Shader.Find("Universal Render Pipeline/Lit")
                            : Shader.Find("Standard");
            Material m = new Material(sh) { name = name, color = color };
            m.SetFloat("_Metallic", 0f);
            if (Srp) m.SetFloat("_Smoothness", smoothness);
            else m.SetFloat("_Glossiness", smoothness);
            return m;
        }

        // Lit + alpha-blended (translucent ghosts/previews). Transparency setup
        // differs per pipeline: the Built-in branch is the classic Standard
        // _Mode=3 incantation; the URP branch uses URP Lit's _Surface/_Blend.
        // NOTE: the URP transparency branch is unverified — check it renders
        // translucent (not opaque/invisible) once URP is active.
        public static Material CreateLitTransparent(Color color, float smoothness, string name = "LitTransparent")
        {
            Material m;
            if (Srp)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                m.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent
                m.SetFloat("_Blend", 0f);     // 0 = Alpha
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetFloat("_Smoothness", smoothness);
            }
            else
            {
                m = new Material(Shader.Find("Standard")) { name = name };
                m.SetFloat("_Mode", 3f);      // Transparent
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.DisableKeyword("_ALPHATEST_ON");
                m.EnableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.SetFloat("_Glossiness", smoothness);
            }
            m.SetFloat("_Metallic", 0f);
            m.renderQueue = 3000;
            m.color = color;
            return m;
        }

        // Unlit solid color — preview/ghost meshes drawn on top of the world.
        public static Material CreateUnlitColor(Color color, string name = "UnlitColor")
        {
            Shader sh = Srp ? Shader.Find("Universal Render Pipeline/Unlit")
                            : Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default"); // last-ditch
            return new Material(sh) { name = name, color = color };
        }

        // Unlit, alpha-blended (color.a = opacity). Mirrors CreateLitTransparent's URP setup, minus lighting.
        public static Material CreateUnlitTransparent(Color color, string name = "UnlitTransparent")
        {
            Material m;
            if (Srp)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = name };
                m.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent
                m.SetFloat("_Blend", 0f);     // 0 = Alpha
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            else
            {
                m = new Material(Shader.Find("Unlit/Transparent")) { name = name };
            }
            m.renderQueue = 3000;
            m.color = color;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            return m;
        }
    }
}
