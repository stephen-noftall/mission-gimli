// Stars as single-pixel points. Pixel value = pi * E_star / pixel_solid_angle * exposure, so they are
// invisible while the camera is exposed for sunlit ground (as in every Apollo photo) and appear
// when the exposure opens up in shadow, at night or in the tube mouth.
Shader "MoonBaseAlpha/SkyStars"
{
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off ZTest LEqual Cull Off Blend One One
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float _MBA_Exposure;
            float _MBA_PixelSolidAngle;
            float _MBA_GameVisor;
            struct A { float4 positionOS : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float3 color : COLOR; };
            V vert(A v)
            {
                V o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                float lux = v.uv.x * 1e-6;
                float physical = PI * lux / max(_MBA_PixelSolidAngle, 1e-9) * _MBA_Exposure;
                // game visor: stars as a dark-adapted eye sees them (bright ones clearly, faint ones dimly)
                float game = saturate(v.uv.x * 0.35) * 0.9;   // uv.x = illuminance in micro-lux
                o.color = v.color.rgb * max(physical, game * _MBA_GameVisor);
                return o;
            }
            float4 frag(V i) : SV_Target { return float4(i.color, 1); }
            ENDHLSL
        }
    }
}
