// Smart visor low-light assist (Rendering/VisorAssist.cs): soft-edged local gain at the view centre, applied to the
// HDR scene before tone mapping. Multiplicative only: black stays black.
Shader "Hidden/MoonBaseAlpha/VisorGain"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always
        Pass
        {
            Name "VisorGain"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _MBA_VisorGain;    // xy centre (uv), z radius (uv height units), w gain
            float _MBA_VisorAspect;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float2 d = (uv - _MBA_VisorGain.xy) * float2(_MBA_VisorAspect, 1);
                float m = 1 - smoothstep(_MBA_VisorGain.z * 0.55, _MBA_VisorGain.z * 1.4, length(d));
                // soft highlight knee: dark pixels get the full gain, pixels near white ~none (no blow-out in the patch)
                float g = _MBA_VisorGain.w;
                float l = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
                float gl = g / (1.0 + l * (g - 1.0));
                c.rgb *= lerp(1.0, gl, m);
                return c;
            }
            ENDHLSL
        }
    }
}
