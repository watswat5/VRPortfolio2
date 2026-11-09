// One spray of graffiti into a GraffitiSurface's paint: reads the paint so far (_MainTex), lays a soft round dot of
// _Color over it (its alpha the dot's strength) centred on _Stamp.xy with radii _Stamp.zw (UV), solid inside _Hardness
// of the radius and fading to nothing at its edge, speckled near the edge like a real can. Straight (not premultiplied)
// alpha, composited "over"; GraffitiSurface ping-pongs it through a temporary texture.
Shader "Hidden/Freestyle/GraffitiStamp"
{
    Properties { _MainTex ("Paint", 2D) = "black" {} }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Stamp;      // xy centre, zw radii (UV)
            float4 _Color;      // rgb colour, a strength
            float _Hardness;    // share of the radius that's solid

            float hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            float4 frag(v2f_img i) : SV_Target
            {
                float4 d = tex2D(_MainTex, i.uv);
                float r = length((i.uv - _Stamp.xy) / _Stamp.zw);
                float a = _Color.a * (1 - smoothstep(_Hardness, 1, r));
                // overspray: the soft edge is a scatter of fine droplets rather than a smooth fade
                float edge = saturate((r - _Hardness) / max(1 - _Hardness, 1e-3));
                a *= lerp(1, step(0.55, hash(floor(i.uv * 2048) + _Stamp.xy * 131)), edge * 0.8);
                float oa = a + d.a * (1 - a);
                float3 rgb = oa > 1e-4 ? (_Color.rgb * a + d.rgb * d.a * (1 - a)) / oa : d.rgb;
                return float4(rgb, oa);
            }
            ENDCG
        }
    }
}
