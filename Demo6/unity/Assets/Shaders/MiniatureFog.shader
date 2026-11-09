Shader "WiFiHeatmap/MiniatureFog"
{
    Properties
    {
        [NoScaleOffset] _Field ("RSSI and coverage", 3D) = "" {}
        _Steps ("Ray steps", Integer) = 24
        _Opacity ("Density", Range(0,4)) = .35
        _LowColor ("Low", Color) = (1,.1,.1,1)
        _MidColor ("Middle", Color) = (1,.7,.05,1)
        _HighColor ("High", Color) = (.1,.9,.3,1)
        _SignalRange ("Color anchors dBm", Vector) = (-81,-39,0,0)
        _SignalDensityInfluence ("Relative signal density effect", Range(0,1)) = 0
        _DensitySignalRange ("Density endpoints dBm", Vector) = (-90,-30,0,0)
        _RoomSize ("Room size in meters", Vector) = (1,1,1,0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            // Draw the exit faces once, including when the viewer enters the proxy box.
            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            // Real-world occlusion from Meta's Depth API: EnvironmentDepthManager turns one of these on globally.
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"
            TEXTURE3D(_Field); SAMPLER(sampler_Field);
            CBUFFER_START(UnityPerMaterial)
                half4 _LowColor, _MidColor, _HighColor;
                float4 _SignalRange, _RoomSize, _DensitySignalRange;
                float _Opacity, _SignalDensityInfluence;
                int _Steps;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionOS:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input,output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionOS=input.positionOS.xyz;
                output.positionCS=TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }
            half4 Frag(Varyings input):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 origin=TransformWorldToObject(GetCameraPositionWS());
                float3 direction=normalize(input.positionOS-origin);
                float3 inverse=lerp(-1.0,1.0,step(0.0,direction))/max(abs(direction),1e-6);
                float3 first=(-.5-origin)*inverse, last=(.5-origin)*inverse;
                float3 nearPlane=min(first,last), farPlane=max(first,last);
                float enter=max(0,max(nearPlane.x,max(nearPlane.y,nearPlane.z)));
                float exit=min(farPlane.x,min(farPlane.y,farPlane.z));
                clip(exit-enter);
                int steps=clamp(_Steps,8,64);
                float stride=(exit-enter)/steps;
                // Use room-space path length so miniature zoom cannot change optical thickness.
                float metersPerStep=length(direction*_RoomSize.xyz)*stride;
                float3 uv=origin+direction*(enter+stride*.5)+.5;
                float3 delta=direction*stride;
                half4 accumulated=0;
                float3 firstFog=0;   // object-space position of the first sample with fog, for the occlusion test
                [loop] for (int i=0;i<steps;i++)
                {
                    half3 field=SAMPLE_TEXTURE3D_LOD(_Field,sampler_Field,uv,0).rgb;
                    if (field.b>.001h)
                    {
                        if (accumulated.a<=0) firstFog=uv-.5;
                        float dbm=(field.r/field.b)*160-150;
                        half confidence=saturate(field.g/field.b);
                        half t=saturate((dbm-_SignalRange.x)/max(1,_SignalRange.y-_SignalRange.x));
                        half3 color=t<=.5h ? lerp(_LowColor.rgb,_MidColor.rgb,t*2) : lerp(_MidColor.rgb,_HighColor.rgb,t*2-1);
                        half relativeSignal=saturate((dbm-_DensitySignalRange.x)/max(1,_DensitySignalRange.y-_DensitySignalRange.x));
                        // Optional visual emphasis, not extra measured coverage/confidence or RF power.
                        half signalDensity=lerp(1.0h,lerp(.2h,2.0h,relativeSignal),saturate(_SignalDensityInfluence));
                        half density=field.b*lerp(.2h,1.0h,confidence)*signalDensity;
                        half alpha=1-exp(-density*_Opacity*6*metersPerStep);
                        accumulated.rgb+=(1-accumulated.a)*color*alpha;
                        accumulated.a+=(1-accumulated.a)*alpha;
                        if (accumulated.a>.97h) break;
                    }
                    uv+=delta;
                }
                // One depth test per pixel, where the fog starts: a real hand or person in front of it hides the
                // whole ray's fog (premultiplied, so colour and alpha scale together). Fog partly behind something
                // is shown or hidden as a whole.
                float3 fogWS=TransformObjectToWorld(firstFog);
                META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY_WORLDPOS(fogWS,accumulated,0);
                return accumulated;
            }
            ENDHLSL
        }
    }
}
