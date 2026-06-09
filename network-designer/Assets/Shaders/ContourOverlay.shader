// Per-pixel topographic CONTOUR overlay (URP, unlit, transparent). Draws anti-aliased isolines from
// the fragment's WORLD-Y: a thin minor line every _MinorInterval m and a thicker major every
// _MajorEvery'th. Lines stay ~constant screen-width (fwidth) and FADE where contours pack tighter than
// a few pixels, so distance never collapses to a black mass. Rendered as a second pass over the chunk
// mesh — purely visual, nothing baked.

Shader "NetworkDesigner/ContourOverlay"
{
    Properties
    {
        _Color ("Line Color", Color) = (0.1, 0.08, 0.05, 1)
        _MinorInterval ("Minor Interval (m)", Float) = 1
        _MajorEvery ("Major Every", Float) = 10
        _Strength ("Strength", Range(0,1)) = 0.55
        _MinorWidth ("Minor Width (px)", Float) = 1.2
        _MajorWidth ("Major Width (px)", Float) = 2.5
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }

        Pass
        {
            Name "Contour"
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
                float4 _Color;
                float _MinorInterval;
                float _MajorEvery;
                float _Strength;
                float _MinorWidth;
                float _MajorWidth;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionHCS : SV_POSITION; float height : TEXCOORD0; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                float3 wp = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(wp);
                OUT.height = wp.y;
                return OUT;
            }

            // Anti-aliased contour intensity at elevation h for the given spacing, target ~widthPx pixels.
            float ContourAA (float h, float spacing, float widthPx)
            {
                float f = h / spacing;
                float aa = fwidth(f);                          // index change per pixel
                float d  = abs(frac(f + 0.5) - 0.5);           // distance to nearest contour (index units)
                float ln = 1.0 - smoothstep(0.0, max(aa * widthPx * 0.5, 1e-6), d);
                float fade = 1.0 - smoothstep(0.35, 1.2, aa);  // fade when contours are denser than a few px
                return ln * fade;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float h = IN.height;
                float minorS = max(_MinorInterval, 0.01);
                float minor = ContourAA(h, minorS, _MinorWidth);
                float major = ContourAA(h, minorS * max(_MajorEvery, 1.0), _MajorWidth);
                float ln = max(minor, major);
                float a = ln * _Strength * _Color.a;
                clip(a - 0.003);
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
