// Always-on-top unlit line/overlay shader for the terrain brush cursor.
// ZTest Always so the ring is never occluded by the terrain it sits on
// (the cursor only floats a few cm above the surface and would otherwise
// clip into relief). ZWrite Off + transparent blend; color via _Color
// ([MainColor] so Material.color drives it).

Shader "NetworkDesigner/CursorOverlay"
{
    Properties
    {
        [MainColor] _Color ("Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Overlay" "IgnoreProjector" = "True" }
        ZTest Always
        ZWrite Off
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return _Color; }
            ENDCG
        }
    }
}
