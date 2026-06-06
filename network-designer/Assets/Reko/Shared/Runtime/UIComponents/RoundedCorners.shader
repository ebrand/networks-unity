Shader "Reko/RoundedCorners"
{
    Properties
    {
        _Radius("Radius", Vector) = (10, 10, 10, 10)
        _Size("Size", Vector) = (0, 0, 0, 0)
        _Border("Border", Float) = 0

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
            "IGNOREPROJECTOR"="true"
            "CanUseSpriteAtlas"="true"
            "PreviewType"="Plane"
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
        ZTest [unity_GUIZTestMode]
        ColorMask[_ColorMask]

        Pass
        {
            HLSLPROGRAM
            #include "UnityCG.cginc"

            #pragma vertex UnlitVertex
            #pragma fragment UnlitFragment


            struct Attributes
            {
                float3 positionOS   : POSITION;
                float2 localPos     : TEXCOORD0;
                half4 color         : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4  positionCS  : SV_POSITION;
                float2  localPos    : TEXCOORD0;
                half4 color         : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _Radius;
            float4 _Size;
            float _Border;

            float UniformRoundedBox(half2 position, half2 halfsize, float radius)
            {
                if (radius == 0)
                    return -1;
                return length(max(abs(position) - halfsize + radius, 0.0)) - radius;
            }

            float RoundedBox(half2 position, half2 halfsize, float4 radii)
            {
                int ri = max(0, sign(position.x)) + max(0, sign(position.y)) * 2;
                float radius = radii[ri];

                float dist = UniformRoundedBox(position, halfsize, radius);
                dist = max(dist, -1);

                return dist;
            }

            float SmoothCutoff(float dist)
            {
                float derivative = fwidth(dist) * 0.5;
                float cutoff = smoothstep(derivative, -derivative, dist);

                return cutoff;
            }

            float SubstractShape( float d1, float d2 )
            {
                return max(d1, -d2);
            }

            Varyings UnlitVertex(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = UnityObjectToClipPos(v.positionOS);

                o.localPos = v.localPos;
                o.color = v.color;
                return o;
            }

            half4 UnlitFragment(Varyings i) : SV_Target
            {
                float dist = RoundedBox(i.localPos, _Size.xy * 0.5f, _Radius);

                if(_Border > 0)
                {
                    float innerRadius = max(1, _Radius - _Border);
                    float dist2 = RoundedBox(i.localPos, _Size.xy * 0.5f - _Border, innerRadius);
                    dist = SubstractShape(dist, dist2);
                }

                clip(-dist);

                float alpha = SmoothCutoff(dist);
                half4 col = i.color;
                col.a *= alpha;
                
                return col;
            }
            ENDHLSL
        }

    }
}
