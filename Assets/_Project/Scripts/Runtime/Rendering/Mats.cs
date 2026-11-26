using UnityEngine;
using UnityEngine.Rendering;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>Shared URP Lit materials for hardware (rover, suit), with generated textures.</summary>
    public static class Mats
    {
        static Shader lit;
        static Texture2D foil, brushed, fabric, wire, panel;

        static Shader Lit { get { if (lit == null) lit = Shader.Find("Universal Render Pipeline/Lit"); return lit; } }

        public static Material Make(string name, Color c, float metal, float smooth, Texture2D normal = null, float normalScale = 1f)
        {
            var m = new Material(Lit) { name = name };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Metallic", metal);
            m.SetFloat("_Smoothness", smooth);
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.SetFloat("_BumpScale", normalScale);
                m.EnableKeyword("_NORMALMAP");
            }
            return m;
        }

        public static Material Cutout(Material m, Texture2D albedoWithAlpha, Vector2 tiling, bool twoSided = true)
        {
            m.SetTexture("_BaseMap", albedoWithAlpha);
            m.SetTextureScale("_BaseMap", tiling);
            m.SetFloat("_AlphaClip", 1f);
            m.SetFloat("_Cutoff", 0.5f);
            m.EnableKeyword("_ALPHATEST_ON");
            if (twoSided) { m.SetFloat("_Cull", (float)CullMode.Off); m.doubleSidedGI = true; }
            m.renderQueue = (int)RenderQueue.AlphaTest;
            return m;
        }

        /// <summary>An albedo texture (base colour x texture; base colour usually white).</summary>
        public static Material Textured(Material m, Texture2D albedo, Vector2? tiling = null)
        {
            m.SetTexture("_BaseMap", albedo);
            if (tiling.HasValue) m.SetTextureScale("_BaseMap", tiling.Value);
            return m;
        }

        public static Material Emissive(Material m, Color emission, Texture2D map = null)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission);
            if (map != null) { m.SetTexture("_EmissionMap", map); m.SetTexture("_BaseMap", map); }
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return m;
        }

        public static Texture2D Foil { get { if (foil == null) foil = ProcTex.FoilNormal(); return foil; } }
        public static Texture2D Brushed { get { if (brushed == null) brushed = ProcTex.BrushedNormal(); return brushed; } }
        public static Texture2D Fabric { get { if (fabric == null) fabric = ProcTex.FabricNormal(); return fabric; } }
        public static Texture2D Wire { get { if (wire == null) wire = ProcTex.WireMesh(); return wire; } }
        public static Texture2D PanelTex { get { if (panel == null) panel = ProcTex.Panel(); return panel; } }
    }
}
