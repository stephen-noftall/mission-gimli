using UnityEngine;

namespace MoonBaseAlpha.UI
{
    /// <summary>
    /// Procedural helmet frame for the first-person view: the visor opening (a rounded superellipse),
    /// the dark helmet interior around it, a soft edge, the bright rim of the visor seal and faint
    /// reflections on the visor surface. Generated once (no image assets).
    /// </summary>
    public static class HelmetOverlay
    {
        static Texture2D tex;

        public static Texture2D Texture
        {
            get
            {
                if (tex == null) tex = Build(640, 360);
                return tex;
            }
        }

        /// <summary>Shader-style smoothstep(e0, e1, x). (Mathf.SmoothStep is an interpolation, not this.)</summary>
        public static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        static Texture2D Build(int W, int H)
        {
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "HelmetFrame", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[W * H];
            const float n = 3.2f;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                // normalised coords, visor centre slightly above screen centre (helmet sits on the shoulders)
                float u = (x + 0.5f) / W * 2 - 1, v = (y + 0.5f) / H * 2 - 1;
                float vx = u / 1.06f, vy = (v - 0.04f) / (v > 0 ? 1.02f : 1.12f);
                float r = Mathf.Pow(Mathf.Pow(Mathf.Abs(vx), n) + Mathf.Pow(Mathf.Abs(vy), n), 1f / n);
                // inside the visor: clear, with faint reflections near the top-left and a slight edge darkening
                float edge = Smooth(0.86f, 1.0f, r);
                float alpha = edge * 0.97f;
                Color c = new Color(0.012f, 0.012f, 0.014f, alpha);
                // visor seal rim: thin grey band right at the edge
                float rim = Mathf.Exp(-Mathf.Pow((r - 0.985f) / 0.012f, 2));
                c = Color.Lerp(c, new Color(0.22f, 0.22f, 0.23f, 0.9f), rim * 0.8f);
                // reflections on the curved visor (very faint)
                float refl = Mathf.Exp(-Mathf.Pow((u + v * 0.6f + 0.55f) / 0.05f, 2)) * Smooth(0.2f, 0.9f, v) * (1 - edge);
                float refl2 = Mathf.Exp(-Mathf.Pow((u + v * 0.6f + 0.38f) / 0.02f, 2)) * Smooth(0.3f, 0.9f, v) * (1 - edge);
                c = Color.Lerp(c, new Color(1, 1, 1, 0.025f), Mathf.Clamp01(refl * 0.5f + refl2 * 0.3f));
                // chin / neck ring shadow at the bottom
                float chin = Smooth(-0.78f, -1.0f, v) * Smooth(0.9f, 0.2f, Mathf.Abs(u));
                c.a = Mathf.Max(c.a, chin * 0.85f);
                px[y * W + x] = c;
            }
            t.SetPixels(px);
            t.Apply(false, false);
            return t;
        }
    }
}
