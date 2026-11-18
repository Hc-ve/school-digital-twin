// 【P43 · v5 转向】树冠"雪被"：不是一个个雪团，而是一层连续的外壳。
// 调研结论（Unity 做树上雪的主流路线）：雪是"覆盖层"，靠朝向(法线·up)决定哪里挂雪，
// 而不是在叶子上摆独立球体。摆球体的结果就是"一树饺子"。
// 这张壳按"法线朝上程度 + 世界噪声"做 alpha 裁形：冠顶整片挂雪，侧缘破碎渐变，下缘露叶。
Shader "SchoolTwin/SnowCanopy"
{
    Properties
    {
        _SnowColor ("雪面颜色", Color) = (0.88, 0.92, 0.97, 1)
        _ShadeColor ("雪影颜色", Color) = (0.62, 0.71, 0.85, 1)
        _Coverage ("覆盖强度(0-1)", Range(0,1)) = 0.7
        _NoiseScale ("噪声尺度", Float) = 1.6
        _AlphaCut ("裁形阈值", Range(0,1)) = 0.42
        _UpBias ("朝上权重", Float) = 1.35
        _EdgeNoise ("边缘破碎强度", Range(0,1)) = 0.45
        _BottomMul ("底部压暗", Range(0,1)) = 0.55
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            float4 _SnowColor, _ShadeColor;
            float _Coverage, _NoiseScale, _AlphaCut, _UpBias, _EdgeNoise, _BottomMul;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 wn : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            float hash31(float3 p)
            {
                p = frac(p * 0.3183099 + float3(0.11, 0.37, 0.71));
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }
            float vnoise(float3 x)
            {
                float3 i = floor(x), f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash31(i);
                float n100 = hash31(i + float3(1,0,0));
                float n010 = hash31(i + float3(0,1,0));
                float n110 = hash31(i + float3(1,1,0));
                float n001 = hash31(i + float3(0,0,1));
                float n101 = hash31(i + float3(1,0,1));
                float n011 = hash31(i + float3(0,1,1));
                float n111 = hash31(i + float3(1,1,1));
                return lerp(lerp(lerp(n000,n100,f.x), lerp(n010,n110,f.x), f.y),
                            lerp(lerp(n001,n101,f.x), lerp(n011,n111,f.x), f.y), f.z);
            }

            v2f vert (appdata v)
            {
                v2f o;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wpos = wp;
                o.wn = normalize(mul((float3x3)unity_ObjectToWorld, v.normal));
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.wn);
                float up = saturate(n.y * _UpBias);

                // 噪声：一片雪被不可能边缘光滑，必须有"挂不住雪"的缺口
                float nz = vnoise(i.wpos * _NoiseScale);
                float nz2 = vnoise(i.wpos * _NoiseScale * 3.1 + 17.0);
                // 实测：up*coverage + 噪声加法，会让整个冠顶连成一张白布。
                // 改成"朝上程度允许挂雪 × 噪声斑块决定挂哪"，雪才是一簇一簇的。
                float mask = up * _Coverage * (0.30 + 1.55 * lerp(nz, nz2, 0.35));
                clip(mask - _AlphaCut);

                // 雪的明暗：顶面受天光，斜面/下缘压暗，靠这个读出"厚度"
                float lit = saturate(up * 1.15 + 0.10);
                float3 col = lerp(_ShadeColor.rgb, _SnowColor.rgb, lit);
                float amb = saturate(0.55 + 0.45 * ShadeSH9(half4(n, 1)).x);
                col *= lerp(_BottomMul, 1.0, amb);
                col = max(col, _SnowColor.rgb * 0.72);
                return fixed4(col, 1);
            }
            ENDCG
        }

        // ══════════════════════════════════════════════════════════════════
        // 【P44 · 2026-11-18】ForwardAdd：路灯照亮树冠雪被（与 SnowClump 同款修复）。
        // 实拍依据：雪夜路灯旁的树冠积雪发白发亮；base pass 连方向光都不吃，
        // 只有球谐环境色，灯旁照样黑。加法 pass 复用同一套 mask/clip 与色调。
        // ══════════════════════════════════════════════════════════════════
        Pass
        {
            Tags { "LightMode"="ForwardAdd" }
            Blend One One
            ZWrite Off
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vertAdd
            #pragma fragment fragAdd
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            float4 _SnowColor, _ShadeColor;
            float _Coverage, _NoiseScale, _AlphaCut, _UpBias, _EdgeNoise, _BottomMul;

            struct appdataAdd
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };
            struct v2fAdd
            {
                float4 pos : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 wn : TEXCOORD1;
                float2 uv : TEXCOORD2;
                SHADOW_COORDS(3)
                UNITY_FOG_COORDS(4)
            };

            float hash31(float3 p)
            {
                p = frac(p * 0.3183099 + float3(0.11, 0.37, 0.71));
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }
            float vnoise(float3 x)
            {
                float3 i = floor(x), f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash31(i);
                float n100 = hash31(i + float3(1,0,0));
                float n010 = hash31(i + float3(0,1,0));
                float n110 = hash31(i + float3(1,1,0));
                float n001 = hash31(i + float3(0,0,1));
                float n101 = hash31(i + float3(1,0,1));
                float n011 = hash31(i + float3(0,1,1));
                float n111 = hash31(i + float3(1,1,1));
                return lerp(lerp(lerp(n000,n100,f.x), lerp(n010,n110,f.x), f.y),
                            lerp(lerp(n001,n101,f.x), lerp(n011,n111,f.x), f.y), f.z);
            }

            v2fAdd vertAdd (appdataAdd v)
            {
                v2fAdd o;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wpos = wp;
                o.wn = normalize(mul((float3x3)unity_ObjectToWorld, v.normal));
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                o.uv = v.uv;
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 fragAdd (v2fAdd i) : SV_Target
            {
                float3 n = normalize(i.wn);
                float up = saturate(n.y * _UpBias);

                // mask/clip 与 base 同口径：加法光不许溢出到该裁掉的缺口
                float nz = vnoise(i.wpos * _NoiseScale);
                float nz2 = vnoise(i.wpos * _NoiseScale * 3.1 + 17.0);
                float mask = up * _Coverage * (0.30 + 1.55 * lerp(nz, nz2, 0.35));
                clip(mask - _AlphaCut);

                #ifdef USING_DIRECTIONAL_LIGHT
                    float3 L = normalize(_WorldSpaceLightPos0.xyz);
                #else
                    float3 L = normalize(_WorldSpaceLightPos0.xyz - i.wpos);
                #endif
                float ndl = saturate(dot(n, L));
                UNITY_LIGHT_ATTENUATION(atten, i, i.wpos);
                float3 direct = _LightColor0.rgb * ndl * atten;

                float lit = saturate(up * 1.15 + 0.10);
                float3 col = lerp(_ShadeColor.rgb, _SnowColor.rgb, lit) * direct;

                UNITY_APPLY_FOG(i.fogCoord, col);
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
