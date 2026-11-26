// The Earth as seen from the Moon: day map + clouds lit by the Sun (Lambert), city lights on the
// night side, a thin blue limb, and a red refracted-sunset ring when the Earth eclipses the Sun.
// Texture lookup is done per pixel from the true Earth-fixed direction, so rotation, axial tilt
// and libration are exact. Output is pre-multiplied by the camera exposure.
Shader "MoonBaseAlpha/SkyEarth"
{
    Properties
    {
        _DayTex ("Day", 2D) = "white" {}
        _NightTex ("Night lights", 2D) = "black" {}
        _CloudTex ("Clouds", 2D) = "black" {}
        _NightLux ("City lights radiance (lux-equivalent)", Float) = 0.5
    }
    SubShader
    {
        Tags { "Queue"="Background+2" "RenderType"="Background" "RenderPipeline"="UniversalPipeline" "PreviewType"="Sphere" }
        ZWrite Off ZTest LEqual Cull Back
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_DayTex); SAMPLER(sampler_DayTex);
            TEXTURE2D(_NightTex); SAMPLER(sampler_NightTex);
            TEXTURE2D(_CloudTex); SAMPLER(sampler_CloudTex);
            CBUFFER_START(UnityPerMaterial)
                float _NightLux;
            CBUFFER_END
            float4x4 _EarthFixedFromWorld;
            float3 _SunDirWS;
            float _SunLux;
            float _MBA_Exposure;
            float _MBA_GameVisor;

            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 positionWS : TEXCOORD1; };
            V vert(A v)
            {
                V o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }
            float4 frag(V i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 e = mul((float3x3)_EarthFixedFromWorld, n);
                float lat = asin(clamp(e.z, -1, 1));
                float lon = atan2(e.y, e.x);
                float2 uv = float2(lon / (2 * PI) + 0.5, lat / PI + 0.5);
                float3 day = SAMPLE_TEXTURE2D_LOD(_DayTex, sampler_DayTex, uv, 0).rgb;
                float cloud = SAMPLE_TEXTURE2D_LOD(_CloudTex, sampler_CloudTex, uv, 0).r;
                float3 night = SAMPLE_TEXTURE2D_LOD(_NightTex, sampler_NightTex, uv, 0).rgb;
                float3 albedo = lerp(day * 0.75, float3(0.8, 0.8, 0.8), cloud);
                float mu0 = dot(n, _SunDirWS);
                float3 col = albedo * saturate(mu0) * _SunLux;
                col += night * _NightLux * smoothstep(0.0, -0.15, mu0) * (1 - 0.8 * cloud);
                float3 v = normalize(GetCameraPositionWS() - i.positionWS);
                float rim = 1 - saturate(dot(n, v));
                col += float3(0.25, 0.45, 1.0) * pow(rim, 3) * saturate(mu0 + 0.25) * _SunLux * 0.08;
                // back-lit atmosphere ring (sunlight refracted through the limb - seen during an eclipse)
                float behind = saturate(dot(-v, _SunDirWS));
                col += float3(1.0, 0.32, 0.08) * pow(rim, 10) * pow(behind, 40) * _SunLux * 0.05;
                float3 outc = col * _MBA_Exposure;
                // Game visor: the night side as a dark-adapted eye would see it (city lights + moonlit clouds),
                // shown regardless of the daylight exposure.
                float night_w = smoothstep(0.05, -0.1, mu0);
                float3 gameNight = (night * float3(1.0, 0.85, 0.6) * 1.6 + albedo * 0.16) * night_w;
                outc += gameNight * _MBA_GameVisor;
                // soft highlight shoulder: bright cloud tops roll off towards white instead of clipping
                float3 k = max(outc - 0.7, 0);
                outc = min(outc, 0.7) + 0.3 * (1 - exp(-k / 0.3));
                return float4(outc, 1);
            }
            ENDHLSL
        }
    }
}
