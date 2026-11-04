// 地面积雪层（**内置管线 Built-in RP 版**，由 URP 版按队员文档 §2.2 对照表改写）。
//
// 改写对照（§2.2）：
//   LightMode   UniversalForward      → ForwardBase
//   include     URP Core/Lighting     → UnityCG.cginc + AutoLight.cginc
//   GetMainLight+阴影                 → _LightColor0 + UNITY_LIGHT_ATTENUATION
//   SampleSH                          → ShadeSH9(float4(normal,1))
//   MixFog / ComputeFogFactor         → UNITY_APPLY_FOG（宏内部算，不需要自己算 factor）
//   TEXTURE2D / SAMPLER / SAMPLE_*    → sampler2D / tex2D
//   TransformObjectToWorld*           → UnityWorldToClipPos / UnityObjectToWorldNormal
//
// 原来针对"雪太假 / 看着发虚"的三条修正**全部保留**（这是本 Shader 的核心价值）：
//   1) 受主光 Lambert + 球谐环境光 + 环境提亮下限，不再是 Sprites/Default 的灰绿平涂
//   2) 覆盖率 alpha 改实心，只有最外圈淡出；噪声改存 RGB，只做轻微扰动
//   3) 雪面严格按地形平铺（几何位移由 C# 的 DrapeSnow 完成），不叠加程序化雪堆
//
// ⚠️ 内外管线的一处**语义差异**，必须记下来：
//   URP 的 shadowAttenuation 在没开阴影变体时返回 1.0；
//   内置管线这边 UNITY_LIGHT_ATTENUATION 也返回 1.0，但它**要求 SHADOW_COORDS /
//   TRANSFER_SHADOW 成对出现**，漏一个会编译错或阴影全黑。下面按标准 ForwardBase 补齐。
//
// ⚠️ 内置管线的一个硬限制：ForwardBase 只承载**一盏**最亮的平行光。
//   本工程场景里 68 盏 Area 灯全是 Baked，不参与实时直射，所以这条限制不构成影响；
//   但如果以后有人把 Area 灯改成 Realtime，雪面不会受它们照亮 —— 那属于预期行为。
Shader "CampusUI/SnowGround"
{
    Properties
    {
        _SnowTex    ("Snow Coverage (A)", 2D) = "white" {}
        _SnowColor  ("Snow Tint", Color) = (0.92, 0.95, 1.0, 1)
        _SnowAmount ("Snow Amount", Range(0, 1)) = 0
        _SkyColor   ("Sky Tint", Color) = (0.74, 0.82, 0.96, 1)
        _EdgeFade   ("Edge Fade", Range(0.2, 1.2)) = 0.55
        _MicroNormal("Micro Normal", Range(0, 1)) = 0.35
        _AmbientBoost("Ambient Boost", Range(0, 1)) = 0.32
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "SnowGround"
            Tags { "LightMode" = "ForwardBase" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            // 主光实时阴影（角色/树的影子落在雪上）。内置管线用 SHADOWS_SCREEN 关键字，
            // 由 Unity 在方向光投影开启时自动注入，这里不用手写 multi_compile。
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            // 【必须的三个 include，少一个都编译不过】
            //   UnityCG.cginc     —— 坐标变换、TRANSFORM_TEX、fog 宏、ShadeSH9
            //   Lighting.cginc    —— _LightColor0 / _WorldSpaceLightPos0 / _LightMatrix0
            //                        （这两个变量**不在** UnityCG 里。URP 版里它们由
            //                         Light 结构体带进来，改写时最容易漏掉的就是这一句，
            //                         表现为 d3d11 上 "undeclared identifier '_LightColor0'"）
            //   AutoLight.cginc   —— SHADOW_COORDS / TRANSFER_SHADOW / UNITY_LIGHT_ATTENUATION
            //                        它自身 include 了 UnityShadowLibrary，但**不含** _LightColor0
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _SnowTex;
            float4 _SnowTex_ST;
            fixed4 _SnowColor;
            float  _SnowAmount;
            fixed4 _SkyColor;
            float  _EdgeFade;
            float  _MicroNormal;
            float  _AmbientBoost;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float2 uv       : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldN   : TEXCOORD2;
                SHADOW_COORDS(3)
                UNITY_FOG_COORDS(4)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos      = UnityWorldToClipPos(o.worldPos);
                o.worldN   = UnityObjectToWorldNormal(v.normal);
                o.uv       = TRANSFORM_TEX(v.uv, _SnowTex);
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 采样：a = 覆盖率（实心 + 最外圈淡出），r = 颗粒噪声（只做极轻微的质感扰动）
                fixed4 sn = tex2D(_SnowTex, i.uv);
                float cov = sn.a;
                if (cov <= 0.003) discard;

                // 边缘淡出（双保险，贴图里已经淡过一次，这里再收一次方形边界）
                float2 c = i.uv - 0.5;
                float r = length(c) * 2.0;
                float edge = 1.0 - smoothstep(_EdgeFade, 1.0, r);
                if (edge <= 0.003) discard;

                // 微法线：用颗粒噪声的空间梯度扰动法线。梯度取噪声(.r)而不是覆盖率(.a) ——
                // 覆盖率现在是实心的，取 .a 的梯度恒为 0。幅度刻意压得很小。
                float gDX = ddx(sn.r);
                float gDY = ddy(sn.r);
                float3 N = normalize(i.worldN + float3(-gDX, 0.0, -gDY) * _MicroNormal * 2.5);

                // 受光：主光 Lambert + 主光实时阴影。
                // 内置管线里主光颜色就是 _LightColor0，衰减由 UNITY_LIGHT_ATTENUATION 一次给出
                // （它同时含阴影与距离衰减；方向光无距离衰减，所以这里等于 shadow 项）。
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float NdotL = saturate(dot(N, L));
                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                float3 direct = _LightColor0.rgb * NdotL * atten;

                // 环境光：球谐已含天空/环境颜色，白天亮、阴天灰、夜晚偏蓝。
                float3 ambient = ShadeSH9(float4(N, 1.0));

                // 合成：雪色 × (直射 + 环境 + 环境提亮下限)。
                // 雪是强反照率材质，阴天弱光下只按物理直射+环境算会发灰发暗，所以给提亮下限。
                float3 lit = direct + ambient + _AmbientBoost;
                float3 col = _SnowColor.rgb * lit;

                // 极轻微的颜色扰动（幅度 2.5%），避免大面积纯白显得死板。
                col *= (0.975 + 0.025 * sn.r);

                // 远处略偏天空色，强化"雪反射天光"的通透感。
                col = lerp(col, _SkyColor.rgb * (0.6 + 0.4 * lit), 0.22);

                // 雾：让远处雪面和场景雾色融在一起，雪盘远边不再" popping "出白色硬边。
                UNITY_APPLY_FOG(i.fogCoord, col);

                // 覆盖率已是实心：雪量上来后立刻变成"不透地的白"。
                // smoothstep 让刚开始下的一小段时间里由淡到浓，保留"渐进出现"的手感。
                float a = saturate(smoothstep(0.0, 0.22, _SnowAmount) * cov * edge);
                return fixed4(col, a);
            }
            ENDCG
        }
    }

    FallBack Off
}