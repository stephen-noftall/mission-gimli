using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>
    /// Smart visor "look into the dark" (SPECULATIVE electronic visor; the eye's local adaptation is real).
    /// When the view centre (~12.5 deg cone) is more than ~4 stops darker than the frame the visor is adapted to (into
    /// the shadowed pit from the sunlit rim, out of a bright tube mouth into the tube), a soft-edged local gain ramps in
    /// over ~1-2 s, up to +5 EV (x the "visor lift" tuning), and relaxes when you look away. It multiplies the HDR
    /// scene before tone mapping, so it only amplifies light that is there: black stays black, and the gain is capped
    /// so the brightest part of the centre cannot blow out. The surround is untouched.
    /// HUD: "VISOR: LOW-LIGHT ASSIST". Shader: Hidden/MoonBaseAlpha/VisorGain (render-graph pass before post).
    /// </summary>
    [DefaultExecutionOrder(320)]   // after ExposureController (300) metered this frame
    public class VisorAssist : MonoBehaviour
    {
        public const float ConeDeg = 12.5f, StartStops = 4f, MaxEv = 5f;
        public Camera cam;
        public ExposureController exposure;
        public System.Func<bool> active;

        /// <summary>Current lift (EV) and linear gain.</summary>
        public float Ev { get; private set; }
        public float Gain => Mathf.Pow(2f, Ev);
        public float TargetEv { get; private set; }
        public bool Showing => Ev > 0.3f;

        Material mat;
        GainPass pass;

        bool hooked;

        // lazy: also works in edit-mode tests, where OnEnable does not run
        void EnsureInit()
        {
            if (cam == null) cam = GetComponent<Camera>();
            if (pass == null)
            {
                var sh = Shader.Find("Hidden/MoonBaseAlpha/VisorGain");
                if (sh != null) { mat = new Material(sh) { name = "VisorGain" }; pass = new GainPass(mat); }
            }
            if (!hooked) { RenderPipelineManager.beginCameraRendering += OnBeginCamera; hooked = true; }
        }

        void OnEnable() => EnsureInit();

        void OnDisable() { if (hooked) { RenderPipelineManager.beginCameraRendering -= OnBeginCamera; hooked = false; } }
        void OnDestroy() => OnDisable();

        void LateUpdate() => Step(Time.unscaledDeltaTime);

        /// <summary>Advance the ramp by dt from the exposure meter's latest frame / centre readings.</summary>
        /// <summary>Drop the lift at once (a camera cut, e.g. to the probe camera).</summary>
        public void ResetLift() { Ev = 0f; TargetEv = 0f; Push(); }

        public void Step(float dt)
        {
            EnsureInit();
            TargetEv = 0f;
            bool on = exposure != null && (active == null || active());
            if (on && exposure.CentreLum > 0f && exposure.SurroundLum > 0f)
            {
                // centre vs the rest of the frame (the bright surround the visor is adapted to)
                float stops = Mathf.Log(exposure.SurroundLum / Mathf.Max(exposure.CentreLum, 1e-6f), 2f);
                float maxEv = MaxEv * Tuning.VisorLift.Value;
                // bring the centre up to ~2.5 stops under the frame, starting at a 4-stop gap (soft start)
                float want = Mathf.Clamp((stops - 2.5f) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(StartStops - 0.5f, StartStops + 0.5f, stops)), 0f, maxEv);
                // bright parts inside the patch are protected by the shader's highlight knee; only a centre that is
                // mostly bright (looking at a sunlit wall) gets no lift
                float peak = exposure.CentrePeak * exposure.Exposure;
                if (peak > 1e-6f) want = Mathf.Min(want, Mathf.Max(0f, Mathf.Log(8f / peak, 2f)));
                TargetEv = want;
            }
            float tau = TargetEv > Ev ? 0.45f : 0.35f;   // ~1.4 s to 95% in, ~1 s out
            Ev += (TargetEv - Ev) * (1f - Mathf.Exp(-dt / tau));
            if (Ev < 0.01f && TargetEv <= 0f) Ev = 0f;
            Push();
        }

        public void Push()
        {
            float fov = cam != null ? cam.fieldOfView : 75f;
            float r = Mathf.Tan(ConeDeg * Mathf.Deg2Rad) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 0.5f;   // uv (height) units
            Shader.SetGlobalVector("_MBA_VisorGain", new Vector4(0.5f, 0.5f, r, Gain));
            Shader.SetGlobalFloat("_MBA_VisorAspect", cam != null && cam.pixelHeight > 0 ? cam.pixelWidth / (float)cam.pixelHeight : 16f / 9f);
        }

        void OnBeginCamera(ScriptableRenderContext ctx, Camera c)
        {
            if (c != cam || pass == null || Gain < 1.01f) return;
            var data = c.GetUniversalAdditionalCameraData();
            data?.scriptableRenderer?.EnqueuePass(pass);
        }

        class GainPass : ScriptableRenderPass
        {
            readonly Material mat;
            public GainPass(Material m)
            {
                mat = m;
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer) return;
                var src = res.activeColorTexture;
                var desc = renderGraph.GetTextureDesc(src);
                desc.name = "VisorGain"; desc.clearBuffer = false;
                var dst = renderGraph.CreateTexture(desc);
                renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(src, dst, mat, 0), "Visor low-light gain");
                res.cameraColor = dst;
            }
        }
    }
}
