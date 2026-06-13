// Per-pixel RIDGE / VALLEY overlay (URP, unlit, transparent). Reads a per-vertex curvature SIGNAL baked
// into TEXCOORD1.x by ChunkWorld.BuildMesh (signal = -Laplacian of height: > 0 on convex crests/ridges,
// < 0 in concave hollows/valleys). Tints ridge crests one colour and valley floors another where the
// curvature exceeds a threshold, anti-aliased, fading in over a soft band. Drawn as a 2nd renderer over
// each chunk mesh — purely visual, nothing baked into the terrain. Mirrors ContourOverlay's pass setup.

Shader "NetworkDesigner/RidgeValleyOverlay"
{
    Properties
    {
        _RidgeColor  ("Ridge Color",  Color) = (0.95, 0.45, 0.15, 1)   // warm orange = crest
        _ValleyColor ("Valley Color", Color) = (0.20, 0.55, 0.95, 1)   // cool blue   = hollow
        _Threshold ("Curvature Threshold (1/m)", Float) = 0.006
        _Softness  ("Soft Band", Range(0.1, 6)) = 1.6
        _Strength  ("Strength", Range(0,1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }

        Pass
        {
            Name "RidgeValley"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _RidgeColor;
                float4 _ValleyColor;
                float _Threshold;
                float _Softness;
                float _Strength;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv1 : TEXCOORD1; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float signal : TEXCOORD0; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                float3 wp = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(wp);
                OUT.signal = IN.uv1.x;          // baked curvature (1/m): +ridge / -valley
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float s   = IN.signal;
                float thr = max(_Threshold, 1e-5);
                float hi  = thr * (1.0 + _Softness);            // fully saturated past here
                float ridge  = smoothstep(thr, hi,  s);         // s > 0 → crest
                float valley = smoothstep(thr, hi, -s);         // s < 0 → hollow

                float3 col; float a;
                if (ridge >= valley) { col = _RidgeColor.rgb;  a = ridge  * _RidgeColor.a;  }
                else                 { col = _ValleyColor.rgb; a = valley * _ValleyColor.a; }
                a *= _Strength;
                clip(a - 0.003);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
