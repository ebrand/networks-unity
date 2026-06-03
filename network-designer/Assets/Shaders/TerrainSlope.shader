// Slope-blended lit terrain shader for the low-poly chunked mesh.
//
// The mesh is FLAT-SHADED (un-shared verts -> one normal per face), so the
// world normal arriving in the fragment is constant across each facet. We use
// it to derive the facet's steepness and blend a "rock" band in on steep
// faces:  albedo = lerp(grass, rock, smoothstep(_SlopeStart, _SlopeFull, angle)).
// Because the slope is read from the normal at shade time (NOT baked into the
// mesh), _SlopeStart/_SlopeFull/colors are all live material props -> no chunk
// rebuild when they change.
//
// The rock can be a flat color (default) or an optional triplanar texture
// (_RockTex, _UseRockTex=1) projected from world space so it needs no UVs (the
// terrain mesh has none). Standard URP Lit lighting/shadows/fog otherwise.

Shader "NetworkDesigner/TerrainSlope"
{
    Properties
    {
        [MainColor] _GrassColor ("Grass Color (flat)", Color) = (0.40, 0.50, 0.30, 1)
        _RockColor  ("Rock Color (steep)", Color) = (0.42, 0.40, 0.38, 1)
        [Toggle] _UseRockTex ("Use Rock Texture", Float) = 0
        _RockTex ("Rock Texture (triplanar)", 2D) = "white" {}
        _RockTexScale ("Rock Texture Scale (1/world units)", Float) = 0.12
        _SlopeStart ("Rock Slope Start (deg)", Range(0, 90)) = 26
        _SlopeFull  ("Rock Slope Full (deg)", Range(0, 90)) = 45
        _Smoothness ("Smoothness", Range(0, 1)) = 0
        _Metallic   ("Metallic", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        // ---------------- Forward lit ----------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTERED_RENDERING
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _GrassColor;
                float4 _RockColor;
                float4 _RockTex_ST;
                float  _UseRockTex;
                float  _RockTexScale;
                float  _SlopeStart;
                float  _SlopeFull;
                float  _Smoothness;
                float  _Metallic;
            CBUFFER_END

            TEXTURE2D(_RockTex);
            SAMPLER(sampler_RockTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float  fogCoord   : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   n = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = p.positionCS;
                OUT.positionWS = p.positionWS;
                OUT.normalWS   = n.normalWS;
                OUT.fogCoord   = ComputeFogFactor(p.positionCS.z);
                return OUT;
            }

            // Triplanar sample of the rock texture in world space (no mesh UVs).
            float3 TriplanarRock(float3 wp, float3 n)
            {
                float3 bw = pow(abs(n), 4.0);
                bw /= max(bw.x + bw.y + bw.z, 1e-4);
                float3 cx = SAMPLE_TEXTURE2D(_RockTex, sampler_RockTex, wp.zy * _RockTexScale).rgb;
                float3 cy = SAMPLE_TEXTURE2D(_RockTex, sampler_RockTex, wp.xz * _RockTexScale).rgb;
                float3 cz = SAMPLE_TEXTURE2D(_RockTex, sampler_RockTex, wp.xy * _RockTexScale).rgb;
                return cx * bw.x + cy * bw.y + cz * bw.z;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 nrm = normalize(IN.normalWS);
                // 0 deg = flat (normal up), 90 deg = vertical cliff.
                float ang = degrees(acos(saturate(nrm.y)));
                float t = smoothstep(_SlopeStart, max(_SlopeStart + 0.001, _SlopeFull), ang);

                float3 rock = _RockColor.rgb;
                if (_UseRockTex > 0.5) rock *= TriplanarRock(IN.positionWS, nrm);
                float3 albedo = lerp(_GrassColor.rgb, rock, t);

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = nrm;
                inputData.viewDirectionWS = normalize(GetCameraPositionWS() - IN.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogCoord;
                inputData.bakedGI = SampleSH(nrm);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData s = (SurfaceData)0;
                s.albedo = albedo;
                s.metallic = _Metallic;
                s.smoothness = _Smoothness;
                s.occlusion = 1.0;
                s.alpha = 1.0;

                half4 col = UniversalFragmentPBR(inputData, s);
                col.rgb = MixFog(col.rgb, IN.fogCoord);
                return col;
            }
            ENDHLSL
        }

        // ---------------- Shadow caster ----------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V { float4 positionCS : SV_POSITION; };

            float4 GetShadowCS(float3 posWS, float3 normalWS)
            {
                float3 lightDir;
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                lightDir = normalize(_LightPosition - posWS);
            #else
                lightDir = _LightDirection;
            #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, lightDir));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return cs;
            }

            V ShadowVert(A IN)
            {
                V OUT;
                float3 wp = TransformObjectToWorld(IN.positionOS.xyz);
                float3 wn = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionCS = GetShadowCS(wp, wn);
                return OUT;
            }

            half4 ShadowFrag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }

        // ---------------- Depth only (depth texture / SSAO) ----------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask 0

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; };

            V DepthVert(A IN)
            {
                V OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 DepthFrag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    // If the slope shader fails to compile, terrain still renders lit & flat.
    FallBack "Universal Render Pipeline/Lit"
}
