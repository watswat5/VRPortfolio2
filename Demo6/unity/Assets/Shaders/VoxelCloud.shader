Shader "WiFiHeatmap/VoxelCloud"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            // Real-world occlusion from Meta's Depth API: EnvironmentDepthManager turns one of these on globally.
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"
            struct Attributes { float4 positionOS : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; float2 size : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; float3 positionWS : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 view = TransformWorldToView(TransformObjectToWorld(input.positionOS.xyz));
                // Preserve voxel size when a borrowed display mesh is shown in the miniature.
                float objectScale = length(mul((float3x3)UNITY_MATRIX_M, float3(1, 0, 0)));
                view.xy += (input.uv * 2 - 1) * input.size.x * objectScale;
                output.positionCS = mul(UNITY_MATRIX_P, float4(view, 1));
                // The billboard corner's own world position, so occlusion is tested where the disk is drawn.
                output.positionWS = mul(UNITY_MATRIX_I_V, float4(view, 1)).xyz;
                output.color = input.color; output.uv = input.uv * 2 - 1;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float radius = dot(input.uv, input.uv);
                clip(1 - radius);
                half falloff = exp(-3 * radius) * (1 - smoothstep(.65, 1, radius));
                // 0 behind real surfaces (hands, people, furniture), 1 in front; always 1 without depth.
                // Scales alpha only: Meta's PREMULTIPLY macro would also darken rgb under SrcAlpha blending.
                float visible = META_DEPTH_GET_OCCLUSION_VALUE_WORLDPOS(input.positionWS, 0);
                return half4(input.color.rgb, input.color.a * falloff * visible);
            }
            ENDHLSL
        }
    }
}
