// From the class's MultiInteractionExample2026 (AvatarDemo), unchanged.
// Shows _LeftTex to the left eye and _RightTex to the right eye. The u coordinate is flipped because the
// mirror cameras look back at the mirror, so their right is the mirror's left.
Shader "AvatarDemo/StereoMirror"
{
    Properties
    {
        _LeftTex ("Left Eye", 2D) = "black" {}
        _RightTex ("Right Eye", 2D) = "black" {}
        _Tint ("Tint", Color) = (0.95, 0.97, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_LeftTex);  SAMPLER(sampler_LeftTex);
            TEXTURE2D(_RightTex); SAMPLER(sampler_RightTex);
            half4 _Tint;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings  { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = float2(1.0 - i.uv.x, i.uv.y);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half4 l = SAMPLE_TEXTURE2D(_LeftTex, sampler_LeftTex, i.uv);
                half4 r = SAMPLE_TEXTURE2D(_RightTex, sampler_RightTex, i.uv);
                return (unity_StereoEyeIndex == 0 ? l : r) * _Tint;
            }
            ENDHLSL
        }
    }
}
