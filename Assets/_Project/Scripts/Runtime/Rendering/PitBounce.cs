using Unity.Mathematics;
using UnityEngine;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>
    /// Sunlight bounced inside the pit: the sunlit part of the shaft wall (and the floor, when the Sun is high enough
    /// to reach it) relights the shadowed walls and floor. One-bounce radiosity estimate for a cylindrical shaft
    /// (radius R ~26 m, depth D ~40 m), re-evaluated every 0.5 s from the Sun direction:
    ///   lit depth on the far wall   d = min(D, 2R tan(elev));   lit wall area ~ pi R d (half the circumference)
    ///   wall irradiance             E_w = E_sun cos(elev) * 2/pi (average over the lit half)
    ///   reflected flux              Phi = albedo * (E_w * A_wall + E_sun sin(elev) * A_floorLit)
    ///   shadowed-surface irradiance E_b = Phi * (share that stays inside the pit) / A_interior
    /// With regolith albedo 0.12 this gives ~1/40 of a Sun-facing surface's brightness with a high Sun and ~1/600 with
    /// a sliver lit (the round-8 brief: ~1/30 .. ~1/1000). It drives up to two soft, shadowless, diffuse-only sources (MBABounce.hlsl)
    /// placed at the lit wall / lit floor patch, masked to the pit cylinder below the rim: no leak into the tubes.
    /// </summary>
    public static class PitBounce
    {
        public const float Albedo = 0.12f;
        static bool configured;
        static float2 centre, axis = new float2(1, 0); static float radius, semiA = 1, semiB = 1, rimY, floorY;
        static float nextUpdate = -1f;

        /// <summary>Frame-space sources + current estimate (tests and metering).</summary>
        public static Vector3 SrcWall, SrcFloor;      // frame
        public static float IWall, IFloor, Soft = 18f;
        public static float LitFraction { get; private set; }
        public static float BounceLux { get; private set; }
        public static float SurfaceLux { get; private set; }
        public static bool Enabled = true;

        public static void Configure(float2 pitCentre, float a, float b, float rim, float pitFloor, float2? majorAxis = null)
        {
            centre = pitCentre; radius = math.sqrt(a * b); rimY = rim; floorY = pitFloor;
            semiA = a; semiB = b; axis = majorAxis ?? new float2(1, 0);
            configured = true; nextUpdate = -1f;
        }

        public static void Recompute(Sky.SkyController sky)
        {
            nextUpdate = Time.unscaledTime + 0.5f;
            IWall = IFloor = 0; LitFraction = 0; BounceLux = 0; SurfaceLux = 0;
            if (!configured || sky == null || !Enabled) return;
            var s = sky.SunDir;
            float sinE = s.y, E = sky.SunLux;
            if (sinE <= 0.002f || E <= 1f) return;
            float R = radius, D = Mathf.Max(5f, rimY - floorY);
            SurfaceLux = E * sinE;
            Model(sinE, E, R, D, out float phiWall, out float phiFloor, out float dLit, out float bl, out float lf);
            BounceLux = bl; LitFraction = lf;
            // sources: lit wall centroid (far side from the Sun) and lit floor patch, both pulled into the pit air
            var sh = new Vector2(s.x, s.z); sh = sh.sqrMagnitude > 1e-6f ? sh.normalized : Vector2.up;
            var wallP = new Vector3(centre.x - sh.x * R * 0.55f, rimY - dLit * 0.5f, centre.y - sh.y * R * 0.55f);
            var floorP = new Vector3(centre.x - sh.x * R * 0.3f, floorY + 8f, centre.y - sh.y * R * 0.3f);
            SrcWall = wallP; SrcFloor = floorP;
            // intensity so that the irradiance at the pit's centre (mid-depth) equals the estimate, split by flux
            var mid = new Vector3(centre.x, floorY + D * 0.35f, centre.y);
            float total = phiWall + phiFloor;
            if (total <= 0) return;
            float wW = phiWall / total, wF = phiFloor / total;
            IWall = BounceLux * wW * ((wallP - mid).sqrMagnitude + Soft * Soft);
            IFloor = BounceLux * wF * ((floorP - mid).sqrMagnitude + Soft * Soft);
        }

        /// <summary>The one-bounce estimate for a shaft of radius R, depth D (see the class summary).</summary>
        public static void Model(float sinE, float E, float R, float D, out float phiWall, out float phiFloor, out float dLit, out float bounceLux, out float litFraction)
        {
            float elev = Mathf.Asin(Mathf.Clamp01(sinE)), cosE = Mathf.Cos(elev), tanE = Mathf.Tan(elev);
            dLit = Mathf.Min(D, 2f * R * tanE);
            float aWall = Mathf.PI * R * dLit;                                   // lit half-circumference x lit depth
            float eWall = E * cosE * 2f / Mathf.PI;
            // the floor: a lit patch once the Sun clears the far rim
            float floorLitFrac = Mathf.Clamp01((2f * R * tanE - D) / (2f * R * tanE + 1e-3f));
            float aFloor = Mathf.PI * R * R;
            float aFloorLit = aFloor * floorLitFrac;
            phiWall = Albedo * eWall * aWall; phiFloor = Albedo * E * sinE * aFloorLit;
            float aInterior = aFloor + 2f * Mathf.PI * R * D;
            float keep = aInterior / (aInterior + aFloor);                       // the rest escapes through the opening
            bounceLux = (phiWall + phiFloor) * keep / aInterior;
            litFraction = (aWall + aFloorLit) / aInterior;
        }

        /// <summary>
        /// Shadowed-surface brightness / a sunlit surface facing the Sun, at a Sun elevation (pit as configured).
        /// (Against the plain itself the low-Sun ratio is ~1/30: at grazing light the plain is dim too.)
        /// </summary>
        public static float RatioAt(float elevDeg)
        {
            float sinE = Mathf.Sin(elevDeg * Mathf.Deg2Rad);
            Model(sinE, 1000f, radius, Mathf.Max(5f, rimY - floorY), out _, out _, out _, out float b, out _);
            return b / 1000f;
        }

        public static void Tick(Sky.SkyController sky) { if (Time.unscaledTime >= nextUpdate || nextUpdate < 0) Recompute(sky); }

        static readonly Vector4[] pos = new Vector4[2], col = new Vector4[2];

        public static void PushGlobals(float exposure)
        {
            if (!configured || (IWall <= 0 && IFloor <= 0)) { Shader.SetGlobalVector("_MBA_PitMask", Vector4.zero); return; }
            var o = WorldOrigin.Origin;
            Vector3 W(Vector3 f) => new Vector3((float)(f.x - o.x), (float)(f.y - o.y), (float)(f.z - o.z));
            var w = W(SrcWall); var fl = W(SrcFloor);
            pos[0] = new Vector4(w.x, w.y, w.z, Soft * Soft); pos[1] = new Vector4(fl.x, fl.y, fl.z, Soft * Soft);
            var tint = new Color(1f, 0.96f, 0.9f);   // regolith-reddened sunlight
            col[0] = new Vector4(tint.r, tint.g, tint.b, 0) * (IWall * exposure);
            col[1] = new Vector4(tint.r, tint.g, tint.b, 0) * (IFloor * exposure);
            Shader.SetGlobalVectorArray("_MBA_PitBouncePos", pos);
            Shader.SetGlobalVectorArray("_MBA_PitBounceCol", col);
            var c = W(new Vector3(centre.x, rimY, centre.y));
            Shader.SetGlobalVector("_MBA_PitMask", new Vector4(c.x, c.z, radius, c.y));
            Shader.SetGlobalVector("_MBA_PitShape", new Vector4(axis.x, axis.y, semiA, semiB));
        }

        /// <summary>Irradiance (lux) at a world point / normal from the pit bounce (same maths as the shader, incl. the mask).</summary>
        public static float Irradiance(Vector3 world, Vector3 n)
        {
            if (!configured || (IWall <= 0 && IFloor <= 0)) return 0;
            var f = WorldOrigin.WorldToFrame(world);
            float2 d = (float2)f.xz - centre;
            float u = math.dot(d, axis), v = d.x * axis.y - d.y * axis.x;
            float re = math.sqrt(u * u / (semiA * semiA) + v * v / (semiB * semiB));
            float inside = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.97f, 1.05f, re));
            float below = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(rimY + 2f, rimY - 3f, (float)f.y));
            float m = inside * below;
            if (m <= 0) return 0;
            var o = WorldOrigin.Origin;
            Vector3 W(Vector3 v) => new Vector3((float)(v.x - o.x), (float)(v.y - o.y), (float)(v.z - o.z));
            float floorK = (1f - Mathf.Clamp01(n.y * 1.25f)) * Mathf.Clamp01(1f + n.y * 4f);
            return m * floorK * (LampBounce.Eval(W(SrcWall), IWall, Soft, 0, world, n, 0.2f) + LampBounce.Eval(W(SrcFloor), IFloor, Soft, 0, world, n, 0.2f));
        }
    }
}
