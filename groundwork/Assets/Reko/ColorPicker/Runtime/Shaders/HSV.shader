Shader "ColorPicker/HSV"
{
	Properties
	{
		[Toggle(USE_SATURATION)] _UseSaturation("Saturation/Value", FLoat) = 0
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

			#pragma multi_compile __ USE_SATURATION

 

			struct Attributes
			{
				float3 positionOS   : POSITION;
				float2 uv           : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4  positionCS  : SV_POSITION;
				float2  uv          : TEXCOORD0;
				UNITY_VERTEX_OUTPUT_STEREO
			};


			float _SV;

			Varyings UnlitVertex(Attributes v)
			{
				Varyings o = (Varyings)0;
				UNITY_SETUP_INSTANCE_ID(v);
				o.positionCS = UnityObjectToClipPos(v.positionOS);
				o.uv = v.uv;
				return o;
			}

			half4 UnlitFragment(Varyings i) : SV_Target
			{
				half2 polar = GetPolarCoordinates(i.uv);
				float hue = polar.y;
				float sv = polar.x;

				#ifdef USE_SATURATION
					return HSVToRGB(half4(hue, sv, 1, 1));
				#else
					return HSVToRGB(half4(hue, 1, sv, 1));
				#endif			
			}

			ENDHLSL
		}
	}
}
