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
        _Threshold ("Intensity Threshold", Range(0, 1)) = 0.12
        _StepCount ("Ray Steps", Range(16, 256)) = 96
        // von VolumeView.cs gesetzt je nachdem ob das geladene volumen graustufen
        // (R8, nur .r belegt) oder echtes rgb (RGBA32) ist - kein Inspector-regler
        [HideInInspector] _IsColor ("Is Color", Float) = 0
        // r,g,b sind drei ROHE, unabhaengige kanal-intensitaeten (siehe
        // NiftiVolumeLoader.IsLiveChannels) statt einer fertigen farbe - werden hier live
        // gemischt, damit man jeden kanal einzeln faerben/an-ausschalten kann
        [HideInInspector] _IsLiveChannels ("Is Live Channels", Float) = 0
        [HideInInspector] _ChannelColor0 ("Channel 0 Color", Color) = (1,0,0,1)
        [HideInInspector] _ChannelColor1 ("Channel 1 Color", Color) = (0,1,0,1)
        [HideInInspector] _ChannelColor2 ("Channel 2 Color", Color) = (0,0,1,1)
        [Toggle] _Channel0On ("Channel 0 On", Float) = 1
        [Toggle] _Channel1On ("Channel 1 On", Float) = 1
        [Toggle] _Channel2On ("Channel 2 On", Float) = 1
        [Toggle] _EnableShading ("Gradient Shading", Float) = 1
        _ThresholdMax ("Cut Range Max", Range(0, 1)) = 1.0
        [Enum(Translucent,0,MIP,1)] _RenderMode ("Render Mode", Float) = 0
    }

    SubShader
    {
        // queue bewusst unter der standard-ui-queue (3000) - panel soll beim reinzoomen
        // immer sichtbar/bedienbar bleiben statt vom gewachsenen volumen ueberdeckt zu
        // werden, kein tiefen-check zwischen zwei transparenten objekten moeglich
        Tags { "RenderType"="Transparent" "Queue"="Transparent-50" "RenderPipeline"="UniversalPipeline" }
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
            float _ThresholdMax;
            float _RenderMode;
            int _StepCount;
            float _IsColor;
            float _IsLiveChannels;
            float3 _ChannelColor0;
            float3 _ChannelColor1;
            float3 _ChannelColor2;
            float _Channel0On;
            float _Channel1On;
            float _Channel2On;
            float _EnableShading;
            // von VolumeView.cs gesetzt: 1/SizeX,Y,Z - gradient braucht einen zur
            // aufloesung passenden sampling-abstand, sonst zu grob (grosse volumen)
            // oder zu verrauscht (kleine volumen)
            float3 _VolumeTexelSize;

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

            // einzige stelle die die textur interpretiert - drei faelle: graustufen (nur
            // .r belegt), fertig gemischte farbe (baked rgb, z.b. schon farbige slice-
            // bilder), oder live-kanaele (r,g,b = drei unabhaengige rohe intensitaeten,
            // hier je mit eigener farbe gewichtet und an/aus geschaltet aufsummiert)
            void SampleVoxel(float3 uvw, out half3 color, out half intensity)
            {
                half4 texel = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, uvw);
                if (_IsLiveChannels > 0.5)
                {
                    half c0 = texel.r * _Channel0On;
                    half c1 = texel.g * _Channel1On;
                    half c2 = texel.b * _Channel2On;
                    color = c0 * _ChannelColor0 + c1 * _ChannelColor1 + c2 * _ChannelColor2;
                    intensity = max(c0, max(c1, c2));
                }
                else if (_IsColor > 0.5)
                {
                    color = texel.rgb;
                    intensity = max(texel.r, max(texel.g, texel.b));
                }
                else
                {
                    color = texel.rrr;
                    intensity = texel.r;
                }
            }

            // gleiche intensitaets-metrik wie im hauptsample, fuer den gradient reused
            half SampleIntensity(float3 uvw)
            {
                half3 unusedColor;
                half intensity;
                SampleVoxel(uvw, unusedColor, intensity);
                return intensity;
            }

            // vorwaerts-differenz gradient (3 statt 6 extra samples, zentrum ist eh schon
            // gesampelt) als pseudo-normale, "kopflampe" von der kamera aus statt echtem
            // szenen-licht - kein extra lighting setup, aber gibt der struktur sichtbar
            // tiefe statt flachem nebel-look. gemeinsam von translucent- und MIP-pfad genutzt
            half3 ApplyShading(half3 color, half intensity, float3 uvw, float3 samplePosOS, float3 camOS)
            {
                if (_EnableShading <= 0.5) return color;

                float3 ix = uvw + float3(_VolumeTexelSize.x, 0, 0);
                float3 iy = uvw + float3(0, _VolumeTexelSize.y, 0);
                float3 iz = uvw + float3(0, 0, _VolumeTexelSize.z);
                float3 grad = float3(SampleIntensity(ix), SampleIntensity(iy), SampleIntensity(iz)) - intensity;
                float gradLen = length(grad);
                if (gradLen <= 0.0005) return color;

                float3 normalOS = -grad / gradLen; // intensitaet steigt nach innen -> normale nach aussen
                float3 viewDirOS = normalize(camOS - samplePosOS);
                half ndotl = saturate(dot(normalOS, viewDirOS));
                return color * lerp(0.5h, 1.15h, ndotl);
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

                if (_RenderMode > 0.5)
                {
                    // MIP: zeigt pro sichtstrahl nur den hellsten treffer im
                    // [threshold,thresholdMax] bereich, kein alpha-blending - dadurch nie
                    // "milchig", aber auch keine tiefenwirkung durch mehrere ueberlappende
                    // strukturen (nur der eine hellste punkt pro strahl gewinnt)
                    half bestIntensity = 0;
                    half3 bestColor = 0;
                    float3 bestPos = pos;
                    bool hit = false;

                    [loop]
                    for (int s = 0; s < _StepCount; s++)
                    {
                        float3 uvw = pos + 0.5;
                        half3 color;
                        half intensity;
                        SampleVoxel(uvw, color, intensity);

                        if (intensity > _Threshold && intensity <= _ThresholdMax && intensity > bestIntensity)
                        {
                            bestIntensity = intensity;
                            bestColor = color;
                            bestPos = pos;
                            hit = true;
                        }

                        pos += rayDir * stepSize;
                    }

                    if (!hit) discard;

                    float3 bestUvw = bestPos + 0.5;
                    half3 finalColor = ApplyShading(bestColor, bestIntensity, bestUvw, bestPos, camOS);
                    return half4(finalColor, 1);
                }

                half4 accum = half4(0, 0, 0, 0);

                [loop]
                for (int s = 0; s < _StepCount; s++)
                {
                    float3 uvw = pos + 0.5; // objekt raum auf textur raum 0 bis 1
                    half3 color;
                    half intensity;
                    SampleVoxel(uvw, color, intensity);

                    if (intensity > _Threshold && intensity <= _ThresholdMax)
                    {
                        // 0 direkt ueber threshold, 1 beim maximum - quadriert fuer steileren
                        // kontrast (schwaches/mittleres signal traegt kaum noch zum nebel bei,
                        // vorher lineares intensity*density sorgte fuer den milchigen look)
                        half shaped = saturate((intensity - _Threshold) / max(1.0 - _Threshold, 0.0001));
                        shaped *= shaped;
                        half alpha = saturate(shaped * _Density * stepSize * 10.0);

                        half3 shadedColor = ApplyShading(color, intensity, uvw, pos, camOS);

                        accum.rgb += (1.0 - accum.a) * alpha * shadedColor;
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
