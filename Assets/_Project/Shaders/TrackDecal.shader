// Rover tyre tracks: a ribbon a few cm above the ground that multiplies what is under it (Blend DstColor Zero), so
// the imprint is "slightly darker" in sunlight, earthshine or a headlight alike. The chevron tread is drawn
// procedurally: uv.x across the track (0..1), uv.y along it in metres. Disturbed regolith is darker than the
// undisturbed surface because the bright, fluffy top grains (and their opposition-surge sparkle) are pressed flat.
Shader "MoonBaseAlpha/TrackDecal"
{
    Properties
    {
        _Darken ("Darken (track floor)", Float) = 0.22
        _Chevron ("Chevron contrast", Float) = 0.35
        _Pitch ("Chevron pitch (m)", Float) = 0.18
    }
    SubShader
    {
        Tags { "Queue"="Geometry+10" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Blend DstColor Zero
        ZWrite Off Cull Off
        Offset -2, -4
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float _Darken, _Chevron, _Pitch;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float3 positionWS : TEXCOORD1; };
            V vert(A v)
            {
                V o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = v.uv; o.color = v.color;
                return o;
            }
            float4 frag(V i) : SV_Target
            {
                float x = i.uv.x * 2 - 1;                            // -1..1 across
                float edge = smoothstep(1.0, 0.75, abs(x));          // soft berms at the sides
                float chev = frac((i.uv.y - abs(x) * 0.09) / _Pitch);   // V-shaped grousers
                float bar = smoothstep(0.0, 0.08, chev) * smoothstep(0.5, 0.38, chev);
                float fade = i.color.a;                              // ends + age
                float d = (_Darken + _Chevron * _Darken * bar) * edge * fade;
                // fade out with distance (no aliasing shimmer of the chevrons far away)
                float dist = length(GetCameraPositionWS() - i.positionWS);
                d *= saturate(1.0 - (dist - 120.0) / 200.0);
                return float4(1 - d, 1 - d, 1 - d, 1);
            }
            ENDHLSL
        }
    }
}
