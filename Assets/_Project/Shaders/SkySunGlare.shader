// Visor glare around the Sun (no air on the Moon, so this is scatter in the helmet visor, not sky):
// an additive halo falling off ~1/theta^2 plus faint six-fold spikes, scaled by the disc radiance x exposure.
// Hidden behind terrain / rock / the Earth (they draw over it) and by _MBA_SunGlareVis (camera->Sun ray).
// The visor's local dimming patch (_MBA_SunFilter, speculative ~2035 electrochromic) darkens the inner 1 deg.
Shader "MoonBaseAlpha/SkySunGlare"
{
    SubShader
    {
        Tags { "Queue"="Background+1" "RenderType"="Background" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off ZTest LEqual Cull Off Blend One One
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float _MBA_Exposure, _MBA_SunRadiance, _MBA_SunGlareVis, _MBA_SunFilter, _MBA_GlareMaxDeg;
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            V vert(A v) { V o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.uv = v.uv; return o; }
            float4 frag(V i) : SV_Target
            {
                float2 d = i.uv * 2 - 1;
                float r = length(d);
                float maxDeg = _MBA_GlareMaxDeg > 0 ? _MBA_GlareMaxDeg : 12.0;
                float th = max(r * maxDeg, 0.3);                       // deg from the Sun centre
                float phi = atan2(d.y, d.x);
                float halo = 7.5e-6 * (4.0 / (th * th));               // (2 deg / theta)^2
                float spikes = 3e-6 * (2.0 / th) * pow(abs(cos(3 * phi)), 400);
                float filt = lerp(1.0, 3e-5, _MBA_SunFilter * smoothstep(1.15, 0.9, th));
                float edge = smoothstep(1.0, 0.75, r);
                float g = (halo + spikes) * _MBA_SunRadiance * _MBA_Exposure * _MBA_SunGlareVis * filt * edge;
                g = min(g, 40.0);
                return float4(float3(1.0, 0.97, 0.92) * g, 1);
            }
            ENDHLSL
        }
    }
}
