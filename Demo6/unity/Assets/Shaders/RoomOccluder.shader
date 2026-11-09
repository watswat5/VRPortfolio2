Shader "WiFiHeatmap/RoomOccluder"
{
    // Invisible copies of the room's walls, floor, ceiling and furniture: depth only, no colour, so passthrough
    // stays visible and the transparent voxel cloud (drawn later, depth-tested) is hidden behind real surfaces.
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry-1" "RenderType"="Opaque" }
        Pass
        {
            ColorMask 0
            ZWrite On
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }
            half4 Frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
