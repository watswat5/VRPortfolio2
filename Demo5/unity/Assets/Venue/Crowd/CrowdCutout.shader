// The crowd's cutouts (Crowd): the sheet's cell, alpha-clipped (alpha to coverage under MSAA, so the edges don't
// shimmer), unlit, fading toward a haze colour with distance from the camera so overlapping rows of black figures
// separate front to back, like a smoky room. Single-pass stereo safe.
Shader "Freestyle/CrowdCutout"
{
    Properties
    {
        _MainTex ("Sheet", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _Haze ("Haze colour", Color) = (0.2, 0.22, 0.28, 1)
        _HazeNear ("Haze starts (m)", Float) = 2
        _HazeFar ("Haze full (m)", Float) = 12
        _HazeMax ("Haze at its fullest", Range(0, 1)) = 0.45
    }
    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }
        Cull Back
        AlphaToMask On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Cutoff, _HazeNear, _HazeFar, _HazeMax;
            fixed4 _Haze;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float haze : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                float d = distance(mul(unity_ObjectToWorld, v.vertex).xyz, _WorldSpaceCameraPos);
                o.haze = saturate((d - _HazeNear) / max(0.01, _HazeFar - _HazeNear)) * _HazeMax;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                // sharpen the alpha around the cutoff so alpha to coverage gives a clean, anti-aliased edge
                c.a = (c.a - _Cutoff) / max(fwidth(c.a), 0.0001) + 0.5;
                clip(c.a);
                c.rgb = lerp(c.rgb, _Haze.rgb, i.haze);
                return c;
            }
            ENDCG
        }
    }
}
