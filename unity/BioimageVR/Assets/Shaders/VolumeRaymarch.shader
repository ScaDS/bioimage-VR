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
        // 03.09.: 96 -> 160 angehoben, gegen sichtbares "schichten"/banding beim
        // raymarchen (STATUS.md 26.08., Punkt 2 wunschliste, guenstigster hebel zuerst)
        _StepCount ("Ray Steps", Range(16, 256)) = 160
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
        // 22.09.: sigg/hadwiger tricubic (8 statt 64 fetches), siehe SampleVoxelCubic.
        // eigener toggle statt fest an, weil das den hauptsample teuer macht (8x) -
        // live testbar/rueckgaengig zu machen ohne shader-rebuild, falls zu teuer
        [Toggle] _TricubicFilter ("Tricubic Filter", Float) = 1
        // segmentierung, von VolumeView gesetzt wenn eine _labels datei da ist
        [HideInInspector] _LabelTex ("Label Texture", 3D) = "black" {}
        [HideInInspector] _HasLabels ("Has Labels", Float) = 0
        [Toggle] _ShowLabels ("Show Labels", Float) = 0
        [HideInInspector] _SelectedLabel ("Selected Label", Float) = 0
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
            // point filter kommt von der textur selbst, ids duerfen nicht gemischt werden
            TEXTURE3D(_LabelTex);
            SAMPLER(sampler_LabelTex);
            float _HasLabels;
            float _ShowLabels;
            float _SelectedLabel;
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
            float _TricubicFilter;
            // von VolumeView.cs gesetzt: 1/SizeX,Y,Z - gradient braucht einen zur
            // aufloesung passenden sampling-abstand, sonst zu grob (grosse volumen)
            // oder zu verrauscht (kleine volumen)
            float3 _VolumeTexelSize;
            // von VolumeView.cs gesetzt: SizeX,Y,Z in texeln - fuer die tricubic
            // filterung noetig, _VolumeTexelSize allein reicht nicht (ist 1.5x skaliert
            // fuers gradienten-sampling, keine echte texelgroesse)
            float3 _VolumeSize;

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

            // id aus lo und hi byte, siehe NiftiVolumeLoader.BuildLabelTexture
            float SampleLabel(float3 uvw)
            {
                float2 rg = SAMPLE_TEXTURE3D_LOD(_LabelTex, sampler_LabelTex, uvw, 0).rg;
                return round(rg.r * 255.0) + 256.0 * round(rg.g * 255.0);
            }

            // feste farbe pro id, hue ueber goldenen schnitt, benachbarte ids weit auseinander
            half3 LabelColor(float id)
            {
                float h = frac(id * 0.61803398875);
                half3 rgb = saturate(abs(frac(h + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0);
                return lerp(half3(1, 1, 1), rgb, 0.65h);
            }

            // segmentierung ueber die rohfarbe legen, gewaehlte zelle staerker, rest gedimmt
            half3 ApplyLabel(half3 color, float3 uvw)
            {
                if (_ShowLabels < 0.5 || _HasLabels < 0.5) return color;
                float id = SampleLabel(uvw);
                bool anySelected = _SelectedLabel > 0.5;
                if (id < 0.5) return anySelected ? color * 0.5h : color;
                bool selected = abs(id - _SelectedLabel) < 0.5;
                half tintAmount = anySelected ? (selected ? 0.85h : 0.25h) : 0.6h;
                half3 tinted = lerp(color, LabelColor(id) * max(max(color.r, color.g), max(color.b, 0.35h)) * 1.4h, tintAmount);
                return anySelected && !selected ? tinted * 0.5h : tinted;
            }

            // b-spline gewichte einer achse aus v (bruchteil zwischen zwei texeln),
            // sigg/hadwiger gpu gems 2 kap. 20 - w0..w3 summieren sich immer zu 1
            half4 CubicWeights(half v)
            {
                half4 n = half4(1.0h, 2.0h, 3.0h, 4.0h) - v;
                half4 s = n * n * n;
                half x = s.x;
                half y = s.y - 4.0h * s.x;
                half z = s.z - 4.0h * s.y + 6.0h * s.x;
                half w = 6.0h - x - y - z;
                return half4(x, y, z, w) * (1.0h / 6.0h);
            }

            // tricubic filterung ueber 8 statt 64 texture fetches (sigg/hadwiger trick,
            // 22.09.: vtk/slicer vergleich, siehe VOLUME_RENDERING_RESEARCH.md punkt 2).
            // je zwei benachbarte b-spline gewichte lassen sich zu einem einzigen
            // trilinearen hardware-fetch mit passendem offset zusammenfassen - pro achse
            // 4 gewichte -> 2 fetches, bei 3 achsen macht das aus 4*4*4=64 nur 2*2*2=8
            void SampleVoxelCubic(float3 uvw, out half3 color, out half intensity)
            {
                float3 coord = uvw * _VolumeSize - 0.5;
                float3 texelIndex = floor(coord);
                half3 f = half3(coord - texelIndex);

                half4 wx = CubicWeights(f.x);
                half4 wy = CubicWeights(f.y);
                half4 wz = CubicWeights(f.z);

                half gx0 = wx.x + wx.y, gx1 = wx.z + wx.w;
                half gy0 = wy.x + wy.y, gy1 = wy.z + wy.w;
                half gz0 = wz.x + wz.y, gz1 = wz.z + wz.w;

                float3 invSize = 1.0 / _VolumeSize;
                float hx0 = (texelIndex.x - 0.5 + wx.y / gx0) * invSize.x;
                float hx1 = (texelIndex.x + 1.5 + wx.w / gx1) * invSize.x;
                float hy0 = (texelIndex.y - 0.5 + wy.y / gy0) * invSize.y;
                float hy1 = (texelIndex.y + 1.5 + wy.w / gy1) * invSize.y;
                float hz0 = (texelIndex.z - 0.5 + wz.y / gz0) * invSize.z;
                float hz1 = (texelIndex.z + 1.5 + wz.w / gz1) * invSize.z;

                half3 c000, c100, c010, c110, c001, c101, c011, c111;
                half i000, i100, i010, i110, i001, i101, i011, i111;
                SampleVoxel(float3(hx0, hy0, hz0), c000, i000);
                SampleVoxel(float3(hx1, hy0, hz0), c100, i100);
                SampleVoxel(float3(hx0, hy1, hz0), c010, i010);
                SampleVoxel(float3(hx1, hy1, hz0), c110, i110);
                SampleVoxel(float3(hx0, hy0, hz1), c001, i001);
                SampleVoxel(float3(hx1, hy0, hz1), c101, i101);
                SampleVoxel(float3(hx0, hy1, hz1), c011, i011);
                SampleVoxel(float3(hx1, hy1, hz1), c111, i111);

                // erst x, dann y, dann z zusammenmischen - g0 ist das gewicht richtung
                // "niedrig" (hx0/hy0/hz0), g0+g1 ist immer 1 also reicht lerp mit g0
                half3 cx00 = lerp(c100, c000, gx0), cx10 = lerp(c110, c010, gx0);
                half3 cx01 = lerp(c101, c001, gx0), cx11 = lerp(c111, c011, gx0);
                half ix00 = lerp(i100, i000, gx0), ix10 = lerp(i110, i010, gx0);
                half ix01 = lerp(i101, i001, gx0), ix11 = lerp(i111, i011, gx0);

                half3 cxy0 = lerp(cx10, cx00, gy0), cxy1 = lerp(cx11, cx01, gy0);
                half ixy0 = lerp(ix10, ix00, gy0), ixy1 = lerp(ix11, ix01, gy0);

                color = lerp(cxy1, cxy0, gz0);
                intensity = lerp(ixy1, ixy0, gz0);
            }

            // haupt-sample fuers eigentliche signal - tricubic nur hier, nicht fuer
            // den gradienten (ApplyShading bleibt bei SampleIntensity/trilinear,
            // sonst waeren das 8x mehr fetches pro schritt nur fuers shading)
            void SampleVoxelMain(float3 uvw, out half3 color, out half intensity)
            {
                if (_TricubicFilter > 0.5) SampleVoxelCubic(uvw, color, intensity);
                else SampleVoxel(uvw, color, intensity);
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
                half ndotv = saturate(dot(normalOS, viewDirOS));

                // kopflampe sitzt an der kamera, licht und blickrichtung fallen
                // zusammen, der halbvektor fuers specular ist dadurch einfach ndotv.
                // vtk's presets nutzen echtes ambient/diffuse/specular statt nur
                // diffus wie vorher hier, das glanzlicht liest sich als feste
                // oberflaeche statt als rauschen (siehe VOLUME_RENDERING research)
                half diffuse = lerp(0.5h, 1.05h, ndotv);
                half specular = 0.35h * pow(ndotv, 24.0h);
                return saturate(color * diffuse + specular);
            }

            // billiger screen-space hash (keine textur/noise-map noetig) - streut den
            // start jedes strahls um einen zufaelligen bruchteil einer schrittweite,
            // bricht dadurch das feste "an jedem pixel exakt an derselben relativen
            // tiefe abgetastet"-muster auf, das als sichtbare schichten/ringe um
            // strukturen erscheint (STATUS.md 26.08., Punkt 2 wunschliste, standardtrick
            // gegen raymarch-banding, keine mehrkosten an samples)
            float RayJitter(float2 screenPos)
            {
                float2 p = frac(screenPos * float2(443.897, 441.423));
                p += dot(p, p.yx + 19.19);
                return frac((p.x + p.y) * p.x);
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
                float jitter = RayJitter(i.positionHCS.xy);
                float3 pos = rayOrigin + rayDir * (tNear + jitter * stepSize);

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
                        SampleVoxelMain(uvw, color, intensity);

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
                    bestColor = ApplyLabel(bestColor, bestUvw);
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
                    SampleVoxelMain(uvw, color, intensity);

                    if (intensity > _Threshold && intensity <= _ThresholdMax)
                    {
                        // 0 direkt ueber threshold, 1 beim maximum. smoothstep statt reinem
                        // quadrat (21.09.: vtk/slicer vergleich, siehe VOLUME_RENDERING
                        // research) - draengt rauschen nah der schwelle genauso weg, laesst
                        // mittlere werte aber nicht mehr so stark absacken wie das alte
                        // quadrat, naeher an vtk's stueckweise-linearen opacity-rampen
                        half shaped = saturate((intensity - _Threshold) / max(1.0 - _Threshold, 0.0001));
                        shaped = shaped * shaped * (3.0h - 2.0h * shaped);
                        half alpha = saturate(shaped * _Density * stepSize * 10.0);

                        color = ApplyLabel(color, uvw);
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
