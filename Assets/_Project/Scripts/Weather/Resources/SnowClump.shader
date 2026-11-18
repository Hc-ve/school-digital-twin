// 树冠/树干积雪团块专用：不透明、走场景光照。
// 【为什么必须新写一个】
//   现有 CampusUI/SnowGround 是半透明材质（Queue=Transparent + Blend SrcAlpha + 边缘渐隐），
//   拿它画"团块"会变成软边白气球（P43 三次调参都死在这条上）。
//   积雪团块必须：不透、有体积、只按噪声做"逐渐长出来"，不做 alpha 渐隐。
Shader "CampusUI/SnowClump"
{
    Properties
    {
        _SnowColor ("Snow Color", Color) = (0.90, 0.93, 0.97, 1)
        _ShadeColor ("Shade Color", Color) = (0.74, 0.80, 0.91, 1)
        _Grow ("Grow", Range(0, 1)) = 1
        _NoiseScale ("Noise Scale", Float) = 3.4
        _AmbientMul ("Ambient Mul", Float) = 1.25
        _SunMul ("Sun Mul", Float) = 0.45
        _BottomMul ("Bottom Mul", Range(0.2, 1)) = 0.38
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200
        // 雪是强散射体，正反两面都要看到；也避免低模半球因手写绕序错误被背面剔除（实测整棵树没雪）
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

            float4 _SnowColor;
            float4 _ShadeColor;
            float  _Grow;
            float  _NoiseScale;
            float  _AmbientMul;
            float  _SunMul;
            float  _BottomMul;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldN   : TEXCOORD1;
                float2 uv       : TEXCOORD4;
                SHADOW_COORDS(2)
                UNITY_FOG_COORDS(3)
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldN = normalize(UnityObjectToWorldNormal(v.normal));
                o.uv = v.uv;
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            // 值噪声：3 个八度，给雪面"颗粒 + 起伏"，避免塑料球感
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
                float3 n = normalize(i.worldN);

                // ① 主光：雪是强散射体，用半兰伯特压平（不要硬高光）
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float ndl = saturate(dot(n, L) * 0.5 + 0.5);
                float3 sun = _LightColor0.rgb * ndl * _SunMul;

                // ② 环境光：雪会把天空光再反射一轮，这里用球谐环境项
                float3 amb = ShadeSH9(float4(n, 1.0)) * _AmbientMul;

                float3 lit = amb + sun;

                // ③ 顶面白、侧面偏蓝灰（真实第 6 条），但**给亮度兜底**：
                //    阴雪天光弱，纯乘法会把雪压成"蓝泡泡"（实测用户截图）。
                float up = saturate(n.y * 1.15 - 0.05);
                float3 col = lerp(_ShadeColor.rgb * lit, _SnowColor.rgb * lit, up);
                col = max(col, _SnowColor.rgb * 0.78);

                // ④ 噪声细节：明度 ±6%
                float nz = vnoise(i.worldPos * _NoiseScale);
                col *= 0.94 + nz * 0.12;

                // ④b 体积感：dome 的 UV.y 就是环高（0=底 1=顶），底部压暗。
                //     不加这条，侧视看到的雪团上下一样亮 → 读成"贴片/云絮"（用户截图实证）。
                float aoBottom = lerp(_BottomMul, 1.0, saturate(i.uv.y));
                col *= aoBottom;

                // ⑤ 生长：用抖动阈值做"积雪逐渐盖上来"，不做 alpha
                //    _Grow=1 → 全部保留；_Grow=0 → 基本全裁掉
                clip(_Grow * 1.20 - (1.0 - nz) * 0.18 - 0.002);

                UNITY_APPLY_FOG(i.fogCoord, col);
                return fixed4(col, 1.0);
            }
            ENDCG
        }

        // ══════════════════════════════════════════════════════════════════
        // 【P44 · 2026-11-18】ForwardAdd：让实时点光/聚光（P44 路灯）照亮树枝积雪。
        //
        // ── 为什么必须有（主理人报障"晚上雪天树依旧是黑的"，经实拍调研）──
        // alamy《Winter forest, road and trees covered with snow · night》等实拍：
        // 现实里雪夜路灯旁的积雪树枝发白发亮，只有远离灯的树才是黑剪影。
        // 本 shader 原先只有 ForwardBase —— 树雪只吃月光/球谐环境光，
        // 灯旁照样黑。加法 pass 与 base 用同一套噪声 mask / clip / 调色，
        // 保证"月光照的雪"和"路灯照的雪"是同一种雪。
        // ══════════════════════════════════════════════════════════════════
        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }

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

            float4 _SnowColor;
            float4 _ShadeColor;
            float  _Grow;
            float  _NoiseScale;
            float  _AmbientMul;
            float  _SunMul;
            float  _BottomMul;

            struct appdataAdd
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            struct v2fAdd
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldN   : TEXCOORD1;
                float2 uv       : TEXCOORD4;
                SHADOW_COORDS(2)
                UNITY_FOG_COORDS(3)
            };

            v2fAdd vertAdd (appdataAdd v)
            {
                v2fAdd o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldN = normalize(UnityObjectToWorldNormal(v.normal));
                o.uv = v.uv;
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            // 值噪声：与 base 同一份实现（独立的 CGPROGRAM 编译单元，各自声明）
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

            fixed4 fragAdd (v2fAdd i) : SV_Target
            {
                float3 n = normalize(i.worldN);

                // 与 base 同一套"哪里有雪"判据——clip 不一致会露馅（加法光溢出到该裁掉的区域）
                float nz = vnoise(i.worldPos * _NoiseScale);
                clip(_Grow * 1.20 - (1.0 - nz) * 0.18 - 0.002);

                // 【ForwardAdd 核心】本 pass 收到的是"这一盏灯"：
                // 点/聚光按像素重算方向；衰减含距离与（fullshadows 下的）实时阴影。
                #ifdef USING_DIRECTIONAL_LIGHT
                    float3 L = normalize(_WorldSpaceLightPos0.xyz);
                #else
                    float3 L = normalize(_WorldSpaceLightPos0.xyz - i.worldPos);
                #endif
                // 半兰伯特与 base 同款：雪是强散射体，不要硬高光
                float ndl = saturate(dot(n, L) * 0.5 + 0.5);
                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                float3 direct = _LightColor0.rgb * ndl * _SunMul * atten;

                float up = saturate(n.y * 1.15 - 0.05);
                float3 col = lerp(_ShadeColor.rgb * direct, _SnowColor.rgb * direct, up);
                col *= 0.94 + nz * 0.12;
                float aoBottom = lerp(_BottomMul, 1.0, saturate(i.uv.y));
                col *= aoBottom;

                UNITY_APPLY_FOG(i.fogCoord, col);
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
