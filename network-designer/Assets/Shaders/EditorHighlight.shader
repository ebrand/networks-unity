// Minimal unlit, uniform-tint, alpha-blended shader for Edit-mode
// hover overlays — paints the entire mesh in a single _Color (with
// alpha). Sibling of NetworkDesigner/EditorOverlay (which uses
// per-vertex colors); this one is for cases where we're cloning an
// existing mesh (road body, intersection asphalt) that has no vertex
// colors and we just want to tint the whole shape one color.
//
// Two-sided (Cull Off) so the overlay stays visible if the camera
// flips below ground; ZWrite Off + RenderQueue=Transparent+10 so it
// reliably draws on top of the underlying road/intersection asphalt
// without occluding subsequent transparents.

Shader "NetworkDesigner/EditorHighlight"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 0.9, 0, 0.25)
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+10"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }
}
