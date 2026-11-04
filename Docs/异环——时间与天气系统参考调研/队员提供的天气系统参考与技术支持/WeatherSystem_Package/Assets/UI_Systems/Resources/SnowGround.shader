// 地面积雪层。把原来 Sprites/Default 无光照的"灰绿平涂"换成受天光影响的雪。
//
// 针对"雪太假 / 看着发虚、一块一块"的根因：
//   1) 原版用 Sprites/Default（完全不受光），整片雪是死板的灰绿平涂。
//      现在改成：主光 Lambert 直射（含主光实时阴影）+ 球谐(SH)环境光 + 环境提亮下限。
//   2) 原版用"噪声 × 径向淡出"调制覆盖率 alpha，导致雪一块一块、草地颜色透出来发灰显"虚"。
//      现在覆盖率 alpha 改为实心（只有最外圈淡出），噪声改存 RGB，只做极轻微的颜色/法线扰动。
//   3) 雪面严格按地形平铺（几何位移由 C# 端逐顶点向下射线完成），不再叠加任何程序化雪堆。
//   4) 雪量用 smoothstep 快速变实：下雪后很快就是"不透地的白"，同时保留由淡到浓的渐进手感。

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
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "SnowGround"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            // 接收主光实时阴影（角色/树的影子落在雪上）：自定义 shader 必须显式带这个变体。
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            // 雾：雪天预设会自动开 ExponentialSquared 雾，这里让雪色也参与雾混合，雪盘远边融进雾里。
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _SnowTex_ST;
                float4 _SnowColor;
                float  _SnowAmount;
                float4 _SkyColor;
                float  _EdgeFade;
                float  _MicroNormal;
                float  _AmbientBoost;
            CBUFFER_END

            TEXTURE2D(_SnowTex);
            SAMPLER(sampler_SnowTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _SnowTex);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                // 采样：a = 覆盖率（实心 + 最外圈淡出），r = 颗粒噪声（只做极轻微的质感扰动）
                float4 sn = SAMPLE_TEXTURE2D(_SnowTex, sampler_SnowTex, i.uv);
                float cov = sn.a;
                if (cov <= 0.003) discard;

                // 边缘淡出（双保险，贴图里已经淡过一次，这里再收一次方形边界）
                float2 c = i.uv - 0.5;
                float r = length(c) * 2.0;
                float edge = 1.0 - smoothstep(_EdgeFade, 1.0, r);
                if (edge <= 0.003) discard;

                // 微法线：用颗粒噪声的空间梯度扰动法线。梯度取噪声(.r)而不是覆盖率(.a) ——
                // 覆盖率现在是实心的，取 .a 的梯度恒为 0。幅度刻意压得很小：
                // 目标是"平铺干净的雪面"，不是雪花噪点。
                float gDX = ddx(sn.r);
                float gDY = ddy(sn.r);
                float3 N = normalize(i.normalWS + float3(-gDX, 0.0, -gDY) * _MicroNormal * 2.5);

                // 受光：主光方向 Lambert + 接收主光实时阴影（角色/树的影子落在雪上）。
                // TransformWorldToShadowCoord 把世界坐标变换到阴影图空间，GetMainLight(shadowCoord)
                // 顺带算出 shadowAttenuation；没有 _MAIN_LIGHT_SHADOWS 变体时 MainLightRealtimeShadow
                // 直接返回 1.0，安全兜底（不会让雪变黑）。
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float NdotL = saturate(dot(N, mainLight.direction));
                float3 direct = mainLight.color * NdotL * mainLight.shadowAttenuation;

                // 环境光：球谐(SH) 已含天空/环境颜色，白天亮、阴天灰、夜晚偏蓝。
                float3 ambient = SampleSH(N);

                // 合成：雪色 × (直射 + 环境 + 环境提亮下限)。
                // 雪是强反照率材质，阴天弱光下只按物理直射+环境算会发灰发暗（看着"虚"），
                // 所以给一个提亮下限，保证弱光下雪依然是"白实"的。
                float3 lit = direct + ambient + _AmbientBoost;
                float3 col = _SnowColor.rgb * lit;

                // 极轻微的颜色扰动（幅度 2.5%），避免大面积纯白显得死板。
                col *= (0.975 + 0.025 * sn.r);

                // 远处略偏天空色，强化"雪反射天光"的通透感（雪不是纯白死板色）。
                col = lerp(col, _SkyColor.rgb * (0.6 + 0.4 * lit), 0.22);

                // 雾：让远处雪面和场景雾色融在一起，雪盘远边不再" popping "出白色硬边。
                // 雪天预设会自动开雾（ExponentialSquared），这里把雪色送进 MixFog。
                float fogFactor = ComputeFogFactor(i.positionCS.z);
                col = MixFog(col, fogFactor);

                // 覆盖率已是实心：雪量上来后立刻变成"不透地的白"，草地的颜色不会透出来。
                // smoothstep 让刚开始下的一小段时间里由淡到浓，保留"渐进出现"的手感。
                float a = saturate(smoothstep(0.0, 0.22, _SnowAmount) * cov * edge);
                return float4(col, a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
