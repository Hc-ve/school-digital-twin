// 雨雪粒子专用：可被路灯"光锥显雪"照亮的透明粒子着色器（P47 单 pass 版）。
//
// ══════════════════════════════════════════════════════════════════
// 【P44 · 2026-11-18】为什么必须有这张 shader（主理人报障"路灯下看不到雨雪粒子"）
//
// 雨雪粒子原先用 Sprites/Default——它 SubShader 里写着 `Lighting Off`，
// 是纯自发光材质，结构上就收不到任何实时灯：夜里粒子在哪都是同一个亮度，
// 路灯锥里"应该亮成一片"的雪粒完全不存在。
//
// ══════════════════════════════════════════════════════════════════
// 【P47 · 2026-11-19 · 为什么删掉 ForwardAdd 改成单 pass】
//
// 上一版是 ForwardBase + ForwardAdd 两 pass：灯的提亮走 ForwardAdd。
// 但内置管线的 ForwardAdd 会**逐灯重复渲染整个对象**——雨雪粒子是
// 一个 110m 大包围盒的渲染器，14 盏活跃路灯全都打得中它的包围盒，
// 于是整个粒子系统（密度上调后 ~15 万 quad）要按灯数画 14 遍，
// 帧率直接打死。
//
// 解法：提亮全部并入 **单次的 ForwardBase**，用 ExteriorLampAnchors
// 每次预算刷新写入的 `_P44Glow` 全局距离场（xyz=活跃灯头坐标，w=强度）
// 在片元里解析算"到灯距离"的散射提亮——渲染次数恒为 1 次，与灯数无关。
// 这在物理上也更对：雪粒是各向同性散射体（毫米级，没有朝向），
// 亮度 ≈ 灯在粒子位置的光强，本来就不该有 NdotL 方向项。
// 白天 _P44GlowCount=0 → glow 恒 0 → 与 Sprites/Default 观感零回归。
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

        // ---- 单 pass：tint × 贴图（白天与 Sprites/Default 同观感）+ 路灯距离场提亮 ----
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

            // 【P44 光锥显雪】活跃灯头表：ExteriorLampAnchors 每次预算刷新写入
            // （xyz=灯头世界坐标，w=该灯强度）。白天/拉闸时 Count=0 → glow 恒 0。
            #define P44_GLOW_MAX 16
            float4 _P44Glow[P44_GLOW_MAX];
            float  _P44GlowCount;

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
                float3 wpos  : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityWorldToClipPos(o.wpos);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.col = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                fixed4 c = i.col * t;

                // 【光锥显雪】到各活跃灯头的解析距离场：1/(1+d²·k) 柔衰减。
                // 【P50 2026-11-19 主理人裁决：丁达尔光柱"适度增强、可控不过曝"】
                // 系数 0.14 → 0.07：灯旁(d≈3m)提亮 ≈0.93→1.23(+32%)、d≈5m ≈0.44→0.73(+66%)，
                // d≈10m 仍衰减到 0.25 —— 光柱明显但不全场泛白。灯球 emission 已回调到 3.0、
                // Spot 强度回调过，实测室内/灯球不过曝才定此值。
                float3 glow = float3(0, 0, 0);
                int gcount = (int)_P44GlowCount;
                for (int gi = 0; gi < P44_GLOW_MAX; gi++)
                {
                    if (gi >= gcount) break;
                    float4 lamp = _P44Glow[gi];
                    float3 d = lamp.xyz - i.wpos;
                    glow += lamp.w * (1.0 / (1.0 + dot(d, d) * 0.07));
                }
                // 提亮只加在雪粒自己的覆盖范围内（乘 alpha），粒子间隙不发光
                c.rgb += glow * c.a;

                return c;
            }
            ENDCG
        }
    }

    FallBack Off
}
