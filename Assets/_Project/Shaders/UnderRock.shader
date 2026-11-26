// Black shell around the camera while it is under rock: hides everything beyond ~0.9x the tunnel stream
// radius (open chunk ends, terrain seen from below) so the far end of a tube recedes into darkness.
// Round 12 (S3): also in the pit (sky shown), clipped above the rim plane (_MBA_ShellClipY), so the tube ends seen
// from the pit floor are black while the sky through the pit opening stays.
// Unlit, writes depth, casts no shadows; the exposure meter uses raycasts, so it never sees this.
Shader "MoonBaseAlpha/UnderRock"
{
    SubShader
    {
        Tags { "Queue"="Geometry+10" "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            ZWrite On ZTest LEqual Cull Front
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float _MBA_ShellClipY;   // pit mode: no shell above this world height (the sky through the pit opening)
            struct V2F { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            V2F vert(float4 positionOS : POSITION) { V2F o; o.positionWS = TransformObjectToWorld(positionOS.xyz); o.positionCS = TransformWorldToHClip(o.positionWS); return o; }
            half4 frag(V2F i) : SV_Target { clip(_MBA_ShellClipY - i.positionWS.y); return half4(0, 0, 0, 1); }
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R Cull Front
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float _MBA_ShellClipY;
            struct V2F { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            V2F vert(float4 positionOS : POSITION) { V2F o; o.positionWS = TransformObjectToWorld(positionOS.xyz); o.positionCS = TransformWorldToHClip(o.positionWS); return o; }
            half frag(V2F i) : SV_Target { clip(_MBA_ShellClipY - i.positionWS.y); return 0; }
            ENDHLSL
        }
    }
}
