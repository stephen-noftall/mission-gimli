// The solar disc in a vacuum: hard edge, limb darkening, true radiance x exposure (it saturates). The halo
// around it is visor scatter (SkySunGlare); the visor's local dimming patch makes the disc readable.
Shader "MoonBaseAlpha/SkySun"
{
    SubShader
    {
        Tags { "Queue"="Background+1" "RenderType"="Background" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off ZTest LEqual Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float _MBA_Exposure;
            float _MBA_SunRadiance;
            float _MBA_SunFilter;     // visor local dimming over the Sun (0 off .. 1 on): ~ND 4.5
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            V vert(A v) { V o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.uv = v.uv; return o; }
            float4 frag(V i) : SV_Target
            {
                float r = length(i.uv * 2 - 1);
                clip(1 - r);
                float mu = sqrt(saturate(1 - r * r));
                float limb = 1 - 0.6 * (1 - mu) - 0.2 * (1 - mu) * (1 - mu);
                float3 c = float3(1.0, 0.985, 0.96) * limb * _MBA_SunRadiance * _MBA_Exposure * lerp(1.0, 3e-5, _MBA_SunFilter);
                return float4(min(c, 60000), 1);
            }
            ENDHLSL
        }
    }
}
