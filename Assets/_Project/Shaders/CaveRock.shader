// Lava-tube rock. Materials grounded in lunar samples / terrestrial lava tubes:
//  - fresh mare basalt (not space-weathered: slightly lighter and browner/greener than surface regolith)
//  - lava glaze: glossy black glassy skin on the walls (terrestrial tubes are lined with it)
//  - ilmenite / native-iron glints (Marius-type mare basalts carry ilmenite and metal grains)
//  - orange & green volcanic glass bead deposits on floors (Apollo 17 orange soil, Apollo 15 green glass)
//  - pale yellowish Zn/S volatile crusts along cracks in deep levels (speculative; the orange beads carry
//    such coatings)
//  - talus dust near openings
// Diffuse: lunar-Lambert; specular: GGX for glaze and glints. Procedural in frame coordinates.
Shader "MoonBaseAlpha/CaveRock"
{
    Properties
    {
        _Albedo ("Basalt albedo", Float) = 0.085
        _GlazeAmount ("Lava glaze amount", Range(0,1)) = 0.55
        _GlintAmount ("Mineral glints", Range(0,1)) = 0.6
        _GlassAmount ("Volcanic glass deposits", Range(0,1)) = 0.5
        _FarRing ("Ring: 0 near, 1 far, -1 never clipped", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _Albedo, _GlazeAmount, _GlintAmount, _GlassAmount, _FarRing;
        CBUFFER_END
        float4 _MBA_TubeR;   // near radius (0 = no far ring), far radius, shell radius
        // near chunks end at the near radius, the coarse far ring starts there (horizontal distance to the camera)
        void RingClip(float3 positionWS)
        {
            if (_MBA_TubeR.x <= 0 || _FarRing < -0.5) return;
            float d = length(positionWS.xz - _WorldSpaceCameraPos.xz);
            // round 12 (S3): the two rings overlap by 3 m at the hand-off (their surfaces differ: 1.5 vs 3 m voxels), so
            // there is no crack between them to see through
            clip(_FarRing > 0.5 ? d - (_MBA_TubeR.x - 3.0) : (_MBA_TubeR.x + 3.0) - d);
        }
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
            float _MBA_MouthFloorY;   // level-0 floor at the pit (frame Y)
            float _MBA_Detail;        // quality preset: 0..1 (0 when unset -> treated as 1)
            float _MBA_DepositLegacy; // tests only: 1 = the round-7 per-cell deposit colour (before/after screenshot)

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; };
            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            float hash13(float3 p) { p = frac(p * 0.1031); p += dot(p, p.zyx + 31.32); return frac((p.x + p.y) * p.z); }
            float3 hash33(float3 p) { return float3(hash13(p), hash13(p + 17.1), hash13(p + 41.7)); }
            float vnoise(float3 x)
            {
                float3 i = floor(x), f = frac(x); f = f * f * (3 - 2 * f);
                return lerp(lerp(lerp(hash13(i), hash13(i + float3(1,0,0)), f.x), lerp(hash13(i + float3(0,1,0)), hash13(i + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(hash13(i + float3(0,0,1)), hash13(i + float3(1,0,1)), f.x), lerp(hash13(i + float3(0,1,1)), hash13(i + float3(1,1,1)), f.x), f.y), f.z);
            }
            float fbm(float3 p) { float s = 0, a = 0.5; [unroll] for (int i = 0; i < 4; i++) { s += a * vnoise(p); p = p * 2.03 + 7.7; a *= 0.5; } return s; }

            float LunarDiffuse(float3 N, float3 L, float3 V)
            {
                float mu0 = dot(N, L); if (mu0 <= 0) return 0;
                float mu = max(dot(N, V), 0.03);
                float gd = degrees(acos(clamp(dot(L, V), -1, 1)));
                float Lw = saturate(1.0 - 0.019 * gd + 2.42e-4 * gd * gd - 1.46e-6 * gd * gd * gd);
                return 2.0 * Lw * mu0 / (mu0 + mu) * 0.5 + (1.0 - Lw * 0.5) * mu0;   // rock: more Lambertian than regolith
            }
            float GGX(float3 N, float3 L, float3 V, float rough)
            {
                float3 H = normalize(L + V);
                float nh = saturate(dot(N, H)), nl = saturate(dot(N, L)), nv = saturate(dot(N, V));
                float a2 = rough * rough * rough * rough;
                float dd = nh * nh * (a2 - 1) + 1;
                float D = a2 / (PI * dd * dd);
                float k = rough * rough * 0.5;
                float G = nl / (nl * (1 - k) + k) * nv / (nv * (1 - k) + k);
                float F = 0.04 + 0.96 * pow(1 - saturate(dot(H, V)), 5);
                return D * G * F / max(4 * nv, 1e-3);      // multiplied by light later (already contains n.l via G/4nl.. approx)
            }

            struct Mat { float3 albedo; float gloss; float specAmt; };

            Mat Surface(float3 fp, float3 N, float dist)
            {
                Mat m;
                float depth = _MBA_MouthFloorY - fp.y;               // metres below the level-0 floor
                float levelF = depth / 75.0;
                float upness = saturate(N.y);
                float wall = 1 - upness;
                float big = fbm(fp * 0.02), mid = fbm(fp * 0.11), fine = lerp(0.5, vnoise(fp * 2.3), saturate(1.2 - length(fwidth(fp)) * 4.0));
                // fresh basalt: slightly brown-green, mottled
                float3 basalt = float3(0.98, 0.95, 0.88) * _Albedo * (0.8 + 0.45 * mid + 0.15 * (fine - 0.5));
                // talus / dust near the pit and on floors close to openings
                float dust = saturate(1 - depth / 25.0) * upness;
                float3 col = lerp(basalt, float3(0.95, 0.93, 0.9) * 0.11, dust);
                m.gloss = 0; m.specAmt = 0;
                // lava glaze: walls & ceilings, patchy
                float glaze = smoothstep(0.45, 0.65, fbm(fp * 0.05 + 3.3)) * smoothstep(0.15, 0.6, wall) * _GlazeAmount * (1 - dust);
                col = lerp(col, float3(0.035, 0.034, 0.033), glaze);
                m.gloss = glaze; m.specAmt = glaze * 0.9;
                // volcanic glass bead deposits on floors (orange / green). Round 8: the colour used to be picked per
                // 180 m cell (step(0.5, vnoise(floor(fp/180)))) -> straight, square colour edges; now it drifts smoothly
                // over ~150 m, and the deposit edge is broken up by fine noise into a patchy bead spray
                float gEdge = (vnoise(fp * 0.55 + 3.7) - 0.5) * 0.10 + (vnoise(fp * 2.7 + 9.1) - 0.5) * 0.06;
                float gm = smoothstep(0.62, 0.72, fbm(fp * 0.015 + 19.0) + gEdge) * upness * _GlassAmount;
                gm *= lerp(0.55, 1.0, vnoise(fp * 1.3 + 1.9));   // sprayed beads: uneven cover inside the patch
                // science review (round 8): pyroclastic beads are a surface deposit; in a tube they are mostly fall-in
                // through the pit / skylights / collapses -> common on the upper level, rare deeper (SPECULATIVE)
                gm *= lerp(1.0, 0.3, saturate(depth / 160.0));
                float glassMix = _MBA_DepositLegacy > 0.5 ? step(0.5, vnoise(floor(fp / 180.0)))
                               : smoothstep(0.3, 0.7, vnoise(fp / 150.0 + 4.2));
                float3 glassCol = lerp(float3(0.42, 0.22, 0.08), float3(0.2, 0.27, 0.12), glassMix);
                col = lerp(col, glassCol * 0.42, gm);
                // pale volatile crusts along cracks, deeper levels only (speculative)
                float crack = 1 - abs(fbm(fp * 0.09 + 5.0) * 2 - 1);
                float crust = smoothstep(0.9, 0.97, crack) * smoothstep(1.5, 3.0, levelF) * smoothstep(0.4, 0.6, big);
                col = lerp(col, float3(0.62, 0.58, 0.36) * 0.45, crust);
                m.albedo = col;
                return m;
            }

            half4 frag(Varyings i) : SV_Target
            {
                RingClip(i.positionWS);
                float3 fp = i.positionWS + _MBA_Origin;
                float3 N = normalize(i.normalWS);
                float3 V = normalize(GetCameraPositionWS() - i.positionWS);
                float dist = length(GetCameraPositionWS() - i.positionWS);
                float detail = _MBA_Detail > 0 ? _MBA_Detail : 1.0;
                float pix = length(fwidth(i.positionWS));                  // metres per pixel here
                // relief: fractures + flow texture; only where pixels can resolve it (no shimmer)
                float fade = saturate(1 - dist / 40.0) * saturate(1.6 - pix * 3.0) * detail;
                if (fade > 0.001)
                {
                    float e = 0.06; float3 q = fp * 0.9;
                    float h0 = fbm(q);
                    float3 g = float3(fbm(q + float3(e,0,0)) - h0, fbm(q + float3(0,e,0)) - h0, fbm(q + float3(0,0,e)) - h0) / e;
                    g -= N * dot(g, N);
                    N = normalize(N - g * 0.45 * fade);
                }
                Mat m = Surface(fp, N, dist);
                float rough = lerp(0.85, 0.18, m.gloss);

                // glints: tiny mineral facets (ilmenite / metal), each with its own random normal
                float3 cellP = floor(fp * 70.0);
                float3 rn = normalize(hash33(cellP) * 2 - 1 + N * 1.6);
                // ~0.5% of the 1.4 cm cells, faded out by 3 m (beyond that they alias into strobing)
                float glintOn = step(0.995, hash13(cellP + 3.0)) * _GlintAmount * saturate(1 - dist / 3.0) * saturate(1.0 - pix * 60.0) * detail;

                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light ml = GetMainLight(shadowCoord);
                // tunnels are under tens of metres of rock: no direct sunlight ever (shadows end at the
                // shadow distance, so without this, far sun-facing walls would light up through the rock)
                float3 lc = 0;
                float3 col = m.albedo * LunarDiffuse(N, ml.direction, V) * lc;
                col += lc * m.specAmt * GGX(N, ml.direction, V, rough) * saturate(dot(N, ml.direction));
                col += lc * glintOn * pow(saturate(dot(reflect(-ml.direction, rn), V)), 200) * 2.5;

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS; inputData.normalWS = N;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                uint count = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(count)
                    Light l = GetAdditionalLight(lightIndex, i.positionWS, half4(1,1,1,1));
                    float3 c = l.color * l.distanceAttenuation * l.shadowAttenuation;
                    col += m.albedo * LunarDiffuse(N, l.direction, V) * c;
                    col += c * m.specAmt * GGX(N, l.direction, V, rough) * saturate(dot(N, l.direction));
                    col += c * glintOn * pow(saturate(dot(reflect(-l.direction, rn), V)), 200) * 2.5;
                LIGHT_LOOP_END
                // soft diffuse bounce (no specular / glints): lamps + the sunlit pit walls near the mouth
                col += m.albedo * (MBA_LampBounce(i.positionWS, N) + MBA_PitBounce(i.positionWS, N));
                // the visor's soft fill fades out towards the draw distance: the tube recedes into darkness
                float farFade = _MBA_TubeR.z > 0 ? 1 - smoothstep(0.6 * _MBA_TubeR.z, _MBA_TubeR.z, dist) : 1;
                col += m.albedo * max(0, SampleSH(N)) * farFade;
                // round 12 (S3): everything on the rock fades to black before the stream edge (150-200 m in, a tube is
                // pitch dark: no sunlight, lamps reach far less), in the pit and in the tubes alike
                float edgeFade = _MBA_TubeR.y > 0 ? 1 - smoothstep(0.62 * _MBA_TubeR.y, 0.86 * _MBA_TubeR.y, dist) : 1;
                return half4(col * edgeFade, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection; float3 _LightPosition;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V2F { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            V2F vert(Attributes v)
            {
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                V2F o; o.positionWS = positionWS;
                o.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS)));
                return o;
            }
            half4 frag(V2F i) : SV_Target { RingClip(i.positionWS); return 0; }
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
            struct V2F { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            V2F vert(Attributes v) { V2F o; o.positionWS = TransformObjectToWorld(v.positionOS.xyz); o.positionCS = TransformWorldToHClip(o.positionWS); return o; }
            half frag(V2F i) : SV_Target { RingClip(i.positionWS); return 0; }
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
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 positionWS : TEXCOORD1; };
            Varyings vert(Attributes v) { Varyings o; o.positionWS = TransformObjectToWorld(v.positionOS.xyz); o.positionCS = TransformWorldToHClip(o.positionWS); o.normalWS = TransformObjectToWorldNormal(v.normalOS); return o; }
            half4 frag(Varyings i) : SV_Target { RingClip(i.positionWS); return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
