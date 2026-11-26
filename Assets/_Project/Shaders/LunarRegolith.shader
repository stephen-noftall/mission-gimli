// Lunar regolith surface for URP.
// Reflectance: lunar-Lambert (McEwen 1991) mix of Lommel-Seeliger and Lambert with phase-dependent
// weight, times a Hapke-style shadow-hiding opposition surge (the bright halo around your own shadow
// in Apollo photos). Lighting: main light (Sun, cascaded shadows) + additional lights (rover lamps,
// Forward/Forward+), ambient SH (earthshine + ground bounce, set by ExposureController).
// Albedo / micro-normal detail are procedural, evaluated in frame coordinates (stable under origin shifts).
Shader "MoonBaseAlpha/LunarRegolith"
{
    Properties
    {
        _Albedo ("Normal albedo", Float) = 0.095
        _AlbedoVariation ("Albedo variation", Range(0,0.6)) = 0.22
        _Tint ("Tint", Color) = (1, 0.975, 0.94, 1)
        _OppositionB0 ("Opposition amplitude B0", Float) = 0.9
        _OppositionH ("Opposition width h", Float) = 0.06
        _DetailNormal ("Micro relief strength", Range(0,1)) = 0.45
        _Rocky ("Rocky (cave walls)", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _Albedo;
            float _AlbedoVariation;
            half4 _Tint;
            float _OppositionB0;
            float _OppositionH;
            float _DetailNormal;
            float _Rocky;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "MBABounce.hlsl"

            float3 _MBA_Origin;
            float _MBA_Detail;
            float4 _MBA_Blast;   // xz centre, radius

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            // --- procedural noise (hash-based value noise, no textures)
            float hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.zyx + 31.32);
                return frac((p.x + p.y) * p.z);
            }
            float vnoise(float3 x)
            {
                float3 i = floor(x), f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash13(i), n100 = hash13(i + float3(1,0,0)), n010 = hash13(i + float3(0,1,0)), n110 = hash13(i + float3(1,1,0));
                float n001 = hash13(i + float3(0,0,1)), n101 = hash13(i + float3(1,0,1)), n011 = hash13(i + float3(0,1,1)), n111 = hash13(i + float3(1,1,1));
                return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                            lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
            }
            float fbm3(float3 p)
            {
                float s = 0, a = 0.5;
                [unroll] for (int i = 0; i < 4; i++) { s += a * vnoise(p); p = p * 2.03 + 17.1; a *= 0.5; }
                return s;
            }

            // --- lunar photometry
            float LunarBRDF(float3 N, float3 L, float3 V)
            {
                float mu0 = dot(N, L);
                if (mu0 <= 0) return 0;
                float mu = max(dot(N, V), 0.03);
                float g = acos(clamp(dot(L, V), -1, 1));             // phase angle
                float gd = degrees(g);
                float Lw = saturate(1.0 - 0.019 * gd + 2.42e-4 * gd * gd - 1.46e-6 * gd * gd * gd);
                float f = 2.0 * Lw * mu0 / (mu0 + mu) + (1.0 - Lw) * mu0;
                float B = 1.0 + _OppositionB0 / (1.0 + tan(min(g, 3.0) * 0.5) / _OppositionH);
                return f * B;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 frameP = i.positionWS + _MBA_Origin;
                float3 N = normalize(i.normalWS);
                float3 V = normalize(GetCameraPositionWS() - i.positionWS);
                float dist = length(GetCameraPositionWS() - i.positionWS);

                // albedo: large-scale mare variation, fresh-ejecta brightening, fine speckle
                float big = fbm3(frameP * 0.004);
                float mid = fbm3(frameP * 0.05);
                float fine = vnoise(frameP * 3.1);
                float var = (big - 0.5) * 1.2 + (mid - 0.5) * 0.6 + (fine - 0.5) * 0.25 * saturate(1 - dist / 40);
                float rocky = _Rocky;
                float albedo = _Albedo * (1.0 + _AlbedoVariation * var) * lerp(1.0, 0.9 + 0.3 * mid, rocky);
                // rocket-plume blast zone: the fine top layer is swept away -> a brighter halo (seen around every Apollo LM)
                float bd = length(frameP.xz - _MBA_Blast.xy);
                if (_MBA_Blast.z > 0) albedo *= 1.0 + 0.22 * smoothstep(_MBA_Blast.z, _MBA_Blast.z * 0.3, bd) * (1 - rocky);
                // maturity: fresh ejecta slightly bluish-grey, mature soil slightly brown
                float fresh = saturate((fbm3(frameP * 0.013 + 4.0) - 0.55) * 4.0);
                float3 tint = lerp(float3(1.0, 0.965, 0.92), float3(0.95, 0.965, 1.0), fresh);

                // micro relief (clods, footprint-scale texture) as a normal perturbation, fades with distance
                float pix = length(fwidth(i.positionWS));
                float detail = _MBA_Detail > 0 ? _MBA_Detail : 1.0;
                float fade = saturate(1.0 - dist / 35.0) * _DetailNormal * saturate(1.5 - pix * 4.0) * detail;
                if (fade > 0.001)
                {
                    float sc = lerp(2.2, 0.9, rocky);
                    float e = 0.07;
                    float3 q = frameP * sc;
                    float h0 = fbm3(q);
                    float3 grad = float3(fbm3(q + float3(e,0,0)) - h0, fbm3(q + float3(0,e,0)) - h0, fbm3(q + float3(0,0,e)) - h0) / e;
                    grad -= N * dot(grad, N);
                    N = normalize(N - grad * fade * 0.35);
                }

                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 col = albedo * LunarBRDF(N, mainLight.direction, V) * mainLight.color * mainLight.shadowAttenuation;

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.normalWS = N;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                uint count = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(count)
                    Light l = GetAdditionalLight(lightIndex, i.positionWS, half4(1,1,1,1));
                    col += albedo * LunarBRDF(N, l.direction, V) * l.color * l.distanceAttenuation * l.shadowAttenuation;
                LIGHT_LOOP_END

                // soft diffuse bounce: lamps (rover / suit) + the sunlit pit walls (pit only)
                col += albedo * (MBA_LampBounce(i.positionWS, N) + MBA_PitBounce(i.positionWS, N));
                col += albedo * max(0, SampleSH(N));
                return half4(col * _Tint.rgb * tint, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            float4 vert(Attributes v) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                return ApplyShadowClamping(positionCS);
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R Cull Back
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; };
            float4 vert(Attributes v) : SV_POSITION { return TransformObjectToHClip(v.positionOS.xyz); }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On Cull Back
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }
            half4 frag(Varyings i) : SV_Target { return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
