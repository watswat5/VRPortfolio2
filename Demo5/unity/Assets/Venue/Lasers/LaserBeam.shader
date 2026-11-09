// Laser light for LaserProjector: additive, unlit, vertex colours, both sides, no depth writes. Single-pass stereo safe.
// Vertices with alpha 1 are beams in the air, lit as smoke lights them up:
//   - only where there's smoke: a thin even haze plus swirling smoke (SmokeVolume: a looping recording of a smoke
//     simulation that tiles, sampled by world position, so every beam crosses the same smoke), blended from one frame
//     to the next as the loop plays, and carried along by the wind.
//   - mostly forward: smoke scatters a beam's light ahead of it (Henyey-Greenstein, g), so a beam aimed toward you
//     glows and one seen side-on is faint.
//   - fading at the aperture and as it's used up along the way, and fading out within a metre of your eyes (so a beam
//     through your head doesn't flood the view).
// Vertices with alpha 0 (the lines where beams land) are drawn as they are.
Shader "Freestyle/LaserBeam"
{
    Properties
    {
        _Intensity ("Intensity", Float) = 1
        _Smoke ("Smoke (SmokeVolume)", 3D) = "gray" {}
        _SmokeSize ("Cells a side", Float) = 32
        _SmokeFrames ("Frames in the loop", Float) = 1
        _SmokeLoop ("Seconds a loop", Float) = 12
        _SmokeScale ("Tiles per m", Float) = 0.3
        _Wind ("Wind (m/s)", Vector) = (0.12, 0.04, 0.08, 0)
        _Haze ("Even haze under the smoke", Range(0, 1)) = 0.04
        _Clumps ("Smoke contrast", Range(0.5, 4)) = 1.6
        _Thickness ("Smoke brightness", Float) = 3.5
        _Forward ("Forward scatter (g)", Range(0, 0.9)) = 0.55
        _MaxBoost ("Brightest toward you (x side-on)", Float) = 3
        _NearFade ("Fade near the eyes (m)", Float) = 0.6
        _Absorb ("Light used up per m", Float) = 0.02
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float _Intensity, _SmokeScale, _SmokeSize, _SmokeFrames, _SmokeLoop, _Haze, _Clumps, _Thickness;
            float _Forward, _MaxBoost, _NearFade, _Absorb;
            float4 _Wind;
            sampler3D _Smoke;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half4 color : COLOR;
                float3 world : TEXCOORD0;
                float3 origin : TEXCOORD1;   // the projector's aperture
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.origin = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
                return o;
            }

            // Henyey-Greenstein, relative to side-on (cos 0) so `side-on` is 1
            float Phase(float c, float g)
            {
                float g2 = g * g;
                float hg = (1 - g2) / pow(max(1 + g2 - 2 * g * c, 1e-4), 1.5);
                float side = (1 - g2) / pow(1 + g2, 1.5);
                return hg / side;
            }

            half4 frag(v2f i) : SV_Target
            {
                half3 light = i.color.rgb * _Intensity;
                if (i.color.a < 0.5) return half4(light, 1);   // a line on a surface

                // where in the smoke's tile, carried by the wind
                float3 q = (i.world - _Wind.xyz * _Time.y) * _SmokeScale;
                float z = frac(q.z) * _SmokeSize + 0.5;            // texel within the frame (slice Size repeats slice 0)
                float stride = _SmokeSize + 1, depth = stride * _SmokeFrames;
                float ft = frac(_Time.y / max(_SmokeLoop, 0.1)) * _SmokeFrames;
                float f0 = floor(ft), f1 = fmod(f0 + 1, _SmokeFrames);
                float a = tex3D(_Smoke, float3(q.xy, (f0 * stride + z) / depth)).r;
                float b = tex3D(_Smoke, float3(q.xy, (f1 * stride + z) / depth)).r;
                float smoke = lerp(a, b, ft - f0);
                float density = _Haze + (1 - _Haze) * pow(smoke, _Clumps) * _Thickness;

                float3 toEye = _WorldSpaceCameraPos - i.world;
                float eye = length(toEye);
                float3 travel = i.world - i.origin;
                float along = length(travel);
                float scatter = min(Phase(dot(travel / max(along, 1e-4), toEye / max(eye, 1e-4)), _Forward), _MaxBoost);
                float fade = saturate(eye / max(_NearFade, 1e-3)) * exp(-_Absorb * along) * saturate(along / 0.15);
                return half4(light * density * scatter * fade, 1);
            }
            ENDCG
        }
    }
}
