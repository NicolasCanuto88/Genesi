// ShieldFilm - pellicola degli scudi del Quartermaster (Rev BV-b - workshop Quartermaster, Q103-a).
//
// URP, unlit, trasparente additivo. Pensato per stare come materiale IN PIU' sui renderer del corpo
// (PlayerShieldFilm): Unity lo disegna sopra l'ultima sotto-mesh. Funziona su MeshRenderer e
// SkinnedMeshRenderer. Da Rev BV-c veste anche la sfera della Bubble Shield (con Cull Off).
//
// Aspetto:
//   - leggero gonfiamento lungo le normali (_Inflate, metri), cosi' il film sta appena fuori dal corpo;
//   - bordo fresnel (_RimPower, _RimStrength): piu' luminoso dove la superficie e' di taglio;
//   - rumore che scorre verso l'alto nello spazio dell'oggetto (_NoiseScale, _NoiseSpeed,
//     _NoiseStrength), cosi' la trama resta attaccata al corpo mentre si muove;
//   - un velo uniforme (_Fill);
//   - _Intensity (0-1) per la dissolvenza, scritta da codice con un MaterialPropertyBlock.
// Il dot fra normale e vista e' preso in valore assoluto: con Cull Off anche le facce interne hanno
// il loro bordo (la bolla vista da dentro).
Shader "SpaceSurvivor/Shield Film"
{
    Properties
    {
        [HDR] _BaseColor ("Color", Color) = (0.35, 0.75, 1.0, 1.0)
        _Intensity ("Intensity", Range(0, 1)) = 1
        _Fill ("Fill", Range(0, 1)) = 0.06
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5
        _RimStrength ("Rim Strength", Range(0, 4)) = 1.4
        _NoiseScale ("Noise Scale", Float) = 7
        _NoiseSpeed ("Noise Speed", Float) = 0.5
        _NoiseStrength ("Noise Strength", Range(0, 1)) = 0.35
        _Inflate ("Inflate (m)", Range(0, 0.1)) = 0.03
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ShieldFilm"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Intensity;
                half _Fill;
                half _RimPower;
                half _RimStrength;
                float _NoiseScale;
                float _NoiseSpeed;
                half _NoiseStrength;
                float _Inflate;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
            };

            // Hash 3D -> [0, 1) senza texture.
            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            // Value noise 3D con interpolazione morbida.
            float ValueNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                float3 u = f * f * (3.0 - 2.0 * f);

                float n000 = Hash31(i + float3(0.0, 0.0, 0.0));
                float n100 = Hash31(i + float3(1.0, 0.0, 0.0));
                float n010 = Hash31(i + float3(0.0, 1.0, 0.0));
                float n110 = Hash31(i + float3(1.0, 1.0, 0.0));
                float n001 = Hash31(i + float3(0.0, 0.0, 1.0));
                float n101 = Hash31(i + float3(1.0, 0.0, 1.0));
                float n011 = Hash31(i + float3(0.0, 1.0, 1.0));
                float n111 = Hash31(i + float3(1.0, 1.0, 1.0));

                float x00 = lerp(n000, n100, u.x);
                float x10 = lerp(n010, n110, u.x);
                float x01 = lerp(n001, n101, u.x);
                float x11 = lerp(n011, n111, u.x);
                float y0 = lerp(x00, x10, u.y);
                float y1 = lerp(x01, x11, u.y);
                return lerp(y0, y1, u.z);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz) + normalWS * _Inflate;

                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS = normalWS;
                output.positionOS = input.positionOS.xyz;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float3 viewDirWS = normalize(GetCameraPositionWS() - input.positionWS);
                float facing = abs(dot(normalWS, viewDirWS));

                float rim = pow(saturate(1.0 - facing), _RimPower) * _RimStrength;

                float3 noisePos = input.positionOS * _NoiseScale + float3(0.0, -_Time.y * _NoiseSpeed, 0.0);
                float noise = ValueNoise(noisePos) * _NoiseStrength;

                float alpha = (_Fill + rim + noise * (0.5 + rim)) * _Intensity * _BaseColor.a;
                return half4(_BaseColor.rgb, saturate(alpha));
            }
            ENDHLSL
        }
    }

    FallBack Off
}
