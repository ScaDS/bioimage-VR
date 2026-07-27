// raymarch ueber texture3d im objekt raum, intensitaet zu alpha
// urp plus openxr
// single pass stereo, deshalb GetCameraPositionWS statt eigener kamera mathe
// vorsicht beim aendern vom vertex fragment stereo setup
Shader "BioimageVR/VolumeRaymarch"
{
    Properties
    {
        _VolumeTex ("Volume Texture", 3D) = "white" {}
        _Density ("Density", Range(0.01, 5)) = 1.0
        _Threshold ("Intensity Threshold", Range(0, 1)) = 0.05
        _StepCount ("Ray Steps", Range(16, 256)) = 96
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Front
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_VolumeTex);
            SAMPLER(sampler_VolumeTex);
            float _Density;
            float _Threshold;
            int _StepCount;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionOS  : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionOS = v.positionOS.xyz;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            // schnitt strahl mit einheitswuerfel, objekt raum
            bool IntersectBox(float3 rayOrigin, float3 rayDir, out float tNear, out float tFar)
            {
                float3 invDir = 1.0 / rayDir;
                float3 t0 = (-0.5 - rayOrigin) * invDir;
                float3 t1 = (0.5 - rayOrigin) * invDir;
                float3 tmin = min(t0, t1);
                float3 tmax = max(t0, t1);
                tNear = max(max(tmin.x, tmin.y), tmin.z);
                tFar = min(min(tmax.x, tmax.y), tmax.z);
                return tFar > max(tNear, 0.0);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // gibt korrekte kamera position pro auge im stereo modus
                float3 camWS = GetCameraPositionWS();
                float3 camOS = TransformWorldToObject(camWS);

                // strahl startet an der kamera richtung rueckseite
                // andersrum zeigt der strahl am volumen vorbei
                float3 rayOrigin = camOS;
                float3 rayDir = normalize(i.positionOS - camOS);

                float tNear, tFar;
                if (!IntersectBox(rayOrigin, rayDir, tNear, tFar))
                    discard;

                tNear = max(tNear, 0.0);
                float stepSize = (tFar - tNear) / _StepCount;
                float3 pos = rayOrigin + rayDir * tNear;

                half4 accum = half4(0, 0, 0, 0);

                [loop]
                for (int s = 0; s < _StepCount; s++)
                {
                    float3 uvw = pos + 0.5; // objekt raum auf textur raum 0 bis 1
                    half intensity = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, uvw).r;

                    if (intensity > _Threshold)
                    {
                        half alpha = saturate(intensity * _Density * stepSize * 10.0);
                        half3 color = half3(intensity, intensity, intensity);
                        accum.rgb += (1.0 - accum.a) * alpha * color;
                        accum.a += (1.0 - accum.a) * alpha;
                    }

                    pos += rayDir * stepSize;

                    if (accum.a >= 0.995) break;
                }

                if (accum.a <= 0.001) discard;

                return accum;
            }
            ENDHLSL
        }
    }
}
