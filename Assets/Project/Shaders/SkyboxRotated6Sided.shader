// SkyboxRotated6Sided.shader
// Space Survivor — Milestone 3, Blocco 3, Fase 2, Sottofase 2c (v3).
//
// Clone del Skybox/6 Sided built-in di Unity, con rotazione 3D arbitraria
// applicata al vertex object-space via matrice globale _SkyboxRotationMatrix
// settata ogni frame da ShipSkyboxRotator.
//
// PERCHÉ QUESTA VERSIONE (sostituisce v1 sfera equirect e v2 sfera cubemap):
//   Le versioni precedenti tentavano di replicare il pattern di
//   ExternalWorldFollower con una sfera invertita rotante. Overengineering:
//   Unity ha già l'infrastruttura Skybox Material che disegna 6 quad ai
//   bordi del frustum, senza sphere tessellation e senza layer separati.
//   Il fix corretto è aggiungere una matrice di rotazione al vertex shader
//   dello Skybox/6 Sided standard.
//
// USO:
//   1. Crea un Material che usa questo shader (categoria "SpaceSurvivor").
//   2. Assegna le 6 texture (le stesse del vecchio Skybox/6 Sided).
//   3. Assegna il material a Lighting Settings → Environment → Skybox Material.
//   4. Attacca ShipSkyboxRotator a un GameObject della scena. Ogni frame
//      chiama Shader.SetGlobalMatrix("_SkyboxRotationMatrix", ...).
//
// COME FUNZIONA LA ROTAZIONE:
//   Ogni pass del Skybox/6 Sided disegna un quad ai bordi del frustum
//   corrispondente a una face del cubo skybox. Il vertex shader riceve
//   positionOS = posizione del vertex sul quad. Applicando
//     rotatedPos = mul((float3x3)_SkyboxRotationMatrix, positionOS)
//   ruotiamo il cubo attorno alla camera (che è al centro dell'origine).
//   Le texture restano ancorate alle face del cubo, quindi ruotano
//   rigidamente insieme. Yaw+pitch+roll arbitrari senza distorsioni.
//
// STATO DEFAULT: se _SkyboxRotationMatrix non è mai stato settato, Unity
// lo tratta come zero-matrix, che RUOLEREBBE tutto a zero. ShipSkyboxRotator
// forza identity in OnEnable per evitare skybox nera al primo frame.
//
// COMPATIBILITÀ: URP 14+ (Unity 6.3).

Shader "SpaceSurvivor/SkyboxRotated6Sided"
{
    Properties
    {
        _Tint ("Tint Color", Color) = (0.5, 0.5, 0.5, 0.5)
        [Gamma] _Exposure ("Exposure", Range(0, 8)) = 1.0
        [NoScaleOffset] _FrontTex ("Front [+Z]", 2D) = "grey" {}
        [NoScaleOffset] _BackTex  ("Back [-Z]",  2D) = "grey" {}
        [NoScaleOffset] _LeftTex  ("Left [+X]",  2D) = "grey" {}
        [NoScaleOffset] _RightTex ("Right [-X]", 2D) = "grey" {}
        [NoScaleOffset] _UpTex    ("Up [+Y]",    2D) = "grey" {}
        [NoScaleOffset] _DownTex  ("Down [-Y]",  2D) = "grey" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off
        ZWrite Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Matrice globale settata da ShipSkyboxRotator ogni frame. Fuori dal
        // CBUFFER UnityPerMaterial perché è globale (Shader.SetGlobalMatrix),
        // non per-material.
        float4x4 _SkyboxRotationMatrix;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionHCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        CBUFFER_START(UnityPerMaterial)
            half4 _Tint;
            half _Exposure;
        CBUFFER_END

        Varyings SkyboxVert(Attributes IN)
        {
            Varyings OUT;
            // Ruota positionOS PRIMA della trasformazione in clip space.
            // Il quad skybox è centrato sull'origine object-space e viene
            // disegnato ai bordi del frustum; ruotarlo attorno all'origine
            // equivale a ruotare il cubo skybox attorno alla camera.
            float3 rotated = mul((float3x3)_SkyboxRotationMatrix, IN.positionOS.xyz);
            OUT.positionHCS = TransformObjectToHClip(rotated);
            OUT.uv = IN.uv;
            return OUT;
        }

        // Fragment condiviso: campiona la texture della face corrente e
        // applica Tint × 2 × Exposure (compatibile col comportamento del
        // Skybox/6 Sided built-in di Unity in linear color space).
        half4 SkyboxFrag(Varyings IN, TEXTURE2D_PARAM(tex, samplerTex))
        {
            half4 c = SAMPLE_TEXTURE2D(tex, samplerTex, IN.uv);
            c.rgb *= _Tint.rgb * 2.0 * _Exposure;
            return half4(c.rgb, 1.0);
        }
        ENDHLSL

        Pass
        {
            Name "FrontPass"
            HLSLPROGRAM
            #pragma vertex SkyboxVert
            #pragma fragment frag
            TEXTURE2D(_FrontTex); SAMPLER(sampler_FrontTex);
            half4 frag(Varyings IN) : SV_Target
            {
                return SkyboxFrag(IN, TEXTURE2D_ARGS(_FrontTex, sampler_FrontTex));
            }
            ENDHLSL
        }

        Pass
        {
            Name "BackPass"
            HLSLPROGRAM
            #pragma vertex SkyboxVert
            #pragma fragment frag
            TEXTURE2D(_BackTex); SAMPLER(sampler_BackTex);
            half4 frag(Varyings IN) : SV_Target
            {
                return SkyboxFrag(IN, TEXTURE2D_ARGS(_BackTex, sampler_BackTex));
            }
            ENDHLSL
        }

        Pass
        {
            Name "LeftPass"
            HLSLPROGRAM
            #pragma vertex SkyboxVert
            #pragma fragment frag
            TEXTURE2D(_LeftTex); SAMPLER(sampler_LeftTex);
            half4 frag(Varyings IN) : SV_Target
            {
                return SkyboxFrag(IN, TEXTURE2D_ARGS(_LeftTex, sampler_LeftTex));
            }
            ENDHLSL
        }

        Pass
        {
            Name "RightPass"
            HLSLPROGRAM
            #pragma vertex SkyboxVert
            #pragma fragment frag
            TEXTURE2D(_RightTex); SAMPLER(sampler_RightTex);
            half4 frag(Varyings IN) : SV_Target
            {
                return SkyboxFrag(IN, TEXTURE2D_ARGS(_RightTex, sampler_RightTex));
            }
            ENDHLSL
        }

        Pass
        {
            Name "UpPass"
            HLSLPROGRAM
            #pragma vertex SkyboxVert
            #pragma fragment frag
            TEXTURE2D(_UpTex); SAMPLER(sampler_UpTex);
            half4 frag(Varyings IN) : SV_Target
            {
                return SkyboxFrag(IN, TEXTURE2D_ARGS(_UpTex, sampler_UpTex));
            }
            ENDHLSL
        }

        Pass
        {
            Name "DownPass"
            HLSLPROGRAM
            #pragma vertex SkyboxVert
            #pragma fragment frag
            TEXTURE2D(_DownTex); SAMPLER(sampler_DownTex);
            half4 frag(Varyings IN) : SV_Target
            {
                return SkyboxFrag(IN, TEXTURE2D_ARGS(_DownTex, sampler_DownTex));
            }
            ENDHLSL
        }
    }

    FallBack Off
}
