Shader "ColorPicker/Gradient"
{
	Properties
	{
		_Color1("Color 1", Color) = (0,0,0,1)
		_Color2("Color 2", Color) = (1,1,1,1)
        [Toggle(VERTICAL)] _Vertical("Vertical", Float) = 0

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

            #pragma vertex UnlitVertex
            #pragma fragment UnlitFragment
            #pragma shader_feature VERTICAL

            struct Attributes
            {
                float3 positionOS   : POSITION;
                float2 uv           : TEXCOORD0;
                float4 color        : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float4 color        : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _Color1;
            float4 _Color2;
            float _Vertical;

            Varyings UnlitVertex(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = UnityObjectToClipPos(v.positionOS);
				
#ifdef VERTICAL
                o.uv = v.uv.yx;
#else
                o.uv = v.uv;
#endif
                o.color = v.color;
                return o;
            }

            half4 UnlitFragment(Varyings i) : SV_Target
            {
                float4 color2 = _Color2 * i.color;
                return lerp(_Color1, color2, i.uv.x);
            }
            ENDHLSL
        }

    }
}
