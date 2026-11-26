using Unity.Mathematics;
using UnityEngine;

namespace MoonBaseAlpha.Cave
{
    /// <summary>
    /// C# port of the CaveRock.shader surface classification (same hash / value noise / fbm, same thresholds), so
    /// gameplay can ask "what deposit is on the rock here": the 3D tube map colours and legend, test screenshots.
    /// Positions are frame coordinates (world + origin), exactly as the shader's fp.
    /// Keep in sync with Shaders/CaveRock.shader (Surface()).
    /// </summary>
    public static class RockDeposits
    {
        public enum Kind { Basalt, Dust, Glaze, OrangeGlass, GreenGlass, Crust }

        public static readonly Color OrangeGlassCol = new(0.42f, 0.22f, 0.08f), GreenGlassCol = new(0.2f, 0.27f, 0.12f);
        public static readonly Color CrustCol = new(0.62f, 0.58f, 0.36f), GlazeCol = new(0.12f, 0.12f, 0.13f);

        static float Frac(float x) => x - math.floor(x);
        static float3 Frac(float3 x) => x - math.floor(x);

        public static float Hash13(float3 p)
        {
            p = Frac(p * 0.1031f);
            p += math.dot(p, p.zyx + 31.32f);
            return Frac((p.x + p.y) * p.z);
        }

        public static float VNoise(float3 x)
        {
            float3 i = math.floor(x), f = Frac(x);
            f = f * f * (3f - 2f * f);
            return math.lerp(
                math.lerp(math.lerp(Hash13(i), Hash13(i + new float3(1, 0, 0)), f.x), math.lerp(Hash13(i + new float3(0, 1, 0)), Hash13(i + new float3(1, 1, 0)), f.x), f.y),
                math.lerp(math.lerp(Hash13(i + new float3(0, 0, 1)), Hash13(i + new float3(1, 0, 1)), f.x), math.lerp(Hash13(i + new float3(0, 1, 1)), Hash13(i + new float3(1, 1, 1)), f.x), f.y), f.z);
        }

        public static float Fbm(float3 p)
        {
            float s = 0, a = 0.5f;
            for (int i = 0; i < 4; i++) { s += a * VNoise(p); p = p * 2.03f + 7.7f; a *= 0.5f; }
            return s;
        }

        static float SmoothStep(float a, float b, float x) { float t = math.saturate((x - a) / (b - a)); return t * t * (3 - 2 * t); }

        /// <summary>Glass-bead cover (0..1) on a floor of this upness (normal.y), and the orange (0) .. green (1) mix.</summary>
        public static float Glass(float3 fp, float upness, out float greenMix, bool legacy = false, float depth = 0f)
        {
            float gEdge = (VNoise(fp * 0.55f + 3.7f) - 0.5f) * 0.10f + (VNoise(fp * 2.7f + 9.1f) - 0.5f) * 0.06f;
            float gm = SmoothStep(0.62f, 0.72f, Fbm(fp * 0.015f + 19f) + (legacy ? 0 : gEdge)) * math.saturate(upness) * 0.5f;   // _GlassAmount 0.5
            if (!legacy) gm *= math.lerp(0.55f, 1f, VNoise(fp * 1.3f + 1.9f)) * math.lerp(1f, 0.3f, math.saturate(depth / 160f));
            greenMix = legacy ? (VNoise(math.floor(fp / 180f)) >= 0.5f ? 1f : 0f) : SmoothStep(0.3f, 0.7f, VNoise(fp / 150f + 4.2f));
            return gm;
        }

        /// <summary>Dominant surface type at a frame point with surface normal n (mouthFloorY: level-0 floor height).</summary>
        public static Kind At(float3 fp, float3 n, float mouthFloorY, out float strength)
        {
            float depth = mouthFloorY - fp.y;
            float levelF = depth / 75f;
            float upness = math.saturate(n.y), wall = 1 - upness;
            float big = Fbm(fp * 0.02f);
            float dust = math.saturate(1 - depth / 25f) * upness;
            float glaze = SmoothStep(0.45f, 0.65f, Fbm(fp * 0.05f + 3.3f)) * SmoothStep(0.15f, 0.6f, wall) * 0.55f * (1 - dust);
            float gm = Glass(fp, upness, out float green, depth: depth);
            float crack = 1 - math.abs(Fbm(fp * 0.09f + 5f) * 2 - 1);
            float crust = SmoothStep(0.9f, 0.97f, crack) * SmoothStep(1.5f, 3f, levelF) * SmoothStep(0.4f, 0.6f, big);
            strength = 0; var k = Kind.Basalt;
            if (dust > 0.5f) { strength = dust; k = Kind.Dust; }
            if (glaze > 0.3f && glaze > strength) { strength = glaze; k = Kind.Glaze; }
            if (gm > 0.15f && gm * 2f > strength) { strength = gm * 2f; k = green > 0.5f ? Kind.GreenGlass : Kind.OrangeGlass; }
            if (crust > 0.3f && crust > strength) { strength = crust; k = Kind.Crust; }
            return k;
        }
    }
}
