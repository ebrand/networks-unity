// Realistic ground shader for the smooth chunked terrain: blends THREE triplanar
// world-space textures — grass, grass+dirt, dirt+gravel — by SLOPE and fBm NOISE
// so the surface reads as natural rolling terrain with organic, un-banded seams.
//
//   • Triplanar/world-space → needs no mesh UVs (the chunk grid has none) and
//     never stretches on slopes; LOD/stream rebuilds change nothing.
//   • Each layer = its texture × a tint, so it renders sensibly even before real
//     textures are assigned (white default tex → just the tint colour).
//   • Blend: grass on flat, gravel on steep (slope), grass+dirt mixed in by noise
//     patches + gentle slope; noise jitters every slope threshold so boundaries
//     wander instead of forming clean contour bands. All live material props →
//     no chunk rebuild on tweak.
//   • Standard URP Lit lighting/shadows/fog + the same world grid overlay as
//     TerrainSlope (this is cloned from that proven shader).

Shader "NetworkDesigner/TerrainGround"
{
    Properties
    {
        [MainColor] _GrassTint ("Grass Tint", Color) = (0.34, 0.50, 0.26, 1)
        _GrassTex   ("Grass (triplanar)", 2D) = "white" {}
        _GrassScale ("Grass Scale (1/world units)", Float) = 0.15

        _MidTint  ("Grass+Dirt Tint", Color) = (0.42, 0.40, 0.24, 1)
        _MidTex   ("Grass+Dirt (triplanar)", 2D) = "white" {}
        _MidScale ("Grass+Dirt Scale", Float) = 0.15

        _GravelTint  ("Dirt+Gravel Tint", Color) = (0.40, 0.36, 0.31, 1)
        _GravelTex   ("Dirt+Gravel (triplanar)", 2D) = "white" {}
        _GravelScale ("Dirt+Gravel Scale", Float) = 0.18

        [Normal] _GrassNormal  ("Grass Normal", 2D) = "bump" {}
        [Normal] _MidNormal    ("Grass+Dirt Normal", 2D) = "bump" {}
        [Normal] _GravelNormal ("Dirt+Gravel Normal", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1

        [Toggle] _Stochastic ("Stochastic Tiling (anti-repeat)", Float) = 1
        _MacroScale  ("Macro Variation Scale (1/m)", Float) = 0.004
        _MacroAmount ("Macro Variation Amount", Range(0, 0.8)) = 0.28

        _NoiseScale ("Blend Noise Scale (1/world units)", Float) = 0.01
        _SlopeNoise ("Slope Threshold Jitter (deg)", Range(0, 40)) = 14

        _MidSlopeStart    ("Grass->Dirt Slope Start (deg)", Range(0, 90)) = 8
        _MidSlopeFull     ("Grass->Dirt Slope Full (deg)",  Range(0, 90)) = 24
        _GravelSlopeStart ("Dirt->Gravel Slope Start (deg)", Range(0, 90)) = 28
        _GravelSlopeFull  ("Dirt->Gravel Slope Full (deg)",  Range(0, 90)) = 46

        _DirtPatchLo     ("Dirt Patch Noise Lo", Range(0, 1)) = 0.55
        _DirtPatchHi     ("Dirt Patch Noise Hi", Range(0, 1)) = 0.80
        _DirtPatchAmount ("Dirt Patch Amount", Range(0, 1)) = 0.6

        _Smoothness ("Smoothness", Range(0, 1)) = 0
        _Metallic   ("Metallic", Range(0, 1)) = 0

        _GridColor      ("Grid Color", Color) = (0.14, 0.15, 0.17, 1)
        _GridSpacing    ("Grid Spacing (m)", Float) = 5
        _GridMajorEvery ("Grid Major Every N", Float) = 10
        _GridStrength   ("Grid Strength (0 = off)", Range(0, 1)) = 0
        _GridLineWidth  ("Grid Line Width (px)", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

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
                float4 _GrassTint;   float4 _GrassTex_ST;   float _GrassScale;
                float4 _MidTint;     float4 _MidTex_ST;     float _MidScale;
                float4 _GravelTint;  float4 _GravelTex_ST;  float _GravelScale;
                float4 _GrassNormal_ST; float4 _MidNormal_ST; float4 _GravelNormal_ST;
                float  _NormalStrength;
                float  _Stochastic;
                float  _MacroScale;
                float  _MacroAmount;
                float  _NoiseScale;
                float  _SlopeNoise;
                float  _MidSlopeStart;
                float  _MidSlopeFull;
                float  _GravelSlopeStart;
                float  _GravelSlopeFull;
                float  _DirtPatchLo;
                float  _DirtPatchHi;
                float  _DirtPatchAmount;
                float  _Smoothness;
                float  _Metallic;
                float4 _GridColor;
                float  _GridSpacing;
                float  _GridMajorEvery;
                float  _GridStrength;
                float  _GridLineWidth;
            CBUFFER_END

            TEXTURE2D(_GrassTex);  SAMPLER(sampler_GrassTex);
            TEXTURE2D(_MidTex);    SAMPLER(sampler_MidTex);
            TEXTURE2D(_GravelTex); SAMPLER(sampler_GravelTex);
            TEXTURE2D(_GrassNormal);  SAMPLER(sampler_GrassNormal);
            TEXTURE2D(_MidNormal);    SAMPLER(sampler_MidNormal);
            TEXTURE2D(_GravelNormal); SAMPLER(sampler_GravelNormal);

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
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

            // ── Stochastic "hex" tiling (Heitz–Neyret simplex grid) — kills the visible repeat by sampling
            // the texture on a randomly-offset triangular grid and blending the 3 nearest cells. ──
            float2 HashOff(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return frac(sin(p) * 43758.5453);
            }
            void TriGrid(float2 uv, out float w1, out float w2, out float w3, out float2 v1, out float2 v2, out float2 v3)
            {
                uv *= 3.464;   // 2*sqrt(3)
                const float2x2 toSkewed = float2x2(1.0, 0.0, -0.57735027, 1.15470054);
                float2 sk = mul(toSkewed, uv);
                float2 baseId = floor(sk);
                float3 t = float3(frac(sk), 0.0); t.z = 1.0 - t.x - t.y;
                if (t.z > 0.0) { w1 = t.z; w2 = t.y; w3 = t.x; v1 = baseId; v2 = baseId + float2(0, 1); v3 = baseId + float2(1, 0); }
                else { w1 = -t.z; w2 = 1.0 - t.y; w3 = 1.0 - t.x; v1 = baseId + float2(1, 1); v2 = baseId + float2(1, 0); v3 = baseId + float2(0, 1); }
            }
            float3 HexSample(TEXTURE2D_PARAM(tex, smp), float2 uv)
            {
                float w1, w2, w3; float2 v1, v2, v3;
                TriGrid(uv, w1, w2, w3, v1, v2, v3);
                float2 dx = ddx(uv), dy = ddy(uv);   // hand mips the original derivatives (uv has per-cell jumps)
                float3 c1 = SAMPLE_TEXTURE2D_GRAD(tex, smp, uv + HashOff(v1), dx, dy).rgb;
                float3 c2 = SAMPLE_TEXTURE2D_GRAD(tex, smp, uv + HashOff(v2), dx, dy).rgb;
                float3 c3 = SAMPLE_TEXTURE2D_GRAD(tex, smp, uv + HashOff(v3), dx, dy).rgb;
                float3 w = pow(float3(w1, w2, w3), 3.0); w /= max(w.x + w.y + w.z, 1e-4);   // sharpen → less contrast loss
                return c1 * w.x + c2 * w.y + c3 * w.z;
            }
            // One projected axis sample — stochastic hex when enabled, plain otherwise.
            float3 AxisSample(TEXTURE2D_PARAM(tex, smp), float2 uv)
            {
                if (_Stochastic > 0.5) return HexSample(TEXTURE2D_ARGS(tex, smp), uv);
                return SAMPLE_TEXTURE2D(tex, smp, uv).rgb;
            }

            // Triplanar world-space sample of a texture (blend weights `bw` from the normal).
            float3 TriSample(TEXTURE2D_PARAM(tex, smp), float3 wp, float3 bw, float sc)
            {
                float3 cx = AxisSample(TEXTURE2D_ARGS(tex, smp), wp.zy * sc);
                float3 cy = AxisSample(TEXTURE2D_ARGS(tex, smp), wp.xz * sc);
                float3 cz = AxisSample(TEXTURE2D_ARGS(tex, smp), wp.xy * sc);
                return cx * bw.x + cy * bw.y + cz * bw.z;
            }

            // Triplanar tangent-space normal map → world normal (Ben Golus "whiteout" blend). `gn` = the
            // geometric world normal; returns a perturbed world normal. Flat ("bump") default → returns gn.
            float3 TriNormal(TEXTURE2D_PARAM(tex, smp), float3 wp, float3 gn, float3 bw, float sc, float str)
            {
                float3 nx = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, smp, wp.zy * sc), str);
                float3 ny = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, smp, wp.xz * sc), str);
                float3 nz = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, smp, wp.xy * sc), str);
                nx = float3(nx.xy + gn.zy, abs(nx.z) * gn.x);
                ny = float3(ny.xy + gn.xz, abs(ny.z) * gn.y);
                nz = float3(nz.xy + gn.xy, abs(nz.z) * gn.z);
                return normalize(nx.zyx * bw.x + ny.xzy * bw.y + nz.xyz * bw.z);
            }

            // Value noise (hash-based) + 4-octave fBm in world XZ → 0..1.
            float Hash(float2 p) { p = frac(p * float2(127.1, 311.7)); p += dot(p, p + 34.5); return frac(p.x * p.y); }
            float VNoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = Hash(i), b = Hash(i + float2(1, 0)), c = Hash(i + float2(0, 1)), d = Hash(i + float2(1, 1));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }
            float FBM(float2 p)
            {
                float v = 0.0, amp = 0.5;
                for (int i = 0; i < 4; i++) { v += amp * VNoise(p); p *= 2.0; amp *= 0.5; }
                return v;
            }

            float GridCoverage(float2 wxz, float spacing, float width)
            {
                float2 c = wxz / max(spacing, 0.01);
                float2 d = fwidth(c);
                float2 g = abs(frac(c - 0.5) - 0.5) / max(d * max(width, 0.01), 1e-5);
                return 1.0 - min(min(g.x, g.y), 1.0);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 nrm = normalize(IN.normalWS);
                float3 wp = IN.positionWS;
                float ang = degrees(acos(saturate(nrm.y)));   // 0 = flat, 90 = vertical

                float noise = FBM(wp.xz * _NoiseScale);        // 0..1
                float jit = (noise - 0.5) * 2.0 * _SlopeNoise;  // ±_SlopeNoise degrees

                // Per-layer triplanar albedo (texture × tint).
                float3 bw = pow(abs(nrm), 4.0); bw /= max(bw.x + bw.y + bw.z, 1e-4);
                float3 grass  = TriSample(TEXTURE2D_ARGS(_GrassTex, sampler_GrassTex),   wp, bw, _GrassScale)  * _GrassTint.rgb;
                float3 mid    = TriSample(TEXTURE2D_ARGS(_MidTex, sampler_MidTex),       wp, bw, _MidScale)    * _MidTint.rgb;
                float3 gravel = TriSample(TEXTURE2D_ARGS(_GravelTex, sampler_GravelTex), wp, bw, _GravelScale) * _GravelTint.rgb;

                // Blend weights: grass base → grass+dirt by gentle slope + noise patches → gravel by steep slope.
                float midT    = smoothstep(_MidSlopeStart,    max(_MidSlopeStart + 0.001, _MidSlopeFull),       ang + jit);
                float patch   = smoothstep(_DirtPatchLo, max(_DirtPatchLo + 0.001, _DirtPatchHi), noise) * _DirtPatchAmount;
                midT = saturate(max(midT, patch));
                float gravelT = smoothstep(_GravelSlopeStart, max(_GravelSlopeStart + 0.001, _GravelSlopeFull), ang + jit);

                float3 albedo = lerp(grass, mid, midT);
                albedo = lerp(albedo, gravel, saturate(gravelT));

                // Macro variation: very-low-frequency brightness drift so the overall tone wanders across the
                // landscape (the CS2-style dry/green patches) — your eye latches onto this, not the tile.
                float macro = FBM(wp.xz * _MacroScale);
                albedo *= lerp(1.0 - _MacroAmount, 1.0 + _MacroAmount, macro);

                // Per-layer triplanar normals, blended by the same slope/noise weights → perturbed world normal.
                float3 nGrass  = TriNormal(TEXTURE2D_ARGS(_GrassNormal, sampler_GrassNormal),   wp, nrm, bw, _GrassScale,  _NormalStrength);
                float3 nMid    = TriNormal(TEXTURE2D_ARGS(_MidNormal, sampler_MidNormal),       wp, nrm, bw, _MidScale,    _NormalStrength);
                float3 nGravel = TriNormal(TEXTURE2D_ARGS(_GravelNormal, sampler_GravelNormal), wp, nrm, bw, _GravelScale, _NormalStrength);
                float3 N = normalize(lerp(lerp(nGrass, nMid, midT), nGravel, saturate(gravelT)));

                if (_GridStrength > 0.0)
                {
                    float minor = GridCoverage(wp.xz, _GridSpacing, _GridLineWidth);
                    float major = GridCoverage(wp.xz, _GridSpacing * max(_GridMajorEvery, 1.0), _GridLineWidth * 1.4);
                    float g = max(minor * 0.55, major);
                    albedo = lerp(albedo, _GridColor.rgb, saturate(g) * _GridStrength);
                }

                InputData inputData = (InputData)0;
                inputData.positionWS = wp;
                inputData.normalWS = N;
                inputData.viewDirectionWS = normalize(GetCameraPositionWS() - wp);
                inputData.shadowCoord = TransformWorldToShadowCoord(wp);
                inputData.fogCoord = IN.fogCoord;
                inputData.bakedGI = SampleSH(N);
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

        // ---------------- Depth only ----------------
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

            V DepthVert(A IN) { V OUT; OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz); return OUT; }
            half4 DepthFrag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
