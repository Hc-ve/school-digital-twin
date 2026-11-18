// 雨雪粒子专用：可被实时灯（P44 路灯）照亮的透明粒子着色器。
//
// ══════════════════════════════════════════════════════════════════
// 【P44 · 2026-11-18】为什么必须有这张 shader（主理人报障"路灯下看不到雨雪粒子"）
//
// 雨雪粒子原先用 Sprites/Default——它 SubShader 里写着 `Lighting Off`，
// 是纯自发光材质，结构上就收不到任何实时灯：白天看不出问题，
// 夜里粒子在哪都是同一个亮度，路灯锥里"应该亮成一片"的雪粒完全不存在。
//
// 调研结论（alamy《Snow falling in cones of light under streetlights》等实拍）：
// 现实里飘落的雪/雨在灯锥里是**一大片亮白粒子**（雪粒各向同性散射，
// 亮度 ≈ 灯在粒子位置的光强），这是雪夜路灯最有辨识度的画面；
// 离开光锥粒子立刻隐回黑暗。
//
// ── 两个 pass 各干什么 ──────────────────────────────────────────
//   · ForwardBase：与 Sprites/Default **完全同观感**（tint × 贴图，
//     不做雾、不吃方向光）——白天零回归，粒子白天还是老样子。
//   · ForwardAdd：按"各向同性散射"提亮：亮度 = 灯色 × 距离/锥角衰减，
//     **没有 NdotL 方向项**（雪粒不是平面，没有朝向可言；粒子 billboard
//     的法线永远朝相机，拿它算 NdotL 是纯错误）。衰减用
//     UNITY_LIGHT_ATTENUATION——Spot 的锥角外衰减天然把"只在光锥里亮"
//     做对了，不需要任何 C# 侧判断。
// ══════════════════════════════════════════════════════════════════
Shader "CampusUI/PrecipitationLit"
{
    Properties
    {
        _MainTex ("Particle Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
        }

        Cull Off
        ZWrite Off

        // ---- 基础层：tint × 贴图，与 Sprites/Default 同观感（白天零回归）----
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }

            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                fixed4 col   : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.col = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                return i.col * t;
            }
            ENDCG
        }

        // ---- 加法层：灯锥内的散射提亮（夜里路灯下粒子亮起来的入口）----
        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }

            Blend One One

            CGPROGRAM
            #pragma vertex vertAdd
            #pragma fragment fragAdd
            // 不带 fullshadows：雪花是几毫米的散射体，接阴影既贵又没意义，
            // 只要距离/锥角衰减（atten）就足够做出"光锥里亮、锥外隐"。
            #pragma multi_compile_fwdadd
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            // 【P44】路灯"光锥显雪"：由 ExteriorLampAnchors 每次预算刷新时写入的
            // 活跃灯头表（xyz=灯头世界坐标，w=该灯强度）。粒子片元按"到灯距离"
            // 解析算散射提亮——现实中光柱贯穿灯头上下（alamy 实拍），而路灯 Spot
            // 的锥只罩灯头下方 3 米，粒子下落的大半程无光可接；解析距离场让
            // "灯周围的粒子幕"整段亮起来，不需要任何额外光源/层。
            #define P44_GLOW_MAX 16
            float4 _P44Glow[P44_GLOW_MAX];
            float  _P44GlowCount;

            struct appdataAdd
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };

            struct v2fAdd
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                fixed4 col   : COLOR;
                float3 wpos  : TEXCOORD1;
            };

            v2fAdd vertAdd(appdataAdd v)
            {
                v2fAdd o;
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityWorldToClipPos(o.wpos);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.col = v.color * _Color;
                return o;
            }

            fixed4 fragAdd(v2fAdd i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                float a = t.a * i.col.a;
                if (a <= 0.01) discard;

                float3 lit;
                #ifdef USING_DIRECTIONAL_LIGHT
                    // 月光（平行光也会走一次 ForwardAdd）：低系数兜底，
                    // 让灯与灯之间的粒子不至于完全隐身，又不会提亮过头。
                    lit = _LightColor0.rgb * 0.35;
                #else
                    // 点/聚光：atten = 距离衰减 × 锥角（spot 边缘）衰减。
                    // 无方向项——雪粒各向同性散射，没有"朝向"可言。
                    UNITY_LIGHT_ATTENUATION(atten, i, i.wpos);
                    lit = _LightColor0.rgb * atten;
                #endif

                // 【P44 光锥显雪】到各活跃灯头的解析距离场：1/(1+d²·k) 的柔衰减，
                // 让灯头上下大范围内的粒子都亮（对应实拍里贯穿头尾的光柱雪幕）。
                // 粒子像素极少，16 次循环开销可忽略。
                float3 glow = float3(0, 0, 0);
                int gcount = (int)_P44GlowCount;
                for (int gi = 0; gi < P44_GLOW_MAX; gi++)
                {
                    if (gi >= gcount) break;
                    float4 lamp = _P44Glow[gi];
                    float3 d = lamp.xyz - i.wpos;
                    float d2 = dot(d, d);
                    // 系数取到 1.35/0.10：灯旁雪粒亮度可到 2~3（>1），让 [PPS] Bloom
                    // 把它们拾成"发光光斑"——实拍里雪粒在灯下就是这种过曝小亮斑。
                    glow += lamp.w * (1.35 / (1.0 + d2 * 0.10));
                }
                lit += glow;

                float3 col = i.col.rgb * t.rgb * lit;
                // 加法粒子不做雾：粒子只有几毫米大，远处本来就缩成一个像素，
                // 灯锥里的亮粒在雾里反而该亮（现实里远处灯柱的雪幕也看得见）。
                return fixed4(col * a, 1.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
