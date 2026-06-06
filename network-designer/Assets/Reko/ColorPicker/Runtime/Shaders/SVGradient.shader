Shader "ColorPicker/SVGradient"
{
    Properties
    {
        _Stencil("Stencil ID", Float) = 0
        _StencilComp("StencilComp", Float) = 8
        _StencilOp("StencilOp", Float) = 0
        _StencilReadMask("StencilReadMask", Float) = 255
        _StencilWriteMask("StencilWriteMask", Float) = 255
        _ColorMask("ColorMask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
        }

        Stencil
        {
            Ref[_Stencil]
            Comp[_StencilComp]
            Pass[_StencilOp]
            ReadMask[_StencilReadMask]
            WriteMask[_StencilWriteMask]
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest[unity_GUIZTestMode]
        ColorMask[_ColorMask]

        Pass
        {
            HLSLPROGRAM
            #include "UnityCG.cginc"
            #include "ColorUtils.hlsl"

            #pragma vertex UnlitVertex
            #pragma fragment UnlitFragment


            struct Attributes
            {
                float3 positionOS   : POSITION;
                float2 uv           : TEXCOORD0;
                half4 color         : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4  positionCS  : SV_POSITION;
                float2  uv          : TEXCOORD0;
                half4 color         : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings UnlitVertex(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = UnityObjectToClipPos(v.positionOS);
				o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            half4 UnlitFragment(Varyings i) : SV_Target
            {
                float S = i.uv.x;
                float V = i.uv.y;
                float H = RGBToHSV(i.color).r;

                return HSVToRGB(half4(H, S, V, 1));
            }
            ENDHLSL
        }

    }
}
