Shader "Gobmine/Expedition Fluid URP"
{
    Properties
    {
        _BaseColor("Fluid Tint", Color) = (0.025,0.29,0.48,0.50)
        _Glow("Lava Emission", Float) = 0
        [Toggle] _ZWrite("Depth Write", Float) = 0
        _RippleScale("Ripple Frequency", Range(0.3,8)) = 1.65
        _RippleSpeed("Surface Movement", Range(0,5)) = 0.75
        _RippleStrength("Wave Highlights", Range(0,0.5)) = 0.20
        _FresnelStrength("Glancing Reflection", Range(0,1)) = 0.58
        _ShoreFoam("Rock Shoreline Highlight", Range(0,1)) = 0.36
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _Glow;
                float _ZWrite;
                float _RippleScale;
                float _RippleSpeed;
                float _RippleStrength;
                float _FresnelStrength;
                float _ShoreFoam;
            CBUFFER_END
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 world : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tint : COLOR;
                float fog : TEXCOORD2;
            };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.world = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.world);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.tint = input.color;
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float time = _Time.y * _RippleSpeed;
                float2 p = input.world.xz * _RippleScale;
                float s1 = sin(p.x * 1.37 + p.y * .47 + time);
                float s2 = cos(p.y * 1.11 - p.x * .61 - time * .73);
                float s3 = sin(p.x * .57 - p.y * 1.69 + time * .49);
                float flow = (s1 * s2 + s3) * .5;
                float3 waves = normalize(float3(-.18 * (s1 + s3), 1, -.18 * (s2 - s3)));
                float3 n = normalize(input.normalWS + (waves - float3(0,1,0)) * (1.8 * _RippleStrength));
                float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.world);
                float fresnel = pow(1 - saturate(abs(dot(n, viewDir))), 3);
                float lava = saturate(_Glow);
                float shore = saturate(input.tint.g) * _ShoreFoam;
                float shimmer = .5 + .5 * flow;
                float3 water = _BaseColor.rgb * (0.93 + 0.22 * shimmer)
                    + float3(.10,.18,.20) * fresnel * _FresnelStrength
                    + float3(.22,.36,.40) * shore * (.65 + .35 * shimmer);
                float3 molten = _BaseColor.rgb * (.78 + shimmer * (.40 + _RippleStrength)) * (1 + _Glow)
                    + float3(.35,.08,.009) * shore * .65;
                float3 color = lerp(water, molten, lava);
                float alpha = lerp(saturate(_BaseColor.a * (.78 + .26 * fresnel) *
                    lerp(.75, 1, saturate(input.tint.r))), saturate(_BaseColor.a), lava);
                return half4(MixFog(color, input.fog), alpha);
            }
            ENDHLSL
        }
    }
}
