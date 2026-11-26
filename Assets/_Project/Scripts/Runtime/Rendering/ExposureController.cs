using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using MoonBaseAlpha.Sky;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>
    /// The suit's "smart visor" (Artemis / AxEMU-type helmet, ~2035): one auto-exposure that is readable
    /// everywhere except deep in an unlit tube. Centre-weighted log-average metering (the Earth disc counts
    /// when it is within 5 deg of the view centre, so the night-side full Earth shows detail), a neutral tone
    /// curve with lifted shadows, and a soft fill tied to the adapted light level. Companion features live
    /// in <see cref="SkyController"/> (Sun glare + local dimming patch) and PlayerControl (zoom).
    /// Real: the gold sun visor. Speculative: auto-dimming, local dimming, zoom.
    /// Light units: lux / candela; Unity intensity = physical x exposure (see PhysicalLight).
    /// </summary>
    [DefaultExecutionOrder(300)]
    public class ExposureController : MonoBehaviour
    {
        public Camera cam;
        public SkyController sky;
        public float evCompensation = 0f;        // set by Tuning (exposure EV)
        public float fillScale = 1f;             // set by Tuning (visor fill)
        public int raysX = 11, raysY = 9;
        public float minExposure = 1e-6f, maxExposure = 5f;
        public const float Key = 0.3f, Ceiling = 1.0f, Tau = 0.7f;   // one adaptation time for exposure, fill and darkness
        public const float EarthMeterConeDeg = 5f;

        /// <summary>0 = open sky, 1 = deep in the tube (set by the cave system).</summary>
        public System.Func<Vector3, float> darknessAt;

        public float Exposure { get; private set; } = 4e-5f;
        /// <summary>Adapted (smoothed, tau) metered luminance: the fill follows the eye, not the instantaneous meter.</summary>
        public float MeteredLuminance { get; private set; }
        /// <summary>Adapted (smoothed) tube darkness; the meter uses the instantaneous value.</summary>
        public float Darkness { get; private set; }
        float rawLum, rawDark;
        public bool EarthMetered { get; private set; }
        /// <summary>Raw (this meter pass) log-average / brightest luminance in the view centre cone (visor assist).</summary>
        public float CentreLum { get; private set; }
        /// <summary>Log-average luminance of the frame outside the centre (visor assist compares the centre with it).</summary>
        public float SurroundLum { get; private set; }
        public float CentrePeak { get; private set; }

        float targetLog;
        int frameCount;
        readonly List<float> samples = new();
        readonly List<float> weights = new();
        Volume volume;
        VolumeProfile profile;
        bool curveApplied;

        void Start() => Init();

        public void Init()
        {
            if (cam == null) cam = GetComponent<Camera>();
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.skybox = null;
            targetLog = Mathf.Log(Exposure);
            if (volume == null) volume = Object.FindAnyObjectByType<Volume>();
            ApplyToneCurve();
        }

        public void SnapToTarget() { Init(); Meter(); Force(); Exposure = Mathf.Exp(targetLog); MeteredLuminance = rawLum; Darkness = rawDark; Apply(); }

        /// <summary>
        /// &gt; 0: meter as if the view had this luminance (cd/m^2). The probe camera in the sealed chamber uses it: the
        /// meter's rays only know the tube network, so inside solid rock it would read "black" and open fully.
        /// </summary>
        public float ForcedLuminance;
        void Force()
        {
            if (ForcedLuminance <= 0f) return;
            rawLum = ForcedLuminance;
            targetLog = Mathf.Log(Mathf.Clamp(Key * Mathf.Pow(2f, evCompensation) / ForcedLuminance, minExposure, maxExposure));
        }

        void LateUpdate()
        {
            if (cam == null || sky == null) return;
            if (!curveApplied) ApplyToneCurve();
            if ((frameCount++ & 1) == 0) { Meter(); Force(); }
            float cur = Mathf.Log(Exposure);
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Tau);
            cur += (targetLog - cur) * k;
            Exposure = Mathf.Exp(cur);
            // fill + ambient adapt with the exposure (log-space for the luminance): no instant swing at the pit/tube edge
            MeteredLuminance = MeteredLuminance > 0 && rawLum > 0 ? Mathf.Exp(Mathf.Lerp(Mathf.Log(MeteredLuminance), Mathf.Log(rawLum), k)) : rawLum;
            Darkness = Mathf.Lerp(Darkness, rawDark, k);
            Apply();
        }

        void ApplyToneCurve()
        {
            curveApplied = true;
            Shader.SetGlobalFloat("_MBA_GameVisor", 1f);   // night Earth / stars as the visor's low-light display shows them
            if (volume == null) return;
            if (profile == null) profile = Application.isPlaying ? volume.profile : volume.sharedProfile;
            if (profile.TryGet(out Tonemapping tm)) tm.mode.Override(TonemappingMode.Neutral);
            if (!profile.TryGet(out LiftGammaGain lgg)) { lgg = profile.Add<LiftGammaGain>(true); }
            // keep space black: lift shadows with gamma + fill light, never raise the black level
            lgg.lift.Override(new Vector4(1, 1, 1, 0f));
            lgg.gamma.Override(new Vector4(1, 1, 1, 0.08f));
            if (profile.TryGet(out Bloom bloom))
            {
                bloom.intensity.Override(0f);          // no bloom (exhibit feedback: glare hid the scene)
                bloom.active = false;
            }
        }

        /// <summary>
        /// Visibility floor (lux): a fixed fraction of the light level the view is adapted to, applied as soft
        /// ambient. Shadows and the unlit parts of a tunnel stay readable at every exposure without any surge.
        /// </summary>
        /// <summary>No soft fill (the probe camera has no visor): only real light sources.</summary>
        public bool NoFill;
        public float GameFillLux => NoFill ? 0f : fillScale * (0.3f * MeteredLuminance / Lunar.RegolithAlbedo + 0.4f * (1f - Darkness) * (sky != null ? sky.EarthshineLux : 0f));

        void Meter()
        {
            float albedo = Lunar.RegolithAlbedo;
            Vector3 sunDir = sky.SunDir, earthDir = sky.EarthDir;
            float sunLux = sky.SunLux, earthLux = sky.EarthshineLux;
            rawDark = darknessAt != null ? darknessAt(cam.transform.position) : 0f;
            float Darkness = rawDark;
            Vector3 up = -TerrainFrame.DownAt(cam.transform.position);

            // luminance (albedo x lux) seen along a ray; -1 = open sky (not metered)
            float Lum(Ray ray)
            {
                    float E;
                    if (Physics.Raycast(ray, out var hit, 3000f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                    {
                        var nrm = hit.normal;
                        E = 0f;
                        float ns = Vector3.Dot(nrm, sunDir);
                        if (ns > 0 && sunLux > 0 && !Physics.Raycast(hit.point + nrm * 0.05f, sunDir, 400f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                            E += sunLux * LunarBrdf(nrm, sunDir, -ray.direction);
                        E += (1f - Darkness) * earthLux * Mathf.Max(0, Vector3.Dot(nrm, earthDir));
                        E += (1f - Darkness) * BounceLux() * 0.5f * (1f - Vector3.Dot(nrm, up));
                        foreach (var pl in PhysicalLight.All)
                        {
                            var l = pl.Light;
                            if (l == null || !l.enabled || l.type == LightType.Directional) continue;
                            var toL = l.transform.position - hit.point;
                            float d2 = toL.sqrMagnitude;
                            if (d2 > l.range * l.range) continue;
                            var dir = toL / Mathf.Sqrt(d2);
                            float cone = 1f;
                            if (l.type == LightType.Spot)
                            {
                                float cosAng = Vector3.Dot(-dir, l.transform.forward);
                                float outer = Mathf.Cos(l.spotAngle * 0.5f * Mathf.Deg2Rad), inner = Mathf.Cos(l.innerSpotAngle * 0.5f * Mathf.Deg2Rad);
                                cone = Mathf.Clamp01((cosAng - outer) / Mathf.Max(1e-3f, inner - outer));
                            }
                            E += pl.physicalIntensity * cone * Mathf.Max(0, Vector3.Dot(nrm, dir)) / Mathf.Max(d2, 0.25f);
                        }
                        E += LampBounce.Irradiance(hit.point, nrm) + PitBounce.Irradiance(hit.point, nrm);
                    }
                    else
                    {
                        if (Vector3.Dot(ray.direction, up) > -0.002f) return -1f;   // sky: not metered
                        E = sunLux * LunarBrdf(up, sunDir, -ray.direction) * (1f - Darkness) + (1f - Darkness) * earthLux * Mathf.Max(0, Vector3.Dot(up, earthDir));
                    }
                return albedo * E;
            }

            samples.Clear(); weights.Clear();
            double sAcc = 0; int sN = 0;
            for (int j = 0; j < raysY; j++)
            for (int i = 0; i < raysX; i++)
            {
                var vp = new Vector3((i + 0.5f) / raysX, (j + 0.5f) / raysY, 0);
                float L = Lum(cam.ViewportPointToRay(vp));
                if (L < 0) continue;
                float dx = vp.x - 0.5f, dy = vp.y - 0.5f;
                if (dx * dx + dy * dy > 0.2f * 0.2f) { sAcc += System.Math.Log(Mathf.Max(L, 1e-5f)); sN++; }
                samples.Add(L);
                weights.Add(Mathf.Exp(-(dx * dx + dy * dy) / 0.08f) + 0.15f);
            }

            SurroundLum = sN > 0 ? (float)System.Math.Exp(sAcc / sN) : 0f;
            // view centre (visor low-light assist): 9 rays in the ~12.5 deg cone
            {
                double cAcc = 0; int n = 0; float peak = 0;
                var t = cam.transform;
                for (int k = 0; k < 9; k++)
                {
                    var dir = t.forward;
                    if (k > 0)
                    {
                        float a = k * Mathf.PI / 4f, r = VisorAssist.ConeDeg * 0.45f;
                        dir = Quaternion.AngleAxis(r, t.right * Mathf.Sin(a) + t.up * Mathf.Cos(a)) * t.forward;
                    }
                    float L = Lum(new Ray(t.position, dir));
                    if (L < 0) continue;
                    cAcc += System.Math.Log(Mathf.Max(L, 1e-5f)); n++; peak = Mathf.Max(peak, L);
                }
                CentreLum = n > 0 ? (float)System.Math.Exp(cAcc / n) : 0f;
                CentrePeak = peak;
            }

            // the Earth near the view centre counts (as one big centre sample), so the visor adapts to it
            EarthMetered = false;
            if (sky.SkyShown && Vector3.Angle(cam.transform.forward, earthDir) < EarthMeterConeDeg &&
                !Physics.Raycast(cam.transform.position, earthDir, 5000f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                var st = sky.State;
                // bright-cloud reflectance factor x lit fraction (same albedo x lux units as the ground samples);
                // the Earth shader's highlight shoulder rolls off the brightest cloud tops above this
                float earthL = Mathf.Max(0.45f * sky.SunLux * (float)st.earthLitFraction, 1e-3f);
                float wsum = 0; foreach (var w in weights) wsum += w;
                samples.Add(earthL);
                weights.Add(Mathf.Max(wsum, 1f) * 0.6f);
                EarthMetered = true;
            }
            if (samples.Count == 0) return;   // looking at the sky: hold exposure

            double sw = 0, acc = 0;
            for (int k = 0; k < samples.Count; k++)
            {
                acc += weights[k] * System.Math.Log(Mathf.Max(samples[k], 1e-4f));
                sw += weights[k];
            }
            rawLum = (float)System.Math.Exp(acc / sw);
            float MeteredLuminance = rawLum;
            var sorted = new List<float>(samples);
            sorted.Sort();
            float p95 = sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))];
            float target = Key * Mathf.Pow(2f, evCompensation) / Mathf.Max(MeteredLuminance, 1e-4f);
            if (p95 > 0) target = Mathf.Min(target, Ceiling / p95);
            targetLog = Mathf.Log(Mathf.Clamp(target, minExposure, maxExposure));
        }

        /// <summary>Same lunar-Lambert + opposition model as the regolith shader (mu0-weighted reflectance factor).</summary>
        public static float LunarBrdf(Vector3 n, Vector3 l, Vector3 v)
        {
            float mu0 = Vector3.Dot(n, l);
            if (mu0 <= 0) return 0;
            float mu = Mathf.Max(Vector3.Dot(n, v), 0.03f);
            float g = Mathf.Acos(Mathf.Clamp(Vector3.Dot(l, v), -1, 1));
            float gd = g * Mathf.Rad2Deg;
            float Lw = Mathf.Clamp01(1f - 0.019f * gd + 2.42e-4f * gd * gd - 1.46e-6f * gd * gd * gd);
            float f = 2f * Lw * mu0 / (mu0 + mu) + (1f - Lw) * mu0;
            float B = 1f + 0.9f / (1f + Mathf.Tan(Mathf.Min(g, 3f) * 0.5f) / 0.06f);
            return f * B;
        }

        float BounceLux() => Lunar.RegolithAlbedo * sky.SunLux * Mathf.Max(0, sky.SunDir.y);

        void Apply()
        {
            foreach (var pl in PhysicalLight.All)
                if (pl.Light != null) pl.Light.intensity = pl.physicalIntensity * Exposure;
            Shader.SetGlobalFloat("_MBA_Exposure", Exposure);
            LampBounce.PushGlobals(Exposure);
            PitBounce.Tick(sky);
            PitBounce.PushGlobals(Exposure);
            Shader.SetGlobalFloat("_MBA_GameVisor", 1f);
            Shader.SetGlobalVector("_MBA_Origin", new Vector4((float)WorldOrigin.Origin.x, (float)WorldOrigin.Origin.y, (float)WorldOrigin.Origin.z, 0));

            // Ambient SH: earthshine + sunlit-ground bounce from below (+ the visor's soft fill).
            var sh = new SphericalHarmonicsL2();
            float open = 1f - Darkness;
            float earth = sky.EarthshineLux * Exposure * open;
            float bounce = BounceLux() * Exposure * open;
            if (earth > 0) sh.AddDirectionalLight(sky.EarthDir, new Color(0.85f, 0.9f, 1f), earth);
            if (bounce > 0) sh.AddDirectionalLight(Vector3.down, new Color(1f, 0.97f, 0.93f), bounce);
            var test = new SphericalHarmonicsL2();
            test.AddDirectionalLight(Vector3.down, Color.white, 1f);
            var outCol = new Color[1];
            test.Evaluate(new[] { Vector3.down }, outCol);
            float norm = outCol[0].r > 1e-6f ? 1f / outCol[0].r : 1f;
            sh *= norm;
            float fill = GameFillLux * Exposure;
            if (fill > 0) sh.AddAmbientLight(new Color(fill, fill * 0.98f, fill * 0.96f));
            RenderSettings.ambientProbe = sh;
        }
    }
}
