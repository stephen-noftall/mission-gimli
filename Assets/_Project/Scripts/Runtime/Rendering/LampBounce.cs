using System.Collections.Generic;
using UnityEngine;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>
    /// One-bounce indirect light from lamps. Each lamp beam is sampled with a few rays; the light the lit rock
    /// reflects (Lambertian: I = albedo * I_lamp * omega_ray * cos / pi per ray) is gathered into ONE soft source per
    /// lamp group (rover / walker): placed in the air between the lamps and the lit rock, smoothed over ~0.3 s, and
    /// evaluated in the rock shaders as a diffuse-only lift with a large softening radius (E = I / (d^2 + r_s^2)).
    /// So the opposite wall brightens softly; there is no visible point, hot spot, specular or glint, and nothing
    /// jumps when a lamp bobs. Costs no URP lights (the round-7 version placed up to ~30 pixel point lights at the
    /// ray hits, which jumped with the helmet lamps: the "pulsing lights in the walls").
    /// Shader globals: _MBA_BouncePos[4] (xyz world, w = r_s^2), _MBA_BounceCol[4] (rgb = I x exposure), _MBA_BounceCount.
    /// Only CaveRock / LunarRegolith receive it (the rover and suits keep the visor fill).
    /// </summary>
    public class LampBounce : MonoBehaviour
    {
        public Light[] lamps;
        public float albedo = 0.09f;        // fresh basalt / regolith
        public int raysPerLamp = 5;
        public float range = 35f;
        public float smoothTime = 0.3f;

        /// <summary>Perf bench only: the round-7 point-light bounce (one URP point light per ray hit).</summary>
        public static bool LegacyPointLights;
        public const int MaxSources = 4;
        public static readonly List<LampBounce> Active = new();

        /// <summary>Smoothed soft source: world position, intensity (cd, physical), softening radius (m), colour.</summary>
        public Vector3 Position { get; private set; }
        public float Intensity { get; private set; }
        public float Softening { get; private set; } = 3f;
        public Color Tint { get; private set; } = Color.white;

        Vector3 tPos; float tInt, tSoft = 3f; bool hasState;
        readonly List<Light> pool = new();
        int frame;

        static readonly Vector2[] Pattern = { new(0, 0), new(0.55f, 0), new(-0.55f, 0), new(0, 0.55f), new(0, -0.55f), new(0.4f, 0.4f), new(-0.4f, -0.4f) };

        void OnEnable() { if (!Active.Contains(this)) Active.Add(this); WorldOrigin.Shifted += Shift; }
        void OnDisable() { Active.Remove(this); WorldOrigin.Shifted -= Shift; foreach (var l in pool) if (l) l.enabled = false; }
        void OnDestroy() { foreach (var l in pool) if (l) Util.Kill(l.gameObject); }

        void LateUpdate()
        {
            if ((frame++ & 1) == 0) Refresh(false);
            Smooth(Time.deltaTime);
        }

        /// <summary>Re-sample the beams. snap: jump straight to the new value (tests, teleports).</summary>
        public void Refresh() => Refresh(true);

        public void Refresh(bool snap)
        {
            if (LegacyPointLights) { RefreshLegacy(); tInt = 0; if (snap) Smooth(-1); return; }
            for (int i = 0; i < pool.Count; i++) if (pool[i]) pool[i].enabled = false;
            Vector3 sumP = Vector3.zero; float sumI = 0, sumD = 0; Color sumC = Color.black;
            if (lamps != null)
            {
                foreach (var lamp in lamps)
                {
                    if (lamp == null || !lamp.enabled || !lamp.gameObject.activeInHierarchy) continue;
                    var pl = lamp.GetComponent<PhysicalLight>();
                    if (pl == null) continue;
                    float half = lamp.spotAngle * 0.5f * Mathf.Deg2Rad;
                    float omegaCone = 2f * Mathf.PI * (1f - Mathf.Cos(half));
                    int n = Mathf.Min(raysPerLamp, Pattern.Length);
                    float omegaRay = omegaCone / n;
                    var t = lamp.transform;
                    for (int i = 0; i < n; i++)
                    {
                        var p = Pattern[i] * Mathf.Tan(half);
                        var dir = (t.forward + t.right * p.x + t.up * p.y).normalized;
                        if (!Physics.Raycast(t.position, dir, out var hit, Mathf.Min(lamp.range, range * 2f), ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;
                        float cosIn = Mathf.Max(0.05f, Vector3.Dot(hit.normal, -dir));
                        float I = albedo * pl.physicalIntensity * omegaRay * cosIn / Mathf.PI;
                        // the lit patch lights the space in front of it: put the source ~45% of the way back to the lamp
                        var src = hit.point + (t.position - hit.point) * 0.45f + hit.normal * 0.5f;
                        sumP += src * I; sumI += I; sumD += hit.distance * I; sumC += lamp.color * I;
                    }
                }
            }
            if (sumI > 0)
            {
                tPos = sumP / sumI; tInt = sumI;
                // wide source: never closer than ~3 m in effect, wider for far-reaching beams
                tSoft = Mathf.Max(3f, 0.6f * sumD / sumI);
                Tint = sumC / sumI;
                if (!hasState) { Position = tPos; Softening = tSoft; hasState = true; }
            }
            else tInt = 0;
            if (snap) Smooth(-1);
        }

        void Smooth(float dt)
        {
            float k = dt < 0 ? 1f : 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, smoothTime));
            Position = Vector3.Lerp(Position, tPos, k);
            Intensity = Mathf.Lerp(Intensity, tInt, k);
            Softening = Mathf.Lerp(Softening, tSoft, k);
        }

        /// <summary>World origin shifted: move the smoothed state with the world.</summary>
        public void Shift(Vector3 delta) { Position += delta; tPos += delta; }

        /// <summary>Irradiance (lux) at a point from all soft bounce sources (same maths as the shaders).</summary>
        public static float Irradiance(Vector3 p, Vector3 n)
        {
            float e = 0;
            int c = 0;
            foreach (var b in Active)
            {
                if (b == null || b.Intensity <= 0 || c++ >= MaxSources) continue;
                e += Eval(b.Position, b.Intensity, b.Softening, b.range, p, n);
            }
            return e;
        }

        public static float Eval(Vector3 src, float I, float soft, float range, Vector3 p, Vector3 n, float wrapK = 0.5f)
        {
            var d = src - p; float d2 = d.sqrMagnitude;
            var L = d / Mathf.Sqrt(Mathf.Max(d2, 1e-4f));
            float wrap = Mathf.Clamp01(Vector3.Dot(n, L) * (1f - wrapK) + wrapK);
            float fade = range > 0 ? Mathf.Clamp01(1f - d2 / (range * range)) : 1f; fade *= fade;
            return I * wrap * wrap * fade / (d2 + soft * soft);
        }

        static readonly Vector4[] posBuf = new Vector4[MaxSources], colBuf = new Vector4[MaxSources];

        /// <summary>Push the soft sources to the rock shaders (called by ExposureController with the exposure).</summary>
        public static void PushGlobals(float exposure)
        {
            int c = 0;
            foreach (var b in Active)
            {
                if (b == null || b.Intensity <= 1e-4f || c >= MaxSources) continue;
                posBuf[c] = new Vector4(b.Position.x, b.Position.y, b.Position.z, b.Softening * b.Softening);
                var col = b.Tint.linear * (b.Intensity * exposure);
                colBuf[c] = new Vector4(col.r, col.g, col.b, b.range);
                c++;
            }
            for (int i = c; i < MaxSources; i++) { posBuf[i] = Vector4.zero; colBuf[i] = Vector4.zero; }
            Shader.SetGlobalVectorArray("_MBA_BouncePos", posBuf);
            Shader.SetGlobalVectorArray("_MBA_BounceCol", colBuf);
            Shader.SetGlobalFloat("_MBA_BounceCount", c);
        }

        // ---------------------------------------------------------------- round-7 path (perf comparison only)
        void RefreshLegacy()
        {
            int used = 0;
            if (lamps != null)
            {
                foreach (var lamp in lamps)
                {
                    if (lamp == null || !lamp.enabled || !lamp.gameObject.activeInHierarchy) continue;
                    var pl = lamp.GetComponent<PhysicalLight>();
                    if (pl == null) continue;
                    float half = lamp.spotAngle * 0.5f * Mathf.Deg2Rad;
                    float omegaRay = 2f * Mathf.PI * (1f - Mathf.Cos(half)) / Mathf.Min(raysPerLamp, Pattern.Length);
                    var t = lamp.transform;
                    for (int i = 0; i < Mathf.Min(raysPerLamp, Pattern.Length); i++)
                    {
                        var p = Pattern[i] * Mathf.Tan(half);
                        var dir = (t.forward + t.right * p.x + t.up * p.y).normalized;
                        if (!Physics.Raycast(t.position, dir, out var hit, lamp.range, ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;
                        var vpl = Get(used++);
                        vpl.transform.position = hit.point + hit.normal * Mathf.Clamp(hit.distance * 0.35f, 2.5f, 8f) - dir * 1.0f;
                        float cosIn = Mathf.Max(0.05f, Vector3.Dot(hit.normal, -dir));
                        vpl.GetComponent<PhysicalLight>().physicalIntensity = albedo * pl.physicalIntensity * omegaRay * cosIn / Mathf.PI;
                        vpl.color = lamp.color;
                        vpl.enabled = true;
                    }
                }
            }
            for (int i = used; i < pool.Count; i++) pool[i].enabled = false;
        }

        Light Get(int i)
        {
            while (pool.Count <= i)
            {
                var go = new GameObject("BounceLight");
                go.layer = 2;
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = range;
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.ForcePixel;
                go.AddComponent<PhysicalLight>();
                go.AddComponent<WorldOriginIgnore>();
                pool.Add(l);
            }
            return pool[i];
        }
    }
}
