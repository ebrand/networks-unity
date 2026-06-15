// Unlit VERTEX-COLOUR transparent overlay (URP). Each vertex carries its colour + alpha (blue = ponding
// depth, cyan = drainage concentration) baked by HydrologyOverlay; this just blends it over the terrain.
// Drawn slightly above the ground; purely visual, nothing baked.

Shader "NetworkDesigner/HydrologyOverlay"
{
    Properties { _Strength ("Strength", Range(0,1)) = 1 }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }

        Pass
        {
            Name "Hydrology"
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
                float _Strength;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float4 color : COLOR; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float4 color : COLOR; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformWorldToHClip(TransformObjectToWorld(IN.positionOS.xyz));
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float a = IN.color.a * _Strength;
                clip(a - 0.003);
                return half4(IN.color.rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
