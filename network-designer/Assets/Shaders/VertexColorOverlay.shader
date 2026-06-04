// Always-on-top unlit line/overlay shader that draws per-vertex COLOR — used by
// the rail-planning analyzer to tint each section of the survey corridor by its
// classification (at-grade / cut / fill / bridge / tunnel / over-grade). Like
// CursorOverlay (ZTest Always, transparent) but the colour comes from the mesh
// vertex colours instead of a single _Color.

Shader "NetworkDesigner/VertexColorOverlay"
{
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

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return i.color; }
            ENDCG
        }
    }
}
