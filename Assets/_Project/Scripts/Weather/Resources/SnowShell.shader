// 叶面雪壳：把真实的树叶几何沿法线抬 8cm，用同一张叶纹理的 alpha 裁形，
// 再按世界噪声做"斑块保留"——雪就贴在叶子表面，永远不会浮成气球。
// 【为什么必须要这一层】
//   只做离散雪团时，团块埋在叶簇里，侧视几乎看不见（v4 第二版实测：150 团/树仍只见几点白）。
//   参考图的读法是"叶簇上表面结了一层雪"，本质是**覆盖**，不是**漂浮的球**。
//   这层和雪团是互补关系：壳负责面积与轮廓，团负责厚度与凸起。
Shader "CampusUI/SnowShell"
{
    Properties
    {
        _MainTex ("Leaf Alpha", 2D) = "white" {}
        _SnowColor ("Snow Color", Color) = (0.94, 0.96, 1.0, 1)
        _ShadeColor ("Shade Color", Color) = (0.72, 0.78, 0.88, 1)
        _Coverage ("Coverage", Range(0, 1)) = 1
        _NoiseScale ("Noise Scale", Float) = 0.60
        _AlphaCut ("Leaf Alpha Cut", Range(0, 1)) = 0.22
        _AmbientMul ("Ambient Mul", Float) = 1.15
        _SunMul ("Sun Mul", Float) = 0.40
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200
        // 双面：叶卡本身 Cull Off，壳跟着双面才不会出现"只在一半叶子上有雪"
        Cull Off

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _SnowColor;
            float4 _ShadeColor;
            float  _Coverage;
            float  _NoiseScale;
            float  _AlphaCut;
            float  _AmbientMul;
            float  _SunMul;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldN   : TEXCOORD1;
                float2 uv       : TEXCOORD2;
                SHADOW_COORDS(3)
                UNITY_FOG_COORDS(4)
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldN = normalize(UnityObjectToWorldNormal(v.normal));
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            float hash3 (float3 p)
            {
                return frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453);
            }

            float vnoise (float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash3(i + float3(0, 0, 0));
                float n100 = hash3(i + float3(1, 0, 0));
                float n010 = hash3(i + float3(0, 1, 0));
                float n110 = hash3(i + float3(1, 1, 0));
                float n001 = hash3(i + float3(0, 0, 1));
                float n101 = hash3(i + float3(1, 0, 1));
                float n011 = hash3(i + float3(0, 1, 1));
                float n111 = hash3(i + float3(1, 1, 1));
                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);
                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);
                return lerp(nxy0, nxy1, f.z);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // ① 用叶纹理自己的 alpha 裁形 —— 雪的形状 = 叶的形状
                float leafA = tex2D(_MainTex, i.uv).a;
                clip(leafA - _AlphaCut);

                // ② 斑块保留：世界空间噪声阈值，雪量越少露出的叶子越多
                float nz = vnoise(i.worldPos * _NoiseScale);
                clip(nz - (1.0 - _Coverage) * 0.85);

                float3 n = normalize(i.worldN);
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float ndl = saturate(dot(n, L) * 0.5 + 0.5);
                float3 sun = _LightColor0.rgb * ndl * _SunMul;
                float3 amb = ShadeSH9(float4(n, 1.0)) * _AmbientMul;

                float up = saturate(n.y * 0.65 + 0.35);
                float3 col = lerp(_ShadeColor.rgb * (amb + sun), _SnowColor.rgb * (amb + sun), up);
                col = max(col, _SnowColor.rgb * 0.86);
                col *= 0.95 + nz * 0.10;

                UNITY_APPLY_FOG(i.fogCoord, col);
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
