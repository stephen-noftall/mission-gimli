using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>
    /// Graphics presets for exhibit hardware (F4 cycles Low / Medium / High; remembered between runs).
    ///  Low    - integrated GPUs / small kiosks: 1 shadow cascade, 150 m shadows, 1024 maps, no MSAA,
    ///           3D rendered at 900p, coarser terrain LOD, shorter tunnel streaming, minimal shader detail.
    ///  Medium - mid-range GPU (default for the exhibit). 3D at 1080p.
    ///  High   - desktop GPU: everything on. 3D at 1080p.
    /// The 3D view renders at the preset's height (never above the screen) and AMD FSR 1 upscales it to the
    /// monitor; the IMGUI HUD draws afterwards at native resolution. Frame rate is uncapped (vSync off);
    /// F6 toggles vSync (cap at the monitor refresh, for shows); remembered between runs.
    /// The URP asset is cloned at runtime so the project asset on disk is never modified.
    /// </summary>
    public class QualityPresets : MonoBehaviour
    {
        public enum Level { Low, Medium, High }
        public Level level = Level.Medium;
        public Terrain.TerrainStreamer terrain;
        public Cave.CaveStreamer tunnels, farTunnels;

        UniversalRenderPipelineAsset asset;
        const string PrefKey = "MBA.Quality", CapKey = "MBA.FrameCap";
        int appliedHeight;

        /// <summary>vSync on (frame rate capped at the monitor refresh). Off by default.</summary>
        public static bool FrameCap { get; private set; }
        /// <summary>Effective 3D render resolution (after the render scale), for the F3 readout.</summary>
        public static Vector2Int RenderResolution { get; private set; }
        public static bool Upscaling { get; private set; }
        /// <summary>Perf bench: render the 3D view at native resolution (no upscale).</summary>
        public static bool ForceNative;

        /// <summary>3D render height per preset (the screen is upscaled from it with FSR).</summary>
        public static int TargetHeight(Level l) => l == Level.Low ? 900 : 1080;
        /// <summary>URP render scale that renders the 3D view at the preset's height on a screen this tall.</summary>
        public static float RenderScaleFor(Level l, int screenHeight) =>
            screenHeight <= 0 ? 1f : Mathf.Clamp(TargetHeight(l) / (float)screenHeight, 0.1f, 1f);

        public static void SetFrameCap(bool on)
        {
            FrameCap = on;
            PlayerPrefs.SetInt(CapKey, on ? 1 : 0);
            QualitySettings.vSyncCount = on ? 1 : 0;
            Application.targetFrameRate = -1;   // uncapped unless vSync is on
            Debug.Log($"[Quality] frame cap {(on ? "ON (vSync)" : "OFF (uncapped)")}");
        }

        public static string Describe(Level l) => l == Level.Low ? "LOW (integrated GPU)" : l == Level.Medium ? "MEDIUM" : "HIGH";

        void Start()
        {
            level = (Level)PlayerPrefs.GetInt(PrefKey, (int)Level.Medium);
            SetFrameCap(PlayerPrefs.GetInt(CapKey, 0) == 1);
            Apply();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F4))
            {
                level = (Level)(((int)level + 1) % 3);
                PlayerPrefs.SetInt(PrefKey, (int)level);
                Apply();
            }
            if (Input.GetKeyDown(KeyCode.F6)) SetFrameCap(!FrameCap);
            if (Screen.height != appliedHeight) Apply();   // window resized / fullscreen toggled: keep 1080p 3D
        }

        /// <summary>Shadow / MSAA settings of a preset on a URP asset (tests render "High" on a clone).</summary>
        public static void Configure(UniversalRenderPipelineAsset a, Level l) => Configure(a, l, Screen.height);

        public static void Configure(UniversalRenderPipelineAsset a, Level l, int screenHeight)
        {
            switch (l)
            {
                case Level.Low:
                    a.shadowDistance = 150f; a.shadowCascadeCount = 1;
                    a.mainLightShadowmapResolution = 1024; a.additionalLightsShadowmapResolution = 512;
                    a.msaaSampleCount = 1;
                    break;
                case Level.Medium:
                    a.shadowDistance = 200f; a.shadowCascadeCount = 4; a.cascade4Split = new Vector3(0.05f, 0.16f, 0.42f);
                    a.mainLightShadowmapResolution = 4096; a.additionalLightsShadowmapResolution = 1024;
                    a.msaaSampleCount = 2;
                    break;
                default:
                    // cascades end at 9.6 / 36 / 120 / 240 m: the crane mast's shadow on the far pit rim (~60-120 m away) stays in
                    // cascade 3 (was the coarse 4th), near shadows keep the same first two cascades
                    a.shadowDistance = 240f; a.shadowCascadeCount = 4; a.cascade4Split = new Vector3(0.04f, 0.15f, 0.5f);
                    a.mainLightShadowmapResolution = 8192; a.additionalLightsShadowmapResolution = 2048;
                    a.msaaSampleCount = 4;
                    break;
            }
            a.renderScale = ForceNative ? 1f : RenderScaleFor(l, screenHeight);
            // FSR 1 (edge-adaptive spatial upscale + RCAS sharpening); URP falls back to bilinear where unsupported
            a.upscalingFilter = UpscalingFilterSelection.FSR;
            a.fsrOverrideSharpness = true;
            a.fsrSharpness = 0.85f;
        }

        public void Apply()
        {
            if (asset == null)
            {
                var src = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
                if (src == null) return;
                asset = Application.isPlaying ? Instantiate(src) : src;
                if (Application.isPlaying)
                {
                    GraphicsSettings.defaultRenderPipeline = asset;
                    QualitySettings.renderPipeline = asset;
                }
            }
            Configure(asset, level, Screen.height);
            appliedHeight = Screen.height;
            Upscaling = asset.renderScale < 0.999f;
            RenderResolution = new Vector2Int(Mathf.RoundToInt(Screen.width * asset.renderScale), Mathf.RoundToInt(Screen.height * asset.renderScale));
            Debug.Log($"[Quality] {level}: screen {Screen.width}x{Screen.height}, 3D render {RenderResolution.x}x{RenderResolution.y} " +
                      $"(scale {asset.renderScale:0.###}{(Upscaling ? ", FSR upscale" : ", native")}), MSAA {asset.msaaSampleCount}x");
            switch (level)
            {
                case Level.Low:
                    Shader.SetGlobalFloat("_MBA_Detail", 0.25f);
                    if (terrain) terrain.lodFactor = 1.15f;
                    if (tunnels) tunnels.radius = 150f;
                    if (farTunnels) farTunnels.enabled = false;   // integrated GPUs: no far ring
                    break;
                case Level.Medium:
                    Shader.SetGlobalFloat("_MBA_Detail", 0.7f);
                    if (terrain) terrain.lodFactor = 1.4f;
                    if (tunnels) tunnels.radius = 200f;
                    if (farTunnels) { farTunnels.enabled = true; farTunnels.radius = 400f; }
                    break;
                default:
                    Shader.SetGlobalFloat("_MBA_Detail", 1f);
                    if (terrain) terrain.lodFactor = 1.6f;
                    if (tunnels) tunnels.radius = 240f;
                    if (farTunnels) { farTunnels.enabled = true; farTunnels.radius = 480f; }
                    break;
            }
            if (tunnels) tunnels.PushShaderGlobals();
        }
    }

    /// <summary>
    /// Frame-time readout (F3): FPS, average and worst (1%) CPU frame time, and GPU time when the platform
    /// reports it (FrameTimingManager). Use it to size the exhibit hardware.
    /// </summary>
    public static class PerfStats
    {
        static readonly float[] history = new float[240];
        static int idx, count;
        static readonly FrameTiming[] timings = new FrameTiming[1];
        public static double GpuMs { get; private set; }

        public static void Sample(float dt)
        {
            history[idx] = dt * 1000f; idx = (idx + 1) % history.Length; count = Mathf.Min(count + 1, history.Length);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0) GpuMs = timings[0].gpuFrameTime;
        }

        public static void Stats(out float avgMs, out float worstMs)
        {
            avgMs = 0; worstMs = 0;
            if (count == 0) return;
            var tmp = new float[count];
            for (int i = 0; i < count; i++) { tmp[i] = history[i]; avgMs += history[i]; }
            avgMs /= count;
            System.Array.Sort(tmp);
            worstMs = tmp[Mathf.Min(count - 1, (int)(count * 0.99f))];
        }
    }
}
