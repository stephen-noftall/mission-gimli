// 3D tunnel map (UI/TunnelMap.cs): unlit, vertex-coloured, additive "hologram" lines and shells.
Shader "MoonBaseAlpha/Holo"
{
    Properties { _Intensity ("Intensity", Float) = 1 }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend One One
        ZWrite Off
        Cull Off
        Pass
        {
            Name "Holo"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float _Intensity;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float4 color : COLOR; };
            struct V { float4 positionCS : SV_POSITION; float4 color : COLOR; };
            V vert(A i) { V o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.color = i.color; return o; }
            half4 frag(V i) : SV_Target { return half4(i.color.rgb * i.color.a * _Intensity, 1); }
            ENDHLSL
        }
    }
}
