// Regolith grains thrown by the wheels: soft round sprites lit by the Sun (strong forward scattering
// when back-lit, as fine dust does), plus ambient SH. Exposure-scaled like everything else.
Shader "MoonBaseAlpha/Dust"
{
    Properties
    {
        _MainTex ("Sprite", 2D) = "white" {}
        _Albedo ("Albedo", Float) = 0.12
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Albedo;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; float3 positionWS : TEXCOORD1; };
            V vert(A v)
            {
                V o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.color = v.color; o.uv = v.uv;
                return o;
            }
            float4 frag(V i) : SV_Target
            {
                float a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a * i.color.a;
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float3 V = normalize(GetCameraPositionWS() - i.positionWS);
                float back = pow(saturate(dot(-V, sun.direction)), 6);
                float phase = 0.6 + 2.5 * back;
                float3 c = _Albedo * i.color.rgb * (sun.color * sun.shadowAttenuation * phase + SampleSH(float3(0, 1, 0)));
                return float4(c, a);
            }
            ENDHLSL
        }
    }
}
