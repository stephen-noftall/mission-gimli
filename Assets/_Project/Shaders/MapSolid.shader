// 3D tunnel map (UI/TunnelMap.cs), flow layout: opaque, unlit, vertex-coloured solids (the mapped tubes, shading
// baked into the colours) so they occlude each other, unlike the additive Holo lines.
Shader "MoonBaseAlpha/MapSolid"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        ZWrite On
        Cull Off
        Pass
        {
            Name "MapSolid"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 positionOS : POSITION; float4 color : COLOR; };
            struct V { float4 positionCS : SV_POSITION; float4 color : COLOR; };
            V vert(A i) { V o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.color = i.color; return o; }
            half4 frag(V i) : SV_Target { return half4(i.color.rgb, 1); }
            ENDHLSL
        }
    }
}
