using Unity.Mathematics;
using UnityEngine;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Sky
{
    /// <summary>
    /// Places the Sun, Earth and stars for the current mission time and drives the sunlight.
    /// Sky objects live on a camera-following root at ~45 km (beyond all terrain) with their true
    /// angular sizes, so terrain correctly occludes them on the horizon.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class SkyController : MonoBehaviour
    {
        public Camera cam;
        public Light sun;
        public Material sunMaterial, earthMaterial, starMaterial;
        public int starCount = 6000;
        public float skyDistance = 45000f;

        public Ephemeris.SkyState State { get; private set; }
        public Vector3 SunDir { get; private set; }
        public Vector3 EarthDir { get; private set; }
        /// <summary>Illuminance at the site, lux.</summary>
        public float SunLux { get; private set; }
        public float EarthshineLux { get; private set; }
        public float SunElevationDeg => Mathf.Asin(Mathf.Clamp(SunDir.y, -1, 1)) * Mathf.Rad2Deg;

        Transform root, sunT, earthT, starsT, glareT;
        public const float GlareMaxDeg = 12f;
        public const float FilterRadiusDeg = 1.0f;
        float glareVis = 1f, sunFilter;
        /// <summary>Visor local dimming over the Sun, 0..1 (speculative: electrochromic patch, ~ND 4.5).</summary>
        public float SunFilter => sunFilter;
        public bool SunFilterOn => sunFilter > 0.5f;
        PhysicalLight sunPhys;
        double lastEphemerisTime = double.NaN;
        MaterialPropertyBlock earthBlock;

        void Start() => Build();

        public void Build()
        {
            if (root != null) return;
            root = new GameObject("SkyRoot").transform;
            root.gameObject.AddComponent<WorldOriginIgnore>();
            sunPhys = sun.GetComponent<PhysicalLight>() ?? sun.gameObject.AddComponent<PhysicalLight>();

            sunT = GameObject.CreatePrimitive(PrimitiveType.Quad).transform;
            sunT.name = "SunDisc";
            Util.Kill(sunT.GetComponent<Collider>());
            sunT.SetParent(root, false);
            Setup(sunT.GetComponent<MeshRenderer>(), sunMaterial);

            glareT = GameObject.CreatePrimitive(PrimitiveType.Quad).transform;
            glareT.name = "SunGlare";
            Util.Kill(glareT.GetComponent<Collider>());
            glareT.SetParent(root, false);
            Setup(glareT.GetComponent<MeshRenderer>(), new Material(Shader.Find("MoonBaseAlpha/SkySunGlare")) { name = "SunGlare" });

            earthT = new GameObject("Earth").transform;
            earthT.SetParent(root, false);
            earthT.gameObject.AddComponent<MeshFilter>().sharedMesh = SphereMesh(64, 32);
            Setup(earthT.gameObject.AddComponent<MeshRenderer>(), earthMaterial);

            starsT = new GameObject("Stars").transform;
            starsT.SetParent(root, false);
            starsT.gameObject.AddComponent<MeshFilter>().sharedMesh = StarMesh(starCount, skyDistance * 1.02f);
            Setup(starsT.gameObject.AddComponent<MeshRenderer>(), starMaterial);
            earthBlock = new MaterialPropertyBlock();
            UpdateSky(true);
        }

        static void Setup(MeshRenderer r, Material m)
        {
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        /// <summary>0..1 how much of the sky the camera can see (set by the game: 0 under rock).</summary>
        public System.Func<Vector3, float> skyVisibility;

        void LateUpdate()
        {
            UpdateSky(false);
            UpdateVisibility();
        }

        public bool SkyShown => root != null && root.gameObject.activeSelf;

        /// <summary>Hide the Sun, Earth and stars when the camera is under rock.</summary>
        public void UpdateVisibility()
        {
            if (root == null || cam == null) return;
            bool visible = skyVisibility == null || skyVisibility(cam.transform.position) > 0.02f;
            if (root.gameObject.activeSelf != visible) root.gameObject.SetActive(visible);
        }

        public void UpdateSky(bool force)
        {
            if (root == null) return;
            var utc = SimClock.NowUtc;
            double t = (utc - SimClock.MissionStartUtc).TotalSeconds;
            if (force || double.IsNaN(lastEphemerisTime) || math.abs(t - lastEphemerisTime) > 0.25)
            {
                State = Ephemeris.Compute(utc, TerrainFrame.SiteLatDeg, TerrainFrame.SiteLonDeg, TerrainFrame.R0 / 1000.0);
                lastEphemerisTime = t;
            }
            var s = State;
            SunDir = Ephemeris.EnuToUnity(s.sunEnu).normalized;
            EarthDir = Ephemeris.EnuToUnity(s.earthEnu).normalized;
            double au = s.sunDistanceKm / 149597870.7;
            SunLux = (float)(Lunar.SolarIlluminance / (au * au) * s.sunVisibleFraction);
            // full-Earth illuminance ~ E_sun * p * (R_e / d)^2 * phase law
            double re = Lunar.EarthRadiusKm / s.earthDistanceKm;
            EarthshineLux = (float)(Lunar.SolarIlluminance / (au * au) * Lunar.EarthGeometricAlbedo * re * re * Ephemeris.LambertPhase(s.earthPhaseAngle));

            // sunlight
            sun.transform.rotation = Quaternion.LookRotation(-SunDir, Vector3.up);
            sunPhys.physicalIntensity = SunLux;
            sun.enabled = SunDir.y > -0.02f && SunLux > 1f;

            // place sky objects around the camera
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            root.position = cam.transform.position;
            float sunSize = 2f * skyDistance * Mathf.Tan((float)s.sunAngularRadiusDeg * Mathf.Deg2Rad);
            sunT.position = root.position + SunDir * skyDistance * 1.01f;
            sunT.rotation = Quaternion.LookRotation(SunDir, Vector3.up);
            sunT.localScale = Vector3.one * sunSize * 1.02f;
            glareT.position = root.position + SunDir * skyDistance * 1.005f;
            glareT.rotation = sunT.rotation;
            glareT.localScale = Vector3.one * 2f * skyDistance * 1.005f * Mathf.Tan(GlareMaxDeg * Mathf.Deg2Rad);
            UpdateGlare(s);

            float earthSize = 2f * skyDistance * Mathf.Tan((float)s.earthAngularRadiusDeg * Mathf.Deg2Rad);
            earthT.position = root.position + EarthDir * skyDistance;
            earthT.localScale = Vector3.one * earthSize;
            earthT.rotation = Quaternion.identity;

            // world (Unity) -> ENU -> Earth-fixed
            var m = s.earthFixedFromEnu;
            var w2e = new Matrix4x4();
            // Unity (x,y,z) = ENU (x, z, y): column j of (M * P) where P swaps y/z
            w2e.SetRow(0, new Vector4((float)m.c0.x, (float)m.c2.x, (float)m.c1.x, 0));
            w2e.SetRow(1, new Vector4((float)m.c0.y, (float)m.c2.y, (float)m.c1.y, 0));
            w2e.SetRow(2, new Vector4((float)m.c0.z, (float)m.c2.z, (float)m.c1.z, 0));
            w2e.SetRow(3, new Vector4(0, 0, 0, 1));
            earthBlock.SetMatrix("_EarthFixedFromWorld", w2e);
            earthBlock.SetVector("_SunDirWS", SunDir);
            earthBlock.SetFloat("_SunLux", SunLux);
            earthT.GetComponent<MeshRenderer>().SetPropertyBlock(earthBlock);

            // sun disc radiance in our units (albedo-1 Lambertian scale): E / solid angle
            double omega = 2 * math.PI * (1 - math.cos(s.sunAngularRadiusDeg * math.PI / 180));
            Shader.SetGlobalFloat("_MBA_SunRadiance", (float)(Lunar.SolarIlluminance / (au * au) / omega));
            Shader.SetGlobalVector("_MBA_SunDirWS", SunDir);
            float pix = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, cam.pixelHeight);
            Shader.SetGlobalFloat("_MBA_PixelSolidAngle", pix * pix);
        }

        /// <summary>Glare visibility (camera -> Sun ray, eclipse, horizon) and the visor's auto local dimming.</summary>
        void UpdateGlare(Ephemeris.SkyState s)
        {
            var cp = cam.transform.position;
            bool blocked = Physics.Raycast(cp, SunDir, 5000f, ~(1 << 2), QueryTriggerInteraction.Ignore);
            float vis = blocked || SunDir.y < -0.01f ? 0f : (float)s.sunVisibleFraction;
            float dt = Application.isPlaying ? Time.unscaledDeltaTime : 1f;
            glareVis = Mathf.MoveTowards(glareVis, vis, dt / 0.15f);
            // auto-dimming: the patch forms over the Sun whenever it is in view (0.3 s electrochromic response)
            var vp = cam.WorldToViewportPoint(cp + SunDir * 1000f);
            bool inView = vp.z > 0 && vp.x > -0.05f && vp.x < 1.05f && vp.y > -0.05f && vp.y < 1.05f && vis > 0.01f;
            sunFilter = Mathf.MoveTowards(sunFilter, inView ? 1f : 0f, dt / 0.3f);
            Shader.SetGlobalFloat("_MBA_SunGlareVis", glareVis);
            Shader.SetGlobalFloat("_MBA_SunFilter", sunFilter);
            Shader.SetGlobalFloat("_MBA_GlareMaxDeg", GlareMaxDeg);
        }

        static Mesh SphereMesh(int seg, int rings)
        {
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float th = Mathf.PI * r / rings;
                for (int s = 0; s <= seg; s++)
                {
                    float ph = 2 * Mathf.PI * s / seg;
                    v.Add(new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)) * 0.5f);
                }
            }
            for (int r = 0; r < rings; r++)
            for (int s = 0; s < seg; s++)
            {
                int a = r * (seg + 1) + s, b = a + seg + 1;
                t.Add(a); t.Add(a + 1); t.Add(b); t.Add(b); t.Add(a + 1); t.Add(b + 1);
            }
            var m = new Mesh { name = "EarthSphere" };
            m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals();
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 2);
            return m;
        }

        /// <summary>
        /// Random sky with a realistic magnitude distribution (N(&lt;m) ~ 10^(0.5 m)) down to m = 6.5
        /// and B-V colours. UV0.x carries the star's illuminance in units of 1e-6 lux.
        /// (A real catalogue - e.g. Yale BSC - can replace this later.)
        /// </summary>
        static Mesh StarMesh(int count, float radius)
        {
            var rng = new System.Random(20300614);
            var v = new Vector3[count];
            var c = new Color[count];
            var uv = new Vector2[count];
            var idx = new int[count];
            for (int i = 0; i < count; i++)
            {
                double u = rng.NextDouble() * 2 - 1, ph = rng.NextDouble() * 2 * Mathf.PI;
                double sxy = System.Math.Sqrt(1 - u * u);
                v[i] = new Vector3((float)(sxy * System.Math.Cos(ph)), (float)u, (float)(sxy * System.Math.Sin(ph))) * radius;
                double mag = 6.5 + 2.0 * System.Math.Log10(System.Math.Max(1e-6, rng.NextDouble()));
                mag = System.Math.Max(mag, -1.46);
                float lux = (float)(System.Math.Pow(10, -0.4 * (mag + 14.18)) * 1e6);   // m=0 -> 2.1e-6 lux
                float bv = (float)(rng.NextDouble() * 1.6 - 0.2);
                var col = Color.Lerp(new Color(0.75f, 0.82f, 1f), new Color(1f, 0.78f, 0.55f), Mathf.InverseLerp(-0.2f, 1.4f, bv));
                c[i] = col; uv[i] = new Vector2(lux, 0); idx[i] = i;
            }
            var m = new Mesh { name = "Stars", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.vertices = v; m.colors = c; m.uv = uv;
            m.SetIndices(idx, MeshTopology.Points, 0);
            m.bounds = new Bounds(Vector3.zero, Vector3.one * radius * 2.2f);
            return m;
        }
    }
}
